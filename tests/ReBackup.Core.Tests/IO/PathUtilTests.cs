using FluentAssertions;
using ReBackup.Core.IO;

namespace ReBackup.Core.Tests.IO;

public class PathUtilTests
{
    [Theory]
    [InlineData(@"C:\data\backup", @"C:\data", true)]
    [InlineData(@"C:\data", @"C:\data", true)]
    [InlineData(@"C:\DATA\x", @"c:\data\", true)]
    [InlineData(@"C:\database", @"C:\data", false)]
    [InlineData(@"D:\data", @"C:\data", false)]
    [InlineData(@"C:\x", @"C:\", true)]
    [InlineData(@"C:\data\..\other", @"C:\data", false)]
    public void IsSameOrInside_compares_normalised_paths(string path, string root, bool expected)
    {
        PathUtil.IsSameOrInside(path, root).Should().Be(expected);
    }

    [Fact]
    public void Normalize_removes_trailing_separator_but_keeps_drive_root()
    {
        PathUtil.Normalize(@"C:\data\").Should().Be(@"C:\data");
        PathUtil.Normalize(@"C:\").Should().Be(@"C:\");
    }
}
