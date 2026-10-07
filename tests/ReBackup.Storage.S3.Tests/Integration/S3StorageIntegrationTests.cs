using System.Text;
using Amazon.S3.Model;
using FluentAssertions;

namespace ReBackup.Storage.S3.Tests.Integration;

/// <summary>S3 behaviour the contract does not cover, checked against a real S3-compatible server.</summary>
[Collection(S3ServerCollection.Name)]
public sealed class S3StorageIntegrationTests(S3ServerFixture server)
{
    private const int MiB = 1024 * 1024;
    private static readonly CancellationToken Ct = CancellationToken.None;

    private string NewPrefix()
    {
        Skip.IfNot(server.Available, server.SkipReason);
        return "i-" + Guid.NewGuid().ToString("N");
    }

    private static byte[] RandomData(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Writes in uneven chunks so part boundaries fall inside a write.</summary>
    private static async Task WriteChunkedAsync(Stream writer, byte[] data)
    {
        for (var offset = 0; offset < data.Length;)
        {
            var count = Math.Min(data.Length - offset, 3 * MiB + 12_345);
            await writer.WriteAsync(data.AsMemory(offset, count), Ct);
            offset += count;
        }
    }

    private static async Task<byte[]> ReadAllAsync(IStorage storage, string path)
    {
        await using var stream = await storage.OpenReadAsync(path, Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    private static async Task<List<StorageEntry>> ListAsync(IStorage storage, string folder, bool recursive)
    {
        var entries = new List<StorageEntry>();
        await foreach (var entry in storage.ListAsync(folder, recursive, Ct)) entries.Add(entry);
        return entries;
    }

    private async Task PutRawAsync(string key, string content = "") =>
        await server.Client.PutObjectAsync(new PutObjectRequest { BucketName = server.Bucket, Key = key, ContentBody = content }, Ct);

    /// <summary>Commits both writers at the same moment; returns how many commits succeeded and the exceptions of the others.</summary>
    private static async Task<(int Won, List<Exception> Lost)> RaceAsync(StorageWriter first, StorageWriter second)
    {
        using var start = new Barrier(2);
        async Task<Exception?> CommitAsync(StorageWriter writer)
        {
            await Task.Yield();
            start.SignalAndWait(Ct);
            try
            {
                await writer.CommitAsync(Ct);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        var results = await Task.WhenAll(Task.Run(() => CommitAsync(first)), Task.Run(() => CommitAsync(second)));
        return (results.Count(r => r is null), results.OfType<Exception>().ToList());
    }

    [SkippableFact]
    public async Task Multipart_round_trip_above_16_MiB()
    {
        var storage = server.CreateStorage(NewPrefix());
        var data = RandomData(40 * MiB, seed: 1);

        await using (var writer = await storage.CreateAsync("big.bin", new CreateOptions(), Ct))
        {
            await WriteChunkedAsync(writer, data);
            await writer.CommitAsync(Ct);
        }

        (await storage.StatAsync("big.bin", Ct))!.Size.Should().Be(data.Length);
        (await ReadAllAsync(storage, "big.bin")).SequenceEqual(data).Should().BeTrue();
    }

    [SkippableFact]
    public async Task Two_concurrent_exclusive_writers_exactly_one_wins()
    {
        var storage = server.CreateStorage(NewPrefix());
        for (var round = 0; round < 5; round++)
        {
            var path = $"race-{round}.txt";
            // Both writers pass the HeadObject check of CreateAsync; only the conditional PutObject can stop the second one.
            await using var first = await storage.CreateAsync(path, new CreateOptions(), Ct);
            await using var second = await storage.CreateAsync(path, new CreateOptions(), Ct);
            await first.WriteAsync(Encoding.UTF8.GetBytes("first"), Ct);
            await second.WriteAsync(Encoding.UTF8.GetBytes("second"), Ct);

            var (won, lost) = await RaceAsync(first, second);

            won.Should().Be(1);
            lost.Should().ContainSingle().Which.Should().BeOfType<StorageConflictException>();
            Encoding.UTF8.GetString(await ReadAllAsync(storage, path)).Should().BeOneOf("first", "second");
        }
    }

    [SkippableFact]
    public async Task Exclusive_multipart_commit_over_a_key_committed_in_between_is_a_conflict()
    {
        var storage = server.CreateStorage(NewPrefix());
        var firstData = RandomData(20 * MiB, seed: 4);
        var secondData = RandomData(20 * MiB, seed: 5);

        // Both writers pass the HeadObject check; the second commit is refused by the conditional CompleteMultipartUpload alone.
        await using var first = await storage.CreateAsync("big.bin", new CreateOptions(), Ct);
        await using var second = await storage.CreateAsync("big.bin", new CreateOptions(), Ct);
        await WriteChunkedAsync(first, firstData);
        await WriteChunkedAsync(second, secondData);
        await first.CommitAsync(Ct);

        var act = () => second.CommitAsync(Ct);

        await act.Should().ThrowAsync<StorageConflictException>();
        (await ReadAllAsync(storage, "big.bin")).SequenceEqual(firstData).Should().BeTrue();
    }

    [SkippableFact(Skip = "LocalStack 4.9 does not serialize concurrent conditional CompleteMultipartUpload (observed 4/6 double wins); " +
        "AWS S3 does — the client sends If-None-Match=* (see unit test S3WriterTests.Exclusive_commit_sends_if_none_match).")]
    public async Task Two_concurrent_exclusive_multipart_writers_exactly_one_wins()
    {
        var prefix = NewPrefix();
        var storage = server.CreateStorage(prefix);
        var firstData = RandomData(20 * MiB, seed: 2);
        var secondData = RandomData(20 * MiB, seed: 3);

        // Both writers pass the HeadObject check and upload a part; only the conditional CompleteMultipartUpload can stop the second one.
        await using (var first = await storage.CreateAsync("big.bin", new CreateOptions(), Ct))
        await using (var second = await storage.CreateAsync("big.bin", new CreateOptions(), Ct))
        {
            await WriteChunkedAsync(first, firstData);
            await WriteChunkedAsync(second, secondData);

            var (won, lost) = await RaceAsync(first, second);

            won.Should().Be(1);
            lost.Should().ContainSingle().Which.Should().BeOfType<StorageConflictException>();
        }

        var stored = await ReadAllAsync(storage, "big.bin");
        (stored.SequenceEqual(firstData) || stored.SequenceEqual(secondData)).Should().BeTrue();
        var uploads = await server.Client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = server.Bucket, Prefix = prefix + "/" }, Ct);
        (uploads.MultipartUploads ?? []).Should().BeEmpty("the loser's upload is aborted on dispose");
    }

    [SkippableFact]
    public async Task Placeholder_folder_is_listed_as_directory_and_never_as_file()
    {
        var prefix = NewPrefix();
        var storage = server.CreateStorage(prefix);
        await PutRawAsync(prefix + "/x/");
        await PutRawAsync(prefix + "/y/");
        await PutRawAsync(prefix + "/y/f.txt", "1");

        (await ListAsync(storage, "", false)).Select(e => (e.Path, e.IsDirectory))
            .Should().BeEquivalentTo(new[] { ("x", true), ("y", true) });
        (await ListAsync(storage, "", true)).Select(e => (e.Path, e.IsDirectory))
            .Should().BeEquivalentTo(new[] { ("x", true), ("y", true), ("y/f.txt", false) });
        (await ListAsync(storage, "x", true)).Should().BeEmpty();
        (await storage.StatAsync("x", Ct))!.IsDirectory.Should().BeTrue();
    }

    [SkippableFact]
    public async Task Placeholder_can_be_deleted()
    {
        var prefix = NewPrefix();
        var storage = server.CreateStorage(prefix);
        await PutRawAsync(prefix + "/x/");

        await storage.DeleteAsync(["x"], Ct);

        (await storage.StatAsync("x", Ct)).Should().BeNull();
        var left = await server.Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = server.Bucket, Prefix = prefix + "/" }, Ct);
        (left.S3Objects ?? []).Should().BeEmpty();
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("a/b c/ü")]
    public async Task Prefix_variants_list_relative_paths(string prefix)
    {
        Skip.IfNot(server.Available, server.SkipReason);
        // A fresh bucket each, so the empty prefix (the whole bucket) sees only this test's objects.
        var bucket = await server.CreateBucketAsync();
        var storage = server.CreateStorage(bucket, prefix);
        string[] files = ["top+1.txt", "with space.txt", "dir ä/ü+ö ß.txt", "dir ä/sub/x+y z.txt"];
        foreach (var file in files)
        {
            await using var writer = await storage.CreateAsync(file, new CreateOptions(), Ct);
            await writer.WriteAsync(Encoding.UTF8.GetBytes(file), Ct);
            await writer.CommitAsync(Ct);
        }

        (await ListAsync(storage, "", true)).Where(e => !e.IsDirectory).Select(e => e.Path).Should().BeEquivalentTo(files);
        (await ListAsync(storage, "", false)).Select(e => (e.Path, e.IsDirectory))
            .Should().BeEquivalentTo(new[] { ("top+1.txt", false), ("with space.txt", false), ("dir ä", true) });
        (await ListAsync(storage, "dir ä", false)).Select(e => (e.Path, e.IsDirectory))
            .Should().BeEquivalentTo(new[] { ("dir ä/ü+ö ß.txt", false), ("dir ä/sub", true) });
        foreach (var file in files)
        {
            (await storage.StatAsync(file, Ct))!.Size.Should().Be(Encoding.UTF8.GetByteCount(file));
            Encoding.UTF8.GetString(await ReadAllAsync(storage, file)).Should().Be(file);
        }
        var keys = await server.Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, Ct);
        keys.S3Objects.Select(o => o.Key).Should().BeEquivalentTo(files.Select(f => prefix.Length == 0 ? f : prefix + "/" + f));
    }

