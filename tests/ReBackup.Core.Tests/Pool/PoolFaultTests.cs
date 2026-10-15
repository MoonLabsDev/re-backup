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
using ReBackup.Storage.InMemory;
using Xunit.Sdk;
using static ReBackup.Core.Tests.TestSupport.PoolVersions;

namespace ReBackup.Core.Tests.Pool;

/// <summary>
/// Fault injection (spec 13): a run of an incremental plan that frees space, writes its version, applies retention and
/// collects garbage fails at its N-th write or delete, for every N. Whatever happens, every version the catalog lists
/// afterwards restores byte-exact, and the next run (after a person released a lock left behind) cleans up and collects
/// what is left over.
/// </summary>
public class PoolFaultTests
{
    private const string PlanId = "p1";
    private const string PlanName = "Projects";
    private const int MaxSteps = 1000;

    private static readonly string Shared = new('s', 100);

    /// <summary>The content of a.txt on day <paramref name="day"/>: 1000 bytes no other day has.</summary>
    private static string DayContent(int day) => new((char)('0' + day), 1000);

    /// <summary>The plan; with <paramref name="retention"/> it keeps two days and frees space by retention.</summary>
    private static BackupPlan Plan(bool retention = true) => new()
    {
        Id = PlanId,
        Name = PlanName,
        Incremental = true,
        FreeSpaceByRetention = retention,
        Source = StorageLocation.FileSystem(Path.GetTempPath()),
        Target = StorageLocation.FileSystem(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "rebackup-fault-test-target")),
        Retention = retention ? [new RetentionRule { Period = RetentionPeriod.Daily, Keep = 2 }] : [],
    };

    /// <summary>One target with three versions in it, and the files each version must restore to.</summary>
    private sealed class Scenario
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 15, 8, 0, 30, TimeSpan.Zero));
        public InMemoryStorage Source { get; } = new();
        public InMemoryStorage Target { get; } = new();
        public Dictionary<string, Dictionary<string, string>> Expected { get; } = new(StringComparer.Ordinal);

        public Scenario()
        {
            Time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
        }

        public long Used() => Target.Files.Sum(path => (long)Target.ReadAllBytes(path).Length);

        /// <summary>Sets the source to day <paramref name="day"/>'s files.</summary>
        public Dictionary<string, string> SourceOfDay(int day)
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["a.txt"] = DayContent(day),
                ["shared.txt"] = Shared,
                [$"day{day}.txt"] = $"only on day {day}",
            };
            foreach (var path in Source.Files.ToList())
                Source.DeleteAsync([path], CancellationToken.None).GetAwaiter().GetResult();
            foreach (var (path, content) in files)
                Source.AddFile(path, Bytes(content), PoolVersions.Mtime.AddDays(day));
            return files;
        }

        public Task<RunLogEntry> RunAsync(IStorage target, bool retention = true) =>
            new BackupRunner(new TestStorageFactory(location => location == Plan().Source ? Source : target), Time,
                    host: "TESTHOST")
                .RunAsync(new BackupRequest(Plan(retention), [], RunTrigger.Manual));

        /// <summary>A run without retention that must succeed; its version is recorded with the files it must restore to.</summary>
        public async Task CleanRunAsync(int day)
        {
            var files = SourceOfDay(day);
            var entry = await RunAsync(Target, retention: false);
            entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
            Expected[entry.Version!] = files;
            Time.Advance(TimeSpan.FromDays(1));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fault_at_every_step_keeps_complete_versions_restorable(bool storageDiesAtFault)
    {
        var completedWithoutFault = false;
        for (var n = 1; n <= MaxSteps && !completedWithoutFault; n++)
        {
            var scenario = new Scenario();
            for (var day = 1; day <= 3; day++)
                await scenario.CleanRunAsync(day);

            // Day 4: the target is short of space, so the run first deletes the oldest version and collects; then it
            // writes its version, applies retention (Daily 2) and collects again. The N-th write or delete fails.
            var files = scenario.SourceOfDay(4);
            var capacity = scenario.Used() + 600;
            var step = 0;
            var stepAtFault = n;
            var faulty = new FaultyStorage(scenario.Target)
            {
                FreeSpace = () => capacity - scenario.Used(),
                Before = (operation, path) =>
                {
                    if (operation is not ("create" or "commit" or "delete" or "ensure"))
                        return;
                    step++;
                    if (step == stepAtFault || (storageDiesAtFault && step > stepAtFault))
                        throw new StorageAccessDeniedException(path);
                },
            };
            scenario.Target.FreeSpace = 0;   // the storage tells its free space (the wrapper's)
            var entry = await scenario.RunAsync(faulty);
            scenario.Target.FreeSpace = null;
            completedWithoutFault = step < n;
            if (completedWithoutFault)
            {
                // The fault-free run went through every step the loop injected faults into.
                entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
                entry.Warnings.Should().BeEmpty();
                entry.RetentionDeleted.Should().Equal("2026_10_15-10_00 Projects", "2026_10_16-10_00 Projects");
                Blobs(scenario.Target).Should().NotContain([BlobOf(DayContent(1)), BlobOf(DayContent(2))]);
            }
            if (entry.Version is { } made)
                scenario.Expected[made] = files;

            var context = $"fault at step {n}{(storageDiesAtFault ? " and after" : "")}, run: {entry.Status} {entry.Reason}";
            await AssertListedVersionsRestoreAsync(scenario, context);

            // A person releases the lock a failed run may have left; the next run cleans up and collects.
            await TargetLock.ForceReleaseAsync(scenario.Target, CancellationToken.None);
            scenario.Time.Advance(TimeSpan.FromDays(1));
            var nextFiles = scenario.SourceOfDay(5);
            var next = await scenario.RunAsync(scenario.Target);
            next.Status.Should().Be(RunStatus.Completed, $"{context}; next run: {next.Reason}");
            next.Warnings.Should().BeEmpty(context);
            scenario.Expected[next.Version!] = nextFiles;
            await AssertListedVersionsRestoreAsync(scenario, context + "; after the next run");
            await AssertPoolHoldsOnlyReferencedBlobsAsync(scenario, context);
        }

        completedWithoutFault.Should().BeTrue($"a run without a fault needs fewer than {MaxSteps} writes and deletes");
    }

    private static async Task AssertListedVersionsRestoreAsync(Scenario scenario, string context)
    {
        var versions = await VersionCatalog.ListAsync(scenario.Target, PlanId, PlanName);
        versions.Should().NotBeEmpty(context);
        foreach (var version in versions)
        {
            if (version.Ownership == VersionOwnership.NoManifest)
            {
                // The empty folder a removal could not delete at its very end: no version, nothing to restore.
                var content = new List<string>();
                await foreach (var item in scenario.Target.ListAsync(version.Path, recursive: true, CancellationToken.None))
                    content.Add(item.Path);
                content.Should().BeEmpty($"{context}: {version.Name} has no manifest");
                continue;
            }
            version.IsOwned.Should().BeTrue($"{context}: {version.Name} is {version.Ownership}");
            scenario.Expected.Should().ContainKey(version.Name, context);
            Dictionary<string, string> restored;
            try
            {
                // The way the app restores: the target's version view, then the Restorer (which verifies every file).
                var view = await VersionViews.OpenAsync(scenario.Target, version.Path, CancellationToken.None);
                view.Should().BeOfType<PoolVersionView>(context);
                restored = await RestoreFromAsync(view, version.Path);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException)
            {
                throw new XunitException($"{context}: {version.Name} does not restore: {ex.Message}");
            }
            restored.Should().Equal(scenario.Expected[version.Name], $"{context}: {version.Name}");
        }
    }

    private static async Task AssertPoolHoldsOnlyReferencedBlobsAsync(Scenario scenario, string context)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in await VersionCatalog.ListAsync(scenario.Target, PlanId, PlanName))
            foreach (var content in (await RestoreAsync(scenario.Target, version.Path)).Values)
                referenced.Add(BlobOf(content));
        Blobs(scenario.Target).Should().BeEquivalentTo(referenced, context);
    }
}
