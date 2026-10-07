namespace ReBackup.Storage.FileSystem;

/// <summary>A read-only stream over an open file that reports I/O failures (lock, unplugged drive) as <see cref="StorageException"/> subtypes.</summary>
internal sealed class FileSystemReadStream : Stream
{
    private readonly Stream _inner;
    private readonly FileSystemStorage _storage;
    private readonly string _path;

    public FileSystemReadStream(FileSystemStorage storage, string path, Stream inner)
    {
        _storage = storage;
        _path = path;
        _inner = inner;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => Map(() => _inner.Length);

    public override long Position
    {
        get => Map(() => _inner.Position);
        set => Map(() => { _inner.Position = value; return 0; });
    }

    public override int Read(byte[] buffer, int offset, int count) => Map(() => _inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return _inner.Read(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Translate(ex);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Translate(ex);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => Map(() => _inner.Seek(offset, origin));

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    private T Map<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Translate(ex);
        }
    }

    private Exception Translate(Exception ex) => _storage.MapError(ex, _path) ?? ex;
}
