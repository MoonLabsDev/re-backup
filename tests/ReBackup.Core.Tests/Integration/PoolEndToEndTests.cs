using System.IO.Hashing;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.Core.Tests.Integration;

/// <summary>
/// An incremental plan on real folders: three runs with changes between them, retention removing the middle version and
/// the collector its content, a thorough verification, byte-exact restores of the oldest and newest version, and a copy
/// to a full target that restores byte-exact too.
/// </summary>
public sealed class PoolEndToEndTests : IDisposable
{
    private const string PlanId = "p-e2e";
    private const string PlanName = "Projects";
    private const string Host = "TESTHOST";

    private readonly TempDir _tmp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));

    public PoolEndToEndTests() => _time.SetLocalTimeZone(TimeZoneInfo.Utc);

    public void Dispose() => _tmp.Dispose();

    private static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new System.Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private void WriteSource(string relative, byte[] data)
    {
        var path = _tmp.PathOf(Path.Combine("source", relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        File.SetLastWriteTimeUtc(path, new DateTime(2025, 5, 6, 7, 8, 9, DateTimeKind.Utc).AddSeconds(data.Length));
    }

    private static Dictionary<string, byte[]> Tree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);

    private static async Task<Dictionary<string, byte[]>> RestoreAsync(IStorage target, string versionName, string folder)
    {
        Directory.CreateDirectory(folder);
        var destination = new StorageFactory().Open(StorageLocation.FileSystem(folder));
        var versions = await VersionViews.OpenAsync(target, versionName, CancellationToken.None);
        var plan = await Restorer.PlanAsync(versions, versionName, [""], destination, RestoreMode.Original);
        var result = await Restorer.RunAsync(plan, ConflictPolicy.Overwrite);
        result.Failures.Should().BeEmpty();
        return Tree(folder);
    }

    private static void ShouldEqualTree(Dictionary<string, byte[]> actual, Dictionary<string, byte[]> expected)
    {
        actual.Keys.Should().BeEquivalentTo(expected.Keys);
        foreach (var (path, data) in expected)
            actual[path].Should().Equal(data, path);
    }

    [Fact]
    public async Task Pool_plan_on_file_system_round_trip()
    {
        var sourceDir = _tmp.CreateDir("source");
        var targetDir = _tmp.PathOf("target");
        var plan = new BackupPlan
        {
            Id = PlanId,
            Name = PlanName,
            Incremental = true,
            Source = StorageLocation.FileSystem(sourceDir),
            Target = StorageLocation.FileSystem(targetDir),
            Retention =
            [
                new RetentionRule { Period = RetentionPeriod.Daily, Keep = 1 },
                new RetentionRule { Period = RetentionPeriod.Monthly, Anchor = "0", Keep = 2 },
            ],
        };
        var storages = new StorageFactory();
        var runner = new BackupRunner(storages, _time, host: Host);
        async Task<RunLogEntry> Run()
        {
            var entry = await runner.RunAsync(new BackupRequest(plan, [], RunTrigger.Manual));
            entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
            entry.Warnings.Should().BeEmpty();
            return entry;
        }

        // Run 1 (Sep 30): files in folders, an empty file and an empty folder.
        WriteSource("a.txt", Utf8("alpha v1"));
        WriteSource("big.bin", Random(3 * 1024 * 1024 + 17, seed: 1));
        WriteSource(Path.Combine("sub", "deep", "b.txt"), Utf8("bravo"));
        WriteSource("empty.txt", []);
        Directory.CreateDirectory(Path.Combine(sourceDir, "emptydir"));
        var firstTree = Tree(sourceDir);
        var first = await Run();

        // Run 2 (Oct 1): a.txt changes, a file is added.
        _time.Advance(TimeSpan.FromDays(1));
        WriteSource("a.txt", Utf8("alpha version two, longer"));
        WriteSource("added.bin", Random(1000, seed: 2));
        var second = await Run();
        second.RetentionDeleted.Should().BeEmpty();

        // Run 3 (Oct 2): b.txt changes, big.bin is removed; retention drops the middle version.
        _time.Advance(TimeSpan.FromDays(1));
        WriteSource(Path.Combine("sub", "deep", "b.txt"), Utf8("bravo three"));
        File.Delete(Path.Combine(sourceDir, "big.bin"));
        var lastTree = Tree(sourceDir);
        var third = await Run();
        third.RetentionDeleted.Should().Equal(second.Version);

        var target = storages.Open(plan.Target);
        var versions = await VersionCatalog.ListAsync(target, PlanId, PlanName);
        versions.Select(v => v.Name).Should().Equal(first.Version, third.Version);
        Directory.Exists(Path.Combine(targetDir, second.Version!)).Should().BeFalse();
        File.Exists(Path.Combine(targetDir, PoolLayout.StoreFile)).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, PoolLayout.LockFile)).Should().BeFalse();

        // GC: the content only the removed middle version had is gone; what the two left need remains.
        var blobs = Directory.EnumerateFiles(Path.Combine(targetDir, PoolLayout.PoolFolder), "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).ToList();
        var needed = firstTree.Values.Concat(lastTree.Values)
            .Select(data => PoolLayout.Format(XxHash128.HashToUInt128(data))[^32..]).Distinct().ToList();
        blobs.Should().BeEquivalentTo(needed);

        // Verify, thorough.
        await using (var held = await TargetLock.TryAcquireAsync(target, Host, "verify", _time, CancellationToken.None))
        {
            held.Should().NotBeNull();
            var problems = await PoolVerifier.VerifyAsync(target, PlanId, PlanName, thorough: true, progress: null,
                CancellationToken.None);
            problems.Should().BeEmpty();
        }

        // Restore the newest and the oldest version byte-exact.
        ShouldEqualTree(await RestoreAsync(target, third.Version!, _tmp.PathOf("restore-last")), lastTree);
        ShouldEqualTree(await RestoreAsync(target, first.Version!, _tmp.PathOf("restore-first")), firstTree);
        Directory.Exists(_tmp.PathOf(Path.Combine("restore-first", "emptydir"))).Should().BeTrue();

        // Copy to a full target and restore from there.
        var copyDir = _tmp.PathOf("copy-target");
        var copyLocation = StorageLocation.FileSystem(copyDir);
        var copier = new PlanCopier(storages, Host, _time);
        (await copier.CheckAsync(plan, copyLocation, CancellationToken.None)).Should().Be(CopyRefusal.None);
        var copy = PlanCopier.MakeCopy(plan, PlanName, copyLocation, incremental: false);
        var copied = await copier.CopyAsync(plan, copy, null, CancellationToken.None);
        copied.Failures.Should().BeEmpty();
        copied.Canceled.Should().BeFalse();
        copied.VersionsCopied.Should().Be(2);

        var copyStorage = storages.Open(copyLocation);
        var copyVersions = await VersionCatalog.ListAsync(copyStorage, copy.Id, PlanName);
        copyVersions.Should().HaveCount(2);
        File.Exists(Path.Combine(copyDir, PoolLayout.StoreFile)).Should().BeFalse();
        ShouldEqualTree(await RestoreAsync(copyStorage, copyVersions[0].Name, _tmp.PathOf("restore-copy-first")), firstTree);
        ShouldEqualTree(await RestoreAsync(copyStorage, copyVersions[1].Name, _tmp.PathOf("restore-copy-last")), lastTree);
    }
}
