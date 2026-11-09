using ReBackup.Core.Ignore;
using ReBackup.Core.Versions;

namespace ReBackup.Core.Cleanup;

/// <summary>Reckons from the local version index what applying the current ignore rules to old versions would remove.</summary>
public static class CleanupScanner
{
    private const int EntriesPerCancelCheck = 1024;

    /// <summary>
    /// Walks the index tree of each version in <paramref name="versionIds"/> and applies <paramref name="matcher"/>. An
    /// ignored folder counts as a whole from its totals in the index; an ignored file counts with its size; every hit
    /// is attributed to the rule that decided it. With <paramref name="pool"/> the pool content freed is reckoned too:
    /// a hash counts once, with its largest size, when every reference to it in the scanned versions is removed and no
    /// other version in the index references it; a removed file without a hash counts with its size. A version the
    /// index does not know yields an empty row (no name, no stamp).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static CleanupScan Scan(VersionIndex index, IReadOnlyCollection<long> versionIds, IgnoreMatcher matcher,
        bool pool, CancellationToken ct = default)
    {
        var names = index.Versions().ToDictionary(v => v.Id, v => v.Name);
        var rules = new Dictionary<(string Rule, string Origin), RuleTotals>();
        var removed = new Dictionary<long, long>();   // hash -> largest size
        var kept = new HashSet<long>();
        long removedWithoutHash = 0;
        var versions = new List<CleanupVersionScan>(versionIds.Count);

        foreach (var versionId in versionIds)
        {
            ct.ThrowIfCancellationRequested();
            var stamp = index.StampOf(versionId) ?? "";
            var entries = index.EntriesOf(versionId);
            var folders = new Dictionary<string, IgnoreResult>(StringComparer.OrdinalIgnoreCase);
            var files = 0;
            long bytes = 0;

            for (var i = 0; i < entries.Count; i++)
            {
                if (i % EntriesPerCancelCheck == 0)
                    ct.ThrowIfCancellationRequested();
                var entry = entries[i];
                var parent = ParentResult(entry.Path, folders, matcher);

                if (parent.IsIgnored)
                {
                    // Inside a folder that was already counted as a whole.
                    if (entry.IsDirectory)
                        folders[entry.Path] = parent;
                    else if (pool)
                        Remove(entry);
                    continue;
                }

                var result = matcher.MatchBelow(parent, entry.Path, entry.IsDirectory);
                if (entry.IsDirectory)
                    folders[entry.Path] = result;

                if (result.IsIgnored)
                {
                    files += entry.Files;
                    bytes += entry.Size;
                    var key = (result.Pattern!.Text, result.Pattern.Origin);
                    if (!rules.TryGetValue(key, out var totals))
                        rules[key] = totals = new RuleTotals();
                    totals.Add(entry);
                    if (!entry.IsDirectory && pool)
                        Remove(entry);
                }
                else if (!entry.IsDirectory && pool && entry.Hash is { } hash)
                {
                    kept.Add(hash);
                }
            }

            versions.Add(new CleanupVersionScan(versionId, names.GetValueOrDefault(versionId, ""), stamp, files, bytes));
        }

        var byRule = rules
            .Select(r => new CleanupRuleHit(r.Key.Rule, r.Key.Origin, r.Value.Files, r.Value.Bytes, r.Value.LargestPath,
                r.Value.LargestBytes))
            .OrderByDescending(h => h.Bytes)
            .ToArray();

        return new CleanupScan(versions, byRule, pool ? PoolBytesFreed() : null);

        void Remove(IndexEntry file)
        {
            if (file.Hash is not { } hash)
                removedWithoutHash += file.Size;
            else if (!removed.TryGetValue(hash, out var size) || file.Size > size)
                removed[hash] = file.Size;
        }

        long PoolBytesFreed()
        {
            var scanned = versionIds.ToHashSet();
            var others = index.HashesOf(names.Keys.Where(id => !scanned.Contains(id)).ToArray());
            return removedWithoutHash + removed
                .Where(r => !kept.Contains(r.Key) && !others.Contains(r.Key))
                .Sum(r => r.Value);
        }
    }

    /// <summary>
    /// The decision for the folder an entry lies in: the one already made in this walk, else a full match of that
    /// folder (the index does not rely on adjacency), and "not matched" at the source root.
    /// </summary>
    private static IgnoreResult ParentResult(string path, Dictionary<string, IgnoreResult> folders, IgnoreMatcher matcher)
    {
        var slash = path.LastIndexOf('/');
        if (slash < 0)
            return IgnoreResult.NotMatched;
        var parentPath = path[..slash];
        if (!folders.TryGetValue(parentPath, out var result))
            folders[parentPath] = result = matcher.Match(parentPath, isDirectory: true);
        return result;
    }

    /// <summary>The running totals of one rule.</summary>
    private sealed class RuleTotals
    {
        public int Files { get; private set; }
        public long Bytes { get; private set; }
        public string LargestPath { get; private set; } = "";
        public long LargestBytes { get; private set; } = -1;

        public void Add(IndexEntry hit)
        {
            Files += hit.Files;
            Bytes += hit.Size;
            if (hit.Size > LargestBytes)
            {
                LargestPath = hit.Path;
                LargestBytes = hit.Size;
            }
        }
    }
}
