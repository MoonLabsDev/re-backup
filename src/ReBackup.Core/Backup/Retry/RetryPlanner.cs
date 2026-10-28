using System.Text.Json;
using ReBackup.Core.Plans;
using ReBackup.Shared.IO;
using ReBackup.Shared.Json;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.Core.Backup.Retry;

/// <summary>
/// Keeps, per plan, the skipped files of its last regular run that may still be copied, saves them as
/// <c>&lt;directory&gt;\&lt;planId&gt;.json</c>, and hands a snapshot to the due delegate when the next attempt is due. It
/// does not run attempts: the app queues one and reports every run back through <see cref="OnRunStarted"/> and
/// <see cref="OnRunFinished"/>. A handed-out state is in flight until its attempt finishes, it is deferred or dropped;
/// meanwhile it is not handed out again.
/// </summary>
/// <remarks>
/// Called from the UI thread; the timer fires on a timer thread. The due delegate and <see cref="Changed"/> are called
/// outside the lock. After <see cref="Dispose"/> the timer no longer fires, but reported runs still update the saved
/// states, so that an attempt that ends while the app exits is not lost.
/// </remarks>
public sealed class RetryPlanner : IDisposable
{
    /// <summary>How long a canceled or deferred attempt waits before it is due again.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>A state loaded at start is due no earlier than this after the start.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);

    // An overdue state fires on the timer thread right away, never synchronously inside Change; a far one is re-armed daily.
    private static readonly TimeSpan MinTimerDelay = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromDays(1);

    private readonly string _directory;
    private readonly Action<RetryState> _due;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, RetryState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITimer _timer;
    private bool _started;
    private bool _disposed;

