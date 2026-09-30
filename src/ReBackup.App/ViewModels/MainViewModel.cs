using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Core.Backup;
using ReBackup.Core.Config;
using ReBackup.Core.Plans;
using ReBackup.Core.Schedule;
using ReBackup.Core.Settings;

namespace ReBackup.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly PlanStore _store;
    private readonly ConfigPaths _paths;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly Action _openSettings;
    private readonly BackupQueue _queue;
    private readonly Scheduler _scheduler;
    private readonly Action<Action> _runOnUi;

    [ObservableProperty] private PlanEditorViewModel? _selectedPlan;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string _queueStatus = "No backup running";
    [ObservableProperty] private string _schedulerStatus = "";

    public MainViewModel(PlanStore store, ConfigPaths paths, AppSettings settings, IDialogService dialogs,
        Action openSettings, BackupQueue queue, Scheduler scheduler, Action<Action> runOnUi)
    {
        _store = store;
        _paths = paths;
        _settings = settings;
        _dialogs = dialogs;
        _openSettings = openSettings;
        _queue = queue;
        _scheduler = scheduler;
        _runOnUi = runOnUi;
        _queue.Changed += update => _runOnUi(() => OnJobUpdate(update));

        var result = _store.LoadAll();
        foreach (var plan in result.Plans)
            AddEditor(new PlanEditorViewModel(plan, isNew: false, AllPlans, GlobalIgnoreDefaults));
        RevalidateAll();
        SelectedPlan = Plans.FirstOrDefault();
        StatusMessage = LoadErrorText(result) ?? $"Configuration: {_paths.Root}";
        PublishPlans();
        RefreshSchedule();
    }

    /// <summary>Plan name and result of a finished run; raised on the UI thread.</summary>
    public event Action<string, RunLogEntry>? RunFinished;

    public ObservableCollection<PlanEditorViewModel> Plans { get; } = [];

    /// <summary>Re-applies the patterns in every open preview, e.g. after the global defaults changed.</summary>
    public void ReevaluatePreviews()
    {
        foreach (var editor in Plans)
            editor.Preview.RequestReevaluate();
    }

    /// <summary>Saved plans for the tray menu; <c>CanRun</c> is false for unsaved, invalid or already active plans.</summary>
    public IReadOnlyList<(string Id, string Name, bool CanRun)> RunnablePlans =>
        Plans.Where(p => !p.IsNew)
            .Select(p => (p.Id, p.Name, CanRun: !p.IsDirty && p.Errors.Count == 0 && !p.Run.IsActive))
            .ToList();

    public bool HasUnsavedChanges => Plans.Any(p => p.IsDirty);

    public IEnumerable<string> UnsavedPlanNames =>
        Plans.Where(p => p.IsDirty).Select(p => string.IsNullOrWhiteSpace(p.Name) ? "(unnamed)" : p.Name);

    public void ReloadFromDisk()
    {
        PlanLoadResult result;
        try
        {
            result = _store.LoadAll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Plans could not be reloaded: {ex.Message}";
            return;
        }

        var loaded = result.Plans.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var unreadable = result.Errors
            .Select(e => Path.GetFileNameWithoutExtension(e.FilePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var editor in Plans.ToList())
        {
            if (loaded.Remove(editor.Id, out var plan))
            {
                if (editor.IsDirty)
                    editor.MarkAsExisting();
                else
                    editor.ReplaceSaved(plan);
            }
            else if (unreadable.Contains(editor.Id))
            {
                // File exists but cannot be read right now: leave the editor untouched.
            }
            else if (!editor.IsNew)
            {
                if (editor.IsDirty)
                    editor.MarkAsNew();
                else
                    Plans.Remove(editor);
            }
        }

        foreach (var plan in loaded.Values)
            AddEditor(new PlanEditorViewModel(plan, isNew: false, AllPlans, GlobalIgnoreDefaults));

        RevalidateAll();

        if (SelectedPlan is null || !Plans.Contains(SelectedPlan))
            SelectedPlan = Plans.FirstOrDefault();

        StatusMessage = LoadErrorText(result) ?? "Plans reloaded after a change on disk.";
        PublishPlans();
    }

    [RelayCommand]
    private void NewPlan()
    {
        var editor = new PlanEditorViewModel(new BackupPlan { Name = UniqueName("New plan") }, isNew: true, AllPlans, GlobalIgnoreDefaults);
        AddEditor(editor);
        RevalidateAll();
        SelectedPlan = editor;
    }

    [RelayCommand]
    private void DeletePlan()
    {
        var editor = SelectedPlan;
        if (editor is null)
            return;

        if (editor.Run.IsActive)
        {
            StatusMessage = $"Cancel the backup of \"{editor.Name}\" before deleting the plan.";
            return;
        }

        if (!editor.IsNew)
        {
            if (!_dialogs.Confirm("Delete plan",
                    $"Delete plan \"{editor.Name}\"?\n\nBackups already stored in the target are not touched."))
                return;
            var deleteLog = _dialogs.AskYesNoCancel("Delete plan", "Also delete this plan's run history?");
            if (deleteLog is null)
                return;

            try
            {
                _store.Delete(editor.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dialogs.ShowError("Delete plan", ex.Message);
                return;
            }

            if (deleteLog == true)
            {
                try
                {
                    var logFile = _paths.LogFileFor(editor.Id);
                    if (File.Exists(logFile))
                        File.Delete(logFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _dialogs.ShowError("Delete plan", $"The plan was deleted, but its run history could not be: {ex.Message}");
                }
            }
        }

        // Modal dialogs pump the dispatcher, so a reload may already have removed the editor.
        var index = Plans.IndexOf(editor);
        if (index >= 0)
            Plans.RemoveAt(index);
        RevalidateAll();
        SelectedPlan = Plans.Count == 0 ? null : Plans[Math.Clamp(index, 0, Plans.Count - 1)];
        PublishPlans();
    }

    [RelayCommand]
    private void Save()
    {
        var editor = SelectedPlan;
        if (editor is null || !editor.IsDirty)
            return;

        try
        {
            var saved = editor.TrySave(_store);
            StatusMessage = saved
                ? $"Saved \"{editor.Name}\"."
                : "Not saved: fix the errors shown in the plan.";
            RevalidateAll();
            if (saved)
                PublishPlans();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Save failed", ex.Message);
        }
    }

    [RelayCommand]
    private void Revert()
    {
        var editor = SelectedPlan;
        if (editor is null)
            return;

        if (editor.IsNew)
        {
            Plans.Remove(editor);
            SelectedPlan = Plans.FirstOrDefault();
        }
        else
        {
            editor.Revert();
        }
        RevalidateAll();
    }

    [RelayCommand]
    private void BrowseSource()
    {
        if (SelectedPlan is { } editor && _dialogs.PickFolder("Choose source folder", editor.Source) is { } folder)
            editor.Source = folder;
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        if (SelectedPlan is { } editor && _dialogs.PickFolder("Choose target folder", editor.Target) is { } folder)
            editor.Target = folder;
    }

    [RelayCommand]
    private void OpenSettings() => _openSettings();

    /// <summary>Queues a run of the saved plan. False when it is dirty, invalid, unknown or already queued.</summary>
    public bool RunPlan(string planId)
    {
        var editor = Plans.FirstOrDefault(p => p.Id.Equals(planId, StringComparison.OrdinalIgnoreCase));
        return editor is not null && Start(editor);
    }

    /// <summary>Queues a run the scheduler started. It uses the plan as saved; unsaved edits do not matter.</summary>
    public void RunScheduled(string planId, RunTrigger trigger)
    {
        // Called from the scheduler through the dispatcher: an exception here would end the app.
        try
        {
            var editor = Plans.FirstOrDefault(p => p.Id.Equals(planId, StringComparison.OrdinalIgnoreCase));
            if (editor is null || editor.IsNew)
                return;
            var plan = editor.SavedPlan();
            if (!plan.Enabled)
                return;   // disabled after the scheduler decided
            _queue.Enqueue(new BackupRequest(plan, _settings.DefaultIgnorePatterns.ToList(), trigger));
        }
        catch (Exception ex)
        {
            StatusMessage = $"A scheduled backup could not be queued: {ex.Message}";
        }
    }

    /// <summary>
    /// Updates the "next run" texts, the next runs shown on the Schedule tab and the scheduler state. Called whenever
    /// the scheduler reports a change, so at least once a minute.
    /// </summary>
    public void RefreshSchedule()
    {
        // Called from the scheduler through the dispatcher: an exception here would end the app.
        try
        {
            var paused = _scheduler.IsPaused;
            SchedulerStatus = paused ? "Scheduler paused" : "";
            foreach (var editor in Plans)
            {
                editor.SchedulerPaused = paused;
                editor.Run.NextRunText = NextRunText(editor, paused);
            }
            SelectedPlan?.RefreshNextRuns();
        }
        catch (Exception ex)
        {
            StatusMessage = $"The schedule could not be updated: {ex.Message}";
        }
    }

    private string NextRunText(PlanEditorViewModel editor, bool paused)
    {
        if (editor.IsNew)
            return "";
        var plan = editor.SavedPlan();
        if (!plan.Enabled)
            return "Disabled: runs only by hand";
        if (plan.Triggers.Count == 0)
            return "No schedule";
        if (plan.Triggers.Any(trigger => ScheduleTriggers.Validate(trigger) is not null))
            return "Schedule has errors: does not run";
        if (paused)
            return "Scheduler paused";
        return _scheduler.NextRunUtc(editor.Id) is { } next
            ? "Next run " + next.ToLocalTime().ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : "No schedule";
    }

    /// <summary>Hands the saved state of all saved plans to the scheduler.</summary>
    private void PublishPlans() =>
        _scheduler.UpdatePlans(Plans
            .Where(p => !p.IsNew)
            .Select(p => p.SavedPlan())
            .Select(plan => new ScheduledPlan(plan.Id, plan.Enabled, plan.Triggers))
            .ToList());

    [RelayCommand]
    private void RunNow(PlanEditorViewModel? editor)
    {
        editor ??= SelectedPlan;
        if (editor is not null)
            Start(editor);
    }

    [RelayCommand]
    private void CancelRun(PlanEditorViewModel? editor)
    {
        editor ??= SelectedPlan;
        if (editor is not null && _queue.Cancel(editor.Id))
            StatusMessage = $"Canceling the backup of \"{editor.Name}\"…";
    }

    private bool Start(PlanEditorViewModel editor)
    {
        if (editor.IsNew || editor.IsDirty || editor.Errors.Count > 0)
        {
            StatusMessage = $"Save \"{editor.Name}\" without errors before running it.";
            return false;
        }

        var request = new BackupRequest(editor.SavedPlan(), _settings.DefaultIgnorePatterns.ToList(), RunTrigger.Manual);
        if (!_queue.Enqueue(request))
        {
            StatusMessage = $"\"{editor.Name}\" is already queued or running.";
            return false;
        }
        return true;
    }

    private void OnJobUpdate(BackupJobUpdate update)
    {
        var editor = Plans.FirstOrDefault(p => p.Id.Equals(update.PlanId, StringComparison.OrdinalIgnoreCase));
        editor?.Run.Apply(update);
        UpdateQueueStatus(update);

        if (update.State == JobState.Removed)
        {
            StatusMessage = $"The queued backup of \"{update.PlanName}\" was removed.";
            return;
        }

        if (update is not { State: JobState.Finished, Result: { } result })
            return;

        if (editor is not null)
        {
            LoadHistory(editor);
            editor.RetentionPreview.ReloadIfLoaded();
        }
        StatusMessage = result.Status switch
        {
            RunStatus.Completed => $"Backup of \"{update.PlanName}\" completed in {RunHistoryRow.FormatDuration(result.DurationMs)}.",
            RunStatus.CompletedWithWarnings =>
                $"Backup of \"{update.PlanName}\" completed with {result.SkippedCount:N0} skipped entries.",
            RunStatus.Canceled => $"Backup of \"{update.PlanName}\" was canceled.",
            _ => $"Backup of \"{update.PlanName}\" was aborted: {result.Reason}",
        };
        RunFinished?.Invoke(update.PlanName, result);
    }

    private void UpdateQueueStatus(BackupJobUpdate update)
    {
        var queued = _queue.QueuedCount;
        var waiting = queued > 0 ? $" · {queued} queued" : "";
        if (update.State == JobState.Running)
        {
            var percent = update.Progress is { } progress ? $" — {progress.Fraction * 100:0} %" : "";
            QueueStatus = $"Backing up \"{update.PlanName}\"{percent}{waiting}";
        }
        else if (!_queue.IsBusy)
        {
            QueueStatus = "No backup running";
        }
        else if (queued > 0)
        {
            QueueStatus = $"{queued} backup(s) queued";
        }
    }

    private void LoadHistory(PlanEditorViewModel editor)
    {
        try
        {
            editor.Run.LoadHistory(new RunLog(_paths.LogFileFor(editor.Id)).ReadAll());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"The run history of \"{editor.Name}\" could not be read: {ex.Message}";
        }
    }

    private void AddEditor(PlanEditorViewModel editor)
    {
        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlanEditorViewModel.Name))
                RevalidateAll();
        };
        LoadHistory(editor);
        Plans.Add(editor);
    }

    private void RevalidateAll()
    {
        foreach (var editor in Plans)
            editor.Validate();
    }

    private IEnumerable<BackupPlan> AllPlans() => Plans.Select(p => p.ToPlan());

    // Read on every use, so edits made in the settings dialog apply to the next evaluation.
    private IReadOnlyList<string> GlobalIgnoreDefaults() => _settings.DefaultIgnorePatterns;

    private string UniqueName(string baseName)
    {
        var names = Plans.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
            return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!names.Contains(candidate))
                return candidate;
        }
    }

    private static string? LoadErrorText(PlanLoadResult result) =>
        result.Errors.Count == 0
            ? null
            : $"{result.Errors.Count} plan file(s) could not be read: " +
              string.Join("; ", result.Errors.Select(e => $"{Path.GetFileName(e.FilePath)}: {e.Message}"));
}
