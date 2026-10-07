using FluentAssertions;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.IO;

namespace ReBackup.Core.Tests.IO;

public class AtomicFileTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Writes_new_file_and_creates_directory()
    {
        var path = _tmp.PathOf(@"a\b\file.json");

        AtomicFile.WriteAllText(path, "hello");

        File.ReadAllText(path).Should().Be("hello");
    }

    [Fact]
    public void Overwrites_existing_file_and_leaves_no_temp_file()
    {
        var path = _tmp.WriteFile("file.json", "old");

        AtomicFile.WriteAllText(path, "new");

        File.ReadAllText(path).Should().Be("new");
        File.Exists(path + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void Writes_utf8_without_bom()
    {
        var path = _tmp.PathOf("file.json");

        AtomicFile.WriteAllText(path, "ä");

        File.ReadAllBytes(path).Should().Equal(0xC3, 0xA4);
    }
}
