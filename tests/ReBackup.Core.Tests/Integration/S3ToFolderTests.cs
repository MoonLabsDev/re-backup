using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using ReBackup.Shared.Json;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;
using ReBackup.Storage.S3.Tests.Integration;
using static ReBackup.Core.Tests.Integration.S3Integration;

namespace ReBackup.Core.Tests.Integration;

/// <summary>
/// Objects on S3 (LocalStack) backed up to a folder, and the version restored into the bucket where they came from: the restore
/// never deletes there, and skip, keep both and overwrite treat the objects that exist as they do on a folder.
/// </summary>
[Collection(S3ServerCollection.Name)]
public sealed class S3ToFolderTests(S3ServerFixture server) : IDisposable
{
    private const string Version = "2026_10_05-08_30 Photos";

    private readonly TempDir _tmp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 8, 30, 0, TimeSpan.Zero));

    public void Dispose() => _tmp.Dispose();

    [SkippableFact]
    public async Task Bucket_prefix_is_backed_up_to_a_folder_and_restored_into_the_bucket_without_deleting()
    {
        Skip.IfNot(server.Available, server.SkipReason);
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var prefix = NewPrefix("s2f");
        var bucket = server.CreateStorage(prefix);
        var files = new Dictionary<string, byte[]>
        {
            ["notes.txt"] = "notes"u8.ToArray(),
            ["photos/a.jpg"] = RandomData(300_000, seed: 2),
            ["photos/2026/deep/b.raw"] = RandomData(70_000, seed: 3),
            ["photos/2026/c.txt"] = "charlie"u8.ToArray(),
        };
        foreach (var (path, data) in files)
            await PutAsync(bucket, path, data);
        var objectTimes = new Dictionary<string, DateTime>();
        foreach (var path in files.Keys)
            objectTimes[path] = (await bucket.StatAsync(path, CancellationToken.None))!.ModifiedUtc;

        var target = _tmp.CreateDir("target");
        var plan = new BackupPlan { Id = "p-photos", Name = "Photos", Source = Location(prefix), Target = StorageLocation.FileSystem(target) };
        var entry = await new BackupRunner(Storages(server), _time).RunAsync(new BackupRequest(plan, [], RunTrigger.Manual));

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        entry.Version.Should().Be(Version);
        var root = Path.Combine(target, Version);
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal)
            .Should().Equal(files.Keys.Append("re-manifest.json").Order(StringComparer.Ordinal));
        foreach (var (path, data) in files)
        {
            var copy = Path.Combine(root, path);
            File.ReadAllBytes(copy).SequenceEqual(data).Should().BeTrue(path);
            File.GetLastWriteTimeUtc(copy).Should().Be(objectTimes[path], "the copy gets the object's modified time ({0})", path);
        }

        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllBytes(Path.Combine(root, "re-manifest.json")),
            JsonDefaults.Options)!;
        manifest.Should().BeEquivalentTo(new { PlanId = "p-photos", PlanName = "Photos", Source = prefix, FileCount = 4,
            TotalBytes = files.Values.Sum(d => (long)d.Length) });
        manifest.Files.Select(f => (f.Path, f.Size, f.MtimeUtc)).Should().Equal(
            files.Keys.Order(StringComparer.Ordinal).Select(p => (p, (long)files[p].Length, objectTimes[p])),
            "the manifest lists every object in a fixed order with its size and time");
        manifest.Files.Should().OnlyContain(f => !string.IsNullOrEmpty(f.Hash));

        // Meanwhile on S3: one object changed, one deleted, one added.
        await PutAsync(bucket, "notes.txt", "notes, edited"u8.ToArray());
        await bucket.DeleteAsync(["photos/2026/deep/b.raw"], CancellationToken.None);
        await PutAsync(bucket, "photos/new.jpg", "new"u8.ToArray());
        var versions = new FileSystemStorage(target);

        // Skip: only the deleted object comes back; the changed one stays as it is, the added one is not touched.
        var skipPlan = await Restorer.PlanAsync(versions, Version, [""], bucket, RestoreMode.Original);
        skipPlan.Conflicts.Select(c => c.Destination).Order(StringComparer.Ordinal)
            .Should().Equal("notes.txt", "photos/2026/c.txt", "photos/a.jpg");
        var skipped = await Restorer.RunAsync(skipPlan, ConflictPolicy.Skip);
        skipped.Should().BeEquivalentTo(new { Copied = 1, Skipped = 3, KeptBoth = 0, Canceled = false });
        skipped.Failures.Should().BeEmpty();
        (await ReadAsync(bucket, "photos/2026/deep/b.raw")).SequenceEqual(files["photos/2026/deep/b.raw"]).Should().BeTrue();
        (await ReadAsync(bucket, "notes.txt")).Should().Equal("notes, edited"u8.ToArray());

        // Keep both: the version's copies go next to the objects, under new names.
        var keepPlan = await Restorer.PlanAsync(versions, Version, ["notes.txt"], bucket, RestoreMode.Original);
        keepPlan.Conflicts.Should().ContainSingle();
        var kept = await Restorer.RunAsync(keepPlan, ConflictPolicy.KeepBoth);
        kept.Should().BeEquivalentTo(new { Copied = 0, Skipped = 0, KeptBoth = 1, Canceled = false });
        var keptName = Restorer.KeepBothName("notes.txt", keepPlan.VersionTime, 1);
        (await ReadAsync(bucket, keptName)).Should().Equal("notes"u8.ToArray());
        (await ReadAsync(bucket, "notes.txt")).Should().Equal("notes, edited"u8.ToArray());

        // Overwrite: the changed object gets the version's content back.
        var overwritePlan = await Restorer.PlanAsync(versions, Version, ["notes.txt"], bucket, RestoreMode.Original);
        var overwritten = await Restorer.RunAsync(overwritePlan, ConflictPolicy.Overwrite);
        overwritten.Should().BeEquivalentTo(new { Copied = 1, Skipped = 0, KeptBoth = 0, Canceled = false });
        (await ReadAsync(bucket, "notes.txt")).Should().Equal("notes"u8.ToArray());

        // Nothing was ever deleted: the added object and the keep-both copy are still there.
        (await KeysAsync(server, prefix)).Should().Equal(
            new[] { keptName }.Concat(files.Keys).Append("photos/new.jpg").Order(StringComparer.Ordinal));
        (await ReadAsync(bucket, "photos/new.jpg")).Should().Equal("new"u8.ToArray());
    }
}
