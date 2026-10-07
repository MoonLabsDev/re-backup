using System.Runtime.ExceptionServices;
using ReBackup.Core.Ignore;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Storage;

namespace ReBackup.Core.Indexing;

public sealed record LiveScanOptions
{
    /// <summary>Folders listed at the same time.</summary>
    public int MaxParallel { get; init; } = LiveScan.DefaultParallelism;

    /// <summary>Called on a worker thread right before a folder is listed. For tests.</summary>
    public Action<LiveNode>? BeforeListing { get; init; }
}

/// <summary>
/// Scans a source with several workers (async tasks) into a tree that can be read while it grows. Entries are evaluated
/// against the ignore patterns known at the start as they are found. When it is finished, <see cref="Completion"/>
/// gives the same <see cref="SourceIndex"/> that <see cref="SourceIndexer"/> builds.
/// </summary>
public sealed class LiveScan
{
    public const int DefaultParallelism = 4;

    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly IStorage _source;
    private readonly StringComparer _comparer;
    private TaskCompletionSource _wake = NewWake();
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

    private LiveScan(IStorage source, string rootName, IgnoreSettings settings, IReadOnlyList<string> globalDefaults,
        LiveScanOptions options, CancellationToken cancellationToken)
    {
        _source = source;
        _comparer = SourceIndexer.NameComparer(source);
        RootName = rootName;
        _settings = settings;
        _globalDefaults = globalDefaults;
        _options = options;
        _cancellationToken = cancellationToken;

        // The time of the root is not known yet; the scan sets it as its first step.
        Root = new LiveNode(null, rootName, "", true, 0, default, 0, false, null, false)
        {
            Matcher = IgnoreMatcher.ForPlan(settings, globalDefaults, []),
        };
        Enqueue(Root);
        Completion = RunAsync();
    }

