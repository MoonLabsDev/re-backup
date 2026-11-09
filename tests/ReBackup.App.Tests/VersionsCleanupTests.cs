using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Cleanup;
using ReBackup.Core.Ignore;
using ReBackup.Core.Plans;
using ReBackup.Core.Versions;
using ReBackup.Shared.Json;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests;

/// <summary>
/// A cleanup of old versions runs as a job of the Versions tab, like a deletion: the plan counts as deleting versions
/// while it runs (its runs wait, copy, delete and restore are refused), and the index sees the shortened manifests.
/// </summary>
public class VersionsCleanupTests : IDisposable
{
    private const string Older = "2026_09_29-14_05 Projects";
    private const string Newer = "2026_09_30-14_05 Projects";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly MainViewModelFixture _fixture = new();
    private readonly TableStorageFactory _storages = new();
    private readonly InMemoryStorage _target = new();
    private readonly GatedStorage _gated;

    public VersionsCleanupTests()
    {
        _gated = new GatedStorage(_target);
        _storages.Storages[("full", "")] = _gated;
        _fixture.Storages = _storages;
        _fixture.AddConnection("full", "Full bucket", "bucket-a");
        WriteVersion(Older);
        WriteVersion(Newer);
    }

    public void Dispose()
    {
        _gated.Gate.TrySetResult();
        _fixture.Queue?.CancelAll();
        _fixture.Dispose();
    }

    private BackupPlan Plan => new()
    {
        Id = "p1",
        Name = "Projects",
        Source = StorageLocation.FileSystem(Path.Combine(_fixture.Root, "source")),
        Target = new StorageLocation("s3", "", "full"),
    };

    private static IgnoreMatcher Rules(params string[] patterns) =>
        IgnoreMatcher.ForPlan(new IgnoreSettings { UseGlobalDefaults = false, Patterns = [.. patterns] }, [], []);

    private static string ManifestPath(string version) => $"{version}/{VersionMarkerNames.Manifest}";

    /// <summary>The cleanup's marker: written once the version's cleanup has started, before its manifest is replaced.</summary>
    private static string MarkerPath(string version) => $"{version}/{VersionMarkerNames.Pruning}";

