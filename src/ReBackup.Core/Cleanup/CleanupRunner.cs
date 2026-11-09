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

/// <summary>Applies the current ignore rules to old versions of a plan: shortens their manifests and deletes what the rules ignore.</summary>
public static class CleanupRunner
{
    /// <summary>
    /// Cleans up <paramref name="versions"/> of <paramref name="plan"/> on <paramref name="target"/> by
    /// <paramref name="matcher"/> (spec 5.2). First the target is checked (<see cref="StoreGuard.CheckAsync"/>, nothing
    /// created) and the plan's versions are listed; a failure there fails every version and writes nothing. Then the
    /// versions go one by one, oldest first in the catalog's order:
    /// <list type="number">
    /// <item>A version the catalog does not list as owned is <see cref="CleanupOutcome.Gone"/>; one whose stamp
    /// (<see cref="VersionIndex.StampOfAsync"/>) is no longer <see cref="CleanupTarget.Stamp"/> is
    /// <see cref="CleanupOutcome.Changed"/>.</item>
    /// <item>The manifest is read (its storage stamp taken first) and pruned (<see cref="ManifestPruner.Prune"/>); when
    /// nothing is ignored the version is <see cref="CleanupOutcome.NothingToDo"/>.</item>
    /// <item><see cref="VersionMarkerNames.Pruning"/> is written durably with the paths to delete, then the shortened
    /// manifest replaces the old one, conditionally on the stamp read. A manifest replaced meanwhile
    /// (<see cref="StorageConflictException"/>) is never overwritten: the marker is removed, nothing is deleted, and the
    /// version is <see cref="CleanupOutcome.Changed"/>.</item>
    /// <item>The removed files are deleted in batches of <see cref="VersionRemover.BatchSize"/> (a file already missing
    /// is fine), then each removed folder, deepest first, when it is empty; then the marker is removed and the version
    /// handed to <paramref name="index"/>.</item>
    /// </list>
    /// The manifest never lists a file that is gone: it is shortened before anything is deleted. A version that fails
    /// after its marker is written keeps the marker, and the next start finishes the deletion; any failure of a version
    /// makes it <see cref="CleanupOutcome.Failed"/> with the message, and the run goes on with the next.
    /// </summary>
    /// <param name="machineName">The computer the marker names; <see cref="Environment.MachineName"/> when null.</param>
    /// <remarks>
    /// <paramref name="ct"/> is looked at only before a version starts: a version that has started is finished
    /// (deleting what its marker lists anyway), the versions after it are left out of the result and
    /// <see cref="CleanupResult.Canceled"/> is set. The index hand-off is best effort: a failure there leaves the index
    /// with the old stamp, and its next sync reads the version again.
    /// </remarks>
    /// <exception cref="NotSupportedException"><paramref name="plan"/> is incremental.</exception>
    public static async Task<CleanupResult> RunAsync(IStorage target, BackupPlan plan, IgnoreMatcher matcher,
        IReadOnlyList<CleanupTarget> versions, IVersionIndexSink? index, IProgress<CleanupProgress>? progress,
        CancellationToken ct, string? machineName = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(versions);
        if (plan.Incremental)
            throw new NotSupportedException("The cleanup of incremental plans is not available yet.");
        var host = machineName ?? Environment.MachineName;

        IReadOnlyList<VersionInfo> catalog;
        try
        {
            // Copies are never pruned in a pool's target; the store file is never created here.
            var guard = await StoreGuard.CheckAsync(target, plan, createIfMissing: false, ct).ConfigureAwait(false);
            if (guard != StoreGuardOutcome.Ok)
                return AllFailed(versions, CoreTexts.English(StoreGuard.MessageKey(guard)));
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
    /// Steps 1–6 of spec 5.2 for one version. Not cancelable: what it writes, it finishes (or leaves the marker for).
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
        if ("m:" + manifestEntry.Stamp != request.Stamp)
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

        // Marker first: from here on a crash leaves it, and the next start deletes what the manifest no longer lists.
        await PruningMarker.WriteAsync(target, version.Path,
            new PruningInfo(PruningMarker.FormatVersion, plan.Id, host, DateTime.UtcNow, pruned.RemovedFiles,
                pruned.RemovedFolders), ct).ConfigureAwait(false);
        try
        {
            await ManifestWriter.ReplaceAsync(target, manifestPath, manifest, manifestEntry.Stamp, ct).ConfigureAwait(false);
        }
        catch (StorageConflictException)
        {
            // Someone replaced the manifest meanwhile: it is kept, and nothing it may still list is deleted.
            await PruningMarker.RemoveAsync(target, version.Path, ct).ConfigureAwait(false);
            return Result(CleanupOutcome.Changed);
        }

        report(CleanupPhase.Deleting);
        var files = pruned.RemovedFiles.Select(path => StoragePath.Combine(version.Path, path)).ToList();
        for (var start = 0; start < files.Count; start += VersionRemover.BatchSize)
            await target.DeleteAsync(files.GetRange(start, Math.Min(VersionRemover.BatchSize, files.Count - start)), ct)
                .ConfigureAwait(false);   // a file that is missing already is not an error
        // Elsewhere folders vanish with their last file.
        if (target.Capabilities.HasFlag(StorageCapabilities.EmptyDirectories))
        {
            foreach (var folder in pruned.RemovedFolders)   // deepest first
            {
                try
                {
                    await target.DeleteAsync([StoragePath.Combine(version.Path, folder)], ct).ConfigureAwait(false);
                }
                catch (StorageConflictException)
                {
                    // Not empty: it holds something the manifest did not list, which is not ours to delete.
                }
            }
        }
        await PruningMarker.RemoveAsync(target, version.Path, ct).ConfigureAwait(false);

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
            catch (Exception)
            {
                // Deliberately broad: the version is clean; the index's next sync sees the new stamp and reads it again.
            }
        }
        return new CleanupVersionResult(version.Name, CleanupOutcome.Cleaned, pruned.RemovedFiles.Count, bytes, null);

        CleanupVersionResult Result(CleanupOutcome outcome) => new(version.Name, outcome, 0, 0, null);
    }

    private static CleanupResult AllFailed(IReadOnlyList<CleanupTarget> versions, string message) =>
        new([.. versions.Select(v => new CleanupVersionResult(v.Name, CleanupOutcome.Failed, 0, 0, message))], 0, false);
}
