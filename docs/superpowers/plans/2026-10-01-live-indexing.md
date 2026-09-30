# Live, Parallel Indexing of the Preview — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Ignore & Preview tab scans the source like WinDirStat: four folders at a time, folders shown at once as "waiting"/"loading", sizes and ignore status growing live, the tree browsable during the scan; a folder expanded while it waits is scanned first.

**Architecture:** Core gets `LiveScan` (four workers, a priority queue, a tree of `LiveNode`s whose totals are updated atomically and whose ignore decision is made when an entry is found) and a small interface `IPreviewEntry` that both `LiveNode` and the existing `EvaluatedNode` implement. When the scan is finished it hands over the same `SourceIndex` as `SourceIndexer`, so pattern edits and the treemap keep working as today. The App's preview tree works on `IPreviewEntry`, re-reads the live tree four times a second, and shows loading folders with an hourglass. The backup run keeps `SourceIndexer`.

**Tech Stack:** .NET 9, WPF, CommunityToolkit.Mvvm 8.4.2, xUnit, FluentAssertions 7.0.0.

**Spec:** `docs/superpowers/specs/2026-10-01-live-indexing-design.md` (and `2026-09-30-rebackup-design.md` §5, §10.2 tab 3)

## Global Constraints

- Target frameworks: `net9.0` for Core and Tests, `net9.0-windows` for App. Nullable and implicit usings are on. Builds must stay at 0 warnings; check with `dotnet build --no-incremental`.
- Only the Ignore & Preview tab uses `LiveScan`. `SourceIndexer`, `BackupRunner` and the backup run stay unchanged.
- Parallelism: 4 folders at a time (`LiveScan.DefaultParallelism = 4`), not configurable in the UI.
- A finished `LiveScan` must give the same `SourceIndex` as `SourceIndexer.Build` for the same folder (same tree, sizes, errors, counts, nested ignore files, unreadable ignore files), and its live values must equal `IndexEvaluator.Evaluate` on that index with the same patterns.
- Rules taken over from `SourceIndexer`: links are not followed ("Link is not followed."), at most `SourceIndexer.MaxDepth` levels ("Folder nesting too deep."), a folder that cannot be read keeps what was listed and gets the exception message as error, a `.backupignore` that cannot be read is recorded in `UnreadableIgnoreFiles`.
- During a scan the patterns known at its start are applied; edits made meanwhile are applied once it is finished. The treemap appears only after the scan.
- Workers never touch UI objects; the UI only reads.
- FluentAssertions stays pinned to 7.0.0.
- Every commit message ends with a `Co-Authored-By: Claude <model> <noreply@anthropic.com>` trailer naming the authoring model, separated from the subject by a blank line (two `-m` arguments).
- Tests that depend on timing use events and generous timeouts (5–30 s), never `Thread.Sleep` to wait for a state; a short sleep inside a test hook to keep a worker busy is fine.
- The App has no automated tests. App tasks are verified by build, the Core suite, a startup smoke test, and the manual checklist in the task.

Startup smoke test (App tasks): build, start `src/ReBackup.App/bin/Debug/net9.0-windows/ReBackup.App.exe --minimized`, wait 5 s, confirm the process is still running, then stop it. Make sure no `ReBackup.App` process is left behind — and that none was running before you built (a running copy locks the output). Never start a backup.

Ruling recorded while writing the plan: the spec says an unreadable source folder ends the scan with an error. `SourceIndexer` instead records the error on the root node and finishes, and the equivalence constraint above wins: `LiveScan` does the same (a missing source still throws at `Start`). The preview shows the root's error as today.

## File Structure

```
src/ReBackup.Core/Indexing/
  PreviewEntry.cs      IPreviewEntry, ScanState
  LiveNode.cs          one entry of a running scan
  LiveScan.cs          LiveScanOptions, LiveScan (workers, queue, ignore decisions, hand-over)
  IndexEvaluator.cs    (modify) EvaluatedNode implements IPreviewEntry
src/ReBackup.App/
  ViewModels/LoadingPlaceholder.cs       the "loading …" row of an expanded folder with nothing listed yet
  ViewModels/PreviewRowViewModel.cs      (rewrite) row over IPreviewEntry, live values
  ViewModels/PreviewTreeViewModel.cs     (rewrite) rows over IPreviewEntry, Refresh, LoadingFolderExpanded
  ViewModels/IgnorePreviewViewModel.cs   (modify, then rewrite) SelectedNode as IPreviewEntry; live scan
  ViewModels/PlanEditorViewModel.cs      (modify) SelectedEntry over IPreviewEntry
  Controls/TreemapControl.cs             (modify) Selected as IPreviewEntry
  Views/IgnorePreviewView.xaml           (modify) loading display, notes
tests/ReBackup.Core.Tests/Indexing/
  LiveScanTests.cs
```

---

### Task 1: LiveScan

**Files:**
- Create: `src/ReBackup.Core/Indexing/PreviewEntry.cs`, `src/ReBackup.Core/Indexing/LiveNode.cs`, `src/ReBackup.Core/Indexing/LiveScan.cs`
- Modify: `src/ReBackup.Core/Indexing/IndexEvaluator.cs`
- Test: `tests/ReBackup.Core.Tests/Indexing/LiveScanTests.cs` (create)

**Interfaces:**
- Consumes: `SourceIndex`, `IndexNode`, `SourceIndexer.MaxDepth`, `EvaluatedNode`, `IncludeStatus`, `IndexEvaluator.Evaluate`, `IgnoreMatcher.ForPlan/MatchEntry`, `NestedIgnoreFile`, `IgnoreOrigins`, `IgnorePattern`, `IgnoreSettings`, `PathUtil.Normalize`; test helpers `TempDir`, `Junction.Create`.
- Produces:
  - `enum ScanState { Waiting, Scanning, Done }`
  - `interface IPreviewEntry` — `Name`, `RelativePath`, `IsDirectory`, `Error`, `State`, `Status`, `Pattern`, `IgnoredByParent`, `IncludedSize`, `IgnoredSize`, `IncludedFiles`, `IgnoredFiles`, `TotalSize`, `TotalFiles`, `IReadOnlyList<IPreviewEntry> GetChildren()`
  - `EvaluatedNode : IPreviewEntry` (State is always Done)
  - `sealed class LiveNode : IPreviewEntry` with `Parent`, `Children` (copy), `Depth`, `Size`, `LastWriteUtc`, `IsIgnored`
  - `sealed record LiveScanOptions { int MaxParallel = 4; Action<LiveNode>? BeforeListing }`
  - `sealed class LiveScan`: `const int DefaultParallelism = 4`; `static LiveScan Start(string root, IgnoreSettings settings, IReadOnlyList<string> globalDefaults, LiveScanOptions? options = null, CancellationToken cancellationToken = default)` (throws `DirectoryNotFoundException` for a missing root); `string RootPath`; `LiveNode Root`; `Task<SourceIndex> Completion` (canceled on cancellation, faulted when a worker failed); `int Files`, `int Directories`, `int WaitingFolders`

