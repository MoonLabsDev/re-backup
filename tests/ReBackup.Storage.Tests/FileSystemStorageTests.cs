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

    [Theory]
    [InlineData("relative")]
    [InlineData("C:relative")]
    [InlineData(@"\rooted-no-drive")]
    public void A_root_that_is_not_fully_qualified_is_rejected(string root)
    {
        // Such a root would resolve against the process's (or the drive's) current directory.
        var act = () => new FileSystemStorage(root);

        act.Should().Throw<ArgumentException>();
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
    public async Task Expected_stamp_conflict_leaves_no_temp_file_and_no_new_directory()
    {
        var dir = new TempDir();
        _dirs.Add(dir);
        dir.WriteFile("a.txt", "old");
        var storage = new FileSystemStorage(dir.Root);
        var stamp = (await storage.StatAsync("a.txt", Ct))!.Stamp;
        await File.WriteAllTextAsync(dir.PathOf("a.txt"), "changed by someone else");

        await using (var writer = await storage.CreateAsync("a.txt", new CreateOptions(Overwrite: true, ExpectedStamp: stamp), Ct))
        {
            await writer.WriteAsync(Encoding.UTF8.GetBytes("mine"), Ct);
            var commit = () => writer.CommitAsync(Ct);
            await commit.Should().ThrowAsync<StorageConflictException>();
        }
        var missing = () => storage.CreateAsync("new/sub/b.txt", new CreateOptions(Overwrite: true, ExpectedStamp: "1:2"), Ct);
        await missing.Should().ThrowAsync<StorageConflictException>();

        Directory.EnumerateFiles(dir.Root, "*" + FileSystemStorage.TempSuffix, SearchOption.AllDirectories).Should().BeEmpty();
        Directory.Exists(dir.PathOf("new")).Should().BeFalse();
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

    [Theory]
    [InlineData("C:/x")]
    [InlineData("a:b")]
    [InlineData("C:")]
    [InlineData("a|b")]
    [InlineData("a<b")]
    [InlineData(" ")]
    [InlineData("...")]
    [InlineData("a ")]
    [InlineData("x.")]
    [InlineData("a/b.")]
    public async Task Paths_that_could_leave_the_root_are_rejected_by_every_operation(string path)
    {
        var storage = (FileSystemStorage)CreateEmpty();

        var calls = new List<Func<Task>>
        {
            () => storage.StatAsync(path, Ct),
            async () => await ListAsync(storage, path, true),
            () => storage.OpenReadAsync(path, Ct),
            () => storage.CreateAsync(path, new CreateOptions(), Ct),
            () => storage.DeleteAsync(new[] { path }, Ct),
            () => storage.EnsureDirectoryAsync(path, Ct),
            () => { storage.FullPathOf(path); return Task.CompletedTask; },
        };
        foreach (var call in calls)
            await call.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Stat_on_an_offline_root_throws_unavailable()
    {
        var letter = Enumerable.Range('D', 'Z' - 'D' + 1).Reverse().Select(c => (char)c)
            .FirstOrDefault(c => !Directory.Exists($"{c}:\\"));
        if (letter == default) return; // every drive letter is in use: nothing to test
        var storage = new FileSystemStorage($@"{letter}:\nope");

        var act = () => storage.StatAsync("a.txt", Ct);

        await act.Should().ThrowAsync<StorageUnavailableException>();
    }

    [Fact]
    public async Task Stat_of_a_missing_file_under_an_existing_root_is_null()
    {
        (await CreateEmpty().StatAsync("nope.txt", Ct)).Should().BeNull();
    }

    [Theory]
    [InlineData(21)]
    [InlineData(53)]
    [InlineData(59)]
    [InlineData(64)]
    [InlineData(67)]
    [InlineData(121)]
    [InlineData(1167)]
    public void Network_and_device_errors_map_to_unavailable(int code)
    {
        FileSystemErrors.Map(new IOException("x", unchecked((int)0x80070000) | code), "a", Path.GetTempPath())
            .Should().BeOfType<StorageUnavailableException>();
        FileSystemErrors.Map(new IOException("x", code), "a", Path.GetTempPath())
            .Should().BeOfType<StorageUnavailableException>();
    }

    [Fact]
    public void Unknown_io_errors_map_to_io_exception_or_unavailable_when_the_root_is_gone()
    {
        var other = new IOException("boom", 5555);
        FileSystemErrors.Map(other, "a", Path.GetTempPath()).Should().BeOfType<StorageIOException>().Which.InnerException.Should().BeSameAs(other);
        FileSystemErrors.Map(other, "a", @"Z:\x").Should().Match<StorageException>(e => e is StorageIOException || e is StorageUnavailableException);
        FileSystemErrors.Map(new InvalidOperationException(), "a", Path.GetTempPath()).Should().BeNull();
    }

    [Fact]
    public async Task Disk_full_while_committing_surfaces_full_and_leaves_no_temp_file()
    {
        var dir = new TempDir();
        _dirs.Add(dir);
        var temp = dir.PathOf("a.txt.00000001.rebackup-tmp");
        var inner = new FileStream(temp, FileMode.CreateNew, FileAccess.Write);
        var storage = new FileSystemStorage(dir.Root);
        var writer = new FileSystemWriter(storage, "a.txt", dir.PathOf("a.txt"), temp, new FullDiskStream(inner), new CreateOptions());

        Func<Task> act = async () =>
        {
            await using (writer)
            {
                await writer.WriteAsync(new byte[] { 1, 2, 3 }, Ct);
                await writer.CommitAsync(Ct);
            }
        };

        await act.Should().ThrowAsync<StorageFullException>();
        File.Exists(temp).Should().BeFalse();
        File.Exists(dir.PathOf("a.txt")).Should().BeFalse();
    }

    /// <summary>A stream whose flush and dispose fail like a full disk.</summary>
    private sealed class FullDiskStream : Stream
    {
        private readonly FileStream _inner;
        public FullDiskStream(FileStream inner) => _inner = inner;
        private static IOException Full() => new("disk full", unchecked((int)0x80070070));
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => throw Full();
        public override Task FlushAsync(CancellationToken cancellationToken) => throw Full();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw Full();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            throw Full();
        }
    }

    [Fact]
    public async Task Read_failures_surface_as_storage_exceptions()
    {
        var dir = new TempDir();
        _dirs.Add(dir);
        var storage = new FileSystemStorage(dir.Root);
        await using var read = new FileSystemReadStream(storage, "a.txt", new FailingReadStream(new IOException("lock", unchecked((int)0x80070021))));

        var asyncRead = async () => await read.ReadAsync(new byte[4], Ct);
        var syncRead = () => read.Read(new byte[4], 0, 4);
        var length = () => read.Length;

        await asyncRead.Should().ThrowAsync<StorageLockedException>();
        syncRead.Should().Throw<StorageLockedException>();
        length.Should().Throw<StorageLockedException>();
        read.CanWrite.Should().BeFalse();
    }

    [Fact]
    public async Task Opened_file_reads_through_the_wrapper()
    {
        var dir = new TempDir();
        _dirs.Add(dir);
        dir.WriteFile("a.txt", "abc");
        var storage = new FileSystemStorage(dir.Root);

        await using var read = await storage.OpenReadAsync("a.txt", Ct);

        read.Should().BeOfType<FileSystemReadStream>();
        read.Length.Should().Be(3);
        read.Seek(1, SeekOrigin.Begin);
        read.ReadByte().Should().Be('b');
    }

    private sealed class FailingReadStream : Stream
    {
        private readonly IOException _error;
        public FailingReadStream(IOException error) => _error = error;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => throw _error;
        public override long Position { get => throw _error; set => throw _error; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw _error;
        public override long Seek(long offset, SeekOrigin origin) => throw _error;
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
