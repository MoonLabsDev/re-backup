using System.Diagnostics;
using ReBackup.Core.Cleanup;
using ReBackup.Core.Ignore;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The Versions tab: cleaning up old versions by the current ignore rules (<see cref="CleanupRunner"/>), run as a job
/// like a deletion by hand: while it runs <see cref="VersionsViewModel.IsDeleting"/> is true, so the plan's runs wait,
/// copying, deleting and restoring are refused, and quitting asks the deletion's question.
/// </summary>
public sealed partial class VersionsViewModel
{
    /// <summary>
    /// A cleanup can start now: no deletion, resume or restore of the plan runs, no backup of it is queued or running,
    /// and it is not being copied.
    /// </summary>
    public bool CanStartCleanup => !IsDeleting && !IsResuming && !IsRestoring && !_isBackupActive() && !_isCopyActive();

    /// <summary>
    /// Cleans up <paramref name="versions"/> of the saved plan by <paramref name="matcher"/> in the background, as a
    /// deletion-like job: the plan card and the tab show "Cleaning up 1 of 3: …" (each report also goes to
    /// <paramref name="progress"/>), the footer gets the outcome, the index is handed each cleaned version, and afterwards
    /// the target is read again. Null when it is refused (<see cref="CanStartCleanup"/> is false, or the plan is not
    /// saved) or when the run failed (the footer says why); a canceled run (<see cref="CancelCleanup"/>) returns its
    /// result with <see cref="CleanupResult.Canceled"/> set.
    /// </summary>
    public async Task<CleanupResult?> CleanUpAsync(IgnoreMatcher matcher, IReadOnlyList<CleanupTarget> versions,
        IProgress<CleanupProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(versions);
        if (!CanStartCleanup || _savedPlan() is not { } plan)
            return null;

        // A sync must not index manifests that are being rewritten; the target is read again afterwards.
        _syncCts?.Cancel();
        var cts = _deleteCts = new CancellationTokenSource();
        var running = LocText.Of("cleanup.running", ("count", versions.Count));
        StartDeleting(running);
        DeletionStatus = new DeletionCardStatus(LocText.Of("cleanup.card"), running, 0, true);
        var report = CleanupProgressOf(cts, progress);
        var host = _context.Host;
        CleanupResult? result;
        try
        {
            result = await RunAsJobAsync(cts, () => CleanupRunner.RunAsync(_context.Storages.Open(plan.Target), plan,
                matcher, versions, _context.Worker, report, cts.Token, host));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            result = new CleanupResult([], 0, Canceled: true);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"The cleanup of plan {plan.Id} failed: {ex.Message}");
            _context.ReportStatus(new LocText(() => Loc.F("cleanup.stopped", ("error", Loc.Known(ex.Message)))));
            result = null;
        }
        finally
        {
            EndDeleting();
            cts.Dispose();
        }

        if (result is not null)
        {
            var cleaned = result.Versions.Count(v => v.Outcome == CleanupOutcome.Cleaned);
            var freed = result.BytesFreed;
            _context.ReportStatus(result.Canceled
                ? new LocText(() => Loc.F("cleanup.canceled", ("count", cleaned), ("size", Formats.Bytes(freed))))
                : new LocText(() => Loc.F("cleanup.done", ("count", cleaned), ("size", Formats.Bytes(freed)))));
        }

        // Also after a failure: versions before it may have been cleaned up already.
        VersionsDeleted?.Invoke();
        _ = IndexAfterDeleteAsync();
        return result;
    }

    /// <summary>Stops the cleanup after the version being cleaned up (a version that has started is finished).</summary>
    public void CancelCleanup()
    {
        if (_deleteCts is not { } cts || cts.IsCancellationRequested)
            return;
        cts.Cancel();
        DeleteProgress = LocText.Of("cleanup.canceling");
        DeleteIndeterminate = true;
        DeletionStatus = new DeletionCardStatus(LocText.Of("cleanup.stopping"), DeleteProgress, 0, true);
    }

    /// <summary>
    /// The progress of a cleanup: the tab's line, the ring and the plan card's <see cref="DeletionStatus"/> (while
    /// <paramref name="cts"/> is the running, uncanceled job), and every report forwarded to <paramref name="forward"/>.
    /// Reports come on the UI thread.
    /// </summary>
    private IProgress<CleanupProgress> CleanupProgressOf(CancellationTokenSource cts, IProgress<CleanupProgress>? forward) =>
        new Progress<CleanupProgress>(p =>
        {
            forward?.Report(p);
            if (!IsDeleting || !ReferenceEquals(_deleteCts, cts) || cts.IsCancellationRequested)
                return;
            var total = Math.Max(p.Total, 1);
            var collecting = p.Phase == CleanupPhase.CollectingPool;
            DeleteProgress = collecting
                ? LocText.Of("cleanup.collecting")
                : LocText.Of("cleanup.progress", ("current", p.Current), ("total", p.Total), ("version", p.VersionName));
            // A version counts half done once its manifest is short and its files go.
            DeleteFraction = collecting ? 0 : (p.Current - (p.Phase == CleanupPhase.Deleting ? 0.5 : 1.0)) / total;
            DeleteIndeterminate = collecting;
            DeletionStatus = new DeletionCardStatus(LocText.Of("cleanup.card"), DeleteProgress, DeleteFraction, collecting);
            _context.ReportStatus(DeleteProgress);
        });
}
