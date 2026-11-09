using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Cleanup;
using ReBackup.Core.Ignore;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Cleanup;

public class CleanupScannerTests : IDisposable
{
    private static readonly DateTime Time = new(2026, 9, 30, 14, 5, 0, DateTimeKind.Utc);
    private readonly TempDir _tmp = new();
    private readonly VersionIndex _index;
    private readonly InMemoryStorage _target = new();

    public CleanupScannerTests() => _index = VersionIndex.Open(_tmp.PathOf("index.db"));

    public void Dispose() => _tmp.Dispose();

    private long Add(string name, params ManifestFile[] files)
    {
        _index.Add(new VersionInfo(name, name, Time, VersionOwnership.Owned, null, null),
            new BackupManifest { Files = [.. files] }, _target);
        return _index.Versions().Single(v => v.Name == name).Id;
    }

    private static ManifestFile File(string path, long size, int hash) =>
        new(path, size, Time, $"xxh64:{hash:x16}");

    /// <summary>src/a.cs (10), target/x.dll (100), target/sub/y.dll (50).</summary>
    private long AddFirst() => Add("2026_09_29-14_05",
        File("src/a.cs", 10, 1), File("target/x.dll", 100, 2), File("target/sub/y.dll", 50, 3));

    private static IgnoreMatcher Rules(params string[] patterns) =>
        IgnoreMatcher.ForPlan(new IgnoreSettings { UseGlobalDefaults = false, Patterns = [.. patterns] }, [], []);

    [Fact]
    public void An_ignored_folder_counts_from_its_totals()
    {
        var first = AddFirst();

        var scan = CleanupScanner.Scan(_index, [first], Rules("target/"), pool: false);

        scan.Versions.Should().ContainSingle().Which.Should().Be(
            new CleanupVersionScan(first, "2026_09_29-14_05", _index.StampOf(first)!, 2, 150));
        scan.ByRule.Should().Equal(new CleanupRuleHit("target/", IgnoreOrigins.Plan, 2, 150, "target", 150));
        scan.Files.Should().Be(2);
        scan.Bytes.Should().Be(150);
    }

    [Fact]
    public void A_file_rule_counts_each_file()
    {
        var first = AddFirst();

        var scan = CleanupScanner.Scan(_index, [first], Rules("*.dll"), pool: false);

        scan.Versions.Single().Files.Should().Be(2);
        scan.Versions.Single().Bytes.Should().Be(150);
        var hit = scan.ByRule.Should().ContainSingle().Subject;
        hit.Should().Be(new CleanupRuleHit("*.dll", IgnoreOrigins.Plan, 2, 150, "target/x.dll", 100));
    }

    [Fact]
    public void A_nested_backupignore_applies_below_its_folder()
    {
        var first = Add("2026_09_29-14_05", File("a.cs", 5, 4), File("src/a.cs", 10, 1));
        var settings = new IgnoreSettings { UseGlobalDefaults = false, HonorNestedFiles = true };
        var matcher = IgnoreMatcher.ForPlan(settings, [], [new NestedIgnoreFile("src", ["*.cs"])]);

        var scan = CleanupScanner.Scan(_index, [first], matcher, pool: false);

        scan.Versions.Single().Files.Should().Be(1);
        scan.Versions.Single().Bytes.Should().Be(10);
        scan.ByRule.Should().Equal(new CleanupRuleHit("*.cs", IgnoreOrigins.ForNestedFile("src"), 1, 10, "src/a.cs", 10));
    }

    [Fact]
    public void Global_defaults_apply_only_when_the_plan_uses_them()
    {
        var first = AddFirst();
        var off = IgnoreMatcher.ForPlan(new IgnoreSettings { UseGlobalDefaults = false }, ["target/"], []);
        var on = IgnoreMatcher.ForPlan(new IgnoreSettings { UseGlobalDefaults = true }, ["target/"], []);

        var without = CleanupScanner.Scan(_index, [first], off, pool: false);
        var with = CleanupScanner.Scan(_index, [first], on, pool: false);

        without.Files.Should().Be(0);
        without.ByRule.Should().BeEmpty();
        with.ByRule.Should().Equal(new CleanupRuleHit("target/", IgnoreOrigins.GlobalDefaults, 2, 150, "target", 150));
    }

    [Fact]
    public void Rule_hits_add_up_across_versions_and_keep_the_largest()
    {
        var first = AddFirst();
        var second = Add("2026_09_30-14_05", File("target/x.dll", 300, 5));

        var scan = CleanupScanner.Scan(_index, [second, first], Rules("*.dll"), pool: false);

        scan.Versions.Select(v => v.VersionId).Should().Equal(second, first);
        scan.ByRule.Should().Equal(new CleanupRuleHit("*.dll", IgnoreOrigins.Plan, 3, 450, "target/x.dll", 300));
    }

    [Fact]
    public void By_rule_is_sorted_by_bytes_descending()
    {
        var first = AddFirst();

        var scan = CleanupScanner.Scan(_index, [first], Rules("*.cs", "target/"), pool: false);

        scan.ByRule.Select(h => h.Rule).Should().Equal("target/", "*.cs");
        scan.Bytes.Should().Be(160);
    }

    [Fact]
    public void A_folder_sorting_between_a_parent_and_its_child_does_not_break_the_walk()
    {
        // "target-b" sorts between "target" and "target/x.dll" (NOCASE: '-' < '/').
        var first = Add("2026_09_29-14_05", File("target-b/k.txt", 7, 6), File("target/x.dll", 100, 2));

        var scan = CleanupScanner.Scan(_index, [first], Rules("target/"), pool: false);

        scan.Versions.Single().Files.Should().Be(1);
        scan.Versions.Single().Bytes.Should().Be(100);
    }

    [Fact]
    public void Pool_estimate_skips_content_an_unchecked_version_still_uses()
    {
        var first = AddFirst();
        Add("2026_09_30-14_05", File("target/x.dll", 100, 2));

        var scan = CleanupScanner.Scan(_index, [first], Rules("target/"), pool: true);

        scan.PoolBytesFreed.Should().Be(50);
    }

    [Fact]
    public void Pool_estimate_skips_content_a_checked_version_keeps()
    {
        var first = Add("2026_09_29-14_05", File("target/x.dll", 100, 2), File("lib/x.dll", 100, 2));

        var scan = CleanupScanner.Scan(_index, [first], Rules("target/"), pool: true);

        scan.Bytes.Should().Be(100);
        scan.PoolBytesFreed.Should().Be(0);
    }

    [Fact]
    public void Duplicates_inside_a_version_count_once_in_the_pool()
    {
        var first = Add("2026_09_29-14_05", File("target/a.bin", 100, 7), File("target/b.bin", 100, 7));

        var scan = CleanupScanner.Scan(_index, [first], Rules("target/"), pool: true);

        scan.Bytes.Should().Be(200);
        scan.PoolBytesFreed.Should().Be(100);
    }

    [Fact]
    public void Full_plans_have_no_pool_estimate()
    {
        var first = AddFirst();

        var scan = CleanupScanner.Scan(_index, [first], Rules("target/"), pool: false);

        scan.PoolBytesFreed.Should().BeNull();
    }
}
