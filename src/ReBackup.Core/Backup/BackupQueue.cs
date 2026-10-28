using ReBackup.Core.Backup.Retry;
using ReBackup.Core.Plans;
using ReBackup.Shared.Schedule;

namespace ReBackup.Core.Backup;

public enum JobState
{
    Queued,
    Running,
    Finished,
    /// <summary>Canceled while still queued: nothing ran and nothing was logged.</summary>
    Removed,
}

/// <param name="IsRetry">The job retries the skipped files of an earlier run (<see cref="BackupQueue.EnqueueRetry"/>).</param>
/// <param name="Plan">The plan the job runs with, as it was when the job was queued; the queue sets it on every update.</param>
public sealed record BackupJobUpdate(string PlanId, string PlanName, JobState State, BackupProgress? Progress,
    RunLogEntry? Result, bool IsRetry = false, BackupPlan? Plan = null);

/// <summary>
/// Where a waiting job stands: <paramref name="Position"/> 1 starts next. The job ahead is the one before it in the
/// queue, or the running job for position 1 (null when nothing runs yet). The name is the plan's name when it was queued.
/// </summary>
public sealed record QueuePosition(int Position, string? AheadPlanId, string? AheadPlanName);

/// <summary>
/// Runs backups one at a time. A plan can be queued or running only once, except that its regular job may wait behind
/// its running retry. Retries wait in a low-priority lane: one starts only when no regular job waits, and a running
/// retry is never canceled because a regular job arrives.
/// </summary>
public sealed class BackupQueue
{
    private readonly IBackupRunner _runner;
    private readonly Func<string, RunLog> _logForPlan;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<Job> _queued = [];
    private readonly List<Job> _lowQueued = [];   // waiting retries; they start only when _queued is empty
    private Job? _running;
    private bool _workerActive;
    private bool _closed;
    private Task _worker = Task.CompletedTask;

