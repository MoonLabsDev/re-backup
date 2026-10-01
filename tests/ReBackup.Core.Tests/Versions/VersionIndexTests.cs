using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using ReBackup.Core.Backup;
using ReBackup.Core.Json;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using static ReBackup.Core.Tests.TestSupport.VersionBuilder;

namespace ReBackup.Core.Tests.Versions;

public class VersionIndexTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _target;
    private readonly string _db;

    public VersionIndexTests()
    {
        _target = _tmp.CreateDir("target");
        _db = _tmp.PathOf(@"index\plan1.db");
    }

    public void Dispose() => _tmp.Dispose();

    /// <summary>Runs a query against the database file directly; each row as an array of column values.</summary>
    private List<object?[]> Rows(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_db};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private List<(string Path, long Size, long Mtime, long? Hash)> FilesOf(string versionName) =>
        Rows($"""
              SELECT p.path, f.size, f.mtime_ticks, f.hash FROM files f JOIN paths p ON p.id = f.path_id
              JOIN versions v ON v.id = f.version_id WHERE v.name = '{versionName}' ORDER BY p.path
              """)
            .Select(r => ((string)r[0]!, (long)r[1]!, (long)r[2]!, (long?)r[3]))
            .ToList();

    private Dictionary<string, (long Size, long Files)> DirsOf(string versionName) =>
        Rows($"""
              SELECT p.path, d.size, d.files FROM dirs d JOIN paths p ON p.id = d.path_id
              JOIN versions v ON v.id = d.version_id WHERE v.name = '{versionName}'
              """)
            .ToDictionary(r => (string)r[0]!, r => ((long)r[1]!, (long)r[2]!));

    [Fact]
    public void Creates_the_schema()
    {
        VersionIndex.Open(_db);

        Rows("SELECT value FROM meta WHERE key = 'schema_version'").Should().ContainSingle().Which[0].Should().Be("1");
        Rows("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name").Select(r => (string)r[0]!)
            .Should().Equal("dirs", "files", "meta", "paths", "versions");
        Rows("PRAGMA journal_mode").Single()[0].Should().Be("wal");
    }

    [Fact]
    public void Imports_a_version_from_its_manifest()
    {
        var later = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha"), File("sub/b.txt", "bravo!", later), File("sub/deep/c.txt", "c")]);
        var index = VersionIndex.Open(_db);

        var result = index.Sync(List(_target));

        result.Should().BeEquivalentTo(new IndexSyncResult(1, 0, 0, []));
        var version = index.Versions().Should().ContainSingle().Subject;
        version.Name.Should().Be("2026_09_30-14_05 Projects");
        version.LocalTime.Should().Be(new DateTime(2026, 9, 30, 14, 5, 0));
        version.Ownership.Should().Be(VersionOwnership.Owned);
        version.Origin.Should().Be(IndexOrigin.Manifest);
        version.Source.Should().Be(Source);
        version.FileCount.Should().Be(3);
        version.TotalBytes.Should().Be(12);

        var files = FilesOf(version.Name);
        files.Select(f => (f.Path, f.Size, f.Mtime)).Should().Equal(
            ("a.txt", 5L, Mtime.Ticks), ("sub/b.txt", 6L, later.Ticks), ("sub/deep/c.txt", 1L, Mtime.Ticks));
        files.Should().OnlyContain(f => f.Hash != null);
        DirsOf(version.Name).Should().BeEquivalentTo(new Dictionary<string, (long, long)>
        {
            [""] = (12, 3),
            ["sub"] = (7, 2),
            ["sub/deep"] = (1, 1),
        });
    }

    [Fact]
    public void Stores_the_hash_as_a_signed_64_bit_number()
    {
        VersionIndex.ParseHash("xxh64:ffffffffffffffff").Should().Be(-1);
        VersionIndex.ParseHash("xxh64:0000000000000010").Should().Be(16);
        VersionIndex.ParseHash("").Should().BeNull();
        VersionIndex.ParseHash("xxh64:xyz").Should().BeNull();
        VersionIndex.ParseHash("sha1:0000000000000010").Should().BeNull();
    }

    [Fact]
    public void Scans_a_folder_without_a_manifest()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha"), File("sub/b.txt", "bravo!")], withManifest: false);
        var index = VersionIndex.Open(_db);

        index.Sync(List(_target));

        var version = index.Versions().Should().ContainSingle().Subject;
        version.Ownership.Should().Be(VersionOwnership.NoManifest);
        version.Origin.Should().Be(IndexOrigin.Scan);
        version.Source.Should().BeNull();
        FilesOf(version.Name).Should().Equal(("a.txt", 5L, Mtime.Ticks, null), ("sub/b.txt", 6L, Mtime.Ticks, null));
        DirsOf(version.Name)[""].Should().Be((11, 2));
        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public void Scans_a_folder_whose_manifest_cannot_be_read_and_leaves_the_manifest_out()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")], withManifest: false);
        System.IO.File.WriteAllText(Path.Combine(folder, VersionName.ManifestFileName), "{ not json");
        var index = VersionIndex.Open(_db);

        index.Sync(List(_target));

        var version = index.Versions().Should().ContainSingle().Subject;
        version.Ownership.Should().Be(VersionOwnership.Unreadable);
        version.Origin.Should().Be(IndexOrigin.Scan);
        FilesOf(version.Name).Select(f => f.Path).Should().Equal("a.txt");
    }

    [Fact]
    public void A_scan_does_not_follow_links()
    {
        var outside = _tmp.CreateDir("outside");
        _tmp.WriteFile(@"outside\secret.txt", "secret");
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")], withManifest: false);
        Junction.Create(Path.Combine(folder, "link"), outside);
        var index = VersionIndex.Open(_db);

        index.Sync(List(_target));

        FilesOf("2026_09_30-14_05 Projects").Select(f => f.Path).Should().Equal("a.txt");
    }

    [Fact]
    public void Leaves_an_unchanged_version_alone_and_imports_a_changed_one_again()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        var index = VersionIndex.Open(_db);
        index.Sync(List(_target));
        var firstId = index.Versions().Single().Id;

        index.Sync(List(_target)).Should().BeEquivalentTo(new IndexSyncResult(0, 1, 0, []));
        index.Versions().Single().Id.Should().Be(firstId);

        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha"), File("new.txt", "new")]);
        System.IO.File.SetLastWriteTimeUtc(Path.Combine(folder, VersionName.ManifestFileName), DateTime.UtcNow.AddMinutes(1));

        index.Sync(List(_target)).Should().BeEquivalentTo(new IndexSyncResult(1, 0, 0, []));
        FilesOf("2026_09_30-14_05 Projects").Select(f => f.Path).Should().Equal("a.txt", "new.txt");
    }

    [Fact]
    public void Imports_a_scanned_folder_again_when_its_top_level_changes()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")], withManifest: false);
        var index = VersionIndex.Open(_db);
        index.Sync(List(_target));

        System.IO.File.WriteAllText(Path.Combine(folder, "b.txt"), "bravo");

        index.Sync(List(_target)).Imported.Should().Be(1);
        FilesOf("2026_09_30-14_05 Projects").Select(f => f.Path).Should().Equal("a.txt", "b.txt");
    }

    [Fact]
    public void Removes_versions_whose_folder_is_gone()
    {
        var old = Write(_target, "2026_09_29-14_05", [File("a.txt", "alpha")]);
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        var index = VersionIndex.Open(_db);
        index.Sync(List(_target));

        Directory.Delete(old, recursive: true);

        index.Sync(List(_target)).Removed.Should().Be(1);
        index.Versions().Select(v => v.Name).Should().Equal("2026_09_30-14_05 Projects");
        Rows("SELECT COUNT(*) FROM files").Single()[0].Should().Be(1L);
        Rows("SELECT COUNT(*) FROM dirs").Single()[0].Should().Be(1L);
    }

    [Fact]
    public void Shares_paths_between_versions()
    {
        Write(_target, "2026_09_29-14_05", [File("sub/a.txt", "alpha")]);
        Write(_target, "2026_09_30-14_05", [File("sub/a.txt", "alpha2")]);
        var index = VersionIndex.Open(_db);

        index.Sync(List(_target));

        Rows("SELECT path, is_dir FROM paths ORDER BY path").Select(r => ((string)r[0]!, (long)r[1]!))
            .Should().Equal(("", 1L), ("sub", 1L), ("sub/a.txt", 0L));
        Rows("SELECT COUNT(*) FROM files").Single()[0].Should().Be(2L);
    }

    [Fact]
    public void Reports_progress_per_version()
    {
        Write(_target, "2026_09_29-14_05", [File("a.txt", "alpha")]);
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha"), File("b.txt", "bravo")]);
        var index = VersionIndex.Open(_db);
        var reports = new List<IndexSyncProgress>();

        index.Sync(List(_target), new SyncProgress<IndexSyncProgress>(reports.Add));

        reports.Should().Equal(
            new IndexSyncProgress(1, 2, "2026_09_29-14_05 Projects", 0, false),
            new IndexSyncProgress(1, 2, "2026_09_29-14_05 Projects", 1, true),
            new IndexSyncProgress(2, 2, "2026_09_30-14_05 Projects", 0, false),
            new IndexSyncProgress(2, 2, "2026_09_30-14_05 Projects", 2, true));
    }

    [Fact]
    public void Cancellation_during_an_import_keeps_the_versions_already_committed()
    {
        Write(_target, "2026_09_29-14_05", [File("a.txt", "alpha")]);
        var big = Write(_target, "2026_09_30-14_05", []);
        var manifest = new BackupManifest { PlanId = PlanId, PlanName = PlanName, Source = Source };
        for (var i = 0; i < 3 * VersionIndex.ProgressEvery; i++)
            manifest.Files.Add(new ManifestFile($"f{i}.bin", 1, Mtime, "xxh64:0000000000000001"));
        System.IO.File.WriteAllText(Path.Combine(big, VersionName.ManifestFileName), JsonSerializer.Serialize(manifest, JsonDefaults.Options));
        var index = VersionIndex.Open(_db);
        using var cts = new CancellationTokenSource();

        var sync = () => index.Sync(List(_target), new SyncProgress<IndexSyncProgress>(p =>
        {
            if (p.FilesImported > 0 && !p.Finished)
                cts.Cancel();   // in the middle of the big version
        }), cts.Token);

        sync.Should().Throw<OperationCanceledException>();
        index.Versions().Select(v => v.Name).Should().Equal("2026_09_29-14_05 Projects");
        Rows("SELECT COUNT(*) FROM files").Single()[0].Should().Be(1L);
        Rows("SELECT COUNT(*) FROM dirs").Single()[0].Should().Be(1L);

        index.Sync(List(_target)).Imported.Should().Be(1, "the next sync imports the version that was canceled");
        index.Versions().Last().FileCount.Should().Be(3 * VersionIndex.ProgressEvery);
    }

    [Fact]
    public void Rebuilds_a_corrupt_database()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_db)!);
        System.IO.File.WriteAllText(_db, "this is not a database, just text that is long enough to look like a header");
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);

        var index = VersionIndex.Open(_db);
        index.Sync(List(_target));

        index.Versions().Should().ContainSingle();
    }

    [Fact]
    public void Rebuilds_a_database_of_another_schema_version()
    {
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        VersionIndex.Open(_db).Sync(List(_target));
        using (var connection = new SqliteConnection($"Data Source={_db};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE meta SET value = '0' WHERE key = 'schema_version'";
            command.ExecuteNonQuery();
        }

        var index = VersionIndex.Open(_db);

        index.Versions().Should().BeEmpty("the old database was thrown away");
        Rows("SELECT value FROM meta WHERE key = 'schema_version'").Single()[0].Should().Be("1");
    }

    [Fact]
    public void Imports_a_large_manifest()
    {
        var folder = Write(_target, "2026_09_30-14_05", []);
        var manifest = new BackupManifest { PlanId = PlanId, PlanName = PlanName, Source = Source };
        for (var i = 0; i < 100_000; i++)
            manifest.Files.Add(new ManifestFile($"folder{i % 100}/file{i}.bin", 2, Mtime, "xxh64:0123456789abcdef"));
        System.IO.File.WriteAllText(Path.Combine(folder, VersionName.ManifestFileName), JsonSerializer.Serialize(manifest, JsonDefaults.Options));
        var index = VersionIndex.Open(_db);

        index.Sync(List(_target));

        index.Versions().Single().FileCount.Should().Be(100_000);
        DirsOf("2026_09_30-14_05 Projects")["folder7"].Should().Be((2_000, 1_000));
    }

    [Fact]
    public void Adds_a_version_from_a_manifest_in_memory()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        var version = List(_target).Single();
        var manifest = new BackupManifest
        {
            PlanId = PlanId, PlanName = PlanName, Source = @"D:\elsewhere",
            Files = [new ManifestFile("x/y.txt", 3, Mtime, "xxh64:0000000000000002")],
        };
        var index = VersionIndex.Open(_db);

        index.Add(version, manifest);

        index.Versions().Single().Source.Should().Be(@"D:\elsewhere");
        FilesOf(version.Name).Should().Equal(("x/y.txt", 3L, Mtime.Ticks, 2L));
        index.Sync(List(_target)).Unchanged.Should().Be(1, "the stamp of the manifest on disk was recorded");
        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public void Invalid_utf8_in_a_manifest_falls_back_to_a_scan_and_does_not_break_the_sync()
    {
        Write(_target, "2026_09_29-14_05", [File("a.txt", "alpha")]);
        var bad = Write(_target, "2026_09_30-14_05", [File("b.txt", "bravo")], withManifest: false);
        var json = System.Text.Encoding.UTF8.GetBytes("""{ "planId": "plan1", "files": [ { "path": "@@", "size": 1 } ] }""");
        var at = Array.IndexOf(json, (byte)'@');
        json[at] = 0xC3;
        json[at + 1] = 0x28;
        System.IO.File.WriteAllBytes(Path.Combine(bad, VersionName.ManifestFileName), json);
        var index = VersionIndex.Open(_db);

        var result = index.Sync(List(_target));

        result.Errors.Should().BeEmpty();
        index.Versions().Select(v => (v.Name, v.Origin)).Should().Equal(
            ("2026_09_29-14_05 Projects", IndexOrigin.Manifest), ("2026_09_30-14_05 Projects", IndexOrigin.Scan));
    }

    [Fact]
    public void A_manifest_that_cannot_be_read_is_an_error_and_is_retried_by_the_next_sync()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        var index = VersionIndex.Open(_db);

        IndexSyncResult first;
        using (new FileStream(Path.Combine(folder, VersionName.ManifestFileName), FileMode.Open, FileAccess.Read, FileShare.None))
            first = index.Sync(List(_target));

        first.Errors.Should().ContainSingle().Which.Should().StartWith("2026_09_30-14_05 Projects: ");
        first.Imported.Should().Be(0);
        index.Versions().Should().BeEmpty();
        Rows("SELECT COUNT(*) FROM files").Single()[0].Should().Be(0L);

        index.Sync(List(_target)).Imported.Should().Be(1);
        index.Versions().Single().Origin.Should().Be(IndexOrigin.Manifest);
        index.Versions().Single().Source.Should().Be(Source);
    }

    [Fact]
    public void A_missing_target_keeps_the_rows_and_reports_an_error()
    {
        Write(_target, "2026_09_29-14_05", [File("a.txt", "alpha")]);
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        var index = VersionIndex.Open(_db);
        index.Sync(List(_target), targetFolder: _target);
        var gone = _tmp.PathOf("offline-nas");

        var result = index.Sync(VersionCatalog.List(gone, PlanId, PlanName), targetFolder: gone);

        result.Removed.Should().Be(0);
        result.Errors.Should().ContainSingle();
        index.Versions().Should().HaveCount(2);
    }

    [Fact]
    public void Duplicate_paths_in_a_manifest_count_once_and_the_first_wins()
    {
        var folder = Write(_target, "2026_09_30-14_05", []);
        var version = List(_target).Single();
        var manifest = new BackupManifest
        {
            PlanId = PlanId, PlanName = PlanName, Source = Source,
            Files =
            [
                new ManifestFile("a.txt", 5, Mtime, ""),
                new ManifestFile("A.TXT", 9, Mtime, ""),
                new ManifestFile(@"sub\b.txt", 2, Mtime, ""),
                new ManifestFile("sub/b.txt", 7, Mtime, ""),
            ],
        };
        var index = VersionIndex.Open(_db);

        index.Add(version, manifest);

        var indexed = index.Versions().Single();
        indexed.FileCount.Should().Be(2);
        indexed.TotalBytes.Should().Be(7);
        DirsOf(version.Name).Should().BeEquivalentTo(new Dictionary<string, (long, long)> { [""] = (7, 2), ["sub"] = (2, 1) });
        FilesOf(version.Name).Select(f => (f.Path, f.Size)).Should().Equal(("a.txt", 5L), ("sub/b.txt", 2L));
        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public void Index_sets_put_one_database_per_plan_into_their_folder()
    {
        var set = new VersionIndexSet(_tmp.PathOf("indexes"));

        set.PathFor("abc").Should().Be(_tmp.PathOf(@"indexes\abc.db"));
        set.For("abc").Should().BeSameAs(set.For("ABC"));
        System.IO.File.Exists(_tmp.PathOf(@"indexes\abc.db")).Should().BeTrue();
        var bad = () => set.PathFor(@"..\x");
        bad.Should().Throw<ArgumentException>();
    }
}
