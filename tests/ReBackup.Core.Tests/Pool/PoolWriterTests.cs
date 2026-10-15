using System.IO.Hashing;
using FluentAssertions;
using ReBackup.Core.Pool;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Pool;

public class PoolWriterTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly InMemoryStorage _target = new();

    private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static string HashOf(byte[] b) => PoolLayout.Format(XxHash128.HashToUInt128(b));

    [Fact]
    public async Task HashAsync_matches_XxHash128()
    {
        var data = new byte[100_000];
        new Random(1).NextBytes(data);
        var seen = 0;

        var (hash, size) = await PoolWriter.HashAsync(new MemoryStream(data), new byte[4096], n => seen += n, Ct);

        hash.Should().Be(HashOf(data));
        size.Should().Be(data.Length);
        seen.Should().Be(data.Length);
    }

    [Fact]
    public async Task Put_writes_blob_at_its_path()
    {
        var data = Bytes("hello pool");
        var hash = HashOf(data);
        var writer = new PoolWriter(_target);

        var outcome = await writer.PutAsync(new MemoryStream(data), hash, new byte[16], null, Ct);

        outcome.Should().Be(PutOutcome.Written);
        _target.ReadAllBytes(PoolLayout.BlobPath(hash)).Should().Equal(data);
        writer.Contains(hash).Should().BeTrue();
        writer.Contains("XXH128:" + hash["xxh128:".Length..].ToUpperInvariant()).Should().BeTrue();
    }

    [Fact]
    public async Task Put_of_known_hash_is_already_present()
    {
        var data = Bytes("hello pool");
        var hash = HashOf(data);
        await new PoolWriter(_target).PutAsync(new MemoryStream(data), hash, new byte[16], null, Ct);
        _target.AddFile(PoolLayout.PoolFolder + "/junk.txt", [1]);
        var second = new PoolWriter(_target);

        await second.LoadKnownAsync(Ct);
        second.Contains(hash).Should().BeTrue();
        var outcome = await second.PutAsync(new MemoryStream(data), hash, new byte[16], null, Ct);

        outcome.Should().Be(PutOutcome.AlreadyPresent);
    }

    [Fact]
    public async Task Put_of_existing_blob_unknown_to_writer_is_already_present()
    {
        var data = Bytes("x");
        var hash = HashOf(data);
        await new PoolWriter(_target).PutAsync(new MemoryStream(data), hash, new byte[16], null, Ct);
        var fresh = new PoolWriter(_target);

        (await fresh.PutAsync(new MemoryStream(data), hash, new byte[16], null, Ct)).Should().Be(PutOutcome.AlreadyPresent);
        fresh.Contains(hash).Should().BeTrue();
    }

    [Fact]
    public async Task Content_changed_between_reads_is_not_committed()
    {
        var hash = HashOf(Bytes("a"));
        var writer = new PoolWriter(_target);

        var outcome = await writer.PutAsync(new MemoryStream(Bytes("b")), hash, new byte[16], null, Ct);

        outcome.Should().Be(PutOutcome.ContentChanged);
        _target.Files.Should().BeEmpty();
        writer.Contains(hash).Should().BeFalse();
    }

    [Fact]
    public async Task Cancelled_put_is_not_committed()
    {
        var data = new byte[10_000];
        var hash = HashOf(data);
        using var cts = new CancellationTokenSource();
        var writer = new PoolWriter(_target);

        var act = () => writer.PutAsync(new MemoryStream(data), hash, new byte[100], _ => cts.Cancel(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _target.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadKnown_on_empty_target_is_empty()
    {
        var writer = new PoolWriter(_target);

        await writer.LoadKnownAsync(Ct);

        writer.Contains(HashOf(Bytes("a"))).Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_puts_of_same_content_leave_one_blob()
    {
        var data = new byte[200_000];
        new Random(2).NextBytes(data);
        var hash = HashOf(data);
        var writer = new PoolWriter(_target);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            writer.PutAsync(new MemoryStream(data), hash, new byte[4096], null, Ct))));

        outcomes.Count(o => o == PutOutcome.Written).Should().Be(1);
        outcomes.Count(o => o == PutOutcome.AlreadyPresent).Should().Be(7);
        _target.Files.Should().BeEquivalentTo([PoolLayout.BlobPath(hash)]);
        _target.ReadAllBytes(PoolLayout.BlobPath(hash)).Should().Equal(data);
    }
}
