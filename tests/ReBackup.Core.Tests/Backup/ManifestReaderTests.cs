using System.Text;
using System.Text.Json;
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Json;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Backup;

public class ManifestReaderTests : IDisposable
{
    private const string TwoFiles = """
        [ { "path": "a.txt", "size": 100, "mtimeUtc": "2026-01-01T00:00:00Z", "hash": "xxh64:0000000000000000" },
          { "path": "b.txt", "size": 200, "mtimeUtc": "2026-01-01T00:00:00Z", "hash": "xxh64:0000000000000000" } ]
        """;

    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private const string ManifestPath = "re-manifest.json";

    private IStorage Storage => new FileSystemStorage(_tmp.Root);

    private string Write(string content)
    {
        _tmp.WriteFile(ManifestPath, content);
        return ManifestPath;
    }

    private Task<ManifestHeader> ReadHeader(string path) => ManifestReader.ReadHeaderAsync(Storage, path);

    [Fact]
    public async Task Reads_the_fields_in_front_of_the_file_list()
    {
        var path = Write($$"""
            { "formatVersion": 1, "planId": "p1", "planName": "Projects", "createdUtc": "2026-09-30T12:05:00Z",
              "source": "D:\\Projects", "fileCount": 2, "totalBytes": 300, "files": {{TwoFiles}} }
            """);

        var header = await ReadHeader(path);

        header.Should().Be(new ManifestHeader("p1", "Projects", new DateTime(2026, 9, 30, 12, 5, 0, DateTimeKind.Utc), 2, 300));
    }

    [Fact]
    public async Task Does_not_parse_the_file_list_when_the_header_is_complete()
    {
        var path = Write("""{ "planId": "p1", "planName": "Projects", "fileCount": 2, "totalBytes": 300, "files": [ this is not json""");

        (await ReadHeader(path)).PlanId.Should().Be("p1");
    }

    [Fact]
    public async Task A_manifest_without_totals_has_none_in_its_header_and_can_be_summed_up()
    {
        var path = Write($$"""{ "formatVersion": 1, "planId": "p1", "planName": "Projects", "files": {{TwoFiles}} }""");

        var header = await ReadHeader(path);

        header.PlanId.Should().Be("p1");
        header.FileCount.Should().BeNull();
        header.TotalBytes.Should().BeNull();
        (await ManifestReader.ReadTotalsAsync(Storage, path)).Should().Be((2, 300L));
    }

    [Fact]
    public async Task A_manifest_whose_file_list_comes_first_is_read_in_full()
    {
        var path = Write($$"""{ "files": {{TwoFiles}}, "planId": "p1", "planName": "Projects" }""");

        var header = await ReadHeader(path);

        header.PlanId.Should().Be("p1");
        header.FileCount.Should().Be(2);
        header.TotalBytes.Should().Be(300);
    }

    [Fact]
    public async Task A_header_larger_than_the_read_buffer_is_read_in_full()
    {
        var longSource = new string('x', 70_000);
        var path = Write($$"""{ "source": "{{longSource}}", "planId": "p1", "planName": "Projects", "fileCount": 2, "totalBytes": 300, "files": {{TwoFiles}} }""");

        (await ReadHeader(path)).Should().Be(new ManifestHeader("p1", "Projects", default, 2, 300));
    }

    [Fact]
    public async Task A_byte_order_mark_is_accepted()
    {
        const string path = ManifestPath;
        File.WriteAllText(_tmp.PathOf(path), """{ "planId": "p1", "planName": "Projects", "fileCount": 0, "totalBytes": 0, "files": [] }""",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        (await ReadHeader(path)).PlanId.Should().Be("p1");
    }

    [Fact]
    public async Task A_manifest_without_a_plan_id_has_an_empty_one()
    {
        (await ReadHeader(Write("{}"))).PlanId.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ \"planId\": ")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task Damaged_manifests_throw_JsonException(string content)
    {
        var path = Write(content);

        var act = () => ReadHeader(path);

        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task A_missing_manifest_throws_StorageNotFoundException()
    {
        var act = () => ReadHeader(ManifestPath);

        await act.Should().ThrowAsync<StorageNotFoundException>();
    }

    [Fact]
    public async Task A_header_larger_than_the_read_buffer_is_read_from_a_stream_that_cannot_seek()
    {
        var longSource = new string('x', 70_000);
        var path = Write($$"""{ "source": "{{longSource}}", "planId": "p1", "planName": "Projects", "fileCount": 2, "totalBytes": 300, "files": {{TwoFiles}} }""");
        var storage = new WrappedStorage(Storage) { NonSeekable = true };

        var header = await ManifestReader.ReadHeaderAsync(storage, path);

        header.Should().Be(new ManifestHeader("p1", "Projects", default, 2, 300));
        storage.Opens.Should().Be(2, "the window did not hold the header, so the manifest is opened again and read in full");
    }

    [Fact]
    public async Task A_header_within_the_read_buffer_needs_one_open_of_a_stream_that_cannot_seek()
    {
        var path = Write($$"""{ "planId": "p1", "planName": "Projects", "fileCount": 2, "totalBytes": 300, "files": {{TwoFiles}} }""");
        var storage = new WrappedStorage(Storage) { NonSeekable = true };

        (await ManifestReader.ReadHeaderAsync(storage, path)).PlanId.Should().Be("p1");
        (await ManifestReader.ReadTotalsAsync(storage, path)).Should().Be((2, 300L));
        storage.Opens.Should().Be(2, "one for the header, one for the totals");
    }

    [Fact]
    public async Task Format_1_manifest_reads_with_empty_directories()
    {
        const string json = $$"""{ "formatVersion": 1, "planId": "p1", "planName": "Projects", "fileCount": 2, "totalBytes": 300, "files": {{TwoFiles}} }""";

        var manifest = JsonSerializer.Deserialize<BackupManifest>(json, JsonDefaults.Options)!;

        manifest.FormatVersion.Should().Be(1);
        manifest.Directories.Should().BeEmpty();
        manifest.Files.Should().HaveCount(2);
        (await ReadHeader(Write(json))).Should().Be(new ManifestHeader("p1", "Projects", default, 2, 300));
    }

    [Fact]
    public async Task Format_2_manifest_round_trips_directories()
    {
        var written = new BackupManifest
        {
            PlanId = "p1",
            PlanName = "Projects",
            FileCount = 1,
            TotalBytes = 5,
            Files = [new ManifestFile("sub/a.txt", 5, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "xxh64:0000000000000000")],
            Directories = ["empty", "sub", "sub/deeper"],
        };
        written.FormatVersion.Should().Be(2, "new manifests are format 2");
        var json = JsonSerializer.Serialize(written, JsonDefaults.Options);
        var storage = new InMemoryStorage();
        storage.AddFile(ManifestPath, Encoding.UTF8.GetBytes(json));

        var read = JsonSerializer.Deserialize<BackupManifest>(json, JsonDefaults.Options)!;

        read.FormatVersion.Should().Be(2);
        read.Directories.Should().Equal("empty", "sub", "sub/deeper");
        json.IndexOf("\"directories\"", StringComparison.Ordinal).Should().BeGreaterThan(json.IndexOf("\"files\"", StringComparison.Ordinal),
            "the directory list follows the file list, so the header stays small");
        (await ManifestReader.ReadHeaderAsync(storage, ManifestPath)).Should().Be(new ManifestHeader("p1", "Projects", default, 1, 5));
        (await ManifestReader.ReadTotalsAsync(storage, ManifestPath)).Should().Be((1, 5L));
    }
}
