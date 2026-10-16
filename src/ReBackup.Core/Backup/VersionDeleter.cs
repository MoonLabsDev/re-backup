using System.Diagnostics;
using System.Text.Json;
using ReBackup.Core.Localization;
using ReBackup.Core.Pool;
using ReBackup.Storage;

namespace ReBackup.Core.Backup;

public enum VersionDeletionOutcome
{
    /// <summary>The folder is gone.</summary>
    Deleted,

    /// <summary>The folder no longer is a version, but its remains are left (marked as being deleted); the next run removes them.</summary>
    RemainsLeft,

    /// <summary>There was no such folder (any more).</summary>
    Gone,

    /// <summary>The folder is not a version of the plan (<see cref="VersionOwnership.Owned"/>): nothing was changed.</summary>
    NotManaged,

    /// <summary>The folder could not be removed: nothing was changed.</summary>
    Failed,
}

/// <summary>What happened to one version a person asked to delete; <see cref="Error"/> for RemainsLeft and Failed.</summary>
public sealed record VersionDeletion(string Name, VersionDeletionOutcome Outcome, string? Error);

/// <summary>
/// Preparing: the versions' manifests are read for their file counts (an incremental plan's version folders are listed).
/// Deleting: the files are deleted. CleaningUp: an incremental plan's pool is collected (<see cref="VersionDeletionProgress.Cleanup"/>).
/// </summary>
public enum VersionDeletionPhase { Preparing, Deleting, CleaningUp }

/// <summary>
/// A deletion's progress: the version being examined or deleted (1-based <see cref="Current"/> of <see cref="VersionCount"/>) and
/// the files deleted so far over all versions, of the total their manifests list (of the files in their folders for an
/// incremental plan, whose folders hold only the manifest and markers). While the pool is collected,
/// <see cref="Cleanup"/> tells where the collection stands.
/// </summary>
public readonly record struct VersionDeletionProgress(VersionDeletionPhase Phase, int Current, int VersionCount, string VersionName, long FilesDone,
    long FilesTotal, CollectProgress Cleanup = default)
{
    /// <summary>0..1 by files; by versions when the manifests list no files; within the cleanup phase while cleaning up.</summary>
    public double Fraction => Phase == VersionDeletionPhase.Preparing ? 0
        : Phase == VersionDeletionPhase.CleaningUp ? Cleanup.Fraction
        : FilesTotal > 0
        ? Math.Clamp((double)FilesDone / FilesTotal, 0, 1)
        : VersionCount > 0 ? Math.Clamp((double)(Current - 1) / VersionCount, 0, 1) : 1;
}

