using ReBackup.Control;

namespace ReBackup.App.Control;

/// <summary>Starts and stops the control pipe at the user's switch (Settings → "Allow control via MCP").</summary>
public interface IMcpControlHost
{
    /// <summary>True while the pipe is hosted.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Hosts the pipe (<paramref name="enabled"/>) or releases it. Null when the pipe is in the wanted state afterwards;
    /// otherwise why it could not be hosted (another instance or another process owns the pipe name).
    /// </summary>
    Task<string?> ApplyAsync(bool enabled);
}

/// <summary>
/// Owns the <see cref="ControlServerHost"/> of the running app: one at a time, created when the pipe is switched on and
/// disposed (bounded) when it is switched off or the app exits. Calls are serialized.
/// </summary>
public sealed class McpControlHost : IMcpControlHost, IAsyncDisposable
{
    private readonly IControlHandler _handler;
    private readonly string _appVersion;
    private readonly string? _pipeName;
    private readonly Func<ControlServerHost> _hostFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ControlServerHost? _host;
    private bool _disposed;

    public McpControlHost(IControlHandler handler, string appVersion, string? pipeName = null,
        Func<ControlServerHost>? hostFactory = null)
    {
        _handler = handler;
        _appVersion = appVersion;
        _pipeName = pipeName;
        _hostFactory = hostFactory ?? (() => new ControlServerHost(_handler, _appVersion, _pipeName));
    }

    /// <summary>True while a host listens; false once its listener faulted (it could not create the next pipe instance).</summary>
    public bool IsRunning => Volatile.Read(ref _host) is { IsFaulted: false };

    public async Task<string?> ApplyAsync(bool enabled)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return null;
            if (!enabled)
            {
                await StopAsync().ConfigureAwait(false);
                return null;
            }
            if (_host is { IsFaulted: false })
                return null;
            await StopAsync().ConfigureAwait(false);   // a faulted listener is replaced

            ControlServerHost? host = null;
            try
            {
                host = _hostFactory();
                host.Start();
            }
            catch (Exception ex)
            {
                // A taken pipe name (InvalidOperationException) or anything else that stops it (user SID, ACL): told, never thrown.
                if (host is not null)
                    await host.DisposeAsync().ConfigureAwait(false);
                return ex.Message;
            }
            Volatile.Write(ref _host, host);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopAsync()
    {
        var host = _host;
        Volatile.Write(ref _host, null);
        if (host is not null)
            await host.DisposeAsync().ConfigureAwait(false);   // bounded (5 s) by the host
    }

    /// <summary>Releases the pipe for good (the app exits); a later <see cref="ApplyAsync"/> does nothing.</summary>
    /// <summary>
    /// <see cref="IMcpControlHost.ApplyAsync"/>(true), tried again every <paramref name="interval"/> while it fails, for up
    /// to <paramref name="retryFor"/> (zero: once). After a restart the old process may still be releasing the pipe name.
    /// Null once hosted; otherwise the last failure.
    /// </summary>
    public static async Task<string?> ApplyRetryingAsync(IMcpControlHost host, TimeSpan retryFor, TimeSpan interval)
    {
        var end = DateTime.UtcNow + retryFor;
        while (true)
        {
            var error = await host.ApplyAsync(true).ConfigureAwait(false);
            if (error is null || DateTime.UtcNow + interval > end)
                return error;
            await Task.Delay(interval).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
