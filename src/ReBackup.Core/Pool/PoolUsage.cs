using ReBackup.Core.Backup;
using ReBackup.Core.Versions;
using ReBackup.Storage;

namespace ReBackup.Core.Pool;

/// <summary>How much of an incremental plan's pool its versions hold (spec 5).</summary>
public static class PoolUsage
{
    /// <summary>
    /// For each of <paramref name="versions"/> (by <see cref="VersionInfo.Name"/>): the bytes of the distinct contents
    /// it references and none of the other <paramref name="versions"/> does, i.e. what deleting it alone and collecting
    /// would free. The manifests are read from the target. A version without a manifest references nothing.
    /// </summary>
    /// <remarks>
    /// Contents shared by several of the versions count for none of them, so deleting several versions can free more
    /// than the sum of their values, never less.
    /// </remarks>
    /// <exception cref="StorageException">A manifest cannot be read.</exception>
    /// <exception cref="System.Text.Json.JsonException">A manifest is not one.</exception>
    public static async Task<IReadOnlyDictionary<string, long>> UniqueBytesAsync(IStorage target,
        IReadOnlyList<VersionInfo> versions, CancellationToken ct)
    {
        var referencedBy = new List<(string Version, Dictionary<string, long> Sizes)>(versions.Count);
        var owners = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var version in versions)
        {
            ct.ThrowIfCancellationRequested();
            var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
            if (version.Ownership != VersionOwnership.NoManifest)
            {
                var stream = await target.OpenReadAsync(StoragePath.Combine(version.Path, VersionMarkerNames.Manifest), ct)
                    .ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    ManifestStream.Read(stream, file =>
                    {
                        if (PoolLayout.TryParseHex(file.Hash, out var hex))
                            sizes[hex.ToLowerInvariant()] = file.Size;
                    }, ct);
                }
            }
            foreach (var hash in sizes.Keys)
                owners[hash] = owners.GetValueOrDefault(hash) + 1;
            referencedBy.Add((version.Name, sizes));
        }

        var unique = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (version, sizes) in referencedBy)
            unique[version] = sizes.Where(pair => owners[pair.Key] == 1).Sum(pair => pair.Value);
        return unique;
    }

    /// <summary><see cref="MeasurePoolAsync"/> reports its count after this many blobs.</summary>
    public const int MeasureProgressEvery = 1000;

    /// <summary>
    /// The blobs in <paramref name="target"/>'s pool and their bytes, by listing
    /// <see cref="PoolLayout.PoolFolder"/> (<see cref="PoolListing"/>): read-only, without the lock. Only files named like a blob (32 hex digits) count.
    /// <paramref name="progress"/> gets the blobs counted so far every <see cref="MeasureProgressEvery"/> blobs and at
    /// the end. A pool that does not exist yet is (0, 0).
    /// </summary>
    /// <exception cref="StorageException">The pool cannot be listed.</exception>
    public static async Task<(int Blobs, long Bytes)> MeasurePoolAsync(IStorage target, IProgress<int>? progress,
        CancellationToken ct)
    {
        var blobs = 0;
        long bytes = 0;
        await PoolListing.ListAsync(target, entry =>
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory || entry.IsLink ||
                !PoolLayout.TryParseHex(PoolLayout.HashPrefix + StoragePath.Name(entry.Path), out _))
                return;
            blobs++;
            bytes += entry.Size;
            if (blobs % MeasureProgressEvery == 0)
                progress?.Report(blobs);
        }, ct).ConfigureAwait(false);
        if (blobs % MeasureProgressEvery != 0)
            progress?.Report(blobs);
        return (blobs, bytes);
    }

    /// <summary>
    /// The files in <paramref name="folders"/> (a full plan's version folders) and their bytes, by listing each folder
    /// recursively, <see cref="PoolListing.ParallelShards"/> at a time: read-only, without the lock. A folder that is gone
    /// counts nothing. <paramref name="progress"/> gets the files counted so far every <see cref="MeasureProgressEvery"/>
    /// files and at the end.
    /// </summary>
    /// <exception cref="StorageException">A folder cannot be listed.</exception>
    public static async Task<(int Files, long Bytes)> MeasureFoldersAsync(IStorage target, IReadOnlyList<string> folders,
        IProgress<int>? progress, CancellationToken ct)
    {
        var gate = new object();
        var files = 0;
        long bytes = 0;
        await Parallel.ForEachAsync(folders,
            new ParallelOptions { MaxDegreeOfParallelism = PoolListing.ParallelShards, CancellationToken = ct },
            async (folder, token) =>
            {
                try
                {
                    await foreach (var entry in target.ListAsync(folder, recursive: true, token).ConfigureAwait(false))
                    {
                        if (entry.IsDirectory)
                            continue;
                        lock (gate)
                        {
                            files++;
                            bytes += entry.Size;
                            if (files % MeasureProgressEvery == 0)
                                progress?.Report(files);
                        }
                    }
                }
                catch (StorageNotFoundException)
                {
                    // Deleted meanwhile: it takes no space.
                }
            }).ConfigureAwait(false);
        if (files % MeasureProgressEvery != 0)
            progress?.Report(files);
        return (files, bytes);
    }
}
