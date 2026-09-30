using FluentAssertions;
using ReBackup.Core.Schedule;

namespace ReBackup.Core.Tests.Schedule;

// Zone: CET (UTC+1), summer time CEST (UTC+2) 2026-03-29 02:00 → 03:00 and 2026-10-25 03:00 → 02:00; 2027-03-28.
// 2026-09-30 is a Wednesday, 2026-10-01 a Thursday.
public class ScheduleCalculatorTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static ScheduleTrigger Daily(string time) => new() { Type = TriggerType.Daily, Time = time };

    private static ScheduleTrigger Interval(int hours, string? from = null, string? to = null) =>
        new() { Type = TriggerType.Interval, EveryHours = hours, From = from, To = to };

    private static ScheduleTrigger Monthly(int day, string time) => new() { Type = TriggerType.Monthly, Day = day, Time = time };

    private static DateTime[] First(ScheduleTrigger trigger, DateTime afterUtc, int count) =>
        ScheduleCalculator.Occurrences(trigger, afterUtc, Zone).Take(count).ToArray();

    [Fact]
    public void Daily_runs_every_day_at_the_local_time()
    {
        First(Daily("02:00"), Utc(2026, 9, 30, 12), 2).Should().Equal(Utc(2026, 10, 1, 0), Utc(2026, 10, 2, 0));
    }

    [Fact]
    public void Occurrences_are_strictly_after_the_given_instant()
    {
        First(Daily("02:00"), Utc(2026, 10, 1, 0), 1).Should().Equal(Utc(2026, 10, 2, 0));
    }

    [Fact]
    public void Weekly_runs_on_the_chosen_days()
    {
        var trigger = new ScheduleTrigger { Type = TriggerType.Weekly, Days = ["Mon", "Wed"], Time = "18:00" };

        First(trigger, Utc(2026, 9, 30, 0), 3).Should().Equal(Utc(2026, 9, 30, 16), Utc(2026, 10, 5, 16), Utc(2026, 10, 7, 16));
    }

    [Fact]
    public void Monthly_uses_the_last_day_for_31_and_0_and_counts_back_for_negative_days()
    {
        First(Monthly(31, "03:00"), Utc(2027, 1, 31, 12), 2).Should().Equal(Utc(2027, 2, 28, 2), Utc(2027, 3, 31, 1));
        First(Monthly(0, "03:00"), Utc(2027, 2, 1, 0), 1).Should().Equal(Utc(2027, 2, 28, 2));
        First(Monthly(-1, "03:00"), Utc(2027, 2, 1, 0), 1).Should().Equal(Utc(2027, 2, 27, 2));
        First(Monthly(1, "03:00"), Utc(2026, 9, 30, 12), 1).Should().Equal(Utc(2026, 10, 1, 1));
    }

    [Fact]
    public void Interval_runs_within_the_daily_window_and_starts_again_the_next_day()
    {
        First(Interval(4, "08:00", "20:00"), Utc(2026, 9, 30, 5), 5).Should().Equal(
            Utc(2026, 9, 30, 6), Utc(2026, 9, 30, 10), Utc(2026, 9, 30, 14), Utc(2026, 9, 30, 18), Utc(2026, 10, 1, 6));
    }

    [Fact]
    public void Interval_without_bounds_covers_the_whole_day()
    {
        First(Interval(6), Utc(2026, 9, 30, 12), 4).Should().Equal(
            Utc(2026, 9, 30, 16), Utc(2026, 9, 30, 22), Utc(2026, 10, 1, 4), Utc(2026, 10, 1, 10));
    }

    [Fact]
    public void A_time_in_the_spring_gap_runs_at_the_first_valid_minute_after_it()
    {
        First(Daily("02:30"), Utc(2026, 3, 28, 12), 2).Should().Equal(Utc(2026, 3, 29, 1), Utc(2026, 3, 30, 0, 30));
    }

    [Fact]
    public void Runs_moved_out_of_the_spring_gap_are_not_doubled()
    {
        // 02:00 does not exist and becomes 03:00, which the trigger has anyway.
        First(Interval(1, "00:00", "05:00"), Utc(2026, 3, 28, 12), 6).Should().Equal(
            Utc(2026, 3, 28, 23), Utc(2026, 3, 29, 0), Utc(2026, 3, 29, 1), Utc(2026, 3, 29, 2), Utc(2026, 3, 29, 3),
            Utc(2026, 3, 29, 22));
    }

    [Fact]
    public void A_time_in_the_autumn_overlap_runs_once_at_the_earlier_instant()
    {
        First(Daily("02:30"), Utc(2026, 10, 24, 12), 2).Should().Equal(Utc(2026, 10, 25, 0, 30), Utc(2026, 10, 26, 1, 30));
        First(Interval(1, "00:00", "04:00"), Utc(2026, 10, 24, 12), 5).Should().Equal(
            Utc(2026, 10, 24, 22), Utc(2026, 10, 24, 23), Utc(2026, 10, 25, 0), Utc(2026, 10, 25, 2), Utc(2026, 10, 25, 3));
    }

    [Fact]
    public void NextRuns_merges_triggers_without_duplicates()
    {
        ScheduleTrigger[] triggers = [Daily("02:00"), new() { Type = TriggerType.Weekly, Days = ["Thu"], Time = "02:00" }];

        ScheduleCalculator.NextRuns(triggers, Utc(2026, 9, 30, 12), Zone).Take(3).Should().Equal(
            Utc(2026, 10, 1, 0), Utc(2026, 10, 2, 0), Utc(2026, 10, 3, 0));
        ScheduleCalculator.NextRuns([], Utc(2026, 9, 30, 12), Zone).Should().BeEmpty();
    }

    [Fact]
    public void LastDue_is_the_latest_trigger_between_the_last_run_and_now()
    {
        ScheduleTrigger[] daily = [Daily("02:00")];

        ScheduleCalculator.LastDue(daily, Utc(2026, 9, 28, 10), Utc(2026, 9, 30, 12), Zone).Should().Be(Utc(2026, 9, 30, 0));
        ScheduleCalculator.LastDue(daily, Utc(2026, 9, 30, 1), Utc(2026, 9, 30, 12), Zone).Should().BeNull();
        ScheduleCalculator.LastDue(daily, Utc(2026, 9, 29, 12), Utc(2026, 9, 30, 0), Zone).Should().Be(Utc(2026, 9, 30, 0),
            "a trigger exactly at now is due");
        ScheduleCalculator.LastDue(daily, Utc(2020, 1, 1, 0), Utc(2026, 9, 30, 12), Zone).Should().Be(Utc(2026, 9, 30, 0));
        ScheduleCalculator.LastDue([], Utc(2026, 9, 28, 10), Utc(2026, 9, 30, 12), Zone).Should().BeNull();
    }

    [Fact]
    public void LocalRunTimes_gives_wall_clock_times()
    {
        ScheduleCalculator.LocalRunTimes([Daily("02:00")], Utc(2026, 9, 30, 12), Zone).First()
            .Should().Be(new DateTime(2026, 10, 1, 2, 0, 0));
    }

    [Fact]
    public void An_invalid_trigger_is_rejected_at_the_call()
    {
        var weekly = new ScheduleTrigger { Type = TriggerType.Weekly, Time = "18:00" };

        var occurrences = () => ScheduleCalculator.Occurrences(weekly, Utc(2026, 9, 30, 0), Zone);
        var nextRuns = () => ScheduleCalculator.NextRuns([Daily("02:00"), weekly], Utc(2026, 9, 30, 0), Zone);

        occurrences.Should().Throw<ArgumentException>();
        nextRuns.Should().Throw<ArgumentException>();
    }
}