    /// <summary>The display name of the root.</summary>
    public string RootName { get; }
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
            Pulse();
        }
    }

    /// <summary>
    /// Starts scanning <paramref name="source"/> and returns at once. A source that does not exist or cannot be reached
    /// shows in <see cref="Completion"/>: <see cref="StorageNotFoundException"/> or <see cref="StorageUnavailableException"/>.
    /// </summary>
    public static LiveScan Start(IStorage source, string rootName, IgnoreSettings settings,
        IReadOnlyList<string> globalDefaults, LiveScanOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // A copy: the caller may edit the settings while the scan runs.
        var snapshot = new IgnoreSettings
        {
            UseGlobalDefaults = settings.UseGlobalDefaults,
            HonorNestedFiles = settings.HonorNestedFiles,
            Patterns = [.. settings.Patterns],
        };
        return new LiveScan(source, rootName, snapshot, [.. globalDefaults], options ?? new LiveScanOptions(), ct);
    }

    private async Task<SourceIndex> RunAsync()
    {
        try
        {
            if ((await _source.StatAsync("", _cancellationToken).ConfigureAwait(false)) is { } rootEntry)
                Root.SetLastWrite(rootEntry.ModifiedUtc);
        }
        catch (StorageException)
        {
            // Best effort; listing the root reports the real problem.
        }

        var workers = new Task[Math.Max(1, _options.MaxParallel)];
        for (var i = 0; i < workers.Length; i++)
            workers[i] = Task.Run(WorkAsync);
        await Task.WhenAll(workers).ConfigureAwait(false);

        if (_failure is not null)
            ExceptionDispatchInfo.Throw(_failure);
        _cancellationToken.ThrowIfCancellationRequested();
        return ToSourceIndex();
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            LiveNode? folder = null;
            Task? wake = null;
            lock (_gate)
            {
                if (_failure is not null || _cancellationToken.IsCancellationRequested)
                {
                    Pulse();
                    return;
                }
                if (_queue.TryDequeue(out var next, out _))
                {
                    // A second entry of a prioritised folder that was taken already is skipped silently.
                    if (next.State != ScanState.Waiting)
                        continue;
                    folder = next;
                    folder.SetState(ScanState.Scanning);
                    Interlocked.Decrement(ref _waiting);
                    _busy++;
                }
                else if (_busy == 0)
                {
                    // Nothing queued and no worker that could still find more: finished.
                    Pulse();
                    return;
                }
                else
                {
                    wake = _wake.Task;
                }
            }

            if (folder is null)
            {
                await Task.WhenAny(wake!, Task.Delay(IdleWait)).ConfigureAwait(false);
                continue;
            }

            try
            {
                await ListAsync(folder).ConfigureAwait(false);
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
                    Pulse();
                }
            }
        }
    }

    private async Task ListAsync(LiveNode folder)
    {
        _options.BeforeListing?.Invoke(folder);

        var entries = new List<StorageEntry>();
        try
        {
            await foreach (var entry in _source.ListAsync(folder.RelativePath, recursive: false, _cancellationToken).ConfigureAwait(false))
                entries.Add(entry);
        }
        catch (StorageNotFoundException) when (folder.Parent is null)
        {
            throw;   // the source itself is missing
        }
        catch (StorageUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            // ArgumentException: a folder name the storage cannot address.
            folder.SetError(ex.Message);   // what was listed so far is kept, as in SourceIndexer
        }

        // The folder's own .backupignore applies to its entries, so it is read before they are evaluated.
        var matcher = folder.Matcher!;
        var chain = folder.IgnoreChain;
        var ignoreFile = entries.FirstOrDefault(e => !e.IsDirectory &&
            StoragePath.Name(e.Path).Equals(IgnoreOrigins.NestedFileName, StringComparison.OrdinalIgnoreCase));
        if (ignoreFile is not null && await ReadIgnoreFileAsync(ignoreFile.Path, folder.RelativePath).ConfigureAwait(false) is { } nested
            && _settings.HonorNestedFiles)
        {
            chain = [.. chain, nested];
            matcher = IgnoreMatcher.ForPlan(_settings, _globalDefaults, chain);
        }

        var subfolders = new List<LiveNode>();
        foreach (var entry in entries)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var path = entry.Path;
            var name = StoragePath.Name(path);

            if (entry.IsDirectory)
            {
                Interlocked.Increment(ref _directories);
                var (ignored, pattern) = Decide(folder, matcher, path, isDirectory: true);
                var child = new LiveNode(folder, name, path, true, 0, entry.ModifiedUtc,
                    folder.Depth + 1, ignored, pattern, folder.IsIgnored);
                if (entry.IsLink)
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
            else
            {
                Interlocked.Increment(ref _files);
                var (ignored, pattern) = Decide(folder, matcher, path, isDirectory: false);
                folder.Add(new LiveNode(folder, name, path, false, entry.Size, entry.ModifiedUtc,
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
            Pulse();
        }
    }

    /// <summary>Wanted folders first, then shallower ones, then in the order found. Call under the lock.</summary>
    private (int Rank, int Depth, long Order) Rank(LiveNode folder) =>
        (folder.Wanted ? 0 : 1, folder.Depth, _order++);

    private async Task<NestedIgnoreFile?> ReadIgnoreFileAsync(string path, string directoryRelativePath)
    {
        var lines = await SourceIndexer.ReadIgnoreLinesAsync(_source, path, _cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (lines is null)
            {
                _unreadableIgnoreFiles.Add(IgnoreOrigins.ForNestedFile(directoryRelativePath));
                return null;
            }
            var nested = new NestedIgnoreFile(directoryRelativePath, lines);
            _ignoreFiles.Add(nested);
            return nested;
        }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Wakes the workers that wait for work. Call under the lock.</summary>
    private void Pulse()
    {
        var old = _wake;
        _wake = NewWake();
        old.TrySetResult();
    }

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
        return new SourceIndex(RootName, ToIndexNode(Root), ignoreFiles, Files, Directories)
        {
            UnreadableIgnoreFiles = unreadable,
        };
    }

    private IndexNode ToIndexNode(LiveNode node)
    {
        var children = node.Children.Select(ToIndexNode).ToList();
        children.Sort((a, b) => _comparer.Compare(a.Name, b.Name));
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
