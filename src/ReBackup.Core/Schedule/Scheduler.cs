using System.Text.Json;
using ReBackup.Core.Backup;
using ReBackup.Core.Json;

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
    private readonly Dictionary<string, string> _plansSerialized = new(StringComparer.OrdinalIgnoreCase);

    // Per plan: the instant up to which its triggers have been handled.
    private readonly Dictionary<string, DateTime> _checkedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _catchUps = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _catchUpAtUtc;
    private ITimer? _timer;
    private bool _paused;
    private bool _stopped;
    private bool _started;

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
            var due = new List<(string PlanId, RunTrigger Trigger)>();
            lock (_gate)
            {
                if (_paused == value)
                    return;

                var now = UtcNow;
                if (value)
                {
                    // PAUSING: handle already-due runs only if scheduler is running
                    if (_started && !_stopped)
                    {
                        // Queue due catch-ups first (like Check does)
                        if (_catchUps.Count > 0 && now >= _catchUpAtUtc)
                        {
                            foreach (var id in _catchUps)
                            {
                                if (_plans.TryGetValue(id, out var plan) && plan.Enabled)
                                    due.Add((id, RunTrigger.CatchUp));
                            }
                            _catchUps.Clear();
                        }

                        // Queue due scheduled runs, but skip plans that got catch-ups
                        foreach (var plan in _plans.Values)
                        {
                            if (!plan.Enabled || due.Exists(d => string.Equals(d.PlanId, plan.Id, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            var since = _checkedUntil.TryGetValue(plan.Id, out var checkedUntil) ? checkedUntil : now;
                            if (now > since && LastDue(plan, since, now) is not null)
                            {
                                due.Add((plan.Id, RunTrigger.Scheduled));
                                // The scheduled run covers the pending catch-up, as in Check
                                _catchUps.Remove(plan.Id);
                            }
                        }
                    }

                    // Set _checkedUntil to max(existing, now) to never go backwards (e.g. after clock set-back)
                    foreach (var id in _plans.Keys)
                    {
                        if (_checkedUntil.TryGetValue(id, out var checkedUntil))
                            _checkedUntil[id] = now > checkedUntil ? now : checkedUntil;
                        else
                            _checkedUntil[id] = now;
                    }
                    _paused = true;
                }
                else
                {
                    // RESUMING: set _checkedUntil to max(existing, now) to not go backwards
                    foreach (var id in _plans.Keys)
                    {
                        if (_checkedUntil.TryGetValue(id, out var checkedUntil))
                            _checkedUntil[id] = now > checkedUntil ? now : checkedUntil;
                        else
                            _checkedUntil[id] = now;
                    }
                    _paused = false;
                }
            }

            // Queue any due runs outside the lock
            foreach (var (planId, trigger) in due)
            {
                try
                {
                    _enqueue(planId, trigger);
                }
                catch (Exception)
                {
                    // enqueue failure must not stop pause
                }
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
            var newIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var plan in list)
            {
                newIds.Add(plan.Id);
                var serialized = JsonSerializer.Serialize(plan.Triggers, JsonDefaults.Options);

                // Check if this plan is new or has changed
                if (!_plans.ContainsKey(plan.Id) ||
                    !_plansSerialized.TryGetValue(plan.Id, out var oldSerialized) ||
                    oldSerialized != serialized ||
                    _plans[plan.Id].Enabled != plan.Enabled)
                {
                    // Plan is new or changed (enabled flag or triggers): start from now
                    _checkedUntil[plan.Id] = now;
                    _plansSerialized[plan.Id] = serialized;
                }

                _plans[plan.Id] = plan;
            }

            // Remove plans that are no longer in the list
            foreach (var id in _plans.Keys.Where(id => !newIds.Contains(id)).ToList())
                _plans.Remove(id);
            foreach (var id in _checkedUntil.Keys.Where(id => !newIds.Contains(id)).ToList())
                _checkedUntil.Remove(id);
            foreach (var id in _plansSerialized.Keys.Where(id => !newIds.Contains(id)).ToList())
                _plansSerialized.Remove(id);
            _catchUps.RemoveWhere(id => !newIds.Contains(id));
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
            if (_started || _stopped)
                throw new InvalidOperationException("The scheduler can be started only once.");
            _started = true;
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
            {
                // Never move _checkedUntil backwards
                if (_checkedUntil.TryGetValue(id, out var checkedUntil))
                    _checkedUntil[id] = now > checkedUntil ? now : checkedUntil;
                else
                    _checkedUntil[id] = now;
            }
            var nextMinute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(1);
            _timer = _time.CreateTimer(_ => SafeCheck(), null, nextMinute + CheckOffset - now, CheckInterval);
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

    private void SafeCheck()
    {
        try
        {
            Check();
        }
        catch (Exception)
        {
            // a failing check must not take down the app; the next one runs a minute later
        }
    }

    private void Check()
    {
        var due = new List<(string PlanId, RunTrigger Trigger)>();
        lock (_gate)
        {
            if (_stopped)
                return;
            var now = UtcNow;

            if (_catchUps.Count > 0 && now >= _catchUpAtUtc)
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

                // Never move _checkedUntil backwards
                if (now > since)
                    _checkedUntil[plan.Id] = now;

                if (_paused || !plan.Enabled || due.Exists(d => string.Equals(d.PlanId, plan.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Only compute LastDue when time has moved forward
                if (now > since && LastDue(plan, since, now) is not null)
                {
                    due.Add((plan.Id, RunTrigger.Scheduled));
                    // Remove from catch-ups so we don't queue a second run
                    _catchUps.Remove(plan.Id);
                }
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
        var handlers = Changed;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
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
