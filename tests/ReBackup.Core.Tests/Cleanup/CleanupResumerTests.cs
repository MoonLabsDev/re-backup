using System.Text.Json;
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Cleanup;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Json;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;

namespace ReBackup.Core.Tests.Cleanup;

/// <summary>Finishing, at start, a full-plan cleanup that stopped between the manifest write and the deletion (spec 6.2).</summary>
public class CleanupResumerTests : IDisposable
{
    private const string Host = "this-pc";
    private static readonly string Name = $"2026_09_30-14_05 {VersionBuilder.PlanName}";

    private readonly TempDir _tmp = new();
    private readonly string _target;

    public CleanupResumerTests()
    {
        _target = _tmp.CreateDir("target");
        VersionBuilder.Write(_target, "2026_09_30-14_05",
        [
            VersionBuilder.File("src/a.cs", "class A {}"),
            VersionBuilder.File("target/x.dll", "xx"),
            VersionBuilder.File("target/sub/y.dll", "yyy"),
        ]);
    }

    public void Dispose() => _tmp.Dispose();

    private IStorage Target => new FileSystemStorage(_target);

    private string PathOf(string relative) => Path.Combine(_target, Name, relative.Replace('/', Path.DirectorySeparatorChar));

    private Task WriteMarker(string host = Host, string[]? files = null, string[]? folders = null) =>
        PruningMarker.WriteAsync(Target, Name,
            new PruningInfo(PruningMarker.FormatVersion, VersionBuilder.PlanId, host, DateTime.UtcNow,
                files ?? ["target/x.dll", "target/sub/y.dll"], folders ?? ["target/sub", "target"]),
            CancellationToken.None);

    /// <summary>The manifest as the cleanup wrote it before it stopped: without the folder "target".</summary>
    private void RewriteManifest()
    {
        var path = PathOf(VersionMarkerNames.Manifest);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), JsonDefaults.Options)!;
        manifest.Files.RemoveAll(f => f.Path.StartsWith("target/", StringComparison.Ordinal));
        manifest.Directories = ["src"];
        manifest.FileCount = manifest.Files.Count;
        manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
        File.WriteAllText(path, VersionBuilder.Serialize(manifest));
    }

    private async Task<int> FindAndResume()
    {
        var folders = await CleanupResumer.FindAsync(Target, VersionBuilder.PlanId, Host, CancellationToken.None);
        folders.Should().Equal(Name);
        return await CleanupResumer.ResumeAsync(Target, folders, CancellationToken.None);
    }

    [Fact]
    public async Task Finishes_the_deletion_after_the_manifest_was_rewritten()
    {
        RewriteManifest();
        await WriteMarker();

        var deleted = await FindAndResume();

        deleted.Should().Be(2);
        File.Exists(PathOf("target/x.dll")).Should().BeFalse();
        Directory.Exists(PathOf("target")).Should().BeFalse();
        File.Exists(PathOf("src/a.cs")).Should().BeTrue();
        File.Exists(PathOf(VersionMarkerNames.Pruning)).Should().BeFalse();
    }

    [Fact]
    public async Task A_marker_without_a_rewritten_manifest_deletes_nothing()
    {
        await WriteMarker();

        var deleted = await FindAndResume();

        deleted.Should().Be(0);
        File.Exists(PathOf("target/x.dll")).Should().BeTrue();
        File.Exists(PathOf("target/sub/y.dll")).Should().BeTrue();
        File.Exists(PathOf(VersionMarkerNames.Pruning)).Should().BeFalse();
    }

    [Fact]
    public async Task A_path_the_manifest_lists_in_another_case_is_kept_on_a_target_that_ignores_case()
    {
        Target.Capabilities.HasFlag(StorageCapabilities.CaseSensitive).Should().BeFalse("the test needs a target that ignores case");
        await WriteMarker(files: ["TARGET/X.dll"], folders: ["TARGET/SUB", "TARGET"]);

        var deleted = await FindAndResume();

        deleted.Should().Be(0);
        File.Exists(PathOf("target/x.dll")).Should().BeTrue();
        Directory.Exists(PathOf("target/sub")).Should().BeTrue();
        File.Exists(PathOf(VersionMarkerNames.Pruning)).Should().BeFalse();
    }

    [Fact]
    public async Task A_folder_that_still_holds_an_unlisted_file_stays()
    {
        RewriteManifest();
        File.WriteAllText(PathOf("target/mine.txt"), "a person's");
        await WriteMarker();

        await FindAndResume();

        File.Exists(PathOf("target/mine.txt")).Should().BeTrue();
        Directory.Exists(PathOf("target/sub")).Should().BeFalse();
        File.Exists(PathOf(VersionMarkerNames.Pruning)).Should().BeFalse();
    }

    [Fact]
    public async Task Without_a_readable_manifest_nothing_is_deleted_and_the_marker_stays()
    {
        File.Delete(PathOf(VersionMarkerNames.Manifest));
        await WriteMarker();

        var deleted = await FindAndResume();

        deleted.Should().Be(0);
        File.Exists(PathOf("target/x.dll")).Should().BeTrue();
        File.Exists(PathOf(VersionMarkerNames.Pruning)).Should().BeTrue();
    }

    [Fact]
    public async Task Markers_of_other_computers_are_left_alone()
    {
        RewriteManifest();
        await WriteMarker(host: "OTHER");

        (await CleanupResumer.FindAsync(Target, VersionBuilder.PlanId, Host, CancellationToken.None)).Should().BeEmpty();
        File.Exists(PathOf(VersionMarkerNames.Pruning)).Should().BeTrue();
    }

    [Fact]
    public async Task Markers_of_other_plans_are_left_alone()
    {
        await WriteMarker();

        (await CleanupResumer.FindAsync(Target, "another-plan", Host, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_target_that_does_not_exist_yet_has_nothing_to_resume()
    {
        var missing = new FileSystemStorage(_tmp.PathOf("missing"));

        (await CleanupResumer.FindAsync(missing, VersionBuilder.PlanId, Host, CancellationToken.None)).Should().BeEmpty();
    }
}
