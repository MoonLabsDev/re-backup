using FluentAssertions;
using ReBackup.Core.Ignore;

namespace ReBackup.Core.Tests.Ignore;

public class IgnoreTranslationTests
{
    [Theory]
    [InlineData("foo/", "P/X", "/P/X/**/foo/")]
    [InlineData("*.tmp", "P/X", "/P/X/**/*.tmp")]
    [InlineData("name", "P/X", "/P/X/**/name")]
    [InlineData("**/x", "P/X", "/P/X/**/x")]
    [InlineData("/build/", "P/X", "/P/X/build/")]
    [InlineData("a/b", "P/X", "/P/X/a/b")]
    [InlineData("!keep.tmp", "P/X", "!/P/X/**/keep.tmp")]
    [InlineData("# note", "P/X", "# note")]
    [InlineData("", "P/X", "")]
    [InlineData("foo/", "", "foo/")]                         // root file: unchanged
    [InlineData("x", "P/[alt] X", "/P/\\[alt\\] X/**/x")]    // escaped prefix (EscapeLiteral escapes both brackets)
    public void ToPlanLine_translates(string line, string dir, string expected) =>
        IgnoreTranslation.ToPlanLine(line, dir).Should().Be(expected);

    [Theory]
    [InlineData("/P/X/**/foo/", "P/X", "foo/")]
    [InlineData("/P/X/**/x/y", "P/X", "**/x/y")]
    [InlineData("/P/X/build/", "P/X", "/build/")]
    [InlineData("!/P/X/**/keep.tmp", "P/X", "!keep.tmp")]
    [InlineData("/P/\\[alt\\] X/**/x", "P/[alt] X", "x")]
    [InlineData("*.tmp", "", "*.tmp")]                       // root: unanchored allowed
    public void ToNestedLine_translates(string line, string dir, string expected) =>
        IgnoreTranslation.ToNestedLine(line, dir).Line.Should().Be(expected);

    [Theory]
    [InlineData("*.tmp", "P/X", RejectReason.MatchesOutsideFolder)]
    [InlineData("node_modules/", "P/X", RejectReason.MatchesOutsideFolder)]
    [InlineData("/Q/build/", "P/X", RejectReason.NotBelowFolder)]
    [InlineData("/P/XY/a", "P/X", RejectReason.NotBelowFolder)]   // prefix must end at a slash
    public void ToNestedLine_rejects(string line, string dir, RejectReason reason) =>
        IgnoreTranslation.ToNestedLine(line, dir).Reason.Should().Be(reason);

    // Pattern rows only. "**/x" and "a/b" come back in their canonical spelling ("x", "/a/b"): section 3.2 has no
    // way to produce the original text, and both spellings mean the same.
    [Theory]
    [InlineData("foo/", "P/X", "foo/")]
    [InlineData("*.tmp", "P/X", "*.tmp")]
    [InlineData("name", "P/X", "name")]
    [InlineData("**/x", "P/X", "x")]
    [InlineData("**/x/y", "P/X", "**/x/y")]
    [InlineData("/build/", "P/X", "/build/")]
    [InlineData("a/b", "P/X", "/a/b")]
    [InlineData("!keep.tmp", "P/X", "!keep.tmp")]
    [InlineData("x", "P/[alt] X", "x")]
    public void Round_trip_gives_the_original_line(string line, string dir, string expected) =>
        IgnoreTranslation.ToNestedLine(IgnoreTranslation.ToPlanLine(line, dir), dir).Line.Should().Be(expected);
}
