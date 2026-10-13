namespace ReBackup.Storage.GoogleDrive;

/// <summary>
/// The body of a download or export, read as it arrives. <c>HttpClient.Timeout</c> ends with the response headers, so a connection
/// that stalls while the body arrives would hang a read forever: every read here gets <paramref name="idleTimeout"/> to return
/// data. A read that gets none in time throws <see cref="TimeoutException"/> (Drive "unavailable" for the storage); cancellation by
/// the caller stays <see cref="OperationCanceledException"/>. Disposing the stream disposes the body and <paramref name="owner"/>
/// (the HTTP response).
/// </summary>
internal sealed class DriveResponseStream(Stream inner, IDisposable? owner, TimeSpan idleTimeout) : Stream
{
    /// <summary>How long a read of a download body may wait for data by default.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(2);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(idleTimeout);
        try
        {
            return await inner.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && idle.IsCancellationRequested)
        {
            throw new TimeoutException($"No data arrived from Google Drive for {idleTimeout.TotalSeconds:0.#} s.", ex);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Reads through <see cref="ReadAsync(Memory{byte}, CancellationToken)"/>, so the idle timeout holds here too.</summary>
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            owner?.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        owner?.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
