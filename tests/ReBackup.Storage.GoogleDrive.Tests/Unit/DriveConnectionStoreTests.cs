using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReBackup.Storage.GoogleDrive.Connections;

namespace ReBackup.Storage.GoogleDrive.Tests.Unit;

public sealed class DriveConnectionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rebackup-drive-store-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "drive-connections.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string Key(string email = "backup@proj.iam.gserviceaccount.com") =>
        "{ \"type\": \"service_account\", \"project_id\": \"proj\", \"private_key\": \"-----BEGIN PRIVATE KEY-----\\nSECRET-PRIVATE-KEY\\n-----END PRIVATE KEY-----\\n\", \"client_email\": \"" + email + "\" }";

    private static DriveConnectionInfo Connection(string id, string accountId = "acc", string? sharedDriveId = "drive1") =>
        new(id, "Name " + id, accountId, "root-" + id, "Root " + id, sharedDriveId);

    private DriveConnectionStore StoreWithAccount()
    {
        var store = new DriveConnectionStore(FilePath);
        store.SaveAccount("acc", "Account acc", Key());
        return store;
    }

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, json);
    }

    private const string GoodAccount = """{ "id": "acc", "name": "A", "clientEmail": "a@p.iam.gserviceaccount.com", "protectedKey": null }""";
    private const string GoodConnection = """{ "id": "c1", "name": "C", "accountId": "acc", "rootId": "r", "rootName": "R", "sharedDriveId": null }""";

    private static string File1(string accounts = GoodAccount, string connections = GoodConnection, int version = 1) =>
        "{ \"formatVersion\": " + version + ", \"accounts\": [" + accounts + "], \"connections\": [" + connections + "] }";

    // ---- round trip ----

    [Fact]
    public void Store_round_trips_accounts_and_connections_and_never_writes_the_plain_key()
    {
        var store = StoreWithAccount();
        store.Save(Connection("c1"));
        store.Save(Connection("c2", sharedDriveId: null));

        var text = File.ReadAllText(FilePath);
        text.Should().NotContain("SECRET-PRIVATE-KEY");
        var root = JsonNode.Parse(text)!.AsObject();
        root.Select(p => p.Key).Should().BeEquivalentTo("formatVersion", "accounts", "connections");
        root["formatVersion"]!.GetValue<int>().Should().Be(1);
        root["accounts"]![0]!["clientEmail"]!.GetValue<string>().Should().Be("backup@proj.iam.gserviceaccount.com");
        root["accounts"]![0]!["protectedKey"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        root["connections"]![0]!["accountId"]!.GetValue<string>().Should().Be("acc");

        var reloaded = new DriveConnectionStore(FilePath);
        var account = reloaded.LoadAccounts().Should().ContainSingle().Subject;
        account.Id.Should().Be("acc");
        account.Name.Should().Be("Account acc");
        account.ClientEmail.Should().Be("backup@proj.iam.gserviceaccount.com");
        account.NeedsKey.Should().BeFalse();
        reloaded.TryGetAccount("ACC").Should().Be(account);
        reloaded.TryGetAccount("nope").Should().BeNull();
        reloaded.LoadAll().Should().Equal(Connection("c1"), Connection("c2", sharedDriveId: null));
        reloaded.TryGet("C1").Should().Be(Connection("c1"));
        reloaded.TryGet("nope").Should().BeNull();
    }

    [Fact]
    public void A_missing_file_is_empty()
    {
        var store = new DriveConnectionStore(FilePath);

        store.LoadAccounts().Should().BeEmpty();
        store.LoadAll().Should().BeEmpty();
        store.TryResolve("x").Should().BeNull();
        File.Exists(FilePath).Should().BeFalse();
    }

    [Fact]
    public void TryResolve_combines_the_connection_with_the_account_key()
    {
        var store = StoreWithAccount();
        store.Save(Connection("c1"));

        var resolved = store.TryResolve("C1")!;

        resolved.Info.Should().Be(Connection("c1"));
        resolved.ClientEmail.Should().Be("backup@proj.iam.gserviceaccount.com");
        resolved.KeyJson.Should().Be(Key());
        store.TryResolve("other").Should().BeNull();
    }

    [Fact]
    public void Saving_an_account_without_key_keeps_the_stored_key_and_a_new_key_replaces_it()
    {
        var store = StoreWithAccount();
        store.Save(Connection("c1"));

        store.SaveAccount("acc", "Renamed", null);
        store.TryGetAccount("acc")!.Name.Should().Be("Renamed");
        store.TryResolve("c1")!.KeyJson.Should().Be(Key());

        store.SaveAccount("acc", "Renamed", Key("other@proj.iam.gserviceaccount.com"));
        store.TryGetAccount("acc")!.ClientEmail.Should().Be("other@proj.iam.gserviceaccount.com");
        store.TryResolve("c1")!.KeyJson.Should().Be(Key("other@proj.iam.gserviceaccount.com"));
    }

    [Fact]
    public void Save_replaces_a_connection_with_the_same_id()
    {
        var store = StoreWithAccount();
        store.Save(Connection("c1"));

        store.Save(Connection("C1") with { Name = "Other", RootName = "New root" });

        store.LoadAll().Should().ContainSingle().Which.Should().Be(Connection("C1") with { Name = "Other", RootName = "New root" });
    }

    [Fact]
    public void Delete_removes_a_connection_and_ignores_a_missing_one()
    {
        var store = StoreWithAccount();
        store.Save(Connection("c1"));

        store.Delete("nope");
        store.Delete("C1");

        store.LoadAll().Should().BeEmpty();
    }

    // ---- key validation ----

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{ "type": "authorized_user", "client_email": "a@b.c", "private_key": "x" }""")]
    [InlineData("""{ "type": "service_account", "private_key": "x" }""")]
    [InlineData("""{ "type": "service_account", "client_email": "  ", "private_key": "x" }""")]
    [InlineData("""{ "type": "service_account", "client_email": "a@b.c" }""")]
    [InlineData("""{ "type": "service_account", "client_email": 5, "private_key": "x" }""")]
    public void A_key_that_is_not_a_service_account_key_is_rejected_and_nothing_is_written(string keyJson)
    {
        var store = new DriveConnectionStore(FilePath);

        var act = () => store.SaveAccount("acc", "A", keyJson);

        act.Should().Throw<ArgumentException>().WithMessage("*service account*");
        File.Exists(FilePath).Should().BeFalse();
    }

    [Fact]
    public void The_rejection_message_does_not_contain_the_key()
    {
        var store = new DriveConnectionStore(FilePath);

        var act = () => store.SaveAccount("acc", "A", """{ "type": "authorized_user", "private_key": "SECRET-PRIVATE-KEY" }""");

        act.Should().Throw<ArgumentException>().Which.Message.Should().NotContain("SECRET-PRIVATE-KEY");
    }

    [Fact]
    public void A_new_account_needs_a_key_and_names_must_be_unique()
    {
        var store = StoreWithAccount();

        var noKey = () => store.SaveAccount("new", "New", null);
        var dupName = () => store.SaveAccount("new", "ACCOUNT ACC", Key());
        var blank = () => store.SaveAccount("new", " ", Key());

        noKey.Should().Throw<ArgumentException>();
        dupName.Should().Throw<ArgumentException>();
        blank.Should().Throw<ArgumentException>();
        store.LoadAccounts().Should().ContainSingle();
    }

    [Fact]
    public void Save_rejects_what_loading_would_reject()
    {
        var store = StoreWithAccount();
        store.Save(Connection("c1"));

        store.Invoking(s => s.Save(Connection("c2", accountId: "missing"))).Should().Throw<ArgumentException>();
        store.Invoking(s => s.Save(Connection("c2") with { Name = "NAME C1" })).Should().Throw<ArgumentException>();
        store.Invoking(s => s.Save(Connection("c2") with { RootId = " " })).Should().Throw<ArgumentException>();
        store.Invoking(s => s.Save(Connection("c2") with { RootName = "" })).Should().Throw<ArgumentException>();
        store.LoadAll().Should().ContainSingle();
    }

    // ---- other Windows account ----

    [Fact]
    public void A_key_that_cannot_be_decrypted_is_kept_flagged_and_resolving_says_to_enter_it_again()
    {
        WriteFile(File1(accounts: """{ "id": "acc", "name": "A", "clientEmail": "a@p.iam.gserviceaccount.com", "protectedKey": "AAAAbm90LWRwYXBp" }"""));
        var store = new DriveConnectionStore(FilePath);

        store.LoadAccounts().Should().ContainSingle().Which.NeedsKey.Should().BeTrue();
        var act = () => store.TryResolve("c1");

        act.Should().Throw<StorageIOException>().WithMessage("*enter the key again*").And.Path.Should().Be("c1");

        // The account survives a later save; entering the key again fixes it.
        store.SaveAccount("acc", "A renamed", null);
        store.TryGetAccount("acc")!.NeedsKey.Should().BeTrue();
        store.SaveAccount("acc", "A renamed", Key());
        store.TryGetAccount("acc")!.NeedsKey.Should().BeFalse();
        store.TryResolve("c1")!.KeyJson.Should().Be(Key());
    }

    // ---- delete refused while used ----

    [Fact]
    public void Deleting_an_account_that_a_connection_uses_is_refused_naming_the_connections()
    {
        var store = StoreWithAccount();
        store.Save(Connection("c1"));
        store.Save(Connection("c2"));
        var before = File.ReadAllText(FilePath);

        var act = () => store.DeleteAccount("ACC");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Name c1*Name c2*");
        File.ReadAllText(FilePath).Should().Be(before);
        store.ConnectionsUsing("ACC").Select(c => c.Id).Should().Equal("c1", "c2");
    }

    [Fact]
    public void An_unused_account_can_be_deleted_and_a_missing_one_is_ignored()
    {
        var store = StoreWithAccount();
        store.Save(Connection("c1"));
        store.Delete("c1");

        store.DeleteAccount("nope");
        store.DeleteAccount("acc");

        store.LoadAccounts().Should().BeEmpty();
        store.ConnectionsUsing("acc").Should().BeEmpty();
    }

    // ---- corrupt / newer ----

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{ "accounts": [], "connections": [] }""")]
    [InlineData("""{ "formatVersion": 1, "connections": [] }""")]
    [InlineData("""{ "formatVersion": 1, "accounts": [] }""")]
    [InlineData("""{ "formatVersion": 1, "accounts": [null], "connections": [] }""")]
    [InlineData("""{ "formatVersion": 1, "accounts": [{ "id": "a", "name": "A" }], "connections": [] }""")]
    public void A_corrupt_file_throws_JsonException_from_every_member_and_is_not_overwritten(string content)
    {
        WriteFile(content);
        var store = new DriveConnectionStore(FilePath);

        store.Invoking(s => s.LoadAccounts()).Should().Throw<JsonException>();
        store.Invoking(s => s.TryGetAccount("a")).Should().Throw<JsonException>();
        store.Invoking(s => s.LoadAll()).Should().Throw<JsonException>();
        store.Invoking(s => s.TryGet("a")).Should().Throw<JsonException>();
        store.Invoking(s => s.ConnectionsUsing("a")).Should().Throw<JsonException>();
        store.Invoking(s => s.SaveAccount("n", "N", Key())).Should().Throw<JsonException>();
        store.Invoking(s => s.Save(Connection("c"))).Should().Throw<JsonException>();
        store.Invoking(s => s.Delete("a")).Should().Throw<JsonException>();
        store.Invoking(s => s.DeleteAccount("a")).Should().Throw<JsonException>();
        File.ReadAllText(FilePath).Should().Be(content);
    }

    [Theory]
    [InlineData("""{ "id": "c1", "name": "C", "accountId": "acc", "rootId": "r" }""")]
    [InlineData("""{ "id": "c1", "name": "C", "accountId": "ghost", "rootId": "r", "rootName": "R" }""")]
    public void A_connection_without_required_field_or_account_is_corrupt(string connection)
    {
        WriteFile(File1(connections: connection));

        new DriveConnectionStore(FilePath).Invoking(s => s.LoadAll()).Should().Throw<JsonException>().WithMessage("*corrupt*");
    }

    [Fact]
    public void Duplicate_ids_and_names_are_corrupt()
    {
        var dupAccountId = File1(accounts: GoodAccount + "," + GoodAccount.Replace("\"A\"", "\"B\""));
        var dupAccountName = File1(accounts: GoodAccount + "," + GoodAccount.Replace("\"acc\"", "\"acc2\"").Replace("\"A\"", "\"a\""));
        var dupConnectionId = File1(connections: GoodConnection + "," + GoodConnection.Replace("\"C\"", "\"D\""));
        var dupConnectionName = File1(connections: GoodConnection + "," + GoodConnection.Replace("\"c1\"", "\"c2\""));

        foreach (var content in new[] { dupAccountId, dupAccountName, dupConnectionId, dupConnectionName })
        {
            WriteFile(content);
            new DriveConnectionStore(FilePath).Invoking(s => s.LoadAll()).Should().Throw<JsonException>().WithMessage("*corrupt*");
        }
    }

    [Fact]
    public void A_newer_format_is_refused_with_its_version_and_left_unchanged()
    {
        var content = File1(version: 2);
        WriteFile(content);
        var store = new DriveConnectionStore(FilePath);

        store.Invoking(s => s.LoadAll()).Should().Throw<JsonException>().WithMessage("*newer version (format 2)*");
        store.Invoking(s => s.Save(Connection("x"))).Should().Throw<JsonException>();
        File.ReadAllText(FilePath).Should().Be(content);
    }

    [Fact]
    public void TryResolve_reports_a_corrupt_or_newer_file_as_StorageIOException_with_the_reason()
    {
        var store = new DriveConnectionStore(FilePath);

        WriteFile("not json");
        store.Invoking(s => s.TryResolve("c1")).Should().Throw<StorageIOException>().WithMessage("*onnections file*");

        WriteFile(File1(version: 7));
        store.Invoking(s => s.TryResolve("c1")).Should().Throw<StorageIOException>().WithMessage("*newer version (format 7)*");
    }
}
