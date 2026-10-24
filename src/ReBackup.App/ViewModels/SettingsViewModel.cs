using System.IO;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.App.Control;
using ReBackup.App.Services;
using ReBackup.Shared.Wpf.Services;
using ReBackup.Core.Config;
using ReBackup.Core.Settings;
using ReBackup.Shared.IO;
using ReBackup.Shared.Settings;

namespace ReBackup.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly string _appDataRoot;
    private readonly IDialogService _dialogs;
    private readonly Func<bool> _confirmRestart;
    private readonly Func<bool> _isBackupActive;
    private readonly Action<ThemeMode> _previewTheme;
    private readonly Action<string> _previewLanguage;
    private readonly string _initialLanguage;
    private readonly Action _manageS3Accounts;
    private readonly Action _manageS3Connections;
    private readonly Action _manageDriveAccounts;
    private readonly Action _manageDriveConnections;
    private readonly IMcpControlHost? _mcpControl;
    private readonly Action<string> _copyToClipboard;
    private bool _revertingMcp;
    private int _copiedVersion;
    private readonly bool _ready;
    private bool _saved;
    private ConfigPaths _paths;

    [ObservableProperty] private string _configFolder = "";
    [ObservableProperty] private string _defaultIgnorePatterns = "";
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _startWithWindows;

    /// <summary>"Allow control via MCP": switching it hosts or releases the control pipe at once; OK stores it.</summary>
    [ObservableProperty] private bool _allowMcpControl;

    /// <summary>"Copied": shown for a few seconds after the client configuration went to the clipboard.</summary>
    [ObservableProperty] private bool _mcpCopied;

    /// <summary>The latest start or stop of the pipe; completes when the pipe is in the state the switch shows.</summary>
    public Task McpApplyTask { get; private set; } = Task.CompletedTask;

    /// <summary>The theme; applied at once while the dialog is open, restored on Cancel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDarkTheme), nameof(IsLightTheme))]
    private ThemeMode _theme;

    /// <summary>The language; applied at once while the dialog is open, restored on Cancel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGerman), nameof(IsEnglish))]
    private string _language = AppLanguages.English;

    public SettingsViewModel(SettingsStore store, AppSettings settings, ConfigPaths paths, string appDataRoot,
        IDialogService dialogs, Func<bool> confirmRestart, Func<bool> isBackupActive, Action<ThemeMode> previewTheme,
        string language, Action<string> previewLanguage, Action? manageS3Accounts = null, Action? manageS3Connections = null,
        Action? manageDriveAccounts = null, Action? manageDriveConnections = null,
        IMcpControlHost? mcpControl = null, Action<string>? copyToClipboard = null)
    {
        _mcpControl = mcpControl;
        _copyToClipboard = copyToClipboard ?? (_ => { });
        _manageS3Accounts = manageS3Accounts ?? (() => { });
        _manageS3Connections = manageS3Connections ?? (() => { });
        _manageDriveAccounts = manageDriveAccounts ?? (() => { });
        _manageDriveConnections = manageDriveConnections ?? (() => { });
        _store = store;
        _settings = settings;
        _paths = paths;
        _appDataRoot = appDataRoot;
        _dialogs = dialogs;
        _confirmRestart = confirmRestart;
        _isBackupActive = isBackupActive;
        _previewTheme = previewTheme;

        ConfigFolder = paths.Root;
        DefaultIgnorePatterns = string.Join(Environment.NewLine, settings.DefaultIgnorePatterns);
        CloseToTray = settings.CloseToTray;
        StartWithWindows = settings.StartWithWindows;
        AllowMcpControl = settings.AllowMcpControl;
        Theme = settings.Theme;   // already applied: previewing it again changes nothing
        _previewLanguage = previewLanguage;
        _initialLanguage = language;
        Language = language;
        _ready = true;   // from here on a chosen language is previewed
    }

    // The two segments of the theme switch; a segment that is unchecked leaves the theme as it is.
    public bool IsDarkTheme
    {
        get => Theme == ThemeMode.Dark;
        set { if (value) Theme = ThemeMode.Dark; }
    }

    public bool IsLightTheme
    {
        get => Theme == ThemeMode.Light;
        set { if (value) Theme = ThemeMode.Light; }
    }

    // The two segments of the language switch; a segment that is unchecked leaves the language as it is.
    public bool IsGerman
    {
        get => Language == AppLanguages.German;
        set { if (value) Language = AppLanguages.German; }
    }

    public bool IsEnglish
    {
        get => Language == AppLanguages.English;
        set { if (value) Language = AppLanguages.English; }
    }

    partial void OnLanguageChanged(string value)
    {
        if (_ready)
            _previewLanguage(value);
    }

    partial void OnAllowMcpControlChanged(bool value)
    {
        if (_ready && !_revertingMcp)
            McpApplyTask = ApplyMcpAsync(value, McpApplyTask);
    }

    /// <summary>Hosts or releases the pipe after the previous change; a pipe that cannot be hosted puts the switch back off.</summary>
    private async Task ApplyMcpAsync(bool enabled, Task previous)
    {
        // An earlier change that failed must not break the chain of later ones.
        try { await previous.ConfigureAwait(true); }
        catch (Exception) { }
        if (_mcpControl is null)
            return;
        string? error;
        try { error = await _mcpControl.ApplyAsync(enabled).ConfigureAwait(true); }
        catch (Exception ex) { error = ex.Message; }
        if (error is null || !enabled)
            return;

        _revertingMcp = true;
        try { AllowMcpControl = false; }
        finally { _revertingMcp = false; }
        _dialogs.ShowError(Loc.T("settings.mcp.title"), Loc.F("settings.mcp.startFailed", ("error", error)));
    }

    /// <summary>"Copy configuration": the client JSON with the path of this exe goes to the clipboard.</summary>
    [RelayCommand]
    private void CopyMcpConfiguration()
    {
        _copyToClipboard(McpClientConfiguration.Json(Environment.ProcessPath ?? "ReBackup.exe"));
        McpCopied = true;
        _ = HideCopiedAsync(++_copiedVersion);
    }

    private async Task HideCopiedAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        if (version == _copiedVersion)
            McpCopied = false;
    }

    partial void OnThemeChanged(ThemeMode value) => _previewTheme(value);

    /// <summary>The dialog closed: without a save the theme and the language go back to what they were.</summary>
    public void OnClosed()
    {
        if (!_saved && Theme != _settings.Theme)
            _previewTheme(_settings.Theme);
        if (!_saved && Language != _initialLanguage)
            _previewLanguage(_initialLanguage);
        // The pipe was switched while the dialog was open; without a save it goes back to the saved setting.
        if (!_saved && AllowMcpControl != _settings.AllowMcpControl)
            McpApplyTask = ApplyMcpAsync(_settings.AllowMcpControl, McpApplyTask);
    }

    /// <summary>Raised with the dialog result (true = saved).</summary>
    public event EventHandler<bool>? CloseRequested;

    public bool RestartRequired { get; private set; }

    [RelayCommand]
    private void ChangeConfigFolder()
    {
        if (_isBackupActive())
        {
            _dialogs.ShowInfo(Loc.T("settings.config.dialogTitle"), Loc.T("settings.config.backupRunning"));
            return;
        }

        var folder = _dialogs.PickFolder(Loc.T("settings.config.choose"), ConfigFolder);
        if (folder is null)
            return;
        if (string.Equals(PathUtil.Normalize(folder), PathUtil.Normalize(_paths.Root), StringComparison.OrdinalIgnoreCase))
            return;

        var mode = ConfigMoveMode.CopyCurrent;
        if (ConfigLocation.ContainsConfiguration(folder))
        {
            if (!_dialogs.Confirm(Loc.T("settings.config.dialogTitle"), Loc.T("settings.config.containsConfiguration")))
                return;
            mode = ConfigMoveMode.UseExisting;
        }

        if (HasOtherEdits() && !_dialogs.Confirm(Loc.T("settings.config.dialogTitle"), Loc.T("settings.config.otherEdits")))
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
            _dialogs.ShowError(Loc.T("settings.config.dialogTitle"), Loc.Known(ex.Message));
            return;
        }

        ConfigFolder = _paths.Root;
        RestartRequired = true;
        _dialogs.ShowInfo(Loc.T("settings.config.dialogTitle"), Loc.T("settings.config.restart"));
        CloseRequested?.Invoke(this, false);
    }

    private bool HasOtherEdits() =>
        CloseToTray != _settings.CloseToTray
        || Theme != _settings.Theme
        || Language != _initialLanguage
        || StartWithWindows != _settings.StartWithWindows
        || AllowMcpControl != _settings.AllowMcpControl
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
        var oldAllowMcpControl = _settings.AllowMcpControl;
        var oldTheme = _settings.Theme;
        var oldLanguage = _settings.Language;
        var registryChanged = false;

        try
        {
            AppStartup.Apply(StartWithWindows);
            registryChanged = StartWithWindows != oldStartWithWindows;

            _settings.DefaultIgnorePatterns = ParsePatterns();
            _settings.CloseToTray = CloseToTray;
            _settings.StartWithWindows = StartWithWindows;
            _settings.AllowMcpControl = AllowMcpControl;
            _settings.Theme = Theme;
            // Unchanged in this dialog: an unset language stays unset (it keeps following Windows).
            if (Language != _initialLanguage)
                _settings.Language = Language;
            _store.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _settings.DefaultIgnorePatterns = oldPatterns;
            _settings.CloseToTray = oldCloseToTray;
            _settings.StartWithWindows = oldStartWithWindows;
            _settings.AllowMcpControl = oldAllowMcpControl;
            _settings.Theme = oldTheme;
            _settings.Language = oldLanguage;
            if (registryChanged)
            {
                try { AppStartup.Apply(oldStartWithWindows); }
                catch (Exception revertEx) when (revertEx is IOException or UnauthorizedAccessException or SecurityException) { }
            }
            _dialogs.ShowError(Loc.T("settings.title"), ex.Message);
            return;
        }

        _saved = true;
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    /// <summary>"S3 accounts…": the shared accounts dialog; its changes are stored at once (not by OK).</summary>
    [RelayCommand]
    private void ManageS3Accounts() => _manageS3Accounts();

    /// <summary>"S3 connections…": the list of connections; its changes are stored at once (not by OK).</summary>
    [RelayCommand]
    private void ManageS3Connections() => _manageS3Connections();

    /// <summary>"Google accounts…": the service accounts; their changes are stored at once (not by OK).</summary>
    [RelayCommand]
    private void ManageDriveAccounts() => _manageDriveAccounts();

    /// <summary>"Drive connections…": the list of Drive connections; its changes are stored at once (not by OK).</summary>
    [RelayCommand]
    private void ManageDriveConnections() => _manageDriveConnections();
}
