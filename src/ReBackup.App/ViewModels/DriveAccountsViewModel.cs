using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage.GoogleDrive.Connections;

namespace ReBackup.App.ViewModels;

/// <summary>
/// Settings → "Google accounts…": the stored service accounts with Add and Edit (name and key file) and Delete, which is
/// refused while a Drive connection uses the account. Every change is stored at once; a file that cannot be read is shown
/// with its reason and never overwritten.
/// </summary>
public sealed partial class DriveAccountsViewModel : ObservableObject
{
    private readonly DriveConnections _connections;
    private readonly IDialogService _dialogs;

    public DriveAccountsViewModel(DriveConnections connections, IDialogService dialogs)
    {
        _connections = connections;
        _dialogs = dialogs;
        connections.Reload();
        connections.Accounts.CollectionChanged += OnListChanged;
    }

    /// <summary>The stored accounts in file order.</summary>
    public ObservableCollection<DriveAccount> Accounts => _connections.Accounts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand))]
    private DriveAccount? _selected;

    /// <summary>Why the file cannot be read; null when it was read.</summary>
    public string? LoadError =>
        _connections.LoadError is { } error ? Loc.F("driveconnections.loadFailed", ("error", error)) : null;

    /// <summary>There are no accounts (and the file was read).</summary>
    public bool IsEmpty => _connections.LoadError is null && Accounts.Count == 0;

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(LoadError));
        AddCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The window closed: it no longer follows the list.</summary>
    public void Detach() => _connections.Accounts.CollectionChanged -= OnListChanged;

    private bool CanAdd() => _connections.LoadError is null;

    private bool CanChange() => CanAdd() && Selected is not null;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add() => SaveFromDialog(null);

    [RelayCommand(CanExecute = nameof(CanChange))]
    private void Edit()
    {
        if (Selected is { } account)
            SaveFromDialog(account);
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private void Delete()
    {
        if (Selected is not { } account)
            return;
        try
        {
            var users = _connections.Store.ConnectionsUsing(account.Id);
            if (users.Count > 0)
            {
                _dialogs.ShowError(Loc.T("driveaccounts.title"), Loc.F("driveaccounts.inUse", ("name", account.Name)) +
                    Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, users.Select(c => "• " + c.Name)));
                return;
            }
            if (!_dialogs.ConfirmDefaultNo(Loc.T("driveaccounts.title"), Loc.F("driveaccounts.deleteConfirm", ("name", account.Name))))
                return;
            _connections.DeleteAccount(account.Id);
        }
        catch (Exception ex) when (IsStoreFailure(ex) || ex is InvalidOperationException)
        {
            _dialogs.ShowError(Loc.T("driveaccounts.title"), Loc.F("driveconnections.saveFailed", ("error", ex.Message)));
        }
        Selected = null;
    }

    private void SaveFromDialog(DriveAccount? existing)
    {
        if (_dialogs.EditDriveAccount(existing) is not { } input)
            return;
        var id = existing?.Id ?? Guid.NewGuid().ToString("N");
        try
        {
            _connections.SaveAccount(id, input.Name, input.KeyJson);
        }
        catch (Exception ex) when (IsStoreFailure(ex) || ex is ArgumentException)
        {
            _dialogs.ShowError(Loc.T("driveaccounts.title"), Loc.F("driveconnections.saveFailed", ("error", ex.Message)));
            return;
        }
        Selected = Accounts.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsStoreFailure(Exception ex) => ex is JsonException or IOException or UnauthorizedAccessException;
}
