using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace ReBackup.Storage.GoogleDrive;

/// <summary>
/// A storage on Google Drive below the folder (or shared drive) <c>rootId</c>. Folders are real; folder paths are resolved
/// segment by segment and their IDs cached per instance (an entry is dropped when Drive no longer knows its ID).
/// </summary>
/// <remarks>
/// Drive allows siblings with the same name: reading, writing or resolving through such a name throws
/// <see cref="StorageIOException"/> "ambiguous name: &lt;path&gt;", and a listing reports only the first of them. Names that are
/// no valid storage path segment (e.g. containing <c>/</c>) are left out of listings. Google Docs, Sheets, Slides and Drawings
/// are listed under their name plus <c>.docx</c> / <c>.xlsx</c> / <c>.pptx</c> / <c>.pdf</c> with size 0 and read as exports;
/// a real file with that name wins and the Google file is left out (with a warning). Other Google types (shortcuts, forms, …)
/// are left out. Delete is permanent.
/// </remarks>
public sealed class DriveStorage : IStorage
{
    private readonly IDriveApi _api;
    private readonly string _rootId;
    private readonly DriveRetry _retry;
    private readonly TimeProvider _time;

    // Folder path (never "") → folder ID; only unambiguous folders.
    private readonly ConcurrentDictionary<string, string> _folders = new(StringComparer.Ordinal);

    // Creating folders one at a time, so parallel writers never create two folders of the same name.
    private readonly SemaphoreSlim _creatingFolders = new(1, 1);

    /// <summary>Creates a storage on <paramref name="api"/> with the folder or shared drive <paramref name="rootId"/> as its root.</summary>
    public DriveStorage(IDriveApi api, string rootId, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentException.ThrowIfNullOrEmpty(rootId);
        _api = api;
        _rootId = rootId;
        _time = time ?? TimeProvider.System;
        _retry = new DriveRetry(_time);
    }

    public StorageCapabilities Capabilities => StorageCapabilities.SetModifiedTime | StorageCapabilities.EmptyDirectories;

