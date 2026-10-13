using System.IO;
using System.Text.Json;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Plans;
using ReBackup.Storage;
using ReBackup.Storage.GoogleDrive;
using ReBackup.Storage.GoogleDrive.Connections;

namespace ReBackup.App.Tests;

/// <summary>
/// The Google accounts and Drive connections of the configuration folder (the app's composition), the Settings lists of
/// them and the account and connection dialogs' logic.
/// </summary>
public class DriveConnectionsTests : IDisposable
{
    private readonly MainViewModelFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void The_store_lives_in_the_configuration_folder()
    {
        _fixture.Drive.FilePath.Should().Be(Path.Combine(_fixture.Root, "drive-connections.json"));
    }

    [Fact]
    public void A_corrupt_store_surfaces_as_a_storage_error_with_its_reason()
    {
        File.WriteAllText(_fixture.Paths.DriveConnectionsFile, """{ "formatVersion": 99, "accounts": [], "connections": [] }""");

        var resolve = () => _fixture.Drive.Resolve("d1");
        var root = () => _fixture.Drive.RootOf("d1");

        var error = resolve.Should().Throw<StorageIOException>().Which;
        error.Message.Should().StartWith("drive-connections.json could not be read: ").And.Contain("newer version");
        error.Path.Should().Be(_fixture.Paths.DriveConnectionsFile);
        root.Should().Throw<StorageIOException>().WithMessage("drive-connections.json could not be read: *");
    }

