using System.Text;
using System.Text.Json;
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Backup;

public class ManifestReaderTests : IDisposable
{
    private const string TwoFiles = """
        [ { "path": "a.txt", "size": 100, "mtimeUtc": "2026-01-01T00:00:00Z", "hash": "xxh64:0000000000000000" },
          { "path": "b.txt", "size": 200, "mtimeUtc": "2026-01-01T00:00:00Z", "hash": "xxh64:0000000000000000" } ]
        """;

    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private string Write(string content) => _tmp.WriteFile("re-manifest.json", content);

    [Fact]
    public void Reads_the_fields_in_front_of_the_file_list()
    {
        var path = Write($$"""
            { "formatVersion": 1, "planId": "p1", "planName": "Projects", "createdUtc": "2026-09-30T12:05:00Z",
              "source": "D:\\Projects", "fileCount": 2, "totalBytes": 300, "files": {{TwoFiles}} }
            """);

        var header = ManifestReader.ReadHeader(path);

        header.Should().Be(new ManifestHeader("p1", "Projects", new DateTime(2026, 9, 30, 12, 5, 0, DateTimeKind.Utc), 2, 300));
    }

    [Fact]
    public void Does_not_parse_the_file_list_when_the_header_is_complete()
    {
        var path = Write("""{ "planId": "p1", "planName": "Projects", "fileCount": 2, "totalBytes": 300, "files": [ this is not json""");

        ManifestReader.ReadHeader(path).PlanId.Should().Be("p1");
    }

    [Fact]
    public void A_manifest_without_totals_has_none_in_its_header_and_can_be_summed_up()
    {
        var path = Write($$"""{ "formatVersion": 1, "planId": "p1", "planName": "Projects", "files": {{TwoFiles}} }""");

        var header = ManifestReader.ReadHeader(path);

        header.PlanId.Should().Be("p1");
        header.FileCount.Should().BeNull();
        header.TotalBytes.Should().BeNull();
        ManifestReader.ReadTotals(path).Should().Be((2, 300L));
    }

    [Fact]
    public void A_manifest_whose_file_list_comes_first_is_read_in_full()
    {
        var path = Write($$"""{ "files": {{TwoFiles}}, "planId": "p1", "planName": "Projects" }""");

        var header = ManifestReader.ReadHeader(path);

        header.PlanId.Should().Be("p1");
        header.FileCount.Should().Be(2);
        header.TotalBytes.Should().Be(300);
    }

    [Fact]
    public void A_header_larger_than_the_read_buffer_is_read_in_full()
    {
        var longSource = new string('x', 70_000);
        var path = Write($$"""{ "source": "{{longSource}}", "planId": "p1", "planName": "Projects", "fileCount": 2, "totalBytes": 300, "files": {{TwoFiles}} }""");

        ManifestReader.ReadHeader(path).Should().Be(new ManifestHeader("p1", "Projects", default, 2, 300));
    }

    [Fact]
    public void A_byte_order_mark_is_accepted()
    {
        var path = _tmp.PathOf("re-manifest.json");
        File.WriteAllText(path, """{ "planId": "p1", "planName": "Projects", "fileCount": 0, "totalBytes": 0, "files": [] }""",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        ManifestReader.ReadHeader(path).PlanId.Should().Be("p1");
    }

    [Fact]
    public void A_manifest_without_a_plan_id_has_an_empty_one()
    {
        ManifestReader.ReadHeader(Write("{}")).PlanId.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ \"planId\": ")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Damaged_manifests_throw_JsonException(string content)
    {
        var path = Write(content);

        var act = () => ManifestReader.ReadHeader(path);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void A_missing_manifest_throws_FileNotFoundException()
    {
        var act = () => ManifestReader.ReadHeader(_tmp.PathOf("re-manifest.json"));

        act.Should().Throw<FileNotFoundException>();
    }
}
