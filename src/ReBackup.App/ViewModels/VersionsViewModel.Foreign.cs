using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Backup;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The Versions tab: folders another computer is writing or deleting in the target (spec 5). They are listed and left
/// alone; a person who knows that run is over takes one over, which cleans it up by the rules of a leftover of this plan.
/// </summary>
public sealed partial class VersionsViewModel
{
    /// <summary>The folders of other computers, by name.</summary>
    public RangeObservableCollection<ForeignFolderRowViewModel> ForeignRows { get; } = new();

    /// <summary>The folder of another computer the context menu acts on.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TakeOverCommand))]
    private ForeignFolderRowViewModel? _selectedForeign;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TakeOverCommand))]
    private bool _isTakingOver;

    public bool HasForeignRows => ForeignRows.Count > 0;

    partial void OnIsDeletingChanged(bool value) => TakeOverCommand.NotifyCanExecuteChanged();

    partial void OnIsRestoringChanged(bool value) => TakeOverCommand.NotifyCanExecuteChanged();

    /// <summary>Shows the listed folders of other computers, keeping the selection by name.</summary>
    private void ShowForeign(IReadOnlyList<ForeignMarkedFolder> folders)
    {
        var selectedName = SelectedForeign?.Name;
        var rows = folders.Select(folder => new ForeignFolderRowViewModel(folder)).ToList();
        ForeignRows.ReplaceAll(rows);
        SelectedForeign = rows.FirstOrDefault(r => r.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(HasForeignRows));
    }

    private bool CanTakeOver() => SelectedForeign is not null && !IsTakingOver && !IsDeleting && !IsRestoring;

    /// <summary>
    /// Confirm (naming the computer and the time) → clean the folder up as a leftover of this plan → read the target
    /// again. Refused while a backup of the plan is queued or running.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTakeOver))]
    private async Task TakeOverAsync()
    {
        if (_savedPlan() is not { } plan || SelectedForeign is not { } row)
            return;

        var dialogs = _context.Dialogs;
        var title = Loc.T("versions.foreign.title");
        if (_isBackupActive())
        {
            dialogs.ShowError(title, Loc.T("versions.foreign.backupRunning"));
            return;
        }
        var confirm = row.Folder.Kind == MarkerKind.Pending ? "versions.foreign.confirmPending" : "versions.foreign.confirmDeleting";
        if (!dialogs.ConfirmDefaultNo(title, Loc.F(confirm, ("name", row.Name), ("host", row.Folder.Host), ("time", row.TimeText))))
            return;

        // A sync must not import the folder while it is cleaned up; the target is read again afterwards.
        _syncCts?.Cancel();
        IsTakingOver = true;
        try
        {
            var warnings = await Task.Run(() => LeftoverCleaner.TakeOverAsync(_context.Storages.Open(plan.Target), plan.Id,
                plan.Name, row.Folder, CancellationToken.None));
            if (warnings.Count == 0)
                _context.ReportStatus(LocText.Of("versions.foreign.done", ("name", row.Name)));
            else
                dialogs.ShowFailures(title, Loc.F("versions.foreign.problems", ("name", row.Name)),
                    warnings.Select(warning => Loc.Known(warning)).ToList());
        }
        catch (Exception ex)
        {
            dialogs.ShowError(title, Loc.F("versions.foreign.failed", ("error", Loc.Known(ex.Message))));
        }
        finally
        {
            IsTakingOver = false;
        }

        VersionsDeleted?.Invoke();
        await RefreshAsync();
    }
}
