using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Retention;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Backup;

public class BackupRunnerRetentionTests : IDisposable
{
    private const string NewVersion = "2026_09_30-16_05 Projects";   // 14:05 UTC in the fixed +02:00 test zone
    private readonly TempDir _tmp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.Zero));
    private readonly string _source;
    private readonly string _target;

    public BackupRunnerRetentionTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
        _source = _tmp.CreateDir("source");
        _target = _tmp.CreateDir("target");
        _tmp.WriteFile(@"source\a.txt", "alpha");
        _tmp.WriteFile(@"source\sub\b.bin", "bravo-bravo");
    }

    public void Dispose() => _tmp.Dispose();

    private static RetentionRule Daily(int keep) => new() { Period = RetentionPeriod.Daily, Keep = keep };

    private BackupPlan Plan(params RetentionRule[] rules) => new()
    {
        Id = "p1",
        Name = "Projects",
        Source = _source,
        Target = _target,
        Retention = [.. rules],
    };

    private Task<RunLogEntry> Run(BackupPlan plan, ITargetVolume? volume = null, IProgress<BackupProgress>? progress = null) =>
        new BackupRunner(volume ?? new PhysicalTargetVolume(), _time)
            .RunAsync(new BackupRequest(plan, [], RunTrigger.Manual), progress);

    /// <summary>An existing version folder of 2026-09-<paramref name="day"/> 02:00.</summary>
    private string Old(int day, string planId = "p1", string name = "Projects", long bytes = 10) =>
        VersionFolder.Create(_target, OldName(day, name), planId, bytes);

    private static string OldName(int day, string name = "Projects") => $"2026_09_{day:00}-02_00 {name}";

    private string[] TargetEntries() =>
        Directory.GetFileSystemEntries(_target).Select(e => Path.GetFileName(e)!).ToArray();

    [Fact]
    public async Task Retention_deletes_the_versions_the_rules_no_longer_keep()
    {
        Old(26);
        Old(27);
        Old(28);
        Old(29);

        var entry = await Run(Plan(Daily(2)));

        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().Equal(OldName(26), OldName(27), OldName(28));
        entry.Warnings.Should().BeEmpty();
        TargetEntries().Should().BeEquivalentTo(OldName(29), NewVersion);
    }

    [Fact]
    public async Task Without_rules_nothing_is_deleted()
    {
        Old(26);
        Old(27);

        var entry = await Run(Plan());

        entry.RetentionDeleted.Should().BeEmpty();
        TargetEntries().Should().BeEquivalentTo(OldName(26), OldName(27), NewVersion);
    }

    [Fact]
    public async Task Only_versions_whose_manifest_carries_the_plan_id_are_deleted()
    {
        Old(1, planId: "other");
        _tmp.WriteFile($@"target\{OldName(2)}\a.txt", "no manifest");
        Old(3);

        var entry = await Run(Plan(Daily(1)));

        entry.RetentionDeleted.Should().Equal(OldName(3));
        TargetEntries().Should().BeEquivalentTo(OldName(1), OldName(2), NewVersion);
    }

    [Fact]
    public async Task Versions_made_under_an_earlier_plan_name_are_managed_too()
    {
        Old(3, name: "Old name");

        var entry = await Run(Plan(Daily(1)));

        entry.RetentionDeleted.Should().Equal(OldName(3, "Old name"));
        TargetEntries().Should().BeEquivalentTo(NewVersion);
    }

    [Fact]
    public async Task A_version_that_cannot_be_deleted_is_a_warning_and_the_run_stays_completed()
    {
        Old(26);
        var stubborn = Old(27);
        var volume = new ScriptedVolume { FailMove = source => source == stubborn };

        var entry = await Run(Plan(Daily(1)), volume);

        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().Equal(OldName(26));
        entry.Warnings.Should().ContainSingle().Which.Should()
            .Be($"Retention could not delete \"{OldName(27)}\": the folder is in use");
        File.Exists(Path.Combine(stubborn, "data.bin")).Should().BeTrue();
    }

    [Fact]
    public async Task Retention_does_not_run_after_a_failed_run()
    {
        Old(26);
        Old(27);
        var volume = new ScriptedVolume { FreeSpace = () => 0 };

        var entry = await Run(Plan(Daily(1)), volume);

        entry.Status.Should().Be(RunStatus.Full);
        entry.RetentionDeleted.Should().BeEmpty();
        TargetEntries().Should().BeEquivalentTo(OldName(26), OldName(27));
    }

    [Fact]
    public async Task Invalid_rules_skip_retention_with_a_warning()
    {
        Old(26);

        var entry = await Run(Plan(Daily(0)));

        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().BeEmpty();
        entry.Warnings.Should().ContainSingle().Which.Should().StartWith("Retention was skipped: Retention rule 1: keep must be");
        TargetEntries().Should().BeEquivalentTo(OldName(26), NewVersion);
    }

    [Fact]
    public async Task The_version_just_made_is_never_deleted_even_when_a_later_dated_one_exists()
    {
        VersionFolder.Create(_target, "2026_10_05-10_00 Projects", "p1");

        var entry = await Run(Plan(Daily(1)));

        entry.RetentionDeleted.Should().BeEmpty();
        TargetEntries().Should().BeEquivalentTo("2026_10_05-10_00 Projects", NewVersion);
    }

    [Fact]
    public async Task Remains_of_an_interrupted_removal_are_cleaned_up_at_the_next_run()
    {
        VersionFolder.Create(_target, OldName(1) + ".deleting", "p1");
        VersionFolder.Create(_target, OldName(2) + ".deleting", "other");
        _tmp.WriteFile($@"target\{OldName(3)}.deleting\a.txt", "remains without a manifest");
        _tmp.WriteFile($@"target\{OldName(4, "Other")}.deleting\a.txt", "remains of another plan");

        var entry = await Run(Plan());

        entry.Status.Should().Be(RunStatus.Completed);
        TargetEntries().Should().BeEquivalentTo(OldName(2) + ".deleting", OldName(4, "Other") + ".deleting", NewVersion);
    }

    [Fact]
    public async Task A_plan_name_ending_in_deleting_aborts_as_Error()
    {
        var plan = Plan();
        plan.Name = "Projects.deleting";

        var entry = await Run(plan);

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Be("The plan name must not end with \".deleting\".");
        TargetEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Reports_a_retention_phase_when_there_are_rules()
    {
        Old(26);
        var phases = new List<BackupPhase>();

        await Run(Plan(Daily(1)), progress: new SyncProgress(p => phases.Add(p.Phase)));

        phases.Last().Should().Be(BackupPhase.Retention);
        new BackupProgress(BackupPhase.Retention, 0, 0, 0, 0, "").Fraction.Should().Be(1);
    }

    private sealed class SyncProgress(Action<BackupProgress> onReport) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => onReport(value);
    }
}
