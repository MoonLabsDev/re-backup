using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
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
using ReBackup.Storage.FileSystem;

namespace ReBackup.Core.Tests.Cleanup;

public class CleanupRunnerFullTests : IDisposable
{
    private const string Old = "2026_09_30-14_05";
    private const string New = "2026_10_01-09_00";
    private static readonly string OldName = $"{Old} {VersionBuilder.PlanName}";
    private static readonly string NewName = $"{New} {VersionBuilder.PlanName}";

    private readonly TempDir _tmp = new();
    private readonly string _target;

    public CleanupRunnerFullTests()
    {
        _target = _tmp.CreateDir("target");
    }

    public void Dispose() => _tmp.Dispose();

    private IStorage Target => new FileSystemStorage(_target);

    private BackupPlan Plan => new()
    {
        Id = VersionBuilder.PlanId,
        Name = VersionBuilder.PlanName,
        Target = StorageLocation.FileSystem(_target),
    };

    private static IgnoreMatcher Rules(params string[] patterns) =>
        IgnoreMatcher.ForPlan(new IgnoreSettings { UseGlobalDefaults = false, Patterns = [.. patterns] }, [], []);

    private void WriteVersion(string minute) =>
        VersionBuilder.Write(_target, minute,
        [
            VersionBuilder.File("src/a.cs", "class A {}"),
            VersionBuilder.File("target/x.dll", "xx"),
            VersionBuilder.File("target/sub/y.dll", "yyy"),
        ]);

    private async Task<CleanupTarget> Scanned(string name) =>
        new(name, await VersionIndex.StampOfAsync(Target, name, CancellationToken.None));

    private BackupManifest ManifestOf(string name) =>
        JsonSerializer.Deserialize<BackupManifest>(
            File.ReadAllText(Path.Combine(_target, name, VersionMarkerNames.Manifest)), JsonDefaults.Options)!;

    private Task<CleanupResult> Run(IReadOnlyList<CleanupTarget> versions, IStorage? storage = null,
        IgnoreMatcher? matcher = null, IVersionIndexSink? index = null, IProgress<CleanupProgress>? progress = null,
        CancellationToken ct = default) =>
        CleanupRunner.RunAsync(storage ?? Target, Plan, matcher ?? Rules("target/"), versions, index, progress, ct, "this-pc");

    [Fact]
    public async Task Shortens_the_manifest_then_deletes_the_files_and_ignored_folders()
    {
        WriteVersion(Old);

        var result = await Run([await Scanned(OldName)]);

        var manifest = ManifestOf(OldName);
        manifest.Files.Select(f => f.Path).Should().Equal("src/a.cs");
        manifest.FileCount.Should().Be(1);
        manifest.TotalBytes.Should().Be("class A {}".Length);
        manifest.Directories.Should().Equal("src");
        Directory.Exists(Path.Combine(_target, OldName, "target")).Should().BeFalse();
        File.Exists(Path.Combine(_target, OldName, "src", "a.cs")).Should().BeTrue();
        File.Exists(Path.Combine(_target, OldName, VersionMarkerNames.Pruning)).Should().BeFalse();

        result.Versions.Should().ContainSingle().Which.Should().Be(
            new CleanupVersionResult(OldName, CleanupOutcome.Cleaned, 2, 5, null));
        result.FilesRemoved.Should().Be(2);
        result.BytesFreed.Should().Be(5);
        result.Canceled.Should().BeFalse();
    }

