using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Pool;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The lock of an incremental plan's target (spec 11): "Locked by … since …" above the Plan tab while someone holds it,
/// and "Release lock…" for a lock a crashed run left behind; and whether "Copy plan…" is copying this plan.
/// </summary>
public sealed partial class PlanEditorViewModel
{
    /// <summary>Drops the result of a lock read that a newer one (or a release) overtook.</summary>
    private int _lockReads;

    /// <summary>The holder the banner shows; null with <see cref="_lockUnreadable"/> for a lock file that cannot be parsed.</summary>
    private LockInfo? _lock;

    private bool _lockUnreadable;

    /// <summary>"Copy plan…" copies this plan's versions right now: it does not run and its versions are not deleted.</summary>
    [ObservableProperty] private bool _isCopying;

    /// <summary>The saved plan is incremental: its target has a lock, can be verified and shows the lock banner.</summary>
    public bool ShowPoolActions => !IsNew && _saved.Incremental;

    /// <summary>Someone holds the saved target's lock (or the lock file cannot be read); the banner is shown.</summary>
    public bool HasLock => _lock is not null || _lockUnreadable;

    /// <summary>"Locked by {host} ({operation}) since {time}"; null while the target is not locked.</summary>
    public string? LockBanner => _lockUnreadable
        ? Loc.T("plan.lock.unreadable")
        : _lock is { } holder
            ? Loc.F("plan.lock.banner", ("host", holder.Host), ("operation", holder.Operation), ("time", When(holder)))
            : null;

    /// <summary>The saved plan changed (saved, reloaded): whether it is incremental may have changed, and so its lock.</summary>
    private void OnSavedPlanChanged()
    {
        OnPropertyChanged(nameof(ShowPoolActions));
        _ = RefreshLockAsync();
    }

    /// <summary>
    /// Reads the saved target's lock off the UI thread and shows who holds it. A full or unsaved plan has none; a target
    /// that cannot be read shows no banner (the next run or action reports why).
    /// </summary>
    public async Task RefreshLockAsync()
    {
        var read = ++_lockReads;
        if (!ShowPoolActions)
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
        OnPropertyChanged(nameof(LockBanner));
        ReleaseLockCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// "Release lock…": after a confirmation naming the holder and its start time, removes the lock whoever holds it. Only
    /// the lock the user confirmed is removed: when another holder took the target meanwhile, the banner shows it instead.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasLock))]
    private async Task ReleaseLockAsync()
    {
        if (!HasLock || !ShowPoolActions)
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
