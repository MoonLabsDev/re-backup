using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Core.Versions;
using ReBackup.Shared.IO;

namespace ReBackup.App.ViewModels;

/// <summary>The Versions tab: Open, Show in Explorer and the two restores of the selected entry.</summary>
public sealed partial class VersionsViewModel
{
    /// <summary>Paths and reasons listed in a confirmation's message (the failures dialog lists them all).</summary>
    private const int FailuresInMessage = 5;

    /// <summary>The progress line is updated at most this often (a restore of small files reports thousands per second).</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Types Windows runs rather than shows when they are opened: "Open" on one of them asks first, since it would start
    /// a program or script from the backup. Windows' own list (<see cref="AssocIsDangerous"/>) is asked as well.
    /// </summary>
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".msi", ".scr", ".lnk", ".url", ".reg",
        ".hta", ".cpl",
        // further types the shell executes in the same way
        ".pif", ".vbe", ".wsh", ".msc", ".msp", ".jar", ".psm1", ".appref-ms", ".application", ".scf",
        ".ws", ".vb", ".chm", ".py", ".pyw", ".pyz", ".settingcontent-ms", ".library-ms", ".search-ms", ".sct", ".shb",
        ".shs", ".website", ".diagcab", ".appinstaller", ".msix", ".appx", ".gadget", ".ps1xml", ".psd1", ".mst", ".ins",
        ".isp",
    };

    private CancellationTokenSource? _restoreCts;

    /// <summary>The copying of the running restore (null while none copies); it removes its temp file when canceled.</summary>
    private Task? _restoreRun;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreToOriginalCommand), nameof(RestoreToCommand), nameof(CancelRestoreCommand),
        nameof(DeleteVersionsCommand))]
    private bool _isRestoring;

    /// <summary>"Restoring 120 of 300 files · 34 %" while a restore runs (Versions tab and plan card).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestoreText))]
    private LocText _restoreProgress = LocText.Empty;

    public string RestoreText => RestoreProgress.ToString();

    [ObservableProperty] private double _restoreFraction;

    partial void OnTreeSelectionChanged()
    {
        OpenSelectedCommand.NotifyCanExecuteChanged();
        ShowSelectedInExplorerCommand.NotifyCanExecuteChanged();
        RestoreToOriginalCommand.NotifyCanExecuteChanged();
        RestoreToCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The selected tree entry and the version that holds it (B for entries only in B). Null for message rows, and while
    /// the tree shows ids that are no longer the versions' current ones (a sync imported one of them again): the
    /// entry is then not used until the tree has been loaded with the new ids.
    /// </summary>
    private (VersionTreeNode Node, VersionRowViewModel Version)? Selection
    {
        get
        {
            if (Tree.SelectedNode is not { IsMessage: false } node)
                return null;
            var (version, shownId) = node.OnlyInOther
                ? (SelectedCompare?.Row, Tree.ShownOtherId)
                : (SelectedVersion, Tree.ShownVersionId);
            return version is { Indexed: { } indexed } && shownId == indexed.Id ? (node, version) : null;
        }
    }

    private bool CanOpenSelected() => Selection is { Node.IsDirectory: false };

    private bool CanActOnSelected() => Selection is not null;

    private bool CanRestore() => !IsRestoring && !IsDeleting && Selection is not null;

    /// <summary>
    /// Opens the copy of the selected file in the version folder with its program (meant read-only). A program, script
    /// or shortcut is never started from a backup: its folder is shown in Explorer instead, with the file selected.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    private async Task OpenSelectedAsync()
    {
        if (Current() is not { Node.IsDirectory: false } selection)
            return;
        var (folder, path) = (FolderOf(selection.Version), selection.Node.Path);
        if (IsRunnable(Path.GetExtension(selection.Node.Name)))
        {
            if (!await Task.Run(() => _files.ShowInExplorer(folder, path)))
                _context.ReportStatus(LocText.Of("restore.notInVersion", ("path", path)));
            else
                _context.ReportStatus(LocText.Of("restore.runnable", ("name", selection.Node.Name)));
            return;
        }
        if (!await Task.Run(() => _files.OpenFile(folder, path)))
            _context.ReportStatus(LocText.Of("restore.cannotOpen", ("path", path)));
    }

    /// <summary>Shows the selected entry in Explorer, selected in its folder.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private async Task ShowSelectedInExplorerAsync()
    {
        if (Current() is not { } selection)
            return;
        var (folder, path) = (FolderOf(selection.Version), selection.Node.Path);
        if (!await Task.Run(() => _files.ShowInExplorer(folder, path)))
            _context.ReportStatus(LocText.Of("restore.notInVersion", ("path", path)));
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreToOriginalAsync() => RestoreAsync(RestoreMode.Original);

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreToAsync() => RestoreAsync(RestoreMode.ToFolder);

    [RelayCommand(CanExecute = nameof(IsRestoring))]
    private void CancelRestore()
    {
        if (_restoreCts is not { } cts || cts.IsCancellationRequested)
            return;
        cts.Cancel();
        // Planning cannot be interrupted (it writes nothing); the copy stops after the current file.
        RestoreProgress = _restoreRun is null ? LocText.Of("restore.canceling") : LocText.Of("restore.cancelingAfterFile");
    }

    /// <summary>
    /// Cancels a running restore (exit, restart) and returns the copy's task, which ends after the current file once
    /// the temp file is removed; a completed task when nothing is being copied.
    /// </summary>
    public Task StopRestore()
    {
        if (IsRestoring)
            CancelRestore();
        return _restoreRun ?? Task.CompletedTask;
    }

    /// <summary><see cref="Selection"/>; when there is none although a row is selected, says why in the footer.</summary>
    private (VersionTreeNode Node, VersionRowViewModel Version)? Current()
    {
        var selection = Selection;
        if (selection is null && Tree.SelectedNode is { IsMessage: false })
            _context.ReportStatus(LocText.Of("restore.reindexing"));
        return selection;
    }

    /// <summary>
    /// Confirm (original location) or pick a folder → plan in the background → ask once about existing files and show
    /// the parts of the version that cannot be read → copy in the background. Nothing at the destination is ever deleted.
    /// </summary>
    private async Task RestoreAsync(RestoreMode mode)
    {
        if (IsRestoring || Current() is not { } selection)
            return;
        var (node, version) = selection;
        var dialogs = _context.Dialogs;
        // The whole version has sentences of its own (no "the whole version from the version of …").
        var whole = node.Path.Length == 0;
        var item = LocText.Of("restore.item", ("path", node.Path));
        var relative = node.Path;
        var versionFolder = FolderOf(version);
        var versionDate = new LocText(() => version.DateText);

        string root;
        if (mode == RestoreMode.Original)
        {
            // The manifest's source; a version without a (readable) manifest goes back to the plan's source.
            var source = version.Indexed is { Origin: IndexOrigin.Manifest, Source.Length: > 0 } indexed
                ? indexed.Source
                : _savedPlan()?.Source.Path;
            if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source))
            {
                dialogs.ShowError(Loc.T("restore.title"), Loc.T("restore.originUnknown"));
                return;
            }
            root = source;
            var warning = _isBackupActive()
                ? Loc.T("restore.backupRunningWarning")
                : "";
            if (!dialogs.ConfirmDefaultNo(Loc.T("restore.confirmTitle"),
                    Loc.F(whole ? "restore.confirmVersion" : "restore.confirm", ("item", item), ("date", versionDate),
                        ("destination", Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))),
                        ("warning", warning))))
                return;
        }
        else
        {
            if (dialogs.PickFolder(Loc.T("restore.pickFolder"), null) is not { } folder)
                return;
            root = folder;
        }

        if (InsideTarget(root, mode == RestoreMode.Original ? relative : Path.GetFileName(relative)) is { } target)
        {
            dialogs.ShowError(Loc.T("restore.title"), Loc.F("restore.insideTarget", ("target", target)));
            return;
        }

        // From here until the copy ends, the restore buttons are off (a second click while planning does nothing).
        var cts = _restoreCts = new CancellationTokenSource();
        IsRestoring = true;
        RestoreFraction = 0;
        RestoreProgress = LocText.Of("restore.preparing");
        try
        {
            RestorePlan plan;
            try
            {
                plan = await Task.Run(() => Restorer.Plan(versionFolder, [relative], root, mode), cts.Token);
            }
            catch (OperationCanceledException)
            {
                _context.ReportStatus(LocText.Of("restore.canceledNothing"));
                return;
            }
            catch (Exception ex)
            {
                // e.g. the destination is inside the version folder, the entry is gone or is a link
                var reason = ex is ArgumentException { ParamName: { } name }
                    ? ex.Message.Replace($" (Parameter '{name}')", "", StringComparison.Ordinal)
                    : ex.Message;
                dialogs.ShowError(Loc.T("restore.title"), Loc.F(whole ? "restore.cannotRestoreVersion" : "restore.cannotRestore",
                    ("item", item), ("reason", Loc.Known(reason))));
                return;
            }
            if (cts.IsCancellationRequested)
            {
                _context.ReportStatus(LocText.Of("restore.canceledNothing"));
                return;
            }

            if (Ask(plan, mode, root) is not { } policy)
            {
                _context.ReportStatus(CanceledNothing(plan));
                return;
            }

            RestoreProgress = LocText.Of("restore.restoring");
            _context.ReportStatus(LocText.Of(whole ? "restore.restoringVersion" : "restore.restoringFrom", ("item", item), ("date", versionDate)));
            var progress = new ThrottledProgress(new Progress<RestoreProgress>(p =>
            {
                if (!IsRestoring || !ReferenceEquals(_restoreCts, cts) || cts.IsCancellationRequested)
                    return;
                RestoreFraction = p.Fraction;
                RestoreProgress = LocText.Of("restore.progress",
                    ("done", p.FilesDone), ("count", p.FilesTotal), ("percent", p.Fraction * 100));
            }));

            RestoreResult result;
            try
            {
                var run = Task.Run(() => Restorer.Run(plan, policy, progress, cts.Token));
                _restoreRun = run;
                result = await run;
            }
            catch (Exception ex)
            {
                var stopped = new LocText(() => Loc.F("restore.stopped", ("error", Loc.Known(ex.Message))));
                _context.ReportStatus(stopped);
                dialogs.ShowError(Loc.T("restore.title"), stopped.ToString());
                return;
            }

            var summary = Summary(result);
            _context.ReportStatus(summary);
            if (result.Failures.Count > 0)
            {
                dialogs.ShowFailures(Loc.T("restore.title"),
                    Loc.F("restore.failures", ("summary", summary), ("count", result.Failures.Count), ("root", root)),
                    Lines(result.Failures));
            }
        }
        finally
        {
            if (ReferenceEquals(_restoreCts, cts))
            {
                _restoreCts = null;
                _restoreRun = null;
                IsRestoring = false;
                RestoreProgress = LocText.Empty;
                RestoreFraction = 0;
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// What happens to existing files (asked only when there are any), with the parts of the version that cannot be read
    /// shown first; null when the user cancels.
    /// </summary>
    private ConflictPolicy? Ask(RestorePlan plan, RestoreMode mode, string root)
    {
        var dialogs = _context.Dialogs;
        var unreadable = Lines(plan.PlanFailures);
        if (plan.Conflicts.Count > 0)
        {
            var examples = string.Join("\n", plan.Conflicts.Take(FailuresInMessage)
                .Select(f => "  " + Path.GetRelativePath(plan.DestinationRoot, f.Destination)));
            var more = plan.Conflicts.Count > FailuresInMessage ? "\n  …" : "";
            var message = mode == RestoreMode.Original
                ? Loc.F("restore.conflictsOriginal", ("count", plan.Conflicts.Count), ("examples", examples), ("more", more))
                : Loc.F("restore.conflictsFolder", ("count", plan.Conflicts.Count), ("folder", root), ("examples", examples),
                    ("more", more));
            return dialogs.AskConflictPolicy(Loc.T("restore.title"), message, unreadable);
        }
        if (unreadable.Count > 0)
        {
            return dialogs.ConfirmFailures(Loc.T("restore.title"),
                Loc.F("restore.unreadable", ("count", unreadable.Count)), unreadable, Loc.T("restore.restoreRest"))
                ? ConflictPolicy.Skip   // there are no conflicts: the policy does not matter
                : null;
        }
        return ConflictPolicy.Skip;
    }

    /// <summary>
    /// The plan's target when the destination (<paramref name="root"/> + <paramref name="relative"/>) would lie in it:
    /// a restore must not write into the versions. Null otherwise.
    /// </summary>
    private string? InsideTarget(string root, string relative)
    {
        if (_savedPlan()?.Target.Path is not { Length: > 0 } target)
            return null;
        try
        {
            var destination = relative.Length == 0
                ? root
                : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            return PathUtil.IsSameOrInside(root, target) || PathUtil.IsSameOrInside(destination, target) ? target : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;   // Restorer.Plan rejects such a path with its own message
        }
    }

    private static bool IsRunnable(string extension)
    {
        if (extension.Length == 0)
            return false;
        if (RunnableExtensions.Contains(extension))
            return true;
        try
        {
            return AssocIsDangerous(extension);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Windows' list of file types that are dangerous to open (shlwapi, Windows XP SP2 and later).</summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssocIsDangerous(string pszAssoc);

    /// <summary>"Restore canceled; nothing was written.", with the parts of the version that could not be read.</summary>
    private static LocText CanceledNothing(RestorePlan plan)
    {
        var unreadable = plan.PlanFailures.Count;
        return new LocText(() => Loc.T("restore.canceledNothing") +
                                 (unreadable == 0 ? "" : Loc.F("restore.unreadableNote", ("count", unreadable))));
    }

    private static List<string> Lines(IReadOnlyList<RestoreFailure> failures) =>
        failures.Select(f => $"{f.Path}: {Loc.Known(f.Reason)}").ToList();

    private static LocText Summary(RestoreResult result) => new(() =>
    {
        var parts = new List<string> { Loc.F("restore.summary.copied", ("count", result.Copied)) };
        if (result.KeptBoth > 0)
            parts.Add(Loc.F("restore.summary.keptBoth", ("count", result.KeptBoth)));
        if (result.Skipped > 0)
            parts.Add(Loc.F("restore.summary.skipped", ("count", result.Skipped)));
        if (result.Failures.Count > 0)
            parts.Add(Loc.F("restore.summary.failed", ("count", result.Failures.Count)));
        var joined = string.Join(", ", parts);
        return result.Canceled
            ? Loc.F("restore.summary.canceled", ("parts", joined))
            : Loc.F("restore.summary.finished", ("parts", joined));
    });

    /// <summary>
    /// Passes a restore's progress on (from the copying thread) at most every <see cref="ProgressInterval"/>, and always
    /// the last report of the run, so the UI thread is not flooded with one message per buffer.
    /// </summary>
    private sealed class ThrottledProgress(IProgress<RestoreProgress> inner) : IProgress<RestoreProgress>
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan _last = -ProgressInterval;

        public void Report(RestoreProgress value)
        {
            var now = _clock.Elapsed;
            if (now - _last < ProgressInterval && value.FilesDone < value.FilesTotal)
                return;
            _last = now;
            inner.Report(value);
        }
    }
}
