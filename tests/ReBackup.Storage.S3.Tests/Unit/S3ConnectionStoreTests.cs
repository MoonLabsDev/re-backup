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

    private const string ValidEntry = """{ "id": "a", "name": "A", "region": "eu-west-1", "bucket": "bkt", "accessKeyId": "AKIAX" }""";

    /// <summary>Writes <paramref name="json"/>, then asserts that loading, saving and deleting all throw and leave the file as it was.</summary>
    private JsonException AssertRejected(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, json);
        var store = new S3ConnectionStore(FilePath);

        var thrown = store.Invoking(s => s.LoadAll()).Should().Throw<JsonException>().Which;
        store.Invoking(s => s.TryGet("a")).Should().Throw<JsonException>();
        store.Invoking(s => s.Save(Connection("b"))).Should().Throw<JsonException>();
        store.Invoking(s => s.Delete("a")).Should().Throw<JsonException>();

        File.ReadAllText(FilePath).Should().Be(json);
        return thrown;
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("region")]
    [InlineData("bucket")]
    [InlineData("accessKeyId")]
    public void Entry_with_a_missing_or_empty_required_field_is_corrupt(string field)
    {
        var missing = ValidEntry.Replace($"\"{field}\": ", $"\"x{field}\": ");
        var empty = System.Text.RegularExpressions.Regex.Replace(ValidEntry, $"\"{field}\": \"[^\"]*\"", $"\"{field}\": \"\"");
        var nul = System.Text.RegularExpressions.Regex.Replace(ValidEntry, $"\"{field}\": \"[^\"]*\"", $"\"{field}\": null");

        foreach (var entry in new[] { missing, empty, nul })
            AssertRejected($$"""{ "formatVersion": 1, "connections": [ {{entry}} ] }""");
    }

    [Fact]
    public void Duplicate_ids_are_corrupt()
    {
        var other = ValidEntry.Replace("\"a\"", "\"A\"").Replace("\"name\": \"A\"", "\"name\": \"Other\"");

        AssertRejected($$"""{ "formatVersion": 1, "connections": [ {{ValidEntry}}, {{other}} ] }""");
    }

    [Theory]
    [InlineData("""{ "connections": [] }""")]
    [InlineData("""{ "formatVersion": 0, "connections": [] }""")]
    public void Missing_format_version_is_corrupt(string json) => AssertRejected(json);

    [Fact]
    public void Newer_format_version_is_rejected_and_never_overwritten()
    {
        var thrown = AssertRejected($$"""{ "formatVersion": 2, "connections": [ {{ValidEntry}} ] }""");

        thrown.Message.Should().Contain("newer version");
    }

    [Fact]
    public void Null_connections_are_corrupt() => AssertRejected("""{ "formatVersion": 1, "connections": null }""");

    public static TheoryData<S3Connection> InvalidConnections => new()
    {
        Connection("a") with { Id = "" },
        Connection("a") with { Id = " " },
        Connection("a") with { Name = "" },
        Connection("a") with { Region = "" },
        Connection("a") with { Region = "eu-central" },
        Connection("a") with { Region = "EU-CENTRAL-1" },
        Connection("a") with { Bucket = " " },
        Connection("a") with { AccessKeyId = "" },
    };

    [Theory]
    [MemberData(nameof(InvalidConnections))]
    public void Save_rejects_a_connection_that_load_would_reject_and_leaves_the_file(S3Connection invalid)
    {
        var store = new S3ConnectionStore(FilePath);
        store.Save(Connection("keep"));
        var before = File.ReadAllText(FilePath);

        store.Invoking(s => s.Save(invalid)).Should().Throw<ArgumentException>();

        File.ReadAllText(FilePath).Should().Be(before);
        store.LoadAll().Select(c => c.Id).Should().Equal("keep");
    }

    [Fact]
    public void Save_of_an_invalid_connection_creates_no_file()
    {
        var store = new S3ConnectionStore(FilePath);

        store.Invoking(s => s.Save(Connection("a") with { Name = "" })).Should().Throw<ArgumentException>();

        File.Exists(FilePath).Should().BeFalse();
    }

    [Fact]
    public void A_null_entry_is_corrupt() => AssertRejected("""{ "formatVersion": 1, "connections": [ null ] }""");

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
