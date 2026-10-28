using System.IO;
using Microsoft.Extensions.Time.Testing;
using ReBackup.App.Services;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Backup.Retry;
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
using ReBackup.Storage.GoogleDrive;
using ReBackup.Storage.GoogleDrive.Connections;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.App.Tests.TestSupport;

/// <summary>
/// A <see cref="MainViewModel"/> on a temporary configuration folder; the scheduler is never started. Its retry planner keeps
/// its states in the configuration folder and runs on <see cref="Time"/>: a retry falls due only when the test moves it on.
/// </summary>
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
        S3 = new S3Connections(Paths.ConnectionsFile, async (connection, prefix, checkWrite, ct) =>
        {
            TestRuns.Add((connection.Bucket, prefix, checkWrite));
            if (TestGate is { } gate)
                await gate.Task.WaitAsync(ct);
            return [new S3CheckResult(S3Check.Bucket, S3CheckState.Ok, null, null)];
        });
        Drive = new DriveConnections(Paths.DriveConnectionsFile, async (connection, path, asTarget, ct) =>
        {
            DriveTestRuns.Add((connection.Info.RootId, path, asTarget));
            if (TestGate is { } gate)
                await gate.Task.WaitAsync(ct);
            return NextDriveTest ?? new DriveTestResult(DriveTestOutcome.Ok, "fine");
        }, key =>
        {
            DriveApiKeys.Add(key);
            return DriveApi;
        });
    }

    /// <summary>The Google accounts and Drive connections in the configuration folder; "Test" records its runs in <see cref="DriveTestRuns"/>.</summary>
    public DriveConnections Drive { get; }

    /// <summary>The Drive connection tests run: root id, path and whether it was tested as a target.</summary>
    public List<(string RootId, string Path, bool AsTarget)> DriveTestRuns { get; } = [];

    /// <summary>What the next Drive connection tests return; Ok when null.</summary>
    public DriveTestResult? NextDriveTest { get; set; }

    /// <summary>The API the Drive connection dialog talks to, whatever the key.</summary>
    public FakeDriveApi DriveApi { get; } = new();

    /// <summary>The keys the API was built for.</summary>
    public List<string> DriveApiKeys { get; } = [];

    /// <summary>The id of the Google account <see cref="AddDriveConnection"/> stores its connections under.</summary>
    public const string DriveAccountId = "gacc-1";

    /// <summary>The e-mail of that account's key.</summary>
    public const string DriveEmail = "backup@moonlabs.iam.gserviceaccount.com";

    /// <summary>A service-account key file's JSON (the key itself is a placeholder; nothing is sent to Google).</summary>
    public static string ServiceAccountKey(string email = DriveEmail) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account",
            ["private_key"] = "not-a-real-key",
            ["client_email"] = email,
        });

    /// <summary>Stores a Drive connection (and the one account it needs) and reloads the lists.</summary>
    public void AddDriveConnection(string id, string name, string rootId, string? sharedDriveId, string? rootName = null)
    {
        if (Drive.Store.TryGetAccount(DriveAccountId) is null)
            Drive.SaveAccount(DriveAccountId, "Backup", ServiceAccountKey());
        Drive.Save(new DriveConnectionInfo(id, name, DriveAccountId, rootId, rootName ?? name, sharedDriveId));
    }

    /// <summary>When set, a connection test waits for it (or for its cancellation).</summary>
    public TaskCompletionSource? TestGate { get; set; }

    /// <summary>The queue of the last <see cref="Create"/>.</summary>
    public BackupQueue Queue { get; private set; } = null!;

    /// <summary>The scheduler of the last <see cref="Create"/>; never started, but it can be paused.</summary>
    public Scheduler Scheduler { get; private set; } = null!;

    /// <summary>The retry planner of the last <see cref="Create"/>; its due attempts go straight to that view model.</summary>
    public RetryPlanner Retries { get; private set; } = null!;

    /// <summary>The retry planner's clock; it starts at the real time, as the runs are stamped with that.</summary>
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    private readonly List<RetryPlanner> _planners = [];

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

    /// <summary>Where the editors put integrated <c>.backupignore</c> files; moves them into a folder of its own.</summary>
    public FakeRecycleBin RecycleBin { get; } = new();

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

    /// <summary>What <see cref="Create"/> opens locations with; the real folders and storages when null.</summary>
    public IStorageFactory? Storages { get; set; }

    public MainViewModel Create()
    {
        var storages = Storages ?? new StorageFactory();
        var queue = Queue = new BackupQueue(new BackupRunner(storages), planId => new RunLog(Paths.LogFileFor(planId)));
        var scheduler = Scheduler = new Scheduler((_, _) => { });
        MainViewModel? created = null;
        var retries = Retries = new RetryPlanner(Paths.RetriesDirectory, state => created?.RunRetry(state), Time);
        _planners.Add(retries);
        var versionIndex = new VersionIndexWorker(new VersionIndexSet(Path.Combine(Root, "index")));
        return created = new MainViewModel(Store, Paths, Settings, SaveSettings, () => LoadError, Dialogs, () => { }, queue,
            scheduler, retries, action => action(), new ThemeToggleViewModel(() => ThemeMode.Dark, _ => { }),
            new LanguageToggleViewModel(() => "en-US", _ => { }), new FakeFolderOpener(), versionIndex, storages, S3, Drive,
            recycleBin: RecycleBin);
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
        foreach (var planner in _planners)
            planner.Dispose();
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

    /// <summary>The messages shown with <see cref="ShowInfo"/>.</summary>
    public List<string> Infos { get; } = [];

    /// <summary>The "Copy plan…" dialogs opened; the test drives them.</summary>
    public List<CopyPlanViewModel> CopyDialogs { get; } = [];

    /// <summary>The "Verify backup…" dialogs opened; the test drives them.</summary>
    public List<VerifyViewModel> VerifyDialogs { get; } = [];

    public void ShowCopyPlan(CopyPlanViewModel viewModel) => CopyDialogs.Add(viewModel);

    /// <summary>The relocation dialogs shown, in order.</summary>
    public List<RelocationDialogViewModel> Relocations { get; } = [];

    /// <summary>What a relocation dialog answers when <see cref="OnRelocation"/> is not set.</summary>
    public bool RelocationAnswer { get; set; } = true;

    /// <summary>Plays the user in the relocation dialog (ticks boxes, changes files meanwhile); answers the dialog.</summary>
    public Func<RelocationDialogViewModel, bool>? OnRelocation { get; set; }

    public bool ConfirmRelocation(RelocationDialogViewModel model)
    {
        Relocations.Add(model);
        return OnRelocation?.Invoke(model) ?? RelocationAnswer;
    }

    public void ShowVerify(VerifyViewModel viewModel) => VerifyDialogs.Add(viewModel);

    /// <summary>The questions asked (Confirm and ConfirmDefaultNo).</summary>
    public List<string> Confirms { get; } = [];

    /// <summary>What the next S3 connection dialog returns (then null again: cancelled).</summary>
    public S3ConnectionInfo? NextConnection { get; set; }

    /// <summary>The connections the S3 connection dialog was opened on (null = a new one).</summary>
    public List<S3ConnectionInfo?> EditedConnections { get; } = [];

    public int AccountDialogs { get; private set; }

    /// <summary>Runs while a Confirm question is open, as the dispatcher a modal dialog pumps would run other work.</summary>
    public Action? OnConfirm { get; set; }

    public bool Confirm(string title, string message)
    {
        Confirms.Add(message);
        OnConfirm?.Invoke();
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

    /// <summary>What the next Drive connection dialog returns (then null again: cancelled).</summary>
    public DriveConnectionInfo? NextDriveConnection { get; set; }

    /// <summary>The connections the Drive connection dialog was opened on (null = a new one).</summary>
    public List<DriveConnectionInfo?> EditedDriveConnections { get; } = [];

    /// <summary>What the next Google account dialog returns (then null again: cancelled).</summary>
    public DriveAccountInput? NextDriveAccount { get; set; }

    /// <summary>The accounts the Google account dialog was opened on (null = a new one).</summary>
    public List<DriveAccount?> EditedDriveAccounts { get; } = [];

    public DriveConnectionInfo? EditDriveConnection(DriveConnectionInfo? existing)
    {
        EditedDriveConnections.Add(existing);
        var result = NextDriveConnection;
        NextDriveConnection = null;
        return result;
    }

    public DriveAccountInput? EditDriveAccount(DriveAccount? existing)
    {
        EditedDriveAccounts.Add(existing);
        var result = NextDriveAccount;
        NextDriveAccount = null;
        return result;
    }

    public bool? AskYesNoCancel(string title, string message) => YesNoCancelAnswer;

    public string? PickFolder(string title, string? initialFolder) => null;

    public void ShowError(string title, string message) => Errors.Add(message);

    public void ShowInfo(string title, string message) => Infos.Add(message);

    public ConflictPolicy? AskConflictPolicy(string title, string message, IReadOnlyList<string>? details = null) => null;

    public void ShowFailures(string title, string message, IReadOnlyList<string> lines) { }

    public bool ConfirmFailures(string title, string message, IReadOnlyList<string> lines, string confirmText) => false;
}

/// <summary>Moves recycled files into a folder of its own (never deletes them) and answers <see cref="Outcome"/>.</summary>
public sealed class FakeRecycleBin : ReBackup.Core.Ignore.IRecycleBin
{
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "rebackup-app-tests-bin", Guid.NewGuid().ToString("N"));

    public List<string> Recycled { get; } = [];

    public ReBackup.Core.Ignore.RecycleOutcome Outcome { get; set; } = ReBackup.Core.Ignore.RecycleOutcome.Recycled;

    public ReBackup.Core.Ignore.RecycleOutcome Recycle(string path)
    {
        Directory.CreateDirectory(_bin);
        File.Move(path, Path.Combine(_bin, Recycled.Count + "-" + Path.GetFileName(path)));
        Recycled.Add(path);
        return Outcome;
    }
}