    [Fact]
    public async Task Writes_the_marker_first_then_the_manifest_then_deletes_and_removes_the_marker_last()
    {
        WriteVersion(Old);
        var steps = new List<string>();
        string? markerSeen = null;
        var watching = new FaultyStorage(Target)
        {
            Before = (op, path) =>
            {
                lock (steps) steps.Add($"{op} {path}");
                // While the files go, the marker lists them.
                if (op == "delete" && path.EndsWith("x.dll", StringComparison.Ordinal))
                    markerSeen = File.ReadAllText(Path.Combine(_target, OldName, VersionMarkerNames.Pruning));
            },
        };

        var result = await Run([await Scanned(OldName)], watching);

        result.Versions.Single().Outcome.Should().Be(CleanupOutcome.Cleaned);
        var markerCreate = steps.IndexOf($"create {OldName}/{VersionMarkerNames.Pruning}");
        var manifestCreate = steps.IndexOf($"create {OldName}/{VersionMarkerNames.Manifest}");
        var firstDelete = steps.FindIndex(s => s.StartsWith("delete ", StringComparison.Ordinal));
        var markerDelete = steps.IndexOf($"delete {OldName}/{VersionMarkerNames.Pruning}");
        markerCreate.Should().BeGreaterThanOrEqualTo(0);
        manifestCreate.Should().BeGreaterThan(markerCreate);
        firstDelete.Should().BeGreaterThan(manifestCreate);
        markerDelete.Should().Be(steps.FindLastIndex(s => s.StartsWith("delete ", StringComparison.Ordinal)));
        watching.Creates.Should().Contain(c => c.Path.EndsWith(VersionMarkerNames.Pruning, StringComparison.Ordinal) &&
                                               c.Options.Durable);

        var marker = JsonSerializer.Deserialize<PruningInfo>(markerSeen!, JsonDefaults.Options)!;
        marker.PlanId.Should().Be(VersionBuilder.PlanId);
        marker.Host.Should().Be("this-pc");
        marker.Files.Should().BeEquivalentTo("target/x.dll", "target/sub/y.dll");
        marker.Folders.Should().Equal("target/sub", "target");
    }

    [Fact]
    public async Task Hands_the_cleaned_version_to_the_index()
    {
        WriteVersion(Old);
        var sink = new RecordingSink();

        await Run([await Scanned(OldName)], index: sink);

        sink.Calls.Should().ContainSingle();
        var (planId, version, manifest) = sink.Calls[0];
        planId.Should().Be(VersionBuilder.PlanId);
        version.Name.Should().Be(OldName);
        version.Ownership.Should().Be(VersionOwnership.Owned);
        version.FileCount.Should().Be(1);
        manifest.Files.Select(f => f.Path).Should().Equal("src/a.cs");
    }

