using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Versions;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The coverage timeline above the History tab's runs: the owned versions in the saved target under the saved rules,
/// with the run log's warnings. The selected run rings the version it wrote; a click on a slot selects the run that
/// wrote its version. The target is listed (no index sync) off the UI thread when the tab is shown.
/// </summary>
public sealed partial class HistoryCoverageViewModel : ObservableObject
{
    private readonly Func<BackupPlan?> _savedPlan;
    private readonly IStorageFactory _storages;
    private readonly PlanRunViewModel _run;
    private CancellationTokenSource? _loadCts;
    private IReadOnlyList<VersionInfo>? _versions;   // null until listed; empty for a target that is not there yet
    private bool _unreachable;
    private (DateTime StartUtc, string? Version, bool IsRetryLine)? _reselect;   // the selected run while the history is read again

    /// <param name="savedPlan">The plan as saved; null while it is new (a new plan has no versions).</param>
    /// <param name="storages">Opens the plan's target.</param>
    /// <param name="run">The plan's runs: its log (warnings) and the History rows that are selected.</param>
    public HistoryCoverageViewModel(Func<BackupPlan?> savedPlan, IStorageFactory storages, PlanRunViewModel run)
    {
        _savedPlan = savedPlan;
        _storages = storages;
        _run = run;
        run.HistoryLoading += OnHistoryLoading;
        run.HistoryLoaded += OnHistoryLoaded;
    }

    [ObservableProperty] private bool _isLoading;

    /// <summary>The lanes of the saved rules; empty without rules or while the target could not be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoverage))]
    private IReadOnlyList<CoverageLane> _coverageLanes = [];

    /// <summary>The labels of <see cref="CoverageLanes"/>, e.g. "Daily · 7".</summary>
    [ObservableProperty] private IReadOnlyList<string> _coverageLabels = [];

    /// <summary>The target was listed and the saved plan has no rules: the hint replaces the timeline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoRulesText))]
    private bool _coverageWithoutRules;

    /// <summary>The run selected in the History table.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedId), nameof(SelectedDate))]
    private RunHistoryRow? _selectedRun;

    /// <summary>True while the timeline is shown.</summary>
    public bool HasCoverage => CoverageLanes.Count > 0;

    /// <summary>"No retention rules" in place of the timeline; null while the hint is not shown.</summary>
    public string? NoRulesText => CoverageWithoutRules ? Loc.T("retention.coverage.noRules") : null;

    /// <summary>The version the selected run wrote: the one the timeline rings.</summary>
    public string? SelectedId => SelectedRun is { HasVersion: true } run ? run.VersionName : null;

    /// <summary>
    /// When the selected run's backup was taken (the time in its version's name, else its start): the timeline marks
    /// that date also when its slot keeps another version.
    /// </summary>
    public DateTime? SelectedDate => SelectedRun is not { } run ? null
        : run.VersionName is { } name && VersionName.TryParseAny(name, out var time, out _) ? time
        : run.Entry.StartUtc.ToLocalTime();

    /// <summary>The tool tip of a coverage cell.</summary>
    public Func<CoverageSlot, string> CoverageSlotText => slot => CoverageBuilder.SlotText(slot,
        name => _versions?.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.TotalBytes);

    /// <summary>
    /// Called when the tab is shown: lists the target the first time, else builds the lanes again from the listed
    /// versions (the saved rules or the run log may have changed since).
    /// </summary>
    public void EnsureLoaded()
    {
        if (_versions is null)
        {
            if (!IsLoading)
                _ = LoadAsync();
        }
        else
        {
            Rebuild();
        }
    }

    /// <summary>Lists the target again if it was listed before, e.g. after a run of the plan.</summary>
    public void ReloadIfLoaded()
    {
        if (_versions is not null || IsLoading)
            _ = LoadAsync();
    }

    /// <summary>The saved target changed: forgets the versions, and lists the new target if the tab was in use.</summary>
    public void OnSavedTargetChanged()
    {
        var wasLoaded = _versions is not null || IsLoading;
        Invalidate();
        if (wasLoaded)
            _ = LoadAsync();
    }

    /// <summary>Forgets the versions.</summary>
    public void Invalidate()
    {
        _loadCts?.Cancel();
        _loadCts = null;
        _versions = null;
        _unreachable = false;
        IsLoading = false;
        Clear();
    }

