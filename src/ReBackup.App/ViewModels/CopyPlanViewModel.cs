using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Shared.Localization;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;

namespace ReBackup.App.ViewModels;

/// <summary>What "Copy plan…" needs from the plan list.</summary>
/// <param name="Validate">The errors a plan would have in the plan list (name, target, connections).</param>
/// <param name="AddPlan">
/// Saves a new copy (disabled, marked unfinished) through the plan store before the first write and shows it in the plan
/// list; false when it could not be saved.
/// </param>
/// <param name="FinishPlan">Removes the unfinished mark from the saved copy once it has finished; false when it could not be saved.</param>
/// <param name="Copying">Called with true when a copy starts and with false when it has ended.</param>
/// <param name="CanStart">Why no copy may start right now (a backup of the plan is queued or running); null when it may.</param>
public sealed record CopyPlanHooks(Func<BackupPlan, IReadOnlyList<Message>> Validate, Func<BackupPlan, bool> AddPlan,
    Func<BackupPlan, bool> FinishPlan, Action<bool> Copying, Func<string?> CanStart);

/// <summary>
/// "Copy plan…" (spec 10): a new name, a new target (picked as on the Plan tab) and a storage format; the new target is
/// checked (<see cref="PlanCopier.CheckAsync"/>) and the copy is saved as a new, disabled plan marked unfinished
/// (<see cref="BackupPlan.CopyFromPlanId"/>) before anything is written; then the versions are copied with progress, and
/// the mark is removed once the copy has finished. "Continue copy…" opens it on such a plan (<see cref="IsResume"/>):
/// name, target and format are fixed, versions complete on the target are skipped.
/// </summary>
public sealed partial class CopyPlanViewModel : ObservableObject, IPoolJob
{
    private readonly BackupPlan _source;
    private readonly PlanCopier _copier;
    private readonly IDialogService _dialogs;
    private readonly CopyPlanHooks _hooks;
    private CancellationTokenSource? _cts;
    private Task _work = Task.CompletedTask;

    /// <summary>The copy as saved (unfinished); every later start continues it.</summary>
    private BackupPlan? _copy;

    [ObservableProperty] private string _name;

    /// <summary>The copy is an incremental pool rather than full copies.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IncrementalIsFull))]
    private bool _incremental;

