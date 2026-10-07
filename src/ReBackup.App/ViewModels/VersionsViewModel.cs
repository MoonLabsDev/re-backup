using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.App.Services;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Versions;
using ReBackup.Storage;

namespace ReBackup.App.ViewModels;

/// <summary>
/// What every plan's Versions tab shares: the index worker (syncs of one plan's index run one at a time, behind the
/// versions finished runs hand to it), the dialogs and the footer's status line.
/// </summary>
public sealed record VersionsContext(VersionIndexWorker Worker, IDialogService Dialogs, Action<LocText> ReportStatus)
{
    /// <summary>The plans' indexes; reads (queries) may run beside a sync.</summary>
    public VersionIndexSet Indexes => Worker.Indexes;
}

/// <summary>
/// The Versions tab of one plan: the version folders of its saved target and their local index. The target is read
/// and the index synced off the UI thread whenever the tab is shown or refreshed.
/// </summary>
public sealed partial class VersionsViewModel : ObservableObject
{
    private readonly Func<BackupPlan?> _savedPlan;
    private readonly Func<bool> _isBackupActive;
    private readonly IFolderOpener _files;
    private readonly VersionsContext _context;
    private CancellationTokenSource? _syncCts;
    private VersionIndex? _index;
    /// <summary>The target folder the shown rows were listed from; their <see cref="VersionInfo.Path"/> is relative to it.</summary>
    private string? _versionsTarget;
    /// <summary>The location the shown rows were listed from (the restore reads the versions there).</summary>
    private StorageLocation? _versionsLocation;
    private bool _loaded;
    private bool _reloadRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isSyncing;