- [ ] **Step 1: Write the failing tests**

Create `tests/ReBackup.Core.Tests/Indexing/LiveScanTests.cs`:

```csharp
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
    public async Task Evaluated_nodes_are_preview_entries_too()
    {
        WriteFixture();
        var index = SourceIndexer.Build(_source);
        IPreviewEntry root = IndexEvaluator.Evaluate(index, IgnoreMatcher.Create([], ["*.tmp"], []));

        root.State.Should().Be(ScanState.Done);
        root.RelativePath.Should().BeEmpty();
        root.IsDirectory.Should().BeTrue();
        root.GetChildren().Select(c => c.Name).Should().Contain(["a.txt", "sub"]);
        await Task.CompletedTask;
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
        listed.Should().BeLessThan(31);
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
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~LiveScanTests"`
Expected: FAIL — compile errors, `LiveScan` does not exist.

- [ ] **Step 3: The preview entry interface**

Create `src/ReBackup.Core/Indexing/PreviewEntry.cs`:

```csharp
using ReBackup.Core.Ignore;

namespace ReBackup.Core.Indexing;

public enum ScanState
{
    /// <summary>Found by its parent folder, not listed yet.</summary>
    Waiting,

    /// <summary>Listed; something below it is not finished yet.</summary>
    Scanning,

    /// <summary>The entry and everything below it is finished.</summary>
    Done,
}

/// <summary>An entry of the preview tree: a finished evaluation or an entry of a scan that is still running.</summary>
public interface IPreviewEntry
{
    string Name { get; }

    /// <summary>Path relative to the source root, with forward slashes; "" for the root.</summary>
    string RelativePath { get; }

    bool IsDirectory { get; }
    string? Error { get; }
    ScanState State { get; }
    IncludeStatus Status { get; }
    IgnorePattern? Pattern { get; }
    bool IgnoredByParent { get; }
    long IncludedSize { get; }
    long IgnoredSize { get; }
    int IncludedFiles { get; }
    int IgnoredFiles { get; }
    long TotalSize { get; }
    int TotalFiles { get; }

    /// <summary>The children as they are now; for a running scan a copy that does not change afterwards.</summary>
    IReadOnlyList<IPreviewEntry> GetChildren();
}
```

In `src/ReBackup.Core/Indexing/IndexEvaluator.cs`, change `public sealed class EvaluatedNode` to `public sealed class EvaluatedNode : IPreviewEntry` and add at the end of the class body (below `TotalFiles`):

```csharp

    public string Name => Node.Name;
    public string RelativePath => Node.RelativePath;
    public bool IsDirectory => Node.IsDirectory;
    public string? Error => Node.Error;

    /// <summary>An evaluation is always of a finished index.</summary>
    public ScanState State => ScanState.Done;

    public IReadOnlyList<IPreviewEntry> GetChildren() => Children;
```

- [ ] **Step 4: LiveNode**

Create `src/ReBackup.Core/Indexing/LiveNode.cs`:

```csharp
using ReBackup.Core.Ignore;

namespace ReBackup.Core.Indexing;

/// <summary>
/// A file or folder of a running <see cref="LiveScan"/>. Every member can be read from any thread while the scan
/// runs; totals only grow. The ignore decision is made when the entry is found and does not change.
/// </summary>
public sealed class LiveNode : IPreviewEntry
{
    private readonly object _gate = new();
    private readonly List<LiveNode> _children = [];
    private readonly long _size;
    private long _includedSize;
    private long _ignoredSize;
    private int _includedFiles;
    private int _ignoredFiles;
    private int _skippedEntries;
    private int _state;
    private string? _error;

    // Folders below this one that are not Done yet, plus one for this folder until it has been listed.
    internal int PendingFolders = 1;
    internal volatile bool Wanted;
    internal IgnoreMatcher? Matcher;
    internal IReadOnlyList<NestedIgnoreFile> IgnoreChain = [];

    internal LiveNode(LiveNode? parent, string name, string relativePath, bool isDirectory, long size,
        DateTime lastWriteUtc, int depth, bool isIgnored, IgnorePattern? pattern, bool ignoredByParent)
    {
        Parent = parent;
        Name = name;
        RelativePath = relativePath;
        IsDirectory = isDirectory;
        _size = isDirectory ? 0 : size;
        LastWriteUtc = lastWriteUtc;
        Depth = depth;
        IsIgnored = isIgnored;
        Pattern = pattern;
        IgnoredByParent = ignoredByParent;
        if (!isDirectory)
        {
            _state = (int)ScanState.Done;
            if (isIgnored)
            {
                _ignoredSize = size;
                _ignoredFiles = 1;
            }
            else
            {
                _includedSize = size;
                _includedFiles = 1;
            }
        }
    }

    public LiveNode? Parent { get; }
    public string Name { get; }
    public string RelativePath { get; }
    public bool IsDirectory { get; }
    public DateTime LastWriteUtc { get; }
    public int Depth { get; }

    /// <summary>The file size; 0 for folders.</summary>
    public long Size => _size;

    public bool IsIgnored { get; }
    public IgnorePattern? Pattern { get; }
    public bool IgnoredByParent { get; }
    public string? Error => Volatile.Read(ref _error);
    public ScanState State => (ScanState)Volatile.Read(ref _state);

    public IncludeStatus Status =>
        IsIgnored ? IncludeStatus.Ignored
        : Volatile.Read(ref _skippedEntries) > 0 ? IncludeStatus.Partial
        : IncludeStatus.Included;

    public long IncludedSize => Interlocked.Read(ref _includedSize);
    public long IgnoredSize => Interlocked.Read(ref _ignoredSize);
    public int IncludedFiles => Volatile.Read(ref _includedFiles);
    public int IgnoredFiles => Volatile.Read(ref _ignoredFiles);
    public long TotalSize => IncludedSize + IgnoredSize;
    public int TotalFiles => IncludedFiles + IgnoredFiles;

    /// <summary>The children found so far (a copy).</summary>
    public IReadOnlyList<LiveNode> Children
    {
        get
        {
            lock (_gate)
                return _children.ToArray();
        }
    }

    public IReadOnlyList<IPreviewEntry> GetChildren() => Children;

    internal void SetState(ScanState state) => Volatile.Write(ref _state, (int)state);

    internal void SetError(string error) => Volatile.Write(ref _error, error);

    /// <summary>
    /// Adds a child. A file's size, and an ignored entry below a folder that is not ignored, are added to this folder
    /// and every folder above it, the outermost first, so that a folder read after its parent does not show more.
    /// </summary>
    internal void Add(LiveNode child)
    {
        lock (_gate)
            _children.Add(child);

        var skipped = child.IsIgnored && !IsIgnored;
        if (child.IsDirectory && !skipped)
            return;

        var chain = new List<LiveNode>();
        for (var node = this; node is not null; node = node.Parent)
            chain.Add(node);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var node = chain[i];
            if (!child.IsDirectory)
            {
                if (child.IsIgnored)
                {
                    Interlocked.Add(ref node._ignoredSize, child._size);
                    Interlocked.Increment(ref node._ignoredFiles);
                }
                else
                {
                    Interlocked.Add(ref node._includedSize, child._size);
                    Interlocked.Increment(ref node._includedFiles);
                }
            }
            if (skipped)
                Interlocked.Increment(ref node._skippedEntries);
        }
    }
}
```