/// <summary>Deletes versions a person picked by hand, with the same care retention takes.</summary>
public static class VersionDeleter
{
    /// <summary>
    /// Deletes the plan's versions <paramref name="versionNames"/> (folder names directly under the root of
    /// <paramref name="target"/>) one after the other. Each folder is examined again right before it is removed: only a
    /// folder that is still <see cref="VersionOwnership.Owned"/> by the plan, and is not being written by a run, is deleted,
    /// whatever the caller listed. Never throws for a single version. Cancellation stops between versions: a version that
    /// was started is finished (a half-deleted one would be left as remains), the versions not started yet are left out of
    /// the result.
    /// </summary>
    /// <param name="planName">
    /// The plan's current name; it goes into the deletion marker unless the folder is named after an earlier one
    /// (<see cref="VersionRemover.RemoveAsync"/>).
    /// </param>
    /// <param name="machineName">
    /// This computer, as markers name it (<see cref="Environment.MachineName"/> when null): a folder another computer marked
    /// as being deleted is not managed, as that deletion may still go on there.
    /// </param>
    /// <param name="progress">
    /// Gets a report while the manifests are read (for the file counts), then when each version starts, then at most every <see cref="ProgressInterval"/> while its files are
    /// deleted, and when it ends; on the deleting thread.
    /// </param>
    /// <param name="pool">
    /// The plan is incremental (spec 5): the target's lock is taken (operation <c>"delete"</c>, host
    /// <paramref name="machineName"/>) before anything else, the versions are deleted, the pool is collected
    /// (<see cref="PoolCollector"/>) and the lock is released. While another holder has the lock nothing is changed and every
    /// version is <see cref="VersionDeletionOutcome.Failed"/> with the <c>core.run.targetLocked</c> text. Before the first
    /// version is deleted <see cref="CleanupMarker"/> is written (when that fails nothing is changed and every version is
    /// Failed); it is removed once the pool was collected, so a cleanup that did not get there is finished at the next app
    /// start (<see cref="DeletionResumer"/>). Collection problems and a lock that cannot be released are traced only: the
    /// versions are deleted all the same, and the next collection continues.
    /// </param>
    public static async Task<IReadOnlyList<VersionDeletion>> DeleteAsync(IStorage target, string planId, string planName,
        IReadOnlyList<string> versionNames, IProgress<VersionDeletionProgress>? progress = null, CancellationToken ct = default,
        string? machineName = null, bool pool = false)
    {
        var host = machineName ?? Environment.MachineName;
        if (!pool)
            return await DeleteVersionsAsync(target, planId, planName, versionNames, progress, host, pool: false, ct).ConfigureAwait(false);

        TargetLock? held;
        try
        {
            held = await TargetLock.TryAcquireAsync(target, host, LockOperation, TimeProvider.System, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return [];   // as without the lock: the versions not started are left out of the result
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            return versionNames.Select(name => new VersionDeletion(name, VersionDeletionOutcome.Failed, ex.Message)).ToList();
        }
        if (held is null)
        {
            var locked = await TargetLock.LockedMessageAsync(target, CancellationToken.None).ConfigureAwait(false);
            return versionNames.Select(name => new VersionDeletion(name, VersionDeletionOutcome.Failed, locked)).ToList();
        }

        try
        {
            try
            {
                await CleanupMarker.WriteAsync(target, host, DateTime.UtcNow, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return [];
            }
            catch (Exception ex) when (ex is StorageException or ArgumentException)
            {
                return versionNames.Select(name => new VersionDeletion(name, VersionDeletionOutcome.Failed, ex.Message)).ToList();
            }
            var results = await DeleteVersionsAsync(target, planId, planName, versionNames, progress, host, pool: true, ct)
                .ConfigureAwait(false);
            await CollectPoolAsync(target, planId, planName, host, progress, versionNames.Count, ct).ConfigureAwait(false);
            return results;
        }
        finally
        {
            await ReleaseAsync(held).ConfigureAwait(false);
        }
    }

    /// <summary>Releases the lock; a lock that cannot be released is traced only.</summary>
    internal static async Task ReleaseAsync(TargetLock held)
    {
        try
        {
            await held.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            Trace.TraceWarning(CoreTexts.English("core.run.lockReleaseFailed", ("error", ex.Message)));
        }
    }

    /// <summary>
    /// Collects the pool under the caller's lock, reporting <see cref="VersionDeletionPhase.CleaningUp"/>, and removes this
    /// computer's <see cref="CleanupMarker"/> once the pool was swept. True when it was; problems are traced only.
    /// </summary>
    internal static async Task<bool> CollectPoolAsync(IStorage target, string planId, string planName, string host,
        IProgress<VersionDeletionProgress>? progress, int versionCount, CancellationToken ct)
    {
        try
        {
            var cleanup = progress is null ? null : new CleanupProgress(progress, versionCount);
            var collected = await PoolCollector.CollectAsync(target, planId, planName, ct, cleanup).ConfigureAwait(false);
            if (collected.SkippedReason is { } reason)
            {
                Trace.TraceWarning($"The pool of plan {planId} was not collected: {reason}");
                return false;
            }
            await CleanupMarker.RemoveOwnAsync(target, host, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;   // the marker stays: the next app start continues
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            Trace.TraceWarning(CoreTexts.English("core.gc.failed", ("error", ex.Message)));
            return false;
        }
    }

    /// <summary>Hands the collection's reports on as <see cref="VersionDeletionPhase.CleaningUp"/>, on the reporting thread.</summary>
    private sealed class CleanupProgress(IProgress<VersionDeletionProgress> progress, int versionCount) : IProgress<CollectProgress>
    {
        public void Report(CollectProgress value) =>
            progress.Report(new VersionDeletionProgress(VersionDeletionPhase.CleaningUp, versionCount, versionCount, "", 0, 0, value));
    }

    /// <summary>What a lock holder is doing while it deletes versions by hand (<see cref="LockInfo.Operation"/>).</summary>
    private const string LockOperation = "delete";

    /// <summary>The deletion itself, as <see cref="DeleteAsync"/> describes it, without lock or collection.</summary>
    private static async Task<IReadOnlyList<VersionDeletion>> DeleteVersionsAsync(IStorage target, string planId,
        string planName, IReadOnlyList<string> versionNames, IProgress<VersionDeletionProgress>? progress, string host,
        bool pool, CancellationToken ct)
    {
        long filesTotal = 0;
        var counts = new long[versionNames.Count];
        if (progress is not null)
        {
            for (var i = 0; i < versionNames.Count && !ct.IsCancellationRequested; i++)
            {
                progress.Report(new VersionDeletionProgress(VersionDeletionPhase.Preparing, i + 1, versionNames.Count,
                    versionNames[i], 0, filesTotal));
                counts[i] = pool
                    ? await FilesInFolderAsync(target, versionNames[i]).ConfigureAwait(false)
                    : await FileCountOfAsync(target, versionNames[i]).ConfigureAwait(false);
                filesTotal += counts[i];
            }
        }
        long filesDone = 0;
        var results = new List<VersionDeletion>(versionNames.Count);
        for (var i = 0; i < versionNames.Count; i++)
        {
            if (ct.IsCancellationRequested)
                break;
            var name = versionNames[i];
            var current = i + 1;
            progress?.Report(new VersionDeletionProgress(VersionDeletionPhase.Deleting, current, versionNames.Count, name, filesDone, filesTotal));
            var sinceReport = Stopwatch.StartNew();
            var doneBefore = filesDone;
            var marker = new MarkerInfo(VersionMarkers.FormatVersion, planId, planName, DateTime.UtcNow, host);
            results.Add(await DeleteOneAsync(target, planId, name, marker, host, progress is null ? null : count =>
            {
                filesDone += count;
                if (sinceReport.Elapsed < ProgressInterval)
                    return;
                sinceReport.Restart();
                progress.Report(new VersionDeletionProgress(VersionDeletionPhase.Deleting, current, versionNames.Count, name, filesDone, filesTotal));
            }).ConfigureAwait(false));
            // The manifest and the marker are not counted while they are deleted; an incremental plan's folder holds little else.
            if (pool && results[^1].Outcome == VersionDeletionOutcome.Deleted)
                filesDone = Math.Max(filesDone, doneBefore + counts[i]);
            progress?.Report(new VersionDeletionProgress(VersionDeletionPhase.Deleting, current, versionNames.Count, name, filesDone, filesTotal));
        }
        return results;
    }

    /// <summary>Reports while files are deleted come at most this often.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>The number of files the version's manifest lists; 0 when that is not known. Never throws.</summary>
    private static async Task<long> FileCountOfAsync(IStorage target, string name)
    {
        if (!IsVersionName(name))
            return 0;
        try
        {
            var manifest = StoragePath.Combine(name, VersionMarkerNames.Manifest);
            return (await ManifestReader.ReadHeaderAsync(target, manifest).ConfigureAwait(false)).FileCount ??
                   (await ManifestReader.ReadTotalsAsync(target, manifest).ConfigureAwait(false)).FileCount;
        }
        catch (Exception ex) when (ex is StorageException or JsonException or ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>The files in the version's folder (an incremental plan's: its manifest and markers); 0 when that is not known. Never throws.</summary>
    private static async Task<long> FilesInFolderAsync(IStorage target, string name)
    {
        if (!IsVersionName(name))
            return 0;
        try
        {
            long files = 0;
            await foreach (var entry in target.ListAsync(name, recursive: true, CancellationToken.None).ConfigureAwait(false))
            {
                if (!entry.IsDirectory)
                    files++;
            }
            return files;
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>Only a plain folder name of a version: nothing that leads elsewhere, no ".partial" or ".deleting" folder.</summary>
    private static bool IsVersionName(string name) =>
        VersionName.IsPlainFolderName(name) && !VersionName.IsTransient(name) && VersionName.TryParseAny(name, out _, out _);

    /// <remarks>
    /// The removal runs without the caller's cancellation: once its marker is written, stopping half-way would only
    /// leave remains for the next run.
    /// </remarks>
    private static async Task<VersionDeletion> DeleteOneAsync(IStorage target, string planId, string name, MarkerInfo marker,
        string machineName, Action<int>? onFileDeleted)
    {
        if (!IsVersionName(name))
            return new VersionDeletion(name, VersionDeletionOutcome.NotManaged, null);

        try
        {
            if (await target.StatAsync(name, CancellationToken.None).ConfigureAwait(false) is not { } entry)
                return new VersionDeletion(name, VersionDeletionOutcome.Gone, null);
            // A link is never a version; a folder that a run is writing is not one yet.
            if (entry.IsLink || !entry.IsDirectory ||
                await target.StatAsync(StoragePath.Combine(name, VersionMarkerNames.Pending), CancellationToken.None).ConfigureAwait(false) is not null ||
                (await VersionCatalog.ProbeAsync(target, name, planId).ConfigureAwait(false)).Ownership != VersionOwnership.Owned ||
                await IsDeletedElsewhereAsync(target, name, machineName).ConfigureAwait(false))
                return new VersionDeletion(name, VersionDeletionOutcome.NotManaged, null);

            await VersionRemover.RemoveAsync(target, name, marker, onFileDeleted).ConfigureAwait(false);
            return new VersionDeletion(name, VersionDeletionOutcome.Deleted, null);
        }
        catch (VersionRemainsException ex)
        {
            return new VersionDeletion(name, VersionDeletionOutcome.RemainsLeft, ex.Message);
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            return await ExistsAsync(target, name).ConfigureAwait(false)
                ? new VersionDeletion(name, VersionDeletionOutcome.Failed, ex.Message)
                : new VersionDeletion(name, VersionDeletionOutcome.Gone, null);
        }
    }

    /// <summary>
    /// Whether another computer marked the folder as being deleted: its deletion may still go on there. A marker
    /// that cannot be read counts as none, as before.
    /// </summary>
    private static async Task<bool> IsDeletedElsewhereAsync(IStorage target, string name, string machineName) =>
        await VersionMarkers.TryReadAsync(target, StoragePath.Combine(name, VersionMarkerNames.Deleting), CancellationToken.None)
            .ConfigureAwait(false) is { } marker && !marker.IsOwn(machineName);

    /// <summary>
    /// Whether the version folder is still there: its manifest is, or the folder can be listed. When that cannot be found
    /// out it counts as there, so a failure is never reported as gone.
    /// </summary>
    private static async Task<bool> ExistsAsync(IStorage target, string name)
    {
        try
        {
            if (await target.StatAsync(StoragePath.Combine(name, VersionMarkerNames.Manifest), CancellationToken.None).ConfigureAwait(false) is not null)
                return true;
            await foreach (var _ in target.ListAsync(name, recursive: false, CancellationToken.None).ConfigureAwait(false))
                break;
            return true;
        }
        catch (StorageNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            return true;
        }
    }
}
