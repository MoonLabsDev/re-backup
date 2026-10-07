using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;
using static ReBackup.Core.Tests.TestSupport.VersionBuilder;

namespace ReBackup.Core.Tests.Backup;

/// <summary>
/// The manual end-to-end check of the storage refactoring, on temp folders: a target as ReBackup 1.0.5 left it, a run,
/// a canceled run, a restore and a manual deletion.
/// </summary>
public class EndToEndTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly string _source;
    private readonly string _target;

    public EndToEndTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _source = _tmp.CreateDir("source");
        _target = _tmp.CreateDir("target");
    }

    public void Dispose() => _tmp.Dispose();

    private BackupPlan Plan() => new()
    {
        Id = PlanId,
        Name = PlanName,
        Source = StorageLocation.FileSystem(_source),
        Target = StorageLocation.FileSystem(_target),
    };

    private static BackupRequest Request(BackupPlan plan) => new(plan, [], RunTrigger.Manual);

    private string[] TargetEntries() =>
        Directory.GetFileSystemEntries(_target).Select(e => Path.GetFileName(e)!).Order().ToArray();

    [Fact]
    public async Task A_1_0_5_target_is_cleaned_run_canceled_restored_and_a_version_deleted()
    {
        var mtime = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var a = _tmp.WriteFile(@"source\a.txt", "alpha");
        System.IO.File.SetLastWriteTimeUtc(a, mtime);
        _tmp.WriteFile(@"source\sub\b.txt", "bravo");

        // 1. A target as 1.0.5 left it: an owned version (format 1 manifest), an unfinished .partial and remains of a deletion.
        var old = Path.GetFileName(Write(_target, "2026_09_01-10_00", [File("old.txt", "old")], formatVersion: 1));
        _tmp.WriteFile(@"target\2026_09_02-10_00 Projects.partial\x.txt", "stale");
        VersionFolder.Create(_target, "2026_09_03-10_00 Projects.deleting", PlanId);

        var storages = new StorageFactory();
        (await ListAsync(_target)).Should().ContainSingle(v => v.Name == old).Which.IsOwned.Should().BeTrue();

        var runner = new BackupRunner(storages, _time);
        var entry = await runner.RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Completed);
        var created = entry.Version!;
        TargetEntries().Should().Equal(new[] { old, created }.Order(), "the leftovers are gone and the new version is there");
        Directory.GetFiles(Path.Combine(_target, created), "re-pending.json", SearchOption.AllDirectories).Should().BeEmpty();

        // 2. A canceled run leaves no new folder.
        _time.Advance(TimeSpan.FromMinutes(1));
        using var cts = new CancellationTokenSource();
        var canceling = new BackupRunner(new TestStorageFactory(location =>
        {
            var storage = storages.Open(location);
            return location.Path != _target ? storage : new FaultyStorage(storage)
            {
                Before = (operation, path) =>
                {
                    if (operation == "commit" && path.EndsWith("/a.txt", StringComparison.Ordinal)) cts.Cancel();
                },
            };
        }), _time);
        var canceled = await canceling.RunAsync(Request(Plan()), cancellationToken: cts.Token);

        canceled.Status.Should().Be(RunStatus.Canceled);
        TargetEntries().Should().Equal(new[] { old, created }.Order());

        // 3. Restoring one file brings back its content and modified time.
        var destination = _tmp.CreateDir("restored");
        var versions = new FileSystemStorage(_target);
        var plan = await Restorer.PlanAsync(versions, created, ["a.txt"], new FileSystemStorage(destination), RestoreMode.ToFolder);
        var restored = await Restorer.RunAsync(plan, ConflictPolicy.Overwrite);

        restored.Copied.Should().Be(1);
        var file = Path.Combine(destination, "a.txt");
        System.IO.File.ReadAllText(file).Should().Be("alpha");
        System.IO.File.GetLastWriteTimeUtc(file).Should().Be(mtime);

        // 4. Deleting a version by hand removes its folder.
        var results = await VersionDeleter.DeleteAsync(versions, PlanId, PlanName, [created]);

        results.Should().ContainSingle().Which.Outcome.Should().Be(VersionDeletionOutcome.Deleted);
        TargetEntries().Should().Equal(old);
    }
}
