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
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

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
        S3 = new S3Connections(Paths.ConnectionsFile, (connection, prefix, checkWrite, _) =>
        {
            TestRuns.Add((connection.Bucket, prefix, checkWrite));
            return Task.FromResult<IReadOnlyList<S3CheckResult>>([new S3CheckResult(S3Check.Bucket, S3CheckState.Ok, null, null)]);
        });
    }

    /// <summary>The id of the account <see cref="AddConnection"/> stores its connections under.</summary>
    public const string AccountId = "acc-1";

    /// <summary>The S3 connections in the configuration folder; "Test" records its runs in <see cref="TestRuns"/>.</summary>
    public S3Connections S3 { get; }

    /// <summary>The connection tests run: bucket, prefix and whether the write check was asked for.</summary>
    public List<(string Bucket, string Prefix, bool CheckWrite)> TestRuns { get; } = [];

    /// <summary>Stores a connection (and the one account it needs) and reloads the list.</summary>
    public void AddConnection(string id, string name, string bucket, string region = "eu-central-1")
    {
        if (S3.Store.TryGetAccount(AccountId) is null)
            S3.Store.SaveAccount(new S3Account(AccountId, "Main", "AKIAEXAMPLE", "secret-key"));
        S3.Save(new S3ConnectionInfo(id, name, region, bucket, AccountId));
    }

    /// <summary>Makes the stored account's secret undecryptable, as on another PC or Windows account.</summary>
    public bool Secretless
    {
        set
        {
            if (!value)
                return;
            var file = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Paths.ConnectionsFile))!;
            foreach (var account in file["accounts"]!.AsArray())
                account!["secretProtected"] = "AAAA";
            File.WriteAllText(Paths.ConnectionsFile, file.ToJsonString());
        }
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
            new LanguageToggleViewModel(() => "en-US", _ => { }), new FakeFolderOpener(), versionIndex, storages, S3);
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

    /// <summary>The questions asked (Confirm and ConfirmDefaultNo).</summary>
    public List<string> Confirms { get; } = [];

    /// <summary>What the next S3 connection dialog returns (then null again: cancelled).</summary>
    public S3ConnectionInfo? NextConnection { get; set; }

    /// <summary>The connections the S3 connection dialog was opened on (null = a new one).</summary>
    public List<S3ConnectionInfo?> EditedConnections { get; } = [];

    public int AccountDialogs { get; private set; }

    public bool Confirm(string title, string message)
    {
        Confirms.Add(message);
        return ConfirmAnswer;
    }

    public bool ConfirmDefaultNo(string title, string message)
    {
        Confirms.Add(message);
        return ConfirmAnswer;
    }

    public S3ConnectionInfo? EditS3Connection(S3ConnectionInfo? existing)
    {
        EditedConnections.Add(existing);
        var result = NextConnection;
        NextConnection = null;
        return result;
    }

    public void ManageS3Accounts() => AccountDialogs++;

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
