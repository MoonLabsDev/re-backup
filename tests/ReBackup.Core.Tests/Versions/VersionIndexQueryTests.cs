using FluentAssertions;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using static ReBackup.Core.Tests.TestSupport.VersionBuilder;

namespace ReBackup.Core.Tests.Versions;

public class VersionIndexQueryTests : IDisposable
{
    private static readonly DateTime Later = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly TempDir _tmp = new();
    private readonly string _target;
    private readonly VersionIndex _index;

    public VersionIndexQueryTests()
    {
        _target = _tmp.CreateDir("target");
        _index = VersionIndex.Open(_tmp.PathOf("index.db"));
    }

    public void Dispose() => _tmp.Dispose();

    private IReadOnlyList<IndexedVersion> Sync()
    {
        _index.Sync(List(_target));
        return _index.Versions();
    }

    private long Path(string path, bool isDirectory = false) =>
        _index.FindPath(path, isDirectory) ?? throw new InvalidOperationException($"{path} is not indexed");

    private Dictionary<string, DiffStatus> ByPath(IReadOnlyDictionary<long, DiffStatus> statuses, params string[] paths) =>
        paths.ToDictionary(p => p, p => statuses[_index.FindPath(p.TrimEnd('/'), p.EndsWith('/'))!.Value]);

    [Fact]
    public void Lists_the_folders_and_files_of_a_folder()
    {
        Write(_target, "2026_09_30-14_05", [File("b.txt", "bravo"), File("A.txt", "alpha", Later), File("sub/c.txt", "charlie"), File("sub/x/d.txt", "d")]);
        var version = Sync().Single();

        var root = _index.Children(version.Id, Path("", isDirectory: true));

        root.Select(c => (c.Name, c.Path, c.IsDirectory)).Should().Equal(
            ("sub", "sub", true), ("A.txt", "A.txt", false), ("b.txt", "b.txt", false));
        root[0].InVersion.Should().Be(new IndexStats(8, 2, null, null));
        root[1].InVersion!.Size.Should().Be(5);
        root[1].InVersion!.MtimeUtc.Should().Be(Later);
        root[1].InVersion!.Hash.Should().NotBeNull();
        root.Should().OnlyContain(c => c.InOther == null);
        _index.Children(version.Id, Path("sub", isDirectory: true)).Select(c => c.Path).Should().Equal("sub/x", "sub/c.txt");
    }

    [Fact]
    public void Lists_the_entries_of_both_versions_of_a_comparison()
    {
        Write(_target, "2026_09_29-14_05", [File("old.txt", "old"), File("both.txt", "1")]);
        Write(_target, "2026_09_30-14_05", [File("new.txt", "new!"), File("both.txt", "22")]);
        var versions = Sync();

        var root = _index.Children(versions[0].Id, Path("", isDirectory: true), versions[1].Id);

        root.Select(c => (c.Name, c.InVersion?.Size, c.InOther?.Size)).Should().Equal(
            ("both.txt", 1L, 2L), ("new.txt", null, 4L), ("old.txt", 3L, null));
    }

    [Fact]
    public void Compares_by_hash_when_both_versions_have_one()
    {
        Write(_target, "2026_09_29-14_05", [File("same.txt", "same"), File("touched.txt", "same"), File("edited.txt", "abcd"), File("gone.txt", "x")]);
        Write(_target, "2026_09_30-14_05", [File("same.txt", "same"), File("touched.txt", "same", Later), File("edited.txt", "abce"), File("added.txt", "y")]);
        var versions = Sync();

        var statuses = _index.Compare(versions[0].Id, versions[1].Id);

        ByPath(statuses, "same.txt", "touched.txt", "edited.txt", "gone.txt", "added.txt").Should().Equal(new Dictionary<string, DiffStatus>
        {
            ["same.txt"] = DiffStatus.Unchanged,
            ["touched.txt"] = DiffStatus.Unchanged,   // only the time differs: the hash decides
            ["edited.txt"] = DiffStatus.Changed,      // same size, other content
            ["gone.txt"] = DiffStatus.Deleted,
            ["added.txt"] = DiffStatus.Added,
        });
    }

    [Fact]
    public void Compares_by_size_and_time_when_a_hash_is_missing()
    {
        Write(_target, "2026_09_29-14_05", [File("same.txt", "same"), File("touched.txt", "same"), File("edited.txt", "abcd")], withManifest: false);
        Write(_target, "2026_09_30-14_05", [File("same.txt", "same"), File("touched.txt", "same", Later), File("edited.txt", "abce")]);
        var versions = Sync();

        var statuses = _index.Compare(versions[0].Id, versions[1].Id);

        ByPath(statuses, "same.txt", "touched.txt", "edited.txt").Should().Equal(new Dictionary<string, DiffStatus>
        {
            ["same.txt"] = DiffStatus.Unchanged,
            ["touched.txt"] = DiffStatus.Changed,
            ["edited.txt"] = DiffStatus.Unchanged,   // same size and time: without hashes it counts as unchanged
        });
    }

