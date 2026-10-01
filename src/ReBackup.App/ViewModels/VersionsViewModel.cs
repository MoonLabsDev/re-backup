using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Versions;

namespace ReBackup.App.ViewModels;

/// <summary>
/// What every plan's Versions tab shares: the index worker (syncs of one plan's index run one at a time, behind the
/// versions finished runs hand to it), the dialogs and the footer's status line.
/// </summary>
public sealed record VersionsContext(VersionIndexWorker Worker, IDialogService Dialogs, Action<string> ReportStatus)
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
    private bool _loaded;
    private bool _reloadRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isSyncing;

    /// <summary>The sync's progress line, e.g. "Indexing 2 of 5 · 2026_10_01-11_34 MoonLabs · 120,000 files".</summary>
    [ObservableProperty] private string _syncText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private string? _error;

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
        _loaded = false;
        _reloadRequested = false;
        IsSyncing = false;
        SyncText = "";
        Error = null;
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
            Error = "Save the plan to see its versions.";
            return;
        }

        _syncCts?.Cancel();
        var cts = _syncCts = new CancellationTokenSource();
        _reloadRequested = false;
        IsSyncing = true;
        Error = null;
        SyncText = "Reading the target…";
        try
        {
            // Listing the target and reading the index never wait for a sync (the index allows reads beside writes).
            var (index, state, folders, indexed) = await Task.Run(() =>
            {
                var planIndex = _context.Indexes.For(plan.Id);
                var targetState = TargetStateOf(plan.Target);
                var list = targetState == TargetState.Present
                    ? VersionCatalog.List(plan.Target, plan.Id, plan.Name, cts.Token)
                    : [];
                return (planIndex, targetState, list, planIndex.Versions());
            }, cts.Token);
            if (!ReferenceEquals(_syncCts, cts))
                return;

            _index = index;
            if (state == TargetState.Unreachable)
            {
                // An offline target (e.g. a NAS): show nothing, and keep the index as it is.
                ShowFolders([], []);
                SyncText = "";
                Error = TargetUnavailableText(plan.Target);
                return;
            }
            if (state == TargetState.NotCreatedYet)
            {
                // The plan has not run yet (the first run creates the folder): nothing to list or index.
                ShowFolders([], []);
                SyncText = "";
                return;
            }

            ShowFolders(folders, indexed);
            SyncText = "Waiting for the index…";
            var progress = new Progress<IndexSyncProgress>(p =>
            {
                if (ReferenceEquals(_syncCts, cts) && IsSyncing)
                    OnSyncProgress(p);
            });
            // Queued behind other work on this plan's index (another sync, a finished run's version); the ids are
            // read in the same step, after the sync, because a re-import gives a version a new id.
            var (result, versions, targetGone) = await _context.Worker.RunAsync(plan.Id, planIndex =>
            {
                var synced = planIndex.Sync(folders, progress, cts.Token, targetFolder: plan.Target);
                // Errors because the whole target went away (e.g. the NAS went offline mid-sync) are one message.
                var gone = synced.Errors.Count > 0 && !Directory.Exists(plan.Target);
                return (synced, planIndex.Versions(), gone);
            }, cts.Token);
            if (!ReferenceEquals(_syncCts, cts))
                return;

            if (targetGone)
            {
                ApplyIndexed(versions, []);
                SyncText = "";
                Error = TargetUnavailableText(plan.Target);
                return;
            }

            ApplyIndexed(versions, result.Errors);
            SyncText = result.Errors.Count == 0
                ? ""
                : $"{result.Errors.Count} version(s) could not be indexed: {string.Join("; ", result.Errors)}";
            OnIndexReady();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_syncCts, cts))
            {
                Error = ex.Message;
                SyncText = "";
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

    private static string TargetUnavailableText(string target) => $"The target folder \"{target}\" is not available.";

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
        SyncText = progress.Finished
            ? $"Indexing {progress.Current} of {progress.Total}"
            : string.Create(CultureInfo.CurrentCulture,
                $"Indexing {progress.Current} of {progress.Total} · {progress.VersionName} · {progress.FilesImported:N0} files");
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
