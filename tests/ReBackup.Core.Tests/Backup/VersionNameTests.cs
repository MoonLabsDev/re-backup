using FluentAssertions;
using ReBackup.Core.Backup;

namespace ReBackup.Core.Tests.Backup;

public class VersionNameTests
{
    [Fact]
    public void Format_uses_the_spec_pattern_with_a_24_hour_clock()
    {
        VersionName.Format(new DateTime(2026, 9, 30, 14, 5, 59), "Projects").Should().Be("2026_09_30-14_05 Projects");
        VersionName.Format(new DateTime(2026, 1, 2, 3, 4, 0), "My Plan").Should().Be("2026_01_02-03_04 My Plan");
    }

    [Fact]
    public void TryParse_round_trips_Format()
    {
        var name = VersionName.Format(new DateTime(2026, 9, 30, 14, 5, 59), "Projects");

        VersionName.TryParse(name, "Projects", out var time).Should().BeTrue();
        time.Should().Be(new DateTime(2026, 9, 30, 14, 5, 0));
    }

    [Fact]
    public void TryParse_ignores_the_case_of_the_plan_name()
    {
        VersionName.TryParse("2026_09_30-14_05 PROJECTS", "Projects", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("2026_09_30-14_05 Other")]
    [InlineData("2026_09_30-14_05 Projects.partial")]
    [InlineData("2026_09_30-14_05 Projects 2")]
    [InlineData("2026_09_30-14_05  Projects")]
    [InlineData("2026_09_30-14_05Projects")]
    [InlineData("2026_13_40-25_61 Projects")]
    [InlineData("2026-09-30 14:05 Projects")]
    [InlineData("Projects")]
    [InlineData("")]
    public void TryParse_rejects_everything_else(string folderName)
    {
        VersionName.TryParse(folderName, "Projects", out _).Should().BeFalse();
    }
}
