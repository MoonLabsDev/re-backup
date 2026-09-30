using FluentAssertions;
using ReBackup.Core.Ignore;
using ReBackup.Core.Plans;

namespace ReBackup.Core.Tests.Ignore;

public class IgnoreMatcherTests
{
    private static IgnoreMatcher Plan(params string[] patterns) => IgnoreMatcher.Create([], patterns, []);

    [Fact]
    public void Unmatched_path_is_not_ignored_and_has_no_pattern()
    {
        var result = Plan("*.tmp").Match("a.txt", false);

        result.IsIgnored.Should().BeFalse();
        result.Pattern.Should().BeNull();
    }

    [Fact]
    public void Last_matching_pattern_wins()
    {
        var matcher = Plan("*.log", "!keep.log");

        matcher.Match("a.log", false).IsIgnored.Should().BeTrue();
        var kept = matcher.Match("keep.log", false);
        kept.IsIgnored.Should().BeFalse();
        kept.Pattern!.Text.Should().Be("!keep.log");
    }

    [Fact]
    public void Order_of_patterns_matters()
    {
        Plan("!keep.log", "*.log").Match("keep.log", false).IsIgnored.Should().BeTrue();
    }

    [Fact]
    public void Comments_and_blank_lines_are_skipped()
    {
        Plan("# comment", "", "*.a").Patterns.Should().ContainSingle().Which.Text.Should().Be("*.a");
    }

    [Fact]
    public void Plan_overrides_global_defaults_and_nested_files_override_the_plan()
    {
        var globalOnly = IgnoreMatcher.Create(["*.tmp"], [], []);
        var withPlan = IgnoreMatcher.Create(["*.tmp"], ["!important.tmp"], []);
        var withNested = IgnoreMatcher.Create(["*.tmp"], ["!important.tmp"],
            [new NestedIgnoreFile("", ["important.tmp"])]);

        var byGlobal = globalOnly.Match("important.tmp", false);
        byGlobal.IsIgnored.Should().BeTrue();
        byGlobal.Pattern!.Origin.Should().Be("Global defaults");

        var byPlan = withPlan.Match("important.tmp", false);
        byPlan.IsIgnored.Should().BeFalse();
        byPlan.Pattern!.Origin.Should().Be("Plan");

        var byNested = withNested.Match("important.tmp", false);
        byNested.IsIgnored.Should().BeTrue();
        byNested.Pattern!.Origin.Should().Be(".backupignore");
    }

    [Fact]
    public void Nested_file_only_applies_below_its_folder()
    {
        var matcher = IgnoreMatcher.Create([], [], [new NestedIgnoreFile("sub", ["*.txt"])]);

        var inside = matcher.Match("sub/a.txt", false);
        inside.IsIgnored.Should().BeTrue();
        inside.Pattern!.Origin.Should().Be("sub/.backupignore");
        matcher.Match("a.txt", false).IsIgnored.Should().BeFalse();
    }

    [Fact]
    public void Deeper_nested_file_overrides_a_shallower_one_regardless_of_input_order()
    {
        var matcher = IgnoreMatcher.Create([], [],
        [
            new NestedIgnoreFile("sub\\deep", ["!*.txt"]),
            new NestedIgnoreFile("", ["*.txt"]),
        ]);

        matcher.Match("a.txt", false).IsIgnored.Should().BeTrue();
        matcher.Match("sub/a.txt", false).IsIgnored.Should().BeTrue();
        var reincluded = matcher.Match("sub/deep/a.txt", false);
        reincluded.IsIgnored.Should().BeFalse();
        reincluded.Pattern!.Origin.Should().Be("sub/deep/.backupignore");
    }

    [Fact]
    public void File_below_an_ignored_folder_cannot_be_reincluded()
    {
        var matcher = Plan("build/", "!build/keep.txt");

        matcher.MatchEntry("build/keep.txt", false).IsIgnored.Should().BeFalse();
        var full = matcher.Match("build/keep.txt", false);
        full.IsIgnored.Should().BeTrue();
        full.Pattern!.Text.Should().Be("build/");
        matcher.Match("a/build/x/y.txt", false).IsIgnored.Should().BeTrue();
    }

    [Fact]
    public void ForPlan_honours_the_two_switches()
    {
        var nested = new[] { new NestedIgnoreFile("", ["*.nested"]) };
        var settings = new IgnoreSettings { Patterns = ["*.plan"] };

        var all = IgnoreMatcher.ForPlan(settings, ["*.global"], nested);
        all.Match("a.global", false).IsIgnored.Should().BeTrue();
        all.Match("a.plan", false).IsIgnored.Should().BeTrue();
        all.Match("a.nested", false).IsIgnored.Should().BeTrue();

        settings.UseGlobalDefaults = false;
        settings.HonorNestedFiles = false;
        var planOnly = IgnoreMatcher.ForPlan(settings, ["*.global"], nested);
        planOnly.Match("a.global", false).IsIgnored.Should().BeFalse();
        planOnly.Match("a.plan", false).IsIgnored.Should().BeTrue();
        planOnly.Match("a.nested", false).IsIgnored.Should().BeFalse();
    }
}
