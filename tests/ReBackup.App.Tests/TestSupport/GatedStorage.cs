using System.IO;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests.TestSupport;

/// <summary>
/// An <see cref="InMemoryStorage"/> whose reads of the paths <see cref="Holds"/> picks wait for <see cref="Gate"/> (or for
/// their token), so that a test can look at a job while it is in the middle of reading.
/// </summary>
public sealed class GatedStorage(InMemoryStorage inner) : IStorage
{
    public InMemoryStorage Inner { get; } = inner;

    /// <summary>The paths whose reads wait; none by default.</summary>
    public Func<string, bool> Holds { get; set; } = _ => false;

    /// <summary>Opened when the test lets the reads go on.</summary>
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed when the first held read starts waiting.</summary>
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StorageCapabilities Capabilities => Inner.Capabilities;

    public Task<StorageEntry?> StatAsync(string path, CancellationToken ct) => Inner.StatAsync(path, ct);

    public IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive, CancellationToken ct) =>
        Inner.ListAsync(folder, recursive, ct);

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        if (Holds(path))
        {
            Reached.TrySetResult();
            await Gate.Task.WaitAsync(ct);
        }
        return await Inner.OpenReadAsync(path, ct);
    }

    public Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct) =>
        Inner.CreateAsync(path, options, ct);

    public Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct) => Inner.DeleteAsync(paths, ct);

    public Task EnsureDirectoryAsync(string path, CancellationToken ct) => Inner.EnsureDirectoryAsync(path, ct);

    public Task<long?> GetFreeSpaceAsync(CancellationToken ct) => Inner.GetFreeSpaceAsync(ct);
}

/// <summary>Opens S3 locations from a table keyed by connection id and path; folders on the real file system.</summary>
public sealed class TableStorageFactory : IStorageFactory
{
    private readonly StorageFactory _files = new();

    public Dictionary<(string Connection, string Path), IStorage> Storages { get; } = [];

    public IStorage Open(StorageLocation location) =>
        location.IsFileSystem ? _files.Open(location) : Storages[(location.ConnectionId ?? "", location.Path)];
}
