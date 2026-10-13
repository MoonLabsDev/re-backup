using System.Runtime.CompilerServices;

namespace ReBackup.Storage.GoogleDrive;

/// <summary>
/// A storage on the folder <c>basePath</c> of another storage (a location's path below its Drive connection root): every path is
/// taken relative to that folder, entries come back relative to it, and the folder itself is the root (never deleted; it need not
/// exist until something is written or <see cref="EnsureDirectoryAsync"/> is called). Exceptions keep the path below the
/// connection root, which names the place on Drive.
/// </summary>
internal sealed class DriveFolderStorage : IStorage
{
    private readonly IStorage _inner;
    private readonly string _base;

    public DriveFolderStorage(IStorage inner, string basePath)
    {
        ArgumentNullException.ThrowIfNull(inner);
        StoragePath.Validate(basePath);
        if (basePath.Length == 0) throw new ArgumentException("A folder path is required.", nameof(basePath));
        _inner = inner;
        _base = basePath;
    }

    public StorageCapabilities Capabilities => _inner.Capabilities;

    public async Task<StorageEntry?> StatAsync(string path, CancellationToken ct)
    {
        var entry = await _inner.StatAsync(Inner(path), ct).ConfigureAwait(false);
        return entry is null ? null : entry with { Path = path };
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive, [EnumeratorCancellation] CancellationToken ct)
    {
        var prefix = _base + "/";
        await foreach (var entry in _inner.ListAsync(Inner(folder), recursive, ct).ConfigureAwait(false))
            yield return entry with { Path = entry.Path[prefix.Length..] };
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        if (StoragePath.Validate(path).Length == 0) throw new StorageNotFoundException(path);
        return _inner.OpenReadAsync(Inner(path), ct);
    }

    public Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct)
    {
        if (StoragePath.Validate(path).Length == 0) throw new ArgumentException("A file path is required.", nameof(path));
        return _inner.CreateAsync(Inner(path), options, ct);
    }

    public Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths) StoragePath.Validate(path);
        foreach (var path in paths)
            if (path.Length == 0) throw new StorageConflictException(path, "The root cannot be deleted.");
        return _inner.DeleteAsync(paths.Select(Inner).ToList(), ct);
    }

    public Task EnsureDirectoryAsync(string path, CancellationToken ct) => _inner.EnsureDirectoryAsync(Inner(path), ct);

    public Task<long?> GetFreeSpaceAsync(CancellationToken ct) => _inner.GetFreeSpaceAsync(ct);

    private string Inner(string path) => StoragePath.Combine(_base, StoragePath.Validate(path));
}
