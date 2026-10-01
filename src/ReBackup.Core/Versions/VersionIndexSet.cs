using ReBackup.Core.Backup;

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

    public void Add(string planId, VersionInfo version, BackupManifest manifest) => For(planId).Add(version, manifest);
}
