using System.IO;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Versions;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;

namespace ReBackup.App.Tests;

/// <summary>
/// The Versions tab lists a folder another computer is writing into the plan's target, and takes it over on request.
/// </summary>
public class VersionsForeignFolderTests : IDisposable
{
    private const string Folder = "2026_09_30-15_00 Projects";
    private static readonly DateTime Started = new(2026, 9, 30, 13, 0, 0, DateTimeKind.Utc);
    private readonly MainViewModelFixture _fixture = new();
    private readonly string _target;
    private readonly BackupPlan _plan;

    public VersionsForeignFolderTests()
    {
        _target = Path.Combine(_fixture.Root, "target");
        Directory.CreateDirectory(Path.Combine(_target, Folder, "sub"));
        File.WriteAllText(Path.Combine(_target, Folder, "sub", "a.txt"), "alpha");
        _plan = new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = StorageLocation.FileSystem(Path.Combine(_fixture.Root, "source")),
            Target = StorageLocation.FileSystem(_target),
        };
    }

    public void Dispose() => _fixture.Dispose();

    private VersionsViewModel Create()
    {
        var worker = new VersionIndexWorker(new VersionIndexSet(Path.Combine(_fixture.Root, "index")));
        var context = new VersionsContext(worker, _fixture.Dialogs, _ => { }, new StorageFactory(), MachineName: "THIS-PC");
        return new VersionsViewModel(() => _plan, () => false, new FakeFolderOpener(), context);
    }

    [Fact]
    public async Task A_folder_another_computer_writes_is_listed_and_can_be_taken_over()
    {
        await VersionMarkers.WritePendingAsync(new FileSystemStorage(_target), Folder,
            new MarkerInfo(1, "p1", "Projects", Started, "OTHER-PC"), CancellationToken.None);
        var vm = Create();

        await vm.RefreshCommand.ExecuteAsync(null);

        var row = vm.ForeignRows.Should().ContainSingle().Subject;
        row.Text.Should().Be($"Being written by OTHER-PC since {Formats.DateAndTime(Started.ToLocalTime())}");
        vm.VersionRows.Should().BeEmpty("a folder in transit is no version");
        vm.TakeOverCommand.CanExecute(null).Should().BeFalse("nothing is selected");

        vm.SelectedForeign = row;
        await vm.TakeOverCommand.ExecuteAsync(null);

        Directory.Exists(Path.Combine(_target, Folder)).Should().BeFalse();
        vm.ForeignRows.Should().BeEmpty();
        _fixture.Dialogs.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_is_taken_over_without_the_confirmation()
    {
        await VersionMarkers.WriteDeletingAsync(new FileSystemStorage(_target), Folder,
            new MarkerInfo(1, "p1", "Projects", Started, "OTHER-PC"), CancellationToken.None);
        var vm = Create();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.ForeignRows.Should().ContainSingle().Which.Text.Should().StartWith("Being deleted by OTHER-PC since ");
        _fixture.Dialogs.ConfirmAnswer = false;

        vm.SelectedForeign = vm.ForeignRows[0];
        await vm.TakeOverCommand.ExecuteAsync(null);

        File.Exists(Path.Combine(_target, Folder, "sub", "a.txt")).Should().BeTrue();
        vm.ForeignRows.Should().ContainSingle();
    }
}
