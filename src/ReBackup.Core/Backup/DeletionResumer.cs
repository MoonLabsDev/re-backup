using ReBackup.Core.Pool;
using ReBackup.Storage;

namespace ReBackup.Core.Backup;

/// <summary>What <see cref="DeletionResumer.ResumeAsync"/> did.</summary>
public enum ResumeOutcome
{
    /// <summary>No deletion of this computer was left unfinished in the target.</summary>
    NothingToDo,

    /// <summary>The unfinished deletions and the cleanup were finished.</summary>
    Finished,

    /// <summary>Something was left (a folder that could not be removed, a cleanup that did not get through); the next start tries again.</summary>
    Incomplete,

    /// <summary>The target cannot be reached, or another holder has its lock: nothing was changed; the next start tries again.</summary>
    NotNow,

    /// <summary>Canceled (the app exits): what was removed before is in the result, the rest stays for the next start.</summary>
    Canceled,
}

/// <summary>
/// What a resumed deletion did: <paramref name="Outcome"/> and the names of the version folders it finished removing.
/// </summary>
public sealed record DeletionResume(ResumeOutcome Outcome, IReadOnlyList<string> Removed);

/// <summary>
/// Finishes, at app start, the deletions by hand this computer did not finish (the app was quit, crashed, or the target
/// dropped): the version folders that carry this computer's <see cref="VersionMarkerNames.Deleting"/> marker of the plan,
/// and for an incremental plan the pending pool cleanup (<see cref="CleanupMarker"/>). Markers of other computers are never
/// touched.
/// </summary>
public static class DeletionResumer
{
    /// <summary>
    /// The plan's version folders in <paramref name="target"/> that this computer marked as being deleted, and whether
    /// this computer left a pool cleanup pending (incremental plans only).
    /// </summary>
    /// <exception cref="StorageException">The target cannot be read.</exception>
    public static async Task<(IReadOnlyList<string> Folders, bool Cleanup)> FindAsync(IStorage target, string planId, bool pool,
        string host, CancellationToken ct)
    {
        var folders = new List<string>();
        try
        {
            await foreach (var entry in target.ListAsync("", recursive: false, ct).ConfigureAwait(false))
            {
                if (!entry.IsDirectory || entry.IsLink)
                    continue;
                var name = StoragePath.Name(entry.Path);
                if (!VersionName.IsPlainFolderName(name) || VersionName.IsTransient(name) || !VersionName.TryParseAny(name, out _, out _))
                    continue;
                MarkerInfo? marker;
                try
                {
                    marker = await VersionMarkers.TryReadAsync(target, StoragePath.Combine(entry.Path, VersionMarkerNames.Deleting), ct)
                        .ConfigureAwait(false);
                }
                catch (StorageNotFoundException)
                {
                    continue;
                }
                if (marker is not null && marker.IsOwn(host) && marker.PlanId == planId)
                    folders.Add(entry.Path);
            }
        }
        catch (StorageNotFoundException)
        {
            return ([], false);   // the target has not been created yet
        }
        var cleanup = pool && await CleanupMarker.ExistsAsync(target, host, ct).ConfigureAwait(false);
        return (folders, cleanup);
    }

