using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;
using static ReBackup.Core.Tests.TestSupport.PoolVersions;

namespace ReBackup.Core.Tests.Pool;

/// <summary>
/// Garbage collection of an incremental plan's pool (spec 6), the space a version alone holds (spec 5), and the places
/// that collect: a run after retention, freeing space before a run, and a manual delete.
/// </summary>
public class PoolCollectorTests
{
    private const string PlanId = "p1";
    private const string PlanName = "Projects";
    private const string V1 = "2026_10_15-10_00 Projects";
    private const string V2 = "2026_10_16-10_00 Projects";
    private const string V3 = "2026_10_17-10_00 Projects";

    private readonly InMemoryStorage _target = new();

    private void Version(string name, params (string Path, string Content)[] files) =>
        PoolVersions.Write(_target, name, PlanId, PlanName, files);

    private Task<CollectResult> Collect(IStorage? target = null) =>
        PoolCollector.CollectAsync(target ?? _target, PlanId, PlanName, CancellationToken.None);

    [Fact]
    public async Task Unreferenced_blobs_are_deleted()
    {
        Version(V1, ("a.txt", "kept"));
        AddBlob(_target, "orphan");

        var result = await Collect();

        result.Should().Be(new CollectResult(1, 6, null));
        Blobs(_target).Should().Equal(BlobOf("kept"));
    }

    [Fact]
    public async Task Blob_shared_with_kept_version_survives()
    {
        Version(V1, ("a.txt", "shared"), ("b.txt", "only-v1"));
        Version(V2, ("a.txt", "shared"));
        await _target.DeleteAsync([$"{V1}/re-manifest.json"], CancellationToken.None);

        var result = await Collect();

        result.Should().Be(new CollectResult(1, 7, null));
        Blobs(_target).Should().Equal(BlobOf("shared"));
        (await RestoreAsync(_target, V2)).Should().Equal(new Dictionary<string, string> { ["a.txt"] = "shared" });
    }

