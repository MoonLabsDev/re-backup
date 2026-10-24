using System.IO;
using System.Text;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Ignore;
using ReBackup.Core.Plans;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;
using ReBackup.Storage.S3;

namespace ReBackup.App.Tests;

/// <summary>Integrating a <c>.backupignore</c> into the plan and moving plan rules into one, through the confirmation dialog.</summary>
public class RelocationTests : IDisposable
{
    private readonly MainViewModelFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private string SourceFolder => Directory.CreateDirectory(Path.Combine(_fixture.Root, "src")).FullName;

    private string TargetFolder => Path.Combine(_fixture.Root, "dst");

    private string FileIn(string relativePath) => Path.Combine(SourceFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string relativePath, string content = "x")
    {
        var path = FileIn(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A saved folder plan with <paramref name="patterns"/>, opened in the editor.</summary>
    private PlanEditorViewModel Editor(IReadOnlyList<string> patterns, bool honorNested = true)
    {
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Docs",
            Source = StorageLocation.FileSystem(SourceFolder),
            Target = StorageLocation.FileSystem(TargetFolder),
            Ignore = new IgnoreSettings { HonorNestedFiles = honorNested, Patterns = [.. patterns] },
        });
        var editor = _fixture.Create().Plans.Single();
        editor.IsDirty.Should().BeFalse();
        return editor;
    }

    private async Task<PlanEditorViewModel> ScannedAsync(IReadOnlyList<string> patterns, bool honorNested = true)
    {
        var editor = Editor(patterns, honorNested);
        await editor.Preview.IndexCommand.ExecuteAsync(null);
        editor.Preview.CompletedIndex.Should().NotBeNull();
        return editor;
    }

    private static void ChooseIgnoreFile(PlanEditorViewModel editor, string directory)
    {
        editor.Preview.ShowIgnoreFilesOnly = true;
        editor.Preview.SelectedIgnoreFileItem = editor.Preview.IgnoreFileItems.Single(i => i.Directory == directory);
        editor.Preview.SelectedIgnoreFile.Should().NotBeNull();
    }

    private static void ChooseFolder(PlanEditorViewModel editor, string name) =>
        editor.Preview.Tree.SelectedRow = editor.Preview.Tree.Rows.Single(r => r.Name == name);

    private IReadOnlyList<string> SavedPatterns() => _fixture.Store.LoadAll().Plans.Single().Ignore.Patterns;

