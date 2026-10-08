using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;

namespace ReBackup.Storage.S3;

/// <summary>
/// Writes an S3 object. Up to <see cref="InitialPartSize"/> is buffered and sent with one <c>PutObject</c> on commit; beyond that the
/// object goes up as a multipart upload, one part uploading while the next one fills (at most two part buffers in memory). The
/// object becomes visible only when the commit succeeds; disposing without commit aborts the multipart upload (best effort).
/// </summary>
internal sealed class S3Writer : StorageWriter
{
    /// <summary>The size of the first 1000 parts, and the most a single <c>PutObject</c> carries.</summary>
    internal const int InitialPartSize = 16 * 1024 * 1024;

    /// <summary>S3 accepts at most 10 000 parts per upload.</summary>
    internal const int MaxParts = 10_000;

    private const int PartsPerSize = 1000;

    // 16 MiB << 6 = 1 GiB: the largest power of two an int (and so a byte array) holds.
    private const int MaxDoublings = 6;

    private const int FirstBufferSize = 81_920;

    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _key;
    private readonly string _path;
    private readonly bool _overwrite;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly List<PartETag> _parts = [];

    private byte[] _buffer = [];
    private int _filled;
    private int _partNumber = 1;
    private string? _uploadId;
    private Task? _pendingUpload;
    private byte[]? _uploadingBuffer;
    private ExceptionDispatchInfo? _failure;
    private bool _committed;
    private bool _disposed;

    public S3Writer(IAmazonS3 client, string bucket, string key, string path, bool overwrite)
    {
        _client = client;
        _bucket = bucket;
        _key = key;
        _path = path;
        _overwrite = overwrite;
    }

