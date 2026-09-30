using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
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
    private TaskbarIcon? _tray;
    private readonly ContextMenu _trayMenu = new();

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
        CreateTrayIcon();
        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
            ShowMainWindow();
    }

    private void CreateTrayIcon()
    {
        _trayMenu.Items.Add(CreateTrayMenuItem("Open", ShowMainWindow));
        _trayMenu.Items.Add(new Separator());
        _trayMenu.Items.Add(CreateTrayMenuItem("Exit", ExitApp));

        _tray = new TaskbarIcon
        {
            ToolTipText = "ReBackup",
            ContextMenu = _trayMenu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(ShowMainWindow),
            IconSource = new GeneratedIconSource
            {
                Text = "R",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xB0)),
                FontWeight = FontWeights.Bold,
            },
        };
        // Efficiency mode would throttle the process while hidden, which would slow scheduled backups.
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    internal static MenuItem CreateTrayMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
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
        if (_settings.CloseToTray)
            _window.Hide();
        else
            ExitApp();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        _exitRequested = true;
        _tray?.Dispose();
        _planStore.Dispose();
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
        if (_mainViewModel.HasUnsavedChanges && !_window.IsVisible)
            ShowMainWindow();
        if (!ConfirmDiscardUnsaved())
            return;

        _exitRequested = true;
        _tray?.Dispose();
        _planStore.Dispose();
        Shutdown();
    }
}
