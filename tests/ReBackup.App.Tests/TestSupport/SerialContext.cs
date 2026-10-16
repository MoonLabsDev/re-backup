namespace ReBackup.App.Tests.TestSupport;

/// <summary>A synchronization context that runs what is posted to it one after the other on one thread, like a dispatcher.</summary>
public sealed class SerialContext : SynchronizationContext, IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    public SerialContext()
    {
        new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                callback(state);
        }) { IsBackground = true }.Start();
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (!_queue.IsAddingCompleted)
            _queue.TryAdd((d, state));
    }

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    /// <summary>Starts <paramref name="work"/> on the context's thread; completes when it has.</summary>
    public Task RunAsync(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try
            {
                await work();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null);
        return done.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}
