using ReBackup.Core.Backup;

namespace ReBackup.Core.Schedule;

/// <summary>What the scheduler needs to know about a saved plan.</summary>
public sealed record ScheduledPlan(string Id, bool Enabled, IReadOnlyList<ScheduleTrigger> Triggers);

/// <summary>
/// Starts scheduled and catch-up runs while the app runs, by checking the triggers once a minute. It does not run
/// backups: it hands the plan id and the kind of run to the enqueue delegate, which must not block.
/// </summary>
public sealed class Scheduler : IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CatchUpDelay = TimeSpan.FromMinutes(1);

    /// <summary>Checks run this long after a full minute, so that a trigger on that minute is never seen a moment too early.</summary>
    private static readonly TimeSpan CheckOffset = TimeSpan.FromSeconds(1);

    private readonly Action<string, RunTrigger> _enqueue;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, ScheduledPlan> _plans = new(StringComparer.OrdinalIgnoreCase);

    // Per plan: the instant up to which its triggers have been handled.
    private readonly Dictionary<string, DateTime> _checkedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _catchUps = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _catchUpAtUtc;
    private ITimer? _timer;
    private bool _paused;
    private bool _stopped;

    public Scheduler(Action<string, RunTrigger> enqueue, TimeProvider? timeProvider = null)
    {
        _enqueue = enqueue;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised after every check and whenever the plans or the pause state change; on the thread that caused it.</summary>
    public event Action? Changed;

    /// <summary>A paused scheduler queues nothing; triggers that pass meanwhile are skipped. Not saved.</summary>
    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
        set
        {
            lock (_gate)
            {
                if (_paused == value)
                    return;
                _paused = value;
                var now = UtcNow;
                foreach (var id in _plans.Keys)
                    _checkedUntil[id] = now;
            }
            RaiseChanged();
        }
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>Replaces the known plans (their saved state). A plan seen for the first time is only run for triggers from now on.</summary>
    public void UpdatePlans(IEnumerable<ScheduledPlan> plans)
    {
        var list = plans.ToList();
        lock (_gate)
        {
            var now = UtcNow;
            _plans.Clear();
            foreach (var plan in list)
            {
                _plans[plan.Id] = plan;
                _checkedUntil.TryAdd(plan.Id, now);
            }
            foreach (var id in _checkedUntil.Keys.Where(id => !_plans.ContainsKey(id)).ToList())
                _checkedUntil.Remove(id);
            _catchUps.RemoveWhere(id => !_plans.ContainsKey(id));
        }
        RaiseChanged();
    }

    /// <summary>
    /// Plans one catch-up run, <see cref="CatchUpDelay"/> from now, for every enabled plan that missed a trigger
    /// since the start of its last run, and starts the checks.
    /// </summary>
    /// <param name="lastRunStartUtc">Start of a plan's last logged run; null when it never ran (it is then not caught up).</param>
    /// <exception cref="InvalidOperationException">The scheduler was started or disposed before.</exception>
    public void Start(Func<string, DateTime?> lastRunStartUtc)
    {
        List<ScheduledPlan> plans;
        lock (_gate)
        {
            if (_timer is not null || _stopped)
                throw new InvalidOperationException("The scheduler can be started only once.");
            plans = [.. _plans.Values];
        }

        var now = UtcNow;
        var missed = new List<string>();
        foreach (var plan in plans.Where(p => p.Enabled && p.Triggers.Count > 0))
        {
            DateTime? lastRun;
            try
            {
                lastRun = lastRunStartUtc(plan.Id);
            }
            catch (Exception)
            {
                continue;   // an unreadable log: no catch-up rather than a guess
            }
            if (lastRun is { } since && LastDue(plan, since, now) is not null)
                missed.Add(plan.Id);
        }

        lock (_gate)
        {
            foreach (var id in missed)
                _catchUps.Add(id);
            _catchUpAtUtc = now + CatchUpDelay;
            foreach (var id in _plans.Keys)
                _checkedUntil[id] = now;
            var nextMinute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(1);
            _timer = _time.CreateTimer(_ => Check(), null, nextMinute + CheckOffset - now, CheckInterval);
        }
        RaiseChanged();
    }

    /// <summary>
    /// When the plan's triggers start it next; null when it is unknown, disabled, has no (valid) triggers. The value
    /// does not take the pause into account.
    /// </summary>
    public DateTime? NextRunUtc(string planId)
    {
        ScheduledPlan? plan;
        DateTime after;
        lock (_gate)
        {
            if (!_plans.TryGetValue(planId, out plan) || !plan.Enabled || plan.Triggers.Count == 0)
                return null;
            after = _checkedUntil.TryGetValue(planId, out var checkedUntil) ? checkedUntil : UtcNow;
        }

        try
        {
            foreach (var run in ScheduleCalculator.NextRuns(plan.Triggers, after, _time.LocalTimeZone))
                return run;
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        ITimer? timer;
        lock (_gate)
        {
            _stopped = true;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
    }

    private void Check()
    {
        var due = new List<(string PlanId, RunTrigger Trigger)>();
        lock (_gate)
        {
            if (_stopped)
                return;
            var now = UtcNow;

            if (_catchUps.Count > 0 && now > _catchUpAtUtc)
            {
                if (!_paused)
                {
                    foreach (var id in _catchUps)
                    {
                        if (_plans.TryGetValue(id, out var plan) && plan.Enabled)
                            due.Add((id, RunTrigger.CatchUp));
                    }
                }
                _catchUps.Clear();
            }

            foreach (var plan in _plans.Values)
            {
                var since = _checkedUntil.TryGetValue(plan.Id, out var checkedUntil) ? checkedUntil : now;
                _checkedUntil[plan.Id] = now;
                if (_paused || !plan.Enabled || due.Exists(d => string.Equals(d.PlanId, plan.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (LastDue(plan, since, now) is not null)
                    due.Add((plan.Id, RunTrigger.Scheduled));
            }
        }

        foreach (var (planId, trigger) in due)
        {
            try
            {
                _enqueue(planId, trigger);
            }
            catch (Exception)
            {
                // The next plan must still be started.
            }
        }
        RaiseChanged();
    }

    private DateTime? LastDue(ScheduledPlan plan, DateTime sinceUtc, DateTime nowUtc)
    {
        if (plan.Triggers.Count == 0)
            return null;
        try
        {
            return ScheduleCalculator.LastDue(plan.Triggers, sinceUtc, nowUtc, _time.LocalTimeZone);
        }
        catch (ArgumentException)
        {
            return null;   // a plan with a broken trigger (edited by hand) is never started by the schedule
        }
    }

    private void RaiseChanged()
    {
        if (Changed is null)
            return;
        foreach (var handler in Changed.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception)
            {
                // A failing listener must not stop the scheduler.
            }
        }
    }
}
