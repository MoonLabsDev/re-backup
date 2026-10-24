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
using ReBackup.Shared.Wpf.Localization;
using ReBackup.App.Mcp;
using ReBackup.App.Services;
using ReBackup.Shared.Wpf.Services;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Config;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Settings;
using ReBackup.Core.Versions;
using ReBackup.Shared.Localization;
using ReBackup.Shared.Schedule;
using ReBackup.Shared.Settings;
using ReBackup.Storage;
using ReBackup.Storage.GoogleDrive;
using ReBackup.Storage.S3;
using ThemeMode = ReBackup.Shared.Settings.ThemeMode;   // not System.Windows.ThemeMode (WPF Fluent)

namespace ReBackup.App;

public partial class App : Application
{
    /// <summary>Names the single-instance mutex and event (<c>Local\ReBackup.SingleInstance</c>, <c>Local\ReBackup.Activate</c>).</summary>
    internal const string AppId = "ReBackup";

    private readonly IDialogService _dialogs;
    private S3Connections? _s3;
    private DriveConnections? _drive;
    private S3StorageFactory? _s3Storages;
    private DriveStorageFactory? _storages;
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
    private VersionIndexWorker _versionIndex = null!;
    private TempOpener _tempOpener = null!;

    public App()
    {
        _dialogs = new WpfDialogService(() => _s3?.Store, () => _drive);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // An MCP client started the exe: serve it over stdio, before the mutex, settings, tray and windows.
        if (e.Args.Contains("--mcp", StringComparer.OrdinalIgnoreCase))
        {
            RunMcpBridge(e.Args);
            return;
        }

        // Label files (later sources override earlier ones) and the recognizers of stored English texts.
        Loc.Configure(
            [
                new LabelSource(typeof(SharedTexts).Assembly, "ReBackup.Shared.Locales.shared."),
                new LabelSource(typeof(Loc).Assembly, "ReBackup.Shared.Wpf.Locales.wpf."),
                new LabelSource(typeof(App).Assembly, "ReBackup.App.Locales."),
            ],
            [CoreTexts.Recognize, SharedTexts.Recognize]);

        var restarted = e.Args.Contains("--restarted", StringComparer.OrdinalIgnoreCase);
        _singleInstance = SingleInstance.TryAcquire(AppId, restarted ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        if (_singleInstance is null)
        {
            SingleInstance.SignalRunningInstance(AppId);
            Shutdown();
            return;
        }

        // The bootstrap dialogs come before settings.json is read: they use the default language.
        Loc.Instance.Apply(AppLanguages.Default);
        _appDataRoot = ConfigLocation.DefaultAppDataRoot;
        if (!BootstrapConfiguration())
        {
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        Loc.Instance.Apply(AppLanguages.Resolve(_settings.Language));
        CleanUpInBackground();
        // Deletions the app did not finish last time (it was quit, crashed, or the target dropped) go on in the background.
        _ = _mainViewModel.ResumeDeletionsAsync();

        // The Run entry follows the exe that is running now (moved or updated installs would leave a stale path).
        try
        {
            AppStartup.RefreshOnStart(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            // Autostart is a convenience; a locked registry must not stop the app.
        }

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

    private async void RunMcpBridge(string[] args)
    {
        int exitCode;
        try
        {
            exitCode = await McpBridge.RunAsync(args, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // stderr, never stdout: stdout carries the protocol.
            Console.Error.WriteLine(ex);
            exitCode = 1;
        }
        Shutdown(exitCode);
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
            // One storage factory for everything the app opens (runs, versions, previews, restores, deletions): folders,
            // S3 locations on the connections in connections.json and Google Drive locations on the connections in
            // drive-connections.json of this configuration folder. An unreadable file (or a Google key that cannot be read
            // on this Windows account) surfaces as a StorageIOException with the store's reason (the run ends as error,
            // nothing is deleted).
            var s3 = new S3Connections(paths.ConnectionsFile);
            var drive = new DriveConnections(paths.DriveConnectionsFile);
            var s3Storages = new S3StorageFactory(new StorageFactory(), s3.Resolve);
            var storages = new DriveStorageFactory(s3Storages, drive.Resolve);
            var runner = new BackupRunner(storages, currentRules: planId => planStore.TryLoad(planId)?.Retention,
                indexSink: versionIndex);
            var queue = new BackupQueue(runner, planId => new RunLog(paths.LogFileFor(planId)));
            // Called on a timer thread: InvokeAsync, never Invoke — the UI thread may be waiting for the queue.
            var scheduler = new Scheduler((planId, trigger) =>
                Dispatcher.InvokeAsync(() => created?.RunScheduled(planId, trigger)));
            var themeToggle = new ThemeToggleViewModel(() => ThemeManager.Mode, ChooseTheme);
            var languageToggle = new LanguageToggleViewModel(() => Loc.Instance.Language, ChooseLanguage);
            // Files and folders of versions that cannot be opened in place go through %TEMP%\ReBackup\open.
            var tempOpener = new TempOpener();
            var mainViewModel = new MainViewModel(planStore, paths, settings, () => settingsStore.Save(settings),
                () => settingsStore.LastLoadError, _dialogs, ShowSettings, queue, scheduler,
                action => Dispatcher.InvokeAsync(action), themeToggle, languageToggle, new ExplorerFolderOpener(),
                versionIndex, storages, s3, drive, tempOpener, new ShellRecycleBin());
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
            _s3 = s3;
            _drive = drive;
            _s3Storages = s3Storages;
            _storages = storages;
            _versionIndex = versionIndex;
            _tempOpener = tempOpener;
        }
        catch
        {
            planStore.Dispose();
            throw;
        }

        if (_settingsStore.LastLoadError is { } error)
            _dialogs.ShowError(Loc.T("settings.title"), Loc.F("app.settingsUnreadable", ("error", error)));
    }

    /// <summary>
    /// On start, off the UI thread: deletes temporary copies of opened files older than a day, and the version indexes
    /// of plans that no longer exist (a plan file that could not be read keeps its index).
    /// </summary>
    private void CleanUpInBackground()
    {
        var planIds = new HashSet<string>(_mainViewModel.Plans.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        var plansDirectory = _paths.PlansDirectory;
        var indexes = _versionIndex.Indexes;
        var tempOpener = _tempOpener;
        _ = Task.Run(() =>
        {
            tempOpener.CleanUp(TimeSpan.FromDays(1));
            try
            {
                foreach (var file in Directory.EnumerateFiles(plansDirectory, "*.json"))
                    planIds.Add(Path.GetFileNameWithoutExtension(file));
                indexes.PruneOrphans(planIds);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                Trace.TraceWarning($"The version indexes of deleted plans could not be removed: {ex.Message}");
            }
        });
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
            Id = TrayIconId.ForCurrentProcess("ReBackup"),
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
        _mainViewModel.ControlNotified += ShowControlNotification;
    }

    /// <summary>Claude changed something through the control pipe (MCP): the user sees it at once.</summary>
    private void ShowControlNotification(string title, string message)
    {
        if (_exitRequested || _tray is null)
            return;
        try
        {
            _tray.ShowNotification(title, message, NotificationIcon.Info);
        }
        catch (Exception)
        {
            // A tray failure must never take the app down.
        }
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
    /// one to clean up its unfinished version folder. False when it did not stop in time.
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
            () => ConfirmDiscardUnsaved() && ConfirmStopRestores(exit: false) && ConfirmStopPoolJobs(exit: false),
            () => _queue.IsBusy, ThemeManager.Apply, Loc.Instance.Language, Loc.Instance.Apply,
            manageS3Accounts: () =>
            {
                _dialogs.ManageS3Accounts();
                _s3?.Reload();   // an account's secret entered again: the plans validate again
            },
            manageS3Connections: ShowS3Connections,
            manageDriveAccounts: ShowDriveAccounts,
            manageDriveConnections: ShowDriveConnections);
        var window = new SettingsWindow(viewModel);
        if (_window.IsVisible)
            window.Owner = _window;
        window.ShowDialog();

        if (viewModel.RestartRequired)
            Restart();
        else
            _mainViewModel.ReevaluatePreviews();
    }

    /// <summary>Settings → "S3 connections…": the list over the settings window; its changes are stored at once.</summary>
    private void ShowS3Connections()
    {
        if (_s3 is null)
            return;
        WpfDialogService.ShowOwned(new S3ConnectionsWindow(
            new S3ConnectionsViewModel(_s3, _dialogs, _mainViewModel.PlansUsingConnection)));
    }

    /// <summary>Settings → "Google accounts…": the list over the settings window; its changes are stored at once.</summary>
    private void ShowDriveAccounts()
    {
        if (_drive is null)
            return;
        WpfDialogService.ShowOwned(new DriveAccountsWindow(new DriveAccountsViewModel(_drive, _dialogs)));
    }

    /// <summary>Settings → "Drive connections…": the list over the settings window; its changes are stored at once.</summary>
    private void ShowDriveConnections()
    {
        if (_drive is null)
            return;
        WpfDialogService.ShowOwned(new DriveConnectionsWindow(
            new DriveConnectionsViewModel(_drive, _dialogs, _mainViewModel.PlansUsingDriveConnection)));
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
        StopPoolJobs();
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
        _mainViewModel?.StopPoolJobs(TimeSpan.FromSeconds(3));
        if (_queue is not null)
            StopBackups(TimeSpan.FromSeconds(3));
        DisposeTray();
        _planStore?.Dispose();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        // The Drive and S3 clients; backups and restores were stopped before the app shut down.
        _storages?.Dispose();
        _s3Storages?.Dispose();
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

    /// <summary>True when no copy or verify runs, or the user agrees to stop the running ones to exit (or restart) ReBackup.</summary>
    private bool ConfirmStopPoolJobs(bool exit)
    {
        if (!_mainViewModel.IsAnyPoolJobRunning)
            return true;
        if (!_window.IsVisible)
            ShowMainWindow();
        return _dialogs.ConfirmDefaultNo(Loc.T("app.poolJobRunning.title"),
            exit ? Loc.T("app.poolJobRunning.exit") : Loc.T("app.poolJobRunning.restart"));
    }

    /// <summary>
    /// Stops running copies and verifies and waits (bounded) until they have released their locks, so that the process
    /// does not end with a lock or a half-written version on the target.
    /// </summary>
    private void StopPoolJobs()
    {
        if (!_mainViewModel.StopPoolJobs(TimeSpan.FromSeconds(10)))
            _dialogs.ShowInfo("ReBackup", Loc.T("app.poolJobStuck"));
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

        if (!ConfirmStopRestores(exit: true) || !ConfirmStopPoolJobs(exit: true))
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
        StopPoolJobs();
        StopBackups(backupBusy ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);

        _exitRequested = true;
        DisposeTray();
        _planStore.Dispose();
        Shutdown();
    }
}
