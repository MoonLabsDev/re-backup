using ReBackup.Core.Backup;
using ReBackup.Core.Localization;
using ReBackup.Core.Versions;
using ReBackup.Storage;

namespace ReBackup.Core.Pool;

/// <summary>
/// What <see cref="PoolCollector.CollectAsync"/> did. <paramref name="SkippedReason"/> is null when the pool was swept;
/// otherwise the English text (<see cref="CoreTexts"/>, <c>core.gc.*</c>) of why nothing was deleted.
/// </summary>
public sealed record CollectResult(int BlobsDeleted, long BytesFreed, string? SkippedReason);

/// <summary>
/// Garbage collection of an incremental plan's pool (spec 6): deletes the blobs no version's manifest names any more.
/// It only ever deletes blobs that no manifest on the target references; when it cannot be sure of that, it deletes nothing.
/// </summary>
public static class PoolCollector
{
    /// <summary>How many paths one <see cref="IStorage.DeleteAsync"/> call gets.</summary>
    public const int DeleteBatchSize = 1000;

    /// <summary>
    /// Collects the pool of plan <paramref name="planId"/> in <paramref name="target"/>; the caller holds the target's lock.
    /// <list type="number">
    /// <item>Skips (<c>core.gc.skippedMarkers</c>) while any folder in the target's root still carries a pending or
    /// deleting marker, whoever wrote it, or has a ".partial" / ".deleting" name: such a folder may reference blobs.</item>
    /// <item>Marks the hashes named by the <see cref="VersionMarkerNames.Manifest"/> of every folder in the target's root
    /// that has one, read from the target, whatever plan id or folder name it carries: a plan that was renamed keeps its
    /// old version names, and a folder renamed by hand still restores. Extra marks only keep blobs (the pool belongs to one
    /// plan). A manifest that cannot be looked at or fully read aborts (<c>core.gc.manifestUnreadable</c>).</item>
    /// <item>Deletes every blob whose hash is not marked, in batches of <see cref="DeleteBatchSize"/>, then the
    /// <c>xx/yy</c> folders that became empty. Hashes compare in lower case. A file in the pool that is not a blob at its
    /// own path is left alone.</item>
    /// </list>
    /// An interruption is harmless: what was deleted was unreferenced, and the next collection continues.
    /// </summary>
    /// <exception cref="StorageException">The target cannot be listed or a deletion failed; blobs deleted before stay deleted.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public static async Task<CollectResult> CollectAsync(IStorage target, string planId, string planName,
        CancellationToken ct)
    {
        var (marked, skipped) = await MarkAsync(target, ct).ConfigureAwait(false);
        if (skipped is not null)
            return new CollectResult(0, 0, skipped);

        var (pool, doomed, bytes) = await ListPoolAsync(target, marked, ct).ConfigureAwait(false);
        var deleted = 0;
        long freed = 0;
        for (var start = 0; start < doomed.Count; start += DeleteBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = doomed.GetRange(start, Math.Min(DeleteBatchSize, doomed.Count - start));
            await target.DeleteAsync(batch, ct).ConfigureAwait(false);
            deleted += batch.Count;
            freed += bytes.GetRange(start, batch.Count).Sum();
        }

        if (deleted > 0)
        {
            var gone = doomed.ToHashSet(StringComparer.Ordinal);
            await DeleteEmptyShardFoldersAsync(target, pool.Where(entry => !gone.Contains(entry.Path)).ToList(), ct)
                .ConfigureAwait(false);
        }
        return new CollectResult(deleted, freed, null);
    }

    /// <summary>
    /// Whether a collection would sweep the pool now: null when it would, otherwise the English text (<c>core.gc.*</c>) of
    /// why it would be skipped (a folder in transit, a manifest that cannot be read). It checks the preconditions and reads
    /// every manifest as <see cref="CollectAsync"/> does, and deletes nothing. Used before deleting versions to free space,
    /// which frees nothing unless the pool is collected afterwards.
    /// </summary>
    /// <exception cref="StorageException">The target cannot be listed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public static async Task<string?> CanCollectAsync(IStorage target, CancellationToken ct) =>
        (await MarkAsync(target, ct).ConfigureAwait(false)).Skipped;

    /// <summary>
    /// The preconditions and the mark phase: the hashes every manifest in the target's root names, or why the collection
    /// is skipped (then the hashes are incomplete).
    /// </summary>
    private static async Task<(HashSet<string> Marked, string? Skipped)> MarkAsync(IStorage target, CancellationToken ct)
    {
        var marked = new HashSet<string>(StringComparer.Ordinal);
        var folders = await PoolManifests.RootFoldersAsync(target, ct).ConfigureAwait(false);
        if (await HasFoldersInTransitAsync(target, folders, ct).ConfigureAwait(false))
            return (marked, CoreTexts.English("core.gc.skippedMarkers"));
        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            if (!await TryMarkAsync(target, folder, marked, ct).ConfigureAwait(false))
                return (marked, CoreTexts.English("core.gc.manifestUnreadable", ("version", StoragePath.Name(folder))));
        }
        return (marked, null);
    }

    /// <summary>
    /// Whether one of <paramref name="folders"/> carries <see cref="VersionMarkerNames.Pending"/> or
    /// <see cref="VersionMarkerNames.Deleting"/>, or has a ".partial" / ".deleting" name. A folder name the storage cannot
    /// address cannot hold a manifest either.
    /// </summary>
    private static async Task<bool> HasFoldersInTransitAsync(IStorage target, List<string> folders, CancellationToken ct)
    {
        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            if (VersionName.IsTransient(StoragePath.Name(folder)))
                return true;
            try
            {
                if (await target.StatAsync(StoragePath.Combine(folder, VersionMarkerNames.Pending), ct).ConfigureAwait(false) is not null ||
                    await target.StatAsync(StoragePath.Combine(folder, VersionMarkerNames.Deleting), ct).ConfigureAwait(false) is not null)
                    return true;
            }
            catch (ArgumentException)
            {
                // Not addressable on this storage: no run wrote it.
            }
        }
        return false;
    }

    /// <summary>
    /// Adds the hashes the folder's manifest names (lower case) to <paramref name="marked"/>. True when the folder has no
    /// manifest (or a name this storage cannot address, so nobody can read one there) or its manifest was read to the end;
    /// false when the manifest could not be looked at or read, for whatever reason.
    /// </summary>
    private static async Task<bool> TryMarkAsync(IStorage target, string folder, HashSet<string> marked,
        CancellationToken ct) =>
        await PoolManifests.TryReadAsync(target, folder, file =>
        {
            if (PoolLayout.TryParseHex(file.Hash, out var hex))
                marked.Add(hex.ToLowerInvariant());
        }, ct).ConfigureAwait(false) != ManifestRead.Unreadable;

    /// <summary>
    /// Everything in the pool, and the blobs in it whose hash is not marked, with their sizes. A pool that does not exist
    /// yet is empty.
    /// </summary>
    private static async Task<(List<StorageEntry> Pool, List<string> Doomed, List<long> Sizes)> ListPoolAsync(
        IStorage target, HashSet<string> marked, CancellationToken ct)
    {
        var pool = new List<StorageEntry>();
        var doomed = new List<string>();
        var sizes = new List<long>();
        try
        {
            await foreach (var entry in target.ListAsync(PoolLayout.PoolFolder, recursive: true, ct).ConfigureAwait(false))
            {
                pool.Add(entry);
                if (entry.IsDirectory || entry.IsLink ||
                    !PoolLayout.TryParseHex(PoolLayout.HashPrefix + StoragePath.Name(entry.Path), out var hex))
                    continue;
                var lower = hex.ToLowerInvariant();
                if (marked.Contains(lower) ||
                    !entry.Path.Equals(PoolLayout.BlobPath(PoolLayout.HashPrefix + lower), StringComparison.OrdinalIgnoreCase))
                    continue;   // referenced, or not a blob at its own path: never ours to delete
                doomed.Add(entry.Path);
                sizes.Add(entry.Size);
            }
        }
        catch (StorageNotFoundException)
        {
            // No pool yet.
        }
        return (pool, doomed, sizes);
    }

    /// <summary>
    /// Deletes the shard folders (<c>re-pool/xx/yy</c>, then <c>re-pool/xx</c>) that hold nothing any more, judged by
    /// <paramref name="remaining"/>: what the pool listing showed, less the blobs just deleted. On a storage without real
    /// folders there are none. A folder that is not empty after all stays.
    /// </summary>
    private static async Task DeleteEmptyShardFoldersAsync(IStorage target, List<StorageEntry> remaining,
        CancellationToken ct)
    {
        static int Depth(string path) => path.Count(c => c == '/');
        var children = remaining.ToLookup(entry => StoragePath.Parent(entry.Path), entry => entry.Path, StringComparer.Ordinal);
        var folders = remaining.Where(entry => entry.IsDirectory && !entry.IsLink).Select(entry => entry.Path).ToList();

        var emptyShards = folders.Where(f => Depth(f) == 2 && !children[f].Any()).ToList();
        var emptyShardSet = emptyShards.ToHashSet(StringComparer.Ordinal);
        var emptyTops = folders.Where(f => Depth(f) == 1 && children[f].All(emptyShardSet.Contains)).ToList();

        await DeleteFoldersAsync(target, emptyShards, ct).ConfigureAwait(false);
        await DeleteFoldersAsync(target, emptyTops, ct).ConfigureAwait(false);
    }

    private static async Task DeleteFoldersAsync(IStorage target, List<string> folders, CancellationToken ct)
    {
        for (var start = 0; start < folders.Count; start += DeleteBatchSize)
        {
            var batch = folders.GetRange(start, Math.Min(DeleteBatchSize, folders.Count - start));
            try
            {
                await target.DeleteAsync(batch, ct).ConfigureAwait(false);
            }
            catch (StorageConflictException)
            {
                // Not empty after all: it stays, and so may the rest of this batch until the next collection.
            }
        }
    }
}
