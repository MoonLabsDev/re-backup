using System.Runtime.CompilerServices;
using System.Text.Json;
using ReBackup.Core.Backup;
using ReBackup.Core.Localization;
using ReBackup.Core.Versions;
using ReBackup.Storage;

namespace ReBackup.Core.Pool;

/// <summary>
/// A read-only view of one version of an incremental plan's pool that looks like a version folder of a full plan, so
/// that the <see cref="Restorer"/>, file opening and plan copy read it unchanged. Below <c>&lt;version&gt;/</c> it
/// answers from the version's manifest: a file reads its blob from <c>re-pool</c>, folders are the manifest's
/// <c>directories</c> and the parents of its files. The manifest itself and everything outside the version pass through
/// to the target. Every write throws <see cref="NotSupportedException"/>.
/// </summary>
/// <remarks>
/// The manifest is read on the first call that looks below the version (and again after a call that could not read
/// it), and kept in memory: one entry per file and folder. Lookups below the version are case-insensitive unless the
/// target is <see cref="StorageCapabilities.CaseSensitive"/>.
/// </remarks>
public sealed class PoolVersionView : IStorage
{
    private readonly IStorage _target;
    private readonly string _versionName;
    private readonly StringComparer _comparer;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private Tree? _tree;

    /// <summary>A view of the version folder <paramref name="versionName"/> (a storage path) in <paramref name="target"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="versionName"/> is not a storage path, or is the root.</exception>
    public PoolVersionView(IStorage target, string versionName)
    {
        ArgumentNullException.ThrowIfNull(target);
        StoragePath.Validate(versionName);
        if (versionName.Length == 0)
            throw new ArgumentException("A version is a folder below the root.", nameof(versionName));
        _target = target;
        _versionName = versionName;
        _comparer = target.Capabilities.HasFlag(StorageCapabilities.CaseSensitive)
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
    }

    /// <summary>The storage that holds the pool.</summary>
    public IStorage Target => _target;

    /// <summary>The version's folder in <see cref="Target"/>.</summary>
    public string VersionName => _versionName;

    /// <summary>The target's capabilities without <see cref="StorageCapabilities.Links"/>: the view never shows a link.</summary>
    public StorageCapabilities Capabilities => _target.Capabilities & ~StorageCapabilities.Links;

    public async Task<StorageEntry?> StatAsync(string path, CancellationToken ct)
    {
        if (InVersion(path) is not { } relative)
            return await _target.StatAsync(path, ct).ConfigureAwait(false);
        var tree = await LoadAsync(ct).ConfigureAwait(false);
        if (tree.Files.TryGetValue(relative, out var file))
            return FileEntry(path, file);
        return tree.Directories.ContainsKey(relative) ? DirectoryEntry(path, tree) : null;
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var relative = InVersion(folder);
        if (relative is null && !IsVersion(folder))
        {
            await foreach (var entry in _target.ListAsync(folder, recursive, ct).ConfigureAwait(false))
                yield return entry;
            yield break;
        }

        var tree = await LoadAsync(ct).ConfigureAwait(false);
        relative ??= "";
        if (!tree.Directories.TryGetValue(relative, out var children))
            throw new StorageNotFoundException(folder);
        if (relative.Length == 0 &&
            await _target.StatAsync(ManifestPath, ct).ConfigureAwait(false) is { IsDirectory: false } manifest)
            yield return manifest;

        var pending = new Stack<List<string>>();
        pending.Push(children);
        while (pending.Count > 0)
        {
            foreach (var child in pending.Pop())
            {
                ct.ThrowIfCancellationRequested();
                var path = StoragePath.Combine(_versionName, child);
                if (tree.Files.TryGetValue(child, out var file))
                {
                    yield return FileEntry(path, file);
                    continue;
                }
                yield return DirectoryEntry(path, tree);
                if (recursive)
                    pending.Push(tree.Directories[child]);
            }
        }
    }

