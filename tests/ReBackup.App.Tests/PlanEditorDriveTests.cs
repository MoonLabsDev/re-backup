using System.IO;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;
using ReBackup.Storage.GoogleDrive;
using ReBackup.Storage.GoogleDrive.Connections;

namespace ReBackup.App.Tests;

/// <summary>The Plan tab's Folder | S3 | Drive switch, the Drive connection picker, the path and the test.</summary>
public class PlanEditorDriveTests : IDisposable
{
    private readonly MainViewModelFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private string SourceFolder => Directory.CreateDirectory(Path.Combine(_fixture.Root, "src")).FullName;

    private string TargetFolder => Path.Combine(_fixture.Root, "dst");

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

    private static StorageLocation Drive(string path, string connectionId) => new(DriveStorageFactory.Kind, path, connectionId);

    [Fact]
    public void A_Drive_plan_shows_its_connection_and_path()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Docs",
            Source = StorageLocation.FileSystem(SourceFolder),
            Target = Drive("backups/docs", "d1"),
        });

        var editor = _fixture.Create().Plans.Single();

        editor.TargetIsDrive.Should().BeTrue();
        editor.TargetIsS3.Should().BeFalse();
        editor.TargetIsFolder.Should().BeFalse();
        editor.TargetDrive.ConnectionId.Should().Be("d1");
        editor.TargetDrive.Path.Should().Be("backups/docs");
        editor.Target.Should().BeEmpty();
        editor.Errors.Should().BeEmpty();
        editor.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Switching_between_three_kinds_keeps_the_other_kinds_values_until_saved()
    {
        _fixture.AddConnection("c1", "Offsite", "offsite");
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        var editor = FolderPlan(out _);

        editor.ChooseTargetKindCommand.Execute("S3");
        editor.TargetS3.ConnectionId = "c1";
        editor.TargetS3.Prefix = "s3/docs";
        editor.ChooseTargetKindCommand.Execute("Drive");
        editor.TargetIsS3.Should().BeFalse("a side is one kind at a time");
        editor.TargetIsDrive.Should().BeTrue();
        editor.TargetDrive.ConnectionId = "d1";
        editor.TargetDrive.Path = "drive/docs";
        editor.ToPlan().Target.Should().Be(Drive("drive/docs", "d1"));

        editor.ChooseTargetKindCommand.Execute("Folder");
        editor.TargetIsFolder.Should().BeTrue();
        editor.ToPlan().Target.Should().Be(StorageLocation.FileSystem(TargetFolder), "the folder typed before is still there");
        editor.ChooseTargetKindCommand.Execute("S3");
        editor.ToPlan().Target.Should().Be(new StorageLocation("s3", "s3/docs", "c1"), "the S3 values are kept as well");
        editor.ChooseTargetKindCommand.Execute("Drive");
        editor.ToPlan().Target.Should().Be(Drive("drive/docs", "d1"));

        editor.TrySave(_fixture.Store).Should().BeTrue();
        _fixture.Store.TryLoad("p1")!.Target.Should().Be(Drive("drive/docs", "d1"));
        editor.Target.Should().BeEmpty("once saved as Drive, the folder is no longer kept");
        editor.TargetS3.ConnectionId.Should().BeNull("nor the S3 values");
        editor.TargetDrive.ConnectionId.Should().Be("d1");
    }

    [Fact]
    public void Revert_brings_the_saved_kind_back()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        var editor = FolderPlan(out _);
        editor.ChooseSourceKindCommand.Execute("Drive");
        editor.SourceDrive.ConnectionId = "d1";

        editor.Revert();

        editor.SourceIsDrive.Should().BeFalse();
        editor.SourceIsFolder.Should().BeTrue();
        editor.Source.Should().Be(SourceFolder);
        editor.SourceDrive.ConnectionId.Should().BeNull();
        editor.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void A_Drive_location_needs_a_connection_a_valid_path_and_a_shared_drive_for_a_target()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        _fixture.AddDriveConnection("my", "Mine", "my-folder", null);
        var editor = FolderPlan(out _);

        editor.ChooseTargetKindCommand.Execute("Drive");
        editor.Errors.Should().Contain("Choose a Google Drive connection for the target.");
        editor.TargetDrive.TestCommand.CanExecute(null).Should().BeFalse();

        editor.TargetDrive.ConnectionId = "d1";
        editor.TargetDrive.Path = "/backups/";
        editor.Errors.Should().ContainSingle(e => e.StartsWith("The target path is not valid", StringComparison.Ordinal));
        editor.TargetDrive.TestCommand.CanExecute(null).Should().BeFalse();

        editor.TargetDrive.Path = "";
        editor.Errors.Should().BeEmpty("the shared drive itself is a valid target");

        editor.TargetDrive.ConnectionId = "my";
        editor.Errors.Should().Equal("Drive targets must be on a shared drive.");
        editor.TrySave(_fixture.Store).Should().BeFalse();

        editor.ChooseSourceKindCommand.Execute("Drive");
        editor.SourceDrive.ConnectionId = "my";
        editor.TargetDrive.ConnectionId = "d1";
        editor.Errors.Should().BeEmpty("a source may be in My Drive");
    }

    [Fact]
    public void A_target_inside_the_source_on_the_same_root_cannot_be_saved()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        _fixture.AddDriveConnection("d2", "Team again", "team", "team");
        var editor = FolderPlan(out _);

        editor.ChooseSourceKindCommand.Execute("Drive");
        editor.SourceDrive.ConnectionId = "d1";
        editor.SourceDrive.Path = "data";
        editor.ChooseTargetKindCommand.Execute("Drive");
        editor.TargetDrive.ConnectionId = "d2";
        editor.TargetDrive.Path = "data/backups";

        editor.Errors.Should().Equal("Target must not be inside the source.");

        editor.TargetDrive.Path = "backups";
        editor.Errors.Should().BeEmpty();
    }

    [Fact]
    public void A_deleted_connection_shows_as_missing()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Docs",
            Source = StorageLocation.FileSystem(SourceFolder),
            Target = Drive("docs", "d1"),
        });
        var vm = _fixture.Create();

        _fixture.Drive.Delete("d1");

        vm.Plans.Single().Errors.Should().Equal("The target's Google Drive connection no longer exists.");
    }

    [Fact]
    public void The_target_full_toggle_is_hidden_for_a_Drive_target()
    {
        var editor = FolderPlan(out _);
        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        editor.ShowFreeSpaceOption.Should().BeTrue();
        editor.ChooseTargetKindCommand.Execute("Drive");
        editor.ShowFreeSpaceOption.Should().BeFalse();
        changed.Should().Contain(nameof(PlanEditorViewModel.ShowFreeSpaceOption));
        editor.ChooseTargetKindCommand.Execute("Folder");
        editor.ShowFreeSpaceOption.Should().BeTrue();
    }

    [Fact]
    public void New_connection_saves_the_dialogs_result_and_picks_it()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        var editor = FolderPlan(out _);
        editor.ChooseTargetKindCommand.Execute("Drive");
        _fixture.Dialogs.NextDriveConnection =
            new DriveConnectionInfo("d9", "Archive", MainViewModelFixture.DriveAccountId, "arch", "Archive", "arch");

        editor.TargetDrive.NewConnectionCommand.Execute(null);

        _fixture.Dialogs.EditedDriveConnections.Should().ContainSingle().Which.Should().BeNull("a new connection");
        _fixture.Drive.Store.TryGet("d9").Should().NotBeNull();
        editor.TargetDrive.ConnectionId.Should().Be("d9");
        editor.TargetDrive.Connections.Select(c => c.Name).Should().Equal("Team", "Archive");
    }

    [Theory]
    [InlineData(DriveTestOutcome.Ok, true, "The location can be written to (a test file was written and deleted).")]
    [InlineData(DriveTestOutcome.AccessDenied, false, "The service account " + MainViewModelFixture.DriveEmail + " may not open, write or delete here.")]
    [InlineData(DriveTestOutcome.NotFound, false, "Not found. Check the folder, or share it with " + MainViewModelFixture.DriveEmail + ".")]
    [InlineData(DriveTestOutcome.NotSharedDrive, false, "Backups can only be written to a shared drive")]
    [InlineData(DriveTestOutcome.Error, false, "The test failed: Drive is down.")]
    public async Task Test_maps_the_testers_outcome_to_a_text_in_the_applied_language(DriveTestOutcome outcome, bool passed, string text)
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        var editor = FolderPlan(out _);
        editor.ChooseTargetKindCommand.Execute("Drive");
        editor.TargetDrive.ConnectionId = "d1";
        editor.TargetDrive.Path = "backups";
        _fixture.NextDriveTest = new DriveTestResult(outcome, "Drive is down.");

        await editor.TargetDrive.TestCommand.ExecuteAsync(null);

        _fixture.DriveTestRuns.Should().Equal(("team", "backups", true));
        editor.TargetDrive.TestPassed.Should().Be(passed);
        editor.TargetDrive.TestMessage.Should().StartWith(text);
    }

    [Fact]
    public async Task A_source_is_tested_without_writing()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        var editor = FolderPlan(out _);
        editor.ChooseSourceKindCommand.Execute("Drive");
        editor.SourceDrive.ConnectionId = "d1";

        await editor.SourceDrive.TestCommand.ExecuteAsync(null);

        _fixture.DriveTestRuns.Should().Equal(("team", "", false));
        editor.SourceDrive.TestMessage.Should().Be("The location can be read.");
    }

    [Fact]
    public async Task Test_with_a_key_to_choose_again_says_so_and_runs_nothing()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        var editor = FolderPlan(out _);
        editor.ChooseTargetKindCommand.Execute("Drive");
        editor.TargetDrive.ConnectionId = "d1";
        var file = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_fixture.Paths.DriveConnectionsFile))!;
        foreach (var account in file["accounts"]!.AsArray())
            account!["protectedKey"] = "AAAA";
        File.WriteAllText(_fixture.Paths.DriveConnectionsFile, file.ToJsonString());

        await editor.TargetDrive.TestCommand.ExecuteAsync(null);

        _fixture.DriveTestRuns.Should().BeEmpty();
        editor.TargetDrive.TestMessage.Should().Contain("chosen again");
    }

    [Fact]
    public async Task A_result_whose_path_changed_meanwhile_is_dropped()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        var editor = FolderPlan(out _);
        editor.ChooseTargetKindCommand.Execute("Drive");
        editor.TargetDrive.ConnectionId = "d1";
        _fixture.TestGate = new TaskCompletionSource();

        var test = editor.TargetDrive.TestCommand.ExecuteAsync(null);
        editor.TargetDrive.Path = "b";
        _fixture.TestGate.TrySetResult();
        await test;

        editor.TargetDrive.TestMessage.Should().BeNull("it belongs to the path before the change");
        editor.TargetDrive.IsTesting.Should().BeFalse();
    }

    [Fact]
    public void A_scheduled_run_of_a_saved_Drive_plan_that_no_longer_validates_is_skipped_and_logged()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team");
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Docs",
            Enabled = true,
            Source = StorageLocation.FileSystem(SourceFolder),
            Target = Drive("backups", "d1"),
        });
        var vm = _fixture.Create();
        vm.Plans.Single().Errors.Should().BeEmpty();

        // The connection now points at a folder in someone's My Drive: a service account cannot write there.
        _fixture.Drive.Save(new DriveConnectionInfo("d1", "Team", MainViewModelFixture.DriveAccountId, "folder", "Folder", null));
        vm.RunScheduled("p1", RunTrigger.Scheduled);

        _fixture.Queue.IsBusy.Should().BeFalse("nothing is queued");
        var entry = new RunLog(_fixture.Paths.LogFileFor("p1")).ReadLast()!;
        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Contain("Drive targets must be on a shared drive.");
        vm.StatusMessage.Should().Contain("Docs").And.Contain("Drive targets must be on a shared drive.");
    }

    [Fact]
    public void A_saved_Drive_target_opens_through_the_apps_storage_factory_on_its_connection()
    {
        _fixture.AddDriveConnection("d1", "Team", "team", "team", rootName: "Team Backups");
        using var factory = new DriveStorageFactory(new StorageFactory(), _fixture.Drive.Resolve, _ => _fixture.DriveApi);   // as App composes it

        var location = Drive("docs", "d1");

        factory.Open(location).Should().NotBeNull("the location opens on the saved connection (no request is sent)");
        _fixture.Drive.Describe(location).Should().Be("Team · gdrive://Team Backups/docs");
        _fixture.Drive.Describe(Drive("docs", "gone")).Should().Be("Drive connection not found · docs");
        _fixture.Drive.Resolve("d1")!.ClientEmail.Should().Be(MainViewModelFixture.DriveEmail);
    }
}
