using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Cleanup;
using ReBackup.Core.Ignore;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using ReBackup.Shared.Json;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;
using static ReBackup.Core.Tests.TestSupport.PoolVersions;

namespace ReBackup.Core.Tests.Cleanup;

/// <summary>The cleanup of an incremental plan (spec 5.2, 5.3): manifests are shortened, then the pool is collected once.</summary>
public class CleanupRunnerPoolTests
{
    private const string PlanId = "p1";
    private const string PlanName = "Projects";
    private const string V1 = "2026_10_15-10_00 Projects";
    private const string V2 = "2026_10_16-10_00 Projects";

    private readonly InMemoryStorage _target = new();

    public CleanupRunnerPoolTests()
    {
        _target.AddFile(PoolLayout.StoreFile,
            Bytes($"{{\"format\":\"pool\",\"hash\":\"xxh128\",\"version\":1,\"planId\":\"{PlanId}\"}}"));
    }

    private static BackupPlan Plan => new() { Id = PlanId, Name = PlanName, Incremental = true };

    private static IgnoreMatcher Rules(params string[] patterns) =>
        IgnoreMatcher.ForPlan(new IgnoreSettings { UseGlobalDefaults = false, Patterns = [.. patterns] }, [], []);

    private void Version(string name, params (string Path, string Content)[] files) =>
        PoolVersions.Write(_target, name, PlanId, PlanName, files);

    private async Task<CleanupTarget> Scanned(string name) =>
        new(name, await VersionIndex.StampOfAsync(_target, name, CancellationToken.None));

    private BackupManifest ManifestOf(string name) =>
        JsonSerializer.Deserialize<BackupManifest>(
            _target.ReadAllBytes(StoragePath.Combine(name, VersionMarkerNames.Manifest)), JsonDefaults.Options)!;

    private Task<CleanupResult> Run(IReadOnlyList<CleanupTarget> versions, IStorage? storage = null,
        IgnoreMatcher? matcher = null, IProgress<CleanupProgress>? progress = null) =>
        CleanupRunner.RunAsync(storage ?? _target, Plan, matcher ?? Rules("target/"), versions, null, progress,
            CancellationToken.None, "this-pc");

    [Fact]
    public async Task Rewrites_the_manifests_and_collects_only_unreferenced_blobs()
    {
        Version(V1, ("a.txt", "a-content"), ("target/x.dll", "x-content"));
        Version(V2, ("target/x.dll", "x-content"));
        var phases = new List<CleanupPhase>();

        var result = await Run([await Scanned(V1), await Scanned(V2)],
            progress: new SyncProgress<CleanupProgress>(p => phases.Add(p.Phase)));

        result.Versions.Select(v => (v.Name, v.Outcome)).Should().Equal(
            (V1, CleanupOutcome.Cleaned), (V2, CleanupOutcome.Cleaned));
        ManifestOf(V1).Files.Select(f => f.Path).Should().Equal("a.txt");
        ManifestOf(V2).Files.Should().BeEmpty();
        Blobs(_target).Should().Equal(BlobOf("a-content"));
        result.BytesFreed.Should().Be("x-content".Length);
        phases.Last().Should().Be(CleanupPhase.CollectingPool);
        phases.Should().NotContain(CleanupPhase.Deleting);
        // No marker and no lock is left; the version folders hold only their manifests.
        _target.Files.Should().NotContain(f => f.EndsWith(VersionMarkerNames.Pruning, StringComparison.Ordinal));
        _target.Files.Should().NotContain(PoolLayout.LockFile);
        (await RestoreAsync(_target, V1)).Should().Equal(new Dictionary<string, string> { ["a.txt"] = "a-content" });
    }

    [Fact]
    public async Task A_blob_an_unchecked_version_uses_stays()
    {
        Version(V1, ("a.txt", "a-content"), ("target/x.dll", "x-content"));
        Version(V2, ("target/x.dll", "x-content"));

        var result = await Run([await Scanned(V1)]);

        result.Versions.Single().Outcome.Should().Be(CleanupOutcome.Cleaned);
        ManifestOf(V2).Files.Select(f => f.Path).Should().Equal("target/x.dll");
        Blobs(_target).Should().BeEquivalentTo([BlobOf("a-content"), BlobOf("x-content")]);
        result.BytesFreed.Should().Be(0);
        (await RestoreAsync(_target, V2)).Should().Equal(new Dictionary<string, string> { ["target/x.dll"] = "x-content" });
    }

