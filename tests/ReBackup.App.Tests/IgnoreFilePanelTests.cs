using System.Text;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Plans;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests;

/// <summary>The Ignore tab's .backupignore chip (a flat list of the files) and the detail panel with hits per line.</summary>
public class IgnoreFilePanelTests : IDisposable
{
    private readonly MainViewModelFixture _fixture = new();   // the labels the view model's texts need
    private readonly InMemoryStorage _source = new(StorageCapabilities.None);
    private readonly List<string> _opened = [];
    private IgnoreSettings _settings = new();

    public void Dispose() => _fixture.Dispose();

    private bool _shellWorks = true;
    private bool _notepadWorks = true;
    private readonly List<string> _notepad = [];

    private IgnorePreviewViewModel Create(StorageLocation? location = null, IStorage? storage = null)
    {
        location ??= StorageLocation.FileSystem(@"C:\src");
        return new IgnorePreviewViewModel(() => location, () => _settings, () => [], new Factory(_ => storage ?? _source),
            openFile: path =>
            {
                _opened.Add(path);
                return _shellWorks;
            },
            openInNotepad: path =>
            {
                _notepad.Add(path);
                return _notepadWorks;
            },
            dialogs: _fixture.Dialogs);
    }

    private void AddIgnore(string path, string text) => _source.AddFile(path, Encoding.UTF8.GetBytes(text));

