using System.IO;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Plans;
using ReBackup.Storage;

namespace ReBackup.App.Tests;

/// <summary>The Plan tab's "Storage format" (Full copies | Incremental) and "Compare files" (Fast | Safe) settings.</summary>
public class PlanEditorPoolTests : IDisposable
{
    private readonly MainViewModelFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private PlanEditorViewModel SavedPlan(bool incremental = false, ChangeDetection detection = ChangeDetection.Fast)
    {
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Docs",
            Source = StorageLocation.FileSystem(Directory.CreateDirectory(Path.Combine(_fixture.Root, "src")).FullName),
            Target = StorageLocation.FileSystem(Path.Combine(_fixture.Root, "dst")),
            Incremental = incremental,
            ChangeDetection = detection,
        });
        return _fixture.Create().Plans.Single();
    }

    [Fact]
    public void Incremental_is_editable_only_for_new_plans()
    {
        var editor = SavedPlan();

        editor.CanEditIncremental.Should().BeFalse();
        editor.ChooseStorageFormatCommand.Execute("Incremental");
        editor.Incremental.Should().BeFalse();

        editor.MarkAsNew();
        editor.CanEditIncremental.Should().BeTrue();
        editor.ChooseStorageFormatCommand.Execute("Incremental");
        editor.Incremental.Should().BeTrue();
        editor.IsDirty.Should().BeTrue();
        editor.ToPlan().Incremental.Should().BeTrue();
        editor.ChooseStorageFormatCommand.Execute("Full");
        editor.Incremental.Should().BeFalse();
    }

    [Fact]
    public void Saved_plan_keeps_incremental_after_reload()
    {
        var editor = SavedPlan();
        editor.MarkAsNew();
        editor.ChooseStorageFormatCommand.Execute("Incremental");

        editor.TrySave(_fixture.Store).Should().BeTrue();

        editor.CanEditIncremental.Should().BeFalse();
        _fixture.Store.TryLoad("p1")!.Incremental.Should().BeTrue();
        editor.ChooseStorageFormatCommand.Execute("Full");
        editor.Incremental.Should().BeTrue();
        editor.Revert();
        editor.Incremental.Should().BeTrue();
        SavedPlan(incremental: true).Incremental.Should().BeTrue();
    }

    [Fact]
    public void ChangeDetection_round_trips()
    {
        var editor = SavedPlan(incremental: true);
        editor.ChangeDetection.Should().Be(ChangeDetection.Fast);

        editor.ChooseChangeDetectionCommand.Execute("Safe");
        editor.ChangeDetection.Should().Be(ChangeDetection.Safe);
        editor.ChangeDetectionIsSafe.Should().BeTrue();
        editor.IsDirty.Should().BeTrue();
        editor.TrySave(_fixture.Store).Should().BeTrue();
        _fixture.Store.TryLoad("p1")!.ChangeDetection.Should().Be(ChangeDetection.Safe);

        editor.ChooseChangeDetectionCommand.Execute("Fast");
        editor.Revert();
        editor.ChangeDetection.Should().Be(ChangeDetection.Safe);
        SavedPlan(incremental: true, detection: ChangeDetection.Safe).ChangeDetection.Should().Be(ChangeDetection.Safe);
    }

    [Fact]
    public void ChangeDetection_hidden_for_full_plans()
    {
        SavedPlan().ShowChangeDetection.Should().BeFalse();

        var editor = SavedPlan(incremental: true);
        editor.ShowChangeDetection.Should().BeTrue();
        editor.MarkAsNew();
        editor.ChooseStorageFormatCommand.Execute("Full");
        editor.ShowChangeDetection.Should().BeFalse();
    }

    [Fact]
    public void Setting_Incremental_directly_on_a_saved_plan_is_not_persisted()
    {
        var editor = SavedPlan();

        editor.Incremental = true;

        editor.ToPlan().Incremental.Should().BeFalse();
        editor.TrySave(_fixture.Store).Should().BeTrue();
        _fixture.Store.TryLoad("p1")!.Incremental.Should().BeFalse();
    }
}
