using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.Storage.Tests;

/// <summary>The contract against an in-memory storage with real (empty) directories.</summary>
public class InMemoryStorageTests : StorageContractTests
{
    protected override IStorage CreateEmpty() => new InMemoryStorage();

    [Fact]
    public async Task Empty_directories_are_listed_when_supported()
    {
        var storage = new InMemoryStorage();
        await storage.EnsureDirectoryAsync("a/b", CancellationToken.None);

        var listed = await ListAsync(storage, "", true);

        listed.Where(e => e.IsDirectory).Select(e => e.Path).Should().BeEquivalentTo("a", "a/b");
    }

    [Fact]
    public async Task Commit_time_comes_from_the_time_provider_without_a_requested_modified_time()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero));
        var storage = new InMemoryStorage(time: clock);

        await using (var writer = await storage.CreateAsync("a.txt", new CreateOptions(), CancellationToken.None))
        {
            await writer.WriteAsync(new byte[] { 1 }, CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(1));
            await writer.CommitAsync(CancellationToken.None);
        }

        (await storage.StatAsync("a.txt", CancellationToken.None))!.ModifiedUtc.Should().Be(new DateTime(2026, 5, 6, 7, 9, 9, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Stamp_changes_even_when_two_writes_share_length_and_tick()
    {
        var storage = new InMemoryStorage(time: new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        await WriteAsync(storage, "a.txt", "aaa");
        var before = (await storage.StatAsync("a.txt", CancellationToken.None))!.Stamp;

        await WriteAsync(storage, "a.txt", "bbb", new CreateOptions(Overwrite: true));

        (await storage.StatAsync("a.txt", CancellationToken.None))!.Stamp.Should().NotBe(before);
    }

    [Fact]
    public async Task Test_helpers_expose_committed_files_free_space_and_arranged_content()
    {
        var storage = new InMemoryStorage { FreeSpace = 42 };
        storage.AddFile("d/x.bin", new byte[] { 1, 2, 3 });

        storage.Files.Should().BeEquivalentTo("d/x.bin");
        storage.ReadAllBytes("d/x.bin").Should().Equal(1, 2, 3);
        (await storage.GetFreeSpaceAsync(CancellationToken.None)).Should().Be(42);
        storage.FreeSpace = null;
        (await storage.GetFreeSpaceAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Modified_time_is_ignored_without_the_set_modified_time_capability()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero));
        var storage = new InMemoryStorage(StorageCapabilities.None, clock);

        await WriteAsync(storage, "a.txt", "x", new CreateOptions(ModifiedUtc: new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        (await storage.StatAsync("a.txt", CancellationToken.None))!.ModifiedUtc.Should().Be(new DateTime(2026, 5, 6, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Paths_are_compared_ordinally()
    {
        var storage = new InMemoryStorage();
        await WriteAsync(storage, "A.txt", "x");

        (await storage.StatAsync("a.txt", CancellationToken.None)).Should().BeNull();
    }
}

/// <summary>The contract against an in-memory storage without real directories: they are implied by file paths.</summary>
public class InMemoryPrefixStorageTests : StorageContractTests
{
    protected override IStorage CreateEmpty() => new InMemoryStorage(StorageCapabilities.None);

    [Fact]
    public async Task Directories_are_implied_prefixes_and_vanish_with_their_last_file()
    {
        var storage = new InMemoryStorage(StorageCapabilities.None);
        await WriteAsync(storage, "a/b/c.txt", "1");

        (await storage.StatAsync("a/b", CancellationToken.None))!.IsDirectory.Should().BeTrue();
        var listed = await ListAsync(storage, "", true);
        listed.Where(e => e.IsDirectory).Select(e => e.Path).Should().BeEquivalentTo("a", "a/b");

        await storage.DeleteAsync(new[] { "a/b/c.txt" }, CancellationToken.None);

        (await storage.StatAsync("a", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Ensure_directory_is_a_no_op()
    {
        var storage = new InMemoryStorage(StorageCapabilities.None);

        await storage.EnsureDirectoryAsync("a/b", CancellationToken.None);

        (await storage.StatAsync("a", CancellationToken.None)).Should().BeNull();
    }
}
