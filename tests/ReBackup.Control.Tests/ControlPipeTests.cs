using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FluentAssertions;
using ReBackup.Control;

namespace ReBackup.Control.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ControlPipeTests
{
    private static string UniquePipe() => "ReBackup.Control.Test." + Guid.NewGuid().ToString("N");

    private sealed class EchoHandler : IControlHandler
    {
        public Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken ct) =>
            Task.FromResult(new ControlResponse(request.Id, true, JsonSerializer.SerializeToElement(new { op = request.Op, args = request.Args }), null));
    }

    private sealed class GateHandler : IControlHandler
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken ct)
        {
            if (request.Op == "block") await Release.Task.WaitAsync(ct);
            return new ControlResponse(request.Id, true, JsonSerializer.SerializeToElement(request.Op), null);
        }
    }

    [Fact]
    public async Task Request_round_trips_through_the_pipe()
    {
        var name = UniquePipe();
        await using var host = new ControlServerHost(new EchoHandler(), "1.2.3", name);
        host.Start();

        await using var client = await ControlClient.ConnectAsync(name, TimeSpan.FromSeconds(5), default);
        var response = await client.SendAsync("ping", new { x = 7 }, default);

        response.Ok.Should().BeTrue();
        response.Error.Should().BeNull();
        response.Result!.Value.GetProperty("op").GetString().Should().Be("ping");
        response.Result!.Value.GetProperty("args").GetProperty("x").GetInt32().Should().Be(7);
        client.AppVersion.Should().Be("1.2.3");
    }

    [Fact]
    public async Task Two_clients_are_served_at_the_same_time()
    {
        var name = UniquePipe();
        var handler = new GateHandler();
        await using var host = new ControlServerHost(handler, "1", name);
        host.Start();

        await using var a = await ControlClient.ConnectAsync(name, TimeSpan.FromSeconds(5), default);
        await using var b = await ControlClient.ConnectAsync(name, TimeSpan.FromSeconds(5), default);

        var blocked = a.SendAsync("block", null, default);
        var quick = await b.SendAsync("fast", null, default).WaitAsync(TimeSpan.FromSeconds(5));
        quick.Ok.Should().BeTrue();
        blocked.IsCompleted.Should().BeFalse();

        handler.Release.SetResult();
        (await blocked.WaitAsync(TimeSpan.FromSeconds(5))).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Connect_without_server_is_unavailable()
    {
        var act = () => ControlClient.ConnectAsync(UniquePipe(), TimeSpan.FromMilliseconds(200), default);
        var ex = (await act.Should().ThrowAsync<ControlUnavailableException>()).Which;
        ex.Message.Should().Be("ReBackup is not running, or MCP control is disabled in Settings.");
    }

    [Fact]
    public async Task Protocol_mismatch_is_reported()
    {
        var name = UniquePipe();
        using var raw = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serve = Task.Run(async () =>
        {
            await raw.WaitForConnectionAsync();
            var bytes = System.Text.Encoding.UTF8.GetBytes("{\"protocol\":99,\"appVersion\":\"9.9\"}\n");
            await raw.WriteAsync(bytes);
            await raw.FlushAsync();
            await Task.Delay(1000);
        });

        var act = () => ControlClient.ConnectAsync(name, TimeSpan.FromSeconds(5), default);
        var ex = (await act.Should().ThrowAsync<ControlProtocolException>()).Which;
        ex.Message.Should().Contain("version 9.9").And.Contain("control protocol 99").And.Contain("this client speaks 1");
        ex.Key.Should().Be(ControlErrors.ProtocolMismatch);
        raw.Dispose();
        try { await serve; } catch { }
    }

    [Fact]
    public async Task Server_disposed_mid_session_makes_the_next_call_unavailable()
    {
        var name = UniquePipe();
        var host = new ControlServerHost(new EchoHandler(), "1", name);
        try
        {
            host.Start();
            await using var client = await ControlClient.ConnectAsync(name, TimeSpan.FromSeconds(5), default);
            (await client.SendAsync("ping", null, default)).Ok.Should().BeTrue();

            await host.DisposeAsync();

            var act = () => client.SendAsync("ping", null, default);
            await act.Should().ThrowAsync<ControlUnavailableException>();
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Request_written_to_a_closed_pipe_is_unavailable_and_marked_not_sent()
    {
        var name = UniquePipe();
        var host = new ControlServerHost(new EchoHandler(), "1", name);
        try
        {
            host.Start();
            await using var client = await ControlClient.ConnectAsync(name, TimeSpan.FromSeconds(5), default);
            (await client.SendAsync("ping", null, default)).Ok.Should().BeTrue();
            await host.DisposeAsync();   // the app closed (or restarted): the old pipe is dead

            var act = () => client.SendAsync("start_backup", null, default);

            (await act.Should().ThrowAsync<ControlUnavailableException>()).Which.NotSent
                .Should().BeTrue("writing the request failed, so the app never saw it");
            (await act.Should().ThrowAsync<ControlUnavailableException>()).Which.NotSent
                .Should().BeTrue("a broken client sends nothing either");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Connection_lost_after_the_request_left_is_unavailable_and_not_marked_not_sent()
    {
        var name = UniquePipe();
        var received = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fake = FakeServer.Start(name, async (stream, reader) =>
        {
            await FakeServer.WriteLineAsync(stream, "{\"protocol\":1,\"appVersion\":\"1\"}");
            received.SetResult(await reader.ReadLineAsync(default));
            await stream.DisposeAsync();   // the app went away while handling the request
        });
        await using var client = await ControlClient.ConnectAsync(name, TimeSpan.FromSeconds(5), default);

        var act = () => client.SendAsync("start_backup", null, default);

        (await act.Should().ThrowAsync<ControlUnavailableException>()).Which.NotSent
            .Should().BeFalse("the app may have carried the request out before the pipe broke");
        (await received.Task).Should().Contain("start_backup");
    }

    [Fact]
    public async Task Listener_that_cannot_create_the_next_instance_is_marked_faulted()
    {
        var name = UniquePipe();
        var creates = 0;
        await using var host = new ControlServerHost(new EchoHandler(), "1", name,
            (pipe, first) => Interlocked.Increment(ref creates) == 1
                ? ControlPipe.CreateServerStream(pipe, first)
                : throw new InvalidOperationException("no more instances"),
            TimeSpan.FromMilliseconds(10));
        host.Start();
        host.IsFaulted.Should().BeFalse();

        await using var client = await ControlClient.ConnectAsync(name, TimeSpan.FromSeconds(5), default);
        (await client.SendAsync("ping", null, default)).Ok.Should().BeTrue("the connection that came is still served");

        await WaitUntilAsync(() => host.IsFaulted);
        creates.Should().BeGreaterThan(2, "the listener retried before it gave up");
    }

    [Fact]
    public async Task Listener_retries_an_instance_that_failed_once_and_keeps_listening()
    {
        var name = UniquePipe();
        var creates = 0;
        await using var host = new ControlServerHost(new EchoHandler(), "1", name,
            (pipe, first) => Interlocked.Increment(ref creates) == 2
                ? throw new UnauthorizedAccessException("for a moment")
                : ControlPipe.CreateServerStream(pipe, first),
            TimeSpan.FromMilliseconds(10));
        host.Start();

        await using var a = await ControlClient.ConnectAsync(name, TimeSpan.FromSeconds(5), default);
        await using var b = await ControlClient.ConnectAsync(name, TimeSpan.FromSeconds(5), default);

        (await b.SendAsync("ping", null, default)).Ok.Should().BeTrue();
        host.IsFaulted.Should().BeFalse();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(end);
            await Task.Delay(10);
        }
    }

    [Fact]
    public void Start_refuses_to_join_a_pipe_name_that_already_exists()
    {
        var name = UniquePipe();
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var host = new ControlServerHost(new EchoHandler(), "1", name);

        var act = host.Start;
        act.Should().Throw<InvalidOperationException>().WithMessage("*already exists*");
    }

    [Fact]
    public async Task Failed_start_can_be_retried_once_the_name_is_free()
    {
        var name = UniquePipe();
        var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var host = new ControlServerHost(new EchoHandler(), "1", name);
        var act = host.Start;
        act.Should().Throw<InvalidOperationException>();
        squatter.Dispose();

        host.Start();
        await using var client = await ControlClient.ConnectAsync(name, TimeSpan.FromSeconds(5), default);
        (await client.SendAsync("ping", null, default)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelled_call_breaks_the_client_so_a_stale_response_is_never_returned()
    {
        var name = UniquePipe();
        var handler = new GateHandler();
        await using var host = new ControlServerHost(handler, "1", name);
        host.Start();
        await using var client = await ControlClient.ConnectAsync(name, TimeSpan.FromSeconds(5), default);

        using var cts = new CancellationTokenSource();
        var blocked = client.SendAsync("block", null, cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        var waiting = async () => await blocked;
        await waiting.Should().ThrowAsync<OperationCanceledException>();
        handler.Release.SetResult(); // the late answer for "block" is now on its way

        var next = () => client.SendAsync("fast", null, default);
        await next.Should().ThrowAsync<ControlUnavailableException>();
    }

    [Fact]
    public async Task Response_with_a_foreign_id_is_a_protocol_error()
    {
        var name = UniquePipe();
        await using var fake = FakeServer.Start(name, async (stream, reader) =>
        {
            await FakeServer.WriteLineAsync(stream, "{\"protocol\":1,\"appVersion\":\"1\"}");
            await reader.ReadLineAsync(default);
            await FakeServer.WriteLineAsync(stream, "{\"id\":\"999\",\"ok\":true}");
        });
        await using var client = await ControlClient.ConnectAsync(name, TimeSpan.FromSeconds(5), default);

        var act = () => client.SendAsync("ping", null, default);
        await act.Should().ThrowAsync<ControlProtocolException>().WithMessage("*999*");
    }

    [Fact]
    public async Task Oversized_response_is_a_protocol_error()
    {
        var name = UniquePipe();
        await using var fake = FakeServer.Start(name, async (stream, reader) =>
        {
            await FakeServer.WriteLineAsync(stream, "{\"protocol\":1,\"appVersion\":\"1\"}");
            await reader.ReadLineAsync(default);
            await FakeServer.WriteLineAsync(stream, new string('x', 1024 * 1024 + 10));
        });
        await using var client = await ControlClient.ConnectAsync(name, TimeSpan.FromSeconds(5), default);

        var act = () => client.SendAsync("ping", null, default);
        await act.Should().ThrowAsync<ControlProtocolException>().WithMessage("*oversized*");
    }

    [Fact]
    public async Task Oversized_request_gets_bad_request_and_the_connection_closes()
    {
        var name = UniquePipe();
        await using var host = new ControlServerHost(new EchoHandler(), "1", name);
        host.Start();
        await using var raw = await RawClient.ConnectAsync(name);

        await raw.SendRawAsync(new string('x', 1024 * 1024 + 10) + "\n");

        var response = JsonSerializer.Deserialize<ControlResponse>((await raw.ReadLineAsync())!, ControlJson.Options)!;
        response.Ok.Should().BeFalse();
        response.Error!.Key.Should().Be(ControlErrors.BadRequest);
        (await raw.ReadLineAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{\"id\":\"5\",\"args\":{}}")]
    public async Task Malformed_line_or_missing_op_gets_bad_request_and_session_survives(string line)
    {
        var name = UniquePipe();
        await using var host = new ControlServerHost(new EchoHandler(), "1", name);
        host.Start();
        await using var raw = await RawClient.ConnectAsync(name);

        await raw.SendRawAsync(line + "\n");
        var bad = JsonSerializer.Deserialize<ControlResponse>((await raw.ReadLineAsync())!, ControlJson.Options)!;
        bad.Ok.Should().BeFalse();
        bad.Error!.Key.Should().Be(ControlErrors.BadRequest);

        await raw.SendRawAsync("{\"id\":\"6\",\"op\":\"ping\"}\n");
        var good = JsonSerializer.Deserialize<ControlResponse>((await raw.ReadLineAsync())!, ControlJson.Options)!;
        good.Ok.Should().BeTrue();
        good.Id.Should().Be("6");
    }

    [Fact]
    public void Owner_check_failure_maps_to_a_protocol_error()
    {
        // A pipe with a foreign owner cannot be created without SeRestorePrivilege, so the mapping is tested directly.
        var mapped = ControlClient.MapConnectFailure(new UnauthorizedAccessException("owner"));

        mapped.Should().BeOfType<ControlProtocolException>().Which.Message.Should().Contain("owned by a different identity");
        ((ControlProtocolException)mapped!).IdentityMismatch.Should().BeTrue("the bridge tells elevation apart from versions");
        ControlClient.MapConnectFailure(new InvalidOperationException()).Should().BeNull();
    }

    private sealed class RawClient : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;

        private RawClient(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _reader = new StreamReader(pipe, new System.Text.UTF8Encoding(false));
        }

        public static async Task<RawClient> ConnectAsync(string name)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            var client = new RawClient(pipe);
            (await client.ReadLineAsync()).Should().Contain("\"protocol\":1"); // handshake
            return client;
        }

        public Task<string?> ReadLineAsync() => _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));

        public async Task SendRawAsync(string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            await _pipe.WriteAsync(bytes);
            await _pipe.FlushAsync();
        }

        public ValueTask DisposeAsync() => _pipe.DisposeAsync();
    }

    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _stream;
        private readonly Task _task;

        private FakeServer(NamedPipeServerStream stream, Task task)
        {
            _stream = stream;
            _task = task;
        }

        public static FakeServer Start(string name, Func<NamedPipeServerStream, StreamReader, Task> script)
        {
            var stream = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var task = Task.Run(async () =>
            {
                try
                {
                    await stream.WaitForConnectionAsync();
                    await script(stream, new StreamReader(stream, new System.Text.UTF8Encoding(false)));
                    await Task.Delay(1000);
                }
                catch (Exception) { /* client went away */ }
            });
            return new FakeServer(stream, task);
        }

        public static async Task WriteLineAsync(Stream s, string line)
        {
            await s.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line + "\n"));
            await s.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            try { await _task; } catch { }
        }
    }

    [Fact]
    public async Task Handler_exception_becomes_an_error_response_and_session_survives()
    {
        var name = UniquePipe();
        await using var host = new ControlServerHost(new ThrowingHandler(), "1", name);
        host.Start();
        await using var client = await ControlClient.ConnectAsync(name, TimeSpan.FromSeconds(5), default);

        var bad = await client.SendAsync("boom", null, default);
        bad.Ok.Should().BeFalse();
        bad.Error!.Key.Should().Be("internal_error");
        (await client.SendAsync("ok", null, default)).Ok.Should().BeTrue();
    }

    private sealed class ThrowingHandler : IControlHandler
    {
        public Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken ct) =>
            request.Op == "boom"
                ? throw new InvalidOperationException("kaputt")
                : Task.FromResult(new ControlResponse(request.Id, true, null, null));
    }

    [Fact]
    public void Pipe_acl_grants_only_the_current_user()
    {
        var name = UniquePipe();
        using var stream = ControlPipe.CreateServerStream(name);
        var security = stream.GetAccessControl();
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var me = WindowsIdentity.GetCurrent().User!;

        rules.Should().NotBeEmpty();
        rules.Where(r => r.AccessControlType == AccessControlType.Allow)
             .Select(r => r.IdentityReference.Value).Distinct().Should().Equal(me.Value);
        security.AreAccessRulesProtected.Should().BeTrue();
    }

    [Fact]
    public void Pipe_name_contains_the_user_sid()
    {
        ControlPipe.NameForCurrentUser().Should().Be("ReBackup.Control." + WindowsIdentity.GetCurrent().User!.Value);
        ControlPipe.Protocol.Should().Be(1);
    }
}
