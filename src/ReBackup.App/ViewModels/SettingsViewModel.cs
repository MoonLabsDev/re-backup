using System.IO;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Core.Config;
using ReBackup.Core.IO;
using ReBackup.Core.Settings;

namespace ReBackup.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly string _appDataRoot;
    private readonly IDialogService _dialogs;
    private readonly Func<bool> _confirmRestart;
    private readonly Func<bool> _isBackupActive;
    private ConfigPaths _paths;

    [ObservableProperty] private string _configFolder = "";
    [ObservableProperty] private string _defaultIgnorePatterns = "";
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _startWithWindows;

    public SettingsViewModel(SettingsStore store, AppSettings settings, ConfigPaths paths, string appDataRoot,
        IDialogService dialogs, Func<bool> confirmRestart, Func<bool> isBackupActive)
    {
        _store = store;
        _settings = settings;
        _paths = paths;
        _appDataRoot = appDataRoot;
        _dialogs = dialogs;
        _confirmRestart = confirmRestart;
        _isBackupActive = isBackupActive;

        ConfigFolder = paths.Root;
        DefaultIgnorePatterns = string.Join(Environment.NewLine, settings.DefaultIgnorePatterns);
        CloseToTray = settings.CloseToTray;
        StartWithWindows = settings.StartWithWindows;
    }

    /// <summary>Raised with the dialog result (true = saved).</summary>
    public event EventHandler<bool>? CloseRequested;

    public bool RestartRequired { get; private set; }

    [RelayCommand]
    private void ChangeConfigFolder()
    {
        if (_isBackupActive())
        {
            _dialogs.ShowInfo("Configuration folder",
                "A backup is running or queued. Wait for it to finish or cancel it before moving the configuration folder.");
            return;
        }

        var folder = _dialogs.PickFolder("Choose configuration folder", ConfigFolder);
        if (folder is null)
            return;
        if (string.Equals(PathUtil.Normalize(folder), PathUtil.Normalize(_paths.Root), StringComparison.OrdinalIgnoreCase))
            return;

        var mode = ConfigMoveMode.CopyCurrent;
        if (ConfigLocation.ContainsConfiguration(folder))
        {
            if (!_dialogs.Confirm("Configuration folder",
                    "That folder already contains a ReBackup configuration.\n\nSwitch to it without copying the current plans?"))
                return;
            mode = ConfigMoveMode.UseExisting;
        }

        if (HasOtherEdits() && !_dialogs.Confirm("Configuration folder",
                "Your other changes in this dialog will not be saved. Continue?"))
            return;

        if (!_confirmRestart())
            return;

        try
        {
            _paths = ConfigLocation.Move(_appDataRoot, _paths, folder, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or NotSupportedException)
        {
            _dialogs.ShowError("Configuration folder", ex.Message);
            return;
        }

        ConfigFolder = _paths.Root;
        RestartRequired = true;
        _dialogs.ShowInfo("Configuration folder", "ReBackup will now restart to use the new configuration folder.");
        CloseRequested?.Invoke(this, false);
    }

    private bool HasOtherEdits() =>
        CloseToTray != _settings.CloseToTray
        || StartWithWindows != _settings.StartWithWindows
        || !ParsePatterns().SequenceEqual(_settings.DefaultIgnorePatterns);

    private List<string> ParsePatterns() => DefaultIgnorePatterns
        .Split((char)10)
        .Select(line => line.TrimEnd((char)13, (char)32, (char)9))
        .Where(line => line.Length > 0)
        .ToList();

    [RelayCommand]
    private void Save()
    {
        var oldPatterns = _settings.DefaultIgnorePatterns.ToList();
        var oldCloseToTray = _settings.CloseToTray;
        var oldStartWithWindows = _settings.StartWithWindows;
        var registryChanged = false;

        try
        {
            StartupRegistration.Apply(StartWithWindows);
            registryChanged = StartWithWindows != oldStartWithWindows;

            _settings.DefaultIgnorePatterns = ParsePatterns();
            _settings.CloseToTray = CloseToTray;
            _settings.StartWithWindows = StartWithWindows;
            _store.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _settings.DefaultIgnorePatterns = oldPatterns;
            _settings.CloseToTray = oldCloseToTray;
            _settings.StartWithWindows = oldStartWithWindows;
            if (registryChanged)
            {
                try { StartupRegistration.Apply(oldStartWithWindows); }
                catch (Exception revertEx) when (revertEx is IOException or UnauthorizedAccessException or SecurityException) { }
            }
            _dialogs.ShowError("Settings", ex.Message);
            return;
        }

        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