    [Fact]
    public void Unreadable_connections_are_named_in_the_plans_errors()
    {
        File.WriteAllText(_fixture.Paths.DriveConnectionsFile, "{ broken");
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Docs",
            Source = StorageLocation.FileSystem(Directory.CreateDirectory(Path.Combine(_fixture.Root, "src")).FullName),
            Target = new StorageLocation("gdrive", "docs", "d1"),
        });

        var editor = _fixture.Create().Plans.Single();

        editor.Errors.Should().ContainSingle().Which.Should()
            .StartWith("The Google Drive connections could not be read: drive-connections.json could not be read: ");
    }

    [Fact]
    public void The_apps_storage_factory_reports_the_store_failure_when_a_Drive_location_is_opened()
    {
        File.WriteAllText(_fixture.Paths.DriveConnectionsFile, "{ broken");
        using var factory = new DriveStorageFactory(new StorageFactory(), _fixture.Drive.Resolve);

        var act = () => factory.Open(new StorageLocation("gdrive", "x", "d1"));

        act.Should().Throw<StorageIOException>().WithMessage("drive-connections.json could not be read: *");
        factory.Open(StorageLocation.FileSystem(_fixture.Root)).Should().NotBeNull("folders never read the store");
    }

    // ---- Settings → Drive connections… ----

    private DriveConnectionsViewModel ConnectionsOf(MainViewModel vm) => new(_fixture.Drive, _fixture.Dialogs, vm.PlansUsingDriveConnection);

    [Fact]
    public void The_list_shows_the_connections_and_adds_and_edits_through_the_dialog()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        var list = ConnectionsOf(_fixture.Create());
        list.Connections.Select(c => c.Name).Should().Equal("Team");

        _fixture.Dialogs.NextDriveConnection = new DriveConnectionInfo("d2", "Archive", MainViewModelFixture.DriveAccountId, "a", "Archive", "a");
        list.AddCommand.Execute(null);
        list.Connections.Select(c => c.Name).Should().Equal("Team", "Archive");
        list.Selected!.Id.Should().Be("d2");

        _fixture.Dialogs.NextDriveConnection = list.Connections[0] with { Name = "Team drive" };
        list.Selected = list.Connections[0];
        list.EditCommand.Execute(null);
        _fixture.Dialogs.EditedDriveConnections.Should().HaveCount(2).And.Contain(c => c != null && c.Id == "d1");
        _fixture.Drive.Store.TryGet("d1")!.Name.Should().Be("Team drive");
    }

    [Fact]
    public void Delete_is_refused_while_plans_use_the_connection_and_names_them()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        _fixture.Store.Save(new BackupPlan { Id = "p1", Name = "Docs", Target = new StorageLocation("gdrive", "docs", "d1") });
        _fixture.Store.Save(new BackupPlan { Id = "p2", Name = "Photos", Source = new StorageLocation("gdrive", "", "D1") });
        _fixture.Store.Save(new BackupPlan { Id = "p3", Name = "Music", Target = new StorageLocation("s3", "", "d1") });
        var list = ConnectionsOf(_fixture.Create());
        list.Selected = list.Connections[0];

        list.DeleteCommand.Execute(null);

        _fixture.Drive.Store.TryGet("d1").Should().NotBeNull();
        _fixture.Dialogs.Errors.Should().ContainSingle().Which.Should()
            .Contain("used by these plans").And.Contain("• Docs").And.Contain("• Photos").And.NotContain("Music");
        _fixture.Dialogs.Confirms.Should().BeEmpty("nothing is asked when it cannot be deleted");
    }

    [Fact]
    public void Delete_is_refused_for_an_unsaved_edit_that_uses_the_connection()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        _fixture.Store.Save(new BackupPlan { Id = "p1", Name = "Docs" });
        var vm = _fixture.Create();
        vm.Plans[0].ChooseTargetKindCommand.Execute("Drive");
        vm.Plans[0].TargetDrive.ConnectionId = "d1";
        var list = ConnectionsOf(vm);
        list.Selected = list.Connections[0];

        list.DeleteCommand.Execute(null);

        _fixture.Drive.Store.TryGet("d1").Should().NotBeNull();
        _fixture.Dialogs.Errors.Should().ContainSingle().Which.Should().Contain("• Docs");
    }

    [Fact]
    public void An_unused_connection_is_deleted_after_confirmation()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        var list = ConnectionsOf(_fixture.Create());
        list.Selected = list.Connections[0];

        list.DeleteCommand.Execute(null);

        _fixture.Drive.Store.TryGet("d1").Should().BeNull();
        _fixture.Dialogs.Confirms.Should().ContainSingle().Which.Should().Contain("Team");
    }

    [Fact]
    public void An_unreadable_store_shows_its_reason_and_allows_no_changes()
    {
        File.WriteAllText(_fixture.Paths.DriveConnectionsFile, "{ broken");
        var list = ConnectionsOf(_fixture.Create());
        var accounts = new DriveAccountsViewModel(_fixture.Drive, _fixture.Dialogs);

        list.LoadError.Should().StartWith("The Google Drive connections could not be read: ");
        list.AddCommand.CanExecute(null).Should().BeFalse();
        accounts.AddCommand.CanExecute(null).Should().BeFalse();
        File.ReadAllText(_fixture.Paths.DriveConnectionsFile).Should().Be("{ broken", "the file is never overwritten");
    }

    // ---- Settings → Google accounts… ----

    [Fact]
    public void Accounts_are_added_with_their_key_and_show_its_e_mail()
    {
        var accounts = new DriveAccountsViewModel(_fixture.Drive, _fixture.Dialogs);
        _fixture.Dialogs.NextDriveAccount = new DriveAccountInput("Backup", MainViewModelFixture.ServiceAccountKey("a@p.iam.gserviceaccount.com"));

        accounts.AddCommand.Execute(null);

        accounts.Accounts.Should().ContainSingle().Which.ClientEmail.Should().Be("a@p.iam.gserviceaccount.com");
        accounts.Selected.Should().BeSameAs(accounts.Accounts[0]);
        accounts.Accounts[0].NeedsKey.Should().BeFalse();
    }

    [Fact]
    public void Renaming_an_account_keeps_its_key()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        var accounts = new DriveAccountsViewModel(_fixture.Drive, _fixture.Dialogs);
        accounts.Selected = accounts.Accounts[0];
        _fixture.Dialogs.NextDriveAccount = new DriveAccountInput("Renamed", null);

        accounts.EditCommand.Execute(null);

        _fixture.Drive.Store.TryGetAccount(MainViewModelFixture.DriveAccountId)!.Name.Should().Be("Renamed");
        _fixture.Drive.Resolve("d1")!.KeyJson.Should().Be(MainViewModelFixture.ServiceAccountKey());
    }

    [Fact]
    public void An_account_used_by_connections_is_not_deleted_and_the_connections_are_named()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        var accounts = new DriveAccountsViewModel(_fixture.Drive, _fixture.Dialogs);
        accounts.Selected = accounts.Accounts[0];

        accounts.DeleteCommand.Execute(null);

        _fixture.Drive.Store.TryGetAccount(MainViewModelFixture.DriveAccountId).Should().NotBeNull();
        _fixture.Dialogs.Errors.Should().ContainSingle().Which.Should().Contain("used by these Drive connections").And.Contain("• Team");
    }

    [Fact]
    public void A_key_that_is_no_service_account_key_is_reported_by_the_store()
    {
        var accounts = new DriveAccountsViewModel(_fixture.Drive, _fixture.Dialogs);
        _fixture.Dialogs.NextDriveAccount = new DriveAccountInput("Backup", """{ "type": "authorized_user" }""");

        accounts.AddCommand.Execute(null);

        accounts.Accounts.Should().BeEmpty();
        _fixture.Dialogs.Errors.Should().ContainSingle().Which.Should().Contain("not a Google service account key");
    }

    // ---- the account dialog ----

    [Fact]
    public void The_account_dialog_reads_the_key_file_and_shows_the_e_mail_to_share_with()
    {
        var keyFile = Path.Combine(_fixture.Root, "key.json");
        File.WriteAllText(keyFile, MainViewModelFixture.ServiceAccountKey());
        var dialog = new DriveAccountDialogViewModel(null, _ => false, () => keyFile) { Name = "Backup" };
        dialog.SaveCommand.CanExecute(null).Should().BeFalse("a new account needs a key");

        dialog.ChooseKeyCommand.Execute(null);

        dialog.ClientEmail.Should().Be(MainViewModelFixture.DriveEmail);
        dialog.KeyFileName.Should().Be("key.json");
        dialog.SaveCommand.CanExecute(null).Should().BeTrue();
        dialog.SaveCommand.Execute(null);
        dialog.Result.Should().Be(new DriveAccountInput("Backup", MainViewModelFixture.ServiceAccountKey()));
        dialog.Result!.ToString().Should().NotContain("not-a-real-key", "the key is never printed");
    }

    [Fact]
    public void The_account_dialog_refuses_a_file_that_is_no_service_account_key()
    {
        var keyFile = Path.Combine(_fixture.Root, "oauth.json");
        File.WriteAllText(keyFile, """{ "installed": { "client_id": "x" } }""");
        var dialog = new DriveAccountDialogViewModel(null, _ => false, () => keyFile) { Name = "Backup" };

        dialog.ChooseKeyCommand.Execute(null);

        dialog.KeyError.Should().StartWith("This is not a Google service account key.");
        dialog.ClientEmail.Should().BeNull();
        dialog.SaveCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void The_account_dialog_refuses_a_file_over_64_KB_without_reading_it()
    {
        var keyFile = Path.Combine(_fixture.Root, "huge.json");
        var key = MainViewModelFixture.ServiceAccountKey();
        File.WriteAllText(keyFile, key[..^1] + ", \"padding\": \"" + new string('x', 64 * 1024) + "\" }");
        var dialog = new DriveAccountDialogViewModel(null, _ => false, () => keyFile) { Name = "Backup" };

        dialog.ChooseKeyCommand.Execute(null);

        dialog.KeyError.Should().StartWith("This is not a Google service account key.", "a valid key padded past 64 KB is refused by size");
        dialog.ClientEmail.Should().BeNull();
    }

    [Fact]
    public void The_account_dialog_refuses_a_name_another_account_has()
    {
        var dialog = new DriveAccountDialogViewModel(null, name => name == "Taken", () => null) { Name = "Taken" };

        dialog.NameError.Should().Be("Another account already has this name.");
    }

    // ---- the connection dialog ----

    private DriveConnectionDialogViewModel ConnectionDialog(DriveConnectionInfo? existing = null) =>
        new(existing, () => _fixture.Drive.Store.LoadAccounts(), _ => false, _fixture.Drive.ApiForAccount, () => null);

    [Fact]
    public async Task The_connection_dialog_lists_the_shared_drives_of_the_account()
    {
        _fixture.Drive.SaveAccount(MainViewModelFixture.DriveAccountId, "Backup", MainViewModelFixture.ServiceAccountKey());
        _fixture.DriveApi.SharedDrives.AddRange([new SharedDrive("z", "Zeta"), new SharedDrive("a", "Alpha")]);
        var dialog = ConnectionDialog();
        dialog.Name = "Team";
        dialog.SelectedAccount.Should().NotBeNull("the only account is taken");
        dialog.AccountHint.Should().Contain(MainViewModelFixture.DriveEmail);

        await dialog.LoadSharedDrivesCommand.ExecuteAsync(null);

        _fixture.DriveApiKeys.Should().Equal(MainViewModelFixture.ServiceAccountKey());
        dialog.SharedDrives.Select(d => d.Name).Should().Equal("Alpha", "Zeta");
        dialog.SaveCommand.CanExecute(null).Should().BeFalse("no shared drive is chosen yet");
        dialog.SelectedSharedDrive = dialog.SharedDrives[1];
        await dialog.SaveCommand.ExecuteAsync(null);

        dialog.Result.Should().BeEquivalentTo(new { Name = "Team", AccountId = MainViewModelFixture.DriveAccountId, RootId = "z",
            RootName = "Zeta", SharedDriveId = "z" });
    }

    [Fact]
    public async Task No_shared_drives_says_how_to_add_the_account()
    {
        _fixture.Drive.SaveAccount(MainViewModelFixture.DriveAccountId, "Backup", MainViewModelFixture.ServiceAccountKey());
        var dialog = ConnectionDialog();

        await dialog.LoadSharedDrivesCommand.ExecuteAsync(null);

        dialog.Error.Should().Contain(MainViewModelFixture.DriveEmail).And.Contain("not a member of any shared drive");
    }

    [Theory]
    [InlineData("https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOp", "1AbCdEfGhIjKlMnOp")]
    [InlineData("https://drive.google.com/drive/u/0/folders/1AbCdEfGhIjKlMnOp?usp=sharing", "1AbCdEfGhIjKlMnOp")]
    [InlineData("https://drive.google.com/open?id=1AbCdEfGhIjKlMnOp", "1AbCdEfGhIjKlMnOp")]
    [InlineData("  1AbCdEfGhIjKlMnOp  ", "1AbCdEfGhIjKlMnOp")]
    [InlineData("https://example.com/drive/folders/1AbCdEfGhIjKlMnOp", null)]
    [InlineData("drive.google.com/drive/folders/1AbCdEfGhIjKlMnOp", "1AbCdEfGhIjKlMnOp")]
    [InlineData("https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOp?resourcekey=0-abc", "1AbCdEfGhIjKlMnOp")]
    [InlineData("not a link", null)]
    [InlineData("", null)]
    public void A_folder_link_or_ID_is_parsed(string text, string? expected)
    {
        DriveConnectionDialogViewModel.ParseFolderId(text).Should().Be(expected);
    }

    [Fact]
    public async Task A_folder_given_by_link_is_looked_up_and_keeps_its_shared_drive()
    {
        _fixture.Drive.SaveAccount(MainViewModelFixture.DriveAccountId, "Backup", MainViewModelFixture.ServiceAccountKey());
        _fixture.DriveApi.Items["1AbCdEfGhIjKlMnOp"] =
            new DriveItem("1AbCdEfGhIjKlMnOp", "Backups", DriveMimeTypes.Folder, null, DateTime.UtcNow, null, 1, "team");
        var dialog = ConnectionDialog();
        dialog.Name = "Backups";
        dialog.UseFolder = true;
        dialog.FolderText = "https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOp";

        await dialog.SaveCommand.ExecuteAsync(null);

        dialog.Result.Should().BeEquivalentTo(new { RootId = "1AbCdEfGhIjKlMnOp", RootName = "Backups", SharedDriveId = "team" });
    }

    [Fact]
    public async Task A_folder_that_is_not_shared_with_the_account_is_reported_with_the_address()
    {
        _fixture.Drive.SaveAccount(MainViewModelFixture.DriveAccountId, "Backup", MainViewModelFixture.ServiceAccountKey());
        var dialog = ConnectionDialog();
        dialog.Name = "Backups";
        dialog.UseFolder = true;
        dialog.FolderText = "1AbCdEfGhIjKlMnOp";

        await dialog.SaveCommand.ExecuteAsync(null);

        dialog.Result.Should().BeNull();
        dialog.Error.Should().StartWith("Not found.").And.Contain(MainViewModelFixture.DriveEmail);

        dialog.FolderText = "nope";
        await dialog.SaveCommand.ExecuteAsync(null);
        dialog.Error.Should().Be("This is neither a Drive folder link nor a folder ID.");
    }

    [Fact]
    public async Task A_listing_that_takes_too_long_says_so()
    {
        _fixture.Drive.SaveAccount(MainViewModelFixture.DriveAccountId, "Backup", MainViewModelFixture.ServiceAccountKey());
        _fixture.DriveApi.Gate = new TaskCompletionSource();
        var dialog = new DriveConnectionDialogViewModel(null, () => _fixture.Drive.Store.LoadAccounts(), _ => false,
            _fixture.Drive.ApiForAccount, () => null) { CallTimeout = TimeSpan.FromMilliseconds(50) };

        await dialog.LoadSharedDrivesCommand.ExecuteAsync(null);

        dialog.Error.Should().Be("Google Drive did not answer in time. Check the connection and try again.");
        dialog.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task A_network_timeout_says_so_as_well()
    {
        _fixture.Drive.SaveAccount(MainViewModelFixture.DriveAccountId, "Backup", MainViewModelFixture.ServiceAccountKey());
        _fixture.DriveApi.Failure = new TimeoutException("socket");
        var dialog = ConnectionDialog();

        await dialog.LoadSharedDrivesCommand.ExecuteAsync(null);

        dialog.Error.Should().Be("Google Drive did not answer in time. Check the connection and try again.");
    }

    [Fact]
    public async Task Closing_the_dialog_cancels_a_running_listing_without_a_message()
    {
        _fixture.Drive.SaveAccount(MainViewModelFixture.DriveAccountId, "Backup", MainViewModelFixture.ServiceAccountKey());
        _fixture.DriveApi.Gate = new TaskCompletionSource();
        var dialog = ConnectionDialog();

        var load = dialog.LoadSharedDrivesCommand.ExecuteAsync(null);
        dialog.IsBusy.Should().BeTrue();
        dialog.Cancel();
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        dialog.Error.Should().BeNull();
        dialog.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void Editing_a_shared_drive_connection_shows_it_without_listing_first()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team", rootName: "Team Drive");
        var dialog = ConnectionDialog(_fixture.Drive.Store.TryGet("d1"));

        dialog.UseSharedDrive.Should().BeTrue();
        dialog.SelectedSharedDrive.Should().Be(new SharedDrive("team", "Team Drive"));
        dialog.SaveCommand.CanExecute(null).Should().BeTrue();
        _fixture.DriveApi.Calls.Should().Be(0);
    }

    [Fact]
    public void Settings_open_the_Google_accounts_and_Drive_connections()
    {
        var accounts = 0;
        var connections = 0;
        var settings = new SettingsViewModel(_fixture.SettingsStore, _fixture.Settings, _fixture.Paths, _fixture.Root,
            _fixture.Dialogs, () => true, () => false, _ => { }, "en-US", _ => { },
            manageDriveAccounts: () => accounts++, manageDriveConnections: () => connections++);

        settings.ManageDriveAccountsCommand.Execute(null);
        settings.ManageDriveConnectionsCommand.Execute(null);

        accounts.Should().Be(1);
        connections.Should().Be(1);
    }
}
