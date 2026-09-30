using System.ComponentModel;
using System.IO;
using System.Windows;
using ReBackup.App.Services;
using ReBackup.App.ViewModels;
using ReBackup.Core.Config;
using ReBackup.Core.Plans;
using ReBackup.Core.Settings;

namespace ReBackup.App;

public partial class App : Application
{
    private readonly IDialogService _dialogs = new WpfDialogService();
    private string _appDataRoot = "";
    private ConfigPaths _paths = null!;
    private SettingsStore _settingsStore = null!;
    private AppSettings _settings = null!;
    private PlanStore _planStore = null!;
    private MainViewModel _mainViewModel = null!;
    private MainWindow _window = null!;
    private bool _exitRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _appDataRoot = ConfigLocation.DefaultAppDataRoot;
        _paths = ConfigLocation.Resolve(_appDataRoot);
        Directory.CreateDirectory(_paths.PlansDirectory);
        Directory.CreateDirectory(_paths.LogsDirectory);

        _settingsStore = new SettingsStore(_paths.SettingsFile);
        _settings = _settingsStore.Load();
        if (_settingsStore.LastLoadError is { } error)
            _dialogs.ShowError("Settings", $"settings.json could not be read, defaults are used.\n\n{error}");

        _planStore = new PlanStore(_paths.PlansDirectory);
        _mainViewModel = new MainViewModel(_planStore, _paths, _dialogs, ShowSettings);
        _planStore.ExternalChange += (_, _) => Dispatcher.InvokeAsync(_mainViewModel.ReloadFromDisk);
        _planStore.StartWatching();

        _window = new MainWindow { DataContext = _mainViewModel };
        _window.Closing += OnMainWindowClosing;
        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ShowSettings()
    {
        // Implemented in Task 8.
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested)
            return;

        e.Cancel = true;
        ExitApp();
    }

    private bool ConfirmDiscardUnsaved()
    {
        if (!_mainViewModel.HasUnsavedChanges)
            return true;
        return _dialogs.Confirm("Unsaved changes",
            "These plans have unsaved changes:\n\n" + string.Join("\n", _mainViewModel.UnsavedPlanNames) +
            "\n\nDiscard the changes?");
    }

    private void ExitApp()
    {
        if (!ConfirmDiscardUnsaved())
            return;

        _exitRequested = true;
        _planStore.Dispose();
        Shutdown();
    }
}
