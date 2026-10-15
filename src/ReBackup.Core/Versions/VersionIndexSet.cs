using Microsoft.Data.Sqlite;
using ReBackup.Core.Backup;
using ReBackup.Storage;

namespace ReBackup.Core.Versions;

/// <summary>The version indexes of all plans: one database per plan id in one folder, opened on first use.</summary>
public sealed class VersionIndexSet : IVersionIndexSink
{
    private readonly Dictionary<string, VersionIndex> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public VersionIndexSet(string directory) => Directory = directory;

    /// <summary><c>%LOCALAPPDATA%\ReBackup\index</c>: a machine-local cache, never in the (roaming) config folder.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReBackup", "index");

    public string Directory { get; }

    /// <summary><c>&lt;directory&gt;\&lt;planId&gt;.db</c>.</summary>
    /// <exception cref="ArgumentException">The id cannot be a file name.</exception>
    public string PathFor(string planId)
    {
        if (string.IsNullOrWhiteSpace(planId) || planId is "." or ".." ||
            planId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"\"{planId}\" is not a usable plan id.", nameof(planId));
        return Path.Combine(Directory, planId + ".db");
    }

    /// <summary>The plan's index, opened (and created or rebuilt when needed) on first use.</summary>
    public VersionIndex For(string planId)
    {
        var path = PathFor(planId);
        lock (_gate)
        {
            if (!_open.TryGetValue(planId, out var index))
            {
                index = VersionIndex.Open(path);
                _open[planId] = index;
            }
            return index;
        }
    }

    /// <summary>
    /// Deletes the plan's database with its <c>-wal</c> and <c>-shm</c> files; a missing database is fine. The cached
    /// instance is dropped first and pooled connections are released, so Windows holds no lock on the files.
    /// </summary>
    public void Delete(string planId)
    {
        var path = PathFor(planId);
        lock (_gate)
        {
            _open.Remove(planId);
            DeleteFiles(path);
        }
    }

    /// <summary>
    /// Deletes the databases of plans that are not in <paramref name="planIds"/> (compared without regard to case), e.g.
    /// of plans that were removed. Returns how many were deleted.
    /// </summary>
    public int PruneOrphans(IReadOnlySet<string> planIds)
    {
        if (!System.IO.Directory.Exists(Directory))
            return 0;
        var known = new HashSet<string>(planIds, StringComparer.OrdinalIgnoreCase);
        var pruned = 0;
        lock (_gate)
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.db"))
            {
                var planId = Path.GetFileNameWithoutExtension(file);
                if (known.Contains(planId))
                    continue;
                _open.Remove(planId);
                DeleteFiles(file);
                pruned++;
            }
        }
        return pruned;
    }

    private static void DeleteFiles(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            File.Delete(path);
    }

    public void Add(string planId, IStorage target, VersionInfo version, BackupManifest manifest) =>
        For(planId).Add(version, manifest, target);
}
