using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using ReBackup.Shared.Json;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;
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

    /// <summary>Lists the target as the app does and syncs the index with it.</summary>
    private async Task<IndexSyncResult> Sync(VersionIndex index, IProgress<IndexSyncProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        index.Sync(await ListAsync(_target), TargetStorage(_target), progress, cancellationToken);

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
        Rows("SELECT value FROM meta WHERE key = 'stamp_format'").Should().ContainSingle().Which[0].Should().Be("2");
        Rows("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name").Select(r => (string)r[0]!)
            .Should().Equal("dirs", "files", "meta", "paths", "versions");
        Rows("PRAGMA journal_mode").Single()[0].Should().Be("wal");
    }

    [Fact]
    public async Task Imports_a_version_from_its_manifest()
    {
        var later = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha"), File("sub/b.txt", "bravo!", later), File("sub/deep/c.txt", "c")]);
        var index = VersionIndex.Open(_db);

        var result = await Sync(index);

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
    public async Task Scans_a_folder_without_a_manifest()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha"), File("sub/b.txt", "bravo!")], withManifest: false);
        var index = VersionIndex.Open(_db);

        await Sync(index);

        var version = index.Versions().Should().ContainSingle().Subject;
        version.Ownership.Should().Be(VersionOwnership.NoManifest);
        version.Origin.Should().Be(IndexOrigin.Scan);
        version.Source.Should().BeNull();
        FilesOf(version.Name).Should().Equal(("a.txt", 5L, Mtime.Ticks, null), ("sub/b.txt", 6L, Mtime.Ticks, null));
        DirsOf(version.Name)[""].Should().Be((11, 2));
        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public async Task Scans_a_folder_whose_manifest_cannot_be_read_and_leaves_the_manifest_out()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")], withManifest: false);
        System.IO.File.WriteAllText(Path.Combine(folder, VersionName.ManifestFileName), "{ not json");
        var index = VersionIndex.Open(_db);

        await Sync(index);

        var version = index.Versions().Should().ContainSingle().Subject;
        version.Ownership.Should().Be(VersionOwnership.Unreadable);
        version.Origin.Should().Be(IndexOrigin.Scan);
        FilesOf(version.Name).Select(f => f.Path).Should().Equal("a.txt");
    }

    [Fact]
    public async Task A_scan_does_not_follow_links()
    {
        var outside = _tmp.CreateDir("outside");
        _tmp.WriteFile(@"outside\secret.txt", "secret");
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")], withManifest: false);
        Junction.Create(Path.Combine(folder, "link"), outside);
        var index = VersionIndex.Open(_db);

        await Sync(index);

        FilesOf("2026_09_30-14_05 Projects").Select(f => f.Path).Should().Equal("a.txt");
    }

    [Fact]
    public async Task Leaves_an_unchanged_version_alone_and_imports_a_changed_one_again()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        var index = VersionIndex.Open(_db);
        await Sync(index);
        var firstId = index.Versions().Single().Id;

        (await Sync(index)).Should().BeEquivalentTo(new IndexSyncResult(0, 1, 0, []));
        index.Versions().Single().Id.Should().Be(firstId);

        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha"), File("new.txt", "new")]);
        System.IO.File.SetLastWriteTimeUtc(Path.Combine(folder, VersionName.ManifestFileName), DateTime.UtcNow.AddMinutes(1));

        (await Sync(index)).Should().BeEquivalentTo(new IndexSyncResult(1, 0, 0, []));
        FilesOf("2026_09_30-14_05 Projects").Select(f => f.Path).Should().Equal("a.txt", "new.txt");
    }

    [Fact]
    public async Task Imports_a_scanned_folder_again_when_its_top_level_changes()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")], withManifest: false);
        var index = VersionIndex.Open(_db);
        await Sync(index);

        System.IO.File.WriteAllText(Path.Combine(folder, "b.txt"), "bravo");

        (await Sync(index)).Imported.Should().Be(1);
        FilesOf("2026_09_30-14_05 Projects").Select(f => f.Path).Should().Equal("a.txt", "b.txt");
    }

    [Fact]
    public async Task Removes_versions_whose_folder_is_gone()
    {
        var old = Write(_target, "2026_09_29-14_05", [File("a.txt", "alpha")]);
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        var index = VersionIndex.Open(_db);
        await Sync(index);

        Directory.Delete(old, recursive: true);

        (await Sync(index)).Removed.Should().Be(1);
        index.Versions().Select(v => v.Name).Should().Equal("2026_09_30-14_05 Projects");
        Rows("SELECT COUNT(*) FROM files").Single()[0].Should().Be(1L);
        Rows("SELECT COUNT(*) FROM dirs").Single()[0].Should().Be(1L);
    }

    [Fact]
    public async Task A_sync_that_removes_versions_also_removes_the_paths_no_version_uses_any_more()
    {
        var old = Write(_target, "2026_09_29-14_05", [File("gone/x.txt", "x"), File("a.txt", "alpha")]);
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        var index = VersionIndex.Open(_db);
        await Sync(index);

        Directory.Delete(old, recursive: true);
        await Sync(index);

        Rows("SELECT path, is_dir FROM paths ORDER BY path").Select(r => ((string)r[0]!, (long)r[1]!))
            .Should().Equal(("", 1L), ("a.txt", 0L));
    }

    [Fact]
    public async Task Remove_takes_the_versions_and_their_unused_paths_out_of_the_index()
    {
        Write(_target, "2026_09_28-14_05", [File("only-old/x.txt", "x"), File("a.txt", "alpha")]);
        Write(_target, "2026_09_29-14_05", [File("a.txt", "alpha")]);
        Write(_target, "2026_09_30-14_05", [File("b.txt", "bravo")]);
        var index = VersionIndex.Open(_db);
        await Sync(index);

        var removed = index.Remove(["2026_09_28-14_05 PROJECTS", "2026_09_30-14_05 Projects", "not indexed"]);

        removed.Should().Be(2, "names are matched without regard to case; unknown names are ignored");
        index.Versions().Select(v => v.Name).Should().Equal("2026_09_29-14_05 Projects");
        Rows("SELECT path FROM paths ORDER BY path").Select(r => (string)r[0]!).Should().Equal("", "a.txt");
        Rows("SELECT COUNT(*) FROM files").Single()[0].Should().Be(1L);
        Rows("SELECT COUNT(*) FROM dirs").Single()[0].Should().Be(1L);
    }

    [Fact]
    public async Task A_version_imported_after_a_removal_gets_its_paths_again()
    {
        Write(_target, "2026_09_29-14_05", [File("sub/a.txt", "alpha")]);
        var index = VersionIndex.Open(_db);
        await Sync(index);
        index.Remove(["2026_09_29-14_05 Projects"]);

        Write(_target, "2026_09_30-14_05", [File("sub/a.txt", "alpha")]);
        await Sync(index);

        FilesOf("2026_09_30-14_05 Projects").Select(f => f.Path).Should().Equal("sub/a.txt");
        DirsOf("2026_09_30-14_05 Projects").Keys.Should().BeEquivalentTo("", "sub");
    }

    [Fact]
    public async Task Shares_paths_between_versions()
    {
        Write(_target, "2026_09_29-14_05", [File("sub/a.txt", "alpha")]);
        Write(_target, "2026_09_30-14_05", [File("sub/a.txt", "alpha2")]);
        var index = VersionIndex.Open(_db);

        await Sync(index);

        Rows("SELECT path, is_dir FROM paths ORDER BY path").Select(r => ((string)r[0]!, (long)r[1]!))
            .Should().Equal(("", 1L), ("sub", 1L), ("sub/a.txt", 0L));
        Rows("SELECT COUNT(*) FROM files").Single()[0].Should().Be(2L);
    }

    [Fact]
    public async Task Reports_progress_per_version()
    {
        Write(_target, "2026_09_29-14_05", [File("a.txt", "alpha")]);
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha"), File("b.txt", "bravo")]);
        var index = VersionIndex.Open(_db);
        var reports = new List<IndexSyncProgress>();

        await Sync(index, new SyncProgress<IndexSyncProgress>(reports.Add));

        reports.Should().Equal(
            new IndexSyncProgress(1, 2, "2026_09_29-14_05 Projects", 0, false),
            new IndexSyncProgress(1, 2, "2026_09_29-14_05 Projects", 1, true),
            new IndexSyncProgress(2, 2, "2026_09_30-14_05 Projects", 0, false),
            new IndexSyncProgress(2, 2, "2026_09_30-14_05 Projects", 2, true));
    }

    [Fact]
    public async Task Cancellation_during_an_import_keeps_the_versions_already_committed()
    {
        Write(_target, "2026_09_29-14_05", [File("a.txt", "alpha")]);
        var big = Write(_target, "2026_09_30-14_05", []);
        var manifest = new BackupManifest { PlanId = PlanId, PlanName = PlanName, Source = Source };
        for (var i = 0; i < 3 * VersionIndex.ProgressEvery; i++)
            manifest.Files.Add(new ManifestFile($"f{i}.bin", 1, Mtime, "xxh64:0000000000000001"));
        System.IO.File.WriteAllText(Path.Combine(big, VersionName.ManifestFileName), JsonSerializer.Serialize(manifest, JsonDefaults.Options));
        var index = VersionIndex.Open(_db);
        using var cts = new CancellationTokenSource();

        var folders = await ListAsync(_target);
        var sync = () => index.Sync(folders, TargetStorage(_target), new SyncProgress<IndexSyncProgress>(p =>
        {
            if (p.FilesImported > 0 && !p.Finished)
                cts.Cancel();   // in the middle of the big version
        }), cts.Token);

        sync.Should().Throw<OperationCanceledException>();
        index.Versions().Select(v => v.Name).Should().Equal("2026_09_29-14_05 Projects");
        Rows("SELECT COUNT(*) FROM files").Single()[0].Should().Be(1L);
        Rows("SELECT COUNT(*) FROM dirs").Single()[0].Should().Be(1L);

        (await Sync(index)).Imported.Should().Be(1, "the next sync imports the version that was canceled");
        index.Versions().Last().FileCount.Should().Be(3 * VersionIndex.ProgressEvery);
    }

    [Fact]
    public async Task Rebuilds_a_corrupt_database()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_db)!);
        System.IO.File.WriteAllText(_db, "this is not a database, just text that is long enough to look like a header");
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);

        var index = VersionIndex.Open(_db);
        await Sync(index);

        index.Versions().Should().ContainSingle();
    }

    [Fact]
    public async Task Rebuilds_a_database_of_another_schema_version()
    {
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        await Sync(VersionIndex.Open(_db));
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
    public async Task Imports_a_large_manifest()
    {
        var folder = Write(_target, "2026_09_30-14_05", []);
        var manifest = new BackupManifest { PlanId = PlanId, PlanName = PlanName, Source = Source };
        for (var i = 0; i < 100_000; i++)
            manifest.Files.Add(new ManifestFile($"folder{i % 100}/file{i}.bin", 2, Mtime, "xxh64:0123456789abcdef"));
        System.IO.File.WriteAllText(Path.Combine(folder, VersionName.ManifestFileName), JsonSerializer.Serialize(manifest, JsonDefaults.Options));
        var index = VersionIndex.Open(_db);

        await Sync(index);

        index.Versions().Single().FileCount.Should().Be(100_000);
        DirsOf("2026_09_30-14_05 Projects")["folder7"].Should().Be((2_000, 1_000));
    }

    [Fact]
    public async Task Adds_a_version_from_a_manifest_in_memory()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        var version = (await ListAsync(_target)).Single();
        var manifest = new BackupManifest
        {
            PlanId = PlanId, PlanName = PlanName, Source = @"D:\elsewhere",
            Files = [new ManifestFile("x/y.txt", 3, Mtime, "xxh64:0000000000000002")],
        };
        var index = VersionIndex.Open(_db);

        index.Add(version, manifest, TargetStorage(_target));

        index.Versions().Single().Source.Should().Be(@"D:\elsewhere");
        FilesOf(version.Name).Should().Equal(("x/y.txt", 3L, Mtime.Ticks, 2L));
        (await Sync(index)).Unchanged.Should().Be(1, "the stamp of the manifest on disk was recorded");
        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public async Task Invalid_utf8_in_a_manifest_falls_back_to_a_scan_and_does_not_break_the_sync()
    {
        Write(_target, "2026_09_29-14_05", [File("a.txt", "alpha")]);
        var bad = Write(_target, "2026_09_30-14_05", [File("b.txt", "bravo")], withManifest: false);
        var json = System.Text.Encoding.UTF8.GetBytes("""{ "planId": "plan1", "files": [ { "path": "@@", "size": 1 } ] }""");
        var at = Array.IndexOf(json, (byte)'@');
        json[at] = 0xC3;
        json[at + 1] = 0x28;
        System.IO.File.WriteAllBytes(Path.Combine(bad, VersionName.ManifestFileName), json);
        var index = VersionIndex.Open(_db);

        var result = await Sync(index);

        result.Errors.Should().BeEmpty();
        index.Versions().Select(v => (v.Name, v.Origin)).Should().Equal(
            ("2026_09_29-14_05 Projects", IndexOrigin.Manifest), ("2026_09_30-14_05 Projects", IndexOrigin.Scan));
    }

    [Fact]
    public async Task A_manifest_that_cannot_be_read_is_an_error_and_is_retried_by_the_next_sync()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        var index = VersionIndex.Open(_db);

        IndexSyncResult first;
        using (new FileStream(Path.Combine(folder, VersionName.ManifestFileName), FileMode.Open, FileAccess.Read, FileShare.None))
            first = await Sync(index);

        first.Errors.Should().ContainSingle().Which.Should().StartWith("2026_09_30-14_05 Projects: ");
        first.Imported.Should().Be(0);
        index.Versions().Should().BeEmpty();
        Rows("SELECT COUNT(*) FROM files").Single()[0].Should().Be(0L);

        (await Sync(index)).Imported.Should().Be(1);
        index.Versions().Single().Origin.Should().Be(IndexOrigin.Manifest);
        index.Versions().Single().Source.Should().Be(Source);
    }

    [Fact]
    public async Task A_missing_target_keeps_the_rows_and_reports_an_error()
    {
        Write(_target, "2026_09_29-14_05", [File("a.txt", "alpha")]);
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        var index = VersionIndex.Open(_db);
        await Sync(index);
        var gone = TargetStorage(_tmp.PathOf("offline-nas"));

        var result = index.Sync(await VersionCatalog.ListAsync(gone, PlanId, PlanName), gone);

        result.Removed.Should().Be(0);
        result.Errors.Should().ContainSingle();
        index.Versions().Should().HaveCount(2);
    }

    [Fact]
    public async Task Duplicate_paths_in_a_manifest_count_once_and_the_first_wins()
    {
        var folder = Write(_target, "2026_09_30-14_05", []);
        var version = (await ListAsync(_target)).Single();
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

        index.Add(version, manifest, TargetStorage(_target));

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

    [Fact]
    public async Task Index_with_old_stamp_format_is_rebuilt_once()
    {
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        await Sync(VersionIndex.Open(_db));
        using (var connection = new SqliteConnection($"Data Source={_db};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM meta WHERE key = 'stamp_format'";   // as written before stamps came from the storage
            command.ExecuteNonQuery();
        }

        var rebuilt = VersionIndex.Open(_db);

        rebuilt.Versions().Should().BeEmpty("the stamps of the old database mean something else");
        Rows("SELECT value FROM meta WHERE key = 'stamp_format'").Single()[0].Should().Be("2");
        (await Sync(rebuilt)).Imported.Should().Be(1);

        var reopened = VersionIndex.Open(_db);

        reopened.Versions().Should().ContainSingle("a database of the current stamp format is kept");
        (await Sync(reopened)).Should().BeEquivalentTo(new IndexSyncResult(0, 1, 0, []));
    }

    [Fact]
    public async Task Stamp_of_a_version_is_its_manifests_stamp_or_a_summary_of_its_files()
    {
        var storage = new InMemoryStorage();
        var early = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var late = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        storage.AddFile("with/re-manifest.json", "{}"u8.ToArray(), early);
        storage.AddFile("without/a.txt", "a"u8.ToArray(), early);
        storage.AddFile("without/sub/b.txt", "b"u8.ToArray(), late);

        var manifest = await storage.StatAsync("with/re-manifest.json", CancellationToken.None);
        (await VersionIndex.StampOfAsync(storage, "with", CancellationToken.None)).Should().Be("m:" + manifest!.Stamp);
        (await VersionIndex.StampOfAsync(storage, "without", CancellationToken.None))
            .Should().Be($"s:3:{late.Ticks}", "two files and the folder that holds one of them; the newest time");
        var missing = () => VersionIndex.StampOfAsync(storage, "missing", CancellationToken.None);
        await missing.Should().ThrowAsync<StorageNotFoundException>();
    }

    [Fact]
    public async Task Sync_of_a_storage_version_imports_its_manifest_and_scans_one_without()
    {
        var storage = new InMemoryStorage();
        var manifest = new BackupManifest
        {
            PlanId = PlanId, PlanName = PlanName, Source = Source,
            Files = [new ManifestFile("x.txt", 3, Mtime, "xxh64:0000000000000003")],
        };
        storage.AddFile("2026_09_29-14_05 Projects/x.txt", "xyz"u8.ToArray(), Mtime);
        storage.AddFile("2026_09_29-14_05 Projects/re-manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, JsonDefaults.Options));
        storage.AddFile("2026_09_30-14_05 Projects/sub/y.txt", "yy"u8.ToArray(), Mtime);
        var folders = await VersionCatalog.ListAsync(storage, PlanId, PlanName);
        var index = VersionIndex.Open(_db);

        index.Sync(folders, storage).Should().BeEquivalentTo(new IndexSyncResult(2, 0, 0, []));

        index.Versions().Select(v => (v.Name, v.Origin)).Should().Equal(
            ("2026_09_29-14_05 Projects", IndexOrigin.Manifest), ("2026_09_30-14_05 Projects", IndexOrigin.Scan));
        FilesOf("2026_09_30-14_05 Projects").Should().Equal(("sub/y.txt", 2L, Mtime.Ticks, null));
        index.Sync(folders, storage).Should().BeEquivalentTo(new IndexSyncResult(0, 2, 0, []));
    }
}
