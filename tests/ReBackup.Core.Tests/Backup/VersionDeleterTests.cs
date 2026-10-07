using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;

namespace ReBackup.Core.Tests.Backup;

public class VersionDeleterTests : IDisposable
{
    private const string PlanId = "p1";
    private const string PlanName = "Projects";
    private const string Old = "2026_09_01-02_00 Projects";
    private const string New = "2026_09_02-02_00 Projects";
    private readonly TempDir _tmp = new();
    private readonly string _target;

    public VersionDeleterTests()
    {
        _target = _tmp.CreateDir("target");
        VersionFolder.Create(_target, Old, PlanId);
        VersionFolder.Create(_target, New, PlanId);
    }

    public void Dispose() => _tmp.Dispose();

    private IStorage Target => new FileSystemStorage(_target);

    private Task<IReadOnlyList<VersionDeletion>> Delete(IReadOnlyList<string> names, IStorage? storage = null,
        IProgress<VersionDeletionProgress>? progress = null, CancellationToken ct = default) =>
        VersionDeleter.DeleteAsync(storage ?? Target, PlanId, PlanName, names, progress, ct);

    [Fact]
    public async Task Deletes_the_named_versions_and_nothing_else()
    {
        var results = await Delete([Old]);

        results.Should().Equal(new VersionDeletion(Old, VersionDeletionOutcome.Deleted, null));
        Directory.GetDirectories(_target).Select(Path.GetFileName).Should().Equal(New);
    }

    [Fact]
    public async Task Deleter_reports_deleted_gone_notManaged_failed()
    {
        const string gone = "2026_09_03-02_00 Projects";
        const string foreign = "2026_09_04-02_00 Projects";
        VersionFolder.Create(_target, foreign, "another-plan");
        var storage = new FaultyStorage(Target) { FailCreate = path => path == $"{New}/re-deleting.json" };

        var results = await Delete([Old, gone, foreign, New], storage);

        results.Select(r => (r.Name, r.Outcome)).Should().Equal(
            (Old, VersionDeletionOutcome.Deleted),
            (gone, VersionDeletionOutcome.Gone),
            (foreign, VersionDeletionOutcome.NotManaged),
            (New, VersionDeletionOutcome.Failed));
        results[3].Error.Should().NotBeNullOrEmpty();
        Directory.GetDirectories(_target).Select(Path.GetFileName).Should().BeEquivalentTo(foreign, New);
        File.Exists(Path.Combine(_target, New, "re-manifest.json")).Should().BeTrue("a failed deletion changes nothing");
    }

    [Fact]
    public async Task Leaves_folders_that_are_not_versions_of_the_plan()
    {
        const string foreign = "2026_09_03-02_00 Projects";
        VersionFolder.Create(_target, foreign, "another-plan");
        const string noManifest = "2026_09_04-02_00 Projects";
        _tmp.CreateDir($@"target\{noManifest}");

        var results = await Delete([foreign, noManifest]);

        results.Select(r => r.Outcome).Should().Equal(VersionDeletionOutcome.NotManaged, VersionDeletionOutcome.NotManaged);
        Directory.Exists(Path.Combine(_target, foreign)).Should().BeTrue();
        Directory.Exists(Path.Combine(_target, noManifest)).Should().BeTrue();
    }

    [Fact]
    public async Task Refuses_names_that_lead_out_of_the_target()
    {
        var outside = VersionFolder.Create(_tmp.CreateDir("elsewhere"), Old, PlanId);

        var results = await Delete([$@"..\elsewhere\{Old}", $"../elsewhere/{Old}", ".partial", "", Old + ".deleting"]);

        results.Select(r => r.Outcome).Should().AllBeEquivalentTo(VersionDeletionOutcome.NotManaged);
        Directory.Exists(outside).Should().BeTrue();
    }

