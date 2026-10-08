using Amazon.Runtime;
using Amazon.S3;
using FluentAssertions;
using ReBackup.Storage.S3.Connections;
using ReBackup.Storage.S3.Tests.Unit.Fakes;

namespace ReBackup.Storage.S3.Tests.Unit;

public class S3StorageReadTests
{
    private static S3Connection Connection => new("id", "n", "eu-central-1", "bucket", "AKIA", "secret");

    private static (S3Storage Storage, FakeS3Client Fake) Make(string prefix)
    {
        var (client, fake) = FakeS3Client.Create();
        return (new S3Storage(Connection, prefix, client), fake);
    }

    private static async Task<List<StorageEntry>> List(S3Storage s, string folder, bool recursive)
    {
        var result = new List<StorageEntry>();
        await foreach (var e in s.ListAsync(folder, recursive, CancellationToken.None)) result.Add(e);
        return result;
    }

    [Fact]
    public async Task Placeholder_is_listed_only_as_a_directory_and_own_placeholder_is_skipped()
    {
        var (s, fake) = Make("p");
        fake.Add("p/").Add("p/x/").Add("p/f.txt");

        var flat = await List(s, "", recursive: false);
        flat.Select(e => (e.Path, e.IsDirectory)).Should().BeEquivalentTo([("x", true), ("f.txt", false)]);

        var deep = await List(s, "", recursive: true);
        deep.Select(e => (e.Path, e.IsDirectory)).Should().BeEquivalentTo([("x", true), ("f.txt", false)]);
        deep.Count(e => e.Path == "x").Should().Be(1);
    }

    [Fact]
    public async Task Prefix_with_spaces_and_umlauts_gives_relative_paths()
    {
        var (s, fake) = Make("a/b c/ü");
        fake.Add("a/b c/ü/ä b+c.txt").Add("a/b c/ü/sub dir/ö+1.bin").Add("a/b c/üx/other.txt").Add("a/b c/ü");

        var all = await List(s, "", recursive: true);

        all.Select(e => e.Path).Should().BeEquivalentTo(["ä b+c.txt", "sub dir", "sub dir/ö+1.bin"]);
        (await List(s, "sub dir", recursive: false)).Select(e => e.Path).Should().Equal("sub dir/ö+1.bin");
    }

    [Fact]
    public async Task Empty_prefix_lists_the_bucket_root()
    {
        var (s, fake) = Make("");
        fake.Add("a.txt").Add("d/b.txt");

        (await List(s, "", recursive: false)).Select(e => e.Path).Should().BeEquivalentTo(["a.txt", "d"]);
    }

    [Fact]
    public async Task Recursive_emits_each_ancestor_once_across_pages()
    {
        var (s, fake) = Make("p");
        fake.PageSize = 2;
        fake.Add("p/a/b/1").Add("p/a/b/2").Add("p/a/c/3").Add("p/a/4").Add("p/z");

        var all = await List(s, "", recursive: true);

        fake.ListRequests.Count.Should().BeGreaterThan(1);
        var dirs = all.Where(e => e.IsDirectory).Select(e => e.Path).ToList();
        dirs.Should().BeEquivalentTo(["a", "a/b", "a/c"]);
        dirs.Should().OnlyHaveUniqueItems();
        all.Where(e => !e.IsDirectory).Select(e => e.Path).Should().BeEquivalentTo(["a/b/1", "a/b/2", "a/c/3", "a/4", "z"]);
        // Each directory comes before the first file below it.
        foreach (var dir in dirs)
            all.FindIndex(e => e.Path == dir).Should().BeLessThan(all.FindIndex(e => !e.IsDirectory && e.Path.StartsWith(dir + "/")));
    }

    [Fact]
    public async Task Invalid_keys_are_skipped()
    {
        var (s, fake) = Make("p");
        fake.Add("p//x").Add("p/a//b").Add("p/./c").Add("p/d/../e").Add("p/ok");

        (await List(s, "", recursive: true)).Select(e => e.Path).Should().Equal("ok");
    }

    [Fact]
    public async Task Missing_non_root_folder_is_not_found()
    {
        var (s, fake) = Make("p");
        fake.Add("p/other/x");

        var act = async () => await List(s, "missing", recursive: false);
        await act.Should().ThrowAsync<StorageNotFoundException>();
    }

    [Fact]
    public async Task Empty_root_lists_nothing()
    {
        var (s, _) = Make("p");

        (await List(s, "", recursive: true)).Should().BeEmpty();
    }

