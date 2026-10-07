using Amazon.S3.Model;

namespace ReBackup.Storage.S3;

/// <summary>The body of a <c>GetObject</c> response as a forward-only stream that reports read failures as <see cref="StorageException"/> subtypes.</summary>
internal sealed class S3ReadStream : Stream
{
    private readonly GetObjectResponse _response;
    private readonly Stream _inner;
    private readonly string _path;

    public S3ReadStream(GetObjectResponse response, string path)
    {
        _response = response;
        _inner = response.ResponseStream;
        _path = path;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return _inner.Read(buffer);
        }
        catch (Exception ex) when (ex is not StorageException)
        {
            throw Translate(ex, CancellationToken.None);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not StorageException)
        {
            throw Translate(ex, cancellationToken);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _response.Dispose();
        }
        base.Dispose(disposing);
    }

    private Exception Translate(Exception ex, CancellationToken ct) => S3Errors.Map(ex, _path, ct);
}
