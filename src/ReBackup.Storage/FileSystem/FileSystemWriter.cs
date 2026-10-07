namespace ReBackup.Storage.FileSystem;

/// <summary>Writes to a temp file next to the target and renames it on commit.</summary>
internal sealed class FileSystemWriter : StorageWriter
{
    private readonly FileSystemStorage _storage;
    private readonly string _path;
    private readonly string _target;
    private readonly string _temp;
    private readonly CreateOptions _options;
    private Stream? _stream;
    private bool _committed;

    public FileSystemWriter(FileSystemStorage storage, string path, string target, string temp, Stream stream, CreateOptions options)
    {
        _storage = storage;
        _path = path;
        _target = target;
        _temp = temp;
        _stream = stream;
        _options = options;
    }

    public override bool CanWrite => _stream is not null;

    private Stream Open()
    {
        if (_committed) throw new InvalidOperationException("The file has already been committed.");
        return _stream ?? throw new ObjectDisposedException(nameof(FileSystemWriter));
    }

    private static bool IsIo(Exception ex) => ex is IOException or UnauthorizedAccessException;

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var stream = Open();
        try
        {
            stream.Write(buffer);
        }
        catch (Exception ex) when (IsIo(ex))
        {
            var mapped = _storage.MapError(ex, _path);
            if (mapped is null) throw;
            throw mapped;
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var stream = Open();
        try
        {
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsIo(ex))
        {
            var mapped = _storage.MapError(ex, _path);
            if (mapped is null) throw;
            throw mapped;
        }
    }

    public override void Flush()
    {
        if (_stream is null || _committed) return;
        try
        {
            _stream.Flush();
        }
        catch (Exception ex) when (IsIo(ex))
        {
            var mapped = _storage.MapError(ex, _path);
            if (mapped is null) throw;
            throw mapped;
        }
    }

    public override async Task CommitAsync(CancellationToken ct)
    {
        var stream = Open();
        ct.ThrowIfCancellationRequested();
        try
        {
            await stream.FlushAsync(ct).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;

            if (_options.ModifiedUtc is { } modified)
            {
                try
                {
                    File.SetLastWriteTimeUtc(_temp, modified);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
                {
                    // The copy itself is fine; only the time stays at "now".
                }
            }

            // overwrite:false makes the rename itself exclusive: a path taken in the meantime surfaces as "exists".
            File.Move(_temp, _target, _options.Overwrite);
            _committed = true;
        }
        catch (Exception ex) when (IsIo(ex))
        {
            var mapped = _storage.MapError(ex, _path);
            if (mapped is null) throw;
            throw mapped;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var stream = _stream;
            _stream = null;
            if (stream is not null)
            {
                // Disposing flushes buffered bytes, which fails again after a full disk: drop them first and never let disposal throw.
                if (!_committed)
                {
                    try
                    {
                        stream.SetLength(0);
                    }
                    catch (Exception ex) when (IsIo(ex) || ex is NotSupportedException)
                    {
                    }
                }
                try
                {
                    stream.Dispose();
                }
                catch (Exception ex) when (IsIo(ex))
                {
                }
            }
            if (!_committed)
            {
                try
                {
                    File.Delete(_temp);
                }
                catch (Exception ex) when (IsIo(ex))
                {
                    // Best effort: a leftover temp file is invisible to listings.
                }
            }
        }
        base.Dispose(disposing);
    }
}
