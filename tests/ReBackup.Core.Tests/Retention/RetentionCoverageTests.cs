using System.Globalization;
using FluentAssertions;
using ReBackup.Shared.Retention;

namespace ReBackup.Core.Tests.Retention;

// Today is Tuesday 2026-10-13. Sundays: 10-04, 10-11.
public class RetentionCoverageTests
{
    private static readonly DateOnly Today = new(2026, 10, 13);

    private static CoverageVersion V(string date, CoverageQuality quality = CoverageQuality.Ok, bool counted = true,
        string time = "02:00", string? id = null)
    {
        var local = DateTime.ParseExact($"{date} {time}", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return new CoverageVersion(id ?? $"v{date}T{time}", local, quality, counted, null);
    }

    private static RetentionRule Rule(RetentionPeriod period, string? anchor, int keep) =>
        new() { Period = period, Anchor = anchor, Keep = keep };

    private static CoverageVersion[] Days(DateOnly from, DateOnly to)
    {
        var list = new List<CoverageVersion>();
        for (var d = from; d <= to; d = d.AddDays(1))
            list.Add(V(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        return list.ToArray();
    }

    private static CoverageLane Single(IReadOnlyList<CoverageVersion> versions, RetentionRule rule) =>
        RetentionCoverage.Compute(versions, [rule], Today).Single();

    [Fact]
    public void No_rules_gives_no_lanes() =>
        RetentionCoverage.Compute([V("2026-10-12")], [], Today).Should().BeEmpty();

    [Fact]
    public void Daily_keep_7_with_all_days_is_seven_present_slots()
    {
        var lane = Single(Days(new DateOnly(2026, 10, 7), Today), Rule(RetentionPeriod.Daily, null, 7));

        lane.RuleIndex.Should().Be(0);
        lane.Slots.Should().HaveCount(7);
        lane.Slots.Should().OnlyContain(s => s.State == SlotState.Present && s.Version != null);
        lane.Slots[0].Start.Should().Be(new DateOnly(2026, 10, 7));
        lane.Slots[^1].Start.Should().Be(Today);
        lane.Slots[^1].End.Should().Be(Today.AddDays(1));
    }

    [Fact]
    public void Daily_gap_is_a_missing_slot_and_lane_reaches_to_the_oldest_kept_version()
    {
        var versions = Days(new DateOnly(2026, 10, 6), Today).Where(v => v.LocalTime.Day != 10).ToArray();
        var lane = Single(versions, Rule(RetentionPeriod.Daily, null, 7));

        // 7 versions exist (6th..13th without the 10th) and all are kept, so the lane reaches back to the 6th.
        lane.Slots.Should().HaveCount(8);
        lane.Slots.Single(s => s.State == SlotState.Missing).Start.Should().Be(new DateOnly(2026, 10, 10));
        lane.Slots[0].Start.Should().Be(new DateOnly(2026, 10, 6));
    }

    [Fact]
    public void Gaps_extend_the_lane_to_the_oldest_kept_version()
    {
        var versions = new[] { V("2026-09-01"), V("2026-09-20"), V("2026-10-10"), V("2026-10-13") };
        var lane = Single(versions, Rule(RetentionPeriod.Daily, null, 4));

        lane.Slots[0].Start.Should().Be(new DateOnly(2026, 9, 1));
        lane.Slots[0].State.Should().Be(SlotState.Present);
        lane.Slots.Should().HaveCount(43);
        lane.Slots.Count(s => s.State == SlotState.Present).Should().Be(4);
        lane.Slots.Count(s => s.State == SlotState.Missing).Should().Be(39);
        lane.Slots.Should().NotContain(s => s.State == SlotState.BeforeFirst);
    }

    [Fact]
    public void Weekly_sunday_slots_start_on_sundays_and_end_on_the_next_sunday()
    {
        var versions = new[] { V("2026-09-20"), V("2026-09-27"), V("2026-10-04"), V("2026-10-11") };
        var lane = Single(versions, Rule(RetentionPeriod.Weekly, "Sun", 4));

        lane.Slots.Should().HaveCount(4);
        lane.Slots.Should().OnlyContain(s => s.Start.DayOfWeek == DayOfWeek.Sunday && s.End == s.Start.AddDays(7));
        lane.Slots[^1].Start.Should().Be(new DateOnly(2026, 10, 11));
        lane.Slots.Should().OnlyContain(s => s.State == SlotState.Present);
    }

    [Fact]
    public void Monthly_31_has_one_slot_per_month_through_february()
    {
        var rule = Rule(RetentionPeriod.Monthly, "31", 12);
        var lane = Single([V("2025-11-30")], rule);

        // Today 2026-10-13 is in the slot starting 2026-09-30; 12 slots back is 2025-10-31.
        lane.Slots.Select(s => s.Start).Should().Equal(
            new DateOnly(2025, 10, 31), new DateOnly(2025, 11, 30), new DateOnly(2025, 12, 31),
            new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 31),
            new DateOnly(2026, 4, 30), new DateOnly(2026, 5, 31), new DateOnly(2026, 6, 30),
            new DateOnly(2026, 7, 31), new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 30));
        for (var i = 0; i < lane.Slots.Count - 1; i++)
            lane.Slots[i].End.Should().Be(lane.Slots[i + 1].Start);
        lane.Slots[0].State.Should().Be(SlotState.BeforeFirst);
        lane.Slots[1].State.Should().Be(SlotState.Present);
        lane.Slots[^1].End.Should().Be(new DateOnly(2026, 10, 31));
    }

    [Fact]
    public void Monthly_0_and_minus_2_anchor_on_the_end_of_the_month()
    {
        var zero = Single([V("2026-08-31")], Rule(RetentionPeriod.Monthly, "0", 3));
        zero.Slots.Select(s => s.Start).Should().Equal(
            new DateOnly(2026, 7, 31), new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 30));

        var minus2 = Single([V("2026-08-29")], Rule(RetentionPeriod.Monthly, "-2", 3));
        minus2.Slots.Select(s => s.Start).Should().Equal(
            new DateOnly(2026, 7, 29), new DateOnly(2026, 8, 29), new DateOnly(2026, 9, 28));
        minus2.Slots[1].State.Should().Be(SlotState.Present);
    }