    [Fact]
    public void Rolls_folders_up()
    {
        Write(_target, "2026_09_29-14_05", [File("calm/a.txt", "a"), File("busy/deep/b.txt", "b"), File("busy/c.txt", "c"), File("old/d.txt", "d")]);
        Write(_target, "2026_09_30-14_05", [File("calm/a.txt", "a"), File("busy/deep/b.txt", "B!"), File("busy/c.txt", "c"), File("fresh/e.txt", "e")]);
        var versions = Sync();

        var statuses = _index.Compare(versions[0].Id, versions[1].Id);

        ByPath(statuses, "/", "calm/", "busy/", "busy/deep/", "old/", "fresh/").Should().Equal(new Dictionary<string, DiffStatus>
        {
            ["/"] = DiffStatus.Changed,
            ["calm/"] = DiffStatus.Unchanged,
            ["busy/"] = DiffStatus.Changed,
            ["busy/deep/"] = DiffStatus.Changed,
            ["old/"] = DiffStatus.Deleted,
            ["fresh/"] = DiffStatus.Added,
        });
    }

    [Fact]
    public void Compares_in_the_given_direction()
    {
        Write(_target, "2026_09_29-14_05", [File("gone.txt", "x")]);
        Write(_target, "2026_09_30-14_05", [File("added.txt", "y")]);
        var versions = Sync();

        var statuses = _index.Compare(versions[1].Id, versions[0].Id);

        ByPath(statuses, "gone.txt", "added.txt").Should().Equal(new Dictionary<string, DiffStatus>
        {
            ["gone.txt"] = DiffStatus.Added,
            ["added.txt"] = DiffStatus.Deleted,
        });
    }

    [Fact]
    public void Gives_the_history_of_a_file()
    {
        Write(_target, "2026_09_25-14_05", [File("other.txt", "o")]);
        Write(_target, "2026_09_26-14_05", [File("f.txt", "one")]);
        Write(_target, "2026_09_27-14_05", [File("f.txt", "one")]);
        Write(_target, "2026_09_28-14_05", [File("f.txt", "two", Later)]);
        Write(_target, "2026_09_29-14_05", [File("other.txt", "o")]);
        Write(_target, "2026_09_30-14_05", [File("other.txt", "o")]);
        Write(_target, "2026_10_01-14_05", [File("f.txt", "two", Later)]);
        Sync();

        var history = _index.History(Path("f.txt"));

        history.Select(h => (h.Version.Name[..10], h.Present, h.Status)).Should().Equal(
            ("2026_09_25", false, HistoryStatus.Absent),
            ("2026_09_26", true, HistoryStatus.New),
            ("2026_09_27", true, HistoryStatus.Unchanged),
            ("2026_09_28", true, HistoryStatus.Changed),
            ("2026_09_29", false, HistoryStatus.Deleted),
            ("2026_09_30", false, HistoryStatus.Absent),
            ("2026_10_01", true, HistoryStatus.Unchanged));   // against the last version that had it
        history[3].Size.Should().Be(3);
        history[3].MtimeUtc.Should().Be(Later);
        history[4].Size.Should().BeNull();
    }

    [Fact]
    public void Searches_names_by_substring_ignoring_case()
    {
        Write(_target, "2026_09_30-14_05", [File("Report.docx", "r"), File("docs/readme.md", "m"), File("docs/REPORTS/q1.xlsx", "q"), File("other.txt", "o")]);
        var version = Sync().Single();

        var hits = _index.Search(version.Id, "report");

        hits.Select(h => (h.Path, h.IsDirectory)).Should().Equal(("docs/REPORTS", true), ("Report.docx", false));
        hits[0].Size.Should().Be(1);
    }

    [Fact]
    public void Searches_names_by_wildcard_for_the_whole_name()
    {
        Write(_target, "2026_09_30-14_05", [File("a.cs", "1"), File("src/b.CS", "2"), File("src/b.csproj", "3"), File("c1.txt", "4"), File("c12.txt", "5")]);
        var version = Sync().Single();

        _index.Search(version.Id, "*.cs").Select(h => h.Path).Should().Equal("a.cs", "src/b.CS");
        _index.Search(version.Id, "c?.txt").Select(h => h.Path).Should().Equal("c1.txt");
        _index.Search(version.Id, "  ").Should().BeEmpty();
    }

    [Fact]
    public void A_wildcard_pattern_with_many_stars_over_a_long_name_stays_fast_and_correct()
    {
        var name = new string('a', 60) + ".txt";
        Write(_target, "2026_09_30-14_05", [File(name, "1"), File(new string('a', 60) + "b", "2")]);
        var version = Sync().Single();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var none = _index.Search(version.Id, "*a*a*a*a*a*a*a*a*c");
        var one = _index.Search(version.Id, "*a*a*a*a*a*a*a*a*b");
        watch.Stop();

        none.Should().BeEmpty();
        one.Select(h => h.Path).Should().Equal(new string('a', 60) + "b");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Limits_the_number_of_search_hits()
    {
        Write(_target, "2026_09_30-14_05", Enumerable.Range(0, 30).Select(i => File($"f{i:00}.txt", "x")));
        var version = Sync().Single();

        _index.Search(version.Id, "f", limit: 10).Select(h => h.Path).Should().HaveCount(10).And.StartWith("f00.txt");
        VersionIndex.SearchLimit.Should().Be(500);
    }
}