    /// <param name="directory">Where the state files are kept (<c>ConfigPaths.RetriesDirectory</c>); created on the first save.</param>
    /// <param name="due">Called on the timer thread with a copy of a state whose attempt is due; must not block.</param>
    /// <param name="timeProvider">The clock and timer; <see cref="TimeProvider.System"/> when null.</param>
    public RetryPlanner(string directory, Action<RetryState> due, TimeProvider? timeProvider = null)
    {
        _directory = Path.GetFullPath(directory);
        _due = due;
        _time = timeProvider ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised with the plan id after every change of a plan's state; on the thread that caused it.</summary>
    public event Action<string>? Changed;

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Loads the saved states and drops (deletes) those whose plan is gone or has retries off, whose source or target
    /// changed, or whose run is no longer the plan's last regular run; a damaged file is dropped too. The kept states are
    /// due no earlier than <see cref="StartDelay"/> from now. Whether the version still exists is checked by the attempt.
    /// </summary>
    /// <param name="planOf">The saved plan with that id; null when there is none.</param>
    /// <param name="lastRegularRunOf">The newest run-log entry of the plan that is not a retry; null when there is none.</param>
    /// <exception cref="InvalidOperationException">The planner was started or disposed before.</exception>
    public void Start(Func<string, BackupPlan?> planOf, Func<string, RunLogEntry?> lastRegularRunOf)
    {
        lock (_gate)
        {
            if (_started || _disposed)
                throw new InvalidOperationException("The retry planner can be started only once.");
            _started = true;
        }

        var now = UtcNow;
        var kept = new List<RetryState>();
        var dropped = new List<string>();
        foreach (var (planId, state) in LoadAll())
        {
            if (state is null || !IsWellFormed(planId, state) || !StillApplies(state, planOf, lastRegularRunOf))
            {
                dropped.Add(planId);
                continue;
            }
            if (state.NextDueUtc < now + StartDelay)
                state.NextDueUtc = now + StartDelay;
            kept.Add(state);
        }

        var changed = new List<string>();
        lock (_gate)
        {
            // A state reported before the start is newer than its file.
            foreach (var planId in dropped.Where(id => !_states.ContainsKey(id)))
            {
                DeleteFile(planId);
                changed.Add(planId);
            }
            foreach (var state in kept.Where(s => !_states.ContainsKey(s.PlanId)))
            {
                _states[state.PlanId] = state;
                changed.Add(state.PlanId);
            }
            Arm();
        }
        foreach (var planId in changed)
            RaiseChanged(planId);
    }

    /// <summary>A run of the plan starts. A regular run drops the plan's state: it makes its own from its own skips.</summary>
    public void OnRunStarted(string planId, bool isRetry)
    {
        if (!isRetry)
            Change(planId, () => Remove(planId));
    }

    /// <summary>
    /// A run of the plan finished. A regular run replaces the plan's state: with a new one (attempt 0, due after the
    /// plan's interval) when retries are on, it made a version and skipped retryable files; with none otherwise. A
    /// retry line of the current state's run updates it: a gone version drops it, a canceled attempt is due again after
    /// <see cref="RetryDelay"/> without counting, any other attempt counts and leaves the files it could not copy,
    /// due after the plan's interval; the state is dropped when no file is left or no attempt is.
    /// A retry line of another run is ignored.
    /// </summary>
    public void OnRunFinished(BackupPlan plan, RunLogEntry entry) =>
        Change(plan.Id, () => entry.Trigger == RunTrigger.Retry ? FinishRetry(plan, entry) : FinishRegular(plan, entry));

    /// <summary>A due state could not be queued: it is due again after <see cref="RetryDelay"/>.</summary>
    public void Defer(string planId) => Change(planId, () => DeferLocked(planId, runId: null));

    /// <summary>
    /// The plan was saved: its state is dropped when retries are off or source or target changed; a lower number of
    /// attempts caps the state's (a higher one applies from the next regular run on).
    /// </summary>
    public void OnPlanSaved(BackupPlan plan) => Change(plan.Id, () =>
    {
        if (!_states.TryGetValue(plan.Id, out var state))
            return false;
        if (!plan.RetrySkipped.Enabled || !SameLocations(state, plan))
            return Remove(plan.Id);
        if (plan.RetrySkipped.Attempts >= state.Attempts)
            return false;

        state.Attempts = plan.RetrySkipped.Attempts;
        if (state.Attempt >= state.Attempts)
            return Remove(plan.Id);
        Save(state);
        return true;
    });

    /// <summary>The plan was deleted: so is its state.</summary>
    public void OnPlanDeleted(string planId) => Change(planId, () => Remove(planId));

    /// <summary>A copy of the plan's state; null when it has none.</summary>
    public RetryState? StateOf(string planId)
    {
        lock (_gate)
            return _states.TryGetValue(planId, out var state) ? state.Clone() : null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _timer.Dispose();
    }

    /// <summary>Applies <paramref name="change"/> under the lock, re-arms the timer and raises <see cref="Changed"/> when it returns true.</summary>
    private void Change(string planId, Func<bool> change)
    {
        bool changed;
        lock (_gate)
        {
            changed = change();
            if (changed)
                Arm();
        }
        if (changed)
            RaiseChanged(planId);
    }

    private bool FinishRegular(BackupPlan plan, RunLogEntry entry)
    {
        var settings = plan.RetrySkipped;
        if (!settings.Enabled || entry.Version is null || entry.RetryCandidates.Count == 0)
            return Remove(plan.Id);

        var state = new RetryState
        {
            PlanId = plan.Id,
            RunId = entry.RunId,
            Version = entry.Version,
            Source = plan.Source,
            Target = plan.Target,
            Attempt = 0,
            Attempts = settings.Attempts,
            NextDueUtc = entry.EndUtc + Interval(plan),
            Files = [.. entry.RetryCandidates],
        };
        _states[plan.Id] = state;
        _inFlight.Remove(plan.Id);
        Save(state);
        return true;
    }

    private bool FinishRetry(BackupPlan plan, RunLogEntry entry)
    {
        if (!_states.TryGetValue(plan.Id, out var state) || !string.Equals(state.RunId, entry.RetryOf, StringComparison.Ordinal))
            return false;
        if (entry.VersionGone)
            return Remove(plan.Id);
        if (entry.Status == RunStatus.Canceled)
            return DeferLocked(plan.Id, runId: null);

        _inFlight.Remove(plan.Id);
        state.Files = [.. entry.RetryCandidates];
        state.Attempt++;
        if (state.Files.Count == 0 || state.Attempt >= state.Attempts)
            return Remove(plan.Id);
        state.NextDueUtc = entry.EndUtc + Interval(plan);
        Save(state);
        return true;
    }

    /// <summary>Under the lock: the state (of that run, when given) is due again after <see cref="RetryDelay"/>.</summary>
    private bool DeferLocked(string planId, string? runId)
    {
        if (!_states.TryGetValue(planId, out var state) || (runId is not null && !string.Equals(state.RunId, runId, StringComparison.Ordinal)))
            return false;
        _inFlight.Remove(planId);
        state.NextDueUtc = UtcNow + RetryDelay;
        Save(state);
        return true;
    }

    /// <summary>Under the lock: drops the plan's state and its file; false when it had none.</summary>
    private bool Remove(string planId)
    {
        _inFlight.Remove(planId);
        if (!_states.Remove(planId))
            return false;
        DeleteFile(planId);
        return true;
    }

    private void Fire()
    {
        var due = new List<RetryState>();
        lock (_gate)
        {
            if (_disposed)
                return;
            var now = UtcNow;
            foreach (var state in _states.Values)
            {
                if (state.NextDueUtc <= now && _inFlight.Add(state.PlanId))
                    due.Add(state.Clone());
            }
            Arm();
        }

        foreach (var state in due)
        {
            try
            {
                _due(state);
            }
            catch (Exception)
            {
                // Not queued: like a deferred one, rather than in flight for good.
                Change(state.PlanId, () => DeferLocked(state.PlanId, state.RunId));
                continue;
            }
            RaiseChanged(state.PlanId);
        }
    }

    /// <summary>Under the lock: sets the timer to the earliest due time of the states not in flight; never after Dispose.</summary>
    private void Arm()
    {
        if (_disposed)
            return;

        DateTime? next = null;
        foreach (var state in _states.Values)
        {
            if (!_inFlight.Contains(state.PlanId) && (next is null || state.NextDueUtc < next))
                next = state.NextDueUtc;
        }

        var delay = Timeout.InfiniteTimeSpan;
        if (next is { } at)
        {
            delay = at - UtcNow;
            delay = delay < MinTimerDelay ? MinTimerDelay : delay > MaxTimerDelay ? MaxTimerDelay : delay;
        }
        try
        {
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (Exception)
        {
            // Nothing better to do; the next change re-arms it.
        }
    }

    /// <summary>Every state file with the plan id from its name; a null state for a damaged one. Unreadable files are left out.</summary>
    private List<(string PlanId, RetryState? State)> LoadAll()
    {
        var result = new List<(string, RetryState?)>();
        IEnumerable<string> files;
        try
        {
            if (!Directory.Exists(_directory))
                return result;
            files = Directory.EnumerateFiles(_directory, "*.json")
                .Where(f => Path.GetExtension(f).Equals(".json", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var file in files)
        {
            var planId = Path.GetFileNameWithoutExtension(file);
            try
            {
                result.Add((planId, JsonSerializer.Deserialize<RetryState>(File.ReadAllText(file), JsonDefaults.Options)));
            }
            catch (JsonException)
            {
                result.Add((planId, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cannot be read now (locked, no access): neither used nor deleted.
            }
        }
        return result;
    }

    private static bool IsWellFormed(string planId, RetryState state) =>
        string.Equals(state.PlanId, planId, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(state.RunId)
        && !string.IsNullOrEmpty(state.Version)
        && state.Source is not null && state.Target is not null
        && state.Files is { Count: > 0 } files && files.TrueForAll(f => f is not null)
        && state.Attempt < state.Attempts;

    private static bool StillApplies(RetryState state, Func<string, BackupPlan?> planOf, Func<string, RunLogEntry?> lastRegularRunOf)
    {
        try
        {
            return planOf(state.PlanId) is { RetrySkipped.Enabled: true } plan
                && SameLocations(state, plan)
                && string.Equals(lastRegularRunOf(state.PlanId)?.RunId, state.RunId, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;   // an unreadable plan or log: the run cannot be confirmed as the last one
        }
    }

    private static bool SameLocations(RetryState state, BackupPlan plan) =>
        Same(state.Source, plan.Source) && Same(state.Target, plan.Target);

    private static bool Same(StorageLocation a, StorageLocation b) =>
        JsonSerializer.Serialize(a, JsonDefaults.Options) == JsonSerializer.Serialize(b, JsonDefaults.Options);

    private static TimeSpan Interval(BackupPlan plan) => TimeSpan.FromMinutes(plan.RetrySkipped.IntervalMinutes);

    private string PathOf(string planId) => Path.Combine(_directory, planId + ".json");

    /// <summary>Writes the state's file; a failure only costs the state after a restart.</summary>
    private void Save(RetryState state)
    {
        try
        {
            AtomicFile.WriteAllText(PathOf(state.PlanId), JsonSerializer.Serialize(state, JsonDefaults.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes the state's file; one that cannot be deleted costs at most an extra attempt after a restart.</summary>
    private void DeleteFile(string planId)
    {
        try
        {
            File.Delete(PathOf(planId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void RaiseChanged(string planId)
    {
        var handlers = Changed;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                handler(planId);
            }
            catch (Exception)
            {
                // A failing listener must not stop the planner.
            }
        }
    }
}
