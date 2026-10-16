using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Wpf.Controls;
using ReBackup.App.Services;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Retention;
using ReBackup.Core.Versions;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.App.ViewModels;

/// <summary>
/// A backup frequency assumed for the full-extension preview of a plan without triggers. <paramref name="Key"/> names
/// its labels: <c>retention.scheduleLabel.&lt;Key&gt;</c> goes into sentences ("one backup a day"),
/// <c>retention.scheduleRuns.&lt;Key&gt;</c> after "two years of" in the forecast title (the view binds it).
/// </summary>
public sealed record AssumedSchedule(string Key, TimeSpan Interval)
{
    /// <summary>The frequency as it goes into a sentence, in the applied language.</summary>
    public string Label => Loc.T("retention.scheduleLabel." + Key);
}

/// <summary>Shows what a plan's retention rules do with the versions in its target.</summary>
public sealed partial class RetentionPreviewViewModel : ObservableObject
{
    private static readonly TimeSpan EvaluateDelay = TimeSpan.FromMilliseconds(300);

    private readonly Func<BackupPlan> _plan;
    private readonly Func<long?> _fallbackVersionBytes;
    private readonly IFolderOpener _folders;
    private readonly IStorageFactory _storages;
    private readonly Func<IReadOnlyList<RunLogEntry>> _runLog;
    private readonly Func<BackupPlan, VersionIndex?> _poolIndex;
    private readonly Action _requestIndexSync;
    private IReadOnlyList<VersionInfo>? _versions;
    private string? _versionsTarget;   // belongs to _versions: the target they were read from
    private bool _targetMissing;   // belongs to _versions: the target folder did not exist when they were read
    private bool _targetUnreachable;   // belongs to _versions: the target could not be reached (not merely not created yet)
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _evaluateCts;
    private CancellationTokenSource? _simulateCts;
    private CancellationTokenSource? _poolCts;
    private bool _syncRequested;   // belongs to _versions: a sync of the index was asked for since they were read

