using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Pool;
using ReBackup.Core.Versions;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The Versions tab of an incremental plan: the pool header. Its sizes come from the index (refreshed with every sync);
/// "Measure pool" lists the pool on the target once, read-only, and tells how much of it the index does not reference.
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
    [NotifyPropertyChangedFor(nameof(SavedFraction), nameof(PoolSummary), nameof(MeasuredSummary))]
    private long _poolBytes;

    /// <summary>The bytes the indexed versions would take as full copies.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SavedFraction), nameof(PoolSummary))]
    private long _fullEquivalentBytes;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelMeasurePoolCommand))]
    private bool _isMeasuringPool;

    /// <summary>"Measuring the pool… 12,000 files" while a measurement runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeasureText))]
    private LocText _measureProgress = LocText.Empty;

    public string MeasureText => MeasureProgress.ToString();

    /// <summary>The pool header is shown: incremental plans only.</summary>
    public bool ShowPoolStats => IsPoolPlan;

    /// <summary>The share of the full copies' size the pool saves (0 to 1); 0 while there is nothing.</summary>
    public double SavedFraction => FullEquivalentBytes <= 0
        ? 0
        : Math.Clamp(1 - (double)PoolBytes / FullEquivalentBytes, 0, 1);

    /// <summary>"Pool 12.4 GB · full copies would be 186 GB · 93 % saved"; empty for full plans.</summary>
    public string PoolSummary => !ShowPoolStats
        ? ""
        : Loc.F("versions.pool.summary", ("pool", Formats.Bytes(PoolBytes)), ("full", Formats.Bytes(FullEquivalentBytes)),
            ("percent", (int)Math.Round(SavedFraction * 100, MidpointRounding.AwayFromZero)));

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

    /// <summary>Lists the pool on the target once (read-only, no lock) and shows what it holds.</summary>
    [RelayCommand]
    private async Task MeasurePoolAsync()
    {
        if (_savedPlan() is not { Incremental: true } plan)
            return;

        _measureCts?.Cancel();
        var cts = _measureCts = new CancellationTokenSource();
        ClearMeasurement(cancel: false);
        IsMeasuringPool = true;
        MeasureProgress = LocText.Of("versions.pool.measuring", ("count", 0));
        var progress = new Progress<int>(count =>
        {
            if (ReferenceEquals(_measureCts, cts) && !cts.IsCancellationRequested)
                MeasureProgress = LocText.Of("versions.pool.measuring", ("count", count));
        });
        try
        {
            var (state, measured) = await Task.Run(async () =>
            {
                var (targetState, storage) = await TargetStates.OpenAndProbeAsync(_context.Storages, plan.Target, cts.Token);
                return storage is null
                    ? (targetState, (0, 0L))
                    : (targetState, await PoolUsage.MeasurePoolAsync(storage, progress, cts.Token));
            }, cts.Token);
            if (!ReferenceEquals(_measureCts, cts))
                return;
            if (state == TargetState.Unreachable)
                _measureMessage = LocText.Of("versions.pool.unreachable");
            else
                _measured = measured;   // a target not created yet holds no pool
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is StorageException or IOException)
        {
            if (ReferenceEquals(_measureCts, cts))
                _measureMessage = new LocText(() => Loc.F("versions.pool.failed", ("error", Loc.Known(ex.Message))));
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
