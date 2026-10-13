using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using ReBackup.Core.Plans;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.GoogleDrive;
using ReBackup.Storage.GoogleDrive.Connections;

namespace ReBackup.App.Services;

/// <summary>Runs the Drive connection test on a connection and a path (<see cref="DriveConnectionTester.RunAsync"/>).</summary>
public delegate Task<DriveTestResult> DriveTestRunner(DriveConnection connection, string path, bool asTarget, CancellationToken ct);

/// <summary>
/// The Google accounts and Drive connections of the configuration folder (<c>drive-connections.json</c>): the lookup the app's
/// storage factory and plan validation use, the lists the Plan tab and Settings show, the connection test and the API client
/// the connection dialog lists shared drives with. A file the store cannot read (corrupt, newer format, no access) surfaces as
/// a <see cref="StorageIOException"/> naming the store's reason (never a key), so plans show it and runs end with it; the file is
/// never overwritten.
/// </summary>
public sealed class DriveConnections
{
    private readonly DriveTestRunner _test;
    private readonly Func<string, IDriveApi> _apiForKey;

    /// <param name="filePath">The <c>drive-connections.json</c> of the configuration folder.</param>
    /// <param name="test">Runs "Test"; the real <see cref="DriveConnectionTester"/> when null.</param>
    /// <param name="apiForKey">Builds an API client for a service-account key (the connection dialog); a <see cref="GoogleDriveApi"/> when null.</param>
    public DriveConnections(string filePath, DriveTestRunner? test = null, Func<string, IDriveApi>? apiForKey = null)
    {
        FilePath = filePath;
        Store = new DriveConnectionStore(filePath);
        _test = test ?? new DriveTestRunner(new DriveConnectionTester().RunAsync);
        _apiForKey = apiForKey ?? (key => new GoogleDriveApi(key));
        Reload();
    }

    public string FilePath { get; }

    /// <summary>The store itself.</summary>
    public DriveConnectionStore Store { get; }

    /// <summary>The connections in file order, as last read (UI thread); empty when the file cannot be read.</summary>
    public ObservableCollection<DriveConnectionInfo> All { get; } = [];

    /// <summary>The accounts in file order, as last read (UI thread); empty when the file cannot be read.</summary>
    public ObservableCollection<DriveAccount> Accounts { get; } = [];

    /// <summary>Why the file could not be read at the last <see cref="Reload"/>; null when it was fine.</summary>
    public string? LoadError { get; private set; }

    /// <summary>The lists were read again (after a change here or in the dialogs); plans validate again.</summary>
    public event EventHandler? Changed;

    /// <summary>Reads the lists again and raises <see cref="Changed"/>.</summary>
    public void Reload()
    {
        IReadOnlyList<DriveConnectionInfo> connections;
        IReadOnlyList<DriveAccount> accounts;
        try
        {
            connections = Store.LoadAll();
            accounts = Store.LoadAccounts();
            LoadError = null;
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            connections = [];
            accounts = [];
            LoadError = ex.Message;
        }
        All.Clear();
        foreach (var connection in connections)
            All.Add(connection);
        Accounts.Clear();
        foreach (var account in accounts)
            Accounts.Add(account);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stores <paramref name="connection"/> and reloads the lists. Throws what the store throws.</summary>
    public void Save(DriveConnectionInfo connection)
    {
        Store.Save(connection);
        Reload();
    }

    /// <summary>Removes the connection and reloads the lists. Throws what the store throws.</summary>
    public void Delete(string id)
    {
        Store.Delete(id);
        Reload();
    }

    /// <summary>Stores the account (a <c>null</c> key keeps the stored one) and reloads the lists. Throws what the store throws.</summary>
    public void SaveAccount(string id, string name, string? keyJson)
    {
        Store.SaveAccount(id, name, keyJson);
        Reload();
    }

    /// <summary>Removes the account and reloads the lists. Throws what the store throws (also while a connection uses it).</summary>
    public void DeleteAccount(string id)
    {
        Store.DeleteAccount(id);
        Reload();
    }

    /// <summary>
    /// The connection resolved with its account's key, or null when there is none: the lookup of the app's
    /// <see cref="DriveStorageFactory"/>. Any thread. An unreadable file, or a key that cannot be decrypted on this Windows
    /// account, throws <see cref="StorageIOException"/>.
    /// </summary>
    public DriveConnection? Resolve(string id)
    {
        try
        {
            return Read(() => Store.TryResolve(id));
        }
        catch (StorageIOException ex) when (ex.InnerException is JsonException json)
        {
            throw Unreadable(json);
        }
    }

    /// <summary>The root of a connection for plan validation; null when there is none. Throws like <see cref="Resolve"/>.</summary>
    public DriveRoot? RootOf(string id) =>
        Read(() => Store.TryGet(id)) is { } connection ? new DriveRoot(connection.RootId, connection.SharedDriveId) : null;

    /// <summary>The account of a connection; null when the connection or its account is gone. Throws like <see cref="Resolve"/>.</summary>
    public DriveAccount? AccountOf(string connectionId) =>
        Read(() => Store.TryGet(connectionId) is { } connection ? Store.TryGetAccount(connection.AccountId) : null);

    /// <summary>
    /// An API client on the key of the account <paramref name="accountId"/> (the caller disposes it); null when the account is
    /// gone or its key has to be entered again. A key Google cannot read throws <see cref="StorageAccessDeniedException"/>.
    /// </summary>
    public IDriveApi? ApiForAccount(string accountId)
    {
        var account = Read(() => Store.TryGetAccount(accountId));
        return SecretProtector.TryUnprotect(account?.ProtectedKey) is { } key ? _apiForKey(key) : null;
    }

    /// <summary>Runs the connection test.</summary>
    public Task<DriveTestResult> TestAsync(DriveConnection connection, string path, bool asTarget, CancellationToken ct) =>
        _test(connection, path, asTarget, ct);

    /// <summary>
    /// A location as the user reads it: a folder's path; for Drive the connection's name with its root and path
    /// ("Team · gdrive://Team Backups/docs"), or "Drive connection not found · path".
    /// </summary>
    public string Describe(StorageLocation location)
    {
        if (location.IsFileSystem)
            return location.Path;
        if (!string.Equals(location.Kind, DriveStorageFactory.Kind, StringComparison.Ordinal))
            return $"{location.Kind}://{location.Path}";

        DriveConnectionInfo? connection;
        try
        {
            connection = location.ConnectionId is { } id ? Read(() => Store.TryGet(id)) : null;
        }
        catch (StorageIOException)
        {
            connection = null;
        }
        return connection is null
            ? Loc.F("drivelocation.unknown", ("path", location.Path))
            : Loc.F("drivelocation.describe", ("connection", connection.Name), ("root", connection.RootName), ("path", location.Path));
    }

    /// <summary>Who uses a connection: whether <paramref name="location"/> is on the Drive connection <paramref name="id"/>.</summary>
    public static bool Uses(StorageLocation location, string id) =>
        string.Equals(location.Kind, DriveStorageFactory.Kind, StringComparison.Ordinal) &&
        string.Equals(location.ConnectionId, id, StringComparison.OrdinalIgnoreCase);

    private T Read<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            throw Unreadable(ex);
        }
    }

    private StorageIOException Unreadable(Exception ex) =>
        new(FilePath, "drive-connections.json could not be read: " + ex.Message, ex);

    /// <summary>A failure reading the file; a <see cref="StorageException"/> (e.g. a key to enter again) is no such failure.</summary>
    private static bool IsStoreFailure(Exception ex) =>
        ex is JsonException or UnauthorizedAccessException || (ex is IOException && ex is not StorageException);
}
