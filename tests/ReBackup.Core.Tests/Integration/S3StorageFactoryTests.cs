using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;
using ReBackup.Storage.S3.Tests.Integration;
using static ReBackup.Core.Tests.Integration.S3Integration;

namespace ReBackup.Core.Tests.Integration;

/// <summary>
/// The runner on storages composed as the App composes them: the real <see cref="S3StorageFactory"/> resolving a connection saved in
/// a <see cref="S3ConnectionStore"/> (account with an encrypted secret + connection), only its clients pointed at LocalStack.
/// </summary>
[Collection(S3ServerCollection.Name)]
public sealed class S3StorageFactoryTests(S3ServerFixture server) : IDisposable
{
    private const string ConnectionId = "offsite";
    private const string Version = "2026_10_06-09_15 Docs";

    private readonly TempDir _tmp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 9, 15, 0, TimeSpan.Zero));

    public void Dispose() => _tmp.Dispose();

    [SkippableFact]
    public async Task A_folder_goes_to_S3_and_back_through_the_real_factory_on_a_saved_connection()
    {
        Skip.IfNot(server.Available, server.SkipReason);
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var connection = server.CreateConnection(server.Bucket);
        var store = new S3ConnectionStore(_tmp.PathOf("connections.json"));
        store.SaveAccount(new S3Account("acc", "LocalStack", connection.AccessKeyId, connection.Secret));
        store.Save(new S3ConnectionInfo(ConnectionId, "Offsite", connection.Region, server.Bucket, "acc"));
        using var storages = new S3StorageFactory(new StorageFactory(), store.TryResolve, server.CreateClient);

        var source = _tmp.CreateDir("source");
        var files = new Dictionary<string, byte[]>
        {
            ["a.txt"] = "alpha"u8.ToArray(),
            ["sub/b.bin"] = RandomData(200_000, seed: 7),
        };
        foreach (var (path, data) in files)
        {
            var file = Path.Combine(source, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, data);
        }
        var prefix = NewPrefix("factory");
        var push = new BackupPlan
        {
            Id = "p-push",
            Name = "Docs",
            Source = StorageLocation.FileSystem(source),
            Target = new StorageLocation(S3StorageFactory.Kind, prefix, ConnectionId),
        };

        var pushed = await new BackupRunner(storages, _time).RunAsync(new BackupRequest(push, [], RunTrigger.Manual));

        pushed.Status.Should().Be(RunStatus.Completed, pushed.Reason);
        pushed.Version.Should().Be(Version);
        (await KeysAsync(server, prefix)).Should().Equal($"{Version}/a.txt", $"{Version}/re-manifest.json", $"{Version}/sub/b.bin");
        (await VersionCatalog.ListAsync(storages.Open(push.Target), push.Id, push.Name)).Select(v => v.Name)
            .Should().Equal(Version);

        // The version on S3 as the source of another plan, through the same factory and connection.
        var target = _tmp.CreateDir("target");
        var pull = new BackupPlan
        {
            Id = "p-pull",
            Name = "Pull",
            Source = new StorageLocation(S3StorageFactory.Kind, $"{prefix}/{Version}", ConnectionId),
            Target = StorageLocation.FileSystem(target),
            Ignore = { Patterns = ["re-manifest.json"] },
        };

        var pulled = await new BackupRunner(storages, _time).RunAsync(new BackupRequest(pull, [], RunTrigger.Manual));

        pulled.Status.Should().Be(RunStatus.Completed, pulled.Reason);
        var root = Path.Combine(target, pulled.Version!);
        foreach (var (path, data) in files)
            File.ReadAllBytes(Path.Combine(root, path)).SequenceEqual(data).Should().BeTrue(path);

        // A connection id the store does not know is not found, as in the App.
        storages.Invoking(f => f.Open(new StorageLocation(S3StorageFactory.Kind, prefix, "gone")))
            .Should().Throw<StorageNotFoundException>();
    }
}
