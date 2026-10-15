using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Json;
using ReBackup.Shared.Retention;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Pool;

/// <summary>Copy plan / switch mode (spec 10): all versions to a new, empty target, full ↔ incremental, resumable.</summary>
public class PlanCopierTests
{
    private const string Host = "PC1";
    private const string SourceRoot = @"C:\backup\projects";
    private const string CopyRoot = @"D:\nas\projects";
    private static readonly DateTime Created = new(2026, 3, 4, 5, 6, 0, DateTimeKind.Utc);
    private static readonly DateTime FileTime = new(2025, 12, 24, 18, 30, 15, DateTimeKind.Utc);

    private readonly Dictionary<string, IStorage> _storages = new(StringComparer.OrdinalIgnoreCase);
    private readonly InMemoryStorage _from = new();
    private readonly InMemoryStorage _to = new();
    private readonly BackupPlan _source;

    public PlanCopierTests()
    {
        _storages[SourceRoot] = _from;
        _storages[CopyRoot] = _to;
        _source = new BackupPlan
        {
            Id = "source-plan",
            Name = "Projects",
            Source = StorageLocation.FileSystem(@"C:\data"),
            Target = StorageLocation.FileSystem(SourceRoot),
        };
    }

    private PlanCopier Copier(Func<string, ConnectionBucket?>? connections = null) =>
        new(new TestStorageFactory(location => _storages.TryGetValue(location.Path, out var storage)
            ? storage
            : _storages[location.Path] = new InMemoryStorage()), Host, TimeProvider.System, connections);

    private BackupPlan CopyPlan(bool incremental, string? name = null) =>
        PlanCopier.MakeCopy(_source, name ?? _source.Name, StorageLocation.FileSystem(CopyRoot), incremental);

    private static string Xxh64(string text) => "xxh64:" + Convert.ToHexStringLower(XxHash64.Hash(Encoding.UTF8.GetBytes(text)));

    /// <summary>A full version: the files in the folder and a format 2 manifest with their xxh64.</summary>
    private void FullVersion(string folder, params (string Path, string Content)[] files) =>
        FullVersionWithHashes(folder, files.Select(f => (f.Path, f.Content, Xxh64(f.Content))).ToArray());

