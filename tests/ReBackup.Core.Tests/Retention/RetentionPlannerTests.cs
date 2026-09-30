using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Retention;

namespace ReBackup.Core.Tests.Retention;

public class RetentionPlannerTests
{
    private static readonly RetentionRule[] KeepTwoDays = [new() { Period = RetentionPeriod.Daily, Keep = 2 }];

    private static VersionInfo Version(int day, VersionOwnership ownership = VersionOwnership.Owned)
    {
        var name = $"2026_09_{day:00}-02_00 Projects";
        return new VersionInfo(name, @"T:\" + name, new DateTime(2026, 9, day, 2, 0, 0), ownership, 1, 10);
    }

    [Fact]
    public void Decides_for_owned_versions_in_the_given_order()
    {
        VersionInfo[] versions = [Version(27), Version(28), Version(29)];

        var decisions = RetentionPlanner.Decide(versions, KeepTwoDays);

        decisions.Select(d => d.Version).Should().Equal(versions);
        decisions.Select(d => d.Delete).Should().Equal(true, false, false);
        decisions[2].Decision!.Reasons.Select(r => r.Label).Should().Equal("Daily #1");
    }

    [Fact]
    public void Versions_that_are_not_owned_get_no_decision_and_do_not_count()
    {
        VersionInfo[] versions = [Version(27), Version(28, VersionOwnership.Foreign), Version(29, VersionOwnership.NoManifest), Version(30)];

        var decisions = RetentionPlanner.Decide(versions, KeepTwoDays);

        decisions.Select(d => d.Decision is null).Should().Equal(false, true, true, false);
        decisions.Select(d => d.Delete).Should().Equal(false, false, false, false);
    }

    [Fact]
    public void An_upcoming_run_is_counted_as_if_its_version_already_existed()
    {
        VersionInfo[] versions = [Version(28), Version(29)];

        RetentionPlanner.Decide(versions, KeepTwoDays).Select(d => d.Delete).Should().Equal(false, false);

        var decisions = RetentionPlanner.Decide(versions, KeepTwoDays, upcomingRun: new DateTime(2026, 9, 30, 2, 0, 0));
        decisions.Should().HaveCount(2, "the upcoming version itself is not part of the result");
        decisions.Select(d => d.Delete).Should().Equal(true, false);
    }

    [Fact]
    public void Without_rules_nothing_is_deleted()
    {
        RetentionPlanner.Decide([Version(28), Version(29)], []).Should().OnlyContain(d => !d.Delete);
    }

    [Fact]
    public void An_invalid_rule_is_rejected()
    {
        var act = () => RetentionPlanner.Decide([Version(28)], [new RetentionRule { Period = RetentionPeriod.Daily, Keep = 0 }]);

        act.Should().Throw<ArgumentException>();
    }
}
