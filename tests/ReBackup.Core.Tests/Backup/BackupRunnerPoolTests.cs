using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Json;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Backup;

/// <summary>
/// Runs of incremental plans (spec 4): the version folder holds only its manifest (format 3), every distinct content is
/// one blob in <c>re-pool</c>, unchanged files are found by the previous manifest (Fast) or by their hash (Safe), and the
/// run holds <c>re-lock.json</c> from start to end. Full plans never touch the lock or the store file.
/// </summary>
public class BackupRunnerPoolTests
{
    private const string First = "2026_10_15-10_00 Projects";
    private const string Second = "2026_10_15-10_01 Projects";
    private static readonly DateTime Mtime = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime OtherMtime = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    /// <summary>A folder target outside the source folder (the runner checks that a folder source exists, not the target).</summary>
    private static readonly string FolderTarget = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "rebackup-pool-test-target");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 8, 0, 30, TimeSpan.Zero));
    private readonly InMemoryStorage _sourceFiles = new();
    private readonly InMemoryStorage _targetFiles = new();
    private readonly ConcurrentQueue<string> _sourceOpens = new();
    private readonly ConcurrentQueue<string> _targetCreates = new();

    public BackupRunnerPoolTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
        _sourceFiles.AddFile("a.txt", Bytes("hello"), Mtime);
        _sourceFiles.AddFile("sub/b.txt", Bytes("hello"), Mtime);
        _sourceFiles.AddFile("c.txt", Bytes("world"), Mtime);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string HashOf(string text) => PoolLayout.Format(XxHash128.HashToUInt128(Bytes(text)));

    private static BackupPlan Plan(bool incremental = true, ChangeDetection detection = ChangeDetection.Fast,
        string sourceKind = StorageLocation.FileSystemKind, string targetKind = StorageLocation.FileSystemKind) => new()
    {
        Id = "p1",
        Name = "Projects",
        Incremental = incremental,
        ChangeDetection = detection,
        Source = Location(sourceKind, Path.GetTempPath(), "conn-1"),
        Target = Location(targetKind, FolderTarget, "conn-2"),
    };

    /// <summary>A folder location needs a real folder as source; other kinds address their storage through the connection.</summary>
    private static StorageLocation Location(string kind, string folder, string connectionId) =>
        kind == StorageLocation.FileSystemKind ? StorageLocation.FileSystem(folder) : new StorageLocation(kind, "", connectionId);

    /// <summary>Runs the plan with the source and target wrapped so that opens and creates are recorded.</summary>
    private Task<RunLogEntry> Run(BackupPlan plan, IStorage? target = null, IStorage? source = null)
    {
        var sourceStorage = source ?? new FaultyStorage(_sourceFiles)
        {
            Before = (operation, path) =>
            {
                if (operation == "open")
                    _sourceOpens.Enqueue(path);
            },
        };
        var targetStorage = target ?? new FaultyStorage(_targetFiles)
        {
            Before = (operation, path) =>
            {
                if (operation == "create")
                    _targetCreates.Enqueue(path);
            },
        };
        var storages = new TestStorageFactory(location => location == plan.Source ? sourceStorage : targetStorage);
        return new BackupRunner(storages, _time, host: "TESTHOST")
            .RunAsync(new BackupRequest(plan, [], RunTrigger.Manual));
    }

    /// <summary>Runs the plan, then moves the clock to the next minute so the next run gets its own version name.</summary>
    private async Task<RunLogEntry> RunAndAdvance(BackupPlan plan)
    {
        var entry = await Run(plan);
        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        _time.Advance(TimeSpan.FromMinutes(1));
        _sourceOpens.Clear();
        _targetCreates.Clear();
        return entry;
    }

    private static BackupManifest Manifest(InMemoryStorage target, string version) =>
        JsonSerializer.Deserialize<BackupManifest>(target.ReadAllBytes($"{version}/re-manifest.json"), JsonDefaults.Options)!;

    private BackupManifest Manifest(string version) => Manifest(_targetFiles, version);

    private static List<string> Blobs(InMemoryStorage target) =>
        target.Files.Where(f => f.StartsWith(PoolLayout.PoolFolder + "/", StringComparison.Ordinal)).Order().ToList();

    private IEnumerable<string> PoolCreates => _targetCreates.Where(p => p.StartsWith(PoolLayout.PoolFolder + "/", StringComparison.Ordinal));

    [Fact]
    public async Task First_run_writes_store_blobs_and_manifest_only()
    {
        var entry = await Run(Plan());

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        entry.Version.Should().Be(First);
        entry.FilesCopied.Should().Be(3);
        entry.BytesCopied.Should().Be(15);

        var versionContent = new List<string>();
        await foreach (var item in _targetFiles.ListAsync(First, recursive: true, CancellationToken.None))
            versionContent.Add(item.Path);
        versionContent.Should().Equal($"{First}/re-manifest.json");

        Blobs(_targetFiles).Should().BeEquivalentTo(PoolLayout.BlobPath(HashOf("hello")), PoolLayout.BlobPath(HashOf("world")));
        _targetFiles.ReadAllBytes(PoolLayout.BlobPath(HashOf("hello"))).Should().Equal(Bytes("hello"));
        _targetFiles.ReadAllBytes(PoolLayout.BlobPath(HashOf("world"))).Should().Equal(Bytes("world"));

        var manifest = Manifest(First);
        manifest.FormatVersion.Should().Be(BackupManifest.PoolFormatVersion);
        manifest.Files.Should().Equal(
            new ManifestFile("a.txt", 5, Mtime, HashOf("hello")),
            new ManifestFile("c.txt", 5, Mtime, HashOf("world")),
            new ManifestFile("sub/b.txt", 5, Mtime, HashOf("hello")));
        manifest.Directories.Should().Equal("sub");
        manifest.FileCount.Should().Be(3);
        manifest.TotalBytes.Should().Be(15);

        using var store = JsonDocument.Parse(_targetFiles.ReadAllBytes(PoolLayout.StoreFile));
        store.RootElement.GetProperty("format").GetString().Should().Be("pool");
        store.RootElement.GetProperty("hash").GetString().Should().Be("xxh128");
        store.RootElement.GetProperty("version").GetInt32().Should().Be(1);
        store.RootElement.GetProperty("planId").GetString().Should().Be("p1");

        _targetFiles.Files.Should().NotContain(PoolLayout.LockFile, "the lock is released when the run ends");
    }

    [Fact]
    public async Task Second_run_unchanged_uploads_nothing()
    {
        await RunAndAdvance(Plan());

        var entry = await Run(Plan());

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        entry.Version.Should().Be(Second);
        PoolCreates.Should().BeEmpty();
        Blobs(_targetFiles).Should().HaveCount(2);
        Manifest(Second).Files.Should().Equal(Manifest(First).Files);
        _targetFiles.Files.Should().NotContain(PoolLayout.LockFile);
    }

    [Fact]
    public async Task Fast_mode_does_not_open_unchanged_files()
    {
        await RunAndAdvance(Plan());
        _sourceFiles.AddFile("c.txt", Bytes("world!"), OtherMtime);

        var entry = await Run(Plan());

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        // The changed file is read twice: once to hash it, once to store the new content.
        _sourceOpens.Should().Equal("c.txt", "c.txt");
        PoolCreates.Should().Equal(PoolLayout.BlobPath(HashOf("world!")));
        Manifest(Second).Files.Should().ContainSingle(f => f.Path == "c.txt").Which.Hash.Should().Be(HashOf("world!"));
    }

    [Fact]
    public async Task Safe_mode_reads_every_file_but_uploads_nothing_new()
    {
        await RunAndAdvance(Plan(detection: ChangeDetection.Safe));

        var entry = await Run(Plan(detection: ChangeDetection.Safe));

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        _sourceOpens.Should().BeEquivalentTo("a.txt", "c.txt", "sub/b.txt");
        PoolCreates.Should().BeEmpty();
        Manifest(Second).Files.Should().Equal(Manifest(First).Files);
    }

    [Fact]
    public async Task Mtime_only_change_rehashes_and_records_new_mtime()
    {
        await RunAndAdvance(Plan());
        _sourceFiles.AddFile("a.txt", Bytes("hello"), OtherMtime);

        var entry = await Run(Plan());

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        _sourceOpens.Should().Equal("a.txt");
        PoolCreates.Should().BeEmpty();
        Manifest(Second).Files.Should().ContainSingle(f => f.Path == "a.txt")
            .Which.Should().Be(new ManifestFile("a.txt", 5, OtherMtime, HashOf("hello")));
    }

    [Theory]
    [InlineData(ChangeDetection.Fast)]
    [InlineData(ChangeDetection.Safe)]
    public async Task Restored_mtime_with_new_content_is_missed_in_fast_and_caught_in_safe(ChangeDetection detection)
    {
        await RunAndAdvance(Plan(detection: detection));
        _sourceFiles.AddFile("c.txt", Bytes("WORLD"), Mtime);   // same size, same time, other content

        var entry = await Run(Plan(detection: detection));

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        var c = Manifest(Second).Files.Single(f => f.Path == "c.txt");
        if (detection == ChangeDetection.Fast)
        {
            c.Hash.Should().Be(HashOf("world"), "Fast mode trusts size and time");
            PoolCreates.Should().BeEmpty();
        }
        else
        {
            c.Hash.Should().Be(HashOf("WORLD"));
            PoolCreates.Should().Equal(PoolLayout.BlobPath(HashOf("WORLD")));
            _targetFiles.ReadAllBytes(PoolLayout.BlobPath(HashOf("WORLD"))).Should().Equal(Bytes("WORLD"));
        }
    }

    [Fact]
    public async Task Content_changing_between_hash_and_store_skips_the_file_as_locked()
    {
        var opens = 0;
        var source = new FaultyStorage(_sourceFiles)
        {
            Before = (operation, path) =>
            {
                // The second read of c.txt (the one that stores it) sees other content than the first one hashed.
                if (operation == "open" && path == "c.txt" && ++opens == 2)
                    _sourceFiles.AddFile("c.txt", Bytes("WORLD"), Mtime);
            },
        };

        var entry = await Run(Plan(), source: source);

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings, entry.Reason);
        entry.Skipped.Should().Equal(new SkippedEntry("c.txt", CoreTexts.English("core.file.locked"), "core.file.locked"));
        Manifest(First).Files.Select(f => f.Path).Should().Equal("a.txt", "sub/b.txt");
        Blobs(_targetFiles).Should().Equal(PoolLayout.BlobPath(HashOf("hello")));
    }

    [Fact]
    public async Task Locked_target_skips_run_without_writing()
    {
        await using var held = await TargetLock.TryAcquireAsync(_targetFiles, "OTHERPC", "verify", _time, CancellationToken.None);
        held.Should().NotBeNull();
        var before = _targetFiles.Files.ToList();

        var entry = await Run(Plan());

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Be(CoreTexts.English("core.run.targetLocked",
            ("host", "OTHERPC"), ("operation", "verify"), ("since", "2026-10-15 08:00 UTC")));
        _targetFiles.Files.Should().BeEquivalentTo(before);
        _sourceOpens.Should().BeEmpty();
        (await TargetLock.ReadAsync(_targetFiles, CancellationToken.None))!.Token.Should().Be(held!.Info.Token,
            "another holder's lock is never released");
    }

    [Fact]
    public async Task Lock_that_cannot_be_released_is_a_warning()
    {
        var target = new FaultyStorage(_targetFiles) { FailDelete = path => path == PoolLayout.LockFile };

        var entry = await Run(Plan(), target);

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        entry.Warnings.Should().ContainSingle().Which.Should().StartWith(
            CoreTexts.English("core.run.lockReleaseFailed", ("error", "")));
    }

    [Fact]
    public async Task Full_plan_runs_unchanged_on_pool_free_target()
    {
        var entry = await Run(Plan(incremental: false));

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        _targetCreates.Should().NotContain(PoolLayout.LockFile).And.NotContain(PoolLayout.StoreFile);
        _targetFiles.Files.Should().BeEquivalentTo(
            $"{First}/a.txt", $"{First}/c.txt", $"{First}/sub/b.txt", $"{First}/re-manifest.json");
        var manifest = Manifest(First);
        manifest.FormatVersion.Should().Be(BackupManifest.CurrentFormatVersion);
        manifest.Files.Should().OnlyContain(f => f.Hash.StartsWith("xxh64:"));
    }

    [Fact]
    public async Task Full_plan_on_pool_target_aborts_without_writing()
    {
        await RunAndAdvance(Plan());
        var before = _targetFiles.Files.ToList();

        var entry = await Run(Plan(incremental: false));

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Be(CoreTexts.English("core.run.storeFullOnPool"));
        _targetFiles.Files.Should().BeEquivalentTo(before);
        _targetCreates.Should().BeEmpty();
    }

    [Fact]
    public async Task Incremental_plan_on_full_target_aborts_and_releases_the_lock()
    {
        await RunAndAdvance(Plan(incremental: false));
        var before = _targetFiles.Files.ToList();

        var entry = await Run(Plan());

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Be(CoreTexts.English("core.run.storePoolOnFull"));
        _targetFiles.Files.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Incremental_plan_on_another_plans_full_target_aborts_and_the_full_plan_keeps_running()
    {
        var other = Plan(incremental: false);
        other.Id = "p2";
        other.Name = "Other";
        await RunAndAdvance(other);
        var before = _targetFiles.Files.ToList();

        var entry = await Run(Plan());

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Be(CoreTexts.English("core.run.storeNotEmpty"));
        _targetFiles.Files.Should().BeEquivalentTo(before);
        (await Run(other)).Status.Should().Be(RunStatus.Completed);
    }

    [Fact]
    public async Task Parallel_copy_path_writes_same_result()
    {
        var parallelTarget = new InMemoryStorage(StorageCapabilities.None);
        var sequential = await Run(Plan());
        var parallel = await Run(Plan(sourceKind: "mem", targetKind: "s3"), target: parallelTarget);

        sequential.Status.Should().Be(RunStatus.Completed, sequential.Reason);
        parallel.Status.Should().Be(RunStatus.Completed, parallel.Reason);
        Manifest(parallelTarget, First).Files.Should().Equal(Manifest(First).Files);
        Manifest(parallelTarget, First).Directories.Should().Equal("sub");
        Blobs(parallelTarget).Should().Equal(Blobs(_targetFiles));
        parallelTarget.Files.Where(f => f.StartsWith(First + "/", StringComparison.Ordinal))
            .Should().Equal($"{First}/re-manifest.json");
    }

    [Fact]
    public async Task Pool_folders_are_not_listed_as_versions()
    {
        await RunAndAdvance(Plan());
        await RunAndAdvance(Plan());

        var versions = await VersionCatalog.ListAsync(_targetFiles, "p1", "Projects");

        versions.Select(v => v.Name).Should().Equal(First, Second);
        versions.Should().OnlyContain(v => v.IsOwned);
    }

    [Fact]
    public async Task Free_space_preflight_counts_only_changed_files()
    {
        await RunAndAdvance(Plan());
        _sourceFiles.AddFile("c.txt", Bytes("world!"), OtherMtime);
        _targetFiles.FreeSpace = 7;   // room for c.txt (6 bytes) plus the margin, not for all 16 bytes

        var entry = await Run(Plan());

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
    }
}
