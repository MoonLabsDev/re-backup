using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Schedule;

namespace ReBackup.Core.Tests.Schedule;

// The clock starts at 2026-09-30 01:58:30 UTC, and local time is UTC. Checks run at hh:mm:01.
// FakeTimeProvider.Advance fires due timers with the clock already at the end of the step.
public class SchedulerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 1, 58, 30, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Start);
    private readonly List<(string PlanId, RunTrigger Trigger)> _enqueued = [];
    private readonly Scheduler _scheduler;

    public SchedulerTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _scheduler = new Scheduler((planId, trigger) => _enqueued.Add((planId, trigger)), _time);
    }

    public void Dispose() => _scheduler.Dispose();

    private static ScheduleTrigger DailyAt(string time) => new() { Type = TriggerType.Daily, Time = time };

    private static ScheduledPlan Plan(string id, params ScheduleTrigger[] triggers) => new(id, true, triggers);

    private static DateTime Utc(int month, int day, int hour, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    private void StartWith(params ScheduledPlan[] plans) => StartWith(_ => null, plans);

    private void StartWith(Func<string, DateTime?> lastRunStartUtc, params ScheduledPlan[] plans)
    {
        _scheduler.UpdatePlans(plans);
        _scheduler.Start(lastRunStartUtc);
    }

    private void Minutes(int count) => _time.Advance(TimeSpan.FromMinutes(count));

    [Fact]
    public void Queues_a_plan_once_when_its_trigger_time_has_come()
    {
        StartWith(Plan("p1", DailyAt("02:00")));

        Minutes(1);   // 01:59:30
        _enqueued.Should().BeEmpty();

        Minutes(1);   // 02:00:30
        _enqueued.Should().Equal(("p1", RunTrigger.Scheduled));

        Minutes(10);
        _enqueued.Should().HaveCount(1);

        _time.Advance(TimeSpan.FromDays(1));
        _enqueued.Should().Equal(("p1", RunTrigger.Scheduled), ("p1", RunTrigger.Scheduled));
    }

    [Fact]
    public void A_disabled_plan_is_not_queued()
    {
        StartWith(new ScheduledPlan("p1", false, [DailyAt("02:00")]));

        Minutes(5);

        _enqueued.Should().BeEmpty();
        _scheduler.NextRunUtc("p1").Should().BeNull();
    }

    [Fact]
    public void Triggers_that_pass_while_paused_are_skipped()
    {
        StartWith(Plan("p1", DailyAt("02:00")));
        _scheduler.IsPaused = true;

        Minutes(5);
        _scheduler.IsPaused = false;
        Minutes(5);
        _enqueued.Should().BeEmpty();

        _time.Advance(TimeSpan.FromDays(1));
        _enqueued.Should().Equal(("p1", RunTrigger.Scheduled));
    }

    [Fact]
    public void A_plan_that_missed_a_trigger_gets_one_catch_up_run_about_a_minute_after_start()
    {
        StartWith(_ => Utc(9, 20, 10), Plan("p1", DailyAt("12:00")));

        _time.Advance(TimeSpan.FromSeconds(31));   // the check at 01:59:01 comes before the catch-up time 01:59:30
        _enqueued.Should().BeEmpty();

        _time.Advance(TimeSpan.FromSeconds(60));   // 02:00:01, past the check at 02:00:01
        _enqueued.Should().Equal(("p1", RunTrigger.CatchUp));

        Minutes(60);
        _enqueued.Should().HaveCount(1);
    }

    [Fact]
    public void A_trigger_due_together_with_the_catch_up_is_covered_by_it()
    {
        StartWith(_ => Utc(9, 28, 10), Plan("p1", DailyAt("02:00")));

        Minutes(2);

        _enqueued.Should().Equal(("p1", RunTrigger.CatchUp));
    }

    [Fact]
    public void No_catch_up_without_a_missed_trigger_or_for_a_plan_that_never_ran()
    {
        var lastRuns = new Dictionary<string, DateTime?> { ["ran"] = Utc(9, 29, 13), ["never"] = null };
        StartWith(id => lastRuns[id], Plan("ran", DailyAt("12:00")), Plan("never", DailyAt("12:00")));

        Minutes(5);

        _enqueued.Should().BeEmpty();
    }

    [Fact]
    public void No_catch_up_for_disabled_plans_or_when_the_log_cannot_be_read()
    {
        StartWith(
            id => id == "broken" ? throw new IOException("log is locked") : Utc(9, 20, 10),
            new ScheduledPlan("off", false, [DailyAt("12:00")]),
            Plan("broken", DailyAt("12:00")),
            Plan("fine", DailyAt("12:00")));

        Minutes(5);

        _enqueued.Should().Equal(("fine", RunTrigger.CatchUp));
    }

    [Fact]
    public void A_paused_scheduler_drops_the_catch_up_runs()
    {
        StartWith(_ => Utc(9, 20, 10), Plan("p1", DailyAt("12:00")));
        _scheduler.IsPaused = true;

        Minutes(5);
        _scheduler.IsPaused = false;
        Minutes(5);

        _enqueued.Should().BeEmpty();
    }

    [Fact]
    public void A_plan_added_later_is_not_run_for_the_past()
    {
        StartWith();
        Minutes(3);   // 02:01:30

        _scheduler.UpdatePlans([Plan("p2", DailyAt("02:00"))]);
        Minutes(5);
        _enqueued.Should().BeEmpty();

        _time.Advance(TimeSpan.FromDays(1));
        _enqueued.Should().Equal(("p2", RunTrigger.Scheduled));
    }

    [Fact]
    public void A_removed_plan_is_no_longer_run()
    {
        StartWith(Plan("p1", DailyAt("02:00")));

        _scheduler.UpdatePlans([]);
        Minutes(5);

        _enqueued.Should().BeEmpty();
        _scheduler.NextRunUtc("p1").Should().BeNull();
    }

    [Fact]
    public void NextRunUtc_is_the_next_trigger_time()
    {
        StartWith(Plan("p1", DailyAt("02:00")), Plan("none"));

        _scheduler.NextRunUtc("p1").Should().Be(Utc(9, 30, 2));
        Minutes(3);
        _scheduler.NextRunUtc("p1").Should().Be(Utc(10, 1, 2));
        _scheduler.NextRunUtc("none").Should().BeNull("a plan without triggers only runs by hand");
        _scheduler.NextRunUtc("unknown").Should().BeNull();
    }

    [Fact]
    public void A_plan_with_an_invalid_trigger_does_not_disturb_the_others()
    {
        StartWith(Plan("bad", new ScheduleTrigger { Type = TriggerType.Weekly, Time = "02:00" }), Plan("good", DailyAt("02:00")));

        Minutes(3);

        _enqueued.Should().Equal(("good", RunTrigger.Scheduled));
        _scheduler.NextRunUtc("bad").Should().BeNull();
    }

    [Fact]
    public void A_failing_enqueue_does_not_stop_the_scheduler()
    {
        var calls = new List<string>();
        using var scheduler = new Scheduler((planId, _) =>
        {
            calls.Add(planId);
            throw new InvalidOperationException("queue is gone");
        }, _time);
        scheduler.UpdatePlans([Plan("p1", DailyAt("02:00")), Plan("p2", DailyAt("02:00"))]);
        scheduler.Start(_ => null);

        Minutes(3);
        _time.Advance(TimeSpan.FromDays(1));

        calls.Should().Equal("p1", "p2", "p1", "p2");
    }

    [Fact]
    public void Nothing_is_queued_after_Dispose()
    {
        StartWith(Plan("p1", DailyAt("02:00")));

        _scheduler.Dispose();
        Minutes(5);

        _enqueued.Should().BeEmpty();
    }

    [Fact]
    public void Changed_is_raised_after_every_check()
    {
        var changes = 0;
        _scheduler.Changed += () => changes++;
        StartWith(Plan("p1", DailyAt("02:00")));
        var afterStart = changes;

        Minutes(3);

        (changes - afterStart).Should().Be(3);
    }

    [Fact]
    public void Start_can_only_be_called_once()
    {
        StartWith(Plan("p1", DailyAt("02:00")));

        var again = () => _scheduler.Start(_ => null);

        again.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_trigger_between_start_and_the_catch_up_does_not_run_twice()
    {
        // Start at 01:58:30 with last run 09-20 10:00 and a trigger at 01:59
        StartWith(_ => Utc(9, 20, 10), Plan("p1", DailyAt("01:59")));

        // Advance to exactly 01:59:01 (the check runs after 31 seconds)
        _time.Advance(TimeSpan.FromSeconds(31));
        // The trigger at 01:59 is scheduled, so it runs; catch-up is removed
        _enqueued.Should().Equal(("p1", RunTrigger.Scheduled));

        // Advance to 02:05 (well past the catch-up time of 01:59:30)
        _time.Advance(TimeSpan.FromSeconds(4 * 60 + 29));
        // Still only one run queued (the scheduled one; catch-up is not queued)
        _enqueued.Should().HaveCount(1);
    }

    [Fact]
    public void Dispose_from_a_Changed_handler_does_not_throw()
    {
        StartWith(Plan("p1", DailyAt("02:00")));

        // Add a handler that disposes the scheduler
        _scheduler.Changed += () => _scheduler.Dispose();

        // Advancing time should not throw even though a handler disposes the scheduler
        _time.Advance(TimeSpan.FromSeconds(31));

        // After dispose, nothing more is queued
        _enqueued.Clear();
        _time.Advance(TimeSpan.FromMinutes(1));
        _enqueued.Should().BeEmpty();
    }

    [Fact]
    public void Pausing_right_after_a_trigger_still_runs_it()
    {
        StartWith(Plan("p1", DailyAt("02:00")));

        // Advance to 01:59:01 (the check runs)
        _time.Advance(TimeSpan.FromSeconds(31));
        _enqueued.Should().BeEmpty();

        // Advance to 02:00:00.5 (after the trigger, before the next check at 02:00:01)
        _time.Advance(TimeSpan.FromSeconds(59.5));

        // Pause immediately - should queue the 02:00 trigger that just passed
        _scheduler.IsPaused = true;
        _enqueued.Should().Equal(("p1", RunTrigger.Scheduled));

        // Resume and advance - no more runs should be queued
        _scheduler.IsPaused = false;
        _time.Advance(TimeSpan.FromMinutes(5));

        _enqueued.Should().HaveCount(1);
    }

    [Fact]
    public void Resaving_a_plan_with_changed_triggers_starts_it_from_now()
    {
        StartWith(Plan("p1", DailyAt("12:00")));

        // Advance to 01:59:01 (the check runs)
        _time.Advance(TimeSpan.FromSeconds(31));

        // Advance to 02:00:00.5 (before the next check at 02:00:01)
        _time.Advance(TimeSpan.FromSeconds(59.5));

        // Update plan with new trigger Daily 02:00 (which just passed)
        _scheduler.UpdatePlans([Plan("p1", DailyAt("02:00"))]);

        // Advance past the next check at 02:00:01 - the new trigger is in the past from update time
        _time.Advance(TimeSpan.FromSeconds(0.5));
        _enqueued.Should().BeEmpty();
    }

    [Fact]
    public void Resaving_an_unchanged_plan_keeps_a_trigger_that_just_passed()
    {
        var time = new FakeTimeProvider(Start);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var enqueued = new List<(string, RunTrigger)>();
        using var scheduler = new Scheduler((id, trigger) => enqueued.Add((id, trigger)), time);

        var plan = Plan("p1", DailyAt("02:00"));
        scheduler.UpdatePlans([plan]);
        scheduler.Start(_ => null);

        // Advance to 02:00:00.5
        time.Advance(TimeSpan.FromSeconds(90.5));

        // Re-publish the same plan (new object, same content)
        scheduler.UpdatePlans([new ScheduledPlan("p1", true, plan.Triggers)]);

        // Advance past the next check - trigger should still be queued once
        time.Advance(TimeSpan.FromSeconds(1));
        enqueued.Should().Equal(("p1", RunTrigger.Scheduled));
    }

    [Fact]
    public void A_clock_set_back_does_not_run_triggers_twice()
    {
        var fakeTime = new FakeTimeProvider(Start);
        fakeTime.SetLocalTimeZone(TimeZoneInfo.Utc);
        var offsetTime = new OffsetTimeProvider(fakeTime);
        var enqueued = new List<(string, RunTrigger)>();
        using var scheduler = new Scheduler((id, trigger) => enqueued.Add((id, trigger)), offsetTime);

        scheduler.UpdatePlans([Plan("p1", DailyAt("02:00"))]);
        scheduler.Start(_ => null);

        // Advance to 02:00:01 (past the trigger)
        fakeTime.Advance(TimeSpan.FromSeconds(122));
        enqueued.Should().Equal(("p1", RunTrigger.Scheduled));

        // Set the clock back by 3 minutes (simulating a system clock adjustment)
        offsetTime.Offset = TimeSpan.FromMinutes(-3);

        // Advance 10 minutes - the trigger should not be run again
        fakeTime.Advance(TimeSpan.FromMinutes(10));
        enqueued.Should().HaveCount(1);
    }

    [Fact]
    public void Pausing_after_Dispose_queues_nothing()
    {
        StartWith(Plan("p1", DailyAt("02:00")));

        _scheduler.Dispose();

        // Pausing after dispose should not queue anything
        _scheduler.IsPaused = true;
        _enqueued.Should().BeEmpty();

        _scheduler.IsPaused = false;
        _enqueued.Should().BeEmpty();
    }

    [Fact]
    public void Pausing_before_Start_queues_nothing()
    {
        _scheduler.UpdatePlans([Plan("p1", DailyAt("02:00"))]);

        // Pausing before Start should not queue anything
        _scheduler.IsPaused = true;
        _enqueued.Should().BeEmpty();

        _scheduler.IsPaused = false;
        _enqueued.Should().BeEmpty();

        // Now start and verify normal operation
        _scheduler.Start(_ => null);
        _time.Advance(TimeSpan.FromSeconds(122));
        _enqueued.Should().Equal(("p1", RunTrigger.Scheduled));
    }

    [Fact]
    public void Pausing_after_the_clock_was_set_back_does_not_run_a_trigger_twice()
    {
        var fakeTime = new FakeTimeProvider(Start);
        fakeTime.SetLocalTimeZone(TimeZoneInfo.Utc);
        var offsetTime = new OffsetTimeProvider(fakeTime);
        var enqueued = new List<(string, RunTrigger)>();
        using var scheduler = new Scheduler((id, trigger) => enqueued.Add((id, trigger)), offsetTime);

        scheduler.UpdatePlans([Plan("p1", DailyAt("02:00"))]);
        scheduler.Start(_ => null);

        // Advance to 02:00:01 (past the trigger)
        fakeTime.Advance(TimeSpan.FromSeconds(122));
        enqueued.Should().Equal(("p1", RunTrigger.Scheduled));

        // Set the clock back by 3 minutes
        offsetTime.Offset = TimeSpan.FromMinutes(-3);

        // Pause and resume
        scheduler.IsPaused = true;
        scheduler.IsPaused = false;

        // Advance 10 minutes - trigger should not run again
        fakeTime.Advance(TimeSpan.FromMinutes(10));
        enqueued.Should().HaveCount(1);
    }

    [Fact]
    public void Pausing_when_a_catch_up_is_due_still_runs_it()
    {
        StartWith(_ => Utc(9, 20, 10), Plan("p1", DailyAt("12:00")));

        // Advance to 01:59:01 (first check, catch-up is pending but not due yet)
        _time.Advance(TimeSpan.FromSeconds(31));

        // Advance to 01:59:46 (45 seconds later, no check between 01:59:01 and 01:59:46)
        // Next check would be at 02:00:01
        _time.Advance(TimeSpan.FromSeconds(45));

        // Pause - the catch-up at 01:59:30 is due but hasn't been picked up by a check yet
        _scheduler.IsPaused = true;
        _enqueued.Should().Equal(("p1", RunTrigger.CatchUp));
    }
}

/// <summary>
/// TimeProvider that applies an offset to the underlying FakeTimeProvider's clock.
/// Used to simulate system clock adjustments without requiring FakeTimeProvider to support SetUtcNow backwards.
/// </summary>
internal sealed class OffsetTimeProvider : TimeProvider
{
    private readonly FakeTimeProvider _inner;
    public TimeSpan Offset { get; set; } = TimeSpan.Zero;

    public OffsetTimeProvider(FakeTimeProvider inner)
    {
        _inner = inner;
    }

    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow() + Offset;

    public override long GetTimestamp() => _inner.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => _inner.CreateTimer(callback, state, dueTime, period);
}
