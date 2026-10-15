using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Shared.Localization;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;

namespace ReBackup.App.ViewModels;

/// <summary>
/// "Copy plan…" (spec 10): a new name, a new target (picked as on the Plan tab) and a storage format; the new target is
/// checked (<see cref="PlanCopier.CheckAsync"/>), the versions are copied with progress, and a copy that ran to its end
/// is added as a new, disabled plan. Started again in the same dialog with the same name, target and format, a copy that
/// stopped continues where it stopped.
/// </summary>
public sealed partial class CopyPlanViewModel : ObservableObject
{
    private readonly BackupPlan _source;
    private readonly PlanCopier _copier;
    private readonly Func<BackupPlan, IReadOnlyList<Message>> _validate;
    private readonly IDialogService _dialogs;
    private readonly Func<BackupPlan, bool> _addPlan;
    private readonly Action<bool> _copying;
    private CancellationTokenSource? _cts;

    /// <summary>The copy an earlier start in this dialog began writing; a start on the same target continues it.</summary>
    private BackupPlan? _attempt;

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
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand), nameof(BrowseTargetCommand))]
    private bool _isRunning;

    /// <summary>The copy ran and its plan was added: nothing more to start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(BrowseTargetCommand))]
    private bool _isDone;

    [ObservableProperty] private string? _progressText;

    [ObservableProperty] private double _progressFraction;

    /// <summary>Why the copy did not start: the plan's errors or the target's refusal. Null otherwise.</summary>
    [ObservableProperty] private string? _message;

    /// <summary>What the copy did, once it has ended.</summary>
    [ObservableProperty] private string? _summary;

    /// <summary>What the copier reported, once it has ended.</summary>
    [ObservableProperty] private PlanCopyResult? _result;

    /// <param name="source">The saved plan that is copied.</param>
    /// <param name="validate">The errors a plan would have in the plan list (name, target, connections).</param>
    /// <param name="addPlan">Saves the copy as a new plan and shows it in the list; false when it could not be saved.</param>
    /// <param name="copying">Called with true when a copy starts and with false when it has ended.</param>
    public CopyPlanViewModel(BackupPlan source, PlanCopier copier, Func<BackupPlan, IReadOnlyList<Message>> validate,
        S3Connections s3, DriveConnections drive, IDialogService dialogs, Func<BackupPlan, bool> addPlan, Action<bool> copying)
    {
        _source = source;
        _copier = copier;
        _validate = validate;
        _dialogs = dialogs;
        _addPlan = addPlan;
        _copying = copying;
        _name = Loc.F("copy.defaultName", ("plan", source.Name));
        // Copying mostly switches the format: the other one is suggested.
        _incremental = !source.Incremental;
        TargetS3 = new S3LocationViewModel(s3, dialogs, isTarget: true, () => { });
        TargetDrive = new DriveLocationViewModel(drive, dialogs, isTarget: true, () => { });
    }

    /// <summary>"Copy "{plan}" to a new target".</summary>
    public string Heading => Loc.F("copy.heading", ("plan", _source.Name));

    /// <summary>The S3 connection and prefix of the new target; used while <see cref="TargetIsS3"/>.</summary>
    public S3LocationViewModel TargetS3 { get; }

    /// <summary>The Drive connection and path of the new target; used while <see cref="TargetIsDrive"/>.</summary>
    public DriveLocationViewModel TargetDrive { get; }

    public bool TargetIsFolder => !TargetIsS3 && !TargetIsDrive;

    public bool IncrementalIsFull => !Incremental;

    /// <summary>Name, target and format can be changed while nothing runs and nothing was added.</summary>
    public bool CanEdit => !IsRunning && !IsDone;

    /// <summary>What the copier could not copy, in the applied language.</summary>
    public ObservableCollection<string> Failures { get; } = [];

    /// <summary>The plan the copy added; null until then.</summary>
    public BackupPlan? CreatedPlan { get; private set; }

    /// <summary>The Folder | S3 | Drive segments of the new target ("Folder", "S3" or "Drive").</summary>
    [RelayCommand]
    private void ChooseTargetKind(string? kind)
    {
        if (!CanEdit)
            return;
        TargetIsS3 = kind == "S3";
        TargetIsDrive = kind == "Drive";
    }

    /// <summary>The Full copies | Incremental segments ("Full" or "Incremental").</summary>
    [RelayCommand]
    private void ChooseStorageFormat(string? format)
    {
        if (CanEdit)
            Incremental = format == "Incremental";
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void BrowseTarget()
    {
        if (_dialogs.PickFolder(Loc.T("shell.actions.chooseTarget"), Target) is { } folder)
            Target = folder;
    }

    /// <summary>The new target as chosen.</summary>
    private StorageLocation TargetLocation() =>
        TargetIsS3 ? TargetS3.Location : TargetIsDrive ? TargetDrive.Location : StorageLocation.FileSystem(Target);

    /// <summary>
    /// Checks the copy as a plan and its target, then copies off the UI thread. A copy that was not canceled and copied
    /// something (or had nothing to copy and nothing failed) is added as a disabled plan.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task StartAsync()
    {
        Message = null;
        Summary = null;
        Result = null;
        Failures.Clear();
        var target = TargetLocation();
        var copy = _attempt is { } earlier && earlier.Name == Name && earlier.Incremental == Incremental && earlier.Target == target
            ? earlier
            : PlanCopier.MakeCopy(_source, Name, target, Incremental);
        // The copy reads nothing from the source: a source that is not there right now does not matter.
        var errors = _validate(copy).Where(error => error.Key != "core.plan.sourceMissing").Select(Loc.F).ToList();
        if (errors.Count > 0)
        {
            Message = string.Join("\n", errors);
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        IsRunning = true;
        _copying(true);
        ProgressFraction = 0;
        ProgressText = Loc.T("copy.checking");
        try
        {
            if (await CheckAsync(copy, cts.Token) is { } refused)
            {
                Message = refused;
                return;
            }
            _attempt = copy;
            var progress = Progress(cts);
            var result = Result = await Task.Run(() => _copier.CopyAsync(_source, copy, progress, cts.Token));
            foreach (var failure in result.Failures)
                Failures.Add(Loc.Known(failure));
            Summary = Finish(copy, result);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Summary = Loc.T("copy.canceled");
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            IsRunning = false;
            ProgressText = null;
            ProgressFraction = 0;
            _copying(false);
        }
    }

    /// <summary>Why the new target cannot take the copy; null when it can.</summary>
    private async Task<string?> CheckAsync(BackupPlan copy, CancellationToken ct)
    {
        CopyRefusal refusal;
        try
        {
            refusal = await Task.Run(() => _copier.CheckAsync(_source, copy.Target, ct), ct);
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            return Loc.F("copy.checkFailed", ("error", Loc.Known(ex.Message)));
        }
        // What an earlier start in this dialog wrote is no reason to refuse continuing it.
        if (refusal == CopyRefusal.TargetNotEmpty && ReferenceEquals(copy, _attempt))
            refusal = CopyRefusal.None;
        return refusal switch
        {
            CopyRefusal.None => null,
            CopyRefusal.SameOrNestedTarget => Loc.T("copy.refusal.sameOrNested"),
            CopyRefusal.TargetNotEmpty => Loc.T("copy.refusal.targetNotEmpty"),
            _ => Loc.T("copy.refusal.sourceLocked"),
        };
    }

    /// <summary>Adds the copy when it ran to its end; the summary line.</summary>
    private string Finish(BackupPlan copy, PlanCopyResult result)
    {
        if (result.Canceled)
            return Loc.T("copy.canceled");
        if (result.VersionsCopied + result.VersionsSkipped == 0 && result.Failures.Count > 0)
            return Loc.T("copy.notCompleted");
        var done = Loc.F("copy.done", ("copied", result.VersionsCopied), ("skipped", result.VersionsSkipped));
        if (!_addPlan(copy))
            return done + "\n" + Loc.T("copy.notAdded");
        CreatedPlan = copy;
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

    /// <summary>The dialog closes: a copy still running stops (and releases its locks).</summary>
    public void Stop() => _cts?.Cancel();

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
