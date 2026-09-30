using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using ReBackup.App.Services;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
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
    private BackupQueue _queue = null!;
    private MainViewModel _mainViewModel = null!;
    private MainWindow _window = null!;
    private bool _exitRequested;
    private bool _exiting;
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
            var queue = new BackupQueue(new BackupRunner(), planId => new RunLog(paths.LogFileFor(planId)));
            var mainViewModel = new MainViewModel(planStore, paths, settings, _dialogs, ShowSettings, queue,
                action => Dispatcher.InvokeAsync(action));
            planStore.ExternalChange += (_, _) => Dispatcher.InvokeAsync(mainViewModel.ReloadFromDisk);
            planStore.StartWatching();

            _paths = paths;
            _settingsStore = settingsStore;
            _settings = settings;
            _planStore = planStore;
            _mainViewModel = mainViewModel;
            _queue = queue;
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
        var runMenu = new MenuItem { Header = "Run plan" };
        _trayMenu.Items.Add(CreateTrayMenuItem("Open", ShowMainWindow));
        _trayMenu.Items.Add(runMenu);
        _trayMenu.Items.Add(new Separator());
        _trayMenu.Items.Add(CreateTrayMenuItem("Exit", ExitApp));
        _trayMenu.Opened += (_, _) => FillRunMenu(runMenu);
        FillRunMenu(runMenu);

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

        _mainViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.QueueStatus) || _exitRequested || _tray is null)
                return;
            try
            {
                var text = "ReBackup — " + _mainViewModel.QueueStatus;
                _tray.ToolTipText = text.Length > 120 ? text[..119] + "…" : text;
            }
            catch (Exception)
            {
                // A tray failure must never take the app down.
            }
        };
        _mainViewModel.RunFinished += ShowRunNotification;
    }

    private void ShowRunNotification(string planName, RunLogEntry result)
    {
        if (_exitRequested || _tray is null)
            return;

        var duration = RunHistoryRow.FormatDuration(result.DurationMs);
        var (icon, message) = result.Status switch
        {
            RunStatus.Completed => (NotificationIcon.Info, $"Completed in {duration}."),
            RunStatus.CompletedWithWarnings =>
                (NotificationIcon.Warning, $"Completed in {duration}, {result.SkippedCount:N0} entries were skipped."),
            RunStatus.Canceled => (NotificationIcon.Info, "Canceled."),
            RunStatus.Full => (NotificationIcon.Error, $"Aborted, the target is full. {result.Reason}"),
            _ => (NotificationIcon.Error, $"Aborted with an error. {result.Reason}"),
        };
        try
        {
            _tray.ShowNotification($"Backup \"{planName}\"", message, icon);
        }
        catch (Exception)
        {
            // A tray failure must never take the app down.
        }
    }

    private void FillRunMenu(MenuItem runMenu)
    {
        try
        {
            runMenu.Items.Clear();
            foreach (var (id, name, canRun) in _mainViewModel.RunnablePlans)
            {
                // A single underscore would be taken as an access key.
                var item = CreateTrayMenuItem(name.Replace("_", "__"), () => _mainViewModel.RunPlan(id));
                item.IsEnabled = canRun;
                runMenu.Items.Add(item);
            }
            runMenu.IsEnabled = runMenu.Items.Count > 0;
        }
        catch (Exception)
        {
            runMenu.IsEnabled = false;
        }
    }

    /// <summary>Cancels queued and running backups and waits briefly for the running one to clean up its partial folder.</summary>
    private bool StopBackups(TimeSpan wait)
    {
        _queue.CancelAll();
        try
        {
            return _queue.WhenIdleAsync().Wait(wait);
        }
        catch (AggregateException)
        {
            // The worker never faults; this only guards the wait itself.
            return false;
        }
    }

    private void DisposeTray()
    {
        var tray = _tray;
        _tray = null;
        tray?.Dispose();
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
        if (!StopBackups(TimeSpan.FromSeconds(15)))
        {
            // Keep the lock: a second copy must not run next to a backup that is still stopping.
            DisposeTray();
            _planStore.Dispose();
            _dialogs.ShowInfo("ReBackup", "A running backup could not be stopped in time. Please start ReBackup again manually.");
            Shutdown();
            return;
        }

        DisposeTray();
        _planStore.Dispose();
        _singleInstance?.Dispose();   // the new process needs the lock; released only after the backups stopped
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
        _exitRequested = true;
        if (_queue is not null)
            StopBackups(TimeSpan.FromSeconds(3));
        DisposeTray();
        _planStore?.Dispose();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
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
        if (_exiting)
            return;
        _exiting = true;

        if (_mainViewModel.HasUnsavedChanges && !_window.IsVisible)
            ShowMainWindow();
        if (!ConfirmDiscardUnsaved())
        {
            _exiting = false;
            return;
        }

        if (_queue.IsBusy)
        {
            if (!_dialogs.Confirm("Backup in progress",
                    "A backup is running or queued.\n\nCancel it and exit ReBackup?"))
            {
                _exiting = false;
                return;
            }
            StopBackups(TimeSpan.FromSeconds(15));
        }

        _exitRequested = true;
        DisposeTray();
        _planStore.Dispose();
        Shutdown();
    }
}
