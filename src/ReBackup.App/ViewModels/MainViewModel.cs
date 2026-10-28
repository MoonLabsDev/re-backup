using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.App.Services;
using ReBackup.Core.Backup;
using ReBackup.Core.Backup.Retry;
using ReBackup.Core.Config;
using ReBackup.Core.Ignore;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Settings;
using ReBackup.Core.Versions;
using ReBackup.Shared.Json;
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
    private readonly Func<string?> _settingsLoadError;
    private readonly IDialogService _dialogs;
    private readonly IFolderOpener _folders;
    private readonly Action _openSettings;
    private readonly BackupQueue _queue;
    private readonly Scheduler _scheduler;
    private readonly RetryPlanner _retries;
    private readonly Action<Action> _runOnUi;
    private readonly VersionsContext _versions;
    private readonly S3Connections _s3;
    private readonly DriveConnections _drive;
    private readonly IRecycleBin _recycleBin;

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
    /// <param name="settingsLoadError">Why settings.json could not be read; the plan order then is not saved over it.</param>
    /// <summary>The finished source scans of the plans last looked at; shared by the Ignore tabs and the control service.</summary>
    public SourceScanCache ScanCache { get; }

    public MainViewModel(PlanStore store, ConfigPaths paths, AppSettings settings, Action saveSettings,
        Func<string?> settingsLoadError, IDialogService dialogs,
        Action openSettings, BackupQueue queue, Scheduler scheduler, RetryPlanner retries, Action<Action> runOnUi,
        ThemeToggleViewModel theme,
        LanguageToggleViewModel language, IFolderOpener folders, VersionIndexWorker versionIndex,
        IStorageFactory storages, S3Connections? s3 = null, DriveConnections? drive = null, ITempOpener? tempOpener = null,
        IRecycleBin? recycleBin = null)
    {
        _folders = folders;
        _recycleBin = recycleBin ?? new ShellRecycleBin();
        _s3 = s3 ?? new S3Connections(paths.ConnectionsFile);
        _drive = drive ?? new DriveConnections(paths.DriveConnectionsFile);
        ScanCache = new SourceScanCache(storages, GlobalIgnoreDefaults);
        _versions = new VersionsContext(versionIndex, dialogs, text => SetStatus(text), storages,
            DescribeLocation: DescribeLocation, TempOpener: tempOpener, TrackJob: (job, running) => Track(job, running),
            ScanCache: ScanCache);
        // A connection added, edited or deleted (Settings, "New connection…"): the plans that use one validate again.
        _s3.Changed += (_, _) => RevalidateAll();
        _drive.Changed += (_, _) => RevalidateAll();
        Theme = theme;
        Language = language;
        Loc.LanguageChanged += (_, _) => OnLanguageChanged();
        _store = store;
        _paths = paths;
        _settings = settings;
        _saveSettings = saveSettings;
        _settingsLoadError = settingsLoadError;
        _dialogs = dialogs;
        _openSettings = openSettings;
        _queue = queue;
        _scheduler = scheduler;
        _retries = retries;
        _runOnUi = runOnUi;
        _queue.Changed += update => _runOnUi(() => OnJobUpdate(update));
        // Raised on the planner's timer thread too.
        _retries.Changed += planId => _runOnUi(() => ShowRetry(planId));

        Plans.CollectionChanged += (_, _) =>
        {
            MoveUpCommand.NotifyCanExecuteChanged();
            MoveDownCommand.NotifyCanExecuteChanged();
        };

        var result = _store.LoadAll();
        foreach (var plan in PlanOrder.Apply(result.Plans, _settings.PlanOrder))
            AddEditor(NewEditor(plan, isNew: false));
        RevalidateAll();
        SelectedPlan = Plans.FirstOrDefault();
        SetStatus(LoadErrorText(result) ?? LocText.Of("shell.status.configuration", ("path", _paths.Root)));
        PublishPlans();
        RefreshSchedule();
    }

    /// <summary>
    /// At app start: every saved plan finishes, in the background, the deletions this computer left unfinished in its
    /// target (<see cref="VersionsViewModel.ResumeDeletionAsync"/>); meanwhile its runs are refused or skipped. Completes
    /// when all have ended.
    /// </summary>
    public Task ResumeDeletionsAsync() =>
        Task.WhenAll(Plans.Where(p => !p.IsNew).Select(p => p.Versions.ResumeDeletionAsync(_exit.Token)).ToList());

    /// <summary>Canceled when the app stops its jobs to exit: resumed deletions stop looking and stop.</summary>
    private readonly CancellationTokenSource _exit = new();

    /// <summary>Scheduled runs that came while versions of their plan were deleted, by plan id: they start afterwards.</summary>
    private readonly Dictionary<string, RunTrigger> _deferredRuns = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Saved plans for the tray menu; <c>CanRun</c> is false for unsaved, invalid, already active or copied plans and for
    /// unfinished copies.
    /// </summary>
    public IReadOnlyList<(string Id, string Name, bool CanRun)> RunnablePlans =>
        Plans.Where(p => !p.IsNew)
            .Select(p => (p.Id, p.Name, CanRun: !p.IsDirty && p.Errors.Count == 0 && !p.Run.IsActive && !p.IsCopying &&
                                         !p.IsCopyUnfinished && !p.IsDeletingVersions))
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
                _retries.OnPlanSaved(plan);   // as saved on disk, also under unsaved edits
                if (editor.IsDirty)
                {
                    editor.MarkAsExisting();
                }
                else
                {
                    var targetBefore = editor.SavedPlan().Target;
                    editor.ReplaceSaved(plan);
                    if (!SameLocation(targetBefore, plan.Target))
                    {
                        LoadHistory(editor);
                        editor.Versions.OnSavedTargetChanged();
                        editor.HistoryCoverage.OnSavedTargetChanged();
                    }
                }
            }
            else if (unreadable.Contains(editor.Id))
            {
                // File exists but cannot be read right now: leave the editor untouched.
            }
            else if (!editor.IsNew)
            {
                _retries.OnPlanDeleted(editor.Id);
                if (editor.IsDirty)
                {
                    editor.MarkAsNew();
                    editor.Versions.Invalidate();   // no saved plan, so no versions
                    editor.HistoryCoverage.Invalidate();
                }
                else
                {
                    editor.Versions.Invalidate();   // stops its sync
                    editor.HistoryCoverage.Invalidate();
                    Plans.Remove(editor);
                }
            }
        }

        // Plans that appeared go where the saved order puts them, the others to the end (by name).
        foreach (var plan in PlanOrder.Apply(loaded.Values, _settings.PlanOrder))
        {
            var editor = NewEditor(plan, isNew: false);
            AddEditor(editor, PlanOrder.InsertionIndex(Plans, p => p.Id, plan.Id, _settings.PlanOrder));
        }

        RevalidateAll();

        if (SelectedPlan is null || !Plans.Contains(SelectedPlan))
            SelectedPlan = Plans.FirstOrDefault();

        SetStatus(LoadErrorText(result) ?? LocText.Of("shell.status.reloaded"));
        PublishPlans();
    }

    [RelayCommand]
    private void NewPlan()
    {
        var editor = NewEditor(new BackupPlan { Name = UniqueName(Loc.T("shell.plans.newName")) }, isNew: true);
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
        if (editor.IsCopying)
        {
            SetStatus(LocText.Of("shell.status.deleteWaitCopy", ("plan", editor.Name)));
            return;
        }
        if (editor.IsDeletingVersions)
        {
            SetStatus(LocText.Of("shell.status.deleteWaitDelete", ("plan", editor.Name)));
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

        if (!editor.IsNew)
        {
            _retries.OnPlanDeleted(editor.Id);
            // Its retry would write into the target of a plan that no longer exists (its regular run is not active).
            if (editor.Run.IsRetryActive || _queue.IsRetryRunning(editor.Id))
                _queue.Cancel(editor.Id);
        }

        // Modal dialogs pump the dispatcher, so a reload may already have removed the editor.
        var index = Plans.IndexOf(editor);
        ScanCache.Invalidate(editor.Id);
        editor.Versions.Invalidate();   // stops its sync
        editor.HistoryCoverage.Invalidate();
        if (index >= 0)
            Plans.RemoveAt(index);
        if (!editor.IsNew)
            _ = DeleteIndexAsync(editor.Id);
        RevalidateAll();
        SelectedPlan = Plans.Count == 0 ? null : Plans[Math.Clamp(index, 0, Plans.Count - 1)];
        PublishPlans();
        if (!editor.IsNew)
            SavePlanOrder();   // drops the deleted plan's id
    }

    /// <summary>
    /// Deletes a deleted plan's version index once the work queued on it (a sync that is being canceled) has ended. It
    /// is a cache only: a failure is logged, and the next start removes the file as an orphan.
    /// </summary>
    private async Task DeleteIndexAsync(string planId)
    {
        try
        {
            await _versions.Worker.WhenIdle(planId);
            await Task.Run(() => _versions.Indexes.Delete(planId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning($"The version index of the deleted plan {planId} could not be deleted: {ex.Message}");
        }
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

    /// <summary>
    /// Saves the order of the saved plans to settings.json; a failure is shown in the footer and the order is kept for
    /// this session. An unreadable settings.json is not replaced by a move: it would lose the other settings.
    /// </summary>
    private void SavePlanOrder()
    {
        _settings.PlanOrder = Plans.Where(p => !p.IsNew).Select(p => p.Id).ToList();
        if (_settingsLoadError() is { } loadError)
        {
            SetStatus(LocText.Of("shell.status.orderNotSaved", ("error", loadError)));
            return;
        }
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
            var targetBefore = wasNew ? null : editor.SavedPlan().Target;
            var saved = editor.TrySave(_store);
            SetStatus(saved ? LocText.Of("shell.status.saved", ("plan", editor.Name)) : LocText.Of("shell.status.notSaved"));
            RevalidateAll();
            if (saved)
            {
                _retries.OnPlanSaved(editor.SavedPlan());
                PublishPlans();
                if (wasNew)
                    SavePlanOrder();   // its place in the list, also when it was moved before the first save
                if (targetBefore is null || !SameLocation(targetBefore, editor.SavedPlan().Target))
                {
                    LoadHistory(editor);   // the folder buttons look in the new target
                    editor.Versions.OnSavedTargetChanged();
                    editor.HistoryCoverage.OnSavedTargetChanged();
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

    /// <summary>Queues a run of the saved plan. False when it is dirty, invalid, unknown, already queued or being copied.</summary>
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
            if (editor.IsCopying)
            {
                // "Copy plan…" reads its versions: no run may add or remove one meanwhile.
                SkipScheduledRun(editor, plan, trigger, CoreTexts.English("core.run.copyRunning"));
                return;
            }
            if (plan.CopyFromPlanId is not null)
            {
                SkipScheduledRun(editor, plan, trigger, CoreTexts.English("core.run.copyUnfinished"));
                return;
            }
            if (editor.IsDeletingVersions)
            {
                // Versions are deleted (or an unfinished deletion is finished): the run starts once that has ended.
                _deferredRuns[editor.Id] = trigger;
                SetStatus(LocText.Of("shell.status.runDeferred", ("plan", editor.Name)));
                return;
            }
            if (ScheduledRunBlocker(plan) is { } reason)
            {
                SkipScheduledRun(editor, plan, trigger, reason);
                return;
            }
            _queue.Enqueue(new BackupRequest(plan, _settings.DefaultIgnorePatterns.ToList(), trigger));
        }
        catch (Exception ex)
        {
            SetStatus(LocText.Of("shell.status.scheduledFailed", ("error", ex.Message)));
        }
    }

    /// <summary>
    /// Queues the due attempt of the plan's retry; called by the retry planner through the dispatcher, with a copy of the
    /// state it handed out. It uses the plan as saved. While the plan could not run on schedule (scheduler paused, plan
    /// copied, an unfinished copy, versions deleted, invalid with the connections as they are now) or the queue refuses the
    /// job (a job of the plan is queued or running, the app exits), the attempt is deferred: it costs no attempt and is not
    /// logged.
    /// </summary>
    public void RunRetry(RetryState due)
    {
        // Called from the planner through the dispatcher: an exception here would end the app.
        try
        {
            // A save, a deletion or a run of the plan may have dropped or replaced the state since then; that one is not
            // handed out (nothing to defer).
            if (_retries.StateOf(due.PlanId) is not { } state || state.RunId != due.RunId)
                return;
            var editor = Plans.FirstOrDefault(p => p.Id.Equals(state.PlanId, StringComparison.OrdinalIgnoreCase));
            if (editor is null || editor.IsNew)
            {
                _retries.OnPlanDeleted(state.PlanId);   // the plan is no longer saved
                return;
            }
            var plan = editor.SavedPlan();
            var blocked = _scheduler.IsPaused || editor.IsCopying || plan.CopyFromPlanId is not null ||
                          editor.IsDeletingVersions || ScheduledRunBlocker(plan) is not null;
            if (blocked || !_queue.EnqueueRetry(new RetryRequest(plan, state.RunId, state.Version, state.Files,
                    state.Attempt + 1, state.Attempts)))
                _retries.Defer(state.PlanId);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"The retry of plan {due.PlanId} could not be queued: {ex.Message}");
            try
            {
                _retries.Defer(due.PlanId);
            }
            catch (Exception)
            {
                // Then it stays handed out until the app starts again.
            }
        }
    }

    /// <summary>The plan's card shows its pending retry as the planner has it now.</summary>
    private void ShowRetry(string planId) =>
        Plans.FirstOrDefault(p => p.Id.Equals(planId, StringComparison.OrdinalIgnoreCase))?.Run.SetRetry(_retries.StateOf(planId));

    /// <summary>
    /// When the plan's newest regular run started; null when it never ran. The scheduler's catch-up asks it: a retry line
    /// is no run of the schedule and would hide a missed one.
    /// </summary>
    public DateTime? LastRegularRunStartUtc(string planId) =>
        new RunLog(_paths.LogFileFor(planId)).ReadLastRegular()?.StartUtc;

    /// <summary>
    /// Once, at app start: the retry planner loads the saved retries and drops those that no longer apply to the saved
    /// plans and their run logs.
    /// </summary>
    public void StartRetries() => _retries.Start(
        planId => Plans.FirstOrDefault(p => !p.IsNew && p.Id.Equals(planId, StringComparison.OrdinalIgnoreCase))?.SavedPlan(),
        planId => new RunLog(_paths.LogFileFor(planId)).ReadLastRegular());

    /// <summary>
    /// Why the saved plan must not run on schedule: its validation with the connections as they are now (a connection
    /// edited, deleted or unreadable since the plan was saved; an S3 or Drive target now inside its source; a Drive target
    /// whose connection no longer points at a shared drive). English, as the run log stores reasons. Null when it may run.
    /// Plans with folders on both sides are left to the runner as before (it checks folders itself and logs its own errors).
    /// </summary>
    private string? ScheduledRunBlocker(BackupPlan plan)
    {
        if (plan.Source.IsFileSystem && plan.Target.IsFileSystem)
            return null;
        var saved = Plans.Where(p => !p.IsNew).Select(p => p.SavedPlan()).ToList();
        var errors = PlanValidator.Validate(plan, saved, _s3.BucketOf, _drive.RootOf);
        return errors.Count == 0 ? null : string.Join(" ", errors.Select(error => CoreTexts.English(error)));
    }

    /// <summary>
    /// A scheduled run that is not started: an error entry in the plan's run log (the history and the scheduler's
    /// catch-up see it, as a run that ended as error with nothing written), and the reason in the footer.
    /// </summary>
    private void SkipScheduledRun(PlanEditorViewModel editor, BackupPlan plan, RunTrigger trigger, string reason)
    {
        var now = DateTime.UtcNow;
        new RunLog(_paths.LogFileFor(plan.Id)).Append(new RunLogEntry
        {
            RunId = Guid.NewGuid().ToString("N"),
            Trigger = trigger,
            StartUtc = now,
            EndUtc = now,
            Status = RunStatus.Error,
            Reason = reason,
        });
        LoadHistory(editor);
        SetStatus(LocText.Of("shell.status.scheduledInvalid", ("plan", plan.Name), ("reason", new LocText(() => Loc.Known(reason)))));
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
            // A run waiting behind its own plan's running retry starts next; it waits for no other plan.
            var ahead = position.Position == 1 && _queue.IsRetryRunning(editor.Id) ? null
                : position.AheadPlanId is { } aheadId
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
        if (editor.IsCopying)
        {
            SetStatus(LocText.Of("shell.status.copyRunning", ("plan", editor.Name)));
            return false;
        }
        if (editor.IsCopyUnfinished)
        {
            SetStatus(LocText.Of("shell.status.copyUnfinished", ("plan", editor.Name)));
            return false;
        }
        if (editor.IsDeletingVersions)
        {
            SetStatus(LocText.Of("shell.status.deleteRunning", ("plan", editor.Name)));
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

    /// <summary>
    /// Queues a run of the saved plan for the control service (trigger <see cref="RunTrigger.Mcp"/>), without dialogs: unsaved
    /// edits are neither saved nor touched. Null when it is queued, with the time just before it was; otherwise why not
    /// (English), with <paramref name="busy"/> true when a run, a copy or a deletion of versions of the plan is active.
    /// </summary>
    internal string? StartForControl(PlanEditorViewModel editor, out bool busy, out DateTime queuedUtc)
    {
        busy = true;
        queuedUtc = default;
        if (editor.Run.IsActive)
            return $"A backup of '{editor.SavedPlan().Name}' is already queued or running.";
        if (editor.IsCopying)
            return $"'{editor.SavedPlan().Name}' is being copied (Copy plan…); its runs wait until the copy has ended.";
        if (editor.IsDeletingVersions)
            return $"Versions of '{editor.SavedPlan().Name}' are being deleted; start the backup once that has ended.";

        busy = false;
        if (editor.IsNew)
            return "The plan is not saved yet.";
        var plan = editor.SavedPlan();
        if (plan.CopyFromPlanId is not null)
            return CoreTexts.English("core.run.copyUnfinished");
        if (ScheduledRunBlocker(plan) is { } reason)
            return reason;

        queuedUtc = _queue.Clock.GetUtcNow().UtcDateTime;   // the clock the runs are stamped with
        if (!_queue.Enqueue(new BackupRequest(plan, _settings.DefaultIgnorePatterns.ToList(), RunTrigger.Mcp)))
        {
            busy = true;
            return $"A backup of '{plan.Name}' is already queued or running.";
        }
        NotifyControl(LocText.Of("control.notification.backupStarted", ("plan", plan.Name)));
        return null;
    }

    private void OnJobUpdate(BackupJobUpdate update)
    {
        var editor = Plans.FirstOrDefault(p => p.Id.Equals(update.PlanId, StringComparison.OrdinalIgnoreCase));
        ReportToRetries(update, editor);   // first: every attempt handed out must come back to the planner
        editor?.Run.Apply(update);
        editor?.ReleaseLockCommand.NotifyCanExecuteChanged();   // not while its own run holds the lock
        if (update.Progress is null)
            RefreshQueuePositions();   // the queue changed: a job was added, started, removed or finished
        UpdateQueueStatus(update, editor?.Run);

        // A finished run made a version newer than any cached scan of its source: the next answer scans again.
        if (update.State == JobState.Finished)
            ScanCache.Invalidate(update.PlanId);

        if (update.State == JobState.Removed)
        {
            // A waiting retry is removed when a regular run of its plan is queued: nothing the user asked for went.
            if (!update.IsRetry)
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
            editor.HistoryCoverage.ReloadIfLoaded();
            _ = editor.RefreshLockAsync();
        }
        if (update.IsRetry)
        {
            ShowRetryFinished(update.PlanName, result);   // no notification per attempt
            return;
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
    /// Tells the retry planner about the plan's jobs: a run starts (a regular one drops the plan's retry), a run finished
    /// (with the plan as saved now: its interval sets when the next attempt is due), a waiting retry was removed from the
    /// queue (due again later; a regular run that replaced it drops it when it starts). The finished run of a plan that is
    /// no longer saved is left out: its retry was dropped with it.
    /// </summary>
    private void ReportToRetries(BackupJobUpdate update, PlanEditorViewModel? editor)
    {
        try
        {
            switch (update.State)
            {
                case JobState.Running when update.Progress is null:   // the first Running; the others carry progress
                    _retries.OnRunStarted(update.PlanId, update.IsRetry);
                    break;
                case JobState.Finished when update.Result is { } result && editor is { IsNew: false }:
                    _retries.OnRunFinished(RetryPlanOf(editor.SavedPlan(), update), result);
                    break;
                case JobState.Removed when update.IsRetry:
                    _retries.Defer(update.PlanId);
                    break;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"The retry planner could not take the update of plan {update.PlanId}: {ex.Message}");
        }
    }

    /// <summary>
    /// The plan to hand the planner with a finished job: the plan as saved (its retry settings and interval apply). A regular
    /// run whose source or target was saved differently while it ran made its version from the old locations: its skipped
    /// files must not be read from the new source into that version, so it gets no retry (the saved plan with retries off).
    /// </summary>
    private static BackupPlan RetryPlanOf(BackupPlan saved, BackupJobUpdate update)
    {
        if (update.IsRetry || update.Plan is not { } ran || SameLocations(ran, saved))
            return saved;
        var plan = saved.Clone();
        plan.RetrySkipped.Enabled = false;
        return plan;
    }

    /// <summary>Source and target compared as the retry planner compares them (their JSON).</summary>
    private static bool SameLocations(BackupPlan a, BackupPlan b) =>
        JsonSerializer.Serialize(a.Source, JsonDefaults.Options) == JsonSerializer.Serialize(b.Source, JsonDefaults.Options) &&
        JsonSerializer.Serialize(a.Target, JsonDefaults.Options) == JsonSerializer.Serialize(b.Target, JsonDefaults.Options);

    /// <summary>
    /// The footer after a retry attempt: what it copied and what is still open. A canceled one says so; a failed one, or
    /// one whose version is gone, leaves the footer as it is (the history shows a failed attempt).
    /// </summary>
    private void ShowRetryFinished(string planName, RunLogEntry result)
    {
        if (result.VersionGone)
            return;
        if (result.Status is RunStatus.Completed or RunStatus.CompletedWithWarnings)
            SetStatus(LocText.Of("shell.status.retryFinished", ("plan", planName), ("recovered", result.FilesCopied),
                ("open", result.SkippedCount)));
        else if (result.Status == RunStatus.Canceled)
            SetStatus(LocText.Of("shell.status.canceled", ("plan", planName)));
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
            // Only a folder target's versions can be opened in Explorer from the history.
            editor.Run.LoadHistory(new RunLog(_paths.LogFileFor(editor.Id)).ReadAll(),
                editor.IsNew || editor.SavedPlan().Target is not { IsFileSystem: true } target ? null : target.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(LocText.Of("shell.status.historyUnreadable", ("plan", editor.Name), ("error", ex.Message)));
        }
    }

    /// <summary>An editor on <paramref name="plan"/>; it saves through <see cref="SaveEditor"/>, as the Save button does.</summary>
    private PlanEditorViewModel NewEditor(BackupPlan plan, bool isNew) =>
        new(plan, isNew, AllPlans, GlobalIgnoreDefaults, _folders, _versions, _s3, _drive, _recycleBin, SaveEditor);

    private void AddEditor(PlanEditorViewModel editor, int? index = null)
    {
        editor.Versions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(VersionsViewModel.IsDeleting) or nameof(VersionsViewModel.IsResuming)))
                return;
            OnPropertyChanged(nameof(RunnablePlans));
            if (!editor.IsDeletingVersions && _deferredRuns.Remove(editor.Id, out var trigger))
                RunScheduled(editor.Id, trigger);
        };
        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlanEditorViewModel.Name))
                RevalidateAll();
        };
        LoadHistory(editor);
        ShowSchedule(editor, _scheduler.IsPaused);
        editor.Run.SetRetry(_retries.StateOf(editor.Id));
        Plans.Insert(index ?? Plans.Count, editor);
    }

    private void RevalidateAll()
    {
        foreach (var editor in Plans)
            editor.Validate();
    }

    private IEnumerable<BackupPlan> AllPlans() => Plans.Select(p => p.ToPlan());

    /// <summary>
    /// The names of the plans whose source or target is on the S3 connection <paramref name="connectionId"/>, as saved or
    /// as edited (a connection must not be deleted under either).
    /// </summary>
    public IReadOnlyList<string> PlansUsingConnection(string connectionId) =>
        Plans.Where(editor => (!editor.IsNew && UsesConnection(editor.SavedPlan(), connectionId)) ||
                              UsesConnection(editor.ToPlan(), connectionId))
            .Select(editor => string.IsNullOrWhiteSpace(editor.Name) ? Loc.T("common.unnamed") : editor.Name)
            .ToList();

    private static bool UsesConnection(BackupPlan plan, string connectionId) =>
        S3Connections.Uses(plan.Source, connectionId) || S3Connections.Uses(plan.Target, connectionId);

    /// <summary>
    /// The names of the plans whose source or target is on the Google Drive connection <paramref name="connectionId"/>, as
    /// saved or as edited (a connection must not be deleted under either).
    /// </summary>
    public IReadOnlyList<string> PlansUsingDriveConnection(string connectionId) =>
        Plans.Where(editor => (!editor.IsNew && UsesDriveConnection(editor.SavedPlan(), connectionId)) ||
                              UsesDriveConnection(editor.ToPlan(), connectionId))
            .Select(editor => string.IsNullOrWhiteSpace(editor.Name) ? Loc.T("common.unnamed") : editor.Name)
            .ToList();

    private static bool UsesDriveConnection(BackupPlan plan, string connectionId) =>
        DriveConnections.Uses(plan.Source, connectionId) || DriveConnections.Uses(plan.Target, connectionId);

    /// <summary>A location as the user reads it: a Drive location by its connection, root and path, any other as S3 describes it.</summary>
    private string DescribeLocation(StorageLocation location) =>
        location.Kind == PlanValidator.DriveKind ? _drive.Describe(location) : _s3.Describe(location);

    /// <summary>The same location: kind and connection, a folder's path case-insensitively, a storage path exactly.</summary>
    private static bool SameLocation(StorageLocation a, StorageLocation b) =>
        string.Equals(a.Kind, b.Kind, StringComparison.Ordinal) &&
        string.Equals(a.ConnectionId, b.ConnectionId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Path, b.Path, a.IsFileSystem ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
