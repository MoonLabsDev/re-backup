using System.Diagnostics;
using System.Text;
using ReBackup.Core.Ignore;
using ReBackup.Core.Localization;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;

namespace ReBackup.Core.Indexing;

/// <summary>
/// A scanned file or folder. It keeps its name and its parent, not its path (<see cref="TreePath"/>): a source of
/// millions of entries would otherwise hold millions of long paths.
/// </summary>
public sealed class IndexNode : ITreePathNode<IndexNode>
{
    private readonly IReadOnlyList<IndexNode> _children = [];

    public required string Name { get; init; }

    /// <summary>The folder this entry is in; null for the root. Set when the folder gets its <see cref="Children"/>.</summary>
    public IndexNode? Parent { get; private set; }

    /// <summary>Path relative to the source root, with forward slashes; "" for the root. Put together on every call.</summary>
    public string RelativePath => TreePath.Of(this);

    public required bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime LastWriteUtc { get; init; }

    /// <summary>
    /// The storage's change token (<see cref="StorageEntry.Stamp"/>) of a file listed with size 0, whose real size may only be known
    /// by reading it; <c>null</c> for every other file, for folders, and when the storage has none.
    /// </summary>
    public string? Stamp { get; init; }
    public IReadOnlyList<IndexNode> Children
    {
        get => _children;
        init
        {
            _children = value;
            foreach (var child in value)
                child.Parent = this;
        }
    }

    /// <summary>Why this folder could not be (fully) read, if so.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// A file node for one path, as a scan would give it: under a chain of folder nodes up to a root, so that its
    /// <see cref="RelativePath"/> is <paramref name="relativePath"/> (a bare node would be at the root). Size and time
    /// are those of <paramref name="entry"/>; <see cref="Stamp"/> is set only for a size of 0, as in a scan.
    /// </summary>
    /// <param name="relativePath">The file's path relative to the source root, with forward slashes.</param>
    internal static IndexNode ForFile(string relativePath, StorageEntry entry)
    {
        var names = relativePath.Split('/');
        var file = new IndexNode
        {
            Name = names[^1],
            IsDirectory = false,
            Size = entry.Size,
            LastWriteUtc = entry.ModifiedUtc,
            Stamp = entry.Size == 0 ? entry.Stamp : null,
        };
        // Innermost folder first; the root (whose name is only a display name) closes the chain. Each folder sets the
        // parent of the one below it.
        var node = file;
        for (var index = names.Length - 2; index >= -1; index--)
            node = new IndexNode { Name = index >= 0 ? names[index] : "", IsDirectory = true, Children = [node] };
        return file;
    }
}

/// <summary>
/// One scan of a source. <see cref="Root"/> is the display name of the root (the folder name; the full path when it has none), not an address.
/// <see cref="DirectoryCount"/> does not include the root.
/// </summary>
public sealed record SourceIndex(
    string Root,
    IndexNode RootNode,
    IReadOnlyList<NestedIgnoreFile> IgnoreFiles,
    int FileCount,
    int DirectoryCount)
{
    /// <summary>Relative paths of <c>.backupignore</c> files that could not be read; their patterns are not applied.</summary>
    public IReadOnlyList<string> UnreadableIgnoreFiles { get; init; } = [];
}

public readonly record struct IndexProgress(int Files, int Directories, string CurrentDirectory);

public static class SourceIndexer
{
    /// <summary>Folders nested deeper than this are not scanned.</summary>
    public const int MaxDepth = 256;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Scans <paramref name="source"/> with one non-recursive listing per folder. <paramref name="rootName"/> is the display
    /// name of the root; it defaults to the folder name for a <see cref="FileSystemStorage"/> and to "" otherwise.
    /// </summary>
    /// <exception cref="StorageNotFoundException">The source does not exist.</exception>
    /// <exception cref="StorageUnavailableException">The source cannot be reached.</exception>
    public static async Task<SourceIndex> BuildAsync(IStorage source, IProgress<IndexProgress>? progress = null,
        CancellationToken ct = default, string? rootName = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var name = rootName ?? DefaultRootName(source);
        var walk = new Walk(source, progress, ct);
        var rootNode = await walk.ScanDirectoryAsync(name, "", 0, isRoot: true, default).ConfigureAwait(false);
        progress?.Report(new IndexProgress(walk.Files, walk.Directories, DisplayPath(source, "")));
        return new SourceIndex(name, rootNode, walk.IgnoreFiles, walk.Files, walk.Directories)
        {
            UnreadableIgnoreFiles = walk.UnreadableIgnoreFiles,
        };
    }

    /// <summary>The order of children: ignoring case unless the storage tells names apart by case.</summary>
    internal static StringComparer NameComparer(IStorage source) =>
        source.Capabilities.HasFlag(StorageCapabilities.CaseSensitive) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>The folder name for a file system storage (its full path when it has none, e.g. a drive root), "" for others.</summary>
    public static string DefaultRootName(IStorage source)
    {
        if (source is not FileSystemStorage fs)
            return "";
        var name = Path.GetFileName(fs.RootPath);
        return name.Length > 0 ? name : fs.RootPath;
    }

