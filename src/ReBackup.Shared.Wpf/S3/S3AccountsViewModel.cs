using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Shared.Wpf.S3;

/// <summary>The windows <see cref="S3AccountsViewModel"/> opens; <see cref="S3AccountDialogs"/> in the app, a fake in tests.</summary>
public interface IS3AccountDialogs
{
    /// <summary>Opens the account dialog; the saved account (<c>Secret</c> null = unchanged), or <c>null</c> when cancelled.</summary>
    S3Account? EditAccount(S3Account? existing, Func<string, bool> isNameTaken);

    /// <summary>Asks a yes/no question; <paramref name="titleKey"/> is the label key of the caption.</summary>
    bool Confirm(string titleKey, string message);

    void ShowError(string message);
}

/// <summary>One row of the account list: the account (secret held in memory only) and how many connections use it.</summary>
public sealed record S3AccountRow(S3Account Account, int UsedBy)
{
    /// <summary>The secret cannot be decrypted here and has to be entered again.</summary>
    public bool NeedsSecret => Account.NeedsSecret;
}

/// <summary>
/// The logic of <see cref="S3AccountsDialog"/>: the accounts of a <see cref="S3ConnectionStore"/>, with Add, Edit and Delete.
/// Every change goes to the store at once; an account still used by a connection is not deleted.
/// </summary>
public sealed partial class S3AccountsViewModel : ObservableObject
{
    public const string DeleteTitleKey = "s3.accounts.deleteTitle";
    public const string DeleteConfirmKey = "s3.accounts.deleteConfirm";
    public const string InUseKey = "s3.accounts.inUse";
    public const string SaveFailedKey = "s3.accounts.saveFailed";
    public const string LoadFailedKey = "s3.accounts.loadFailed";

    private readonly S3ConnectionStore _store;
    private readonly IS3AccountDialogs _dialogs;

    /// <param name="store">The accounts and connections file of the app.</param>
    /// <param name="tester">Kept for the app's account dialogs; the list itself runs no test.</param>
    /// <param name="dialogs">Opens the account dialog and the message boxes.</param>
    public S3AccountsViewModel(S3ConnectionStore store, S3ConnectionTester tester, IS3AccountDialogs dialogs)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tester);
        ArgumentNullException.ThrowIfNull(dialogs);
        _store = store;
        _dialogs = dialogs;
        Reload(null);
    }

    /// <summary>The accounts in file order.</summary>
    public ObservableCollection<S3AccountRow> Accounts { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand))]
    private S3AccountRow? _selected;

    /// <summary>The store's file could not be read: the list stays empty and nothing can be changed (the file is never overwritten).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand), nameof(EditCommand), nameof(DeleteCommand))]
    private bool _loadFailed;

    /// <summary>There are no accounts (and the file was read).</summary>
    public bool IsEmpty => !LoadFailed && Accounts.Count == 0;

    private bool CanAdd() => !LoadFailed;

    private bool CanChange() => !LoadFailed && Selected is not null;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add() => SaveFromDialog(null);

    [RelayCommand(CanExecute = nameof(CanChange))]
    private void Edit()
    {
        if (Selected is { } row) SaveFromDialog(row.Account);
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private void Delete()
    {
        if (Selected is not { } row) return;
        try
        {
            var users = _store.ConnectionsUsing(row.Account.Id);
            if (users.Count > 0)
            {
                _dialogs.ShowError(InUseMessage(users));
                return;
            }
            if (!_dialogs.Confirm(DeleteTitleKey, Loc.F(DeleteConfirmKey, ("name", row.Account.Name)))) return;
            _store.DeleteAccount(row.Account.Id);
        }
        catch (InvalidOperationException)
        {
            // A connection started to use the account after the check (another app wrote the file).
            _dialogs.ShowError(InUseMessage(SafeConnectionsUsing(row.Account.Id)));
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            _dialogs.ShowError(Loc.T(SaveFailedKey));
        }
        Reload(null);
    }

    private void SaveFromDialog(S3Account? existing)
    {
        var others = Accounts.Select(row => row.Account).Where(a => existing is null || !SameId(a.Id, existing.Id)).ToList();
        var result = _dialogs.EditAccount(existing,
            name => others.Exists(a => string.Equals(a.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (result is null) return;
        try
        {
            _store.SaveAccount(result);
        }
        catch (Exception ex) when (IsStoreFailure(ex) || ex is ArgumentException)
        {
            _dialogs.ShowError(Loc.T(SaveFailedKey));
            return;
        }
        Reload(result.Id);
    }

    /// <summary>Reads the list again and selects the account <paramref name="selectId"/> (or nothing).</summary>
    private void Reload(string? selectId)
    {
        Accounts.Clear();
        try
        {
            var usedBy = _store.LoadAll().GroupBy(c => c.AccountId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var account in _store.LoadAccounts())
                Accounts.Add(new S3AccountRow(account, usedBy.GetValueOrDefault(account.Id)));
            LoadFailed = false;
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            Accounts.Clear();
            LoadFailed = true;
        }
        Selected = selectId is null ? null : Accounts.FirstOrDefault(row => SameId(row.Account.Id, selectId));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private IReadOnlyList<S3ConnectionInfo> SafeConnectionsUsing(string accountId)
    {
        try
        {
            return _store.ConnectionsUsing(accountId);
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            return [];
        }
    }

    /// <summary>The refusal, then one connection name per line (names are user data, not part of the label).</summary>
    private static string InUseMessage(IReadOnlyList<S3ConnectionInfo> users) =>
        Loc.T(InUseKey) + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, users.Select(c => "• " + c.Name));

    private static bool IsStoreFailure(Exception ex) => ex is JsonException or IOException or UnauthorizedAccessException;

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
