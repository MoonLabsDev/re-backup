using System.Runtime.CompilerServices;

namespace ReBackup.Storage.InMemory;

/// <summary>
/// A <see cref="IStorage"/> that lives in memory, for tests and as the reference behaviour of the contract. Thread-safe through one lock.
/// Paths are compared ordinally (case-sensitive) whatever <see cref="StorageCapabilities.CaseSensitive"/> says; the flag is only reported.
/// Without <see cref="StorageCapabilities.EmptyDirectories"/> directories are the implied prefixes of the file paths and <see cref="EnsureDirectoryAsync"/> does nothing.
/// <see cref="CreateOptions.Durable"/> is ignored: there is no stable storage to flush to.
/// <see cref="StorageEntry.Stamp"/> is <c>"{length}:{modifiedTicks}:{version}"</c>; the per-file version counter keeps the stamp different
/// when two writes share length and tick. Directory entries have size 0 and <see cref="DateTime.UnixEpoch"/> as modified time.
/// </summary>
public sealed class InMemoryStorage : IStorage
{
    private sealed record FileData(byte[] Content, DateTime ModifiedUtc, long Version);

    private readonly object _gate = new();
    private readonly Dictionary<string, FileData> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private long _versionCounter;

    public InMemoryStorage(
        StorageCapabilities capabilities = StorageCapabilities.EmptyDirectories | StorageCapabilities.SetModifiedTime,
        TimeProvider? time = null)
    {
        Capabilities = capabilities;
        _time = time ?? TimeProvider.System;
    }

    public StorageCapabilities Capabilities { get; }

    /// <summary>What <see cref="GetFreeSpaceAsync"/> returns; <c>null</c> means unknown.</summary>
    public long? FreeSpace { get; set; }

    /// <summary>The committed file paths, for assertions.</summary>
    public IReadOnlyCollection<string> Files
    {
        get { lock (_gate) return _files.Keys.ToList(); }
    }

    /// <summary>The content of a committed file, for assertions.</summary>
    public byte[] ReadAllBytes(string path)
    {
        StoragePath.Validate(path);
        lock (_gate)
        {
            if (!_files.TryGetValue(path, out var file)) throw new StorageNotFoundException(path);
            return file.Content.ToArray();
        }
    }

    /// <summary>Puts a committed file in place (test arrangement), replacing an existing one.</summary>
    public void AddFile(string path, byte[] content, DateTime? modifiedUtc = null)
    {
        StoragePath.Validate(path);
        lock (_gate) Commit(path, content.ToArray(), modifiedUtc ?? _time.GetUtcNow().UtcDateTime, overwrite: true);
    }

