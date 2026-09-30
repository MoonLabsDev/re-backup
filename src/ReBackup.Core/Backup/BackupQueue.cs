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

    /// <summary>Raised on worker threads. Per job: Queued, Running (repeated with progress), then Finished or Removed.</summary>
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
            // Handlers must not block or call back into the queue.
            Raise(job, JobState.Queued);

            if (!_workerActive)
            {
                _workerActive = true;
                _worker = Task.Run(ProcessAsync);
            }
        }
        return true;
    }

    /// <summary>Cancels the running job of that plan or removes its queued job. False when there is neither.</summary>
    public bool Cancel(string planId)
    {
        Job? removed = null;
        lock (_gate)
        {
            if (_running?.PlanId == planId)
            {
                _running.Cancellation.Cancel();
                return true;
            }
            var index = _queued.FindIndex(q => q.PlanId == planId);
            if (index < 0)
                return false;
            removed = _queued[index];
            _queued.RemoveAt(index);
        }

        Raise(removed, JobState.Removed);
        removed.Cancellation.Dispose();
        return true;
    }

    public void CancelAll()
    {
        List<Job> removed;
        lock (_gate)
        {
            removed = [.. _queued];
            _queued.Clear();
            _running?.Cancellation.Cancel();
        }

        foreach (var job in removed)
        {
            Raise(job, JobState.Removed);
            job.Cancellation.Dispose();
        }
    }

    /// <summary>Completes when nothing is queued or running.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
            return _worker;
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

            try
            {
                Raise(job, JobState.Running);
                var result = await RunAsync(job);

                try
                {
                    _logForPlan(job.PlanId).Append(result);
                }
                catch (Exception)
                {
                    // The run itself is over; a log that cannot be written must not stop the queue.
                }

                lock (_gate)
                    _running = null;
                Raise(job, JobState.Finished, result: result);
            }
            catch (Exception)
            {
                // Unexpected exception: ensure state cleanup and continue
                lock (_gate)
                    _running = null;
            }
            finally
            {
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