    public async Task<StorageEntry?> StatAsync(string path, CancellationToken ct)
    {
        StoragePath.Validate(path);
        if (path.Length == 0)
        {
            try
            {
                var root = await _retry.RunAsync(c => _api.GetAsync(_rootId, c), ct).ConfigureAwait(false);
                return root.IsFolder ? new StorageEntry("", true, 0, Utc(root.ModifiedUtc), false, null) : null;
            }
            catch (DriveApiException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex) when (ex is not StorageException)
            {
                throw DriveErrors.Map(ex, path, ct);
            }
        }

        return await RunAsync(path, async () =>
        {
            var child = await FindAsync(path, ct).ConfigureAwait(false);
            return child is null ? null : Entry(path, child.Value);
        }, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive, [EnumeratorCancellation] CancellationToken ct)
    {
        StoragePath.Validate(folder);
        var start = await RunAsync(folder, async () =>
        {
            var id = await ResolveFolderAsync(folder, folder, ct).ConfigureAwait(false) ?? throw new StorageNotFoundException(folder);
            return await ListChildrenAsync(id, null, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        var pending = new Queue<(string Path, string Id)>();
        var (path, items) = (folder, (IReadOnlyList<DriveItem>?)start);
        while (true)
        {
            // Null for a folder below the start that is gone meanwhile: left out.
            foreach (var (name, child) in items is null ? [] : Visible(items, path))
            {
                var childPath = StoragePath.Combine(path, name);
                yield return Entry(childPath, child);
                if (recursive && child.Kind == ChildKind.Folder) pending.Enqueue((childPath, child.Item.Id));
            }
            if (!pending.TryDequeue(out var next)) break;
            ct.ThrowIfCancellationRequested();
            path = next.Path;
            items = await ListBelowAsync(next.Path, next.Id, ct).ConfigureAwait(false);
        }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        StoragePath.Validate(path);
        if (path.Length == 0) throw new StorageNotFoundException(path);
        return await RunAsync(path, async () =>
        {
            var found = await FindAsync(path, ct).ConfigureAwait(false);
            if (found is not { Kind: not ChildKind.Folder } child) throw new StorageNotFoundException(path);

            if (child.Kind == ChildKind.File)
            {
                var download = await _retry.RunAsync(c => _api.DownloadAsync(child.Item.Id, c), ct).ConfigureAwait(false);
                return (Stream)new DriveReadStream(download, path);
            }

            // Exports are at most 10 MB: taken into memory whole, so a failure surfaces here and not halfway through a copy.
            return await _retry.RunAsync(async c =>
            {
                var export = await _api.ExportAsync(child.Item.Id, child.ExportType!, c).ConfigureAwait(false);
                await using (export.ConfigureAwait(false))
                {
                    var buffer = new MemoryStream();
                    await export.CopyToAsync(buffer, c).ConfigureAwait(false);
                    buffer.Position = 0;
                    return (Stream)buffer;
                }
            }, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a <see cref="DriveWriter"/>, which buffers to a temporary file and uploads on commit (<see cref="CommitAsync"/>).
    /// A folder at the path, or an exclusive create of an existing path, is a conflict. <see cref="CreateOptions.Durable"/> is ignored.
    /// </summary>
    public async Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        StoragePath.Validate(path);
        if (path.Length == 0) throw new ArgumentException("A file path is required.", nameof(path));
        if (options.ExpectedStamp is not null && !options.Overwrite)
            throw new ArgumentException("An expected stamp needs Overwrite.", nameof(options));

        var existing = await StatAsync(path, ct).ConfigureAwait(false);
        if (existing is { IsDirectory: true }) throw new StorageConflictException(path, $"'{path}' is a folder.");
        if (existing is not null && !options.Overwrite) throw new StorageConflictException(path);
        return new DriveWriter(this, path, options);
    }

    /// <summary>
    /// Deletes in the given order, permanently. A folder with anything in it (also items no listing shows) is a conflict; a
    /// path given as the export name of a Google file deletes that Google file.
    /// </summary>
    public async Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths) StoragePath.Validate(path);
        foreach (var path in paths)
            if (path.Length == 0) throw new StorageConflictException(path, "The root cannot be deleted.");

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            await RunAsync(path, async () =>
            {
                if (await FindAsync(path, ct).ConfigureAwait(false) is not { } child) return true;
                if (child.Kind == ChildKind.Folder)
                {
                    var below = await _retry.RunAsync(c => _api.ListChildrenAsync(child.Item.Id, c), ct).ConfigureAwait(false);
                    if (below.Count > 0) throw new StorageConflictException(path, $"The directory '{path}' is not empty.");
                }
                try
                {
                    await _retry.RunAsync(c => _api.DeleteAsync(child.Item.Id, c), ct).ConfigureAwait(false);
                }
                catch (DriveApiException ex) when (ex.Status == 404)
                {
                    // Gone already.
                }
                if (child.Kind == ChildKind.Folder) DropCachedPath(path);
                return true;
            }, ct).ConfigureAwait(false);
        }
    }

    public async Task EnsureDirectoryAsync(string path, CancellationToken ct)
    {
        StoragePath.Validate(path);
        await RunAsync(path, () => EnsureFolderAsync(path, path, ct), ct).ConfigureAwait(false);
    }

    public Task<long?> GetFreeSpaceAsync(CancellationToken ct) => Task.FromResult<long?>(null);

    /// <summary>
    /// Writes a committed file: creates missing parent folders, then updates an existing file's content (same ID) or uploads a
    /// new one (see <see cref="UploadAsync"/> for how a failed upload is recovered). An exclusive create re-lists the parent
    /// after the upload: a second file of that name means a racing writer, and ours is deleted again. The stamp of a
    /// conditional overwrite is compared right before the update (not atomic).
    /// </summary>
    internal async Task CommitAsync(string path, CreateOptions options, Stream content, CancellationToken ct)
    {
        content.Position = 0;
        var md5 = Convert.ToHexStringLower(await MD5.HashDataAsync(content, ct).ConfigureAwait(false));
        content.Position = 0;
        await RunAsync(path, async () =>
        {
            var parentId = await EnsureFolderAsync(StoragePath.Parent(path), path, ct).ConfigureAwait(false);
            var name = StoragePath.Name(path);
            var existing = await FindInAsync(parentId, StoragePath.Parent(path), name, path, ct).ConfigureAwait(false);
            if (existing is { Kind: ChildKind.Folder }) throw new StorageConflictException(path, $"'{path}' is a folder.");

            if (options.Overwrite)
            {
                if (options.ExpectedStamp is not null && (existing is null || Stamp(existing.Value) != options.ExpectedStamp))
                    throw new StorageConflictException(path);
                if (existing is { Kind: ChildKind.File } file)
                {
                    // The same content again is harmless: repeatable.
                    await _retry.RunAsync(c =>
                    {
                        content.Position = 0;
                        return _api.UpdateContentAsync(file.Item.Id, content, options.ModifiedUtc, c);
                    }, ct).ConfigureAwait(false);
                    return true;
                }
                await UploadAsync(parentId, name, content, md5, options.ModifiedUtc, ct).ConfigureAwait(false);
                return true;
            }

            if (existing is not null) throw new StorageConflictException(path);
            var created = await UploadAsync(parentId, name, content, md5, options.ModifiedUtc, ct).ConfigureAwait(false);
            var now = await CandidatesAsync(parentId, name, ct).ConfigureAwait(false);
            if (now.Any(c => c.Item.Id != created.Id))
            {
                try
                {
                    await _retry.RunAsync(c => _api.DeleteAsync(created.Id, c), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Trace.TraceWarning($"Google Drive: the losing copy of '{path}' could not be deleted: {ex.Message}");
                }
                throw new StorageConflictException(path);
            }
            return true;
        }, ct).ConfigureAwait(false);
    }

    // ---- resolution ----

    private enum ChildKind { Folder, File, Export }

    /// <summary>An item as the storage shows it: under <see cref="Name"/> (plus the export extension for Google files).</summary>
    private readonly record struct Child(DriveItem Item, string Name, ChildKind Kind, string? ExportType);

    /// <summary>Runs <paramref name="operation"/>; once more after a cached folder turned out gone. Errors are mapped to <paramref name="path"/>.</summary>
    private async Task<T> RunAsync<T>(string path, Func<Task<T>> operation, CancellationToken ct)
    {
        try
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (StaleFolderException)
            {
                // The stale entries are dropped: resolve again from the root.
                return await operation().ConfigureAwait(false);
            }
        }
        catch (StaleFolderException ex)
        {
            throw new StorageNotFoundException(path, $"Not found: '{path}'.", ex.InnerException);
        }
        catch (Exception ex) when (ex is not (StorageException or ArgumentException))
        {
            throw DriveErrors.Map(ex, path, ct);
        }
    }

    /// <summary>The item at <paramref name="path"/> (not the root); null when it or a folder on the way is missing.</summary>
    private async Task<Child?> FindAsync(string path, CancellationToken ct)
    {
        var parent = StoragePath.Parent(path);
        var parentId = await ResolveFolderAsync(parent, path, ct).ConfigureAwait(false);
        return parentId is null ? null : await FindInAsync(parentId, parent, StoragePath.Name(path), path, ct).ConfigureAwait(false);
    }

    /// <summary>The ID of the folder at <paramref name="folder"/>; null when it is missing or a file. Ambiguous names on the way throw.</summary>
    private async Task<string?> ResolveFolderAsync(string folder, string requestPath, CancellationToken ct)
    {
        var id = _rootId;
        var prefix = "";
        foreach (var segment in folder.Length == 0 ? [] : folder.Split('/'))
        {
            prefix = StoragePath.Combine(prefix, segment);
            if (_folders.TryGetValue(prefix, out var cached))
            {
                id = cached;
                continue;
            }
            if (await FindInAsync(id, StoragePath.Parent(prefix), segment, requestPath, ct).ConfigureAwait(false) is not { Kind: ChildKind.Folder } child)
                return null;
            id = child.Item.Id;
            _folders[prefix] = id;
        }
        return id;
    }

    /// <summary>Like <see cref="ResolveFolderAsync"/>, creating missing folders; a file on the way is a conflict.</summary>
    private async Task<string> EnsureFolderAsync(string folder, string requestPath, CancellationToken ct)
    {
        var id = _rootId;
        var prefix = "";
        foreach (var segment in folder.Length == 0 ? [] : folder.Split('/'))
        {
            var parentPath = prefix;
            prefix = StoragePath.Combine(prefix, segment);
            if (_folders.TryGetValue(prefix, out var cached))
            {
                id = cached;
                continue;
            }

            var child = await FindInAsync(id, parentPath, segment, requestPath, ct).ConfigureAwait(false);
            if (child is null)
            {
                await _creatingFolders.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    child = await FindInAsync(id, parentPath, segment, requestPath, ct).ConfigureAwait(false)
                        ?? await CreateFolderAsync(id, segment, ct).ConfigureAwait(false);
                }
                finally
                {
                    _creatingFolders.Release();
                }
            }
            if (child.Value.Kind != ChildKind.Folder)
                throw new StorageConflictException(requestPath, $"'{prefix}' is a file, not a folder.");
            id = child.Value.Item.Id;
            _folders[prefix] = id;
        }
        return id;
    }

    /// <summary>Creates a folder; after a server or network failure a single folder of that name found in the parent is taken as it.</summary>
    private async Task<Child> CreateFolderAsync(string parentId, string name, CancellationToken ct)
    {
        var created = await CreateCheckedAsync(parentId, name, c => _api.CreateFolderAsync(parentId, name, c),
            now => now is [{ Kind: ChildKind.Folder } made] ? made.Item : null, ct).ConfigureAwait(false);
        return new Child(created, name, ChildKind.Folder, null);
    }

    /// <summary>
    /// Uploads a new file; after a server or network failure a single file of that name in the parent whose MD5 equals
    /// <paramref name="md5"/> (the content's) is taken as ours: the upload landed, only the answer was lost.
    /// </summary>
    private Task<DriveItem> UploadAsync(string parentId, string name, Stream content, string md5, DateTime? modifiedUtc, CancellationToken ct) =>
        CreateCheckedAsync(parentId, name, c =>
            {
                content.Position = 0;
                return _api.UploadAsync(parentId, name, content, modifiedUtc, c);
            },
            now => now is [{ Kind: ChildKind.File } file] && string.Equals(file.Item.Md5, md5, StringComparison.OrdinalIgnoreCase)
                ? file.Item
                : null,
            ct);

    /// <summary>
    /// Runs a call that creates <paramref name="name"/> in the parent. Rate limits are retried as usual. A server or network
    /// failure may have created the item unseen, so instead of repeating blindly the parent is listed by name:
    /// <paramref name="ours"/> recognises the item as the one this call made (taken as the result); with nothing of that name the
    /// call is repeated after the usual backoff, up to <see cref="DriveRetry.MaxAttempts"/> attempts; anything else (another
    /// item of that name, or no attempts left) throws the failure. Blind repeats could leave two items of the same name.
    /// </summary>
    private async Task<DriveItem> CreateCheckedAsync(string parentId, string name, Func<CancellationToken, Task<DriveItem>> create,
        Func<List<Child>, DriveItem?> ours, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await OnFolderAsync(parentId, create, ct, repeatable: false).ConfigureAwait(false);
            }
            catch (Exception ex) when (DriveErrors.IsServerOrNetworkFailure(ex, ct))
            {
                var now = await CandidatesAsync(parentId, name, ct).ConfigureAwait(false);
                if (ours(now) is { } made) return made;
                if (now.Count > 0 || attempt >= DriveRetry.MaxAttempts) throw;
                await Task.Delay(DriveRetry.DelayFor(attempt), _time, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The single item named <paramref name="name"/> in the folder; null when none; ambiguous names throw.</summary>
    private async Task<Child?> FindInAsync(string folderId, string folderPath, string name, string requestPath, CancellationToken ct)
    {
        var candidates = await CandidatesAsync(folderId, name, ct).ConfigureAwait(false);
        if (candidates.Count > 1)
        {
            var ambiguous = StoragePath.Combine(folderPath, name);
            throw new StorageIOException(requestPath, $"ambiguous name: {ambiguous}");
        }
        return candidates.Count == 0 ? null : candidates[0];
    }

    /// <summary>
    /// Every item the storage shows as <paramref name="name"/> in the folder: the folders and files of that name, or, when there
    /// are none, the Google files whose export takes that name.
    /// </summary>
    private async Task<List<Child>> CandidatesAsync(string folderId, string name, CancellationToken ct)
    {
        var named = (await ListChildrenAsync(folderId, name, ct).ConfigureAwait(false))
            .Select(Classify).OfType<Child>().Where(c => c.Name == name).ToList();
        var real = named.Where(c => c.Kind != ChildKind.Export).ToList();
        if (real.Count > 0) return real;

        foreach (var (_, extension, _) in DriveMimeTypes.Exports)
        {
            if (name.Length <= extension.Length || !name.EndsWith(extension, StringComparison.Ordinal)) continue;
            var exports = (await ListChildrenAsync(folderId, name[..^extension.Length], ct).ConfigureAwait(false))
                .Select(Classify).OfType<Child>().Where(c => c.Kind == ChildKind.Export && c.Name == name);
            named.AddRange(exports);
        }
        return named;
    }

    private Task<IReadOnlyList<DriveItem>> ListChildrenAsync(string folderId, string? name, CancellationToken ct) =>
        OnFolderAsync(folderId, c => _api.ListChildrenAsync(folderId, c, name), ct);

    /// <summary>The children of a folder below a listing's start; null when the folder is gone meanwhile.</summary>
    private async Task<IReadOnlyList<DriveItem>?> ListBelowAsync(string path, string id, CancellationToken ct)
    {
        try
        {
            return await _retry.RunAsync(c => _api.ListChildrenAsync(id, c), ct).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.Status == 404)
        {
            DropCachedPath(path);
            return null;
        }
        catch (Exception ex) when (ex is not StorageException)
        {
            throw DriveErrors.Map(ex, path, ct);
        }
    }

    /// <summary>
    /// The children a listing shows, by name: the first item of each name (same-name siblings), a real file before a Google file
    /// exported under its name. Unambiguous folders are cached.
    /// </summary>
    private IEnumerable<(string Name, Child Child)> Visible(IReadOnlyList<DriveItem> items, string folderPath)
    {
        foreach (var group in items.Select(Classify).OfType<Child>().GroupBy(c => c.Name, StringComparer.Ordinal))
        {
            var children = group.ToList();
            var real = children.Where(c => c.Kind != ChildKind.Export).ToList();
            if (real.Count > 0 && real.Count < children.Count)
                Trace.TraceWarning($"Google Drive: the Google file '{StoragePath.Combine(folderPath, group.Key)}' is left out: a file with that name exists.");
            var shown = real.Count > 0 ? real : children;
            if (shown is [{ Kind: ChildKind.Folder } folder])
                _folders[StoragePath.Combine(folderPath, group.Key)] = folder.Item.Id;
            yield return (group.Key, shown[0]);
        }
    }

    /// <summary>How the storage shows the item; null for items it leaves out (invalid names, Google types without export).</summary>
    private static Child? Classify(DriveItem item)
    {
        if (!IsValidSegment(item.Name)) return null;
        if (item.IsFolder) return new Child(item, item.Name, ChildKind.Folder, null);
        if (DriveMimeTypes.ExportOf(item.MimeType) is { } export)
            return new Child(item, item.Name + export.Extension, ChildKind.Export, export.ExportType);
        if (DriveMimeTypes.IsGoogleType(item.MimeType)) return null;
        return new Child(item, item.Name, ChildKind.File, null);
    }

    private static bool IsValidSegment(string name)
    {
        if (name.Length == 0 || name.Contains('/')) return false;
        try
        {
            StoragePath.Validate(name);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // ---- the folder cache ----

    /// <summary>Runs a call about a folder; a 404 for a cached folder drops it (and everything below) and throws <see cref="StaleFolderException"/>.</summary>
    private async Task<T> OnFolderAsync<T>(string folderId, Func<CancellationToken, Task<T>> call, CancellationToken ct, bool repeatable = true)
    {
        try
        {
            return await _retry.RunAsync(call, ct, repeatable).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.Status == 404 && DropCachedId(folderId))
        {
            throw new StaleFolderException(ex);
        }
    }

    private bool DropCachedId(string folderId)
    {
        var dropped = false;
        foreach (var (path, id) in _folders)
        {
            if (id != folderId) continue;
            DropCachedPath(path);
            dropped = true;
        }
        return dropped;
    }

    private void DropCachedPath(string path)
    {
        foreach (var cached in _folders.Keys)
            if (cached == path || cached.StartsWith(path + "/", StringComparison.Ordinal))
                _folders.TryRemove(cached, out _);
    }

    /// <summary>A cached folder ID that Drive no longer knows; the cache is already cleaned up.</summary>
    private sealed class StaleFolderException(Exception inner) : Exception("A cached folder is gone.", inner);

    // ---- entries ----

    private static StorageEntry Entry(string path, Child child) => child.Kind switch
    {
        ChildKind.Folder => new StorageEntry(path, true, 0, Utc(child.Item.ModifiedUtc), false, null),
        ChildKind.Export => new StorageEntry(path, false, 0, Utc(child.Item.ModifiedUtc), false, Stamp(child)),
        _ => new StorageEntry(path, false, child.Item.Size ?? 0, Utc(child.Item.ModifiedUtc), false, Stamp(child)),
    };

    /// <summary>The MD5 of a file's content (Drive's version when there is none, e.g. for Google files).</summary>
    private static string Stamp(Child child) =>
        child.Kind == ChildKind.File && !string.IsNullOrEmpty(child.Item.Md5)
            ? child.Item.Md5
            : child.Item.Version.ToString(CultureInfo.InvariantCulture);

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };

    /// <summary>A downloaded file's stream whose read failures surface as storage exceptions.</summary>
    private sealed class DriveReadStream(Stream inner, string path) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return inner.Read(buffer, offset, count);
            }
            catch (Exception ex) when (ex is not StorageException)
            {
                throw DriveErrors.Map(ex, path, default);
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not StorageException)
            {
                throw DriveErrors.Map(ex, path, cancellationToken);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
