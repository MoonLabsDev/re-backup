using FluentAssertions;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using static ReBackup.Core.Tests.TestSupport.VersionBuilder;

namespace ReBackup.Core.Tests.Versions;

public class VersionIndexSetTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _target;
    private readonly VersionIndexSet _set;

    public VersionIndexSetTests()
    {
        _target = _tmp.CreateDir("target");
        _set = new VersionIndexSet(_tmp.PathOf("index"));
    }

    public void Dispose() => _tmp.Dispose();

    /// <summary>Opens the plan's index in the set and fills it, so that the database and its WAL files exist.</summary>
    private void Populate(string planId)
    {
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        var index = _set.For(planId);
        index.Sync(ListAsync(_target).GetAwaiter().GetResult(), TargetStorage(_target));
        index.Versions().Should().NotBeEmpty();
    }

    [Fact]
    public void Delete_removes_database_files()
    {
        Populate("plan1");
        System.IO.File.WriteAllText(_set.PathFor("plan1") + "-wal", "");   // as if a connection left them behind
        System.IO.File.WriteAllText(_set.PathFor("plan1") + "-shm", "");

        _set.Delete("plan1");

        Directory.GetFiles(_set.Directory).Should().BeEmpty();
        _set.For("plan1").Versions().Should().BeEmpty("the index is created anew on next use");
        _set.Delete("never-existed");   // a missing database is fine
    }

    [Fact]
    public void PruneOrphans_keeps_known_plans()
    {
        Populate("keep");
        _set.For("gone1").Versions();
        _set.For("gone2").Versions();

        var pruned = _set.PruneOrphans(new HashSet<string> { "KEEP" });

        pruned.Should().Be(2);
        Directory.GetFiles(_set.Directory, "*.db").Select(Path.GetFileName).Should().Equal("keep.db");
        _set.For("keep").Versions().Should().ContainSingle();
    }
}