    /// <summary>The folder of the new target, while it is a folder.</summary>
    [ObservableProperty] private string _target = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetIsFolder))]
    private bool _targetIsS3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetIsFolder))]
    private bool _targetIsDrive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanEditSettings))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand), nameof(BrowseTargetCommand))]
    private bool _isRunning;

    /// <summary>The copy has finished: nothing more to start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanEditSettings))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(BrowseTargetCommand))]
    private bool _isDone;

    /// <summary>The copy is saved as a plan already: name, target and format are fixed, a start continues it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings))]
    [NotifyCanExecuteChangedFor(nameof(BrowseTargetCommand))]
    private bool _isResume;

    [ObservableProperty] private string? _progressText;

    [ObservableProperty] private double _progressFraction;

    /// <summary>Why the copy did not start: the plan's errors or the target's refusal. Null otherwise.</summary>
    [ObservableProperty] private string? _message;

    /// <summary>What the copy did, once it has ended.</summary>
    [ObservableProperty] private string? _summary;

    /// <summary>What the copier reported, once it has ended.</summary>
    [ObservableProperty] private PlanCopyResult? _result;

    /// <param name="source">The saved plan that is copied.</param>
    /// <param name="unfinished">The saved, unfinished copy to continue ("Continue copy…"); null for a new copy.</param>
    public CopyPlanViewModel(BackupPlan source, PlanCopier copier, S3Connections s3, DriveConnections drive,
        IDialogService dialogs, CopyPlanHooks hooks, BackupPlan? unfinished = null)
    {
        _source = source;
        _copier = copier;
        _dialogs = dialogs;
        _hooks = hooks;
        TargetS3 = new S3LocationViewModel(s3, dialogs, isTarget: true, () => { });
        TargetDrive = new DriveLocationViewModel(drive, dialogs, isTarget: true, () => { });
        if (unfinished is null)
        {
            _name = Loc.F("copy.defaultName", ("plan", source.Name));
            // Copying mostly switches the format: the other one is suggested.
            _incremental = !source.Incremental;
            return;
        }

        _copy = unfinished;
        _isResume = true;
        _name = unfinished.Name;
        _incremental = unfinished.Incremental;
        var target = unfinished.Target;
        if (target.Kind == PlanValidator.S3Kind)
        {
            _targetIsS3 = true;
            TargetS3.Load(target.ConnectionId, target.Path);
        }
        else if (target.Kind == PlanValidator.DriveKind)
        {
            _targetIsDrive = true;
            TargetDrive.Load(target.ConnectionId, target.Path);
        }
        else
        {
            _target = target.Path;
        }
    }

    /// <summary>"Copy "{plan}" to a new target", or "Continue copying "{plan}" into "{copy}"".</summary>
    public string Heading => _copy is { } copy && IsResume
        ? Loc.F("copy.headingResume", ("plan", _source.Name), ("copy", copy.Name))
        : Loc.F("copy.heading", ("plan", _source.Name));

    /// <summary>The S3 connection and prefix of the new target; used while <see cref="TargetIsS3"/>.</summary>
    public S3LocationViewModel TargetS3 { get; }

    /// <summary>The Drive connection and path of the new target; used while <see cref="TargetIsDrive"/>.</summary>
    public DriveLocationViewModel TargetDrive { get; }

    public bool TargetIsFolder => !TargetIsS3 && !TargetIsDrive;

    public bool IncrementalIsFull => !Incremental;

    /// <summary>A copy can be started while nothing runs and it has not finished.</summary>
    public bool CanEdit => !IsRunning && !IsDone;

    /// <summary>Name, target and format can be changed until the copy is saved as a plan.</summary>
    public bool CanEditSettings => CanEdit && !IsResume;

    /// <summary>What the copier could not copy, in the applied language.</summary>
    public ObservableCollection<string> Failures { get; } = [];

    /// <summary>The copy's plan once it is saved (unfinished or finished); null until then.</summary>
    public BackupPlan? CopyPlan => _copy;

    /// <summary>The copy's work off the UI thread (check, copy); completed while none runs. Its locks are released when it ends.</summary>
    public Task Work => _work;

    /// <summary>The Folder | S3 | Drive segments of the new target ("Folder", "S3" or "Drive").</summary>
    [RelayCommand]
    private void ChooseTargetKind(string? kind)
    {
        if (!CanEditSettings)
            return;
        TargetIsS3 = kind == "S3";
        TargetIsDrive = kind == "Drive";
    }

    /// <summary>The Full copies | Incremental segments ("Full" or "Incremental").</summary>
    [RelayCommand]
    private void ChooseStorageFormat(string? format)
    {
        if (CanEditSettings)
            Incremental = format == "Incremental";
    }

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private void BrowseTarget()
    {
        if (_dialogs.PickFolder(Loc.T("shell.actions.chooseTarget"), Target) is { } folder)
            Target = folder;
    }

    /// <summary>The new target as chosen.</summary>
    private StorageLocation TargetLocation() =>
        TargetIsS3 ? TargetS3.Location : TargetIsDrive ? TargetDrive.Location : StorageLocation.FileSystem(Target);

    /// <summary>
    /// Checks the copy as a plan and its target, saves it as an unfinished plan, then copies off the UI thread. The
    /// unfinished mark is removed once the copy has gone through all versions without being canceled or stopped.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task StartAsync()
    {
        Message = null;
        Summary = null;
        Result = null;
        Failures.Clear();
        var copy = _copy ?? PlanCopier.MakeCopy(_source, Name, TargetLocation(), Incremental);
        // The copy reads nothing from the source: a source that is not there right now does not matter.
        var errors = _hooks.Validate(copy).Where(error => error.Key != "core.plan.sourceMissing").Select(Loc.F).ToList();
        if (errors.Count > 0)
        {
            Message = string.Join("\n", errors);
            return;
        }
        // Asked again now: a run of the plan may have been queued while the dialog was open.
        if (_hooks.CanStart() is { } blocked)
        {
            Message = blocked;
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        IsRunning = true;
        _hooks.Copying(true);
        ProgressFraction = 0;
        ProgressText = Loc.T("copy.checking");
        try
        {
            var check = Task.Run(() => _copier.CheckAsync(_source, copy.Target, cts.Token), cts.Token);
            _work = check;
            if (await RefusalAsync(check, copy) is { } refused)
            {
                Message = refused;
                return;
            }
            if (!SaveUnfinished(copy))
            {
                Message = Loc.T("copy.notAdded");
                return;
            }
            var progress = Progress(cts);
            var run = Task.Run(() => _copier.CopyAsync(_source, copy, progress, cts.Token));
            _work = run;
            var result = Result = await run;
            foreach (var failure in result.Failures)
                Failures.Add(Loc.Known(failure));
            Summary = Finish(copy, result);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Summary = _copy is null ? Loc.T("copy.canceledBeforeStart") : Loc.T("copy.canceled");
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            IsRunning = false;
            ProgressText = null;
            ProgressFraction = 0;
            _hooks.Copying(false);
        }
    }

    /// <summary>Why the new target cannot take the copy; null when it can.</summary>
    private async Task<string?> RefusalAsync(Task<CopyRefusal> check, BackupPlan copy)
    {
        CopyRefusal refusal;
        try
        {
            refusal = await check;
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            return Loc.F("copy.checkFailed", ("error", Loc.Known(ex.Message)));
        }
        // The copy's own versions are on the target already when it is continued.
        if (refusal == CopyRefusal.TargetNotEmpty && ReferenceEquals(copy, _copy))
            refusal = CopyRefusal.None;
        return refusal switch
        {
            CopyRefusal.None => null,
            CopyRefusal.SameOrNestedTarget => Loc.T("copy.refusal.sameOrNested"),
            CopyRefusal.TargetNotEmpty => Loc.T("copy.refusal.targetNotEmpty"),
            _ => Loc.T("copy.refusal.sourceLocked"),
        };
    }

    /// <summary>Saves a new copy as an unfinished plan before its first write; a copy saved already stays as it is.</summary>
    private bool SaveUnfinished(BackupPlan copy)
    {
        if (_copy is not null)
            return true;
        copy.CopyFromPlanId = _source.Id;
        if (!_hooks.AddPlan(copy.Clone()))
        {
            copy.CopyFromPlanId = null;
            return false;
        }
        _copy = copy;
        IsResume = true;
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(CopyPlan));
        return true;
    }

    /// <summary>Removes the unfinished mark when the copy has finished; the summary line.</summary>
    private string Finish(BackupPlan copy, PlanCopyResult result)
    {
        if (result.Canceled)
            return Loc.T("copy.canceled");
        if (!result.Finished)
            return Loc.F("copy.notFinished", ("plan", copy.Name));
        var done = Loc.F("copy.done", ("copied", result.VersionsCopied), ("skipped", result.VersionsSkipped));
        if (!_hooks.FinishPlan(copy))
            return done + "\n" + Loc.F("copy.notFinished", ("plan", copy.Name));
        copy.CopyFromPlanId = null;
        IsDone = true;
        var added = Loc.F("copy.added", ("plan", copy.Name));
        return result.Failures.Count == 0
            ? done + "\n" + added
            : done + "\n" + added + "\n" + Loc.F("copy.failures", ("count", result.Failures.Count));
    }

    /// <summary>Stops the copy after the file being copied; started again, it continues.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        if (_cts is { IsCancellationRequested: false } cts)
        {
            cts.Cancel();
            ProgressText = Loc.T("copy.canceling");
        }
    }

    /// <summary>The dialog closes or the app exits: a copy still running stops (and releases its locks).</summary>
    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It ended meanwhile.
        }
    }

    /// <summary>The progress line; reports come on the UI thread.</summary>
    private Progress<PlanCopyProgress> Progress(CancellationTokenSource cts) => new(p =>
    {
        if (!IsRunning || cts.IsCancellationRequested || p.VersionCount == 0)
            return;
        var part = p.BytesTotal > 0 ? Math.Clamp((double)p.BytesDone / p.BytesTotal, 0, 1) : 0;
        ProgressFraction = (p.Version - 1 + part) / p.VersionCount;
        ProgressText = Loc.F("copy.progress", ("current", p.Version), ("count", p.VersionCount), ("version", p.VersionName),
            ("done", Formats.Bytes(p.BytesDone)), ("total", Formats.Bytes(p.BytesTotal)));
    });
}

/// <summary>A copy or verify the app must stop (and wait for) before it exits, so that no lock or half-written file is left.</summary>
public interface IPoolJob
{
    /// <summary>Asks the job to stop.</summary>
    void Stop();

    /// <summary>The job's work off the UI thread; it has released its locks once this has completed.</summary>
    Task Work { get; }
}
