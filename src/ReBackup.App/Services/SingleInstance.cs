namespace ReBackup.App.Services;

/// <summary>Holds a per-session mutex for as long as this copy of the app runs, and lets a second start wake the first.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\ReBackup.SingleInstance";
    private const string ActivateEventName = @"Local\ReBackup.Activate";

    private readonly Mutex _mutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _registration;
    private bool _disposed;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Null when another instance is running. Call <see cref="Dispose"/> on the same thread.</summary>
    public static SingleInstance? TryAcquire(TimeSpan waitForPrevious)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(waitForPrevious);
        }
        catch (AbandonedMutexException)
        {
            owned = true;   // the previous instance died without releasing it
        }

        if (owned)
            return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    /// <summary>Asks the instance that is already running to show its window.</summary>
    public static void SignalRunningInstance()
    {
        if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var activate))
        {
            using (activate)
                activate.Set();
        }
    }

    /// <summary><paramref name="onActivate"/> runs on a thread-pool thread each time another start signals this instance.</summary>
    public void ListenForActivation(Action onActivate)
    {
        _activateEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivateEventName);
        _registration = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) => onActivate(), null,
            System.Threading.Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _registration?.Unregister(null);
        _activateEvent?.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released from another thread than the one that acquired it; disposing the handle frees it as well.
        }
        _mutex.Dispose();
    }
}
