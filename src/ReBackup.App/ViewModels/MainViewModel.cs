using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.App.Services;
using ReBackup.Core.Backup;
using ReBackup.Core.Config;
using ReBackup.Core.Plans;
using ReBackup.Core.Settings;
using ReBackup.Core.Versions;
using ReBackup.Shared.Schedule;
using ReBackup.Shared.Wpf.Controls;
using ReBackup.Storage;

namespace ReBackup.App.ViewModels;

/// <summary>The tabs of a plan; the header tabs and the icon rail show and change the same one.</summary>
public enum MainTab
{
    Plan,
    Ignore,
    Retention,
    History,
    Versions,
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly PlanStore _store;
    private readonly ConfigPaths _paths;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly IDialogService _dialogs;
    private readonly IFolderOpener _folders;
    private readonly Action _openSettings;
    private readonly BackupQueue _queue;
    private readonly Scheduler _scheduler;
    private readonly Action<Action> _runOnUi;
    private readonly VersionsContext _versions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPlan))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand))]
    private PlanEditorViewModel? _selectedPlan;

    /// <summary>The tab shown for the selected plan; it stays when another plan is selected.</summary>
    [ObservableProperty] private MainTab _selectedTab = MainTab.Plan;

    private LocText? _status;
    private LocText _queueText = LocText.Of("shell.queue.idle");
    private bool _schedulerPaused;

    /// <param name="saveSettings">Saves <paramref name="settings"/> (the plan order) to settings.json.</param>
    public MainViewModel(PlanStore store, ConfigPaths paths, AppSettings settings, Action saveSettings, IDialogService dialogs,
        Action openSettings, BackupQueue queue, Scheduler scheduler, Action<Action> runOnUi, ThemeToggleViewModel theme,
        LanguageToggleViewModel language, IFolderOpener folders, VersionIndexWorker versionIndex,
        IStorageFactory storages)
    {
        _folders = folders;
        _versions = new VersionsContext(versionIndex, dialogs, text => SetStatus(text), storages);
        Theme = theme;
        Language = language;
        Loc.LanguageChanged += (_, _) => OnLanguageChanged();
        _store = store;
        _paths = paths;
        _settings = settings;
        _saveSettings = saveSettings;
        _dialogs = dialogs;
        _openSettings = openSettings;
        _queue = queue;
        _scheduler = scheduler;
        _runOnUi = runOnUi;
        _queue.Changed += update => _runOnUi(() => OnJobUpdate(update));

        Plans.CollectionChanged += (_, _) =>
        {
            MoveUpCommand.NotifyCanExecuteChanged();
            MoveDownCommand.NotifyCanExecuteChanged();
        };

        var result = _store.LoadAll();
        foreach (var plan in PlanOrder.Apply(result.Plans, _settings.PlanOrder))
            AddEditor(new PlanEditorViewModel(plan, isNew: false, AllPlans, GlobalIgnoreDefaults, _folders, _versions));
        RevalidateAll();
        SelectedPlan = Plans.FirstOrDefault();
        SetStatus(LoadErrorText(result) ?? LocText.Of("shell.status.configuration", ("path", _paths.Root)));
        PublishPlans();
        RefreshSchedule();
    }

    /// <summary>Plan name and result of a finished run; raised on the UI thread.</summary>
    public event Action<string, RunLogEntry>? RunFinished;

    public ObservableCollection<PlanEditorViewModel> Plans { get; } = [];

    /// <summary>The tab buttons of the rail work only while a plan is shown.</summary>
    public bool HasSelectedPlan => SelectedPlan is not null;

    /// <summary>The theme button of the icon rail.</summary>
    public ThemeToggleViewModel Theme { get; }

    /// <summary>The language button of the icon rail.</summary>
    public LanguageToggleViewModel Language { get; }

    /// <summary>The footer's status line, in the applied language.</summary>
    public string? StatusMessage => _status?.ToString();

    /// <summary>The queue line of the footer and the tray.</summary>
    public string QueueStatus => _queueText.ToString();

    /// <summary>"Scheduler paused" while it is; empty otherwise.</summary>
    public string SchedulerStatus => _schedulerPaused ? Loc.T("shell.scheduler.paused") : "";

    /// <summary>Shows <paramref name="text"/> in the footer; it follows language switches.</summary>
    public void SetStatus(LocText? text)
    {
        _status = text;
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void SetQueueStatus(LocText text)
    {
        _queueText = text;
        OnPropertyChanged(nameof(QueueStatus));
    }

    /// <summary>
    /// The language changed: everything built in code is built again. Bound labels follow by themselves; this is the
    /// one subscription, and it reaches every plan editor.
    /// </summary>
    private void OnLanguageChanged()
    {
        Language.Refresh();
        Theme.Refresh();
        foreach (var editor in Plans)
            editor.RefreshTexts();
        RefreshSchedule();   // the cards' "next …" texts and the scheduler line
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(QueueStatus));
    }

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

    /// <summary>A restore from a Versions tab is running (planning or copying).</summary>
    public bool IsAnyRestoring => Plans.Any(p => p.Versions.IsRestoring);

    /// <summary>
    /// Cancels every running restore and waits up to <paramref name="wait"/> for their copies to stop (each finishes
    /// its current file or removes its temp file). Blocks the calling thread; the copies do not need it. False when one
    /// is still running after the wait.
    /// </summary>
    public bool StopRestores(TimeSpan wait)
    {
        var runs = Plans.Select(p => p.Versions.StopRestore()).ToArray();
        try
        {
            return Task.WaitAll(runs, wait);
        }
        catch (AggregateException)
        {
            return true;   // a copy that failed has stopped too
        }
    }

    public IEnumerable<string> UnsavedPlanNames =>
        Plans.Where(p => p.IsDirty).Select(p => string.IsNullOrWhiteSpace(p.Name) ? Loc.T("common.unnamed") : p.Name);

    public void ReloadFromDisk()
    {
        PlanLoadResult result;
        try
        {
            result = _store.LoadAll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(LocText.Of("shell.status.reloadFailed", ("error", ex.Message)));
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
                {
                    editor.MarkAsExisting();
                }
                else
                {
                    var targetBefore = editor.SavedPlan().Target.Path;
                    editor.ReplaceSaved(plan);
                    if (!string.Equals(targetBefore, plan.Target.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        LoadHistory(editor);
                        editor.Versions.OnSavedTargetChanged();
                    }
                }
            }
            else if (unreadable.Contains(editor.Id))
            {
                // File exists but cannot be read right now: leave the editor untouched.
            }
            else if (!editor.IsNew)
            {
                if (editor.IsDirty)
                {
                    editor.MarkAsNew();
                    editor.Versions.Invalidate();   // no saved plan, so no versions
                }
                else
                {
                    editor.Versions.Invalidate();   // stops its sync
                    Plans.Remove(editor);
                }
            }
        }

        // Plans that appeared go to the end, in the saved order where it names them.
        foreach (var plan in PlanOrder.Apply(loaded.Values, _settings.PlanOrder))
            AddEditor(new PlanEditorViewModel(plan, isNew: false, AllPlans, GlobalIgnoreDefaults, _folders, _versions));

        RevalidateAll();

        if (SelectedPlan is null || !Plans.Contains(SelectedPlan))
            SelectedPlan = Plans.FirstOrDefault();

        SetStatus(LoadErrorText(result) ?? LocText.Of("shell.status.reloaded"));
        PublishPlans();
    }

    [RelayCommand]
    private void NewPlan()
    {
        var editor = new PlanEditorViewModel(new BackupPlan { Name = UniqueName(Loc.T("shell.plans.newName")) }, isNew: true, AllPlans, GlobalIgnoreDefaults,
            _folders, _versions);
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
            SetStatus(LocText.Of("shell.status.deleteCancelBackup", ("plan", editor.Name)));
            return;
        }
        if (editor.Versions.IsRestoring)
        {
            SetStatus(LocText.Of("shell.status.deleteWaitRestore", ("plan", editor.Name)));
            return;
        }

        if (!editor.IsNew)
        {
            if (!_dialogs.Confirm(Loc.T("shell.plans.delete"), Loc.F("shell.plans.deleteConfirm", ("plan", editor.Name))))
                return;
            var deleteLog = _dialogs.AskYesNoCancel(Loc.T("shell.plans.delete"), Loc.T("shell.plans.deleteHistory"));
            if (deleteLog is null)
                return;

            try
            {
                _store.Delete(editor.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dialogs.ShowError(Loc.T("shell.plans.delete"), Loc.Known(ex.Message));
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
                    _dialogs.ShowError(Loc.T("shell.plans.delete"), Loc.F("shell.plans.historyNotDeleted", ("error", ex.Message)));
                }
            }
        }

        // Modal dialogs pump the dispatcher, so a reload may already have removed the editor.
        var index = Plans.IndexOf(editor);
        editor.Versions.Invalidate();   // stops its sync
        if (index >= 0)
            Plans.RemoveAt(index);
        RevalidateAll();
        SelectedPlan = Plans.Count == 0 ? null : Plans[Math.Clamp(index, 0, Plans.Count - 1)];
        PublishPlans();
        if (!editor.IsNew)
            SavePlanOrder();   // drops the deleted plan's id
    }

    /// <summary>Moves the plan (the selected one when null) one place up; disabled for the first plan.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp(PlanEditorViewModel? editor)
    {
        var index = Plans.IndexOf(editor ?? SelectedPlan!);
        if (index > 0)
            MoveTo(index, index - 1);
    }

    private bool CanMoveUp(PlanEditorViewModel? editor) => Plans.IndexOf((editor ?? SelectedPlan)!) > 0;

    /// <summary>Moves the plan (the selected one when null) one place down; disabled for the last plan.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown(PlanEditorViewModel? editor)
    {
        var index = Plans.IndexOf(editor ?? SelectedPlan!);
        if (index >= 0 && index < Plans.Count - 1)
            MoveTo(index, index + 1);
    }

    private bool CanMoveDown(PlanEditorViewModel? editor) =>
        (editor ?? SelectedPlan) is { } plan && Plans.IndexOf(plan) is var index && index >= 0 && index < Plans.Count - 1;

    /// <summary>A drop in the plan list (<see cref="ListReorder"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanMovePlan))]
    private void MovePlan(ListMove move) => MoveTo(move.From, move.To);

    private bool CanMovePlan(ListMove move) => IsMove(move.From, move.To);

    private bool IsMove(int from, int to) => from != to && from >= 0 && to >= 0 && from < Plans.Count && to < Plans.Count;

    /// <summary>
    /// Moves the plan at <paramref name="from"/> to <paramref name="to"/> and saves the order; the selection and the
    /// open tab stay. False (nothing changes) when that is no move.
    /// </summary>
    public bool MoveTo(int from, int to)
    {
        if (!IsMove(from, to))
            return false;
        var selected = SelectedPlan;
        Plans.Move(from, to);
        // A list may drop its selection while its item moves; the plan stays selected.
        if (!ReferenceEquals(SelectedPlan, selected))
            SelectedPlan = selected;
        SavePlanOrder();
        return true;
    }

    /// <summary>Saves the plan list's order to settings.json; a failure is shown in the footer and the order is kept for this session.</summary>
    private void SavePlanOrder()
    {
        _settings.PlanOrder = Plans.Select(p => p.Id).ToList();
        try
        {
            _saveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SetStatus(LocText.Of("shell.status.orderNotSaved", ("error", ex.Message)));
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (SelectedPlan is { IsDirty: true } editor)
            SaveEditor(editor);
    }

    /// <summary>Saves the plan's edits; true when they are saved. Reports in the footer, or with a dialog when the file fails.</summary>
    private bool SaveEditor(PlanEditorViewModel editor)
    {
        try
        {
            var wasNew = editor.IsNew;
            var targetBefore = wasNew ? null : editor.SavedPlan().Target.Path;
            var saved = editor.TrySave(_store);
            SetStatus(saved ? LocText.Of("shell.status.saved", ("plan", editor.Name)) : LocText.Of("shell.status.notSaved"));
            RevalidateAll();
            if (saved)
            {
                PublishPlans();
                if (wasNew)
                    SavePlanOrder();   // its place in the list, also when it was moved before the first save
                if (!string.Equals(targetBefore, editor.SavedPlan().Target.Path, StringComparison.OrdinalIgnoreCase))
                {
                    LoadHistory(editor);   // the folder buttons look in the new target
                    editor.Versions.OnSavedTargetChanged();
                }
            }
            return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(Loc.T("shell.actions.saveFailed"), ex.Message);
            return false;
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
        if (SelectedPlan is { } editor && _dialogs.PickFolder(Loc.T("shell.actions.chooseSource"), editor.Source) is { } folder)
            editor.Source = folder;
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        if (SelectedPlan is { } editor && _dialogs.PickFolder(Loc.T("shell.actions.chooseTarget"), editor.Target) is { } folder)
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
            SetStatus(LocText.Of("shell.status.scheduledFailed", ("error", ex.Message)));
        }
    }

    /// <summary>
    /// Updates the "next run" texts, the next runs shown on the Plan tab and the scheduler state. Called whenever
    /// the scheduler reports a change, so at least once a minute.
    /// </summary>
    public void RefreshSchedule()
    {
        // Called from the scheduler through the dispatcher: an exception here would end the app.
        try
        {
            var paused = _scheduler.IsPaused;
            _schedulerPaused = paused;
            OnPropertyChanged(nameof(SchedulerStatus));
            foreach (var editor in Plans)
            {
                editor.SchedulerPaused = paused;
                ShowSchedule(editor, paused);
            }
            SelectedPlan?.RefreshNextRuns();
        }
        catch (Exception ex)
        {
            SetStatus(LocText.Of("shell.status.scheduleFailed", ("error", ex.Message)));
        }
    }

    /// <summary>The saved plan's schedule on its card: disabled, paused, or the next run.</summary>
    private void ShowSchedule(PlanEditorViewModel editor, bool paused)
    {
        if (editor.IsNew)
        {
            editor.Run.SetSchedule(enabled: true, schedulerPaused: false, LocText.Of("card.schedule.notSaved"));
            return;
        }
        var plan = editor.SavedPlan();
        if (!plan.Enabled)
        {
            editor.Run.SetSchedule(enabled: false, schedulerPaused: false, LocText.Empty);
        }
        else if (plan.Triggers.Count == 0)
        {
            editor.Run.SetSchedule(enabled: true, schedulerPaused: false, LocText.Of("card.schedule.none"));
        }
        else if (plan.Triggers.Any(trigger => ScheduleTriggers.Validate(trigger) is not null))
        {
            editor.Run.SetSchedule(enabled: true, schedulerPaused: false, LocText.Of("card.schedule.errors"));
        }
        else if (paused)
        {
            editor.Run.SetSchedule(enabled: true, schedulerPaused: true, LocText.Empty);
        }
        else if (_scheduler.NextRunUtc(editor.Id) is { } next)
        {
            var local = next.ToLocalTime();
            editor.Run.SetSchedule(enabled: true, schedulerPaused: false,
                new LocText(() => Loc.F("card.schedule.next", ("when", PlanRunViewModel.ShortWhen(local, DateTime.Now)))));
        }
        else
        {
            editor.Run.SetSchedule(enabled: true, schedulerPaused: false, LocText.Of("card.schedule.none"));
        }
    }

    /// <summary>The queue position and the plan ahead on the card of every queued plan.</summary>
    private void RefreshQueuePositions()
    {
        foreach (var editor in Plans)
        {
            if (editor.Run.State != JobState.Queued || _queue.PositionOf(editor.Id) is not { } position)
                continue;   // not queued, or already started: its Running update follows
            var ahead = position.AheadPlanId is { } aheadId
                ? Plans.FirstOrDefault(p => p.Id.Equals(aheadId, StringComparison.OrdinalIgnoreCase)) is { Name.Length: > 0 } aheadEditor
                    ? aheadEditor.Name
                    : position.AheadPlanName
                : null;
            editor.Run.SetQueuePosition(position.Position, ahead);
        }
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
        if (editor is null)
            return;

        // A run uses the plan as saved: unsaved edits are saved first, after asking, rather than refused in the footer.
        if (editor.Errors.Count > 0)
        {
            _dialogs.ShowError(Loc.T("shell.runUnsaved.title"),
                Loc.F("shell.runUnsaved.errors", ("plan", editor.Name),
                    ("errors", string.Join("\n", editor.Errors.Select(e => "  " + e)))));
            return;
        }
        if (editor.IsDirty)
        {
            if (!_dialogs.Confirm(Loc.T("shell.runUnsaved.title"), Loc.F("shell.runUnsaved.saveAndRun", ("plan", editor.Name))) ||
                !SaveEditor(editor))
                return;
        }
        Start(editor);
    }

    [RelayCommand]
    private void CancelRun(PlanEditorViewModel? editor)
    {
        editor ??= SelectedPlan;
        if (editor is not null && _queue.Cancel(editor.Id))
        {
            editor.Run.MarkCanceling();
            SetStatus(LocText.Of("shell.status.canceling", ("plan", editor.Name)));
        }
    }

    private bool Start(PlanEditorViewModel editor)
    {
        if (editor.IsNew || editor.IsDirty || editor.Errors.Count > 0)
        {
            SetStatus(LocText.Of("shell.status.saveFirst", ("plan", editor.Name)));
            return false;
        }

        var request = new BackupRequest(editor.SavedPlan(), _settings.DefaultIgnorePatterns.ToList(), RunTrigger.Manual);
        if (!_queue.Enqueue(request))
        {
            SetStatus(LocText.Of("shell.status.alreadyQueued", ("plan", editor.Name)));
            return false;
        }
        return true;
    }

    private void OnJobUpdate(BackupJobUpdate update)
    {
        var editor = Plans.FirstOrDefault(p => p.Id.Equals(update.PlanId, StringComparison.OrdinalIgnoreCase));
        editor?.Run.Apply(update);
        if (update.Progress is null)
            RefreshQueuePositions();   // the queue changed: a job was added, started, removed or finished
        UpdateQueueStatus(update, editor?.Run);

        if (update.State == JobState.Removed)
        {
            SetStatus(LocText.Of("shell.status.removed", ("plan", update.PlanName)));
            return;
        }

        if (update is not { State: JobState.Finished, Result: { } result })
            return;

        if (editor is not null)
        {
            LoadHistory(editor);
            editor.RetentionPreview.ReloadIfLoaded();
            editor.Versions.ReloadIfLoaded();
        }
        var planName = update.PlanName;
        var reason = result.Reason;
        SetStatus(result.Status switch
        {
            RunStatus.Completed => new LocText(() => Loc.F("shell.status.completed", ("plan", planName),
                ("duration", RunHistoryRow.FormatDuration(result.DurationMs)))),
            RunStatus.CompletedWithWarnings =>
                LocText.Of("shell.status.completedSkipped", ("plan", planName), ("count", result.SkippedCount)),
            RunStatus.Canceled => LocText.Of("shell.status.canceled", ("plan", planName)),
            _ => new LocText(() => Loc.F("shell.status.aborted", ("plan", planName), ("reason", Loc.Known(reason)))),
        });
        RunFinished?.Invoke(update.PlanName, result);
    }

    /// <summary>
    /// A finished run's version could not be added to the plan's version index; the next sync of the Versions tab
    /// imports it from its folder.
    /// </summary>
    public void ReportIndexError(string planId, string message)
    {
        var name = Plans.FirstOrDefault(p => p.Id.Equals(planId, StringComparison.OrdinalIgnoreCase))?.Name ?? planId;
        SetStatus(new LocText(() => Loc.F("shell.status.indexError", ("plan", name), ("error", Loc.Known(message)))));
    }

    /// <summary>The queue line of the footer and the tray; <paramref name="run"/> is the updated plan's card (its remaining time).</summary>
    private void UpdateQueueStatus(BackupJobUpdate update, PlanRunViewModel? run)
    {
        var queued = _queue.QueuedCount;
        if (update.State == JobState.Running)
        {
            var planName = update.PlanName;
            var fraction = update.Progress?.Fraction;
            var remaining = run?.RemainingTime;
            SetQueueStatus(new LocText(() =>
                Loc.F("shell.queue.running", ("plan", planName)) +
                (fraction is { } done ? Loc.F("shell.queue.percent", ("percent", done * 100)) : "") +
                (remaining is { } left ? " · " + PlanRunViewModel.FormatRemaining(left) : "") +
                (queued > 0 ? Loc.F("shell.queue.waiting", ("count", queued)) : "")));
        }
        else if (!_queue.IsBusy)
        {
            SetQueueStatus(LocText.Of("shell.queue.idle"));
        }
        else if (queued > 0)
        {
            SetQueueStatus(LocText.Of("shell.queue.queued", ("count", queued)));
        }
    }

    private void LoadHistory(PlanEditorViewModel editor)
    {
        try
        {
            editor.Run.LoadHistory(new RunLog(_paths.LogFileFor(editor.Id)).ReadAll(),
                editor.IsNew ? null : editor.SavedPlan().Target.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(LocText.Of("shell.status.historyUnreadable", ("plan", editor.Name), ("error", ex.Message)));
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
        ShowSchedule(editor, _scheduler.IsPaused);
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

    private static LocText? LoadErrorText(PlanLoadResult result)
    {
        if (result.Errors.Count == 0)
            return null;
        var errors = result.Errors.Select(e => (File: Path.GetFileName(e.FilePath), Error: e.Message)).ToList();
        return new LocText(() => Loc.F("shell.status.loadErrors", ("count", errors.Count),
            ("details", string.Join("; ", errors.Select(e => $"{e.File}: {Loc.Known(e.Error)}")))));
    }
}
