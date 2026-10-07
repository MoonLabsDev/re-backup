using System.Text;
using FluentAssertions;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;
using ReBackup.Storage.Tests.TestSupport;

namespace ReBackup.Storage.Tests;

/// <summary>The contract against a real temporary directory, plus the filesystem specifics.</summary>
public sealed class FileSystemStorageTests : StorageContractTests, IDisposable
{
    private readonly List<TempDir> _dirs = new();

    protected override IStorage CreateEmpty()
    {
        var dir = new TempDir();
        _dirs.Add(dir);
        return new FileSystemStorage(dir.Root);
    }

    public void Dispose()
    {
        foreach (var dir in _dirs) dir.Dispose();
    }

    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public void Capabilities_are_reported()
    {
        CreateEmpty().Capabilities.Should().Be(
            StorageCapabilities.FreeSpace | StorageCapabilities.Links | StorageCapabilities.EmptyDirectories | StorageCapabilities.SetModifiedTime);
    }

    [Fact]
    public void FullPathOf_returns_the_absolute_windows_path_and_validates()
    {
        var dir = new TempDir();
        _dirs.Add(dir);
        var storage = new FileSystemStorage(dir.Root);

        storage.FullPathOf("a/b.txt").Should().Be(Path.Combine(dir.Root, "a", "b.txt"));
        storage.FullPathOf("").Should().Be(dir.Root);
        var act = () => storage.FullPathOf("../x");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Junction_is_listed_as_link_and_not_descended()
    {
        var dir = new TempDir();
        _dirs.Add(dir);
        dir.WriteFile("real/inner.txt", "x");
        var link = dir.PathOf("link");
        Junction.Create(link, dir.PathOf("real"));
        var storage = new FileSystemStorage(dir.Root);
        try
        {
            var listed = await ListAsync(storage, "", true);

            listed.Single(e => e.Path == "link").IsLink.Should().BeTrue();
            listed.Select(e => e.Path).Should().NotContain("link/inner.txt");
            listed.Select(e => e.Path).Should().Contain("real/inner.txt");
            listed.Single(e => e.Path == "real").IsLink.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task Deleting_a_junction_removes_the_link_only()
    {
        var dir = new TempDir();
        _dirs.Add(dir);
        dir.WriteFile("real/inner.txt", "x");
        Junction.Create(dir.PathOf("link"), dir.PathOf("real"));
        var storage = new FileSystemStorage(dir.Root);

        await storage.DeleteAsync(new[] { "link" }, Ct);

        Directory.Exists(dir.PathOf("link")).Should().BeFalse();
        File.Exists(dir.PathOf("real/inner.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Temp_files_are_not_listed_or_statted()
    {
        var dir = new TempDir();
        _dirs.Add(dir);
        dir.WriteFile("a.txt", "x");
        dir.WriteFile("a.txt.0123abcd.rebackup-tmp", "partial");
        var storage = new FileSystemStorage(dir.Root);

        var listed = await ListAsync(storage, "", true);

        listed.Select(e => e.Path).Should().Equal("a.txt");
    }

    [Fact]
    public async Task Writer_uses_a_temp_file_next_to_the_target_until_commit()
    {
        var dir = new TempDir();
        _dirs.Add(dir);
        var storage = new FileSystemStorage(dir.Root);

        await using var writer = await storage.CreateAsync("sub/a.txt", new CreateOptions(), Ct);
        await writer.WriteAsync(Encoding.UTF8.GetBytes("x"), Ct);
        Directory.GetFiles(dir.PathOf("sub")).Should().ContainSingle().Which.Should().EndWith(".rebackup-tmp");
        await writer.CommitAsync(Ct);

        Directory.GetFiles(dir.PathOf("sub")).Select(Path.GetFileName).Should().Equal("a.txt");
    }

    [Fact]
    public async Task Missing_drive_root_throws_unavailable()
    {
        var letter = Enumerable.Range('D', 'Z' - 'D' + 1).Reverse().Select(c => (char)c)
            .FirstOrDefault(c => !Directory.Exists($"{c}:\\"));
        if (letter == default) return; // every drive letter is in use: nothing to test

        var storage = new FileSystemStorage($@"{letter}:\nope");

        var act = async () => await ListAsync(storage, "", false);

        await act.Should().ThrowAsync<StorageUnavailableException>();
    }

    [Fact]
    public async Task Missing_folder_under_existing_drive_throws_not_found()
    {
        var dir = new TempDir();
        _dirs.Add(dir);
        var storage = new FileSystemStorage(Path.Combine(dir.Root, "not-created"));

        var act = async () => await ListAsync(storage, "", false);

        await act.Should().ThrowAsync<StorageNotFoundException>();
    }

    [Fact]
    public async Task Long_and_non_ascii_paths_round_trip()
    {
        var dir = new TempDir();
        _dirs.Add(dir);
        var storage = new FileSystemStorage(dir.Root);
        var path = string.Join('/', Enumerable.Repeat("Ünïcode ordner", 20)) + "/ä ö ü.txt";
        path.Length.Should().BeGreaterThan(300);
        var bytes = Encoding.UTF8.GetBytes("Grüße ✓");

        await using (var writer = await storage.CreateAsync(path, new CreateOptions(), Ct))
        {
            await writer.WriteAsync(bytes, Ct);
            await writer.CommitAsync(Ct);
        }

        var listed = await ListAsync(storage, "", true);
        listed.Select(e => e.Path).Should().Contain(path);
        await using var read = await storage.OpenReadAsync(path, Ct);
        var ms = new MemoryStream();
        await read.CopyToAsync(ms, Ct);
        ms.ToArray().Should().Equal(bytes);
    }

    [Fact]
    public async Task Free_space_is_null_for_unc_roots()
    {
        var storage = new FileSystemStorage(@"\\server\share\x");

        (await storage.GetFreeSpaceAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Free_space_is_known_for_a_local_root()
    {
        var storage = CreateEmpty();

        (await storage.GetFreeSpaceAsync(Ct)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Source_file_open_for_writing_by_another_process_can_still_be_read()
    {
        var dir = new TempDir();
        _dirs.Add(dir);
        var path = dir.WriteFile("busy.txt", "content");
        var storage = new FileSystemStorage(dir.Root);
        using var other = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        await using var read = await storage.OpenReadAsync("busy.txt", Ct);
        using var reader = new StreamReader(read);

        (await reader.ReadToEndAsync(Ct)).Should().Be("content");
    }

    [Fact]
    public async Task Open_read_of_a_file_without_sharing_throws_locked()
    {
        var dir = new TempDir();
        _dirs.Add(dir);
        var path = dir.WriteFile("busy.txt", "content");
        var storage = new FileSystemStorage(dir.Root);
        using var other = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var act = () => storage.OpenReadAsync("busy.txt", Ct);

        await act.Should().ThrowAsync<StorageLockedException>();
    }
}
