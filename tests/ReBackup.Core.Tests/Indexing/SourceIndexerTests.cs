using FluentAssertions;
using ReBackup.Core.Indexing;
using ReBackup.Core.Ignore;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Indexing;

public class SourceIndexerTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static FileSystemStorage Fs(string path) => new(path);

    private string CreateSource()
    {
        _tmp.WriteFile(@"src\b.txt", "12345");
        _tmp.WriteFile(@"src\A.txt", "1");
        _tmp.WriteFile(@"src\sub\deep\c.bin", "1234567890");
        _tmp.CreateDir(@"src\empty");
        return _tmp.PathOf("src");
    }

    [Fact]
    public async Task Builds_a_tree_with_sizes_and_forward_slash_paths()
    {
        var index = await SourceIndexer.BuildAsync(Fs(CreateSource()));

        index.Root.Should().Be("src", "the root display name, not an address");
        index.FileCount.Should().Be(3);
        index.DirectoryCount.Should().Be(3);

        var root = index.RootNode;
        root.Name.Should().Be("src");
        root.RelativePath.Should().Be("");
        root.IsDirectory.Should().BeTrue();
        root.Children.Select(c => c.Name).Should().Equal("A.txt", "b.txt", "empty", "sub");

        var b = root.Children.Single(c => c.Name == "b.txt");
        b.IsDirectory.Should().BeFalse();
        b.Size.Should().Be(5);
        b.RelativePath.Should().Be("b.txt");
        b.LastWriteUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        var c = root.Children.Single(n => n.Name == "sub").Children.Single().Children.Single();
        c.RelativePath.Should().Be("sub/deep/c.bin");
        c.Size.Should().Be(10);

        root.Children.Single(n => n.Name == "empty").Children.Should().BeEmpty();
    }

    [Fact]
    public async Task Collects_nested_ignore_files_with_their_folder()
    {
        var source = CreateSource();
        _tmp.WriteFile(@"src\.backupignore", "*.tmp\n# comment");
        _tmp.WriteFile(@"src\sub\deep\.BACKUPIGNORE", "!keep.tmp");

        var index = await SourceIndexer.BuildAsync(Fs(source));

        index.IgnoreFiles.Should().HaveCount(2);
        index.IgnoreFiles.Single(f => f.DirectoryRelativePath == "").Lines.Should().Equal("*.tmp", "# comment");
        index.IgnoreFiles.Single(f => f.DirectoryRelativePath == "sub/deep").Lines.Should().Equal("!keep.tmp");
        index.RootNode.Children.Should().Contain(c => c.Name == ".backupignore", "ignore files are ordinary files");
    }

    [Fact]
    public async Task Missing_root_throws()
    {
        var act = () => SourceIndexer.BuildAsync(Fs(_tmp.PathOf("nope")));

        await act.Should().ThrowAsync<StorageNotFoundException>();
    }

    [Fact]
    public async Task Cancellation_stops_the_scan()
    {
        var source = CreateSource();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => SourceIndexer.BuildAsync(Fs(source), ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Reports_final_progress()
    {
        var reports = new List<IndexProgress>();

        await SourceIndexer.BuildAsync(Fs(CreateSource()), new SyncProgress(reports.Add));

        reports.Should().NotBeEmpty();
        reports[^1].Files.Should().Be(3);
        reports[^1].Directories.Should().Be(3);
    }

    [Fact]
    public async Task BuildAsync_returns_the_same_result()
    {
        var index = await SourceIndexer.BuildAsync(Fs(CreateSource()));

        index.FileCount.Should().Be(3);
    }

    [Fact]
    public async Task Unreadable_ignore_file_is_reported()
    {
        var source = CreateSource();
        var ignoreFile = _tmp.WriteFile(@"src\sub\.backupignore", "*.tmp");
        using var locked = new FileStream(ignoreFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var index = await SourceIndexer.BuildAsync(Fs(source));

        index.UnreadableIgnoreFiles.Should().Equal("sub/.backupignore");
        index.IgnoreFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task Readable_ignore_files_are_not_reported_as_unreadable()
    {
        var source = CreateSource();
        _tmp.WriteFile(@"src\.backupignore", "*.tmp");

        (await SourceIndexer.BuildAsync(Fs(source))).UnreadableIgnoreFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task Folders_nested_deeper_than_the_limit_are_not_scanned()
    {
        var source = _tmp.PathOf("src");
        _tmp.CreateDir("src");

        // Build a chain of nested folders
        var path = source;
        for (int i = 0; i < SourceIndexer.MaxDepth + 2; i++)
        {
            path = Path.Combine(path, "d");
        }

        // Create the deep chain - use \\?\ prefix if needed for long paths
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (PathTooLongException)
        {
            Directory.CreateDirectory(@"\\?" + Path.GetFullPath(path));
        }

        // Put a file in the deepest folder
        File.WriteAllText(Path.Combine(path, "deep.txt"), "content");

        // Build the index
        var index = await SourceIndexer.BuildAsync(Fs(source));
        index.Should().NotBeNull("build does not throw");

        // Walk the chain iteratively to find the cutoff point
        var current = index.RootNode;
        int depth = 0;
        while (current.Children.SingleOrDefault(c => c.Name == "d") is { } dNode)
        {
            // If this child is blocked, don't increment depth or move to it
            if (dNode.Error == "Folder nesting too deep.")
                break;

            depth++;
            current = dNode;
        }

        // The node at depth MaxDepth should have a child "d" that is blocked
        depth.Should().Be(SourceIndexer.MaxDepth, "we should traverse down to MaxDepth");

        // The node at depth MaxDepth should have a child "d" node that has Error set
        var blockedNode = current.Children.Single(c => c.Name == "d");
        blockedNode.Error.Should().Be("Folder nesting too deep.");
        blockedNode.Children.Should().BeEmpty("blocked node has no children");
        blockedNode.IsDirectory.Should().BeTrue();
    }

    [Fact]
    public async Task Junction_folders_are_not_followed()
    {
        _tmp.WriteFile(@"src\real\inside.txt", "x");
        var source = _tmp.PathOf("src");
        var linkPath = _tmp.PathOf(@"src\link");
        var targetPath = _tmp.PathOf(@"src\real");

        var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using (var process = System.Diagnostics.Process.Start(startInfo)!)
        {
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                Assert.Fail($"mklink failed ({process.ExitCode}): {output}");
        }

        try
        {
            var index = await SourceIndexer.BuildAsync(Fs(source));

            var link = index.RootNode.Children.Single(c => c.Name == "link");
            link.IsDirectory.Should().BeTrue();
            link.Children.Should().BeEmpty();
            link.Error.Should().Be("Link is not followed.");
            index.RootNode.Children.Single(c => c.Name == "real").Children.Should().ContainSingle(c => c.Name == "inside.txt");
            index.FileCount.Should().Be(1);
        }
        finally
        {
            Directory.Delete(linkPath);
        }
    }

    private static InMemoryStorage Memory(StorageCapabilities capabilities = StorageCapabilities.EmptyDirectories | StorageCapabilities.SetModifiedTime)
    {
        var storage = new InMemoryStorage(capabilities);
        storage.AddFile("a.txt", [1, 2, 3]);
        storage.AddFile("sub/b.bin", [1, 2]);
        storage.AddFile("sub/.backupignore", "*.tmp\n!keep.tmp"u8.ToArray());
        return storage;
    }

    [Fact]
    public async Task Indexes_in_memory_source_with_nested_ignore_file()
    {
        var index = await SourceIndexer.BuildAsync(Memory(), rootName: "memory");

        index.Root.Should().Be("memory");
        index.RootNode.Name.Should().Be("memory");
        index.FileCount.Should().Be(3);
        index.DirectoryCount.Should().Be(1);
        index.RootNode.Children.Select(c => c.Name).Should().Equal("a.txt", "sub");
        index.RootNode.Children.Single(c => c.Name == "sub").Children.Select(c => c.RelativePath)
            .Should().Equal("sub/.backupignore", "sub/b.bin");
        index.IgnoreFiles.Should().ContainSingle().Which.Should().Match<NestedIgnoreFile>(f =>
            f.DirectoryRelativePath == "sub" && f.Lines.SequenceEqual(new[] { "*.tmp", "!keep.tmp" }));
    }

    [Fact]
    public async Task Case_sensitive_storage_sorts_ordinal()
    {
        var names = new[] { "b.txt", "A.txt", "a.txt", "B.txt" };

        var sensitive = new InMemoryStorage(StorageCapabilities.CaseSensitive);
        var insensitive = new InMemoryStorage();
        foreach (var name in names)
        {
            sensitive.AddFile(name, [1]);
            insensitive.AddFile(name, [1]);
        }

        (await SourceIndexer.BuildAsync(sensitive)).RootNode.Children.Select(c => c.Name)
            .Should().Equal("A.txt", "B.txt", "a.txt", "b.txt");
        (await SourceIndexer.BuildAsync(insensitive)).RootNode.Children.Select(c => c.Name)
            .Should().BeEquivalentTo(names).And.BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_listed_gets_an_error_and_keeps_its_siblings()
    {
        var storage = new FaultyStorage(Memory())
        {
            Before = (op, path) =>
            {
                if (op == "list" && path == "sub")
                    throw new StorageAccessDeniedException(path, "denied");
            },
        };

        var index = await SourceIndexer.BuildAsync(storage);

        index.RootNode.Error.Should().BeNull();
        index.RootNode.Children.Select(c => c.Name).Should().Equal("a.txt", "sub");
        index.RootNode.Children.Single(c => c.Name == "sub").Error.Should().Be("denied");
    }

    [Fact]
    public async Task An_unavailable_source_aborts_the_scan()
    {
        var storage = new FaultyStorage(Memory())
        {
            Before = (op, path) =>
            {
                if (op == "list" && path == "sub")
                    throw new StorageUnavailableException(path);
            },
        };

        var act = () => SourceIndexer.BuildAsync(storage);

        await act.Should().ThrowAsync<StorageUnavailableException>();
    }

    [Fact]
    public async Task Names_the_storage_cannot_address_do_not_crash_the_scan()
    {
        var memory = Memory();
        memory.AddFile("odd./x.txt", [1]);
        var storage = new FaultyStorage(memory)
        {
            Before = (op, path) =>
            {
                if (path.StartsWith("odd.", StringComparison.Ordinal))
                    throw new ArgumentException("A path segment must not end with a space or a dot.");
            },
        };

        var index = await SourceIndexer.BuildAsync(storage);

        index.RootNode.Children.Select(c => c.Name).Should().Contain("odd.");
        index.RootNode.Children.Single(c => c.Name == "odd.").Error.Should().NotBeNullOrEmpty();
        index.FileCount.Should().Be(3);
    }

    [Fact]
    public async Task An_ignore_file_with_an_unaddressable_path_is_unreadable()
    {
        var storage = new FaultyStorage(Memory())
        {
            Before = (op, path) =>
            {
                if (op == "open" && path == "sub/.backupignore")
                    throw new ArgumentException("not addressable");
            },
        };

        var index = await SourceIndexer.BuildAsync(storage);

        index.UnreadableIgnoreFiles.Should().Equal("sub/.backupignore");
        index.IgnoreFiles.Should().BeEmpty();
    }

    private sealed class SyncProgress(Action<IndexProgress> onReport) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => onReport(value);
    }
}
