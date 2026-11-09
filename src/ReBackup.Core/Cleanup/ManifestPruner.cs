using ReBackup.Core.Backup;
using ReBackup.Core.Ignore;

namespace ReBackup.Core.Cleanup;

/// <summary>What <see cref="ManifestPruner.Prune"/> took out of a manifest. Folders are listed deepest first.</summary>
public sealed record PruneResult(IReadOnlyList<string> RemovedFiles, IReadOnlyList<string> RemovedFolders);

/// <summary>Shortens a version's manifest by the current ignore rules.</summary>
public static class ManifestPruner
{
    /// <summary>
    /// Removes from <paramref name="manifest"/> every file <paramref name="matcher"/> ignores and every ignored folder in
    /// its <see cref="BackupManifest.Directories"/>, and sets <see cref="BackupManifest.FileCount"/> and
    /// <see cref="BackupManifest.TotalBytes"/> to match. The removed folders are the ignored ones listed in the manifest or
    /// found in the parent chain of a removed file: the outermost ignored folder and every sub-folder of it that is known.
    /// A folder that only loses some of its files is not ignored, so it stays.
    /// </summary>
    public static PruneResult Prune(BackupManifest manifest, IgnoreMatcher matcher)
    {
        var removedFiles = new List<string>();
        var kept = new List<ManifestFile>(manifest.Files.Count);
        foreach (var file in manifest.Files)
        {
            if (matcher.Match(file.Path, false).IsIgnored)
                removedFiles.Add(file.Path);
            else
                kept.Add(file);
        }

        var candidates = new HashSet<string>(manifest.Directories, StringComparer.Ordinal);
        foreach (var path in removedFiles)
        {
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                candidates.Add(path[..slash]);
        }
        var removedFolders = candidates.Where(d => matcher.Match(d, true).IsIgnored)
            .OrderByDescending(Depth).ThenBy(d => d, StringComparer.Ordinal).ToList();

        var gone = new HashSet<string>(removedFolders, StringComparer.Ordinal);
        manifest.Files = kept;
        manifest.Directories = [.. manifest.Directories.Where(d => !gone.Contains(d))];
        manifest.FileCount = kept.Count;
        manifest.TotalBytes = kept.Sum(f => f.Size);
        return new PruneResult(removedFiles, removedFolders);
    }

    private static int Depth(string folder) => folder.Count(c => c == '/');
}
