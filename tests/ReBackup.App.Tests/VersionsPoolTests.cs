using System.IO;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Core.Versions;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests;

/// <summary>
/// The Versions tab of an incremental plan: its versions live in a pool, so nothing is opened in place — files and
/// folders are restored into a temporary folder first — and the list shows each version's exclusive size and the
/// versions whose manifest is damaged.
/// </summary>
public class VersionsPoolTests : IDisposable
{
    private const string First = "2026_09_29-14_05 Projects";
    private const string Second = "2026_09_30-14_05 Projects";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly MainViewModelFixture _fixture = new();
    private readonly InMemoryStorage _target = new();
    private readonly RecordingTempOpener _temp = new();
    private readonly FakeFolderOpener _files = new();
    private readonly BackupPlan _poolPlan;

    public VersionsPoolTests()
    {
        _poolPlan = new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = StorageLocation.FileSystem(Path.Combine(_fixture.Root, "source")),
            Target = new StorageLocation("s3", "", "conn-1"),
            Incremental = true,
        };
        PoolTargets.WriteStore(_target, "p1");
    }

    public void Dispose() => _fixture.Dispose();

    private VersionsViewModel Create(BackupPlan plan)
    {
        var storages = new LambdaStorageFactory(location => location.Kind == "s3" ? _target : new StorageFactory().Open(location));
        var worker = new VersionIndexWorker(new VersionIndexSet(Path.Combine(_fixture.Root, "index")));
        var context = new VersionsContext(worker, _fixture.Dialogs, _ => { }, storages, MachineName: "THIS-PC",
            TempOpener: _temp);
        return new VersionsViewModel(() => plan, () => false, _files, context);
    }

    /// <summary>Lists the pool, selects <paramref name="version"/> and the tree's top-level entry <paramref name="name"/>.</summary>
    private async Task<VersionsViewModel> SelectAsync(BackupPlan plan, string version, string name)
    {
        var vm = Create(plan);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedVersion = vm.VersionRows.Single(r => r.Name == version);
        vm.Tree.SelectedNode = await WaitForAsync(() => vm.Tree.Rows.FirstOrDefault(r => !r.IsMessage && r.Name == name));
        return vm;
    }

    [Fact]
    public async Task Pool_plan_hides_explorer_actions()
    {
        PoolTargets.WriteVersion(_target, First, "p1", "Projects", new PoolFile("x.txt", "alpha"));

        var vm = await SelectAsync(_poolPlan, First, "x.txt");

        vm.ShowExplorerActions.Should().BeFalse();
        vm.ShowTempRestore.Should().BeTrue();
        vm.ShowSelectedInExplorerCommand.CanExecute(null).Should().BeFalse();
        vm.OpenSelectedCommand.CanExecute(null).Should().BeTrue("a file is opened through a temporary copy");
        var history = await WaitForAsync(() => vm.HistoryRows.FirstOrDefault());
        history.CanOpen.Should().BeFalse("a pool version's folder holds no files to show");
    }

    [Fact]
    public async Task Open_on_pool_plan_uses_temp_opener()
    {
        PoolTargets.WriteVersion(_target, First, "p1", "Projects", new PoolFile("x.txt", "alpha"));
        var vm = await SelectAsync(_poolPlan, First, "x.txt");

        await vm.OpenSelectedCommand.ExecuteAsync(null);

        var (versions, version, path) = _temp.Files.Should().ContainSingle().Subject;
        versions.Should().BeOfType<PoolVersionView>().Which.VersionName.Should().Be(First);
        version.Should().Be(First);
        path.Should().Be("x.txt");
        _files.OpenedFiles.Should().BeEmpty();
        _files.Shown.Should().BeEmpty();
        vm.IsRestoring.Should().BeFalse();
    }

    [Fact]
    public async Task Open_on_full_fs_plan_opens_in_place()
    {
        var target = Path.Combine(_fixture.Root, "target");
        Directory.CreateDirectory(Path.Combine(target, Second));
        File.WriteAllText(Path.Combine(target, Second, "a.txt"), "alpha");
        var plan = new BackupPlan
        {
            Id = "p2",
            Name = "Projects",
            Source = StorageLocation.FileSystem(Path.Combine(_fixture.Root, "source")),
            Target = StorageLocation.FileSystem(target),
        };
        var vm = await SelectAsync(plan, Second, "a.txt");

        vm.ShowExplorerActions.Should().BeTrue();
        vm.ShowTempRestore.Should().BeFalse();
        await vm.OpenSelectedCommand.ExecuteAsync(null);

        _files.OpenedFiles.Should().Equal((Path.Combine(target, Second), "a.txt"));
        _temp.Files.Should().BeEmpty();
        vm.VersionRows.Single().ExclusiveSizeText.Should().BeEmpty("only pool plans show an exclusive size");
    }

    [Fact]
    public async Task Open_on_full_plan_with_a_storage_target_uses_temp_opener()
    {
        await _target.DeleteAsync([PoolLayout.StoreFile], CancellationToken.None);   // a full plan's target holds no pool
        _target.AddFile($"{Second}/a.txt", "alpha"u8.ToArray());
        var plan = new BackupPlan
        {
            Id = "p2",
            Name = "Projects",
            Source = StorageLocation.FileSystem(Path.Combine(_fixture.Root, "source")),
            Target = new StorageLocation("s3", "", "conn-1"),
        };
        var vm = await SelectAsync(plan, Second, "a.txt");

        await vm.OpenSelectedCommand.ExecuteAsync(null);

        var (versions, version, path) = _temp.Files.Should().ContainSingle().Subject;
        versions.Should().BeSameAs(_target, "a full plan's versions are read from the target itself");
        (version, path).Should().Be((Second, "a.txt"));
        _files.OpenedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_reads_the_pool_version_through_its_manifest()
    {
        var source = Path.Combine(_fixture.Root, "source");
        PoolTargets.WriteVersion(_target, First, "p1", "Projects", source, new PoolFile("docs/a.txt", "alpha"));
        var vm = await SelectAsync(_poolPlan, First, "docs");

        await vm.RestoreToOriginalCommand.ExecuteAsync(null);

        _fixture.Dialogs.Errors.Should().BeEmpty();
        File.ReadAllText(Path.Combine(source, "docs", "a.txt")).Should().Be("alpha");
    }

    [Fact]
    public async Task Folder_above_1GB_asks_before_temp_restore()
    {
        PoolTargets.WriteVersion(_target, First, "p1", "Projects",
            new PoolFile("big/video.bin", "huge", Size: 1_073_741_825), new PoolFile("small/note.txt", "tiny"));
        var vm = await SelectAsync(_poolPlan, First, "big");
        vm.RestoreSelectedToTempCommand.CanExecute(null).Should().BeTrue();

        _fixture.Dialogs.ConfirmAnswer = false;
        await vm.RestoreSelectedToTempCommand.ExecuteAsync(null);

        _fixture.Dialogs.Confirms.Should().ContainSingle().Which.Should().Contain(Formats.Bytes(1_073_741_825));
        _temp.Folders.Should().BeEmpty("the restore was not confirmed");

        _fixture.Dialogs.ConfirmAnswer = true;
        await vm.RestoreSelectedToTempCommand.ExecuteAsync(null);
        _temp.Folders.Select(f => (f.Version, f.Path)).Should().Equal((First, "big"));

        vm.Tree.SelectedNode = vm.Tree.Rows.Single(r => r.Name == "small");
        await vm.RestoreSelectedToTempCommand.ExecuteAsync(null);
        _fixture.Dialogs.Confirms.Should().HaveCount(2, "a folder up to 1 GB is restored without asking");
        _temp.Folders.Select(f => (f.Version, f.Path)).Should().Equal((First, "big"), (First, "small"));
    }

    [Fact]
    public async Task Whole_version_is_restored_to_temp()
    {
        PoolTargets.WriteVersion(_target, First, "p1", "Projects", new PoolFile("x.txt", "alpha"));
        var vm = Create(_poolPlan);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedVersion = vm.VersionRows.Single();

        await vm.RestoreVersionToTempCommand.ExecuteAsync(null);

        _temp.Folders.Select(f => (f.Version, f.Path)).Should().Equal((First, ""));
        _fixture.Dialogs.Confirms.Should().BeEmpty();
    }

    [Fact]
    public async Task Damaged_version_row_is_flagged()
    {
        PoolTargets.WriteVersion(_target, First, "p1", "Projects", new PoolFile("x.txt", "alpha"));
        _target.AddFile($"{Second}/re-manifest.json", "{ not json"u8.ToArray());
        var vm = Create(_poolPlan);

        await vm.RefreshCommand.ExecuteAsync(null);

        var damaged = vm.VersionRows.Single(r => r.Name == Second);
        var intact = vm.VersionRows.Single(r => r.Name == First);
        damaged.IsDamaged.Should().BeTrue();
        damaged.DamagedToolTip.Should().NotBeEmpty();
        intact.IsDamaged.Should().BeFalse();
        intact.DamagedToolTip.Should().BeEmpty();

        vm.SelectedVersion = damaged;
        vm.RestoreVersionToTempCommand.CanExecute(null).Should().BeFalse("a damaged version cannot be restored");
        await WaitForAsync(() => vm.Tree.Rows.FirstOrDefault(r => r.IsMessage && r.Name.Contains("damaged")));
        vm.SelectedVersion = intact;
        vm.RestoreVersionToTempCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Exclusive_size_shown_for_pool_plans()
    {
        PoolTargets.WriteVersion(_target, First, "p1", "Projects",
            new PoolFile("shared.txt", "shared content"), new PoolFile("old.txt", "12345678"));
        PoolTargets.WriteVersion(_target, Second, "p1", "Projects",
            new PoolFile("shared.txt", "shared content"), new PoolFile("new.txt", "1234567"));
        var vm = Create(_poolPlan);

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.VersionRows.Single(r => r.Name == First).ExclusiveBytes.Should().Be(8);
        vm.VersionRows.Single(r => r.Name == Second).ExclusiveBytes.Should().Be(7);
        vm.VersionRows.Single(r => r.Name == First).ExclusiveSizeText.Should().Contain(Formats.Bytes(8));
    }

    [Fact]
    public async Task Deleting_plan_deletes_its_index()
    {
        _fixture.AddPlan("p1", "Projects");
        var indexes = Path.Combine(_fixture.Root, "index");
        Directory.CreateDirectory(indexes);
        File.WriteAllText(Path.Combine(indexes, "p1.db"), "");
        File.WriteAllText(Path.Combine(indexes, "other.db"), "");
        var vm = _fixture.Create();
        vm.SelectedPlan = vm.Plans.Single(p => p.Id == "p1");

        vm.DeletePlanCommand.Execute(null);

        await WaitForAsync(() => File.Exists(Path.Combine(indexes, "p1.db")) ? null : "gone");
        File.Exists(Path.Combine(indexes, "other.db")).Should().BeTrue("only the deleted plan's index goes");
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe) where T : class
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } found)
                return found;
            await Task.Delay(20);
        }
        throw new TimeoutException("The condition was not met in time.");
    }

    private sealed class LambdaStorageFactory(Func<StorageLocation, IStorage> open) : IStorageFactory
    {
        public IStorage Open(StorageLocation location) => open(location);
    }
}
