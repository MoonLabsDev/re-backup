using FluentAssertions;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;

namespace ReBackup.Core.Tests.Ignore;

public class IgnoreLineStatsTests
{
    private static IndexNode File(string name, long size) => new() { Name = name, IsDirectory = false, Size = size };

    private static IndexNode Dir(string name, params IndexNode[] children) =>
        new() { Name = name, IsDirectory = true, Children = children };

    private static EvaluatedNode Evaluate(IndexNode root, string[] planLines, params NestedIgnoreFile[] nested) =>
        IndexEvaluator.Evaluate(new SourceIndex(@"C:\src", root, [], 0, 0), IgnoreMatcher.Create([], planLines, nested));

    [Fact]
    public void Counts_entries_decided_by_each_line()
    {
        var root = Dir("root", Dir("foo", File("a", 100), File("b", 100), File("c", 100)), File("x.tmp", 7));
        string[] lines = ["# ignore things", "foo/", "", "*.tmp"];
        var evaluated = Evaluate(root, [], new NestedIgnoreFile("", lines));

        var stats = IgnoreLineStats.ForOrigin(evaluated, IgnoreOrigins.ForNestedFile(""), lines);

        stats.Should().Equal(
            new LineHits(2, "foo/", 1, 300),
            new LineHits(4, "*.tmp", 1, 7));
    }

    [Fact]
    public void Entries_ignored_only_by_a_parent_are_not_counted_again()
    {
        var root = Dir("root", Dir("foo", File("a.tmp", 100), Dir("sub", File("b.tmp", 50))));
        string[] lines = ["foo/", "*.tmp"];
        var evaluated = Evaluate(root, [], new NestedIgnoreFile("", lines));

        var stats = IgnoreLineStats.ForOrigin(evaluated, IgnoreOrigins.ForNestedFile(""), lines);

        stats.Should().Equal(
            new LineHits(1, "foo/", 1, 150),
            new LineHits(2, "*.tmp", 0, 0));
    }

    [Fact]
    public void Unused_line_has_zero_hits()
    {
        var root = Dir("root", File("a.txt", 1));
        string[] lines = ["*.log"];
        var evaluated = Evaluate(root, [], new NestedIgnoreFile("", lines));

        IgnoreLineStats.ForOrigin(evaluated, IgnoreOrigins.ForNestedFile(""), lines)
            .Should().Equal(new LineHits(1, "*.log", 0, 0));
    }

    [Fact]
    public void Other_origins_are_ignored()
    {
        var root = Dir("root", File("a.tmp", 5), File("b.log", 9));
        string[] nestedLines = ["*.log"];
        var evaluated = Evaluate(root, ["*.tmp"], new NestedIgnoreFile("", nestedLines));

        IgnoreLineStats.ForOrigin(evaluated, IgnoreOrigins.ForNestedFile(""), nestedLines)
            .Should().Equal(new LineHits(1, "*.log", 1, 9));
        IgnoreLineStats.ForOrigin(evaluated, IgnoreOrigins.Plan, ["*.tmp"])
            .Should().Equal(new LineHits(1, "*.tmp", 1, 5));
    }

    [Fact]
    public void Re_included_entries_count_for_the_negated_line()
    {
        var root = Dir("root", File("a.tmp", 5), File("keep.tmp", 11));
        string[] lines = ["*.tmp", "!keep.tmp"];
        var evaluated = Evaluate(root, lines);

        IgnoreLineStats.ForOrigin(evaluated, IgnoreOrigins.Plan, lines).Should().Equal(
            new LineHits(1, "*.tmp", 1, 5),
            new LineHits(2, "!keep.tmp", 1, 11));
    }

    [Fact]
    public void Repeated_line_text_is_credited_to_its_last_occurrence()
    {
        var root = Dir("root", File("a.tmp", 5));
        string[] lines = ["*.tmp", "*.tmp"];
        var evaluated = Evaluate(root, lines);

        IgnoreLineStats.ForOrigin(evaluated, IgnoreOrigins.Plan, lines).Should().Equal(
            new LineHits(1, "*.tmp", 0, 0),
            new LineHits(2, "*.tmp", 1, 5));
    }
}
