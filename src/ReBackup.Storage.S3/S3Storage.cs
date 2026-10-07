using System.Net;
using System.Runtime.CompilerServices;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Storage.S3;

/// <summary>
/// A storage on an Amazon S3 bucket; the storage root is a key prefix inside it (<c>""</c> = the whole bucket). S3 has no real
/// directories: a directory exists while an object lies below it, and keys ending in <c>/</c> (console placeholders) are directories, never files.
/// </summary>
public sealed class S3Storage : IStorage
{
    // S3 directories have no time of their own.
    private static readonly DateTime DirectoryTime = DateTime.UnixEpoch;

    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _prefix;

    /// <summary>
    /// Creates a storage on <paramref name="connection"/> with <paramref name="prefix"/> as its root. The client is created from the
    /// connection's region and access key unless one is given (tests). Throws <see cref="ArgumentException"/> when the connection has
    /// no secret or the prefix is not a valid storage path.
    /// </summary>
    public S3Storage(S3Connection connection, string prefix, IAmazonS3? client = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.NeedsSecret) throw new ArgumentException("The connection has no secret; it must be entered again.", nameof(connection));
        _prefix = S3Keys.NormalizePrefix(prefix);
        _bucket = connection.Bucket;
        _client = client ?? new AmazonS3Client(
            new BasicAWSCredentials(connection.AccessKeyId, connection.Secret),
            RegionEndpoint.GetBySystemName(connection.Region));
    }

    public StorageCapabilities Capabilities => StorageCapabilities.CaseSensitive;

    public async Task<StorageEntry?> StatAsync(string path, CancellationToken ct)
    {
        var key = S3Keys.ToKey(_prefix, path);
        try
        {
            if (path.Length == 0)
            {
                // The root exists when the bucket does.
                await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = S3Keys.DirectoryPrefix(_prefix, ""), MaxKeys = 1 }, ct).ConfigureAwait(false);
                return new StorageEntry(path, true, 0, DirectoryTime, false, null);
            }

            try
            {
                var head = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, ct).ConfigureAwait(false);
                return new StorageEntry(path, false, head.ContentLength, UtcOf(head.LastModified), false, Unquote(head.ETag));
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                var below = await _client.ListObjectsV2Async(
                    new ListObjectsV2Request { BucketName = _bucket, Prefix = key + "/", MaxKeys = 1 }, ct).ConfigureAwait(false);
                return below.S3Objects is { Count: > 0 } ? new StorageEntry(path, true, 0, DirectoryTime, false, null) : null;
            }
        }
        catch (Exception ex) when (ex is not StorageException)
        {
            throw S3Errors.Map(ex, path, ct);
        }
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive, [EnumeratorCancellation] CancellationToken ct)
    {
        var keyPrefix = S3Keys.DirectoryPrefix(_prefix, folder);
        var emittedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var any = false;
        string? token = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var page = await ListPageAsync(folder, keyPrefix, recursive, token, ct).ConfigureAwait(false);
            token = page.IsTruncated == true ? page.NextContinuationToken : null;

            foreach (var commonPrefix in page.CommonPrefixes ?? [])
            {
                any = true;
                var relative = commonPrefix[keyPrefix.Length..].TrimEnd('/');
                if (IsValidRelative(relative) && emittedDirectories.Add(relative))
                    yield return Directory(StoragePath.Combine(folder, relative));
            }

            foreach (var item in page.S3Objects ?? [])
            {
                any = true;
                var relative = item.Key[keyPrefix.Length..];
                if (relative.Length == 0) continue; // the folder's own placeholder
                var isPlaceholder = relative.EndsWith('/');
                if (isPlaceholder) relative = relative.TrimEnd('/');
                if (!IsValidRelative(relative)) continue;

                if (recursive)
                {
                    // Directories are implied by the keys below them; each is reported once, before its first child.
                    for (var slash = relative.IndexOf('/'); slash >= 0; slash = relative.IndexOf('/', slash + 1))
                    {
                        var parent = relative[..slash];
                        if (emittedDirectories.Add(parent)) yield return Directory(StoragePath.Combine(folder, parent));
                    }
                    if (isPlaceholder && emittedDirectories.Add(relative)) yield return Directory(StoragePath.Combine(folder, relative));
                }

                if (isPlaceholder) continue;
                yield return new StorageEntry(StoragePath.Combine(folder, relative), false, item.Size ?? 0, UtcOf(item.LastModified), false, Unquote(item.ETag));
            }
        }
        while (token is not null);

        // The root prefix without objects is an existing empty folder; any other folder exists only while something lies below it.
        if (!any && folder.Length > 0) throw new StorageNotFoundException(folder);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var key = S3Keys.ToKey(_prefix, path);
        if (path.Length == 0) throw new StorageNotFoundException(path);
        try
        {
            var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key }, ct).ConfigureAwait(false);
            return new S3ReadStream(response, path);
        }
        catch (Exception ex) when (ex is not StorageException)
        {
            throw S3Errors.Map(ex, path, ct);
        }
    }

    public Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task EnsureDirectoryAsync(string path, CancellationToken ct)
    {
        S3Keys.ToKey(_prefix, path);
        return Task.CompletedTask;
    }

    public Task<long?> GetFreeSpaceAsync(CancellationToken ct) => Task.FromResult<long?>(null);

    private async Task<ListObjectsV2Response> ListPageAsync(string folder, string keyPrefix, bool recursive, string? token, CancellationToken ct)
    {
        try
        {
            return await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = keyPrefix.Length == 0 ? null : keyPrefix,
                Delimiter = recursive ? null : "/",
                ContinuationToken = token,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not StorageException)
        {
            throw S3Errors.Map(ex, folder, ct);
        }
    }

    private static StorageEntry Directory(string path) => new(path, true, 0, DirectoryTime, false, null);

    /// <summary>False for key remainders that are no valid storage path (empty, double or leading <c>/</c>, <c>.</c> and <c>..</c> segments, backslash): such objects cannot be addressed and are not listed.</summary>
    private static bool IsValidRelative(string relative)
    {
        if (relative.Length == 0) return false;
        try
        {
            StoragePath.Validate(relative);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The SDK may hand out <see cref="DateTimeKind.Unspecified"/> times; S3 times are UTC, so those are taken as UTC, not as local time.</summary>
    internal static DateTime UtcOf(DateTime? value) => value switch
    {
        null => DirectoryTime,
        { Kind: DateTimeKind.Unspecified } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        { } v => v.ToUniversalTime(),
    };

    private static string? Unquote(string? etag) => etag?.Trim('"');
}
