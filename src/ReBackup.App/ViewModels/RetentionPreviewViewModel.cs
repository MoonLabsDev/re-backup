using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Wpf.Controls;
using ReBackup.App.Services;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Retention;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Schedule;

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
    private IReadOnlyList<VersionInfo>? _versions;
    private string? _versionsTarget;   // belongs to _versions: the target they were read from
    private bool _targetMissing;   // belongs to _versions: the target folder did not exist when they were read
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _evaluateCts;
    private CancellationTokenSource? _simulateCts;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
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
    [ObservableProperty] private DateTime _timelineFrom;
    [ObservableProperty] private DateTime _timelineTo;

    /// <param name="plan">Gives the plan as currently edited.</param>
    /// <param name="fallbackVersionBytes">Size of one version when the target has none yet; null when unknown.</param>
    /// <param name="folders">Opens a version folder in Explorer.</param>
    public RetentionPreviewViewModel(Func<BackupPlan> plan, Func<long?> fallbackVersionBytes, IFolderOpener folders)
    {
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

    /// <summary>The "Space" number of the forecast, e.g. "≈ 286.0 GB"; "unknown" while no version size is known.</summary>
    public string ForecastSpaceText => ForecastBytes is { } bytes ? "≈ " + Formats.Bytes(bytes) : Loc.T("retention.forecast.unknown");

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
        IsLoading = false;
        ErrorText = null;
        NowRows.ReplaceAll([]);
        Now = LocText.Of("retention.now.notLoaded");
        ClearSimulation();
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
        if (!await Task.Run(() => _folders.OpenVersionFolder(target, name)) && ReferenceEquals(target, _versionsTarget))
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
            var (versions, targetMissing) = await Task.Run(() =>
            {
                var missing = string.IsNullOrWhiteSpace(plan.Target.Path) || !Directory.Exists(plan.Target.Path);
                return (VersionCatalog.List(plan.Target.Path, plan.Id, plan.Name, cts.Token), missing);
            }, cts.Token);
            if (!ReferenceEquals(_loadCts, cts))
                return;
            _versions = versions;
            _versionsTarget = plan.Target.Path;
            _targetMissing = targetMissing;
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
                NowRows.ReplaceAll([]);
                Now = LocText.Empty;
                ClearSimulation();
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
            Now = LocText.Of("retention.now.fixRules");
            ClearSimulation();
            Full = LocText.Of("retention.forecast.fixRules");
            return;
        }

        NowRows.ReplaceAll(decisions.Reverse().Select(d => new RetentionNowRow(d, rules)));

        var managed = decisions.Where(d => d.Decision is not null).ToList();
        var deleted = managed.Where(d => d.Delete).ToList();
        var unmanaged = decisions.Count - managed.Count;
        var targetMissing = _targetMissing;
        var (count, keep, delete) = (managed.Count, managed.Count - deleted.Count, deleted.Count);
        var (size, freed) = (SizeOf(managed), SizeOf(deleted));
        Now = decisions.Count == 0
            ? targetMissing ? LocText.Of("retention.now.targetMissing") : LocText.Of("retention.now.noVersions")
            : new LocText(() =>
                Loc.F("retention.now.summary", ("count", count), ("size", Formats.Bytes(size)), ("keep", keep),
                    ("delete", delete), ("freed", Formats.Bytes(freed))) +
                (unmanaged > 0 ? Loc.F("retention.now.unmanaged", ("count", unmanaged)) : ""));

        _ = SimulateAsync(versions, rules, plan.Triggers);
    }

    private void ClearSimulation()
    {
        _simulateCts?.Cancel();
        _simulateCts = null;
        Full = LocText.Empty;
        ForecastVersions = null;
        ForecastBytes = null;
        TimelineLanes = [];
    }

    private async Task SimulateAsync(IReadOnlyList<VersionInfo> versions, IReadOnlyList<RetentionRule> rules,
        IReadOnlyList<ScheduleTrigger> triggers)
    {
        _simulateCts?.Cancel();
        var cts = _simulateCts = new CancellationTokenSource();
        Full = LocText.Of("retention.forecast.calculating");
        ForecastVersions = null;
        ForecastBytes = null;

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
                ShowSimulation(result, rules, frequencyKey, now);
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

    private void ShowSimulation(SimulationResult result, IReadOnlyList<RetentionRule> rules, string frequencyKey,
        DateTime now)
    {
        var (count, bytes, truncated, runs) = (result.SteadyStateCount, result.EstimatedBytes, result.Truncated, result.RunsSimulated);
        var noRules = rules.Count == 0;
        Full = new LocText(() =>
        {
            var frequency = Loc.T(frequencyKey);
            var size = bytes is { } total ? Loc.F("retention.forecast.size", ("size", Formats.Bytes(total))) : "";
            var cut = truncated ? Loc.F("retention.forecast.cut", ("runs", runs)) : "";
            return noRules
                ? Loc.F("retention.forecast.noRules", ("frequency", frequency), ("count", count), ("size", size), ("cut", cut))
                : Loc.F("retention.forecast.withRules", ("frequency", frequency), ("count", count), ("size", size), ("cut", cut));
        });
        ForecastVersions = count;
        ForecastBytes = bytes;

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

    private static long SizeOf(IEnumerable<VersionDecision> decisions) =>
        decisions.Sum(d => d.Version.TotalBytes ?? 0);
}
