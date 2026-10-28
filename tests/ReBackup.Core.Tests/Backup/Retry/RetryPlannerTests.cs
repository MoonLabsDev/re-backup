using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Backup.Retry;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.Core.Tests.Backup.Retry;

// FakeTimeProvider.Advance fires due timers with the clock already at the end of the step.
public class RetryPlannerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Start);
    private readonly TempDir _dir = new();
    private readonly List<RetryState> _due = [];
    private readonly List<string> _changed = [];
    private readonly RetryPlanner _planner;

    public RetryPlannerTests()
    {
        _planner = NewPlanner();
        _planner.Changed += _changed.Add;
    }

    public void Dispose()
    {
        _planner.Dispose();
        _dir.Dispose();
    }

    private string Directory => _dir.PathOf("retries");

    private RetryPlanner NewPlanner() => new(Directory, _due.Add, _time);

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private string FileOf(string planId) => Path.Combine(Directory, planId + ".json");

    private static BackupPlan Plan(string id = "p1", int attempts = 3, int interval = 5, bool enabled = true,
        string source = @"C:\data", string target = @"E:\backup") => new()
    {
        Id = id,
        Name = id,
        Source = StorageLocation.FileSystem(source),
        Target = StorageLocation.FileSystem(target),
        RetrySkipped = new RetrySkippedSettings { Enabled = enabled, Attempts = attempts, IntervalMinutes = interval },
    };

    private static List<SkippedEntry> Locked(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new SkippedEntry($"locked{i}.txt", "locked", "core.file.locked"))];

    private RunLogEntry Regular(int candidates, string runId = "run-1", string? version = "2026_10_28-03_00 MoonLabs")
    {
        var entry = new RunLogEntry
        {
            RunId = runId,
            Trigger = RunTrigger.Scheduled,
            StartUtc = Now,
            EndUtc = Now,
            Status = candidates > 0 ? RunStatus.CompletedWithWarnings : RunStatus.Completed,
            Version = version,
        };
        foreach (var file in Locked(candidates))
            entry.AddSkipped(file);
        return entry;
    }

    private RunLogEntry RetryLine(RunStatus status, int open, string retryOf = "run-1", bool versionGone = false)
    {
        var entry = new RunLogEntry
        {
            RunId = Guid.NewGuid().ToString("N"),
            Trigger = RunTrigger.Retry,
            RetryOf = retryOf,
            StartUtc = Now,
            EndUtc = Now,
            Status = status,
            VersionGone = versionGone,
        };
        entry.RetryCandidates.AddRange(Locked(open));
        return entry;
    }

    private void Minutes(int count) => _time.Advance(TimeSpan.FromMinutes(count));

    private void Seconds(int count) => _time.Advance(TimeSpan.FromSeconds(count));

    [Fact]
    public void A_run_with_retryable_skips_is_retried_after_the_interval()
    {
        var start = Now;
        _planner.OnRunFinished(Plan(attempts: 3, interval: 5), Regular(2));

        _time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        _due.Should().BeEmpty();

        Seconds(1);
        _due.Should().ContainSingle();
        var state = _due[0];
        state.PlanId.Should().Be("p1");
        state.RunId.Should().Be("run-1");
        state.Version.Should().Be("2026_10_28-03_00 MoonLabs");
        state.Source.Should().Be(StorageLocation.FileSystem(@"C:\data"));
        state.Target.Should().Be(StorageLocation.FileSystem(@"E:\backup"));
        state.Attempt.Should().Be(0);
        state.Attempts.Should().Be(3);
        state.NextDueUtc.Should().Be(start.AddMinutes(5));
        state.Files.Should().Equal(Locked(2));
        File.Exists(FileOf("p1")).Should().BeTrue();

        // In flight until the attempt reports back: never handed out twice.
        Minutes(30);
        _due.Should().ContainSingle();
    }

    [Fact]
    public void A_run_without_candidates_or_with_retries_off_plans_nothing()
    {
        _planner.OnRunFinished(Plan(), Regular(0));
        _planner.OnRunFinished(Plan("p2", enabled: false), Regular(2));
        _planner.OnRunFinished(Plan("p3"), Regular(2, version: null));

        _planner.StateOf("p1").Should().BeNull();
        _planner.StateOf("p2").Should().BeNull();
        _planner.StateOf("p3").Should().BeNull();
        System.IO.Directory.Exists(Directory).Should().BeFalse();
        Minutes(60);
        _due.Should().BeEmpty();
    }

    [Fact]
    public void Recovered_files_leave_the_list_and_an_attempt_is_counted()
    {
        _planner.OnRunFinished(Plan(), Regular(2));
        Minutes(5);

        var line = RetryLine(RunStatus.CompletedWithWarnings, open: 1);
        _planner.OnRunFinished(Plan(), line);

        var state = _planner.StateOf("p1")!;
        state.Files.Should().Equal(Locked(1));
        state.Attempt.Should().Be(1);
        state.NextDueUtc.Should().Be(line.EndUtc.AddMinutes(5));

        Minutes(5);
        _due.Should().HaveCount(2);
        _due[1].Attempt.Should().Be(1);
        _due[1].Files.Should().Equal(Locked(1));
    }

    [Fact]
    public void A_failed_attempt_keeps_the_files_and_counts()
    {
        _planner.OnRunFinished(Plan(), Regular(2));
        Minutes(5);

        _planner.OnRunFinished(Plan(), RetryLine(RunStatus.Error, open: 2));

        var state = _planner.StateOf("p1")!;
        state.Files.Should().Equal(Locked(2));
        state.Attempt.Should().Be(1);
    }

    [Fact]
    public void The_state_is_deleted_when_everything_was_recovered()
    {
        _planner.OnRunFinished(Plan(), Regular(2));
        Minutes(5);

        _planner.OnRunFinished(Plan(), RetryLine(RunStatus.Completed, open: 0));

        _planner.StateOf("p1").Should().BeNull();
        File.Exists(FileOf("p1")).Should().BeFalse();
        Minutes(60);
        _due.Should().ContainSingle();
    }

    [Fact]
    public void The_state_is_deleted_after_the_last_attempt()
    {
        _planner.OnRunFinished(Plan(attempts: 3), Regular(2));

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Minutes(5);
            _due.Should().HaveCount(attempt);
            _planner.OnRunFinished(Plan(attempts: 3), RetryLine(RunStatus.CompletedWithWarnings, open: 1));
            if (attempt < 3)
                _planner.StateOf("p1")!.Attempt.Should().Be(attempt);
        }

        _planner.StateOf("p1").Should().BeNull();
        File.Exists(FileOf("p1")).Should().BeFalse();
        Minutes(60);
        _due.Should().HaveCount(3);
    }

    [Fact]
    public void A_canceled_attempt_is_not_counted()
    {
        _planner.OnRunFinished(Plan(), Regular(2));
        Minutes(5);

        _planner.OnRunFinished(Plan(), RetryLine(RunStatus.Canceled, open: 2));

        var state = _planner.StateOf("p1")!;
        state.Attempt.Should().Be(0);
        state.Files.Should().Equal(Locked(2));
        state.NextDueUtc.Should().Be(Now.AddMinutes(1));
        Seconds(59);
        _due.Should().ContainSingle();
        Seconds(1);
        _due.Should().HaveCount(2);
    }

    [Fact]
    public void A_gone_version_drops_the_state()
    {
        _planner.OnRunFinished(Plan(), Regular(2));
        Minutes(5);

        _planner.OnRunFinished(Plan(), RetryLine(RunStatus.Error, open: 2, versionGone: true));

        _planner.StateOf("p1").Should().BeNull();
        File.Exists(FileOf("p1")).Should().BeFalse();
    }

    [Fact]
    public void A_retry_line_of_another_run_is_ignored()
    {
        _planner.OnRunFinished(Plan(), Regular(2));
        Minutes(5);

        _planner.OnRunFinished(Plan(), RetryLine(RunStatus.Completed, open: 0, retryOf: "older-run"));

        _planner.StateOf("p1")!.Attempt.Should().Be(0);
    }

    [Fact]
    public void A_regular_run_of_the_plan_drops_its_state()
    {
        _planner.OnRunFinished(Plan(), Regular(2));

        _planner.OnRunStarted("p1", isRetry: true);
        _planner.StateOf("p1").Should().NotBeNull();

        _planner.OnRunStarted("p1", isRetry: false);
        _planner.StateOf("p1").Should().BeNull();
        File.Exists(FileOf("p1")).Should().BeFalse();
        Minutes(60);
        _due.Should().BeEmpty();
    }

    [Fact]
    public void A_deferred_retry_is_due_again_a_minute_later()
    {
        _planner.OnRunFinished(Plan(), Regular(2));
        Minutes(5);
        _due.Should().ContainSingle();

        _planner.Defer("p1");

        Seconds(59);
        _due.Should().ContainSingle();
        Seconds(1);
        _due.Should().HaveCount(2);
        _due[1].Attempt.Should().Be(0);
    }

    [Fact]
    public void A_failing_due_callback_counts_as_deferred()
    {
        var calls = 0;
        using var planner = new RetryPlanner(_dir.PathOf("other"), _ => { calls++; throw new InvalidOperationException(); }, _time);
        planner.OnRunFinished(Plan(), Regular(2));

        Minutes(5);
        calls.Should().Be(1);
        Minutes(1);
        calls.Should().Be(2);
    }

    [Fact]
    public void Pending_retries_survive_a_restart()
    {
        var plan = Plan();
        var run = Regular(2);
        _planner.OnRunFinished(plan, run);
        var before = _planner.StateOf("p1")!;
        _planner.Dispose();
        Minutes(30);   // overdue meanwhile

        using var restarted = NewPlanner();
        restarted.Start(id => id == "p1" ? plan : null, _ => run);

        var after = restarted.StateOf("p1")!;
        after.Should().BeEquivalentTo(before, o => o.Excluding(s => s.NextDueUtc));
        after.NextDueUtc.Should().Be(Now.AddMinutes(1));
        Seconds(59);
        _due.Should().BeEmpty();
        Seconds(1);
        _due.Should().ContainSingle().Which.RunId.Should().Be("run-1");
    }

    [Fact]
    public void A_restart_keeps_a_due_time_still_ahead()
    {
        var plan = Plan(interval: 30);
        var run = Regular(2);
        _planner.OnRunFinished(plan, run);
        _planner.Dispose();

        using var restarted = NewPlanner();
        restarted.Start(_ => plan, _ => run);

        restarted.StateOf("p1")!.NextDueUtc.Should().Be(run.EndUtc.AddMinutes(30));
    }

    [Fact]
    public void A_restart_drops_states_superseded_disabled_or_moved()
    {
        var plans = new Dictionary<string, BackupPlan>
        {
            ["kept"] = Plan("kept"),
            ["superseded"] = Plan("superseded"),
            ["disabled"] = Plan("disabled"),
            ["moved"] = Plan("moved"),
            ["deleted"] = Plan("deleted"),
        };
        var runs = new Dictionary<string, RunLogEntry>();
        foreach (var (id, plan) in plans)
        {
            runs[id] = Regular(1, runId: "run-" + id);
            _planner.OnRunFinished(plan, runs[id]);
        }
        _planner.Dispose();

        runs["superseded"] = Regular(0, runId: "newer");
        plans["disabled"].RetrySkipped.Enabled = false;
        plans["moved"].Target = StorageLocation.FileSystem(@"F:\elsewhere");
        plans.Remove("deleted");

        using var restarted = NewPlanner();
        restarted.Start(id => plans.GetValueOrDefault(id), id => runs[id]);

        restarted.StateOf("kept").Should().NotBeNull();
        foreach (var id in new[] { "superseded", "disabled", "moved", "deleted" })
        {
            restarted.StateOf(id).Should().BeNull(id);
            File.Exists(FileOf(id)).Should().BeFalse(id);
        }
    }

    [Fact]
    public void A_state_whose_run_ended_over_a_day_ago_is_dropped_at_start()
    {
        // Ruling R15: week-old files must not be written into an old version after a long shutdown.
        var plan = Plan();
        var run = Regular(2);
        _planner.OnRunFinished(plan, run);
        _planner.StateOf("p1")!.RunEndUtc.Should().Be(run.EndUtc);
        _planner.Dispose();
        _time.Advance(RetryPlanner.MaxAge + TimeSpan.FromSeconds(1));

        using var restarted = NewPlanner();
        restarted.Start(_ => plan, _ => run);

        restarted.StateOf("p1").Should().BeNull();
        File.Exists(FileOf("p1")).Should().BeFalse();
        Minutes(60);
        _due.Should().BeEmpty();
    }

    [Fact]
    public void A_state_without_the_run_end_is_not_expired()
    {
        // A state file written before the run end was stored.
        var plan = Plan();
        var run = Regular(2);
        _planner.OnRunFinished(plan, run);
        _planner.Dispose();
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(FileOf("p1")))!.AsObject();
        json.Remove("runEndUtc").Should().BeTrue();
        File.WriteAllText(FileOf("p1"), json.ToJsonString());
        _time.Advance(TimeSpan.FromDays(3));

        using var restarted = NewPlanner();
        restarted.Start(_ => plan, _ => run);

        restarted.StateOf("p1").Should().NotBeNull().And.Subject.As<RetryState>().RunEndUtc.Should().BeNull();
        Minutes(1);
        _due.Should().ContainSingle();
    }

    [Fact]
    public void A_state_that_falls_due_over_a_day_after_its_run_is_dropped_instead_of_handed_out()
    {
        var plan = Plan(interval: 720);
        _planner.OnRunFinished(plan, Regular(2));
        Minutes(720);
        _due.Should().ContainSingle();
        Minutes(1);   // the attempt takes a minute: the next one is due a minute after a day
        _planner.OnRunFinished(plan, RetryLine(RunStatus.CompletedWithWarnings, open: 1));
        _changed.Clear();

        Minutes(719);
        _planner.StateOf("p1").Should().NotBeNull("it is not due yet");

        Minutes(1);
        _due.Should().ContainSingle("the second attempt is never handed out");
        _planner.StateOf("p1").Should().BeNull();
        File.Exists(FileOf("p1")).Should().BeFalse();
        _changed.Should().Equal("p1");
    }

    [Fact]
    public void A_corrupt_state_file_is_dropped_at_start()
    {
        _dir.WriteFile(Path.Combine("retries", "broken.json"), "{ not json");
        _dir.WriteFile(Path.Combine("retries", "empty.json"), "null");

        var start = () => _planner.Start(id => Plan(id), _ => null);

        start.Should().NotThrow();
        _planner.StateOf("broken").Should().BeNull();
        File.Exists(FileOf("broken")).Should().BeFalse();
        File.Exists(FileOf("empty")).Should().BeFalse();
    }

    [Fact]
    public void Turning_retries_off_or_moving_the_target_drops_the_state()
    {
        foreach (var id in new[] { "off", "target", "source", "renamed" })
            _planner.OnRunFinished(Plan(id), Regular(2, runId: "run-" + id));

        _planner.OnPlanSaved(Plan("off", enabled: false));
        _planner.OnPlanSaved(Plan("target", target: @"F:\elsewhere"));
        _planner.OnPlanSaved(Plan("source", source: @"D:\other"));
        var renamed = Plan("renamed");
        renamed.Name = "A new name";
        _planner.OnPlanSaved(renamed);

        _planner.StateOf("off").Should().BeNull();
        _planner.StateOf("target").Should().BeNull();
        _planner.StateOf("source").Should().BeNull();
        File.Exists(FileOf("target")).Should().BeFalse();
        _planner.StateOf("renamed").Should().NotBeNull();
    }

    [Fact]
    public void Lowering_the_attempts_caps_a_pending_state()
    {
        _planner.OnRunFinished(Plan(attempts: 3), Regular(2));

        _planner.OnPlanSaved(Plan(attempts: 5));
        _planner.StateOf("p1")!.Attempts.Should().Be(3);

        _planner.OnPlanSaved(Plan(attempts: 1));
        _planner.StateOf("p1")!.Attempts.Should().Be(1);

        _planner.OnPlanSaved(Plan(attempts: 5));
        _planner.StateOf("p1")!.Attempts.Should().Be(1);
        File.ReadAllText(FileOf("p1")).Should().Contain("\"attempts\": 1");
    }

    [Fact]
    public void Lowering_the_attempts_below_those_made_drops_the_state()
    {
        _planner.OnRunFinished(Plan(attempts: 3), Regular(2));
        Minutes(5);
        _planner.OnRunFinished(Plan(attempts: 3), RetryLine(RunStatus.CompletedWithWarnings, open: 1));

        _planner.OnPlanSaved(Plan(attempts: 1));

        _planner.StateOf("p1").Should().BeNull();
    }

    [Fact]
    public void The_interval_of_the_saved_plan_is_used_for_the_next_attempt()
    {
        _planner.OnRunFinished(Plan(interval: 5), Regular(2));
        Minutes(5);
        _due.Should().ContainSingle();

        _planner.OnPlanSaved(Plan(interval: 30));
        _planner.OnRunFinished(Plan(interval: 30), RetryLine(RunStatus.CompletedWithWarnings, open: 1));

        Minutes(29);
        _due.Should().ContainSingle();
        Minutes(1);
        _due.Should().HaveCount(2);
    }

    [Fact]
    public void Every_change_is_announced_with_the_plan_id()
    {
        _planner.OnRunFinished(Plan(), Regular(2));
        _changed.Should().Equal("p1");

        _planner.OnRunStarted("p1", isRetry: false);
        _changed.Should().Equal("p1", "p1");

        _planner.OnPlanDeleted("p1");   // nothing left to change
        _changed.Should().HaveCount(2);
    }

    [Fact]
    public void A_deleted_plan_loses_its_state()
    {
        _planner.OnRunFinished(Plan(), Regular(2));

        _planner.OnPlanDeleted("p1");

        _planner.StateOf("p1").Should().BeNull();
        File.Exists(FileOf("p1")).Should().BeFalse();
        Minutes(60);
        _due.Should().BeEmpty();
    }

    [Fact]
    public void The_state_handed_out_is_a_copy()
    {
        _planner.OnRunFinished(Plan(), Regular(2));

        _planner.StateOf("p1")!.Files.Clear();

        _planner.StateOf("p1")!.Files.Should().HaveCount(2);
    }
}
