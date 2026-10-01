using FluentAssertions;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Indexing;

public class LiveScanTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly TempDir _tmp = new();
    private readonly string _source;

    public LiveScanTests() => _source = _tmp.CreateDir("source");

    public void Dispose() => _tmp.Dispose();

    private static IgnoreSettings Settings(params string[] patterns) => new() { Patterns = [.. patterns] };

    private void WriteFixture()
    {
        _tmp.WriteFile(@"source\a.txt", "alpha");
        _tmp.WriteFile(@"source\notes.tmp", "x");
        _tmp.WriteFile(@"source\keep.tmp", "keep");
        _tmp.WriteFile(@"source\Thumbs.db", "t");
        _tmp.WriteFile(@"source\build\out.bin", "0123456789");
        _tmp.WriteFile(@"source\build\deep\x.bin", "xx");
        _tmp.WriteFile(@"source\sub\.backupignore", "secret.txt");
        _tmp.WriteFile(@"source\sub\secret.txt", "s");
        _tmp.WriteFile(@"source\sub\b.bin", "bravo-bravo");
        _tmp.WriteFile(@"source\sub\inner\c.txt", "charlie");
        _tmp.CreateDir(@"source\empty");
        SettleFolderTimes();
    }

    /// <summary>
    /// A folder's time in its parent's listing can lag behind the folder itself right after files were written into
    /// it. Setting every folder's time explicitly, deepest first, makes both indexers see the same value.
    /// </summary>
    private void SettleFolderTimes()
    {
        var time = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        foreach (var directory in Directory.GetDirectories(_source, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
            Directory.SetLastWriteTimeUtc(directory, time);
    }

    private static LiveNode Child(LiveNode folder, string name) =>
        folder.Children.Single(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void ShouldEqual(IndexNode actual, IndexNode expected)
    {
        actual.Name.Should().Be(expected.Name, expected.RelativePath);
        actual.RelativePath.Should().Be(expected.RelativePath);
        actual.IsDirectory.Should().Be(expected.IsDirectory, expected.RelativePath);
        actual.Size.Should().Be(expected.Size, expected.RelativePath);
        actual.LastWriteUtc.Should().Be(expected.LastWriteUtc, expected.RelativePath);
        actual.Error.Should().Be(expected.Error, expected.RelativePath);
        actual.Children.Select(c => c.Name).Should().Equal(expected.Children.Select(c => c.Name), expected.RelativePath);
        for (var i = 0; i < expected.Children.Count; i++)
            ShouldEqual(actual.Children[i], expected.Children[i]);
    }

    private static void ShouldMatch(IPreviewEntry actual, EvaluatedNode expected)
    {
        var path = expected.Node.RelativePath;
        actual.State.Should().Be(ScanState.Done, path);
        actual.Status.Should().Be(expected.Status, path);
        actual.IncludedSize.Should().Be(expected.IncludedSize, path);
        actual.IgnoredSize.Should().Be(expected.IgnoredSize, path);
        actual.IncludedFiles.Should().Be(expected.IncludedFiles, path);
        actual.IgnoredFiles.Should().Be(expected.IgnoredFiles, path);
        actual.IgnoredByParent.Should().Be(expected.IgnoredByParent, path);
        actual.Pattern?.Text.Should().Be(expected.Pattern?.Text, path);
        actual.Pattern?.Origin.Should().Be(expected.Pattern?.Origin, path);
        (actual.Pattern is null).Should().Be(expected.Pattern is null, path);

        var children = actual.GetChildren();
        children.Select(c => c.Name).Should().BeEquivalentTo(expected.Children.Select(c => c.Node.Name), path);
        foreach (var child in expected.Children)
            ShouldMatch(children.Single(c => c.Name == child.Node.Name), child);
    }

    [Fact]
    public async Task A_finished_scan_gives_the_same_index_as_SourceIndexer()
    {
        WriteFixture();
        Junction.Create(Path.Combine(_source, "link"), Path.Combine(_source, "sub"));
        try
        {
            var scan = LiveScan.Start(_source, Settings("*.tmp", "!keep.tmp", "build/"), ["Thumbs.db"]);
            var index = await scan.Completion.WaitAsync(Timeout);
            var expected = SourceIndexer.Build(_source);

            index.Root.Should().Be(expected.Root);
            ShouldEqual(index.RootNode, expected.RootNode);
            index.FileCount.Should().Be(expected.FileCount);
            index.DirectoryCount.Should().Be(expected.DirectoryCount);
            index.IgnoreFiles.Select(f => (f.DirectoryRelativePath, string.Join("\n", f.Lines)))
                .Should().BeEquivalentTo(expected.IgnoreFiles.Select(f => (f.DirectoryRelativePath, string.Join("\n", f.Lines))));
            index.UnreadableIgnoreFiles.Should().BeEquivalentTo(expected.UnreadableIgnoreFiles);
            scan.Files.Should().Be(expected.FileCount);
            scan.Directories.Should().Be(expected.DirectoryCount);
            scan.WaitingFolders.Should().Be(0);
        }
        finally
        {
            Directory.Delete(Path.Combine(_source, "link"));
        }
    }

    [Fact]
    public async Task Live_values_equal_the_evaluation_of_the_finished_index()
    {
        WriteFixture();
        var settings = Settings("*.tmp", "!keep.tmp", "build/");
        string[] defaults = ["Thumbs.db"];

        var scan = LiveScan.Start(_source, settings, defaults);
        var index = await scan.Completion.WaitAsync(Timeout);
        var evaluated = IndexEvaluator.Evaluate(index, IgnoreMatcher.ForPlan(settings, defaults, index.IgnoreFiles));

        ShouldMatch(scan.Root, evaluated);
        Child(Child(scan.Root, "sub"), "secret.txt").Status.Should().Be(IncludeStatus.Ignored, "the nested .backupignore applies");
        Child(scan.Root, "keep.tmp").Status.Should().Be(IncludeStatus.Included, "it is re-included");
        Child(Child(scan.Root, "build"), "deep").IgnoredByParent.Should().BeTrue();
        scan.Root.Status.Should().Be(IncludeStatus.Partial);
    }

    [Fact]
    public void Evaluated_nodes_are_preview_entries_too()
    {
        WriteFixture();
        var index = SourceIndexer.Build(_source);
        IPreviewEntry root = IndexEvaluator.Evaluate(index, IgnoreMatcher.Create([], ["*.tmp"], []));

        root.State.Should().Be(ScanState.Done);
        root.RelativePath.Should().BeEmpty();
        root.IsDirectory.Should().BeTrue();
        root.GetChildren().Select(c => c.Name).Should().Contain(["a.txt", "sub"]);
    }

    [Fact]
    public async Task ChildCount_equals_the_number_of_children_for_live_and_evaluated_entries()
    {
        WriteFixture();
        var scan = LiveScan.Start(_source, Settings("*.tmp"), []);
        var index = await scan.Completion.WaitAsync(Timeout);
        IPreviewEntry evaluated = IndexEvaluator.Evaluate(index, IgnoreMatcher.Create([], ["*.tmp"], []));

        foreach (IPreviewEntry live in new[] { scan.Root, Child(scan.Root, "sub"), Child(scan.Root, "empty"), Child(scan.Root, "a.txt") })
            live.ChildCount.Should().Be(live.GetChildren().Count, live.RelativePath);
        scan.Root.ChildCount.Should().BeGreaterThan(0);
        evaluated.ChildCount.Should().Be(evaluated.GetChildren().Count);
        evaluated.ChildCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_folder_is_done_only_when_everything_below_it_is_done()
    {
        _tmp.WriteFile(@"source\a\b\f.txt", "f");
        _tmp.WriteFile(@"source\c\g.txt", "g");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var options = new LiveScanOptions
        {
            MaxParallel = 1,
            BeforeListing = folder =>
            {
                if (folder.RelativePath == "a/b")
                {
                    entered.Set();
                    release.Wait(Timeout);
                }
            },
        };

        var scan = LiveScan.Start(_source, Settings(), [], options);
        entered.Wait(Timeout).Should().BeTrue();

        var a = Child(scan.Root, "a");
        scan.Root.State.Should().Be(ScanState.Scanning);
        a.State.Should().Be(ScanState.Scanning);
        Child(a, "b").State.Should().Be(ScanState.Scanning);
        Child(scan.Root, "c").State.Should().Be(ScanState.Done, "shallower folders are listed first");
        scan.Root.TotalFiles.Should().Be(1, "only c/g.txt has been found so far");

        release.Set();
        await scan.Completion.WaitAsync(Timeout);
        scan.Root.State.Should().Be(ScanState.Done);
        a.State.Should().Be(ScanState.Done);
        scan.Root.TotalFiles.Should().Be(2);
    }

    [Fact]
    public async Task Folders_not_listed_yet_are_waiting()
    {
        _tmp.WriteFile(@"source\a\f.txt", "f");
        _tmp.WriteFile(@"source\c\g.txt", "g");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var options = new LiveScanOptions
        {
            MaxParallel = 1,
            BeforeListing = folder =>
            {
                if (folder.RelativePath == "a")
                {
                    entered.Set();
                    release.Wait(Timeout);
                }
            },
        };

        var scan = LiveScan.Start(_source, Settings(), [], options);
        entered.Wait(Timeout).Should().BeTrue();

        Child(scan.Root, "c").State.Should().Be(ScanState.Waiting);
        scan.WaitingFolders.Should().Be(1);

        release.Set();
        await scan.Completion.WaitAsync(Timeout);
    }

    [Fact]
    public async Task At_most_four_folders_are_listed_at_the_same_time()
    {
        for (var i = 0; i < 12; i++)
            _tmp.WriteFile($@"source\d{i}\f.txt", "f");
        var current = 0;
        var max = 0;
        var options = new LiveScanOptions
        {
            BeforeListing = _ =>
            {
                var now = Interlocked.Increment(ref current);
                int seen;
                while (now > (seen = Volatile.Read(ref max)) && Interlocked.CompareExchange(ref max, now, seen) != seen)
                {
                }
                Thread.Sleep(50);
                Interlocked.Decrement(ref current);
            },
        };

        var scan = LiveScan.Start(_source, Settings(), [], options);
        await scan.Completion.WaitAsync(Timeout);

        max.Should().BeInRange(2, LiveScan.DefaultParallelism);
    }

    [Fact]
    public async Task Cancellation_stops_all_workers()
    {
        for (var i = 0; i < 30; i++)
            _tmp.WriteFile($@"source\d{i}\f.txt", "f");
        using var cts = new CancellationTokenSource();
        var listed = 0;
        var options = new LiveScanOptions
        {
            BeforeListing = _ =>
            {
                if (Interlocked.Increment(ref listed) == 3)
                    cts.Cancel();
                Thread.Sleep(20);
            },
        };

        var scan = LiveScan.Start(_source, Settings(), [], options, cts.Token);
        var act = () => scan.Completion.WaitAsync(Timeout);

        await act.Should().ThrowAsync<OperationCanceledException>();
        listed.Should().BeLessThanOrEqualTo(3 + LiveScan.DefaultParallelism, "the workers stop at the next folder after the cancel");
    }

    [Fact]
    public async Task A_failing_worker_ends_the_scan_with_its_error()
    {
        _tmp.WriteFile(@"source\a\f.txt", "f");
        var options = new LiveScanOptions
        {
            BeforeListing = folder =>
            {
                if (folder.RelativePath == "a")
                    throw new InvalidOperationException("boom");
            },
        };

        var scan = LiveScan.Start(_source, Settings(), [], options);
        var act = () => scan.Completion.WaitAsync(Timeout);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        scan.Root.Children.Select(c => c.Name).Should().Contain("a", "what was found stays visible");
    }

    [Fact]
    public async Task A_large_tree_completes()
    {
        for (var i = 0; i < 10; i++)
            for (var j = 0; j < 10; j++)
                _tmp.WriteFile($@"source\d{i}\e{j}\f.txt", "f");

        var scan = LiveScan.Start(_source, Settings(), []);
        await scan.Completion.WaitAsync(Timeout);

        scan.Files.Should().Be(100);
        scan.Directories.Should().Be(110);
        scan.Root.TotalFiles.Should().Be(100);
        scan.Root.State.Should().Be(ScanState.Done);
    }

    [Fact]
    public async Task An_unreadable_ignore_file_is_reported_and_not_applied()
    {
        _tmp.WriteFile(@"source\sub\.backupignore", "secret.txt");
        _tmp.WriteFile(@"source\sub\secret.txt", "s");
        using (new FileStream(_tmp.PathOf(@"source\sub\.backupignore"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var scan = LiveScan.Start(_source, Settings(), []);
            var index = await scan.Completion.WaitAsync(Timeout);

            index.UnreadableIgnoreFiles.Should().Equal("sub/.backupignore");
            Child(Child(scan.Root, "sub"), "secret.txt").Status.Should().Be(IncludeStatus.Included);
        }
    }

    [Fact]
    public void A_missing_source_throws()
    {
        var act = () => LiveScan.Start(_tmp.PathOf("nowhere"), Settings(), []);

        act.Should().Throw<DirectoryNotFoundException>();
    }

    private (LiveScan Scan, System.Collections.Concurrent.ConcurrentQueue<string> Order, ManualResetEventSlim Entered,
        ManualResetEventSlim Release) StartBlockedAtB()
    {
        _tmp.WriteFile(@"source\a\x\y\f.txt", "f");
        _tmp.WriteFile(@"source\b\f.txt", "f");
        _tmp.WriteFile(@"source\c\f.txt", "f");
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var options = new LiveScanOptions
        {
            MaxParallel = 1,
            BeforeListing = folder =>
            {
                order.Enqueue(folder.RelativePath);
                if (folder.RelativePath == "b")
                {
                    entered.Set();
                    release.Wait(Timeout);
                }
            },
        };
        return (LiveScan.Start(_source, Settings(), [], options), order, entered, release);
    }

    [Fact]
    public async Task Without_priority_shallower_folders_come_first()
    {
        var (scan, order, entered, release) = StartBlockedAtB();
        using (entered)
        using (release)
        {
            entered.Wait(Timeout).Should().BeTrue();
            release.Set();
            await scan.Completion.WaitAsync(Timeout);
        }

        order.Should().Equal("", "a", "b", "c", "a/x", "a/x/y");
    }

    [Fact]
    public async Task A_prioritised_folder_and_what_is_found_in_it_are_listed_first()
    {
        var (scan, order, entered, release) = StartBlockedAtB();
        using (entered)
        using (release)
        {
            entered.Wait(Timeout).Should().BeTrue();
            var x = Child(Child(scan.Root, "a"), "x");
            x.State.Should().Be(ScanState.Waiting);

            // Before prioritizing: waiting folders are c and a/x, and b is being scanned (not counted as waiting)
            var waitingBefore = scan.WaitingFolders;
            scan.Prioritize(x);
            // Prioritize does not change WaitingFolders: it just re-queues an already-counted folder
            scan.WaitingFolders.Should().Be(waitingBefore);

            // Calling Prioritize again on an already-wanted folder is a no-op
            scan.Prioritize(x);
            scan.WaitingFolders.Should().Be(waitingBefore);

            release.Set();
            await scan.Completion.WaitAsync(Timeout);
        }

        order.Should().Equal("", "a", "b", "a/x", "a/x/y", "c");
        scan.WaitingFolders.Should().Be(0);
    }

    [Fact]
    public async Task Prioritising_a_folder_that_is_not_waiting_does_nothing()
    {
        _tmp.WriteFile(@"source\a\f.txt", "f");
        var scan = LiveScan.Start(_source, Settings(), []);
        await scan.Completion.WaitAsync(Timeout);

        var act = () => scan.Prioritize(scan.Root);

        act.Should().NotThrow();
        scan.WaitingFolders.Should().Be(0);
    }
}
