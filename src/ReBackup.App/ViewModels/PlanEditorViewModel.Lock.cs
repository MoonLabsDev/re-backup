using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Pool;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The lock of a plan's target (spec 11): "Locked by … since …" above the Plan tab while someone holds it, and "Release
/// lock…" for a lock a crashed run, delete or copy left behind; and whether "Copy plan…" is copying this plan. Incremental
/// plans lock their target for runs, deletes and verifies; any saved plan's target is locked while it is copied, so the
/// lock of every saved plan is read.
/// </summary>
public sealed partial class PlanEditorViewModel
{
    /// <summary>Drops the result of a lock read that a newer one (or a release) overtook.</summary>
    private int _lockReads;

    /// <summary>The holder the banner shows; null with <see cref="_lockUnreadable"/> for a lock file that cannot be parsed.</summary>
    private LockInfo? _lock;

    private bool _lockUnreadable;

    /// <summary>
    /// "Copy plan…" copies this plan's versions (or into this plan) right now: it does not run, its versions are not
    /// deleted and its lock is not released.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReleaseLockCommand))]
    private bool _isCopying;

    /// <summary>"Verify backup…" checks this plan's target right now: its lock is not released.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReleaseLockCommand))]
    private bool _isVerifying;

    /// <summary>
    /// The saved plan is a copy "Copy plan…" has not finished (<see cref="BackupPlan.CopyFromPlanId"/>): it is not enabled
    /// or run until "Continue copy…" finishes it.
    /// </summary>
    public bool IsCopyUnfinished => !IsNew && _saved.CopyFromPlanId is not null;

    /// <summary>The "Enabled" switch can be turned on (not for an unfinished copy; it can always be turned off).</summary>
    public bool CanEnable => !IsCopyUnfinished || Enabled;

    /// <summary>
    /// The name can be edited: not for an unfinished copy, whose versions on the target carry its name ("Continue copy…"
    /// would copy them again under a new one).
    /// </summary>
    public bool CanEditName => !IsCopyUnfinished;

    /// <summary>The saved plan is incremental: its target has a lock, can be verified and shows the lock banner.</summary>
    public bool ShowPoolActions => !IsNew && _saved.Incremental;

    /// <summary>Someone holds the saved target's lock (or the lock file cannot be read); the banner is shown.</summary>
    public bool HasLock => _lock is not null || _lockUnreadable;

    /// <summary>
    /// "Release lock…" is in the plan's menu: always for an incremental plan, for a full plan while its target has a lock
    /// (one a copy of it left behind).
    /// </summary>
    public bool ShowReleaseLock => ShowPoolActions || HasLock;

    /// <summary>"Locked by {host} ({operation}) since {time}"; null while the target is not locked.</summary>
    public string? LockBanner => _lockUnreadable
        ? Loc.T("plan.lock.unreadable")
        : _lock is { } holder
            ? Loc.F("plan.lock.banner", ("host", holder.Host), ("operation", holder.Operation), ("time", When(holder)))
            : null;

    /// <summary>"Copy plan…" has finished copying into this plan: its saved file no longer carries the unfinished mark.</summary>
    public void MarkCopyFinished()
    {
        _saved.CopyFromPlanId = null;
        OnSavedPlanChanged();
        Validate();
    }

    /// <summary>The saved plan changed (saved, reloaded): whether it is incremental may have changed, and so its lock.</summary>
    private void OnSavedPlanChanged()
    {
        OnPropertyChanged(nameof(ShowPoolActions));
        OnPropertyChanged(nameof(ShowReleaseLock));
        OnPropertyChanged(nameof(IsCopyUnfinished));
        OnPropertyChanged(nameof(CanEnable));
        OnPropertyChanged(nameof(CanEditName));
        _ = RefreshLockAsync();
    }

    /// <summary>
    /// Reads the saved target's lock off the UI thread and shows who holds it. An unsaved plan has none; a target that
    /// cannot be read shows no banner (the next run or action reports why).
    /// </summary>
    public async Task RefreshLockAsync()
    {
        var read = ++_lockReads;
        if (IsNew)
        {
            ShowLock(null, unreadable: false);
            return;
        }
        var target = _saved.Target;
        var (holder, unreadable) = await ReadLockAsync(target);
        if (read == _lockReads)
            ShowLock(holder, unreadable);
    }

    /// <summary>The holder of the lock on <paramref name="target"/>; never throws.</summary>
    private async Task<(LockInfo? Holder, bool Unreadable)> ReadLockAsync(StorageLocation target)
    {
        try
        {
            var holder = await Task.Run(() => TargetLock.ReadAsync(_context.Storages.Open(target), CancellationToken.None));
            return (holder, false);
        }
        catch (JsonException)
        {
            return (null, true);
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            Trace.TraceWarning($"The lock of plan {Id} could not be read: {ex.Message}");
            return (null, false);
        }
    }

    private void ShowLock(LockInfo? holder, bool unreadable)
    {
        _lock = holder;
        _lockUnreadable = unreadable;
        OnPropertyChanged(nameof(HasLock));
        OnPropertyChanged(nameof(ShowReleaseLock));
        OnPropertyChanged(nameof(LockBanner));
        ReleaseLockCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// "Release lock…" is offered for someone else's lock: not while this app's own run (or retry), copy, verify or delete
    /// (in the Versions tab, with its garbage collection) of the plan holds it (they release it themselves).
    /// </summary>
    private bool CanReleaseLock() =>
        HasLock && !Run.IsActive && !Run.IsRetryActive && !IsCopying && !IsVerifying && !Versions.IsDeleting;

    /// <summary>
    /// "Release lock…": after a confirmation naming the holder and its start time, removes the lock whoever holds it. Only
    /// the lock the user confirmed is removed: when another holder took the target meanwhile, the banner shows it instead.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReleaseLock))]
    private async Task ReleaseLockAsync()
    {
        if (!CanReleaseLock() || IsNew)
            return;
        var shown = _lock;
        var title = Loc.T("plan.lock.releaseTitle");
        var question = shown is null
            ? Loc.T("plan.lock.releaseConfirmUnreadable")
            : Loc.F("plan.lock.releaseConfirm", ("host", shown.Host), ("operation", shown.Operation), ("time", When(shown)));
        if (!_context.Dialogs.ConfirmDefaultNo(title, question))
            return;

        var target = _saved.Target;
        var (current, unreadable) = await ReadLockAsync(target);
        if (current?.Token != shown?.Token || (shown is null && !unreadable))
        {
            ++_lockReads;
            ShowLock(current, unreadable);
            _context.Dialogs.ShowInfo(title, Loc.T("plan.lock.changed"));
            return;
        }
        try
        {
            await Task.Run(() => TargetLock.ForceReleaseAsync(_context.Storages.Open(target), CancellationToken.None));
            _context.ReportStatus(LocText.Of("plan.lock.released", ("plan", Name)));
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            _context.Dialogs.ShowError(title, Loc.F("plan.lock.releaseFailed", ("error", Loc.Known(ex.Message))));
        }
        await RefreshLockAsync();
    }

    /// <summary>The lock's start, in local time as the language writes it.</summary>
    private static string When(LockInfo holder) =>
        Formats.DateAndTime(DateTime.SpecifyKind(holder.StartedUtc, DateTimeKind.Utc).ToLocalTime());
}
