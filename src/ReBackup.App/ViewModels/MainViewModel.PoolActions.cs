using System.IO;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Shared.Localization;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The plan list's actions on a plan's target: "Copy plan…" (every plan), "Verify backup…" and "Release lock…"
/// (incremental plans). They act on the plan given, else the selected one, as saved.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The clock of the locks this app takes.</summary>
    private readonly TimeProvider _time = TimeProvider.System;

    /// <summary>A plan was selected: its lock banner reads the target's lock.</summary>
    partial void OnSelectedPlanChanged(PlanEditorViewModel? value) => _ = value?.RefreshLockAsync();

    /// <summary>
    /// "Copy plan…": the dialog copies the saved plan's versions to a new target. Refused for an unsaved plan and while
    /// the plan runs or is copied already; while the copy runs, the plan neither runs nor loses versions.
    /// </summary>
    [RelayCommand]
    private void CopyPlan(PlanEditorViewModel? editor)
    {
        editor ??= SelectedPlan;
        if (editor is null)
            return;
        if (editor.IsNew)
        {
            SetStatus(LocText.Of("shell.status.saveFirst", ("plan", editor.Name)));
            return;
        }
        var title = Loc.T("copy.title");
        if (editor.Run.IsActive)
        {
            _dialogs.ShowError(title, Loc.F("copy.backupRunning", ("plan", editor.Name)));
            return;
        }
        if (editor.IsCopying)
        {
            _dialogs.ShowError(title, Loc.F("copy.alreadyCopying", ("plan", editor.Name)));
            return;
        }

        // The same connection lookups as the plan validation: two connections to one bucket or root are one place.
        var copier = new PlanCopier(_versions.Storages, _versions.Host, _time, _s3.BucketOf, _drive.RootOf);
        var dialog = new CopyPlanViewModel(editor.SavedPlan(), copier, ValidateNewPlan, _s3, _drive, _dialogs, AddCopiedPlan,
            copying => SetCopying(editor, copying));
        _dialogs.ShowCopyPlan(dialog);
        _ = editor.RefreshLockAsync();
    }

    /// <summary>Marks the plan as copied (or no longer); called by the dialog on the UI thread.</summary>
    private void SetCopying(PlanEditorViewModel editor, bool copying)
    {
        editor.IsCopying = copying;
        if (copying)
            SetStatus(LocText.Of("shell.status.copying", ("plan", editor.Name)));
    }

    /// <summary>What would be wrong with <paramref name="plan"/> in the plan list.</summary>
    private IReadOnlyList<Message> ValidateNewPlan(BackupPlan plan) =>
        PlanValidator.Validate(plan, AllPlans(), _s3.BucketOf, _drive.RootOf);

    /// <summary>
    /// Saves a finished copy as a new plan and shows it at the end of the list, disabled as the copier made it; called by
    /// the dialog on the UI thread. False when its file could not be written.
    /// </summary>
    private bool AddCopiedPlan(BackupPlan plan)
    {
        try
        {
            _store.Save(plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(Loc.T("copy.title"), Loc.F("copy.saveFailed", ("error", ex.Message)));
            return false;
        }
        if (!Plans.Any(p => p.Id.Equals(plan.Id, StringComparison.OrdinalIgnoreCase)))   // else a reload from disk was quicker
            AddEditor(new PlanEditorViewModel(plan, isNew: false, AllPlans, GlobalIgnoreDefaults, _folders, _versions, _s3, _drive));
        RevalidateAll();
        PublishPlans();
        SavePlanOrder();
        SetStatus(LocText.Of("shell.status.copied", ("plan", plan.Name)));
        return true;
    }

    /// <summary>"Verify backup…" of an incremental plan as saved; the dialog takes the target's lock itself.</summary>
    [RelayCommand]
    private void VerifyBackup(PlanEditorViewModel? editor)
    {
        editor ??= SelectedPlan;
        if (editor is not { ShowPoolActions: true })
            return;
        _dialogs.ShowVerify(new VerifyViewModel(editor.SavedPlan(), _versions.Storages, _versions.Host, _time));
        _ = editor.RefreshLockAsync();
    }

    /// <summary>"Release lock…" of an incremental plan (asks first).</summary>
    [RelayCommand]
    private Task ReleaseLockAsync(PlanEditorViewModel? editor) =>
        (editor ?? SelectedPlan) is { ShowPoolActions: true } plan ? plan.ReleaseLockCommand.ExecuteAsync(null) : Task.CompletedTask;
}
