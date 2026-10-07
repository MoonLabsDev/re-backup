using FluentAssertions;

namespace ReBackup.Storage.S3.Tests.Unit;

public class S3KeysTests
{
    [Theory]
    [InlineData("", "a/b.txt", "a/b.txt")]
    [InlineData("wp/elvora", "x", "wp/elvora/x")]
    [InlineData("wp", "", "wp")]
    [InlineData("", "", "")]
    public void ToKey_joins_prefix_and_path(string prefix, string path, string expected) =>
        S3Keys.ToKey(prefix, path).Should().Be(expected);

    [Theory]
    [InlineData("", "a/b.txt", "a/b.txt")]
    [InlineData("wp/elvora", "wp/elvora/x", "x")]
    [InlineData("wp/elvora", "wp/elvora/x/y.bin", "x/y.bin")]
    public void ToPath_is_the_inverse_of_ToKey(string prefix, string key, string expected)
    {
        S3Keys.ToPath(prefix, key).Should().Be(expected);
        S3Keys.ToKey(prefix, expected).Should().Be(key);
    }

    [Theory]
    [InlineData("wp", "other/x")]
    [InlineData("wp", "wpx/x")]
    [InlineData("wp", "wp")]
    public void ToPath_rejects_a_key_outside_the_prefix(string prefix, string key)
    {
        var act = () => S3Keys.ToPath(prefix, key);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToKey_rejects_more_than_1024_utf8_bytes()
    {
        var path = new string('ä', 512) + "x"; // 1025 bytes
        var act = () => S3Keys.ToKey("", path);
        act.Should().Throw<ArgumentException>();

        S3Keys.ToKey("", new string('ä', 512)).Should().HaveLength(512);
    }

    [Fact]
    public void ToKey_rejects_invalid_paths()
    {
        var act = () => S3Keys.ToKey("p", "../x");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("wp", "", "wp/")]
    [InlineData("", "a", "a/")]
    [InlineData("wp", "a/b", "wp/a/b/")]
    public void DirectoryPrefix_ends_with_a_slash_except_for_the_bucket_root(string prefix, string path, string expected) =>
        S3Keys.DirectoryPrefix(prefix, path).Should().Be(expected);

    [Fact]
    public void NormalizePrefix_validates_and_keeps_the_empty_prefix()
    {
        S3Keys.NormalizePrefix("").Should().Be("");
        S3Keys.NormalizePrefix("a/b").Should().Be("a/b");
        var act = () => S3Keys.NormalizePrefix("/a");
        act.Should().Throw<ArgumentException>();
    }
}
