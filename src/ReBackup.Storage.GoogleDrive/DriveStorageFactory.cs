using ReBackup.Storage.GoogleDrive.Connections;

namespace ReBackup.Storage.GoogleDrive;

/// <summary>
/// Opens <c>"gdrive"</c> locations on the saved Drive connection they name and hands every other kind to the inner factory. The
/// location's path is a folder below the connection's root (<c>""</c> = the root). One API client is kept per connection state
/// (id, root, shared drive, key), so storages opened again reuse its connections and an edited connection gets a fresh client.
/// Disposing the factory disposes those clients, so dispose it only after the storages it opened are no longer in use.
/// </summary>
/// <remarks>
/// With <see cref="DriveConnectionStore.TryResolve"/> as the lookup, a key that cannot be decrypted on this Windows account or a
/// corrupt connections file surface as the store's <see cref="StorageIOException"/>; an unknown connection id is
/// <see cref="StorageNotFoundException"/>.
/// </remarks>
public sealed class DriveStorageFactory : IStorageFactory, IDisposable
{
    /// <summary>The kind of a location on a Google Drive connection.</summary>
    public const string Kind = "gdrive";

    private readonly IStorageFactory _inner;
    private readonly Func<string, DriveConnection?> _resolve;
    private readonly Func<DriveConnection, IDriveApi> _apiFactory;
    private readonly Dictionary<(string Id, string RootId, string? SharedDriveId, string Key), IDriveApi> _apis = [];
    private readonly object _gate = new();
    private bool _disposed;

    /// <param name="inner">Opens every kind except <c>"gdrive"</c>.</param>
    /// <param name="resolve">Looks a saved connection up by id (<c>null</c> = unknown), e.g. <see cref="DriveConnectionStore.TryResolve"/>.</param>
    /// <param name="apiFactory">
    /// Builds the API client of a connection (called once per connection state; the factory disposes clients that are
    /// <see cref="IDisposable"/>). By default a <see cref="GoogleDriveApi"/> on the connection's key; tests pass a fake.
    /// </param>
    public DriveStorageFactory(IStorageFactory inner, Func<string, DriveConnection?> resolve, Func<DriveConnection, IDriveApi>? apiFactory = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(resolve);
        _inner = inner;
        _resolve = resolve;
        _apiFactory = apiFactory ?? (connection => new GoogleDriveApi(connection));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The path of a Drive location is no valid storage path.</exception>
    public IStorage Open(StorageLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!string.Equals(location.Kind, Kind, StringComparison.Ordinal)) return _inner.Open(location);

        StoragePath.Validate(location.Path);
        var connection = location.ConnectionId is { } id ? _resolve(id) : null;
        if (connection is null)
            throw new StorageNotFoundException(location.Path, $"The Google Drive connection '{location.ConnectionId}' was not found.");

        var storage = new DriveStorage(ApiFor(connection), connection.Info.RootId);
        return location.Path.Length == 0 ? storage : new DriveFolderStorage(storage, location.Path);
    }

    private IDriveApi ApiFor(DriveConnection connection)
    {
        var key = (connection.Info.Id, connection.Info.RootId, connection.Info.SharedDriveId, connection.KeyJson);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_apis.TryGetValue(key, out var api))
                _apis[key] = api = _apiFactory(connection);
            return api;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var api in _apis.Values) (api as IDisposable)?.Dispose();
            _apis.Clear();
        }
    }
}