    /// <summary>A full-plan version: <c>src/a.cs</c> and two files under <c>target/</c>, with a format 2 manifest.</summary>
    private void WriteVersion(string name)
    {
        var mtime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var manifest = new BackupManifest
        {
            FormatVersion = 2,
            PlanId = "p1",
            PlanName = "Projects",
            CreatedUtc = mtime,
            Source = @"C:\source",
        };
        foreach (var (path, content) in new[] { ("src/a.cs", "class A {}"), ("target/x.dll", "xx"), ("target/y.dll", "yyy") })
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            _target.AddFile($"{name}/{path}", bytes, mtime);
            manifest.Files.Add(new ManifestFile(path, bytes.Length, mtime, "xxh64:" + Convert.ToHexStringLower(XxHash64.Hash(bytes))));
        }
        manifest.FileCount = manifest.Files.Count;
        manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
        manifest.Directories = ["src", "target"];
        _target.AddFile(ManifestPath(name), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonDefaults.Options)));
    }

    private MainViewModel CreateMain()
    {
        _fixture.Store.Save(Plan);
        return _fixture.Create();
    }

    private static PlanEditorViewModel Projects(MainViewModel vm) => vm.Plans.Single(p => p.Id == "p1");

    /// <summary>Reads the target and waits until both versions are indexed.</summary>
    private static async Task LoadAsync(VersionsViewModel versions)
    {
        await versions.RefreshCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => versions.VersionRows.Count == 2 && versions.VersionRows.All(r => r.Indexed is not null) &&
                                   !versions.IsSyncing, "both versions are indexed");
    }

    private async Task<CleanupTarget> Scanned(string name) =>
        new(name, await VersionIndex.StampOfAsync(_target, name, CancellationToken.None));

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Not met in time: {because}");
    }

    /// <summary>Collects the reports as they come (synchronously, on the reporting thread).</summary>
    private sealed class Recorder : IProgress<CleanupProgress>
    {
        public List<CleanupProgress> Reports { get; } = [];

        public void Report(CleanupProgress value)
        {
            lock (Reports)
                Reports.Add(value);
        }
    }

    [Fact]
    public async Task Cleanup_marks_the_plan_as_deleting_while_it_runs()
    {
        var vm = CreateMain();
        var editor = Projects(vm);
        var versions = editor.Versions;
        await LoadAsync(versions);
        var target = await Scanned(Older);
        _gated.HoldsCreate = path => path == MarkerPath(Older);
        var recorder = new Recorder();

        var running = versions.CleanUpAsync(Rules("target/"), [target], recorder);
        await _gated.Reached.Task.WaitAsync(Timeout);

        versions.IsDeleting.Should().BeTrue();
        versions.CanStartCleanup.Should().BeFalse();
        editor.IsDeletingVersions.Should().BeTrue();
        var progress = Loc.F("cleanup.progress", ("current", 1), ("total", 1), ("version", Older));
        await WaitUntilAsync(() => versions.DeleteText == progress, "the tab shows the version being cleaned up");
        versions.DeletionStatus!.Phase.ToString().Should().Be(Loc.T("cleanup.card"));
        await WaitUntilAsync(() => editor.Run.CardState == PlanCardState.Deleting, "the card shows the cleanup");

        _gated.Gate.SetResult();
        var result = await running.WaitAsync(Timeout);

        result.Should().NotBeNull();
        result!.Versions.Should().ContainSingle().Which.Outcome.Should().Be(CleanupOutcome.Cleaned);
        result.Canceled.Should().BeFalse();
        versions.IsDeleting.Should().BeFalse();
        lock (recorder.Reports)
            recorder.Reports.Should().Contain(new CleanupProgress(1, 1, Older, CleanupPhase.Rewriting));
        await WaitUntilAsync(() => !editor.IsDeletingVersions && versions.DeletionStatus is null && !versions.IsSyncing,
            "the cleanup and the indexing after it end");
        versions.CanStartCleanup.Should().BeTrue();
        _target.Files.Should().NotContain($"{Older}/target/x.dll").And.Contain($"{Newer}/target/x.dll");
    }

    [Fact]
    public async Task Cleanup_is_refused_while_a_backup_is_active()
    {
        var worker = new VersionIndexWorker(new VersionIndexSet(Path.Combine(_fixture.Root, "index")));
        var context = new VersionsContext(worker, _fixture.Dialogs, _ => { }, _storages, MachineName: "THIS-PC");
        var plan = Plan;
        var versions = new VersionsViewModel(() => plan, () => true, new FakeFolderOpener(), context);
        var before = _target.Files.ToList();

        versions.CanStartCleanup.Should().BeFalse();
        var result = await versions.CleanUpAsync(Rules("target/"), [await Scanned(Older)]);

        result.Should().BeNull();
        versions.IsDeleting.Should().BeFalse();
        _target.Files.Should().BeEquivalentTo(before, "nothing was changed");
    }

    [Fact]
    public async Task Cleanup_reports_the_versions_cleaned_up_and_the_bytes_freed()
    {
        var statuses = new List<string>();
        var worker = new VersionIndexWorker(new VersionIndexSet(Path.Combine(_fixture.Root, "index")));
        var context = new VersionsContext(worker, _fixture.Dialogs, s => { lock (statuses) statuses.Add(s.ToString()); },
            _storages, MachineName: "THIS-PC");
        var plan = Plan;
        var versions = new VersionsViewModel(() => plan, () => false, new FakeFolderOpener(), context);

        var result = await versions.CleanUpAsync(Rules("target/"), [await Scanned(Older)]).WaitAsync(Timeout);

        result!.BytesFreed.Should().Be(5);
        lock (statuses)
        {
            statuses.Should().Contain(Loc.F("cleanup.running", ("count", 1)));
            statuses.Should().Contain(Loc.F("cleanup.done", ("count", 1), ("size", Formats.Bytes(5))));
        }
    }

    [Fact]
    public async Task Cleanup_updates_the_index()
    {
        var vm = CreateMain();
        var versions = Projects(vm).Versions;
        await LoadAsync(versions);

        var result = await versions.CleanUpAsync(Rules("target/"), [await Scanned(Older)]).WaitAsync(Timeout);

        result!.FilesRemoved.Should().Be(2);
        await WaitUntilAsync(() => versions.VersionRows.SingleOrDefault(r => r.Name == Older)?.Indexed?.FileCount == 1,
            "the index lists the shortened version");
        versions.VersionRows.Single(r => r.Name == Newer).Indexed!.FileCount.Should().Be(3);
    }

    [Fact]
    public async Task Cancel_stops_after_the_version_being_cleaned_up()
    {
        var vm = CreateMain();
        var versions = Projects(vm).Versions;
        await LoadAsync(versions);
        IReadOnlyList<CleanupTarget> targets = [await Scanned(Older), await Scanned(Newer)];
        _gated.HoldsCreate = path => path == MarkerPath(Older);

        var running = versions.CleanUpAsync(Rules("target/"), targets);
        await _gated.Reached.Task.WaitAsync(Timeout);
        versions.CancelCleanup();
        _gated.Gate.SetResult();
        var result = await running.WaitAsync(Timeout);

        result!.Canceled.Should().BeTrue();
        result.Versions.Select(v => v.Name).Should().Equal(Older);
        versions.IsDeleting.Should().BeFalse();
        _target.Files.Should().NotContain($"{Older}/target/x.dll").And.Contain($"{Newer}/target/x.dll");
    }
}
