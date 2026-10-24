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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ControlServerHost? _host;
    private bool _disposed;

    public McpControlHost(IControlHandler handler, string appVersion, string? pipeName = null)
    {
        _handler = handler;
        _appVersion = appVersion;
        _pipeName = pipeName;
    }

    public bool IsRunning => Volatile.Read(ref _host) is not null;

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
            if (_host is not null)
                return null;

            var host = new ControlServerHost(_handler, _appVersion, _pipeName);
            try
            {
                host.Start();
            }
            catch (InvalidOperationException ex)
            {
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
