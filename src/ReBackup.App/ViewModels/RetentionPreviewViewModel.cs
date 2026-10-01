using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;
using ReBackup.Core.Plans;
using ReBackup.Core.Retention;
using ReBackup.Core.Schedule;

namespace ReBackup.App.ViewModels;

/// <summary>A backup frequency assumed for the full-extension preview of a plan without triggers.</summary>
public sealed record AssumedSchedule(string Label, TimeSpan Interval);

/// <summary>Shows what a plan's retention rules do with the versions in its target.</summary>
public sealed partial class RetentionPreviewViewModel : ObservableObject
{
    private const string NotLoadedText = "The versions in the target have not been read yet.";
    private static readonly TimeSpan EvaluateDelay = TimeSpan.FromMilliseconds(300);

    private readonly Func<BackupPlan> _plan;
    private readonly Func<long?> _fallbackVersionBytes;
    private IReadOnlyList<VersionInfo>? _versions;
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
    [ObservableProperty] private IReadOnlyList<TimelineLane> _timelineLanes = [];
    [ObservableProperty] private DateTime _timelineFrom;
    [ObservableProperty] private DateTime _timelineTo;

    /// <param name="plan">Gives the plan as currently edited.</param>
    /// <param name="fallbackVersionBytes">Size of one version when the target has none yet; null when unknown.</param>
    public RetentionPreviewViewModel(Func<BackupPlan> plan, Func<long?> fallbackVersionBytes)
    {
        _plan = plan;
        _fallbackVersionBytes = fallbackVersionBytes;
        NowRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowEmpty));
    }

    /// <summary>True when the target was read successfully and holds no versions.</summary>
    public bool ShowEmpty => _versions is not null && NowRows.Count == 0 && !IsLoading && Error is null;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(ShowEmpty));

    public static IReadOnlyList<AssumedSchedule> Schedules { get; } =
    [
        new("one backup a week", TimeSpan.FromDays(7)),
        new("one backup a day", TimeSpan.FromDays(1)),
        new("two backups a day", TimeSpan.FromHours(12)),
        new("a backup every 4 hours", TimeSpan.FromHours(4)),
        new("a backup every hour", TimeSpan.FromHours(1)),
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
            OnPropertyChanged(nameof(ShowEmpty));
            _targetMissing = targetMissing;
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
        TimelineLanes = [];
    }

    private async Task SimulateAsync(IReadOnlyList<VersionInfo> versions, IReadOnlyList<RetentionRule> rules,
        IReadOnlyList<ScheduleTrigger> triggers)
    {
        _simulateCts?.Cancel();
        var cts = _simulateCts = new CancellationTokenSource();
        FullSummary = "Calculating…";

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
                    FullSummary = "Correct the plan's triggers on the Schedule tab to see the full extension.";
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

        var lanes = new List<TimelineLane>();
        for (var i = 0; i < rules.Count; i++)
        {
            var ruleIndex = i;
            lanes.Add(new TimelineLane($"{RetentionRules.Describe(rules[i])}, keep {rules[i].Keep:N0}",
                result.Survivors.Where(s => s.Reasons.Any(r => r.RuleIndex == ruleIndex)).Select(s => s.LocalTime).ToList()));
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
