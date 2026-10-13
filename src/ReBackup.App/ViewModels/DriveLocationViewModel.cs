using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.GoogleDrive;
using ReBackup.Storage.GoogleDrive.Connections;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The Google Drive half of a plan's source or target: the connection (picked from the saved ones, or created with "New
/// connection…"), the path below the connection's root and "Test". The plan editor decides whether it is used
/// (Folder | S3 | Drive) and validates it.
/// </summary>
public sealed partial class DriveLocationViewModel : ObservableObject
{
    private readonly DriveConnections _connections;
    private readonly IDialogService _dialogs;
    private readonly bool _isTarget;
    private readonly Action _changed;
    private CancellationTokenSource? _testCts;
    private bool _loading;
    private int _edits;   // counts connection and path changes; a test's result belongs to the count it started at

    /// <param name="isTarget">A target is tested with a probe file written and deleted, a source (only read) without it.</param>
    /// <param name="changed">Called after the user changed the connection or the path.</param>
    public DriveLocationViewModel(DriveConnections connections, IDialogService dialogs, bool isTarget, Action changed)
    {
        _connections = connections;
        _dialogs = dialogs;
        _isTarget = isTarget;
        _changed = changed;
        // The list is read again after every change; the picker then finds the connection's new entry.
        connections.All.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SelectedConnection));
    }

    /// <summary>The id of the chosen connection; null until one is chosen (a deleted one stays until changed).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private string? _connectionId;

    /// <summary>The folder below the connection's root (<c>""</c> = the root itself).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private string _path = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand), nameof(CancelTestCommand), nameof(NewConnectionCommand))]
    private bool _isTesting;

    /// <summary>The last test's outcome as the user reads it (cancelled, could not run, key to re-enter, or the result); null when there is none.</summary>
    [ObservableProperty] private string? _testMessage;

    /// <summary>Whether the last test found the location usable; null without a result (no test, cancelled, could not run).</summary>
    [ObservableProperty] private bool? _testPassed;

    /// <summary>
    /// The picker's entry of <see cref="ConnectionId"/>; null when it is not in the list (none chosen, or deleted). The
    /// picker setting null (its list being read again) changes nothing.
    /// </summary>
    public DriveConnectionInfo? SelectedConnection
    {
        get => ConnectionId is { } id
            ? Connections.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
            : null;
        set
        {
            if (value is not null)
                ConnectionId = value.Id;
        }
    }

    /// <summary>The saved connections (shared by every plan).</summary>
    public ObservableCollection<DriveConnectionInfo> Connections => _connections.All;

    /// <summary>The location as edited.</summary>
    public StorageLocation Location => new(DriveStorageFactory.Kind, Path, ConnectionId);

    /// <summary>Shows a saved location without counting as an edit.</summary>
    public void Load(string? connectionId, string path)
    {
        _loading = true;
        try
        {
            ConnectionId = connectionId;
            Path = path;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnConnectionIdChanged(string? value)
    {
        OnPropertyChanged(nameof(SelectedConnection));
        OnEdited();
    }

    partial void OnPathChanged(string value) => OnEdited();

    /// <summary>The result belongs to the inputs it was made for: it goes, and a running test is cancelled.</summary>
    private void OnEdited()
    {
        _edits++;
        _testCts?.Cancel();
        TestMessage = null;
        TestPassed = null;
        if (!_loading)
            _changed();
    }

    /// <summary>"New connection…": the connection dialog, then the connection is stored and chosen.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void NewConnection()
    {
        if (_dialogs.EditDriveConnection(null) is not { } created)
            return;
        try
        {
            _connections.Save(created);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _dialogs.ShowError(Loc.T("driveconnections.title"), Loc.F("driveconnections.saveFailed", ("error", ex.Message)));
            return;
        }
        ConnectionId = created.Id;
    }

    private bool CanEdit() => !IsTesting;

    private bool CanTest() => !IsTesting && !string.IsNullOrWhiteSpace(ConnectionId) && IsValidPath(Path);

    /// <summary>The connection test on the chosen connection, scoped to the path.</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        TestMessage = null;
        TestPassed = null;
        DriveConnection? connection;
        try
        {
            if (_connections.AccountOf(ConnectionId!) is { NeedsKey: true })
            {
                TestMessage = Loc.T("plan.drive.needsKey");
                return;
            }
            connection = _connections.Resolve(ConnectionId!);
        }
        catch (StorageIOException ex)
        {
            TestMessage = ex.Message;
            return;
        }
        if (connection is null)
        {
            TestMessage = Loc.T("plan.drive.connectionMissing");
            return;
        }

        using var cts = new CancellationTokenSource();
        _testCts = cts;
        var edits = _edits;
        IsTesting = true;
        try
        {
            var result = await _connections.TestAsync(connection, Path, _isTarget, cts.Token);
            if (edits == _edits)
            {
                TestPassed = result.Outcome == DriveTestOutcome.Ok;
                TestMessage = Describe(result, connection.ClientEmail, _isTarget);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (edits == _edits)   // cancelled by an edit: nothing to report about the old inputs
                TestMessage = Loc.T("plan.drive.result.cancelled");
        }
        catch (Exception)
        {
            // The tester classifies Drive's errors itself; whatever is left is reported without its message.
            if (edits == _edits)
                TestMessage = Loc.T("plan.drive.result.failed");
        }
        finally
        {
            _testCts = null;
            IsTesting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsTesting))]
    private void CancelTest() => _testCts?.Cancel();

    /// <summary>The tester's outcome in the applied language (its own messages are English); an error keeps the tester's reason.</summary>
    public static string Describe(DriveTestResult result, string email, bool asTarget) => result.Outcome switch
    {
        DriveTestOutcome.Ok => Loc.T(asTarget ? "plan.drive.result.okTarget" : "plan.drive.result.okSource"),
        DriveTestOutcome.AccessDenied => Loc.F("plan.drive.result.accessDenied", ("email", email)),
        DriveTestOutcome.NotFound => Loc.F("plan.drive.result.notFound", ("email", email)),
        DriveTestOutcome.NotSharedDrive => Loc.F("plan.drive.result.notSharedDrive", ("email", email)),
        _ => Loc.F("plan.drive.result.error", ("error", result.Message)),
    };

    private static bool IsValidPath(string path)
    {
        try
        {
            StoragePath.Validate(path);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