    /// <summary>The size of part <paramref name="partNumber"/> (1-based): 16 MiB for parts 1–1000, doubling after every 1000 parts, at most 1 GiB.</summary>
    internal static int PartSizeFor(int partNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partNumber, 1);
        return InitialPartSize << Math.Min((partNumber - 1) / PartsPerSize, MaxDoublings);
    }

    public override bool CanWrite => !_disposed && !_committed;

    public override void Flush()
    {
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        while (!buffer.IsEmpty)
        {
            var partSize = PartSizeFor(_partNumber);
            // A full part goes up only once more data follows, so an object of exactly one part size stays a single put.
            if (_filled == partSize)
            {
                await StartPartUploadAsync(cancellationToken).ConfigureAwait(false);
                partSize = PartSizeFor(_partNumber);
            }

            var count = Math.Min(buffer.Length, partSize - _filled);
            Reserve(_filled + count, partSize);
            buffer.Span[..count].CopyTo(_buffer.AsSpan(_filled));
            _filled += count;
            buffer = buffer[count..];
        }
    }

    public override async Task CommitAsync(CancellationToken ct)
    {
        EnsureWritable();
        ct.ThrowIfCancellationRequested();
        try
        {
            if (_uploadId is null)
            {
                await _client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = _bucket,
                    Key = _key,
                    InputStream = new MemoryStream(_buffer, 0, _filled, writable: false),
                    Headers = { ContentLength = _filled },
                    IfNoneMatch = _overwrite ? null : "*",
                }, ct).ConfigureAwait(false);
            }
            else
            {
                await WaitForPendingUploadAsync().ConfigureAwait(false);
                await UploadPartAsync(_buffer, _filled, _partNumber, ct).ConfigureAwait(false);
                await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
                {
                    BucketName = _bucket,
                    Key = _key,
                    UploadId = _uploadId,
                    PartETags = _parts,
                    IfNoneMatch = _overwrite ? null : "*",
                }, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!_overwrite && S3Errors.IsConditionalConflict(ex))
        {
            if (!await IsOwnObjectAsync(ct).ConfigureAwait(false)) throw Fail(ex, ct);
        }
        catch (Exception ex)
        {
            throw Fail(ex, ct);
        }
        _committed = true;
        _buffer = [];
        _uploadingBuffer = null;
    }

    /// <summary>
    /// After an exclusive commit was refused (412, or 409 <c>ConditionalRequestConflict</c>): whether the object now at the key is
    /// provably this writer's, i.e. the SDK retried a first attempt that had succeeded unseen. The proof is the object's ETag: for a
    /// single put the MD5 of the buffer, for multipart the MD5 of the binary part MD5s (the ETags S3 returned for our parts) + "-" +
    /// part count. Any doubt is no proof and the conflict stands: an ETag that is no MD5 (e.g. under SSE-KMS), a failed HEAD.
    /// Cancellation by <paramref name="ct"/> during the HEAD throws <see cref="OperationCanceledException"/>.
    /// </summary>
    private async Task<bool> IsOwnObjectAsync(CancellationToken ct)
    {
        var expected = _uploadId is null ? Md5Hex(_buffer.AsSpan(0, _filled)) : MultipartETag(_parts);
        if (expected is null) return false;
        try
        {
            var head = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = _key }, ct).ConfigureAwait(false);
            return string.Equals(head.ETag?.Trim('"'), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Cancelled by the caller: cancellation, not a conflict to remember (a later commit may still find its object).
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    private static string Md5Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(MD5.HashData(data));

    /// <summary>The ETag S3 gives a multipart object made of <paramref name="parts"/>; <c>null</c> when a part's ETag is no MD5.</summary>
    internal static string? MultipartETag(IEnumerable<PartETag> parts)
    {
        var ordered = parts.OrderBy(p => p.PartNumber).ToList();
        var md5s = new byte[ordered.Count * 16];
        for (var i = 0; i < ordered.Count; i++)
        {
            var hex = ordered[i].ETag?.Trim('"');
            if (hex is not { Length: 32 } || !hex.All(char.IsAsciiHexDigit)) return null;
            Convert.FromHexString(hex).CopyTo(md5s, i * 16);
        }
        return Md5Hex(md5s) + "-" + ordered.Count;
    }

    public override async ValueTask DisposeAsync()
    {
        await CleanUpAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) CleanUpAsync().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    /// <summary>Throws when the writer takes no more data or commits: disposed, committed, or failed before (that failure again).</summary>
    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed) throw new InvalidOperationException("The file has already been committed.");
        _failure?.Throw();
    }

    /// <summary>Maps <paramref name="ex"/> and remembers it, so every later write or commit reports the same failure.</summary>
    private Exception Fail(Exception ex, CancellationToken ct)
    {
        var mapped = S3Errors.Map(ex, _path, ct);
        _failure ??= ExceptionDispatchInfo.Capture(mapped);
        return mapped;
    }

    /// <summary>Grows the first part's buffer as data comes in, so small files never allocate a whole part.</summary>
    private void Reserve(int needed, int partSize)
    {
        if (needed <= _buffer.Length) return;
        var size = Math.Min(partSize, Math.Max(needed, Math.Max(FirstBufferSize, _buffer.Length * 2)));
        Array.Resize(ref _buffer, size);
    }

    /// <summary>Sends the full buffer as the next part and continues in a fresh one; waits for the previous part first.</summary>
    private async Task StartPartUploadAsync(CancellationToken ct)
    {
        try
        {
            if (_partNumber >= MaxParts)
                throw new StorageIOException(_path, $"'{_path}' is larger than an S3 object can be.");

            if (_uploadId is null)
            {
                var initiated = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
                {
                    BucketName = _bucket,
                    Key = _key,
                    ChecksumAlgorithm = ChecksumAlgorithm.CRC32,
                }, ct).ConfigureAwait(false);
                _uploadId = initiated.UploadId;
            }

            await WaitForPendingUploadAsync().ConfigureAwait(false);
            var spare = _uploadingBuffer;
            _uploadingBuffer = _buffer;
            _pendingUpload = UploadPartAsync(_buffer, _filled, _partNumber, ct);

            _partNumber++;
            var nextSize = PartSizeFor(_partNumber);
            _buffer = spare?.Length == nextSize ? spare : new byte[nextSize];
            _filled = 0;
        }
        catch (Exception ex)
        {
            throw Fail(ex, ct);
        }
    }

    private async Task WaitForPendingUploadAsync()
    {
        var pending = _pendingUpload;
        _pendingUpload = null;
        if (pending is not null) await pending.ConfigureAwait(false);
    }

    /// <summary>Uploads one part. The SDK gets its own read-only view of the buffer, so it may seek (retries) and dispose it freely.</summary>
    private async Task UploadPartAsync(byte[] buffer, int length, int partNumber, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        try
        {
            var response = await _client.UploadPartAsync(new UploadPartRequest
            {
                BucketName = _bucket,
                Key = _key,
                UploadId = _uploadId,
                PartNumber = partNumber,
                PartSize = length,
                InputStream = new MemoryStream(buffer, 0, length, writable: false),
                ChecksumAlgorithm = ChecksumAlgorithm.CRC32,
            }, linked.Token).ConfigureAwait(false);
            _parts.Add(new PartETag(response, copyChecksums: true) { PartNumber = partNumber });
        }
        catch (Exception ex) when (ex is not StorageException)
        {
            throw S3Errors.Map(ex, _path, ct);
        }
    }

    /// <summary>Without a successful commit: stops a part upload in flight and aborts the multipart upload. Runs once and never throws.</summary>
    private async Task CleanUpAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_committed || _uploadId is null) return;
            await _disposeCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await WaitForPendingUploadAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Its failure no longer matters: the whole upload is aborted below.
            }
            try
            {
                await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = _bucket,
                    Key = _key,
                    UploadId = _uploadId,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: a lifecycle rule ("abort incomplete multipart uploads") removes what is left.
            }
        }
        finally
        {
            _buffer = [];
            _uploadingBuffer = null;
            _disposeCts.Dispose();
        }
    }
}
