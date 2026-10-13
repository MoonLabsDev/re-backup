using System.Security.Cryptography;

namespace ReBackup.Storage.GoogleDrive.Tests.Fakes;

/// <summary>
/// An in-memory Google Drive: folders and files by ID, same-name siblings allowed, Google files that export to fixed content.
/// <see cref="OnCall"/> runs before every call (operation name, the ID or parent ID it is about) and may throw or change the drive.
/// </summary>
internal sealed class InMemoryDriveApi : IDriveApi
{
    public const string RootId = "root";
    public const int ExportLimit = 10 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly List<SharedDrive> _sharedDrives = [];
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
    private int _nextId;
    private long _nextVersion = 1;

    public InMemoryDriveApi()
    {
        _nodes[RootId] = new Node(RootId, "Root", DriveMimeTypes.Folder, null, [], DateTime.UnixEpoch, 1, null);
    }

    /// <summary>Runs before every call with the operation ("list", "get", "createFolder", "upload", "update", "delete", "download", "export", "sharedDrives") and its ID.</summary>
    public Func<string, string, CancellationToken, Task>? OnCall { get; set; }

    /// <summary>
    /// Runs after a call that changes the drive ("createFolder", "upload", "update", "delete") has taken effect, with the ID of
    /// the item it made or changed; throwing here is a lost answer: the change stays.
    /// </summary>
    public Func<string, string, CancellationToken, Task>? AfterCall { get; set; }

    public DateTime Now { get; set; } = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    public int Calls(string operation)
    {
        lock (_gate) return _calls.GetValueOrDefault(operation);
    }

    public string AddFolder(string parentId, string name) => Add(parentId, name, DriveMimeTypes.Folder, null);

    public string AddFile(string parentId, string name, byte[] content, DateTime? modifiedUtc = null) =>
        Add(parentId, name, "application/octet-stream", content, modifiedUtc);

    public string AddGoogleFile(string parentId, string name, string mimeType, byte[]? export = null) =>
        Add(parentId, name, mimeType, export ?? []);

    public void AddSharedDrive(string id, string name)
    {
        lock (_gate) _sharedDrives.Add(new SharedDrive(id, name));
    }

    /// <summary>The children of the folder, for assertions (no call is counted).</summary>
    public IReadOnlyList<DriveItem> Children(string folderId)
    {
        lock (_gate) return _nodes.Values.Where(n => n.ParentId == folderId).Select(n => n.ToItem()).ToList();
    }

    public byte[] Content(string id)
    {
        lock (_gate) return _nodes[id].Content.ToArray();
    }

    public DriveItem Item(string id)
    {
        lock (_gate) return _nodes[id].ToItem();
    }

    public bool Exists(string id)
    {
        lock (_gate) return _nodes.ContainsKey(id);
    }

    /// <summary>Removes the item and everything below it, as someone else would.</summary>
    public void Remove(string id)
    {
        lock (_gate) RemoveTree(id);
    }

    public async Task<IReadOnlyList<DriveItem>> ListChildrenAsync(string folderId, CancellationToken ct, string? name = null)
    {
        await Enter("list", folderId, ct);
        lock (_gate)
        {
            if (!_nodes.TryGetValue(folderId, out var folder) || folder.MimeType != DriveMimeTypes.Folder) throw NotFound();
            return _nodes.Values.Where(n => n.ParentId == folderId && (name is null || n.Name == name))
                .OrderBy(n => n.Order).Select(n => n.ToItem()).ToList();
        }
    }

    public async Task<DriveItem> GetAsync(string id, CancellationToken ct)
    {
        await Enter("get", id, ct);
        lock (_gate) return _nodes.TryGetValue(id, out var node) ? node.ToItem() : throw NotFound();
    }

    public async Task<DriveItem> CreateFolderAsync(string parentId, string name, CancellationToken ct)
    {
        await Enter("createFolder", parentId, ct);
        var id = Add(parentId, name, DriveMimeTypes.Folder, null);
        await Leave("createFolder", id, ct);
        return Item(id);
    }

