using FluentAssertions;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Storage.S3.Tests.Unit;

public class S3StorageFactoryTests
{
    private sealed class RecordingFactory : IStorageFactory
    {
        public List<StorageLocation> Opened { get; } = [];

        public IStorage Open(StorageLocation location)
        {
            Opened.Add(location);
            return null!;
        }
    }

    private static S3Connection Connection(string? secret = "secret", string region = "eu-central-1") =>
        new("c1", "name", region, "bucket", "AKIA", secret);

    private static S3StorageFactory Factory(IStorageFactory inner, S3Connection? connection) =>
        new(inner, id => id == "c1" ? connection : null);

    [Fact]
    public void S3_with_a_known_connection_opens_an_S3Storage()
    {
        using var factory = Factory(new RecordingFactory(), Connection());
        factory.Open(new StorageLocation("s3", "backups", "c1")).Should().BeOfType<S3Storage>();
    }

    [Fact]
    public void Unknown_connection_id_is_not_found()
    {
        using var factory = Factory(new RecordingFactory(), Connection());
        var act = () => factory.Open(new StorageLocation("s3", "backups", "other"));
        act.Should().Throw<StorageNotFoundException>();
    }

    [Fact]
    public void Missing_connection_id_is_not_found()
    {
        using var factory = Factory(new RecordingFactory(), Connection());
        var act = () => factory.Open(new StorageLocation("s3", "backups"));
        act.Should().Throw<StorageNotFoundException>();
    }

    [Fact]
    public void Connection_without_secret_is_access_denied_and_says_to_re_enter_it()
    {
        using var factory = Factory(new RecordingFactory(), Connection(secret: null));
        var act = () => factory.Open(new StorageLocation("s3", "backups", "c1"));
        act.Should().Throw<StorageAccessDeniedException>().WithMessage("*re-entered*");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not a region!")]
    [InlineData("eu-central-1\n")]
    [InlineData("EU-CENTRAL-1")]
    public void An_invalid_region_is_a_storage_error(string region)
    {
        using var factory = Factory(new RecordingFactory(), Connection(region: region));
        var act = () => factory.Open(new StorageLocation("s3", "backups", "c1"));
        act.Should().Throw<StorageIOException>().WithMessage("*region*invalid*");
        factory.CachedClientCount.Should().Be(0);
    }

    [Theory]
    [InlineData("us-east-1")]
    [InlineData("us-gov-west-1")]
    [InlineData("ap-southeast-2")]
    [InlineData("cn-northwest-1")]
    public void Real_region_names_are_accepted(string region)
    {
        using var factory = Factory(new RecordingFactory(), Connection(region: region));
        factory.Open(new StorageLocation("s3", "backups", "c1")).Should().BeOfType<S3Storage>();
    }

    [Fact]
    public void Other_kinds_go_to_the_inner_factory()
    {
        var inner = new RecordingFactory();
        using var factory = Factory(inner, Connection());
        var location = StorageLocation.FileSystem(@"C:\data");

        factory.Open(location);

        inner.Opened.Should().Equal(location);
    }

    [Fact]
    public void One_client_per_connection_is_cached()
    {
        using var factory = Factory(new RecordingFactory(), Connection());
        factory.Open(new StorageLocation("s3", "a", "c1"));
        factory.Open(new StorageLocation("s3", "b", "c1"));
        factory.CachedClientCount.Should().Be(1);
    }

    [Fact]
    public void An_edited_connection_gets_a_new_client()
    {
        var current = Connection();
        using var factory = new S3StorageFactory(new RecordingFactory(), _ => current);
        var location = new StorageLocation("s3", "a", "c1");

        factory.Open(location);
        current = Connection("changed");
        factory.Open(location);
        current = Connection(region: "us-east-1");
        factory.Open(location);
        current = current with { AccessKeyId = "AKIB" };
        factory.Open(location);

        factory.CachedClientCount.Should().Be(4);
    }

    [Fact]
    public void Concurrent_opens_share_one_client()
    {
        using var factory = Factory(new RecordingFactory(), Connection());
        Parallel.For(0, 50, _ => factory.Open(new StorageLocation("s3", "a", "c1")));
        factory.CachedClientCount.Should().Be(1);
    }

    /// <summary>A store in a temp folder with account "acc" (secret <paramref name="secret"/>) and connection "c1" on it.</summary>
    private static (S3ConnectionStore Store, string Dir) StoreWithConnection(string secret = "secret-1")
    {
        var dir = Path.Combine(Path.GetTempPath(), "rebackup-s3-factory-" + Guid.NewGuid().ToString("N"));
        var store = new S3ConnectionStore(Path.Combine(dir, "connections.json"));
        store.SaveAccount(new S3Account("acc", "Account", "AKIA", secret));
        store.Save(new S3ConnectionInfo("c1", "Conn", "eu-central-1", "bucket", "acc"));
        return (store, dir);
    }

    [Fact]
    public void Store_TryResolve_opens_a_saved_connection()
    {
        var (store, dir) = StoreWithConnection();
        try
        {
            using var factory = new S3StorageFactory(new RecordingFactory(), store.TryResolve);
            factory.Open(new StorageLocation("s3", "a", "c1")).Should().BeOfType<S3Storage>();
            factory.Invoking(f => f.Open(new StorageLocation("s3", "a", "other"))).Should().Throw<StorageNotFoundException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_account_is_not_found()
    {
        var info = new S3ConnectionInfo("c1", "Conn", "eu-central-1", "bucket", "gone");
        using var factory = new S3StorageFactory(new RecordingFactory(), id => id == "c1" ? S3ConnectionResolver.Resolve(info, null) : null);

        factory.Invoking(f => f.Open(new StorageLocation("s3", "a", "c1"))).Should().Throw<StorageNotFoundException>().WithMessage("*not found*");
    }

    [Fact]
    public void Account_whose_secret_cannot_be_decrypted_is_access_denied()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rebackup-s3-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "connections.json");
        File.WriteAllText(path, """
            { "formatVersion": 2,
              "accounts": [ { "id": "acc", "name": "Account", "accessKeyId": "AKIA", "secretProtected": "AAAA" } ],
              "connections": [ { "id": "c1", "name": "Conn", "region": "eu-central-1", "bucket": "bucket", "accountId": "acc" } ] }
            """);
        try
        {
            using var factory = new S3StorageFactory(new RecordingFactory(), new S3ConnectionStore(path).TryResolve);
            factory.Invoking(f => f.Open(new StorageLocation("s3", "a", "c1"))).Should().Throw<StorageAccessDeniedException>().WithMessage("*re-entered*");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Changed_account_secret_gives_a_new_client()
    {
        var (store, dir) = StoreWithConnection("secret-1");
        try
        {
            using var factory = new S3StorageFactory(new RecordingFactory(), store.TryResolve);
            var location = new StorageLocation("s3", "a", "c1");
            var before = (S3Storage)factory.Open(location);

            store.SaveAccount(new S3Account("acc", "Account", "AKIA", "secret-2"));
            var after = (S3Storage)factory.Open(location);

            factory.CachedClientCount.Should().Be(2);
            ClientOf(after).Should().NotBeSameAs(ClientOf(before));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static object? ClientOf(S3Storage storage) =>
        typeof(S3Storage).GetField("_client", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(storage);

    [Fact]
    public void Dispose_clears_the_cache()
    {
        var factory = Factory(new RecordingFactory(), Connection());
        factory.Open(new StorageLocation("s3", "a", "c1"));
        factory.Dispose();
        factory.CachedClientCount.Should().Be(0);
    }
}