    private async Task<IgnorePreviewViewModel> ScannedAsync(StorageLocation? location = null)
    {
        var vm = Create(location);
        await vm.IndexCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task Chip_counts_and_lists_only_backupignore_files()
    {
        AddIgnore("sub/.backupignore", "*.tmp\n");
        AddIgnore(".BackupIgnore", "*.log\n");
        _source.AddFile("sub/a.txt", new byte[3]);
        _source.AddFile("ignore.txt", new byte[3]);
        var vm = await ScannedAsync();

        vm.IgnoreFileCount.Should().Be(2);
        vm.IgnoreFilesChipText.Should().Be(Loc.F("ignore.files.chip", ("count", 2)));
        vm.ShowIgnoreFilesOnly = true;
        vm.IgnoreFileItems.Select(i => i.Path).Should().Equal(".BackupIgnore", "sub/.backupignore");
        vm.CompletedIndex.Should().NotBeNull();
    }

    [Fact]
    public async Task Selecting_a_backupignore_fills_the_panel_with_hits()
    {
        AddIgnore("sub/.backupignore", "# temp files\n*.tmp\n\nbig/\n");
        _source.AddFile("sub/a.tmp", new byte[100]);
        _source.AddFile("sub/b.tmp", new byte[50]);
        _source.AddFile("sub/big/c.txt", new byte[1000]);
        _source.AddFile("sub/keep.txt", new byte[7]);
        var vm = await ScannedAsync();

        vm.ShowIgnoreFilesOnly = true;
        vm.SelectedIgnoreFileItem = vm.IgnoreFileItems.Single();

        var detail = vm.SelectedIgnoreFile!;
        detail.Directory.Should().Be("sub");
        detail.Path.Should().Be("sub/.backupignore");
        detail.Lines.Select(l => l.Number).Should().Equal(1, 2, 3, 4);
        detail.Lines.Select(l => l.Text).Should().Equal("# temp files", "*.tmp", "", "big/");
        detail.Lines[0].Entries.Should().BeNull("a comment decides nothing");
        detail.Lines[2].Entries.Should().BeNull("a blank line decides nothing");
        detail.Lines[1].Entries.Should().Be(2);
        detail.Lines[1].BytesText.Should().NotBeNullOrEmpty();
        detail.Lines[3].Entries.Should().Be(1, "the folder counts for its line; its content is ignored by it");
    }

    [Fact]
    public async Task Selecting_the_row_in_the_tree_fills_the_panel_too()
    {
        AddIgnore(".backupignore", "*.tmp\n");
        _source.AddFile("a.tmp", new byte[10]);
        var vm = await ScannedAsync();

        vm.Tree.SelectedRow = vm.Tree.Rows.Single(r => r.Name == ".backupignore");

        vm.SelectedIgnoreFile!.Path.Should().Be(".backupignore");
        vm.SelectedIgnoreFile.Lines.Single().Entries.Should().Be(1);
    }

    [Fact]
    public async Task Unused_lines_are_flagged()
    {
        AddIgnore(".backupignore", "*.tmp\n*.nothing\n");
        _source.AddFile("a.tmp", new byte[10]);
        var vm = await ScannedAsync();
        vm.Tree.SelectedRow = vm.Tree.Rows.Single(r => r.Name == ".backupignore");

        var lines = vm.SelectedIgnoreFile!.Lines;
        lines[0].IsUnused.Should().BeFalse();
        lines[1].IsUnused.Should().BeTrue();
        lines[1].Entries.Should().Be(0);
        lines[1].HitsText.Should().Be(Loc.T("ignore.files.unused"));
    }

    [Fact]
    public async Task Panel_closes_when_selection_moves_away()
    {
        AddIgnore(".backupignore", "*.tmp\n");
        _source.AddFile("a.txt", new byte[10]);
        var vm = await ScannedAsync();
        vm.Tree.SelectedRow = vm.Tree.Rows.Single(r => r.Name == ".backupignore");
        vm.SelectedIgnoreFile.Should().NotBeNull();

        vm.Tree.SelectedRow = vm.Tree.Rows.Single(r => r.Name == "a.txt");
        vm.SelectedIgnoreFile.Should().BeNull();

        vm.Tree.SelectedRow = vm.Tree.Rows.Single(r => r.Name == ".backupignore");
        vm.Tree.SelectedRow = null;
        vm.SelectedIgnoreFile.Should().BeNull();
    }

    [Fact]
    public async Task Panel_closes_and_the_index_is_gone_after_release()
    {
        AddIgnore(".backupignore", "*.tmp\n");
        var vm = await ScannedAsync();
        vm.Tree.SelectedRow = vm.Tree.Rows.Single(r => r.Name == ".backupignore");
        vm.SelectedIgnoreFile.Should().NotBeNull();

        vm.Release();

        vm.CompletedIndex.Should().BeNull();
        vm.SelectedIgnoreFile.Should().BeNull();
        vm.IgnoreFileCount.Should().Be(0);
        vm.IgnoreFileItems.Should().BeEmpty();
    }

    [Fact]
    public async Task Hits_follow_a_pattern_edit_without_a_rescan()
    {
        AddIgnore(".backupignore", "*.tmp\n");
        _source.AddFile("a.tmp", new byte[10]);
        var vm = await ScannedAsync();
        vm.Tree.SelectedRow = vm.Tree.Rows.Single(r => r.Name == ".backupignore");
        vm.SelectedIgnoreFile!.Lines.Single().Entries.Should().Be(1);

        // The plan stops honoring nested files: the file's line decides nothing any more.
        _settings = new IgnoreSettings { HonorNestedFiles = false };
        vm.RequestReevaluate();
        await WaitUntilAsync(() => vm.SelectedIgnoreFile?.Lines.Single().Entries == 0);
    }

    [Fact]
    public async Task Open_in_editor_opens_the_absolute_path_for_folder_sources()
    {
        AddIgnore("sub/.backupignore", "*.tmp\n");
        var vm = await ScannedAsync(StorageLocation.FileSystem(@"C:\src"));
        vm.ShowIgnoreFilesOnly = true;
        vm.SelectedIgnoreFileItem = vm.IgnoreFileItems.Single();

        vm.OpenIgnoreFileInEditorCommand.CanExecute(null).Should().BeTrue();
        vm.OpenIgnoreFileInEditorCommand.Execute(null);

        _opened.Should().Equal(@"C:\src\sub\.backupignore");
    }

    [Fact]
    public async Task Open_in_editor_is_disabled_for_s3_sources()
    {
        AddIgnore(".backupignore", "*.tmp\n");
        var vm = await ScannedAsync(new StorageLocation("mem", "", "conn-1"));
        vm.Tree.SelectedRow = vm.Tree.Rows.Single(r => r.Name == ".backupignore");

        vm.SelectedIgnoreFile.Should().NotBeNull("the panel works for every source");
        vm.OpenIgnoreFileInEditorCommand.CanExecute(null).Should().BeFalse();
        vm.OpenIgnoreFileInEditorCommand.Execute(null);
        _opened.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rescan_starts_without_the_previous_scans_folder_badges()
    {
        AddIgnore("old/.backupignore", "*.tmp\n");
        var gated = new GatedStorage(_source);
        var vm = Create(storage: gated);
        await vm.IndexCommand.ExecuteAsync(null);
        vm.Tree.IgnoreFileDirectories.Should().Contain("old");

        // The folder is gone and the next scan stops in the middle of reading another file.
        await _source.DeleteAsync(["old/.backupignore"], CancellationToken.None);
        AddIgnore("z/.backupignore", "*.log\n");
        gated.Holds = path => path == "z/.backupignore";
        var rescan = vm.IndexCommand.ExecuteAsync(null);
        await gated.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));

        vm.IsIndexing.Should().BeTrue();
        vm.Tree.IgnoreFileDirectories.Should().NotContain("old");
        gated.Gate.SetResult();
        await rescan;
        vm.Tree.IgnoreFileDirectories.Should().BeEquivalentTo(["z"]);
    }

