using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ReBackup.Storage;

namespace ReBackup.Core.Tests.TestSupport;

/// <summary>
/// Passes every call to <c>inner</c>, with asynchronous hooks before a file is opened and before a file is committed, for
/// tests that hold copies in flight at a known point and release them in a chosen order. The hooks get the caller's
/// token, so a copy held in a hook sees the run being canceled. Counts the files open for reading at the same time, the
/// writers committed and the writers disposed without a commit (aborted).
/// </summary>
public sealed class GatedStorage(IStorage inner, StorageCapabilities? capabilities = null) : IStorage
{
    private int _opens;
    private int _inFlight;
    private int _maxInFlight;
    private int _aborted;
    private int _freeSpaceQueries;

    /// <summary>Runs before a file is opened for reading, with its path; what it throws, the open throws.</summary>
    public Func<string, CancellationToken, Task>? BeforeOpen { get; init; }

    /// <summary>Runs before <see cref="StatAsync"/>, with the path; what it throws, the stat throws.</summary>
    public Func<string, CancellationToken, Task>? BeforeStat { get; init; }

    /// <summary>Runs before a folder is listed, with its path; what it throws, the listing throws.</summary>
    public Func<string, CancellationToken, Task>? BeforeList { get; init; }

    /// <summary>Runs before a file is committed, with its path; what it throws, the commit throws.</summary>
    public Func<string, CancellationToken, Task>? BeforeCommit { get; init; }

    /// <summary>Runs after a commit has succeeded, with the path.</summary>
    public Action<string>? AfterCommit { get; init; }

    /// <summary>How often <see cref="OpenReadAsync"/> was called.</summary>
    public int Opens => Volatile.Read(ref _opens);

    /// <summary>Files opened for reading and not yet closed (an open that failed counts until it fails).</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>The most files that were open for reading at the same time.</summary>
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>Writers disposed without a commit.</summary>
    public int Aborted => Volatile.Read(ref _aborted);

    /// <summary>How often <see cref="GetFreeSpaceAsync"/> was called.</summary>
    public int FreeSpaceQueries => Volatile.Read(ref _freeSpaceQueries);

    /// <summary>The paths committed, in the order the commits finished.</summary>
    public ConcurrentQueue<string> Commits { get; } = new();

    public StorageCapabilities Capabilities => capabilities ?? inner.Capabilities;

    public async Task<StorageEntry?> StatAsync(string path, CancellationToken ct)
    {
        if (BeforeStat is { } before)
            await before(path, ct);
        return await inner.StatAsync(path, ct);
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive, [EnumeratorCancellation] CancellationToken ct)
    {
        if (BeforeList is { } before)
            await before(folder, ct);
        await foreach (var entry in inner.ListAsync(folder, recursive, ct))
            yield return entry;
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        Interlocked.Increment(ref _opens);
        var now = Interlocked.Increment(ref _inFlight);
        for (var max = MaxInFlight; now > max; max = MaxInFlight)
            Interlocked.CompareExchange(ref _maxInFlight, now, max);
        try
        {
            if (BeforeOpen is { } before)
                await before(path, ct);
            return new ClosingStream(await inner.OpenReadAsync(path, ct), () => Interlocked.Decrement(ref _inFlight));
        }
        catch
        {
            Interlocked.Decrement(ref _inFlight);
            throw;
        }
    }

    public async Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct) =>
        new Writer(this, path, await inner.CreateAsync(path, options, ct));

    public Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct) => inner.DeleteAsync(paths, ct);

    public Task EnsureDirectoryAsync(string path, CancellationToken ct) => inner.EnsureDirectoryAsync(path, ct);

    public Task<long?> GetFreeSpaceAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _freeSpaceQueries);
        return inner.GetFreeSpaceAsync(ct);
    }

    private sealed class Writer(GatedStorage owner, string path, StorageWriter inner) : StorageWriter
    {
        private bool _committed;
        private int _disposed;

        public override bool CanWrite => inner.CanWrite;
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Flush() => inner.Flush();

        public override async Task CommitAsync(CancellationToken ct)
        {
            if (owner.BeforeCommit is { } before)
                await before(path, ct);
            await inner.CommitAsync(ct);
            _committed = true;
            owner.Commits.Enqueue(path);
            owner.AfterCommit?.Invoke(path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                if (!_committed)
                    Interlocked.Increment(ref owner._aborted);
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>A read stream that tells when it is closed.</summary>
    private sealed class ClosingStream(Stream inner, Action onClose) : Stream
    {
        private int _closed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
            {
                inner.Dispose();
                onClose();
            }
            base.Dispose(disposing);
        }
    }
}
