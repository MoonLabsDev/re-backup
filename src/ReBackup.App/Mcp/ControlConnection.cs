using ReBackup.Control;

namespace ReBackup.App.Mcp;

/// <summary>
/// The bridge's way to the app: connects on the first request, keeps that one connection for every later request, and
/// connects anew after it broke (the app was closed or restarted). Concurrent requests share the connection; the client
/// sends them one at a time, in order. A failed connect is not repeated within the same request.
/// </summary>
/// <param name="connect">Opens a new connection; <see cref="ControlClient.ConnectAsync"/> in the bridge.</param>
/// <param name="resendableOps">
/// Ops sent once more on a new connection when an existing one turns out to be broken: reads only. Any other op is sent
/// again only when it provably never left (<see cref="ControlUnavailableException.NotSent"/>: the old connection broke
/// before or while the request was written, typically the first call after the app restarted). A write that may have
/// reached the app is never repeated, because the app may have carried it out before the pipe broke.
/// </param>
public sealed class ControlConnection(Func<CancellationToken, Task<IControlChannel>> connect, IReadOnlySet<string> resendableOps)
    : IControlChannel, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IControlChannel? _channel;

    public async Task<ControlResponse> SendAsync(string op, object? args, CancellationToken ct)
    {
        var (channel, reused) = await ChannelAsync(ct).ConfigureAwait(false);
        try
        {
            return await SendOnAsync(channel, op, args, ct).ConfigureAwait(false);
        }
        catch (ControlUnavailableException ex) when (reused && (ex.NotSent || resendableOps.Contains(op)))
        {
            // The app went away since this connection was opened: a read, or any request the old app never received,
            // is safe to send again on a new one.
            (channel, _) = await ChannelAsync(ct).ConfigureAwait(false);
            return await SendOnAsync(channel, op, args, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The open connection, else a new one; <c>Reused</c> tells whether it was open before this call.</summary>
    private async Task<(IControlChannel Channel, bool Reused)> ChannelAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_channel is { } open)
                return (open, true);
            _channel = await connect(ct).ConfigureAwait(false);
            return (_channel, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sends on <paramref name="channel"/>; any failure lets it go (a client is unusable after one).</summary>
    private async Task<ControlResponse> SendOnAsync(IControlChannel channel, string op, object? args, CancellationToken ct)
    {
        try
        {
            return await channel.SendAsync(op, args, ct).ConfigureAwait(false);
        }
        catch
        {
            await DropAsync(channel).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Forgets <paramref name="channel"/> unless another call already replaced it, and disposes it.</summary>
    private async Task DropAsync(IControlChannel channel)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_channel, channel))
                return;
            _channel = null;
        }
        finally
        {
            _gate.Release();
        }
        if (channel is IAsyncDisposable disposable)
            await disposable.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _channel, null) is IAsyncDisposable disposable)
            await disposable.DisposeAsync().ConfigureAwait(false);
    }
}
