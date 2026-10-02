using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReBackup.Core.Backup;
using ReBackup.Core.Localization;

namespace ReBackup.Core.Versions;

/// <summary>
/// A machine-local SQLite cache of the file lists of one plan's versions. The manifests in the version folders stay
/// the source of truth: the database can be deleted at any time and is rebuilt by <see cref="Sync"/>.
/// Thread-safe: every call opens its own connection; writes are serialized, reads run beside them (WAL).
/// </summary>
public sealed partial class VersionIndex
{
    public const int SchemaVersion = 1;

    /// <summary>Progress is reported after this many files of a version.</summary>
    public const int ProgressEvery = 4096;

    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss";

    private const string Schema = """
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS meta     (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS versions (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                                             local_time TEXT NOT NULL, ownership TEXT NOT NULL, source TEXT,
                                             origin TEXT NOT NULL, stamp TEXT NOT NULL,
                                             file_count INTEGER NOT NULL, total_bytes INTEGER NOT NULL, indexed_utc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS paths    (id INTEGER PRIMARY KEY, parent_id INTEGER, name TEXT NOT NULL,
                                             path TEXT NOT NULL COLLATE NOCASE, is_dir INTEGER NOT NULL,
                                             UNIQUE (path, is_dir));
        CREATE INDEX IF NOT EXISTS paths_parent ON paths(parent_id);
        CREATE TABLE IF NOT EXISTS files    (version_id INTEGER NOT NULL, path_id INTEGER NOT NULL, size INTEGER NOT NULL,
                                             mtime_ticks INTEGER NOT NULL, hash INTEGER,
                                             PRIMARY KEY (version_id, path_id)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS files_path ON files(path_id, version_id);
        CREATE TABLE IF NOT EXISTS dirs     (version_id INTEGER NOT NULL, path_id INTEGER NOT NULL, size INTEGER NOT NULL,
                                             files INTEGER NOT NULL,
                                             PRIMARY KEY (version_id, path_id)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS dirs_path ON dirs(path_id);
        INSERT OR IGNORE INTO meta(key, value) VALUES ('schema_version', '1');
        """;

    private readonly string _connectionString;
    private readonly object _writeGate = new();