    // The "now" numbers of the last evaluation; for an incremental plan the pool numbers follow from the index.
    private NowNumbers? _nowNumbers;
    private PoolInputs? _poolInputs;   // null for a full plan
    private PoolFigures? _poolFigures;   // null while the pool numbers are being read
    private ShownSimulation? _shown;   // the forecast shown, to show again when the pool numbers arrive

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(HasCoverage), nameof(NoRulesText))]
    private LocText? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NowSummary))]
    private LocText _now = LocText.Of("retention.now.notLoaded");

    [ObservableProperty] private AssumedSchedule _selectedSchedule = Schedules[1];
    [ObservableProperty] private bool _usesPlanSchedule;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullSummary))]
    private LocText _full = LocText.Empty;

    public string? Error => ErrorText?.ToString();

    public string NowSummary => Now.ToString();

    public string FullSummary => Full.ToString();

    /// <summary>The language changed: the summaries, rows and timeline are built again (the target is not read again).</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(string.Empty);
        if (_versions is null)
            return;
        try
        {
            Evaluate();
        }
        catch (Exception ex)
        {
            ErrorText = LocText.Known(ex.Message);
        }
    }

    /// <summary>Versions the target holds at most once the rules are in full effect; null while there is no forecast.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForecast), nameof(ForecastVersionsText))]
    private int? _forecastVersions;

    /// <summary>Space those versions take; null while unknown (no version size known yet) or without a forecast.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForecastSpaceText))]
    private long? _forecastBytes;

    [ObservableProperty] private IReadOnlyList<TimelineLane> _timelineLanes = [];

    /// <summary>The coverage timeline: per rule, its slots and whether a version holds each (the rules as edited).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoverage))]
    private IReadOnlyList<CoverageLane> _coverageLanes = [];

    /// <summary>The labels of <see cref="CoverageLanes"/>, e.g. "Daily · 7".</summary>
    [ObservableProperty] private IReadOnlyList<string> _coverageLabels = [];

    /// <summary>The versions were read and the edited plan has no rules: the hint replaces the timeline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoRulesText))]
    private bool _coverageWithoutRules;

    /// <summary>The row of the "now" table that is selected (a click on a coverage slot selects its version here).</summary>
    [ObservableProperty] private RetentionNowRow? _selectedNowRow;

    /// <summary>True while the coverage timeline is shown; hidden when the target could not be read.</summary>
    public bool HasCoverage => CoverageLanes.Count > 0 && Error is null;

    /// <summary>"No retention rules" in place of the timeline; null while the hint is not shown.</summary>
    public string? NoRulesText => CoverageWithoutRules && Error is null ? Loc.T("retention.coverage.noRules") : null;

    /// <summary>The tool tip of a coverage cell.</summary>
    public Func<CoverageSlot, string> CoverageSlotText => slot => CoverageBuilder.SlotText(slot,
        name => _versions?.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.TotalBytes);
    [ObservableProperty] private DateTime _timelineFrom;
    [ObservableProperty] private DateTime _timelineTo;

    /// <param name="plan">Gives the plan as currently edited.</param>
    /// <param name="fallbackVersionBytes">Size of one version when the target has none yet; null when unknown.</param>
    /// <param name="folders">Opens a version folder in Explorer.</param>
    /// <param name="storages">Opens the plan's target.</param>
    /// <param name="runLog">The plan's run log, oldest first: versions whose run ended with warnings show so in the coverage.</param>
    /// <param name="poolIndex">
    /// The version index of a plan (called on a worker thread); null while the plan has none (e.g. it is new). The pool
    /// numbers of an incremental plan are read from it.
    /// </param>
    /// <param name="requestIndexSync">
    /// Asks for a sync of the plan's index because it lacks versions the target holds; the end of a sync is reported to
    /// <see cref="OnIndexSynced"/>.
    /// </param>
    public RetentionPreviewViewModel(Func<BackupPlan> plan, Func<long?> fallbackVersionBytes, IFolderOpener folders,
        IStorageFactory storages, Func<IReadOnlyList<RunLogEntry>>? runLog = null,
        Func<BackupPlan, VersionIndex?>? poolIndex = null, Action? requestIndexSync = null)
    {
        _poolIndex = poolIndex ?? (_ => null);
        _requestIndexSync = requestIndexSync ?? (() => { });
        _storages = storages;
        _runLog = runLog ?? (() => []);
        _plan = plan;
        _fallbackVersionBytes = fallbackVersionBytes;
        _folders = folders;
    }

    /// <summary>
    /// True when the target was read successfully and holds no versions. Depends on the versions read, not on the
    /// rows: invalid rules (briefly while typing) clear the rows of a target that does hold versions.
    /// </summary>
    public bool ShowEmpty => _versions is { Count: 0 } && !IsLoading && Error is null;

    /// <summary>The empty-state text shown over the table while <see cref="ShowEmpty"/> is true.</summary>
    public string EmptyText => _targetMissing ? Loc.T("retention.now.targetMissing") : Loc.T("retention.now.empty");

    /// <summary>True while the forecast numbers are shown; otherwise <see cref="FullSummary"/> says why there are none.</summary>
    public bool HasForecast => ForecastVersions is not null;

    /// <summary>The "Versions kept" number of the forecast, e.g. "≈ 21".</summary>
    public string ForecastVersionsText => ForecastVersions is { } count ? string.Create(Loc.Culture, $"≈ {count:N0}") : "";

    /// <summary>
    /// The "Space" number of the forecast, e.g. "≈ 286.0 GB"; "unknown" while no version size is known. For an
    /// incremental plan the pool's estimate, e.g. "≈ 61 MB (pool, estimated)", and "calculating…" until it is known.
    /// </summary>
    public string ForecastSpaceText =>
        _poolInputs is not null && _shown is not null && _poolFigures is null ? Loc.T("retention.pool.calculating")
        : ForecastBytes is not { } bytes ? Loc.T("retention.forecast.unknown")
        : _poolInputs is not null ? Loc.F("retention.forecast.poolSpace", ("size", Formats.Bytes(bytes)))
        : "≈ " + Formats.Bytes(bytes);

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    partial void OnErrorTextChanged(LocText? value) => OnPropertyChanged(nameof(ShowEmpty));

    public static IReadOnlyList<AssumedSchedule> Schedules { get; } =
    [
        new("weekly", TimeSpan.FromDays(7)),
        new("daily", TimeSpan.FromDays(1)),
        new("twiceDaily", TimeSpan.FromHours(12)),
        new("every4Hours", TimeSpan.FromHours(4)),
        new("hourly", TimeSpan.FromHours(1)),
    ];

    partial void OnSelectedScheduleChanged(AssumedSchedule value) => RequestEvaluate();

    /// <summary>The version folders in the target, newest first.</summary>
    public RangeObservableCollection<RetentionNowRow> NowRows { get; } = new();

    /// <summary>Reads the target if that has not happened yet. Called when the tab is shown.</summary>
    public void EnsureLoaded()
    {
        if (_versions is null && !IsLoading)
            _ = LoadAsync();
    }

    /// <summary>Reads the target again if its versions are shown, e.g. after a run of the plan.</summary>
    public void ReloadIfLoaded()
    {
        if (_versions is not null || IsLoading)
            _ = LoadAsync();
    }

    /// <summary>Forgets the versions, e.g. after the target folder was edited.</summary>
    public void Invalidate()
    {
        _loadCts?.Cancel();
        _loadCts = null;   // the aborted load must not touch the state below any more
        _evaluateCts?.Cancel();
        _versions = null;
        _versionsTarget = null;
        OnPropertyChanged(nameof(ShowEmpty));
        _targetMissing = false;
        _targetUnreachable = false;
        _syncRequested = false;
        IsLoading = false;
        ErrorText = null;
        NowRows.ReplaceAll([]);
        ClearPool();
        Now = LocText.Of("retention.now.notLoaded");
        ClearSimulation();
        ClearCoverage();
    }

    /// <summary>Selects the "now" row of the version a coverage slot holds.</summary>
    [RelayCommand]
    private void SelectCoverage(string? id)
    {
        if (NowRows.FirstOrDefault(r => r.Name.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } row)
            SelectedNowRow = row;
    }

    /// <summary>Applies the edited rules again after a short pause in typing. The target is not read again.</summary>
    public async void RequestEvaluate()
    {
        if (_versions is null)
            return;

        _evaluateCts?.Cancel();
        var cts = _evaluateCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(EvaluateDelay, cts.Token);
            Evaluate();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorText = LocText.Known(ex.Message);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    /// <summary>
    /// Shows the row's version folder in Explorer (checked and opened on a worker thread); reads the target again
    /// when the folder is gone.
    /// </summary>
    [RelayCommand]
    private async Task OpenVersionFolderAsync(RetentionNowRow? row)
    {
        if (row is null || _versionsTarget is not { } target)
            return;
        var name = row.Name;
        if (!await Task.Run(() => _folders.OpenVersionFolderAsync(target, name)) && ReferenceEquals(target, _versionsTarget))
            _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var plan = _plan();   // a snapshot, because the target is read on a worker thread
        IsLoading = true;
        ErrorText = null;
        try
        {
            var (versions, targetMissing, targetUnreachable) = await Task.Run(async () =>
            {
                // Only a present target is listed; NotCreatedYet and Unreachable both show as "missing", as before.
                var (state, storage) = await TargetStates.OpenAndProbeAsync(_storages, plan.Target, cts.Token);
                var missing = state != TargetState.Present;
                IReadOnlyList<VersionInfo> list = storage is null
                    ? []
                    : await VersionCatalog.ListAsync(storage, plan.Id, plan.Name, cts.Token);
                return (list, missing, state == TargetState.Unreachable);
            }, cts.Token);
            if (!ReferenceEquals(_loadCts, cts))
                return;
            _versions = versions;
            _versionsTarget = plan.Target.Path;
            _targetMissing = targetMissing;
            _targetUnreachable = targetUnreachable;
            _syncRequested = false;
            ClearPool();   // the versions may hold other content now
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(EmptyText));
            Evaluate();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _versions = null;
                _versionsTarget = null;
                _targetMissing = false;
                _targetUnreachable = false;
                NowRows.ReplaceAll([]);
                ClearPool();
                Now = LocText.Empty;
                ClearSimulation();
                ClearCoverage();
                ErrorText = LocText.Of("retention.targetUnreadable", ("error", ex.Message));
            }
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
                IsLoading = false;
        }
    }

    private void Evaluate()
    {
        if (_versions is not { } versions)
            return;

        var plan = _plan();
        var rules = plan.Retention;
        UsesPlanSchedule = plan.Triggers.Count > 0;
        IReadOnlyList<VersionDecision> decisions;
        try
        {
            decisions = RetentionPlanner.Decide(versions, rules);
        }
        catch (ArgumentException)
        {
            NowRows.ReplaceAll([]);
            ClearPool();
            Now = LocText.Of("retention.now.fixRules");
            ClearSimulation();
            ClearCoverage();
            Full = LocText.Of("retention.forecast.fixRules");
            return;
        }

        var selected = SelectedNowRow?.Name;
        NowRows.ReplaceAll(decisions.Reverse().Select(d => new RetentionNowRow(d, rules)));
        SelectedNowRow = NowRows.FirstOrDefault(r => r.Name.Equals(selected, StringComparison.OrdinalIgnoreCase));
        // An unreachable target (e.g. an offline NAS) lists no versions: its slots would all read "before the first backup".
        if (_targetUnreachable)
            ClearCoverage();
        else
            ShowCoverage(versions, rules);

        var managed = decisions.Where(d => d.Decision is not null).ToList();
        var deleted = managed.Where(d => d.Delete).ToList();
        var unmanaged = decisions.Count - managed.Count;
        var targetMissing = _targetMissing;
        var (count, keep, delete) = (managed.Count, managed.Count - deleted.Count, deleted.Count);
        var (size, freed) = (SizeOf(managed), SizeOf(deleted));
        _nowNumbers = new NowNumbers(decisions.Count, count, keep, delete, unmanaged, size, freed, targetMissing);

        var inputs = plan.Incremental ? PoolInputs.Of(versions, managed, deleted) : null;
        if (inputs is null || _poolInputs is null || inputs.Key != _poolInputs.Key)
        {
            // Other versions or other decisions: the pool numbers are read again (they stay on a mere language change).
            CancelPool();
            _poolFigures = inputs is { All.Count: 0 } ? PoolFigures.None : null;
        }
        _poolInputs = inputs;
        ShowNow();
        if (inputs is not null && _poolFigures is null)
            _ = ReadPoolAsync(plan, inputs);

        _ = SimulateAsync(versions, rules, plan.Triggers);
    }

    /// <summary>
    /// The plan's index finished a sync (on the Versions tab): the pool numbers of an incremental plan are read again,
    /// now that the index may hold the versions it lacked.
    /// </summary>
    public void OnIndexSynced()
    {
        if (_poolInputs is { } inputs && _versions is not null)
            _ = ReadPoolAsync(_plan(), inputs);
    }

    /// <summary>The "now" summary of the last evaluation, with the pool numbers as far as they are known.</summary>
    private void ShowNow()
    {
        if (_nowNumbers is not { } n)
            return;
        if (n.Listed == 0)
        {
            Now = n.TargetMissing ? LocText.Of("retention.now.targetMissing") : LocText.Of("retention.now.noVersions");
            return;
        }

        var pool = _poolInputs is null ? null : _poolFigures;
        var isPool = _poolInputs is not null;
        Now = new LocText(() =>
        {
            var unmanaged = n.Unmanaged > 0 ? Loc.F("retention.now.unmanaged", ("count", n.Unmanaged)) : "";
            if (!isPool)
            {
                return Loc.F("retention.now.summary", ("count", n.Count), ("size", Formats.Bytes(n.Size)), ("keep", n.Keep),
                    ("delete", n.Delete), ("freed", Formats.Bytes(n.Freed))) + unmanaged;
            }
            var calculating = Loc.T("retention.pool.calculating");
            return Loc.F("retention.now.poolSummary", ("count", n.Count),
                ("pool", pool is null ? calculating : Formats.Bytes(pool.Pool)), ("size", Formats.Bytes(n.Size)),
                ("keep", n.Keep), ("delete", n.Delete),
                ("freed", pool is null ? calculating : Formats.Bytes(pool.Freed))) + unmanaged;
        });
    }

    /// <summary>
    /// Reads the pool numbers of <paramref name="inputs"/> from the plan's index on a worker thread. While the index
    /// lacks a version the target holds they stay "calculating…", and a sync is asked for (once per read of the target);
    /// <see cref="OnIndexSynced"/> reads them again.
    /// </summary>
    private async Task ReadPoolAsync(BackupPlan plan, PoolInputs inputs)
    {
        _poolCts?.Cancel();
        var cts = _poolCts = new CancellationTokenSource();
        try
        {
            var figures = await Task.Run(() => _poolIndex(plan) is { } index ? PoolFigures.Read(index, inputs, cts.Token) : null,
                cts.Token);
            if (!ReferenceEquals(_poolCts, cts) || !ReferenceEquals(_poolInputs, inputs))
                return;
            if (figures is null)
            {
                if (!_syncRequested)
                {
                    _syncRequested = true;
                    _requestIndexSync();
                }
                return;
            }

            _poolFigures = figures;
            ShowNow();
            ShowForecast();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // An index that cannot be read leaves the pool numbers open; never a full-copy number in their place.
            System.Diagnostics.Trace.TraceWarning($"The pool numbers of plan {plan.Id} could not be read: {ex.Message}");
        }
    }

    private void CancelPool()
    {
        _poolCts?.Cancel();
        _poolCts = null;
    }

    /// <summary>Forgets the pool numbers and the "now" numbers they belong to.</summary>
    private void ClearPool()
    {
        CancelPool();
        _poolInputs = null;
        _poolFigures = null;
        _nowNumbers = null;
    }

    /// <summary>The coverage of the owned versions under the edited rules (already checked by the caller).</summary>
    private void ShowCoverage(IReadOnlyList<VersionInfo> versions, IReadOnlyList<RetentionRule> rules)
    {
        var (lanes, labels) = CoverageBuilder.Build(versions, rules, _runLog(), DateOnly.FromDateTime(DateTime.Now));
        CoverageLabels = labels;
        CoverageLanes = lanes;
        CoverageWithoutRules = rules.Count == 0;
    }

    private void ClearCoverage()
    {
        CoverageLanes = [];
        CoverageLabels = [];
        CoverageWithoutRules = false;
        SelectedNowRow = null;
    }

    private void ClearSimulation()
    {
        _simulateCts?.Cancel();
        _simulateCts = null;
        _shown = null;
        Full = LocText.Empty;
        ForecastVersions = null;
        ForecastBytes = null;
        TimelineLanes = [];
        OnPropertyChanged(nameof(ForecastSpaceText));
    }

    private async Task SimulateAsync(IReadOnlyList<VersionInfo> versions, IReadOnlyList<RetentionRule> rules,
        IReadOnlyList<ScheduleTrigger> triggers)
    {
        _simulateCts?.Cancel();
        var cts = _simulateCts = new CancellationTokenSource();
        _shown = null;
        Full = LocText.Of("retention.forecast.calculating");
        ForecastVersions = null;
        ForecastBytes = null;
        OnPropertyChanged(nameof(ForecastSpaceText));

        try
        {
            var now = DateTime.Now;
            IEnumerable<DateTime> runs;
            string frequencyKey;
            if (triggers.Count > 0)
            {
                if (triggers.Any(t => ScheduleTriggers.Validate(t) is not null))
                {
                    ClearSimulation();
                    Full = LocText.Of("retention.forecast.fixTriggers");
                    return;
                }
                runs = ScheduleCalculator.LocalRunTimes(triggers, DateTime.UtcNow, TimeZoneInfo.Local);
                frequencyKey = "retention.forecast.planFrequency";
            }
            else
            {
                // The assumed backups run at 02:00 and then every interval.
                var schedule = SelectedSchedule;
                runs = RetentionSimulator.Every(now.Date.AddHours(2), schedule.Interval);
                frequencyKey = "retention.scheduleLabel." + schedule.Key;
            }

            var owned = versions.Where(v => v.IsOwned).ToList();
            var seeds = owned.Select(v => new RetentionVersion(v.Name, v.LocalTime)).ToList();
            var sizes = owned.Where(v => v.TotalBytes is not null).Select(v => v.TotalBytes!.Value).ToList();
            long? average = sizes.Count > 0 ? (long)sizes.Average() : _fallbackVersionBytes();

            var result = await Task.Run(() => RetentionSimulator.Simulate(seeds, rules, runs, now, average, cts.Token), cts.Token);
            if (ReferenceEquals(_simulateCts, cts))
                ShowSimulation(result, rules, frequencyKey, now, average);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Covers ArgumentException too: Evaluate() checked the rules already, so this is unexpected.
            if (ReferenceEquals(_simulateCts, cts))
            {
                ClearSimulation();
                Full = LocText.Of("retention.forecast.failed", ("error", ex.Message));
            }
        }
    }

    /// <param name="versionBytes">
    /// The size of one version the simulation went by (the average, else the fallback); an incremental plan without
    /// owned versions takes it for the first version of its pool.
    /// </param>
    private void ShowSimulation(SimulationResult result, IReadOnlyList<RetentionRule> rules, string frequencyKey,
        DateTime now, long? versionBytes)
    {
        _shown = new ShownSimulation(result, rules.Count == 0, frequencyKey, versionBytes);
        ShowForecast();

        var noRules = rules.Count == 0;
        var lanes = new List<TimelineLane>();
        for (var i = 0; i < rules.Count; i++)
        {
            var ruleIndex = i;
            lanes.Add(new TimelineLane(
                Loc.F("retention.timeline.lane", ("rule", RetentionTexts.Describe(rules[i])), ("keep", rules[i].Keep)),
                result.Survivors.Where(s => s.Reasons.Any(r => r.RuleIndex == ruleIndex)).Select(s => s.LocalTime).ToList(),
                rules[i].Period));
        }

        var others = result.Survivors.Where(s => s.Reasons.Count > 0 && s.Reasons.All(r => r.RuleIndex < 0)).Select(s => s.LocalTime).ToList();
        if (others.Count > 0)
            lanes.Add(new TimelineLane(noRules ? Loc.T("retention.timeline.all") : Loc.T("retention.timeline.newest"), others));

        TimelineFrom = result.Survivors.Count > 0 ? result.Survivors[0].LocalTime : now;
        TimelineTo = result.Horizon;
        TimelineLanes = lanes;
    }

    /// <summary>
    /// The forecast sentence and numbers of the shown simulation. A full plan's space is the simulator's (every version
    /// a full copy); an incremental plan's is the pool's: the newest version, plus the new content an average version
    /// adds for every further version. That space is not known while the pool numbers are being read.
    /// </summary>
    private void ShowForecast()
    {
        if (_shown is not { } shown)
            return;
        var result = shown.Result;
        var (count, truncated, runs, noRules) = (result.SteadyStateCount, result.Truncated, result.RunsSimulated, shown.NoRules);
        var pool = _poolInputs is not null;
        long? bytes = !pool ? result.EstimatedBytes
            : _poolFigures is not { } figures ? null
            : (figures.ForecastBase ?? shown.VersionBytes) is { } first ? first + figures.Growth * Math.Max(count - 1, 0)
            : null;
        var frequencyKey = shown.FrequencyKey;
        Full = new LocText(() =>
        {
            var frequency = Loc.T(frequencyKey);
            var size = bytes is not { } total ? ""
                : Loc.F(pool ? "retention.forecast.poolSize" : "retention.forecast.size", ("size", Formats.Bytes(total)));
            var cut = truncated ? Loc.F("retention.forecast.cut", ("runs", runs)) : "";
            return noRules
                ? Loc.F("retention.forecast.noRules", ("frequency", frequency), ("count", count), ("size", size), ("cut", cut))
                : Loc.F("retention.forecast.withRules", ("frequency", frequency), ("count", count), ("size", size), ("cut", cut));
        });
        ForecastVersions = count;
        ForecastBytes = bytes;
        OnPropertyChanged(nameof(ForecastSpaceText));
    }

    private static long SizeOf(IEnumerable<VersionDecision> decisions) =>
        decisions.Sum(d => d.Version.TotalBytes ?? 0);

    /// <summary>The numbers of the "now" summary; <paramref name="Listed"/> counts every version read from the target.</summary>
    private sealed record NowNumbers(int Listed, int Count, int Keep, int Delete, int Unmanaged, long Size, long Freed,
        bool TargetMissing);

    /// <summary>A finished simulation as the forecast shows it.</summary>
    private sealed record ShownSimulation(SimulationResult Result, bool NoRules, string FrequencyKey, long? VersionBytes);

    /// <summary>
    /// The versions whose pool numbers an incremental plan shows, by name: all read from the target, the managed ones,
    /// those the rules delete, and the owned ones oldest first (the forecast's). Two evaluations with the same
    /// <see cref="Key"/> have the same versions and decisions.
    /// </summary>
    private sealed record PoolInputs(IReadOnlyList<string> All, IReadOnlyList<string> Managed, IReadOnlyList<string> Deleted,
        IReadOnlyList<string> Owned, string Key)
    {
        public static PoolInputs Of(IReadOnlyList<VersionInfo> versions, IReadOnlyList<VersionDecision> managed,
            IReadOnlyList<VersionDecision> deleted)
        {
            var all = versions.Select(v => v.Name).ToList();
            var managedNames = managed.Select(d => d.Version.Name).ToList();
            var deletedNames = deleted.Select(d => d.Version.Name).ToList();
            var owned = versions.Where(v => v.IsOwned).OrderBy(v => v.LocalTime).Select(v => v.Name).ToList();
            var key = string.Join('\n', [.. all, "", .. managedNames, "", .. deletedNames]);
            return new PoolInputs(all, managedNames, deletedNames, owned, key);
        }
    }

    /// <summary>
    /// The pool numbers of an incremental plan: <paramref name="Pool"/> is what the managed versions need,
    /// <paramref name="Freed"/> what deleting the versions the rules delete frees (the content no other version holds);
    /// the forecast starts at <paramref name="ForecastBase"/> (the newest owned version; null without one) and grows by
    /// <paramref name="Growth"/> (the new content of an average further version).
    /// </summary>
    private sealed record PoolFigures(long Pool, long Freed, long? ForecastBase, long Growth)
    {
        /// <summary>No versions: nothing to read.</summary>
        public static readonly PoolFigures None = new(0, 0, null, 0);

        /// <summary>The numbers from <paramref name="index"/>; null while it lacks one of the versions.</summary>
        public static PoolFigures? Read(VersionIndex index, PoolInputs inputs, CancellationToken ct)
        {
            var ids = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var version in index.Versions())
                ids[version.Name] = version.Id;
            if (inputs.All.Any(name => !ids.ContainsKey(name)))
                return null;

            long[] Ids(IEnumerable<string> names) => names.Select(name => ids[name]).Distinct().ToArray();
            var deleted = new HashSet<string>(inputs.Deleted, StringComparer.OrdinalIgnoreCase);
            ct.ThrowIfCancellationRequested();
            var pool = index.PoolBytesOf(Ids(inputs.Managed));
            ct.ThrowIfCancellationRequested();
            var freed = deleted.Count == 0
                ? 0
                : index.PoolBytesOf(Ids(inputs.All)) - index.PoolBytesOf(Ids(inputs.All.Where(name => !deleted.Contains(name))));
            ct.ThrowIfCancellationRequested();
            if (inputs.Owned.Count == 0)
                return new PoolFigures(pool, freed, null, 0);

            var first = index.PoolBytesOf(Ids([inputs.Owned[^1]]));
            var growth = inputs.Owned.Count >= 2 ? (index.PoolBytesOf(Ids(inputs.Owned)) - first) / (inputs.Owned.Count - 1) : 0;
            return new PoolFigures(pool, freed, first, growth);
        }
    }
}
