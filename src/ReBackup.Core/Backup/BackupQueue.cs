namespace ReBackup.Core.Backup;

public enum JobState
{
    Queued,
    Running,
    Finished,
    /// <summary>Canceled while still queued: nothing ran and nothing was logged.</summary>
    Removed,
}

public sealed record BackupJobUpdate(string PlanId, string PlanName, JobState State, BackupProgress? Progress,
    RunLogEntry? Result);

/// <summary>Runs backups one at a time. A plan can be queued or running only once.</summary>
public sealed class BackupQueue
{
    private readonly IBackupRunner _runner;
    private readonly Func<string, RunLog> _logForPlan;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<Job> _queued = [];
    private Job? _running;
    private bool _workerActive;
    private Task _worker = Task.CompletedTask;

    public BackupQueue(IBackupRunner runner, Func<string, RunLog> logForPlan, TimeProvider? timeProvider = null)
    {
        _runner = runner;
        _logForPlan = logForPlan;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Per job: Queued, Running (repeated with progress), then Finished or Removed. Queued is raised on the enqueuing
    /// thread, Removed on the canceling thread, Running and Finished on the worker. Queued, Removed and Finished are
    /// raised while the queue's lock is held, so the events of one plan are always ordered across its jobs. Handlers
    /// must therefore not block or call back into the queue; an exception thrown by a handler is swallowed.
    /// </summary>
    public event Action<BackupJobUpdate>? Changed;

    public bool IsBusy
    {
        get { lock (_gate) return _running is not null || _queued.Count > 0; }
    }

    public int QueuedCount
    {
        get { lock (_gate) return _queued.Count; }
    }

    public string? RunningPlanId
    {
        get { lock (_gate) return _running?.PlanId; }
    }

    /// <summary>False when that plan is already queued or running.</summary>
    public bool Enqueue(BackupRequest request)
    {
        var job = new Job(request);
        lock (_gate)
        {
            if (_running?.PlanId == job.PlanId || _queued.Any(q => q.PlanId == job.PlanId))
            {
                job.Cancellation.Dispose();
                return false;
            }
            _queued.Add(job);

            // Raised inside the lock so that the worker cannot report Running before Queued.
            Raise(job, JobState.Queued);

            if (!_workerActive)
            {
                _worker = Task.Run(ProcessAsync);
                _workerActive = true;
            }
        }
        return true;
    }

    /// <summary>Cancels the running job of that plan or removes its queued job. False when there is neither.</summary>
    public bool Cancel(string planId)
    {
        Job? running = null;
        Job? removed = null;
        lock (_gate)
        {
            if (_running?.PlanId == planId)
            {
                running = _running;
            }
            else
            {
                var index = _queued.FindIndex(q => q.PlanId == planId);
                if (index < 0)
                    return false;
                removed = _queued[index];
                _queued.RemoveAt(index);
                Raise(removed, JobState.Removed);
            }
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
            removed = [.. _queued];
            _queued.Clear();
            running = _running;
            foreach (var job in removed)
                Raise(job, JobState.Removed);
        }

        foreach (var job in removed)
            job.Cancellation.Dispose();
        if (running is not null)
            CancelToken(running);
    }

    /// <summary>Completes when nothing is queued or running. Never faults.</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task worker;
            lock (_gate)
                worker = _worker;

            try { await worker; }
            catch (Exception) { }

            lock (_gate)
            {
                if (_queued.Count == 0 && _running is null && !_workerActive)
                    return;
                // A new worker may have been started meanwhile; if the old one is still winding down, let it.
                worker = _worker;
            }
            if (worker.IsCompleted)
                await Task.Yield();
        }
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
                if (_queued.Count == 0)
                {
                    _workerActive = false;
                    return;
                }
                job = _queued[0];
                _queued.RemoveAt(0);
                _running = job;
            }

            RunLogEntry? result = null;
            try
            {
                Raise(job, JobState.Running);
                result = await RunAsync(job);

                try
                {
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
                // Clearing _running and raising Finished in one critical section keeps a concurrent Enqueue of
                // the same plan from raising Queued before Finished.
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
            return await _runner.RunAsync(job.Request, progress, job.Cancellation.Token);
        }
        catch (Exception ex)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            return new RunLogEntry
            {
                RunId = Guid.NewGuid().ToString("N"),
                Trigger = job.Request.Trigger,
                StartUtc = now,
                EndUtc = now,
                Status = ex is OperationCanceledException ? RunStatus.Canceled : RunStatus.Error,
                Reason = ex is OperationCanceledException ? null : ex.Message,
            };
        }
    }

    private void Raise(Job job, JobState state, BackupProgress? progress = null, RunLogEntry? result = null)
    {
        var update = new BackupJobUpdate(job.PlanId, job.Request.Plan.Name, state, progress, result);
        if (Changed is null)
            return;

        foreach (Action<BackupJobUpdate> handler in Changed.GetInvocationList().Cast<Action<BackupJobUpdate>>())
        {
            try { handler(update); }
            catch { }
        }
    }

    private sealed class Job(BackupRequest request)
    {
        public BackupRequest Request { get; } = request;
        public string PlanId => Request.Plan.Id;
        public CancellationTokenSource Cancellation { get; } = new();
    }

    /// <summary>Forwards progress synchronously, so updates keep their order.</summary>
    private sealed class JobProgress(BackupQueue queue, Job job) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => queue.Raise(job, JobState.Running, value);
    }
}
