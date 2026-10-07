using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using ReBackup.Storage.S3.Connections;
using ReBackup.Storage.S3.Tests.Unit.Fakes;

namespace ReBackup.Storage.S3.Tests.Unit;

public class S3WriterTests
{
    private const int MiB = 1024 * 1024;
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static S3Connection Connection => new("id", "n", "eu-central-1", "bucket", "AKIA", "secret");

    private static (S3Storage Storage, FakeS3Client Fake) Make(string prefix = "p")
    {
        var (client, fake) = FakeS3Client.Create();
        return (new S3Storage(Connection, prefix, client), fake);
    }

    private static byte[] Data(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)(i * 31 + i / 997);
        return data;
    }

    /// <summary>Writes in uneven chunks so part boundaries fall inside a write.</summary>
    private static async Task WriteChunked(Stream writer, byte[] data)
    {
        const int chunk = 3 * MiB + 17;
        for (var offset = 0; offset < data.Length; offset += chunk)
            await writer.WriteAsync(data.AsMemory(offset, Math.Min(chunk, data.Length - offset)), Ct);
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(1000, 16)]
    [InlineData(1001, 32)]
    [InlineData(2001, 64)]
    public void Part_size_doubles_after_every_1000_parts(int partNumber, int mebibytes)
    {
        S3Writer.InitialPartSize.Should().Be(16 * MiB);
        S3Writer.PartSizeFor(partNumber).Should().Be(mebibytes * MiB);
    }

    [Fact]
    public void Part_size_is_capped_at_1_GiB_so_it_fits_an_array()
    {
        S3Writer.PartSizeFor(6001).Should().Be(1024 * MiB);
        S3Writer.PartSizeFor(10000).Should().Be(1024 * MiB);
    }

    [Fact]
    public async Task Small_write_uses_a_single_put_on_commit()
    {
        var (s, fake) = Make();
        var data = Data(1024);

        await using (var writer = await s.CreateAsync("d/a.bin", new CreateOptions(Overwrite: true), Ct))
        {
            await writer.WriteAsync(data, Ct);
            fake.Puts.Should().BeEmpty();
            await writer.CommitAsync(Ct);
        }

        fake.Puts.Should().ContainSingle();
        fake.Puts[0].Key.Should().Be("p/d/a.bin");
        fake.Puts[0].Body.Should().Equal(data);
        fake.Puts[0].ContentLength.Should().Be(1024);
        fake.Initiates.Should().BeEmpty();
        fake.Parts.Should().BeEmpty();
        fake.Aborts.Should().BeEmpty();
    }

    [Fact]
    public async Task Exactly_16_MiB_is_still_a_single_put()
    {
        var (s, fake) = Make();
        var data = Data(16 * MiB);

        await using (var writer = await s.CreateAsync("a.bin", new CreateOptions(Overwrite: true), Ct))
        {
            await WriteChunked(writer, data);
            await writer.CommitAsync(Ct);
        }

        fake.Puts.Should().ContainSingle().Which.Body.Should().Equal(data);
        fake.Initiates.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_file_is_a_put_of_zero_bytes()
    {
        var (s, fake) = Make();

        await using (var writer = await s.CreateAsync("e", new CreateOptions(Overwrite: true), Ct))
            await writer.CommitAsync(Ct);

        fake.Puts.Should().ContainSingle().Which.Body.Should().BeEmpty();
    }

    [Fact]
    public async Task Large_write_uses_multipart_with_16_MiB_parts()
    {
        var (s, fake) = Make();
        var data = Data(40 * MiB);

        await using (var writer = await s.CreateAsync("big.bin", new CreateOptions(Overwrite: true), Ct))
        {
            await WriteChunked(writer, data);
            await writer.CommitAsync(Ct);
        }

        fake.Puts.Should().BeEmpty();
        fake.Initiates.Should().ContainSingle().Which.Key.Should().Be("p/big.bin");
        fake.Parts.Select(p => (p.PartNumber, p.Body.Length)).Should().Equal((1, 16 * MiB), (2, 16 * MiB), (3, 8 * MiB));
        fake.Parts.Should().OnlyContain(p => p.PartSize == p.Body.Length);
        var complete = fake.Completes.Should().ContainSingle().Subject;
        complete.UploadId.Should().Be("upload-1");
        complete.PartETags.Select(p => (p.PartNumber, p.ETag)).Should().Equal((1, "\"part1\""), (2, "\"part2\""), (3, "\"part3\""));
        fake.Objects.Single(o => o.Key == "p/big.bin").Body.Should().Equal(data);
        fake.Aborts.Should().BeEmpty();
    }

    [Fact]
    public async Task At_most_one_part_uploads_at_a_time()
    {
        var (s, fake) = Make();
        fake.UploadPartHook = (_, ct) => Task.Delay(20, ct);

        await using (var writer = await s.CreateAsync("big.bin", new CreateOptions(Overwrite: true), Ct))
        {
            await WriteChunked(writer, Data(50 * MiB));
            await writer.CommitAsync(Ct);
        }

        fake.Parts.Should().HaveCount(4);
        fake.MaxUploadsInFlight.Should().Be(1);
    }

    [Fact]
    public async Task Synchronous_writes_work_too()
    {
        var (s, fake) = Make();
        var data = Data(20 * MiB);

        await using (var writer = await s.CreateAsync("big.bin", new CreateOptions(Overwrite: true), Ct))
        {
            writer.Write(data, 0, data.Length);
            await writer.CommitAsync(Ct);
        }

        fake.Objects.Single(o => o.Key == "p/big.bin").Body.Should().Equal(data);
    }

    [Theory]
    [InlineData(false, "*")]
    [InlineData(true, null)]
    public async Task Exclusive_commit_sends_if_none_match(bool overwrite, string? expected)
    {
        var (s, fake) = Make();

        await using (var small = await s.CreateAsync("small", new CreateOptions(Overwrite: overwrite), Ct))
        {
            await small.WriteAsync(Data(10), Ct);
            await small.CommitAsync(Ct);
        }
        await using (var big = await s.CreateAsync("big", new CreateOptions(Overwrite: overwrite), Ct))
        {
            await WriteChunked(big, Data(17 * MiB));
            await big.CommitAsync(Ct);
        }

        fake.Puts.Should().ContainSingle().Which.IfNoneMatch.Should().Be(expected);
        fake.Completes.Should().ContainSingle().Which.IfNoneMatch.Should().Be(expected);
    }

    [Fact]
    public async Task Precondition_failed_on_commit_throws_conflict()
    {
        var (s, fake) = Make();
        await using var small = await s.CreateAsync("small", new CreateOptions(), Ct);
        await using var big = await s.CreateAsync("big", new CreateOptions(), Ct);
        await small.WriteAsync(Data(10), Ct);
        await WriteChunked(big, Data(17 * MiB));
        fake.Add("p/small").Add("p/big"); // committed by someone else meanwhile

        var commitSmall = () => small.CommitAsync(Ct);
        var commitBig = () => big.CommitAsync(Ct);

        (await commitSmall.Should().ThrowAsync<StorageConflictException>()).Which.Path.Should().Be("small");
        (await commitBig.Should().ThrowAsync<StorageConflictException>()).Which.Path.Should().Be("big");
        await big.DisposeAsync();
        fake.Aborts.Should().ContainSingle().Which.UploadId.Should().Be("upload-1");
    }

    [Fact]
    public async Task Dispose_without_commit_aborts_the_multipart_upload()
    {
        var (s, fake) = Make();
        var writer = await s.CreateAsync("big", new CreateOptions(), Ct);
        await WriteChunked(writer, Data(17 * MiB));

        await writer.DisposeAsync();

        var abort = fake.Aborts.Should().ContainSingle().Subject;
        (abort.BucketName, abort.Key, abort.UploadId).Should().Be(("bucket", "p/big", "upload-1"));
        fake.Completes.Should().BeEmpty();
        fake.Objects.Should().NotContain(o => o.Key == "p/big");
    }

    [Fact]
    public async Task Synchronous_dispose_aborts_too()
    {
        var (s, fake) = Make();
        var writer = await s.CreateAsync("big", new CreateOptions(), Ct);
        await WriteChunked(writer, Data(17 * MiB));

        writer.Dispose();
        writer.Dispose();

        fake.Aborts.Should().ContainSingle();
    }

    [Fact]
    public async Task Dispose_of_a_small_or_committed_writer_calls_nothing()
    {
        var (s, fake) = Make();
        var small = await s.CreateAsync("small", new CreateOptions(), Ct);
        await small.WriteAsync(Data(10), Ct);
        await small.DisposeAsync();

        var big = await s.CreateAsync("big", new CreateOptions(), Ct);
        await WriteChunked(big, Data(17 * MiB));
        await big.CommitAsync(Ct);
        await big.DisposeAsync();

        fake.Aborts.Should().BeEmpty();
        fake.Puts.Should().BeEmpty();
        fake.Objects.Select(o => o.Key).Should().Equal("p/big");
    }

    [Fact]
    public async Task Abort_failure_is_swallowed()
    {
        var (s, fake) = Make();
        fake.Failures["AbortMultipartUploadAsync"] = new HttpRequestException("down");
        var writer = await s.CreateAsync("big", new CreateOptions(), Ct);
        await WriteChunked(writer, Data(17 * MiB));

        var dispose = async () => await writer.DisposeAsync();

        await dispose.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Network_failure_during_part_upload_maps_to_unavailable_and_dispose_aborts()
    {
        var (s, fake) = Make();
        fake.Failures["UploadPartAsync"] = new HttpRequestException("connection reset");
        var writer = await s.CreateAsync("big", new CreateOptions(), Ct);

        var write = async () =>
        {
            await WriteChunked(writer, Data(40 * MiB));
            await writer.CommitAsync(Ct);
        };

        (await write.Should().ThrowAsync<StorageUnavailableException>()).Which.Path.Should().Be("big");
        var commitAgain = () => writer.CommitAsync(Ct);
        await commitAgain.Should().ThrowAsync<StorageUnavailableException>();
        await writer.DisposeAsync();
        fake.Aborts.Should().ContainSingle().Which.UploadId.Should().Be("upload-1");
        fake.Completes.Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_initiate_maps_the_error()
    {
        var (s, fake) = Make();
        fake.Failures["InitiateMultipartUploadAsync"] = new AmazonS3Exception("x", ErrorType.Sender, "AccessDenied", "r", System.Net.HttpStatusCode.Forbidden);
        await using var writer = await s.CreateAsync("big", new CreateOptions(), Ct);

        var write = () => WriteChunked(writer, Data(17 * MiB));

        await write.Should().ThrowAsync<StorageAccessDeniedException>();
    }

    [Fact]
    public async Task Writer_rejects_second_commit_and_writes_after_commit_or_dispose()
    {
        var (s, _) = Make();
        var writer = await s.CreateAsync("a", new CreateOptions(), Ct);
        writer.CanWrite.Should().BeTrue();
        writer.CanRead.Should().BeFalse();
        writer.CanSeek.Should().BeFalse();
        await writer.WriteAsync(Data(1), Ct);
        await writer.CommitAsync(Ct);
        writer.CanWrite.Should().BeFalse();

        var commitAgain = () => writer.CommitAsync(Ct);
        var writeAfter = () => writer.WriteAsync(new byte[] { 1 }, Ct).AsTask();
        await commitAgain.Should().ThrowAsync<InvalidOperationException>();
        await writeAfter.Should().ThrowAsync<InvalidOperationException>();

        var disposed = await s.CreateAsync("b", new CreateOptions(), Ct);
        await disposed.DisposeAsync();
        var writeDisposed = () => disposed.WriteAsync(new byte[] { 1 }, Ct).AsTask();
        await writeDisposed.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Create_exclusive_on_existing_key_throws_conflict_before_writing()
    {
        var (s, fake) = Make();
        fake.Add("p/a.txt");

        var act = () => s.CreateAsync("a.txt", new CreateOptions(Overwrite: false), Ct);

        (await act.Should().ThrowAsync<StorageConflictException>()).Which.Path.Should().Be("a.txt");
        fake.HeadRequests.Should().ContainSingle().Which.Key.Should().Be("p/a.txt");
        fake.Puts.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_with_overwrite_does_not_check_existence()
    {
        var (s, fake) = Make();
        fake.Add("p/a.txt");

        await using var writer = await s.CreateAsync("a.txt", new CreateOptions(Overwrite: true), Ct);

        fake.HeadRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_maps_head_errors_and_rejects_the_root()
    {
        var (s, fake) = Make();
        fake.Failures["GetObjectMetadataAsync"] = new HttpRequestException("down");

        var create = () => s.CreateAsync("a.txt", new CreateOptions(), Ct);
        var root = () => s.CreateAsync("", new CreateOptions(Overwrite: true), Ct);

        await create.Should().ThrowAsync<StorageUnavailableException>();
        await root.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Delete_batches_by_1000()
    {
        var (s, fake) = Make();
        var paths = Enumerable.Range(0, 2500).Select(i => $"f{i}").ToList();
        foreach (var p in paths) fake.Add("p/" + p);

        await s.DeleteAsync(paths, Ct);

        fake.DeleteBatches.Select(b => b.Count).Should().Equal(1000, 1000, 500);
        fake.DeleteBatches.SelectMany(b => b).Should().Equal(paths.Select(p => "p/" + p));
        fake.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_of_missing_paths_is_not_an_error()
    {
        var (s, fake) = Make();

        await s.DeleteAsync(["nope", "d/nope"], Ct);

        fake.DeleteBatches.SelectMany(b => b).Should().Equal("p/nope", "p/d/nope");
    }

    [Fact]
    public async Task Delete_of_root_throws_conflict()
    {
        var (s, fake) = Make();
        fake.Add("p/a");

        var act = () => s.DeleteAsync(["a", ""], Ct);

        await act.Should().ThrowAsync<StorageConflictException>();
        fake.DeleteBatches.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_of_non_empty_directory_throws_conflict()
    {
        var (s, fake) = Make();
        fake.Add("p/d/").Add("p/d/f.txt").Add("p/x");

        var act = () => s.DeleteAsync(["x", "d"], Ct);

        (await act.Should().ThrowAsync<StorageConflictException>()).Which.Path.Should().Be("d");
        fake.Objects.Select(o => o.Key).Should().BeEquivalentTo(["p/d/", "p/d/f.txt"]);
    }

    [Fact]
    public async Task Delete_of_lone_placeholder_deletes_it()
    {
        var (s, fake) = Make();
        fake.Add("p/x/").Add("p/xy");

        await s.DeleteAsync(["x"], Ct);

        fake.DeleteBatches.SelectMany(b => b).Should().BeEquivalentTo(["p/x", "p/x/"]);
        fake.Objects.Select(o => o.Key).Should().Equal("p/xy");
    }

    [Fact]
    public async Task Delete_of_a_directory_emptied_earlier_in_the_same_call_succeeds()
    {
        var (s, fake) = Make();
        fake.Add("p/d/").Add("p/d/f.txt");

        await s.DeleteAsync(["d/f.txt", "d"], Ct);

        fake.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_errors_in_the_response_are_mapped()
    {
        var (s, fake) = Make();
        fake.Add("p/a").Add("p/b");
        fake.DeleteErrors["p/b"] = "AccessDenied";

        var act = () => s.DeleteAsync(["a", "b"], Ct);

        (await act.Should().ThrowAsync<StorageAccessDeniedException>()).Which.Path.Should().Be("b");
    }

    [Fact]
    public async Task Delete_error_for_a_missing_key_is_ignored()
    {
        var (s, fake) = Make();
        fake.DeleteErrors["p/a"] = "NoSuchKey";

        await s.DeleteAsync(["a"], Ct);
    }

    [Fact]
    public async Task Delete_maps_request_failures()
    {
        var (s, fake) = Make();
        fake.Failures["DeleteObjectsAsync"] = new HttpRequestException("down");

        var act = () => s.DeleteAsync(["a"], Ct);

        await act.Should().ThrowAsync<StorageUnavailableException>();
    }

    [Fact]
    public async Task Delete_exception_without_a_response_fails()
    {
        var (s, fake) = Make();
        fake.Add("p/a");
        fake.Failures["DeleteObjectsAsync"] = new DeleteObjectsException(new DeleteObjectsResponse()) { Response = null };

        var act = () => s.DeleteAsync(["a"], Ct);

        (await act.Should().ThrowAsync<StorageIOException>()).Which.Path.Should().Be("a");
    }

    [Fact]
    public async Task Delete_exception_with_per_key_errors_is_mapped()
    {
        var (s, fake) = Make();
        fake.Add("p/a");
        var response = new DeleteObjectsResponse { DeleteErrors = [new DeleteError { Key = "p/a", Code = "AccessDenied" }] };
        fake.Failures["DeleteObjectsAsync"] = new DeleteObjectsException(response);

        var act = () => s.DeleteAsync(["a"], Ct);

        await act.Should().ThrowAsync<StorageAccessDeniedException>();
    }
}
