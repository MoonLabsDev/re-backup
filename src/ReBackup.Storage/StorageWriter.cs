namespace ReBackup.Storage;

/// <summary>
/// A write-only stream to a file that is not visible before <see cref="CommitAsync"/>. Disposing it without a prior commit
/// discards the file. Committing twice, or writing after the commit, throws <see cref="InvalidOperationException"/>.
/// </summary>
public abstract class StorageWriter : Stream
{
    /// <summary>Makes the written file visible atomically (and, for an exclusive create, throws <see cref="StorageConflictException"/> when the path was taken meanwhile).</summary>
    public abstract Task CommitAsync(CancellationToken ct);

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