    [Fact]
    public async Task A_link_named_like_a_version_is_not_managed()
    {
        var real = VersionFolder.Create(_tmp.CreateDir("elsewhere"), "2026_09_05-02_00 Projects", PlanId);
        var link = Path.Combine(_target, "2026_09_05-02_00 Projects");
        Junction.Create(link, real);

        try
        {
            (await Delete(["2026_09_05-02_00 Projects"])).Single().Outcome.Should().Be(VersionDeletionOutcome.NotManaged);
            File.Exists(Path.Combine(real, "data.bin")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task A_version_that_is_still_being_written_is_not_managed()
    {
        File.WriteAllText(Path.Combine(_target, Old, "re-pending.json"), "{}");

        (await Delete([Old])).Single().Outcome.Should().Be(VersionDeletionOutcome.NotManaged);
        File.Exists(Path.Combine(_target, Old, "data.bin")).Should().BeTrue();
    }

    [Fact]
    public async Task A_version_that_is_already_gone_is_reported_as_gone()
    {
        Directory.Delete(Path.Combine(_target, Old), recursive: true);

        (await Delete([Old])).Single().Outcome.Should().Be(VersionDeletionOutcome.Gone);
    }

    [Fact]
    public async Task A_version_that_vanishes_while_it_is_deleted_is_reported_as_gone()
    {
        var storage = new FaultyStorage(Target)
        {
            FailCreate = path =>
            {
                if (path != $"{Old}/re-deleting.json")
                    return false;
                Directory.Delete(Path.Combine(_target, Old), recursive: true);   // as if someone else removed it meanwhile
                return true;
            },
        };

        (await Delete([Old], storage)).Single().Outcome.Should().Be(VersionDeletionOutcome.Gone);
    }

    [Fact]
    public async Task A_failing_marker_write_is_reported_and_the_others_are_still_deleted()
    {
        var storage = new FaultyStorage(Target) { FailCreate = path => path == $"{Old}/re-deleting.json" };

        var results = await Delete([Old, New], storage);

        results[0].Outcome.Should().Be(VersionDeletionOutcome.Failed);
        results[0].Error.Should().NotBeNullOrEmpty();
        results[1].Outcome.Should().Be(VersionDeletionOutcome.Deleted);
        File.Exists(Path.Combine(_target, Old, "data.bin")).Should().BeTrue();
        File.Exists(Path.Combine(_target, Old, "re-manifest.json")).Should().BeTrue();
    }

    [Fact]
    public async Task Remains_that_cannot_be_deleted_are_reported_and_are_no_longer_a_version()
    {
        var storage = new FaultyStorage(Target) { FailDelete = path => path == $"{Old}/re-deleting.json" };

        var result = (await Delete([Old], storage)).Single();

        result.Outcome.Should().Be(VersionDeletionOutcome.RemainsLeft);
        result.Error.Should().NotBeNullOrEmpty();
        (await VersionCatalog.ListAsync(Target, PlanId, PlanName)).Select(v => v.Name).Should().Equal(New);
        var marker = await VersionMarkers.TryReadAsync(Target, $"{Old}/re-deleting.json", CancellationToken.None);
        marker.Should().NotBeNull("the next run of the plan finishes the deletion by it");
        marker!.PlanId.Should().Be(PlanId);
        marker.PlanName.Should().Be(PlanName);
        marker.Host.Should().Be(Environment.MachineName);
        marker.FormatVersion.Should().Be(1);
    }

    [Fact]
    public async Task Remains_of_a_version_made_under_an_earlier_plan_name_are_finished_by_the_next_cleanup()
    {
        const string older = "2026_08_01-02_00 Old name";   // made before the plan was renamed to "Projects"
        VersionFolder.Create(_target, older, PlanId);
        var storage = new FaultyStorage(Target) { FailDelete = path => path == $"{older}/data.bin" };

        var result = (await Delete([older], storage)).Single();

        result.Outcome.Should().Be(VersionDeletionOutcome.RemainsLeft);
        var warnings = await LeftoverCleaner.CleanAsync(Target, PlanId, PlanName, null, CancellationToken.None);
        warnings.Should().BeEmpty();
        Directory.Exists(Path.Combine(_target, older)).Should().BeFalse();
    }

    [Fact]
    public async Task Reports_the_progress_over_all_versions_by_files()
    {
        _tmp.WriteFile($@"target\{New}\more.bin", "x");   // not in the manifest: the total comes from the manifests
        var reports = new List<VersionDeletionProgress>();

        await Delete([Old, New], progress: new SyncProgress<VersionDeletionProgress>(reports.Add));

        reports.TakeWhile(r => r.Phase == VersionDeletionPhase.Preparing).Should().Equal(
            new VersionDeletionProgress(VersionDeletionPhase.Preparing, 1, 2, Old, 0, 0),
            new VersionDeletionProgress(VersionDeletionPhase.Preparing, 2, 2, New, 0, 1));
        var deleting = reports.SkipWhile(r => r.Phase == VersionDeletionPhase.Preparing).ToList();
        deleting.Should().OnlyContain(r => r.Phase == VersionDeletionPhase.Deleting && r.VersionCount == 2 && r.FilesTotal == 2);
        deleting.First().Should().Be(new VersionDeletionProgress(VersionDeletionPhase.Deleting, 1, 2, Old, 0, 2));
        deleting.Select(r => r.FilesDone).Should().BeInAscendingOrder();
        deleting.Last().Should().Be(new VersionDeletionProgress(VersionDeletionPhase.Deleting, 2, 2, New, 3, 2),
            "files beyond the manifest still count");
        reports.Last().Fraction.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_during_a_version_finishes_it_and_returns_what_was_done()
    {
        using var cts = new CancellationTokenSource();
        var storage = new FaultyStorage(Target)
        {
            Before = (operation, path) =>
            {
                if (operation == "create" && path == $"{Old}/re-deleting.json")
                    cts.Cancel();
            },
        };

        var results = await Delete([Old, New], storage, ct: cts.Token);

        results.Should().Equal(new VersionDeletion(Old, VersionDeletionOutcome.Deleted, null));
        Directory.GetDirectories(_target).Select(Path.GetFileName).Should().Equal(New);
    }

    [Fact]
    public async Task Cancellation_stops_before_the_next_version()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var results = await Delete([Old, New], ct: cts.Token);

        results.Should().BeEmpty("the versions not started are left out");
        Directory.GetDirectories(_target).Should().HaveCount(2);
    }
}
