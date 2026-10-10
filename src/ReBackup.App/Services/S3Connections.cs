using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using ReBackup.Core.Plans;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.App.Services;

/// <summary>Runs the connection test on a connection and a prefix (<see cref="S3ConnectionTester.RunAsync(S3Connection, string, bool, CancellationToken)"/>).</summary>
public delegate Task<IReadOnlyList<S3CheckResult>> S3TestRunner(S3Connection connection, string prefix, bool checkWrite,
    CancellationToken ct);

/// <summary>
/// The S3 accounts and connections of the configuration folder (<c>connections.json</c>): the lookup the app's storage
/// factory and plan validation use, the list the Plan tab and Settings show, and the connection test. A file the store
/// cannot read (corrupt, newer format, no access) surfaces as a <see cref="StorageIOException"/> naming the store's
/// reason (never a secret), so plans show it and runs end with it; the file is never overwritten.
/// </summary>
public sealed class S3Connections
{
    private readonly S3TestRunner _test;

    /// <param name="filePath">The <c>connections.json</c> of the configuration folder.</param>
    /// <param name="test">Runs "Test"; the real <see cref="S3ConnectionTester"/> when null.</param>
    public S3Connections(string filePath, S3TestRunner? test = null)
    {
        FilePath = filePath;
        Store = new S3ConnectionStore(filePath);
        _test = test ?? new S3TestRunner(new S3ConnectionTester().RunAsync);
        Reload();
    }

    public string FilePath { get; }

    /// <summary>The store itself, for the shared account dialogs.</summary>
    public S3ConnectionStore Store { get; }

    /// <summary>The connections in file order, as last read (UI thread); empty when the file cannot be read.</summary>
    public ObservableCollection<S3ConnectionInfo> All { get; } = [];

    /// <summary>Why the file could not be read at the last <see cref="Reload"/>; null when it was fine.</summary>
    public string? LoadError { get; private set; }

    /// <summary>The list was read again (after a change here or in the dialogs); plans validate again.</summary>
    public event EventHandler? Changed;

    /// <summary>Reads the list again and raises <see cref="Changed"/>.</summary>
    public void Reload()
    {
        IReadOnlyList<S3ConnectionInfo> connections;
        try
        {
            connections = Store.LoadAll();
            LoadError = null;
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            connections = [];
            LoadError = ex.Message;
        }
        All.Clear();
        foreach (var connection in connections)
            All.Add(connection);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stores <paramref name="connection"/> and reloads the list. Throws what the store throws.</summary>
    public void Save(S3ConnectionInfo connection)
    {
        Store.Save(connection);
        Reload();
    }

    /// <summary>Removes the connection and reloads the list. Throws what the store throws.</summary>
    public void Delete(string id)
    {
        Store.Delete(id);
        Reload();
    }

    /// <summary>
    /// The connection resolved with its account, or null when there is none: the lookup of the app's
    /// <see cref="S3StorageFactory"/>. Any thread. An unreadable file throws <see cref="StorageIOException"/>.
    /// </summary>
    public S3Connection? Resolve(string id) => Read(() => Store.TryResolve(id));

    /// <summary>The bucket of a connection for plan validation; null when there is none. Throws like <see cref="Resolve"/>.</summary>
    public ConnectionBucket? BucketOf(string id) =>
        Read(() => Store.TryGet(id)) is { } connection ? new ConnectionBucket(connection.Bucket, connection.Region) : null;

    /// <summary>Runs the connection test.</summary>
    public Task<IReadOnlyList<S3CheckResult>> TestAsync(S3Connection connection, string prefix, bool checkWrite, CancellationToken ct) =>
        _test(connection, prefix, checkWrite, ct);

    /// <summary>
    /// A location as the user reads it: a folder's path; for S3 the connection's name with bucket and prefix
    /// ("Offsite · s3://bucket/prefix"), or "S3 connection not found · prefix".
    /// </summary>
    public string Describe(StorageLocation location)
    {
        if (location.IsFileSystem)
            return location.Path;
        if (!string.Equals(location.Kind, S3StorageFactory.Kind, StringComparison.Ordinal))
            return $"{location.Kind}://{location.Path}";

        S3ConnectionInfo? connection;
        try
        {
            connection = location.ConnectionId is { } id ? Read(() => Store.TryGet(id)) : null;
        }
        catch (StorageIOException)
        {
            connection = null;
        }
        return connection is null
            ? Loc.F("s3location.unknown", ("prefix", location.Path))
            : Loc.F("s3location.describe", ("connection", connection.Name), ("bucket", connection.Bucket), ("prefix", location.Path));
    }

    /// <summary>Who uses a connection: whether <paramref name="location"/> is on the connection <paramref name="id"/>.</summary>
    public static bool Uses(StorageLocation location, string id) =>
        string.Equals(location.Kind, S3StorageFactory.Kind, StringComparison.Ordinal) &&
        string.Equals(location.ConnectionId, id, StringComparison.OrdinalIgnoreCase);

    private T Read<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            throw new StorageIOException(FilePath, "connections.json could not be read: " + ex.Message, ex);
        }
    }

    private static bool IsStoreFailure(Exception ex) => ex is JsonException or IOException or UnauthorizedAccessException;
}
