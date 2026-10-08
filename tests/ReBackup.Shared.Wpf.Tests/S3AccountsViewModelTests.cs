using System.IO;
using FluentAssertions;
using ReBackup.Shared.Wpf.S3;
using ReBackup.Shared.Wpf.Tests.Fakes;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Shared.Wpf.Tests;

public sealed class S3AccountsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rebackup-wpf-tests", Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private readonly S3ConnectionStore _store;
    private readonly FakeAccountDialogs _dialogs = new();

    public S3AccountsViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "connections.json");
        _store = new S3ConnectionStore(_file);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private S3AccountsViewModel Create() => new(_store, _dialogs);

    private void Seed()
    {
        _store.SaveAccount(new S3Account("a", "Main", "AKIA1", "s1"));
        _store.SaveAccount(new S3Account("b", "Spare", "AKIA2", "s2"));
        _store.Save(new S3ConnectionInfo("c1", "Backups", "eu-central-1", "bucket-one", "a"));
        _store.Save(new S3ConnectionInfo("c2", "Photos", "eu-west-1", "bucket-two", "a"));
    }

    [Fact]
    public void The_list_shows_every_account_with_the_number_of_connections_using_it()
    {
        Seed();

        var vm = Create();

        vm.Accounts.Select(row => (row.Account.Name, row.Account.AccessKeyId, row.UsedBy, row.NeedsSecret))
            .Should().Equal(("Main", "AKIA1", 2, false), ("Spare", "AKIA2", 0, false));
        vm.IsEmpty.Should().BeFalse();
        vm.LoadFailed.Should().BeFalse();
    }

    [Fact]
    public void An_empty_store_gives_an_empty_list()
    {
        var vm = Create();

        vm.Accounts.Should().BeEmpty();
        vm.IsEmpty.Should().BeTrue();
        vm.AddCommand.CanExecute(null).Should().BeTrue();
        vm.EditCommand.CanExecute(null).Should().BeFalse();
        vm.DeleteCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void A_file_the_store_rejects_disables_the_list_and_stays_unchanged()
    {
        File.WriteAllText(_file, "{ not json");

        var vm = Create();

        vm.LoadFailed.Should().BeTrue();
        vm.Accounts.Should().BeEmpty();
        vm.AddCommand.CanExecute(null).Should().BeFalse();
        File.ReadAllText(_file).Should().Be("{ not json");
    }

    [Fact]
    public void Add_saves_the_new_account_and_selects_it()
    {
        Seed();
        _dialogs.OnEdit = (_, _) => new S3Account("new", "Third", "AKIA3", "s3");
        var vm = Create();

        vm.AddCommand.Execute(null);

        _dialogs.Edited.Should().Equal([null]);
        _store.TryGetAccount("new").Should().Be(new S3Account("new", "Third", "AKIA3", "s3"));
        vm.Accounts.Select(row => row.Account.Name).Should().Equal("Main", "Spare", "Third");
        vm.Selected!.Account.Id.Should().Be("new");
    }

    [Fact]
    public void The_name_check_compares_with_the_other_accounts_ignoring_case()
    {
        Seed();
        var vm = Create();
        vm.Selected = vm.Accounts[0];

        vm.EditCommand.Execute(null);
        vm.AddCommand.Execute(null);

        var editCheck = _dialogs.NameChecks[0];
        editCheck("spare").Should().BeTrue();
        editCheck("MAIN").Should().BeFalse("the edited account may keep its name");
        var addCheck = _dialogs.NameChecks[1];
        addCheck("main").Should().BeTrue();
        addCheck("Other").Should().BeFalse();
    }

    [Fact]
    public void Cancelling_the_account_dialog_changes_nothing()
    {
        Seed();
        var before = File.ReadAllBytes(_file);
        var vm = Create();

        vm.AddCommand.Execute(null);

        File.ReadAllBytes(_file).Should().Equal(before);
        vm.Accounts.Should().HaveCount(2);
    }

    [Fact]
    public void Edit_saves_the_account_and_keeps_an_unchanged_secret()
    {
        Seed();
        _dialogs.OnEdit = (existing, _) => existing! with { Name = "Primary", Secret = null };
        var vm = Create();
        vm.Selected = vm.Accounts[0];

        vm.EditCommand.Execute(null);

        _dialogs.Edited.Should().Equal(new S3Account("a", "Main", "AKIA1", "s1"));
        _store.TryGetAccount("a").Should().Be(new S3Account("a", "Primary", "AKIA1", "s1"));
        vm.Accounts.Select(row => (row.Account.Name, row.UsedBy)).Should().Equal(("Primary", 2), ("Spare", 0));
        vm.Selected!.Account.Id.Should().Be("a");
    }

    [Fact]
    public void A_failed_save_is_reported()
    {
        Seed();
        _dialogs.OnEdit = (_, _) => new S3Account("new", "Spare", "AKIA3", "s3");
        var vm = Create();

        vm.AddCommand.Execute(null);

        _dialogs.Errors.Should().Equal("s3.accounts.saveFailed");
        _store.TryGetAccount("new").Should().BeNull();
    }

    [Fact]
    public void Deleting_an_account_in_use_is_refused_with_the_connection_names()
    {
        Seed();
        var before = File.ReadAllBytes(_file);
        var vm = Create();
        vm.Selected = vm.Accounts[0];

        vm.DeleteCommand.Execute(null);

        _dialogs.Confirms.Should().BeEmpty();
        _dialogs.Errors.Should().ContainSingle().Which.Should().StartWith("s3.accounts.inUse").And.Contain("Backups").And.Contain("Photos");
        File.ReadAllBytes(_file).Should().Equal(before);
        vm.Accounts.Should().HaveCount(2);
    }

    [Fact]
    public void Deleting_an_unused_account_asks_first()
    {
        Seed();
        var vm = Create();
        vm.Selected = vm.Accounts[1];

        vm.DeleteCommand.Execute(null);

        _dialogs.Confirms.Should().ContainSingle().Which.TitleKey.Should().Be("s3.accounts.deleteTitle");
        _store.LoadAccounts().Select(a => a.Id).Should().Equal("a");
        vm.Accounts.Select(row => row.Account.Id).Should().Equal("a");
        vm.Selected.Should().BeNull();
    }

    [Fact]
    public void A_declined_delete_keeps_the_account()
    {
        Seed();
        _dialogs.ConfirmAnswer = false;
        var vm = Create();
        vm.Selected = vm.Accounts[1];

        vm.DeleteCommand.Execute(null);

        _store.LoadAccounts().Should().HaveCount(2);
        vm.Accounts.Should().HaveCount(2);
    }

    [Fact]
    public void An_account_whose_secret_cannot_be_decrypted_is_marked()
    {
        File.WriteAllText(_file, """
            { "formatVersion": 2,
              "accounts": [ { "id": "a", "name": "Main", "accessKeyId": "AKIA1", "secretProtected": "AAAA" } ],
              "connections": [] }
            """);

        var vm = Create();

        vm.Accounts.Should().ContainSingle().Which.NeedsSecret.Should().BeTrue();
    }
}