    [Fact]
    public async Task Open_in_editor_falls_back_to_notepad_when_the_shell_has_no_program()
    {
        AddIgnore(".backupignore", "*.tmp\n");
        _shellWorks = false;
        var vm = await ScannedAsync();
        vm.Tree.SelectedRow = vm.Tree.Rows.Single(r => r.Name == ".backupignore");

        vm.OpenIgnoreFileInEditorCommand.Execute(null);

        _opened.Should().Equal(@"C:\src\.backupignore");
        _notepad.Should().Equal(@"C:\src\.backupignore");
        _fixture.Dialogs.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Open_in_editor_shows_an_error_when_nothing_opens_the_file()
    {
        AddIgnore(".backupignore", "*.tmp\n");
        _shellWorks = false;
        _notepadWorks = false;
        var vm = await ScannedAsync();
        vm.Tree.SelectedRow = vm.Tree.Rows.Single(r => r.Name == ".backupignore");

        vm.OpenIgnoreFileInEditorCommand.Execute(null);

        _notepad.Should().HaveCount(1);
        _fixture.Dialogs.Errors.Should().ContainSingle().Which.Should().Contain(@"C:\src\.backupignore");
    }

    [Fact]
    public async Task Open_in_editor_does_not_start_notepad_when_the_shell_worked()
    {
        AddIgnore(".backupignore", "*.tmp\n");
        var vm = await ScannedAsync();
        vm.Tree.SelectedRow = vm.Tree.Rows.Single(r => r.Name == ".backupignore");

        vm.OpenIgnoreFileInEditorCommand.Execute(null);

        _notepad.Should().BeEmpty();
    }

    [Fact]
    public async Task The_chosen_file_stays_chosen_when_the_list_view_drops_its_selection_on_a_reset()
    {
        AddIgnore("sub/.backupignore", "*.tmp\n");
        _source.AddFile("sub/a.tmp", new byte[10]);
        var vm = await ScannedAsync();
        vm.ShowIgnoreFilesOnly = true;
        vm.SelectedIgnoreFileItem = vm.IgnoreFileItems.Single();
        // What the two-way bound ListView does when the collection is reset.
        vm.IgnoreFileItems.CollectionChanged += (_, _) => vm.SelectedIgnoreFileItem = null;

        _settings = new IgnoreSettings { HonorNestedFiles = false };
        vm.RequestReevaluate();
        await WaitUntilAsync(() => vm.SelectedIgnoreFile?.Lines.Single().Entries == 0);

        vm.SelectedIgnoreFileItem.Should().NotBeNull("the row is highlighted again");
        vm.SelectedIgnoreFileItem!.Path.Should().Be("sub/.backupignore");
        vm.SelectedIgnoreFile.Should().NotBeNull();
    }

    [Fact]
    public async Task A_language_change_during_a_rescan_does_not_bring_back_the_previous_list()
    {
        AddIgnore("old/.backupignore", "*.tmp\n");
        var gated = new GatedStorage(_source);
        var vm = Create(storage: gated);
        await vm.IndexCommand.ExecuteAsync(null);
        vm.IgnoreFileCount.Should().Be(1);

        gated.Holds = path => path == "old/.backupignore";
        var rescan = vm.IndexCommand.ExecuteAsync(null);
        await gated.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        vm.IgnoreFileCount.Should().Be(0);

        vm.RefreshTexts();

        vm.IgnoreFileCount.Should().Be(0);
        vm.IgnoreFileItems.Should().BeEmpty();
        gated.Gate.SetResult();
        await rescan;
        vm.IgnoreFileCount.Should().Be(1);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(20);
        }
    }

    private sealed class Factory(Func<StorageLocation, IStorage> open) : IStorageFactory
    {
        public IStorage Open(StorageLocation location) => open(location);
    }
}
