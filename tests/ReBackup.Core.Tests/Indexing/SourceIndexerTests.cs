using FluentAssertions;
using ReBackup.Core.Indexing;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Indexing;

public class SourceIndexerTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private string CreateSource()
    {
        _tmp.WriteFile(@"src\b.txt", "12345");
        _tmp.WriteFile(@"src\A.txt", "1");
        _tmp.WriteFile(@"src\sub\deep\c.bin", "1234567890");
        _tmp.CreateDir(@"src\empty");
        return _tmp.PathOf("src");
    }

    [Fact]
    public void Builds_a_tree_with_sizes_and_forward_slash_paths()
    {
        var index = SourceIndexer.Build(CreateSource());

        index.Root.Should().Be(_tmp.PathOf("src"));
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
    public void Collects_nested_ignore_files_with_their_folder()
    {
        var source = CreateSource();
        _tmp.WriteFile(@"src\.backupignore", "*.tmp\n# comment");
        _tmp.WriteFile(@"src\sub\deep\.BACKUPIGNORE", "!keep.tmp");

        var index = SourceIndexer.Build(source);

        index.IgnoreFiles.Should().HaveCount(2);
        index.IgnoreFiles.Single(f => f.DirectoryRelativePath == "").Lines.Should().Equal("*.tmp", "# comment");
        index.IgnoreFiles.Single(f => f.DirectoryRelativePath == "sub/deep").Lines.Should().Equal("!keep.tmp");
        index.RootNode.Children.Should().Contain(c => c.Name == ".backupignore", "ignore files are ordinary files");
    }

    [Fact]
    public void Missing_root_throws()
    {
        var act = () => SourceIndexer.Build(_tmp.PathOf("nope"));

        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void Cancellation_stops_the_scan()
    {
        var source = CreateSource();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => SourceIndexer.Build(source, cancellationToken: cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Reports_final_progress()
    {
        var reports = new List<IndexProgress>();

        SourceIndexer.Build(CreateSource(), new SyncProgress(reports.Add));

        reports.Should().NotBeEmpty();
        reports[^1].Files.Should().Be(3);
        reports[^1].Directories.Should().Be(3);
    }

    [Fact]
    public async Task BuildAsync_returns_the_same_result()
    {
        var index = await SourceIndexer.BuildAsync(CreateSource());

        index.FileCount.Should().Be(3);
    }

    private sealed class SyncProgress(Action<IndexProgress> onReport) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => onReport(value);
    }
}
