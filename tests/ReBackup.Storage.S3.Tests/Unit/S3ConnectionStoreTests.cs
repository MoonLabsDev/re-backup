using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

    private static S3Account Account(string id, string? secret = "s3cr3t-value") => new(id, "Account " + id, "AKIA" + id.ToUpperInvariant(), secret);

    private static S3ConnectionInfo Connection(string id, string accountId = "acc") =>
        new(id, "Name " + id, "eu-central-1", "my-bucket", accountId);

    /// <summary>A store with account "acc" saved.</summary>
    private S3ConnectionStore StoreWithAccount()
    {
        var store = new S3ConnectionStore(FilePath);
        store.SaveAccount(Account("acc"));
        return store;
    }

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, json);
    }

    // ---- v2 round trip ----

    [Fact]
    public void Store_round_trips_accounts_and_connections_and_never_writes_the_plain_secret()
    {
        var store = StoreWithAccount();
        store.Save(Connection("c1"));

        var text = File.ReadAllText(FilePath);
        text.Should().NotContain("s3cr3t-value");
        var root = JsonNode.Parse(text)!.AsObject();
        root.Select(p => p.Key).Should().BeEquivalentTo("formatVersion", "accounts", "connections");
        root["formatVersion"]!.GetValue<int>().Should().Be(2);
        root["accounts"]![0]!["secretProtected"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        root["connections"]![0]!["accountId"]!.GetValue<string>().Should().Be("acc");

        var reloaded = new S3ConnectionStore(FilePath);
        reloaded.LoadAccounts().Should().Equal(Account("acc"));
        reloaded.TryGetAccount("ACC").Should().Be(Account("acc"));
        reloaded.LoadAll().Should().Equal(Connection("c1"));
        reloaded.TryGet("C1").Should().Be(Connection("c1"));
    }

    [Fact]
    public void TryResolve_combines_the_connection_and_its_account()
    {
        var store = StoreWithAccount();
        store.Save(Connection("c1"));

        store.TryResolve("C1").Should().Be(new S3Connection("c1", "Name c1", "eu-central-1", "my-bucket", "AKIAACC", "s3cr3t-value"));
        store.TryResolve("other").Should().BeNull();
    }

    [Fact]
    public void Saving_an_account_with_null_secret_keeps_the_stored_secret()
    {
        var store = StoreWithAccount();

        store.SaveAccount(Account("acc", secret: null) with { Name = "Renamed" });

        var loaded = store.TryGetAccount("acc")!;
        loaded.Name.Should().Be("Renamed");
        loaded.Secret.Should().Be("s3cr3t-value");
    }

    [Fact]
    public void A_new_account_without_secret_is_rejected()
    {
        var store = new S3ConnectionStore(FilePath);

        store.Invoking(s => s.SaveAccount(Account("a", secret: null))).Should().Throw<ArgumentException>();

        File.Exists(FilePath).Should().BeFalse();
    }

    [Fact]
    public void A_changed_account_secret_reaches_the_resolved_connection()
    {
        var store = StoreWithAccount();
        store.Save(Connection("c1"));

        store.SaveAccount(Account("acc", "rotated-secret"));

        store.TryResolve("c1")!.Secret.Should().Be("rotated-secret");
    }

    [Fact]
    public void Missing_file_is_empty()
    {
        var store = new S3ConnectionStore(FilePath);

        store.LoadAll().Should().BeEmpty();
        store.LoadAccounts().Should().BeEmpty();
        store.TryGet("x").Should().BeNull();
        store.TryGetAccount("x").Should().BeNull();
        store.TryResolve("x").Should().BeNull();
        store.ConnectionsUsing("x").Should().BeEmpty();
    }

    [Fact]
    public void Save_with_an_unknown_account_is_rejected_and_leaves_the_file()
    {
        var store = StoreWithAccount();
        var before = File.ReadAllText(FilePath);

        store.Invoking(s => s.Save(Connection("c1", accountId: "nope"))).Should().Throw<ArgumentException>();

        File.ReadAllText(FilePath).Should().Be(before);
    }

    [Fact]
    public void Delete_removes_only_that_connection()
    {
        var store = StoreWithAccount();
        store.Save(Connection("a"));
        store.Save(Connection("b"));

        store.Delete("A");

        store.LoadAll().Select(c => c.Id).Should().Equal("b");
        store.LoadAccounts().Should().ContainSingle();
    }

    [Fact]
    public void DeleteAccount_in_use_is_refused_with_the_connection_names_and_leaves_the_file()
    {
        var store = StoreWithAccount();
        store.Save(Connection("a"));
        store.Save(Connection("b"));
        var before = File.ReadAllText(FilePath);

        store.Invoking(s => s.DeleteAccount("ACC")).Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Name a").And.Contain("Name b");

        File.ReadAllText(FilePath).Should().Be(before);
        store.ConnectionsUsing("acc").Select(c => c.Id).Should().Equal("a", "b");
    }

    [Fact]
    public void DeleteAccount_unused_removes_it()
    {
        var store = StoreWithAccount();
        store.SaveAccount(Account("other"));

        store.DeleteAccount("acc");

        store.LoadAccounts().Select(a => a.Id).Should().Equal("other");
    }

    // ---- resolver ----

    [Fact]
    public void Resolver_takes_bucket_and_region_from_the_connection_and_the_key_from_the_account()
    {
        S3ConnectionResolver.Resolve(Connection("c1"), Account("acc"))
            .Should().Be(new S3Connection("c1", "Name c1", "eu-central-1", "my-bucket", "AKIAACC", "s3cr3t-value"));
    }

    [Fact]
    public void Resolver_without_account_is_null() => S3ConnectionResolver.Resolve(Connection("c1"), null).Should().BeNull();

    [Fact]
    public void Resolver_keeps_a_missing_secret_missing() =>
        S3ConnectionResolver.Resolve(Connection("c1"), Account("acc", secret: null))!.NeedsSecret.Should().BeTrue();

    [Fact]
    public void Account_ToString_does_not_contain_the_secret()
    {
        Account("a", "top-secret-42").ToString().Should().NotContain("top-secret-42").And.Contain("AKIAA");
        Account("a", null).NeedsSecret.Should().BeTrue();
    }

    // ---- migration from format 1 ----

    private static string V1(params string[] entries) => $$"""{ "formatVersion": 1, "connections": [ {{string.Join(", ", entries)}} ] }""";

    private static string V1Entry(string id, string name, string key, string? blob, string region = "eu-west-1", string bucket = "bkt") =>
        $$"""{ "id": "{{id}}", "name": "{{name}}", "region": "{{region}}", "bucket": "{{bucket}}", "accessKeyId": "{{key}}", "secretProtected": {{(blob is null ? "null" : "\"" + blob + "\"")}} }""";

    [Fact]
    public void Migration_with_same_key_keeps_first_blob_and_all_connections()
    {
        var blobX = SecretProtector.Protect("secret-x");
        var blobY = SecretProtector.Protect("secret-y");
        var json = V1(
            V1Entry("a", "Alpha", "AKIAK", blobX, bucket: "bucket-a"),
            V1Entry("b", "Beta", "AKIAK", blobY, bucket: "bucket-b", region: "us-east-1"),
            V1Entry("c", "Gamma", "AKIAL", "AAAA", bucket: "bucket-c"));
        WriteFile(json);
        var store = new S3ConnectionStore(FilePath);

        var accounts = store.LoadAccounts();
        var connections = store.LoadAll();

        accounts.Should().HaveCount(2);
        var shared = accounts.Single(a => a.AccessKeyId == "AKIAK");
        shared.Name.Should().Be("Alpha");
        shared.Secret.Should().Be("secret-x");
        accounts.Single(a => a.AccessKeyId == "AKIAL").NeedsSecret.Should().BeTrue();

        connections.Select(c => (c.Id, c.Name, c.Region, c.Bucket)).Should().Equal(
            ("a", "Alpha", "eu-west-1", "bucket-a"), ("b", "Beta", "us-east-1", "bucket-b"), ("c", "Gamma", "eu-west-1", "bucket-c"));
        connections[0].AccountId.Should().Be(shared.Id);
        connections[1].AccountId.Should().Be(shared.Id);
        store.TryResolve("b")!.Secret.Should().Be("secret-x");
        store.TryResolve("c")!.NeedsSecret.Should().BeTrue();
        new S3ConnectionStore(FilePath).TryGetAccount(shared.Id).Should().Be(shared, "account ids are stable across loads");

        File.ReadAllText(FilePath).Should().Be(json, "loading never rewrites the file");

        store.SaveAccount(shared with { Secret = null, Name = "Shared" });

        var root = JsonNode.Parse(File.ReadAllText(FilePath))!;
        root["formatVersion"]!.GetValue<int>().Should().Be(2);
        root["accounts"]!.AsArray().Select(a => a!["secretProtected"]!.GetValue<string>()).Should().Equal(blobX, "AAAA");
        var reloaded = new S3ConnectionStore(FilePath);
        reloaded.LoadAll().Should().Equal(connections);
        reloaded.TryResolve("a")!.Secret.Should().Be("secret-x");
        reloaded.TryResolve("c")!.NeedsSecret.Should().BeTrue();
    }

    [Fact]
    public void Migration_keeps_an_entry_without_a_secret_as_an_account_that_needs_one()
    {
        WriteFile(V1(V1Entry("a", "Alpha", "AKIAK", null)));
        var store = new S3ConnectionStore(FilePath);

        store.LoadAccounts().Should().ContainSingle().Which.NeedsSecret.Should().BeTrue();
        store.LoadAll().Should().ContainSingle();
    }

    [Fact]
    public void Migration_makes_duplicate_names_unique_instead_of_rejecting_the_file()
    {
        WriteFile(V1(V1Entry("a", "Same", "AKIAK", null), V1Entry("b", "same", "AKIAL", null)));
        var store = new S3ConnectionStore(FilePath);

        store.LoadAll().Select(c => c.Name.ToUpperInvariant()).Should().OnlyHaveUniqueItems().And.HaveCount(2);
        store.LoadAccounts().Select(a => a.Name.ToUpperInvariant()).Should().OnlyHaveUniqueItems().And.HaveCount(2);
    }

    [Fact]
    public void Saving_a_connection_on_a_v1_file_writes_v2_with_every_migrated_entry()
    {
        WriteFile(V1(V1Entry("a", "Alpha", "AKIAK", "AAAA")));
        var store = new S3ConnectionStore(FilePath);
        var accountId = store.LoadAccounts().Single().Id;

        store.Save(Connection("n", accountId));

        var reloaded = new S3ConnectionStore(FilePath);
        reloaded.LoadAll().Select(c => c.Id).Should().Equal("a", "n");
        File.ReadAllText(FilePath).Should().Contain("\"AAAA\"").And.Contain("\"formatVersion\": 2");
    }

    // ---- rejected files ----

    private const string ValidAccount = """{ "id": "acc", "name": "Acc", "accessKeyId": "AKIAX", "secretProtected": null }""";
    private const string ValidConnection = """{ "id": "a", "name": "A", "region": "eu-west-1", "bucket": "bkt", "accountId": "acc" }""";

    private static string V2(string accounts, string connections) =>
        $$"""{ "formatVersion": 2, "accounts": [ {{accounts}} ], "connections": [ {{connections}} ] }""";

    /// <summary>Writes <paramref name="json"/>, then asserts that every member throws and leaves the file as it was.</summary>
    private JsonException AssertRejected(string json)
    {
        WriteFile(json);
        var store = new S3ConnectionStore(FilePath);

        var thrown = store.Invoking(s => s.LoadAll()).Should().Throw<JsonException>().Which;
        store.Invoking(s => s.LoadAccounts()).Should().Throw<JsonException>();
        store.Invoking(s => s.TryGet("a")).Should().Throw<JsonException>();
        store.Invoking(s => s.TryGetAccount("acc")).Should().Throw<JsonException>();
        store.Invoking(s => s.TryResolve("a")).Should().Throw<JsonException>();
        store.Invoking(s => s.ConnectionsUsing("acc")).Should().Throw<JsonException>();
        store.Invoking(s => s.SaveAccount(Account("new"))).Should().Throw<JsonException>();
        store.Invoking(s => s.Save(Connection("b"))).Should().Throw<JsonException>();
        store.Invoking(s => s.Delete("a")).Should().Throw<JsonException>();
        store.Invoking(s => s.DeleteAccount("acc")).Should().Throw<JsonException>();

        File.ReadAllText(FilePath).Should().Be(json);
        return thrown;
    }

    [Fact]
    public void Corrupt_json_is_rejected() => AssertRejected("{ this is not json");

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("accessKeyId")]
    public void Account_with_a_missing_or_blank_field_is_corrupt(string field)
    {
        foreach (var account in Variants(ValidAccount, field))
            AssertRejected(V2(account, ValidConnection));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("region")]
    [InlineData("bucket")]
    [InlineData("accountId")]
    public void Connection_with_a_missing_or_blank_field_is_corrupt(string field)
    {
        foreach (var connection in Variants(ValidConnection, field))
            AssertRejected(V2(ValidAccount, connection));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("region")]
    [InlineData("bucket")]
    [InlineData("accessKeyId")]
    public void V1_entry_with_a_missing_or_blank_field_is_corrupt(string field)
    {
        const string entry = """{ "id": "a", "name": "A", "region": "eu-west-1", "bucket": "bkt", "accessKeyId": "AKIAX" }""";
        foreach (var variant in Variants(entry, field))
            AssertRejected(V1(variant));
    }

    /// <summary>The entry with <paramref name="field"/> missing, empty, blank and null.</summary>
    private static IEnumerable<string> Variants(string entry, string field)
    {
        yield return entry.Replace($"\"{field}\": ", $"\"x{field}\": ");
        foreach (var value in new[] { "\"\"", "\" \"", "null" })
            yield return Regex.Replace(entry, $"\"{field}\": (\"[^\"]*\"|null)", $"\"{field}\": {value}");
    }

    [Fact]
    public void Duplicate_account_ids_are_corrupt() =>
        AssertRejected(V2(ValidAccount + ", " + ValidAccount.Replace("\"acc\"", "\"ACC\"").Replace("\"Acc\"", "\"Other\""), ValidConnection));

    [Fact]
    public void Duplicate_account_names_are_corrupt() =>
        AssertRejected(V2(ValidAccount + ", " + ValidAccount.Replace("\"acc\"", "\"acc2\"").Replace("\"Acc\"", "\"ACC\""), ValidConnection));

    [Fact]
    public void Duplicate_connection_ids_are_corrupt() =>
        AssertRejected(V2(ValidAccount, ValidConnection + ", " + ValidConnection.Replace("\"a\"", "\"A\"").Replace("\"name\": \"A\"", "\"name\": \"Other\"")));

    [Fact]
    public void Duplicate_connection_names_are_corrupt() =>
        AssertRejected(V2(ValidAccount, ValidConnection + ", " + ValidConnection.Replace("\"id\": \"a\"", "\"id\": \"b\"").Replace("\"name\": \"A\"", "\"name\": \"a\"")));

    [Fact]
    public void V1_duplicate_ids_are_corrupt() =>
        AssertRejected(V1(V1Entry("a", "A", "AKIAX", null), V1Entry("A", "Other", "AKIAY", null)));

    [Fact]
    public void Unknown_account_id_is_corrupt() =>
        AssertRejected(V2(ValidAccount, ValidConnection.Replace("\"accountId\": \"acc\"", "\"accountId\": \"nope\"")));

    [Theory]
    [InlineData("eu-central")]
    [InlineData("EU-WEST-1")]
    public void Invalid_region_is_corrupt(string region) => AssertRejected(V2(ValidAccount, ValidConnection.Replace("eu-west-1", region)));

    [Fact]
    public void Invalid_region_in_a_v1_file_is_corrupt() => AssertRejected(V1(V1Entry("a", "A", "AKIAX", null, region: "eu-central")));

    [Theory]
    [InlineData("""{ "accounts": [], "connections": [] }""")]
    [InlineData("""{ "formatVersion": 0, "accounts": [], "connections": [] }""")]
    [InlineData("""{ "formatVersion": 2, "connections": [] }""")]
    [InlineData("""{ "formatVersion": 2, "accounts": [], "connections": null }""")]
    [InlineData("""{ "formatVersion": 2, "accounts": [ null ], "connections": [] }""")]
    [InlineData("""{ "formatVersion": 2, "accounts": [], "connections": [ null ] }""")]
    [InlineData("""{ "formatVersion": 1, "connections": null }""")]
    [InlineData("""{ "formatVersion": 1, "connections": [ null ] }""")]
    [InlineData("null")]
    public void Structurally_broken_files_are_corrupt(string json) => AssertRejected(json);

    [Fact]
    public void Newer_format_version_is_rejected_and_never_overwritten()
    {
        var thrown = AssertRejected($$"""{ "formatVersion": 3, "accounts": [ {{ValidAccount}} ], "connections": [ {{ValidConnection}} ] }""");

        thrown.Message.Should().Contain("newer version");
    }

    // ---- save validation ----

    public static TheoryData<S3ConnectionInfo> InvalidConnections => new()
    {
        Connection("x") with { Id = "" },
        Connection("x") with { Id = " " },
        Connection("x") with { Name = "" },
        Connection("x") with { Name = "NAME KEEP" },
        Connection("x") with { Region = "" },
        Connection("x") with { Region = "eu-central" },
        Connection("x") with { Region = "EU-CENTRAL-1" },
        Connection("x") with { Bucket = " " },
        Connection("x") with { AccountId = "" },
    };

    [Theory]
    [MemberData(nameof(InvalidConnections))]
    public void Save_rejects_a_connection_that_load_would_reject_and_leaves_the_file(S3ConnectionInfo invalid)
    {
        var store = StoreWithAccount();
        store.Save(Connection("keep"));
        var before = File.ReadAllText(FilePath);

        store.Invoking(s => s.Save(invalid)).Should().Throw<ArgumentException>();

        File.ReadAllText(FilePath).Should().Be(before);
        store.LoadAll().Select(c => c.Id).Should().Equal("keep");
    }

    [Fact]
    public void Renaming_a_connection_to_its_own_name_in_other_case_is_allowed()
    {
        var store = StoreWithAccount();
        store.Save(Connection("keep"));

        store.Save(Connection("keep") with { Name = "NAME KEEP" });

        store.TryGet("keep")!.Name.Should().Be("NAME KEEP");
    }

    public static TheoryData<S3Account> InvalidAccounts => new()
    {
        Account("x") with { Id = "" },
        Account("x") with { Name = " " },
        Account("x") with { Name = "account ACC" },
        Account("x") with { AccessKeyId = "" },
    };

    [Theory]
    [MemberData(nameof(InvalidAccounts))]
    public void SaveAccount_rejects_an_account_that_load_would_reject_and_leaves_the_file(S3Account invalid)
    {
        var store = StoreWithAccount();
        var before = File.ReadAllText(FilePath);

        store.Invoking(s => s.SaveAccount(invalid)).Should().Throw<ArgumentException>();

        File.ReadAllText(FilePath).Should().Be(before);
    }

    [Fact]
    public void Save_of_a_connection_without_account_creates_no_file()
    {
        var store = new S3ConnectionStore(FilePath);

        store.Invoking(s => s.Save(Connection("a"))).Should().Throw<ArgumentException>();

        File.Exists(FilePath).Should().BeFalse();
    }
}
