using ReBackup.Core.Backup;
using ReBackup.Core.Versions;
using ReBackup.Storage;

namespace ReBackup.Core.Pool;

/// <summary>What is wrong with one file of one version.</summary>
public enum PoolProblemKind
{
    /// <summary>The blob the manifest names is not in the pool.</summary>
    Missing,

    /// <summary>The blob exists, but its content does not hash to its name.</summary>
    Corrupt,

    /// <summary>The version's manifest cannot be read, so nothing of the version can be checked.</summary>
    ManifestUnreadable,
}

/// <summary>
/// One finding of <see cref="PoolVerifier"/>. <paramref name="Version"/> is the version folder's name,
/// <paramref name="Path"/> the file in it (the manifest itself for <see cref="PoolProblemKind.ManifestUnreadable"/>) and
/// <paramref name="Hash"/> the hash the manifest names (empty for an unreadable manifest).
/// </summary>
public sealed record PoolProblem(string Version, string Path, string Hash, PoolProblemKind Kind);

/// <summary>
/// Progress of <see cref="PoolVerifier.VerifyAsync"/>: first one step per version folder (<paramref name="Current"/> is
/// its name), then, when thorough, one per distinct blob read (<paramref name="Current"/> is its path); each phase counts
/// from 0 to its own <paramref name="Total"/>.
/// </summary>
public readonly record struct VerifyProgress(int Done, int Total, string Current);

/// <summary>Verify backup (spec 9): a read-only check of an incremental plan's target. Nothing is repaired.</summary>
public static class PoolVerifier
{
    private const int BufferSize = 1024 * 1024;

    private sealed record Reference(string Version, string Path, string Hash);

    /// <summary>
    /// Checks every version of the pool in <paramref name="target"/> (every folder in the root with a manifest, whatever
    /// plan id or name it carries, as the collector sees them); the caller holds the target's "verify" lock and this
    /// method writes nothing. Quick: every hash a manifest names must be in the pool listing. Thorough: additionally every
    /// such blob is read once and hashed again, however many versions use it. Hashes compare in lower case. A blob that
    /// is missing or corrupt is reported once for each (version, path) that names it.
    /// </summary>
    /// <returns>The problems found, by version and in manifest order; empty when the pool is healthy.</returns>
    /// <exception cref="StorageException">The target cannot be listed or a blob cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public static async Task<IReadOnlyList<PoolProblem>> VerifyAsync(IStorage target, string planId, string planName,
        bool thorough, IProgress<VerifyProgress>? progress, CancellationToken ct)
    {
        var folders = await PoolManifests.RootFoldersAsync(target, ct).ConfigureAwait(false);
        folders.Sort(StringComparer.Ordinal);
        var pool = await ListBlobsAsync(target, ct).ConfigureAwait(false);

        var problems = new List<PoolProblem>();
        var references = new Dictionary<string, List<Reference>>(StringComparer.Ordinal);
        for (var i = 0; i < folders.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var version = StoragePath.Name(folders[i]);
            progress?.Report(new VerifyProgress(i, folders.Count, version));
            var found = new List<ManifestFile>();
            var read = await PoolManifests.TryReadAsync(target, folders[i], found.Add, ct).ConfigureAwait(false);
            if (read == ManifestRead.Unreadable)
                problems.Add(new PoolProblem(version, VersionMarkerNames.Manifest, "", PoolProblemKind.ManifestUnreadable));
            else if (read == ManifestRead.Read)
                foreach (var file in found)
                    CheckPresence(version, file, pool, thorough, problems, references);
        }
        progress?.Report(new VerifyProgress(folders.Count, folders.Count, ""));

        if (thorough)
            await CheckContentsAsync(target, references, problems, progress, ct).ConfigureAwait(false);
        return problems;
    }

    /// <summary>Reports <paramref name="file"/> as missing, or (thorough) remembers it for the content check.</summary>
    private static void CheckPresence(string version, ManifestFile file, HashSet<string> pool, bool thorough,
        List<PoolProblem> problems, Dictionary<string, List<Reference>> references)
    {
        // A hash that is no pool hash names no blob at all.
        if (!PoolLayout.TryParseHex(file.Hash, out var hex) ||
            !pool.Contains(PoolLayout.BlobPath(PoolLayout.HashPrefix + hex)))
        {
            problems.Add(new PoolProblem(version, file.Path, file.Hash, PoolProblemKind.Missing));
            return;
        }
        if (!thorough)
            return;
        var key = hex.ToLowerInvariant();
        if (!references.TryGetValue(key, out var users))
            references[key] = users = [];
        users.Add(new Reference(version, file.Path, file.Hash));
    }

    /// <summary>Reads every referenced blob once; a blob whose content hashes differently is corrupt for all its users.</summary>
    private static async Task CheckContentsAsync(IStorage target, Dictionary<string, List<Reference>> references,
        List<PoolProblem> problems, IProgress<VerifyProgress>? progress, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        var done = 0;
        foreach (var (hex, users) in references)
        {
            ct.ThrowIfCancellationRequested();
            var blob = PoolLayout.BlobPath(PoolLayout.HashPrefix + hex);
            progress?.Report(new VerifyProgress(done++, references.Count, blob));
            if (await ClassifyAsync(target, blob, hex, buffer, ct).ConfigureAwait(false) is { } kind)
                foreach (var user in users)
                    problems.Add(new PoolProblem(user.Version, user.Path, user.Hash, kind));
        }
        progress?.Report(new VerifyProgress(references.Count, references.Count, ""));
    }

    /// <summary>Null when the blob's content matches its name.</summary>
    private static async Task<PoolProblemKind?> ClassifyAsync(IStorage target, string blob, string hex, byte[] buffer,
        CancellationToken ct)
    {
        try
        {
            var stream = await target.OpenReadAsync(blob, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var (actual, _) = await PoolWriter.HashAsync(stream, buffer, null, ct).ConfigureAwait(false);
                return actual.Equals(PoolLayout.HashPrefix + hex, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : PoolProblemKind.Corrupt;
            }
        }
        catch (StorageNotFoundException)
        {
            return PoolProblemKind.Missing;   // gone since the listing
        }
    }

    /// <summary>The paths (compared ignoring case) of the files in the pool. A pool that does not exist yet is empty.</summary>
    private static async Task<HashSet<string>> ListBlobsAsync(IStorage target, CancellationToken ct)
    {
        var blobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await foreach (var entry in target.ListAsync(PoolLayout.PoolFolder, recursive: true, ct).ConfigureAwait(false))
                if (!entry.IsDirectory && !entry.IsLink)
                    blobs.Add(entry.Path);
        }
        catch (StorageNotFoundException)
        {
            // No pool yet.
        }
        return blobs;
    }
}
