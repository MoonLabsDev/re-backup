using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;
using ReBackup.Core.Plans;
using ReBackup.Core.Retention;
using ReBackup.Core.Schedule;

namespace ReBackup.App.ViewModels;

/// <summary>
/// A backup frequency assumed for the full-extension preview of a plan without triggers. <paramref name="Label"/> goes
/// into sentences ("one backup a day"), <paramref name="RunsLabel"/> after "two years of" in the forecast title.
/// </summary>
public sealed record AssumedSchedule(string Label, TimeSpan Interval, string RunsLabel);

/// <summary>Shows what a plan's retention rules do with the versions in its target.</summary>
public sealed partial class RetentionPreviewViewModel : ObservableObject
{
    private const string NotLoadedText = "The versions in the target have not been read yet.";
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
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _nowSummary = NotLoadedText;
    [ObservableProperty] private AssumedSchedule _selectedSchedule = Schedules[1];
    [ObservableProperty] private bool _usesPlanSchedule;
    [ObservableProperty] private string _fullSummary = "";

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
    public string EmptyText => _targetMissing ? "The target folder does not exist (yet)." : "No versions yet";

    /// <summary>True while the forecast numbers are shown; otherwise <see cref="FullSummary"/> says why there are none.</summary>
    public bool HasForecast => ForecastVersions is not null;

    /// <summary>The "Versions kept" number of the forecast, e.g. "≈ 21".</summary>
    public string ForecastVersionsText => ForecastVersions is { } count ? $"≈ {count:N0}" : "";

