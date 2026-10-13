using System.Runtime.ExceptionServices;

namespace ReBackup.Storage.GoogleDrive;

/// <summary>
/// Writes a Drive file: the content is buffered in a temporary file (deleted on close) and uploaded on commit, so nothing is
/// visible before. A failed commit is remembered: later writes and commits throw it again.
/// </summary>
internal sealed class DriveWriter : StorageWriter
{
    private readonly DriveStorage _storage;
    private readonly string _path;
    private readonly CreateOptions _options;
    private FileStream? _buffer;
    private ExceptionDispatchInfo? _failure;
    private bool _committed;
    private bool _disposed;

    public DriveWriter(DriveStorage storage, string path, CreateOptions options)
    {
        _storage = storage;
        _path = path;
        _options = options;
    }

    public override bool CanWrite => !_disposed && !_committed;

    public override void Flush()
    {
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureWritable();
        Buffer().Write(buffer, offset, count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await Buffer().WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override async Task CommitAsync(CancellationToken ct)
    {
        EnsureWritable();
        ct.ThrowIfCancellationRequested();
        var content = Buffer();
        await content.FlushAsync(ct).ConfigureAwait(false);
        try
        {
            await _storage.CommitAsync(_path, _options, content, ct).ConfigureAwait(false);
        }
        catch (StorageException ex)
        {
            _failure = ExceptionDispatchInfo.Capture(ex);
            throw;
        }
        _committed = true;
        await ReleaseAsync().ConfigureAwait(false);
    }

    public override async ValueTask DisposeAsync()
    {
        _disposed = true;
        await ReleaseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            _buffer?.Dispose();
            _buffer = null;
        }
        base.Dispose(disposing);
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed) throw new InvalidOperationException("The file has already been committed.");
        _failure?.Throw();
    }

    private FileStream Buffer() => _buffer ??= new FileStream(
        Path.Combine(Path.GetTempPath(), "rebackup-drive-" + Guid.NewGuid().ToString("N") + ".tmp"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81_920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);

    private async Task ReleaseAsync()
    {
        if (_buffer is null) return;
        await _buffer.DisposeAsync().ConfigureAwait(false);
        _buffer = null;
    }
}
