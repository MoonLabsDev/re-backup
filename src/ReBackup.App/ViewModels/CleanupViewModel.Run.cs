using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Cleanup;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.ViewModels;

/// <summary>The Cleanup tab: "Clean up…" after a confirmation, its outcome per version, and Cancel (spec 5).</summary>
public sealed partial class CleanupViewModel
{
    /// <summary>The cleanup this tab started runs; its progress reports count only while it is this object.</summary>
    private object? _run;

    private bool IsCleaningUp => _run is not null;

    /// <summary>
    /// "Clean up…" is enabled after a current scan that found something, while no backup, retry, copy, deletion, resume
    /// or restore of the plan runs (<see cref="VersionsViewModel.CanStartCleanup"/>).
    /// </summary>
    private bool CanCleanUp() =>
        _scan is { Result.Files: > 0 } && !IsStale && !IsScanning && !IsCleaningUp && _versions.CanStartCleanup;

    /// <summary>
    /// Asks (versions, files, bytes; "≈" for the pool of an incremental plan; "cannot be undone"), then cleans up the
    /// scanned versions with the scan's rules and stamps. Afterwards each row shows its outcome, the totals what was
    /// measured, the versions that failed, changed or went are listed, and the scan is dropped.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCleanUp))]
    private async Task CleanUpAsync()
    {
        // The global defaults may have changed in the settings since the scan.
        OnPlanChanged();
        if (!CanCleanUp() || _scan is not { } scan)
            return;
        var result = scan.Result;
        var dialogs = _context.Dialogs;
        var title = Loc.T("cleanup.title");
        var size = scan.Pool ? "≈ " + Formats.Bytes(result.PoolBytesFreed ?? 0) : Formats.Bytes(result.Bytes);
        var question = Loc.F("cleanup.confirm", ("count", result.Versions.Count), ("files", Formats.Files(result.Files)),
            ("size", size));
        if (!dialogs.ConfirmDefaultNo(title, question))
            return;
        // The question pumps the dispatcher: a run may have been queued, or the scan gone stale meanwhile.
        if (!ReferenceEquals(_scan, scan) || IsStale)
            return;
        if (!_versions.CanStartCleanup)
        {
            dialogs.ShowError(title, Loc.T("cleanup.busy"));
            return;
        }

        var targets = result.Versions.Select(v => new CleanupTarget(v.Name, v.Stamp)).ToList();
        var byName = Rows.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
            row.SetStatus(null);
        var run = _run = new object();
        NotifyCommands();
        var progress = new Progress<CleanupProgress>(p =>
        {
            if (ReferenceEquals(_run, run) && p.Phase != CleanupPhase.CollectingPool &&
                byName.TryGetValue(p.VersionName, out var row))
                row.SetStatus(LocText.Of("cleanup.status.running"));
        });
        CleanupResult? outcome;
        try
        {
            outcome = await _versions.CleanUpAsync(scan.Matcher, targets, progress);
        }
        finally
        {
            _run = null;
        }

        // Also after a failure: versions before it may have been cleaned up, so the scan no longer holds.
        DropScan();
        foreach (var row in Rows)
            row.SetStatus(null);
        if (outcome is not null)
            ShowOutcome(outcome, byName, title);
        RefreshHint();
        NotifyCommands();
    }

    private bool CanCancel() => IsCleaningUp;

    /// <summary>Stops the cleanup after the version being cleaned up.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _versions.CancelCleanup();

    /// <summary>Each version's outcome in its row, the measured totals, and the versions that were not cleaned up.</summary>
    private void ShowOutcome(CleanupResult outcome, IReadOnlyDictionary<string, CleanupRowViewModel> byName, string title)
    {
        foreach (var version in outcome.Versions)
        {
            if (byName.TryGetValue(version.Name, out var row))
                row.SetStatus(StatusOf(version));
        }
        var cleaned = outcome.Versions.Count(v => v.Outcome == CleanupOutcome.Cleaned);
        var files = outcome.FilesRemoved;
        var freed = outcome.BytesFreed;
        TotalsText = LocText.Of("cleanup.result", ("count", cleaned), ("files", Formats.Files(files)),
            ("size", Formats.Bytes(freed)));

        var problems = outcome.Versions
            .Where(v => v.Outcome is CleanupOutcome.Failed or CleanupOutcome.Changed or CleanupOutcome.Gone)
            .Select(v => $"{v.Name}: {StatusOf(v)}")
            .ToList();
        if (problems.Count > 0)
            _context.Dialogs.ShowFailures(title, Loc.F("cleanup.problems", ("count", problems.Count)), problems);
    }

    private static LocText StatusOf(CleanupVersionResult version)
    {
        var error = version.Error ?? "";
        return version.Outcome switch
        {
            CleanupOutcome.Cleaned => LocText.Of("cleanup.status.cleaned"),
            CleanupOutcome.NothingToDo => LocText.Of("cleanup.status.nothing"),
            CleanupOutcome.Changed => LocText.Of("cleanup.status.changed"),
            CleanupOutcome.Gone => LocText.Of("cleanup.status.gone"),
            _ => new LocText(() => Loc.F("cleanup.status.failed", ("error", Loc.Known(error)))),
        };
    }
}
