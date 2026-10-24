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
        ex.Message.Should().Contain("99").And.Contain("1");
        ex.Key.Should().Be(ControlErrors.ProtocolMismatch);
        raw.Dispose();
        try { await serve; } catch { }
    }

    [Fact]
    public async Task Server_disposed_mid_session_makes_the_next_call_unavailable()
    {
        var name = UniquePipe();
        var host = new ControlServerHost(new EchoHandler(), "1", name);
        host.Start();
        await using var client = await ControlClient.ConnectAsync(name, TimeSpan.FromSeconds(5), default);
        (await client.SendAsync("ping", null, default)).Ok.Should().BeTrue();

        await host.DisposeAsync();

        var act = () => client.SendAsync("ping", null, default);
        await act.Should().ThrowAsync<ControlUnavailableException>();
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