    [Fact]
    public async Task Folder_that_only_has_its_placeholder_exists()
    {
        var (s, fake) = Make("p");
        fake.Add("p/empty/");

        (await List(s, "empty", recursive: false)).Should().BeEmpty();
    }

    [Fact]
    public async Task Listing_failure_is_mapped()
    {
        var (s, fake) = Make("p");
        fake.Failures["ListObjectsV2Async"] = new HttpRequestException("down");

        var act = async () => await List(s, "", recursive: false);
        await act.Should().ThrowAsync<StorageUnavailableException>();
    }

    [Fact]
    public async Task Stat_returns_a_file_with_unquoted_etag_and_utc_time()
    {
        var (s, fake) = Make("p");
        var modified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        fake.Add("p/f", size: 42, etag: "\"abc123\"", modified: modified);

        var entry = await s.StatAsync("f", CancellationToken.None);

        entry.Should().Be(new StorageEntry("f", false, 42, modified, false, "abc123"));
    }

    [Fact]
    public async Task Stat_falls_back_to_a_directory_probe_after_head_404()
    {
        var (s, fake) = Make("p");
        fake.Add("p/d/x");

        (await s.StatAsync("d", CancellationToken.None))!.IsDirectory.Should().BeTrue();
        (await s.StatAsync("nope", CancellationToken.None)).Should().BeNull();
        fake.ListRequests.Should().Contain(r => r.Prefix == "p/d/" && r.MaxKeys == 1);
    }

    [Fact]
    public async Task Stat_root_probes_with_the_storage_prefix()
    {
        var (s, fake) = Make("p/q");

        var entry = await s.StatAsync("", CancellationToken.None);

        entry!.IsDirectory.Should().BeTrue();
        var request = fake.ListRequests.Should().ContainSingle().Subject;
        request.Prefix.Should().Be("p/q/");
        request.MaxKeys.Should().Be(1);
    }

    [Fact]
    public async Task Stat_maps_access_denied()
    {
        var (s, fake) = Make("p");
        fake.Failures["GetObjectMetadataAsync"] = new AmazonS3Exception("x", ErrorType.Unknown, "AccessDenied", "r", System.Net.HttpStatusCode.Forbidden);

        var act = () => s.StatAsync("f", CancellationToken.None);
        await act.Should().ThrowAsync<StorageAccessDeniedException>();
    }

    [Fact]
    public async Task OpenRead_returns_the_body_and_missing_is_not_found()
    {
        var (s, fake) = Make("p");
        fake.Add("p/f", body: [1, 2, 3]);

        await using (var stream = await s.OpenReadAsync("f", CancellationToken.None))
        {
            var buffer = new byte[3];
            (await stream.ReadAsync(buffer)).Should().Be(3);
            buffer.Should().Equal(1, 2, 3);
            stream.CanSeek.Should().BeFalse();
        }

        var act = () => s.OpenReadAsync("missing", CancellationToken.None);
        await act.Should().ThrowAsync<StorageNotFoundException>();
    }

    [Fact]
    public async Task OpenRead_maps_mid_read_failures_and_propagates_caller_cancel()
    {
        var (s, fake) = Make("p");
        fake.Add("p/f");
        fake.BodyFactory = _ => new ThrowingStream(new IOException("reset"));

        await using (var stream = await s.OpenReadAsync("f", CancellationToken.None))
        {
            var act = async () => await stream.ReadAsync(new byte[4]);
            await act.Should().ThrowAsync<StorageUnavailableException>();
        }

        fake.BodyFactory = _ => new ThrowingStream(new HttpRequestException("reset"));
        await using (var stream = await s.OpenReadAsync("f", CancellationToken.None))
        {
            var act = () => stream.Read(new byte[4], 0, 4);
            act.Should().Throw<StorageUnavailableException>();
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        fake.BodyFactory = _ => new ThrowingStream(new OperationCanceledException(cts.Token));
        await using (var stream = await s.OpenReadAsync("f", CancellationToken.None))
        {
            var act = async () => await stream.ReadAsync(new byte[4], cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void UtcOf_treats_unspecified_as_utc_and_converts_local(DateTimeKind kind)
    {
        var raw = new DateTime(2026, 6, 1, 12, 0, 0, kind);

        var result = S3Storage.UtcOf(raw);

        result.Kind.Should().Be(DateTimeKind.Utc);
        result.Should().Be(kind == DateTimeKind.Local ? raw.ToUniversalTime() : new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));
    }

    private sealed class ThrowingStream(Exception ex) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw ex;
        public override int Read(Span<byte> buffer) => throw ex;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw ex;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
