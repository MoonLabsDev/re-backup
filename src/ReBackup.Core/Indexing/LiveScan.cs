using System.Runtime.ExceptionServices;
using ReBackup.Core.Ignore;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Shared.IO;

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

    /// <exception cref="DirectoryNotFoundException">The source folder does not exist.</exception>
    public static LiveScan Start(string root, IgnoreSettings settings, IReadOnlyList<string> globalDefaults,
        LiveScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        var fullRoot = PathUtil.Normalize(root);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException(CoreTexts.English("core.run.sourceMissing", ("source", fullRoot)));

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
                        {
                            // A second entry of a prioritised folder that was taken already; skip silently.
                            continue;
                        }
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
                    Close(child, CoreTexts.English("core.scan.link"));
                }
                else if (folder.Depth + 1 > SourceIndexer.MaxDepth)
                {
                    Close(child, CoreTexts.English("core.scan.tooDeep"));
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
            // Found in scan order, not SourceIndexer's walk order; harmless, IgnoreMatcher.ForPlan sorts by depth and name.
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
