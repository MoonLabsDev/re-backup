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
            List<(string PlanId, RunTrigger Trigger)> due = [];
            lock (_gate)
            {
                if (_paused == value)
                    return;

                var now = UtcNow;
                // Pausing still queues what was due before it (a check may just not have come yet), except before Start
                // and after Dispose; resuming skips what passed meanwhile.
                if (value && _started && !_stopped)
                    due = CollectDue(now);
                else
                    AdvanceCheckedUntil(now);
                _paused = value;
            }

            Enqueue(due);
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
            AdvanceCheckedUntil(now);
            // One-shot, re-armed after every check: a periodic timer drifts, and keeps its phase after a sleep or a
            // clock change, so its checks would no longer come right after the full minute.
            _timer = _time.CreateTimer(_ => SafeCheck(), null, DelayToNextCheck(now), Timeout.InfiniteTimeSpan);
        }
        RaiseChanged();
    }

    /// <summary>From <paramref name="nowUtc"/> to the next check time, the first hh:mm plus <see cref="CheckOffset"/> after it.</summary>
    private static TimeSpan DelayToNextCheck(DateTime nowUtc)
    {
        var sinceLastCheckTime = nowUtc - CheckOffset;
        var lastMinute = new DateTime(sinceLastCheckTime.Ticks - sinceLastCheckTime.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        return lastMinute + CheckInterval + CheckOffset - nowUtc;
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
        finally
        {
            ArmNextCheck();
        }
    }

    /// <summary>Sets the timer to the next check time, computed from the current time; never after Dispose.</summary>
    private void ArmNextCheck()
    {
        lock (_gate)
        {
            if (_stopped || _timer is not { } timer)
                return;
            try
            {
                timer.Change(DelayToNextCheck(UtcNow), Timeout.InfiniteTimeSpan);
            }
            catch (Exception)
            {
                // Nothing better to do; the check thread must not die.
            }
        }
    }

    private void Check()
    {
        List<(string PlanId, RunTrigger Trigger)> due;
        lock (_gate)
        {
            if (_stopped)
                return;
            due = CollectDue(UtcNow);
        }

        Enqueue(due);
        RaiseChanged();
    }

    /// <summary>
    /// Under the lock: the runs due at <paramref name="now"/> — due catch-ups (dropped when paused), and a Scheduled run
    /// for every enabled plan with a trigger in (checked-until, now], which covers that plan's catch-up — and then moves
    /// every plan's checked-until to <paramref name="now"/>, never backwards.
    /// </summary>
    private List<(string PlanId, RunTrigger Trigger)> CollectDue(DateTime now)
    {
        var due = new List<(string PlanId, RunTrigger Trigger)>();
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

        if (!_paused)
        {
            foreach (var plan in _plans.Values)
            {
                if (!plan.Enabled || due.Exists(d => string.Equals(d.PlanId, plan.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Only when time moved forward: after the clock was set back, triggers already handled must not run again.
                var since = _checkedUntil.TryGetValue(plan.Id, out var checkedUntil) ? checkedUntil : now;
                if (now > since && LastDue(plan, since, now) is not null)
                {
                    due.Add((plan.Id, RunTrigger.Scheduled));
                    _catchUps.Remove(plan.Id);   // the scheduled run covers the catch-up
                }
            }
        }

        AdvanceCheckedUntil(now);
        return due;
    }

    /// <summary>Under the lock: every plan has been handled up to <paramref name="now"/>, or later if the clock was set back.</summary>
    private void AdvanceCheckedUntil(DateTime now)
    {
        foreach (var id in _plans.Keys)
        {
            if (!_checkedUntil.TryGetValue(id, out var checkedUntil) || now > checkedUntil)
                _checkedUntil[id] = now;
        }
    }

    private void Enqueue(List<(string PlanId, RunTrigger Trigger)> due)
    {
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
