using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;

namespace ReBackup.Core.Tests.Versions;

public class VersionIndexWorkerTests : IDisposable
{
    private static readonly DateTime Mtime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private VersionIndexWorker Worker(Action<string, Exception>? onError = null) =>
        new(new VersionIndexSet(_tmp.PathOf("indexes")), onError);

    private (VersionInfo Version, BackupManifest Manifest) Finished(string minute)
    {
        var folder = VersionBuilder.Write(_tmp.PathOf("target"), minute, [VersionBuilder.File("a.txt", "alpha")]);
        var version = VersionBuilder.List(_tmp.PathOf("target")).Single(v => v.Path == folder);
        var manifest = new BackupManifest { PlanId = VersionBuilder.PlanId, Source = VersionBuilder.Source };
        manifest.Files.Add(new ManifestFile("a.txt", 5, Mtime, "xxh64:0000000000000001"));
        return (version, manifest);
    }

    [Fact]
    public async Task Add_returns_at_once_while_the_index_is_busy_and_the_version_follows()
    {
        var worker = Worker();
        using var release = new ManualResetEventSlim();
        var busy = worker.RunAsync("p1", _ => release.Wait());
        var (version, manifest) = Finished("2026_09_30-16_05");

        worker.Add("p1", version, manifest);

        busy.IsCompleted.Should().BeFalse();
        worker.Indexes.For("p1").Versions().Should().BeEmpty("the add waits behind the work queued before it");
        release.Set();
        await worker.WhenIdle("p1");
        worker.Indexes.For("p1").Versions().Should().ContainSingle().Which.Name.Should().Be(version.Name);
    }

    [Fact]
    public async Task Work_on_one_plans_index_runs_one_at_a_time_in_order()
    {
        var worker = Worker();
        var running = 0;
        var maxRunning = 0;
        var order = new List<int>();
        var tasks = Enumerable.Range(0, 8).Select(i => worker.RunAsync("p1", _ =>
        {
            var now = Interlocked.Increment(ref running);
            lock (order)
            {
                maxRunning = Math.Max(maxRunning, now);
                order.Add(i);
            }
            Thread.SpinWait(20_000);
            Interlocked.Decrement(ref running);
            return i;
        })).ToList();

        var results = await Task.WhenAll(tasks);

        results.Should().Equal(Enumerable.Range(0, 8));
        order.Should().Equal(Enumerable.Range(0, 8));
        maxRunning.Should().Be(1);
    }

    [Fact]
    public async Task Work_on_another_plans_index_does_not_wait()
    {
        var worker = Worker();
        using var release = new ManualResetEventSlim();
        var busy = worker.RunAsync("p1", _ => release.Wait());

        var other = await worker.RunAsync("p2", index => index.Versions().Count);

        other.Should().Be(0);
        busy.IsCompleted.Should().BeFalse();
        release.Set();
        await busy;
    }

    [Fact]
    public async Task Work_runs_off_the_calling_thread()
    {
        var worker = Worker();
        var caller = Environment.CurrentManagedThreadId;

        var thread = await worker.RunAsync("p1", _ => Environment.CurrentManagedThreadId);

        thread.Should().NotBe(caller);
    }

    [Fact]
    public async Task Failed_or_canceled_work_does_not_stop_the_work_queued_after_it()
    {
        var worker = Worker();
        using var release = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var busy = worker.RunAsync("p1", _ => release.Wait());
        var ranCanceled = false;
        var failing = worker.RunAsync<int>("p1", _ => throw new IOException("network gone"));
        var canceled = worker.RunAsync("p1", _ => ranCanceled = true, cts.Token);
        var after = worker.RunAsync("p1", _ => 42);

        cts.Cancel();
        release.Set();

        await busy;
        await failing.Invoking(t => t).Should().ThrowAsync<IOException>();
        await canceled.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        ranCanceled.Should().BeFalse();
        (await after).Should().Be(42);
    }

    [Fact]
    public async Task A_failing_add_is_reported_to_the_error_callback_and_not_thrown()
    {
        _tmp.WriteFile("indexes", "a file where the index folder should be");
        var errors = new List<(string PlanId, Exception Error)>();
        var worker = Worker((planId, ex) => { lock (errors) errors.Add((planId, ex)); });
        var (version, manifest) = Finished("2026_09_30-16_05");

        worker.Add("p1", version, manifest);
        await worker.WhenIdle("p1");

        errors.Should().ContainSingle().Which.PlanId.Should().Be("p1");
    }

    [Fact]
    public void Add_with_an_unusable_plan_id_throws_right_away()
    {
        var worker = Worker();
        var (version, manifest) = Finished("2026_09_30-16_05");

        worker.Invoking(w => w.Add("a/b", version, manifest)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task WhenIdle_of_a_plan_without_work_is_done()
    {
        await Worker().WhenIdle("p1");
    }
}
