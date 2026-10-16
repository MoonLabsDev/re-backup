using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Core.Versions;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests;

/// <summary>
/// The pool header of an incremental plan's Versions tab: the pool's size and what full copies would take, from the
/// index, and the pool measured on the target by hand.
/// </summary>
public class VersionsPoolStatsTests : IDisposable
{
    private const string First = "2026_09_29-14_05 Projects";
    private const string Second = "2026_09_30-14_05 Projects";
    private readonly MainViewModelFixture _fixture = new();
    private readonly InMemoryStorage _target = new();
    private readonly BackupPlan _poolPlan;
    private IStorage _current;

    public VersionsPoolStatsTests()
    {
        _current = _target;
        _poolPlan = new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = StorageLocation.FileSystem(Path.Combine(_fixture.Root, "source")),
            Target = new StorageLocation("s3", "", "conn-1"),
            Incremental = true,
        };
        PoolTargets.WriteStore(_target, "p1");
    }

    public void Dispose() => _fixture.Dispose();

    private VersionsViewModel Create(BackupPlan plan)
    {
        var storages = new LambdaStorageFactory(location => location.Kind == "s3" ? _current : new StorageFactory().Open(location));
        var worker = new VersionIndexWorker(new VersionIndexSet(Path.Combine(_fixture.Root, "index")));
        var context = new VersionsContext(worker, _fixture.Dialogs, _ => { }, storages, MachineName: "THIS-PC");
        return new VersionsViewModel(() => plan, () => false, new FakeFolderOpener(), context);
    }

    /// <summary>Two versions sharing 14 bytes: 43 bytes as full copies, 29 bytes in the pool.</summary>
    private void WriteTwoVersions()
    {
        PoolTargets.WriteVersion(_target, First, "p1", "Projects",
            new PoolFile("shared.txt", "shared content"), new PoolFile("old.txt", "12345678"));
        PoolTargets.WriteVersion(_target, Second, "p1", "Projects",
            new PoolFile("shared.txt", "shared content"), new PoolFile("new.txt", "1234567"));
    }

    private async Task<VersionsViewModel> LoadedPoolAsync()
    {
        WriteTwoVersions();
        var vm = Create(_poolPlan);
        await vm.RefreshCommand.ExecuteAsync(null);
        return vm;
    }

    /// <summary>A full plan on a folder with two versions: 5 + 7 bytes, and a folder that is no version of the plan.</summary>
    private VersionsViewModel CreateFullPlan()
    {
        var target = Path.Combine(_fixture.Root, "target");
        Directory.CreateDirectory(Path.Combine(target, First));
        File.WriteAllText(Path.Combine(target, First, "a.txt"), "alpha");
        Directory.CreateDirectory(Path.Combine(target, Second, "sub"));
        File.WriteAllText(Path.Combine(target, Second, "sub", "b.txt"), "bravo 2");
        Directory.CreateDirectory(Path.Combine(target, "not a version"));
        File.WriteAllText(Path.Combine(target, "not a version", "big.bin"), new string('x', 1000));
        return Create(new BackupPlan
        {
            Id = "p2",
            Name = "Projects",
            Source = StorageLocation.FileSystem(Path.Combine(_fixture.Root, "source")),
            Target = StorageLocation.FileSystem(target),
        });
    }

    [Fact]
    public async Task Full_plan_shows_what_all_versions_take_together()
    {
        var vm = CreateFullPlan();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.ShowPoolStats.Should().BeTrue();
        vm.FullEquivalentBytes.Should().Be(12);
        vm.PoolSummary.Should().Be($"All versions together: {Formats.Bytes(12)} (full copies, no deduplication)");
        vm.PoolSummary.Should().NotContain("saved");
        vm.BarFraction.Should().Be(1, "full copies save nothing: the bar shows the space as used");
        vm.MeasureLabel.Should().Be(Loc.T("versions.pool.measureFull"));
    }

    [Fact]
    public async Task Full_plan_measures_its_version_folders_on_the_target()
    {
        var vm = CreateFullPlan();
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.MeasurePoolCommand.ExecuteAsync(null);

        vm.MeasuredSummary.Should().Be($"On target: {Formats.Bytes(12)} in 2 files", "the folder that is no version is left out");
    }

    [Fact]
    public async Task Pool_values_come_from_the_index_after_a_sync()
    {
        var vm = await LoadedPoolAsync();

        vm.ShowPoolStats.Should().BeTrue();
        vm.PoolBytes.Should().Be(29);
        vm.FullEquivalentBytes.Should().Be(43);
        vm.SavedFraction.Should().BeApproximately(14.0 / 43, 1e-9);
        vm.PoolSummary.Should().Be($"Pool {Formats.Bytes(29)} · full copies would be {Formats.Bytes(43)} · 32 % saved", "32.6 % is floored");
    }

    [Fact]
    public async Task Pool_values_follow_a_new_version()
    {
        var vm = await LoadedPoolAsync();

        PoolTargets.WriteVersion(_target, "2026_10_01-14_05 Projects", "p1", "Projects", new PoolFile("big.txt", "0123456789"));
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.PoolBytes.Should().Be(39);
        vm.FullEquivalentBytes.Should().Be(53);
    }

    [Fact]
    public async Task Saved_fraction_is_zero_without_versions()
    {
        var vm = Create(_poolPlan);

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.ShowPoolStats.Should().BeTrue();
        vm.FullEquivalentBytes.Should().Be(0);
        vm.SavedFraction.Should().Be(0);
    }

    [Fact]
    public async Task Measure_shows_the_bytes_the_index_does_not_reference()
    {
        var vm = await LoadedPoolAsync();
        _target.AddFile(PoolLayout.BlobPath(PoolTargets.HashOf("orphan!!!!")), Encoding.UTF8.GetBytes("orphan!!!!"));

        await vm.MeasurePoolCommand.ExecuteAsync(null);

        vm.MeasuredSummary.Should().Be(
            $"On target: {Formats.Bytes(39)} in 4 files · about {Formats.Bytes(10)} not referenced");
        vm.IsMeasuringPool.Should().BeFalse();
    }

    [Fact]
    public async Task Measure_hides_not_referenced_when_nothing_is_left_over()
    {
        var vm = await LoadedPoolAsync();

        await vm.MeasurePoolCommand.ExecuteAsync(null);

        vm.MeasuredSummary.Should().Be($"On target: {Formats.Bytes(29)} in 3 files");
    }

    [Fact]
    public async Task Measure_of_an_unreachable_target_shows_a_message()
    {
        var vm = await LoadedPoolAsync();
        _current = RetentionCoverageTests.OfflineStorage.Create();

        await vm.MeasurePoolCommand.ExecuteAsync(null);

        vm.MeasuredSummary.Should().Be("The target cannot be reached, so the pool was not measured.");
        vm.PoolBytes.Should().Be(29, "the index values stay");
    }

    [Fact]
    public async Task Measurement_survives_a_sync_with_unchanged_stats()
    {
        var vm = await LoadedPoolAsync();
        await vm.MeasurePoolCommand.ExecuteAsync(null);
        var measured = vm.MeasuredSummary;
        measured.Should().NotBeNull();

        vm.EnsureLoaded();   // the tab is shown again
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.MeasuredSummary.Should().Be(measured);
    }

    [Fact]
    public async Task Measurement_is_cleared_when_the_stats_change_or_the_target_changes()
    {
        var vm = await LoadedPoolAsync();
        await vm.MeasurePoolCommand.ExecuteAsync(null);

        PoolTargets.WriteVersion(_target, "2026_10_01-14_05 Projects", "p1", "Projects", new PoolFile("big.txt", "0123456789"));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.PoolBytes.Should().Be(39);
        vm.MeasuredSummary.Should().BeNull("it was measured against the index before the new version");

        await vm.MeasurePoolCommand.ExecuteAsync(null);
        vm.MeasuredSummary.Should().NotBeNull();
        vm.OnSavedTargetChanged();
        vm.MeasuredSummary.Should().BeNull();
    }

    [Fact]
    public async Task Measure_shows_a_message_when_listing_the_pool_is_denied()
    {
        var vm = await LoadedPoolAsync();
        _current = DeniedPoolStorage.Create(_target);

        await vm.MeasurePoolCommand.ExecuteAsync(null);

        vm.MeasuredSummary.Should().Be("The pool could not be measured: no access");
        vm.IsMeasuringPool.Should().BeFalse();
        vm.MeasurePoolCommand.CanExecute(null).Should().BeTrue();

        _current = _target;
        await vm.MeasurePoolCommand.ExecuteAsync(null);
        vm.MeasuredSummary.Should().Be($"On target: {Formats.Bytes(29)} in 3 files");
    }

    [Fact]
    public async Task Saved_percentage_never_rounds_up_to_100()
    {
        // 200 files of one content: 99.5 % saved, which rounding would show as 100 %.
        PoolTargets.WriteVersion(_target, First, "p1", "Projects",
            Enumerable.Range(0, 200).Select(i => new PoolFile($"copy{i}.txt", "x", Size: 1000)).ToArray());
        var vm = Create(_poolPlan);

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.SavedFraction.Should().BeApproximately(0.995, 1e-9);
        vm.PoolSummary.Should().EndWith("· 99 % saved");
    }

    /// <summary>Forwards to an in-memory storage; listing the pool is denied.</summary>
    public class DeniedPoolStorage : DispatchProxy
    {
        private InMemoryStorage _inner = null!;

        public static IStorage Create(InMemoryStorage inner)
        {
            var proxy = Create<IStorage, DeniedPoolStorage>();
            ((DeniedPoolStorage)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStorage.ListAsync) && (string)args![0]! == PoolLayout.PoolFolder)
                return Denied();
            return targetMethod!.Invoke(_inner, args);
        }

        private static async IAsyncEnumerable<StorageEntry> Denied()
        {
            await Task.Yield();
            throw new UnauthorizedAccessException("no access");
#pragma warning disable CS0162 // an iterator needs a yield
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task Measure_runs_once_at_a_time_and_can_be_canceled()
    {
        var vm = await LoadedPoolAsync();
        var gated = GatedListStorage.Create(_target);
        _current = gated;
        vm.CancelMeasurePoolCommand.CanExecute(null).Should().BeFalse();

        var measuring = vm.MeasurePoolCommand.ExecuteAsync(null);
        await GatedListStorage.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));

        vm.IsMeasuringPool.Should().BeTrue();
        vm.MeasurePoolCommand.CanExecute(null).Should().BeFalse();
        vm.CancelMeasurePoolCommand.Execute(null);
        await measuring.WaitAsync(TimeSpan.FromSeconds(20));

        vm.IsMeasuringPool.Should().BeFalse();
        vm.MeasuredSummary.Should().BeNull("a canceled measurement shows nothing");
        vm.MeasurePoolCommand.CanExecute(null).Should().BeTrue();
    }

    /// <summary>Forwards to an in-memory storage; listing the pool waits until it is canceled.</summary>
    public class GatedListStorage : DispatchProxy
    {
        public static TaskCompletionSource Reached { get; private set; } = new();

        private InMemoryStorage _inner = null!;

        public static IStorage Create(InMemoryStorage inner)
        {
            Reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var proxy = Create<IStorage, GatedListStorage>();
            ((GatedListStorage)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStorage.ListAsync) && (string)args![0]! == PoolLayout.PoolFolder)
                return Held((CancellationToken)args[2]!);
            return targetMethod!.Invoke(_inner, args);
        }

        private static async IAsyncEnumerable<StorageEntry> Held([EnumeratorCancellation] CancellationToken ct)
        {
            Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }

    private sealed class LambdaStorageFactory(Func<StorageLocation, IStorage> open) : IStorageFactory
    {
        public IStorage Open(StorageLocation location) => open(location);
    }
}
