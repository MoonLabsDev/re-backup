using System.IO;
using System.Text.Json;
using FluentAssertions;
using ReBackup.App.Services;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Plans;
using ReBackup.Storage;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.App.Tests;

/// <summary>The connections of the configuration folder (the app's composition) and the Settings list of them.</summary>
public class S3ConnectionsTests : IDisposable
{
    private readonly MainViewModelFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void The_store_lives_in_the_configuration_folder()
    {
        _fixture.S3.FilePath.Should().Be(Path.Combine(_fixture.Root, "connections.json"));
    }

    [Fact]
    public void A_corrupt_store_surfaces_as_a_storage_error_with_its_reason()
    {
        File.WriteAllText(_fixture.Paths.ConnectionsFile, """{ "formatVersion": 99, "accounts": [], "connections": [] }""");

        var act = () => _fixture.S3.Resolve("c1");

        var error = act.Should().Throw<StorageIOException>().Which;
        error.Message.Should().StartWith("connections.json could not be read: ").And.Contain("newer version");
        error.InnerException.Should().BeOfType<JsonException>();
        error.Path.Should().Be(_fixture.Paths.ConnectionsFile);
    }

    [Fact]
    public void The_apps_storage_factory_reports_the_store_failure_when_an_S3_location_is_opened()
    {
        File.WriteAllText(_fixture.Paths.ConnectionsFile, "{ broken");
        using var factory = new S3StorageFactory(new StorageFactory(), _fixture.S3.Resolve);

        var act = () => factory.Open(new StorageLocation("s3", "x", "c1"));

        act.Should().Throw<StorageIOException>().WithMessage("connections.json could not be read: *");
        factory.Open(StorageLocation.FileSystem(_fixture.Root)).Should().NotBeNull("folders never read the store");
    }

    [Fact]
    public void An_unknown_connection_resolves_to_null_and_a_known_one_with_its_account()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");

        _fixture.S3.Resolve("nope").Should().BeNull();
        var resolved = _fixture.S3.Resolve("c1")!;
        resolved.Bucket.Should().Be("offsite");
        resolved.Secret.Should().Be("secret-key");
    }

    [Fact]
    public void A_location_is_described_by_connection_bucket_and_prefix()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");

        _fixture.S3.Describe(StorageLocation.FileSystem(@"D:\Backups")).Should().Be(@"D:\Backups");
        _fixture.S3.Describe(new StorageLocation("s3", "docs/2026", "c1")).Should().Be("Offsite · s3://offsite/docs/2026");
        _fixture.S3.Describe(new StorageLocation("s3", "", "c1")).Should().Be("Offsite · s3://offsite/");
        _fixture.S3.Describe(new StorageLocation("s3", "docs", "gone")).Should().Be("S3 connection not found · docs");
    }

    // ---- Settings → S3 connections… ----

    private S3ConnectionsViewModel ListOf(MainViewModel vm) => new(_fixture.S3, _fixture.Dialogs, vm.PlansUsingConnection);

    [Fact]
    public void The_list_shows_the_connections_and_adds_through_the_shared_dialog()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");
        var list = ListOf(_fixture.Create());
        list.Connections.Select(c => c.Name).Should().Equal("Offsite");

        _fixture.Dialogs.NextConnection = new S3ConnectionInfo("c2", "Archive", "eu-central-1", "archive", MainViewModelFixture.AccountId);
        list.AddCommand.Execute(null);

        list.Connections.Select(c => c.Name).Should().Equal("Offsite", "Archive");
        list.Selected!.Id.Should().Be("c2");
    }

    [Fact]
    public void Edit_opens_the_dialog_on_the_selected_connection_and_stores_the_change()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");
        var list = ListOf(_fixture.Create());
        list.Selected = list.Connections[0];
        _fixture.Dialogs.NextConnection = list.Connections[0] with { Name = "Offsite NAS copy" };

        list.EditCommand.Execute(null);

        _fixture.Dialogs.EditedConnections.Should().ContainSingle().Which!.Id.Should().Be("c1");
        _fixture.S3.Store.TryGet("c1")!.Name.Should().Be("Offsite NAS copy");
    }

    [Fact]
    public void Delete_is_refused_while_plans_use_the_connection_and_names_them()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");
        _fixture.Store.Save(new BackupPlan { Id = "p1", Name = "Docs", Target = new StorageLocation("s3", "docs", "c1") });
        _fixture.Store.Save(new BackupPlan { Id = "p2", Name = "Photos", Source = new StorageLocation("s3", "", "C1") });
        _fixture.Store.Save(new BackupPlan { Id = "p3", Name = "Music" });
        var list = ListOf(_fixture.Create());
        list.Selected = list.Connections[0];

        list.DeleteCommand.Execute(null);

        _fixture.S3.Store.TryGet("c1").Should().NotBeNull();
        _fixture.Dialogs.Errors.Should().ContainSingle().Which.Should()
            .Contain("used by these plans").And.Contain("• Docs").And.Contain("• Photos").And.NotContain("Music");
        _fixture.Dialogs.Confirms.Should().BeEmpty("nothing is asked when it cannot be deleted");
    }

    [Fact]
    public void Delete_is_refused_for_an_unsaved_edit_that_uses_the_connection()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");
        _fixture.Store.Save(new BackupPlan { Id = "p1", Name = "Docs" });
        var vm = _fixture.Create();
        vm.Plans[0].TargetIsS3 = true;
        vm.Plans[0].TargetS3.ConnectionId = "c1";
        var list = ListOf(vm);
        list.Selected = list.Connections[0];

        list.DeleteCommand.Execute(null);

        _fixture.S3.Store.TryGet("c1").Should().NotBeNull();
        _fixture.Dialogs.Errors.Should().ContainSingle().Which.Should().Contain("• Docs");
    }

    [Fact]
    public void An_unused_connection_is_deleted_after_confirmation()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");
        var list = ListOf(_fixture.Create());
        list.Selected = list.Connections[0];
        _fixture.Dialogs.ConfirmAnswer = false;

        list.DeleteCommand.Execute(null);
        _fixture.S3.Store.TryGet("c1").Should().NotBeNull("the user said no");

        _fixture.Dialogs.ConfirmAnswer = true;
        list.DeleteCommand.Execute(null);

        _fixture.S3.Store.TryGet("c1").Should().BeNull();
        list.Connections.Should().BeEmpty();
        _fixture.Dialogs.Confirms.Should().HaveCount(2).And.AllSatisfy(m => m.Should().Contain("Offsite"));
    }

    [Fact]
    public void An_unreadable_store_shows_its_reason_and_allows_no_changes()
    {
        File.WriteAllText(_fixture.Paths.ConnectionsFile, "{ broken");
        var list = ListOf(_fixture.Create());

        list.LoadError.Should().StartWith("The S3 connections could not be read: ");
        list.AddCommand.CanExecute(null).Should().BeFalse();
        File.ReadAllText(_fixture.Paths.ConnectionsFile).Should().Be("{ broken", "the file is never overwritten");
    }

    [Fact]
    public void Settings_open_the_S3_accounts_and_connections()
    {
        var accounts = 0;
        var connections = 0;
        var settings = new SettingsViewModel(_fixture.SettingsStore, _fixture.Settings, _fixture.Paths, _fixture.Root,
            _fixture.Dialogs, () => true, () => false, _ => { }, "en-US", _ => { },
            manageS3Accounts: () => accounts++, manageS3Connections: () => connections++);

        settings.ManageS3AccountsCommand.Execute(null);
        settings.ManageS3ConnectionsCommand.Execute(null);

        accounts.Should().Be(1);
        connections.Should().Be(1);
    }
}
