using System.Text;
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;
using static ReBackup.Core.Tests.TestSupport.VersionBuilder;

namespace ReBackup.Core.Tests.Versions;

/// <summary>
/// A restore into a storage that is no folder, as an S3 source is when a version goes back to its original location
/// (Review Focus 5): written through the storage, nothing deleted, conflicts handled as asked, modification times
/// handed over only to a storage that can set them.
/// </summary>
public class RestorerStorageDestinationTests
{
    private const string Version = "2026_09_30-14_05 Projects";
    private static readonly DateTime Old = new(2025, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    private static readonly DateTime Existing = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private readonly InMemoryStorage _versions = new(StorageCapabilities.None);
    private readonly List<string> _deletes = [];

    public RestorerStorageDestinationTests()
    {
        _versions.AddFile($"{Version}/a.txt", Encoding.UTF8.GetBytes("alpha"), DateTime.UtcNow);
        _versions.AddFile($"{Version}/docs/b.txt", Encoding.UTF8.GetBytes("bravo"), DateTime.UtcNow);
        _versions.AddFile($"{Version}/{VersionName.ManifestFileName}", Encoding.UTF8.GetBytes(Serialize(new BackupManifest
        {
            PlanId = PlanId, PlanName = PlanName, CreatedUtc = Mtime, Source = "photos",
            Files = [new ManifestFile("a.txt", 5, Old, ""), new ManifestFile("docs/b.txt", 5, Mtime, "")],
        })));
    }

    /// <summary>An S3-like destination (no folders, no modified times) that already holds a.txt and an unrelated file.</summary>
    private (InMemoryStorage Bucket, FaultyStorage Destination) S3Like(StorageCapabilities capabilities = StorageCapabilities.None)
    {
        var bucket = new InMemoryStorage(capabilities);
        bucket.AddFile("a.txt", Encoding.UTF8.GetBytes("current"), Existing);
        bucket.AddFile("other.txt", Encoding.UTF8.GetBytes("untouched"), Existing);
        return (bucket, new FaultyStorage(bucket) { Before = (operation, path) => { if (operation == "delete") _deletes.Add(path); } });
    }

    private async Task<(RestorePlan Plan, RestoreResult Result)> RestoreAsync(IStorage destination, ConflictPolicy policy)
    {
        var plan = await Restorer.PlanAsync(_versions, Version, [""], destination, RestoreMode.Original);
        return (plan, await Restorer.RunAsync(plan, policy));
    }

    [Fact]
    public async Task Overwrite_writes_into_the_storage_deletes_nothing_and_does_not_attempt_modified_times()
    {
        var (bucket, destination) = S3Like();

        var (plan, result) = await RestoreAsync(destination, ConflictPolicy.Overwrite);

        plan.Conflicts.Select(c => c.Destination).Should().Equal("a.txt");
        result.Should().BeEquivalentTo(new RestoreResult(2, 0, 0, [], Canceled: false));
        Encoding.UTF8.GetString(bucket.ReadAllBytes("a.txt")).Should().Be("alpha");
        Encoding.UTF8.GetString(bucket.ReadAllBytes("docs/b.txt")).Should().Be("bravo");
        Encoding.UTF8.GetString(bucket.ReadAllBytes("other.txt")).Should().Be("untouched");
        _deletes.Should().BeEmpty("a restore never deletes");
        destination.Creates.Should().HaveCount(2).And.OnlyContain(c => c.Options.ModifiedUtc == null,
            "the storage cannot set modified times");
        destination.Creates.Should().OnlyContain(c => c.Options.Overwrite);
    }

    [Fact]
    public async Task Skip_leaves_the_existing_file()
    {
        var (bucket, destination) = S3Like();

        var (_, result) = await RestoreAsync(destination, ConflictPolicy.Skip);

        result.Should().BeEquivalentTo(new RestoreResult(1, 1, 0, [], Canceled: false));
        Encoding.UTF8.GetString(bucket.ReadAllBytes("a.txt")).Should().Be("current");
        bucket.Files.Should().BeEquivalentTo(["a.txt", "docs/b.txt", "other.txt"]);
        _deletes.Should().BeEmpty();
        destination.Creates.Should().OnlyContain(c => !c.Options.Overwrite && c.Options.ModifiedUtc == null);
    }

    [Fact]
    public async Task Keep_both_writes_the_version_under_a_new_name()
    {
        var (bucket, destination) = S3Like();

        var (_, result) = await RestoreAsync(destination, ConflictPolicy.KeepBoth);

        result.Should().BeEquivalentTo(new RestoreResult(1, 0, 1, [], Canceled: false));
        Encoding.UTF8.GetString(bucket.ReadAllBytes("a.txt")).Should().Be("current");
        Encoding.UTF8.GetString(bucket.ReadAllBytes("a (2026_09_30-14_05).txt")).Should().Be("alpha");
        _deletes.Should().BeEmpty();
        destination.Creates.Should().OnlyContain(c => c.Options.ModifiedUtc == null);
    }

    [Fact]
    public async Task A_storage_that_sets_modified_times_gets_those_of_the_manifest()
    {
        var (bucket, destination) = S3Like(StorageCapabilities.SetModifiedTime);

        await RestoreAsync(destination, ConflictPolicy.Overwrite);

        destination.Creates.Select(c => c.Options.ModifiedUtc).Should().BeEquivalentTo(new DateTime?[] { Old, Mtime });
        (await bucket.StatAsync("a.txt", CancellationToken.None))!.ModifiedUtc.Should().Be(Old);
    }
}
