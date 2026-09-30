using FluentAssertions;
using ReBackup.Core.Ignore;

namespace ReBackup.Core.Tests.Ignore;

public class IgnorePatternTests
{
    [Theory]
    // simple names and extensions match at any depth
    [InlineData("*.tmp", "a.tmp", false, true)]
    [InlineData("*.tmp", "sub/deep/a.tmp", false, true)]
    [InlineData("*.tmp", "a.tmpx", false, false)]
    [InlineData("*.TMP", "a.tmp", false, true)]
    [InlineData("Thumbs.db", "pics/Thumbs.db", false, true)]
    [InlineData("Thumbs.db", "pics/Thumbs.dbx", false, false)]
    [InlineData("*", "anything", false, true)]
    // directory-only patterns
    [InlineData("node_modules/", "node_modules", true, true)]
    [InlineData("node_modules/", "node_modules", false, false)]
    [InlineData("node_modules/", "src/node_modules", true, true)]
    [InlineData("$RECYCLE.BIN/", "$RECYCLE.BIN", true, true)]
    [InlineData("System Volume Information/", "System Volume Information", true, true)]
    // anchoring
    [InlineData("/build", "build", true, true)]
    [InlineData("/build", "src/build", true, false)]
    [InlineData("docs/*.md", "docs/a.md", false, true)]
    [InlineData("docs/*.md", "docs/sub/a.md", false, false)]
    [InlineData("docs/*.md", "x/docs/a.md", false, false)]
    [InlineData("a*b", "a/b", false, false)]
    // double star
    [InlineData("**/cache", "cache", true, true)]
    [InlineData("**/cache", "a/b/cache", true, true)]
    [InlineData("logs/**", "logs/a/b.txt", false, true)]
    [InlineData("logs/**", "logs", true, false)]
    [InlineData("a/**/z", "a/z", false, true)]
    [InlineData("a/**/z", "a/b/c/z", false, true)]
    [InlineData("a/**/z", "a/b/zz", false, false)]
    // ? and character classes
    [InlineData("file?.txt", "file1.txt", false, true)]
    [InlineData("file?.txt", "file10.txt", false, false)]
    [InlineData("file[0-9].txt", "file5.txt", false, true)]
    [InlineData("file[!0-9].txt", "file5.txt", false, false)]
    [InlineData("file[!0-9].txt", "filex.txt", false, true)]
    // escapes
    [InlineData("\\#notes", "#notes", false, true)]
    [InlineData("\\!important", "!important", false, true)]
    [InlineData("a\\*b", "a*b", false, true)]
    [InlineData("a\\*b", "axb", false, false)]
    public void IsMatch_follows_gitignore_rules(string line, string path, bool isDirectory, bool expected)
    {
        var pattern = IgnorePattern.Parse(line, "Plan")!;

        pattern.IsMatch(path, isDirectory).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# a comment")]
    [InlineData("!")]
    [InlineData("/")]
    public void Parse_returns_null_for_lines_without_a_pattern(string line)
    {
        IgnorePattern.Parse(line, "Plan").Should().BeNull();
    }

    [Fact]
    public void Parse_keeps_text_origin_and_flags()
    {
        var pattern = IgnorePattern.Parse("!/build/  \r", "Plan")!;

        pattern.Text.Should().Be("!/build/");
        pattern.Origin.Should().Be("Plan");
        pattern.IsNegated.Should().BeTrue();
        pattern.IsDirectoryOnly.Should().BeTrue();
        pattern.IsAnchored.Should().BeTrue();
        pattern.IsMatch("build", isDirectory: true).Should().BeTrue();
    }

    [Fact]
    public void Trailing_spaces_are_trimmed_unless_escaped()
    {
        IgnorePattern.Parse("foo   ", "Plan")!.IsMatch("foo", false).Should().BeTrue();
        IgnorePattern.Parse("foo\\ ", "Plan")!.IsMatch("foo ", false).Should().BeTrue();
        IgnorePattern.Parse("foo\\ ", "Plan")!.IsMatch("foo", false).Should().BeFalse();
    }

    [Fact]
    public void Base_directory_scopes_the_pattern()
    {
        var pattern = IgnorePattern.Parse("*.log", "sub/.backupignore", "sub")!;

        pattern.BaseDirectory.Should().Be("sub");
        pattern.IsMatch("sub/a.log", false).Should().BeTrue();
        pattern.IsMatch("sub/x/a.log", false).Should().BeTrue();
        pattern.IsMatch("a.log", false).Should().BeFalse();
        pattern.IsMatch("subx/a.log", false).Should().BeFalse();
    }

    [Fact]
    public void Anchored_pattern_is_relative_to_its_base_directory()
    {
        var pattern = IgnorePattern.Parse("/build", "sub/.backupignore", "sub")!;

        pattern.IsMatch("sub/build", true).Should().BeTrue();
        pattern.IsMatch("sub/x/build", true).Should().BeFalse();
        pattern.IsMatch("build", true).Should().BeFalse();
    }

    [Fact]
    public void Base_directory_is_normalised_to_forward_slashes()
    {
        IgnorePattern.Parse("*.log", "x", "a\\b\\")!.BaseDirectory.Should().Be("a/b");
    }

    [Fact]
    public void Invalid_character_class_falls_back_to_a_literal_match()
    {
        var pattern = IgnorePattern.Parse("x[z-a]", "Plan")!;

        pattern.IsMatch("x[z-a]", false).Should().BeTrue();
        pattern.IsMatch("xb", false).Should().BeFalse();
    }

    [Fact]
    public void EscapeLiteral_makes_a_name_match_itself()
    {
        const string name = "a[1]*?.txt";

        var escaped = IgnorePattern.EscapeLiteral(name);

        escaped.Should().Be("a\\[1\\]\\*\\?.txt");
        IgnorePattern.Parse("/" + escaped, "Plan")!.IsMatch(name, false).Should().BeTrue();
        IgnorePattern.Parse("/" + escaped, "Plan")!.IsMatch("a1xy.txt", false).Should().BeFalse();
    }
}
