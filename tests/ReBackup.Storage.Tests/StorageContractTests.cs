using System.Text;
using FluentAssertions;
using ReBackup.Storage;

namespace ReBackup.Storage.Tests;

/// <summary>
/// The behaviour every <see cref="IStorage"/> must show. A provider's test class derives from this and supplies a fresh,
/// empty storage; the tests only use the public contract, so the filesystem, S3 and Google Drive reuse them unchanged.
/// </summary>
public abstract class StorageContractTests
{
    /// <summary>A fresh, empty storage; every test gets its own.</summary>
    protected abstract IStorage CreateEmpty();

    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>Creates, writes and commits a file.</summary>
    protected static async Task WriteAsync(IStorage storage, string path, string content, CreateOptions? options = null)
    {
        await using var writer = await storage.CreateAsync(path, options ?? new CreateOptions(), Ct);
        await writer.WriteAsync(Encoding.UTF8.GetBytes(content), Ct);
        await writer.CommitAsync(Ct);
    }

    /// <summary>Reads a committed file as text.</summary>
    protected static async Task<string> ReadAsync(IStorage storage, string path)
    {
        await using var stream = await storage.OpenReadAsync(path, Ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(Ct);
    }

    /// <summary>Collects a listing.</summary>
    protected static async Task<List<StorageEntry>> ListAsync(IStorage storage, string folder, bool recursive)
    {
        var entries = new List<StorageEntry>();
        await foreach (var entry in storage.ListAsync(folder, recursive, Ct)) entries.Add(entry);
        return entries;
    }

    [Fact]
    public async Task Created_file_is_invisible_until_committed()
    {
        var storage = CreateEmpty();
        await using var writer = await storage.CreateAsync("a.txt", new CreateOptions(), Ct);
        await writer.WriteAsync(Encoding.UTF8.GetBytes("hello"), Ct);

        (await storage.StatAsync("a.txt", Ct)).Should().BeNull();
        (await ListAsync(storage, "", true)).Should().BeEmpty();

        await writer.CommitAsync(Ct);

        (await storage.StatAsync("a.txt", Ct))!.Size.Should().Be(5);
        var listed = await ListAsync(storage, "", true);
        listed.Should().ContainSingle().Which.Should().Match<StorageEntry>(e => e.Path == "a.txt" && e.Size == 5 && !e.IsDirectory);
    }

    [Fact]
    public async Task Disposing_without_commit_discards_the_file()
    {
        var storage = CreateEmpty();
        var writer = await storage.CreateAsync("a.txt", new CreateOptions(), Ct);
        await writer.WriteAsync(Encoding.UTF8.GetBytes("hello"), Ct);
        await writer.DisposeAsync();

        (await storage.StatAsync("a.txt", Ct)).Should().BeNull();
        (await ListAsync(storage, "", true)).Should().BeEmpty();
    }

    [Fact]
    public async Task Exclusive_create_of_an_existing_file_throws_conflict()
    {
        var storage = CreateEmpty();
        await WriteAsync(storage, "a.txt", "one");

        var act = () => storage.CreateAsync("a.txt", new CreateOptions(Overwrite: false), Ct);

        await act.Should().ThrowAsync<StorageConflictException>();
        (await ReadAsync(storage, "a.txt")).Should().Be("one");
    }

    [Fact]
    public async Task Exclusive_create_does_not_overwrite_a_file_committed_in_between()
    {
        var storage = CreateEmpty();
        await using var first = await storage.CreateAsync("a.txt", new CreateOptions(), Ct);
        await first.WriteAsync(Encoding.UTF8.GetBytes("first"), Ct);
        await WriteAsync(storage, "a.txt", "second");

        var act = () => first.CommitAsync(Ct);

        await act.Should().ThrowAsync<StorageConflictException>();
        (await ReadAsync(storage, "a.txt")).Should().Be("second");
    }

    [Fact]
    public async Task Overwrite_replaces_content_on_commit_and_keeps_old_content_until_then()
    {
        var storage = CreateEmpty();
        await WriteAsync(storage, "a.txt", "old");

        await using var writer = await storage.CreateAsync("a.txt", new CreateOptions(Overwrite: true), Ct);
        await writer.WriteAsync(Encoding.UTF8.GetBytes("brand new"), Ct);

        (await ReadAsync(storage, "a.txt")).Should().Be("old");

        await writer.CommitAsync(Ct);

        (await ReadAsync(storage, "a.txt")).Should().Be("brand new");
    }

    [Fact]
    public async Task Writer_is_write_only_and_rejects_second_commit_and_writes_after_commit()
    {
        var storage = CreateEmpty();
        await using var writer = await storage.CreateAsync("a.txt", new CreateOptions(), Ct);
        writer.CanWrite.Should().BeTrue();
        writer.CanRead.Should().BeFalse();
        writer.CanSeek.Should().BeFalse();
        await writer.WriteAsync(Encoding.UTF8.GetBytes("x"), Ct);
        await writer.CommitAsync(Ct);

        var commitAgain = () => writer.CommitAsync(Ct);
        var writeAfter = () => writer.WriteAsync(new byte[] { 1 }, Ct).AsTask();

        await commitAgain.Should().ThrowAsync<InvalidOperationException>();
        await writeAfter.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Durable_create_commits_like_any_other()
    {
        var storage = CreateEmpty();
        var modified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        await WriteAsync(storage, "sub/manifest.json", "{}", new CreateOptions(ModifiedUtc: modified, Durable: true));
        await WriteAsync(storage, "sub/manifest.json", "{ }", new CreateOptions(Overwrite: true, Durable: true));

        (await ReadAsync(storage, "sub/manifest.json")).Should().Be("{ }");
        (await ListAsync(storage, "sub", false)).Should().ContainSingle().Which.Path.Should().Be("sub/manifest.json");
        var exclusive = () => storage.CreateAsync("sub/manifest.json", new CreateOptions(Durable: true), Ct);
        await exclusive.Should().ThrowAsync<StorageConflictException>();
    }

    [Fact]
    public async Task Modified_time_is_kept_when_supported()
    {
        var storage = CreateEmpty();
        if (!storage.Capabilities.HasFlag(StorageCapabilities.SetModifiedTime)) return;
        var modified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        await WriteAsync(storage, "a.txt", "x", new CreateOptions(ModifiedUtc: modified));

        (await storage.StatAsync("a.txt", Ct))!.ModifiedUtc.Should().Be(modified);
    }

    [Fact]
    public async Task List_non_recursive_returns_direct_children_with_directories()
    {
        var storage = CreateEmpty();
        await WriteAsync(storage, "top.txt", "1");
        await WriteAsync(storage, "d/inner.txt", "22");

        var listed = await ListAsync(storage, "", false);

        listed.Select(e => (e.Path, e.IsDirectory)).Should().BeEquivalentTo(new[] { ("top.txt", false), ("d", true) });

        var inner = await ListAsync(storage, "d", false);
        inner.Select(e => (e.Path, e.IsDirectory)).Should().BeEquivalentTo(new[] { ("d/inner.txt", false) });
    }

    [Fact]
    public async Task List_recursive_returns_all_files_with_relative_paths()
    {
        var storage = CreateEmpty();
        await WriteAsync(storage, "top.txt", "1");
        await WriteAsync(storage, "a/b/c.txt", "333");

        var listed = await ListAsync(storage, "", true);

        listed.Where(e => !e.IsDirectory).Select(e => e.Path).Should().BeEquivalentTo("top.txt", "a/b/c.txt");
        (await ListAsync(storage, "a", true)).Where(e => !e.IsDirectory).Select(e => e.Path).Should().BeEquivalentTo("a/b/c.txt");
    }

    [Fact]
    public async Task List_of_a_missing_folder_throws_not_found()
    {
        var storage = CreateEmpty();

        var act = async () => await ListAsync(storage, "nope", true);

        await act.Should().ThrowAsync<StorageNotFoundException>();
    }

    [Fact]
    public async Task Open_read_of_missing_file_throws_not_found()
    {
        var storage = CreateEmpty();

        var act = () => storage.OpenReadAsync("missing.txt", Ct);

        await act.Should().ThrowAsync<StorageNotFoundException>();
    }

    [Fact]
    public async Task Delete_removes_files_and_ignores_missing_paths()
    {
        var storage = CreateEmpty();
        await WriteAsync(storage, "a.txt", "1");
        await WriteAsync(storage, "b.txt", "2");

        await storage.DeleteAsync(new[] { "a.txt", "never-existed.txt" }, Ct);

        (await storage.StatAsync("a.txt", Ct)).Should().BeNull();
        (await storage.StatAsync("b.txt", Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_of_non_empty_directory_throws()
    {
        var storage = CreateEmpty();
        await WriteAsync(storage, "d/f.txt", "1");

        var act = () => storage.DeleteAsync(new[] { "d" }, Ct);

        var thrown = await act.Should().ThrowAsync<StorageConflictException>();
        thrown.Which.Should().BeAssignableTo<IOException>();
        (await storage.StatAsync("d/f.txt", Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_of_the_root_is_a_conflict_even_when_it_is_empty()
    {
        var storage = CreateEmpty();
        await storage.EnsureDirectoryAsync("", Ct);

        var act = () => storage.DeleteAsync(new[] { "" }, Ct);

        await act.Should().ThrowAsync<StorageConflictException>();
    }

    [Fact]
    public async Task Delete_removes_an_emptied_directory()
    {
        var storage = CreateEmpty();
        await WriteAsync(storage, "d/f.txt", "1");

        await storage.DeleteAsync(new[] { "d/f.txt", "d" }, Ct);

        (await storage.StatAsync("d", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Stamp_changes_when_content_changes()
    {
        var storage = CreateEmpty();
        await WriteAsync(storage, "a.txt", "abc");
        var before = (await storage.StatAsync("a.txt", Ct))!.Stamp;

        await WriteAsync(storage, "a.txt", "abcdef", new CreateOptions(Overwrite: true));
        var after = (await storage.StatAsync("a.txt", Ct))!.Stamp;

        before.Should().NotBeNullOrEmpty();
        after.Should().NotBe(before);
    }

    [Fact]
    public async Task Stamp_is_stable_while_the_file_is_unchanged()
    {
        var storage = CreateEmpty();
        await WriteAsync(storage, "a.txt", "abc");

        var first = (await storage.StatAsync("a.txt", Ct))!.Stamp;
        var second = (await ListAsync(storage, "", true)).Single(e => e.Path == "a.txt").Stamp;

        second.Should().Be(first);
    }

    [Fact]
    public async Task Ensure_directory_does_not_fail_and_keeps_the_storage_usable()
    {
        var storage = CreateEmpty();

        await storage.EnsureDirectoryAsync("x/y", Ct);
        await WriteAsync(storage, "x/y/f.txt", "1");

        (await storage.StatAsync("x/y/f.txt", Ct)).Should().NotBeNull();
        if (storage.Capabilities.HasFlag(StorageCapabilities.EmptyDirectories))
            (await storage.StatAsync("x", Ct))!.IsDirectory.Should().BeTrue();
    }

    [Theory]
    [InlineData("a\\b")]
    [InlineData("/a")]
    [InlineData("a/../b")]
    public async Task Invalid_paths_are_rejected(string path)
    {
        var storage = CreateEmpty();

        await ((Func<Task>)(() => storage.StatAsync(path, Ct))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => storage.OpenReadAsync(path, Ct))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => storage.CreateAsync(path, new CreateOptions(), Ct))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => storage.DeleteAsync(new[] { path }, Ct))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => storage.EnsureDirectoryAsync(path, Ct))).Should().ThrowAsync<ArgumentException>();
    }
}
