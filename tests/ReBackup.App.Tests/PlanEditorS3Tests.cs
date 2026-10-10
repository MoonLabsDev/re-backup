using System.IO;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Plans;
using ReBackup.Storage;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.App.Tests;

/// <summary>The Plan tab's Folder | S3 switch for source and target, the connection picker, the prefix and the test.</summary>
public class PlanEditorS3Tests : IDisposable
{
    private readonly MainViewModelFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private string SourceFolder => Directory.CreateDirectory(Path.Combine(_fixture.Root, "src")).FullName;

    private string TargetFolder => Path.Combine(_fixture.Root, "dst");

    /// <summary>One plan backing a folder up into a folder, opened in the editor.</summary>
    private PlanEditorViewModel FolderPlan(out MainViewModel vm)
    {
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Docs",
            Source = StorageLocation.FileSystem(SourceFolder),
            Target = StorageLocation.FileSystem(TargetFolder),
        });
        vm = _fixture.Create();
        return vm.Plans.Single();
    }

    [Fact]
    public void A_folder_plan_shows_folders_and_an_S3_plan_shows_its_connection_and_prefix()
    {
        _fixture.AddConnection("c1", "Photos", "photos");
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Pull",
            Source = new StorageLocation(S3StorageFactory.Kind, "camera", "c1"),
            Target = StorageLocation.FileSystem(TargetFolder),
        });

        var editor = _fixture.Create().Plans.Single();

        editor.SourceIsS3.Should().BeTrue();
        editor.SourceIsFolder.Should().BeFalse();
        editor.SourceS3.ConnectionId.Should().Be("c1");
        editor.SourceS3.Prefix.Should().Be("camera");
        editor.Source.Should().BeEmpty();
        editor.TargetIsS3.Should().BeFalse();
        editor.Target.Should().Be(TargetFolder);
        editor.Errors.Should().BeEmpty();
        editor.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Switching_keeps_the_other_kinds_last_value_until_saved()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");
        var editor = FolderPlan(out _);

        editor.TargetIsS3 = true;
        editor.TargetS3.ConnectionId = "c1";
        editor.TargetS3.Prefix = "backups/docs";
        editor.IsDirty.Should().BeTrue();
        editor.ToPlan().Target.Should().Be(new StorageLocation("s3", "backups/docs", "c1"));

        editor.TargetIsFolder = true;
        editor.Target.Should().Be(TargetFolder, "the folder typed before is still there");
        editor.ToPlan().Target.Should().Be(StorageLocation.FileSystem(TargetFolder));

        editor.TargetIsS3 = true;
        editor.TargetS3.ConnectionId.Should().Be("c1", "the S3 values are kept as well");
        editor.TargetS3.Prefix.Should().Be("backups/docs");

        editor.TrySave(_fixture.Store).Should().BeTrue();
        _fixture.Store.TryLoad("p1")!.Target.Should().Be(new StorageLocation("s3", "backups/docs", "c1"));
        editor.Target.Should().BeEmpty("once saved as S3, the folder is no longer kept");
        editor.IsDirty.Should().BeFalse();

        editor.TargetIsFolder = true;
        editor.ToPlan().Target.Should().Be(StorageLocation.FileSystem(""));
        editor.Errors.Should().Contain("Target folder is required.");
    }

    [Fact]
    public void Revert_brings_the_saved_kind_back()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");
        var editor = FolderPlan(out _);
        editor.TargetIsS3 = true;
        editor.TargetS3.ConnectionId = "c1";

        editor.Revert();

        editor.TargetIsS3.Should().BeFalse();
        editor.Target.Should().Be(TargetFolder);
        editor.TargetS3.ConnectionId.Should().BeNull();
        editor.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void The_connection_list_is_the_saved_connections_and_follows_changes()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");
        var editor = FolderPlan(out var vm);

        editor.TargetS3.Connections.Select(c => c.Name).Should().Equal("Offsite");
        editor.SourceS3.Connections.Should().BeSameAs(editor.TargetS3.Connections);

        _fixture.S3.Save(new S3ConnectionInfo("c2", "Archive", "eu-central-1", "archive", MainViewModelFixture.AccountId));

        editor.TargetS3.Connections.Select(c => c.Name).Should().Equal("Offsite", "Archive");
    }

    [Fact]
    public void An_S3_location_needs_a_connection_and_a_valid_prefix()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");
        var editor = FolderPlan(out _);

        editor.TargetIsS3 = true;
        editor.Errors.Should().Contain("Choose an S3 connection for the target.");
        editor.TargetS3.TestCommand.CanExecute(null).Should().BeFalse();

        editor.TargetS3.ConnectionId = "c1";
        editor.TargetS3.Prefix = "/backups/";
        editor.Errors.Should().ContainSingle(e => e.StartsWith("The target prefix is not valid", StringComparison.Ordinal));
        editor.TargetS3.TestCommand.CanExecute(null).Should().BeFalse();

        editor.TargetS3.Prefix = "";
        editor.Errors.Should().BeEmpty("the whole bucket is a valid target");
        editor.TargetS3.TestCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void A_target_inside_the_source_on_the_same_bucket_cannot_be_saved()
    {
        _fixture.AddConnection("c1", "Bucket", "photos");
        _fixture.AddConnection("c2", "Same bucket", "photos");
        var editor = FolderPlan(out _);

        editor.SourceIsS3 = true;
        editor.SourceS3.ConnectionId = "c1";
        editor.SourceS3.Prefix = "data";
        editor.TargetIsS3 = true;
        editor.TargetS3.ConnectionId = "c2";
        editor.TargetS3.Prefix = "data/backups";

        editor.Errors.Should().Equal("Target must not be inside the source.");
        editor.TrySave(_fixture.Store).Should().BeFalse();

        editor.TargetS3.Prefix = "backups";
        editor.Errors.Should().BeEmpty();
    }

    [Fact]
    public void A_connection_that_was_deleted_shows_as_missing()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Docs",
            Source = StorageLocation.FileSystem(SourceFolder),
            Target = new StorageLocation("s3", "docs", "c1"),
        });
        var vm = _fixture.Create();

        _fixture.S3.Delete("c1");

        vm.Plans.Single().Errors.Should().Equal("The target's S3 connection no longer exists.");
    }

    [Fact]
    public void Unreadable_connections_are_named_with_their_reason()
    {
        File.WriteAllText(_fixture.Paths.ConnectionsFile, "{ not json");
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Docs",
            Source = StorageLocation.FileSystem(SourceFolder),
            Target = new StorageLocation("s3", "docs", "c1"),
        });

        var editor = _fixture.Create().Plans.Single();

        editor.Errors.Should().ContainSingle().Which.Should()
            .StartWith("The S3 connections could not be read: connections.json could not be read: ");
    }

    [Fact]
    public void The_target_full_toggle_is_hidden_for_an_S3_target()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");
        var editor = FolderPlan(out _);
        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        editor.ShowFreeSpaceOption.Should().BeTrue();
        editor.TargetIsS3 = true;
        editor.ShowFreeSpaceOption.Should().BeFalse();
        changed.Should().Contain(nameof(PlanEditorViewModel.ShowFreeSpaceOption));
        editor.SourceIsS3 = true;
        editor.ShowFreeSpaceOption.Should().BeFalse("only the target matters");
        editor.TargetIsFolder = true;
        editor.ShowFreeSpaceOption.Should().BeTrue();
    }

    [Fact]
    public void New_connection_saves_the_dialogs_result_and_picks_it()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");
        var editor = FolderPlan(out _);
        editor.TargetIsS3 = true;
        _fixture.Dialogs.NextConnection = new S3ConnectionInfo("c9", "New one", "eu-west-1", "fresh-bucket", MainViewModelFixture.AccountId);

        editor.TargetS3.NewConnectionCommand.Execute(null);

        _fixture.Dialogs.EditedConnections.Should().ContainSingle().Which.Should().BeNull("a new connection");
        _fixture.S3.Store.TryGet("c9").Should().NotBeNull();
        editor.TargetS3.ConnectionId.Should().Be("c9");
        editor.TargetS3.Connections.Select(c => c.Name).Should().Contain("New one");
    }

    [Fact]
    public void A_cancelled_new_connection_changes_nothing()
    {
        var editor = FolderPlan(out _);
        editor.TargetIsS3 = true;

        editor.TargetS3.NewConnectionCommand.Execute(null);

        editor.TargetS3.ConnectionId.Should().BeNull();
        _fixture.S3.Store.LoadAll().Should().BeEmpty();
    }

    [Fact]
    public async Task Test_runs_the_tester_on_the_connection_and_prefix_and_writes_only_for_a_target()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");
        var editor = FolderPlan(out _);
        editor.TargetIsS3 = true;
        editor.TargetS3.ConnectionId = "c1";
        editor.TargetS3.Prefix = "backups";
        editor.SourceIsS3 = true;
        editor.SourceS3.ConnectionId = "c1";
        editor.SourceS3.Prefix = "camera";

        await editor.TargetS3.TestCommand.ExecuteAsync(null);
        await editor.SourceS3.TestCommand.ExecuteAsync(null);

        _fixture.TestRuns.Should().Equal(("offsite", "backups", true), ("offsite", "camera", false));
        editor.TargetS3.Results.Should().ContainSingle().Which.State.Should().Be(S3CheckState.Ok);
        editor.TargetS3.TestStatus.Should().BeNull();
    }

    [Fact]
    public async Task Test_with_a_secret_to_re_enter_says_so_and_runs_nothing()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");
        var editor = FolderPlan(out _);
        editor.TargetIsS3 = true;
        editor.TargetS3.ConnectionId = "c1";
        _fixture.Secretless = true;

        await editor.TargetS3.TestCommand.ExecuteAsync(null);

        _fixture.TestRuns.Should().BeEmpty();
        editor.TargetS3.TestStatus.Should().Contain("re-enter", "the user is told what to do");
    }

    [Fact]
    public void The_S3_plan_runs_through_the_apps_storage_factory_with_the_connection()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");
        var editor = FolderPlan(out _);
        editor.TargetIsS3 = true;
        editor.TargetS3.ConnectionId = "c1";
        editor.TargetS3.Prefix = "x";
        editor.TrySave(_fixture.Store).Should().BeTrue();

        editor.SavedPlan().Target.Should().Be(new StorageLocation("s3", "x", "c1"));
        _fixture.S3.Describe(editor.SavedPlan().Target).Should().Be("Offsite · s3://offsite/x");
    }
}
