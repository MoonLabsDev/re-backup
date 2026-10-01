using System.Diagnostics;
using ReBackup.Core.Backup;

namespace ReBackup.Core.Versions;

/// <summary>
/// Runs all work on a plan's version index off the caller's thread, one item at a time and in the order it was
/// queued; work on different plans runs side by side. A sync of a network target can hold the index for minutes, so
/// a finished backup run only queues its version here (<see cref="Add"/> returns at once) and never waits for it.
/// </summary>
public sealed class VersionIndexWorker : IVersionIndexSink
{
    private readonly Dictionary<string, Task> _tails = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Action<string, Exception>? _onAddError;

    /// <param name="indexes">The plans' indexes.</param>
    /// <param name="onAddError">
    /// Gets the plan id and the error of a queued <see cref="Add"/> that failed (on a worker thread). The version is
    /// then imported from its folder by the next sync.
    /// </param>
    public VersionIndexWorker(VersionIndexSet indexes, Action<string, Exception>? onAddError = null)
    {
        Indexes = indexes;
        _onAddError = onAddError;
    }

    public VersionIndexSet Indexes { get; }

    /// <summary>
    /// Queues the version of a finished run for the plan's index and returns at once. Throws only for a plan id that
    /// cannot have an index; failures of the queued work go to the error callback.
    /// </summary>
    public void Add(string planId, VersionInfo version, BackupManifest manifest)
    {
        Indexes.PathFor(planId);
        _ = Chain<object?>(planId, () =>
        {
            try
            {
                Indexes.For(planId).Add(version, manifest);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"The version index of plan {planId} could not take \"{version.Name}\": {ex.Message}");
                _onAddError?.Invoke(planId, ex);
            }
            return null;
        }, CancellationToken.None);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the plan's index on a pool thread after all work queued for that plan before
    /// it. Work whose token is canceled before it starts does not run; the task then throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public Task<T> RunAsync<T>(string planId, Func<VersionIndex, T> work, CancellationToken cancellationToken = default)
    {
        Indexes.PathFor(planId);
        return Chain(planId, () => work(Indexes.For(planId)), cancellationToken);
    }

    private Task<T> Chain<T>(string planId, Func<T> body, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var previous = _tails.GetValueOrDefault(planId) ?? Task.CompletedTask;
            // No token for ContinueWith itself: a canceled item must still wait for the one before it, or the item
            // after it would start too early.
            var task = previous.ContinueWith(_ =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return body();
            }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            _tails[planId] = task;
            return task;
        }
    }

    /// <inheritdoc cref="RunAsync{T}(string, Func{VersionIndex, T}, CancellationToken)"/>
    public Task RunAsync(string planId, Action<VersionIndex> work, CancellationToken cancellationToken = default) =>
        RunAsync<object?>(planId, index =>
        {
            work(index);
            return null;
        }, cancellationToken);

    /// <summary>Completes when everything queued for the plan so far has ended (successfully or not).</summary>
    public Task WhenIdle(string planId)
    {
        lock (_gate)
        {
            var tail = _tails.GetValueOrDefault(planId) ?? Task.CompletedTask;
            return tail.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
