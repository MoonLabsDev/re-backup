using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Storage;

namespace ReBackup.App.ViewModels;

/// <summary>
/// What the plan card shows while versions of the plan are deleted by hand (or a deletion is resumed), cleaned up after
/// and indexed: the phase line, the detail line and the ring (<paramref name="Fraction"/>, ignored while
/// <paramref name="Indeterminate"/>).
/// </summary>
public sealed record DeletionCardStatus(LocText Phase, LocText Detail, double Fraction, bool Indeterminate);

/// <summary>The Versions tab: deleting versions picked by hand, after a confirmation.</summary>
public sealed partial class VersionsViewModel
{
    /// <summary>Versions listed in the confirmation; the rest are summed up as "and N more".</summary>
    private const int VersionsInConfirmation = 5;

    /// <summary>The rows marked in the version list (several with Ctrl or Shift); the delete acts on these.</summary>
    private IReadOnlyList<VersionRowViewModel> _marked = [];

    /// <summary>The remaining time is shown once the deletion has run this long (the first files say little about the rate).</summary>
    private static readonly TimeSpan EtaWarmUp = TimeSpan.FromSeconds(3);

    private CancellationTokenSource? _deleteCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteVersionsCommand), nameof(RestoreToOriginalCommand), nameof(RestoreToCommand),
        nameof(CancelDeleteCommand), nameof(RestoreSelectedToTempCommand), nameof(RestoreVersionToTempCommand))]
    private bool _isDeleting;

    /// <summary>"Deleting 1,200 of 50,000 files · version 1 of 3 · 4 min left" while a deletion runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteText))]
    private LocText _deleteProgress = LocText.Empty;

    public string DeleteText => DeleteProgress.ToString();

    [ObservableProperty] private double _deleteFraction;

    /// <summary>The deletion's progress has no known total right now (preparing, listing the pool).</summary>
    [ObservableProperty] private bool _deleteIndeterminate;

    /// <summary>What the plan card shows of a deletion, its cleanup and the indexing after it; null otherwise.</summary>
    [ObservableProperty] private DeletionCardStatus? _deletionStatus;

    /// <summary>
    /// True from the start of <see cref="ResumeDeletionAsync"/> until it has ended: the plan's runs wait for it (spec: an
    /// unfinished deletion is finished first).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteVersionsCommand))]
    private bool _isResuming;

    /// <summary>Versions were deleted by hand (the retention preview reads the target again).</summary>
    public event Action? VersionsDeleted;

    /// <summary>Stops the deletion after the version being deleted (a half-deleted version would be left as remains).</summary>
    [RelayCommand(CanExecute = nameof(IsDeleting))]
    private void CancelDelete()
    {
        if (_deleteCts is not { } cts || cts.IsCancellationRequested)
            return;
        cts.Cancel();
        DeleteProgress = LocText.Of("versions.delete.canceling");
        DeleteIndeterminate = true;
        DeletionStatus = new DeletionCardStatus(LocText.Of("card.delete.stopping"), LocText.Of("versions.delete.canceling"), 0, true);
    }

    /// <summary>A deletion the app waits for when it exits (<see cref="IPoolJob"/>): canceling stops it after the current version.</summary>
    private sealed class DeletionJob(CancellationTokenSource cts) : IPoolJob
    {
        public Task Work { get; set; } = Task.CompletedTask;

        public void Stop()
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It has ended already.
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> off the UI thread as a job the app stops and waits for on exit.</summary>
    private async Task<T> RunAsJobAsync<T>(CancellationTokenSource cts, Func<Task<T>> work)
    {
        var job = new DeletionJob(cts);
        var task = Task.Run(work);
        job.Work = task;
        _context.TrackJob?.Invoke(job, true);
        try
        {
            return await task;
        }
        finally
        {
            _context.TrackJob?.Invoke(job, false);
        }
    }

    /// <summary>
    /// The line of a cleanup phase, e.g. "Cleaning up: listing pool · 84,120 files" (the Versions tab, and the plan card
    /// while a run collects the pool).
    /// </summary>
    internal static LocText CleanupText(CollectProgress c) => c.Phase switch
    {
        CollectPhase.ReadingManifests => LocText.Of("versions.delete.cleanup.manifests", ("done", c.Done), ("total", c.Total)),
        CollectPhase.ListingPool => LocText.Of("versions.delete.cleanup.listing", ("files", Formats.Files(c.Done))),
        _ => LocText.Of("versions.delete.cleanup.deleting", ("done", c.Done), ("total", c.Total)),
    };

    /// <summary>
    /// The progress line of a deletion (also the footer's status and the plan card's <see cref="DeletionStatus"/>), with
    /// the remaining time from the average rate since it started; reports come on the UI thread.
    /// </summary>
    private IProgress<VersionDeletionProgress> DeletionProgress(CancellationTokenSource cts)
    {
        var clock = Stopwatch.StartNew();
        return new Progress<VersionDeletionProgress>(p =>
        {
            if (!IsDeleting || !ReferenceEquals(_deleteCts, cts) || cts.IsCancellationRequested)
                return;
            DeleteFraction = p.Fraction;
            var version = LocText.Of("card.delete.version");
            if (p.Phase == VersionDeletionPhase.Preparing)
            {
                clock.Restart();   // the rate counts from the first deleted file on
                DeleteProgress = LocText.Of("versions.delete.preparing", ("current", p.Current), ("versions", p.VersionCount));
                DeleteIndeterminate = true;
                DeletionStatus = new DeletionCardStatus(version, LocText.Of("card.delete.preparing"), 0, true);
                _context.ReportStatus(DeleteProgress);
                return;
            }
            if (p.Phase == VersionDeletionPhase.CleaningUp)
            {
                var cleanup = p.Cleanup;
                DeleteProgress = CleanupText(cleanup);
                DeleteIndeterminate = cleanup.IsIndeterminate;
                var percent = (int)Math.Floor(cleanup.Fraction * 100);
                DeletionStatus = new DeletionCardStatus(
                    cleanup.IsIndeterminate
                        ? LocText.Of("card.delete.cleaning")
                        : LocText.Of("card.delete.cleaningPercent", ("percent", percent)),
                    DeleteProgress, cleanup.Fraction, cleanup.IsIndeterminate);
                _context.ReportStatus(DeleteProgress);
                return;
            }
            DeleteIndeterminate = false;
            DeletionStatus = new DeletionCardStatus(version,
                LocText.Of("card.delete.files", ("done", p.FilesDone), ("count", Math.Max(p.FilesTotal, p.FilesDone))),
                p.Fraction, false);

            TimeSpan? remaining = null;
            var elapsed = clock.Elapsed;
            if (elapsed >= EtaWarmUp && p.FilesDone > 0 && p.FilesTotal > p.FilesDone)
                remaining = TimeSpan.FromSeconds((p.FilesTotal - p.FilesDone) * elapsed.TotalSeconds / p.FilesDone);
            DeleteProgress = new LocText(() =>
                Loc.F("versions.delete.progress", ("done", p.FilesDone), ("count", Math.Max(p.FilesTotal, p.FilesDone)),
                    ("current", p.Current), ("versions", p.VersionCount)) +
                (remaining is { } left ? " · " + PlanRunViewModel.FormatRemaining(left) : ""));
            _context.ReportStatus(DeleteProgress);
        });
    }

    /// <summary>The version list's selection changed.</summary>
    public void SetMarkedVersions(IEnumerable<VersionRowViewModel> rows)
    {
        _marked = rows.ToList();
        DeleteVersionsCommand.NotifyCanExecuteChanged();
    }

    private bool CanDeleteVersions() => !IsDeleting && !IsResuming && !IsRestoring && _marked.Any(r => r.IsManaged);

    /// <summary>
    /// Confirm → delete the marked versions of the plan in the background (only those the plan manages) → take them out
    /// of the index → read the target again. Refused while a backup of the plan is queued or running, and while the plan
    /// is copied ("Copy plan…").
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteVersions))]
    private async Task DeleteVersionsAsync()
    {
        if (_savedPlan() is not { } plan)
            return;
        var rows = _marked.Where(VersionRows.Contains).ToList();
        var managed = rows.Where(r => r.IsManaged).ToList();
        if (managed.Count == 0)
            return;

        var dialogs = _context.Dialogs;
        var title = Loc.T("versions.delete.title");
        if (_isBackupActive())
        {
            dialogs.ShowError(title, Loc.T("versions.delete.backupRunning"));
            return;
        }
        if (_isCopyActive())
        {
            dialogs.ShowError(title, Loc.T("versions.delete.copyRunning"));
            return;
        }

        var listed = string.Join("\n", managed.Take(VersionsInConfirmation).Select(r => $"  {r.Name}  ·  {r.SizeText}"));
        var more = managed.Count > VersionsInConfirmation
            ? "\n  " + Loc.F("versions.delete.andMore", ("count", managed.Count - VersionsInConfirmation))
            : "";
        var bytes = managed.Sum(r => r.Info.TotalBytes ?? r.Indexed?.TotalBytes ?? 0);
        var message = managed.Count == 1
            ? Loc.F("versions.delete.confirm", ("versions", listed), ("size", Formats.Bytes(bytes)))
            : Loc.F("versions.delete.confirmMany", ("count", managed.Count), ("versions", listed), ("more", more),
                ("size", Formats.Bytes(bytes)));
        if (rows.Count > managed.Count)
            message += "\n\n" + Loc.F("versions.delete.notManagedNote", ("count", rows.Count - managed.Count));
        if (plan.Incremental)
            message += "\n\n" + Loc.T("versions.delete.poolCleanupNote");
        if (!dialogs.ConfirmDefaultNo(title, message))
            return;

        // A sync must not import folders that are being deleted; the target is read again afterwards.
        _syncCts?.Cancel();
        var cts = _deleteCts = new CancellationTokenSource();
        var names = managed.Select(r => r.Name).ToList();
        StartDeleting(LocText.Of("versions.delete.deleting", ("count", names.Count)));
        var progress = DeletionProgress(cts);
        IReadOnlyList<VersionDeletion> results;
        try
        {
            results = await RunAsJobAsync(cts, () => VersionDeleter.DeleteAsync(_context.Storages.Open(plan.Target), plan.Id,
                plan.Name, names, progress, cts.Token, _context.Host, pool: plan.Incremental));
            var gone = results.Where(r => r.Outcome is not (VersionDeletionOutcome.Failed or VersionDeletionOutcome.NotManaged))
                .Select(r => r.Name)
                .ToList();
            try
            {
                await _context.Worker.RunAsync(plan.Id, index => index.Remove(gone));
            }
            catch (Exception ex)
            {
                // The next sync takes them out of the index as well: their folders are gone.
                Trace.TraceWarning($"The version index of plan {plan.Id} could not drop deleted versions: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            var stopped = new LocText(() => Loc.F("versions.delete.stopped", ("error", Loc.Known(ex.Message))));
            _context.ReportStatus(stopped);
            dialogs.ShowError(title, stopped.ToString());
            results = [];
        }
        finally
        {
            EndDeleting();
        }

        var canceled = cts.IsCancellationRequested;
        cts.Dispose();
        var deleted = results.Count(r => r.Outcome is VersionDeletionOutcome.Deleted or VersionDeletionOutcome.RemainsLeft
            or VersionDeletionOutcome.Gone);
        if (canceled)
            _context.ReportStatus(LocText.Of("versions.delete.canceled", ("count", deleted)));
        else if (results.Count > 0)
            _context.ReportStatus(LocText.Of("versions.delete.done", ("count", deleted)));

        var problems = results.Where(r => r.Outcome is VersionDeletionOutcome.Failed or VersionDeletionOutcome.NotManaged
                or VersionDeletionOutcome.RemainsLeft)
            .Select(r => $"{r.Name}: {ProblemText(r)}")
            .ToList();
        if (problems.Count > 0)
        {
            dialogs.ShowFailures(title,
                Loc.F("versions.delete.problems", ("done", Loc.F("versions.delete.done", ("count", deleted))),
                    ("count", problems.Count)),
                problems);
        }

        VersionsDeleted?.Invoke();
        _ = IndexAfterDeleteAsync();
    }

    private void StartDeleting(LocText text)
    {
        IsDeleting = true;
        DeleteFraction = 0;
        DeleteIndeterminate = true;
        DeleteProgress = text;
        DeletionStatus = new DeletionCardStatus(LocText.Of("card.delete.version"), text, 0, true);
        _context.ReportStatus(DeleteProgress);
    }

    private void EndDeleting()
    {
        _deleteCts = null;
        IsDeleting = false;
        DeleteProgress = LocText.Empty;
        DeleteFraction = 0;
        DeleteIndeterminate = false;
        DeletionStatus = null;
    }

    /// <summary>
    /// Reads the target again and syncs the index after a deletion; meanwhile the plan card shows "Indexing…" with the
    /// sync's progress, until no sync runs any more (a sync started meanwhile supersedes this one).
    /// </summary>
    private async Task IndexAfterDeleteAsync()
    {
        var indexing = new DeletionCardStatus(LocText.Of("card.indexing"), new LocText(() => SyncText), 0, true);
        DeletionStatus = indexing;
        try
        {
            await RefreshAsync();
            while (IsSyncing && ReferenceEquals(DeletionStatus, indexing))
                await SyncEndedAsync();
        }
        finally
        {
            if (ReferenceEquals(DeletionStatus, indexing))
                DeletionStatus = null;
        }
    }

    /// <summary>Completes when <see cref="IsSyncing"/> turns false (at once when it is).</summary>
    private Task SyncEndedAsync()
    {
        if (!IsSyncing)
            return Task.CompletedTask;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(IsSyncing) or "" && !IsSyncing)
            {
                PropertyChanged -= OnChanged;
                ended.TrySetResult();
            }
        }
        PropertyChanged += OnChanged;
        if (!IsSyncing)
        {
            PropertyChanged -= OnChanged;
            ended.TrySetResult();
        }
        return ended.Task;
    }

    /// <summary>How long the app looks for unfinished deletions in a plan's target at start (an unreachable share can hang).</summary>
    public static readonly TimeSpan ResumeFindTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// At app start: finishes the plan's deletions this computer did not finish (version folders it marked as being
    /// deleted, an incremental plan's pending pool cleanup; <see cref="DeletionResumer"/>) in the background, with the same
    /// progress as a deletion by hand. The target is looked at first (at most <see cref="ResumeFindTimeout"/>); only when
    /// something is left does <see cref="IsResuming"/> turn true and the plan's runs wait. Nothing happens while the plan
    /// is queued or running, or while the target cannot be reached; the next start tries again. <paramref name="ct"/> is
    /// the app's exit: it stops looking, or stops after the folder being removed (what was removed leaves the index).
    /// </summary>
    public async Task ResumeDeletionAsync(CancellationToken ct = default)
    {
        if (_savedPlan() is not { } plan || IsDeleting || IsResuming || _isBackupActive())
            return;
        try
        {
            var target = _context.Storages.Open(plan.Target);
            var host = _context.Host;
            IReadOnlyList<string> folders;
            bool cleanup;
            using (var find = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                find.CancelAfter(ResumeFindTimeout);
                (folders, cleanup) = await Task.Run(() =>
                    DeletionResumer.FindAsync(target, plan.Id, plan.Incremental, host, find.Token), find.Token);
            }
            // A run (full plans take no lock) or a deletion by hand may have started meanwhile: it goes first.
            if ((folders.Count == 0 && !cleanup) || ct.IsCancellationRequested || IsDeleting || _isBackupActive())
                return;

            IsResuming = true;
            try
            {
                await ResumeFoundAsync(plan, target, host, ct);
            }
            finally
            {
                IsResuming = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Exit or timeout: the markers stay, the next start continues.
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Resuming the deletions of plan {plan.Id} failed: {ex.Message}");
        }
    }

    private async Task ResumeFoundAsync(BackupPlan plan, IStorage target, string host, CancellationToken ct)
    {
        _syncCts?.Cancel();
        var cts = _deleteCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        StartDeleting(LocText.Of("versions.delete.resuming"));
        var progress = DeletionProgress(cts);
        DeletionResume result;
        try
        {
            result = await RunAsJobAsync(cts, () =>
                DeletionResumer.ResumeAsync(target, plan.Id, plan.Name, plan.Incremental, progress, cts.Token, host));
            if (result.Removed.Count > 0)
                await _context.Worker.RunAsync(plan.Id, index => index.Remove(result.Removed));
        }
        catch (OperationCanceledException)
        {
            return;   // nothing was changed; the markers stay for the next start
        }
        finally
        {
            EndDeleting();
            cts.Dispose();
        }

        if (result.Removed.Count > 0)
            VersionsDeleted?.Invoke();
        if (result.Outcome == ResumeOutcome.Canceled)
            return;   // the rest stays for the next start
        _context.ReportStatus(result.Outcome == ResumeOutcome.Finished
            ? LocText.Of("versions.delete.resumed")
            : LocText.Of("versions.delete.resumeIncomplete"));
        if (_loaded)
            _ = IndexAfterDeleteAsync();
    }

    private static string ProblemText(VersionDeletion result) => result.Outcome switch
    {
        VersionDeletionOutcome.NotManaged => Loc.T("versions.delete.notManaged"),
        VersionDeletionOutcome.RemainsLeft => Loc.F("versions.delete.remainsLeft", ("error", Loc.Known(result.Error))),
        _ => Loc.Known(result.Error),
    };
}