    public BackupQueue(IBackupRunner runner, Func<string, RunLog> logForPlan, TimeProvider? timeProvider = null)
    {
        _runner = runner;
        _logForPlan = logForPlan;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Per job: Queued, Running (repeated with progress), then Finished or Removed. Queued is raised on the enqueuing
    /// thread, Removed on the canceling thread, Running and Finished on the worker. Queued, Removed, the first Running
    /// (without progress) and Finished are raised while the queue's lock is held, so the events of one plan are always
    /// ordered across its jobs. Handlers
    /// must therefore not block or call back into the queue; an exception thrown by a handler is swallowed.
    /// The jobs of one plan follow each other, except that a regular job can be queued while the plan's retry runs: its
    /// Queued then comes between the retry's Running and Finished. <see cref="BackupJobUpdate.IsRetry"/> tells them apart.
    /// </summary>
    public event Action<BackupJobUpdate>? Changed;

    /// <summary>
    /// The clock the queue stamps the runs it logs itself with (a runner that failed outright); the app gives the runner
    /// the same one, so that times taken from it compare with every logged run's start.
    /// </summary>
    public TimeProvider Clock => _time;

    public bool IsBusy
    {
        get { lock (_gate) return _running is not null || _queued.Count > 0 || _lowQueued.Count > 0; }
    }

    /// <summary>The waiting jobs, waiting retries included.</summary>
    public int QueuedCount
    {
        get { lock (_gate) return _queued.Count + _lowQueued.Count; }
    }

    /// <summary>
    /// A regular job is queued or running. Waiting and running retries do not count: they are tried again after the next
    /// start, so nothing the user asked for is lost when they are canceled.
    /// </summary>
    public bool HasRegularJobs
    {
        get { lock (_gate) return _queued.Count > 0 || _running is { IsRetry: false }; }
    }

    public string? RunningPlanId
    {
        get { lock (_gate) return _running?.PlanId; }
    }

    /// <summary>True while a retry of that plan runs.</summary>
    public bool IsRetryRunning(string planId)
    {
        lock (_gate) return _running is { IsRetry: true } running && running.PlanId == planId;
    }

    /// <summary>
    /// A snapshot of the waiting plans, the next one to run first, waiting retries after the regular jobs; the running
    /// plan is not part of it (unless its regular job waits behind its running retry).
    /// </summary>
    public IReadOnlyList<string> QueuedPlanIds
    {
        get { lock (_gate) return Waiting().Select(q => q.PlanId).ToList(); }
    }

    /// <summary>
    /// Where the waiting job of that plan stands, waiting retries counted after the regular jobs; null when the plan is
    /// not waiting (running or not queued).
    /// </summary>
    public QueuePosition? PositionOf(string planId)
    {
        lock (_gate)
        {
            var waiting = Waiting().ToList();
            var index = waiting.FindIndex(q => q.PlanId == planId);
            if (index < 0)
                return null;
            var ahead = index > 0 ? waiting[index - 1] : _running;
            return new QueuePosition(index + 1, ahead?.PlanId, ahead?.PlanName);
        }
    }

    /// <summary>True after <see cref="Close"/>: no new jobs are accepted.</summary>
    public bool IsClosed
    {
        get { lock (_gate) return _closed; }
    }

    /// <summary>
    /// False when a regular job of that plan is already queued or running, or the queue is closed. A waiting retry of
    /// the plan is removed (the regular run supersedes it); behind a running retry of the plan the job is queued normally.
    /// </summary>
    public bool Enqueue(BackupRequest request)
    {
        var job = new Job(request);
        Job? superseded;
        lock (_gate)
        {
            if (_closed
                || _running is { IsRetry: false } running && running.PlanId == job.PlanId
                || _queued.Any(q => q.PlanId == job.PlanId))
            {
                job.Cancellation.Dispose();
                return false;
            }

            superseded = TryRemove(_lowQueued, job.PlanId);
            if (superseded is not null)
                Raise(superseded, JobState.Removed);
            Add(_queued, job);
        }
        superseded?.Cancellation.Dispose();
        return true;
    }

    /// <summary>
    /// Queues a retry in the low-priority lane. False when any job of that plan is queued or running, or the queue is
    /// closed.
    /// </summary>
    public bool EnqueueRetry(RetryRequest request)
    {
        var job = new Job(request);
        lock (_gate)
        {
            if (_closed
                || _running?.PlanId == job.PlanId
                || Waiting().Any(q => q.PlanId == job.PlanId))
            {
                job.Cancellation.Dispose();
                return false;
            }
            Add(_lowQueued, job);
        }
        return true;
    }

    /// <summary>
    /// Cancels the running job of that plan or removes its queued job, its regular job before its retry. False when
    /// there is neither.
    /// </summary>
    public bool Cancel(string planId)
    {
        Job? running = null;
        Job? removed = null;
        lock (_gate)
        {
            if (_running is { IsRetry: false } regular && regular.PlanId == planId)
                running = regular;
            else if (TryRemove(_queued, planId) is { } queued)
                removed = queued;
            else if (_running is { IsRetry: true } retry && retry.PlanId == planId)
                running = retry;
            else if (TryRemove(_lowQueued, planId) is { } lowQueued)
                removed = lowQueued;
            else
                return false;

            if (removed is not null)
                Raise(removed, JobState.Removed);
        }

        removed?.Cancellation.Dispose();
        if (running is not null)
            CancelToken(running);
        return true;
    }

    public void CancelAll()
    {
        List<Job> removed;
        Job? running;
        lock (_gate)
        {
            removed = [.. Waiting()];
            _queued.Clear();
            _lowQueued.Clear();
            running = _running;
            foreach (var job in removed)
                Raise(job, JobState.Removed);
        }

        foreach (var job in removed)
            job.Cancellation.Dispose();
        if (running is not null)
            CancelToken(running);
    }

    /// <summary>Cancels everything and refuses new jobs from now on. Used when the app exits or restarts.</summary>
    public void Close()
    {
        lock (_gate)
            _closed = true;
        CancelAll();
    }

    /// <summary>Completes when nothing is queued or running. Never faults.</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task worker;
            lock (_gate)
                worker = _worker;

            try { await worker.ConfigureAwait(false); }
            catch (Exception) { }

            lock (_gate)
            {
                if (_queued.Count == 0 && _lowQueued.Count == 0 && _running is null && !_workerActive)
                    return;
                // A new worker may have been started meanwhile; if the old one is still winding down, let it.
                worker = _worker;
            }
            if (worker.IsCompleted)
                await Task.Delay(1).ConfigureAwait(false);
        }
    }

    /// <summary>The waiting jobs in the order they start: the regular ones, then the retries. Call under the lock.</summary>
    private IEnumerable<Job> Waiting() => _queued.Concat(_lowQueued);

    /// <summary>Adds a job to one of the lanes and makes sure the worker runs. Call under the lock.</summary>
    private void Add(List<Job> lane, Job job)
    {
        lane.Add(job);

        // Raised inside the lock so that the worker cannot report Running before Queued.
        Raise(job, JobState.Queued);

        if (!_workerActive)
        {
            _worker = Task.Run(ProcessAsync);
            _workerActive = true;
        }
    }

    /// <summary>Removes and returns the job of that plan from the lane; null when it has none. Call under the lock.</summary>
    private static Job? TryRemove(List<Job> lane, string planId)
    {
        var index = lane.FindIndex(q => q.PlanId == planId);
        if (index < 0)
            return null;
        var job = lane[index];
        lane.RemoveAt(index);
        return job;
    }

    /// <summary>The worker may have disposed the source in the meantime.</summary>
    private static void CancelToken(Job job)
    {
        try { job.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            Job job;
            lock (_gate)
            {
                // A retry starts only when no regular job waits.
                var lane = _queued.Count > 0 ? _queued : _lowQueued;
                if (lane.Count == 0)
                {
                    _workerActive = false;
                    return;
                }
                job = lane[0];
                lane.RemoveAt(0);
                _running = job;

                // Raised in the critical section that makes the job the running one: a regular job of the same plan
                // may be queued behind a running retry, and its Queued must not come before the retry's Running.
                Raise(job, JobState.Running);
            }

            RunLogEntry? result = null;
            try
            {
                result = await RunAsync(job);

                try
                {
                    // A retry whose version no longer exists did nothing worth a line.
                    if (!result.VersionGone)
                        _logForPlan(job.PlanId).Append(result);
                }
                catch (Exception)
                {
                    // The run itself is over; a log that cannot be written must not stop the queue.
                }
            }
            catch (Exception)
            {
                // Unexpected: fall through so that the state is cleaned up and the worker carries on.
            }
            finally
            {
                // Clearing _running and raising Finished in one critical section keeps a job of the same plan that
                // is accepted only once this one has ended from raising Queued before Finished. (A regular job
                // queued behind a running retry was accepted earlier; its Queued precedes the retry's Finished.)
                lock (_gate)
                {
                    _running = null;
                    if (result is not null)
                        Raise(job, JobState.Finished, result: result);
                }
                job.Cancellation.Dispose();
            }
        }
    }

    private async Task<RunLogEntry> RunAsync(Job job)
    {
        try
        {
            var progress = new JobProgress(this, job);
            return job.Retry is { } retry
                ? await _runner.RetryAsync(retry, progress, job.Cancellation.Token)
                : await _runner.RunAsync(job.Request!, progress, job.Cancellation.Token);
        }
        catch (Exception ex)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            return new RunLogEntry
            {
                RunId = Guid.NewGuid().ToString("N"),
                Trigger = job.Retry is null ? job.Request!.Trigger : RunTrigger.Retry,
                StartUtc = now,
                EndUtc = now,
                Status = ex is OperationCanceledException ? RunStatus.Canceled : RunStatus.Error,
                Reason = ex is OperationCanceledException ? null : ex.Message,
                RetryOf = job.Retry?.RunId,
                Attempt = job.Retry?.Attempt,
                Attempts = job.Retry?.Attempts,
            };
        }
    }

    private void Raise(Job job, JobState state, BackupProgress? progress = null, RunLogEntry? result = null)
    {
        var update = new BackupJobUpdate(job.PlanId, job.PlanName, state, progress, result, job.IsRetry, job.Plan);
        if (Changed is null)
            return;

        foreach (Action<BackupJobUpdate> handler in Changed.GetInvocationList().Cast<Action<BackupJobUpdate>>())
        {
            try { handler(update); }
            catch { }
        }
    }

    /// <summary>A regular run (<see cref="Request"/>) or a retry (<see cref="Retry"/>); exactly one of them is set.</summary>
    private sealed class Job
    {
        private readonly BackupPlan _plan;

        public Job(BackupRequest request)
        {
            Request = request;
            _plan = request.Plan;
        }

        public Job(RetryRequest retry)
        {
            Retry = retry;
            _plan = retry.Plan;
        }

        public BackupRequest? Request { get; }
        public RetryRequest? Retry { get; }
        public bool IsRetry => Retry is not null;
        public BackupPlan Plan => _plan;
        public string PlanId => _plan.Id;
        public string PlanName => _plan.Name;
        public CancellationTokenSource Cancellation { get; } = new();
    }

    /// <summary>Forwards progress synchronously, so updates keep their order.</summary>
    private sealed class JobProgress(BackupQueue queue, Job job) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => queue.Raise(job, JobState.Running, value);
    }
}
