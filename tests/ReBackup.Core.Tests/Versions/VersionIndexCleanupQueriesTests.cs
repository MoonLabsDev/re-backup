using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Versions;

public class VersionIndexCleanupQueriesTests : IDisposable
{
    private static readonly DateTime Time = new(2026, 9, 30, 14, 5, 0, DateTimeKind.Utc);
    private readonly TempDir _tmp = new();
    private readonly VersionIndex _index;
    private readonly long _first;
    private readonly long _second;

    public VersionIndexCleanupQueriesTests()
    {
        _index = VersionIndex.Open(_tmp.PathOf("index.db"));
        var target = new InMemoryStorage();
        Add("2026_09_29-14_05", target,
            new ManifestFile("src/a.cs", 10, Time, "xxh64:0000000000000001"),
            new ManifestFile("target/x.dll", 100, Time, "xxh64:0000000000000002"),
            new ManifestFile("target/sub/y.dll", 50, Time, "xxh64:0000000000000003"));
        Add("2026_09_30-14_05", target, new ManifestFile("target/x.dll", 100, Time, "xxh64:0000000000000002"));
        var versions = _index.Versions();
        _first = versions.Single(v => v.Name == "2026_09_29-14_05").Id;
        _second = versions.Single(v => v.Name == "2026_09_30-14_05").Id;
    }

    public void Dispose() => _tmp.Dispose();

    private void Add(string name, InMemoryStorage target, params ManifestFile[] files) =>
        _index.Add(new VersionInfo(name, name, Time, VersionOwnership.Owned, null, null),
            new BackupManifest { Files = [.. files] }, target);

    [Fact]
    public void EntriesOf_lists_folders_with_totals_and_files_parents_first()
    {
        var entries = _index.EntriesOf(_first);

        entries.Select(e => e.Path).Should().Equal("src", "src/a.cs", "target", "target/sub", "target/sub/y.dll", "target/x.dll");
        entries.Single(e => e.Path == "target").Should().Be(new IndexEntry("target", true, 150, 2, null));
        entries.Single(e => e.Path == "target/x.dll").Should().Be(
            new IndexEntry("target/x.dll", false, 100, 1, VersionIndex.ParseHash("xxh64:0000000000000002")));
    }

    [Fact]
    public void StampOf_returns_the_recorded_stamp_and_null_for_unknown_ids()
    {
        _index.StampOf(_first).Should().StartWith("m:");
        _index.StampOf(9999).Should().BeNull();
    }

    [Fact]
    public void HashesOf_returns_the_distinct_hashes_of_the_given_versions()
    {
        _index.HashesOf([_second]).Should().BeEquivalentTo([VersionIndex.ParseHash("xxh64:0000000000000002")]);
        _index.HashesOf([_first, _second]).Should().HaveCount(3);
        _index.HashesOf([]).Should().BeEmpty();
    }
}
