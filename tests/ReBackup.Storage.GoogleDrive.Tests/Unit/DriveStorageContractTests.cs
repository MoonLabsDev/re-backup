using ReBackup.Storage.GoogleDrive.Connections;
using ReBackup.Storage.GoogleDrive.Tests.Fakes;

namespace ReBackup.Storage.GoogleDrive.Tests.Unit;

/// <summary>The storage contract on the in-memory Drive, at the connection root.</summary>
public sealed class DriveStorageContractTests : ReBackup.Storage.Tests.StorageContractTests
{
    protected override IStorage CreateEmpty() => new DriveStorage(new InMemoryDriveApi(), InMemoryDriveApi.RootId);
}

/// <summary>
/// The storage contract on the in-memory Drive, on a folder below the connection root as a location path opens it. Like a
/// folder on disk, the empty storage's root exists: until it does, listing it is "not found".
/// </summary>
public sealed class DriveFolderStorageContractTests : ReBackup.Storage.Tests.StorageContractTests
{
    protected override IStorage CreateEmpty()
    {
        var api = new InMemoryDriveApi();
        var connection = new DriveConnection(
            new DriveConnectionInfo("c1", "Docs", "a1", InMemoryDriveApi.RootId, "Root", null), "backup@project.iam.gserviceaccount.com", "{}");
        var factory = new DriveStorageFactory(new StorageFactory(), _ => connection, _ => api);
        var storage = factory.Open(new StorageLocation(DriveStorageFactory.Kind, "backups/pc1", "c1"));
        storage.EnsureDirectoryAsync("", CancellationToken.None).GetAwaiter().GetResult();
        return storage;
    }
}
