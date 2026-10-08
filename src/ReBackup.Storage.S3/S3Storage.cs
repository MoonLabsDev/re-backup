using System.Net;
using System.Runtime.CompilerServices;
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

    // DeleteObjects takes at most 1000 keys.
    private const int MaxDeleteBatch = 1000;

    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _prefix;

    /// <summary>
    /// Creates a storage on <paramref name="connection"/> with <paramref name="prefix"/> as its root, talking through
    /// <paramref name="client"/>. The storage does not own the client and never disposes it: open storages through
    /// <see cref="S3StorageFactory"/>, which creates, caches and disposes the clients. Throws <see cref="ArgumentException"/> when the
    /// connection has no secret or the prefix is not a valid storage path.
    /// </summary>
    public S3Storage(S3Connection connection, string prefix, IAmazonS3 client)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(client);
        if (connection.NeedsSecret) throw new ArgumentException("The connection has no secret; it must be entered again.", nameof(connection));
        _prefix = S3Keys.NormalizePrefix(prefix);
        _bucket = connection.Bucket;
        _client = client;
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
            token = NextToken(page, folder);

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

    /// <summary>
    /// Starts an <see cref="S3Writer"/>. An exclusive create checks with <c>HeadObject</c> first; the commit is exclusive too
    /// (<c>If-None-Match</c>) and is the authority. A check answered with 403 leaves existence unknown and goes ahead: without an
    /// effective <c>s3:ListBucket</c> (e.g. one limited by an <c>s3:prefix</c> condition) S3 answers a missing key with 403 instead
    /// of 404. <see cref="CreateOptions.ModifiedUtc"/> and <see cref="CreateOptions.Durable"/> are ignored.
    /// </summary>
    public async Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var key = S3Keys.ToKey(_prefix, path);
        if (path.Length == 0) throw new ArgumentException("A file path is required.", nameof(path));
        if (!options.Overwrite)
        {
            bool exists;
            try
            {
                await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, ct).ConfigureAwait(false);
                exists = true;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                exists = false;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.Forbidden && !ct.IsCancellationRequested)
            {
                // Unknown; the conditional commit decides. Real lack of write permission fails there.
                exists = false;
            }
            catch (Exception ex) when (ex is not StorageException)
            {
                throw S3Errors.Map(ex, path, ct);
            }
            if (exists) throw new StorageConflictException(path);
        }
        return new S3Writer(_client, _bucket, key, path, options.Overwrite);
    }

    /// <summary>
    /// Deletes in <c>DeleteObjects</c> batches of up to 1000 keys, in the given order. What lies below the paths is listed up front,
    /// once per parent directory (see <see cref="ListKeysBelowAsync"/>), not once per path. A path with keys below it other than its
    /// placeholder <c>x/</c> and the keys this same call deletes is a conflict (thrown after the paths before it are deleted; nothing
    /// below it is deleted); a placeholder whose directory is empty then is deleted with the path.
    /// </summary>
    public async Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var targets = paths.Select(p => (Path: p, Key: S3Keys.ToKey(_prefix, p))).ToList();
        foreach (var (p, _) in targets)
            if (p.Length == 0) throw new StorageConflictException(p, "The root cannot be deleted.");

        var below = await ListKeysBelowAsync(targets, ct).ConfigureAwait(false);

        // Everything this call deletes: the keys and the placeholders of all its paths, wherever they stand in the order.
        var deleting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, key) in targets)
        {
            deleting.Add(key);
            deleting.Add(key + "/");
        }

        var batch = new List<(string Key, string Path)>();
        foreach (var (path, key) in targets)
        {
            ct.ThrowIfCancellationRequested();
            var placeholder = key + "/";
            var hasPlaceholder = false;
            foreach (var keyBelow in KeysWithPrefix(below, placeholder))
            {
                if (keyBelow == placeholder) hasPlaceholder = true;
                else if (!deleting.Contains(keyBelow))
                {
                    await DeleteBatchAsync(batch, ct).ConfigureAwait(false);
                    throw new StorageConflictException(path, $"The directory '{path}' is not empty.");
                }
            }

            if (batch.Count + (hasPlaceholder ? 2 : 1) > MaxDeleteBatch) await DeleteBatchAsync(batch, ct).ConfigureAwait(false);
            batch.Add((key, path));
            if (hasPlaceholder) batch.Add((placeholder, path));
        }
        await DeleteBatchAsync(batch, ct).ConfigureAwait(false);
    }

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

    /// <summary>
    /// Every key below the paths that are directories, sorted ordinally. The parent directory of each path is listed once with
    /// delimiter <c>/</c> (its subdirectories come back as common prefixes, so a page answers for up to 1000 paths); a path found
    /// to be a directory is then listed recursively, once. Parents are taken shallow first, so a parent inside a directory already
    /// listed (or already found empty) needs no listing of its own: deleting a whole tree lists its root's parent and the tree.
    /// </summary>
    private async Task<string[]> ListKeysBelowAsync(List<(string Path, string Key)> targets, CancellationToken ct)
    {
        var known = new List<string>();
        var resolved = new HashSet<string>(StringComparer.Ordinal); // requested keys whose whole subtree is known
        var groups = targets.DistinctBy(t => t.Key).GroupBy(t => StoragePath.Parent(t.Path))
            .OrderBy(g => g.Key.Length == 0 ? 0 : g.Key.Count(c => c == '/') + 1);
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (IsInResolved(S3Keys.ToKey(_prefix, group.Key), resolved))
            {
                foreach (var (_, key) in group) resolved.Add(key);
                continue;
            }

            var children = group.ToList();
            var (_, directories) = await ListAllAsync(S3Keys.DirectoryPrefix(_prefix, group.Key), "/", children[0].Path, ct).ConfigureAwait(false);
            foreach (var (path, key) in children)
            {
                if (directories.Contains(key + "/"))
                    known.AddRange((await ListAllAsync(key + "/", null, path, ct).ConfigureAwait(false)).Keys);
                resolved.Add(key);
            }
        }

        var sorted = known.ToArray();
        Array.Sort(sorted, StringComparer.Ordinal);
        return sorted;

        // Whether the key or one of its ancestors is a requested key whose subtree is known.
        static bool IsInResolved(string key, HashSet<string> resolved)
        {
            for (var candidate = key; candidate.Length > 0; candidate = candidate[..Math.Max(candidate.LastIndexOf('/'), 0)])
                if (resolved.Contains(candidate)) return true;
            return false;
        }
    }

    /// <summary>All pages of a listing: the object keys and the common prefixes. Errors are mapped to <paramref name="path"/>.</summary>
    private async Task<(List<string> Keys, HashSet<string> CommonPrefixes)> ListAllAsync(string keyPrefix, string? delimiter, string path, CancellationToken ct)
    {
        var keys = new List<string>();
        var commonPrefixes = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                var page = await _client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = _bucket,
                    Prefix = keyPrefix.Length == 0 ? null : keyPrefix,
                    Delimiter = delimiter,
                    ContinuationToken = token,
                }, ct).ConfigureAwait(false);
                token = NextToken(page, path);
                keys.AddRange((page.S3Objects ?? []).Select(o => o.Key));
                commonPrefixes.UnionWith(page.CommonPrefixes ?? []);
            }
            while (token is not null);
        }
        catch (Exception ex) when (ex is not StorageException)
        {
            throw S3Errors.Map(ex, path, ct);
        }
        return (keys, commonPrefixes);
    }

    /// <summary>The keys of the ordinally sorted <paramref name="sorted"/> that start with <paramref name="prefix"/> (a contiguous run).</summary>
    private static IEnumerable<string> KeysWithPrefix(string[] sorted, string prefix)
    {
        var index = Array.BinarySearch(sorted, prefix, StringComparer.Ordinal);
        for (index = index < 0 ? ~index : index; index < sorted.Length && sorted[index].StartsWith(prefix, StringComparison.Ordinal); index++)
            yield return sorted[index];
    }

    /// <summary>Deletes the queued keys with one <c>DeleteObjects</c> and empties the queue. Per-key errors are mapped; a missing key is none.</summary>
    private async Task DeleteBatchAsync(List<(string Key, string Path)> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        var request = new DeleteObjectsRequest
        {
            BucketName = _bucket,
            Objects = batch.Select(b => new KeyVersion { Key = b.Key }).ToList(),
            Quiet = true,
        };

        DeleteObjectsResponse response;
        try
        {
            response = await _client.DeleteObjectsAsync(request, ct).ConfigureAwait(false);
        }
        catch (DeleteObjectsException ex) when (ex.Response is not null)
        {
            // The SDK may raise the per-key errors as an exception instead of returning them. Without a response nothing says
            // which keys were deleted, so that case fails below like any other error.
            response = ex.Response;
        }
        catch (Exception ex) when (ex is not StorageException)
        {
            throw S3Errors.Map(ex, batch[0].Path, ct);
        }

        foreach (var error in response?.DeleteErrors ?? [])
        {
            if (error.Code == "NoSuchKey") continue;
            var path = batch.FirstOrDefault(b => b.Key == error.Key).Path ?? batch[0].Path;
            var status = error.Code switch
            {
                "AccessDenied" => HttpStatusCode.Forbidden,
                "InternalError" or "SlowDown" or "ServiceUnavailable" => HttpStatusCode.ServiceUnavailable,
                _ => default,
            };
            throw S3Errors.Map(new AmazonS3Exception($"DeleteObjects failed for a key ({error.Code}).", ErrorType.Unknown, error.Code, null, status), path, ct);
        }
        batch.Clear();
    }

    /// <summary>The token for the next page; a truncated page without one fails closed instead of ending the listing early.</summary>
    private static string? NextToken(ListObjectsV2Response page, string path)
    {
        if (page.IsTruncated != true) return null;
        return string.IsNullOrEmpty(page.NextContinuationToken)
            ? throw new StorageIOException(path, $"The listing at '{path}' is truncated but has no continuation token.")
            : page.NextContinuationToken;
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
