using System.Text;
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Pool;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;
using static ReBackup.Core.Tests.TestSupport.PoolVersions;

namespace ReBackup.Core.Tests.Pool;

/// <summary>
/// The read-only view of one pool version (spec 7): below the version folder it answers from the manifest and reads
/// files from their blobs, so that it looks like a version folder of a full plan.
/// </summary>
public class PoolVersionViewTests
{
    private const string V1 = "2026_10_15-10_00 Projects";
    private const string Manifest = V1 + "/" + VersionMarkerNames.Manifest;

    private static InMemoryStorage Target(StorageCapabilities capabilities =
        StorageCapabilities.EmptyDirectories | StorageCapabilities.SetModifiedTime | StorageCapabilities.Links)
    {
        var target = new InMemoryStorage(capabilities);
        PoolVersions.Write(target, V1, "p1", "Projects",
            [("a.txt", "alpha"), ("docs/b.txt", "bravo"), ("docs/sub/c.txt", "charlie")],
            directories: ["docs", "docs/sub", "docs/empty", "other"]);
        return target;
    }

    private static async Task<List<StorageEntry>> ListAsync(IStorage storage, string folder, bool recursive)
    {
        var entries = new List<StorageEntry>();
        await foreach (var entry in storage.ListAsync(folder, recursive, CancellationToken.None))
            entries.Add(entry);
        return entries;
    }

