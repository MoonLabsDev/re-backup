using FluentAssertions;
using ReBackup.Storage;

namespace ReBackup.Storage.Tests;

public class StoragePathTests
{
    [Fact]
    public void Combine_skips_empty_parts_and_joins_with_slash() =>
        StoragePath.Combine("", "a", "", "b/c").Should().Be("a/b/c");

    [Theory]
    [InlineData("a/b/c.txt", "a/b", "c.txt")]
    [InlineData("top.txt", "", "top.txt")]
    public void Parent_and_name_split_at_the_last_slash(string path, string parent, string name)
    {
        StoragePath.Parent(path).Should().Be(parent);
        StoragePath.Name(path).Should().Be(name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("a/b.txt")]
    public void Validate_accepts_relative_paths_and_the_root(string path) =>
        StoragePath.Validate(path).Should().Be(path);

    [Theory]
    [InlineData("a\\b")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a//b")]
    [InlineData("a/../b")]
    [InlineData("./a")]
    [InlineData("..")]
    public void Validate_rejects_malformed_paths(string path)
    {
        var act = () => StoragePath.Validate(path);

        act.Should().Throw<ArgumentException>();
    }
}