    [SkippableFact]
    public async Task Missing_non_root_folder_throws_not_found()
    {
        var storage = server.CreateStorage(NewPrefix());
        await using (var writer = await storage.CreateAsync("other/f.txt", new CreateOptions(), Ct))
            await writer.CommitAsync(Ct);

        var act = async () => await ListAsync(storage, "missing", false);

        await act.Should().ThrowAsync<StorageNotFoundException>();
        (await storage.StatAsync("missing", Ct)).Should().BeNull();
    }

    [SkippableFact]
    public async Task Empty_root_prefix_lists_empty()
    {
        Skip.IfNot(server.Available, server.SkipReason);
        var bucket = await server.CreateBucketAsync();

        (await ListAsync(server.CreateStorage(bucket, ""), "", true)).Should().BeEmpty();
        (await ListAsync(server.CreateStorage(NewPrefix()), "", false)).Should().BeEmpty();
        (await server.CreateStorage(bucket, "").StatAsync("", Ct))!.IsDirectory.Should().BeTrue();
    }

    [SkippableFact]
    public async Task Stamp_is_the_etag_without_quotes()
    {
        var prefix = NewPrefix();
        var storage = server.CreateStorage(prefix);
        await using (var writer = await storage.CreateAsync("a.txt", new CreateOptions(), Ct))
        {
            await writer.WriteAsync(Encoding.UTF8.GetBytes("abc"), Ct);
            await writer.CommitAsync(Ct);
        }

        var head = await server.Client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = server.Bucket, Key = prefix + "/a.txt" }, Ct);
        var stamp = (await storage.StatAsync("a.txt", Ct))!.Stamp;

        head.ETag.Should().StartWith("\"");
        stamp.Should().Be(head.ETag.Trim('"')).And.NotContain("\"");
        (await ListAsync(storage, "", true)).Single().Stamp.Should().Be(stamp);
    }
}
