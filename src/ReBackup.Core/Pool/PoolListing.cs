using ReBackup.Core.Backup;
using ReBackup.Storage;

namespace ReBackup.Core.Pool;

/// <summary>
/// Lists everything in a target's pool (<see cref="PoolLayout.PoolFolder"/>). On a storage with real folders (a folder,
/// a network share, Google Drive: <see cref="StorageCapabilities.EmptyDirectories"/>) its first level, then each
/// first-level shard folder recursively, <see cref="ParallelShards"/> at a time: much faster than one recursive listing,
/// which walks the up to 65,536 shard folders one after the other, and it finds exactly the same entries. A storage
/// without folders (S3) lists the whole pool by key prefix anyway: one recursive listing takes the fewest requests.
/// </summary>
public static class PoolListing
{
    /// <summary>How many shard folders are listed at the same time.</summary>
    public const int ParallelShards = BackupRunner.ParallelS3Copies;

    /// <summary>
    /// Hands every entry below the pool to <paramref name="onEntry"/>, one call at a time (never two at once), in no
    /// particular order (an entry a storage lists twice may come twice). A pool that does not exist yet has no entries.
    /// </summary>
    /// <exception cref="StorageException">
    /// A listing failed, a shard folder among them (also one that vanished after the first level was listed: that is never
    /// read as a missing pool). Entries handed over before stay handed over; the listing is incomplete.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public static async Task ListAsync(IStorage target, Action<StorageEntry> onEntry, CancellationToken ct)
    {
        var gate = new object();
        var parallel = target.Capabilities.HasFlag(StorageCapabilities.EmptyDirectories);
        var shards = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await foreach (var entry in target.ListAsync(PoolLayout.PoolFolder, recursive: !parallel, ct).ConfigureAwait(false))
            {
                onEntry(entry);
                // A storage that allows two folders of one name (Google Drive) lists the name twice; it is listed once.
                if (parallel && entry.IsDirectory && !entry.IsLink && seen.Add(entry.Path))
                    shards.Add(entry.Path);
            }
        }
        catch (StorageNotFoundException)
        {
            return;   // no pool yet
        }

        await Parallel.ForEachAsync(shards, new ParallelOptions { MaxDegreeOfParallelism = ParallelShards, CancellationToken = ct },
            async (shard, token) =>
            {
                try
                {
                    await foreach (var entry in target.ListAsync(shard, recursive: true, token).ConfigureAwait(false))
                    {
                        lock (gate)
                            onEntry(entry);
                    }
                }
                catch (StorageNotFoundException ex)
                {
                    throw new StorageIOException(shard, ex.Message, ex);
                }
            }).ConfigureAwait(false);
    }
}