    public Task<StorageEntry?> StatAsync(string path, CancellationToken ct)
    {
        StoragePath.Validate(path);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_files.TryGetValue(path, out var file)) return Task.FromResult<StorageEntry?>(FileEntry(path, file));
            return Task.FromResult(IsDirectory(path) ? DirectoryEntry(path) : null);
        }
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive, [EnumeratorCancellation] CancellationToken ct)
    {
        StoragePath.Validate(folder);
        ct.ThrowIfCancellationRequested();
        List<StorageEntry> snapshot;
        lock (_gate)
        {
            if (folder.Length > 0 && !IsDirectory(folder)) throw new StorageNotFoundException(folder);
            snapshot = Snapshot(folder, recursive);
        }
        foreach (var entry in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
        }
        await Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        StoragePath.Validate(path);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_files.TryGetValue(path, out var file)) throw new StorageNotFoundException(path);
            return Task.FromResult<Stream>(new MemoryStream(file.Content, writable: false));
        }
    }

    public Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct)
    {
        StoragePath.Validate(path);
        if (path.Length == 0) throw new ArgumentException("A file needs a path.", nameof(path));
        if (options.ExpectedStamp is not null && !options.Overwrite)
            throw new ArgumentException("An expected stamp needs Overwrite.", nameof(options));
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!options.Overwrite && (_files.ContainsKey(path) || IsDirectory(path)))
                throw new StorageConflictException(path, $"'{path}' already exists.");
        }
        return Task.FromResult<StorageWriter>(new Writer(this, path, options));
    }

    public Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        foreach (var path in paths) StoragePath.Validate(path);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var path in paths)
            {
                if (path.Length == 0) throw new StorageConflictException(path, "The root cannot be deleted.");
                if (_files.Remove(path)) continue;
                if (!IsDirectory(path)) continue;
                if (HasChildren(path)) throw new StorageConflictException(path, $"The directory '{path}' is not empty.");
                _directories.Remove(path);
            }
        }
        return Task.CompletedTask;
    }

    public Task EnsureDirectoryAsync(string path, CancellationToken ct)
    {
        StoragePath.Validate(path);
        ct.ThrowIfCancellationRequested();
        if (!Capabilities.HasFlag(StorageCapabilities.EmptyDirectories)) return Task.CompletedTask;
        lock (_gate) AddDirectoryChain(path);
        return Task.CompletedTask;
    }

    public Task<long?> GetFreeSpaceAsync(CancellationToken ct) => Task.FromResult(FreeSpace);

    // Everything below runs under _gate.

    private bool IsDirectory(string path)
    {
        if (path.Length == 0 || _directories.Contains(path)) return true;
        var prefix = path + "/";
        return _files.Keys.Any(file => file.StartsWith(prefix, StringComparison.Ordinal));
    }

    private bool HasChildren(string path)
    {
        var prefix = path + "/";
        return _files.Keys.Any(file => file.StartsWith(prefix, StringComparison.Ordinal))
            || _directories.Any(dir => dir.StartsWith(prefix, StringComparison.Ordinal));
    }

    private void AddDirectoryChain(string path)
    {
        for (var dir = path; dir.Length > 0; dir = StoragePath.Parent(dir))
        {
            if (_files.ContainsKey(dir)) throw new StorageConflictException(dir, $"'{dir}' is a file.");
            _directories.Add(dir);
        }
    }

    private void Commit(string path, byte[] content, DateTime modifiedUtc, bool overwrite, string? expectedStamp = null)
    {
        if (expectedStamp is not null && !(_files.TryGetValue(path, out var current) && FileEntry(path, current).Stamp == expectedStamp))
            throw new StorageConflictException(path, $"'{path}' changed or is missing.");
        if (_files.ContainsKey(path))
        {
            if (!overwrite) throw new StorageConflictException(path, $"'{path}' already exists.");
        }
        else if (IsDirectory(path))
        {
            throw new StorageConflictException(path, $"'{path}' is a directory.");
        }
        for (var parent = StoragePath.Parent(path); parent.Length > 0; parent = StoragePath.Parent(parent))
            if (_files.ContainsKey(parent)) throw new StorageConflictException(parent, $"'{parent}' is a file.");
        if (Capabilities.HasFlag(StorageCapabilities.EmptyDirectories)) AddDirectoryChain(StoragePath.Parent(path));
        _files[path] = new FileData(content, modifiedUtc, ++_versionCounter);
    }

    private static StorageEntry FileEntry(string path, FileData file) =>
        new(path, false, file.Content.Length, file.ModifiedUtc, false, $"{file.Content.Length}:{file.ModifiedUtc.Ticks}:{file.Version}");

    private static StorageEntry DirectoryEntry(string path) =>
        new(path, true, 0, DateTime.UnixEpoch, false, null);

    private List<StorageEntry> Snapshot(string folder, bool recursive)
    {
        var prefix = folder.Length == 0 ? "" : folder + "/";
        var result = new List<StorageEntry>();
        var directories = new HashSet<string>(StringComparer.Ordinal);

        // A directory (explicit or implied by a file) and every ancestor of it below the listed folder; a direct listing keeps only the first level.
        void AddDirectoryChain(string path)
        {
            for (var dir = path; dir.Length > prefix.Length; dir = StoragePath.Parent(dir))
                if (recursive || StoragePath.Parent(dir) == folder) directories.Add(dir);
        }

        foreach (var (path, file) in _files)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            AddDirectoryChain(StoragePath.Parent(path));
            if (recursive || StoragePath.Parent(path) == folder) result.Add(FileEntry(path, file));
        }
        foreach (var dir in _directories)
            if (dir.StartsWith(prefix, StringComparison.Ordinal)) AddDirectoryChain(dir);

        result.AddRange(directories.Select(DirectoryEntry));
        return result;
    }

    private sealed class Writer : StorageWriter
    {
        private readonly InMemoryStorage _owner;
        private readonly string _path;
        private readonly CreateOptions _options;
        private MemoryStream? _buffer = new();
        private bool _committed;

        public Writer(InMemoryStorage owner, string path, CreateOptions options)
        {
            _owner = owner;
            _path = path;
            _options = options;
        }

        public override bool CanWrite => _buffer is not null && !_committed;

        private MemoryStream Buffer
        {
            get
            {
                if (_committed) throw new InvalidOperationException("The file is already committed.");
                ObjectDisposedException.ThrowIf(_buffer is null, this);
                return _buffer;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Buffer.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => Buffer.Write(buffer);
        public override void WriteByte(byte value) => Buffer.WriteByte(value);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Buffer.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }

        public override Task CommitAsync(CancellationToken ct)
        {
            var content = Buffer.ToArray();
            ct.ThrowIfCancellationRequested();
            lock (_owner._gate)
            {
                var modified = _options.ModifiedUtc is { } requested && _owner.Capabilities.HasFlag(StorageCapabilities.SetModifiedTime)
                    ? requested.ToUniversalTime()
                    : _owner._time.GetUtcNow().UtcDateTime;
                _owner.Commit(_path, content, modified, _options.Overwrite, _options.ExpectedStamp);
            }
            _committed = true;
            _buffer?.Dispose();
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buffer?.Dispose();
                _buffer = null;
            }
            base.Dispose(disposing);
        }
    }
}
