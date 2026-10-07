using System.Text.Json;
using FluentAssertions;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;

namespace ReBackup.Storage.Tests;

public class StorageLocationTests
{
    /// <summary>Options like the app's (camelCase, nulls written) so the converter must not depend on them.</summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Legacy_string_location_is_read_as_file_system()
    {
        var location = JsonSerializer.Deserialize<StorageLocation>("""
            "D:\\Data"
            """, Web);

        location.Should().Be(new StorageLocation("fs", @"D:\Data"));
    }

    [Fact]
    public void Location_is_written_as_object()
    {
        var json = JsonSerializer.Serialize(StorageLocation.FileSystem(@"D:\Data"), Web);

        json.Should().Be("""{"kind":"fs","path":"D:\\Data"}""");
    }

    [Fact]
    public void Connection_id_is_written_and_read_when_present()
    {
        var location = new StorageLocation("s3", "bucket/prefix", "conn-1");

        var json = JsonSerializer.Serialize(location, Web);
        json.Should().Be("{\"kind\":\"s3\",\"path\":\"bucket/prefix\",\"connectionId\":\"conn-1\"}");
        JsonSerializer.Deserialize<StorageLocation>(json, Web).Should().Be(location);
    }

    [Fact]
    public void Object_form_reads_property_names_case_insensitively()
    {
        var location = JsonSerializer.Deserialize<StorageLocation>("{\"Kind\":\"fs\",\"PATH\":\"x\"}", Web);

        location.Should().Be(new StorageLocation("fs", "x"));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"kind\":\"fs\"}")]
    [InlineData("{\"path\":\"x\"}")]
    public void Other_json_is_rejected(string json) =>
        FluentActions.Invoking(() => JsonSerializer.Deserialize<StorageLocation>(json, Web))
            .Should().Throw<JsonException>();

    [Fact]
    public void FileSystem_helper_sets_kind_and_flag()
    {
        var location = StorageLocation.FileSystem(@"C:\x");

        location.Kind.Should().Be("fs");
        location.IsFileSystem.Should().BeTrue();
        new StorageLocation("FS", "x").IsFileSystem.Should().BeFalse();
    }

    [Fact]
    public void Factory_opens_file_system_locations()
    {
        var storage = new StorageFactory().Open(StorageLocation.FileSystem(Path.GetTempPath()));

        storage.Should().BeOfType<FileSystemStorage>();
    }

    [Fact]
    public void Factory_rejects_unknown_kinds() =>
        FluentActions.Invoking(() => new StorageFactory().Open(new StorageLocation("s3", "b")))
            .Should().Throw<NotSupportedException>();
}
