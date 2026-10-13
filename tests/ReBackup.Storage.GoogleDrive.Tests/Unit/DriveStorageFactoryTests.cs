using System.Text;
using FluentAssertions;
using ReBackup.Storage.GoogleDrive.Connections;
using ReBackup.Storage.GoogleDrive.Tests.Fakes;

namespace ReBackup.Storage.GoogleDrive.Tests.Unit;

public sealed class DriveStorageFactoryTests
{
    private const string Root = InMemoryDriveApi.RootId;
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly InMemoryDriveApi _api = new();
    private readonly List<DriveConnection> _apisBuilt = [];

    private sealed class RecordingFactory : IStorageFactory
    {
        public List<StorageLocation> Opened { get; } = [];

        public IStorage Open(StorageLocation location)
        {
            Opened.Add(location);
            return new InMemory.InMemoryStorage();
        }
    }

    private static DriveConnection Connection(string id = "c1", string rootId = Root, string key = "{\"k\":1}") =>
        new(new DriveConnectionInfo(id, "Docs", "a1", rootId, "Root", null), "backup@project.iam.gserviceaccount.com", key);

    private DriveStorageFactory Factory(IStorageFactory inner, Func<string, DriveConnection?> resolve) =>
        new(inner, resolve, connection =>
        {
            _apisBuilt.Add(connection);
            return _api;
        });

    private DriveStorageFactory Factory() => Factory(new RecordingFactory(), id => id == "c1" ? Connection() : null);

    private static async Task WriteAsync(IStorage storage, string path, string text)
    {
        await using var writer = await storage.CreateAsync(path, new CreateOptions(), Ct);
        await writer.WriteAsync(Encoding.UTF8.GetBytes(text), Ct);
        await writer.CommitAsync(Ct);
    }

    private static async Task<List<StorageEntry>> ListAsync(IStorage storage, string folder, bool recursive)
    {
        var entries = new List<StorageEntry>();
        await foreach (var entry in storage.ListAsync(folder, recursive, Ct)) entries.Add(entry);
        return entries;
    }

    [Fact]
    public void Other_kinds_go_to_the_inner_factory()
    {
        var inner = new RecordingFactory();
        using var factory = Factory(inner, _ => throw new InvalidOperationException("not asked"));
        var s3 = new StorageLocation("s3", "backups", "c1");

        factory.Open(StorageLocation.FileSystem(@"C:\data")).Should().BeOfType<InMemory.InMemoryStorage>();
        factory.Open(s3);

        inner.Opened.Should().Equal(StorageLocation.FileSystem(@"C:\data"), s3);
        _apisBuilt.Should().BeEmpty();
    }

    [Fact]
    public void The_kind_is_gdrive()
    {
        DriveStorageFactory.Kind.Should().Be("gdrive");
    }

    [Fact]
    public void Unknown_connection_is_not_found()
    {
        using var factory = Factory();
        var act = () => factory.Open(new StorageLocation("gdrive", "backups", "other"));
        act.Should().Throw<StorageNotFoundException>().WithMessage("*Drive connection*other*");
    }

    [Fact]
    public void Missing_connection_id_is_not_found()
    {
        using var factory = Factory();
        var act = () => factory.Open(new StorageLocation("gdrive", "backups"));
        act.Should().Throw<StorageNotFoundException>();
    }

    [Fact]
    public void A_key_that_cannot_be_read_surfaces_as_the_stores_error()
    {
        using var factory = Factory(new RecordingFactory(), id => throw new StorageIOException(id, "enter the key again"));
        var act = () => factory.Open(new StorageLocation("gdrive", "", "c1"));
        act.Should().Throw<StorageIOException>().WithMessage("*enter the key again*");
    }

