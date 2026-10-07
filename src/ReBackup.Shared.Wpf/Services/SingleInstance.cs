namespace ReBackup.Shared.Wpf.Services;

/// <summary>
/// Holds a per-session mutex (<c>Local\{appId}.SingleInstance</c>) for as long as this copy of the app runs, and lets a
/// second start wake the first (event <c>Local\{appId}.Activate</c>).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _activateEventName;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _registration;
    private bool _disposed;

    private SingleInstance(Mutex mutex, string appId)
    {
        _mutex = mutex;
        _activateEventName = ActivateEventNameOf(appId);
    }

    private static string MutexNameOf(string appId) => $@"Local\{appId}.SingleInstance";

    private static string ActivateEventNameOf(string appId) => $@"Local\{appId}.Activate";

    /// <summary>Null when another instance of <paramref name="appId"/> is running. Call <see cref="Dispose"/> on the same thread.</summary>
    public static SingleInstance? TryAcquire(string appId, TimeSpan waitForPrevious)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        Mutex mutex;
        bool owned;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexNameOf(appId));
        }
        catch (UnauthorizedAccessException)
        {
            return null;    // the mutex exists under an incompatible ACL, e.g. the running copy is elevated
        }

        try
        {
            owned = mutex.WaitOne(waitForPrevious);
        }
        catch (AbandonedMutexException)
        {
            owned = true;   // the previous instance died without releasing it
        }

        if (owned)
            return new SingleInstance(mutex, appId);
        mutex.Dispose();
        return null;
    }

    /// <summary>Asks the instance of <paramref name="appId"/> that is already running to show its window.</summary>
    public static void SignalRunningInstance(string appId)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventNameOf(appId), out var activate))
            {
                using (activate)
                    activate.Set();
            }
        }
        catch (UnauthorizedAccessException)
        {
            // The running copy is elevated and its event is not accessible; nothing to wake.
        }
    }

    /// <summary><paramref name="onActivate"/> runs on a thread-pool thread each time another start signals this instance.</summary>
    public void ListenForActivation(Action onActivate)
    {
        _activateEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, _activateEventName);
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