    [Fact]
    public async Task Blob_named_in_other_case_by_a_manifest_survives()
    {
        PoolVersions.Write(_target, V1, PlanId, PlanName, [("a.txt", "upper")], hash => hash.ToUpperInvariant());

        var result = await Collect();

        result.Should().Be(new CollectResult(0, 0, null));
        Blobs(_target).Should().Equal(BlobOf("upper"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"formatVersion\":3,\"planId\":\"p1\",\"planName\":\"Projects\",\"files\":[{\"path\":\"a.txt\",")]
    public async Task Unreadable_manifest_aborts_collection(string manifest)
    {
        Version(V1, ("a.txt", "kept"));
        Version(V2, ("a.txt", "only-v2"));
        _target.AddFile($"{V2}/re-manifest.json", Encoding.UTF8.GetBytes(manifest));
        AddBlob(_target, "orphan");
        var before = _target.Files.ToList();

        var result = await Collect();

        result.Should().Be(new CollectResult(0, 0, CoreTexts.English("core.gc.manifestUnreadable", ("version", V2))));
        _target.Files.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Pending_marker_skips_collection()
    {
        Version(V1, ("a.txt", "kept"));
        Version(V2, ("a.txt", "being-written"));
        _target.AddFile($"{V2}/re-pending.json", Bytes("{}"));
        AddBlob(_target, "orphan");
        var before = _target.Files.ToList();

        var result = await Collect();

        result.Should().Be(new CollectResult(0, 0, CoreTexts.English("core.gc.skippedMarkers")));
        _target.Files.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Foreign_marker_skips_collection()
    {
        Version(V1, ("a.txt", "kept"));
        _target.AddFile($"{V2}/re-deleting.json", Bytes(
            "{\"formatVersion\":1,\"planId\":\"p1\",\"planName\":\"Projects\",\"startedUtc\":\"2026-10-15T08:00:00Z\",\"host\":\"OTHERPC\"}"));
        AddBlob(_target, "orphan");
        var before = _target.Files.ToList();

        (await VersionCatalog.ListForeignMarkedAsync(_target, PlanId, PlanName, "TESTHOST")).Should().ContainSingle();
        var result = await Collect();

        result.Should().Be(new CollectResult(0, 0, CoreTexts.English("core.gc.skippedMarkers")));
        _target.Files.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Empty_shard_folders_are_removed()
    {
        Version(V1, ("a.txt", "kept"));
        AddBlob(_target, "orphan");
        var orphan = BlobOf("orphan");
        var shard = StoragePath.Parent(orphan);
        var top = StoragePath.Parent(shard);
        StoragePath.Parent(BlobOf("kept")).Should().NotStartWith(top, "the test needs the orphan alone in its folders");

        await Collect();

        (await _target.StatAsync(shard, CancellationToken.None)).Should().BeNull();
        (await _target.StatAsync(top, CancellationToken.None)).Should().BeNull();
        (await _target.StatAsync(StoragePath.Parent(BlobOf("kept")), CancellationToken.None)).Should().NotBeNull();
        (await _target.StatAsync(PoolLayout.PoolFolder, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Batches_of_1000()
    {
        Version(V1, ("a.txt", "kept"));
        for (var i = 0; i < 2500; i++)
            AddBlob(_target, $"orphan-{i}");
        var counting = new FaultyStorage(_target);

        var result = await Collect(counting);

        result.BlobsDeleted.Should().Be(2500);
        counting.DeleteBatches.Where(batch => batch.All(path => path.Count(c => c == '/') == 3))
            .Select(batch => batch.Count).Should().Equal(1000, 1000, 500);
        Blobs(_target).Should().Equal(BlobOf("kept"));
    }

    [Fact]
    public async Task Files_in_the_pool_that_are_no_blobs_are_left_alone()
    {
        Version(V1, ("a.txt", "kept"));
        _target.AddFile("re-pool/ab/cd/notes.txt", Bytes("x"));
        var misplaced = $"re-pool/00/00/{HashOf("misplaced")[7..]}";
        _target.AddFile(misplaced, Bytes("misplaced"));

        var result = await Collect();

        result.Should().Be(new CollectResult(0, 0, null));
        _target.Files.Should().Contain(["re-pool/ab/cd/notes.txt", misplaced]);
    }

    [Fact]
    public async Task Target_without_pool_collects_nothing()
    {
        Version(V1);
        (await Collect()).Should().Be(new CollectResult(0, 0, null));
    }

    [Fact]
    public async Task UniqueBytes_counts_only_exclusive_content()
    {
        Version(V1, ("a.txt", "shared"), ("b.txt", "x1"), ("c.txt", "x1"));
        Version(V2, ("a.txt", "shared"), ("d.txt", "yy2"));
        Version(V3, ("a.txt", "shared"), ("d.txt", "yy2"));
        var versions = await VersionCatalog.ListAsync(_target, PlanId, PlanName);

        var unique = await PoolUsage.UniqueBytesAsync(_target, versions, CancellationToken.None);

        unique.Should().BeEquivalentTo(new Dictionary<string, long> { [V1] = 2, [V2] = 0, [V3] = 0 });
    }

    // ---- Runs, freeing space and manual deletes ----

    private const string RunFirst = "2026_10_15-10_00 Projects";
    private const string RunSecond = "2026_10_16-10_00 Projects";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 8, 0, 30, TimeSpan.Zero));
    private readonly InMemoryStorage _source = new();

    /// <summary>Total space of the target for <see cref="RunAsync"/> runs; the free space is what the files leave of it.</summary>
    private long? _capacity;

    private BackupPlan RunPlan(params RetentionRule[] rules) => new()
    {
        Id = PlanId,
        Name = PlanName,
        Incremental = true,
        Source = StorageLocation.FileSystem(Path.GetTempPath()),
        Target = StorageLocation.FileSystem(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "rebackup-gc-test-target")),
        Retention = [.. rules],
    };

    private static RetentionRule Daily(int keep) => new() { Period = RetentionPeriod.Daily, Keep = keep };

    private long Used() => _target.Files.Sum(path => (long)_target.ReadAllBytes(path).Length);

    public PoolCollectorTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
    }

    private Task<RunLogEntry> RunAsync(BackupPlan plan, Action<string, string>? before = null)
    {
        _target.FreeSpace = _capacity is null ? null : 0;   // the storage tells its free space (the wrapper's)
        var target = new FaultyStorage(_target)
        {
            Before = before,
            FreeSpace = _capacity is { } capacity ? () => capacity - Used() : null,
        };
        var storages = new TestStorageFactory(location => location == plan.Source ? _source : target);
        return new BackupRunner(storages, _time, host: "TESTHOST").RunAsync(new BackupRequest(plan, [], RunTrigger.Manual));
    }

    /// <summary>Runs with a.txt holding <paramref name="content"/> and b.txt unchanged, then moves the clock on by a day.</summary>
    private async Task RunDay(BackupPlan plan, string content)
    {
        _source.AddFile("a.txt", Bytes(content), PoolVersions.Mtime.AddDays(_time.GetUtcNow().Day));
        _source.AddFile("b.txt", Bytes("same-everywhere"), PoolVersions.Mtime);
        var entry = await RunAsync(plan);
        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        entry.Warnings.Should().BeEmpty();
        _time.Advance(TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task Retention_then_collection_frees_space_in_run()
    {
        await RunDay(RunPlan(Daily(1)), "old content");

        _source.AddFile("a.txt", Bytes("new content"), PoolVersions.Mtime.AddDays(30));
        var entry = await RunAsync(RunPlan(Daily(1)));

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        entry.Warnings.Should().BeEmpty();
        entry.RetentionDeleted.Should().Equal(RunFirst);
        Blobs(_target).Should().BeEquivalentTo(BlobOf("new content"), BlobOf("same-everywhere"));
        (await RestoreAsync(_target, RunSecond)).Should().Equal(new Dictionary<string, string>
        {
            ["a.txt"] = "new content",
            ["b.txt"] = "same-everywhere",
        });
        _target.Files.Should().NotContain(PoolLayout.LockFile);
    }

    [Fact]
    public async Task Collection_skip_is_a_run_warning()
    {
        await RunDay(RunPlan(), "old content");
        _target.AddFile("2026_10_01-10_00 Projects/re-deleting.json", Bytes(
            "{\"formatVersion\":1,\"planId\":\"p1\",\"planName\":\"Projects\",\"startedUtc\":\"2026-10-15T08:00:00Z\",\"host\":\"OTHERPC\"}"));

        var entry = await RunAsync(RunPlan());

        entry.Warnings.Should().Contain(CoreTexts.English("core.gc.skippedMarkers"));
    }

    [Fact]
    public async Task FreeSpace_deletes_until_unique_bytes_cover_shortfall()
    {
        var unique = new string('1', 1000);
        await RunDay(RunPlan(), unique);
        await RunDay(RunPlan(), new string('2', 1000));
        await RunDay(RunPlan(), new string('3', 1000));
        // Daily 2 with the coming run: the first two versions may go.
        var plan = RunPlan(Daily(2));
        plan.FreeSpaceByRetention = true;
        _source.AddFile("a.txt", Bytes(new string('4', 400)), PoolVersions.Mtime.AddDays(40));
        // About 300 bytes free (less the lock file); the run needs 420 (400 plus the margin): the first version's 1000 own bytes cover that.
        _capacity = Used() + 300;
        bool? secondThereWhileCopying = null;

        var entry = await RunAsync(plan, (operation, path) =>
        {
            if (operation == "create" && path == BlobOf(new string('4', 400)))
                secondThereWhileCopying = _target.Files.Contains($"{RunSecond}/re-manifest.json");
        });

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        secondThereWhileCopying.Should().BeTrue("one version's own bytes covered the shortfall");
        entry.RetentionDeleted.Should().Equal(RunFirst, RunSecond);
        _target.Files.Should().NotContain(BlobOf(unique));
    }

    [Fact]
    public async Task FreeSpace_deletes_nothing_when_unique_bytes_cannot_cover_shortfall()
    {
        await RunDay(RunPlan(), "a");
        await RunDay(RunPlan(), "b");
        await RunDay(RunPlan(), "c");
        var plan = RunPlan(Daily(2));
        plan.FreeSpaceByRetention = true;
        _source.AddFile("a.txt", Bytes(new string('4', 400)), PoolVersions.Mtime.AddDays(40));
        _capacity = Used() + 300;
        var before = _target.Files.ToList();

        var entry = await RunAsync(plan);

        entry.Status.Should().Be(RunStatus.Full);
        entry.RetentionDeleted.Should().BeEmpty();
        _target.Files.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Manual_delete_collects_and_releases_lock()
    {
        await RunDay(RunPlan(), "old content");
        await RunDay(RunPlan(), "new content");
        LockInfo? heldWhileDeleting = null;
        var target = new FaultyStorage(_target)
        {
            Before = (operation, path) =>
            {
                if (operation == "delete" && path == $"{RunFirst}/re-manifest.json")
                    heldWhileDeleting = TargetLock.ReadAsync(_target, CancellationToken.None).Result;
            },
        };

        var results = await VersionDeleter.DeleteAsync(target, PlanId, PlanName, [RunFirst], machineName: "TESTHOST",
            pool: true);

        results.Should().Equal(new VersionDeletion(RunFirst, VersionDeletionOutcome.Deleted, null));
        heldWhileDeleting.Should().NotBeNull();
        heldWhileDeleting!.Host.Should().Be("TESTHOST");
        heldWhileDeleting.Operation.Should().Be("delete");
        Blobs(_target).Should().BeEquivalentTo(BlobOf("new content"), BlobOf("same-everywhere"));
        _target.Files.Should().NotContain(PoolLayout.LockFile);
    }

    [Fact]
    public async Task Manual_delete_refused_while_locked()
    {
        await RunDay(RunPlan(), "old content");
        await RunDay(RunPlan(), "new content");
        await using var held = await TargetLock.TryAcquireAsync(_target, "OTHERPC", "verify", _time, CancellationToken.None);
        var before = _target.Files.ToList();

        var results = await VersionDeleter.DeleteAsync(_target, PlanId, PlanName, [RunFirst, RunSecond],
            machineName: "TESTHOST", pool: true);

        var locked = CoreTexts.English("core.run.targetLocked",
            ("host", "OTHERPC"), ("operation", "verify"), ("since", "2026-10-17 08:00 UTC"));
        results.Should().Equal(
            new VersionDeletion(RunFirst, VersionDeletionOutcome.Failed, locked),
            new VersionDeletion(RunSecond, VersionDeletionOutcome.Failed, locked));
        _target.Files.Should().BeEquivalentTo(before);
        (await TargetLock.ReadAsync(_target, CancellationToken.None))!.Token.Should().Be(held!.Info.Token);
    }
}
