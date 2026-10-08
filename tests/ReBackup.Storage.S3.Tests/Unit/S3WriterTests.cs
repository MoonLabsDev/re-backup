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
        complete.PartETags.Select(p => (p.PartNumber, p.ETag)).Should().Equal(fake.Parts.Select(p => ((int?)p.PartNumber, FakeS3Client.Quote(FakeS3Client.Md5Hex(p.Body)))));
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
    public async Task Exclusive_put_whose_retry_meets_its_own_object_succeeds()
    {
        // The SDK retried a PutObject whose first attempt had succeeded: the retry gets 412 for our own object.
        var (s, fake) = Make();
        fake.RetryAfterSuccess = true;
        var data = Data(1000);

        await using (var writer = await s.CreateAsync("small", new CreateOptions(), Ct))
        {
            await writer.WriteAsync(data, Ct);
            await writer.CommitAsync(Ct);
            writer.CanWrite.Should().BeFalse("the commit counts as done");
        }

        fake.HeadRequests.Should().HaveCount(2).And.OnlyContain(h => h.Key == "p/small"); // create check + proof
        fake.Objects.Single().Body.Should().Equal(data);
    }

    [Fact]
    public async Task Exclusive_multipart_whose_retry_meets_its_own_object_succeeds()
    {
        var (s, fake) = Make();
        fake.RetryAfterSuccess = true;

        await using (var writer = await s.CreateAsync("big", new CreateOptions(), Ct))
        {
            await WriteChunked(writer, Data(17 * MiB));
            await writer.CommitAsync(Ct);
        }

        fake.Aborts.Should().BeEmpty();
        fake.Objects.Single().Key.Should().Be("p/big");
    }

    [Fact]
    public async Task Exclusive_commit_over_a_foreign_object_with_other_content_is_a_conflict()
    {
        var (s, fake) = Make();
        await using var small = await s.CreateAsync("small", new CreateOptions(), Ct);
        await using var big = await s.CreateAsync("big", new CreateOptions(), Ct);
        await small.WriteAsync(Data(10), Ct);
        await WriteChunked(big, Data(17 * MiB));
        var other = Data(11);
        fake.Add("p/small", etag: FakeS3Client.Quote(FakeS3Client.Md5Hex(other)), body: other);
        fake.Add("p/big", etag: FakeS3Client.Quote(FakeS3Client.MultipartETag([other])), body: other);

        var commitSmall = () => small.CommitAsync(Ct);
        var commitBig = () => big.CommitAsync(Ct);

        await commitSmall.Should().ThrowAsync<StorageConflictException>();
        await commitBig.Should().ThrowAsync<StorageConflictException>();
        fake.HeadRequests.Should().HaveCount(4, "a create check and a proof check each");
    }

    [Fact]
    public async Task Exclusive_commit_conflict_stays_a_conflict_when_the_proof_check_fails()
    {
        var (s, fake) = Make();
        fake.RetryAfterSuccess = true;
        await using var writer = await s.CreateAsync("small", new CreateOptions(), Ct);
        await writer.WriteAsync(Data(10), Ct);
        fake.Failures["GetObjectMetadataAsync"] = new HttpRequestException("down");

        var commit = () => writer.CommitAsync(Ct);

        (await commit.Should().ThrowAsync<StorageConflictException>()).Which.Path.Should().Be("small");
        var again = () => writer.CommitAsync(Ct);
        await again.Should().ThrowAsync<StorageConflictException>();
    }

    [Fact]
    public async Task Exclusive_commit_answered_with_a_conditional_request_conflict_checks_too()
    {
        // 409 ConditionalRequestConflict: AWS's answer while another conditional write to the key is in flight, e.g. our own first attempt.
        var (s, fake) = Make();
        var data = Data(10);
        await using var writer = await s.CreateAsync("small", new CreateOptions(), Ct);
        await writer.WriteAsync(data, Ct);
        fake.Add("p/small", etag: FakeS3Client.Quote(FakeS3Client.Md5Hex(data)), body: data);
        fake.Failures["PutObjectAsync"] = new AmazonS3Exception("x", ErrorType.Sender, "ConditionalRequestConflict", "r", System.Net.HttpStatusCode.Conflict);

        await writer.CommitAsync(Ct);
    }

    [Fact]
    public async Task Exclusive_commit_proof_needs_a_matching_etag_not_just_an_object()
    {
        // An ETag that is no MD5 of our bytes (e.g. under SSE-KMS) proves nothing: conflict.
        var (s, fake) = Make();
        var data = Data(10);
        await using var writer = await s.CreateAsync("small", new CreateOptions(), Ct);
        await writer.WriteAsync(data, Ct);
        fake.Add("p/small", etag: "\"not-an-md5\"", body: data);

        var commit = () => writer.CommitAsync(Ct);

        await commit.Should().ThrowAsync<StorageConflictException>();
    }

    [Fact]
    public async Task Cancellation_during_the_proof_check_is_cancellation_not_a_remembered_conflict()
    {
        var (s, fake) = Make();
        fake.RetryAfterSuccess = true;
        using var cts = new CancellationTokenSource();
        await using var writer = await s.CreateAsync("small", new CreateOptions(), Ct);
        await writer.WriteAsync(Data(10), Ct);
        fake.BeforeHead = () =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        var commit = () => writer.CommitAsync(cts.Token);

        await commit.Should().ThrowAsync<OperationCanceledException>();
        fake.BeforeHead = null;
        await writer.CommitAsync(Ct); // not stuck on a conflict: the retry finds its own object
    }

    [Fact]
    public async Task The_proof_accepts_an_uppercase_quoted_object_etag()
    {
        var (s, fake) = Make();
        var data = Data(10);
        await using var writer = await s.CreateAsync("small", new CreateOptions(), Ct);
        await writer.WriteAsync(data, Ct);
        fake.Add("p/small", etag: FakeS3Client.Quote(FakeS3Client.Md5Hex(data).ToUpperInvariant()), body: data);

        await writer.CommitAsync(Ct);
    }

    [Fact]
    public void Multipart_etag_is_the_md5_of_the_part_md5s_and_null_without_md5_part_etags()
    {
        byte[][] parts = [Data(5), Data(7)];
        var etags = parts.Select((p, i) => new PartETag(i + 1, FakeS3Client.Quote(FakeS3Client.Md5Hex(p)))).ToList();

        S3Writer.MultipartETag(etags).Should().Be(FakeS3Client.MultipartETag(parts));
        S3Writer.MultipartETag([etags[0], new PartETag(2, "\"" + new string('z', 32) + "\"")]).Should().BeNull("not hex");
        S3Writer.MultipartETag([etags[0], new PartETag(2, "\"abc123\"")]).Should().BeNull("too short");
        S3Writer.MultipartETag([etags[0], new PartETag(2, (string?)null)]).Should().BeNull("missing");
    }

    [Fact]
    public async Task A_truncated_listing_page_without_continuation_token_fails_closed()
    {
        var (s, fake) = Make();
        fake.Add("p/d/").Add("p/d/f").Add("p/x");
        fake.TruncatedWithoutToken = true;

        var delete = () => s.DeleteAsync(["d", "x"], Ct);
        var list = async () => { await foreach (var _ in s.ListAsync("", recursive: true, Ct)) { } };

        await delete.Should().ThrowAsync<StorageIOException>();
        await list.Should().ThrowAsync<StorageIOException>();
        fake.DeleteBatches.Should().BeEmpty();
        fake.Objects.Should().HaveCount(3);
    }

    [Fact]
    public async Task Overwrite_commit_never_checks_after_the_write()
    {
        var (s, fake) = Make();
        await using (var writer = await s.CreateAsync("small", new CreateOptions(Overwrite: true), Ct))
        {
            await writer.WriteAsync(Data(10), Ct);
            await writer.CommitAsync(Ct);
        }

        fake.HeadRequests.Should().BeEmpty();
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
    public async Task Create_exclusive_proceeds_when_the_check_is_denied_and_the_commit_stays_exclusive()
    {
        // Prefix-scoped IAM: without an effective s3:ListBucket, AWS answers a HEAD on a missing key with 403, not 404.
        var (s, fake) = Make();
        fake.Failures["GetObjectMetadataAsync"] = new AmazonS3Exception("x", ErrorType.Sender, "AccessDenied", "r", System.Net.HttpStatusCode.Forbidden);

        await using (var writer = await s.CreateAsync("a.txt", new CreateOptions(Overwrite: false), Ct))
        {
            await writer.WriteAsync(new byte[] { 1, 2 }, Ct);
            await writer.CommitAsync(Ct);
        }

        fake.Puts.Should().ContainSingle().Which.IfNoneMatch.Should().Be("*");
        fake.Objects.Should().ContainSingle(o => o.Key == "p/a.txt");
    }

    [Fact]
    public async Task Create_exclusive_with_a_denied_check_still_conflicts_on_commit_when_the_key_exists()
    {
        var (s, fake) = Make();
        fake.Add("p/a.txt");
        fake.Failures["GetObjectMetadataAsync"] = new AmazonS3Exception("x", ErrorType.Sender, "AccessDenied", "r", System.Net.HttpStatusCode.Forbidden);

        await using var writer = await s.CreateAsync("a.txt", new CreateOptions(Overwrite: false), Ct);
        await writer.WriteAsync(new byte[] { 1 }, Ct);
        var commit = () => writer.CommitAsync(Ct);

        (await commit.Should().ThrowAsync<StorageConflictException>()).Which.Path.Should().Be("a.txt");
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
    public async Task Delete_of_2500_files_under_one_folder_lists_by_page_not_by_path()
    {
        var (s, fake) = Make();
        var paths = Enumerable.Range(0, 2500).Select(i => $"d/f{i:D4}").ToList();
        foreach (var p in paths) fake.Add("p/" + p);

        await s.DeleteAsync(paths, Ct);

        fake.ListRequests.Should().HaveCountLessThanOrEqualTo(4);
        fake.DeleteBatches.Select(b => b.Count).Should().Equal(1000, 1000, 500);
        fake.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_of_a_top_level_file_lists_only_the_top_level()
    {
        // A recursive listing of the parent would walk the whole storage for one file.
        var (s, fake) = Make();
        foreach (var i in Enumerable.Range(0, 50)) fake.Add($"p/big/f{i}");
        fake.Add("p/x");

        await s.DeleteAsync(["x"], Ct);

        var list = fake.ListRequests.Should().ContainSingle().Subject;
        (list.Prefix, list.Delimiter).Should().Be(("p/", "/"));
        fake.Objects.Should().HaveCount(50);
    }

    [Fact]
    public async Task Delete_of_a_directory_and_its_tree_lists_the_tree_once()
    {
        var (s, fake) = Make();
        fake.PageSize = 2;
        fake.Add("p/v/").Add("p/v/a/").Add("p/v/a/x").Add("p/v/a/y").Add("p/v/b/z").Add("p/w/keep");

        // Bottom-up, as the version remover sends it.
        await s.DeleteAsync(["v/a/x", "v/a/y", "v/b/z", "v/a", "v/b", "v"], Ct);

        fake.Objects.Select(o => o.Key).Should().Equal("p/w/keep");
        fake.ListRequests.Where(r => r.Delimiter is null).Select(r => r.Prefix).Distinct().Should().Equal("p/v/");
        fake.ListRequests.Where(r => r.Delimiter is not null).Select(r => r.Prefix).Distinct().Should().Equal("p/");
    }

    [Fact]
    public async Task Delete_of_a_directory_whose_content_follows_in_the_same_call_succeeds()
    {
        var (s, fake) = Make();
        fake.Add("p/d/").Add("p/d/f.txt");

        await s.DeleteAsync(["d", "d/f.txt"], Ct);

        fake.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_of_a_directory_with_a_non_empty_subdirectory_conflicts_and_keeps_its_content()
    {
        var (s, fake) = Make();
        fake.Add("p/d/").Add("p/d/e/").Add("p/d/e/f").Add("p/x");

        var act = () => s.DeleteAsync(["x", "d", "d/e"], Ct);

        (await act.Should().ThrowAsync<StorageConflictException>()).Which.Path.Should().Be("d");
        fake.Objects.Select(o => o.Key).Should().BeEquivalentTo(["p/d/", "p/d/e/", "p/d/e/f"]);
    }

    [Fact]
    public async Task Delete_of_a_file_that_also_has_keys_below_it_conflicts()
    {
        var (s, fake) = Make();
        fake.Add("p/a").Add("p/a/b");

        var act = () => s.DeleteAsync(["a"], Ct);

        (await act.Should().ThrowAsync<StorageConflictException>()).Which.Path.Should().Be("a");
        fake.Objects.Should().HaveCount(2);
    }

    [Fact]
    public async Task Delete_maps_listing_failures_to_the_path()
    {
        var (s, fake) = Make();
        fake.Failures["ListObjectsV2Async"] = new HttpRequestException("down");

        var act = () => s.DeleteAsync(["d/a"], Ct);

        (await act.Should().ThrowAsync<StorageUnavailableException>()).Which.Path.Should().Be("d/a");
        fake.DeleteBatches.Should().BeEmpty();
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
