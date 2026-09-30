using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Backup;

public class VersionRemoverTests : IDisposable
{
    private const string Name = "2026_09_01-02_00 Projects";
    private readonly TempDir _tmp = new();
    private readonly string _target;
    private readonly string _version;

    public VersionRemoverTests()
    {
        _target = _tmp.CreateDir("target");
        _version = VersionFolder.Create(_target, Name, "p1");
        _tmp.WriteFile($@"target\{Name}\sub\deep\c.txt", "x");
    }

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Removes_the_folder_with_everything_in_it()
    {
        VersionRemover.Remove(_version, new PhysicalTargetVolume());

        Directory.GetFileSystemEntries(_target).Should().BeEmpty();
    }

    [Fact]
    public void A_failing_rename_leaves_the_version_untouched()
    {
        var volume = new ScriptedVolume { FailMove = _ => true };

        var act = () => VersionRemover.Remove(_version, volume);

        act.Should().Throw<IOException>().Which.Should().NotBeOfType<VersionRemainsException>("nothing was renamed");
        File.Exists(Path.Combine(_version, "data.bin")).Should().BeTrue();
        File.Exists(Path.Combine(_version, "sub", "deep", "c.txt")).Should().BeTrue();
        Directory.Exists(_version + ".deleting").Should().BeFalse();
    }

    [Fact]
    public void A_removal_that_fails_half_way_leaves_a_deleting_folder_that_still_has_its_manifest()
    {
        var doomed = _version + ".deleting";
        var volume = new ScriptedVolume { FailDelete = path => path == doomed };

        var act = () => VersionRemover.Remove(_version, volume);

        var thrown = act.Should().ThrowExactly<VersionRemainsException>().Which;
        thrown.RemainsPath.Should().Be(doomed);
        thrown.Message.Should().Be("a file is in use");
        thrown.InnerException.Should().BeOfType<IOException>();
        Directory.Exists(_version).Should().BeFalse("it must no longer look like a version");
        File.Exists(Path.Combine(doomed, "re-manifest.json")).Should().BeTrue("the manifest goes last");
        File.Exists(Path.Combine(doomed, "data.bin")).Should().BeFalse();
        Directory.Exists(Path.Combine(doomed, "sub")).Should().BeFalse();

        VersionRemover.RemoveRemains(doomed, new PhysicalTargetVolume());
        Directory.GetFileSystemEntries(_target).Should().BeEmpty();
    }

    [Fact]
    public void A_version_that_is_gone_is_a_plain_failure_and_not_a_removal()
    {
        Directory.Delete(_version, recursive: true);

        var act = () => VersionRemover.Remove(_version, new PhysicalTargetVolume());

        act.Should().Throw<IOException>().Which.Should().NotBeOfType<VersionRemainsException>();
        Directory.GetFileSystemEntries(_target).Should().BeEmpty();
    }

    [Theory]
    [InlineData("link")]             // directly in the version folder
    [InlineData(@"sub\deep\link")]   // further down
    public void A_junction_inside_the_version_is_removed_as_a_link_and_its_target_is_untouched(string relativeLink)
    {
        var outside = _tmp.CreateDir("outside");
        var foreign = _tmp.WriteFile(@"outside\nested\keep.txt", "not ours");
        var link = Path.Combine(_version, relativeLink);
        Junction.Create(link, outside);

        try
        {
            VersionRemover.Remove(_version, new PhysicalTargetVolume());

            Directory.GetFileSystemEntries(_target).Should().BeEmpty();
            File.ReadAllText(foreign).Should().Be("not ours");
        }
        finally
        {
            foreach (var leftover in new[] { link, Path.Combine(_version + ".deleting", relativeLink) })
            {
                if (Directory.Exists(leftover))
                    Directory.Delete(leftover);   // removes the junction only
            }
        }
    }

    [Fact]
    public void A_link_is_refused_and_its_target_is_untouched()
    {
        var real = VersionFolder.Create(_tmp.CreateDir("elsewhere"), Name, "p1");
        var link = Path.Combine(_target, "2026_09_02-02_00 Projects");
        Junction.Create(link, real);

        try
        {
            var act = () => VersionRemover.Remove(link, new PhysicalTargetVolume());

            act.Should().Throw<IOException>().WithMessage("*is a link*")
                .Which.Should().NotBeOfType<VersionRemainsException>("nothing was renamed");
            File.Exists(Path.Combine(real, "data.bin")).Should().BeTrue();
            File.Exists(Path.Combine(real, "re-manifest.json")).Should().BeTrue();
            Directory.Exists(link + ".deleting").Should().BeFalse();
        }
        finally
        {
            Directory.Delete(link);
        }
    }
}