    [Fact]
    public void Yearly_0229_in_non_leap_years()
    {
        var lane = Single([V("2025-02-28")], Rule(RetentionPeriod.Yearly, "02-29", 4));

        lane.Slots.Select(s => s.Start).Should().Equal(
            new DateOnly(2023, 2, 28), new DateOnly(2024, 2, 29), new DateOnly(2025, 2, 28), new DateOnly(2026, 2, 28));
        lane.Slots[0].State.Should().Be(SlotState.BeforeFirst);
        lane.Slots[1].State.Should().Be(SlotState.BeforeFirst);
        lane.Slots[2].State.Should().Be(SlotState.Present);
        lane.Slots[3].State.Should().Be(SlotState.Pending);
        lane.Slots[3].End.Should().Be(new DateOnly(2027, 2, 28));
    }

    [Fact]
    public void Slots_before_the_oldest_version_are_BeforeFirst()
    {
        var lane = Single([V("2026-10-11"), V("2026-10-12")], Rule(RetentionPeriod.Daily, null, 5));

        lane.Slots.Select(s => s.State).Should().Equal(
            SlotState.BeforeFirst, SlotState.BeforeFirst, SlotState.Present, SlotState.Present, SlotState.Pending);
    }

    [Fact]
    public void No_versions_is_all_BeforeFirst_except_the_pending_current_slot()
    {
        var lane = Single([], Rule(RetentionPeriod.Daily, null, 3));

        lane.Slots.Select(s => s.State).Should().Equal(SlotState.BeforeFirst, SlotState.BeforeFirst, SlotState.Pending);
        lane.Slots.Should().OnlyContain(s => s.Version == null);
    }

