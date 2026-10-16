using FluentAssertions;
using ReBackup.Core.Pool;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;
using ReBackup.Storage.InMemory;
using static ReBackup.Core.Tests.TestSupport.PoolVersions;

namespace ReBackup.Core.Tests.Pool;

/// <summary>
/// The pool's listing (<see cref="PoolListing"/>): the first-level shard folders, then those shards in parallel. It finds
/// exactly what one recursive listing of the pool finds; a listing that fails fails the whole listing.
/// </summary>
public class PoolListingTests : IDisposable
{
    private const string PlanId = "p1";
    private const string PlanName = "Projects";
    private const string V1 = "2026_10_15-10_00 Projects";
    private readonly InMemoryStorage _target = new();
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static async Task<List<StorageEntry>> ParallelAsync(IStorage target)
    {
        var entries = new List<StorageEntry>();
        await PoolListing.ListAsync(target, entries.Add, CancellationToken.None);
        return entries;
    }

    /// <summary>What a listing tells of an entry (a folder's time may differ between two listings of a real folder).</summary>
    private static List<(string, bool, long, bool, string?)> Key(IEnumerable<StorageEntry> entries) =>
        entries.Select(e => (e.Path, e.IsDirectory, e.IsDirectory ? 0 : e.Size, e.IsLink, e.Stamp)).ToList();

    private static async Task<List<StorageEntry>> RecursiveAsync(IStorage target)
    {
        var entries = new List<StorageEntry>();
        try
        {
            await foreach (var entry in target.ListAsync(PoolLayout.PoolFolder, recursive: true, CancellationToken.None))
                entries.Add(entry);
        }
        catch (StorageNotFoundException)
        {
        }
        return entries;
    }

    private static void Fill(Action<string, string> file, Action<string> folder)
    {
        for (var i = 0; i < 40; i++)
            file(BlobOf("blob " + i), "blob " + i);
        file("re-pool/ab/cd/notes.txt", "not a blob");
        file("re-pool/readme.txt", "at the top");
        file("re-pool/ab/loose.bin", "between the levels");
        file($"re-pool/00/00/{HashOf("misplaced")[7..]}", "misplaced");
        folder("re-pool/ef");          // an empty first-level shard
        folder("re-pool/12/34");       // an empty second-level shard
    }

    [Fact]
    public async Task Finds_what_the_recursive_listing_finds_in_memory()
    {
        Fill((path, content) => _target.AddFile(path, Bytes(content)),
            path => _target.EnsureDirectoryAsync(path, CancellationToken.None).GetAwaiter().GetResult());

        var parallel = await ParallelAsync(_target);

        Key(parallel).Should().BeEquivalentTo(Key(await RecursiveAsync(_target)));
        parallel.Should().HaveCountGreaterThan(40);
    }

    [Fact]
    public async Task Finds_what_the_recursive_listing_finds_in_a_folder()
    {
        var root = _tmp.CreateDir("target");
        Fill((path, content) => _tmp.WriteFile(Path.Combine("target", path.Replace('/', '\\')), content),
            path => _tmp.CreateDir(Path.Combine("target", path.Replace('/', '\\'))));
        var target = new FileSystemStorage(root);

        var parallel = await ParallelAsync(target);

        Key(parallel).Should().BeEquivalentTo(Key(await RecursiveAsync(target)));
        parallel.Should().Contain(entry => entry.Path == "re-pool/ef" && entry.IsDirectory);
        parallel.Should().Contain(entry => entry.Path == "re-pool/12/34" && entry.IsDirectory);
    }

    [Fact]
    public async Task A_storage_without_folders_lists_the_pool_in_one_recursive_listing()
    {
        var keys = new InMemoryStorage(StorageCapabilities.None);
        for (var i = 0; i < 40; i++)
            keys.AddFile(BlobOf("blob " + i), Bytes("blob " + i));
        var lists = new List<string>();
        var counting = new FaultyStorage(keys)
        {
            Before = (operation, path) =>
            {
                if (operation == "list")
                    lock (lists)
                        lists.Add(path);
            },
        };

        var listed = await ParallelAsync(counting);

        lists.Should().Equal(PoolLayout.PoolFolder);
        Key(listed).Should().BeEquivalentTo(Key(await RecursiveAsync(keys)));
    }

    [Fact]
    public async Task Shards_a_storage_lists_twice_are_listed_and_collected_once()
    {
        PoolVersions.Write(_target, V1, PlanId, PlanName, [("a.txt", "kept")]);
        for (var i = 0; i < 20; i++)
            AddBlob(_target, "orphan " + i);
        var twice = new TwiceListedShards(_target);

        var result = await PoolCollector.CollectAsync(twice, PlanId, PlanName, CancellationToken.None);

        result.BlobsDeleted.Should().Be(20);
        twice.ShardLists.Should().OnlyHaveUniqueItems();
        Blobs(_target).Should().Equal(BlobOf("kept"));
    }

    /// <summary>Lists every first-level shard of the pool twice, as Google Drive does for two folders of one name.</summary>
    private sealed class TwiceListedShards(IStorage inner) : IStorage
    {
        public List<string> ShardLists { get; } = [];

        public StorageCapabilities Capabilities => inner.Capabilities;

        public Task<StorageEntry?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);

        public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            if (StoragePath.Parent(folder) == PoolLayout.PoolFolder)
                lock (ShardLists)
                    ShardLists.Add(folder);
            await foreach (var entry in inner.ListAsync(folder, recursive, ct))
            {
                yield return entry;
                if (folder == PoolLayout.PoolFolder && !recursive && entry.IsDirectory)
                    yield return entry;
            }
        }

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);

        public Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct) =>
            inner.CreateAsync(path, options, ct);

        public Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct) => inner.DeleteAsync(paths, ct);

        public Task EnsureDirectoryAsync(string path, CancellationToken ct) => inner.EnsureDirectoryAsync(path, ct);

        public Task<long?> GetFreeSpaceAsync(CancellationToken ct) => inner.GetFreeSpaceAsync(ct);
    }

    [Fact]
    public async Task A_missing_pool_is_empty()
    {
        PoolVersions.Write(_target, V1, PlanId, PlanName, []);

        (await ParallelAsync(_target)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_pool_is_empty()
    {
        await _target.EnsureDirectoryAsync(PoolLayout.PoolFolder, CancellationToken.None);

        (await ParallelAsync(_target)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_shard_listing_fails_the_listing()
    {
        for (var i = 0; i < 40; i++)
            AddBlob(_target, "blob " + i);
        var shard = StoragePath.Parent(StoragePath.Parent(BlobOf("blob 7")));
        var faulty = new FaultyStorage(_target)
        {
            Before = (operation, path) =>
            {
                if (operation == "list" && path == shard)
                    throw new StorageUnavailableException(path);
            },
        };

        var listing = () => ParallelAsync(faulty);

        await listing.Should().ThrowAsync<StorageUnavailableException>();
    }

    [Fact]
    public async Task A_shard_that_vanishes_fails_the_listing()
    {
        AddBlob(_target, "blob");
        var shard = StoragePath.Parent(StoragePath.Parent(BlobOf("blob")));
        var faulty = new FaultyStorage(_target)
        {
            Before = (operation, path) =>
            {
                if (operation == "list" && path == shard)
                    throw new StorageNotFoundException(path);   // gone between the two listings
            },
        };

        var listing = () => ParallelAsync(faulty);

        (await listing.Should().ThrowAsync<StorageException>()).Which
            .Should().NotBeOfType<StorageNotFoundException>("a missing shard must not read as a missing pool");
    }
}