    [Fact]
    public async Task A_target_locked_by_another_holder_changes_nothing()
    {
        Version(V1, ("a.txt", "a-content"), ("target/x.dll", "x-content"));
        Version(V2, ("target/x.dll", "x-content"));
        var scanned = new[] { await Scanned(V1), await Scanned(V2) };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 8, 30, 0, TimeSpan.Zero));
        var other = await TargetLock.TryAcquireAsync(_target, "other-host", "backup", time, CancellationToken.None);
        other.Should().NotBeNull();
        var locked = CoreTexts.English("core.run.targetLocked",
            ("host", "other-host"), ("operation", "backup"), ("since", "2026-11-09 08:30 UTC"));

        var result = await Run(scanned);

        result.Versions.Should().Equal(
            new CleanupVersionResult(V1, CleanupOutcome.Failed, 0, 0, locked),
            new CleanupVersionResult(V2, CleanupOutcome.Failed, 0, 0, locked));
        result.BytesFreed.Should().Be(0);
        ManifestOf(V1).Files.Should().HaveCount(2);
        ManifestOf(V2).Files.Should().HaveCount(1);
        (await TargetLock.ReadAsync(_target, CancellationToken.None))!.Host.Should().Be("other-host");
    }

    [Fact]
    public async Task The_lock_is_taken_for_the_cleanup_and_released()
    {
        Version(V1, ("a.txt", "a-content"), ("target/x.dll", "x-content"));
        LockInfo? held = null;
        var storage = new FaultyStorage(_target)
        {
            Before = (op, path) =>
            {
                if (op == "create" && path.EndsWith(VersionMarkerNames.Manifest, StringComparison.Ordinal))
                    held = TargetLock.ReadAsync(_target, CancellationToken.None).GetAwaiter().GetResult();
            },
        };

        var result = await Run([await Scanned(V1)], storage);

        result.Versions.Single().Outcome.Should().Be(CleanupOutcome.Cleaned);
        held.Should().NotBeNull();
        held!.Operation.Should().Be("cleanup");
        held.Host.Should().Be("this-pc");
        _target.Files.Should().NotContain(PoolLayout.LockFile);
    }

    [Fact]
    public async Task No_rewritten_manifest_means_no_collect()
    {
        Version(V1, ("a.txt", "a-content"), ("target/x.dll", "x-content"));
        AddBlob(_target, "orphan");
        var phases = new List<CleanupPhase>();

        var result = await Run([await Scanned(V1)], matcher: Rules("*.tmp"),
            progress: new SyncProgress<CleanupProgress>(p => phases.Add(p.Phase)));

        result.Versions.Single().Outcome.Should().Be(CleanupOutcome.NothingToDo);
        result.BytesFreed.Should().Be(0);
        Blobs(_target).Should().Contain(BlobOf("orphan"));
        phases.Should().NotContain(CleanupPhase.CollectingPool);
        _target.Files.Should().NotContain(PoolLayout.LockFile);
    }

    [Fact]
    public async Task A_skipped_collect_leaves_the_versions_cleaned()
    {
        Version(V1, ("a.txt", "a-content"), ("target/x.dll", "x-content"));
        // A folder in transit: the collect is skipped, the manifest is short all the same.
        _target.AddFile("2026_10_17-10_00 Projects/re-pending.json", Bytes("{}"));

        var result = await Run([await Scanned(V1)]);

        result.Versions.Single().Outcome.Should().Be(CleanupOutcome.Cleaned);
        result.BytesFreed.Should().Be(0);
        ManifestOf(V1).Files.Select(f => f.Path).Should().Equal("a.txt");
        Blobs(_target).Should().Contain(BlobOf("x-content"));
        _target.Files.Should().NotContain(PoolLayout.LockFile);
    }
}
