using System.Runtime.CompilerServices;
using ReBackup.Storage;

namespace ReBackup.Core.Tests.TestSupport;

/// <summary>
/// Passes every call to <paramref name="inner"/>, with knobs for what a remote storage does differently: read streams that
/// cannot seek, a root that cannot be reached. Counts the opened files.
/// </summary>
public sealed class WrappedStorage(IStorage inner) : IStorage
{
    /// <summary>Read streams report <c>CanSeek = false</c> and refuse <c>Length</c>, <c>Position</c> and <c>Seek</c>.</summary>
    public bool NonSeekable { get; init; }

    /// <summary>Every call throws <see cref="StorageUnavailableException"/>, as for an offline share.</summary>
    public bool Unavailable { get; init; }

    /// <summary>How often <see cref="OpenReadAsync"/> was called.</summary>
    public int Opens { get; private set; }

    public StorageCapabilities Capabilities => inner.Capabilities;

    public Task<StorageEntry?> StatAsync(string path, CancellationToken ct)
    {
        ThrowIfUnavailable();
        return inner.StatAsync(path, ct);
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive, [EnumeratorCancellation] CancellationToken ct)
    {
        ThrowIfUnavailable();
        await foreach (var entry in inner.ListAsync(folder, recursive, ct))
            yield return entry;
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        ThrowIfUnavailable();
        Opens++;
        var stream = await inner.OpenReadAsync(path, ct);
        return NonSeekable ? new ForwardOnlyStream(stream) : stream;
    }

    public Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct)
    {
        ThrowIfUnavailable();
        return inner.CreateAsync(path, options, ct);
    }

    public Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        ThrowIfUnavailable();
        return inner.DeleteAsync(paths, ct);
    }

    public Task EnsureDirectoryAsync(string path, CancellationToken ct)
    {
        ThrowIfUnavailable();
        return inner.EnsureDirectoryAsync(path, ct);
    }

    public Task<long?> GetFreeSpaceAsync(CancellationToken ct) => inner.GetFreeSpaceAsync(ct);

    private void ThrowIfUnavailable()
    {
        if (Unavailable)
            throw new StorageUnavailableException("");
    }

    /// <summary>A read stream like a network response body: forward only, length unknown.</summary>
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
