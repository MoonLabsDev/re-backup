using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ReBackup.Core.Versions;

public sealed partial class VersionIndex
{
    /// <summary>Search results are capped at this many entries.</summary>
    public const int SearchLimit = 500;

    /// <summary>The id of a path ("" = the version root, forward slashes); null when no version has it.</summary>
    public long? FindPath(string relativePath, bool isDirectory)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, "SELECT id FROM paths WHERE path = $path AND is_dir = $dir");
        command.Parameters.AddWithValue("$path", relativePath.Replace('\\', '/').Trim('/'));
        command.Parameters.AddWithValue("$dir", isDirectory ? 1 : 0);
        return command.ExecuteScalar() as long?;
    }

    /// <summary>
    /// The folders and files directly inside a folder of a version, folders first, then by name. With
    /// <paramref name="otherVersionId"/>, entries that exist only in that version are included too
    /// (<see cref="IndexChild.InVersion"/> null).
    /// </summary>
    public IReadOnlyList<IndexChild> Children(long versionId, long folderPathId, long? otherVersionId = null)
    {
        using var connection = OpenConnection();
        var own = ChildStats(connection, versionId, folderPathId);
        var other = otherVersionId is { } otherId ? ChildStats(connection, otherId, folderPathId) : [];

        var children = new List<IndexChild>(own.Count + other.Count);
        foreach (var (pathId, entry) in own)
        {
            other.TryGetValue(pathId, out var inOther);
            children.Add(new IndexChild(pathId, entry.Name, entry.Path, entry.IsDirectory, entry.Stats, inOther.Stats));
        }
        foreach (var (pathId, entry) in other)
        {
            if (!own.ContainsKey(pathId))
                children.Add(new IndexChild(pathId, entry.Name, entry.Path, entry.IsDirectory, null, entry.Stats));
        }

        children.Sort((a, b) => a.IsDirectory != b.IsDirectory
            ? (a.IsDirectory ? -1 : 1)
            : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return children;
    }

    /// <summary>
    /// The status of every file and folder of version A and version B (§11): Added = only in B, Deleted = only in A;
    /// files in both are compared by hash when both have one, otherwise by size and time. A folder in both is Changed
    /// when anything below it differs.
    /// </summary>
    public IReadOnlyDictionary<long, DiffStatus> Compare(long versionA, long versionB)
    {
        using var connection = OpenConnection();
        var status = new Dictionary<long, DiffStatus>();
        var dirParents = new Dictionary<long, long?>();
        var changedFileParents = new List<long>();

        // Folders: in A (and maybe B), then only in B.
        using (var command = Command(connection, null, """
                   SELECT d.path_id, p.parent_id,
                          EXISTS (SELECT 1 FROM dirs o WHERE o.version_id = $b AND o.path_id = d.path_id)
                   FROM dirs d JOIN paths p ON p.id = d.path_id WHERE d.version_id = $a
                   UNION ALL
                   SELECT d.path_id, p.parent_id, -1
                   FROM dirs d JOIN paths p ON p.id = d.path_id WHERE d.version_id = $b
                     AND NOT EXISTS (SELECT 1 FROM dirs o WHERE o.version_id = $a AND o.path_id = d.path_id)
                   """))
        {
            command.Parameters.AddWithValue("$a", versionA);
            command.Parameters.AddWithValue("$b", versionB);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var pathId = reader.GetInt64(0);
                dirParents[pathId] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                status[pathId] = reader.GetInt64(2) switch
                {
                    1 => DiffStatus.Unchanged,
                    0 => DiffStatus.Deleted,
                    _ => DiffStatus.Added,
                };
            }
        }

        // Files: in A (compared with B), then only in B.
        using (var command = Command(connection, null, """
                   SELECT a.path_id, p.parent_id, a.size, a.mtime_ticks, a.hash, b.size, b.mtime_ticks, b.hash
                   FROM files a JOIN paths p ON p.id = a.path_id
                   LEFT JOIN files b ON b.version_id = $b AND b.path_id = a.path_id
                   WHERE a.version_id = $a
                   UNION ALL
                   SELECT b.path_id, p.parent_id, NULL, NULL, NULL, b.size, b.mtime_ticks, b.hash
                   FROM files b JOIN paths p ON p.id = b.path_id
                   WHERE b.version_id = $b
                     AND NOT EXISTS (SELECT 1 FROM files a WHERE a.version_id = $a AND a.path_id = b.path_id)
                   """))
        {
            command.Parameters.AddWithValue("$a", versionA);
            command.Parameters.AddWithValue("$b", versionB);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var pathId = reader.GetInt64(0);
                var inA = !reader.IsDBNull(2);
                var inB = !reader.IsDBNull(5);
                var fileStatus = !inB ? DiffStatus.Deleted
                    : !inA ? DiffStatus.Added
                    : FileDiffers(reader) ? DiffStatus.Changed
                    : DiffStatus.Unchanged;
                status[pathId] = fileStatus;
                if (fileStatus != DiffStatus.Unchanged && !reader.IsDBNull(1))
                    changedFileParents.Add(reader.GetInt64(1));
            }
        }

        // Roll up: a folder present in both versions is Changed when anything below it is not Unchanged.
        foreach (var parent in changedFileParents)
        {
            for (long? dir = parent; dir is { } id; dir = dirParents.GetValueOrDefault(id))
            {
                var current = status.GetValueOrDefault(id, DiffStatus.Unchanged);
                if (current == DiffStatus.Changed)
                    break;   // everything above is Changed already
                if (current == DiffStatus.Unchanged)
                    status[id] = DiffStatus.Changed;
            }
        }
        return status;
    }

    /// <summary>
    /// A file in every indexed version, oldest first, with its status against the previous version that had it.
    /// </summary>
    public IReadOnlyList<HistoryEntry> History(long filePathId)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            SELECT v.id, v.name, v.local_time, v.ownership, v.source, v.origin, v.file_count, v.total_bytes,
                   f.size, f.mtime_ticks, f.hash
            FROM versions v LEFT JOIN files f ON f.version_id = v.id AND f.path_id = $path
            ORDER BY v.local_time, v.name
            """);
        command.Parameters.AddWithValue("$path", filePathId);
        using var reader = command.ExecuteReader();

        var history = new List<HistoryEntry>();
        (long Size, long Mtime, long? Hash)? lastPresent = null;
        var previousPresent = false;
        while (reader.Read())
        {
            var version = ReadVersion(reader, 0);
            if (reader.IsDBNull(8))
            {
                history.Add(new HistoryEntry(version, false, null, null,
                    previousPresent ? HistoryStatus.Deleted : HistoryStatus.Absent));
                previousPresent = false;
                continue;
            }

            var current = (Size: reader.GetInt64(8), Mtime: reader.GetInt64(9), Hash: reader.IsDBNull(10) ? (long?)null : reader.GetInt64(10));
            var historyStatus = lastPresent is not { } last ? HistoryStatus.New
                : Differs(last.Size, last.Mtime, last.Hash, current.Size, current.Mtime, current.Hash) ? HistoryStatus.Changed
                : HistoryStatus.Unchanged;
            history.Add(new HistoryEntry(version, true, current.Size, new DateTime(current.Mtime, DateTimeKind.Utc), historyStatus));
            lastPresent = current;
            previousPresent = true;
        }
        return history;
    }

    /// <summary>
    /// Files and folders of a version whose name matches <paramref name="pattern"/>: a substring, or with <c>*</c> and
    /// <c>?</c> a wildcard pattern for the whole name; both ignore case. At most <paramref name="limit"/> hits, by path.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(long versionId, string pattern, int limit = SearchLimit)
    {
        if (NameMatcher(pattern) is not { } matches)
            return [];

        using var connection = OpenConnection();
        connection.CreateFunction("rb_match", (string name) => matches(name), isDeterministic: true);
        using var command = Command(connection, null, """
            SELECT p.id, p.path, p.name, 1, d.size FROM dirs d JOIN paths p ON p.id = d.path_id
            WHERE d.version_id = $v AND p.path <> '' AND rb_match(p.name)
            UNION ALL
            SELECT p.id, p.path, p.name, 0, f.size FROM files f JOIN paths p ON p.id = f.path_id
            WHERE f.version_id = $v AND rb_match(p.name)
            ORDER BY 2 COLLATE NOCASE
            LIMIT $limit
            """);
        command.Parameters.AddWithValue("$v", versionId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var hits = new List<SearchHit>();
        while (reader.Read())
            hits.Add(new SearchHit(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0, reader.GetInt64(4)));
        return hits;
    }

    /// <summary>Null for a blank pattern.</summary>
    private static Func<string, bool>? NameMatcher(string pattern)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0)
            return null;
        if (pattern.IndexOfAny(['*', '?']) < 0)
            return name => name.Contains(pattern, StringComparison.OrdinalIgnoreCase);

        var regex = new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return regex.IsMatch;
    }

    private static bool FileDiffers(SqliteDataReader reader) =>
        Differs(reader.GetInt64(2), reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.GetInt64(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetInt64(7));

    /// <summary>§11: both hashed → by hash; otherwise by size and time.</summary>
    private static bool Differs(long sizeA, long mtimeA, long? hashA, long sizeB, long mtimeB, long? hashB) =>
        hashA is { } a && hashB is { } b ? a != b : sizeA != sizeB || mtimeA != mtimeB;

    private static Dictionary<long, (string Name, string Path, bool IsDirectory, IndexStats Stats)> ChildStats(
        SqliteConnection connection, long versionId, long folderPathId)
    {
        using var command = Command(connection, null, """
            SELECT p.id, p.name, p.path, 1, d.size, d.files, NULL, NULL
            FROM paths p JOIN dirs d ON d.path_id = p.id AND d.version_id = $v
            WHERE p.parent_id = $folder AND p.is_dir = 1
            UNION ALL
            SELECT p.id, p.name, p.path, 0, f.size, 1, f.mtime_ticks, f.hash
            FROM paths p JOIN files f ON f.path_id = p.id AND f.version_id = $v
            WHERE p.parent_id = $folder AND p.is_dir = 0
            """);
        command.Parameters.AddWithValue("$v", versionId);
        command.Parameters.AddWithValue("$folder", folderPathId);
        using var reader = command.ExecuteReader();
        var children = new Dictionary<long, (string, string, bool, IndexStats)>();
        while (reader.Read())
        {
            var stats = new IndexStats(reader.GetInt64(4), (int)reader.GetInt64(5),
                reader.IsDBNull(6) ? null : new DateTime(reader.GetInt64(6), DateTimeKind.Utc),
                reader.IsDBNull(7) ? null : reader.GetInt64(7));
            children[reader.GetInt64(0)] = (reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0, stats);
        }
        return children;
    }
}
