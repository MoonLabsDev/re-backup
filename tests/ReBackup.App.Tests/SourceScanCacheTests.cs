using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.App.Services;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests;

/// <summary>The scan cache: at most two plans, dropped after ten minutes unused, shared by the Ignore tab and the control service.</summary>
public class SourceScanCacheTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private readonly MainViewModelFixture _fixture = new();   // the labels the view model's texts need
    private readonly InMemoryStorage _source = new(StorageCapabilities.None);
    private readonly FakeTimeProvider _time = new();
    private readonly ListGatedStorage _gated;
    private int _opens;
    private bool _useGate;

    public SourceScanCacheTests()
    {
        _source.AddFile("a.txt", new byte[5]);
        _source.AddFile("docs/b.txt", new byte[7]);
        _gated = new ListGatedStorage(_source);
    }

    public void Dispose() => _fixture.Dispose();

    private SourceScanCache NewCache() => new(new Factory(_ =>
    {
        _opens++;
        return _useGate ? _gated : _source;
    }), () => [], _time);

    private static BackupPlan Plan(string id, string path = "") =>
        new() { Id = id, Name = id, Source = new StorageLocation("mem", path, "conn-1") };

    private async Task<SourceIndex> IndexAsync() =>
        await LiveScan.Start(_source, "root", new IgnoreSettings(), []).Completion;

    [Fact]
    public async Task Ignore_tab_index_is_reused_by_the_cache()
    {
        var cache = NewCache();
        var plan = Plan("p1");
        var vm = new IgnorePreviewViewModel(() => plan.Source, () => new IgnoreSettings(), () => [], new Factory(_ => _source),
            scanCache: cache, planId: plan.Id);

        await vm.IndexCommand.ExecuteAsync(null);
        var lookup = await cache.GetAsync(plan, Wait, CancellationToken.None);

        lookup.Running.Should().BeFalse();
        lookup.Index.Should().BeSameAs(vm.CompletedIndex, "the cache holds the very index the tab holds, not a copy");
        lookup.Files.Should().Be(2);
        _opens.Should().Be(0, "the cache did not open the source itself");
    }

    [Fact]
    public async Task Release_drops_the_plans_cache_entry_and_the_next_index_rescans()
    {
        var cache = NewCache();
        var plan = Plan("p1");
        var tabOpens = 0;
        var vm = new IgnorePreviewViewModel(() => plan.Source, () => new IgnoreSettings(), () => [],
            new Factory(_ => { tabOpens++; return _source; }), scanCache: cache, planId: plan.Id);
        await vm.IndexCommand.ExecuteAsync(null);
        cache.Has("p1").Should().BeTrue();
        vm.Release();
        vm.Root.Should().BeNull();
        cache.Has("p1").Should().BeFalse("a cache rooting the index would defeat the release");

        await vm.IndexCommand.ExecuteAsync(null);

        vm.Root!.IncludedFiles.Should().Be(2);
        tabOpens.Should().Be(2, "the next index scans again");
    }

    [Fact]
    public async Task Release_of_a_tab_that_never_held_an_index_leaves_the_cache_alone()
    {
        var cache = NewCache();
        var plan = Plan("p1");
        cache.Put("p1", await IndexAsync(), plan.Source);
        var vm = new IgnorePreviewViewModel(() => plan.Source, () => new IgnoreSettings(), () => [], new Factory(_ => _source),
            scanCache: cache, planId: plan.Id);

        vm.Release();

        cache.Has("p1").Should().BeTrue();
    }

    [Fact]
    public async Task A_scan_the_cache_started_is_shown_on_the_tabs_first_open()
    {
        var cache = NewCache();
        var plan = Plan("p1");
        var started = (await cache.GetAsync(plan, Wait, CancellationToken.None)).Index;
        var tabOpens = 0;
        var vm = new IgnorePreviewViewModel(() => plan.Source, () => new IgnoreSettings(), () => [],
            new Factory(_ => { tabOpens++; return _source; }), scanCache: cache, planId: plan.Id);

        await vm.IndexCommand.ExecuteAsync(null);

        vm.CompletedIndex.Should().BeSameAs(started);
        vm.Root!.IncludedFiles.Should().Be(2);
        tabOpens.Should().Be(0);
    }

    [Fact]
    public async Task A_waiter_survives_the_tab_putting_its_index_over_the_running_scan()
    {
        _useGate = true;
        var cache = NewCache();
        var plan = Plan("p1");
        var waiter = cache.GetAsync(plan, Wait, CancellationToken.None);
        await WaitUntilAsync(() => _opens == 1 && cache.Count == 1);
        await Task.Delay(50);   // the waiter is inside the scan now

        var index = await IndexAsync();
        cache.Put("p1", index, plan.Source);   // cancels the running scan

        var lookup = await waiter.WaitAsync(TimeSpan.FromSeconds(10));
        lookup.Index.Should().BeSameAs(index);
    }

    [Fact]
    public async Task A_waiter_survives_the_eviction_of_its_scan_by_a_third_plan()
    {
        _useGate = true;
        var cache = NewCache();
        var waiter = cache.GetAsync(Plan("p1"), Wait, CancellationToken.None);
        await WaitUntilAsync(() => _opens == 1 && cache.Count == 1);
        await Task.Delay(50);
        var index = await IndexAsync();
        cache.Put("p2", index);
        cache.Put("p3", index);   // p1 is the least recently used: its scan is cancelled

        _gated.Gate.SetResult();
        var lookup = await waiter.WaitAsync(TimeSpan.FromSeconds(10));

        lookup.Should().NotBeNull();
        lookup.Running.Should().BeFalse("the retry scans again and finishes");
        lookup.Index.Should().NotBeNull();
    }

    [Fact]
    public async Task The_sweep_timer_drops_idle_entries_without_any_lookup()
    {
        var cache = NewCache();
        cache.Put("p1", await IndexAsync());

        _time.Advance(TimeSpan.FromMinutes(11));

        cache.Count.Should().Be(0, "Count does not drop anything itself; the timer did");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(end);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Miss_starts_a_scan_and_waits_up_to_max()
    {
        var cache = NewCache();

        var lookup = await cache.GetAsync(Plan("p1"), Wait, CancellationToken.None);

        lookup.Running.Should().BeFalse();
        lookup.Index.Should().NotBeNull();
        (lookup.Files, lookup.Directories).Should().Be((2, 1));
        _opens.Should().Be(1);
        (await cache.GetAsync(Plan("p1"), Wait, CancellationToken.None)).Index.Should().BeSameAs(lookup.Index);
        _opens.Should().Be(1, "the second lookup is a hit");
    }

    [Fact]
    public async Task Running_scan_returns_progress_after_max_wait()
    {
        _useGate = true;
        var cache = NewCache();
        var pending = cache.GetAsync(Plan("p1"), Wait, CancellationToken.None);
        while (!pending.IsCompleted)
        {
            _time.Advance(Wait);   // the timer of the wait may not be registered yet; push until it fires
            await Task.Delay(10);
        }

        var lookup = await pending;
        lookup.Running.Should().BeTrue();
        lookup.Index.Should().BeNull();

        _gated.Gate.SetResult();
        var done = await cache.GetAsync(Plan("p1"), Wait, CancellationToken.None);
        done.Running.Should().BeFalse();
        done.Index.Should().NotBeNull();
        _opens.Should().Be(1, "the running scan was joined, not restarted");
    }

    [Fact]
    public async Task Holds_at_most_two_plans()
    {
        var cache = NewCache();
        var index = await IndexAsync();
        cache.Put("p1", index);
        cache.Put("p2", index);
        _time.Advance(TimeSpan.FromSeconds(1));
        (await cache.GetAsync(Plan("p1"), Wait, CancellationToken.None)).Index.Should().NotBeNull();   // p1 used last

        cache.Put("p3", index);

        cache.Has("p1").Should().BeTrue();
        cache.Has("p2").Should().BeFalse("the plan unused longest goes");
        cache.Has("p3").Should().BeTrue();
    }

    [Fact]
    public async Task Idle_entries_are_dropped_after_ten_minutes()
    {
        var cache = NewCache();
        cache.Put("p1", await IndexAsync());

        _time.Advance(TimeSpan.FromMinutes(9));
        cache.Has("p1").Should().BeTrue();
        _time.Advance(TimeSpan.FromMinutes(2));

        cache.Has("p1").Should().BeFalse();
    }

    [Fact]
    public async Task Use_keeps_an_entry_alive()
    {
        var cache = NewCache();
        var plan = Plan("p1");
        cache.Put("p1", await IndexAsync());

        for (var i = 0; i < 4; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(8));
            (await cache.GetAsync(plan, Wait, CancellationToken.None)).Index.Should().NotBeNull();
        }

        _opens.Should().Be(0);
    }

    [Fact]
    public async Task Invalidate_forgets_the_plan()
    {
        var cache = NewCache();
        cache.Put("p1", await IndexAsync());

        cache.Invalidate("p1");

        cache.Has("p1").Should().BeFalse();
        (await cache.GetAsync(Plan("p1"), Wait, CancellationToken.None)).Index.Should().NotBeNull();
        _opens.Should().Be(1, "the next lookup scans again");
    }

    [Fact]
    public async Task A_changed_source_location_is_a_miss()
    {
        var cache = NewCache();
        cache.Put("p1", await IndexAsync(), Plan("p1", "old").Source);

        var lookup = await cache.GetAsync(Plan("p1", "new"), Wait, CancellationToken.None);

        lookup.Index.Should().NotBeNull();
        _opens.Should().Be(1);
    }

    private sealed class Factory(Func<StorageLocation, IStorage> open) : IStorageFactory
    {
        public IStorage Open(StorageLocation location) => open(location);
    }

    /// <summary>Folder listings wait for <see cref="Gate"/>.</summary>
    private sealed class ListGatedStorage(InMemoryStorage inner) : IStorage
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StorageCapabilities Capabilities => inner.Capabilities;
        public Task<StorageEntry?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);
        public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive, [EnumeratorCancellation] CancellationToken ct)
        {
            await Gate.Task.WaitAsync(ct);
            await foreach (var entry in inner.ListAsync(folder, recursive, ct))
                yield return entry;
        }
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct) => inner.CreateAsync(path, options, ct);
        public Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct) => inner.DeleteAsync(paths, ct);
        public Task EnsureDirectoryAsync(string path, CancellationToken ct) => inner.EnsureDirectoryAsync(path, ct);
        public Task<long?> GetFreeSpaceAsync(CancellationToken ct) => inner.GetFreeSpaceAsync(ct);
    }
}