    public async Task<DriveItem> UploadAsync(string parentId, string name, Stream content, DateTime? modifiedUtc, CancellationToken ct)
    {
        await Enter("upload", parentId, ct);
        var bytes = await ReadAllAsync(content, ct);
        var id = Add(parentId, name, "application/octet-stream", bytes, modifiedUtc);
        await Leave("upload", id, ct);
        return Item(id);
    }

    public async Task<DriveItem> UpdateContentAsync(string id, Stream content, DateTime? modifiedUtc, CancellationToken ct)
    {
        await Enter("update", id, ct);
        var bytes = await ReadAllAsync(content, ct);
        DriveItem updated;
        lock (_gate)
        {
            if (!_nodes.TryGetValue(id, out var node) || node.MimeType == DriveMimeTypes.Folder) throw NotFound();
            node.Content = bytes;
            node.ModifiedUtc = modifiedUtc ?? Now;
            node.Version = _nextVersion++;
            updated = node.ToItem();
        }
        await Leave("update", id, ct);
        return updated;
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await Enter("delete", id, ct);
        lock (_gate)
        {
            if (!_nodes.ContainsKey(id)) throw NotFound();
            RemoveTree(id);
        }
        await Leave("delete", id, ct);
    }

    public async Task<Stream> DownloadAsync(string id, CancellationToken ct)
    {
        await Enter("download", id, ct);
        lock (_gate)
        {
            if (!_nodes.TryGetValue(id, out var node) || DriveMimeTypes.IsGoogleType(node.MimeType)) throw NotFound();
            return new MemoryStream(node.Content.ToArray(), writable: false);
        }
    }

    public async Task<Stream> ExportAsync(string id, string mimeType, CancellationToken ct)
    {
        await Enter("export", id, ct);
        lock (_gate)
        {
            if (!_nodes.TryGetValue(id, out var node)) throw NotFound();
            if (DriveMimeTypes.ExportOf(node.MimeType)?.ExportType != mimeType)
                throw new DriveApiException(400, "badRequest");
            if (node.Content.Length > ExportLimit) throw new DriveApiException(403, "exportSizeLimitExceeded");
            return new MemoryStream(node.Content.ToArray(), writable: false);
        }
    }

    public async Task<IReadOnlyList<SharedDrive>> ListSharedDrivesAsync(CancellationToken ct)
    {
        await Enter("sharedDrives", "", ct);
        lock (_gate) return _sharedDrives.ToList();
    }

    private async Task Enter(string operation, string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) _calls[operation] = _calls.GetValueOrDefault(operation) + 1;
        if (OnCall is { } hook) await hook(operation, id, ct);
    }

    private async Task Leave(string operation, string id, CancellationToken ct)
    {
        if (AfterCall is { } hook) await hook(operation, id, ct);
    }

    private string Add(string parentId, string name, string mimeType, byte[]? content, DateTime? modifiedUtc = null)
    {
        lock (_gate)
        {
            if (!_nodes.TryGetValue(parentId, out var parent) || parent.MimeType != DriveMimeTypes.Folder) throw NotFound();
            var id = "id" + ++_nextId;
            _nodes[id] = new Node(id, name, mimeType, parentId, content ?? [], modifiedUtc ?? Now, _nextVersion++, _nextId);
            return id;
        }
    }

    private void RemoveTree(string id)
    {
        foreach (var child in _nodes.Values.Where(n => n.ParentId == id).Select(n => n.Id).ToList()) RemoveTree(child);
        _nodes.Remove(id);
    }

    private static async Task<byte[]> ReadAllAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private static DriveApiException NotFound() => new(404, "notFound");

    private sealed class Node(string id, string name, string mimeType, string? parentId, byte[] content, DateTime modifiedUtc, long version, int? order)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string MimeType { get; } = mimeType;
        public string? ParentId { get; } = parentId;
        public int Order { get; } = order ?? 0;
        public byte[] Content { get; set; } = content;
        public DateTime ModifiedUtc { get; set; } = modifiedUtc;
        public long Version { get; set; } = version;

        public DriveItem ToItem()
        {
            var isFile = !DriveMimeTypes.IsGoogleType(MimeType);
            return new DriveItem(Id, Name, MimeType, isFile ? Content.Length : null, ModifiedUtc,
                isFile ? Convert.ToHexStringLower(MD5.HashData(Content)) : null, Version);
        }
    }
}