    /// <summary>
    /// Finishes what <see cref="FindAsync"/> finds: an incremental plan takes the target's lock (operation <c>"delete"</c>)
    /// first, marks the pending cleanup (<see cref="CleanupMarker"/>, when it is not marked yet: the versions are deleted,
    /// the pool not yet), removes the marked folders (<see cref="VersionRemover.FinishRemovalAsync"/>), collects the pool and
    /// removes its cleanup marker; a full plan only removes the folders. Never throws but for cancellation while the
    /// target is still being looked at: an unreachable target or a held lock is <see cref="ResumeOutcome.NotNow"/>, and a
    /// cancellation after that is <see cref="ResumeOutcome.Canceled"/> with the folders removed so far.
    /// </summary>
    /// <param name="progress">
    /// Gets <see cref="VersionDeletionPhase.Deleting"/> per folder (by its files) and <see cref="VersionDeletionPhase.CleaningUp"/>
    /// as <see cref="VersionDeleter.DeleteAsync"/> reports them, on the deleting thread.
    /// </param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled before anything was changed.</exception>
    public static async Task<DeletionResume> ResumeAsync(IStorage target, string planId, string planName, bool pool,
        IProgress<VersionDeletionProgress>? progress, CancellationToken ct, string? machineName = null)
    {
        var host = machineName ?? Environment.MachineName;
        IReadOnlyList<string> folders;
        bool cleanup;
        try
        {
            (folders, cleanup) = await FindAsync(target, planId, pool, host, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            return new DeletionResume(ResumeOutcome.NotNow, []);
        }
        if (folders.Count == 0 && !cleanup)
            return new DeletionResume(ResumeOutcome.NothingToDo, []);

        if (!pool)
            return await RemoveFoldersAsync(target, folders, progress, ct).ConfigureAwait(false);

        TargetLock? held;
        try
        {
            held = await TargetLock.TryAcquireAsync(target, host, "delete", TimeProvider.System, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            return new DeletionResume(ResumeOutcome.NotNow, []);
        }
        if (held is null)
            return new DeletionResume(ResumeOutcome.NotNow, []);
        try
        {
            if (!cleanup)
            {
                // Once the versions are gone, the pool still holds what only they referenced: mark that first.
                try
                {
                    await CleanupMarker.WriteAsync(target, host, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is StorageException or ArgumentException)
                {
                    return new DeletionResume(ResumeOutcome.NotNow, []);   // nothing was changed
                }
            }
            var removed = await RemoveFoldersAsync(target, folders, progress, ct).ConfigureAwait(false);
            if (removed.Outcome == ResumeOutcome.Canceled)
                return removed;
            var collected = await VersionDeleter.CollectPoolAsync(target, planId, planName, host, progress, folders.Count, ct)
                .ConfigureAwait(false);
            return removed with
            {
                Outcome = ct.IsCancellationRequested ? ResumeOutcome.Canceled
                    : removed.Outcome == ResumeOutcome.Finished && collected ? ResumeOutcome.Finished
                    : ResumeOutcome.Incomplete,
            };
        }
        finally
        {
            await VersionDeleter.ReleaseAsync(held).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes the folders one after the other; a cancellation stops before the next folder (a folder that was started is
    /// finished, its removal runs to the end) and is <see cref="ResumeOutcome.Canceled"/>.
    /// </summary>
    private static async Task<DeletionResume> RemoveFoldersAsync(IStorage target, IReadOnlyList<string> folders,
        IProgress<VersionDeletionProgress>? progress, CancellationToken ct)
    {
        var removed = new List<string>();
        var complete = true;
        long done = 0;
        for (var i = 0; i < folders.Count; i++)
        {
            if (ct.IsCancellationRequested)
                return new DeletionResume(ResumeOutcome.Canceled, removed);
            var name = StoragePath.Name(folders[i]);
            var current = i + 1;
            progress?.Report(new VersionDeletionProgress(VersionDeletionPhase.Deleting, current, folders.Count, name, done, 0));
            try
            {
                await VersionRemover.FinishRemovalAsync(target, folders[i], count =>
                {
                    done += count;
                    progress?.Report(new VersionDeletionProgress(VersionDeletionPhase.Deleting, current, folders.Count, name, done, 0));
                }, CancellationToken.None).ConfigureAwait(false);
                removed.Add(name);
            }
            catch (Exception ex) when (ex is StorageException or ArgumentException)
            {
                complete = false;   // the marker stays: the next start tries again
            }
        }
        return new DeletionResume(complete ? ResumeOutcome.Finished : ResumeOutcome.Incomplete, removed);
    }
}
