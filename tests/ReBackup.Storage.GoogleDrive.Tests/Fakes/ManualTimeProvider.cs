namespace ReBackup.Storage.GoogleDrive.Tests.Fakes;

/// <summary>A clock that moves only when <see cref="Advance"/> is called; records every timer's due time.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly List<TimeSpan> _dueTimes = [];
    private DateTimeOffset _now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    /// <summary>The due time of every timer created so far, in order.</summary>
    public IReadOnlyList<TimeSpan> DueTimes
    {
        get
        {
            lock (_gate) return _dueTimes.ToList();
        }
    }

    /// <summary>Timers created and neither fired nor disposed.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate) return _timers.Count;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _dueTimes.Add(dueTime);
            timer.DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _now + dueTime;
            _timers.Add(timer);
        }
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.DueAt <= _now).ToList();
            foreach (var timer in due) _timers.Remove(timer);
        }
        foreach (var timer in due) timer.Fire();
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate) _timers.Remove(timer);
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate) DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
