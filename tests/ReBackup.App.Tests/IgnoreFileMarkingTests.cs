using System.IO;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;
using ReBackup.Core.Versions;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests;

/// <summary>
/// A <c>.backupignore</c> and the folder holding one are marked in every overview (the Ignore tab, the Versions tab's
/// tree, search hits and file history), so that a collapsed tree still shows where they are.
/// </summary>
public class IgnoreFileMarkingTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly MainViewModelFixture _fixture = new();   // the labels the view model's texts need
    private readonly InMemoryStorage _source = new(StorageCapabilities.None);

    public void Dispose() => _fixture.Dispose();

    private IgnorePreviewViewModel CreateIgnore()
    {
        var location = new StorageLocation("mem", "", "conn-1");
        return new IgnorePreviewViewModel(() => location, () => new IgnoreSettings(), () => [], new Factory(_ => _source));
    }

    [Fact]
    public async Task Preview_row_of_a_backupignore_is_marked()
    {
        _source.AddFile(".backupignore", new byte[3]);
        _source.AddFile("sub/.BackupIgnore", new byte[3]);
        _source.AddFile("sub/a.txt", new byte[3]);
        _source.AddFile("b.txt", new byte[3]);
        var vm = CreateIgnore();
        await vm.IndexCommand.ExecuteAsync(null);

        vm.Tree.Rows.Single(r => r.Name == ".backupignore").IsIgnoreFile.Should().BeTrue();
        vm.Tree.Rows.Single(r => r.Name == "b.txt").IsIgnoreFile.Should().BeFalse();
        vm.Tree.Rows.Single(r => r.Name == "sub").IsIgnoreFile.Should().BeFalse("a folder is not an ignore file");
        vm.Tree.Rows.Single(r => r.Name == "sub").IsExpanded = true;
        vm.Tree.Rows.Single(r => r.Name == ".BackupIgnore").IsIgnoreFile.Should().BeTrue();
    }

    [Fact]
    public async Task Folder_with_a_backupignore_is_marked_while_scanning_and_after()
    {
        _source.AddFile("sub/.backupignore", new byte[3]);
        _source.AddFile("sub/a.txt", new byte[3]);
        _source.AddFile("plain/a.txt", new byte[3]);
        _source.AddFile("deep/inner/.backupignore", new byte[3]);

        // While scanning: the tree takes the snapshot of the running scan.
        var scan = LiveScan.Start(_source, "src", new IgnoreSettings(), []);
        await scan.Completion;
        scan.IgnoreFileDirectories.Should().BeEquivalentTo(["sub", "deep/inner"]);
        var tree = new PreviewTreeViewModel();
        tree.SetRoot(scan.Root);
        tree.Rows.Single(r => r.Name == "sub").ContainsIgnoreFile.Should().BeFalse("the tree does not know the folders yet");
        tree.UpdateLiveIgnoreFileDirectories(scan.IgnoreFileDirectories).Should().BeTrue();
        tree.Refresh();
        tree.Rows.Single(r => r.Name == "sub").ContainsIgnoreFile.Should().BeTrue();
        tree.Rows.Single(r => r.Name == "plain").ContainsIgnoreFile.Should().BeFalse();
        tree.Rows.Single(r => r.Name == "deep").ContainsIgnoreFile.Should().BeFalse("only the folder that holds the file directly");

        // After: the finished index.
        var vm = CreateIgnore();
        await vm.IndexCommand.ExecuteAsync(null);
        vm.Tree.Rows.Single(r => r.Name == "sub").ContainsIgnoreFile.Should().BeTrue();
        vm.Tree.Rows.Single(r => r.Name == "plain").ContainsIgnoreFile.Should().BeFalse();
        vm.Tree.Rows.Single(r => r.Name == "deep").ContainsIgnoreFile.Should().BeFalse();
        vm.Tree.Rows.First().ContainsIgnoreFile.Should().BeFalse("the source root holds none");
    }

    [Fact]
    public async Task Folder_flag_follows_the_folders_and_an_unchanged_live_snapshot_is_kept()
    {
        _source.AddFile("sub/.backupignore", new byte[3]);
        _source.AddFile("other/a.txt", new byte[3]);
        var scan = LiveScan.Start(_source, "src", new IgnoreSettings(), []);
        await scan.Completion;
        var tree = new PreviewTreeViewModel();
        tree.SetRoot(scan.Root);
        var sub = tree.Rows.Single(r => r.Name == "sub");
        var other = tree.Rows.Single(r => r.Name == "other");

        tree.IgnoreFileDirectories = new HashSet<string>(["other"], StringComparer.OrdinalIgnoreCase);
        (sub.ContainsIgnoreFile, other.ContainsIgnoreFile).Should().Be((false, true));
        tree.IgnoreFileDirectories = new HashSet<string>(["sub"], StringComparer.OrdinalIgnoreCase);
        (sub.ContainsIgnoreFile, other.ContainsIgnoreFile).Should().Be((true, false),
            "a new set is read again, not the cached flag");

        // The same number of folders as before: nothing new, the set and its generation stay.
        var current = tree.IgnoreFileDirectories;
        var generation = tree.IgnoreFileGeneration;
        tree.UpdateLiveIgnoreFileDirectories(["sub"]).Should().BeFalse();
        tree.IgnoreFileDirectories.Should().BeSameAs(current);
        tree.IgnoreFileGeneration.Should().Be(generation);
        tree.UpdateLiveIgnoreFileDirectories(["sub", "other"]).Should().BeTrue();
        other.ContainsIgnoreFile.Should().BeTrue();
    }

    [Fact]
    public async Task Version_tree_marks_file_and_folder()
    {
        var target = Path.Combine(_fixture.Root, "target");
        Write(target, "a.txt", "alpha");
        Write(target, "sub/.backupignore", "*.tmp");
        Write(target, "sub/x.txt", "x");
        Write(target, "plain/y.txt", "y");
        var plan = new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = StorageLocation.FileSystem(Path.Combine(_fixture.Root, "source")),
            Target = StorageLocation.FileSystem(target),
        };
        var worker = new VersionIndexWorker(new VersionIndexSet(Path.Combine(_fixture.Root, "index")));
        var context = new VersionsContext(worker, _fixture.Dialogs, _ => { }, new StorageFactory(), MachineName: "THIS-PC",
            TempOpener: new RecordingTempOpener());
        var vm = new VersionsViewModel(() => plan, () => false, new FakeFolderOpener(), context);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedVersion = vm.VersionRows.Single();

        var sub = await WaitForAsync(() => vm.Tree.Rows.FirstOrDefault(r => !r.IsMessage && r.Name == "sub"));
        sub.ContainsIgnoreFile.Should().BeTrue();
        sub.IsIgnoreFile.Should().BeFalse();
        vm.Tree.Rows.Single(r => r.Name == "plain").ContainsIgnoreFile.Should().BeFalse();
        vm.Tree.Rows.Single(r => r.Name == "a.txt").IsIgnoreFile.Should().BeFalse();
        sub.IsExpanded = true;
        var file = await WaitForAsync(() => vm.Tree.Rows.FirstOrDefault(r => !r.IsMessage && r.Name == ".backupignore"));
        file.IsIgnoreFile.Should().BeTrue();
        file.ContainsIgnoreFile.Should().BeFalse();
    }

    [Fact]
    public void Search_and_history_rows_mark_the_file()
    {
        new SearchHitRow(new SearchHit(1, "a/.backupignore", ".backupignore", false, 3)).IsIgnoreFile.Should().BeTrue();
        new SearchHitRow(new SearchHit(2, "a/.BACKUPIGNORE", ".BACKUPIGNORE", false, 3)).IsIgnoreFile.Should().BeTrue();
        new SearchHitRow(new SearchHit(3, "a/b.txt", "b.txt", false, 3)).IsIgnoreFile.Should().BeFalse();
        new SearchHitRow(new SearchHit(4, "x/.backupignore", ".backupignore", true, 3)).IsIgnoreFile
            .Should().BeFalse("a folder of that name is not an ignore file");

        var version = new IndexedVersion(1, "v", DateTime.UtcNow, VersionOwnership.Owned, null, IndexOrigin.Manifest, 1, 1);
        var entry = new HistoryEntry(version, true, 3, null, HistoryStatus.New);
        new FileHistoryRow(entry, null, "a/.backupignore").IsIgnoreFile.Should().BeTrue();
        new FileHistoryRow(entry, null, "a/b.txt").IsIgnoreFile.Should().BeFalse();
    }

    private static void Write(string target, string path, string content)
    {
        var file = Path.Combine(target, "2026_09_29-14_05 Projects", path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
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

    private sealed class Factory(Func<StorageLocation, IStorage> open) : IStorageFactory
    {
        public IStorage Open(StorageLocation location) => open(location);
    }
}
