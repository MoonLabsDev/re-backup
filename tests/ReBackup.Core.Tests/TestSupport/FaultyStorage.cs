using System.Runtime.CompilerServices;
using ReBackup.Storage;

namespace ReBackup.Core.Tests.TestSupport;

/// <summary>
/// Passes every call to <c>inner</c>, with switches to make single paths fail, to dictate the free space and to act right
/// before a call (the moment to change something behind the caller's back, or to throw). <see cref="Before"/> gets the
/// operation (<c>stat</c>, <c>list</c>, <c>open</c>, <c>create</c>, <c>commit</c>, <c>delete</c>, <c>ensure</c>) and the
/// path; a <c>delete</c> batch calls it once per path, in order.
/// </summary>
public sealed class FaultyStorage(IStorage inner) : IStorage
{
    private readonly object _gate = new();
    private readonly List<(string Path, CreateOptions Options)> _created = [];

    /// <summary>Free space to report; the inner storage's when null.</summary>
    public Func<long?>? FreeSpace { get; init; }

    /// <summary>Gets the path of a file to create; true makes <see cref="CreateAsync"/> throw <see cref="StorageAccessDeniedException"/>.</summary>
    public Func<string, bool> FailCreate { get; init; } = _ => false;

    /// <summary>Gets the path of a file to commit; true makes the commit throw <see cref="StorageAccessDeniedException"/>.</summary>
    public Func<string, bool> FailCommit { get; init; } = _ => false;

    /// <summary>Gets each path of a deletion; true makes the deletion throw <see cref="StorageLockedException"/> at that path.</summary>
    public Func<string, bool> FailDelete { get; init; } = _ => false;

    /// <summary>Gets the path of a file being written; true makes every write throw <see cref="StorageFullException"/>.</summary>
    public Func<string, bool> DiskFullOnWrite { get; init; } = _ => false;

    /// <summary>Runs before every call with the operation and the path.</summary>
    public Action<string, string>? Before { get; init; }

    /// <summary>The paths <see cref="CreateAsync"/> started writing, in order.</summary>
    public IReadOnlyList<string> Created
    {
        get { lock (_gate) return _created.Select(created => created.Path).ToList(); }
    }

    /// <summary>The paths <see cref="CreateAsync"/> started writing with the options they were created with, in order.</summary>
    public IReadOnlyList<(string Path, CreateOptions Options)> Creates
    {
        get { lock (_gate) return _created.ToList(); }
    }

    public StorageCapabilities Capabilities => inner.Capabilities;

    public Task<StorageEntry?> StatAsync(string path, CancellationToken ct)
    {
        Before?.Invoke("stat", path);
        return inner.StatAsync(path, ct);
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive, [EnumeratorCancellation] CancellationToken ct)
    {
        Before?.Invoke("list", folder);
        await foreach (var entry in inner.ListAsync(folder, recursive, ct))
            yield return entry;
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        Before?.Invoke("open", path);
        return inner.OpenReadAsync(path, ct);
    }

    public async Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct)
    {
        Before?.Invoke("create", path);
        if (FailCreate(path))
            throw new StorageAccessDeniedException(path);
        var writer = await inner.CreateAsync(path, options, ct);
        lock (_gate) _created.Add((path, options));
        return new Writer(this, path, writer);
    }

    public async Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        foreach (var path in paths)
        {
            Before?.Invoke("delete", path);
            if (FailDelete(path))
                throw new StorageLockedException(path);
            await inner.DeleteAsync([path], ct);
        }
    }

    public Task EnsureDirectoryAsync(string path, CancellationToken ct)
    {
        Before?.Invoke("ensure", path);
        return inner.EnsureDirectoryAsync(path, ct);
    }

    public Task<long?> GetFreeSpaceAsync(CancellationToken ct) =>
        FreeSpace is { } free ? Task.FromResult(free()) : inner.GetFreeSpaceAsync(ct);

    private sealed class Writer(FaultyStorage owner, string path, StorageWriter inner) : StorageWriter
    {
        public override bool CanWrite => inner.CanWrite;

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfFull();
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfFull();
            inner.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfFull();
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfFull();
            return inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override void Flush() => inner.Flush();

        public override Task CommitAsync(CancellationToken ct)
        {
            owner.Before?.Invoke("commit", path);
            if (owner.FailCommit(path))
                throw new StorageAccessDeniedException(path);
            return inner.CommitAsync(ct);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        private void ThrowIfFull()
        {
            if (owner.DiskFullOnWrite(path))
                throw new StorageFullException(path);
        }
    }
}