    /// <summary>The "Space" number of the forecast, e.g. "≈ 286.0 GB"; "unknown" while no version size is known.</summary>
    public string ForecastSpaceText => ForecastBytes is { } bytes ? $"≈ {ByteSize.Format(bytes)}" : "unknown";

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(ShowEmpty));

    public static IReadOnlyList<AssumedSchedule> Schedules { get; } =
    [
        new("one backup a week", TimeSpan.FromDays(7), "weekly runs"),
        new("one backup a day", TimeSpan.FromDays(1), "daily runs"),
        new("two backups a day", TimeSpan.FromHours(12), "two runs a day"),
        new("a backup every 4 hours", TimeSpan.FromHours(4), "runs every 4 hours"),
        new("a backup every hour", TimeSpan.FromHours(1), "hourly runs"),
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
        Error = null;
        NowRows.ReplaceAll([]);
        NowSummary = NotLoadedText;
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
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    /// <summary>Shows the row's version folder in Explorer; reads the target again when the folder is gone.</summary>
    [RelayCommand]
    private void OpenVersionFolder(RetentionNowRow? row)
    {
        if (row is null || _versionsTarget is null)
            return;
        if (!_folders.OpenVersionFolder(_versionsTarget, row.Name))
            _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var plan = _plan();   // a snapshot, because the target is read on a worker thread
        IsLoading = true;
        Error = null;
        try
        {
            var (versions, targetMissing) = await Task.Run(() =>
            {
                var missing = string.IsNullOrWhiteSpace(plan.Target) || !Directory.Exists(plan.Target);
                return (VersionCatalog.List(plan.Target, plan.Id, plan.Name, cts.Token), missing);
            }, cts.Token);
            if (!ReferenceEquals(_loadCts, cts))
                return;
            _versions = versions;
            _versionsTarget = plan.Target;
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
                NowSummary = "";
                ClearSimulation();
                Error = $"The target folder could not be read: {ex.Message}";
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
            NowSummary = "Correct the rules above to see what they keep.";
            ClearSimulation();
            FullSummary = "Correct the rules above to see the forecast.";
            return;
        }

        NowRows.ReplaceAll(decisions.Reverse().Select(d => new RetentionNowRow(d)));

        var managed = decisions.Where(d => d.Decision is not null).ToList();
        var deleted = managed.Where(d => d.Delete).ToList();
        var unmanaged = decisions.Count - managed.Count;
        NowSummary = decisions.Count == 0
            ? _targetMissing ? "The target folder does not exist (yet)." : "There are no versions in the target yet."
            : $"{managed.Count:N0} versions, {ByteSize.Format(SizeOf(managed))}  →  the rules keep " +
              $"{managed.Count - deleted.Count:N0} and delete {deleted.Count:N0} (frees {ByteSize.Format(SizeOf(deleted))})" +
              (unmanaged > 0 ? $"  ·  {unmanaged:N0} not managed" : "");

        _ = SimulateAsync(versions, rules, plan.Triggers);
    }

    private void ClearSimulation()
    {
        _simulateCts?.Cancel();
        _simulateCts = null;
        FullSummary = "";
        ForecastVersions = null;
        ForecastBytes = null;
        TimelineLanes = [];
    }

    private async Task SimulateAsync(IReadOnlyList<VersionInfo> versions, IReadOnlyList<RetentionRule> rules,
        IReadOnlyList<ScheduleTrigger> triggers)
    {
        _simulateCts?.Cancel();
        var cts = _simulateCts = new CancellationTokenSource();
        FullSummary = "Calculating…";
        ForecastVersions = null;
        ForecastBytes = null;

        try
        {
            var now = DateTime.Now;
            IEnumerable<DateTime> runs;
            string frequency;
            if (triggers.Count > 0)
            {
                if (triggers.Any(t => ScheduleTriggers.Validate(t) is not null))
                {
                    ClearSimulation();
                    FullSummary = "Correct the plan's triggers on the Plan tab to see the full extension.";
                    return;
                }
                runs = ScheduleCalculator.LocalRunTimes(triggers, DateTime.UtcNow, TimeZoneInfo.Local);
                frequency = "the plan's schedule";
            }
            else
            {
                // The assumed backups run at 02:00 and then every interval.
                var schedule = SelectedSchedule;
                runs = RetentionSimulator.Every(now.Date.AddHours(2), schedule.Interval);
                frequency = schedule.Label;
            }

            var owned = versions.Where(v => v.IsOwned).ToList();
            var seeds = owned.Select(v => new RetentionVersion(v.Name, v.LocalTime)).ToList();
            var sizes = owned.Where(v => v.TotalBytes is not null).Select(v => v.TotalBytes!.Value).ToList();
            long? average = sizes.Count > 0 ? (long)sizes.Average() : _fallbackVersionBytes();

            var result = await Task.Run(() => RetentionSimulator.Simulate(seeds, rules, runs, now, average, cts.Token), cts.Token);
            if (ReferenceEquals(_simulateCts, cts))
                ShowSimulation(result, rules, frequency, now);
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
                FullSummary = $"The full extension could not be calculated: {ex.Message}";
            }
        }
    }

    private void ShowSimulation(SimulationResult result, IReadOnlyList<RetentionRule> rules, string frequency,
        DateTime now)
    {
        var size = result.EstimatedBytes is { } bytes ? $", about {ByteSize.Format(bytes)}" : "";
        var cut = result.Truncated ? $" The simulation stopped after {result.RunsSimulated:N0} runs." : "";
        FullSummary = rules.Count == 0
            ? $"No rules, so nothing is ever deleted: with {frequency} there are {result.SteadyStateCount:N0} versions after two years{size}.{cut}"
            : $"With {frequency} the target holds up to {result.SteadyStateCount:N0} versions{size}.{cut}";
        ForecastVersions = result.SteadyStateCount;
        ForecastBytes = result.EstimatedBytes;

        var lanes = new List<TimelineLane>();
        for (var i = 0; i < rules.Count; i++)
        {
            var ruleIndex = i;
            lanes.Add(new TimelineLane($"{RetentionRules.Describe(rules[i])}, keep {rules[i].Keep:N0}",
                result.Survivors.Where(s => s.Reasons.Any(r => r.RuleIndex == ruleIndex)).Select(s => s.LocalTime).ToList(),
                rules[i].Period));
        }

        var others = result.Survivors.Where(s => s.Reasons.Count > 0 && s.Reasons.All(r => r.RuleIndex < 0)).Select(s => s.LocalTime).ToList();
        if (others.Count > 0)
            lanes.Add(new TimelineLane(rules.Count == 0 ? "All versions" : "Newest", others));

        TimelineFrom = result.Survivors.Count > 0 ? result.Survivors[0].LocalTime : now;
        TimelineTo = result.Horizon;
        TimelineLanes = lanes;
    }

    private static long SizeOf(IEnumerable<VersionDecision> decisions) =>
        decisions.Sum(d => d.Version.TotalBytes ?? 0);
}
