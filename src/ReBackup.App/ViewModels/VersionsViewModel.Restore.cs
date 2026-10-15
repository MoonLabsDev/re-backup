using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.App.Services;
using ReBackup.Core.Versions;
using ReBackup.Shared.IO;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The Versions tab: Open, Show in Explorer, the two restores of the selected entry and, for incremental plans,
/// "Restore to temp and open" of a folder or a whole version.
/// </summary>
public sealed partial class VersionsViewModel
{
    /// <summary>Paths and reasons listed in a confirmation's message (the failures dialog lists them all).</summary>
    private const int FailuresInMessage = 5;

    /// <summary>The progress line is updated at most this often (a restore of small files reports thousands per second).</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>A folder or version larger than this (1 GB) is copied into the temp folder only after a confirmation.</summary>
    public const long TempRestoreConfirmBytes = 1_073_741_824;

    private CancellationTokenSource? _restoreCts;

    /// <summary>The copying of the running restore (null while none copies); it discards its unfinished file when canceled.</summary>
    private Task? _restoreRun;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreToOriginalCommand), nameof(RestoreToCommand), nameof(CancelRestoreCommand),
        nameof(DeleteVersionsCommand), nameof(RestoreSelectedToTempCommand), nameof(RestoreVersionToTempCommand))]
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
        RestoreSelectedToTempCommand.NotifyCanExecuteChanged();
    }

    partial void OnVersionStatesChanged()
    {
        OnTreeSelectionChanged();
        RestoreVersionToTempCommand.NotifyCanExecuteChanged();
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

    // A damaged version (a pool version without a usable manifest) has nothing to open or restore.
    private bool CanOpenSelected() => Selection is { Node.IsDirectory: false, Version.IsDamaged: false };

    private bool CanActOnSelected() => ShowExplorerActions && Selection is not null;

    private bool CanRestore() => !IsRestoring && !IsDeleting && Selection is { Version.IsDamaged: false };

    private bool CanRestoreSelectedToTemp() =>
        ShowTempRestore && !IsRestoring && !IsDeleting && Selection is { Node.IsDirectory: true, Version.IsDamaged: false };

    private bool CanRestoreVersionToTemp() =>
        ShowTempRestore && !IsRestoring && !IsDeleting && SelectedVersion is { IsDamaged: false };

    /// <summary>
    /// Opens the copy of the selected file in the version folder with its program (meant read-only); when the version
    /// holds no files to open there (incremental plans, targets that are no folder), through a temporary copy. A
    /// program, script or shortcut is never started from a backup: its folder is shown in Explorer instead, with the
    /// file selected.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    private async Task OpenSelectedAsync()
    {
        if (Current() is not { Node.IsDirectory: false } selection)
            return;
        if (FolderOf(selection.Version) is not { } folder)
        {
            await OpenThroughTempAsync(selection.Version, selection.Node.Path, selection.Node.Name, isFolder: false);
            return;
        }
        var path = selection.Node.Path;
        if (RunnableFiles.IsRunnable(Path.GetExtension(selection.Node.Name)))
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

    /// <summary>Copies the selected folder into the temp folder and opens it in Explorer (incremental plans).</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreSelectedToTemp))]
    private async Task RestoreSelectedToTempAsync()
    {
        if (Current() is not { Node.IsDirectory: true, Version.IsDamaged: false } selection)
            return;
        var (node, version) = selection;
        await RestoreToTempAsync(version, node.Path, node.Name, node.Stats?.Size ?? 0);
    }

    /// <summary>Copies the selected version into the temp folder and opens it in Explorer (incremental plans).</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreVersionToTemp))]
    private async Task RestoreVersionToTempAsync()
    {
        if (SelectedVersion is not { IsDamaged: false } row)
            return;
        await RestoreToTempAsync(row, "", row.Name, row.Info.TotalBytes ?? row.Indexed?.TotalBytes ?? 0);
    }

    /// <summary>"Restore to temp and open" of a folder ("" = the whole version), asking first above 1 GB.</summary>
    private async Task RestoreToTempAsync(VersionRowViewModel version, string relative, string name, long bytes)
    {
        if (IsRestoring)
            return;
        if (bytes > TempRestoreConfirmBytes && !_context.Dialogs.ConfirmDefaultNo(Loc.T("restore.temp.title"),
                Loc.F("restore.temp.confirmLarge", ("item", LocText.Of("restore.item", ("path", name))),
                    ("size", Formats.Bytes(bytes)))))
            return;
        await OpenThroughTempAsync(version, relative, name, isFolder: true);
    }

    /// <summary>
    /// Copies the entry into the temp folder (verified) and opens it, like a restore: the restore buttons are off and
    /// "Cancel" stops it meanwhile. The outcome goes to the footer.
    /// </summary>
    private async Task OpenThroughTempAsync(VersionRowViewModel version, string relative, string name, bool isFolder)
    {
        if (IsRestoring)
            return;
        if (_context.TempOpener is not { } temp || _versionsLocation is not { } location)
        {
            _context.ReportStatus(LocText.Of("restore.cannotOpen", ("path", relative)));
            return;
        }
        var versionPath = version.Info.Path;
        var cts = _restoreCts = new CancellationTokenSource();
        IsRestoring = true;
        RestoreFraction = 0;
        RestoreProgress = LocText.Of("restore.temp.preparing", ("name", name));
        _context.ReportStatus(RestoreProgress);
        LocText outcome;
        try
        {
            var run = Task.Run(async () =>
            {
                var versions = await VersionViews.OpenAsync(_context.Storages.Open(location), versionPath, cts.Token);
                return isFolder
                    ? await temp.OpenFolderAsync(versions, versionPath, relative, cts.Token)
                    : await temp.OpenFileAsync(versions, versionPath, relative, cts.Token);
            });
            _restoreRun = run;
            var opened = await run;
            outcome = !opened ? LocText.Of("restore.temp.notOpened", ("name", name))
                : !isFolder && RunnableFiles.IsRunnable(Path.GetExtension(name)) ? LocText.Of("restore.runnable", ("name", name))
                : LocText.Empty;
        }
        catch (OperationCanceledException)
        {
            outcome = LocText.Of("restore.temp.canceled", ("name", name));
        }
        catch (Exception ex)
        {
            // e.g. the entry is gone, or the target cannot be read
            var reason = ex is ArgumentException { ParamName: { } param }
                ? ex.Message.Replace($" (Parameter '{param}')", "", StringComparison.Ordinal)
                : ex.Message;
            outcome = new LocText(() => Loc.F("restore.temp.failed", ("name", name), ("error", Loc.Known(reason))));
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
        _context.ReportStatus(outcome);
    }

    /// <summary>Shows the selected entry in Explorer, selected in its folder.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private async Task ShowSelectedInExplorerAsync()
    {
        if (Current() is not { } selection)
            return;
        var (folder, path) = (FolderOf(selection.Version) ?? "", selection.Node.Path);
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
        // Planning stops at once (it writes nothing); the copy stops after the current file.
        RestoreProgress = _restoreRun is null ? LocText.Of("restore.canceling") : LocText.Of("restore.cancelingAfterFile");
    }

    /// <summary>
    /// Cancels a running restore (exit, restart) and returns the copy's task, which ends after the current file once
    /// its unfinished copy is discarded; a completed task when nothing is being copied.
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
        var versionPath = version.Info.Path;
        var versionsLocation = _versionsLocation;
        var versionDate = new LocText(() => version.DateText);

        string root;   // the destination as the user reads it: a folder, or a location in a storage
        StorageLocation destinationLocation;
        if (mode == RestoreMode.Original)
        {
            // The manifest's source (a folder); a version without a (readable) manifest goes back to the plan's source.
            // A source that is no folder (e.g. S3) is only named by the manifest (its prefix): it goes back to the plan's.
            var planSource = _savedPlan()?.Source;
            var source = version.Indexed is { Origin: IndexOrigin.Manifest, Source.Length: > 0 } indexed &&
                         (planSource is null or { IsFileSystem: true } || Path.IsPathFullyQualified(indexed.Source))
                ? StorageLocation.FileSystem(indexed.Source)
                : planSource;
            if (source is null || (source.IsFileSystem &&
                    (string.IsNullOrWhiteSpace(source.Path) || !Path.IsPathFullyQualified(source.Path))))
            {
                dialogs.ShowError(Loc.T("restore.title"), Loc.T("restore.originUnknown"));
                return;
            }
            destinationLocation = source;
            root = DisplayRoot(source);
            var warning = _isBackupActive()
                ? Loc.T("restore.backupRunningWarning")
                : "";
            if (!dialogs.ConfirmDefaultNo(Loc.T("restore.confirmTitle"),
                    Loc.F(whole ? "restore.confirmVersion" : "restore.confirm", ("item", item), ("date", versionDate),
                        ("destination", DisplayDestination(source, relative)),
                        ("warning", warning))))
                return;
        }
        else
        {
            if (dialogs.PickFolder(Loc.T("restore.pickFolder"), null) is not { } folder)
                return;
            if (!Path.IsPathFullyQualified(folder))
            {
                dialogs.ShowError(Loc.T("restore.title"), Loc.F(whole ? "restore.cannotRestoreVersion" : "restore.cannotRestore",
                    ("item", item), ("reason", Loc.T("core.restore.destinationNotAbsolute"))));
                return;
            }
            root = folder;
            destinationLocation = StorageLocation.FileSystem(folder);
        }

        // Only folders can lie in a folder target; the plan's checks keep a storage source apart from its target.
        if (destinationLocation.IsFileSystem &&
            InsideTarget(root, mode == RestoreMode.Original ? relative : Path.GetFileName(relative)) is { } target)
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
                if (versionsLocation is null)
                {
                    _context.ReportStatus(LocText.Of("restore.reindexing"));
                    return;
                }
                var factory = _context.Storages;
                var targetStorage = factory.Open(versionsLocation);
                var destination = factory.Open(destinationLocation);
                // A pool version is read through its manifest (its files are in the pool); a full one from its folder.
                plan = await Task.Run(async () => await Restorer.PlanAsync(
                        await VersionViews.OpenAsync(targetStorage, versionPath, cts.Token), versionPath, [relative], destination,
                        mode, cts.Token),
                    cts.Token);
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
                var run = Task.Run(() => Restorer.RunAsync(plan, policy, progress, cts.Token));
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
                    Lines(result.Failures, plan));
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
        var unreadable = Lines(plan.PlanFailures, plan);
        if (plan.Conflicts.Count > 0)
        {
            var examples = string.Join("\n", plan.Conflicts.Take(FailuresInMessage)
                .Select(f => "  " + f.Destination.Replace('/', Path.DirectorySeparatorChar)));
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
                ? ConflictPolicy.Skip   // no conflicts: Skip on purpose, so a file appearing after planning is never replaced
                : null;
        }
        return ConflictPolicy.Skip;   // as above: a file appearing after planning is never replaced
    }

    /// <summary>A restore destination as the user reads it: the folder, or the connection, bucket and prefix of an S3 location.</summary>
    private string DisplayRoot(StorageLocation location) => _context.Describe(location);

    /// <summary>Where <paramref name="relative"/> (a version path) lands below <paramref name="location"/>, for messages.</summary>
    private string DisplayDestination(StorageLocation location, string relative) =>
        location.IsFileSystem
            ? Path.Combine(location.Path, relative.Replace('/', Path.DirectorySeparatorChar))
            : DisplayRoot(location with { Path = StoragePath.Combine(location.Path, relative) });

    /// <summary>
    /// The plan's target when the destination (<paramref name="root"/> + <paramref name="relative"/>) would lie in it:
    /// a restore must not write into the versions. Null otherwise.
    /// </summary>
    private string? InsideTarget(string root, string relative)
    {
        // Only a folder target can hold a folder destination (the plan's checks keep a storage source apart from its target).
        if (_savedPlan()?.Target is not { IsFileSystem: true, Path: { Length: > 0 } target })
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
            return null;   // Restorer.PlanAsync rejects such a path with its own message
        }
    }

    /// <summary>"Restore canceled; nothing was written.", with the parts of the version that could not be read.</summary>
    private static LocText CanceledNothing(RestorePlan plan)
    {
        var unreadable = plan.PlanFailures.Count;
        return new LocText(() => Loc.T("restore.canceledNothing") +
                                 (unreadable == 0 ? "" : Loc.F("restore.unreadableNote", ("count", unreadable))));
    }

    /// <summary>"path: reason" per failure, with the full path where the storage is a folder (the version's or the destination's).</summary>
    private static List<string> Lines(IReadOnlyList<RestoreFailure> failures, RestorePlan plan) =>
        failures.Select(f => $"{FullPath(f.InVersion ? plan.Versions : plan.Destination, f.Path)}: {Loc.Known(f.Reason)}").ToList();

    private static string FullPath(IStorage storage, string path)
    {
        if (storage is not FileSystemStorage folder)
            return path;
        try
        {
            return folder.FullPathOf(path);
        }
        catch (ArgumentException)
        {
            return path;   // a name the folder cannot hold: shown as it is in the version
        }
    }

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
