using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace ReBackup.Control;

/// <summary>
/// Hosts the control pipe: one listener loop, one task per connection. Every connection starts with the handshake
/// line, then answers newline-delimited requests until the client leaves or the host is disposed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ControlServerHost : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly IControlHandler _handler;
    private readonly string _appVersion;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<NamedPipeServerStream, Task> _connections = new();
    private Task? _listener;
    private int _started;

    public ControlServerHost(IControlHandler handler, string appVersion, string? pipeName = null)
    {
        _handler = handler;
        _appVersion = appVersion;
        _pipeName = pipeName ?? ControlPipe.NameForCurrentUser();
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        // The first instance exists before Start returns, so a client connecting right after never misses the pipe.
        var first = ControlPipe.CreateServerStream(_pipeName);
        _listener = Task.Run(() => ListenAsync(first));
    }

    private async Task ListenAsync(NamedPipeServerStream pending)
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await pending.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await pending.DisposeAsync().ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                try { pending = ControlPipe.CreateServerStream(_pipeName); }
                catch (IOException) { return; }
                continue;
            }

            var connected = pending;
            try { pending = ControlPipe.CreateServerStream(_pipeName); }
            catch (IOException)
            {
                await connected.DisposeAsync().ConfigureAwait(false);
                return;
            }
            _connections[connected] = Task.Run(() => ServeAsync(connected, ct), CancellationToken.None);
        }
        await pending.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ServeAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(stream, Utf8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(stream, Utf8, 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };

            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new ControlHandshake(ControlPipe.Protocol, _appVersion), ControlJson.Options)).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;
                var response = await RespondAsync(line, ct).ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, ControlJson.Options)).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Client left, the pipe broke or the host is shutting down; nobody is left to tell.
        }
        finally
        {
            _connections.TryRemove(stream, out _);
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<ControlResponse> RespondAsync(string line, CancellationToken ct)
    {
        ControlRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ControlRequest>(line, ControlJson.Options);
        }
        catch (JsonException)
        {
            return ControlResponse.Failure("", ControlErrors.BadRequest, "The request line is not valid JSON.");
        }
        if (request is null || string.IsNullOrEmpty(request.Op))
            return ControlResponse.Failure(request?.Id ?? "", ControlErrors.BadRequest, "The request has no 'op'.");

        if (request.Args.ValueKind == JsonValueKind.Undefined)
            request = request with { Args = JsonSerializer.SerializeToElement(new { }) };

        try
        {
            return await _handler.HandleAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ControlResponse.Failure(request.Id, ControlErrors.InternalError, ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        foreach (var stream in _connections.Keys)
        {
            try { stream.Dispose(); } catch { /* already gone */ }
        }
        if (_listener is not null)
        {
            try { await _listener.ConfigureAwait(false); } catch { /* listener ends on cancel */ }
        }
        await Task.WhenAll(_connections.Values.ToArray()).ConfigureAwait(false);
        _cts.Dispose();
    }
}
