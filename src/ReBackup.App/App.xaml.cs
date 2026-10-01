using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Win32;
using ReBackup.App.Localization;
using ReBackup.App.Services;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Config;
using ReBackup.Core.Plans;
using ReBackup.Core.Schedule;
using ReBackup.Core.Settings;
using ReBackup.Core.Versions;
using ThemeMode = ReBackup.Core.Settings.ThemeMode;   // not System.Windows.ThemeMode (WPF Fluent)

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
    private Scheduler _scheduler = null!;
    private MainViewModel _mainViewModel = null!;
    private MainWindow _window = null!;
    private bool _exitRequested;
    private bool _exiting;
    private TaskbarIcon? _tray;
    private readonly ContextMenu _trayMenu = new();
    private SingleInstance? _singleInstance;
    private ThemeToggleViewModel _themeToggle = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Read before ReBackup sets its own culture: without a saved choice the language follows Windows.
        var windowsLanguage = CultureInfo.CurrentUICulture;

        var restarted = e.Args.Contains("--restarted", StringComparer.OrdinalIgnoreCase);
        _singleInstance = SingleInstance.TryAcquire(restarted ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        if (_singleInstance is null)
        {
            SingleInstance.SignalRunningInstance();
            Shutdown();
            return;
        }

        // The bootstrap dialogs come before settings.json is read: they use Windows' language.
        Loc.Instance.Apply(AppLanguages.DefaultFor(windowsLanguage));
        _appDataRoot = ConfigLocation.DefaultAppDataRoot;
        if (!BootstrapConfiguration())
        {
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        Loc.Instance.Apply(AppLanguages.Resolve(_settings.Language, windowsLanguage));

        // Before the first window exists, so a light theme never flashes dark.
        ThemeManager.ThemeChanged += OnThemeChanged;
        ThemeManager.Apply(_settings.Theme);

        _window = new MainWindow { DataContext = _mainViewModel };
        _window.Closing += OnMainWindowClosing;
        CreateTrayIcon();
        _scheduler.Start(planId => new RunLog(_paths.LogFileFor(planId)).ReadLast()?.StartUtc);
        SystemEvents.TimeChanged += OnSystemTimeChanged;
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
                var choice = _dialogs.AskYesNoCancel(Loc.T("app.config.title"),
                    Loc.F("app.config.unreachable", ("path", paths.Root), ("error", ex.Message)));
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
                    _dialogs.ShowError(Loc.T("app.config.title"),
                        Loc.F("app.config.defaultUnusable", ("path", _appDataRoot), ("error", fallbackEx.Message)));
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
            MainViewModel? created = null;
            // One SQLite index per plan in %LOCALAPPDATA%\ReBackup\index. A finished run queues its version there and
            // never waits for it (a sync of a network target can hold the index for minutes); a failure is shown in
            // the footer and the next sync imports the version from its folder.
            var versionIndex = new VersionIndexWorker(new VersionIndexSet(VersionIndexSet.DefaultDirectory),
                (planId, ex) => Dispatcher.InvokeAsync(() => created?.ReportIndexError(planId, ex.Message)));
            // Versions are deleted by the rules as saved at that moment, not as they were when the run was queued.
            var runner = new BackupRunner(currentRules: planId => planStore.TryLoad(planId)?.Retention,
                indexSink: versionIndex);
            var queue = new BackupQueue(runner, planId => new RunLog(paths.LogFileFor(planId)));
            // Called on a timer thread: InvokeAsync, never Invoke — the UI thread may be waiting for the queue.
            var scheduler = new Scheduler((planId, trigger) =>
                Dispatcher.InvokeAsync(() => created?.RunScheduled(planId, trigger)));
            var themeToggle = new ThemeToggleViewModel(() => ThemeManager.Mode, ChooseTheme);
            var languageToggle = new LanguageToggleViewModel(() => Loc.Instance.Language, ChooseLanguage);
            var mainViewModel = new MainViewModel(planStore, paths, settings, _dialogs, ShowSettings, queue, scheduler,
                action => Dispatcher.InvokeAsync(action), themeToggle, languageToggle, new ExplorerFolderOpener(), versionIndex);
            created = mainViewModel;
            scheduler.Changed += () => Dispatcher.InvokeAsync(mainViewModel.RefreshSchedule);
            planStore.ExternalChange += (_, _) => Dispatcher.InvokeAsync(mainViewModel.ReloadFromDisk);
            planStore.StartWatching();

            _paths = paths;
            _settingsStore = settingsStore;
            _settings = settings;
            _planStore = planStore;
            _mainViewModel = mainViewModel;
            _themeToggle = themeToggle;
            _queue = queue;
            _scheduler = scheduler;
        }
        catch
        {
            planStore.Dispose();
            throw;
        }

        if (_settingsStore.LastLoadError is { } error)
            _dialogs.ShowError(Loc.T("settings.title"), Loc.F("app.settingsUnreadable", ("error", error)));
    }

    private void CreateTrayIcon()
    {
        var runMenu = new MenuItem();
        Loc.Bind(runMenu, HeaderedItemsControl.HeaderProperty, "tray.runPlan");
        _trayMenu.Items.Add(CreateLabeledTrayMenuItem("tray.open", ShowMainWindow));
        _trayMenu.Items.Add(runMenu);
        var pauseItem = CreateTrayMenuItem(Loc.T("tray.pause"), () => _scheduler.IsPaused = !_scheduler.IsPaused);
        _trayMenu.Items.Add(pauseItem);
        _trayMenu.Opened += (_, _) =>
            pauseItem.Header = _scheduler.IsPaused ? Loc.T("tray.resume") : Loc.T("tray.pause");
        _trayMenu.Items.Add(new Separator());
        _trayMenu.Items.Add(CreateLabeledTrayMenuItem("tray.exit", ExitApp));
        ThemeManager.Follow(_trayMenu);   // the menu is in no window, so it needs the palette in its own resources
        _trayMenu.Opened += (_, _) => FillRunMenu(runMenu);
        FillRunMenu(runMenu);

        _tray = new TaskbarIcon
        {
            ToolTipText = "ReBackup",
            ContextMenu = _trayMenu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(ShowMainWindow),
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/rebackup.ico")),
        };
        // Efficiency mode would throttle the process while hidden, which would slow scheduled backups.
        _tray.ForceCreate(enablesEfficiencyMode: false);

        _mainViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(MainViewModel.QueueStatus) or nameof(MainViewModel.SchedulerStatus))
                || _exitRequested || _tray is null)
                return;
            try
            {
                var text = "ReBackup — " + _mainViewModel.QueueStatus +
                           (_mainViewModel.SchedulerStatus.Length > 0 ? " · " + _mainViewModel.SchedulerStatus : "");
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
        var reason = Loc.Known(result.Reason);
        var (icon, message) = result.Status switch
        {
            RunStatus.Completed => (NotificationIcon.Info, Loc.F("tray.notification.completed", ("duration", duration))),
            RunStatus.CompletedWithWarnings => (NotificationIcon.Warning,
                Loc.F("tray.notification.completedSkipped", ("duration", duration), ("count", result.SkippedCount))),
            RunStatus.Canceled => (NotificationIcon.Info, Loc.F("tray.notification.canceled", ("duration", duration))),
            RunStatus.Full => (NotificationIcon.Error,
                Loc.F("tray.notification.full", ("duration", duration), ("reason", reason)).TrimEnd()),
            _ => (NotificationIcon.Error, Loc.F("tray.notification.error", ("duration", duration), ("reason", reason)).TrimEnd()),
        };
        try
        {
            _tray.ShowNotification(Loc.F("tray.notification.title", ("plan", planName)), message, icon);
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

    /// <summary>
    /// Stops the scheduler, closes the queue (canceling queued and running backups) and waits briefly for the running
    /// one to clean up its partial folder. False when it did not stop in time.
    /// </summary>
    private bool StopBackups(TimeSpan wait)
    {
        SystemEvents.TimeChanged -= OnSystemTimeChanged;
        _scheduler?.Dispose();
        _queue.Close();
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

    /// <summary>
    /// The clock or the time zone changed. TimeZoneInfo.Local is cached until cleared; the scheduler reads it on every
    /// check, the "next run" texts are refreshed now. Raised on a system events thread.
    /// </summary>
    private void OnSystemTimeChanged(object? sender, EventArgs e)
    {
        TimeZoneInfo.ClearCachedData();
        Dispatcher.InvokeAsync(() => _mainViewModel.RefreshSchedule());
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

    /// <summary>A tray menu item whose header is the label <paramref name="key"/> (it follows language switches).</summary>
    private static MenuItem CreateLabeledTrayMenuItem(string key, Action action)
    {
        var item = new MenuItem();
        Loc.Bind(item, HeaderedItemsControl.HeaderProperty, key);
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
        var viewModel = new SettingsViewModel(_settingsStore, _settings, _paths, _appDataRoot, _dialogs,
            () => ConfirmDiscardUnsaved() && ConfirmStopRestores(exit: false),
            () => _queue.IsBusy, ThemeManager.Apply, Loc.Instance.Language, Loc.Instance.Apply);
        var window = new SettingsWindow(viewModel);
        if (_window.IsVisible)
            window.Owner = _window;
        window.ShowDialog();

        if (viewModel.RestartRequired)
            Restart();
        else
            _mainViewModel.ReevaluatePreviews();
    }

    /// <summary>The rail's theme button: applies the mode at once and saves it right away.</summary>
    private void ChooseTheme(ThemeMode mode)
    {
        ThemeManager.Apply(mode);
        if (_settings.Theme == mode)
            return;

        _settings.Theme = mode;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Kept for this session; the error says it is not saved.
            _dialogs.ShowError(Loc.T("settings.title"), Loc.F("app.themeNotSaved", ("error", ex.Message)));
        }
    }

    /// <summary>The rail's language menu: applies the language at once and saves it right away.</summary>
    private void ChooseLanguage(string language)
    {
        Loc.Instance.Apply(language);
        if (_settings.Language == language)
            return;

        _settings.Language = language;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Kept for this session; the error says it is not saved.
            _dialogs.ShowError(Loc.T("settings.title"), Loc.F("language.notSaved", ("error", ex.Message)));
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => _themeToggle.Refresh();

    private void Restart()
    {
        _exitRequested = true;
        StopRestores();
        if (!StopBackups(TimeSpan.FromSeconds(15)))
        {
            // Keep the lock: a second copy must not run next to a backup that is still stopping.
            DisposeTray();
            _planStore.Dispose();
            _dialogs.ShowInfo("ReBackup", Loc.T("app.restartStuck"));
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
            _dialogs.ShowInfo("ReBackup", Loc.T("app.restartFailed"));
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
        _mainViewModel?.StopRestores(TimeSpan.FromSeconds(3));
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
        return _dialogs.Confirm(Loc.T("app.unsaved.title"),
            Loc.F("app.unsaved.message", ("plans", string.Join("\n", _mainViewModel.UnsavedPlanNames))));
    }

    /// <summary>True when no restore runs, or the user agrees to cancel the running ones to exit (or restart) ReBackup.</summary>
    private bool ConfirmStopRestores(bool exit)
    {
        if (!_mainViewModel.IsAnyRestoring)
            return true;
        if (!_window.IsVisible)
            ShowMainWindow();
        return _dialogs.ConfirmDefaultNo(Loc.T("app.restoreRunning.title"),
            exit ? Loc.T("app.restoreRunning.exit") : Loc.T("app.restoreRunning.restart"));
    }

    /// <summary>
    /// Cancels running restores and waits (bounded) until each has finished its current file and removed its temp
    /// file, so the process does not end in the middle of writing into a live folder.
    /// </summary>
    private void StopRestores()
    {
        if (!_mainViewModel.StopRestores(TimeSpan.FromSeconds(10)))
            _dialogs.ShowInfo("ReBackup", Loc.T("app.restoreStuck"));
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

        if (!ConfirmStopRestores(exit: true))
        {
            _exiting = false;
            return;
        }
        var backupBusy = _queue.IsBusy;
        if (backupBusy && !_dialogs.Confirm(Loc.T("app.backupRunning.title"), Loc.T("app.backupRunning.message")))
        {
            _exiting = false;
            return;
        }

        StopRestores();
        StopBackups(backupBusy ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);

        _exitRequested = true;
        DisposeTray();
        _planStore.Dispose();
        Shutdown();
    }
}
