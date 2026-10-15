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
}
