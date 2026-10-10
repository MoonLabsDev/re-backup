using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The S3 half of a plan's source or target: the connection (picked from the saved ones, or created with "New
/// connection…"), the prefix and "Test". The plan editor decides whether it is used (Folder | S3) and validates it.
/// </summary>
public sealed partial class S3LocationViewModel : ObservableObject
{
    private readonly S3Connections _connections;
    private readonly IDialogService _dialogs;
    private readonly bool _isTarget;
    private readonly Action _changed;
    private CancellationTokenSource? _testCts;
    private bool _loading;
    private int _edits;   // counts connection and prefix changes; a test's results belong to the count it started at

    /// <param name="isTarget">A target is tested with the write check, a source (only read) without it.</param>
    /// <param name="changed">Called after the user changed the connection or the prefix.</param>
    public S3LocationViewModel(S3Connections connections, IDialogService dialogs, bool isTarget, Action changed)
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

    /// <summary>The prefix in the bucket (<c>""</c> = the whole bucket).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private string _prefix = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand), nameof(CancelTestCommand), nameof(NewConnectionCommand))]
    private bool _isTesting;

    /// <summary>Why the last test has no results (cancelled, could not run, secret to re-enter); null otherwise.</summary>
    [ObservableProperty] private string? _testStatus;

    /// <summary>
    /// The picker's entry of <see cref="ConnectionId"/>; null when it is not in the list (none chosen, or deleted). The
    /// picker setting null (its list being read again) changes nothing.
    /// </summary>
    public S3ConnectionInfo? SelectedConnection
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
    public ObservableCollection<S3ConnectionInfo> Connections => _connections.All;

    /// <summary>The results of the last test, one per check; cleared when the connection or the prefix changes.</summary>
    public ObservableCollection<S3CheckResult> Results { get; } = [];

    /// <summary>The location as edited.</summary>
    public StorageLocation Location => new(S3StorageFactory.Kind, Prefix, ConnectionId);

    /// <summary>Shows a saved location without counting as an edit.</summary>
    public void Load(string? connectionId, string prefix)
    {
        _loading = true;
        try
        {
            ConnectionId = connectionId;
            Prefix = prefix;
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

    partial void OnPrefixChanged(string value) => OnEdited();

    /// <summary>Results and status belong to the inputs they were made for: they go, and a running test is cancelled.</summary>
    private void OnEdited()
    {
        _edits++;
        _testCts?.Cancel();
        Results.Clear();
        TestStatus = null;
        if (!_loading)
            _changed();
    }

    /// <summary>"New connection…": the shared dialog, then the connection is stored and chosen.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void NewConnection()
    {
        if (_dialogs.EditS3Connection(null) is not { } created)
            return;
        try
        {
            _connections.Save(created);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _dialogs.ShowError(Loc.T("s3connections.title"), Loc.F("s3connections.saveFailed", ("error", ex.Message)));
            return;
        }
        ConnectionId = created.Id;
    }

    private bool CanEdit() => !IsTesting;

    private bool CanTest() => !IsTesting && !string.IsNullOrWhiteSpace(ConnectionId) && IsValidPrefix(Prefix);

    /// <summary>The shared connection test on the chosen connection, scoped to the prefix.</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        Results.Clear();
        TestStatus = null;
        S3Connection? connection;
        try
        {
            connection = _connections.Resolve(ConnectionId!);
        }
        catch (StorageIOException ex)
        {
            TestStatus = ex.Message;
            return;
        }
        if (connection is null)
        {
            TestStatus = Loc.T("plan.s3.connectionMissing");
            return;
        }
        if (connection.NeedsSecret)
        {
            TestStatus = Loc.T("plan.s3.needsSecret");
            return;
        }

        using var cts = new CancellationTokenSource();
        _testCts = cts;
        var edits = _edits;
        IsTesting = true;
        try
        {
            var results = await _connections.TestAsync(connection, Prefix, _isTarget, cts.Token);
            if (edits == _edits)
            {
                foreach (var result in results)
                    Results.Add(result);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (edits == _edits)   // cancelled by an edit: nothing to report about the old inputs
                TestStatus = Loc.T("s3.test.cancelled");
        }
        catch (Exception)
        {
            // The tester classifies the SDK's errors itself; whatever is left is reported without its message (it may
            // hold request details).
            if (edits == _edits)
                TestStatus = Loc.T("s3.test.error");
        }
        finally
        {
            _testCts = null;
            IsTesting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsTesting))]
    private void CancelTest() => _testCts?.Cancel();

    private static bool IsValidPrefix(string prefix)
    {
        try
        {
            StoragePath.Validate(prefix);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
