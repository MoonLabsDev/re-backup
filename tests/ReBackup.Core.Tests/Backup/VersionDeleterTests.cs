using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Backup;

public class VersionDeleterTests : IDisposable
{
    private const string PlanId = "p1";
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

    [Fact]
    public void Deletes_the_named_versions_and_nothing_else()
    {
        var results = VersionDeleter.Delete(_target, PlanId, [Old]);

        results.Should().Equal(new VersionDeletion(Old, VersionDeletionOutcome.Deleted, null));
        Directory.GetDirectories(_target).Select(Path.GetFileName).Should().Equal(New);
    }

    [Fact]
    public void Leaves_folders_that_are_not_versions_of_the_plan()
    {
        const string foreign = "2026_09_03-02_00 Projects";
        VersionFolder.Create(_target, foreign, "another-plan");
        const string noManifest = "2026_09_04-02_00 Projects";
        _tmp.CreateDir($@"target\{noManifest}");

        var results = VersionDeleter.Delete(_target, PlanId, [foreign, noManifest]);

        results.Select(r => r.Outcome).Should().Equal(VersionDeletionOutcome.NotManaged, VersionDeletionOutcome.NotManaged);
        Directory.Exists(Path.Combine(_target, foreign)).Should().BeTrue();
        Directory.Exists(Path.Combine(_target, noManifest)).Should().BeTrue();
    }

    [Fact]
    public void Refuses_names_that_lead_out_of_the_target()
    {
        var outside = VersionFolder.Create(_tmp.CreateDir("elsewhere"), Old, PlanId);

        var results = VersionDeleter.Delete(_target, PlanId, [$@"..\elsewhere\{Old}", ".partial", ""]);

        results.Select(r => r.Outcome).Should().AllBeEquivalentTo(VersionDeletionOutcome.NotManaged);
        Directory.Exists(outside).Should().BeTrue();
    }

    [Fact]
    public void A_version_that_is_already_gone_is_reported_as_gone()
    {
        Directory.Delete(Path.Combine(_target, Old), recursive: true);

        VersionDeleter.Delete(_target, PlanId, [Old]).Single().Outcome.Should().Be(VersionDeletionOutcome.Gone);
    }

    [Fact]
    public void A_failing_rename_is_reported_and_the_others_are_still_deleted()
    {
        var volume = new ScriptedVolume { FailMove = path => path.EndsWith(Old, StringComparison.Ordinal) };

        var results = VersionDeleter.Delete(_target, PlanId, [Old, New], volume);

        results[0].Outcome.Should().Be(VersionDeletionOutcome.Failed);
        results[0].Error.Should().NotBeNullOrEmpty();
        results[1].Outcome.Should().Be(VersionDeletionOutcome.Deleted);
        Directory.Exists(Path.Combine(_target, Old)).Should().BeTrue();
    }

    [Fact]
    public void Remains_that_cannot_be_deleted_are_reported_and_are_no_longer_a_version()
    {
        var doomed = Path.Combine(_target, Old) + VersionName.DeletingSuffix;
        var volume = new ScriptedVolume { FailDelete = path => path == doomed };

        var result = VersionDeleter.Delete(_target, PlanId, [Old], volume).Single();

        result.Outcome.Should().Be(VersionDeletionOutcome.RemainsLeft);
        result.Error.Should().NotBeNullOrEmpty();
        Directory.Exists(Path.Combine(_target, Old)).Should().BeFalse();
    }

    [Fact]
    public void Reports_the_progress_over_all_versions_by_files()
    {
        _tmp.WriteFile($@"target\{New}\more.bin", "x");   // not in the manifest: the total comes from the manifests
        var reports = new List<VersionDeletionProgress>();

        VersionDeleter.Delete(_target, PlanId, [Old, New], progress: new SyncProgress<VersionDeletionProgress>(reports.Add));

        reports.Should().NotBeEmpty();
        reports.Should().OnlyContain(r => r.VersionCount == 2 && r.FilesTotal == 2);
        reports.First().Should().Be(new VersionDeletionProgress(1, 2, Old, 0, 2));
        reports.Select(r => r.FilesDone).Should().BeInAscendingOrder();
        reports.Last().Should().Be(new VersionDeletionProgress(2, 2, New, 3, 2), "files beyond the manifest still count");
        reports.Last().Fraction.Should().Be(1);
    }

    [Fact]
    public void Cancellation_during_a_version_finishes_it_and_returns_what_was_done()
    {
        using var cts = new CancellationTokenSource();
        var volume = new ScriptedVolume { BeforeMove = _ => cts.Cancel() };

        var results = VersionDeleter.Delete(_target, PlanId, [Old, New], volume, cancellationToken: cts.Token);

        results.Should().Equal(new VersionDeletion(Old, VersionDeletionOutcome.Deleted, null));
        Directory.GetDirectories(_target).Select(Path.GetFileName).Should().Equal(New);
    }

    [Fact]
    public void Cancellation_stops_before_the_next_version()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var results = VersionDeleter.Delete(_target, PlanId, [Old, New], cancellationToken: cts.Token);

        results.Should().BeEmpty("the versions not started are left out");
        Directory.GetDirectories(_target).Should().HaveCount(2);
    }
}
