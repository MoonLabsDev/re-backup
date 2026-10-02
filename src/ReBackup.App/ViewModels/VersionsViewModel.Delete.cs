using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Localization;
using ReBackup.Core.Backup;

namespace ReBackup.App.ViewModels;

/// <summary>The Versions tab: deleting versions picked by hand, after a confirmation.</summary>
public sealed partial class VersionsViewModel
{
    /// <summary>Versions listed in the confirmation; the rest are summed up as "…".</summary>
    private const int VersionsInConfirmation = 8;

    /// <summary>The rows marked in the version list (several with Ctrl or Shift); the delete acts on these.</summary>
    private IReadOnlyList<VersionRowViewModel> _marked = [];

    /// <summary>The remaining time is shown once the deletion has run this long (the first files say little about the rate).</summary>
    private static readonly TimeSpan EtaWarmUp = TimeSpan.FromSeconds(3);

    private CancellationTokenSource? _deleteCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteVersionsCommand), nameof(RestoreToOriginalCommand), nameof(RestoreToCommand),
        nameof(CancelDeleteCommand))]
    private bool _isDeleting;

    /// <summary>"Deleting 1,200 of 50,000 files · version 1 of 3 · 4 min left" while a deletion runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteText))]
    private LocText _deleteProgress = LocText.Empty;

    public string DeleteText => DeleteProgress.ToString();

    [ObservableProperty] private double _deleteFraction;

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
    }

    /// <summary>
    /// The progress line of a deletion, with the remaining time from the average rate since it started; reports come on
    /// the UI thread.
    /// </summary>
    private IProgress<VersionDeletionProgress> DeletionProgress(CancellationTokenSource cts)
    {
        var clock = Stopwatch.StartNew();
        return new Progress<VersionDeletionProgress>(p =>
        {
            if (!IsDeleting || !ReferenceEquals(_deleteCts, cts) || cts.IsCancellationRequested)
                return;
            DeleteFraction = p.Fraction;
            if (p.Phase == VersionDeletionPhase.Preparing)
            {
                clock.Restart();   // the rate counts from the first deleted file on
                DeleteProgress = LocText.Of("versions.delete.preparing", ("current", p.Current), ("versions", p.VersionCount));
                return;
            }

            TimeSpan? remaining = null;
            var elapsed = clock.Elapsed;
            if (elapsed >= EtaWarmUp && p.FilesDone > 0 && p.FilesTotal > p.FilesDone)
                remaining = TimeSpan.FromSeconds((p.FilesTotal - p.FilesDone) * elapsed.TotalSeconds / p.FilesDone);
            DeleteProgress = new LocText(() =>
                Loc.F("versions.delete.progress", ("done", p.FilesDone), ("count", Math.Max(p.FilesTotal, p.FilesDone)),
                    ("current", p.Current), ("versions", p.VersionCount)) +
                (remaining is { } left ? " · " + PlanRunViewModel.FormatRemaining(left) : ""));
        });
    }

    /// <summary>The version list's selection changed.</summary>
    public void SetMarkedVersions(IEnumerable<VersionRowViewModel> rows)
    {
        _marked = rows.ToList();
        DeleteVersionsCommand.NotifyCanExecuteChanged();
    }

    private bool CanDeleteVersions() => !IsDeleting && !IsRestoring && _marked.Any(r => r.IsManaged);

    /// <summary>
    /// Confirm → delete the marked versions of the plan in the background (only those the plan manages) → take them out
    /// of the index → read the target again. Refused while a backup of the plan is queued or running.
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

        var listed = string.Join("\n", managed.Take(VersionsInConfirmation).Select(r => $"  {r.DateText}  ·  {r.SizeText}"));
        var more = managed.Count > VersionsInConfirmation ? "\n  …" : "";
        var bytes = managed.Sum(r => r.Info.TotalBytes ?? r.Indexed?.TotalBytes ?? 0);
        var message = managed.Count == 1
            ? Loc.F("versions.delete.confirm", ("versions", listed), ("size", Formats.Bytes(bytes)))
            : Loc.F("versions.delete.confirmMany", ("count", managed.Count), ("versions", listed), ("more", more),
                ("size", Formats.Bytes(bytes)));
        if (rows.Count > managed.Count)
            message += "\n\n" + Loc.F("versions.delete.notManagedNote", ("count", rows.Count - managed.Count));
        if (!dialogs.ConfirmDefaultNo(title, message))
            return;

        // A sync must not import folders that are being deleted; the target is read again afterwards.
        _syncCts?.Cancel();
        var cts = _deleteCts = new CancellationTokenSource();
        IsDeleting = true;
        DeleteFraction = 0;
        var names = managed.Select(r => r.Name).ToList();
        DeleteProgress = LocText.Of("versions.delete.deleting", ("count", names.Count));
        _context.ReportStatus(DeleteProgress);
        var progress = DeletionProgress(cts);
        IReadOnlyList<VersionDeletion> results;
        try
        {
            results = await Task.Run(() => VersionDeleter.Delete(plan.Target, plan.Id, names, progress: progress,
                cancellationToken: cts.Token));
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
            _deleteCts = null;
            IsDeleting = false;
            DeleteProgress = LocText.Empty;
            DeleteFraction = 0;
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
        _ = RefreshAsync();
    }

    private static string ProblemText(VersionDeletion result) => result.Outcome switch
    {
        VersionDeletionOutcome.NotManaged => Loc.T("versions.delete.notManaged"),
        VersionDeletionOutcome.RemainsLeft => Loc.F("versions.delete.remainsLeft", ("error", Loc.Known(result.Error))),
        _ => Loc.Known(result.Error),
    };
}