- [ ] **Step 5: LiveScan**

Create `src/ReBackup.Core/Indexing/LiveScan.cs`:

```csharp
using System.Runtime.ExceptionServices;
using ReBackup.Core.Ignore;
using ReBackup.Core.IO;
using ReBackup.Core.Plans;

namespace ReBackup.Core.Indexing;

public sealed record LiveScanOptions
{
    /// <summary>Folders listed at the same time.</summary>
    public int MaxParallel { get; init; } = LiveScan.DefaultParallelism;

    /// <summary>Called on a worker thread right before a folder is listed. For tests.</summary>
    public Action<LiveNode>? BeforeListing { get; init; }
}

/// <summary>
/// Scans a source folder with several workers into a tree that can be read while it grows. Entries are evaluated
/// against the ignore patterns known at the start as they are found. When it is finished, <see cref="Completion"/>
/// gives the same <see cref="SourceIndex"/> that <see cref="SourceIndexer"/> builds.
/// </summary>
public sealed class LiveScan
{
    public const int DefaultParallelism = 4;

    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly PriorityQueue<LiveNode, (int Rank, int Depth, long Order)> _queue = new();
    private readonly List<NestedIgnoreFile> _ignoreFiles = [];
    private readonly List<string> _unreadableIgnoreFiles = [];
    private readonly IgnoreSettings _settings;
    private readonly IReadOnlyList<string> _globalDefaults;
    private readonly LiveScanOptions _options;
    private readonly CancellationToken _cancellationToken;
    private long _order;
    private int _busy;
    private int _files;
    private int _directories;
    private int _waiting;
    private Exception? _failure;

    private LiveScan(string rootPath, IgnoreSettings settings, IReadOnlyList<string> globalDefaults,
        LiveScanOptions options, CancellationToken cancellationToken)
    {
        RootPath = rootPath;
        _settings = settings;
        _globalDefaults = globalDefaults;
        _options = options;
        _cancellationToken = cancellationToken;

        var name = Path.GetFileName(rootPath);
        Root = new LiveNode(null, name.Length > 0 ? name : rootPath, "", true, 0,
            new DirectoryInfo(rootPath).LastWriteTimeUtc, 0, false, null, false)
        {
            Matcher = IgnoreMatcher.ForPlan(settings, globalDefaults, []),
        };
        Enqueue(Root);
        Completion = RunAsync();
    }

    public string RootPath { get; }
    public LiveNode Root { get; }

    /// <summary>The finished index. Canceled when the scan was canceled; faulted with the error when a worker failed.</summary>
    public Task<SourceIndex> Completion { get; }

    public int Files => Volatile.Read(ref _files);
    public int Directories => Volatile.Read(ref _directories);

    /// <summary>Folders found but not listed yet.</summary>
    public int WaitingFolders => Volatile.Read(ref _waiting);

    /// <exception cref="DirectoryNotFoundException">The source folder does not exist.</exception>
    public static LiveScan Start(string root, IgnoreSettings settings, IReadOnlyList<string> globalDefaults,
        LiveScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        var fullRoot = PathUtil.Normalize(root);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException($"Source folder \"{fullRoot}\" does not exist.");

        // A copy: the caller's settings may be edited while the scan runs.
        var snapshot = new IgnoreSettings
        {
            UseGlobalDefaults = settings.UseGlobalDefaults,
            HonorNestedFiles = settings.HonorNestedFiles,
            Patterns = [.. settings.Patterns],
        };
        return new LiveScan(fullRoot, snapshot, [.. globalDefaults], options ?? new LiveScanOptions(), cancellationToken);
    }

    private async Task<SourceIndex> RunAsync()
    {
        var workers = new Task[Math.Max(1, _options.MaxParallel)];
        for (var i = 0; i < workers.Length; i++)
            workers[i] = Task.Run(Work);
        await Task.WhenAll(workers).ConfigureAwait(false);

        if (_failure is not null)
            ExceptionDispatchInfo.Throw(_failure);
        _cancellationToken.ThrowIfCancellationRequested();
        return ToSourceIndex();
    }

    private void Work()
    {
        while (true)
        {
            LiveNode folder;
            lock (_gate)
            {
                while (true)
                {
                    if (_failure is not null || _cancellationToken.IsCancellationRequested)
                    {
                        Monitor.PulseAll(_gate);
                        return;
                    }
                    if (_queue.TryDequeue(out var next, out _))
                    {
                        if (next.State != ScanState.Waiting)
                            continue;   // a second entry of a prioritised folder that was taken already
                        folder = next;
                        break;
                    }
                    if (_busy == 0)
                    {
                        // Nothing queued and no worker that could still find more: finished.
                        Monitor.PulseAll(_gate);
                        return;
                    }
                    Monitor.Wait(_gate, IdleWait);
                }
                folder.SetState(ScanState.Scanning);
                Interlocked.Decrement(ref _waiting);
                _busy++;
            }

            try
            {
                List(folder);
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                lock (_gate)
                    _failure ??= ex;
            }
            finally
            {
                lock (_gate)
                {
                    _busy--;
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }

    private void List(LiveNode folder)
    {
        _options.BeforeListing?.Invoke(folder);

        var entries = new List<FileSystemInfo>();
        try
        {
            foreach (var entry in new DirectoryInfo(FullPath(folder)).EnumerateFileSystemInfos())
            {
                _cancellationToken.ThrowIfCancellationRequested();
                entries.Add(entry);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            folder.SetError(ex.Message);   // what was listed so far is kept, as in SourceIndexer
        }

        // The folder's own .backupignore applies to its entries, so it is read before they are evaluated.
        var matcher = folder.Matcher!;
        var chain = folder.IgnoreChain;
        var ignoreFile = entries.OfType<FileInfo>()
            .FirstOrDefault(f => f.Name.Equals(IgnoreOrigins.NestedFileName, StringComparison.OrdinalIgnoreCase));
        if (ignoreFile is not null && ReadIgnoreFile(ignoreFile, folder.RelativePath) is { } nested && _settings.HonorNestedFiles)
        {
            chain = [.. chain, nested];
            matcher = IgnoreMatcher.ForPlan(_settings, _globalDefaults, chain);
        }

        var subfolders = new List<LiveNode>();
        foreach (var entry in entries)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var path = folder.RelativePath.Length == 0 ? entry.Name : folder.RelativePath + "/" + entry.Name;

            if (entry is DirectoryInfo subdirectory)
            {
                Interlocked.Increment(ref _directories);
                var (ignored, pattern) = Decide(folder, matcher, path, isDirectory: true);
                var child = new LiveNode(folder, subdirectory.Name, path, true, 0, subdirectory.LastWriteTimeUtc,
                    folder.Depth + 1, ignored, pattern, folder.IsIgnored);
                if (subdirectory.LinkTarget is not null)
                {
                    Close(child, "Link is not followed.");
                }
                else if (folder.Depth + 1 > SourceIndexer.MaxDepth)
                {
                    Close(child, "Folder nesting too deep.");
                }
                else
                {
                    child.Matcher = matcher;
                    child.IgnoreChain = chain;
                    child.Wanted = folder.Wanted;
                    subfolders.Add(child);
                }
                folder.Add(child);
            }
            else if (entry is FileInfo file)
            {
                Interlocked.Increment(ref _files);
                var (ignored, pattern) = Decide(folder, matcher, path, isDirectory: false);
                folder.Add(new LiveNode(folder, file.Name, path, false, file.Length, file.LastWriteTimeUtc,
                    folder.Depth + 1, ignored, pattern, folder.IsIgnored));
            }
        }

        // Count the subfolders before they are queued, so that none can finish while this folder still looks done.
        Interlocked.Add(ref folder.PendingFolders, subfolders.Count);
        foreach (var subfolder in subfolders)
            Enqueue(subfolder);
        Finish(folder);
    }

    /// <summary>Below an ignored folder nothing can be re-included (git rules), as in <see cref="IndexEvaluator"/>.</summary>
    private static (bool Ignored, IgnorePattern? Pattern) Decide(LiveNode folder, IgnoreMatcher matcher, string path,
        bool isDirectory)
    {
        if (folder.IsIgnored)
            return (true, folder.Pattern);
        var result = matcher.MatchEntry(path, isDirectory);
        return (result.IsIgnored, result.Pattern);
    }

    /// <summary>A folder that is not listed (a link, or nested too deep) is finished at once.</summary>
    private static void Close(LiveNode folder, string error)
    {
        folder.SetError(error);
        folder.PendingFolders = 0;
        folder.SetState(ScanState.Done);
    }

    /// <summary>One unit of work of this folder is finished; a folder with nothing left is Done, and so on upwards.</summary>
    private static void Finish(LiveNode folder)
    {
        for (var node = folder; node is not null; node = node.Parent)
        {
            if (Interlocked.Decrement(ref node.PendingFolders) != 0)
                return;
            node.SetState(ScanState.Done);
        }
    }

    private void Enqueue(LiveNode folder)
    {
        lock (_gate)
        {
            _queue.Enqueue(folder, Rank(folder));
            Interlocked.Increment(ref _waiting);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Wanted folders first, then shallower ones, then in the order found. Call under the lock.</summary>
    private (int Rank, int Depth, long Order) Rank(LiveNode folder) =>
        (folder.Wanted ? 0 : 1, folder.Depth, _order++);

    private NestedIgnoreFile? ReadIgnoreFile(FileInfo file, string directoryRelativePath)
    {
        try
        {
            var nested = new NestedIgnoreFile(directoryRelativePath, File.ReadAllLines(file.FullName));
            lock (_gate)
                _ignoreFiles.Add(nested);
            return nested;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            lock (_gate)
                _unreadableIgnoreFiles.Add(IgnoreOrigins.ForNestedFile(directoryRelativePath));
            return null;
        }
    }

    private string FullPath(LiveNode folder) =>
        folder.RelativePath.Length == 0
            ? RootPath
            : Path.Combine(RootPath, folder.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    private SourceIndex ToSourceIndex()
    {
        List<NestedIgnoreFile> ignoreFiles;
        List<string> unreadable;
        lock (_gate)
        {
            ignoreFiles = [.. _ignoreFiles];
            unreadable = [.. _unreadableIgnoreFiles];
        }
        return new SourceIndex(RootPath, ToIndexNode(Root), ignoreFiles, Files, Directories)
        {
            UnreadableIgnoreFiles = unreadable,
        };
    }

    private static IndexNode ToIndexNode(LiveNode node)
    {
        var children = node.Children.Select(ToIndexNode).ToList();
        children.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return new IndexNode
        {
            Name = node.Name,
            RelativePath = node.RelativePath,
            IsDirectory = node.IsDirectory,
            Size = node.Size,
            LastWriteUtc = node.LastWriteUtc,
            Children = children,
            Error = node.Error,
        };
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~LiveScanTests"`
Expected: PASS (11 tests). Run the filter three times in a row; the timing-dependent tests must pass every time.

