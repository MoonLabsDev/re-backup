using System.IO;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Shared.Localization;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The plan list's actions on a plan's target: "Copy plan…" (every plan) and "Continue copy…" (an unfinished copy),
/// "Verify backup…" and "Release lock…" (incremental plans). They act on the plan given, else the selected one, as saved.
/// Running copies and verifies are tracked, so that exiting stops them and waits until their locks are released.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The clock of the locks this app takes.</summary>
    private readonly TimeProvider _time = TimeProvider.System;

    /// <summary>The copies and verifies running now.</summary>
    private readonly HashSet<IPoolJob> _poolJobs = [];

    /// <summary>A plan was selected: its lock banner reads the target's lock.</summary>
    partial void OnSelectedPlanChanged(PlanEditorViewModel? value) => _ = value?.RefreshLockAsync();

    /// <summary>A "Copy plan…" or "Verify backup…" is running.</summary>
    public bool IsAnyPoolJobRunning
    {
        get
        {
            lock (_poolJobs)
                return _poolJobs.Count > 0;
        }
    }

    /// <summary>
    /// Stops every running copy and verify and waits up to <paramref name="wait"/> until their work has ended and released
    /// its locks (a copy leaves its plan marked unfinished; "Continue copy…" goes on). Blocks the calling thread; the work
    /// does not need it. False when one is still running after the wait.
    /// </summary>
    public bool StopPoolJobs(TimeSpan wait)
    {
        _exit.Cancel();   // resumed deletions stop looking for work
        IPoolJob[] jobs;
        lock (_poolJobs)
            jobs = [.. _poolJobs];
        foreach (var job in jobs)
            job.Stop();
        try
        {
            return Task.WaitAll(jobs.Select(job => job.Work).ToArray(), wait);
        }
        catch (AggregateException)
        {
            return true;   // work that failed has ended too
        }
    }

    private void Track(IPoolJob job, bool running)
    {
        lock (_poolJobs)
        {
            if (running)
                _poolJobs.Add(job);
            else
                _poolJobs.Remove(job);
        }
    }

    /// <summary>
    /// "Copy plan…": the dialog copies the saved plan's versions to a new target. Refused for an unsaved plan and while
    /// the plan runs or is copied already; while the copy runs, the plan neither runs nor loses versions. On an unfinished
    /// copy it is "Continue copy…".
    /// </summary>
    [RelayCommand]
    private void CopyPlan(PlanEditorViewModel? editor)
    {
        editor ??= SelectedPlan;
        if (editor is null)
            return;
        if (editor.IsCopyUnfinished)
        {
            ContinueCopy(editor);
            return;
        }
        if (editor.IsNew)
        {
            SetStatus(LocText.Of("shell.status.saveFirst", ("plan", editor.Name)));
            return;
        }
        if (CopyBlocker(editor) is { } blocked)
        {
            _dialogs.ShowError(Loc.T("copy.title"), blocked);
            return;
        }
        OpenCopy(editor, unfinished: null);
    }

    /// <summary>
    /// "Continue copy…" of a plan "Copy plan…" has not finished: the dialog continues the copy from the plan it copies (the
    /// versions complete on the target are skipped). When that plan no longer exists, the copy cannot be finished: the
    /// message says so, and the unfinished copy can be deleted.
    /// </summary>
    [RelayCommand]
    private void ContinueCopy(PlanEditorViewModel? editor)
    {
        editor ??= SelectedPlan;
        if (editor is not { IsCopyUnfinished: true })
            return;
        var title = Loc.T("copy.title");
        var copy = editor.SavedPlan();
        var source = Plans.FirstOrDefault(p => !p.IsNew && p.Id.Equals(copy.CopyFromPlanId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            _dialogs.ShowError(title, Loc.F("copy.sourceGone", ("plan", copy.Name)));
            return;
        }
        if (editor.IsCopying)
        {
            _dialogs.ShowError(title, Loc.F("copy.alreadyCopying", ("plan", copy.Name)));
            return;
        }
        if (CopyBlocker(source) is { } blocked)
        {
            _dialogs.ShowError(title, blocked);
            return;
        }
        OpenCopy(source, copy);
    }

    /// <summary>Why <paramref name="source"/> cannot be copied right now; null when it can.</summary>
    private static string? CopyBlocker(PlanEditorViewModel source)
    {
        if (source.Run.IsActive)
            return Loc.F("copy.backupRunning", ("plan", source.Name));
        if (source.IsCopying)
            return Loc.F("copy.alreadyCopying", ("plan", source.Name));
        return null;
    }

    private void OpenCopy(PlanEditorViewModel source, BackupPlan? unfinished)
    {
        // The same connection lookups as the plan validation: two connections to one bucket or root are one place.
        var copier = new PlanCopier(_versions.Storages, _versions.Host, _time, _s3.BucketOf, _drive.RootOf);
        CopyPlanViewModel? dialog = null;
        var hooks = new CopyPlanHooks(ValidateNewPlan, AddCopyPlan, FinishCopyPlan,
            copying => SetCopying(source, dialog!, copying),
            // Asked when the copy starts: a run may have been queued while the dialog was open.
            () => source.Run.IsActive ? Loc.F("copy.backupRunning", ("plan", source.Name)) : null);
        dialog = new CopyPlanViewModel(source.SavedPlan(), copier, _s3, _drive, _dialogs, hooks, unfinished);
        _dialogs.ShowCopyPlan(dialog);
        _ = source.RefreshLockAsync();
    }

    /// <summary>
    /// Marks the plan that is copied, and the copy's plan once it is saved, as copied (or no longer); called by the
    /// dialog on the UI thread.
    /// </summary>
    private void SetCopying(PlanEditorViewModel source, CopyPlanViewModel dialog, bool copying)
    {
        Track(dialog, copying);
        source.IsCopying = copying;
        if (CopyEditor(dialog.CopyPlan) is { } copy)
            copy.IsCopying = copying;
        if (copying)
            SetStatus(LocText.Of("shell.status.copying", ("plan", source.Name)));
    }

    private PlanEditorViewModel? CopyEditor(BackupPlan? plan) =>
        plan is null ? null : Plans.FirstOrDefault(p => p.Id.Equals(plan.Id, StringComparison.OrdinalIgnoreCase));

    /// <summary>What would be wrong with <paramref name="plan"/> in the plan list.</summary>
    private IReadOnlyList<Message> ValidateNewPlan(BackupPlan plan) =>
        PlanValidator.Validate(plan, AllPlans().Where(p => !p.Id.Equals(plan.Id, StringComparison.OrdinalIgnoreCase)),
            _s3.BucketOf, _drive.RootOf);

    /// <summary>
    /// Saves a new copy's plan (disabled and marked unfinished) through the plan store before the copy writes anything,
    /// and shows it at the end of the list. Called by the dialog on the UI thread, while the copy runs. False when its file
    /// could not be written.
    /// </summary>
    private bool AddCopyPlan(BackupPlan plan)
    {
        if (!TrySavePlanFile(plan))
            return false;
        var editor = CopyEditor(plan);
        if (editor is null)   // else a reload from disk was quicker
        {
            editor = NewEditor(plan, isNew: false);
            AddEditor(editor);
        }
        editor.IsCopying = true;   // the copy runs: neither run nor delete it meanwhile
        RevalidateAll();
        PublishPlans();
        SavePlanOrder();
        SetStatus(LocText.Of("shell.status.copyAdded", ("plan", plan.Name)));
        return true;
    }

    /// <summary>
    /// The copy has finished: the unfinished mark is removed from the copy's plan as saved now (edits saved meanwhile
    /// stay; unsaved edits stay unsaved). False when the file could not be written.
    /// </summary>
    private bool FinishCopyPlan(BackupPlan plan)
    {
        var editor = CopyEditor(plan);
        var saved = editor?.SavedPlan() ?? plan.Clone();
        saved.CopyFromPlanId = null;
        if (!TrySavePlanFile(saved))
            return false;
        editor?.MarkCopyFinished();
        RevalidateAll();
        PublishPlans();
        SetStatus(LocText.Of("shell.status.copied", ("plan", saved.Name)));
        return true;
    }

    private bool TrySavePlanFile(BackupPlan plan)
    {
        try
        {
            _store.Save(plan);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(Loc.T("copy.title"), Loc.F("copy.saveFailed", ("error", ex.Message)));
            return false;
        }
    }

    /// <summary>"Verify backup…" of an incremental plan as saved; the dialog takes the target's lock itself.</summary>
    [RelayCommand]
    private void VerifyBackup(PlanEditorViewModel? editor)
    {
        editor ??= SelectedPlan;
        if (editor is not { ShowPoolActions: true })
            return;
        VerifyViewModel? dialog = null;
        dialog = new VerifyViewModel(editor.SavedPlan(), _versions.Storages, _versions.Host, _time, running =>
        {
            Track(dialog!, running);
            editor.IsVerifying = running;
        });
        _dialogs.ShowVerify(dialog);
        _ = editor.RefreshLockAsync();
    }

    /// <summary>"Release lock…" of a saved plan whose target is locked (asks first).</summary>
    [RelayCommand]
    private Task ReleaseLockAsync(PlanEditorViewModel? editor) =>
        (editor ?? SelectedPlan) is { ShowReleaseLock: true } plan ? plan.ReleaseLockCommand.ExecuteAsync(null) : Task.CompletedTask;
}
