using System.Runtime.CompilerServices;

namespace ReBackup.Storage.FileSystem;

/// <summary>
/// A storage on a Windows folder (local drive or network share). Storage paths map to <c>RootPath\sub\file</c>. Files are written to a
/// <c>.rebackup-tmp</c> sibling and renamed on commit, so a file is either complete or not there; temp files are never listed.
/// </summary>
public sealed class FileSystemStorage : IStorage
{
    /// <summary>Suffix of uncommitted files.</summary>
    internal const string TempSuffix = ".rebackup-tmp";

    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Creates a storage on the folder <paramref name="rootPath"/> (fully qualified; it does not need to exist yet).
    /// Throws <see cref="ArgumentException"/> for a relative root such as <c>relative</c>, <c>C:relative</c> or
    /// <c>\folder</c>, which would resolve against a current directory.
    /// </summary>
    public FileSystemStorage(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
            throw new ArgumentException($"The root of a file system storage must be a fully qualified path: '{rootPath}'.", nameof(rootPath));
        var full = Path.GetFullPath(rootPath);
        var driveRoot = Path.GetPathRoot(full);
        RootPath = full == driveRoot ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>The absolute folder this storage is bound to.</summary>
    public string RootPath { get; }

    public StorageCapabilities Capabilities =>
        StorageCapabilities.FreeSpace | StorageCapabilities.Links | StorageCapabilities.EmptyDirectories | StorageCapabilities.SetModifiedTime;

    /// <summary>The absolute Windows path of a storage path, for opening it in Explorer and for messages. Throws <see cref="ArgumentException"/> for an invalid path.</summary>
    public string FullPathOf(string relativePath)
    {
        StoragePath.Validate(relativePath);
        if (relativePath.Length == 0) return RootPath;

        // ':' and the other characters Windows forbids in names would let a path name a drive or a stream outside the root.
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var segment in relativePath.Split('/'))
        {
            if (segment.IndexOfAny(invalid) >= 0)
                throw new ArgumentException($"A path segment contains a character that is not allowed in a Windows file name: '{relativePath}'.", nameof(relativePath));
            // Windows drops trailing spaces and dots when resolving, so "a " would alias "a" and "..." the parent folder itself.
            if (segment[^1] is ' ' or '.')
                throw new ArgumentException($"A path segment must not end with a space or a dot: '{relativePath}'.", nameof(relativePath));
        }

        var full = Path.GetFullPath(Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = RootPath.EndsWith(Path.DirectorySeparatorChar) ? RootPath : RootPath + Path.DirectorySeparatorChar;
        if (full.Length <= prefix.Length || !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The path leaves the storage root: '{relativePath}'.", nameof(relativePath));
        return full;
    }

    public Task<StorageEntry?> StatAsync(string path, CancellationToken ct)
    {
        var full = FullPathOf(path);
        return Task.Run(() =>
        {
            try
            {
                if (path.EndsWith(TempSuffix, StringComparison.Ordinal)) return null;
                var entry = Describe(path, GetInfo(full));
                // "Not there" is only true when the drive or share is: an offline root would otherwise look empty.
                if (entry is null) FileSystemErrors.ThrowIfRootUnreachable(path, RootPath);
                return entry;
            }
            catch (Exception ex) when (IsIo(ex))
            {
                var mapped = MapError(ex, path);
                if (mapped is null) throw;
                throw mapped;
            }
        }, ct);
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive, [EnumeratorCancellation] CancellationToken ct)
    {
        var fullFolder = FullPathOf(folder);
        var pending = new Stack<string>();
        pending.Push(folder);
        var first = true;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = pending.Pop();
            var isRequested = first;
            first = false;
            var children = await Task.Run(
                () => ReadFolder(current, isRequested ? fullFolder : FullPathOf(current), isRequested), ct).ConfigureAwait(false);
            foreach (var child in children)
            {
                yield return child;
                if (recursive && child.IsDirectory && !child.IsLink) pending.Push(child.Path);
            }
        }
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var full = FullPathOf(path);
        ct.ThrowIfCancellationRequested();
        try
        {
            Stream stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult<Stream>(new FileSystemReadStream(this, path, stream));
        }
        catch (Exception ex) when (IsIo(ex))
        {
            var mapped = MapError(ex, path);
            if (mapped is null) throw;
            throw mapped;
        }
    }

    public Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var full = FullPathOf(path);
        if (path.Length == 0) throw new ArgumentException("A file path is required.", nameof(path));
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!options.Overwrite && (File.Exists(full) || Directory.Exists(full)))
                throw new StorageConflictException(path);

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var (stream, temp) = CreateTemp(full);
            return Task.FromResult<StorageWriter>(new FileSystemWriter(this, path, full, temp, stream, options));
        }
        catch (Exception ex) when (IsIo(ex))
        {
            var mapped = MapError(ex, path);
            if (mapped is null) throw;
            throw mapped;
        }
    }

    public Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var targets = paths.Select(p => (Path: p, Full: FullPathOf(p))).ToList();
        foreach (var (p, _) in targets)
            if (p.Length == 0) throw new StorageConflictException(p, "The root cannot be deleted.");

        return Task.Run(() =>
        {
            foreach (var (path, full) in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // A junction or directory symlink is removed as a link, never followed: non-recursive Directory.Delete does that.
                    if (Directory.Exists(full)) Directory.Delete(full, recursive: false);
                    else File.Delete(full);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException && FileSystemErrors.IsRootReachable(RootPath))
                {
                    // Already gone.
                }
                catch (Exception ex) when (IsIo(ex))
                {
                    var mapped = MapError(ex, path);
                    if (mapped is null) throw;
                    throw mapped;
                }
            }
        }, ct);
    }

    public Task EnsureDirectoryAsync(string path, CancellationToken ct)
    {
        var full = FullPathOf(path);
        return Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(full);
            }
            catch (Exception ex) when (IsIo(ex))
            {
                var mapped = MapError(ex, path);
                if (mapped is null) throw;
                throw mapped;
            }
        }, ct);
    }

    public Task<long?> GetFreeSpaceAsync(CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var driveRoot = Path.GetPathRoot(RootPath);
            // Network shares (UNC paths) have no drive letter: unknown, so the preflight is skipped; a full disk is still caught while copying.
            if (string.IsNullOrEmpty(driveRoot) || driveRoot.StartsWith(@"\\", StringComparison.Ordinal)) return (long?)null;
            return new DriveInfo(driveRoot).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }, ct);

    /// <summary>Maps the failures of a call on this storage; <c>null</c> when the original exception should surface.</summary>
    internal StorageException? MapError(Exception ex, string path) => FileSystemErrors.Map(ex, path, RootPath);

    private static bool IsIo(Exception ex) => ex is IOException or UnauthorizedAccessException;

    private List<StorageEntry> ReadFolder(string folder, string full, bool isRequested)
    {
        try
        {
            var result = new List<StorageEntry>();
            foreach (var info in new DirectoryInfo(full).EnumerateFileSystemInfos())
            {
                var entry = Describe(StoragePath.Combine(folder, info.Name), info);
                if (entry is not null) result.Add(entry);
            }
            return result;
        }
        catch (DirectoryNotFoundException) when (!isRequested && FileSystemErrors.IsRootReachable(RootPath))
        {
            // A subfolder removed while the listing was running.
            return new List<StorageEntry>();
        }
        catch (Exception ex) when (IsIo(ex))
        {
            var mapped = MapError(ex, folder);
            if (mapped is null) throw;
            throw mapped;
        }
    }

    private static FileSystemInfo GetInfo(string full)
    {
        var file = new FileInfo(full);
        return file.Exists ? file : new DirectoryInfo(full);
    }

    private static StorageEntry? Describe(string path, FileSystemInfo info)
    {
        if (!info.Exists) return null;
        var isLink = info.LinkTarget is not null;
        if (info is FileInfo file)
        {
            if (file.Name.EndsWith(TempSuffix, StringComparison.Ordinal)) return null;
            var modified = file.LastWriteTimeUtc;
            return new StorageEntry(path, false, file.Length, modified, isLink, $"{file.Length}:{modified.Ticks}");
        }
        return new StorageEntry(path, true, 0, info.LastWriteTimeUtc, isLink, null);
    }

    private static (FileStream Stream, string Path) CreateTemp(string target)
    {
        while (true)
        {
            var temp = $"{target}.{Random.Shared.Next():x8}{TempSuffix}";
            try
            {
                var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                return (stream, temp);
            }
            catch (IOException) when (File.Exists(temp))
            {
                // Name collision: pick another random name.
            }
        }
    }
}