- [ ] **Step 7: Full build and test run**

Run: `dotnet build --no-incremental` — expected: 0 warnings, 0 errors.
Run: `dotnet test tests/ReBackup.Core.Tests` — expected: all tests pass.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(core): live, parallel scan of a source folder for the preview" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 2: Scanning an expanded folder first

**Files:**
- Modify: `src/ReBackup.Core/Indexing/LiveScan.cs`
- Test: `tests/ReBackup.Core.Tests/Indexing/LiveScanTests.cs` (modify)

**Interfaces:**
- Consumes: `LiveScan`, `LiveNode.Wanted`, `LiveScan.Rank` (Task 1).
- Produces: `void LiveScan.Prioritize(LiveNode folder)` — a Waiting folder, and the folders later found in it, are listed before all others; no effect on a folder that is not Waiting or already wanted.

- [ ] **Step 1: Write the failing tests**

Add to `LiveScanTests`:

```csharp
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

            scan.Prioritize(x);
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~LiveScanTests"`
Expected: FAIL — compile error, `Prioritize` does not exist.

- [ ] **Step 3: Prioritize**

In `src/ReBackup.Core/Indexing/LiveScan.cs`, add below the `WaitingFolders` property:

```csharp

    /// <summary>
    /// Lists this folder, and the folders later found in it, before all others (e.g. because it was expanded).
    /// Does nothing for a folder that is not waiting or already wanted.
    /// </summary>
    public void Prioritize(LiveNode folder)
    {
        lock (_gate)
        {
            if (folder.State != ScanState.Waiting || folder.Wanted)
                return;
            folder.Wanted = true;
            // The folder's first queue entry stays behind; it is skipped when a worker finds it no longer waiting.
            _queue.Enqueue(folder, Rank(folder));
            Monitor.PulseAll(_gate);
        }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~LiveScanTests"` three times.
Expected: PASS (14 tests) every time.

- [ ] **Step 5: Full build and test run, commit**

Run: `dotnet build --no-incremental` (0 warnings) and `dotnet test tests/ReBackup.Core.Tests` (all pass).

