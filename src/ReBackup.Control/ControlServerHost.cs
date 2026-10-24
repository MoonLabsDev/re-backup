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
    private readonly object _startLock = new();
    private readonly Func<string, bool, NamedPipeServerStream> _createStream;
    private readonly TimeSpan _retryDelay;
    private bool _started;
    private volatile bool _faulted;
    private Task? _listener;

    public ControlServerHost(IControlHandler handler, string appVersion, string? pipeName = null)
        : this(handler, appVersion, pipeName, (name, first) => ControlPipe.CreateServerStream(name, first), DefaultRetryDelay)
    {
    }

    /// <param name="createStream">Creates a listening instance (name, first instance); the seam for tests.</param>
    /// <param name="retryDelay">The pause before another try when creating the next instance failed.</param>
    internal ControlServerHost(IControlHandler handler, string appVersion, string? pipeName,
        Func<string, bool, NamedPipeServerStream> createStream, TimeSpan retryDelay)
    {
        _handler = handler;
        _appVersion = appVersion;
        _pipeName = pipeName ?? ControlPipe.NameForCurrentUser();
        _createStream = createStream;
        _retryDelay = retryDelay;
    }

    /// <summary>How often the listener tries to create the next pipe instance before it gives up.</summary>
    internal const int CreateAttempts = 5;

    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// True once the listener stopped because it could not create the next pipe instance (after
    /// <see cref="CreateAttempts"/> tries): new clients cannot connect any more. Connections already open are still served.
    /// </summary>
    public bool IsFaulted => _faulted;

    /// <exception cref="InvalidOperationException">The pipe name is already taken by another process.</exception>
    public void Start()
    {
        lock (_startLock)
        {
            if (_started) return;
            // The first instance exists before Start returns, so a client connecting right after never misses the
            // pipe. FirstPipeInstance makes this fail when someone else already owns the name (squatting).
            NamedPipeServerStream first;
            try
            {
                first = _createStream(_pipeName, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"The control pipe '{_pipeName}' already exists and belongs to another process; refusing to join it.", ex);
            }
            _started = true;
            _listener = Task.Run(() => ListenAsync(first));
        }
    }

    private async Task ListenAsync(NamedPipeServerStream first)
    {
        var ct = _cts.Token;
        NamedPipeServerStream? pending = first;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await pending.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (ct.IsCancellationRequested)
                        return;
                    // Create the replacement while the failed instance still holds the name, so nobody can slip in.
                    var replacement = await NextInstanceAsync(ct).ConfigureAwait(false);
                    await pending.DisposeAsync().ConfigureAwait(false);
                    pending = replacement;
                    if (pending is null)
                        return;
                    continue;
                }

                var connected = pending;
                pending = null;
                // Register before the task can run, so its cleanup in finally always finds the entry. The connected
                // instance keeps the name taken while the next one is created.
                var serve = new Task<Task>(() => ServeAsync(connected, ct), CancellationToken.None);
                _connections[connected] = serve.Unwrap();
                serve.Start();
                pending = await NextInstanceAsync(ct).ConfigureAwait(false);
                if (pending is null)
                    return;
            }
        }
        finally
        {
            if (pending is not null)
                await pending.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The next listening instance. Any failure is tried again after a pause, up to <see cref="CreateAttempts"/> times;
    /// then the host is <see cref="IsFaulted"/> and null comes back (also when the host is disposed meanwhile).
    /// </summary>
    private async Task<NamedPipeServerStream?> NextInstanceAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= CreateAttempts; attempt++)
        {
            try
            {
                return _createStream(_pipeName, false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                if (attempt == CreateAttempts)
                    break;
                try
                {
                    await Task.Delay(_retryDelay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }
        if (!ct.IsCancellationRequested)
            _faulted = true;
        return null;
    }

    private async Task ServeAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        try
        {
            var reader = new BoundedLineReader(stream);
            using var writer = new StreamWriter(stream, Utf8, 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };

            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new ControlHandshake(ControlPipe.Protocol, _appVersion), ControlJson.Options)).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (LineTooLongException)
                {
                    var tooLong = ControlResponse.Failure("", ControlErrors.BadRequest, "The request line is too long.");
                    await writer.WriteLineAsync(JsonSerializer.Serialize(tooLong, ControlJson.Options)).ConfigureAwait(false);
                    break; // framing is lost; close the connection
                }
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
        // A handler that ignores cancellation must not hang shutdown; stragglers finish in the background.
        var drained = Task.WhenAll(_connections.Values.ToArray());
        await Task.WhenAny(drained, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        if (drained.IsCompleted) _cts.Dispose();
    }
}
