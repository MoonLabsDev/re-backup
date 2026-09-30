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