```bash
git add -A
git commit -m "feat(core): an expanded folder is scanned first" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 3: Preview tree over IPreviewEntry

A refactoring without visible change: the preview tree, the rows, the treemap selection and the plan editor's "Ignore this" commands work on `IPreviewEntry` instead of `EvaluatedNode`, and the tree gets the `Refresh`/placeholder machinery the live scan needs. The preview still scans with `SourceIndexer` in this task.

**Files:**
- Create: `src/ReBackup.App/ViewModels/LoadingPlaceholder.cs`
- Rewrite: `src/ReBackup.App/ViewModels/PreviewRowViewModel.cs`, `src/ReBackup.App/ViewModels/PreviewTreeViewModel.cs`
- Modify: `src/ReBackup.App/ViewModels/IgnorePreviewViewModel.cs`, `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`, `src/ReBackup.App/Controls/TreemapControl.cs`

**Interfaces:**
- Consumes: `IPreviewEntry`, `ScanState`, `EvaluatedNode : IPreviewEntry` (Task 1).
- Produces: `PreviewRowViewModel(PreviewTreeViewModel tree, IPreviewEntry entry, IPreviewEntry parent, int depth, bool isExpanded)` with `Entry`, `Parent`, `IsPlaceholder`, `IsLoading`, `Refresh()`; `PreviewTreeViewModel.SetRoot(IPreviewEntry?)`, `Refresh()`, `Reveal(IPreviewEntry)`, event `LoadingFolderExpanded` (`Action<IPreviewEntry>`); `IgnorePreviewViewModel.SelectedNode` of type `IPreviewEntry?`; `TreemapControl.Selected` of type `IPreviewEntry?`; `LoadingPlaceholder`.

- [ ] **Step 1: Placeholder entry**

Create `src/ReBackup.App/ViewModels/LoadingPlaceholder.cs`:

```csharp
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;

namespace ReBackup.App.ViewModels;

/// <summary>The "loading …" row shown in an expanded folder of which nothing has been listed yet.</summary>
public sealed class LoadingPlaceholder(string parentPath) : IPreviewEntry
{
    public string Name => "loading …";
    public string RelativePath { get; } = parentPath + "/…";
    public bool IsDirectory => false;
    public string? Error => null;
    public ScanState State => ScanState.Waiting;
    public IncludeStatus Status => IncludeStatus.Included;
    public IgnorePattern? Pattern => null;
    public bool IgnoredByParent => false;
    public long IncludedSize => 0;
    public long IgnoredSize => 0;
    public int IncludedFiles => 0;
    public int IgnoredFiles => 0;
    public long TotalSize => 0;
    public int TotalFiles => 0;
    public IReadOnlyList<IPreviewEntry> GetChildren() => [];
}
```

- [ ] **Step 2: Rows**

Replace the content of `src/ReBackup.App/ViewModels/PreviewRowViewModel.cs` with:

```csharp
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Indexing;
using ReBackup.Core.IO;

namespace ReBackup.App.ViewModels;

/// <summary>One visible row of the preview tree. Values are read from the entry on every refresh.</summary>
public sealed class PreviewRowViewModel : ObservableObject
{
    private const double IndentPerLevel = 16;
    private readonly PreviewTreeViewModel _tree;
    private bool _isExpanded;

    public PreviewRowViewModel(PreviewTreeViewModel tree, IPreviewEntry entry, IPreviewEntry parent, int depth,
        bool isExpanded)
    {
        _tree = tree;
        Entry = entry;
        Parent = parent;
        Depth = depth;
        _isExpanded = isExpanded;
    }

    public IPreviewEntry Entry { get; }

    /// <summary>The parent folder's entry; the root row has itself.</summary>
    public IPreviewEntry Parent { get; }