    /// <summary>The language changed: the lane labels are read again.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(NoRulesText));
        OnPropertyChanged(nameof(CoverageSlotText));
        if (CoverageLanes.Count > 0)
            CoverageLabels = CoverageLanes.Select(lane => CoverageBuilder.Label(lane.Rule)).ToList();
    }

    /// <summary>
    /// Selects the run that wrote the version a slot holds; nothing when no run in the history wrote it. A retry line names
    /// the version it added to, but did not write it: it is passed over.
    /// </summary>
    [RelayCommand]
    private void SelectCoverage(string? id)
    {
        if (id is null)
            return;
        if (_run.History.FirstOrDefault(r => !r.IsRetryLine && string.Equals(r.VersionName, id, StringComparison.OrdinalIgnoreCase))
            is { } row)
            SelectedRun = row;
    }

    /// <summary>The runs are about to be read again: remembers which run is selected (the rows are replaced).</summary>
    private void OnHistoryLoading() =>
        _reselect = SelectedRun is { } run ? (run.Entry.StartUtc, run.VersionName, run.IsRetryLine) : null;

    /// <summary>
    /// The runs were read again: the same run (start, version, and whether it is a retry line) is selected among the new
    /// rows, none when it is gone; the log's warnings mark the versions again.
    /// </summary>
    private void OnHistoryLoaded()
    {
        var key = _reselect;
        _reselect = null;
        SelectedRun = key is { } k
            ? _run.History.FirstOrDefault(r => r.Entry.StartUtc == k.StartUtc && r.IsRetryLine == k.IsRetryLine &&
                                               string.Equals(r.VersionName, k.Version, StringComparison.OrdinalIgnoreCase))
            : null;
        if (_versions is not null)
            Rebuild();
    }

    /// <summary>The plan was saved or read again from disk: the lanes follow the saved rules (the target is not read again).</summary>
    public void OnSavedPlanChanged()
    {
        if (_versions is not null)
            Rebuild();
    }

    /// <summary>Lists the saved target's versions (again).</summary>
    public Task LoadAsync()
    {
        _loadCts?.Cancel();
        var plan = _savedPlan();
        if (plan is null)
        {
            Invalidate();
            return Task.CompletedTask;
        }
        var cts = _loadCts = new CancellationTokenSource();
        return LoadAsync(plan, cts);
    }

    private async Task LoadAsync(BackupPlan plan, CancellationTokenSource cts)
    {
        IsLoading = true;
        try
        {
            var (versions, unreachable) = await Task.Run(async () =>
            {
                var (state, storage) = await TargetStates.OpenAndProbeAsync(_storages, plan.Target, cts.Token);
                IReadOnlyList<VersionInfo> list = storage is null
                    ? []
                    : await VersionCatalog.ListAsync(storage, plan.Id, plan.Name, cts.Token);
                return (list, state == TargetState.Unreachable);
            }, cts.Token);
            if (!ReferenceEquals(_loadCts, cts))
                return;
            _versions = versions;
            _unreachable = unreachable;
            Rebuild();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // An unreadable target shows no timeline; the History table stays as it is.
            if (ReferenceEquals(_loadCts, cts))
            {
                _versions = [];
                _unreachable = true;
                Clear();
            }
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
                IsLoading = false;
        }
    }

    /// <summary>The lanes of the listed versions under the saved rules and the run log as now loaded.</summary>
    private void Rebuild()
    {
        var plan = _savedPlan();
        // An unreachable target (e.g. an offline NAS) lists no versions: its slots would all read "before the first backup".
        if (plan is null || _versions is not { } versions || _unreachable)
        {
            Clear();
            return;
        }
        IReadOnlyList<CoverageLane> lanes;
        IReadOnlyList<string> labels;
        try
        {
            (lanes, labels) = CoverageBuilder.Build(versions, plan.Retention, _run.Entries, DateOnly.FromDateTime(DateTime.Now));
        }
        catch (ArgumentException)
        {
            (lanes, labels) = ([], []);
        }
        CoverageLabels = labels;
        CoverageLanes = lanes;
        CoverageWithoutRules = plan.Retention.Count == 0;
    }

    private void Clear()
    {
        CoverageLanes = [];
        CoverageLabels = [];
        CoverageWithoutRules = false;
    }
}