    [Fact]
    public async Task A_version_changed_since_the_scan_is_skipped()
    {
        WriteVersion(Old);
        var sink = new RecordingSink();

        var result = await Run([new CleanupTarget(OldName, "m:stale")], index: sink);

        result.Versions.Single().Should().Be(new CleanupVersionResult(OldName, CleanupOutcome.Changed, 0, 0, null));
        ManifestOf(OldName).FileCount.Should().Be(3);
        File.Exists(Path.Combine(_target, OldName, "target", "x.dll")).Should().BeTrue();
        File.Exists(Path.Combine(_target, OldName, VersionMarkerNames.Pruning)).Should().BeFalse();
        sink.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_conflicting_manifest_write_deletes_nothing()
    {
        WriteVersion(Old);
        var storage = new FaultyStorage(Target)
        {
            Before = (op, path) =>
            {
                if (op == "create" && path.EndsWith(VersionMarkerNames.Manifest, StringComparison.Ordinal))
                    throw new StorageConflictException(path, "replaced meanwhile");
            },
        };

        var result = await Run([await Scanned(OldName)], storage);

        result.Versions.Single().Outcome.Should().Be(CleanupOutcome.Changed);
        result.BytesFreed.Should().Be(0);
        File.Exists(Path.Combine(_target, OldName, VersionMarkerNames.Pruning)).Should().BeFalse();
        File.Exists(Path.Combine(_target, OldName, "target", "x.dll")).Should().BeTrue();
        File.Exists(Path.Combine(_target, OldName, "target", "sub", "y.dll")).Should().BeTrue();
        ManifestOf(OldName).FileCount.Should().Be(3);
        storage.DeleteBatches.SelectMany(b => b).Should().Equal($"{OldName}/{VersionMarkerNames.Pruning}");
    }

    [Fact]
    public async Task A_version_with_nothing_ignored_is_left_alone()
    {
        WriteVersion(Old);
        var storage = new FaultyStorage(Target);

        var result = await Run([await Scanned(OldName)], storage, Rules("*.tmp"));

        result.Versions.Single().Outcome.Should().Be(CleanupOutcome.NothingToDo);
        storage.Created.Should().BeEmpty();
        storage.DeleteBatches.Should().BeEmpty();
    }

    [Fact]
    public async Task A_version_left_empty_stays_valid()
    {
        WriteVersion(Old);

        var result = await Run([await Scanned(OldName)], matcher: Rules("*"));

        result.Versions.Single().Should().Be(new CleanupVersionResult(OldName, CleanupOutcome.Cleaned, 3, 15, null));
        ManifestOf(OldName).FileCount.Should().Be(0);
        Directory.EnumerateFileSystemEntries(Path.Combine(_target, OldName)).Select(Path.GetFileName)
            .Should().Equal(VersionMarkerNames.Manifest);
        (await VersionBuilder.ListAsync(_target)).Should().ContainSingle()
            .Which.Should().Match<VersionInfo>(v => v.Name == OldName && v.IsOwned);
    }

    [Fact]
    public async Task A_version_no_longer_listed_is_gone()
    {
        WriteVersion(Old);

        var result = await Run([new CleanupTarget(NewName, "m:whatever"), await Scanned(OldName)]);

        result.Versions.Select(v => (v.Name, v.Outcome)).Should().Equal(
            (OldName, CleanupOutcome.Cleaned), (NewName, CleanupOutcome.Gone));
    }

    [Fact]
    public async Task Versions_are_cleaned_oldest_first()
    {
        WriteVersion(Old);
        WriteVersion(New);
        var seen = new List<string>();

        var result = await Run([await Scanned(NewName), await Scanned(OldName)],
            progress: new SyncProgress<CleanupProgress>(p => seen.Add(p.VersionName)));

        result.Versions.Select(v => v.Name).Should().Equal(OldName, NewName);
        seen.Distinct().Should().Equal(OldName, NewName);
        result.BytesFreed.Should().Be(10);
    }

    [Fact]
    public async Task A_failing_version_does_not_stop_the_run()
    {
        WriteVersion(Old);
        WriteVersion(New);
        var storage = new FaultyStorage(Target)
        {
            FailDelete = path => path.StartsWith(OldName + "/target/x.dll", StringComparison.Ordinal),
        };

        var result = await Run([await Scanned(OldName), await Scanned(NewName)], storage);

        result.Versions[0].Name.Should().Be(OldName);
        result.Versions[0].Outcome.Should().Be(CleanupOutcome.Failed);
        result.Versions[0].Error.Should().NotBeNullOrEmpty();
        // The manifest is short already; the marker stays so the next start finishes the deletion.
        ManifestOf(OldName).FileCount.Should().Be(1);
        File.Exists(Path.Combine(_target, OldName, VersionMarkerNames.Pruning)).Should().BeTrue();
        result.Versions[1].Should().Be(new CleanupVersionResult(NewName, CleanupOutcome.Cleaned, 2, 5, null));
        result.BytesFreed.Should().Be(5);
    }

    [Fact]
    public async Task Cancel_finishes_the_current_version_and_stops()
    {
        WriteVersion(Old);
        WriteVersion(New);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<CleanupProgress>(p =>
        {
            if (p.VersionName == OldName)
                cts.Cancel();
        });

        var result = await Run([await Scanned(OldName), await Scanned(NewName)], progress: progress, ct: cts.Token);

        result.Versions.Should().ContainSingle().Which.Should().Be(
            new CleanupVersionResult(OldName, CleanupOutcome.Cleaned, 2, 5, null));
        result.Canceled.Should().BeTrue();
        Directory.Exists(Path.Combine(_target, OldName, "target")).Should().BeFalse();
        File.Exists(Path.Combine(_target, OldName, VersionMarkerNames.Pruning)).Should().BeFalse();
        ManifestOf(NewName).FileCount.Should().Be(3);
    }

    [Fact]
    public async Task A_wrong_store_changes_nothing()
    {
        WriteVersion(Old);
        WriteVersion(New);
        File.WriteAllText(Path.Combine(_target, PoolLayout.StoreFile),
            $"{{\"format\":\"pool\",\"hash\":\"xxh128\",\"version\":1,\"planId\":\"{VersionBuilder.PlanId}\"}}");
        var storage = new FaultyStorage(Target);

        var result = await Run([await Scanned(OldName), await Scanned(NewName)], storage);

        var message = CoreTexts.English(StoreGuard.MessageKey(StoreGuardOutcome.FullPlanOnPoolTarget));
        result.Versions.Should().Equal(
            new CleanupVersionResult(OldName, CleanupOutcome.Failed, 0, 0, message),
            new CleanupVersionResult(NewName, CleanupOutcome.Failed, 0, 0, message));
        storage.Created.Should().BeEmpty();
        storage.DeleteBatches.Should().BeEmpty();
        ManifestOf(OldName).FileCount.Should().Be(3);
    }

    [Fact]
    public async Task The_marker_reads_back_as_written()
    {
        WriteVersion(Old);
        var info = new PruningInfo(PruningMarker.FormatVersion, "p1", "this-pc",
            new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc), ["a/b.txt"], ["a"]);

        await PruningMarker.WriteAsync(Target, OldName, info, CancellationToken.None);
        var read = await PruningMarker.TryReadAsync(Target, OldName, CancellationToken.None);
        await PruningMarker.RemoveAsync(Target, OldName, CancellationToken.None);

        read.Should().BeEquivalentTo(info);
        (await PruningMarker.TryReadAsync(Target, OldName, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_failing_index_hand_off_keeps_the_version_cleaned_and_is_traced()
    {
        WriteVersion(Old);
        var listener = new RecordingListener();
        Trace.Listeners.Add(listener);
        try
        {
            var result = await Run([await Scanned(OldName)], index: new ThrowingSink());

            result.Versions.Single().Should().Be(new CleanupVersionResult(OldName, CleanupOutcome.Cleaned, 2, 5, null));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
        listener.Messages.Should().Contain(m => m.Contains(OldName, StringComparison.Ordinal) &&
                                                m.Contains("index is down", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_manifest_without_a_stamp_is_never_replaced()
    {
        WriteVersion(Old);
        var storage = new FaultyStorage(Target)
        {
            MapStat = entry => entry.Path.EndsWith(VersionMarkerNames.Manifest, StringComparison.Ordinal)
                ? entry with { Stamp = null }
                : entry,
        };
        var scanned = new CleanupTarget(OldName, await VersionIndex.StampOfAsync(storage, OldName, CancellationToken.None));

        var result = await Run([scanned], storage);

        result.Versions.Single().Should().Be(new CleanupVersionResult(OldName, CleanupOutcome.Changed, 0, 0, null));
        storage.Created.Should().BeEmpty();
        storage.DeleteBatches.Should().BeEmpty();
        ManifestOf(OldName).FileCount.Should().Be(3);
    }

    [Fact]
    public async Task A_marker_left_behind_is_merged_without_what_the_manifest_still_lists()
    {
        WriteVersion(Old);
        // An earlier cleanup shortened the manifest but did not get to delete old/gone.txt; src/a.cs is listed again.
        Directory.CreateDirectory(Path.Combine(_target, OldName, "old"));
        File.WriteAllText(Path.Combine(_target, OldName, "old", "gone.txt"), "left over");
        await PruningMarker.WriteAsync(Target, OldName,
            new PruningInfo(PruningMarker.FormatVersion, VersionBuilder.PlanId, "this-pc", DateTime.UtcNow,
                ["old/gone.txt", "src/a.cs"], ["old", "src"]), CancellationToken.None);
        string? markerSeen = null;
        var storage = new FaultyStorage(Target)
        {
            Before = (op, path) =>
            {
                if (op == "delete" && path.EndsWith("x.dll", StringComparison.Ordinal))
                    markerSeen = File.ReadAllText(Path.Combine(_target, OldName, VersionMarkerNames.Pruning));
            },
        };

        var result = await Run([await Scanned(OldName)], storage);

        result.Versions.Single().Should().Be(new CleanupVersionResult(OldName, CleanupOutcome.Cleaned, 2, 5, null));
        var marker = JsonSerializer.Deserialize<PruningInfo>(markerSeen!, JsonDefaults.Options)!;
        marker.Files.Should().BeEquivalentTo("target/x.dll", "target/sub/y.dll", "old/gone.txt");
        marker.Folders.Should().Equal("target/sub", "target", "old");
        Directory.Exists(Path.Combine(_target, OldName, "old")).Should().BeFalse();
        File.Exists(Path.Combine(_target, OldName, "src", "a.cs")).Should().BeTrue();
        storage.DeleteBatches.SelectMany(b => b).Should().NotContain(p => p.Contains("src", StringComparison.Ordinal));
        File.Exists(Path.Combine(_target, OldName, VersionMarkerNames.Pruning)).Should().BeFalse();
    }

    private sealed class ThrowingSink : IVersionIndexSink
    {
        public void Add(string planId, IStorage target, VersionInfo version, BackupManifest manifest) =>
            throw new InvalidOperationException("The index is down.");
    }

    private sealed class RecordingListener : TraceListener
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) return [.. _messages]; }
        }

        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            lock (_messages) _messages.Add(message ?? "");
        }

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id,
            string? message)
        {
            lock (_messages) _messages.Add(message ?? "");
        }
    }

    private sealed class RecordingSink : IVersionIndexSink
    {
        public List<(string PlanId, VersionInfo Version, BackupManifest Manifest)> Calls { get; } = [];

        public void Add(string planId, IStorage target, VersionInfo version, BackupManifest manifest) =>
            Calls.Add((planId, version, manifest));
    }
}