    public int Depth { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
                _tree.OnExpandedChanged(this, value);
        }
    }

    /// <summary>Sets the flag while the tree rebuilds its rows, without asking the tree to rebuild again.</summary>
    internal void SyncExpanded(bool value) => SetProperty(ref _isExpanded, value, nameof(IsExpanded));

    public bool IsPlaceholder => Entry is LoadingPlaceholder;

    /// <summary>A folder of a running scan that is not finished yet; its values still grow.</summary>
    public bool IsLoading => Entry.IsDirectory && Entry.State != ScanState.Done;

    public bool IsExpandable => Entry.IsDirectory && (IsLoading || Entry.GetChildren().Count > 0);
    public string Name => Entry.Name;
    public Thickness Indent => new(Depth * IndentPerLevel, 0, 0, 0);

    private string Prefix => IsLoading ? "≥ " : "";

    public string SizeText => IsPlaceholder ? "" : Prefix + ByteSize.Format(Entry.TotalSize);

    /// <summary>What the backup of this entry takes: its size without the ignored parts.</summary>
    public string BackupSizeText =>
        IsPlaceholder ? ""
        : Entry.Status == IncludeStatus.Ignored ? "—"
        : Prefix + ByteSize.Format(Entry.IncludedSize);

    public string FilesText =>
        Entry.IsDirectory ? Prefix + Entry.TotalFiles.ToString("N0", CultureInfo.CurrentCulture) : "";

    public double PercentOfParent => Share(Entry.TotalSize, Parent.TotalSize);

    /// <summary>Share of the parent's backup size (both without ignored entries).</summary>
    public double BackupPercentOfParent => Share(Entry.IncludedSize, Parent.IncludedSize);

    public string PercentText =>
        IsPlaceholder ? "" : PercentOfParent.ToString("0.0", CultureInfo.CurrentCulture) + " %";

    public string BackupPercentText =>
        IsPlaceholder ? ""
        : Entry.Status == IncludeStatus.Ignored ? "—"
        : BackupPercentOfParent.ToString("0.0", CultureInfo.CurrentCulture) + " %";

    public string StatusText =>
        IsPlaceholder ? ""
        : IsLoading ? (Entry.State == ScanState.Waiting ? "waiting" : "loading")
        : Entry.Error is not null && Entry.Status != IncludeStatus.Ignored ? "Not scanned"
        : Entry.Status.ToString();

    public bool IsIgnored => !IsPlaceholder && Entry.Status == IncludeStatus.Ignored;
    public bool IsPartial => !IsLoading && Entry.Status == IncludeStatus.Partial;
    public bool IsNotScanned => Entry.Error is not null && Entry.Status != IncludeStatus.Ignored;

    public string? StatusDetail
    {
        get
        {
            if (IsPlaceholder)
                return null;
            var detail = Entry.Status switch
            {
                IncludeStatus.Ignored when Entry.IgnoredByParent =>
                    $"Ignored because a parent folder is ignored by \"{Entry.Pattern?.Text}\" ({Entry.Pattern?.Origin})",
                IncludeStatus.Ignored =>
                    $"Ignored by \"{Entry.Pattern?.Text}\" ({Entry.Pattern?.Origin})",
                IncludeStatus.Partial =>
                    $"Partly ignored: {ByteSize.Format(Entry.IgnoredSize)} in {Entry.IgnoredFiles:N0} files are skipped",
                _ when Entry.Pattern is not null =>
                    $"Re-included by \"{Entry.Pattern.Text}\" ({Entry.Pattern.Origin})",
                _ => "Included",
            };
            if (IsLoading)
                detail = "Still being scanned; the numbers still grow.\n" + detail;
            return Entry.Error is null ? detail : $"{detail}\nNot scanned: {Entry.Error}";
        }
    }

    /// <summary>Re-reads every value (a running scan changes them).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    // Values of a running scan are read one after the other, so a child can briefly show more than its parent.
    private static double Share(long part, long whole) => whole > 0 ? Math.Min(100, 100.0 * part / whole) : 0;
}
```

- [ ] **Step 3: Tree**

Replace the content of `src/ReBackup.App/ViewModels/PreviewTreeViewModel.cs` with:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Indexing;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The preview tree flattened to its visible rows, so that a plain virtualized list can show it. Works on a finished
/// evaluation as well as on a scan that is still running (<see cref="Refresh"/> re-reads it).
/// </summary>
public sealed partial class PreviewTreeViewModel : ObservableObject
{
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IPreviewEntry, PreviewRowViewModel> _placeholders = new(ReferenceEqualityComparer.Instance);
    private IPreviewEntry? _root;

    [ObservableProperty] private PreviewRowViewModel? _selectedRow;

    public RangeObservableCollection<PreviewRowViewModel> Rows { get; } = new();

    /// <summary>Raised when a folder is expanded that is not finished yet.</summary>
    public event Action<IPreviewEntry>? LoadingFolderExpanded;

    /// <summary>Shows a new tree. Expanded folders and the selection are kept by path.</summary>
    public void SetRoot(IPreviewEntry? root)
    {
        if (_root is null)
            _expanded.Add("");   // first tree: open the root
        _root = root;
        _placeholders.Clear();
        Rebuild(reuseRows: false);
    }

    /// <summary>Re-reads a running scan: the values of the visible rows, new entries and a changed order.</summary>
    public void Refresh() => Rebuild(reuseRows: true);

    /// <summary>Expands the folders above the entry and selects its row.</summary>
    public void Reveal(IPreviewEntry target)
    {
        if (_root is null)
            return;

        var previous = SelectedRow;
        var current = _root;
        var found = true;
        _expanded.Add(_root.RelativePath);
        if (target.RelativePath.Length > 0)
        {
            foreach (var part in target.RelativePath.Split('/'))
            {
                var child = current.GetChildren().FirstOrDefault(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (child is null)
                {
                    found = false;
                    break;
                }
                _expanded.Add(current.RelativePath);
                current = child;
            }
        }

        Rebuild(reuseRows: true);
        var row = found ? Rows.FirstOrDefault(r => ReferenceEquals(r.Entry, current)) : null;
        SelectedRow = row ?? (previous is not null && Rows.Contains(previous) ? previous : null);
    }

    internal void OnExpandedChanged(PreviewRowViewModel row, bool expanded)
    {
        if (expanded)
            _expanded.Add(row.Entry.RelativePath);
        else
            _expanded.Remove(row.Entry.RelativePath);
        if (expanded && row.IsLoading)
            LoadingFolderExpanded?.Invoke(row.Entry);
        Rebuild(reuseRows: true);
    }

    private void Rebuild(bool reuseRows)
    {
        var selectedPath = SelectedRow is { IsPlaceholder: false } selected ? selected.Entry.RelativePath : null;
        var known = new Dictionary<IPreviewEntry, PreviewRowViewModel>(ReferenceEqualityComparer.Instance);
        if (reuseRows)
        {
            foreach (var row in Rows)
                known[row.Entry] = row;
        }

        var rows = new List<PreviewRowViewModel>();
        if (_root is not null)
            Append(rows, _root, _root, 0, known);

        if (rows.Count == Rows.Count && rows.Zip(Rows).All(pair => ReferenceEquals(pair.First, pair.Second)))
        {
            foreach (var row in rows)
                row.Refresh();
            return;
        }

        Rows.ReplaceAll(rows);
        foreach (var row in rows)
            row.Refresh();
        // The list view may drop its selection on a reset; put it back by path (or on the nearest visible folder above).
        SelectedRow = selectedPath is null ? null : FindRow(selectedPath);
    }

    private PreviewRowViewModel? FindRow(string path)
    {
        while (true)
        {
            var row = Rows.FirstOrDefault(r => !r.IsPlaceholder &&
                                               r.Entry.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (row is not null || path.Length == 0)
                return row;
            var slash = path.LastIndexOf('/');
            path = slash < 0 ? "" : path[..slash];
        }
    }

    private void Append(List<PreviewRowViewModel> rows, IPreviewEntry entry, IPreviewEntry parent, int depth,
        Dictionary<IPreviewEntry, PreviewRowViewModel> known)
    {
        var expanded = entry.IsDirectory && _expanded.Contains(entry.RelativePath);
        if (known.TryGetValue(entry, out var row))
            row.SyncExpanded(expanded);
        else
            row = new PreviewRowViewModel(this, entry, parent, depth, expanded);
        rows.Add(row);
        if (!expanded)
            return;

        var children = entry.GetChildren();
        if (children.Count == 0)
        {
            if (entry.State != ScanState.Done)
            {
                if (!_placeholders.TryGetValue(entry, out var placeholder))
                {
                    placeholder = new PreviewRowViewModel(this, new LoadingPlaceholder(entry.RelativePath), entry, depth + 1, false);
                    _placeholders[entry] = placeholder;
                }
                rows.Add(placeholder);
            }
            return;
        }

        foreach (var child in children
                     .OrderByDescending(c => c.TotalSize)
                     .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            Append(rows, child, entry, depth + 1, known);
        }
    }
}
```

- [ ] **Step 4: Selection as IPreviewEntry**

In `src/ReBackup.App/ViewModels/IgnorePreviewViewModel.cs`:

1. Change `[ObservableProperty] private EvaluatedNode? _selectedNode;` to `[ObservableProperty] private IPreviewEntry? _selectedNode;`.
2. In the constructor, change `SelectedNode = Tree.SelectedRow?.Node;` to `SelectedNode = Tree.SelectedRow?.Entry;`.
3. Replace `OnSelectedNodeChanged` with:

```csharp
    partial void OnSelectedNodeChanged(IPreviewEntry? value)
    {
        // Selection coming from outside the tree (the treemap): show it in the tree.
        if (value is not null && !ReferenceEquals(Tree.SelectedRow?.Entry, value))
            Tree.Reveal(value);
    }
```

In `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`, replace the method `SelectedEntry` (and its summary) with:

```csharp
    /// <summary>The selected preview entry, unless it is the source root (which cannot be ignored) or a "loading" row.</summary>
    private IPreviewEntry? SelectedEntry() =>
        Preview.SelectedNode is { RelativePath.Length: > 0 } entry && entry is not LoadingPlaceholder ? entry : null;
```

(`IndexNode` is no longer needed there; remove `using ReBackup.Core.Indexing;` only if nothing else in the file uses it — `IPreviewEntry` lives in that namespace, so it stays.)

In `src/ReBackup.App/Controls/TreemapControl.cs`, change the `Selected` dependency property and its CLR property from `EvaluatedNode` to `IPreviewEntry`:

```csharp
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(IPreviewEntry), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
```

```csharp
    public IPreviewEntry? Selected
    {
        get => (IPreviewEntry?)GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }
```

