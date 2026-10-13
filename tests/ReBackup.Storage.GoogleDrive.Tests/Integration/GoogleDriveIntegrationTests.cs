using System.Text;
using FluentAssertions;
using ReBackup.Storage.GoogleDrive.Connections;

namespace ReBackup.Storage.GoogleDrive.Tests.Integration;

/// <summary>
/// Against a real shared drive, only when <c>REBACKUP_GDRIVE_KEY_FILE</c> (a service-account key JSON file) and
/// <c>REBACKUP_GDRIVE_SHARED_DRIVE_ID</c> (a shared drive the account is a Manager of) are set; skipped otherwise. Works in a folder
/// of its own that is removed at the end.
/// </summary>
public sealed class GoogleDriveIntegrationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static (string Key, string DriveId) Settings()
    {
        var keyFile = Environment.GetEnvironmentVariable("REBACKUP_GDRIVE_KEY_FILE");
        var driveId = Environment.GetEnvironmentVariable("REBACKUP_GDRIVE_SHARED_DRIVE_ID");
        Skip.If(string.IsNullOrWhiteSpace(keyFile) || string.IsNullOrWhiteSpace(driveId),
            "REBACKUP_GDRIVE_KEY_FILE and REBACKUP_GDRIVE_SHARED_DRIVE_ID are not set.");
        return (File.ReadAllText(keyFile!), driveId!);
    }

    private static async Task WriteAsync(IStorage storage, string path, byte[] content, CreateOptions? options = null)
    {
        await using var writer = await storage.CreateAsync(path, options ?? new CreateOptions(), Ct);
        await writer.WriteAsync(content, Ct);
        await writer.CommitAsync(Ct);
    }

    private static async Task<byte[]> ReadAsync(IStorage storage, string path)
    {
        await using var stream = await storage.OpenReadAsync(path, Ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct);
        return buffer.ToArray();
    }

    private static async Task<List<StorageEntry>> ListAsync(IStorage storage, string folder, bool recursive)
    {
        var entries = new List<StorageEntry>();
        await foreach (var entry in storage.ListAsync(folder, recursive, Ct)) entries.Add(entry);
        return entries;
    }

    [SkippableFact]
    public async Task Writes_lists_reads_overwrites_and_deletes_on_a_shared_drive()
    {
        var (key, driveId) = Settings();
        var folder = "rebackup-it-" + Guid.NewGuid().ToString("N");
        using var api = new GoogleDriveApi(key, driveId);
        var connection = new DriveConnection(new DriveConnectionInfo("it", "it", "it", driveId, "it", driveId), "", key);
        using var factory = new DriveStorageFactory(new StorageFactory(), _ => connection, _ => api);
        var storage = factory.Open(new StorageLocation(DriveStorageFactory.Kind, folder, "it"));
        var mtime = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var large = new byte[GoogleDriveApi.ResumableThreshold + 123];
        Random.Shared.NextBytes(large);

        try
        {
            await storage.EnsureDirectoryAsync("", Ct);
            await WriteAsync(storage, "a/small.txt", "hello"u8.ToArray(), new CreateOptions(ModifiedUtc: mtime));
            await WriteAsync(storage, "a/large.bin", large);

            (await ListAsync(storage, "", true)).Select(e => e.Path).Should().BeEquivalentTo("a", "a/small.txt", "a/large.bin");
            var small = (await storage.StatAsync("a/small.txt", Ct))!;
            small.Size.Should().Be(5);
            small.ModifiedUtc.Should().Be(mtime);
            (await ReadAsync(storage, "a/small.txt")).Should().Equal("hello"u8.ToArray());
            (await ReadAsync(storage, "a/large.bin")).Should().Equal(large);

            await WriteAsync(storage, "a/small.txt", "hello again"u8.ToArray(), new CreateOptions(Overwrite: true, ExpectedStamp: small.Stamp));
            Encoding.UTF8.GetString(await ReadAsync(storage, "a/small.txt")).Should().Be("hello again");
            var exclusive = () => WriteAsync(storage, "a/small.txt", [1]);
            await exclusive.Should().ThrowAsync<StorageConflictException>();

            await storage.DeleteAsync(["a/small.txt", "a/large.bin", "a"], Ct);
            (await ListAsync(storage, "", true)).Should().BeEmpty();

            var tester = await new DriveConnectionTester(_ => new GoogleDriveApi(key, driveId)).RunAsync(connection, folder, asTarget: true, Ct);
            tester.Outcome.Should().Be(DriveTestOutcome.Ok, tester.Message);
            (await api.ListSharedDrivesAsync(Ct)).Select(d => d.Id).Should().Contain(driveId);
        }
        finally
        {
            foreach (var item in await api.ListChildrenAsync(driveId, Ct, folder))
                await api.DeleteAsync(item.Id, Ct);
        }
    }

    [SkippableFact]
    public async Task Exports_a_google_document_at_the_top_of_the_shared_drive_if_there_is_one()
    {
        var (key, driveId) = Settings();
        using var api = new GoogleDriveApi(key, driveId);
        var document = (await api.ListChildrenAsync(driveId, Ct)).FirstOrDefault(i => DriveMimeTypes.ExportOf(i.MimeType) is not null);
        Skip.If(document is null, "The shared drive has no Google document at its top level.");

        var storage = new DriveStorage(api, driveId);
        var name = document!.Name + DriveMimeTypes.ExportOf(document.MimeType)!.Value.Extension;
        var entry = await storage.StatAsync(name, Ct);
        Skip.If(entry is null, $"'{name}' is not shown (a real file has that name, or the name is no valid path segment).");

        entry!.Size.Should().Be(0);
        try
        {
            (await ReadAsync(storage, name)).Should().NotBeEmpty();
        }
        catch (StorageIOException ex) when (ex.NotReadable)
        {
            // Over Google's export limit: skipped by a backup, as designed.
        }
    }
}
