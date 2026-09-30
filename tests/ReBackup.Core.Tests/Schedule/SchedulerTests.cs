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

        // Minute by minute: the timer is re-armed from the clock after each check, so one longer step holds only one check
        Minutes(1);
        Minutes(1);
        Minutes(1);

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

        // The clock is already at the end of the step when a check fires: 01:59:01 for the first check,
        // then 02:00:00.5, after the trigger and before the check at 02:00:01
        time.Advance(TimeSpan.FromSeconds(31));
        time.Advance(TimeSpan.FromSeconds(59.5));
        enqueued.Should().BeEmpty();

        // Re-publish the same plan (new plan and new trigger objects, same content)
        scheduler.UpdatePlans([Plan("p1", DailyAt("02:00"))]);

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

        // Advance to 02:00:32 (past the trigger; the check due at 01:59:01 sees 02:00:32)
        fakeTime.Advance(TimeSpan.FromSeconds(122));
        enqueued.Should().Equal(("p1", RunTrigger.Scheduled));

        // Set the clock back by 3 minutes (simulating a system clock adjustment): it reads 01:57:32
        offsetTime.Offset = TimeSpan.FromMinutes(-3);

        // Advance minute by minute, so the checks see 01:58:32, 01:59:32, 02:00:32 (the trigger time again) … 02:07:32:
        // the trigger must not be run again
        for (var i = 0; i < 10; i++)
            fakeTime.Advance(TimeSpan.FromMinutes(1));
        enqueued.Should().HaveCount(1);
    }

    [Fact]
    public void Pausing_after_Dispose_queues_nothing()
    {
        StartWith(Plan("p1", DailyAt("02:00")));

        _scheduler.Dispose();
        Minutes(5);   // 02:03:30: the 02:00 trigger has passed, but no check runs any more

        _scheduler.IsPaused = true;
        _enqueued.Should().BeEmpty();

        _scheduler.IsPaused = false;
        _enqueued.Should().BeEmpty();
    }

    [Fact]
    public void Pausing_before_Start_queues_nothing()
    {
        _scheduler.UpdatePlans([Plan("p1", DailyAt("02:00"))]);
        Minutes(5);   // 02:03:30: the 02:00 trigger has passed, but the scheduler has not started

        _scheduler.IsPaused = true;
        _enqueued.Should().BeEmpty();

        _scheduler.IsPaused = false;
        _enqueued.Should().BeEmpty();

        // Now start: the passed trigger stays skipped, the next day's one runs
        _scheduler.Start(_ => null);
        Minutes(5);
        _enqueued.Should().BeEmpty();
        _time.Advance(TimeSpan.FromDays(1));
        _enqueued.Should().Equal(("p1", RunTrigger.Scheduled));
    }

    [Fact]
    public void Pausing_drops_a_catch_up_covered_by_the_scheduled_run_it_queues()
    {
        StartWith(_ => Utc(9, 20, 10), Plan("p1", DailyAt("01:59")));

        // 01:59:00.5: the 01:59 trigger has passed, the first check (01:59:01) and the catch-up (01:59:30) are still ahead
        _time.Advance(TimeSpan.FromSeconds(30.5));
        _enqueued.Should().BeEmpty();

        _scheduler.IsPaused = true;
        _enqueued.Should().Equal(("p1", RunTrigger.Scheduled));

        _scheduler.IsPaused = false;
        Minutes(3);

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

        // Advance to 02:00:32 (past the trigger; the check due at 01:59:01 sees 02:00:32)
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
    public void Checks_realign_to_the_first_second_of_the_minute_after_the_clock_moved()
    {
        // FakeTimeProvider keeps a periodic timer aligned with its own clock, so the wall clock is moved instead: this is
        // what a sleep, a drifting timer or a time correction looks like to the scheduler (the timer does not move with it).
        var fakeTime = new FakeTimeProvider(Start);
        fakeTime.SetLocalTimeZone(TimeZoneInfo.Utc);
        var wallClock = new OffsetTimeProvider(fakeTime);
        using var scheduler = new Scheduler((_, _) => { }, wallClock);
        scheduler.UpdatePlans([Plan("p1", DailyAt("02:00"))]);
        scheduler.Start(_ => null);
        var checks = new List<DateTime>();
        scheduler.Changed += () => checks.Add(wallClock.GetUtcNow().UtcDateTime);

        fakeTime.Advance(TimeSpan.FromSeconds(31));   // the check at 01:59:01
        wallClock.Offset = TimeSpan.FromSeconds(90.5);   // the wall clock jumps from 01:59:01 to 02:00:31.5
        for (var i = 0; i < 300; i++)   // second by second, so every check sees (within a second) when it runs
            fakeTime.Advance(TimeSpan.FromSeconds(1));

        // The check already due a minute after the last one sees 02:01:31.5; from then on checks run at hh:mm:01(.5) again
        // (a periodic timer would stay at hh:mm:31.5)
        checks.Should().HaveCountGreaterThanOrEqualTo(5);
        checks[0].Should().Be(Utc(9, 30, 1, 59).AddSeconds(1));
        checks[1].Should().Be(Utc(9, 30, 2, 1).AddSeconds(31.5));
        checks.Skip(2).Should().OnlyContain(check => check.Second == 1,
            "every later check comes in the first seconds of a minute: {0}", string.Join(", ", checks.Select(c => c.ToString("HH:mm:ss.f"))));
    }

    /// <summary>
    /// Runs a scheduler in "W. Europe Standard Time" from <paramref name="startUtc"/> (a hh:mm:01 instant) minute by
    /// minute for <paramref name="hours"/> hours, so every check sees exactly hh:mm:01; returns when each run was queued.
    /// </summary>
    private static List<(DateTime QueuedUtc, RunTrigger Trigger)> RunThroughTheNight(DateTimeOffset startUtc, int hours,
        params ScheduleTrigger[] triggers)
    {
        var time = new FakeTimeProvider(startUtc);
        time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"));
        var queued = new List<(DateTime, RunTrigger)>();
        using var scheduler = new Scheduler((_, trigger) => queued.Add((time.GetUtcNow().UtcDateTime, trigger)), time);
        scheduler.UpdatePlans([Plan("p1", triggers)]);
        scheduler.Start(_ => null);

        for (var i = 0; i < hours * 60; i++)
            time.Advance(TimeSpan.FromMinutes(1));
        return queued;
    }

    [Fact]
    public void A_trigger_in_the_hour_that_repeats_when_summer_time_ends_runs_once()
    {
        // 2026-10-25: 03:00 CEST becomes 02:00 CET (01:00 UTC), so 02:30 local exists at 00:30 and at 01:30 UTC.
        var queued = RunThroughTheNight(new DateTimeOffset(2026, 10, 24, 22, 0, 1, TimeSpan.Zero), 6, DailyAt("02:30"));

        queued.Should().Equal((new DateTime(2026, 10, 25, 0, 30, 1, DateTimeKind.Utc), RunTrigger.Scheduled));
    }

    [Fact]
    public void Triggers_in_the_hour_skipped_when_summer_time_starts_run_once_after_it()
    {
        // 2026-03-29: 02:00 CET becomes 03:00 CEST (01:00 UTC); 02:30 does not exist and moves to 03:00, like the other trigger.
        var queued = RunThroughTheNight(new DateTimeOffset(2026, 3, 28, 22, 0, 1, TimeSpan.Zero), 6,
            DailyAt("02:30"), DailyAt("03:00"));

        queued.Should().Equal((new DateTime(2026, 3, 29, 1, 0, 1, DateTimeKind.Utc), RunTrigger.Scheduled));
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
