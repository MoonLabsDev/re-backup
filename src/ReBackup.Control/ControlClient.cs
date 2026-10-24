using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace ReBackup.Control;

/// <summary>Client side of the control pipe, used by the <c>--mcp</c> bridge. Calls on one client are serialized.</summary>
public sealed class ControlClient : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextId;

    public string AppVersion { get; }

    private ControlClient(NamedPipeClientStream pipe, StreamReader reader, StreamWriter writer, string appVersion)
    {
        _pipe = pipe;
        _reader = reader;
        _writer = writer;
        AppVersion = appVersion;
    }

    /// <exception cref="ControlUnavailableException">Nothing listens on the pipe.</exception>
    /// <exception cref="ControlProtocolException">The server speaks another protocol version.</exception>
    [SupportedOSPlatform("windows")]
    public static async Task<ControlClient> ConnectAsync(string? pipeName, TimeSpan timeout, CancellationToken ct)
    {
        pipeName ??= ControlPipe.NameForCurrentUser();
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);

            string? line;
            StreamReader reader;
            StreamWriter writer;
            try
            {
                await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
                reader = new StreamReader(pipe, Utf8, false, 4096, leaveOpen: true);
                writer = new StreamWriter(pipe, Utf8, 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
                line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw Unavailable();
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

            return new ControlClient(pipe, reader, writer, hello.AppVersion);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ControlResponse> SendAsync(string op, object? args, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref _nextId).ToString();
            var request = new ControlRequest(id, op,
                JsonSerializer.SerializeToElement(args ?? new { }, ControlJson.Options));
            try
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(request, ControlJson.Options)).ConfigureAwait(false);
                var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) throw Unavailable();
                return JsonSerializer.Deserialize<ControlResponse>(line, ControlJson.Options)
                       ?? throw new ControlProtocolException("The server sent an empty response.");
            }
            catch (IOException ex) when (ex is not ControlUnavailableException)
            {
                throw Unavailable(ex);
            }
            catch (ObjectDisposedException ex)
            {
                throw Unavailable(ex);
            }
            catch (JsonException ex)
            {
                throw new ControlProtocolException("The server sent an unreadable response: " + ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ControlUnavailableException Unavailable(Exception? inner = null) =>
        new(ControlErrors.AppNotRunningMessage, inner);

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
