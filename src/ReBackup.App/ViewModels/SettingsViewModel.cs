using System.IO;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Localization;
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
    private readonly Action<ThemeMode> _previewTheme;
    private readonly Action<string> _previewLanguage;
    private readonly string _initialLanguage;
    private readonly bool _ready;
    private bool _saved;
    private ConfigPaths _paths;

    [ObservableProperty] private string _configFolder = "";
    [ObservableProperty] private string _defaultIgnorePatterns = "";
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _startWithWindows;

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
        string language, Action<string> previewLanguage)
    {
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

    partial void OnThemeChanged(ThemeMode value) => _previewTheme(value);

    /// <summary>The dialog closed: without a save the theme and the language go back to what they were.</summary>
    public void OnClosed()
    {
        if (!_saved && Theme != _settings.Theme)
            _previewTheme(_settings.Theme);
        if (!_saved && Language != _initialLanguage)
            _previewLanguage(_initialLanguage);
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
        var oldTheme = _settings.Theme;
        var oldLanguage = _settings.Language;
        var registryChanged = false;

        try
        {
            StartupRegistration.Apply(StartWithWindows);
            registryChanged = StartWithWindows != oldStartWithWindows;

            _settings.DefaultIgnorePatterns = ParsePatterns();
            _settings.CloseToTray = CloseToTray;
            _settings.StartWithWindows = StartWithWindows;
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
            _settings.Theme = oldTheme;
            _settings.Language = oldLanguage;
            if (registryChanged)
            {
                try { StartupRegistration.Apply(oldStartWithWindows); }
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
}
