using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;
using ReBackup.Core.Plans;
using ReBackup.Core.Retention;

namespace ReBackup.App.ViewModels;

/// <summary>Shows what a plan's retention rules do with the versions in its target.</summary>
public sealed partial class RetentionPreviewViewModel : ObservableObject
{
    private const string NotLoadedText = "The versions in the target have not been read yet.";
    private static readonly TimeSpan EvaluateDelay = TimeSpan.FromMilliseconds(300);

    private readonly Func<BackupPlan> _plan;
    private IReadOnlyList<VersionInfo>? _versions;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _evaluateCts;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _nowSummary = NotLoadedText;

    /// <param name="plan">Gives the plan as currently edited.</param>
    public RetentionPreviewViewModel(Func<BackupPlan> plan)
    {
        _plan = plan;
    }

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
        IsLoading = false;
        Error = null;
        NowRows.ReplaceAll([]);
        NowSummary = NotLoadedText;
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
            var versions = await Task.Run(
                () => VersionCatalog.List(plan.Target, plan.Id, plan.Name, cts.Token), cts.Token);
            if (!ReferenceEquals(_loadCts, cts))
                return;
            _versions = versions;
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
                NowRows.ReplaceAll([]);
                NowSummary = "";
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

        var rules = _plan().Retention;
        IReadOnlyList<VersionDecision> decisions;
        try
        {
            decisions = RetentionPlanner.Decide(versions, rules);
        }
        catch (ArgumentException)
        {
            NowRows.ReplaceAll([]);
            NowSummary = "Correct the rules above to see what they keep.";
            return;
        }

        NowRows.ReplaceAll(decisions.Reverse().Select(d => new RetentionNowRow(d)));

        var managed = decisions.Where(d => d.Decision is not null).ToList();
        var deleted = managed.Where(d => d.Delete).ToList();
        var unmanaged = decisions.Count - managed.Count;
        if (decisions.Count == 0)
        {
            NowSummary = "There are no versions in the target yet.";
            return;
        }

        NowSummary =
            $"{managed.Count:N0} versions, {ByteSize.Format(SizeOf(managed))}  →  the rules keep " +
            $"{managed.Count - deleted.Count:N0} and delete {deleted.Count:N0} (frees {ByteSize.Format(SizeOf(deleted))})" +
            (unmanaged > 0 ? $"  ·  {unmanaged:N0} not managed" : "");
    }

    private static long SizeOf(IEnumerable<VersionDecision> decisions) =>
        decisions.Sum(d => d.Version.TotalBytes ?? 0);
}