    /// <summary>
    /// Below the version: the file's blob. Throws <see cref="StorageNotFoundException"/> for a path the manifest does not
    /// list as a file and for a missing blob; the latter says so in its message (<c>core.restore.blobMissing</c>), which a
    /// restore reports as the file's reason.
    /// </summary>
    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        if (InVersion(path) is not { } relative)
            return await _target.OpenReadAsync(path, ct).ConfigureAwait(false);
        var tree = await LoadAsync(ct).ConfigureAwait(false);
        if (!tree.Files.TryGetValue(relative, out var file))
            throw new StorageNotFoundException(path);
        if (!PoolLayout.TryParseHex(file.Hash, out _))
            throw new StorageIOException(path, $"The manifest names no pool blob for '{path}'.") { NotReadable = true };
        try
        {
            return await _target.OpenReadAsync(PoolLayout.BlobPath(file.Hash), ct).ConfigureAwait(false);
        }
        catch (StorageNotFoundException)
        {
            // No inner exception: the restore reports an inner one's (provider) message instead of this one.
            throw new StorageNotFoundException(path, CoreTexts.English("core.restore.blobMissing"));
        }
    }

    public Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct) =>
        throw new NotSupportedException("A version view is read-only.");

    public Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        throw new NotSupportedException("A version view is read-only.");

    public Task EnsureDirectoryAsync(string path, CancellationToken ct) =>
        throw new NotSupportedException("A version view is read-only.");

    public Task<long?> GetFreeSpaceAsync(CancellationToken ct) => _target.GetFreeSpaceAsync(ct);

    private string ManifestPath => StoragePath.Combine(_versionName, VersionMarkerNames.Manifest);

    private bool IsVersion(string path) => _comparer.Equals(StoragePath.Validate(path), _versionName);

    /// <summary>
    /// The version-relative part of <paramref name="path"/> when it lies below the version and is not the manifest;
    /// null for the version folder itself, the manifest and everything outside (they pass through).
    /// </summary>
    private string? InVersion(string path)
    {
        StoragePath.Validate(path);
        if (path.Length <= _versionName.Length + 1 || path[_versionName.Length] != '/' ||
            !_comparer.Equals(path[.._versionName.Length], _versionName))
            return null;
        var relative = path[(_versionName.Length + 1)..];
        return _comparer.Equals(relative, VersionMarkerNames.Manifest) ? null : relative;
    }

    private static StorageEntry FileEntry(string path, ManifestFile file) =>
        new(path, IsDirectory: false, file.Size, file.MtimeUtc, IsLink: false, Stamp: file.Hash);

    private static StorageEntry DirectoryEntry(string path, Tree tree) =>
        new(path, IsDirectory: true, 0, tree.CreatedUtc, IsLink: false, Stamp: null);

    /// <summary>The manifest as a tree; read once, again after a failed read.</summary>
    /// <exception cref="StorageException">The manifest cannot be read, or is not a manifest (<see cref="StorageIOException"/>).</exception>
    private async Task<Tree> LoadAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _tree) is { } loaded)
            return loaded;
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tree is not null)
                return _tree;
            var tree = new Tree(_comparer);
            var stream = await _target.OpenReadAsync(ManifestPath, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                try
                {
                    var summary = ManifestStream.Read(stream, tree.AddFile, tree.AddDirectory, ct);
                    tree.CreatedUtc = summary.CreatedUtc.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(summary.CreatedUtc, DateTimeKind.Utc)
                        : summary.CreatedUtc.ToUniversalTime();
                }
                catch (JsonException ex)
                {
                    throw new StorageIOException(ManifestPath, ex.Message, ex) { NotReadable = true };
                }
            }
            Volatile.Write(ref _tree, tree);
            return tree;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>The version's files and folders by version-relative path; every folder knows its direct children ("" is the version).</summary>
    private sealed class Tree(StringComparer comparer)
    {
        public Dictionary<string, ManifestFile> Files { get; } = new(comparer);

        /// <summary>Folder → its direct children (version-relative paths).</summary>
        public Dictionary<string, List<string>> Directories { get; } = new(comparer) { [""] = [] };

        public DateTime CreatedUtc { get; set; }

        public void AddFile(ManifestFile file)
        {
            // An entry that is no storage path, or lies where a folder or file is already, cannot be shown; the first wins.
            if (!IsValid(file.Path) || comparer.Equals(file.Path, VersionMarkerNames.Manifest) ||
                Files.ContainsKey(file.Path) || Directories.ContainsKey(file.Path) || !AddParents(file.Path))
                return;
            Files[file.Path] = file;
            Directories[StoragePath.Parent(file.Path)].Add(file.Path);
        }

        public void AddDirectory(string path)
        {
            if (IsValid(path) && !Directories.ContainsKey(path) && !Files.ContainsKey(path) && AddParents(path))
            {
                Directories[path] = [];
                Directories[StoragePath.Parent(path)].Add(path);
            }
        }

        /// <summary>Adds the folders above <paramref name="path"/>; false when one of them is a file.</summary>
        private bool AddParents(string path)
        {
            var parent = StoragePath.Parent(path);
            if (Directories.ContainsKey(parent))
                return true;
            if (Files.ContainsKey(parent) || !AddParents(parent))
                return false;
            Directories[parent] = [];
            Directories[StoragePath.Parent(parent)].Add(parent);
            return true;
        }

        private static bool IsValid(string path)
        {
            try
            {
                return StoragePath.Validate(path).Length > 0;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
