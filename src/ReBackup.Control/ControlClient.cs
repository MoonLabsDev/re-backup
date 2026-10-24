using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace ReBackup.Control;

/// <summary>Client side of the control pipe, used by the <c>--mcp</c> bridge. Calls on one client are serialized.</summary>
public sealed class ControlClient : IControlChannel, IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly NamedPipeClientStream _pipe;
    private readonly BoundedLineReader _reader;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextId;
    private volatile bool _broken;

    public string AppVersion { get; }

    private ControlClient(NamedPipeClientStream pipe, BoundedLineReader reader, string appVersion)
    {
        _pipe = pipe;
        _reader = reader;
        AppVersion = appVersion;
    }

    /// <exception cref="ControlUnavailableException">Nothing listens on the pipe.</exception>
    /// <exception cref="ControlProtocolException">The server speaks another protocol version.</exception>
    [SupportedOSPlatform("windows")]
    public static async Task<ControlClient> ConnectAsync(string? pipeName, TimeSpan timeout, CancellationToken ct)
    {
        pipeName ??= ControlPipe.NameForCurrentUser();
        // CurrentUserOnly refuses a server whose owner differs from our token's default owner, so an elevated
        // bridge and a non-elevated app (or the reverse) do not connect.
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);

            string? line;
            BoundedLineReader reader;
            try
            {
                await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
                reader = new BoundedLineReader(pipe);
                line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw Unavailable();
            }
            catch (LineTooLongException)
            {
                throw new ControlProtocolException("The server sent an oversized handshake.");
            }
            catch (UnauthorizedAccessException ex)
            {
                throw MapConnectFailure(ex)!;
            }
            catch (IOException ex)
            {
                throw Unavailable(ex);
            }
            if (line is null) throw Unavailable();

            ControlHandshake? hello;
            try { hello = JsonSerializer.Deserialize<ControlHandshake>(line, ControlJson.Options); }
            catch (JsonException) { hello = null; }
            if (hello is null)
                throw new ControlProtocolException("The server did not send a valid ReBackup control handshake.");
            if (hello.Protocol != ControlPipe.Protocol)
                throw new ControlProtocolException(
                    $"ReBackup (version {hello.AppVersion}) speaks control protocol {hello.Protocol}, but this client speaks {ControlPipe.Protocol}. Update ReBackup so both match.");

            return new ControlClient(pipe, reader, hello.AppVersion);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <exception cref="ControlUnavailableException">
    /// The app is gone. <see cref="ControlUnavailableException.NotSent"/> tells that the request never left (the client
    /// was broken already, or writing it failed), so it may be sent again on a new connection.
    /// </exception>
    public async Task<ControlResponse> SendAsync(string op, object? args, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_broken) throw Unavailable(notSent: true);
            var id = Interlocked.Increment(ref _nextId).ToString();
            var request = new ControlRequest(id, op,
                JsonSerializer.SerializeToElement(args ?? new { }, ControlJson.Options));
            // The whole line in one write: when it fails, the pipe was found broken and the app has read none of it
            // (a line without its newline is never a request it carries out).
            var line = Utf8.GetBytes(JsonSerializer.Serialize(request, ControlJson.Options) + "\n");
            try
            {
                await _pipe.WriteAsync(line, ct).ConfigureAwait(false);
                await _pipe.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Break();
                throw Unavailable(ex, notSent: true);
            }
            catch
            {
                Break();   // canceled mid-write: what went out is unknown
                throw;
            }
            try
            {
                var answer = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (answer is null) throw Unavailable();
                var response = JsonSerializer.Deserialize<ControlResponse>(answer, ControlJson.Options)
                               ?? throw new ControlProtocolException("The server sent an empty response.");
                if (response.Id != id)
                    throw new ControlProtocolException(
                        $"The server answered request '{response.Id}' while '{id}' was pending.");
                return response;
            }
            catch (Exception ex)
            {
                // Once the request has left, an unread or half-read answer would be mistaken for the next call's:
                // the connection is unusable from here on. The app may have carried the request out: not NotSent.
                Break();
                Exception? mapped = ex switch
                {
                    LineTooLongException => new ControlProtocolException("The server sent an oversized response."),
                    ControlUnavailableException => null,
                    IOException or ObjectDisposedException => Unavailable(ex),
                    JsonException => new ControlProtocolException("The server sent an unreadable response: " + ex.Message),
                    _ => null,
                };
                if (mapped is null) throw; // pass-through keeps the original stack trace
                throw mapped;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Maps the owner-check failure of <c>CurrentUserOnly</c>; null for exceptions it does not know.</summary>
    internal static Exception? MapConnectFailure(Exception ex) => ex is UnauthorizedAccessException
        ? new ControlProtocolException(
            "The ReBackup control pipe is owned by a different identity than this process (for example a different elevation level), so it was not trusted.", ex,
            identityMismatch: true)
        : null;

    private void Break()
    {
        _broken = true;
        try { _pipe.Dispose(); } catch { /* already closed */ }
    }

    private static ControlUnavailableException Unavailable(Exception? inner = null, bool notSent = false) =>
        new(ControlErrors.AppNotRunningMessage, inner, notSent);

    public async ValueTask DisposeAsync()
    {
        _broken = true;
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
