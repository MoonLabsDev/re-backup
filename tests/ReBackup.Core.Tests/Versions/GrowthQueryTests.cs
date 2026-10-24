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
            new GrowthRow("cache/", 1, 0, 0, 10, 0, 0, 10),
            new GrowthRow("media/", 1, 0, 0, 6, 0, 0, 6),
            new GrowthRow("docs/", 1, 1, 1, 2, 8, 3, 3),   // +2 added, -3 deleted, 4 → 8 changed (8 copied)
            new GrowthRow("top.txt", 0, 0, 1, 0, 0, 1, -1));  // keep.txt is unchanged: no row

        _index.GrowthByFolder(versions[0].Id, versions[1].Id, Folder("docs")).Should().Equal(
            new GrowthRow("a.txt", 0, 1, 0, 0, 8, 0, 4),
            new GrowthRow("new.txt", 1, 0, 0, 2, 0, 0, 2),
            new GrowthRow("old.txt", 0, 0, 1, 0, 0, 3, -3));
    }

    [Fact]
    public void Growth_after_a_case_only_folder_rename_is_one_row_and_drills_down_into_both_spellings()
    {
        Write(_target, "2026_09_29-14_05", [File("Docs/a.txt", "aaaa")]);
        Write(_target, "2026_09_30-14_05", [File("docs/a.txt", "aaaaaa"), File("docs/b.txt", "bb")]);
        var versions = Sync();

        var root = _index.GrowthByFolder(versions[0].Id, versions[1].Id, Folder(""));

        root.Should().ContainSingle();
        root[0].Key.Should().BeEquivalentTo("docs/");
        root[0].Should().Be(new GrowthRow(root[0].Key, 1, 1, 0, 2, 6, 0, 4));
        foreach (var spelling in new[] { "Docs", "docs" })
        {
            _index.GrowthByFolder(versions[0].Id, versions[1].Id, Folder(spelling)).Should().Equal(
                new GrowthRow("a.txt", 0, 1, 0, 0, 6, 0, 2),
                new GrowthRow("b.txt", 1, 0, 0, 2, 0, 0, 2));
        }
        _index.FilesBelow(versions[1].Id, Folder("Docs")).Select(f => f.Path.ToLowerInvariant())
            .Should().Equal("docs/a.txt", "docs/b.txt");
    }

    [Fact]
    public void Same_size_with_another_hash_counts_as_changed_with_no_net_growth()
    {
        Write(_target, "2026_09_29-14_05", [File("data.bin", "abcd")]);
        Write(_target, "2026_09_30-14_05", [File("data.bin", "abce")]);
        var versions = Sync();

        _index.GrowthByFolder(versions[0].Id, versions[1].Id, Folder("")).Should().Equal(new GrowthRow("data.bin", 0, 1, 0, 0, 4, 0, 0));
    }

    [Fact]
    public void A_rewritten_big_file_ranks_first_by_copied_bytes_though_its_net_growth_is_tiny()
    {
        // An incremental run copies a changed file whole: a mailbox rewritten every day costs its full size each time.
        Write(_target, "2026_09_29-14_05", [File("mail/inbox.pst", new string('a', 1000)), File("notes/n.txt", "n")]);
        Write(_target, "2026_09_30-14_05", [File("mail/inbox.pst", new string('b', 1001)), File("notes/n.txt", "n"),
            File("notes/new.txt", new string('x', 50))]);
        var versions = Sync();

        var byNet = _index.Growth(versions[0].Id, versions[1].Id, Folder(""));
        var byCopied = _index.Growth(versions[0].Id, versions[1].Id, Folder(""), GrowthSort.Copied);

        byNet.ByFolder.Select(r => r.Key).Should().Equal("notes/", "mail/");
        byCopied.ByFolder.Select(r => r.Key).Should().Equal("mail/", "notes/");
        byCopied.ByFolder[0].Should().Be(new GrowthRow("mail/", 0, 1, 0, 0, 1001, 0, 1));
        byCopied.ByFolder[0].CopiedBytes.Should().Be(1001, "added plus changed bytes");
        byCopied.ByFolder[1].CopiedBytes.Should().Be(50);
        byCopied.ByExtension.Select(r => r.Key).Should().Equal(".pst", ".txt");
    }

    [Fact]
    public void Growth_gives_both_groupings_in_one_pass()
    {
        Write(_target, "2026_09_29-14_05", [File("a/x.txt", "x"), File("b.JPG", "jj")]);
        Write(_target, "2026_09_30-14_05", [File("a/x.txt", "xxx"), File("c.jpg", "cccc")]);
        var versions = Sync();

        var growth = _index.Growth(versions[0].Id, versions[1].Id, Folder(""));

        growth.ByFolder.Should().Equal(_index.GrowthByFolder(versions[0].Id, versions[1].Id, Folder("")));
        growth.ByExtension.Should().Equal(_index.GrowthByExtension(versions[0].Id, versions[1].Id, Folder("")));
        growth.ByExtension.Should().Equal(new GrowthRow(".jpg", 1, 0, 1, 4, 0, 2, 2), new GrowthRow(".txt", 0, 1, 0, 0, 3, 0, 2));
    }

    [Fact]
    public void Growth_by_extension_groups_case_insensitively()
    {
        Write(_target, "2026_09_29-14_05", [File("a.JPG", "aaa"), File("notes.txt", "nn"), File("sub/gone.TXT", "g")]);
        Write(_target, "2026_09_30-14_05", [File("a.JPG", "aaa"), File("b.jpg", "bbbb"), File("sub/C.Jpg", "ccccc"),
            File("notes.txt", "nnnnnn"), File("README", "rrrrrrr")]);
        var versions = Sync();

        _index.GrowthByExtension(versions[0].Id, versions[1].Id).Should().Equal(
            new GrowthRow(".jpg", 2, 0, 0, 9, 0, 0, 9),
            new GrowthRow("", 1, 0, 0, 7, 0, 0, 7),        // no extension
            new GrowthRow(".txt", 0, 1, 1, 0, 6, 1, 3));   // 2 → 6 changed, 1 deleted

        _index.GrowthByExtension(versions[0].Id, versions[1].Id, Folder("sub")).Should().Equal(
            new GrowthRow(".jpg", 1, 0, 0, 5, 0, 0, 5),
            new GrowthRow(".txt", 0, 0, 1, 0, 0, 1, -1));
    }
}