    private void FullVersionWithHashes(string folder, params (string Path, string Content, string Hash)[] files)
    {
        var manifest = new BackupManifest
        {
            PlanId = _source.Id,
            PlanName = _source.Name,
            CreatedUtc = Created,
            Source = @"C:\data",
            Directories = ["docs", "empty"],
        };
        foreach (var (path, content, hash) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            _from.AddFile(StoragePath.Combine(folder, path), bytes, FileTime);
            manifest.Files.Add(new ManifestFile(path, bytes.Length, FileTime, hash));
        }
        manifest.FileCount = manifest.Files.Count;
        manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
        _from.AddFile(StoragePath.Combine(folder, VersionMarkerNames.Manifest),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonDefaults.Options)));
    }

    private void MakeSourcePool()
    {
        _source.Incremental = true;
        _from.AddFile(PoolLayout.StoreFile, Encoding.UTF8.GetBytes(
            $"{{\"format\":\"pool\",\"hash\":\"xxh128\",\"version\":1,\"planId\":\"{_source.Id}\"}}"));
    }

    private static BackupManifest ManifestOf(InMemoryStorage storage, string folder) =>
        JsonSerializer.Deserialize<BackupManifest>(
            storage.ReadAllBytes(StoragePath.Combine(folder, VersionMarkerNames.Manifest)), JsonDefaults.Options)!;

    private static string Text(InMemoryStorage storage, string path) => Encoding.UTF8.GetString(storage.ReadAllBytes(path));

    [Fact]
    public async Task Refuses_same_target()
    {
        (await Copier().CheckAsync(_source, StorageLocation.FileSystem(SourceRoot + @"\"), CancellationToken.None))
            .Should().Be(CopyRefusal.SameOrNestedTarget);
    }

    [Fact]
    public async Task Refuses_nested_target_both_ways()
    {
        var copier = Copier();
        (await copier.CheckAsync(_source, StorageLocation.FileSystem(SourceRoot + @"\copy"), CancellationToken.None))
            .Should().Be(CopyRefusal.SameOrNestedTarget);
        (await copier.CheckAsync(_source, StorageLocation.FileSystem(@"C:\backup"), CancellationToken.None))
            .Should().Be(CopyRefusal.SameOrNestedTarget);
        (await copier.CheckAsync(_source, StorageLocation.FileSystem(@"C:\backup\projects2"), CancellationToken.None))
            .Should().Be(CopyRefusal.None);
    }

    [Fact]
    public async Task Refuses_nested_target_on_the_same_s3_connection()
    {
        _source.Target = new StorageLocation(PlanValidator.S3Kind, "backups/projects", "conn1");
        var copier = Copier();

        (await copier.CheckAsync(_source, new StorageLocation(PlanValidator.S3Kind, "backups", "conn1"), CancellationToken.None))
            .Should().Be(CopyRefusal.SameOrNestedTarget);
        (await copier.CheckAsync(_source, new StorageLocation(PlanValidator.S3Kind, "backups", "conn2"), CancellationToken.None))
            .Should().Be(CopyRefusal.None);
    }

    [Fact]
    public async Task Refuses_nested_target_through_another_connection_to_the_same_bucket()
    {
        _source.Target = new StorageLocation(PlanValidator.S3Kind, "backups/projects", "conn1");
        var buckets = new Dictionary<string, ConnectionBucket>
        {
            ["conn1"] = new("company-backups", "eu-central-1"),
            ["conn2"] = new("company-backups", "eu-central-1"),
            ["conn3"] = new("other-bucket", "eu-central-1"),
        };
        var copier = Copier(id => buckets.GetValueOrDefault(id));

        (await copier.CheckAsync(_source, new StorageLocation(PlanValidator.S3Kind, "backups/projects/copy", "conn2"),
            CancellationToken.None)).Should().Be(CopyRefusal.SameOrNestedTarget);
        (await copier.CheckAsync(_source, new StorageLocation(PlanValidator.S3Kind, "backups", "conn2"),
            CancellationToken.None)).Should().Be(CopyRefusal.SameOrNestedTarget);
        (await copier.CheckAsync(_source, new StorageLocation(PlanValidator.S3Kind, "backups/projects2", "conn2"),
            CancellationToken.None)).Should().Be(CopyRefusal.None);
        (await copier.CheckAsync(_source, new StorageLocation(PlanValidator.S3Kind, "backups/projects", "conn3"),
            CancellationToken.None)).Should().Be(CopyRefusal.None);

        var copy = PlanCopier.MakeCopy(_source, "Projects", new StorageLocation(PlanValidator.S3Kind, "backups/projects/copy", "conn2"),
            incremental: true);
        var result = await copier.CopyAsync(_source, copy, null, CancellationToken.None);
        result.Failures.Should().ContainSingle().Which.Should().Contain("Nothing was copied");
        result.VersionsCopied.Should().Be(0);
    }

    [Fact]
    public async Task Refuses_non_empty_target()
    {
        var copier = Copier();
        var target = StorageLocation.FileSystem(CopyRoot);
        _to.AddFile("notes.txt", Encoding.UTF8.GetBytes("someone's"));
        (await copier.CheckAsync(_source, target, CancellationToken.None)).Should().Be(CopyRefusal.None);

        _to.AddFile("2026_01_01-10_00 Other/re-manifest.json", Encoding.UTF8.GetBytes("{}"));
        (await copier.CheckAsync(_source, target, CancellationToken.None)).Should().Be(CopyRefusal.TargetNotEmpty);

        var storeOnly = new InMemoryStorage();
        storeOnly.AddFile(PoolLayout.StoreFile, Encoding.UTF8.GetBytes("{}"));
        _storages[@"E:\other"] = storeOnly;
        (await copier.CheckAsync(_source, StorageLocation.FileSystem(@"E:\other"), CancellationToken.None))
            .Should().Be(CopyRefusal.TargetNotEmpty);
    }

    [Fact]
    public async Task Refuses_locked_source()
    {
        FullVersion("2026_01_01-10_00 Projects", ("a.txt", "alpha"));
        await using var held = await TargetLock.TryAcquireAsync(_from, "PC2", "backup", TimeProvider.System, CancellationToken.None);

        (await Copier().CheckAsync(_source, StorageLocation.FileSystem(CopyRoot), CancellationToken.None))
            .Should().Be(CopyRefusal.SourceLocked);
        var result = await Copier().CopyAsync(_source, CopyPlan(incremental: true), null, CancellationToken.None);

        result.Failures.Should().ContainSingle().Which.Should().Contain("PC2");
        result.VersionsCopied.Should().Be(0);
        _to.Files.Should().BeEmpty();
    }

    [Fact]
    public void MakeCopy_is_disabled_with_same_settings()
    {
        _source.FreeSpaceByRetention = true;
        _source.ChangeDetection = ChangeDetection.Safe;
        _source.Retention = [new RetentionRule { Period = RetentionPeriod.Weekly, Anchor = "Sunday", Keep = 7 }];
        _source.Ignore.Patterns.Add("*.tmp");

        var copy = PlanCopier.MakeCopy(_source, "Projects NAS", StorageLocation.FileSystem(CopyRoot), incremental: true);

        copy.Id.Should().NotBe(_source.Id);
        copy.Name.Should().Be("Projects NAS");
        copy.Target.Should().Be(StorageLocation.FileSystem(CopyRoot));
        copy.Incremental.Should().BeTrue();
        copy.Enabled.Should().BeFalse();
        copy.Source.Should().Be(_source.Source);
        copy.FreeSpaceByRetention.Should().BeTrue();
        copy.ChangeDetection.Should().Be(ChangeDetection.Safe);
        copy.Retention.Should().BeEquivalentTo(_source.Retention);
        copy.Retention.Should().NotBeSameAs(_source.Retention);
        copy.Ignore.Patterns.Should().Equal("*.tmp");
        _source.Enabled.Should().BeTrue();
        _source.Incremental.Should().BeFalse();
    }

    [Fact]
    public async Task Full_to_pool_keeps_names_times_and_content()
    {
        FullVersion("2026_01_01-10_00 Projects", ("a.txt", "alpha"), ("docs/b.txt", "beta"));
        FullVersion("2026_01_02-10_00 Projects", ("a.txt", "alpha"), ("docs/b.txt", "beta 2"), ("c.txt", "alpha"));
        var copy = CopyPlan(incremental: true, name: "Projects NAS");

        var result = await Copier().CopyAsync(_source, copy, null, CancellationToken.None);

        result.Should().BeEquivalentTo(new PlanCopyResult(2, 0, [], false));
        var versions = await VersionCatalog.ListAsync(_to, copy.Id, copy.Name);
        versions.Select(v => (v.Name, v.Ownership)).Should().Equal(
            ("2026_01_01-10_00 Projects NAS", VersionOwnership.Owned),
            ("2026_01_02-10_00 Projects NAS", VersionOwnership.Owned));
        (await StoreGuard.ReadAsync(_to))!.PlanId.Should().Be(copy.Id);
        PoolVersions.Blobs(_to).Should().HaveCount(3);   // alpha, beta, beta 2
        _to.Files.Should().NotContain(f => f.EndsWith("a.txt", StringComparison.Ordinal));
        _to.Files.Should().NotContain(f => f.EndsWith(VersionMarkerNames.Pending, StringComparison.Ordinal) ||
                                          f == PoolLayout.LockFile);

        var manifest = ManifestOf(_to, "2026_01_02-10_00 Projects NAS");
        manifest.FormatVersion.Should().Be(BackupManifest.PoolFormatVersion);
        manifest.PlanId.Should().Be(copy.Id);
        manifest.PlanName.Should().Be(copy.Name);
        manifest.CreatedUtc.Should().Be(Created);
        manifest.Source.Should().Be(@"C:\data");
        manifest.Directories.Should().Equal("docs", "empty");
        manifest.Files.Select(f => (f.Path, f.MtimeUtc, f.Hash)).Should().Equal(
            ("a.txt", FileTime, PoolVersions.HashOf("alpha")),
            ("docs/b.txt", FileTime, PoolVersions.HashOf("beta 2")),
            ("c.txt", FileTime, PoolVersions.HashOf("alpha")));
        (await PoolVersions.RestoreAsync(_to, "2026_01_02-10_00 Projects NAS")).Should().BeEquivalentTo(
            new Dictionary<string, string> { ["a.txt"] = "alpha", ["docs/b.txt"] = "beta 2", ["c.txt"] = "alpha" });
    }

    [Fact]
    public async Task Pool_to_full_keeps_names_times_and_content()
    {
        MakeSourcePool();
        PoolVersions.Write(_from, "2026_01_01-10_00 Projects", _source.Id, _source.Name,
            [("a.txt", "alpha"), ("docs/b.txt", "beta")], directories: ["docs", "empty"]);
        PoolVersions.Write(_from, "2026_01_02-10_00 Projects", _source.Id, _source.Name,
            [("a.txt", "alpha 2"), ("docs/b.txt", "beta")], hashCase: h => h.ToUpperInvariant());
        var copy = CopyPlan(incremental: false);

        var result = await Copier().CopyAsync(_source, copy, null, CancellationToken.None);

        result.Should().BeEquivalentTo(new PlanCopyResult(2, 0, [], false));
        var versions = await VersionCatalog.ListAsync(_to, copy.Id, copy.Name);
        versions.Select(v => (v.Name, v.Ownership)).Should().Equal(
            ("2026_01_01-10_00 Projects", VersionOwnership.Owned),
            ("2026_01_02-10_00 Projects", VersionOwnership.Owned));
        _to.Files.Should().NotContain(f => f.StartsWith(PoolLayout.PoolFolder, StringComparison.Ordinal) ||
                                          f == PoolLayout.StoreFile || f == PoolLayout.LockFile);

        Text(_to, "2026_01_01-10_00 Projects/a.txt").Should().Be("alpha");
        Text(_to, "2026_01_01-10_00 Projects/docs/b.txt").Should().Be("beta");
        Text(_to, "2026_01_02-10_00 Projects/a.txt").Should().Be("alpha 2");
        (await _to.StatAsync("2026_01_01-10_00 Projects/empty", CancellationToken.None))!.IsDirectory.Should().BeTrue();
        (await _to.StatAsync("2026_01_02-10_00 Projects/a.txt", CancellationToken.None))!.ModifiedUtc
            .Should().Be(PoolVersions.Mtime);

        var manifest = ManifestOf(_to, "2026_01_02-10_00 Projects");
        manifest.FormatVersion.Should().Be(BackupManifest.CurrentFormatVersion);
        manifest.PlanId.Should().Be(copy.Id);
        manifest.CreatedUtc.Should().Be(PoolVersions.Mtime);
        manifest.Files.Should().BeEquivalentTo(new[]
        {
            new ManifestFile("a.txt", 7, PoolVersions.Mtime, Xxh64("alpha 2")),
            new ManifestFile("docs/b.txt", 4, PoolVersions.Mtime, Xxh64("beta")),
        });
    }

    [Fact]
    public async Task Pool_to_pool_copies_to_another_target()
    {
        MakeSourcePool();
        PoolVersions.Write(_from, "2026_01_01-10_00 Projects", _source.Id, _source.Name, [("a.txt", "alpha"), ("b.txt", "alpha")]);
        var copy = CopyPlan(incremental: true);

        var result = await Copier().CopyAsync(_source, copy, null, CancellationToken.None);

        result.Should().BeEquivalentTo(new PlanCopyResult(1, 0, [], false));
        PoolVersions.Blobs(_to).Should().Equal(PoolVersions.BlobOf("alpha"));
        (await PoolVersions.RestoreAsync(_to, "2026_01_01-10_00 Projects")).Should().BeEquivalentTo(
            new Dictionary<string, string> { ["a.txt"] = "alpha", ["b.txt"] = "alpha" });
    }

    [Fact]
    public async Task Resume_skips_complete_versions()
    {
        string[] names = ["2026_01_01-10_00 Projects", "2026_01_02-10_00 Projects", "2026_01_03-10_00 Projects", "2026_01_04-10_00 Projects"];
        for (var i = 0; i < names.Length; i++)
            FullVersion(names[i], ("a.txt", "alpha " + i), ("b.txt", "beta"));
        var copy = CopyPlan(incremental: true);
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress<PlanCopyProgress>(p =>
        {
            p.VersionCount.Should().Be(4);
            if (p.Version == 3 && p.BytesDone > 0)
                cancel.Cancel();   // in the middle of version 3: its folder is half-written
        });

        var first = await Copier().CopyAsync(_source, copy, progress, cancel.Token);

        first.Canceled.Should().BeTrue();
        first.VersionsCopied.Should().Be(2);
        _to.Files.Should().NotContain(PoolLayout.LockFile);
        _from.Files.Should().NotContain(PoolLayout.LockFile);
        (await VersionCatalog.ListAsync(_to, copy.Id, copy.Name)).Should().HaveCount(2);
        _to.Files.Should().Contain(StoragePath.Combine(names[2], VersionMarkerNames.Pending));

        var second = await Copier().CopyAsync(_source, copy, null, CancellationToken.None);

        second.Should().BeEquivalentTo(new PlanCopyResult(2, 2, [], false));
        (await VersionCatalog.ListAsync(_to, copy.Id, copy.Name)).Select(v => v.Name).Should().Equal(names);
        _to.Files.Should().NotContain(f => f.EndsWith(VersionMarkerNames.Pending, StringComparison.Ordinal));
        (await PoolVersions.RestoreAsync(_to, names[3])).Should().BeEquivalentTo(
            new Dictionary<string, string> { ["a.txt"] = "alpha 3", ["b.txt"] = "beta" });
    }

    [Fact]
    public async Task Resume_rewrites_a_half_written_version()
    {
        const string name = "2026_01_01-10_00 Projects";
        FullVersion(name, ("a.txt", "alpha"), ("docs/b.txt", "beta"));
        var copy = CopyPlan(incremental: false);
        // What a copy that stopped after writing its manifest, before removing the marker, leaves behind.
        await VersionMarkers.WritePendingAsync(_to, name,
            new MarkerInfo(VersionMarkers.FormatVersion, copy.Id, copy.Name, DateTime.UtcNow, Host), CancellationToken.None);
        _to.AddFile(StoragePath.Combine(name, "a.txt"), Encoding.UTF8.GetBytes("half"));
        _to.AddFile(StoragePath.Combine(name, "stale.txt"), Encoding.UTF8.GetBytes("stale"));
        _to.AddFile(StoragePath.Combine(name, VersionMarkerNames.Manifest), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new BackupManifest { PlanId = copy.Id, PlanName = copy.Name, Files = [new ManifestFile("a.txt", 4, FileTime, Xxh64("half"))] },
            JsonDefaults.Options)));

        var result = await Copier().CopyAsync(_source, copy, null, CancellationToken.None);

        result.Should().BeEquivalentTo(new PlanCopyResult(1, 0, [], false));
        _to.Files.Where(f => f.StartsWith(name + "/", StringComparison.Ordinal)).Should().BeEquivalentTo(
            $"{name}/a.txt", $"{name}/docs/b.txt", $"{name}/{VersionMarkerNames.Manifest}");
        Text(_to, $"{name}/a.txt").Should().Be("alpha");
        ManifestOf(_to, name).Files.Select(f => (f.Path, f.Hash)).Should().Equal(
            ("a.txt", Xxh64("alpha")), ("docs/b.txt", Xxh64("beta")));
        (await VersionCatalog.ListAsync(_to, copy.Id, copy.Name)).Should().ContainSingle().Which.IsOwned.Should().BeTrue();
    }

    [Fact]
    public async Task A_copy_name_that_cannot_be_a_folder_name_is_refused_without_writing()
    {
        FullVersion("2026_01_01-10_00 Projects", ("a.txt", "alpha"));

        var result = await Copier().CopyAsync(_source, CopyPlan(incremental: true, name: "Pro/jects"), null, CancellationToken.None);

        result.VersionsCopied.Should().Be(0);
        result.Failures.Should().ContainSingle();
        _to.Files.Should().BeEmpty();
        _from.Files.Should().NotContain(PoolLayout.LockFile);
    }

    [Fact]
    public async Task Folders_that_are_not_versions_of_the_plan_are_reported()
    {
        FullVersion("2026_01_01-10_00 Projects", ("a.txt", "alpha"));
        _from.AddFile("2026_01_02-10_00 Projects/a.txt", Encoding.UTF8.GetBytes("no manifest"));

        var result = await Copier().CopyAsync(_source, CopyPlan(incremental: true), null, CancellationToken.None);

        result.VersionsCopied.Should().Be(1);
        result.Failures.Should().ContainSingle().Which.Should().StartWith("Folders named like versions").And.EndWith(": 1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Corrupt_source_file_is_reported_and_not_written(bool incremental)
    {
        FullVersionWithHashes("2026_01_01-10_00 Projects",
            ("a.txt", "alpha", Xxh64("alpha")), ("bad.txt", "damaged", Xxh64("original")));
        var copy = CopyPlan(incremental);

        var result = await Copier().CopyAsync(_source, copy, null, CancellationToken.None);

        result.VersionsCopied.Should().Be(1);
        result.Failures.Should().ContainSingle().Which.Should().Contain("bad.txt").And.Contain("2026_01_01-10_00 Projects");
        ManifestOf(_to, "2026_01_01-10_00 Projects").Files.Select(f => f.Path).Should().Equal("a.txt");
        if (incremental)
            PoolVersions.Blobs(_to).Should().Equal(PoolVersions.BlobOf("alpha"));
        else
            _to.Files.Should().NotContain("2026_01_01-10_00 Projects/bad.txt");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Corrupt_source_blob_is_reported_and_not_written(bool incremental)
    {
        MakeSourcePool();
        PoolVersions.Write(_from, "2026_01_01-10_00 Projects", _source.Id, _source.Name, [("a.txt", "alpha"), ("b.txt", "beta")]);
        _from.AddFile(PoolVersions.BlobOf("beta"), Encoding.UTF8.GetBytes("damaged"));

        var result = await Copier().CopyAsync(_source, CopyPlan(incremental), null, CancellationToken.None);

        result.VersionsCopied.Should().Be(1);
        result.Failures.Should().ContainSingle().Which.Should().Contain("b.txt");
        ManifestOf(_to, "2026_01_01-10_00 Projects").Files.Select(f => f.Path).Should().Equal("a.txt");
        _to.Files.Should().NotContain(f => f.EndsWith("b.txt", StringComparison.Ordinal) ||
                                          f == PoolVersions.BlobOf("beta"));
    }

    private static readonly string[] Three = ["2026_01_01-10_00 Projects", "2026_01_02-10_00 Projects", "2026_01_03-10_00 Projects"];

    /// <summary>Three full versions whose a.txt differs ("alpha 0" …), and the path the copy writes the second one's a.txt to.</summary>
    private string ThreeVersions(bool incremental)
    {
        for (var i = 0; i < Three.Length; i++)
            FullVersion(Three[i], ("a.txt", "alpha " + i), ("b.txt", "beta"));
        return incremental ? PoolVersions.BlobOf("alpha 1") : $"{Three[1]}/a.txt";
    }

    /// <summary>Every version of the copy is listed as its own and restores to the source's files.</summary>
    private async Task AllThreeRestore(BackupPlan copy)
    {
        (await VersionCatalog.ListAsync(_to, copy.Id, copy.Name)).Where(v => v.IsOwned).Select(v => v.Name)
            .Should().Equal(Three);
        for (var i = 0; i < Three.Length; i++)
        {
            var restored = copy.Incremental
                ? await PoolVersions.RestoreAsync(_to, Three[i])
                : new Dictionary<string, string> { ["a.txt"] = Text(_to, $"{Three[i]}/a.txt"), ["b.txt"] = Text(_to, $"{Three[i]}/b.txt") };
            restored.Should().BeEquivalentTo(new Dictionary<string, string> { ["a.txt"] = "alpha " + i, ["b.txt"] = "beta" });
        }
        _to.Files.Should().NotContain(f => f.EndsWith(VersionMarkerNames.Pending, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task New_target_full_partway_stops_the_copy_and_leaves_the_version_pending(bool incremental)
    {
        var second = ThreeVersions(incremental);
        var full = false;
        _storages[CopyRoot] = new FaultyStorage(_to)
        {
            Before = (operation, path) => full |= operation == "create" && path == second,
            DiskFullOnWrite = _ => full,
        };
        var copy = CopyPlan(incremental);

        var result = await Copier().CopyAsync(_source, copy, null, CancellationToken.None);

        result.Stopped.Should().BeTrue();
        result.Finished.Should().BeFalse();
        result.VersionsCopied.Should().Be(1);
        result.Failures.Should().ContainSingle().Which.Should().StartWith("The copy stopped");
        (await VersionCatalog.ListAsync(_to, copy.Id, copy.Name)).Where(v => v.IsOwned).Select(v => v.Name)
            .Should().Equal(Three[0]);
        _to.Files.Should().Contain(StoragePath.Combine(Three[1], VersionMarkerNames.Pending));

        // Space freed: the copy is continued and finishes.
        _storages[CopyRoot] = _to;
        var resumed = await Copier().CopyAsync(_source, copy, null, CancellationToken.None);

        resumed.Should().BeEquivalentTo(new PlanCopyResult(2, 1, [], false));
        resumed.Finished.Should().BeTrue();
        await AllThreeRestore(copy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task New_target_write_error_fails_the_version_and_the_next_copy_writes_it(bool incremental)
    {
        var second = ThreeVersions(incremental);
        var failing = true;
        _storages[CopyRoot] = new FaultyStorage(_to) { FailCreate = path => failing && path == second };
        var copy = CopyPlan(incremental);

        var result = await Copier().CopyAsync(_source, copy, null, CancellationToken.None);

        result.Stopped.Should().BeFalse();
        result.VersionsFailed.Should().Be(1);
        result.Finished.Should().BeFalse();
        result.VersionsCopied.Should().Be(2);
        result.Failures.Should().ContainSingle().Which.Should().StartWith($"\"{Three[1]}\" was not copied");
        (await VersionCatalog.ListAsync(_to, copy.Id, copy.Name)).Where(v => v.IsOwned).Select(v => v.Name)
            .Should().Equal(Three[0], Three[2]);
        _to.Files.Should().Contain(StoragePath.Combine(Three[1], VersionMarkerNames.Pending));

        failing = false;
        var resumed = await Copier().CopyAsync(_source, copy, null, CancellationToken.None);

        resumed.Should().BeEquivalentTo(new PlanCopyResult(1, 2, [], false));
        await AllThreeRestore(copy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Source_is_never_written(bool incremental)
    {
        FullVersion("2026_01_01-10_00 Projects", ("a.txt", "alpha"), ("docs/b.txt", "beta"));
        FullVersion("2026_01_02-10_00 Projects", ("a.txt", "alpha 2"));
        var wrapped = new WrappedStorage(_from);
        _storages[SourceRoot] = wrapped;

        var result = await Copier().CopyAsync(_source, CopyPlan(incremental), null, CancellationToken.None);

        result.Should().BeEquivalentTo(new PlanCopyResult(2, 0, [], false));
        wrapped.CreatedPaths.Should().Equal(PoolLayout.LockFile);
        wrapped.DeletedPaths.Should().Equal(PoolLayout.LockFile);
    }

    [Fact]
    public async Task Copy_onto_the_source_target_is_refused_without_writing()
    {
        FullVersion("2026_01_01-10_00 Projects", ("a.txt", "alpha"));
        var copy = PlanCopier.MakeCopy(_source, "Projects", StorageLocation.FileSystem(SourceRoot + @"\inner"), incremental: true);
        var before = _from.Files.ToList();

        var result = await Copier().CopyAsync(_source, copy, null, CancellationToken.None);

        result.VersionsCopied.Should().Be(0);
        result.Failures.Should().ContainSingle();
        _from.Files.Should().BeEquivalentTo(before);
    }
}
