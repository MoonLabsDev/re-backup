using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage.GoogleDrive.Connections;

namespace ReBackup.App.ViewModels;

/// <summary>What the Google account dialog returns: the name and, when a key file was chosen, the key JSON (else the stored key stays).</summary>
public sealed record DriveAccountInput(string Name, string? KeyJson)
{
    /// <summary>Never includes the key.</summary>
    public override string ToString() => $"DriveAccountInput {{ Name = {Name} }}";
}

/// <summary>
/// The logic of the Google account dialog: a name and the service account's key file. The key's <c>client_email</c> is shown
/// with the hint to share folders and shared drives with it. The key stays in memory until the caller stores it.
/// </summary>
public sealed partial class DriveAccountDialogViewModel : ObservableObject
{
    /// <summary>Key files larger than this are refused without reading them.</summary>
    public const long MaxKeyFileBytes = 64 * 1024;

    private readonly DriveAccount? _existing;
    private readonly Func<string, bool> _isNameTaken;
    private readonly Func<string?> _pickKeyFile;
    private string? _keyJson;

    /// <param name="existing">The account to edit (rename, or a new key); <c>null</c> for a new one.</param>
    /// <param name="isNameTaken">Whether another account already has the name.</param>
    /// <param name="pickKeyFile">Asks for the key file; its path, or <c>null</c> when cancelled.</param>
    public DriveAccountDialogViewModel(DriveAccount? existing, Func<string, bool> isNameTaken, Func<string?> pickKeyFile)
    {
        _existing = existing;
        _isNameTaken = isNameTaken;
        _pickKeyFile = pickKeyFile;
        if (existing is not null)
        {
            _name = existing.Name;
            _clientEmail = existing.ClientEmail;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name = "";

    /// <summary>The service account's e-mail: from the chosen key, else the stored one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClientEmail))]
    private string? _clientEmail;

    /// <summary>The chosen key file's name; null until one is chosen.</summary>
    [ObservableProperty] private string? _keyFileName;

    /// <summary>Why the chosen file cannot be used; null otherwise.</summary>
    [ObservableProperty] private string? _keyError;

    public bool IsNew => _existing is null;

    public bool HasClientEmail => !string.IsNullOrEmpty(ClientEmail);

    /// <summary>The stored key cannot be read here and must be chosen again before the account works.</summary>
    public bool NeedsKey => _existing is { NeedsKey: true } && _keyJson is null;

    /// <summary>"Another account already has this name."; null otherwise.</summary>
    public string? NameError =>
        Name.Trim() is { Length: > 0 } name && !IsOwnName(name) && _isNameTaken(name) ? Loc.T("driveaccounts.nameTaken") : null;

    /// <summary>The account as saved. Set by <see cref="SaveCommand"/>.</summary>
    public DriveAccountInput? Result { get; private set; }

    /// <summary>Raised after <see cref="SaveCommand"/> set <see cref="Result"/>; the dialog closes.</summary>
    public event EventHandler? Saved;

    /// <summary>"Choose key file…": reads the file and shows its service account's e-mail.</summary>
    [RelayCommand]
    private void ChooseKey()
    {
        if (_pickKeyFile() is not { } path)
            return;
        string json;
        try
        {
            // A service-account key is a few KB; anything much larger is not one and is not read into memory.
            if (new FileInfo(path).Length > MaxKeyFileBytes)
            {
                KeyError = Loc.T("driveaccounts.keyInvalid");
                return;
            }
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            KeyError = Loc.F("driveaccounts.keyUnreadable", ("error", ex.Message));
            return;
        }
        if (ReadClientEmail(json) is not { } email)
        {
            KeyError = Loc.T("driveaccounts.keyInvalid");
            return;
        }
        _keyJson = json;
        KeyError = null;
        KeyFileName = Path.GetFileName(path);
        ClientEmail = email;
        OnPropertyChanged(nameof(NeedsKey));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private bool CanSave() => Name.Trim().Length > 0 && NameError is null && (_existing is not null || _keyJson is not null);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        Result = new DriveAccountInput(Name.Trim(), _keyJson);
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private bool IsOwnName(string name) => _existing is not null && string.Equals(name, _existing.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>client_email</c> of a service-account key (type, private key and e-mail present); null for anything else.</summary>
    public static string? ReadClientEmail(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject key)
                return null;
            return key["type"]?.GetValueKind() == JsonValueKind.String && (string?)key["type"] == "service_account"
                   && key["private_key"]?.GetValueKind() == JsonValueKind.String && !string.IsNullOrWhiteSpace((string?)key["private_key"])
                   && key["client_email"]?.GetValueKind() == JsonValueKind.String && ((string?)key["client_email"])?.Trim() is { Length: > 0 } email
                ? email
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
