using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;
using ReBackup.Storage.S3.Tests.Integration;
using static ReBackup.Core.Tests.Integration.S3Integration;

namespace ReBackup.Core.Tests.Integration;

/// <summary>
/// A folder backed up to S3 (LocalStack) on several days: the versions on S3 have the local format, retention deletes the right
/// ones there, the version index is built from the manifests on S3, and a version restored to a folder equals the source.
/// </summary>
[Collection(S3ServerCollection.Name)]
public sealed class FolderToS3Tests(S3ServerFixture server) : IDisposable
{
    private const string PlanId = "p-s3";
    private const string PlanName = "Projects";
    private const string Day1 = "2026_10_01-12_00 Projects";
    private const string Day2 = "2026_10_02-12_00 Projects";
    private const string Day3 = "2026_10_03-12_00 Projects";

    private readonly TempDir _tmp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _tmp.Dispose();

    private string WriteSource(string relative, byte[] data, DateTime mtime)
    {
        var path = _tmp.PathOf(Path.Combine("source", relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        File.SetLastWriteTimeUtc(path, mtime);
        return path;
    }

    [SkippableFact]
    public async Task Runs_on_three_days_keep_two_versions_index_them_and_restore_one_exactly()
    {
        Skip.IfNot(server.Available, server.SkipReason);
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var prefix = NewPrefix("f2s");
        var source = _tmp.CreateDir("source");
        Directory.CreateDirectory(Path.Combine(source, "empty"));

        // Day 1: small files, an empty folder and one file above the 16 MiB part size (a multipart upload).
        var mtimeA1 = new DateTime(2024, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc);
        var mtimeB = new DateTime(2023, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var mtimeBig = new DateTime(2025, 6, 7, 8, 9, 10, 456, DateTimeKind.Utc);
        var big = RandomData(20 * MiB, seed: 1);
        WriteSource("a.txt", "alpha"u8.ToArray(), mtimeA1);
        WriteSource(Path.Combine("sub", "b.txt"), "bravo"u8.ToArray(), mtimeB);
        WriteSource("big.bin", big, mtimeBig);

        var plan = new BackupPlan
        {
            Id = PlanId,
            Name = PlanName,
            Source = StorageLocation.FileSystem(source),
            Target = Location(prefix),
            Retention = [new RetentionRule { Period = RetentionPeriod.Daily, Keep = 2 }],
        };
        var runner = new BackupRunner(Storages(server), _time);
        Task<RunLogEntry> Run() => runner.RunAsync(new BackupRequest(plan, [], RunTrigger.Manual));

        var first = await Run();
        first.Status.Should().Be(RunStatus.Completed, first.Reason);
        first.Version.Should().Be(Day1);
        (await KeysAsync(server, prefix)).Should().Equal(
            $"{Day1}/a.txt", $"{Day1}/big.bin", $"{Day1}/re-manifest.json", $"{Day1}/sub/b.txt");
        (await OpenUploadsAsync(server, prefix)).Should().BeEmpty();

        // Day 2: a.txt changed.
        _time.Advance(TimeSpan.FromDays(1));
        var mtimeA2 = new DateTime(2026, 10, 2, 9, 30, 0, 789, DateTimeKind.Utc);
        WriteSource("a.txt", "alpha, second edition"u8.ToArray(), mtimeA2);
        var second = await Run();
        second.Status.Should().Be(RunStatus.Completed, second.Reason);
        second.Version.Should().Be(Day2);

        var target = server.CreateStorage(prefix);
        var index = VersionIndex.Open(_tmp.PathOf(Path.Combine("index", PlanId + ".db")));
        var synced = index.Sync(await VersionCatalog.ListAsync(target, PlanId, PlanName), target);
        synced.Should().BeEquivalentTo(new IndexSyncResult(Imported: 2, Unchanged: 0, Removed: 0, Errors: []), "both versions are read from S3");

        // Day 3: big.bin removed, c.txt added; the daily rule keeps two days, so day 1 goes.
        _time.Advance(TimeSpan.FromDays(1));
        File.Delete(Path.Combine(source, "big.bin"));
        WriteSource("c.txt", "charlie"u8.ToArray(), mtimeB);
        var third = await Run();
        third.Status.Should().Be(RunStatus.Completed, third.Reason);
        third.Version.Should().Be(Day3);
        third.Warnings.Should().BeEmpty();

        (await KeysAsync(server, prefix)).Should().Equal(
            $"{Day2}/a.txt", $"{Day2}/big.bin", $"{Day2}/re-manifest.json", $"{Day2}/sub/b.txt",
            $"{Day3}/a.txt", $"{Day3}/c.txt", $"{Day3}/re-manifest.json", $"{Day3}/sub/b.txt");
        var versions = await VersionCatalog.ListAsync(target, PlanId, PlanName);
        versions.Select(v => (v.Name, v.Ownership)).Should().Equal((Day2, VersionOwnership.Owned), (Day3, VersionOwnership.Owned));

        // The index follows: day 1 removed, day 3 imported, day 2 unchanged; every version comes from its manifest on S3.
        index.Sync(versions, target).Should().BeEquivalentTo(new IndexSyncResult(Imported: 1, Unchanged: 1, Removed: 1, Errors: []));
        var indexed = index.Versions();
        indexed.Select(v => (v.Name, v.Origin, v.Ownership, v.FileCount, v.TotalBytes)).Should().Equal(
            (Day2, IndexOrigin.Manifest, VersionOwnership.Owned, 3, 21L + 5 + big.Length),
            (Day3, IndexOrigin.Manifest, VersionOwnership.Owned, 3, 21L + 5 + 7));
        indexed.Should().OnlyContain(v => v.Source == source);
        var bigPath = index.FindPath("big.bin", isDirectory: false);
        bigPath.Should().NotBeNull();
        index.History(bigPath!.Value).Where(h => h.Present).Select(h => (h.Version.Name, h.Size, h.MtimeUtc))
            .Should().Equal((Day2, (long?)big.Length, (DateTime?)mtimeBig));

        // Day 2 restored to a folder: bytes, modified times and the empty folder as the source had them that day.
        var destination = _tmp.CreateDir("restored");
        var restorePlan = await Restorer.PlanAsync(target, Day2, [""], new FileSystemStorage(destination), RestoreMode.ToFolder);
        restorePlan.Conflicts.Should().BeEmpty();
        var restored = await Restorer.RunAsync(restorePlan, ConflictPolicy.Skip);

        restored.Should().BeEquivalentTo(new { Copied = 3, Skipped = 0, KeptBoth = 0, Canceled = false });
        restored.Failures.Should().BeEmpty();
        var root = Path.Combine(destination, Day2);
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).Order()
            .Should().Equal("a.txt", "big.bin", Path.Combine("sub", "b.txt"));
        Directory.Exists(Path.Combine(root, "empty")).Should().BeTrue("the manifest knows the empty folder S3 cannot hold");
        File.ReadAllBytes(Path.Combine(root, "a.txt")).Should().Equal("alpha, second edition"u8.ToArray());
        File.ReadAllBytes(Path.Combine(root, "sub", "b.txt")).Should().Equal("bravo"u8.ToArray());
        File.ReadAllBytes(Path.Combine(root, "big.bin")).SequenceEqual(big).Should().BeTrue("the multipart object is restored byte for byte");
        File.GetLastWriteTimeUtc(Path.Combine(root, "a.txt")).Should().Be(mtimeA2);
        File.GetLastWriteTimeUtc(Path.Combine(root, "sub", "b.txt")).Should().Be(mtimeB);
        File.GetLastWriteTimeUtc(Path.Combine(root, "big.bin")).Should().Be(mtimeBig);
    }
}
