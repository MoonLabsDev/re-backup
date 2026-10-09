using System.IO;
using ReBackup.App.Services;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Config;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Settings;
using ReBackup.Core.Versions;
using ReBackup.Shared.Localization;
using ReBackup.Shared.Schedule;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Settings;
using ReBackup.Storage;

namespace ReBackup.App.Tests.TestSupport;

/// <summary>A <see cref="MainViewModel"/> on a temporary configuration folder; the scheduler is never started.</summary>
public sealed class MainViewModelFixture : IDisposable
{
    private static readonly object LocLock = new();
    private static bool _locConfigured;

    public MainViewModelFixture()
    {
        ConfigureLabels();
        Root = Path.Combine(Path.GetTempPath(), "rebackup-app-tests", Guid.NewGuid().ToString("N"));
        Paths = new ConfigPaths(Root);
        Directory.CreateDirectory(Paths.PlansDirectory);
        Directory.CreateDirectory(Paths.LogsDirectory);
        Store = new PlanStore(Paths.PlansDirectory);
        SettingsStore = new SettingsStore(Paths.SettingsFile);
    }

    public string Root { get; }

    public ConfigPaths Paths { get; }

    public PlanStore Store { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; } = new();

    public FakeDialogs Dialogs { get; } = new();

    /// <summary>How often the view model saved the settings.</summary>
    public int SettingsSaves { get; private set; }

    /// <summary>Thrown by the settings save when set.</summary>
    public Exception? SaveFailure { get; set; }

    /// <summary>Why settings.json could not be read on startup; null when it was fine.</summary>
    public string? LoadError { get; set; }

    public BackupPlan AddPlan(string id, string name)
    {
        var plan = new BackupPlan { Id = id, Name = name };
        Store.Save(plan);
        return plan;
    }

    public MainViewModel Create()
    {
        var storages = new StorageFactory();
        var queue = new BackupQueue(new BackupRunner(storages), planId => new RunLog(Paths.LogFileFor(planId)));
        var scheduler = new Scheduler((_, _) => { });
        var versionIndex = new VersionIndexWorker(new VersionIndexSet(Path.Combine(Root, "index")));
        return new MainViewModel(Store, Paths, Settings, SaveSettings, () => LoadError, Dialogs, () => { }, queue, scheduler,
            action => action(), new ThemeToggleViewModel(() => ThemeMode.Dark, _ => { }),
            new LanguageToggleViewModel(() => "en-US", _ => { }), new FakeFolderOpener(), versionIndex, storages);
    }

    /// <summary>The English labels, as the app registers them, so texts in the tests read as in the app.</summary>
    private static void ConfigureLabels()
    {
        lock (LocLock)
        {
            if (_locConfigured)
                return;
            Loc.Configure(
                [
                    new LabelSource(typeof(SharedTexts).Assembly, "ReBackup.Shared.Locales.shared."),
                    new LabelSource(typeof(Loc).Assembly, "ReBackup.Shared.Wpf.Locales.wpf."),
                    new LabelSource(typeof(App).Assembly, "ReBackup.App.Locales."),
                ],
                [CoreTexts.Recognize, SharedTexts.Recognize]);
            _locConfigured = true;
        }
    }

    private void SaveSettings()
    {
        if (SaveFailure is not null)
            throw SaveFailure;
        SettingsSaves++;
        SettingsStore.Save(Settings);
    }

    public void Dispose()
    {
        Store.Dispose();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class FakeDialogs : IDialogService
{
    public bool ConfirmAnswer { get; set; } = true;

    public bool? YesNoCancelAnswer { get; set; } = false;

    public List<string> Errors { get; } = [];

    public bool Confirm(string title, string message) => ConfirmAnswer;

    public bool ConfirmDefaultNo(string title, string message) => ConfirmAnswer;

    public bool? AskYesNoCancel(string title, string message) => YesNoCancelAnswer;

    public string? PickFolder(string title, string? initialFolder) => null;

    public void ShowError(string title, string message) => Errors.Add(message);

    public void ShowInfo(string title, string message) { }

    public ConflictPolicy? AskConflictPolicy(string title, string message, IReadOnlyList<string>? details = null) => null;

    public void ShowFailures(string title, string message, IReadOnlyList<string> lines) { }

    public bool ConfirmFailures(string title, string message, IReadOnlyList<string> lines, string confirmText) => false;
}

public sealed class FakeFolderOpener : IFolderOpener
{
    public Task<bool> OpenVersionFolderAsync(string? target, string? versionName) => Task.FromResult(false);

    public bool OpenFile(string versionFolder, string relativePath) => false;

    public bool ShowInExplorer(string versionFolder, string relativePath) => false;
}
