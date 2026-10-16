using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Backup;
using ReBackup.Core.Pool;
using ReBackup.Core.Versions;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The Versions tab's size header. An incremental plan's: the pool's size and what full copies would take, from the index
/// (refreshed with every sync); "Measure pool" lists the pool on the target once, read-only, and tells how much of it the
/// index does not reference. A full plan's: what all versions take together (they are full copies, nothing is shared);
/// "Measure on target" adds up the plan's version folders on the target.
/// </summary>
public sealed partial class VersionsViewModel
{
    private CancellationTokenSource? _measureCts;

    /// <summary>The last measurement on the target (blobs, bytes); null until measured, and after a sync.</summary>
    private (int Blobs, long Bytes)? _measured;

    /// <summary>Why the last measurement has no numbers (e.g. the target is offline); null otherwise.</summary>
    private LocText? _measureMessage;

    /// <summary>The bytes the pool needs for the indexed versions (each content once).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SavedFraction), nameof(BarFraction), nameof(PoolSummary), nameof(MeasuredSummary))]
    private long _poolBytes;

    /// <summary>The bytes the indexed versions would take as full copies (a full plan's: what they take).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SavedFraction), nameof(BarFraction), nameof(PoolSummary))]
    private long _fullEquivalentBytes;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelMeasurePoolCommand))]
    private bool _isMeasuringPool;

    /// <summary>"Measuring the pool… 12,000 files" while a measurement runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeasureText))]
    private LocText _measureProgress = LocText.Empty;

    public string MeasureText => MeasureProgress.ToString();

    /// <summary>The size header is shown for every saved plan (the pool's for incremental plans).</summary>
    public bool ShowPoolStats => _savedPlan() is not null;

    /// <summary>The bar under the summary: the share the pool saves; full copies save nothing, so a full plan's bar is full (used).</summary>
    public double BarFraction => IsPoolPlan ? SavedFraction : 1;

    /// <summary>The bar's name for screen readers.</summary>
    public string BarName => IsPoolPlan ? Loc.T("versions.pool.savedName") : Loc.T("versions.pool.fullUsedName");

    /// <summary>The summary's tool tip.</summary>
    public string SummaryToolTip => IsPoolPlan ? Loc.T("versions.pool.summaryToolTip") : Loc.T("versions.pool.fullSummaryToolTip");

    /// <summary>"Measure pool", or "Measure on target" for a full plan.</summary>
    public string MeasureLabel => IsPoolPlan ? Loc.T("versions.pool.measure") : Loc.T("versions.pool.measureFull");

    /// <summary>The measure button's tool tip.</summary>
    public string MeasureToolTip => IsPoolPlan ? Loc.T("versions.pool.measureToolTip") : Loc.T("versions.pool.measureFullToolTip");

    /// <summary>The share of the full copies' size the pool saves (0 to 1); 0 while there is nothing.</summary>
    public double SavedFraction => FullEquivalentBytes <= 0
        ? 0
        : Math.Clamp(1 - (double)PoolBytes / FullEquivalentBytes, 0, 1);

    /// <summary>
    /// "Pool 12.4 GB · full copies would be 186 GB · 93 % saved"; for a full plan "All versions together: 43.6 GB (full
    /// copies, no deduplication)"; empty while there is no saved plan.
    /// </summary>
    public string PoolSummary => !ShowPoolStats
        ? ""
        : !IsPoolPlan
        ? Loc.F("versions.pool.fullSummary", ("size", Formats.Bytes(FullEquivalentBytes)))
        : Loc.F("versions.pool.summary", ("pool", Formats.Bytes(PoolBytes)), ("full", Formats.Bytes(FullEquivalentBytes)),
            ("percent", (int)Math.Floor(SavedFraction * 100)));   // floored: a non-empty pool never shows 100 %

    /// <summary>
    /// "On target: 12.9 GB in 48,213 files · about 0.5 GB not referenced" after a measurement (the last part only when the
    /// target holds more than the index references), or why it could not be measured; null before.
    /// </summary>
    public string? MeasuredSummary
    {
        get
        {
            if (_measureMessage is { } message)
                return message.ToString();
            if (_measured is not { } measured)
                return null;
            var text = Loc.F("versions.pool.measured", ("size", Formats.Bytes(measured.Bytes)), ("count", measured.Blobs));
            if (!IsPoolPlan)
                return text;   // nothing is shared: there is nothing unreferenced to tell
            var unreferenced = measured.Bytes - PoolBytes;
            return unreferenced > 0
                ? text + Loc.F("versions.pool.unreferenced", ("size", Formats.Bytes(unreferenced)))
                : text;
        }
    }

    /// <summary>
    /// Shows the sizes of <paramref name="stats"/> (null for full plans: none). When they differ from the shown ones, the
    /// measurement on the target is forgotten: it was compared with the index as it was.
    /// </summary>
    private void ShowPoolStatsOf((long FullBytes, long PoolBytes)? stats)
    {
        var full = stats?.FullBytes ?? 0;
        var pool = stats?.PoolBytes ?? 0;
        if (full == FullEquivalentBytes && pool == PoolBytes)
            return;
        FullEquivalentBytes = full;
        PoolBytes = pool;
        ClearMeasurement(cancel: false);
    }

    /// <summary>Forgets the shown measurement (a running one goes on unless <paramref name="cancel"/>).</summary>
    private void ClearMeasurement(bool cancel)
    {
        if (cancel)
        {
            _measureCts?.Cancel();
            _measureCts = null;
            IsMeasuringPool = false;
            MeasureProgress = LocText.Empty;
        }
        _measured = null;
        _measureMessage = null;
        OnPropertyChanged(nameof(MeasuredSummary));
    }

    /// <summary>
    /// Lists the pool on the target once (read-only, no lock) and shows what it holds; for a full plan, the version
    /// folders the plan lists on the target, 8 at a time.
    /// </summary>
    [RelayCommand]
    private async Task MeasurePoolAsync()
    {
        if (_savedPlan() is not { } plan)
            return;
        var pool = plan.Incremental;
        var measuring = pool ? "versions.pool.measuring" : "versions.pool.measuringFull";

        _measureCts?.Cancel();
        var cts = _measureCts = new CancellationTokenSource();
        ClearMeasurement(cancel: false);
        IsMeasuringPool = true;
        MeasureProgress = LocText.Of(measuring, ("count", 0));
        var progress = new Progress<int>(count =>
        {
            if (ReferenceEquals(_measureCts, cts) && !cts.IsCancellationRequested)
                MeasureProgress = LocText.Of(measuring, ("count", count));
        });
        try
        {
            var (state, measured) = await Task.Run(async () =>
            {
                var (targetState, storage) = await TargetStates.OpenAndProbeAsync(_context.Storages, plan.Target, cts.Token);
                if (storage is null)
                    return (targetState, (0, 0L));
                if (pool)
                    return (targetState, await PoolUsage.MeasurePoolAsync(storage, progress, cts.Token));
                var versions = await VersionCatalog.ListAsync(storage, plan.Id, plan.Name, cts.Token);
                return (targetState, await PoolUsage.MeasureFoldersAsync(storage, versions.Select(v => v.Path).ToList(),
                    progress, cts.Token));
            }, cts.Token);
            if (!ReferenceEquals(_measureCts, cts))
                return;
            if (state == TargetState.Unreachable)
                _measureMessage = LocText.Of(pool ? "versions.pool.unreachable" : "versions.pool.unreachableFull");
            else
                _measured = measured;   // a target not created yet holds no pool
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Like the tab's sync: any failure to reach or list the target (access denied, an SDK error) is a message.
            if (ReferenceEquals(_measureCts, cts))
                _measureMessage = new LocText(() => Loc.F(pool ? "versions.pool.failed" : "versions.pool.failedFull",
                    ("error", Loc.Known(ex.Message))));
        }
        finally
        {
            if (ReferenceEquals(_measureCts, cts))
            {
                _measureCts = null;
                IsMeasuringPool = false;
                MeasureProgress = LocText.Empty;
                OnPropertyChanged(nameof(MeasuredSummary));
            }
        }
    }

    [RelayCommand(CanExecute = nameof(IsMeasuringPool))]
    private void CancelMeasurePool()
    {
        if (_measureCts is { IsCancellationRequested: false } cts)
            cts.Cancel();
    }
}
