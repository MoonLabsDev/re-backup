using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Versions;
using ReBackup.Shared.Json;
using ReBackup.Shared.Schedule;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests;

/// <summary>
/// The Cleanup tab: which versions can be checked, the scan by the saved rules, when the scan goes stale, and the gating
/// of the cleanup with the plan's other jobs.
/// </summary>
public class CleanupViewModelTests : IDisposable
{
    private const string Older = "2026_09_29-14_05 Projects";
    private const string Newer = "2026_09_30-14_05 Projects";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly MainViewModelFixture _fixture = new();
    private readonly TableStorageFactory _storages = new();
    private readonly InMemoryStorage _target = new();
    private readonly GatedStorage _gated;
    private readonly InMemoryStorage _source = new();
    private readonly GatedStorage _gatedSource;
    private bool _sourceOnS3;

    public CleanupViewModelTests()
    {
        _gated = new GatedStorage(_target);
        _gatedSource = new GatedStorage(_source);
        _storages.Storages[("full", "")] = _gated;
        _storages.Storages[("src", "")] = _gatedSource;
        _fixture.Storages = _storages;
        _fixture.AddConnection("full", "Full bucket", "bucket-a");
        _fixture.AddConnection("src", "Source bucket", "bucket-b");
        WriteVersion(Older);
        WriteVersion(Newer);
        var source = Path.Combine(_fixture.Root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.cs"), "class A {}");
    }

    public void Dispose()
    {
        _gated.Gate.TrySetResult();
        _gatedSource.Gate.TrySetResult();
        _fixture.Queue?.CancelAll();
        _fixture.Dispose();
    }

    private BackupPlan Plan => new()
    {
        Id = "p1",
        Name = "Projects",
        Enabled = true,
        Source = _sourceOnS3
            ? new StorageLocation("s3", "", "src")
            : StorageLocation.FileSystem(Path.Combine(_fixture.Root, "source")),
        Target = new StorageLocation("s3", "", "full"),
        Ignore = new IgnoreSettings { UseGlobalDefaults = false, Patterns = ["target/"] },
    };

    private static string ManifestPath(string version) => $"{version}/{VersionMarkerNames.Manifest}";

    private static string MarkerPath(string version) => $"{version}/{VersionMarkerNames.Pruning}";

    /// <summary>A full-plan version: <c>src/a.cs</c> (10 bytes) and <c>target/</c> with 100 + 50 bytes.</summary>
    private void WriteVersion(string name, string extra = "")
    {
        var mtime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var manifest = new BackupManifest
        {
            FormatVersion = 2,
            PlanId = "p1",
            PlanName = "Projects",
            CreatedUtc = mtime,
            Source = @"C:\source" + extra,
        };
        foreach (var (path, content) in new[]
                 {
                     ("src/a.cs", "class A {}"), ("target/x.dll", new string('x', 100)), ("target/y.dll", new string('y', 50)),
                 })
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            _target.AddFile($"{name}/{path}", bytes, mtime);
            manifest.Files.Add(new ManifestFile(path, bytes.Length, mtime, "xxh64:" + Convert.ToHexStringLower(XxHash64.Hash(bytes))));
        }
        manifest.FileCount = manifest.Files.Count;
        manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
        manifest.Directories = ["src", "target"];
        _target.AddFile(ManifestPath(name), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonDefaults.Options)));
    }

    private MainViewModel CreateMain()
    {
        _fixture.Store.Save(Plan);
        return _fixture.Create();
    }

    private static PlanEditorViewModel Projects(MainViewModel vm) => vm.Plans.Single(p => p.Id == "p1");

    /// <summary>Reads the target and waits until both versions are indexed and the tab lists them.</summary>
    private static async Task LoadAsync(PlanEditorViewModel editor)
    {
        await editor.Versions.RefreshCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => editor.Versions.VersionRows.Count == 2 &&
                                   editor.Versions.VersionRows.All(r => r.Indexed is not null) &&
                                   !editor.Versions.IsSyncing && editor.Cleanup.Rows.All(r => r.IsSelectable),
            "both versions are indexed");
    }

    private static CleanupRowViewModel Row(CleanupViewModel cleanup, string name) => cleanup.Rows.Single(r => r.Name == name);

    /// <summary>Checks <paramref name="name"/> and scans.</summary>
    private static async Task ScanAsync(CleanupViewModel cleanup, params string[] names)
    {
        foreach (var name in names)
            Row(cleanup, name).IsChecked = true;
        cleanup.ScanCommand.CanExecute(null).Should().BeTrue();
        await cleanup.ScanCommand.ExecuteAsync(null).WaitAsync(Timeout);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Not met in time: {because}");
    }

    /// <summary>A Versions tab and its Cleanup tab without a main view model; the rows are added by the test.</summary>
    private (VersionsViewModel Versions, CleanupViewModel Cleanup) Standalone()
    {
        var worker = new VersionIndexWorker(new VersionIndexSet(Path.Combine(_fixture.Root, "index")));
        var context = new VersionsContext(worker, _fixture.Dialogs, _ => { }, _storages, MachineName: "THIS-PC");
        var plan = Plan;
        var versions = new VersionsViewModel(() => plan, () => false, new FakeFolderOpener(), context);
        return (versions, new CleanupViewModel(() => plan, () => false, versions, context, () => []));
    }

    private static VersionRowViewModel VersionRow(string name, VersionOwnership ownership, IndexOrigin? origin)
    {
        var time = new DateTime(2026, 9, 29, 14, 5, 0);
        return new VersionRowViewModel(new VersionInfo(name, name, time, ownership, 3, 160))
        {
            Indexed = origin is { } o ? new IndexedVersion(name.Length, name, time, ownership, null, o, 3, 160) : null,
        };
    }

    [Fact]
    public void Unmanaged_damaged_and_unindexed_versions_cannot_be_checked()
    {
        var (versions, cleanup) = Standalone();
        versions.VersionRows.ReplaceAll([
            VersionRow("2026_09_30-14_05 Projects", VersionOwnership.Owned, IndexOrigin.Manifest),
            VersionRow("2026_09_29-14_05 Projects", VersionOwnership.Foreign, IndexOrigin.Manifest),
            VersionRow("2026_09_28-14_05 Projects", VersionOwnership.Owned, IndexOrigin.Damaged),
            VersionRow("2026_09_27-14_05 Projects", VersionOwnership.Owned, null),
        ]);

        cleanup.Rows.Select(r => r.Name).Should().Equal(versions.VersionRows.Select(r => r.Name), "newest first, as listed");
        cleanup.Rows[0].IsSelectable.Should().BeTrue();
        cleanup.Rows[0].NotSelectableReason.Should().BeNull();
        cleanup.Rows[1].NotSelectableReason.Should().Be(Loc.T("versions.notManaged.foreign"));
        cleanup.Rows[2].NotSelectableReason.Should().Be(Loc.T("versions.damaged.toolTip"));
        cleanup.Rows[3].NotSelectableReason.Should().Be(Loc.T("cleanup.notIndexed"));
        cleanup.Rows.Skip(1).Should().OnlyContain(r => !r.IsSelectable);

        cleanup.AllChecked = true;

        cleanup.Rows[0].IsChecked.Should().BeTrue();
        cleanup.Rows.Skip(1).Should().OnlyContain(r => !r.IsChecked);
        cleanup.Rows[1].IsChecked = true;
        cleanup.Rows[1].IsChecked.Should().BeFalse("a greyed-out row cannot be checked");
        cleanup.AllChecked.Should().BeTrue();
    }

    [Fact]
    public void AllChecked_checks_and_unchecks_the_selectable_rows()
    {
        var (versions, cleanup) = Standalone();
        versions.VersionRows.ReplaceAll([
            VersionRow("2026_09_30-14_05 Projects", VersionOwnership.Owned, IndexOrigin.Manifest),
            VersionRow("2026_09_29-14_05 Projects", VersionOwnership.Owned, IndexOrigin.Manifest),
        ]);

        cleanup.AllChecked.Should().BeFalse();
        cleanup.Rows[0].IsChecked = true;
        cleanup.AllChecked.Should().BeNull("one of two is checked");
        cleanup.AllChecked = true;
        cleanup.Rows.Should().OnlyContain(r => r.IsChecked);
        cleanup.AllChecked = false;
        cleanup.Rows.Should().OnlyContain(r => !r.IsChecked);
        cleanup.AllChecked = true;
        cleanup.AllChecked = null;   // a three-state box clicked once more
        cleanup.Rows.Should().OnlyContain(r => !r.IsChecked);
        cleanup.AllChecked.Should().BeFalse();
    }

    [Fact]
    public async Task Scan_is_disabled_with_unsaved_rules_or_nothing_checked()
    {
        var vm = CreateMain();
        var editor = Projects(vm);
        var cleanup = editor.Cleanup;
        await LoadAsync(editor);

        cleanup.ScanCommand.CanExecute(null).Should().BeFalse("nothing is checked");
        Row(cleanup, Older).IsChecked = true;
        cleanup.ScanCommand.CanExecute(null).Should().BeTrue();

        editor.IgnorePatternsText = "target/\nsrc/";

        cleanup.ScanCommand.CanExecute(null).Should().BeFalse("the rules are not saved");
        cleanup.Hint!.ToString().Should().Be(Loc.T("cleanup.saveFirst"));

        editor.Revert();

        cleanup.ScanCommand.CanExecute(null).Should().BeTrue();
        cleanup.Hint.Should().BeNull();
    }

    [Fact]
    public async Task Scan_fills_removes_frees_and_the_rule_list()
    {
        var vm = CreateMain();
        var editor = Projects(vm);
        var cleanup = editor.Cleanup;
        await LoadAsync(editor);

        await ScanAsync(cleanup, Older);

        var row = Row(cleanup, Older);
        row.RemovesText.Should().Be("2");
        row.FreesText.Should().Be(Formats.Bytes(150));
        Row(cleanup, Newer).RemovesText.Should().BeEmpty("it was not scanned");
        var rule = cleanup.ByRule.Should().ContainSingle().Subject;
        rule.Rule.Should().Be("target/");
        rule.Hit.Files.Should().Be(2);
        rule.Hit.Bytes.Should().Be(150);
        rule.Hit.LargestPath.Should().Be("target");
        cleanup.TotalsText.ToString().Should().Be(Loc.F("cleanup.totals.full", ("count", 1), ("files", Formats.Files(2)),
            ("size", Formats.Bytes(150))));
        cleanup.IsStale.Should().BeFalse();
        cleanup.IsScanning.Should().BeFalse();
        cleanup.Hint.Should().BeNull();
        cleanup.CleanUpCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Saving_rules_makes_the_scan_stale()
    {
        var vm = CreateMain();
        var editor = Projects(vm);
        var cleanup = editor.Cleanup;
        await LoadAsync(editor);
        await ScanAsync(cleanup, Older);

        editor.IgnorePatternsText = "target/\nsrc/";
        editor.TrySave(_fixture.Store).Should().BeTrue();

        cleanup.IsStale.Should().BeTrue();
        cleanup.CleanUpCommand.CanExecute(null).Should().BeFalse();
        cleanup.Hint!.ToString().Should().Be(Loc.T("cleanup.scanAgain"));
    }

    [Fact]
    public async Task Changing_the_selection_makes_the_scan_stale()
    {
        var vm = CreateMain();
        var editor = Projects(vm);
        var cleanup = editor.Cleanup;
        await LoadAsync(editor);
        await ScanAsync(cleanup, Older);

        Row(cleanup, Newer).IsChecked = true;

        cleanup.IsStale.Should().BeTrue();
        cleanup.CleanUpCommand.CanExecute(null).Should().BeFalse();
        cleanup.Hint!.ToString().Should().Be(Loc.T("cleanup.scanAgain"));

        await cleanup.ScanCommand.ExecuteAsync(null).WaitAsync(Timeout);

        cleanup.IsStale.Should().BeFalse("a new scan is current again");
        cleanup.CleanUpCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task A_changed_version_stamp_makes_the_scan_stale()
    {
        var vm = CreateMain();
        var editor = Projects(vm);
        var cleanup = editor.Cleanup;
        await LoadAsync(editor);
        await ScanAsync(cleanup, Older);

        // Another machine replaced the manifest (a retry, say): the index picks up the new stamp at the next sync.
        WriteVersion(Older, extra: "2");
        await editor.Versions.RefreshCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => cleanup.IsStale, "the scan saw another manifest");
        cleanup.CleanUpCommand.CanExecute(null).Should().BeFalse();
        cleanup.Hint!.ToString().Should().Be(Loc.T("cleanup.scanAgain"));
    }

    [Fact]
    public async Task An_unchanged_sync_keeps_the_scan_current()
    {
        var vm = CreateMain();
        var editor = Projects(vm);
        var cleanup = editor.Cleanup;
        await LoadAsync(editor);
        await ScanAsync(cleanup, Older);

        await editor.Versions.RefreshCommand.ExecuteAsync(null);
        await Task.Delay(200);

        cleanup.IsStale.Should().BeFalse();
        cleanup.CleanUpCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task CleanUp_is_disabled_while_a_backup_runs()
    {
        var worker = new VersionIndexWorker(new VersionIndexSet(Path.Combine(_fixture.Root, "index")));
        var context = new VersionsContext(worker, _fixture.Dialogs, _ => { }, _storages, MachineName: "THIS-PC");
        var plan = Plan;
        var backupActive = false;
        var versions = new VersionsViewModel(() => plan, () => backupActive, new FakeFolderOpener(), context);
        var cleanup = new CleanupViewModel(() => plan, () => false, versions, context, () => []);
        await versions.RefreshCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => cleanup.Rows.Count == 2 && cleanup.Rows.All(r => r.IsSelectable), "both versions are indexed");
        await ScanAsync(cleanup, Older);
        cleanup.CleanUpCommand.CanExecute(null).Should().BeTrue();

        backupActive = true;

        cleanup.CleanUpCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task CleanUp_is_disabled_while_a_copy_is_active()
    {
        var vm = CreateMain();
        var editor = Projects(vm);
        var cleanup = editor.Cleanup;
        await LoadAsync(editor);
        await ScanAsync(cleanup, Older);
        var changes = 0;
        cleanup.CleanUpCommand.CanExecuteChanged += (_, _) => changes++;

        editor.IsCopying = true;

        cleanup.CleanUpCommand.CanExecute(null).Should().BeFalse();
        changes.Should().BeGreaterThan(0, "the button is told to look again");
        editor.IsCopying = false;
        cleanup.CleanUpCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task CleanUp_confirms_runs_and_shows_each_versions_outcome()
    {
        var vm = CreateMain();
        var editor = Projects(vm);
        var cleanup = editor.Cleanup;
        await LoadAsync(editor);
        await ScanAsync(cleanup, Older);

        await cleanup.CleanUpCommand.ExecuteAsync(null).WaitAsync(Timeout);

        _fixture.Dialogs.Confirms.Should().ContainSingle().Which.Should().Be(Loc.F("cleanup.confirm", ("count", 1),
            ("files", Formats.Files(2)), ("size", Formats.Bytes(150))));
        Row(cleanup, Older).StatusText.Should().Be(Loc.T("cleanup.status.cleaned"));
        cleanup.TotalsText.ToString().Should().Be(Loc.F("cleanup.result", ("count", 1), ("files", Formats.Files(2)),
            ("size", Formats.Bytes(150))));
        cleanup.ByRule.Should().BeEmpty("the scan was dropped");
        cleanup.CleanUpCommand.CanExecute(null).Should().BeFalse("there is no scan any more");
        _target.Files.Should().NotContain($"{Older}/target/x.dll").And.Contain($"{Newer}/target/x.dll");
    }

    [Fact]
    public async Task CleanUp_does_nothing_when_the_question_is_answered_no()
    {
        var vm = CreateMain();
        var editor = Projects(vm);
        var cleanup = editor.Cleanup;
        await LoadAsync(editor);
        await ScanAsync(cleanup, Older);
        _fixture.Dialogs.ConfirmAnswer = false;

        await cleanup.CleanUpCommand.ExecuteAsync(null).WaitAsync(Timeout);

        _target.Files.Should().Contain($"{Older}/target/x.dll");
        cleanup.CleanUpCommand.CanExecute(null).Should().BeTrue("the scan is kept");
    }

    [Fact]
    public async Task Cancel_stops_the_cleanup_after_the_current_version()
    {
        var vm = CreateMain();
        var editor = Projects(vm);
        var cleanup = editor.Cleanup;
        await LoadAsync(editor);
        await ScanAsync(cleanup, Older, Newer);
        _gated.HoldsCreate = path => path == MarkerPath(Older);

        cleanup.CancelCommand.CanExecute(null).Should().BeFalse("nothing runs");
        var running = cleanup.CleanUpCommand.ExecuteAsync(null);
        await _gated.Reached.Task.WaitAsync(Timeout);
        cleanup.CancelCommand.CanExecute(null).Should().BeTrue();
        cleanup.CancelCommand.Execute(null);
        _gated.Gate.SetResult();
        await running.WaitAsync(Timeout);

        Row(cleanup, Older).StatusText.Should().Be(Loc.T("cleanup.status.cleaned"));
        Row(cleanup, Newer).StatusText.Should().BeEmpty("it was not started");
        _target.Files.Should().NotContain($"{Older}/target/x.dll").And.Contain($"{Newer}/target/x.dll");
    }

    [Fact]
    public async Task The_delete_command_is_disabled_while_a_cleanup_runs()
    {
        var vm = CreateMain();
        var editor = Projects(vm);
        var cleanup = editor.Cleanup;
        await LoadAsync(editor);
        await ScanAsync(cleanup, Older);
        editor.Versions.SetMarkedVersions([editor.Versions.VersionRows.Single(r => r.Name == Newer)]);
        editor.Versions.DeleteVersionsCommand.CanExecute(null).Should().BeTrue();
        _gated.HoldsCreate = path => path == MarkerPath(Older);

        var running = cleanup.CleanUpCommand.ExecuteAsync(null);
        await _gated.Reached.Task.WaitAsync(Timeout);

        editor.Versions.DeleteVersionsCommand.CanExecute(null).Should().BeFalse();
        cleanup.ScanCommand.CanExecute(null).Should().BeFalse("no scan while the cleanup runs");

        _gated.Gate.SetResult();
        await running.WaitAsync(Timeout);
        editor.Versions.DeleteVersionsCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task A_queued_run_waits_for_the_cleanup()
    {
        var vm = CreateMain();
        var editor = Projects(vm);
        var cleanup = editor.Cleanup;
        await LoadAsync(editor);
        await ScanAsync(cleanup, Older);
        _gated.HoldsCreate = path => path == MarkerPath(Older);

        var running = cleanup.CleanUpCommand.ExecuteAsync(null);
        await _gated.Reached.Task.WaitAsync(Timeout);
        vm.RunScheduled("p1", RunTrigger.Scheduled);

        _fixture.Queue.IsBusy.Should().BeFalse("the run waits for the cleanup");
        vm.StatusMessage.Should().Be(Loc.F("shell.status.runDeferred", ("plan", "Projects")));

        _gated.Gate.SetResult();
        await running.WaitAsync(Timeout);
        editor.IsDeletingVersions.Should().BeFalse();
        await WaitUntilAsync(() => new RunLog(_fixture.Paths.LogFileFor("p1")).ReadAll().Count == 1, "the deferred run ran");
    }

    [Fact]
    public async Task A_source_scan_still_running_shows_its_progress()
    {
        _sourceOnS3 = true;
        _source.AddFile(".backupignore", Encoding.UTF8.GetBytes("*.tmp\n"));
        _source.AddFile("a.cs", Encoding.UTF8.GetBytes("class A {}"));
        _gatedSource.Holds = path => path == ".backupignore";
        var vm = CreateMain();
        var editor = Projects(vm);
        var cleanup = editor.Cleanup;
        await LoadAsync(editor);
        Row(cleanup, Older).IsChecked = true;

        var scanning = cleanup.ScanCommand.ExecuteAsync(null);
        await _gatedSource.Reached.Task.WaitAsync(Timeout);
        var texts = Enumerable.Range(0, 3).Select(n => Loc.F("cleanup.scanningSource", ("files", Formats.Files(n)))).ToList();
        await WaitUntilAsync(() => cleanup.Hint is { } hint && texts.Contains(hint.ToString()), "the source scan's progress is shown");

        cleanup.IsScanning.Should().BeTrue();
        Row(cleanup, Older).RemovesText.Should().BeEmpty("no row is filled before the rules are known");
        cleanup.ByRule.Should().BeEmpty();

        _gatedSource.Gate.SetResult();
        await scanning.WaitAsync(Timeout);

        cleanup.IsScanning.Should().BeFalse();
        cleanup.Hint.Should().BeNull();
        Row(cleanup, Older).RemovesText.Should().Be("2");
    }

    [Fact]
    public void The_question_reads_right_for_one_file()
    {
        var size = Formats.Bytes(10);
        foreach (var count in new[] { 1, 2 })
        {
            Loc.F("cleanup.confirm", ("count", count), ("files", Formats.Files(1)), ("size", size))
                .Should().Contain($"Removes {Loc.F(Formats.Files(1))} from").And.Contain($"frees {size}");
        }

        // German: the verb must not agree with the file count ("1 Datei werden …" is wrong).
        using var stream = typeof(MainViewModel).Assembly.GetManifestResourceStream("ReBackup.App.Locales.de-DE.json")!;
        using var german = JsonDocument.Parse(stream);
        var confirm = german.RootElement.GetProperty("cleanup").GetProperty("confirm");
        foreach (var form in new[] { "one", "other" })
            confirm.GetProperty(form).GetString().Should().Contain("Entfernt daraus {files} und gibt {size} frei.");
    }

    [Fact]
    public void Versions_whose_names_differ_only_in_case_do_not_break_the_grid()
    {
        var (versions, cleanup) = Standalone();
        var rows = new[]
        {
            VersionRow("2026_09_30-14_05 X", VersionOwnership.Owned, IndexOrigin.Manifest),
            VersionRow("2026_09_30-14_05 x", VersionOwnership.Owned, IndexOrigin.Manifest),
        };

        versions.VersionRows.ReplaceAll(rows);
        versions.VersionRows.ReplaceAll(rows.Reverse().ToList());

        cleanup.Rows.Should().HaveCount(2);
        cleanup.AllChecked = true;
        cleanup.Rows.Should().OnlyContain(r => r.IsChecked);
    }

    [Fact]
    public async Task CleanUp_is_disabled_while_the_plan_has_unsaved_changes()
    {
        var vm = CreateMain();
        var editor = Projects(vm);
        var cleanup = editor.Cleanup;
        await LoadAsync(editor);
        await ScanAsync(cleanup, Older);
        var changes = 0;
        cleanup.CleanUpCommand.CanExecuteChanged += (_, _) => changes++;

        editor.IgnorePatternsText = "target/\nsrc/";

        cleanup.CleanUpCommand.CanExecute(null).Should().BeFalse("the plan has unsaved changes");
        changes.Should().BeGreaterThan(0, "the button is told to look again");
        cleanup.Hint!.ToString().Should().Be(Loc.T("cleanup.saveFirst"));

        editor.Revert();

        cleanup.CleanUpCommand.CanExecute(null).Should().BeTrue("the saved rules are those of the scan");
    }

    [Fact]
    public async Task A_language_switch_keeps_a_current_scan_current_without_looking_at_each_row()
    {
        var vm = CreateMain();
        var editor = Projects(vm);
        var cleanup = editor.Cleanup;
        await LoadAsync(editor);
        await ScanAsync(cleanup, Older);
        var looks = 0;
        cleanup.ScanCommand.CanExecuteChanged += (_, _) => looks++;

        cleanup.RefreshTexts();

        looks.Should().Be(0, "a language switch changes no selection");
        cleanup.IsStale.Should().BeFalse();
        cleanup.CleanUpCommand.CanExecute(null).Should().BeTrue();
        Row(cleanup, Older).RemovesText.Should().Be("2");
    }

    [Fact]
    public void The_rule_fingerprint_follows_the_rules_in_use()
    {
        var plan = Plan;
        var nested = new[] { new ReBackup.Core.Ignore.NestedIgnoreFile("a", ["*.log"]) };
        var fingerprint = CleanupViewModel.RuleFingerprint(plan, ["bin/"], nested);

        CleanupViewModel.RuleFingerprint(plan, ["obj/"], nested).Should().Be(fingerprint, "the global defaults are not used");
        plan.Ignore.UseGlobalDefaults = true;
        CleanupViewModel.RuleFingerprint(plan, ["bin/"], nested).Should().NotBe(fingerprint);
        CleanupViewModel.RuleFingerprint(plan, ["obj/"], nested)
            .Should().NotBe(CleanupViewModel.RuleFingerprint(plan, ["bin/"], nested));
        CleanupViewModel.RuleFingerprint(plan, ["bin/"], [new ReBackup.Core.Ignore.NestedIgnoreFile("a", ["*.tmp"])])
            .Should().NotBe(CleanupViewModel.RuleFingerprint(plan, ["bin/"], nested));
    }
}