(The tiles still hold `EvaluatedNode`s; `ReferenceEquals(tile.Node, selected)` and `SetCurrentValue(SelectedProperty, node)` keep working.)

- [ ] **Step 5: Build, test, smoke-test**

Run: `dotnet build --no-incremental` (0 warnings, 0 errors), `dotnet test tests/ReBackup.Core.Tests` (all pass), and the startup smoke test.

Manual checklist (for the user; do not click through it as an agent) — everything behaves as before:
1. Index now fills the tree; expanding and collapsing folders works; the selection stays on the folder when its content is collapsed.
2. Clicking a treemap tile selects and reveals the entry in the tree; selecting a row outlines it in the treemap.
3. Right-click → Ignore this / Ignore all files with this extension / Un-ignore work on the selected row; editing a pattern re-evaluates the tree and keeps expanded folders.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(app): preview tree, rows and treemap selection work on IPreviewEntry" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 4: Live scan in the preview

**Files:**
- Rewrite: `src/ReBackup.App/ViewModels/IgnorePreviewViewModel.cs`
- Modify: `src/ReBackup.App/Views/IgnorePreviewView.xaml`

**Interfaces:**
- Consumes: `LiveScan.Start/Root/Completion/Files/Directories/WaitingFolders/Prioritize`, `LiveNode` (Tasks 1–2); `PreviewTreeViewModel.SetRoot/Refresh/LoadingFolderExpanded`, `PreviewRowViewModel.IsLoading/IsPlaceholder` (Task 3); `IndexEvaluator`, `IgnoreMatcher.ForPlan`, `ByteSize`.
- Produces: `IgnorePreviewViewModel.PatternNote` (string?, shown while a scan runs); `IsIndexing` now also means "the treemap waits".

Behaviour: see spec §4. While the scan runs the tree shows `scan.Root` and is refreshed every 250 ms; `Root` (the evaluated tree for the treemap and the size fallback of the retention preview) is null. When the scan finishes, the finished index is evaluated with the patterns as they are then and shown (expanded folders and selection kept by path), then once more after a pattern edit that came in meanwhile. Cancel: with an earlier complete index, that one is evaluated and shown again; without, the partial tree stays with "Scan canceled — incomplete."

- [ ] **Step 1: View model**

Replace the content of `src/ReBackup.App/ViewModels/IgnorePreviewViewModel.cs` with:

```csharp
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.IO;
using ReBackup.Core.Plans;

namespace ReBackup.App.ViewModels;

/// <summary>
/// Scans a plan's source live (several folders at a time; the tree grows while it runs) and afterwards re-evaluates
/// the ignore patterns against the cached index without a rescan.
/// </summary>
public sealed partial class IgnorePreviewViewModel : ObservableObject
{
    private const string NotIndexedText = "Not indexed yet.";
    private static readonly TimeSpan ReevaluateDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<string> _source;
    private readonly Func<IgnoreSettings> _ignoreSettings;
    private readonly Func<IReadOnlyList<string>> _globalDefaults;
    private SourceIndex? _index;
    private LiveScan? _scan;
    private CancellationTokenSource? _indexCts;
    private CancellationTokenSource? _evaluateCts;
    private int _evaluationVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatternNote))]
    private bool _isIndexing;

    [ObservableProperty] private string _progressText = NotIndexedText;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _summary = "";

    /// <summary>The evaluated tree of the last complete scan (treemap, sizes); null while a scan runs.</summary>
    [ObservableProperty] private EvaluatedNode? _root;

    [ObservableProperty] private IPreviewEntry? _selectedNode;

    public IgnorePreviewViewModel(Func<string> source, Func<IgnoreSettings> ignoreSettings,
        Func<IReadOnlyList<string>> globalDefaults)
    {
        _source = source;
        _ignoreSettings = ignoreSettings;
        _globalDefaults = globalDefaults;
        Tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewTreeViewModel.SelectedRow))
                SelectedNode = Tree.SelectedRow?.Entry;
        };
        Tree.LoadingFolderExpanded += entry =>
        {
            if (_scan is { } scan && entry is LiveNode folder)
                scan.Prioritize(folder);
        };
    }

    public PreviewTreeViewModel Tree { get; } = new();

    /// <summary>Shown next to the patterns while a scan runs.</summary>
    public string? PatternNote => IsIndexing ? "Changes to the patterns are applied when the scan is finished." : null;

    partial void OnSelectedNodeChanged(IPreviewEntry? value)
    {
        // Selection coming from outside the tree (the treemap): show it in the tree.
        if (value is not null && !ReferenceEquals(Tree.SelectedRow?.Entry, value))
            Tree.Reveal(value);
    }

    /// <summary>Drops the cached index, e.g. after the source folder changed.</summary>
    public void Invalidate()
    {
        _indexCts?.Cancel();
        _indexCts = null;   // the aborted scan must not touch the state below any more
        _scan = null;
        _evaluateCts?.Cancel();
        _evaluationVersion++;
        _index = null;
        IsIndexing = false;
        Root = null;
        Tree.SetRoot(null);
        Summary = "";
        Error = null;
        ProgressText = NotIndexedText;
    }

    /// <summary>Re-applies the patterns to the cached index after a short pause in typing. No rescan; not during a scan.</summary>
    public async void RequestReevaluate()
    {
        // During a scan the patterns are applied when it is finished (it is then evaluated with the current ones).
        if (_index is null || IsIndexing)
            return;

        _evaluateCts?.Cancel();
        var cts = _evaluateCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(ReevaluateDelay, cts.Token);
            if (_index is { } index && !IsIndexing)
                await EvaluateAsync(index, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task IndexAsync()
    {
        var source = _source();
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            Error = "The source folder does not exist.";
            return;
        }

        _indexCts?.Cancel();
        _evaluateCts?.Cancel();
        var cts = _indexCts = new CancellationTokenSource();
        Error = null;

        LiveScan scan;
        try
        {
            scan = LiveScan.Start(source, _ignoreSettings(), _globalDefaults().ToList(), cancellationToken: cts.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Error = ex.Message;
            return;
        }

        _scan = scan;
        IsIndexing = true;
        Root = null;   // the treemap waits for the finished scan
        Tree.SetRoot(scan.Root);

        try
        {
            while (!scan.Completion.IsCompleted)
            {
                await Task.WhenAny(scan.Completion, Task.Delay(LiveRefreshInterval));
                if (!ReferenceEquals(_indexCts, cts))
                    return;   // superseded by a new scan or dropped by Invalidate
                ShowLive(scan);
            }

            var index = await scan.Completion;
            _scan = null;
            // A newer evaluation (pattern edit) can supersede this one; repeat until the new index is shown.
            while (!await EvaluateAsync(index, cts.Token))
            {
            }
            IsIndexing = false;
            ProgressText = $"Indexed {index.FileCount:N0} files in {index.DirectoryCount:N0} folders.";
            RequestReevaluate();   // in case the patterns were edited while the result was being evaluated
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(_indexCts, cts))
                return;
            _scan = null;
            IsIndexing = false;
            if (_index is { } previous)
            {
                while (!await EvaluateAsync(previous, CancellationToken.None))
                {
                }
                ProgressText = "Indexing canceled; showing the previous index.";
            }
            else
            {
                Tree.Refresh();
                ProgressText = "Scan canceled — incomplete.";
            }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_indexCts, cts))
                return;
            _scan = null;
            Tree.Refresh();
            Error = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_indexCts, cts))
                IsIndexing = false;
        }
    }

    [RelayCommand]
    private void CancelIndex() => _indexCts?.Cancel();

    private void ShowLive(LiveScan scan)
    {
        Tree.Refresh();
        ProgressText = $"{scan.Files:N0} files, {scan.Directories:N0} folders so far — {scan.WaitingFolders:N0} folders waiting";
        var root = scan.Root;
        Summary = $"So far — included: {root.IncludedFiles:N0} files, {ByteSize.Format(root.IncludedSize)}   ·   " +
                  $"ignored: {root.IgnoredFiles:N0} files, {ByteSize.Format(root.IgnoredSize)}";
    }

    /// <summary>Evaluates and publishes the index. False means a newer evaluation superseded this one.</summary>
    private async Task<bool> EvaluateAsync(SourceIndex index, CancellationToken cancellationToken)
    {
        var version = ++_evaluationVersion;

        // Snapshots, because the evaluation runs on a worker thread.
        var settings = _ignoreSettings();
        var globalDefaults = _globalDefaults().ToList();

        var root = await Task.Run(
            () => IndexEvaluator.Evaluate(index, IgnoreMatcher.ForPlan(settings, globalDefaults, index.IgnoreFiles), cancellationToken),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (version != _evaluationVersion)
            return false;

        _index = index;
        Error = null;
        Root = root;
        Tree.SetRoot(root);
        Summary = $"Included: {root.IncludedFiles:N0} files, {ByteSize.Format(root.IncludedSize)}   ·   " +
                  $"Ignored: {root.IgnoredFiles:N0} files, {ByteSize.Format(root.IgnoredSize)}";
        return true;
    }
}
```