    /// <summary>The sync's progress line, e.g. "Indexing 2 of 5 · 2026_10_01-11_34 MoonLabs · 120,000 files".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncText))]
    private LocText _sync = LocText.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(Error))]
    private LocText? _errorText;

    public string SyncText => Sync.ToString();

    public string? Error => ErrorText?.ToString();

    /// <summary>The language changed: the tab's lines, rows, tree, search and history are read again.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(string.Empty);
        foreach (var row in VersionRows)
            row.Refresh();
        OnRefreshTexts();
    }

    /// <summary>The tree part's share of <see cref="RefreshTexts"/>.</summary>
    partial void OnRefreshTexts();

    /// <summary>Version A: the version whose tree is shown.</summary>
    [ObservableProperty] private VersionRowViewModel? _selectedVersion;

    /// <param name="savedPlan">The plan as saved; null while it is new (a new plan has no versions).</param>
    /// <param name="isBackupActive">True while a backup of this plan is queued or running.</param>
    public VersionsViewModel(Func<BackupPlan?> savedPlan, Func<bool> isBackupActive, IFolderOpener files,
        VersionsContext context)
    {
        _savedPlan = savedPlan;
        _isBackupActive = isBackupActive;
        _files = files;
        _context = context;
        OnCreated();
    }

    /// <summary>Runs at the end of the constructor (the tree part subscribes to its tree here).</summary>
    partial void OnCreated();

    /// <summary>The version folders of the target, newest first.</summary>
    public RangeObservableCollection<VersionRowViewModel> VersionRows { get; } = new();

    /// <summary>The target was read and holds no versions.</summary>
    public bool ShowEmpty => _loaded && VersionRows.Count == 0 && !IsSyncing && Error is null;

    /// <summary>Reads the target and syncs the index; called whenever the tab is shown. Does nothing while a sync runs.</summary>
    public void EnsureLoaded()
    {
        if (!IsSyncing)
            _ = RefreshAsync();
    }

    /// <summary>
    /// Reads the target again if the tab has been shown before, e.g. after a run of the plan. While a sync runs, the
    /// read follows when it ends.
    /// </summary>
    public void ReloadIfLoaded()
    {
        if (IsSyncing)
            _reloadRequested = true;
        else if (_loaded)
            _ = RefreshAsync();
    }

    /// <summary>The saved target changed: forget the versions, and read the new target if the tab was in use.</summary>
    public void OnSavedTargetChanged()
    {
        var wasLoaded = _loaded || IsSyncing;
        Invalidate();
        if (wasLoaded)
            _ = RefreshAsync();
    }

    /// <summary>Forgets the versions.</summary>
    public void Invalidate()
    {
        _syncCts?.Cancel();
        _syncCts = null;
        _index = null;
        _versionsTarget = null;
        _versionsLocation = null;
        _loaded = false;
        _reloadRequested = false;
        IsSyncing = false;
        Sync = LocText.Empty;
        ErrorText = null;
        SelectedVersion = null;
        VersionRows.ReplaceAll([]);
        OnPropertyChanged(nameof(ShowEmpty));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var plan = _savedPlan();
        if (plan is null)
        {
            Invalidate();
            ErrorText = LocText.Of("versions.savePlan");
            return;
        }

        _syncCts?.Cancel();
        var cts = _syncCts = new CancellationTokenSource();
        _reloadRequested = false;
        IsSyncing = true;
        ErrorText = null;
        Sync = LocText.Of("versions.sync.reading");
        try
        {
            // Listing the target and reading the index never wait for a sync (the index allows reads beside writes).
            var (index, state, target, folders, indexed) = await Task.Run(async () =>
            {
                var planIndex = _context.Indexes.For(plan.Id);
                var targetState = TargetStateOf(plan.Target.Path);
                var storage = targetState == TargetState.Present ? new StorageFactory().Open(plan.Target) : null;
                IReadOnlyList<VersionInfo> list = storage is null
                    ? []
                    : await VersionCatalog.ListAsync(storage, plan.Id, plan.Name, cts.Token);
                return (planIndex, targetState, storage, list, planIndex.Versions());
            }, cts.Token);
            if (!ReferenceEquals(_syncCts, cts))
                return;

            _index = index;
            _versionsTarget = plan.Target.Path;
            _versionsLocation = plan.Target;
            if (state == TargetState.Unreachable)
            {
                // An offline target (e.g. a NAS): show nothing, and keep the index as it is.
                ShowFolders([], []);
                Sync = LocText.Empty;
                ErrorText = TargetUnavailable(plan.Target.Path);
                return;
            }
            if (state == TargetState.NotCreatedYet)
            {
                // The plan has not run yet (the first run creates the folder): nothing to list or index.
                ShowFolders([], []);
                Sync = LocText.Empty;
                return;
            }

            ShowFolders(folders, indexed);
            Sync = LocText.Of("versions.sync.waiting");
            var progress = new Progress<IndexSyncProgress>(p =>
            {
                if (ReferenceEquals(_syncCts, cts) && IsSyncing)
                    OnSyncProgress(p);
            });
            // Queued behind other work on this plan's index (another sync, a finished run's version); the ids are
            // read in the same step, after the sync, because a re-import gives a version a new id.
            var (result, versions, targetGone) = await _context.Worker.RunAsync(plan.Id, planIndex =>
            {
                var synced = planIndex.Sync(folders, target!, progress, cts.Token);
                // Errors because the whole target went away (e.g. the NAS went offline mid-sync) are one message.
                var gone = synced.Errors.Count > 0 && !Directory.Exists(plan.Target.Path);
                return (synced, planIndex.Versions(), gone);
            }, cts.Token);
            if (!ReferenceEquals(_syncCts, cts))
                return;

            if (targetGone)
            {
                ApplyIndexed(versions, []);
                Sync = LocText.Empty;
                ErrorText = TargetUnavailable(plan.Target.Path);
                return;
            }

            ApplyIndexed(versions, result.Errors);
            var errors = result.Errors;
            Sync = errors.Count == 0
                ? LocText.Empty
                : LocText.Of("versions.sync.errors", ("count", errors.Count), ("errors", string.Join("; ", errors)));
            OnIndexReady();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_syncCts, cts))
            {
                ErrorText = LocText.Known(ex.Message);
                Sync = LocText.Empty;
            }
        }
        finally
        {
            if (ReferenceEquals(_syncCts, cts))
            {
                _loaded = true;
                IsSyncing = false;
                if (_reloadRequested)
                    _ = RefreshAsync();
            }
        }
    }

    private enum TargetState { Present, NotCreatedYet, Unreachable }

    /// <summary>
    /// Present: the target folder exists. NotCreatedYet: it does not, but its drive or share does (a plan that has not
    /// run yet). Unreachable: the drive or share itself is missing, e.g. an offline NAS. Touches the disk: call it off
    /// the UI thread.
    /// </summary>
    private static TargetState TargetStateOf(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return TargetState.Unreachable;
        if (Directory.Exists(target))
            return TargetState.Present;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(target));
            return !string.IsNullOrEmpty(root) && Directory.Exists(root) ? TargetState.NotCreatedYet : TargetState.Unreachable;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return TargetState.Unreachable;
        }
    }

    /// <summary>The full path of a listed version's folder (for Explorer and the restore, which work on folders).</summary>
    private string FolderOf(VersionRowViewModel row) => Path.Combine(_versionsTarget ?? "", row.Info.Path);

    private static LocText TargetUnavailable(string target) => LocText.Of("core.index.targetUnavailable", ("folder", target));

    /// <summary>Runs after every successful sync; the tree reloads here (Versions tab, tree part).</summary>
    partial void OnIndexReady();

    /// <summary>Shows the listed folders newest first, reusing rows by name so the selection stays.</summary>
    private void ShowFolders(IReadOnlyList<VersionInfo> folders, IReadOnlyList<IndexedVersion> indexed)
    {
        var known = VersionRows.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var byName = indexed.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        var rows = new List<VersionRowViewModel>();
        foreach (var folder in folders.Reverse())
        {
            if (!known.TryGetValue(folder.Name, out var row))
                row = new VersionRowViewModel(folder);
            row.Info = folder;
            row.Indexed = byName.GetValueOrDefault(folder.Name);
            row.IndexState = row.Indexed is null ? IndexState.NotIndexed : IndexState.Indexed;
            rows.Add(row);
        }

        var selectedName = SelectedVersion?.Name;
        VersionRows.ReplaceAll(rows);
        SelectedVersion = rows.FirstOrDefault(r => r.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
                          ?? rows.FirstOrDefault();
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private void OnSyncProgress(IndexSyncProgress progress)
    {
        var row = VersionRows.FirstOrDefault(r => r.Name.Equals(progress.VersionName, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            row.IndexState = progress.Finished ? IndexState.Indexed : IndexState.Indexing;
            if (!progress.Finished)
                row.Indexed = null;   // being imported again: its old id is gone
        }
        Sync = progress.Finished
            ? LocText.Of("versions.sync.indexing", ("current", progress.Current), ("total", progress.Total))
            : LocText.Of("versions.sync.indexingVersion", ("current", progress.Current), ("total", progress.Total),
                ("version", progress.VersionName), ("files", Formats.Files(progress.FilesImported)));
    }

    private void ApplyIndexed(IReadOnlyList<IndexedVersion> versions, IReadOnlyList<string> errors)
    {
        var byName = versions.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var row in VersionRows)
        {
            row.Indexed = byName.GetValueOrDefault(row.Name);
            row.IndexState = row.Indexed is not null ? IndexState.Indexed
                : errors.Any(e => e.StartsWith(row.Name + ":", StringComparison.OrdinalIgnoreCase)) ? IndexState.Failed
                : IndexState.NotIndexed;
        }
    }
}
