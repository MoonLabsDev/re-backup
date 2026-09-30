using FluentAssertions;
using ReBackup.Core.Retention;

namespace ReBackup.Core.Tests.Retention;

public class RetentionSimulatorTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0);

    private static RetentionRule Rule(RetentionPeriod period, string? anchor, int keep) =>
        new() { Period = period, Anchor = anchor, Keep = keep };

    private static RetentionVersion Existing(int month, int day) =>
        new($"2026_{month:00}_{day:00}-02_00 P", new DateTime(2026, month, day, 2, 0, 0));

    private static IEnumerable<DateTime> DailyAt2() => RetentionSimulator.Every(new DateTime(2026, 9, 30, 2, 0, 0), TimeSpan.FromDays(1));

    [Fact]
    public void Reference_plan_with_a_daily_backup_settles_at_22_versions()
    {
        RetentionRule[] rules =
        [
            Rule(RetentionPeriod.Daily, null, 7),
            Rule(RetentionPeriod.Weekly, "Sunday", 4),
            Rule(RetentionPeriod.Monthly, "0", 12),
        ];

        var result = RetentionSimulator.Simulate([], rules, DailyAt2(), Now, averageVersionBytes: 1000);

        // 7 days + 4 Sundays (one of them within the 7 days) + 12 month ends, when none of those coincide.
        result.SteadyStateCount.Should().Be(22);
        result.EstimatedBytes.Should().Be(22_000);
        result.RunsSimulated.Should().Be(731);   // 2026-10-01 … 2028-09-30, 2028 is a leap year
        result.Truncated.Should().BeFalse();
        result.Horizon.Should().Be(new DateTime(2028, 9, 30, 12, 0, 0));

        // On 2028-09-30 (a Saturday and a month end) the month end is one of the 7 days: 7 + 3 + 11.
        result.Survivors.Should().HaveCount(21);
        result.Survivors.Should().OnlyContain(s => !s.Existing && s.Reasons.Count > 0);
        result.Survivors[^1].LocalTime.Should().Be(new DateTime(2028, 9, 30, 2, 0, 0));
        result.Survivors.Select(s => s.LocalTime).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Without_rules_every_version_stays()
    {
        var result = RetentionSimulator.Simulate([Existing(9, 28), Existing(9, 29)], [], DailyAt2().Take(6), Now, null);

        // DailyAt2 starts on 09-30 02:00, which is before Now and therefore ignored: 5 runs remain.
        result.RunsSimulated.Should().Be(5);
        result.Survivors.Should().HaveCount(7);
        result.SteadyStateCount.Should().Be(7);
        result.EstimatedBytes.Should().BeNull();
        result.Survivors.Count(s => s.Existing).Should().Be(2);
        result.Survivors[0].Reasons.Should().Equal(new KeepReason(-1, 0, "No rules"));
    }

    [Fact]
    public void Without_future_runs_nothing_is_deleted()
    {
        var result = RetentionSimulator.Simulate([Existing(9, 28), Existing(9, 29), Existing(9, 30)],
            [Rule(RetentionPeriod.Daily, null, 2)], [], Now, 500);

        result.RunsSimulated.Should().Be(0);
        result.Survivors.Should().HaveCount(3);
        result.Survivors.Should().OnlyContain(s => s.Existing);
        result.Survivors[0].Reasons.Should().BeEmpty("the next run would delete it");
        result.Survivors[2].Reasons.Should().Equal(new KeepReason(0, 1, "Daily #1"));
        result.SteadyStateCount.Should().Be(3);
        result.EstimatedBytes.Should().Be(1500);
    }

    [Fact]
    public void Existing_versions_are_deleted_by_the_simulated_runs()
    {
        var result = RetentionSimulator.Simulate([Existing(9, 28), Existing(9, 29), Existing(9, 30)],
            [Rule(RetentionPeriod.Daily, null, 2)], DailyAt2().Take(8), Now, null);

        result.RunsSimulated.Should().Be(7);
        result.Survivors.Select(s => s.LocalTime).Should().Equal(
            new DateTime(2026, 10, 6, 2, 0, 0), new DateTime(2026, 10, 7, 2, 0, 0));
        result.Survivors.Should().OnlyContain(s => !s.Existing);
        result.SteadyStateCount.Should().Be(2, "there is no run in the second year, so the final count is used");
    }

    [Fact]
    public void Runs_at_or_before_now_and_after_the_horizon_are_ignored()
    {
        DateTime[] runs = [Now.AddDays(-1), Now, Now.AddDays(1), Now.AddYears(3)];

        var result = RetentionSimulator.Simulate([], [Rule(RetentionPeriod.Daily, null, 5)], runs, Now, null);

        result.RunsSimulated.Should().Be(1);
        result.Survivors.Should().ContainSingle().Which.LocalTime.Should().Be(Now.AddDays(1));
    }

    [Fact]
    public void Stops_after_the_maximum_number_of_runs()
    {
        var everyMinute = RetentionSimulator.Every(Now.AddMinutes(1), TimeSpan.FromMinutes(1));

        var result = RetentionSimulator.Simulate([], [Rule(RetentionPeriod.Daily, null, 1)], everyMinute, Now, null);

        result.Truncated.Should().BeTrue();
        result.RunsSimulated.Should().Be(RetentionSimulator.MaxRuns);
        result.Survivors.Should().ContainSingle();
    }

    [Fact]
    public void Cancellation_stops_the_simulation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => RetentionSimulator.Simulate([], [Rule(RetentionPeriod.Daily, null, 1)], DailyAt2(), Now, null, cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Every_rejects_an_interval_that_does_not_advance()
    {
        var act = () => RetentionSimulator.Every(Now, TimeSpan.Zero).First();

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
