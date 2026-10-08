using System.Text.Json;
using FluentAssertions;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Storage.S3.Tests.Unit;

public sealed class S3ConnectionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rebackup-s3-store-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "connections.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static S3Connection Connection(string id, string? secret = "s3cr3t-value") =>
        new(id, "Name " + id, "eu-central-1", "my-bucket", "AKIA" + id, secret);

    [Fact]
    public void Store_round_trips_a_connection_and_never_writes_the_plain_secret()
    {
        var store = new S3ConnectionStore(FilePath);
        var connection = Connection("a");

        store.Save(connection);

        File.ReadAllText(FilePath).Should().NotContain("s3cr3t-value");
        store.LoadAll().Should().Equal(connection);
        new S3ConnectionStore(FilePath).TryGet("A").Should().Be(connection);
    }

    [Fact]
    public void Saving_with_null_secret_keeps_the_stored_secret()
    {
        var store = new S3ConnectionStore(FilePath);
        store.Save(Connection("a"));

        store.Save(Connection("a", secret: null) with { Name = "Renamed" });

        var loaded = store.TryGet("a");
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Renamed");
        loaded.Secret.Should().Be("s3cr3t-value");
    }

    [Fact]
    public void Undecryptable_secret_loads_as_needs_secret_and_stays_in_the_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """
            { "formatVersion": 1, "connections": [
              { "id": "broken", "name": "Broken", "region": "eu-west-1", "bucket": "bkt",
                "accessKeyId": "AKIAX", "secretProtected": "AAAA" } ] }
            """);
        var store = new S3ConnectionStore(FilePath);

        var loaded = store.LoadAll();
        loaded.Should().ContainSingle().Which.NeedsSecret.Should().BeTrue();

        store.Save(Connection("other"));

        File.ReadAllText(FilePath).Should().Contain("\"broken\"").And.Contain("\"AAAA\"");
        store.LoadAll().Should().HaveCount(2);
        store.TryGet("broken")!.NeedsSecret.Should().BeTrue();
    }

    [Fact]
    public void Missing_file_is_empty()
    {
        var store = new S3ConnectionStore(FilePath);

        store.LoadAll().Should().BeEmpty();
        store.TryGet("x").Should().BeNull();
    }

    [Fact]
    public void Corrupt_file_throws_json_exception_and_is_not_overwritten()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ this is not json");
        var store = new S3ConnectionStore(FilePath);

        store.Invoking(s => s.LoadAll()).Should().Throw<JsonException>();
        store.Invoking(s => s.Save(Connection("a"))).Should().Throw<JsonException>();
        store.Invoking(s => s.Delete("a")).Should().Throw<JsonException>();

        File.ReadAllText(FilePath).Should().Be("{ this is not json");
    }

    [Fact]
    public void Delete_removes_only_that_connection()
    {
        var store = new S3ConnectionStore(FilePath);
        store.Save(Connection("a"));
        store.Save(Connection("b"));

        store.Delete("A");

        store.LoadAll().Select(c => c.Id).Should().Equal("b");
    }

    [Fact]
    public void ToString_does_not_contain_the_secret()
    {
        Connection("a", "top-secret-42").ToString().Should().NotContain("top-secret-42");
    }
}
