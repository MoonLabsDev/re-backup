using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.GoogleDrive;
using ReBackup.Storage.GoogleDrive.Connections;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The logic of the Drive connection dialog: a name, the Google account, and the root — a shared drive the account is a member
/// of (listed from Drive) or a folder given by its link or ID (looked up on Save). Testable without a window.
/// </summary>
public sealed partial class DriveConnectionDialogViewModel : ObservableObject
{
    /// <summary>A Drive file ID: letters, digits, <c>-</c> and <c>_</c>.</summary>
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{10,}$", RegexOptions.CultureInvariant);

    private readonly DriveConnectionInfo? _existing;
    private readonly Func<IReadOnlyList<DriveAccount>> _accounts;
    private readonly Func<string, bool> _isNameTaken;
    private readonly Func<string, IDriveApi?> _apiForAccount;
    private readonly Func<DriveAccount?> _createAccount;
    private string? _driveToSelect;
    private int _loads;

    /// <param name="existing">The connection to edit; <c>null</c> for a new one.</param>
    /// <param name="accounts">The stored accounts; read again after "New account…".</param>
    /// <param name="isNameTaken">Whether another connection already has the name.</param>
    /// <param name="apiForAccount">An API client on an account's key (disposed after use when it is <see cref="IDisposable"/>); null when the key must be entered again.</param>
    /// <param name="createAccount">"New account…": opens the account dialog and stores the account; the stored account, or <c>null</c>.</param>
    public DriveConnectionDialogViewModel(DriveConnectionInfo? existing, Func<IReadOnlyList<DriveAccount>> accounts,
        Func<string, bool> isNameTaken, Func<string, IDriveApi?> apiForAccount, Func<DriveAccount?> createAccount)
    {
        _existing = existing;
        _accounts = accounts;
        _isNameTaken = isNameTaken;
        _apiForAccount = apiForAccount;
        _createAccount = createAccount;
        foreach (var account in accounts())
            Accounts.Add(account);
        if (existing is not null)
        {
            _name = existing.Name;
            _selectedAccount = Accounts.FirstOrDefault(a => SameId(a.Id, existing.AccountId));
            if (existing.SharedDriveId is { } drive && drive == existing.RootId)
            {
                _driveToSelect = drive;
                SharedDrives.Add(new SharedDrive(existing.RootId, existing.RootName));
                _selectedSharedDrive = SharedDrives[0];
            }
            else
            {
                _useFolder = true;
                _folderText = existing.RootId;
            }
        }
        else if (Accounts.Count == 1)
        {
            _selectedAccount = Accounts[0];
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountHint))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(LoadSharedDrivesCommand))]
    private DriveAccount? _selectedAccount;

    /// <summary>The root is a folder given by link or ID; otherwise a shared drive from the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseSharedDrive))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _useFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private SharedDrive? _selectedSharedDrive;

    /// <summary>The folder's link (<c>https://drive.google.com/drive/folders/…</c>) or ID.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _folderText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(LoadSharedDrivesCommand), nameof(NewAccountCommand))]
    private bool _isBusy;

    /// <summary>Why the shared drives could not be listed or the folder not used; null otherwise.</summary>
    [ObservableProperty] private string? _error;

    public bool UseSharedDrive
    {
        get => !UseFolder;
        set { if (value) UseFolder = false; }
    }

    public ObservableCollection<DriveAccount> Accounts { get; } = [];

    /// <summary>The shared drives the chosen account is a member of, as last listed.</summary>
    public ObservableCollection<SharedDrive> SharedDrives { get; } = [];

    public bool IsNew => _existing is null;

    public bool IsEditable => !IsBusy;

    /// <summary>"Another connection already has this name."; null otherwise.</summary>
    public string? NameError =>
        Name.Trim() is { Length: > 0 } name && !IsOwnName(name) && _isNameTaken(name) ? Loc.T("driveconnections.nameTaken") : null;

    /// <summary>The address to share folders and shared drives with, or that the account's key must be entered again.</summary>
    public string? AccountHint => SelectedAccount switch
    {
        null => null,
        { NeedsKey: true } => Loc.T("driveconnections.accountNeedsKey"),
        { } account => Loc.F("driveconnections.shareWith", ("email", account.ClientEmail)),
    };

    /// <summary>The connection as saved. Set by <see cref="SaveCommand"/>.</summary>
    public DriveConnectionInfo? Result { get; private set; }

    /// <summary>Raised after <see cref="SaveCommand"/> set <see cref="Result"/>; the dialog closes.</summary>
    public event EventHandler? Saved;

    partial void OnSelectedAccountChanged(DriveAccount? value)
    {
        SharedDrives.Clear();
        SelectedSharedDrive = null;
        Error = null;
    }

    private bool CanLoad() => !IsBusy && SelectedAccount is { NeedsKey: false };

    /// <summary>Lists the shared drives the chosen account is a member of; the edited connection's drive is selected again.</summary>
    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadSharedDrivesAsync()
    {
        var account = SelectedAccount!;
        var load = ++_loads;
        Error = null;
        IsBusy = true;
        try
        {
            var drives = await CallAsync(account, (api, ct) => api.ListSharedDrivesAsync(ct));
            if (load != _loads || drives is null)
                return;
            var keep = SelectedSharedDrive?.Id ?? _driveToSelect;
            SharedDrives.Clear();
            foreach (var drive in drives.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
                SharedDrives.Add(drive);
            SelectedSharedDrive = SharedDrives.FirstOrDefault(d => d.Id == keep);
            _driveToSelect = null;
            if (SharedDrives.Count == 0)
                Error = Loc.F("driveconnections.noSharedDrives", ("email", account.ClientEmail));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() =>
        !IsBusy && Name.Trim().Length > 0 && NameError is null && SelectedAccount is not null &&
        (UseFolder ? FolderText.Trim().Length > 0 : SelectedSharedDrive is not null);

    /// <summary>A shared drive is taken as listed; a folder is looked up first (it must exist and be shared with the account).</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Error = null;
        var id = _existing?.Id ?? Guid.NewGuid().ToString("N");
        var account = SelectedAccount!;
        if (!UseFolder)
        {
            var drive = SelectedSharedDrive!;
            Finish(new DriveConnectionInfo(id, Name.Trim(), account.Id, drive.Id, drive.Name, drive.Id));
            return;
        }

        if (ParseFolderId(FolderText) is not { } folderId)
        {
            Error = Loc.T("driveconnections.folderInvalid");
            return;
        }
        IsBusy = true;
        DriveItem? folder;
        try
        {
            folder = await CallAsync(account, (api, ct) => api.GetAsync(folderId, ct));
        }
        finally
        {
            IsBusy = false;
        }
        if (folder is null)
            return;
        if (!folder.IsFolder)
        {
            Error = Loc.T("driveconnections.notAFolder");
            return;
        }
        Finish(new DriveConnectionInfo(id, Name.Trim(), account.Id, folder.Id, folder.Name, folder.DriveId));
    }

    private void Finish(DriveConnectionInfo result)
    {
        Result = result;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"New account…": the account list is read again and the created account chosen.</summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void NewAccount()
    {
        if (_createAccount() is not { } created)
            return;
        Accounts.Clear();
        foreach (var account in _accounts())
            Accounts.Add(account);
        var match = Accounts.FirstOrDefault(a => SameId(a.Id, created.Id));
        if (match is null)
        {
            match = created;
            Accounts.Add(created);
        }
        SelectedAccount = match;
    }

    /// <summary>
    /// A Drive call on the account's key; null with <see cref="Error"/> set when it failed (a key to enter again, access
    /// denied, not found or not shared, anything else with its reason).
    /// </summary>
    private async Task<T?> CallAsync<T>(DriveAccount account, Func<IDriveApi, CancellationToken, Task<T>> call) where T : class
    {
        IDriveApi? api = null;
        try
        {
            api = _apiForAccount(account.Id);
            if (api is null)
            {
                Error = Loc.T("driveconnections.accountNeedsKey");
                return null;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            return await call(api, timeout.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = Describe(ex, account.ClientEmail);
            return null;
        }
        finally
        {
            (api as IDisposable)?.Dispose();
        }
    }

    private static string Describe(Exception ex, string email) => ex switch
    {
        StorageAccessDeniedException => Loc.F("driveconnections.accessDenied", ("email", email)),
        DriveApiException { Status: 404 } => Loc.F("driveconnections.notFound", ("email", email)),
        DriveApiException { Status: 401 or 403 } api when api.Reason is not ("rateLimitExceeded" or "userRateLimitExceeded")
            => Loc.F("driveconnections.accessDenied", ("email", email)),
        _ => Loc.F("driveconnections.failed", ("error", ex.Message)),
    };

    /// <summary>
    /// The folder ID in a Drive folder link (<c>https://drive.google.com/drive/folders/&lt;id&gt;</c>, also with <c>/u/0/</c> or a
    /// query, or <c>…/open?id=&lt;id&gt;</c>) or the ID itself; null when the text is neither.
    /// </summary>
    public static string? ParseFolderId(string? text)
    {
        var value = text?.Trim() ?? "";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            if (!uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase))
                return null;
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var index = Array.IndexOf(segments, "folders");
            value = index >= 0 && index + 1 < segments.Length
                ? segments[index + 1]
                : QueryValue(uri.Query, "id") ?? "";
        }
        return IdPattern.IsMatch(value) ? value : null;
    }

    private static string? QueryValue(string query, string name)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0] == name)
                return Uri.UnescapeDataString(pair[1]);
        }
        return null;
    }

    private bool IsOwnName(string name) => _existing is not null && string.Equals(name, _existing.Name, StringComparison.OrdinalIgnoreCase);

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
