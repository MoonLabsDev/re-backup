using FluentAssertions;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using static ReBackup.Core.Tests.TestSupport.VersionBuilder;

namespace ReBackup.Core.Tests.Versions;

/// <summary>Where the growth between two versions comes from: by folder and by extension.</summary>
public class GrowthQueryTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _target;
    private readonly VersionIndex _index;

    public GrowthQueryTests()
    {
        _target = _tmp.CreateDir("target");
        _index = VersionIndex.Open(_tmp.PathOf("index.db"));
    }

    public void Dispose() => _tmp.Dispose();

    private IReadOnlyList<IndexedVersion> Sync()
    {
        _index.Sync(ListAsync(_target).GetAwaiter().GetResult(), TargetStorage(_target));
        return _index.Versions();
    }

    private long Folder(string path) =>
        _index.FindPath(path, isDirectory: true) ?? throw new InvalidOperationException($"{path} is not indexed");

    [Fact]
    public void Growth_by_folder_sums_added_changed_deleted()
    {
        Write(_target, "2026_09_29-14_05", [File("docs/a.txt", "aaaa"), File("docs/old.txt", "old"), File("media/p.jpg", "12345"),
            File("keep.txt", "k"), File("top.txt", "t")]);
        Write(_target, "2026_09_30-14_05", [File("docs/a.txt", "aaaaaaaa"), File("docs/new.txt", "nn"), File("media/p.jpg", "12345"),
            File("media/q.jpg", "qqqqqq"), File("keep.txt", "k"), File("cache/deep/x.bin", "xxxxxxxxxx")]);
        var versions = Sync();

        var root = _index.GrowthByFolder(versions[0].Id, versions[1].Id, Folder(""));

        root.Should().Equal(
            new GrowthRow("cache/", 1, 0, 0, 10, 0, 10),
            new GrowthRow("media/", 1, 0, 0, 6, 0, 6),
            new GrowthRow("docs/", 1, 1, 1, 2, 3, 3),      // +2 added, -3 deleted, 4 → 8 changed
            new GrowthRow("top.txt", 0, 0, 1, 0, 1, -1));  // keep.txt is unchanged: no row

        _index.GrowthByFolder(versions[0].Id, versions[1].Id, Folder("docs")).Should().Equal(
            new GrowthRow("a.txt", 0, 1, 0, 0, 0, 4),
            new GrowthRow("new.txt", 1, 0, 0, 2, 0, 2),
            new GrowthRow("old.txt", 0, 0, 1, 0, 3, -3));
    }

    [Fact]
    public void Growth_by_extension_groups_case_insensitively()
    {
        Write(_target, "2026_09_29-14_05", [File("a.JPG", "aaa"), File("notes.txt", "nn"), File("sub/gone.TXT", "g")]);
        Write(_target, "2026_09_30-14_05", [File("a.JPG", "aaa"), File("b.jpg", "bbbb"), File("sub/C.Jpg", "ccccc"),
            File("notes.txt", "nnnnnn"), File("README", "rrrrrrr")]);
        var versions = Sync();

        _index.GrowthByExtension(versions[0].Id, versions[1].Id).Should().Equal(
            new GrowthRow(".jpg", 2, 0, 0, 9, 0, 9),
            new GrowthRow("", 1, 0, 0, 7, 0, 7),           // no extension
            new GrowthRow(".txt", 0, 1, 1, 0, 1, 3));      // 2 → 6 changed, 1 deleted

        _index.GrowthByExtension(versions[0].Id, versions[1].Id, Folder("sub")).Should().Equal(
            new GrowthRow(".jpg", 1, 0, 0, 5, 0, 5),
            new GrowthRow(".txt", 0, 0, 1, 0, 1, -1));
    }
}