    [Fact]
    public async Task Integrate_applies_saves_the_plan_and_reevaluates()
    {
        Write("sub/.backupignore", "*.tmp\n");
        Write("sub/a.tmp");
        Write("sub/b.txt");
        var editor = await ScannedAsync(["*.log"]);
        var preview = editor.Preview;
        ChooseIgnoreFile(editor, "sub");
        var rootBefore = preview.Root;
        var includedBefore = preview.LastEvaluatedIncludedSize;
        var scans = 0;
        preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IgnorePreviewViewModel.IsIndexing) && preview.IsIndexing)
                scans++;
        };

        editor.IntegrateIgnoreFileCommand.CanExecute(null).Should().BeTrue();
        editor.IntegrateIgnoreFileCommand.Execute(null);

        var model = _fixture.Dialogs.Relocations.Should().ContainSingle().Subject;
        model.EquivalenceState.Should().Be(EquivalenceState.Identical);
        model.CanApply.Should().BeTrue();
        model.ApplyText.Should().Be(Loc.T("ignore.relocation.apply"));
        editor.CurrentIgnoreSettings().Patterns.Should().Equal(model.Proposal.PlanPatternsAfter);
        editor.CurrentIgnoreSettings().Patterns.Should().Contain("# from sub/.backupignore");
        editor.IsDirty.Should().BeFalse();
        SavedPatterns().Should().Equal(model.Proposal.PlanPatternsAfter);
        _fixture.RecycleBin.Recycled.Should().Equal(FileIn("sub/.backupignore"));
        File.Exists(FileIn("sub/.backupignore")).Should().BeFalse();
        _fixture.Dialogs.Errors.Should().BeEmpty();
        _fixture.Dialogs.Infos.Should().BeEmpty();

        // The cached scan forgets the file and is evaluated again, without scanning.
        await WaitUntilAsync(() => preview.IgnoreFileCount == 0 && preview.Root is not null && !ReferenceEquals(preview.Root, rootBefore));
        scans.Should().Be(0);
        preview.LastEvaluatedIncludedSize.Should().Be(includedBefore, "the scope is unchanged");
    }

    [Fact]
    public async Task Integrate_on_a_drive_without_recycle_bin_says_where_the_file_went()
    {
        Write("sub/.backupignore", "*.tmp\n");
        var editor = await ScannedAsync([]);
        ChooseIgnoreFile(editor, "sub");
        _fixture.RecycleBin.Outcome = RecycleOutcome.Renamed;

        editor.IntegrateIgnoreFileCommand.Execute(null);

        _fixture.Dialogs.Infos.Should().ContainSingle()
            .Which.Should().Be(Loc.F("ignore.relocation.renamed", ("name", ".backupignore.integrated")));
    }

    [Fact]
    public async Task Integrate_from_the_tree_row_of_the_file()
    {
        Write(".backupignore", "*.tmp\n");
        Write("a.tmp");
        var editor = await ScannedAsync([]);
        editor.Preview.Tree.SelectedRow = editor.Preview.Tree.Rows.Single(r => r.Name == ".backupignore");

        editor.IntegrateIgnoreFileCommand.Execute(null);

        editor.CurrentIgnoreSettings().Patterns.Should().StartWith("# from .backupignore");
        File.Exists(FileIn(".backupignore")).Should().BeFalse();
    }

    [Fact]
    public async Task Extract_with_selected_lines_moves_them()
    {
        Write("sub/a.tmp");
        Write("sub/big/c.txt");
        Write("sub/x.txt");
        Write("a.log");
        var editor = await ScannedAsync(["/sub/*.tmp", "*.log", "/sub/big/"]);
        ChooseFolder(editor, "sub");

        editor.ExtractToFolderCommand.CanExecute([0, 2]).Should().BeTrue();
        editor.ExtractToFolderCommand.Execute([0, 2]);

        var model = _fixture.Dialogs.Relocations.Should().ContainSingle().Subject;
        model.EquivalenceState.Should().Be(EquivalenceState.Identical);
        model.Proposal.MovedLines.Should().Equal("/sub/*.tmp", "/sub/big/");
        editor.CurrentIgnoreSettings().Patterns.Should().Equal("*.log");
        SavedPatterns().Should().Equal("*.log");
        editor.IsDirty.Should().BeFalse();
        File.ReadAllText(FileIn("sub/.backupignore")).Should().Be(model.Proposal.FileContentAfter);
        File.ReadAllText(FileIn("sub/.backupignore")).Should().Contain("# from plan");
        _fixture.RecycleBin.Recycled.Should().BeEmpty();
        model.FileDiff.Should().OnlyContain(l => l.Kind == DiffKind.Added);
        model.PatternDiff.Where(l => l.Kind == DiffKind.Removed).Select(l => l.Text).Should().Equal("/sub/*.tmp", "/sub/big/");

        // The new file is part of the cached scan now.
        await WaitUntilAsync(() => editor.Preview.IgnoreFileCount == 1);
    }

    [Fact]
    public async Task Extract_without_selection_preselects_extractable_lines()
    {
        Write("sub/a.tmp");
        var editor = await ScannedAsync(["# comment", "/sub/*.tmp", "*.log", "/other/x"]);
        ChooseFolder(editor, "sub");

        editor.ExtractToFolderCommand.Execute([]);

        var model = _fixture.Dialogs.Relocations.Should().ContainSingle().Subject;
        model.Proposal.MovedLines.Should().Equal("/sub/*.tmp");
        model.Rejected.Should().BeEmpty();
        editor.CurrentIgnoreSettings().Patterns.Should().Equal("# comment", "*.log", "/other/x");
    }

    [Fact]
    public async Task Rejected_lines_are_listed_with_their_reason()
    {
        Write("sub/a.tmp");
        var editor = await ScannedAsync(["/sub/*.tmp", "*.log", "/other/x"]);
        ChooseFolder(editor, "sub");
        _fixture.Dialogs.RelocationAnswer = false;

        editor.ExtractToFolderCommand.Execute([0, 1, 2]);

        var model = _fixture.Dialogs.Relocations.Single();
        model.Rejected.Select(r => (r.Line, r.Reason)).Should().Equal(
            ("*.log", Loc.T("ignore.relocation.rejected.matchesOutsideFolder")),
            ("/other/x", Loc.T("ignore.relocation.rejected.notBelowFolder")));
    }

    [Fact]
    public async Task Commands_are_disabled_while_dirty()
    {
        Write("sub/.backupignore", "*.tmp\n");
        var editor = await ScannedAsync(["/sub/*.log"]);
        ChooseFolder(editor, "sub");
        editor.Preview.SelectedIgnoreFile.Should().BeNull();
        editor.ExtractToFolderCommand.CanExecute([]).Should().BeTrue();
        editor.RelocationDisabledReason.Should().BeNull();

        editor.IgnorePatternsText += Environment.NewLine + "*.bak";

        editor.IsDirty.Should().BeTrue();
        editor.RelocationDisabledReason.Should().Be(Loc.T("ignore.relocation.saveFirst"));
        editor.ExtractDisabledReason.Should().Be(Loc.T("ignore.relocation.saveFirst"));
        editor.ExtractToFolderCommand.CanExecute([]).Should().BeFalse();
        ChooseIgnoreFile(editor, "sub");
        editor.IntegrateIgnoreFileCommand.CanExecute(null).Should().BeFalse();

        editor.IntegrateIgnoreFileCommand.Execute(null);
        _fixture.Dialogs.Relocations.Should().BeEmpty();
    }

    [Fact]
    public void Commands_are_disabled_for_s3_sources()
    {
        _fixture.AddConnection("c1", "Photos", "photos");
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Pull",
            Source = new StorageLocation(S3StorageFactory.Kind, "camera", "c1"),
            Target = StorageLocation.FileSystem(TargetFolder),
            Ignore = new IgnoreSettings { HonorNestedFiles = true },
        });
        var editor = _fixture.Create().Plans.Single();

        editor.IsDirty.Should().BeFalse();
        editor.RelocationDisabledReason.Should().Be(Loc.T("ignore.relocation.notFolder"));
        editor.ExtractDisabledReason.Should().Be(Loc.T("ignore.relocation.notFolder"));
        editor.IntegrateIgnoreFileCommand.CanExecute(null).Should().BeFalse();
        editor.ExtractToFolderCommand.CanExecute([]).Should().BeFalse();
    }

    [Fact]
    public async Task Extract_is_disabled_when_nested_files_are_off()
    {
        Write("sub/.backupignore", "*.tmp\n");
        var editor = await ScannedAsync(["/sub/*.log"], honorNested: false);
        ChooseFolder(editor, "sub");

        editor.RelocationDisabledReason.Should().BeNull();
        editor.ExtractDisabledReason.Should().Be(Loc.T("ignore.relocation.honorOff"));
        editor.ExtractToFolderCommand.CanExecute([0]).Should().BeFalse();
        ChooseIgnoreFile(editor, "sub");
        editor.IntegrateIgnoreFileCommand.CanExecute(null).Should().BeTrue("integrating shows the difference like any other");
    }

    [Fact]
    public async Task Dialog_cannot_apply_while_waiting_for_the_scan()
    {
        // The source is scanned from memory; the scan holds on the nested file, so it is still running.
        var memory = new InMemoryStorage(StorageCapabilities.None);
        memory.AddFile("sub/.backupignore", Encoding.UTF8.GetBytes("*.tmp\n"));
        memory.AddFile("sub/a.tmp", new byte[3]);
        var gated = new GatedStorage(memory) { Holds = path => path == "sub/.backupignore" };
        _fixture.Storages = new Factory(_ => gated);
        var editor = Editor(["/sub/*.tmp"]);
        var scan = editor.Preview.IndexCommand.ExecuteAsync(null);
        await gated.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => editor.Preview.Tree.Rows.Any(r => r.Name == "sub"));
        ChooseFolder(editor, "sub");
        _fixture.Dialogs.OnRelocation = m =>
        {
            m.ApplyAnyway = true;
            return true;
        };

        editor.ExtractToFolderCommand.Execute([0]);

        var model = _fixture.Dialogs.Relocations.Should().ContainSingle().Subject;
        model.EquivalenceState.Should().Be(EquivalenceState.Waiting);
        model.EquivalenceText.Should().Be(Loc.T("ignore.relocation.waiting"));
        model.CanApply.Should().BeFalse();
        editor.CurrentIgnoreSettings().Patterns.Should().Equal("/sub/*.tmp");
        File.Exists(FileIn("sub/.backupignore")).Should().BeFalse();
        gated.Gate.SetResult();
        await scan;
    }

    [Fact]
    public void Waiting_never_applies()
    {
        var model = new RelocationDialogViewModel(Proposal(new EquivalenceResult.NotChecked(EquivalenceResult.NotChecked.ScanRunning)));

        model.EquivalenceState.Should().Be(EquivalenceState.Waiting);
        model.CanApply.Should().BeFalse();
        model.ApplyAnyway = true;
        model.CanApply.Should().BeFalse();
        model.ShowApplyAnyway.Should().BeFalse();

        var noScan = new RelocationDialogViewModel(Proposal(new EquivalenceResult.NotChecked(EquivalenceResult.NotChecked.NoScan)));
        noScan.EquivalenceState.Should().Be(EquivalenceState.Waiting);
        noScan.EquivalenceText.Should().Be(Loc.T("ignore.relocation.noScan"));
        noScan.CanApply.Should().BeFalse();
    }

    [Fact]
    public void Differs_needs_apply_anyway()
    {
        var examples = new[] { new EquivalenceExample("sub/keep.tmp", false, true, "*.tmp") };
        var model = new RelocationDialogViewModel(Proposal(new EquivalenceResult.Differs(3, examples)));
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.EquivalenceState.Should().Be(EquivalenceState.Differs);
        model.EquivalenceText.Should().Be(Loc.F("ignore.relocation.differs", ("count", 3)));
        model.ApplyText.Should().Be(Loc.T("ignore.relocation.applyAnyway"));
        model.ShowApplyAnyway.Should().BeTrue();
        model.Examples.Should().ContainSingle().Which.Path.Should().Be("sub/keep.tmp");
        model.CanApply.Should().BeFalse();

        model.ApplyAnyway = true;

        model.CanApply.Should().BeTrue();
        raised.Should().Contain(nameof(RelocationDialogViewModel.CanApply));

        var identical = new RelocationDialogViewModel(Proposal(new EquivalenceResult.Identical()));
        identical.CanApply.Should().BeTrue();
        identical.ShowApplyAnyway.Should().BeFalse();
        identical.EquivalenceText.Should().Be(Loc.T("ignore.relocation.identical"));
    }

    [Fact]
    public void Nothing_to_move_never_applies()
    {
        var model = new RelocationDialogViewModel(Proposal(new EquivalenceResult.Identical()) with { MovedLines = [] });

        model.CanApply.Should().BeFalse();
        model.NothingToMove.Should().BeTrue();
    }

    [Fact]
    public async Task A_difference_is_applied_only_with_apply_anyway()
    {
        // The nested file outranks the plan: "*.tmp" moved there beats the plan's re-include of keep.tmp.
        Write("sub/keep.tmp");
        Write("sub/a.tmp");
        var editor = await ScannedAsync(["/sub/*.tmp", "!/sub/keep.tmp"]);
        ChooseFolder(editor, "sub");

        editor.ExtractToFolderCommand.Execute([0]);

        var first = _fixture.Dialogs.Relocations.Single();
        first.EquivalenceState.Should().Be(EquivalenceState.Differs);
        first.Examples.Should().ContainSingle().Which.Path.Should().Be("sub/keep.tmp");
        editor.CurrentIgnoreSettings().Patterns.Should().Equal("/sub/*.tmp", "!/sub/keep.tmp");
        File.Exists(FileIn("sub/.backupignore")).Should().BeFalse("the dialog answered yes, but Apply anyway was not ticked");

        _fixture.Dialogs.OnRelocation = m =>
        {
            m.ApplyAnyway = true;
            return true;
        };
        editor.ExtractToFolderCommand.Execute([0]);

        editor.CurrentIgnoreSettings().Patterns.Should().Equal("!/sub/keep.tmp");
        File.Exists(FileIn("sub/.backupignore")).Should().BeTrue();
    }

    [Fact]
    public async Task Stale_apply_shows_the_stale_error_and_changes_nothing()
    {
        Write("sub/.backupignore", "*.tmp\n");
        var editor = await ScannedAsync(["*.log"]);
        ChooseIgnoreFile(editor, "sub");
        _fixture.Dialogs.OnRelocation = _ =>
        {
            File.WriteAllText(FileIn("sub/.backupignore"), "*.other\n");   // edited while the dialog is open
            return true;
        };

        editor.IntegrateIgnoreFileCommand.Execute(null);

        _fixture.Dialogs.Errors.Should().Equal(Loc.T("ignore.relocation.stale"));
        editor.CurrentIgnoreSettings().Patterns.Should().Equal("*.log");
        editor.IsDirty.Should().BeFalse();
        SavedPatterns().Should().Equal("*.log");
        File.ReadAllText(FileIn("sub/.backupignore")).Should().Be("*.other\n");
        _fixture.RecycleBin.Recycled.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_changes_nothing()
    {
        Write("sub/.backupignore", "*.tmp\n");
        var editor = await ScannedAsync(["*.log"]);
        ChooseIgnoreFile(editor, "sub");
        _fixture.Dialogs.RelocationAnswer = false;

        editor.IntegrateIgnoreFileCommand.Execute(null);

        _fixture.Dialogs.Relocations.Should().ContainSingle();
        editor.CurrentIgnoreSettings().Patterns.Should().Equal("*.log");
        editor.IsDirty.Should().BeFalse();
        SavedPatterns().Should().Equal("*.log");
        File.ReadAllText(FileIn("sub/.backupignore")).Should().Be("*.tmp\n");
        _fixture.RecycleBin.Recycled.Should().BeEmpty();
        _fixture.Dialogs.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 0, new int[0])]
    [InlineData(3, 1, new[] { 1 })]          // "b"
    [InlineData(1, 3, new[] { 0, 1 })]       // from inside "a" into "b"
    [InlineData(0, 3, new[] { 0 })]          // "a" and its line break: "b" is not touched
    [InlineData(0, 7, new[] { 0, 1, 2 })]
    public void Selection_maps_to_the_touched_lines(int start, int length, int[] expected)
    {
        PlanEditorViewModel.TouchedPatternLines("a\r\nb\r\nc", start, length).Should().Equal(expected);
    }

    private static RelocationProposal Proposal(EquivalenceResult equivalence) =>
        new(RelocationKind.Integrate, "sub", ["*.log"], ["*.log", "# from sub/.backupignore", "/sub/**/*.tmp"], "*.tmp\n", null, [],
            equivalence, "h", "f")
        {
            MovedLines = ["# from sub/.backupignore", "/sub/**/*.tmp"],
        };

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