    private static async Task<string> ReadAsync(IStorage storage, string path)
    {
        await using var stream = await storage.OpenReadAsync(path, CancellationToken.None);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Lists_version_tree_from_manifest()
    {
        var view = new PoolVersionView(Target(), V1);

        var top = await ListAsync(view, V1, recursive: false);
        top.Select(e => (e.Path, e.IsDirectory)).Should().BeEquivalentTo([
            ($"{V1}/a.txt", false), ($"{V1}/docs", true), ($"{V1}/other", true), (Manifest, false)]);
        var a = top.Single(e => e.Path == $"{V1}/a.txt");
        a.Size.Should().Be(5);
        a.ModifiedUtc.Should().Be(PoolVersions.Mtime);
        a.IsLink.Should().BeFalse();

        (await ListAsync(view, $"{V1}/docs", recursive: false)).Select(e => e.Path).Should().BeEquivalentTo([
            $"{V1}/docs/b.txt", $"{V1}/docs/sub", $"{V1}/docs/empty"]);
        (await ListAsync(view, $"{V1}/docs/empty", recursive: false)).Should().BeEmpty();
        (await ListAsync(view, V1, recursive: true)).Select(e => e.Path).Should().BeEquivalentTo([
            $"{V1}/a.txt", $"{V1}/docs", $"{V1}/docs/b.txt", $"{V1}/docs/sub", $"{V1}/docs/sub/c.txt",
            $"{V1}/docs/empty", $"{V1}/other", Manifest]);

        var listMissing = () => ListAsync(view, $"{V1}/nope", recursive: false);
        await listMissing.Should().ThrowAsync<StorageNotFoundException>();
        var listFile = () => ListAsync(view, $"{V1}/a.txt", recursive: false);
        await listFile.Should().ThrowAsync<StorageNotFoundException>();
    }

    [Fact]
    public async Task Opens_file_from_blob()
    {
        var view = new PoolVersionView(Target(), V1);

        (await ReadAsync(view, $"{V1}/docs/sub/c.txt")).Should().Be("charlie");
        var stat = await view.StatAsync($"{V1}/docs/b.txt", CancellationToken.None);
        stat.Should().NotBeNull();
        stat!.IsDirectory.Should().BeFalse();
        stat.Size.Should().Be(5);
        (await view.StatAsync($"{V1}/docs", CancellationToken.None))!.IsDirectory.Should().BeTrue();
        (await view.StatAsync(V1, CancellationToken.None))!.IsDirectory.Should().BeTrue();

        var openFolder = () => view.OpenReadAsync($"{V1}/docs", CancellationToken.None);
        await openFolder.Should().ThrowAsync<StorageNotFoundException>();
    }

    [Fact]
    public async Task Stat_of_missing_path_is_null()
    {
        var view = new PoolVersionView(Target(), V1);

        (await view.StatAsync($"{V1}/nope.txt", CancellationToken.None)).Should().BeNull();
        (await view.StatAsync($"{V1}/docs/nope/x.txt", CancellationToken.None)).Should().BeNull();
        (await view.StatAsync($"{V1}/a.txt/x", CancellationToken.None)).Should().BeNull();
        var open = () => view.OpenReadAsync($"{V1}/nope.txt", CancellationToken.None);
        await open.Should().ThrowAsync<StorageNotFoundException>();
    }

    [Fact]
    public async Task Lookups_follow_the_targets_case_sensitivity()
    {
        var insensitive = new PoolVersionView(Target(StorageCapabilities.EmptyDirectories), V1);
        (await insensitive.StatAsync($"{V1}/DOCS/B.TXT", CancellationToken.None)).Should().NotBeNull();
        (await ReadAsync(insensitive, $"{V1}/Docs/Sub/C.txt")).Should().Be("charlie");

        var sensitive = new PoolVersionView(Target(StorageCapabilities.EmptyDirectories | StorageCapabilities.CaseSensitive), V1);
        (await sensitive.StatAsync($"{V1}/DOCS/B.TXT", CancellationToken.None)).Should().BeNull();
        (await sensitive.StatAsync($"{V1}/docs/b.txt", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Missing_blob_reads_as_not_found()
    {
        var target = Target();
        await target.DeleteAsync([BlobOf("bravo")], CancellationToken.None);
        var view = new PoolVersionView(target, V1);

        (await view.StatAsync($"{V1}/docs/b.txt", CancellationToken.None)).Should().NotBeNull("the manifest lists it");
        var open = () => view.OpenReadAsync($"{V1}/docs/b.txt", CancellationToken.None);
        await open.Should().ThrowAsync<StorageNotFoundException>();
    }

    [Fact]
    public async Task Writes_are_not_supported()
    {
        var target = Target();
        var view = new PoolVersionView(target, V1);

        var create = () => view.CreateAsync($"{V1}/new.txt", new CreateOptions(), CancellationToken.None);
        await create.Should().ThrowAsync<NotSupportedException>();
        var createOutside = () => view.CreateAsync("elsewhere.txt", new CreateOptions(), CancellationToken.None);
        await createOutside.Should().ThrowAsync<NotSupportedException>();
        var delete = () => view.DeleteAsync([$"{V1}/a.txt"], CancellationToken.None);
        await delete.Should().ThrowAsync<NotSupportedException>();
        var ensure = () => view.EnsureDirectoryAsync($"{V1}/new", CancellationToken.None);
        await ensure.Should().ThrowAsync<NotSupportedException>();
        target.Files.Should().HaveCount(4, "three blobs and the manifest, nothing more");
    }

    [Fact]
    public async Task Manifest_passes_through()
    {
        var target = Target();
        target.AddFile("elsewhere.txt", Bytes("outside"));
        var view = new PoolVersionView(target, V1);

        (await ReadAsync(view, Manifest)).Should().Be(Encoding.UTF8.GetString(target.ReadAllBytes(Manifest)));
        (await view.StatAsync(Manifest, CancellationToken.None))!.Size.Should().Be(target.ReadAllBytes(Manifest).Length);
        (await ReadAsync(view, "elsewhere.txt")).Should().Be("outside");
        (await view.StatAsync(BlobOf("alpha"), CancellationToken.None)).Should().NotBeNull();
        view.Capabilities.Should().Be(StorageCapabilities.EmptyDirectories | StorageCapabilities.SetModifiedTime);
    }

    [Fact]
    public async Task Version_views_open_a_view_only_on_a_pool_target()
    {
        var pool = Target();
        pool.AddFile(PoolLayout.StoreFile, Bytes("""{"format":"pool","hash":"xxh128","version":1,"planId":"p1"}"""));
        (await VersionViews.OpenAsync(pool, V1, CancellationToken.None)).Should().BeOfType<PoolVersionView>();

        var full = new InMemoryStorage();
        (await VersionViews.OpenAsync(full, V1, CancellationToken.None)).Should().BeSameAs(full);
    }
}
