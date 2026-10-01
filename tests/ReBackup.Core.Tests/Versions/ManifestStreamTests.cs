using System.Text;
using System.Text.Json;
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Json;
using ReBackup.Core.Versions;

namespace ReBackup.Core.Tests.Versions;

public class ManifestStreamTests
{
    private static readonly DateTime Mtime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<ManifestFile> ReadAll(string json, out ManifestSummary summary)
    {
        var files = new List<ManifestFile>();
        summary = ManifestStream.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)), files.Add);
        return files;
    }

    [Fact]
    public void Reads_the_fields_and_every_entry()
    {
        var files = ReadAll("""
            { "formatVersion": 1, "planId": "p1", "planName": "Projects", "createdUtc": "2026-09-30T12:05:00Z",
              "source": "D:\\Projects", "fileCount": 2, "totalBytes": 300,
              "files": [ { "path": "a.txt", "size": 100, "mtimeUtc": "2026-01-01T00:00:00Z", "hash": "xxh64:00000000000000ff" },
                         { "path": "sub/b.txt", "size": 200, "mtimeUtc": "2026-01-01T00:00:00Z", "hash": "xxh64:0000000000000001" } ] }
            """, out var summary);

        summary.Should().Be(new ManifestSummary("p1", "Projects", new DateTime(2026, 9, 30, 12, 5, 0, DateTimeKind.Utc), @"D:\Projects"));
        files.Should().Equal(
            new ManifestFile("a.txt", 100, Mtime, "xxh64:00000000000000ff"),
            new ManifestFile("sub/b.txt", 200, Mtime, "xxh64:0000000000000001"));
    }

    [Fact]
    public void Takes_fields_behind_the_file_list_and_skips_unknown_values()
    {
        var files = ReadAll("""
            { "files": [ { "path": "a.txt", "size": 1, "extra": { "x": [1, 2] }, "mtimeUtc": "2026-01-01T00:00:00Z", "hash": "" } ],
              "unknown": { "nested": [ { } ] }, "planId": "p1", "source": "C:\\s" }
            """, out var summary);

        files.Should().ContainSingle().Which.Path.Should().Be("a.txt");
        summary.PlanId.Should().Be("p1");
        summary.Source.Should().Be(@"C:\s");
    }

    [Fact]
    public void Accepts_a_byte_order_mark()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("""{ "planId": "p1", "files": [] }""")).ToArray();

        ManifestStream.Read(new MemoryStream(bytes), _ => { }).PlanId.Should().Be("p1");
    }

    [Theory]
    [InlineData("""[ 1, 2 ]""")]
    [InlineData("""{ "planId": "p1", "files": [ { "path": "a.txt" """)]
    [InlineData("""{ "files": [ 42 ] }""")]
    [InlineData("""{ "files": [ { "size": 1 } ] }""")]
    public void Rejects_what_is_not_a_manifest(string json)
    {
        var read = () => ReadAll(json, out _);

        read.Should().Throw<JsonException>();
    }

    [Fact]
    public void Reads_a_large_manifest_through_a_small_window()
    {
        var manifest = new BackupManifest { PlanId = "p1", PlanName = "Projects", Source = @"C:\s" };
        for (var i = 0; i < 100_000; i++)
            manifest.Files.Add(new ManifestFile($"folder{i % 100}/file{i}.bin", i, Mtime, "xxh64:0123456789abcdef"));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonDefaults.Options);
        bytes.Length.Should().BeGreaterThan(10_000_000, "the test needs a manifest much larger than the read window");
        var stream = new CountingStream(bytes);
        long readWhenFirstFileArrived = -1;
        var count = 0;

        ManifestStream.Read(stream, _ =>
        {
            if (count++ == 0)
                readWhenFirstFileArrived = stream.BytesRead;
        });

        count.Should().Be(100_000);
        readWhenFirstFileArrived.Should().BeLessOrEqualTo(ManifestStream.InitialBufferSize,
            "entries are handed out while the file is read, not after reading all of it");
        stream.LargestRead.Should().BeLessOrEqualTo(ManifestStream.InitialBufferSize);
    }

    /// <summary>A forward-only stream over bytes that records how much has been read.</summary>
    private sealed class CountingStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public long BytesRead { get; private set; }
        public int LargestRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            LargestRead = Math.Max(LargestRead, read);
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
