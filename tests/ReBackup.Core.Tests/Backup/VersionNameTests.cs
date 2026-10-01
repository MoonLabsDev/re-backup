using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;

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

    [Theory]
    [InlineData("2026_09_30-14_05 Projects", true, "Projects")]
    [InlineData("2026_09_30-14_05 My plan v2", true, "My plan v2")]
    [InlineData("2026_09_30-14_05 ", false, "")]
    [InlineData("2026_09_30-14_05", false, "")]
    [InlineData("2026_13_30-14_05 Projects", false, "")]
    [InlineData("2026_09_30-14_05_Projects", false, "")]
    [InlineData("notes", false, "")]
    public void TryParseAny_accepts_a_timestamp_followed_by_any_name(string folder, bool expected, string expectedName)
    {
        VersionName.TryParseAny(folder, out var time, out var name).Should().Be(expected);

        name.Should().Be(expectedName);
        if (expected)
            time.Should().Be(new DateTime(2026, 9, 30, 14, 5, 0));
    }

    [Theory]
    [InlineData("2026_09_30-14_05 Projects.partial", true)]
    [InlineData("2026_09_30-14_05 Projects.PARTIAL", true)]
    [InlineData("2026_09_30-14_05 Projects.deleting", true)]
    [InlineData("2026_09_30-14_05 Projects", false)]
    public void IsTransient_recognises_folders_that_are_being_written_or_removed(string folder, bool expected)
    {
        VersionName.IsTransient(folder).Should().Be(expected);
    }

    [Fact]
    public void FolderIn_combines_the_target_and_a_plain_folder_name()
    {
        VersionName.FolderIn(@"D:\Backups", "2026_09_30-14_05 Projects")
            .Should().Be(@"D:\Backups\2026_09_30-14_05 Projects");
        VersionName.FolderIn(@"D:\Backups\", "2026_09_30-14_05 Projects")
            .Should().Be(@"D:\Backups\2026_09_30-14_05 Projects");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"..\elsewhere")]
    [InlineData(@"sub\2026_09_30-14_05 Projects")]
    [InlineData("sub/2026_09_30-14_05 Projects")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"\\server\share")]
    [InlineData("name:stream")]
    [InlineData("a*b")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData(" leading")]
    public void FolderIn_rejects_anything_but_one_plain_folder_name(string? name)
    {
        VersionName.FolderIn(@"D:\Backups", name).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(@"relative\target")]
    public void FolderIn_needs_an_absolute_target(string? target)
    {
        VersionName.FolderIn(target, "2026_09_30-14_05 Projects").Should().BeNull();
    }

    [Fact]
    public void ExistingFolderIn_returns_the_folder_only_while_it_exists()
    {
        using var tmp = new TempDir();
        tmp.CreateDir("2026_09_30-14_05 Projects");
        File.WriteAllText(tmp.PathOf("2026_09_30-15_05 Projects"), "a file, not a folder");

        VersionName.ExistingFolderIn(tmp.Root, "2026_09_30-14_05 Projects")
            .Should().Be(tmp.PathOf("2026_09_30-14_05 Projects"));
        VersionName.ExistingFolderIn(tmp.Root, "2026_09_30-15_05 Projects").Should().BeNull("it is a file");
        VersionName.ExistingFolderIn(tmp.Root, "2026_09_30-16_05 Projects").Should().BeNull("it does not exist");
        VersionName.ExistingFolderIn(tmp.Root, "..").Should().BeNull("it is not a folder name");
    }
}
