using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Security;
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
    private SingleInstance? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var restarted = e.Args.Contains("--restarted", StringComparer.OrdinalIgnoreCase);
        _singleInstance = SingleInstance.TryAcquire(restarted ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        if (_singleInstance is null)
        {
            SingleInstance.SignalRunningInstance();
            Shutdown();
            return;
        }

        _appDataRoot = ConfigLocation.DefaultAppDataRoot;
        if (!BootstrapConfiguration())
        {
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        _window = new MainWindow { DataContext = _mainViewModel };
        _window.Closing += OnMainWindowClosing;
        CreateTrayIcon();
        _singleInstance.ListenForActivation(() => Dispatcher.InvokeAsync(ShowMainWindow));
        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
            ShowMainWindow();
    }

    /// <summary>Resolves the configuration folder and loads everything that lives in it; false means the app must exit.</summary>
    private bool BootstrapConfiguration()
    {
        while (true)
        {
            var paths = ConfigLocation.Resolve(_appDataRoot);
            try
            {
                LoadConfiguration(paths);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                var choice = _dialogs.AskYesNoCancel("Configuration folder",
                    $"The configuration folder {paths.Root} is not reachable:\n\n{ex.Message}\n\n" +
                    "Yes = Retry, No = Use the default location for this session, Cancel = Exit");
                if (choice is null)
                    return false;
                if (choice == true)
                    continue;

                try
                {
                    LoadConfiguration(new ConfigPaths(_appDataRoot));
                    return true;
                }
                catch (Exception fallbackEx) when (fallbackEx is IOException or UnauthorizedAccessException or SecurityException)
                {
                    _dialogs.ShowError("Configuration folder",
                        $"The default location {_appDataRoot} is not usable either:\n\n{fallbackEx.Message}");
                    return false;
                }
            }
        }
    }

    private void LoadConfiguration(ConfigPaths paths)
    {
        Directory.CreateDirectory(paths.PlansDirectory);
        Directory.CreateDirectory(paths.LogsDirectory);

        var settingsStore = new SettingsStore(paths.SettingsFile);
        var settings = settingsStore.Load();
        var planStore = new PlanStore(paths.PlansDirectory);
        try
        {
            var mainViewModel = new MainViewModel(planStore, paths, settings, _dialogs, ShowSettings);
            planStore.ExternalChange += (_, _) => Dispatcher.InvokeAsync(mainViewModel.ReloadFromDisk);
            planStore.StartWatching();

            _paths = paths;
            _settingsStore = settingsStore;
            _settings = settings;
            _planStore = planStore;
            _mainViewModel = mainViewModel;
        }
        catch
        {
            planStore.Dispose();
            throw;
        }

        if (_settingsStore.LastLoadError is { } error)
            _dialogs.ShowError("Settings", $"settings.json could not be read, defaults are used.\n\n{error}");
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
        var viewModel = new SettingsViewModel(_settingsStore, _settings, _paths, _appDataRoot, _dialogs, ConfirmDiscardUnsaved);
        var window = new SettingsWindow(viewModel);
        if (_window.IsVisible)
            window.Owner = _window;
        window.ShowDialog();

        if (viewModel.RestartRequired)
            Restart();
        else
            _mainViewModel.ReevaluatePreviews();
    }

    private void Restart()
    {
        _exitRequested = true;
        _tray?.Dispose();
        _planStore.Dispose();
        _singleInstance?.Dispose();
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restarted") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _dialogs.ShowInfo("ReBackup", "ReBackup could not restart itself. Please start it again manually.");
        }
        Shutdown();
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
        _singleInstance?.Dispose();
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
        _singleInstance?.Dispose();
        Shutdown();
    }
}
