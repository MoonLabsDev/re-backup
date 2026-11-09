using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Cleanup;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.ViewModels;

/// <summary>The Cleanup tab: "Clean up…" after a confirmation, its outcome per version, and Cancel (spec 5).</summary>
public sealed partial class CleanupViewModel
{
    /// <summary>The cleanup this tab started runs; its progress reports count only while it is this object.</summary>
    private object? _run;

    /// <summary>A cleanup this tab started runs: the tab shows its progress and Cancel.</summary>
    public bool IsCleaningUp => _run is not null;

    /// <summary>True while the files of a full plan's version are deleted: the second bar shows how far that version is.</summary>
    [ObservableProperty] private bool _hasStep;

    /// <summary>How much of the version being cleaned up is deleted, 0 to 1.</summary>
    [ObservableProperty] private double _stepFraction;

    /// <summary>E.g. "2.1 GB of 23.8 GB deleted".</summary>
    [ObservableProperty] private string _stepText = "";

    /// <summary>
    /// "Clean up…" is enabled after a current scan that found something, while the plan has no unsaved changes ("Save
    /// first") and no backup, retry, copy, deletion, resume or restore of it runs
    /// (<see cref="VersionsViewModel.CanStartCleanup"/>).
    /// </summary>
    private bool CanCleanUp() =>
        _scan is { Result.Files: > 0 } && !IsStale && !IsScanning && !IsCleaningUp && !_hasUnsavedChanges() &&
        _versions.CanStartCleanup;

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
        var byName = RowsByName();
        foreach (var row in Rows)
            row.SetStatus(null);
        var run = _run = new object();
        OnPropertyChanged(nameof(IsCleaningUp));
        NotifyCommands();
        var progress = new Progress<CleanupProgress>(p =>
        {
            if (!ReferenceEquals(_run, run))
                return;
            if (p.Phase != CleanupPhase.CollectingPool && byName.TryGetValue(p.VersionName, out var row) &&
                row.Lamp != CleanupLamp.Running)
                row.SetStatus(LocText.Of("cleanup.status.running"), CleanupLamp.Running);
            ShowStep(p);
        });
        CleanupResult? outcome;
        try
        {
            outcome = await _versions.CleanUpAsync(scan.Matcher, targets, progress);
        }
        finally
        {
            _run = null;
            HasStep = false;
            OnPropertyChanged(nameof(IsCleaningUp));
        }

        // Also after a failure: versions before it may have been cleaned up, so the scan no longer holds.
        DropScan();
        foreach (var row in Rows)
            row.SetStatus(null);
        if (outcome is not null)
            ShowOutcome(outcome, targets, byName, title);
        RefreshHint();
        NotifyCommands();
    }

    private bool CanCancel() => IsCleaningUp;

    /// <summary>Stops the cleanup after the version being cleaned up.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _versions.CancelCleanup();

    /// <summary>The second bar: the bytes of a full plan's version deleted so far; hidden otherwise.</summary>
    private void ShowStep(CleanupProgress p)
    {
        HasStep = p.Phase == CleanupPhase.Deleting && p.BytesTotal > 0;
        if (!HasStep)
            return;
        StepFraction = Math.Clamp((double)p.BytesDone / p.BytesTotal, 0, 1);
        StepText = Loc.F("cleanup.step", ("done", Formats.Bytes(p.BytesDone)), ("total", Formats.Bytes(p.BytesTotal)));
    }

    /// <summary>
    /// Each version's outcome in its row (a version the run stopped before shows that), the measured totals, and the
    /// versions that were not cleaned up.
    /// </summary>
    private void ShowOutcome(CleanupResult outcome, IReadOnlyList<CleanupTarget> targets,
        IReadOnlyDictionary<string, CleanupRowViewModel> byName, string title)
    {
        foreach (var version in outcome.Versions)
        {
            if (byName.TryGetValue(version.Name, out var row))
                row.SetStatus(StatusOf(version), LampOf(version.Outcome));
        }
        if (outcome.Canceled)
        {
            var reached = outcome.Versions.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets.Where(t => !reached.Contains(t.Name)))
            {
                if (byName.TryGetValue(target.Name, out var row))
                    row.SetStatus(LocText.Of("cleanup.status.stopped"), CleanupLamp.Warning);
            }
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

    private static CleanupLamp LampOf(CleanupOutcome outcome) => outcome switch
    {
        CleanupOutcome.Cleaned => CleanupLamp.Done,
        CleanupOutcome.NothingToDo => CleanupLamp.Idle,
        CleanupOutcome.Changed or CleanupOutcome.Gone => CleanupLamp.Warning,
        _ => CleanupLamp.Failed,
    };

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
