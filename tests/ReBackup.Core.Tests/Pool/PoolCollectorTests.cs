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
    public async Task CanCollect_tells_why_a_collection_would_be_skipped_and_deletes_nothing()
    {
        Version(V1, ("a.txt", "kept"));
        AddBlob(_target, "orphan");
        (await PoolCollector.CanCollectAsync(_target, CancellationToken.None)).Should().BeNull();

        _target.AddFile($"{V2}/re-manifest.json", Bytes("{ not json"));
        (await PoolCollector.CanCollectAsync(_target, CancellationToken.None))
            .Should().Be(CoreTexts.English("core.gc.manifestUnreadable", ("version", V2)));

        _target.AddFile($"{V3}/re-pending.json", Bytes("{}"));
        (await PoolCollector.CanCollectAsync(_target, CancellationToken.None))
            .Should().Be(CoreTexts.English("core.gc.skippedMarkers"));
        Blobs(_target).Should().BeEquivalentTo([BlobOf("kept"), BlobOf("orphan")]);
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
    public async Task Manifest_of_a_version_named_after_an_old_plan_name_that_cannot_be_opened_aborts_collection()
    {
        const string oldName = "2026_09_01-10_00 Old projects";
        PoolVersions.Write(_target, oldName, PlanId, "Old projects", [("a.txt", "only-old")]);
        Version(V1, ("a.txt", "kept"));
        AddBlob(_target, "orphan");
        var before = _target.Files.ToList();
        var failing = new FaultyStorage(_target)
        {
            Before = (operation, path) =>
            {
                if (operation == "open" && path == $"{oldName}/re-manifest.json")
                    throw new StorageAccessDeniedException(path);
            },
        };

        var result = await Collect(failing);

        result.Should().Be(new CollectResult(0, 0, CoreTexts.English("core.gc.manifestUnreadable", ("version", oldName))));
        _target.Files.Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData("2026_09_01-10_00 Old projects", "Old projects")]   // plan renamed: older versions keep the old name
    [InlineData("2026_10_15-10_00 Projects copy", PlanName)]        // copied or renamed by hand
    [InlineData("kept by hand", PlanName)]                          // no version name at all
    public async Task Blobs_of_folders_the_catalog_does_not_list_as_owned_survive(string folder, string manifestPlanName)
    {
        PoolVersions.Write(_target, folder, PlanId, manifestPlanName, [("a.txt", "outside-catalog")]);
        Version(V1, ("a.txt", "kept"));
        AddBlob(_target, "orphan");

        var result = await Collect();

        result.Should().Be(new CollectResult(1, 6, null));
        Blobs(_target).Should().BeEquivalentTo(BlobOf("outside-catalog"), BlobOf("kept"));
        (await RestoreAsync(_target, folder)).Should().Equal(new Dictionary<string, string> { ["a.txt"] = "outside-catalog" });
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
    public async Task Collection_reports_its_phases_in_order()
    {
        Version(V1, ("a.txt", "kept"));
        Version(V2, ("b.txt", "kept too"));
        AddBlob(_target, "orphan");
        AddBlob(_target, "orphan 2");
        var poolFiles = _target.Files.Count(f => f.StartsWith(PoolLayout.PoolFolder + "/", StringComparison.Ordinal));
        var reports = new List<CollectProgress>();

        await PoolCollector.CollectAsync(_target, PlanId, PlanName, CancellationToken.None,
            new SyncProgress<CollectProgress>(reports.Add));

        reports.Select(r => r.Phase).Should().BeInAscendingOrder()
            .And.Contain([CollectPhase.ReadingManifests, CollectPhase.ListingPool, CollectPhase.DeletingUnreferenced]);
        var reading = reports.Where(r => r.Phase == CollectPhase.ReadingManifests).ToList();
        reading.Should().OnlyContain(r => r.Total == 2);
        reading.Select(r => r.Done).Should().BeInAscendingOrder().And.StartWith(0).And.EndWith(2);
        var listing = reports.Where(r => r.Phase == CollectPhase.ListingPool).ToList();
        listing.Should().OnlyContain(r => r.Total == 0, "the count is not known while listing");
        listing.Select(r => r.Done).Should().BeInAscendingOrder().And.StartWith(0).And.EndWith(poolFiles);
        var deleting = reports.Where(r => r.Phase == CollectPhase.DeletingUnreferenced).ToList();
        deleting.Should().OnlyContain(r => r.Total == 2);
        deleting.Select(r => r.Done).Should().BeInAscendingOrder().And.StartWith(0).And.EndWith(2);
        reports.Should().OnlyContain(r => r.Fraction >= 0 && r.Fraction <= 1);
    }

    [Fact]
    public async Task Listing_error_aborts_collection_with_nothing_deleted()
    {
        Version(V1, ("a.txt", "kept"));
        for (var i = 0; i < 40; i++)
            AddBlob(_target, $"orphan-{i}");
        var shard = StoragePath.Parent(StoragePath.Parent(BlobOf("orphan-7")));
        var before = _target.Files.ToList();
        var faulty = new FaultyStorage(_target)
        {
            Before = (operation, path) =>
            {
                if (operation == "list" && path == shard)
                    throw new StorageUnavailableException(path);
            },
        };

        var collect = () => Collect(faulty);

        await collect.Should().ThrowAsync<StorageUnavailableException>();
        faulty.DeleteBatches.Should().BeEmpty();
        _target.Files.Should().BeEquivalentTo(before);
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
    public async Task Run_reports_the_cleanup_phases_after_retention()
    {
        await RunDay(RunPlan(Daily(1)), "old content");
        _source.AddFile("a.txt", Bytes("new content"), PoolVersions.Mtime.AddDays(30));
        var reports = new List<BackupProgress>();
        _target.FreeSpace = null;
        var storages = new TestStorageFactory(location => location == RunPlan().Source ? _source : _target);

        var entry = await new BackupRunner(storages, _time, host: "TESTHOST").RunAsync(
            new BackupRequest(RunPlan(Daily(1)), [], RunTrigger.Manual), new SyncProgress<BackupProgress>(reports.Add));

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        var cleanup = reports.SkipWhile(r => r.Phase != BackupPhase.CollectingGarbage).ToList();
        cleanup.Should().NotBeEmpty().And.OnlyContain(r => r.Phase == BackupPhase.CollectingGarbage,
            "the collection is the last thing a run does");
        reports.TakeWhile(r => r.Phase != BackupPhase.CollectingGarbage).Should().Contain(r => r.Phase == BackupPhase.Retention);
        cleanup.Select(r => r.Cleanup.Phase).Should().BeInAscendingOrder()
            .And.Contain([CollectPhase.ReadingManifests, CollectPhase.ListingPool, CollectPhase.DeletingUnreferenced]);
        cleanup.Last().Cleanup.Should().Be(new CollectProgress(CollectPhase.DeletingUnreferenced, 1, 1));
        cleanup.Should().OnlyContain(r => r.Fraction >= 0 && r.Fraction <= 1);
    }

    [Fact]
    public async Task Manual_pool_delete_counts_the_files_in_the_folder_and_reports_the_cleanup()
    {
        await RunDay(RunPlan(), "old content");
        await RunDay(RunPlan(), "new content");
        var inFolder = _target.Files.Count(f => f.StartsWith(RunFirst + "/", StringComparison.Ordinal));
        inFolder.Should().BeGreaterThan(0);
        var reports = new List<VersionDeletionProgress>();

        await VersionDeleter.DeleteAsync(_target, PlanId, PlanName, [RunFirst],
            new SyncProgress<VersionDeletionProgress>(reports.Add), machineName: "TESTHOST", pool: true);

        var deleting = reports.Where(r => r.Phase == VersionDeletionPhase.Deleting).ToList();
        deleting.Should().OnlyContain(r => r.FilesTotal == inFolder, "a pool version holds only its manifest and markers");
        deleting.Last().FilesDone.Should().Be(inFolder);
        deleting.Last().Fraction.Should().Be(1);
        var cleanup = reports.SkipWhile(r => r.Phase != VersionDeletionPhase.CleaningUp).ToList();
        cleanup.Should().NotBeEmpty().And.OnlyContain(r => r.Phase == VersionDeletionPhase.CleaningUp);
        cleanup.Select(r => r.Cleanup.Phase).Should().BeInAscendingOrder()
            .And.Contain([CollectPhase.ReadingManifests, CollectPhase.ListingPool, CollectPhase.DeletingUnreferenced]);
        cleanup.Last().Cleanup.Should().Be(new CollectProgress(CollectPhase.DeletingUnreferenced, 1, 1));
        cleanup.Should().OnlyContain(r => r.Fraction >= 0 && r.Fraction <= 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_delete_canceled_before_it_starts_changes_nothing(bool pool)
    {
        await RunDay(RunPlan(), "old content");
        await RunDay(RunPlan(), "new content");
        var before = _target.Files.ToList();

        var results = await VersionDeleter.DeleteAsync(_target, PlanId, PlanName, [RunFirst], ct: new CancellationToken(true),
            machineName: "TESTHOST", pool: pool);

        results.Should().BeEmpty("versions that were not started are left out, with and without the lock");
        _target.Files.Should().BeEquivalentTo(before);
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
    // ---- Resuming deletions at app start ----

    private static byte[] DeletingMarker(string host) => Bytes(
        "{\"formatVersion\":1,\"planId\":\"p1\",\"planName\":\"Projects\",\"startedUtc\":\"2026-10-15T08:00:00Z\",\"host\":\"" + host + "\"}");

    private static byte[] CleanupFile(string host) => Bytes("{\"host\":\"" + host + "\",\"startedUtc\":\"2026-10-15T08:00:00Z\"}");

    private Task<DeletionResume> Resume(IStorage? target = null, IProgress<VersionDeletionProgress>? progress = null) =>
        DeletionResumer.ResumeAsync(target ?? _target, PlanId, PlanName, pool: true, progress, CancellationToken.None, "TESTHOST");

    [Fact]
    public async Task Manual_pool_delete_marks_the_cleanup_and_removes_the_mark_once_collected()
    {
        await RunDay(RunPlan(), "old content");
        await RunDay(RunPlan(), "new content");
        bool? markedWhileDeleting = null;
        var target = new FaultyStorage(_target)
        {
            Before = (operation, path) =>
            {
                if (operation == "delete" && path == $"{RunFirst}/re-manifest.json")
                    markedWhileDeleting = _target.Files.Contains(CleanupMarker.FileName);
            },
        };

        await VersionDeleter.DeleteAsync(target, PlanId, PlanName, [RunFirst], machineName: "TESTHOST", pool: true);

        markedWhileDeleting.Should().BeTrue();
        _target.Files.Should().NotContain(CleanupMarker.FileName);
    }

    [Fact]
    public async Task Interrupted_delete_is_resumed_and_completed_at_the_next_start()
    {
        await RunDay(RunPlan(), "old content");
        await RunDay(RunPlan(), "new content");
        using var cts = new CancellationTokenSource();
        var interrupted = new FaultyStorage(_target)
        {
            // The target drops right after the first file of the version went: the marker and the cleanup mark stay.
            FailDelete = path => path == $"{RunFirst}/re-manifest.json" && cts.IsCancellationRequested,
            Before = (operation, path) =>
            {
                if (operation == "create" && path == $"{RunFirst}/re-deleting.json")
                    cts.Cancel();
            },
        };
        await VersionDeleter.DeleteAsync(interrupted, PlanId, PlanName, [RunFirst], ct: cts.Token, machineName: "TESTHOST",
            pool: true);
        _target.Files.Should().Contain([$"{RunFirst}/re-deleting.json", CleanupMarker.FileName]);
        Blobs(_target).Should().Contain(BlobOf("old content"));
        var reports = new List<VersionDeletionProgress>();

        var resumed = await Resume(progress: new SyncProgress<VersionDeletionProgress>(reports.Add));

        resumed.Should().BeEquivalentTo(new DeletionResume(ResumeOutcome.Finished, [RunFirst]));
        _target.Files.Should().NotContain(f => f.StartsWith(RunFirst + "/", StringComparison.Ordinal));
        _target.Files.Should().NotContain([CleanupMarker.FileName, PoolLayout.LockFile]);
        Blobs(_target).Should().BeEquivalentTo(BlobOf("new content"), BlobOf("same-everywhere"));
        reports.Select(r => r.Phase).Should().BeInAscendingOrder()
            .And.Contain([VersionDeletionPhase.Deleting, VersionDeletionPhase.CleaningUp]);
        (await RestoreAsync(_target, RunSecond)).Should().ContainKey("a.txt");
        (await Resume()).Outcome.Should().Be(ResumeOutcome.NothingToDo, "nothing is left");
    }

    [Fact]
    public async Task Pending_cleanup_is_collected_at_start_and_its_mark_removed()
    {
        await RunDay(RunPlan(), "kept");
        AddBlob(_target, "orphan");
        _target.AddFile(CleanupMarker.FileName, CleanupFile("testhost"));

        var resumed = await Resume();

        resumed.Outcome.Should().Be(ResumeOutcome.Finished);
        _target.Files.Should().NotContain([BlobOf("orphan"), CleanupMarker.FileName, PoolLayout.LockFile]);
    }

    [Fact]
    public async Task Markers_of_another_computer_are_left_alone_at_start()
    {
        await RunDay(RunPlan(), "old content");
        await RunDay(RunPlan(), "new content");
        _target.AddFile($"{RunFirst}/re-deleting.json", DeletingMarker("OTHERPC"));
        _target.AddFile(CleanupMarker.FileName, CleanupFile("OTHERPC"));
        AddBlob(_target, "orphan");
        var before = _target.Files.ToList();

        var resumed = await Resume();

        resumed.Outcome.Should().Be(ResumeOutcome.NothingToDo);
        _target.Files.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Unreachable_target_at_start_changes_nothing_and_is_tried_again_next_start()
    {
        await RunDay(RunPlan(), "old content");
        _target.AddFile(CleanupMarker.FileName, CleanupFile("TESTHOST"));
        AddBlob(_target, "orphan");
        var before = _target.Files.ToList();
        var offline = new FaultyStorage(_target)
        {
            Before = (_, path) => throw new StorageUnavailableException(path),
        };

        (await Resume(offline)).Outcome.Should().Be(ResumeOutcome.NotNow);
        _target.Files.Should().BeEquivalentTo(before);

        (await Resume()).Outcome.Should().Be(ResumeOutcome.Finished, "the next start finds the target");
        _target.Files.Should().NotContain([BlobOf("orphan"), CleanupMarker.FileName]);
    }

    [Fact]
    public async Task Resume_waits_for_another_holder_of_the_lock()
    {
        await RunDay(RunPlan(), "old content");
        _target.AddFile(CleanupMarker.FileName, CleanupFile("TESTHOST"));
        await using var held = await TargetLock.TryAcquireAsync(_target, "OTHERPC", "backup", _time, CancellationToken.None);

        (await Resume()).Outcome.Should().Be(ResumeOutcome.NotNow);
        _target.Files.Should().Contain(CleanupMarker.FileName);
    }

    [Fact]
    public async Task Run_collection_removes_a_pending_cleanup_mark_of_this_computer()
    {
        await RunDay(RunPlan(), "old content");
        _target.AddFile(CleanupMarker.FileName, CleanupFile("TESTHOST"));

        await RunDay(RunPlan(), "new content");

        _target.Files.Should().NotContain(CleanupMarker.FileName);
    }
}
