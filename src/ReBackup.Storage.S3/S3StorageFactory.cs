using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Storage.S3;

/// <summary>
/// Opens <c>"s3"</c> locations on the saved connection they name and hands every other kind to the inner factory. One
/// <see cref="AmazonS3Client"/> is kept per connection state (id, region, access key, secret), so storages opened again reuse its
/// connection pool and an edited connection gets a fresh client. Disposing the factory disposes those clients, so dispose it
/// only after the storages it opened are no longer in use.
/// </summary>
public sealed class S3StorageFactory : IStorageFactory, IDisposable
{
    /// <summary>The kind of a location on an S3 connection.</summary>
    public const string Kind = "s3";

    private readonly IStorageFactory _inner;
    private readonly Func<string, S3Connection?> _connections;
    private readonly Dictionary<(string Id, string Region, string AccessKeyId, string Secret), AmazonS3Client> _clients = [];
    private readonly object _gate = new();
    private bool _disposed;

    /// <param name="inner">Opens every kind except <c>"s3"</c>.</param>
    /// <param name="connections">Looks a saved connection up by id (<c>null</c> = unknown).</param>
    public S3StorageFactory(IStorageFactory inner, Func<string, S3Connection?> connections)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(connections);
        _inner = inner;
        _connections = connections;
    }

    /// <summary>How many clients are cached (for tests).</summary>
    internal int CachedClientCount
    {
        get
        {
            lock (_gate) return _clients.Count;
        }
    }

    /// <inheritdoc />
    public IStorage Open(StorageLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!string.Equals(location.Kind, Kind, StringComparison.Ordinal)) return _inner.Open(location);

        var connection = location.ConnectionId is { } id ? _connections(id) : null;
        if (connection is null)
            throw new StorageNotFoundException(location.Path, $"The S3 connection '{location.ConnectionId}' was not found.");
        if (connection.NeedsSecret)
            throw new StorageAccessDeniedException(location.Path, $"The secret of the S3 connection '{connection.Name}' must be re-entered.");
        if (!S3Regions.IsValid(connection.Region))
            throw new StorageIOException(location.Path, $"The region of the S3 connection '{connection.Name}' is invalid.");

        return new S3Storage(connection, location.Path, ClientFor(connection));
    }

    private AmazonS3Client ClientFor(S3Connection connection)
    {
        var key = (connection.Id, connection.Region, connection.AccessKeyId, connection.Secret!);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_clients.TryGetValue(key, out var client))
                _clients[key] = client = new AmazonS3Client(
                    new BasicAWSCredentials(connection.AccessKeyId, connection.Secret),
                    RegionEndpoint.GetBySystemName(connection.Region));
            return client;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var client in _clients.Values) client.Dispose();
            _clients.Clear();
        }
    }
}