    private VersionIndex(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,   // no file handle outlives a call, so the file can be deleted and rebuilt at any time
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Opens the index at <paramref name="databasePath"/>, creating it when missing. A file that is not a database of
    /// this schema version is deleted and created anew.
    /// </summary>
    public static VersionIndex Open(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var index = new VersionIndex(databasePath);
        if (!index.HasCurrentSchema())
            index.DeleteFiles();
        try
        {
            index.CreateSchema();
        }
        catch (SqliteException)
        {
            index.DeleteFiles();   // e.g. a file that only looked like a database
            index.CreateSchema();
        }
        return index;
    }

    /// <summary>
    /// Brings the index in line with the version folders listed by <see cref="VersionCatalog.List"/>: rows of folders
    /// that are no longer listed are removed; listed folders that are new or whose stamp changed are imported, each in
    /// its own transaction. Cancellation throws and leaves every committed version intact.
    /// When <paramref name="targetFolder"/> is given and does not exist (e.g. a NAS that is offline), nothing is
    /// removed or imported and an error is reported instead.
    /// </summary>
    public IndexSyncResult Sync(IReadOnlyList<VersionInfo> folders, IProgress<IndexSyncProgress>? progress = null,
        CancellationToken cancellationToken = default, string? targetFolder = null)
    {
        if (targetFolder is not null && !Directory.Exists(targetFolder))
        {
            // An offline target lists no versions; that must not wipe the index.
            return new IndexSyncResult(0, 0, 0, [CoreTexts.English("core.index.targetUnavailable", ("folder", targetFolder))]);
        }

        var known = ReadStamps();
        var listed = new HashSet<string>(folders.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var removed = Remove(known.Keys.Where(name => !listed.Contains(name)).ToList());

        var imported = 0;
        var unchanged = 0;
        var errors = new List<string>();
        for (var i = 0; i < folders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folders[i];
            var current = i + 1;
            string stamp;
            try
            {
                stamp = StampOf(folder.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{folder.Name}: {ex.Message}");
                continue;
            }

            if (known.TryGetValue(folder.Name, out var row) && row.Stamp == stamp)
            {
                unchanged++;
                progress?.Report(new IndexSyncProgress(current, folders.Count, folder.Name, 0, Finished: true));
                continue;
            }

            progress?.Report(new IndexSyncProgress(current, folders.Count, folder.Name, 0, Finished: false));
            try
            {
                var files = Import(folder, stamp, cancellationToken, count =>
                    progress?.Report(new IndexSyncProgress(current, folders.Count, folder.Name, count, Finished: false)));
                imported++;
                progress?.Report(new IndexSyncProgress(current, folders.Count, folder.Name, files, Finished: true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{folder.Name}: {ex.Message}");
            }
        }
        return new IndexSyncResult(imported, unchanged, removed, errors);
    }

    /// <summary>
    /// Takes the versions <paramref name="names"/> (matched without regard to case; unknown names are ignored) out of the
    /// index, together with the paths no remaining version uses, in one transaction. Returns how many were removed.
    /// </summary>
    public int Remove(IReadOnlyCollection<string> names)
    {
        if (names.Count == 0)
            return 0;
        var known = ReadStamps();
        var ids = names.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(known.ContainsKey)
            .Select(name => known[name].Id)
            .ToList();
        if (ids.Count == 0)
            return 0;

        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var id in ids)
                DeleteVersion(connection, transaction, id);
            DeleteUnusedPaths(connection, transaction);
            transaction.Commit();
        }
        return ids.Count;
    }

    /// <summary>The indexed versions, oldest first.</summary>
    public IReadOnlyList<IndexedVersion> Versions()
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            SELECT id, name, local_time, ownership, source, origin, file_count, total_bytes
            FROM versions ORDER BY local_time, name
            """);
        using var reader = command.ExecuteReader();
        var versions = new List<IndexedVersion>();
        while (reader.Read())
            versions.Add(ReadVersion(reader, 0));
        return versions;
    }

    /// <summary>
    /// Adds (or replaces) a version from a manifest in memory, e.g. the one a backup run has just written, so that it
    /// does not have to be read again over the network.
    /// </summary>
    public void Add(VersionInfo version, BackupManifest manifest)
    {
        string stamp;
        try
        {
            stamp = StampOf(version.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stamp = "m:unknown";   // the next sync imports it again from the folder
        }

        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var writer = BeginVersion(connection, transaction, version, stamp, IndexOrigin.Manifest);
            foreach (var file in manifest.Files)
            {
                if (file is not null)
                    writer.AddFile(file);
            }
            writer.Finish(manifest.Source);
            transaction.Commit();
        }
    }

    /// <summary>
    /// The change detector of a version folder: the manifest's length and last write time when there is a manifest,
    /// otherwise the folder's last write time and the number of entries directly inside it.
    /// </summary>
    public static string StampOf(string versionFolder)
    {
        var manifest = new FileInfo(Path.Combine(versionFolder, VersionName.ManifestFileName));
        if (manifest.Exists)
            return string.Create(CultureInfo.InvariantCulture, $"m:{manifest.Length}:{manifest.LastWriteTimeUtc.Ticks}");

        var folder = new DirectoryInfo(versionFolder);
        if (!folder.Exists)
            throw new DirectoryNotFoundException(CoreTexts.English("core.restore.versionMissing", ("folder", versionFolder)));
        var count = folder.EnumerateFileSystemInfos().Count();
        return string.Create(CultureInfo.InvariantCulture, $"s:{folder.LastWriteTimeUtc.Ticks}:{count}");
    }

    /// <summary>Imports one version (manifest when readable, otherwise a scan); returns the number of files.</summary>
    private int Import(VersionInfo folder, string stamp, CancellationToken cancellationToken, Action<int> onProgress)
    {
        var manifestPath = Path.Combine(folder.Path, VersionName.ManifestFileName);
        if (File.Exists(manifestPath))
        {
            try
            {
                return ImportOnce(folder, stamp, IndexOrigin.Manifest, cancellationToken, onProgress, writer =>
                {
                    var summary = ManifestStream.Read(manifestPath, writer.AddFile, cancellationToken);
                    return summary.Source;
                });
            }
            catch (JsonException)
            {
                // Unparseable manifest: fall back to the files themselves. (A manifest that merely could not be read,
                // e.g. a network error, is not caught: Sync reports it and the next sync retries.)
            }
        }

        return ImportOnce(folder, stamp, IndexOrigin.Scan, cancellationToken, onProgress, writer =>
        {
            foreach (var file in ScanFiles(folder.Path, cancellationToken))
                writer.AddFile(file);
            return null;
        });
    }

    private int ImportOnce(VersionInfo folder, string stamp, IndexOrigin origin, CancellationToken cancellationToken,
        Action<int> onProgress, Func<VersionWriter, string?> fill)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var writer = BeginVersion(connection, transaction, folder, stamp, origin);
            writer.CancellationToken = cancellationToken;
            writer.OnProgress = onProgress;
            var source = fill(writer);
            writer.Finish(source);
            transaction.Commit();   // not reached on cancellation: disposing the transaction rolls the version back
            return writer.FileCount;
        }
    }

    /// <summary>The files of a version folder without a usable manifest: sizes and times, no hashes, no links.</summary>
    private static IEnumerable<ManifestFile> ScanFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<(DirectoryInfo Folder, string Prefix)>();
        pending.Push((new DirectoryInfo(root), ""));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (folder, prefix) = pending.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = folder.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (prefix.Length > 0 && ex is IOException or UnauthorizedAccessException)
            {
                continue;   // an unreadable subfolder is left out; an unreadable version folder is an error
            }

            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;   // links are not followed
                var relative = prefix + entry.Name;
                if (entry is DirectoryInfo directory)
                    pending.Push((directory, relative + "/"));
                else if (entry is FileInfo file &&
                         !(prefix.Length == 0 && file.Name.Equals(VersionName.ManifestFileName, StringComparison.OrdinalIgnoreCase)))
                    yield return new ManifestFile(relative, file.Length, file.LastWriteTimeUtc, "");
            }
        }
    }

    private VersionWriter BeginVersion(SqliteConnection connection, SqliteTransaction transaction, VersionInfo version,
        string stamp, IndexOrigin origin)
    {
        using (var find = Command(connection, transaction, "SELECT id FROM versions WHERE name = $name"))
        {
            find.Parameters.AddWithValue("$name", version.Name);
            if (find.ExecuteScalar() is long existing)
                DeleteVersion(connection, transaction, existing);
        }

        using var insert = Command(connection, transaction, """
            INSERT INTO versions (name, local_time, ownership, source, origin, stamp, file_count, total_bytes, indexed_utc)
            VALUES ($name, $time, $ownership, NULL, $origin, $stamp, 0, 0, $now) RETURNING id
            """);
        insert.Parameters.AddWithValue("$name", version.Name);
        insert.Parameters.AddWithValue("$time", FormatTime(version.LocalTime));
        insert.Parameters.AddWithValue("$ownership", version.Ownership.ToString());
        insert.Parameters.AddWithValue("$origin", origin.ToString());
        insert.Parameters.AddWithValue("$stamp", stamp);
        insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        var id = (long)insert.ExecuteScalar()!;
        return new VersionWriter(connection, transaction, id);
    }

    private static void DeleteVersion(SqliteConnection connection, SqliteTransaction transaction, long versionId)
    {
        using var delete = Command(connection, transaction, """
            DELETE FROM files WHERE version_id = $id;
            DELETE FROM dirs WHERE version_id = $id;
            DELETE FROM versions WHERE id = $id;
            """);
        delete.Parameters.AddWithValue("$id", versionId);
        delete.ExecuteNonQuery();
    }

    /// <summary>
    /// Paths that neither a file nor a folder row refers to. A folder path has a folder row in every version that holds
    /// anything below it, so no path that is still in use loses its parent.
    /// </summary>
    private static void DeleteUnusedPaths(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var delete = Command(connection, transaction, """
            DELETE FROM paths
            WHERE NOT EXISTS (SELECT 1 FROM files WHERE files.path_id = paths.id)
              AND NOT EXISTS (SELECT 1 FROM dirs WHERE dirs.path_id = paths.id)
            """);
        delete.ExecuteNonQuery();
    }

    private Dictionary<string, (long Id, string Stamp)> ReadStamps()
    {
        var stamps = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
        using var connection = OpenConnection();
        using var command = Command(connection, null, "SELECT id, name, stamp FROM versions");
        using var reader = command.ExecuteReader();
        while (reader.Read())
            stamps[reader.GetString(1)] = (reader.GetInt64(0), reader.GetString(2));
        return stamps;
    }

    private bool HasCurrentSchema()
    {
        if (!File.Exists(DatabasePath))
            return true;
        try
        {
            using var connection = OpenConnection();
            using var command = Command(connection, null, "SELECT value FROM meta WHERE key = 'schema_version'");
            return command.ExecuteScalar() is string value &&
                   value == SchemaVersion.ToString(CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private void CreateSchema()
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, Schema);
        command.ExecuteNonQuery();
    }

    private void DeleteFiles()
    {
        foreach (var suffix in (ReadOnlySpan<string>)["", "-wal", "-shm"])
        {
            var path = DatabasePath + suffix;
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var pragma = Command(connection, null, "PRAGMA synchronous = NORMAL;");
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();   // a file that is not a database fails here; its handle must not stay open
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static string FormatTime(DateTime localTime) => localTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static DateTime ParseTime(string text) =>
        DateTime.ParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static IndexedVersion ReadVersion(SqliteDataReader reader, int first) =>
        new(reader.GetInt64(first),
            reader.GetString(first + 1),
            ParseTime(reader.GetString(first + 2)),
            Enum.TryParse<VersionOwnership>(reader.GetString(first + 3), out var ownership) ? ownership : VersionOwnership.Unreadable,
            reader.IsDBNull(first + 4) ? null : reader.GetString(first + 4),
            Enum.TryParse<IndexOrigin>(reader.GetString(first + 5), out var origin) ? origin : IndexOrigin.Scan,
            (int)reader.GetInt64(first + 6),
            reader.GetInt64(first + 7));

    /// <summary>"xxh64:" plus 16 hex digits as a signed 64-bit number; null for anything else.</summary>
    public static long? ParseHash(string? hash)
    {
        const string prefix = "xxh64:";
        if (hash is null || !hash.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || hash.Length != prefix.Length + 16)
            return null;
        return ulong.TryParse(hash.AsSpan(prefix.Length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            ? unchecked((long)value)
            : null;
    }

    /// <summary>Writes the files of one version inside its transaction: interns paths and rolls up folder totals.</summary>
    private sealed class VersionWriter
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly long _versionId;
        private readonly Dictionary<string, long> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<long> _written = [];
        private readonly Dictionary<long, long?> _dirParents = [];
        private readonly Dictionary<long, (long Size, int Files)> _dirTotals = [];
        private readonly SqliteCommand _insertPath;
        private readonly SqliteCommand _insertFile;

        public VersionWriter(SqliteConnection connection, SqliteTransaction transaction, long versionId)
        {
            _connection = connection;
            _transaction = transaction;
            _versionId = versionId;

            using (var load = Command(connection, transaction, "SELECT id, path, is_dir, parent_id FROM paths"))
            using (var reader = load.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    var isDir = reader.GetInt64(2) != 0;
                    _paths[Key(reader.GetString(1), isDir)] = id;
                    if (isDir)
                        _dirParents[id] = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                }
            }

            _insertPath = Command(connection, transaction,
                "INSERT INTO paths (parent_id, name, path, is_dir) VALUES ($parent, $name, $path, $dir) RETURNING id");
            _insertPath.Parameters.Add("$parent", SqliteType.Integer);
            _insertPath.Parameters.Add("$name", SqliteType.Text);
            _insertPath.Parameters.Add("$path", SqliteType.Text);
            _insertPath.Parameters.Add("$dir", SqliteType.Integer);

            _insertFile = Command(connection, transaction,
                "INSERT OR REPLACE INTO files (version_id, path_id, size, mtime_ticks, hash) VALUES ($v, $p, $size, $mtime, $hash)");
            _insertFile.Parameters.AddWithValue("$v", versionId);
            _insertFile.Parameters.Add("$p", SqliteType.Integer);
            _insertFile.Parameters.Add("$size", SqliteType.Integer);
            _insertFile.Parameters.Add("$mtime", SqliteType.Integer);
            _insertFile.Parameters.Add("$hash", SqliteType.Integer);

            EnsureDirectory("");
        }

        public CancellationToken CancellationToken { get; set; }
        public Action<int>? OnProgress { get; set; }
        public int FileCount { get; private set; }
        public long TotalBytes { get; private set; }

        public void AddFile(ManifestFile file)
        {
            CancellationToken.ThrowIfCancellationRequested();
            var path = file.Path.Replace('\\', '/').Trim('/');
            if (path.Length == 0)
                return;
            var slash = path.LastIndexOf('/');
            var parentId = EnsureDirectory(slash < 0 ? "" : path[..slash]);
            var pathId = EnsurePath(path, isDir: false, parentId);
            if (!_written.Add(pathId))
                return;   // a duplicate path (also by case or slash style): the first entry wins

            _insertFile.Parameters["$p"].Value = pathId;
            _insertFile.Parameters["$size"].Value = file.Size;
            _insertFile.Parameters["$mtime"].Value = file.MtimeUtc.Ticks;
            _insertFile.Parameters["$hash"].Value = (object?)ParseHash(file.Hash) ?? DBNull.Value;
            _insertFile.ExecuteNonQuery();

            for (long? dir = parentId; dir is { } id; dir = _dirParents[id])
            {
                var (size, files) = _dirTotals.GetValueOrDefault(id);
                _dirTotals[id] = (size + file.Size, files + 1);
            }

            FileCount++;
            TotalBytes += file.Size;
            if (FileCount % ProgressEvery == 0)
                OnProgress?.Invoke(FileCount);
        }

        /// <summary>Writes the folder totals (the root always gets a row) and the version's totals and source.</summary>
        public void Finish(string? source)
        {
            var rootId = EnsureDirectory("");
            if (!_dirTotals.ContainsKey(rootId))
                _dirTotals[rootId] = (0, 0);

            using (var insert = Command(_connection, _transaction,
                       "INSERT INTO dirs (version_id, path_id, size, files) VALUES ($v, $p, $size, $files)"))
            {
                insert.Parameters.AddWithValue("$v", _versionId);
                insert.Parameters.Add("$p", SqliteType.Integer);
                insert.Parameters.Add("$size", SqliteType.Integer);
                insert.Parameters.Add("$files", SqliteType.Integer);
                foreach (var (pathId, (size, files)) in _dirTotals)
                {
                    insert.Parameters["$p"].Value = pathId;
                    insert.Parameters["$size"].Value = size;
                    insert.Parameters["$files"].Value = files;
                    insert.ExecuteNonQuery();
                }
            }

            using var update = Command(_connection, _transaction,
                "UPDATE versions SET source = $source, file_count = $count, total_bytes = $bytes WHERE id = $id");
            update.Parameters.AddWithValue("$source", string.IsNullOrEmpty(source) ? DBNull.Value : source);
            update.Parameters.AddWithValue("$count", FileCount);
            update.Parameters.AddWithValue("$bytes", TotalBytes);
            update.Parameters.AddWithValue("$id", _versionId);
            update.ExecuteNonQuery();
            _insertPath.Dispose();
            _insertFile.Dispose();
        }

        private long EnsureDirectory(string path)
        {
            if (_paths.TryGetValue(Key(path, isDir: true), out var id))
                return id;
            long? parentId = null;
            if (path.Length > 0)
            {
                var slash = path.LastIndexOf('/');
                parentId = EnsureDirectory(slash < 0 ? "" : path[..slash]);
            }
            id = EnsurePath(path, isDir: true, parentId);
            _dirParents[id] = parentId;
            return id;
        }

        private long EnsurePath(string path, bool isDir, long? parentId)
        {
            var key = Key(path, isDir);
            if (_paths.TryGetValue(key, out var id))
                return id;
            _insertPath.Parameters["$parent"].Value = (object?)parentId ?? DBNull.Value;
            _insertPath.Parameters["$name"].Value = path[(path.LastIndexOf('/') + 1)..];
            _insertPath.Parameters["$path"].Value = path;
            _insertPath.Parameters["$dir"].Value = isDir ? 1 : 0;
            id = (long)_insertPath.ExecuteScalar()!;
            _paths[key] = id;
            return id;
        }

        private static string Key(string path, bool isDir) => (isDir ? "d:" : "f:") + path;
    }
}
