using System.Globalization;
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Retention;

namespace ReBackup.Core.Tests.Retention;

// September 2026: the 6th, 13th, 20th and 27th are Sundays; the 30th is a Wednesday.
public class RetentionEngineTests
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm";

    private static RetentionVersion V(string localTime)
    {
        var time = DateTime.ParseExact(localTime, TimeFormat, CultureInfo.InvariantCulture);
        return new RetentionVersion(VersionName.Format(time, "P"), time);
    }

    private static RetentionVersion[] Versions(params string[] localTimes) => localTimes.Select(V).ToArray();

    private static RetentionRule Rule(RetentionPeriod period, string? anchor, int keep) =>
        new() { Period = period, Anchor = anchor, Keep = keep };

    private static RetentionRule Daily(int keep) => Rule(RetentionPeriod.Daily, null, keep);
    private static RetentionRule Weekly(string day, int keep) => Rule(RetentionPeriod.Weekly, day, keep);
    private static RetentionRule Monthly(int day, int keep) =>
        Rule(RetentionPeriod.Monthly, day.ToString(CultureInfo.InvariantCulture), keep);
    private static RetentionRule Yearly(string date, int keep) => Rule(RetentionPeriod.Yearly, date, keep);

    private static string[] Times(IEnumerable<RetentionDecision> decisions) =>
        decisions.Select(d => d.Version.LocalTime.ToString(TimeFormat, CultureInfo.InvariantCulture)).ToArray();

    private static string[] Kept(IReadOnlyList<RetentionDecision> decisions) => Times(decisions.Where(d => d.Keep));

    private static string[] Deleted(IReadOnlyList<RetentionDecision> decisions) => Times(decisions.Where(d => !d.Keep));

    private static string[] Labels(IReadOnlyList<RetentionDecision> decisions, string localTime) =>
        decisions.Single(d => d.Version == V(localTime)).Reasons.Select(r => r.Label).ToArray();

    [Fact]
    public void No_versions_give_no_decisions()
    {
        RetentionEngine.Evaluate([], [Daily(7)]).Should().BeEmpty();
    }

    [Fact]
    public void Without_rules_everything_is_kept()
    {
        var decisions = RetentionEngine.Evaluate(Versions("2026-09-01 02:00", "2026-09-30 02:00"), []);

        Kept(decisions).Should().Equal("2026-09-01 02:00", "2026-09-30 02:00");
        decisions[0].Reasons.Should().Equal(new KeepReason(-1, 0, "No rules"));
        decisions[1].Reasons.Should().Equal(new KeepReason(-1, 0, "No rules"));
    }

    [Fact]
    public void Daily_keeps_the_last_version_of_each_of_the_most_recent_days()
    {
        var versions = Versions("2026-09-26 10:00", "2026-09-27 08:00", "2026-09-27 20:00", "2026-09-28 09:00",
            "2026-09-29 09:00", "2026-09-30 09:00");

        var three = RetentionEngine.Evaluate(versions, [Daily(3)]);
        Kept(three).Should().Equal("2026-09-28 09:00", "2026-09-29 09:00", "2026-09-30 09:00");
        Deleted(three).Should().Equal("2026-09-26 10:00", "2026-09-27 08:00", "2026-09-27 20:00");
        Labels(three, "2026-09-30 09:00").Should().Equal("Daily #1");
        Labels(three, "2026-09-28 09:00").Should().Equal("Daily #3");

        var four = RetentionEngine.Evaluate(versions, [Daily(4)]);
        Kept(four).Should().Equal("2026-09-27 20:00", "2026-09-28 09:00", "2026-09-29 09:00", "2026-09-30 09:00");
    }

    [Fact]
    public void Days_without_a_version_do_not_use_up_the_count()
    {
        var decisions = RetentionEngine.Evaluate(Versions("2026-06-01 02:00", "2026-09-30 02:00"), [Daily(2)]);

        Kept(decisions).Should().Equal("2026-06-01 02:00", "2026-09-30 02:00");
        Labels(decisions, "2026-06-01 02:00").Should().Equal("Daily #2");
    }

    [Fact]
    public void Weekly_keeps_the_last_version_of_the_anchor_day()
    {
        var versions = Versions("2026-09-20 02:00", "2026-09-20 18:00", "2026-09-21 02:00", "2026-09-27 02:00",
            "2026-09-30 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Weekly("Sunday", 2)]);

        Kept(decisions).Should().Equal("2026-09-20 18:00", "2026-09-27 02:00", "2026-09-30 02:00");
        Deleted(decisions).Should().Equal("2026-09-20 02:00", "2026-09-21 02:00");
        Labels(decisions, "2026-09-27 02:00").Should().Equal("Weekly(Sun) #1");
        Labels(decisions, "2026-09-20 18:00").Should().Equal("Weekly(Sun) #2");
        Labels(decisions, "2026-09-30 02:00").Should().Equal("Newest");
    }

    [Fact]
    public void A_slot_without_a_version_on_the_anchor_day_is_represented_by_the_first_version_after_it()
    {
        // No Sunday backups: Tuesday the 22nd stands for the week of the 20th, Monday the 28th for the week of the 27th.
        var versions = Versions("2026-09-22 02:00", "2026-09-24 02:00", "2026-09-28 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Weekly("Sunday", 2)]);

        Kept(decisions).Should().Equal("2026-09-22 02:00", "2026-09-28 02:00");
        Deleted(decisions).Should().Equal("2026-09-24 02:00");
        Labels(decisions, "2026-09-22 02:00").Should().Equal("Weekly(Sun) #2");
        Labels(decisions, "2026-09-28 02:00").Should().Equal("Weekly(Sun) #1");
    }

    [Fact]
    public void Monthly_on_day_1_keeps_the_last_version_of_that_day()
    {
        var versions = Versions("2026-08-01 02:00", "2026-08-15 02:00", "2026-09-01 10:00", "2026-09-01 20:00",
            "2026-09-10 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Monthly(1, 2)]);

        Kept(decisions).Should().Equal("2026-08-01 02:00", "2026-09-01 20:00", "2026-09-10 02:00");
        Deleted(decisions).Should().Equal("2026-08-15 02:00", "2026-09-01 10:00");
        Labels(decisions, "2026-09-01 20:00").Should().Equal("Monthly(1) #1");
        Labels(decisions, "2026-08-01 02:00").Should().Equal("Monthly(1) #2");
        Labels(decisions, "2026-09-10 02:00").Should().Equal("Newest");
    }

    [Fact]
    public void Monthly_on_day_31_uses_the_last_day_of_shorter_months()
    {
        var versions = Versions("2026-01-31 02:00", "2026-02-27 02:00", "2026-02-28 02:00", "2026-03-30 02:00",
            "2026-03-31 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Monthly(31, 3)]);

        Kept(decisions).Should().Equal("2026-01-31 02:00", "2026-02-28 02:00", "2026-03-31 02:00");
        Deleted(decisions).Should().Equal("2026-02-27 02:00", "2026-03-30 02:00");
        Labels(decisions, "2026-02-28 02:00").Should().Equal("Monthly(31) #2");
    }

    [Fact]
    public void Monthly_anchor_0_is_the_last_day_of_the_month()
    {
        var versions = Versions("2026-08-30 02:00", "2026-08-31 02:00", "2026-09-29 02:00", "2026-09-30 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Monthly(0, 2)]);

        Kept(decisions).Should().Equal("2026-08-31 02:00", "2026-09-30 02:00");
        Deleted(decisions).Should().Equal("2026-08-30 02:00", "2026-09-29 02:00");
        Labels(decisions, "2026-09-30 02:00").Should().Equal("Monthly(0) #1");
    }

    [Fact]
    public void Monthly_anchor_minus_1_is_the_day_before_the_last_day()
    {
        var versions = Versions("2026-09-28 02:00", "2026-09-29 02:00", "2026-09-30 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Monthly(-1, 1)]);

        Kept(decisions).Should().Equal("2026-09-29 02:00", "2026-09-30 02:00");
        Deleted(decisions).Should().Equal("2026-09-28 02:00");
        Labels(decisions, "2026-09-29 02:00").Should().Equal("Monthly(-1) #1");
        Labels(decisions, "2026-09-30 02:00").Should().Equal("Newest");
    }

    [Fact]
    public void Yearly_falls_through_to_the_first_version_after_the_anchor_date()
    {
        var versions = Versions("2025-01-01 02:00", "2025-06-01 02:00", "2026-01-03 02:00", "2026-09-30 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Yearly("01-01", 5)]);

        Kept(decisions).Should().Equal("2025-01-01 02:00", "2026-01-03 02:00", "2026-09-30 02:00");
        Deleted(decisions).Should().Equal("2025-06-01 02:00");
        Labels(decisions, "2026-01-03 02:00").Should().Equal("Yearly(01-01) #1");
        Labels(decisions, "2025-01-01 02:00").Should().Equal("Yearly(01-01) #2");
    }

    [Fact]
    public void A_version_kept_by_several_rules_lists_every_reason_in_rule_order()
    {
        // Saturday the 26th, Sunday the 27th, Monday the 28th.
        var versions = Versions("2026-09-26 02:00", "2026-09-27 02:00", "2026-09-28 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Daily(2), Weekly("Sunday", 2)]);

        Deleted(decisions).Should().BeEmpty();
        decisions.Single(d => d.Version == V("2026-09-27 02:00")).Reasons.Should().Equal(
            new KeepReason(0, 2, "Daily #2"), new KeepReason(1, 1, "Weekly(Sun) #1"));
        Labels(decisions, "2026-09-26 02:00").Should().Equal("Weekly(Sun) #2");
        Labels(decisions, "2026-09-28 02:00").Should().Equal("Daily #1");
    }

    [Fact]
    public void Two_rules_of_the_same_period_are_combined()
    {
        var versions = Versions("2026-09-01 02:00", "2026-09-05 02:00", "2026-09-14 02:00", "2026-09-20 02:00",
            "2026-09-30 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Monthly(1, 1), Monthly(14, 1)]);

        Kept(decisions).Should().Equal("2026-09-01 02:00", "2026-09-14 02:00", "2026-09-30 02:00");
        Deleted(decisions).Should().Equal("2026-09-05 02:00", "2026-09-20 02:00");
        Labels(decisions, "2026-09-01 02:00").Should().Equal("Monthly(1) #1");
        Labels(decisions, "2026-09-14 02:00").Should().Equal("Monthly(14) #1");
    }

    [Fact]
    public void The_newest_version_is_not_marked_Newest_when_a_rule_keeps_it()
    {
        var decisions = RetentionEngine.Evaluate(Versions("2026-09-29 02:00", "2026-09-30 02:00"), [Daily(1)]);

        Kept(decisions).Should().Equal("2026-09-30 02:00");
        Labels(decisions, "2026-09-30 02:00").Should().Equal("Daily #1");
    }

    [Fact]
    public void Decisions_are_ordered_oldest_first_whatever_the_input_order()
    {
        var decisions = RetentionEngine.Evaluate(Versions("2026-09-30 02:00", "2026-09-01 02:00", "2026-09-15 02:00"), [Daily(9)]);

        Times(decisions).Should().Equal("2026-09-01 02:00", "2026-09-15 02:00", "2026-09-30 02:00");
    }

    [Fact]
    public void An_invalid_rule_is_rejected_with_its_position()
    {
        var act = () => RetentionEngine.Evaluate(Versions("2026-09-30 02:00"), [Daily(7), Weekly("Someday", 4)]);

        act.Should().Throw<ArgumentException>().WithMessage("Retention rule 2: the anchor must be a weekday*");
    }
}
