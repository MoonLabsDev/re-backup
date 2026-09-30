using System.Collections.Concurrent;
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Backup;

public class BackupQueueTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly TempDir _tmp = new();
    private readonly FakeRunner _runner = new();
    private readonly ConcurrentQueue<BackupJobUpdate> _updates = new();
    private readonly BackupQueue _queue;

    public BackupQueueTests()
    {
        _queue = new BackupQueue(_runner, planId => new RunLog(_tmp.PathOf($"{planId}.jsonl")));
        _queue.Changed += _updates.Enqueue;
    }

    public void Dispose() => _tmp.Dispose();

    private static BackupRequest Request(string planId) =>
        new(new BackupPlan { Id = planId, Name = "Plan " + planId }, [], RunTrigger.Manual);

    private string[] States(string planId) =>
        _updates.Where(u => u.PlanId == planId && u.Progress is null).Select(u => u.State.ToString()).ToArray();

    [Fact]
    public async Task Runs_jobs_one_at_a_time_in_order()
    {
        _queue.Enqueue(Request("a")).Should().BeTrue();
        _queue.Enqueue(Request("b")).Should().BeTrue();
        await _runner.Started("a").WaitAsync(Timeout);

        _queue.IsBusy.Should().BeTrue();
        _queue.RunningPlanId.Should().Be("a");
        _queue.QueuedCount.Should().Be(1);
        _runner.HasStarted("b").Should().BeFalse();

        _runner.Complete("a", RunStatus.Completed);
        await _runner.Started("b").WaitAsync(Timeout);
        _runner.Complete("b", RunStatus.Completed);
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        _queue.IsBusy.Should().BeFalse();
        States("a").Should().Equal("Queued", "Running", "Finished");
        States("b").Should().Equal("Queued", "Running", "Finished");
        _updates.Last(u => u.PlanId == "a").Result!.Status.Should().Be(RunStatus.Completed);
        _updates.First(u => u.PlanId == "a").PlanName.Should().Be("Plan a");
    }

    [Fact]
    public async Task A_plan_can_be_queued_or_running_only_once()
    {
        _queue.Enqueue(Request("a")).Should().BeTrue();
        _queue.Enqueue(Request("b")).Should().BeTrue();
        await _runner.Started("a").WaitAsync(Timeout);

        _queue.Enqueue(Request("a")).Should().BeFalse("it is running");
        _queue.Enqueue(Request("b")).Should().BeFalse("it is queued");

        _runner.Complete("a", RunStatus.Completed);
        await _runner.Started("b").WaitAsync(Timeout);
        _queue.Enqueue(Request("a")).Should().BeTrue("it has finished");
        _queue.CancelAll();
        _runner.Complete("b", RunStatus.Completed);
        await _queue.WhenIdleAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task Canceling_a_queued_job_removes_it_without_running_or_logging()
    {
        _queue.Enqueue(Request("a"));
        _queue.Enqueue(Request("b"));
        await _runner.Started("a").WaitAsync(Timeout);

        _queue.Cancel("b").Should().BeTrue();
        _runner.Complete("a", RunStatus.Completed);
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        States("b").Should().Equal("Queued", "Removed");
        _runner.HasStarted("b").Should().BeFalse();
        File.Exists(_tmp.PathOf("b.jsonl")).Should().BeFalse();
        _queue.Cancel("b").Should().BeFalse("nothing to cancel any more");
    }

    [Fact]
    public async Task Canceling_the_running_job_cancels_its_token()
    {
        _queue.Enqueue(Request("a"));
        await _runner.Started("a").WaitAsync(Timeout);

        _queue.Cancel("a").Should().BeTrue();
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        _updates.Last(u => u.PlanId == "a").Result!.Status.Should().Be(RunStatus.Canceled);
    }

    [Fact]
    public async Task Result_is_appended_to_the_log_of_that_plan()
    {
        _queue.Enqueue(Request("a"));
        await _runner.Started("a").WaitAsync(Timeout);
        _runner.Complete("a", RunStatus.CompletedWithWarnings);
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        new RunLog(_tmp.PathOf("a.jsonl")).ReadAll().Should().ContainSingle()
            .Which.Status.Should().Be(RunStatus.CompletedWithWarnings);
    }

    [Fact]
    public async Task Progress_is_forwarded_while_running()
    {
        _queue.Enqueue(Request("a"));
        await _runner.Started("a").WaitAsync(Timeout);

        _runner.ReportProgress("a", new BackupProgress(BackupPhase.Copying, 1, 2, 10, 20, "x"));
        _runner.Complete("a", RunStatus.Completed);
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        var progress = _updates.Single(u => u.Progress is not null);
        progress.State.Should().Be(JobState.Running);
        progress.Progress!.Value.BytesDone.Should().Be(10);
    }

    [Fact]
    public async Task A_runner_that_throws_is_reported_as_Error_and_the_queue_continues()
    {
        _queue.Enqueue(Request("a"));
        _queue.Enqueue(Request("b"));
        await _runner.Started("a").WaitAsync(Timeout);

        _runner.Fail("a", new InvalidOperationException("boom"));
        await _runner.Started("b").WaitAsync(Timeout);
        _runner.Complete("b", RunStatus.Completed);
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        var failed = _updates.Last(u => u.PlanId == "a").Result!;
        failed.Status.Should().Be(RunStatus.Error);
        failed.Reason.Should().Be("boom");
        States("b").Should().Equal("Queued", "Running", "Finished");
    }

    [Fact]
    public async Task WhenIdleAsync_completes_immediately_when_nothing_is_queued()
    {
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        _queue.IsBusy.Should().BeFalse();
        _queue.RunningPlanId.Should().BeNull();
    }

    [Fact]
    public async Task A_handler_that_throws_does_not_stop_the_queue()
    {
        var goodUpdates = new ConcurrentQueue<BackupJobUpdate>();
        _queue.Changed += u => throw new InvalidOperationException("handler fails");
        _queue.Changed += goodUpdates.Enqueue;

        _queue.Enqueue(Request("a"));
        _queue.Enqueue(Request("b"));
        await _runner.Started("a").WaitAsync(Timeout);

        _runner.Complete("a", RunStatus.Completed);
        await _runner.Started("b").WaitAsync(Timeout);
        _runner.Complete("b", RunStatus.Completed);
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        _queue.IsBusy.Should().BeFalse();
        goodUpdates.Where(u => u.PlanId == "a").Select(u => u.State.ToString()).Should()
            .Equal("Queued", "Running", "Finished");
        goodUpdates.Where(u => u.PlanId == "b").Select(u => u.State.ToString()).Should()
            .Equal("Queued", "Running", "Finished");
    }

    [Fact]
    public async Task A_logForPlan_that_throws_InvalidOperationException_does_not_stop_the_queue()
    {
        var logCallCount = 0;
        var queue = new BackupQueue(_runner, planId =>
        {
            logCallCount++;
            if (planId == "a")
                throw new InvalidOperationException("log fails");
            return new RunLog(_tmp.PathOf($"{planId}.jsonl"));
        });
        queue.Changed += _updates.Enqueue;

        queue.Enqueue(Request("a"));
        queue.Enqueue(Request("b"));
        await _runner.Started("a").WaitAsync(Timeout);

        _runner.Complete("a", RunStatus.Completed);
        await _runner.Started("b").WaitAsync(Timeout);
        _runner.Complete("b", RunStatus.Completed);
        await queue.WhenIdleAsync().WaitAsync(Timeout);

        queue.IsBusy.Should().BeFalse();
        logCallCount.Should().Be(2);
        States("b").Should().Equal("Queued", "Running", "Finished");
    }

    [Fact]
    public async Task Worker_restarts_after_idle()
    {
        _queue.Enqueue(Request("a"));
        await _runner.Started("a").WaitAsync(Timeout);
        _runner.Complete("a", RunStatus.Completed);
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        _queue.IsBusy.Should().BeFalse();

        _queue.Enqueue(Request("b"));
        await _runner.Started("b").WaitAsync(Timeout);
        _runner.Complete("b", RunStatus.Completed);
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        States("b").Should().Equal("Queued", "Running", "Finished");
        _queue.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task CancelAll_removes_queued_jobs_and_cancels_running_job()
    {
        _queue.Enqueue(Request("a"));
        _queue.Enqueue(Request("b"));
        _queue.Enqueue(Request("c"));
        await _runner.Started("a").WaitAsync(Timeout);

        _queue.CancelAll();
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        _queue.IsBusy.Should().BeFalse();
        States("a").Should().Equal("Queued", "Running", "Finished");
        States("b").Should().Equal("Queued", "Removed");
        States("c").Should().Equal("Queued", "Removed");
        _updates.Last(u => u.PlanId == "a").Result!.Status.Should().Be(RunStatus.Canceled);
    }

    [Fact]
    public async Task A_runner_that_throws_OperationCanceledException_is_reported_as_Canceled_with_null_Reason()
    {
        _queue.Enqueue(Request("a"));
        await _runner.Started("a").WaitAsync(Timeout);

        _runner.Fail("a", new OperationCanceledException());
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        var result = _updates.Last(u => u.PlanId == "a").Result!;
        result.Status.Should().Be(RunStatus.Canceled);
        result.Reason.Should().BeNull();
    }

    [Fact]
    public async Task Events_of_one_plan_stay_well_formed_when_Enqueue_and_Cancel_race_the_worker()
    {
        var sequence = new List<JobState>();
        var seqLock = new object();
        var queue = new BackupQueue(new ImmediateRunner(), planId => new RunLog(_tmp.PathOf($"{planId}.jsonl")));
        queue.Changed += u =>
        {
            if (u.PlanId != "a" || u.Progress is not null)
                return;
            lock (seqLock)
                sequence.Add(u.State);
        };

        using var stop = new CancellationTokenSource();
        var racer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                queue.Enqueue(Request("a"));
                queue.Cancel("a");
            }
        });

        for (var i = 0; i < 300; i++)
        {
            queue.Enqueue(Request("a"));
            if (i % 50 == 0)
                await Task.Yield();
        }

        stop.Cancel();
        await racer.WaitAsync(Timeout);
        await queue.WhenIdleAsync().WaitAsync(Timeout);

        JobState[] events;
        lock (seqLock)
            events = [.. sequence];
        events.Should().NotBeEmpty();

        JobState? last = null;
        foreach (var state in events)
        {
            var ok = state switch
            {
                JobState.Queued => last is null or JobState.Finished or JobState.Removed,
                JobState.Running => last == JobState.Queued,
                JobState.Finished => last == JobState.Running,
                JobState.Removed => last == JobState.Queued,
                _ => false,
            };
            ok.Should().BeTrue($"{state} must not follow {last?.ToString() ?? "nothing"}");
            last = state;
        }
        last.Should().BeOneOf(JobState.Finished, JobState.Removed);
    }

    [Fact]
    public async Task WhenIdleAsync_does_not_complete_before_a_job_enqueued_meanwhile_has_finished()
    {
        var gate = new ManualResetEventSlim();
        var queue = new BackupQueue(_runner, planId =>
        {
            if (planId == "a")
                gate.Wait(Timeout);
            return new RunLog(_tmp.PathOf($"{planId}.jsonl"));
        });
        queue.Changed += _updates.Enqueue;

        queue.Enqueue(Request("a"));
        await _runner.Started("a").WaitAsync(Timeout);
        var idle = queue.WhenIdleAsync();

        _runner.Complete("a", RunStatus.Completed);
        queue.Enqueue(Request("b")).Should().BeTrue();
        gate.Set();
        await _runner.Started("b").WaitAsync(Timeout);
        idle.IsCompleted.Should().BeFalse("b is still running");

        _runner.Complete("b", RunStatus.Completed);
        await idle.WaitAsync(Timeout);

        States("b").Should().Equal("Queued", "Running", "Finished");
        queue.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task WhenIdleAsync_completes_after_CancelAll_with_only_a_running_job()
    {
        _queue.Enqueue(Request("a"));
        await _runner.Started("a").WaitAsync(Timeout);
        var idle = _queue.WhenIdleAsync();

        _queue.CancelAll();
        await idle.WaitAsync(Timeout);

        _queue.IsBusy.Should().BeFalse();
        _updates.Last(u => u.PlanId == "a").Result!.Status.Should().Be(RunStatus.Canceled);
    }

    [Fact]
    public async Task Cancel_and_CancelAll_do_not_run_token_callbacks_while_holding_the_queue_lock()
    {
        foreach (var useCancelAll in new[] { false, true })
        {
            BackupQueue? queue = null;
            var runner = new CallbackProbeRunner(() => queue!.IsBusy);
            queue = new BackupQueue(runner, planId => new RunLog(_tmp.PathOf($"{planId}.jsonl")));

            queue.Enqueue(Request("a"));
            await runner.Started.WaitAsync(Timeout);
            if (useCancelAll)
                queue.CancelAll();
            else
                queue.Cancel("a").Should().BeTrue();
            await queue.WhenIdleAsync().WaitAsync(Timeout);

            runner.CallbackSawFreeLock.Should().BeTrue($"cancelation (CancelAll: {useCancelAll}) must happen outside the lock");
        }
    }

    private sealed class ImmediateRunner : IBackupRunner
    {
        public Task<RunLogEntry> RunAsync(BackupRequest request, IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RunLogEntry { RunId = request.Plan.Id, Status = RunStatus.Completed });
    }

    /// <summary>On cancellation, checks from another thread whether the queue's lock is free.</summary>
    private sealed class CallbackProbeRunner(Func<bool> probe) : IBackupRunner
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;
        public bool CallbackSawFreeLock { get; private set; }

        public async Task<RunLogEntry> RunAsync(BackupRequest request, IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() =>
            {
                CallbackSawFreeLock = Task.Run(probe).Wait(TimeSpan.FromSeconds(1));
                _done.TrySetResult();
            });
            _started.TrySetResult();
            await _done.Task;
            return new RunLogEntry { RunId = request.Plan.Id, Status = RunStatus.Canceled };
        }
    }

    /// <summary>A runner whose runs finish when the test says so.</summary>
    private sealed class FakeRunner : IBackupRunner
    {
        private readonly ConcurrentDictionary<string, Run> _runs = new();

        private Run For(string planId) => _runs.GetOrAdd(planId, _ => new Run());

        public Task Started(string planId) => For(planId).Started.Task;
        public bool HasStarted(string planId) => For(planId).Started.Task.IsCompleted;

        public void Complete(string planId, RunStatus status) =>
            For(planId).Result.TrySetResult(new RunLogEntry { RunId = planId, Status = status });

        public void Fail(string planId, Exception exception) => For(planId).Result.TrySetException(exception);

        public void ReportProgress(string planId, BackupProgress progress) => For(planId).Progress!.Report(progress);

        public async Task<RunLogEntry> RunAsync(BackupRequest request, IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var run = For(request.Plan.Id);
            run.Progress = progress;
            using var registration = cancellationToken.Register(() =>
                run.Result.TrySetResult(new RunLogEntry { RunId = request.Plan.Id, Status = RunStatus.Canceled }));
            run.Started.TrySetResult();
            var result = await run.Result.Task;
            _runs.TryRemove(request.Plan.Id, out _);
            return result;
        }

        private sealed class Run
        {
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<RunLogEntry> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public IProgress<BackupProgress>? Progress { get; set; }
        }
    }
}