    [Fact]
    public void An_invalid_path_is_refused()
    {
        using var factory = Factory();
        var act = () => factory.Open(new StorageLocation("gdrive", "a//b", "c1"));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task The_root_location_is_the_connection_root()
    {
        using var factory = Factory();
        var storage = factory.Open(new StorageLocation("gdrive", "", "c1"));

        await WriteAsync(storage, "a.txt", "hello");

        storage.Capabilities.Should().Be(StorageCapabilities.SetModifiedTime | StorageCapabilities.EmptyDirectories);
        _api.Children(Root).Should().ContainSingle().Which.Name.Should().Be("a.txt");
    }

    [Fact]
    public async Task A_path_is_a_folder_below_the_connection_root()
    {
        using var factory = Factory();
        var storage = factory.Open(new StorageLocation("gdrive", "backups/pc1", "c1"));

        (await storage.StatAsync("", Ct)).Should().BeNull("the folder does not exist yet");
        await storage.EnsureDirectoryAsync("", Ct);
        await WriteAsync(storage, "v1/a.txt", "hello");

        var backups = _api.Children(Root).Should().ContainSingle().Which;
        backups.Name.Should().Be("backups");
        var pc1 = _api.Children(backups.Id).Should().ContainSingle().Which;
        pc1.Name.Should().Be("pc1");
        _api.Children(pc1.Id).Should().ContainSingle().Which.Name.Should().Be("v1");

        (await storage.StatAsync("", Ct))!.Should().Match<StorageEntry>(e => e.Path == "" && e.IsDirectory);
        (await storage.StatAsync("v1/a.txt", Ct))!.Should().Match<StorageEntry>(e => e.Path == "v1/a.txt" && e.Size == 5);
        (await ListAsync(storage, "", true)).Select(e => e.Path).Should().BeEquivalentTo("v1", "v1/a.txt");
        (await ListAsync(storage, "v1", false)).Select(e => e.Path).Should().Equal("v1/a.txt");
        await using (var read = await storage.OpenReadAsync("v1/a.txt", Ct))
            new StreamReader(read).ReadToEnd().Should().Be("hello");

        await storage.DeleteAsync(["v1/a.txt", "v1"], Ct);
        (await ListAsync(storage, "", true)).Should().BeEmpty();
        _api.Children(pc1.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task The_root_of_a_path_location_is_never_deleted()
    {
        using var factory = Factory();
        var storage = factory.Open(new StorageLocation("gdrive", "backups", "c1"));
        await storage.EnsureDirectoryAsync("", Ct);

        var act = () => storage.DeleteAsync([""], Ct);

        await act.Should().ThrowAsync<StorageConflictException>();
        _api.Children(Root).Should().ContainSingle().Which.Name.Should().Be("backups");
    }

    [Fact]
    public async Task Listing_a_path_location_that_does_not_exist_is_not_found()
    {
        using var factory = Factory();
        var storage = factory.Open(new StorageLocation("gdrive", "backups", "c1"));

        var act = () => ListAsync(storage, "", false);

        await act.Should().ThrowAsync<StorageNotFoundException>();
    }

    [Fact]
    public void One_api_per_connection_state_is_kept()
    {
        var key = "{\"k\":1}";
        using var factory = Factory(new RecordingFactory(), id => id is "c1" or "c2" ? Connection(id, key: key) : null);

        factory.Open(new StorageLocation("gdrive", "a", "c1"));
        factory.Open(new StorageLocation("gdrive", "b", "c1"));
        factory.Open(new StorageLocation("gdrive", "", "c2"));
        key = "{\"k\":2}";
        factory.Open(new StorageLocation("gdrive", "a", "c1"));

        _apisBuilt.Select(c => c.Info.Id).Should().Equal("c1", "c2", "c1");
    }

    [Fact]
    public void Disposing_the_factory_disposes_the_apis_it_built()
    {
        var api = new DisposableApi();
        var factory = new DriveStorageFactory(new RecordingFactory(), _ => Connection(), _ => api);
        factory.Open(new StorageLocation("gdrive", "", "c1"));

        factory.Dispose();

        api.Disposed.Should().BeTrue();
        var act = () => factory.Open(new StorageLocation("gdrive", "", "c1"));
        act.Should().Throw<ObjectDisposedException>();
    }

    private sealed class DisposableApi : IDriveApi, IDisposable
    {
        private readonly InMemoryDriveApi _inner = new();

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;

        public Task<IReadOnlyList<DriveItem>> ListChildrenAsync(string folderId, CancellationToken ct, string? name = null) => _inner.ListChildrenAsync(folderId, ct, name);
        public Task<DriveItem> GetAsync(string id, CancellationToken ct) => _inner.GetAsync(id, ct);
        public Task<DriveItem> CreateFolderAsync(string parentId, string name, CancellationToken ct) => _inner.CreateFolderAsync(parentId, name, ct);
        public Task<DriveItem> UploadAsync(string parentId, string name, Stream content, DateTime? modifiedUtc, CancellationToken ct) => _inner.UploadAsync(parentId, name, content, modifiedUtc, ct);
        public Task<DriveItem> UpdateContentAsync(string id, Stream content, DateTime? modifiedUtc, CancellationToken ct) => _inner.UpdateContentAsync(id, content, modifiedUtc, ct);
        public Task DeleteAsync(string id, CancellationToken ct) => _inner.DeleteAsync(id, ct);
        public Task<Stream> DownloadAsync(string id, CancellationToken ct) => _inner.DownloadAsync(id, ct);
        public Task<Stream> ExportAsync(string id, string mimeType, CancellationToken ct) => _inner.ExportAsync(id, mimeType, ct);
        public Task<IReadOnlyList<SharedDrive>> ListSharedDrivesAsync(CancellationToken ct) => _inner.ListSharedDrivesAsync(ct);
    }
}
