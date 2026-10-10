using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Backup;

/// <summary>
/// Markers written by another computer (spec 5): a folder another PC is writing into or deleting from the same target is
/// never cleaned, deleted or counted; it is listed, and can be taken over by hand.
/// </summary>
public class ForeignMarkerTests : IDisposable
{
    private const string PlanId = "p1";
    private const string PlanName = "Projects";
    private const string ThisPc = "THIS-PC";
    private const string OtherPc = "OTHER-PC";
    private const string NewVersion = "2026_09_30-16_05 Projects";   // 14:05 UTC in the fixed +02:00 test zone
    private const string Marked = "2026_09_20-02_00 Projects";
    private static readonly DateTime Started = new(2026, 9, 30, 13, 0, 0, DateTimeKind.Utc);
    private readonly TempDir _tmp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.Zero));
    private readonly InMemoryStorage _storage = new();
    private readonly string _source;
    private readonly string _target;

    public ForeignMarkerTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
        _source = _tmp.CreateDir("source");
        _target = _tmp.CreateDir("target");
        _tmp.WriteFile(@"source\a.txt", "alpha");
        _tmp.WriteFile(@"source\sub\b.bin", "bravo-bravo");
    }

    public void Dispose() => _tmp.Dispose();

    private static MarkerInfo Marker(string? host, string planId = PlanId) => new(1, planId, PlanName, Started, host!);

    private static string OldName(int day) => $"2026_09_{day:00}-02_00 {PlanName}";

    private string Old(int day) => VersionFolder.Create(_target, OldName(day), PlanId);

    private string[] FilesUnder(string folder) =>
        Directory.GetFiles(Path.Combine(_target, folder), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_target, f)).Order(StringComparer.Ordinal).ToArray();

    /// <summary>A version folder (manifest and all) that <paramref name="host"/> marked as being written or deleted.</summary>
    private async Task MarkOnDisk(string name, string host, MarkerKind kind, string planId = PlanId)
    {
        VersionFolder.Create(_target, name, planId);
        var storage = new FileSystemStorage(_target);
        if (kind == MarkerKind.Pending)
            await VersionMarkers.WritePendingAsync(storage, name, Marker(host, planId), CancellationToken.None);
        else
            await VersionMarkers.WriteDeletingAsync(storage, name, Marker(host, planId), CancellationToken.None);
    }

    private async Task MarkInMemory(string name, string? host, MarkerKind kind, string planId = PlanId)
    {
        _storage.AddFile($"{name}/sub/f.txt", [1]);
        if (kind == MarkerKind.Pending)
            await VersionMarkers.WritePendingAsync(_storage, name, Marker(host, planId), CancellationToken.None);
        else
            await VersionMarkers.WriteDeletingAsync(_storage, name, Marker(host, planId), CancellationToken.None);
    }

    private IEnumerable<string> InMemoryFilesIn(string folder) =>
        _storage.Files.Where(f => f.StartsWith(folder + "/", StringComparison.Ordinal));

    private BackupPlan FreeingPlan() => new()
    {
        Id = PlanId,
        Name = PlanName,
        Source = StorageLocation.FileSystem(_source),
        Target = StorageLocation.FileSystem(_target),
        Retention = [new RetentionRule { Period = RetentionPeriod.Daily, Keep = 1 }],
        FreeSpaceByRetention = true,
    };

    /// <summary>
    /// A run of the plan whose target reports 2 free bytes plus 10 for every old version that is gone: the run frees
    /// space by retention before it copies, then retention runs after it.
    /// </summary>
    private Task<RunLogEntry> RunShortOfSpace(BackupPlan plan, params int[] days) => RunShortOfSpace(plan, null, days);

    /// <param name="before">Called before every operation on the target, with the operation and the path.</param>
    private Task<RunLogEntry> RunShortOfSpace(BackupPlan plan, Action<string, string>? before, params int[] days)
    {
        var storages = new TestStorageFactory(location =>
        {
            var storage = new StorageFactory().Open(location);
            return location != plan.Target
                ? storage
                : new FaultyStorage(storage)
                {
                    Before = before,
                    // Without days, the target tells its real free space.
                    FreeSpace = days.Length == 0
                        ? null
                        : () => 2 + 10 * days.Count(day => !Directory.Exists(Path.Combine(_target, OldName(day)))),
                };
        });
        return new BackupRunner(storages, _time).RunAsync(new BackupRequest(plan, [], RunTrigger.Manual));
    }

    [Theory]
    [InlineData(MarkerKind.Pending, "is being written by OTHER-PC since 2026-09-30 13:00 UTC")]
    [InlineData(MarkerKind.Deleting, "is being deleted by OTHER-PC since 2026-09-30 13:00 UTC")]
    public async Task Foreign_pending_folder_is_never_touched(MarkerKind kind, string warning)
    {
        // Another computer runs (or deletes a version of) the same plan into the same target. The folder is oldest of
        // all, holds a manifest of the plan and is named like it: the leftover cleanup, the deletion that frees space
        // and retention would all take it, were it not for the marker of the other host.
        for (var day = 25; day <= 29; day++)
            Old(day);
        await MarkOnDisk(Marked, OtherPc, kind);
        var before = FilesUnder(Marked);
        string[]? whenCopyStarted = null;   // the folders left once space was freed, before the new version is written

        var entry = await RunShortOfSpace(FreeingPlan(), (operation, path) =>
        {
            if (operation == "create" && path == $"{NewVersion}/re-pending.json")
                whenCopyStarted = Directory.GetDirectories(_target).Select(d => Path.GetFileName(d)!).ToArray();
        }, 25, 26, 27, 28);

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings, "a run that meets another computer's folder never ends as error");
        entry.Version.Should().Be(NewVersion);
        FilesUnder(Marked).Should().Equal(before);
        entry.RetentionDeleted.Should().NotContain(Marked);
        whenCopyStarted.Should().BeEquivalentTo([Marked, OldName(27), OldName(28), OldName(29)],
            "freeing space deleted the two oldest versions it needed, and passed over the oldest folder of all");
        entry.Warnings.Should().ContainSingle().Which.Should().Be($"\"{Marked}\" was left alone: it {warning}.");
        (await VersionCatalog.ListAsync(new FileSystemStorage(_target), PlanId, PlanName)).Select(v => v.Name)
            .Should().Equal([NewVersion], "a folder in transit is no version, so retention never counts it");
    }

    [Fact]
    public async Task A_run_without_foreign_markers_still_completes_without_warnings()
    {
        Old(27);
        Old(28);
        Old(29);

        var entry = await RunShortOfSpace(FreeingPlan(), 27, 28);

        entry.Status.Should().Be(RunStatus.Completed);
        entry.Warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData(MarkerKind.Pending)]
    [InlineData(MarkerKind.Deleting)]
    public async Task The_cleanup_leaves_a_foreign_marker_and_reports_it(MarkerKind kind)
    {
        await MarkInMemory(Marked, OtherPc, kind);
        var before = _storage.Files.ToList();

        var result = await LeftoverCleaner.CleanAsync(_storage, PlanId, PlanName, null, CancellationToken.None, machineName: ThisPc);

        _storage.Files.Should().BeEquivalentTo(before);
        result.ForeignMarked.Should().Equal(new ForeignMarkedFolder(Marked, OtherPc, Started, kind));
        result.Warnings.Should().ContainSingle();
    }

    [Theory]
    [InlineData(MarkerKind.Pending)]
    [InlineData(MarkerKind.Deleting)]
    public async Task The_cleanup_reports_only_the_foreign_markers_that_concern_the_plan(MarkerKind kind)
    {
        // Another plan writes into the same target from another computer: its folder is left alone with a warning, but
        // it is not this plan's business, so it is not listed (and does not raise the run status).
        await MarkInMemory("2026_09_24-02_00 Other", OtherPc, kind, planId: "p2");
        // A folder named after this plan, whatever plan id its marker names, concerns the plan.
        await MarkInMemory("2026_09_25-02_00 Projects", OtherPc, kind, planId: "p2");
        // A folder renamed to another plan's name whose marker names this plan concerns it too.
        await MarkInMemory("2026_09_26-02_00 Other", OtherPc, kind);
        var before = _storage.Files.ToList();

        var result = await LeftoverCleaner.CleanAsync(_storage, PlanId, PlanName, null, CancellationToken.None, machineName: ThisPc);

        _storage.Files.Should().BeEquivalentTo(before);
        result.ForeignMarked.Should().Equal(
            new ForeignMarkedFolder("2026_09_25-02_00 Projects", OtherPc, Started, kind),
            new ForeignMarkedFolder("2026_09_26-02_00 Other", OtherPc, Started, kind));
        result.Warnings.Should().HaveCount(3, "every folder another computer marked is still named in the run log");
    }

    [Fact]
    public async Task A_foreign_marker_of_another_plan_does_not_raise_the_run_status()
    {
        Old(27);
        Old(28);
        await MarkOnDisk("2026_09_20-02_00 Other", OtherPc, MarkerKind.Pending, planId: "p2");

        var entry = await RunShortOfSpace(FreeingPlan());

        entry.Status.Should().Be(RunStatus.Completed, "another plan's folder in the same target is not this plan's concern");
        entry.Version.Should().Be(NewVersion);
        Directory.Exists(Path.Combine(_target, "2026_09_20-02_00 Other")).Should().BeTrue();
    }

    [Theory]
    [InlineData(MarkerKind.Pending, null)]
    [InlineData(MarkerKind.Pending, "")]
    [InlineData(MarkerKind.Deleting, null)]
    [InlineData(MarkerKind.Deleting, "")]
    public async Task A_marker_without_host_counts_as_own(MarkerKind kind, string? host)
    {
        // ReBackup 1.0.x and 1.1 wrote markers without a host.
        await MarkInMemory(Marked, host, kind);

        var result = await LeftoverCleaner.CleanAsync(_storage, PlanId, PlanName, null, CancellationToken.None, machineName: ThisPc);

        result.Warnings.Should().BeEmpty();
        result.ForeignMarked.Should().BeEmpty();
        InMemoryFilesIn(Marked).Should().BeEmpty();
    }

    [Theory]
    [InlineData(MarkerKind.Pending)]
    [InlineData(MarkerKind.Deleting)]
    public async Task An_own_marker_is_cleaned_as_before_whatever_the_case_of_the_host(MarkerKind kind)
    {
        await MarkInMemory(Marked, "this-pc", kind);

        var result = await LeftoverCleaner.CleanAsync(_storage, PlanId, PlanName, null, CancellationToken.None, machineName: ThisPc);

        result.Warnings.Should().BeEmpty();
        InMemoryFilesIn(Marked).Should().BeEmpty();
    }

    [Theory]
    [InlineData("THIS-PC", true)]
    [InlineData("this-pc", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("OTHER-PC", false)]
    public void A_marker_is_own_by_its_host(string? host, bool own)
    {
        Marker(host).IsOwn(ThisPc).Should().Be(own);
    }

    [Fact]
    public async Task The_catalog_lists_the_foreign_marked_folders_of_the_plan()
    {
        await MarkInMemory(Marked, OtherPc, MarkerKind.Pending);
        await MarkInMemory("2026_09_21-02_00 Projects", OtherPc, MarkerKind.Deleting);
        await MarkInMemory("2026_09_22-02_00 Projects", ThisPc, MarkerKind.Pending);   // own: the cleanup's
        await MarkInMemory("2026_09_23-02_00 Projects", null, MarkerKind.Pending);      // no host: own
        await MarkInMemory("2026_09_24-02_00 Other", OtherPc, MarkerKind.Pending, planId: "p2");   // another plan's
        await VersionFolder.CreateAsync(_storage, "2026_09_25-02_00 Projects", PlanId);

        var foreign = await VersionCatalog.ListForeignMarkedAsync(_storage, PlanId, PlanName, ThisPc);

        foreign.Should().Equal(
            new ForeignMarkedFolder(Marked, OtherPc, Started, MarkerKind.Pending),
            new ForeignMarkedFolder("2026_09_21-02_00 Projects", OtherPc, Started, MarkerKind.Deleting));
    }

    [Theory]
    [InlineData(MarkerKind.Pending)]
    [InlineData(MarkerKind.Deleting)]
    public async Task Take_over_cleans_the_folder(MarkerKind kind)
    {
        await MarkInMemory(Marked, OtherPc, kind);
        await VersionFolder.CreateAsync(_storage, "2026_09_25-02_00 Projects", PlanId);

        var warnings = await LeftoverCleaner.TakeOverAsync(_storage, PlanId, PlanName,
            new ForeignMarkedFolder(Marked, OtherPc, Started, kind), CancellationToken.None);

        warnings.Should().BeEmpty();
        InMemoryFilesIn(Marked).Should().BeEmpty();
        (await _storage.StatAsync(Marked, CancellationToken.None)).Should().BeNull();
        InMemoryFilesIn("2026_09_25-02_00 Projects").Should().HaveCount(2, "a version stays");
    }

    [Fact]
    public async Task Take_over_keeps_the_rules_for_other_plans()
    {
        await MarkInMemory(Marked, OtherPc, MarkerKind.Pending, planId: "p2");
        var before = _storage.Files.ToList();

        var warnings = await LeftoverCleaner.TakeOverAsync(_storage, PlanId, PlanName,
            new ForeignMarkedFolder(Marked, OtherPc, Started, MarkerKind.Pending), CancellationToken.None);

        warnings.Should().Equal($"\"{Marked}\" was left alone: it is marked by another plan.");
        _storage.Files.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Take_over_leaves_a_folder_whose_marker_changed_meanwhile()
    {
        // The listing showed an older run of that computer; it has started again since.
        await MarkInMemory(Marked, OtherPc, MarkerKind.Pending);
        var before = _storage.Files.ToList();

        var warnings = await LeftoverCleaner.TakeOverAsync(_storage, PlanId, PlanName,
            new ForeignMarkedFolder(Marked, OtherPc, Started.AddHours(-1), MarkerKind.Pending), CancellationToken.None);

        warnings.Should().Equal($"\"{Marked}\" was not taken over: its marker has changed meanwhile.");
        _storage.Files.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task The_deleter_leaves_a_version_another_computer_is_deleting()
    {
        await MarkOnDisk(Marked, OtherPc, MarkerKind.Deleting);
        var before = FilesUnder(Marked);

        var results = await VersionDeleter.DeleteAsync(new FileSystemStorage(_target), PlanId, PlanName, [Marked],
            machineName: ThisPc);

        results.Should().Equal(new VersionDeletion(Marked, VersionDeletionOutcome.NotManaged, null));
        FilesUnder(Marked).Should().Equal(before);
    }

    [Fact]
    public async Task Retention_leaves_a_version_another_computer_starts_deleting_after_the_listing()
    {
        // The other computer marks the version between retention's listing and its deletion.
        Old(27);
        Old(28);
        var marked = false;
        var plan = FreeingPlan();
        plan.FreeSpaceByRetention = false;

        var entry = await RunShortOfSpace(plan, (operation, path) =>
        {
            if (operation != "stat" || path != OldName(27) || marked)
                return;
            marked = true;
            VersionMarkers.WriteDeletingAsync(new FileSystemStorage(_target), OldName(27), Marker(OtherPc), CancellationToken.None)
                .GetAwaiter().GetResult();
        });

        marked.Should().BeTrue();
        entry.RetentionDeleted.Should().Equal(OldName(28));
        entry.Warnings.Should().ContainSingle().Which.Should().Contain(OldName(27));
        File.Exists(Path.Combine(_target, OldName(27), "re-manifest.json")).Should().BeTrue();
        (await VersionMarkers.TryReadAsync(new FileSystemStorage(_target), $"{OldName(27)}/re-deleting.json", CancellationToken.None))!
            .Host.Should().Be(OtherPc, "the other computer's marker is not overwritten");
    }

    [Theory]
    [InlineData("re-pending.json", "OTHER-PC")]
    [InlineData("re-pending.json", "THIS-PC")]
    [InlineData("re-deleting.json", "OTHER-PC")]
    public async Task The_remover_never_overwrites_a_marker_it_did_not_expect(string markerName, string host)
    {
        var folder = await VersionFolder.CreateAsync(_storage, Marked, PlanId);
        var marker = Marker(host);
        if (markerName == "re-pending.json")
            await VersionMarkers.WritePendingAsync(_storage, folder, marker, CancellationToken.None);
        else
            await VersionMarkers.WriteDeletingAsync(_storage, folder, marker, CancellationToken.None);
        var before = _storage.Files.ToList();

        var removal = () => VersionRemover.RemoveAsync(_storage, folder, Marker(ThisPc));

        await removal.Should().ThrowAsync<StorageConflictException>();
        _storage.Files.Should().BeEquivalentTo(before);
        (await VersionMarkers.TryReadAsync(_storage, $"{folder}/{markerName}", CancellationToken.None))!.Host.Should().Be(host);
    }

    [Fact]
    public async Task The_remover_retries_its_own_interrupted_deletion()
    {
        var folder = await VersionFolder.CreateAsync(_storage, Marked, PlanId);
        await VersionMarkers.WriteDeletingAsync(_storage, folder, Marker("this-pc"), CancellationToken.None);

        await VersionRemover.RemoveAsync(_storage, folder, Marker(ThisPc));

        InMemoryFilesIn(Marked).Should().BeEmpty();
    }

    [Fact]
    public async Task A_version_whose_markers_cannot_be_looked_at_is_not_listed()
    {
        await VersionFolder.CreateAsync(_storage, Marked, PlanId);
        await VersionFolder.CreateAsync(_storage, "2026_09_21-02_00 Projects", PlanId);
        var storage = new FaultyStorage(_storage)
        {
            Before = (operation, path) =>
            {
                if (operation == "stat" && path == $"{Marked}/re-pending.json")
                    throw new StorageIOException(path, "The network name is no longer available.");
            },
        };

        var versions = await VersionCatalog.ListAsync(storage, PlanId, PlanName);

        versions.Select(v => v.Name).Should().Equal("2026_09_21-02_00 Projects");
    }

    [Fact]
    public async Task Take_over_reports_a_folder_that_cannot_be_examined()
    {
        await MarkInMemory(Marked, OtherPc, MarkerKind.Pending);
        var before = _storage.Files.ToList();
        var storage = new FaultyStorage(_storage)
        {
            Before = (operation, path) =>
            {
                if (operation == "stat" && path == $"{Marked}/re-deleting.json")
                    throw new StorageIOException(path, "boom");
            },
        };

        var warnings = await LeftoverCleaner.TakeOverAsync(storage, PlanId, PlanName,
            new ForeignMarkedFolder(Marked, OtherPc, Started, MarkerKind.Pending), CancellationToken.None);

        warnings.Should().Equal($"\"{Marked}\" could not be examined and was left as it is.");
        _storage.Files.Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData(null)]          // the marker vanished: the other computer finished
    [InlineData(ThisPc)]        // the marker became this computer's own
    [InlineData("")]            // ... or one without a host
    public async Task Take_over_is_refused_when_the_marker_is_no_longer_the_listed_one(string? nowHost)
    {
        await MarkInMemory(Marked, OtherPc, MarkerKind.Pending);
        await _storage.DeleteAsync([$"{Marked}/re-pending.json"], CancellationToken.None);
        if (nowHost is not null)
            await VersionMarkers.WritePendingAsync(_storage, Marked, Marker(nowHost), CancellationToken.None);
        var before = _storage.Files.ToList();

        var warnings = await LeftoverCleaner.TakeOverAsync(_storage, PlanId, PlanName,
            new ForeignMarkedFolder(Marked, OtherPc, Started, MarkerKind.Pending), CancellationToken.None);

        warnings.Should().Equal($"\"{Marked}\" was not taken over: its marker has changed meanwhile.");
        _storage.Files.Should().BeEquivalentTo(before);
    }
}
