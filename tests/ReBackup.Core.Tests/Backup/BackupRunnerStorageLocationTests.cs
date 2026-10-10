using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Json;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Backup;

/// <summary>
/// Runs whose source and target are storages that are no folders (as S3 is): the source is scanned and read, its
/// <c>.backupignore</c> honoured, through <see cref="IStorage"/> only; an empty path is the whole bucket, not a missing one.
/// </summary>
public class BackupRunnerStorageLocationTests
{
    private const string Version = "2026_09_30-16_05 Projects";
    private static readonly DateTime Mtime = new(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.Zero));
    private readonly InMemoryStorage _source = new(StorageCapabilities.None);
    private readonly InMemoryStorage _target = new(StorageCapabilities.None);

    public BackupRunnerStorageLocationTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
        _source.AddFile("a.txt", "alpha"u8.ToArray(), Mtime);
        _source.AddFile("docs/b.txt", "bravo"u8.ToArray(), Mtime);
        _source.AddFile("docs/.backupignore", "*.tmp"u8.ToArray(), Mtime);
        _source.AddFile("docs/scratch.tmp", "x"u8.ToArray(), Mtime);
    }

    private Task<RunLogEntry> Run(string sourcePath, string targetPath)
    {
        var storages = new TestStorageFactory(location => location.Kind switch
        {
            "mem-src" => _source,
            "mem-tgt" => _target,
            _ => throw new NotSupportedException(location.Kind),
        });
        var plan = new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = new StorageLocation("mem-src", sourcePath, "conn-1"),
            Target = new StorageLocation("mem-tgt", targetPath, "conn-2"),
        };
        return new BackupRunner(storages, _time, host: "TESTHOST")
            .RunAsync(new BackupRequest(plan, [], RunTrigger.Manual));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("photos", "backups/moonlabs")]
    public async Task A_storage_source_is_backed_up_through_the_storage(string sourcePath, string targetPath)
    {
        var entry = await Run(sourcePath, targetPath);

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        entry.Version.Should().Be(Version);
        _target.Files.Should().BeEquivalentTo([
            $"{Version}/a.txt", $"{Version}/docs/b.txt", $"{Version}/docs/.backupignore", $"{Version}/re-manifest.json"]);
        Encoding.UTF8.GetString(_target.ReadAllBytes($"{Version}/docs/b.txt")).Should().Be("bravo");
        var manifest = JsonSerializer.Deserialize<BackupManifest>(_target.ReadAllBytes($"{Version}/re-manifest.json"),
            JsonDefaults.Options)!;
        manifest.Source.Should().Be(sourcePath);
        manifest.Files.Select(f => f.Path).Should().BeEquivalentTo(["a.txt", "docs/.backupignore", "docs/b.txt"]);
    }

    [Fact]
    public async Task An_empty_s3_source_ends_as_error_without_a_version_or_retention()
    {
        // A mistyped or emptied prefix (or a connection that points elsewhere) scans to nothing: a "completed" empty
        // version would let retention delete the real ones, one run at a time.
        var empty = new InMemoryStorage(StorageCapabilities.None);
        const string old = "2026_09_20-02_00 Projects";
        await VersionFolder.CreateAsync(_target, old, "p1");
        var before = _target.Files.ToList();
        var plan = new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = new StorageLocation("s3", "photoz", "conn-1"),
            Target = new StorageLocation("s3", "backups", "conn-2"),
            Retention = [new RetentionRule { Period = RetentionPeriod.Daily, Keep = 1 }],
        };
        var storages = new TestStorageFactory(location => location == plan.Source ? empty : _target);

        var entry = await new BackupRunner(storages, _time, host: "TESTHOST")
            .RunAsync(new BackupRequest(plan, [], RunTrigger.Manual));

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Be(CoreTexts.English("core.run.sourceEmpty"));
        entry.Version.Should().BeNull();
        entry.RetentionDeleted.Should().BeEmpty();
        _target.Files.Should().BeEquivalentTo(before, "no version is written and none is deleted");
    }
}