    [Fact]
    public void Current_slot_is_pending_without_a_version_and_present_with_one()
    {
        var rule = Rule(RetentionPeriod.Daily, null, 2);

        Single([V("2026-10-12")], rule).Slots[^1].State.Should().Be(SlotState.Pending);
        Single([V("2026-10-12"), V("2026-10-13")], rule).Slots[^1].State.Should().Be(SlotState.Present);
    }

    [Fact]
    public void Warning_quality_is_PresentWithWarnings()
    {
        var lane = Single([V("2026-10-12", CoverageQuality.Warning), V("2026-10-13")], Rule(RetentionPeriod.Daily, null, 2));

        lane.Slots[0].State.Should().Be(SlotState.PresentWithWarnings);
        lane.Slots[0].Version!.Quality.Should().Be(CoverageQuality.Warning);
        lane.Slots[1].State.Should().Be(SlotState.Present);
    }

    [Fact]
    public void A_non_counted_version_alone_in_a_slot_is_PresentWithWarnings_and_ignored_by_the_engine()
    {
        var incomplete = V("2026-10-12", CoverageQuality.Warning, counted: false);
        var lane = Single([V("2026-10-11"), incomplete, V("2026-10-13")], Rule(RetentionPeriod.Daily, null, 3));

        lane.Slots.Select(s => s.State).Should().Equal(
            SlotState.Present, SlotState.PresentWithWarnings, SlotState.Present);
        lane.Slots[1].Version.Should().Be(incomplete);

        // Not counted: it does not use up one of the rule's slots (calendar span of 2 slots still applies).
        var keep2 = Single([V("2026-10-11"), incomplete, V("2026-10-13")], Rule(RetentionPeriod.Daily, null, 2));
        keep2.Slots.Select(s => s.Start).Should().Equal(new DateOnly(2026, 10, 11), new DateOnly(2026, 10, 12), Today);
    }

    [Fact]
    public void A_counted_version_wins_over_a_non_counted_one_in_the_same_slot()
    {
        var counted = V("2026-10-13", time: "01:00", id: "counted");
        var lane = Single([V("2026-10-13", CoverageQuality.Warning, counted: false, time: "05:00"), counted],
            Rule(RetentionPeriod.Daily, null, 1));

        lane.Slots.Single().Version.Should().Be(counted);
        lane.Slots.Single().State.Should().Be(SlotState.Present);
    }

    [Fact]
    public void The_slot_version_is_the_one_the_engine_keeps_for_that_rule()
    {
        // Weekly(Sun): slot 2026-10-04..10-11 holds Sunday 10-04 and Wednesday 10-07; the anchor-day one is kept.
        var sunday = V("2026-10-04", id: "sunday");
        var wednesday = V("2026-10-07", id: "wednesday");
        var lane = Single([sunday, wednesday], Rule(RetentionPeriod.Weekly, "Sun", 2));
        lane.Slots.Single(s => s.Start == new DateOnly(2026, 10, 4)).Version!.Id.Should().Be("sunday");

        // Without a version on the anchor day, the first version of the slot stays.
        var thursday = V("2026-10-08", id: "thursday");
        var lane2 = Single([wednesday, thursday], Rule(RetentionPeriod.Weekly, "Sun", 2));
        lane2.Slots.Single(s => s.Start == new DateOnly(2026, 10, 4)).Version!.Id.Should().Be("wednesday");
    }

    [Fact]
    public void Each_rule_gets_its_own_lane_with_its_index()
    {
        var lanes = RetentionCoverage.Compute([V("2026-10-13")],
            [Rule(RetentionPeriod.Daily, null, 1), Rule(RetentionPeriod.Weekly, "Sun", 1)], Today);

        lanes.Select(l => l.RuleIndex).Should().Equal(0, 1);
        lanes[1].Rule.Period.Should().Be(RetentionPeriod.Weekly);
    }
}