- [ ] **Step 2: View**

In `src/ReBackup.App/Views/IgnorePreviewView.xaml`:

1. In the left `DockPanel` (the pattern editor), directly before the first `CheckBox` that is docked at the bottom, add:

```xml
            <TextBlock DockPanel.Dock="Bottom" Margin="0,6,0,0" Foreground="DarkOrange" TextWrapping="Wrap"
                       Text="{Binding Preview.PatternNote}">
                <TextBlock.Style>
                    <Style TargetType="TextBlock">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding Preview.PatternNote}" Value="{x:Null}">
                                <Setter Property="Visibility" Value="Collapsed" />
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>
```

2. In the `ListView.ItemContainerStyle` `Style.Triggers`, add after the existing two `DataTrigger`s:

```xml
                                <DataTrigger Binding="{Binding IsLoading}" Value="True">
                                    <Setter Property="Foreground" Value="Gray" />
                                </DataTrigger>
                                <DataTrigger Binding="{Binding IsPlaceholder}" Value="True">
                                    <Setter Property="Foreground" Value="Gray" />
                                    <Setter Property="FontStyle" Value="Italic" />
                                </DataTrigger>
```

3. In the "Status" column's cell template, give the `Ellipse` a trigger that hides it while loading, and add an hourglass before the status text. The `StackPanel` of the cell becomes:

```xml
                                        <StackPanel Orientation="Horizontal">
                                            <TextBlock Text="⏳" Margin="0,0,4,0" VerticalAlignment="Center">
                                                <TextBlock.Style>
                                                    <Style TargetType="TextBlock">
                                                        <Setter Property="Visibility" Value="Collapsed" />
                                                        <Style.Triggers>
                                                            <DataTrigger Binding="{Binding IsLoading}" Value="True">
                                                                <Setter Property="Visibility" Value="Visible" />
                                                            </DataTrigger>
                                                        </Style.Triggers>
                                                    </Style>
                                                </TextBlock.Style>
                                            </TextBlock>
                                            <!-- the existing Ellipse, with this extra trigger at the end of its Style.Triggers: -->
                                            <!-- <DataTrigger Binding="{Binding IsLoading}" Value="True"><Setter Property="Visibility" Value="Collapsed" /></DataTrigger> -->
                                            <!-- and a trigger that hides it for placeholders: -->
                                            <!-- <DataTrigger Binding="{Binding IsPlaceholder}" Value="True"><Setter Property="Visibility" Value="Collapsed" /></DataTrigger> -->
                                            <TextBlock VerticalAlignment="Center" Text="{Binding StatusText}" />
                                        </StackPanel>
```

   (Keep the existing `Ellipse` element between the hourglass and the status text, and add the two `DataTrigger`s shown in the comments to its `Style.Triggers`; do not leave the comments in the file.)

4. Put the treemap `Border` into a `Grid` together with a note that is shown while a scan runs:

```xml
                    <Grid>
                        <Border BorderBrush="#ABADB3" BorderThickness="1">
                            <!-- the existing TreemapControl, unchanged -->
                        </Border>
                        <TextBlock HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="Gray"
                                   Text="The treemap appears after the scan.">
                            <TextBlock.Style>
                                <Style TargetType="TextBlock">
                                    <Setter Property="Visibility" Value="Collapsed" />
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Preview.IsIndexing}" Value="True">
                                            <Setter Property="Visibility" Value="Visible" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </TextBlock.Style>
                        </TextBlock>
                    </Grid>
```

   (The `Grid` takes the place of the `Border` inside the `DockPanel` that holds the "Hide ignored entries" check box.)

- [ ] **Step 3: Build, test, smoke-test**

Run: `dotnet build --no-incremental` (0 warnings, 0 errors), `dotnet test tests/ReBackup.Core.Tests` (all pass), and the startup smoke test.

Manual checklist (for the user; do not click through it as an agent):
1. **Index now** on a large source (e.g. a user profile or a drive root): the top-level folders appear within a moment with ⏳ and "loading"/"waiting", grey sizes with "≥"; sizes and file counts grow while you watch; the progress line shows "… folders waiting".
2. Expanding a waiting folder shows "loading …" and then its content soon after, before other folders of the same level are done.
3. Folders that are finished get their dot and colour (green / yellow / red background) while others are still loading; ignored folders are red at once.
4. The treemap shows "The treemap appears after the scan." and draws when the scan is finished.
5. Editing a pattern during the scan shows the orange note; after the scan the tree reflects the edited patterns.
6. **Cancel** during the first scan leaves the partial tree with "Scan canceled — incomplete."; cancel during a later scan brings back the previous complete result.
7. After the scan: expanded folders and the selection are kept, pattern edits re-evaluate without a rescan, the treemap selection works both ways.
8. A backup run is unaffected (it still scans on its own).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(app): the preview scans live — folders appear at once, grow while scanned, and can be browsed" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```
