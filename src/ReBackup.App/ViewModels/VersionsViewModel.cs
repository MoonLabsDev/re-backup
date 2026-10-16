using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.App.Services;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Versions;
using ReBackup.Shared.Retention;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;

namespace ReBackup.App.ViewModels;

/// <summary>
/// What every plan's Versions tab shares: the index worker (syncs of one plan's index run one at a time, behind the
/// versions finished runs hand to it), the dialogs, the footer's status line and the app's one storage factory.
/// <paramref name="MachineName"/> is this computer as markers name it (<see cref="Environment.MachineName"/> when null):
/// folders that other computers marked are listed apart. <paramref name="DescribeLocation"/> names a location in
/// messages (an S3 location by its connection, bucket and prefix); a folder's path, else <c>kind://path</c>, when null.
/// <paramref name="TempOpener"/> opens files and folders of versions that cannot be opened in place (incremental plans,
/// targets that are no folder) through a temporary copy; without it they are not opened.
/// </summary>
public sealed record VersionsContext(VersionIndexWorker Worker, IDialogService Dialogs, Action<LocText> ReportStatus,
    IStorageFactory Storages, string? MachineName = null, Func<StorageLocation, string>? DescribeLocation = null,
    ITempOpener? TempOpener = null)
{
    /// <summary>This computer, as markers name it.</summary>
    public string Host => MachineName ?? Environment.MachineName;

    /// <summary>A location as the user reads it.</summary>
    public string Describe(StorageLocation location) =>
        DescribeLocation?.Invoke(location) ?? (location.IsFileSystem ? location.Path : $"{location.Kind}://{location.Path}");

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
    private readonly Func<bool> _isCopyActive;
    private readonly IFolderOpener _files;
    private readonly VersionsContext _context;
    private readonly Func<IReadOnlyList<RunLogEntry>> _runLog;
    private CancellationTokenSource? _syncCts;
    private VersionIndex? _index;
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
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(Error), nameof(HasCoverage), nameof(NoRulesText))]
    private LocText? _errorText;

    /// <summary>The coverage timeline of the listed owned versions under the saved rules.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoverage))]
    private IReadOnlyList<CoverageLane> _coverageLanes = [];

    /// <summary>The labels of <see cref="CoverageLanes"/>, e.g. "Daily · 7".</summary>
    [ObservableProperty] private IReadOnlyList<string> _coverageLabels = [];

    /// <summary>The target was listed and the saved plan has no rules: the hint replaces the timeline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoRulesText))]
    private bool _coverageWithoutRules;

    /// <summary>True while the coverage timeline is shown; hidden when the target could not be read.</summary>
    public bool HasCoverage => CoverageLanes.Count > 0 && Error is null;

    /// <summary>"No retention rules" in place of the timeline; null while the hint is not shown.</summary>
    public string? NoRulesText => CoverageWithoutRules && Error is null ? Loc.T("retention.coverage.noRules") : null;

    /// <summary>The tool tip of a coverage cell.</summary>
    public Func<CoverageSlot, string> CoverageSlotText => slot => CoverageBuilder.SlotText(slot,
        name => VersionRows.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Info.TotalBytes);

    public string SyncText => Sync.ToString();

    public string? Error => ErrorText?.ToString();

    /// <summary>The language changed: the tab's lines, rows, tree, search and history are read again.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(string.Empty);
        foreach (var row in VersionRows)
            row.Refresh();
        foreach (var row in ForeignRows)
            row.Refresh();
        if (CoverageLanes.Count > 0)
            CoverageLabels = CoverageLanes.Select(lane => CoverageBuilder.Label(lane.Rule)).ToList();
        OnRefreshTexts();
    }

    /// <summary>The tree part's share of <see cref="RefreshTexts"/>.</summary>
    partial void OnRefreshTexts();

    /// <summary>Version A: the version whose tree is shown.</summary>
    [ObservableProperty] private VersionRowViewModel? _selectedVersion;

    /// <param name="savedPlan">The plan as saved; null while it is new (a new plan has no versions).</param>
    /// <param name="isBackupActive">True while a backup of this plan is queued or running.</param>
    /// <param name="runLog">The plan's run log, oldest first: versions whose run ended with warnings show so in the coverage.</param>
    /// <param name="isCopyActive">True while "Copy plan…" copies this plan's versions (none are deleted meanwhile).</param>
    public VersionsViewModel(Func<BackupPlan?> savedPlan, Func<bool> isBackupActive, IFolderOpener files,
        VersionsContext context, Func<IReadOnlyList<RunLogEntry>>? runLog = null, Func<bool>? isCopyActive = null)
    {
        _runLog = runLog ?? (() => []);
        _savedPlan = savedPlan;
        _isBackupActive = isBackupActive;
        _isCopyActive = isCopyActive ?? (() => false);
        _files = files;
        _context = context;
        OnCreated();
    }

    /// <summary>Runs at the end of the constructor (the tree part subscribes to its tree here).</summary>
    partial void OnCreated();

    /// <summary>The version folders of the target, newest first.</summary>
    public RangeObservableCollection<VersionRowViewModel> VersionRows { get; } = new();

    /// <summary>
    /// The saved plan is incremental: its version folders hold only the manifest (the files are in the pool), so they
    /// are never shown in Explorer, and files and folders are opened through a temporary copy.
    /// </summary>
    private bool IsPoolPlan => _savedPlan()?.Incremental == true;

    /// <summary>"Show in Explorer" (and the history's "show this copy"); hidden for incremental plans.</summary>
    public bool ShowExplorerActions => !IsPoolPlan;

    /// <summary>"Restore to temp and open" for folders and versions, in place of Explorer; incremental plans only.</summary>
    public bool ShowTempRestore => IsPoolPlan;

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
        _versionsLocation = null;
        _loaded = false;
        _reloadRequested = false;
        IsSyncing = false;
        Sync = LocText.Empty;
        ErrorText = null;
        SelectedVersion = null;
        VersionRows.ReplaceAll([]);
        ShowForeign([]);
        ShowCoverage(null);
        ShowPoolStatsOf(null);
        ClearMeasurement(cancel: true);
        OnPropertyChanged(nameof(ShowEmpty));
    }

    /// <summary>Selects the version a coverage slot holds (the tree loads it).</summary>
    [RelayCommand]
    private void SelectCoverage(string? id)
    {
        if (VersionRows.FirstOrDefault(r => r.Name.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } row)
            SelectedVersion = row;
    }

    /// <summary>
    /// The coverage of the listed folders under the saved rules of <paramref name="plan"/>; none without a plan, and none
    /// while the saved rules are not valid.
    /// </summary>
    private void ShowCoverage(BackupPlan? plan)
    {
        IReadOnlyList<CoverageLane> lanes = [];
        IReadOnlyList<string> labels = [];
        if (plan is not null)
        {
            try
            {
                (lanes, labels) = CoverageBuilder.Build(VersionRows.Select(r => r.Info).ToList(), plan.Retention, _runLog(),
                    DateOnly.FromDateTime(DateTime.Now));
            }
            catch (ArgumentException)
            {
                (lanes, labels) = ([], []);
            }
        }
        CoverageLabels = labels;
        CoverageLanes = lanes;
        CoverageWithoutRules = plan is not null && plan.Retention.Count == 0;
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
        OnPropertyChanged(nameof(ShowExplorerActions));
        OnPropertyChanged(nameof(ShowTempRestore));
        OnPropertyChanged(nameof(ShowPoolStats));
        OnPropertyChanged(nameof(PoolSummary));
        IsSyncing = true;
        ErrorText = null;
        Sync = LocText.Of("versions.sync.reading");
        try
        {
            // Listing the target and reading the index never wait for a sync (the index allows reads beside writes).
            var (index, state, target, folders, foreign, indexed, pool) = await Task.Run(async () =>
            {
                var planIndex = _context.Indexes.For(plan.Id);
                var (targetState, storage) = await TargetStates.OpenAndProbeAsync(_context.Storages, plan.Target, cts.Token);
                IReadOnlyList<VersionInfo> list = storage is null
                    ? []
                    : await VersionCatalog.ListAsync(storage, plan.Id, plan.Name, cts.Token);
                IReadOnlyList<ForeignMarkedFolder> marked = storage is null
                    ? []
                    : await VersionCatalog.ListForeignMarkedAsync(storage, plan.Id, plan.Name, _context.Host, cts.Token);
                (long, long)? stats = plan.Incremental ? planIndex.PoolStats() : null;
                return (planIndex, targetState, storage, list, marked, planIndex.Versions(), stats);
            }, cts.Token);
            if (!ReferenceEquals(_syncCts, cts))
                return;

            _index = index;
            _versionsLocation = plan.Target;
            ShowPoolStatsOf(pool);   // at once from the index; again after the sync
            if (state == TargetState.Unreachable)
            {
                // An offline target (e.g. a NAS): show nothing, and keep the index as it is.
                ShowFolders([], [], plan);
                ShowForeign([]);
                Sync = LocText.Empty;
                ErrorText = TargetUnavailable(plan.Target);
                return;
            }
            if (state == TargetState.NotCreatedYet)
            {
                // The plan has not run yet (the first run creates the folder): nothing to list or index.
                ShowFolders([], [], plan);
                ShowForeign([]);
                Sync = LocText.Empty;
                return;
            }

            ShowFolders(folders, indexed, plan);
            ShowForeign(foreign);
            Sync = LocText.Of("versions.sync.waiting");
            var progress = new Progress<IndexSyncProgress>(p =>
            {
                if (ReferenceEquals(_syncCts, cts) && IsSyncing)
                    OnSyncProgress(p);
            });
            // Queued behind other work on this plan's index (another sync, a finished run's version); the ids are
            // read in the same step, after the sync, because a re-import gives a version a new id. A pool version
            // without a usable manifest is not scanned (its folder holds no files): it is indexed as damaged.
            var (result, versions, targetGone, exclusive, poolStats) = await _context.Worker.RunAsync(plan.Id, planIndex =>
            {
                var synced = planIndex.Sync(folders, target!, progress, cts.Token, scanFallback: !plan.Incremental);
                // Errors because the whole target went away (e.g. the NAS went offline mid-sync) are one message.
                // The worker runs on a pool thread, so waiting for the probe blocks no UI thread.
                var gone = synced.Errors.Count > 0 &&
                    TargetStates.ProbeAsync(target!, cts.Token).GetAwaiter().GetResult() != TargetState.Present;
                var indexedVersions = planIndex.Versions();
                IReadOnlyDictionary<string, long>? unique = plan.Incremental
                    ? indexedVersions.ToDictionary(v => v.Name, v => planIndex.UniqueBytes(v.Id), StringComparer.OrdinalIgnoreCase)
                    : null;
                (long, long)? stats = plan.Incremental ? planIndex.PoolStats() : null;
                return (synced, indexedVersions, gone, unique, stats);
            }, cts.Token);
            if (!ReferenceEquals(_syncCts, cts))
                return;

            ShowPoolStatsOf(poolStats);   // a measurement stays unless the stats changed

            if (targetGone)
            {
                ApplyIndexed(versions, [], exclusive);
                Sync = LocText.Empty;
                ErrorText = TargetUnavailable(plan.Target);
                return;
            }

            ApplyIndexed(versions, result.Errors, exclusive);
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

    /// <summary>
    /// The full path of a listed version's folder for Explorer, which works on local folders only: null for a target
    /// that is no file system location (or a name that is no path), and for incremental plans, whose version folders
    /// hold no files.
    /// </summary>
    private string? FolderOf(VersionRowViewModel row)
    {
        if (IsPoolPlan || _versionsLocation is not { IsFileSystem: true } location)
            return null;
        try
        {
            return new FileSystemStorage(location.Path).FullPathOf(row.Info.Path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private LocText TargetUnavailable(StorageLocation target) =>
        LocText.Of("core.index.targetUnavailable", ("folder", _context.Describe(target)));

    /// <summary>Runs after every successful sync; the tree reloads here (Versions tab, tree part).</summary>
    partial void OnIndexReady();

    /// <summary>
    /// Shows the listed folders newest first, reusing rows by name so the selection stays, and their coverage under the
    /// saved rules of <paramref name="plan"/>.
    /// </summary>
    private void ShowFolders(IReadOnlyList<VersionInfo> folders, IReadOnlyList<IndexedVersion> indexed, BackupPlan plan)
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
        ShowCoverage(plan);
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

    /// <param name="exclusive">The exclusive bytes of the indexed versions by name (incremental plans); null otherwise.</param>
    private void ApplyIndexed(IReadOnlyList<IndexedVersion> versions, IReadOnlyList<string> errors,
        IReadOnlyDictionary<string, long>? exclusive)
    {
        var byName = versions.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var row in VersionRows)
        {
            row.Indexed = byName.GetValueOrDefault(row.Name);
            row.IndexState = row.Indexed is not null ? IndexState.Indexed
                : errors.Any(e => e.StartsWith(row.Name + ":", StringComparison.OrdinalIgnoreCase)) ? IndexState.Failed
                : IndexState.NotIndexed;
            row.ExclusiveBytes = exclusive is not null && exclusive.TryGetValue(row.Name, out var bytes) ? bytes : null;
        }
        OnVersionStatesChanged();   // a version may have turned out damaged
    }

    /// <summary>Runs when the rows' index states changed (the actions on versions and entries hook in here).</summary>
    partial void OnVersionStatesChanged();
}
