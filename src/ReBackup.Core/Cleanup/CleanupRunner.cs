using System.Diagnostics;
using System.Text.Json;
using ReBackup.Core.Backup;
using ReBackup.Core.Ignore;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Core.Versions;
using ReBackup.Shared.Json;
using ReBackup.Storage;

namespace ReBackup.Core.Cleanup;

/// <summary>
/// Applies the current ignore rules to old versions of a plan: shortens their manifests and deletes what the rules ignore
/// (from the version folders of a full plan, from the pool of an incremental one).
/// </summary>
public static class CleanupRunner
{
    /// <summary>What a lock holder is doing while it cleans up versions (<see cref="LockInfo.Operation"/>).</summary>
    private const string LockOperation = "cleanup";

    /// <summary>
    /// Cleans up <paramref name="versions"/> of <paramref name="plan"/> on <paramref name="target"/> by
    /// <paramref name="matcher"/> (spec 5.2). First the target is checked (<see cref="StoreGuard.CheckAsync"/>, nothing
    /// created); an incremental plan then takes the target's lock (<see cref="TargetLock"/>, operation <c>"cleanup"</c>,
    /// host <paramref name="machineName"/>); then the plan's versions are listed. A failure there fails every version and
    /// writes nothing; while another holder has the lock every version is <see cref="CleanupOutcome.Failed"/> with the
    /// <c>core.run.targetLocked</c> text. Then the versions go one by one, oldest first in the catalog's order:
    /// <list type="number">
    /// <item>A version the catalog does not list as owned is <see cref="CleanupOutcome.Gone"/>; one whose stamp
    /// (<see cref="VersionIndex.StampOfAsync"/>) is no longer <see cref="CleanupTarget.Stamp"/> is
    /// <see cref="CleanupOutcome.Changed"/>.</item>
    /// <item>The manifest is read (its storage stamp taken first; a manifest without a stamp is
    /// <see cref="CleanupOutcome.Changed"/>, as it cannot be replaced conditionally) and pruned
    /// (<see cref="ManifestPruner.Prune"/>); when nothing is ignored the version is
    /// <see cref="CleanupOutcome.NothingToDo"/>.</item>
    /// <item>Full plans: <see cref="VersionMarkerNames.Pruning"/> is written durably with the paths to delete. A marker
    /// an earlier cleanup left behind is merged into it: those of its files and folders the shortened manifest does not
    /// list are deleted too.</item>
    /// <item>The shortened manifest replaces the old one, conditionally on the stamp read. A manifest replaced meanwhile
    /// (<see cref="StorageConflictException"/>) is never overwritten: the marker is removed, nothing is deleted, and the
    /// version is <see cref="CleanupOutcome.Changed"/>.</item>
    /// <item>Full plans: the files are deleted in batches of <see cref="VersionRemover.BatchSize"/> (a file already
    /// missing is fine), then each folder, deepest first, when it is empty; then the marker is removed.</item>
    /// <item>The version is handed to <paramref name="index"/>.</item>
    /// </list>
    /// The manifest never lists a file that is gone: it is shortened before anything is deleted. A version that fails
    /// after its marker is written keeps the marker, and the next start finishes the deletion; any failure of a version
    /// makes it <see cref="CleanupOutcome.Failed"/> with the message, and the run goes on with the next.
    /// <para>
    /// An incremental plan writes no marker and deletes nothing from its version folders: after the last version, when at
    /// least one was <see cref="CleanupOutcome.Cleaned"/>, the pool is collected once (<see cref="PoolCollector"/>,
    /// reported as <see cref="CleanupPhase.CollectingPool"/>), and <see cref="CleanupResult.BytesFreed"/> is what that
    /// freed (0 when it did not run). A collection that is skipped or fails is traced only: the manifests are short
    /// already, and the next collection (after a backup or a deletion) catches up (spec 6.3). The lock is released at
    /// the end, whatever happened.
    /// </para>
    /// </summary>
    /// <param name="machineName">The computer the marker and the lock name; <see cref="Environment.MachineName"/> when null.</param>
    /// <remarks>
    /// <paramref name="ct"/> is looked at only before a version starts: a version that has started is finished
    /// (deleting what its marker lists anyway), the versions after it are left out of the result and
    /// <see cref="CleanupResult.Canceled"/> is set; the pool of an incremental plan is collected all the same (spec 5.5).
    /// The index hand-off is best effort: a failure there is traced and leaves the index with the old stamp, and its next
    /// sync reads the version again.
    /// </remarks>
    public static async Task<CleanupResult> RunAsync(IStorage target, BackupPlan plan, IgnoreMatcher matcher,
        IReadOnlyList<CleanupTarget> versions, IVersionIndexSink? index, IProgress<CleanupProgress>? progress,
        CancellationToken ct, string? machineName = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(versions);
        var host = machineName ?? Environment.MachineName;

        try
        {
            // Copies are never pruned in a pool's target; the store file is never created here.
            var guard = await StoreGuard.CheckAsync(target, plan, createIfMissing: false, ct).ConfigureAwait(false);
            if (guard != StoreGuardOutcome.Ok)
                return AllFailed(versions, CoreTexts.English(StoreGuard.MessageKey(guard)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new CleanupResult([], 0, Canceled: true);
        }
        catch (Exception ex)
        {
            return AllFailed(versions, ex.Message);
        }
        if (!plan.Incremental)
            return await CleanVersionsAsync(target, plan, matcher, versions, index, progress, host, ct).ConfigureAwait(false);

        TargetLock? held;
        try
        {
            held = await TargetLock.TryAcquireAsync(target, host, LockOperation, TimeProvider.System, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new CleanupResult([], 0, Canceled: true);
        }
        catch (Exception ex)
        {
            return AllFailed(versions, ex.Message);
        }
        if (held is null)
            return AllFailed(versions, await TargetLock.LockedMessageAsync(target, CancellationToken.None).ConfigureAwait(false));

        try
        {
            var result = await CleanVersionsAsync(target, plan, matcher, versions, index, progress, host, ct)
                .ConfigureAwait(false);
            // The versions' BytesRemoved are manifest sizes; what the pool frees is only what the collect deletes.
            if (!result.Versions.Any(v => v.Outcome == CleanupOutcome.Cleaned))
                return result with { BytesFreed = 0 };
            var count = result.Versions.Count;
            progress?.Report(new CleanupProgress(count, count, "", CleanupPhase.CollectingPool));
            return result with { BytesFreed = await CollectPoolAsync(target, plan).ConfigureAwait(false) };
        }
        finally
        {
            await VersionDeleter.ReleaseAsync(held).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lists the plan's versions and cleans the requested ones, oldest first, as <see cref="RunAsync"/> describes;
    /// <see cref="CleanupResult.BytesFreed"/> is the sum of the cleaned versions' <see cref="CleanupVersionResult.BytesRemoved"/>.
    /// </summary>
    private static async Task<CleanupResult> CleanVersionsAsync(IStorage target, BackupPlan plan, IgnoreMatcher matcher,
        IReadOnlyList<CleanupTarget> versions, IVersionIndexSink? index, IProgress<CleanupProgress>? progress, string host,
        CancellationToken ct)
    {
        IReadOnlyList<VersionInfo> catalog;
        try
        {
            catalog = await VersionCatalog.ListAsync(target, plan.Id, plan.Name, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new CleanupResult([], 0, Canceled: true);
        }
        catch (Exception ex)
        {
            return AllFailed(versions, ex.Message);
        }

        // Oldest first, as the catalog lists them; a version it does not list keeps its place behind them.
        var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < catalog.Count; i++)
            position.TryAdd(catalog[i].Name, i);
        var ordered = versions.OrderBy(v => position.GetValueOrDefault(v.Name, int.MaxValue)).ToList();

        var results = new List<CleanupVersionResult>(ordered.Count);
        var canceled = false;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                canceled = true;
                break;
            }
            var request = ordered[i];
            // Being written or deleted, deleted, or renamed by hand: the catalog does not list it as owned.
            if (catalog.FirstOrDefault(v => v.IsOwned && v.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase))
                is not { } version)
            {
                results.Add(new CleanupVersionResult(request.Name, CleanupOutcome.Gone, 0, 0, null));
                continue;
            }

            var current = i + 1;
            void Report(CleanupPhase phase) =>
                progress?.Report(new CleanupProgress(current, ordered.Count, version.Name, phase));
            try
            {
                results.Add(await CleanVersionAsync(target, plan, matcher, request, version, index, host, Report)
                    .ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                results.Add(new CleanupVersionResult(version.Name, CleanupOutcome.Failed, 0, 0, ex.Message));
            }
        }

        var freed = results.Where(r => r.Outcome == CleanupOutcome.Cleaned).Sum(r => r.BytesRemoved);
        return new CleanupResult(results, freed, canceled);
    }

    /// <summary>
    /// Steps 1–6 of spec 5.2 for one version (without the marker and the deletions for an incremental plan). Not
    /// cancelable: what it writes, it finishes (or leaves the marker for).
    /// </summary>
    private static async Task<CleanupVersionResult> CleanVersionAsync(IStorage target, BackupPlan plan,
        IgnoreMatcher matcher, CleanupTarget request, VersionInfo version, IVersionIndexSink? index, string host,
        Action<CleanupPhase> report)
    {
        var ct = CancellationToken.None;
        report(CleanupPhase.Rewriting);
        if (await VersionIndex.StampOfAsync(target, version.Path, ct).ConfigureAwait(false) != request.Stamp)
            return Result(CleanupOutcome.Changed);

        // The stamp is taken before the manifest is read: one replaced in between fails the write, it is never overwritten.
        var manifestPath = StoragePath.Combine(version.Path, VersionMarkerNames.Manifest);
        if (await target.StatAsync(manifestPath, ct).ConfigureAwait(false) is not { IsDirectory: false } manifestEntry)
            return Result(CleanupOutcome.Gone);   // removed since it was listed
        // Without a stamp the write could not be conditional: it would overwrite whatever is there by then.
        if (manifestEntry.Stamp is not { } stamp || "m:" + stamp != request.Stamp)
            return Result(CleanupOutcome.Changed);
        BackupManifest manifest;
        var input = await target.OpenReadAsync(manifestPath, ct).ConfigureAwait(false);
        await using (input.ConfigureAwait(false))
        {
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(input, JsonDefaults.Options, ct)
                .ConfigureAwait(false) ?? throw new JsonException($"The manifest of {version.Name} is empty.");
        }

        var listed = manifest.Files;   // Prune replaces the list, this one keeps the sizes of what it removes
        var pruned = ManifestPruner.Prune(manifest, matcher);
        if (pruned.RemovedFiles.Count == 0 && pruned.RemovedFolders.Count == 0)
            return Result(CleanupOutcome.NothingToDo);
        var removed = new HashSet<string>(pruned.RemovedFiles, StringComparer.Ordinal);
        var bytes = listed.Where(f => removed.Contains(f.Path)).Sum(f => f.Size);

        // A pool plan's version folder holds only its manifest: the content goes with the pool collect, no marker needed.
        var deletion = plan.Incremental
            ? null
            : await DeletionOfAsync(target, version.Path, manifest, pruned, plan.Id, host, ct).ConfigureAwait(false);
        if (deletion is not null)
        {
            // Marker first: from here on a crash leaves it, and the next start deletes what the manifest no longer lists.
            await PruningMarker.WriteAsync(target, version.Path, deletion, ct).ConfigureAwait(false);
        }
        try
        {
            await ManifestWriter.ReplaceAsync(target, manifestPath, manifest, stamp, ct).ConfigureAwait(false);
        }
        catch (StorageConflictException)
        {
            // Someone replaced the manifest meanwhile: it is kept, and nothing it may still list is deleted.
            if (deletion is not null)
                await PruningMarker.RemoveAsync(target, version.Path, ct).ConfigureAwait(false);
            return Result(CleanupOutcome.Changed);
        }

        if (deletion is not null)
        {
            report(CleanupPhase.Deleting);
            await DeleteAsync(target, version.Path, deletion, ct).ConfigureAwait(false);
            await PruningMarker.RemoveAsync(target, version.Path, ct).ConfigureAwait(false);
        }

        if (index is not null)
        {
            try
            {
                VersionName.TryParseAny(version.Name, out var localTime, out _);
                var info = new VersionInfo(version.Name, version.Path, localTime, VersionOwnership.Owned,
                    manifest.FileCount, manifest.TotalBytes);
                // The index reads the target synchronously: off the caller's thread, as after a run.
                await Task.Run(() => index.Add(plan.Id, target, info, manifest)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Deliberately broad: the version is clean; the index's next sync sees the new stamp and reads it again.
                Trace.TraceWarning($"The cleaned version {version.Name} of plan {plan.Id} could not be handed to the index: {ex.Message}");
            }
        }
        return new CleanupVersionResult(version.Name, CleanupOutcome.Cleaned, pruned.RemovedFiles.Count, bytes, null);

        CleanupVersionResult Result(CleanupOutcome outcome) => new(version.Name, outcome, 0, 0, null);
    }

    /// <summary>
    /// The marker for a full-plan version: what <paramref name="pruned"/> removed from the manifest, plus the files and
    /// folders of a marker an earlier cleanup left behind that the shortened <paramref name="manifest"/> does not list
    /// (a path it lists again is never deleted). Folders come deepest first.
    /// </summary>
    private static async Task<PruningInfo> DeletionOfAsync(IStorage target, string versionPath, BackupManifest manifest,
        PruneResult pruned, string planId, string host, CancellationToken ct)
    {
        IReadOnlyList<string> files = pruned.RemovedFiles;
        IReadOnlyList<string> folders = pruned.RemovedFolders;
        if (await PruningMarker.TryReadAsync(target, versionPath, ct).ConfigureAwait(false) is { } earlier)
        {
            var listedFiles = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            var listedFolders = manifest.Directories.ToHashSet(StringComparer.Ordinal);
            files = [.. files.Union(earlier.Files.Where(path => !listedFiles.Contains(path)), StringComparer.Ordinal)];
            folders = [.. folders.Union(earlier.Folders.Where(path => !listedFolders.Contains(path)), StringComparer.Ordinal)
                .OrderByDescending(path => path.Count(c => c == '/'))];   // stable: the pruner's order stays
        }
        return new PruningInfo(PruningMarker.FormatVersion, planId, host, DateTime.UtcNow, files, folders);
    }

    /// <summary>
    /// Deletes the marker's files in batches (a file that is missing already is not an error), then its folders, deepest
    /// first, where they are empty (a folder that is not empty stays). Also used by <see cref="CleanupResumer"/>.
    /// </summary>
    internal static async Task DeleteAsync(IStorage target, string versionPath, PruningInfo deletion, CancellationToken ct)
    {
        var files = deletion.Files.Select(path => StoragePath.Combine(versionPath, path)).ToList();
        for (var start = 0; start < files.Count; start += VersionRemover.BatchSize)
            await target.DeleteAsync(files.GetRange(start, Math.Min(VersionRemover.BatchSize, files.Count - start)), ct)
                .ConfigureAwait(false);
        // Elsewhere folders vanish with their last file.
        if (!target.Capabilities.HasFlag(StorageCapabilities.EmptyDirectories))
            return;
        foreach (var folder in deletion.Folders)
        {
            try
            {
                await target.DeleteAsync([StoragePath.Combine(versionPath, folder)], ct).ConfigureAwait(false);
            }
            catch (StorageConflictException)
            {
                // Not empty: it holds something the manifest did not list, which is not ours to delete.
            }
        }
    }

    /// <summary>
    /// Collects the pool under the caller's lock and returns the bytes it freed; 0 when it was skipped or failed, which
    /// is traced only. Runs without cancellation: a canceled cleanup still collects (spec 5.5).
    /// </summary>
    private static async Task<long> CollectPoolAsync(IStorage target, BackupPlan plan)
    {
        try
        {
            var collected = await PoolCollector.CollectAsync(target, plan.Id, plan.Name, CancellationToken.None)
                .ConfigureAwait(false);
            if (collected.SkippedReason is { } reason)
            {
                Trace.TraceWarning($"The pool of plan {plan.Id} was not collected: {reason}");
                return 0;
            }
            return collected.BytesFreed;
        }
        catch (Exception ex)
        {
            // Deliberately broad: the manifests are short already, and the next collection catches up.
            Trace.TraceWarning(CoreTexts.English("core.gc.failed", ("error", ex.Message)));
            return 0;
        }
    }

    private static CleanupResult AllFailed(IReadOnlyList<CleanupTarget> versions, string message) =>
        new([.. versions.Select(v => new CleanupVersionResult(v.Name, CleanupOutcome.Failed, 0, 0, message))], 0, false);
}