    /// <summary>What progress shows for a folder: the full path on a file system, else the storage path.</summary>
    internal static string DisplayPath(IStorage source, string relativePath)
    {
        if (source is FileSystemStorage fs)
        {
            try
            {
                return fs.FullPathOf(relativePath);
            }
            catch (ArgumentException)
            {
            }
        }
        return relativePath;
    }

    /// <summary>
    /// Reads a <c>.backupignore</c> file; null when it cannot be read (its patterns are then not applied).
    /// A source that went away as a whole is an error, not an unreadable file.
    /// </summary>
    internal static async Task<string[]?> ReadIgnoreLinesAsync(IStorage source, string path, CancellationToken ct)
    {
        try
        {
            await using var stream = await source.OpenReadAsync(path, ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>();
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                lines.Add(line);
            return [.. lines];
        }
        catch (StorageUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            // ArgumentException: a path this storage cannot address.
            return null;
        }
    }

    private sealed class Walk(IStorage source, IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        private readonly Stopwatch _sinceReport = Stopwatch.StartNew();
        private readonly StringComparer _comparer = NameComparer(source);
        private readonly NamePool _names = new();

        public List<NestedIgnoreFile> IgnoreFiles { get; } = [];
        public List<string> UnreadableIgnoreFiles { get; } = [];
        public int Files { get; private set; }
        public int Directories { get; private set; }

        public async Task<IndexNode> ScanDirectoryAsync(string name, string relativePath, int depth, bool isRoot,
            DateTime lastWriteUtc)
        {
            ct.ThrowIfCancellationRequested();
            ReportThrottled(relativePath);

            var entries = new List<StorageEntry>();
            string? error = null;
            try
            {
                await foreach (var entry in source.ListAsync(relativePath, recursive: false, ct).ConfigureAwait(false))
                    entries.Add(entry);
            }
            catch (StorageNotFoundException) when (isRoot)
            {
                throw;   // the source itself is missing
            }
            catch (StorageUnavailableException)
            {
                throw;
            }
            catch (Exception ex) when (ex is StorageException or ArgumentException)
            {
                error = ex.Message;   // what was listed so far is kept
            }

            if (isRoot)
                lastWriteUtc = await RootTimeAsync().ConfigureAwait(false);

            var children = new List<IndexNode>();
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var childName = _names.Get(StoragePath.Name(entry.Path));

                if (entry.IsDirectory)
                {
                    Directories++;
                    if (entry.IsLink)
                        children.Add(Leaf(childName, entry, CoreTexts.English("core.scan.link")));
                    else if (depth + 1 > SourceIndexer.MaxDepth)
                        children.Add(Leaf(childName, entry, CoreTexts.English("core.scan.tooDeep")));
                    else
                        children.Add(await ScanDirectoryAsync(childName, entry.Path, depth + 1, false, entry.ModifiedUtc).ConfigureAwait(false));
                }
                else
                {
                    Files++;
                    children.Add(new IndexNode
                    {
                        Name = childName,
                        IsDirectory = false,
                        Size = entry.Size,
                        LastWriteUtc = entry.ModifiedUtc,
                        // Only a size of 0 may be "unknown until read" (a Google document): its stamp tells a change then.
                        Stamp = entry.Size == 0 ? entry.Stamp : null,
                    });
                    if (childName.Equals(IgnoreOrigins.NestedFileName, StringComparison.OrdinalIgnoreCase))
                        await ReadIgnoreFileAsync(entry.Path, relativePath).ConfigureAwait(false);
                }
            }

            children.Sort((a, b) => _comparer.Compare(a.Name, b.Name));
            return new IndexNode
            {
                Name = name,
                IsDirectory = true,
                LastWriteUtc = lastWriteUtc,
                Children = children,
                Error = error,
            };
        }

        private static IndexNode Leaf(string name, StorageEntry entry, string error) => new()
        {
            Name = name,
            IsDirectory = true,
            LastWriteUtc = entry.ModifiedUtc,
            Error = error,
        };

        /// <summary>The time of the root itself (best effort: a root is in no listing).</summary>
        private async Task<DateTime> RootTimeAsync()
        {
            try
            {
                return (await source.StatAsync("", ct).ConfigureAwait(false))?.ModifiedUtc ?? default;
            }
            catch (StorageException)
            {
                return default;
            }
        }

        private async Task ReadIgnoreFileAsync(string path, string directoryRelativePath)
        {
            if (await ReadIgnoreLinesAsync(source, path, ct).ConfigureAwait(false) is { } lines)
                IgnoreFiles.Add(new NestedIgnoreFile(directoryRelativePath, lines));
            else
                UnreadableIgnoreFiles.Add(IgnoreOrigins.ForNestedFile(directoryRelativePath));
        }

        private void ReportThrottled(string currentDirectory)
        {
            if (progress is null || _sinceReport.Elapsed < ProgressInterval)
                return;
            _sinceReport.Restart();
            progress.Report(new IndexProgress(Files, Directories, DisplayPath(source, currentDirectory)));
        }
    }
}