/// <summary>Opens nothing (every call answers false) and records the files it was asked to open or show.</summary>
public sealed class FakeFolderOpener : IFolderOpener
{
    public List<(string Folder, string Path)> OpenedFiles { get; } = [];

    public List<(string Folder, string Path)> Shown { get; } = [];

    public Task<bool> OpenVersionFolderAsync(string? target, string? versionName) => Task.FromResult(false);

    public bool OpenFile(string versionFolder, string relativePath)
    {
        lock (OpenedFiles)
            OpenedFiles.Add((versionFolder, relativePath));
        return false;
    }

    public bool ShowInExplorer(string versionFolder, string relativePath)
    {
        lock (Shown)
            Shown.Add((versionFolder, relativePath));
        return false;
    }
}

/// <summary>A Drive API with shared drives and items set by the test; every other call fails.</summary>
public sealed class FakeDriveApi : IDriveApi
{
    public List<SharedDrive> SharedDrives { get; } = [];

    public Dictionary<string, DriveItem> Items { get; } = [];

    /// <summary>Thrown by every call when set.</summary>
    public Exception? Failure { get; set; }

    public int Calls { get; private set; }

    /// <summary>When set, listing the shared drives waits for it (or for its token).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<IReadOnlyList<SharedDrive>> ListSharedDrivesAsync(CancellationToken ct)
    {
        Calls++;
        if (Failure is not null) throw Failure;
        if (Gate is { } gate)
            await gate.Task.WaitAsync(ct);
        return SharedDrives.ToList();
    }

    public Task<DriveItem> GetAsync(string id, CancellationToken ct)
    {
        Calls++;
        if (Failure is not null) throw Failure;
        return Items.TryGetValue(id, out var item) ? Task.FromResult(item) : throw new DriveApiException(404, "notFound");
    }

    public Task<IReadOnlyList<DriveItem>> ListChildrenAsync(string folderId, CancellationToken ct, string? name = null) => throw new NotSupportedException();

    public Task<DriveItem> CreateFolderAsync(string parentId, string name, CancellationToken ct) => throw new NotSupportedException();

    public Task<DriveItem> UploadAsync(string parentId, string name, Stream content, DateTime? modifiedUtc, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<DriveItem> UpdateContentAsync(string id, Stream content, DateTime? modifiedUtc, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task DeleteAsync(string id, CancellationToken ct) => throw new NotSupportedException();

    public Task<Stream> DownloadAsync(string id, CancellationToken ct) => throw new NotSupportedException();

    public Task<Stream> ExportAsync(string id, string mimeType, CancellationToken ct) => throw new NotSupportedException();
}
