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

    private Task<RunLogEntry> Run(BackupPlan plan, ITargetVolume? volume = null, IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default, Func<string, IReadOnlyList<RetentionRule>?>? currentRules = null) =>
        new BackupRunner(volume ?? new PhysicalTargetVolume(), _time, currentRules)
            .RunAsync(new BackupRequest(plan, [], RunTrigger.Manual), progress, cancellationToken);

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
    public async Task A_version_that_vanishes_before_its_rename_is_not_logged_as_deleted()
    {
        Old(26);
        var vanishing = Old(27);
        var volume = new ScriptedVolume
        {
            BeforeMove = source =>
            {
                if (source == vanishing)
                    Directory.Delete(vanishing, recursive: true);   // as if the target dropped out
            },
        };

        var entry = await Run(Plan(Daily(1)), volume);

        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().Equal(OldName(26));
        entry.Warnings.Should().ContainSingle().Which.Should().StartWith($"Retention could not delete \"{OldName(27)}\": ");
    }

    [Fact]
    public async Task A_folder_copied_or_renamed_by_hand_is_not_managed_and_holds_no_slot()
    {
        var kept = VersionFolder.Create(_target, OldName(26, "Projects KEEP"), "p1", manifestPlanName: "Projects");
        Old(27);
        Old(28);
        var copy = VersionFolder.Create(_target, OldName(29, "Projects - Copy"), "p1", manifestPlanName: "Projects");

        var entry = await Run(Plan(Daily(2)));

        // Were the copy of the 29th a version, it would take the second daily slot and the 28th would go too.
        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().Equal(OldName(27));
        entry.Warnings.Should().BeEmpty();
        TargetEntries().Should().BeEquivalentTo(
            OldName(26, "Projects KEEP"), OldName(28), OldName(29, "Projects - Copy"), NewVersion);
        File.Exists(Path.Combine(kept, "data.bin")).Should().BeTrue();
        File.Exists(Path.Combine(copy, "data.bin")).Should().BeTrue();
    }

    [Fact]
    public async Task Canceling_between_two_deletions_keeps_the_run_completed_and_leaves_the_rest_for_the_next_run()
    {
        var first = Old(26);
        var second = Old(27);
        using var cts = new CancellationTokenSource();
        var volume = new ScriptedVolume
        {
            BeforeMove = source =>
            {
                if (source == first)
                    cts.Cancel();
            },
        };

        var entry = await Run(Plan(Daily(1)), volume, cancellationToken: cts.Token);

        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().Equal(OldName(26));
        Directory.Exists(first).Should().BeFalse();
        File.Exists(Path.Combine(second, "data.bin")).Should().BeTrue();
        TargetEntries().Should().BeEquivalentTo(OldName(27), NewVersion);
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
        _tmp.CreateDir($@"target\{OldName(5)}.deleting");

        var entry = await Run(Plan());

        entry.Status.Should().Be(RunStatus.Completed);
        entry.Warnings.Should().BeEmpty();
        TargetEntries().Should().BeEquivalentTo(OldName(2) + ".deleting", OldName(3) + ".deleting",
            OldName(4, "Other") + ".deleting", NewVersion);
    }

    [Fact]
    public async Task Remains_named_like_the_plan_whose_manifest_cannot_be_read_are_left_alone()
    {
        var manifest = _tmp.WriteFile($@"target\{OldName(1)}.deleting\re-manifest.json", "not json");
        _tmp.WriteFile($@"target\{OldName(2)}.deleting\re-manifest.json", "not json");
        var data = _tmp.WriteFile($@"target\{OldName(2)}.deleting\data.bin", "whose is this?");

        var entry = await Run(Plan(Daily(1)));

        entry.Status.Should().Be(RunStatus.Completed);
        TargetEntries().Should().BeEquivalentTo(OldName(1) + ".deleting", OldName(2) + ".deleting", NewVersion);
        File.Exists(manifest).Should().BeTrue();
        File.Exists(data).Should().BeTrue();
    }

    [Fact]
    public async Task Remains_of_a_folder_copied_by_hand_are_left_alone()
    {
        var copy = VersionFolder.Create(_target, OldName(1, "Projects - Copy") + ".deleting", "p1", manifestPlanName: "Projects");

        var entry = await Run(Plan(Daily(1)));

        entry.Status.Should().Be(RunStatus.Completed);
        entry.Warnings.Should().BeEmpty();
        File.Exists(Path.Combine(copy, "data.bin")).Should().BeTrue();
    }

    [Fact]
    public async Task A_removal_that_fails_half_way_counts_as_deleted_with_a_warning_and_the_remains_go_at_the_next_run()
    {
        Old(26);
        var stubborn = Old(27);
        var doomed = stubborn + ".deleting";
        var volume = new ScriptedVolume { FailDelete = path => path == doomed };

        var entry = await Run(Plan(Daily(1)), volume);

        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().Contain(OldName(27));
        entry.Warnings.Should().ContainSingle().Which.Should()
            .StartWith($"\"{OldName(27)}\" was removed from the versions, but its remains could not be deleted yet");
        Directory.Exists(stubborn).Should().BeFalse();
        Directory.Exists(doomed).Should().BeTrue();

        // A later run, the delete still failing: reported as a warning. The fake clock does not advance, so
        // the version name of the first run must be free again.
        Directory.Delete(Path.Combine(_target, NewVersion), recursive: true);
        var still = await Run(Plan(), volume);
        still.Warnings.Should().ContainSingle().Which.Should().StartWith("Remains of an earlier removal could not be deleted");
        Directory.Exists(doomed).Should().BeTrue();
    }

    [Fact]
    public async Task Remains_are_removed_without_a_warning_when_the_volume_works_again()
    {
        var stubborn = Old(27);
        var doomed = stubborn + ".deleting";
        await Run(Plan(Daily(1)), new ScriptedVolume { FailDelete = path => path == doomed });
        Directory.Exists(doomed).Should().BeTrue();
        Directory.Delete(Path.Combine(_target, NewVersion), recursive: true);   // the fake clock does not advance

        var entry = await Run(Plan());

        entry.Warnings.Should().BeEmpty();
        Directory.Exists(doomed).Should().BeFalse();
    }

    [Fact]
    public async Task A_deleting_junction_to_an_owned_version_is_left_alone()
    {
        var real = VersionFolder.Create(_tmp.CreateDir("elsewhere"), OldName(3), "p1");
        var link = Path.Combine(_target, OldName(3) + ".deleting");
        Junction.Create(link, real);

        try
        {
            var entry = await Run(Plan(Daily(1)));

            entry.Status.Should().Be(RunStatus.Completed);
            File.Exists(Path.Combine(real, "data.bin")).Should().BeTrue();
            File.Exists(Path.Combine(real, "re-manifest.json")).Should().BeTrue();
            Directory.Exists(link).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(link);
        }
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

    private BackupPlan FreeingPlan(params RetentionRule[] rules)
    {
        var plan = Plan(rules);
        plan.FreeSpaceByRetention = true;
        return plan;
    }

    /// <summary>Reports 2 free bytes plus 10 for each of the given old versions that is gone.</summary>
    private Func<long> FreedBy(params int[] days) =>
        () => 2 + 10 * days.Count(day => !Directory.Exists(Path.Combine(_target, OldName(day))));

    [Fact]
    public async Task Frees_space_by_deleting_the_oldest_versions_retention_would_delete_anyway()
    {
        for (var day = 25; day <= 29; day++)
            Old(day);
        var volume = new ScriptedVolume { FreeSpace = FreedBy(25, 26, 27) };

        var entry = await Run(FreeingPlan(Daily(3)), volume);

        entry.Status.Should().Be(RunStatus.Completed);
        // 25 and 26 made room before the run; 27 went in the normal retention pass after it.
        entry.RetentionDeleted.Should().Equal(OldName(25), OldName(26), OldName(27));
        entry.Warnings.Should().BeEmpty();
        TargetEntries().Should().BeEquivalentTo(OldName(28), OldName(29), NewVersion);
    }

    [Fact]
    public async Task Does_not_free_space_when_the_option_is_off()
    {
        for (var day = 25; day <= 29; day++)
            Old(day);
        var volume = new ScriptedVolume { FreeSpace = FreedBy(25, 26, 27) };

        var entry = await Run(Plan(Daily(3)), volume);

        entry.Status.Should().Be(RunStatus.Full);
        entry.RetentionDeleted.Should().BeEmpty();
        TargetEntries().Should().HaveCount(5);
    }

    [Fact]
    public async Task Deletes_nothing_when_even_all_deletable_versions_would_not_make_enough_room()
    {
        for (var day = 25; day <= 29; day++)
            Old(day, bytes: 1);
        var volume = new ScriptedVolume { FreeSpace = () => 2 };

        var entry = await Run(FreeingPlan(Daily(3)), volume);

        entry.Status.Should().Be(RunStatus.Full);
        entry.RetentionDeleted.Should().BeEmpty();
        TargetEntries().Should().HaveCount(5);
    }

    [Fact]
    public async Task Never_deletes_the_newest_existing_version_to_make_room()
    {
        Old(29, bytes: 100);
        var volume = new ScriptedVolume { FreeSpace = () => 2 };

        var entry = await Run(FreeingPlan(Daily(1)), volume);

        entry.Status.Should().Be(RunStatus.Full);
        entry.RetentionDeleted.Should().BeEmpty();
        TargetEntries().Should().BeEquivalentTo(OldName(29));
    }

    [Fact]
    public async Task Records_what_it_deleted_even_when_the_space_is_still_not_enough()
    {
        for (var day = 25; day <= 29; day++)
            Old(day);
        var volume = new ScriptedVolume { FreeSpace = () => 2 };   // deleting does not help on this volume

        var entry = await Run(FreeingPlan(Daily(3)), volume);

        entry.Status.Should().Be(RunStatus.Full);
        entry.RetentionDeleted.Should().Equal(OldName(25), OldName(26), OldName(27));
        TargetEntries().Should().BeEquivalentTo(OldName(28), OldName(29));
    }

    [Fact]
    public async Task Without_rules_there_is_nothing_to_free()
    {
        Old(28);
        Old(29);
        var volume = new ScriptedVolume { FreeSpace = () => 2 };

        var entry = await Run(FreeingPlan(), volume);

        entry.Status.Should().Be(RunStatus.Full);
        TargetEntries().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_version_that_cannot_be_deleted_to_make_room_is_a_warning_and_the_next_one_is_tried()
    {
        for (var day = 25; day <= 29; day++)
            Old(day);
        var stubborn = Path.Combine(_target, OldName(25));
        var volume = new ScriptedVolume { FreeSpace = FreedBy(25, 26, 27), FailMove = source => source == stubborn };

        var entry = await Run(FreeingPlan(Daily(3)), volume);

        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().Equal(OldName(26), OldName(27));
        entry.Warnings.Should().HaveCount(2, "once before the run and once in the retention pass after it");
        entry.Warnings[0].Should().Be($"\"{OldName(25)}\" could not be deleted to free space: the folder is in use");
        Directory.Exists(stubborn).Should().BeTrue();
    }

    [Fact]
    public async Task A_candidate_that_vanishes_while_room_is_made_is_not_logged_as_deleted()
    {
        for (var day = 25; day <= 29; day++)
            Old(day);
        var vanishing = Path.Combine(_target, OldName(25));
        var volume = new ScriptedVolume
        {
            FreeSpace = FreedBy(26, 27),
            BeforeMove = source =>
            {
                if (source == vanishing)
                    Directory.Delete(vanishing, recursive: true);   // as if the target dropped out
            },
        };

        var entry = await Run(FreeingPlan(Daily(3)), volume);

        entry.Status.Should().Be(RunStatus.Completed);
        // 26 and 27 made room before the run; nothing was left to delete after it.
        entry.RetentionDeleted.Should().Equal(OldName(26), OldName(27));
        entry.Warnings.Should().ContainSingle().Which.Should()
            .StartWith($"\"{OldName(25)}\" could not be deleted to free space: ");
    }

    [Fact]
    public async Task Rules_loosened_after_the_request_was_made_delete_nothing()
    {
        Old(26);
        Old(27);
        var asked = new List<string>();

        var entry = await Run(Plan(Daily(1)), currentRules: planId =>
        {
            asked.Add(planId);
            return [Daily(30)];
        });

        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().BeEmpty();
        entry.Warnings.Should().BeEmpty();
        TargetEntries().Should().BeEquivalentTo(OldName(26), OldName(27), NewVersion);
        asked.Should().Equal("p1");
    }

    [Fact]
    public async Task Rules_tightened_after_the_request_was_made_delete_the_old_versions()
    {
        Old(26);
        Old(27);

        var entry = await Run(Plan(), currentRules: _ => [Daily(1)]);

        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().Equal(OldName(26), OldName(27));
        entry.Warnings.Should().BeEmpty();
        TargetEntries().Should().BeEquivalentTo(NewVersion);
    }

    [Fact]
    public async Task Rules_removed_after_the_request_was_made_delete_nothing()
    {
        Old(26);
        var phases = new List<BackupPhase>();

        var entry = await Run(Plan(Daily(1)), progress: new SyncProgress(p => phases.Add(p.Phase)), currentRules: _ => []);

        entry.RetentionDeleted.Should().BeEmpty();
        entry.Warnings.Should().BeEmpty();
        phases.Should().NotContain(BackupPhase.Retention);
        TargetEntries().Should().BeEquivalentTo(OldName(26), NewVersion);
    }

    [Fact]
    public async Task A_plan_that_no_longer_exists_deletes_nothing()
    {
        Old(26);
        Old(27);

        var entry = await Run(Plan(Daily(1)), currentRules: _ => null);

        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().BeEmpty();
        entry.Warnings.Should().Equal("Retention was skipped: the plan no longer exists.");
        TargetEntries().Should().BeEquivalentTo(OldName(26), OldName(27), NewVersion);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(System.Text.Json.JsonException))]
    public async Task Current_rules_that_cannot_be_read_delete_nothing(Type failure)
    {
        Old(26);
        Old(27);

        var entry = await Run(Plan(Daily(1)),
            currentRules: _ => throw (Exception)Activator.CreateInstance(failure, "the plan file is damaged")!);

        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().BeEmpty();
        entry.Warnings.Should().Equal(
            "Retention was skipped: the plan's current rules could not be read: the plan file is damaged");
        TargetEntries().Should().BeEquivalentTo(OldName(26), OldName(27), NewVersion);
    }

    [Fact]
    public async Task Room_is_made_by_the_current_rules_too()
    {
        for (var day = 25; day <= 29; day++)
            Old(day);
        var volume = new ScriptedVolume { FreeSpace = FreedBy(25, 26, 27) };

        // The request still says Daily 30, which would free nothing.
        var entry = await Run(FreeingPlan(Daily(30)), volume, currentRules: _ => [Daily(3)]);

        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().Equal(OldName(25), OldName(26), OldName(27));
        TargetEntries().Should().BeEquivalentTo(OldName(28), OldName(29), NewVersion);
    }

    [Fact]
    public async Task No_room_is_made_when_the_plan_no_longer_exists()
    {
        for (var day = 25; day <= 29; day++)
            Old(day);
        var volume = new ScriptedVolume { FreeSpace = FreedBy(25, 26, 27) };

        var entry = await Run(FreeingPlan(Daily(3)), volume, currentRules: _ => null);

        entry.Status.Should().Be(RunStatus.Full);
        entry.RetentionDeleted.Should().BeEmpty();
        entry.Warnings.Should().Equal("Old versions were not deleted to free space: the plan no longer exists.");
        TargetEntries().Should().HaveCount(5);
    }

    [Fact]
    public async Task No_room_is_made_when_the_current_rules_cannot_be_read()
    {
        for (var day = 25; day <= 29; day++)
            Old(day);
        var volume = new ScriptedVolume { FreeSpace = FreedBy(25, 26, 27) };

        var entry = await Run(FreeingPlan(Daily(3)), volume, currentRules: _ => throw new IOException("the share is gone"));

        entry.Status.Should().Be(RunStatus.Full);
        entry.RetentionDeleted.Should().BeEmpty();
        entry.Warnings.Should().Equal(
            "Old versions were not deleted to free space: the plan's current rules could not be read: the share is gone");
        TargetEntries().Should().HaveCount(5);
    }

    private sealed class SyncProgress(Action<BackupProgress> onReport) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => onReport(value);
    }
}
