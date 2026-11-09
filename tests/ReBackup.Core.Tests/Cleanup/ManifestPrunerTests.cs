using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Cleanup;
using ReBackup.Core.Ignore;
using ReBackup.Core.Plans;

namespace ReBackup.Core.Tests.Cleanup;

public class ManifestPrunerTests
{
    private static readonly DateTime Time = new(2026, 9, 30, 14, 5, 0, DateTimeKind.Utc);

    private static ManifestFile File(string path, long size) => new(path, size, Time, "xxh64:0000000000000000");

    private static IgnoreMatcher Rules(params string[] patterns) =>
        IgnoreMatcher.ForPlan(new IgnoreSettings { UseGlobalDefaults = false, Patterns = [.. patterns] }, [], []);

    [Fact]
    public void Removes_ignored_files_and_folders_and_recomputes_the_header()
    {
        var manifest = new BackupManifest
        {
            Files = [File("src/a.cs", 10), File("target/x.dll", 100), File("target/sub/y.dll", 50)],
            Directories = ["src", "target", "target/sub", "empty"],
        };

        var result = ManifestPruner.Prune(manifest, Rules("target/"));

        manifest.Files.Select(f => f.Path).Should().Equal("src/a.cs");
        manifest.FileCount.Should().Be(1);
        manifest.TotalBytes.Should().Be(10);
        manifest.Directories.Should().Equal("src", "empty");
        result.RemovedFiles.Should().BeEquivalentTo("target/x.dll", "target/sub/y.dll");
        result.RemovedFolders.Should().Equal("target/sub", "target");
    }

    [Fact]
    public void A_folder_that_only_loses_some_files_stays_listed()
    {
        var manifest = new BackupManifest
        {
            Files = [File("logs/a.log", 5), File("logs/keep.txt", 7)],
            Directories = ["logs"],
        };

        var result = ManifestPruner.Prune(manifest, Rules("*.log"));

        result.RemovedFiles.Should().Equal("logs/a.log");
        result.RemovedFolders.Should().BeEmpty();
        manifest.Directories.Should().Equal("logs");
        manifest.Files.Select(f => f.Path).Should().Equal("logs/keep.txt");
    }

    [Fact]
    public void Case_differences_match_as_the_matcher_matches()
    {
        var matcher = Rules("target/");
        var manifest = new BackupManifest { Files = [File("Target/x.dll", 1)], Directories = ["Target"] };
        var expected = matcher.Match("Target/x.dll", false).IsIgnored;

        var result = ManifestPruner.Prune(manifest, matcher);

        (result.RemovedFiles.Count == 1).Should().Be(expected);
        (manifest.Files.Count == 0).Should().Be(expected);
    }

    [Fact]
    public void Nothing_ignored_leaves_the_manifest_unchanged()
    {
        var files = new[] { File("src/a.cs", 10), File("src/b.cs", 20) };
        var manifest = new BackupManifest { Files = [.. files], Directories = ["src"] };

        var result = ManifestPruner.Prune(manifest, Rules("*.log"));

        result.RemovedFiles.Should().BeEmpty();
        result.RemovedFolders.Should().BeEmpty();
        manifest.Files.Should().Equal(files);
        manifest.Directories.Should().Equal("src");
        manifest.FileCount.Should().Be(2);
        manifest.TotalBytes.Should().Be(30);
    }
}
