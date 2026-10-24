using System.IO;
using System.IO.Pipelines;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ReBackup.App.Control;
using ReBackup.App.Mcp;
using ReBackup.App.Tests.TestSupport;
using ReBackup.Control;
using ReBackup.Core.Plans;
using ReBackup.Storage;

namespace ReBackup.App.Tests;

/// <summary>
/// The bridge's MCP surface through the SDK, over in-memory streams: the tools of the spec with their annotations and
/// arguments, results and errors as the control service gives them, the <c>analyze_growth</c> prompt and the server
/// instructions. The control service runs in-process behind an <see cref="IControlChannel"/>; no pipe, no app.
/// </summary>
public class McpToolsTests : IAsyncDisposable
{
    private static readonly string[] ReadTools =
    [
        "list_plans", "list_versions", "compare_versions", "compare_source_with_version", "get_ignore_rules",
        "explain_path", "list_files", "get_run_history", "get_run_status",
    ];

    private static readonly string[] WriteTools = ["preview_ignore_change", "propose_relocation", "apply_change", "start_backup"];

    private readonly MainViewModelFixture _fixture = new();
    private readonly SerialContext _ui = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<IAsyncDisposable> _cleanup = [];
    private readonly string _source;

    public McpToolsTests()
    {
        _source = Path.Combine(_fixture.Root, "source");
        var target = Path.Combine(_fixture.Root, "target");
        Directory.CreateDirectory(Path.Combine(_source, "logs"));
        File.WriteAllText(Path.Combine(_source, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(_source, "logs", "x.log"), "log");
        Directory.CreateDirectory(target);
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = StorageLocation.FileSystem(_source),
            Target = StorageLocation.FileSystem(target),
            Ignore = new IgnoreSettings { Patterns = ["logs/"] },
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        for (var i = _cleanup.Count - 1; i >= 0; i--)
            await _cleanup[i].DisposeAsync();
        _ui.Dispose();
        _fixture.Dispose();
        _stop.Dispose();
    }

    // ------------------------------------------------------------------ tools

    [Fact]
    public async Task Tool_list_matches_the_spec_with_annotations()
    {
        var client = await ConnectAsync(new ServiceChannel(Service()));

        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);

        tools.Keys.Should().BeEquivalentTo(ReadTools.Concat(WriteTools), "one tool per operation of the spec, and no ping");
        foreach (var name in ReadTools)
            tools[name].Annotations!.ReadOnlyHint.Should().BeTrue(name + " only reads");
        foreach (var name in new[] { "preview_ignore_change", "propose_relocation" })
        {
            tools[name].Annotations!.ReadOnlyHint.Should().BeTrue(name + " only stores a proposal in memory");
            tools[name].Annotations!.DestructiveHint.Should().BeFalse(name);
        }
        tools["apply_change"].Annotations!.ReadOnlyHint.Should().BeFalse("apply_change writes");
        tools["apply_change"].Annotations!.DestructiveHint.Should().BeTrue("apply_change changes the rules");
        tools["start_backup"].Annotations!.ReadOnlyHint.Should().BeFalse("start_backup starts a run");
        tools["start_backup"].Annotations!.DestructiveHint.Should().BeFalse("a run adds a version");
        foreach (var tool in tools.Values)
            tool.Description.Should().NotBeNullOrWhiteSpace(tool.Name);

        Arguments(tools["list_plans"]).Should().BeEmpty();
        Arguments(tools["list_versions"]).Should().BeEquivalentTo([("plan", true)]);
        Arguments(tools["compare_versions"]).Should().BeEquivalentTo(
            [("plan", true), ("a", true), ("b", true), ("path", false), ("top", false), ("sort", false)]);
        Arguments(tools["compare_source_with_version"]).Should().BeEquivalentTo(
            [("plan", true), ("version", false), ("path", false), ("top", false), ("sort", false)]);
        Arguments(tools["get_ignore_rules"]).Should().BeEquivalentTo([("plan", true), ("path", false)]);
        Arguments(tools["explain_path"]).Should().BeEquivalentTo([("plan", true), ("path", true)]);
        Arguments(tools["list_files"]).Should().BeEquivalentTo(
            [("plan", true), ("path", false), ("in", false), ("sort", false), ("limit", false)]);
        Arguments(tools["get_run_history"]).Should().BeEquivalentTo([("plan", true), ("n", false)]);
        Arguments(tools["get_run_status"]).Should().BeEquivalentTo([("runId", true)]);
        Arguments(tools["preview_ignore_change"]).Should().BeEquivalentTo([("plan", true), ("add", false), ("remove", false)]);
        Arguments(tools["propose_relocation"]).Should().BeEquivalentTo(
            [("plan", true), ("integrate", false), ("extract", false), ("folder", false)]);
        Arguments(tools["apply_change"]).Should().BeEquivalentTo([("proposalId", true)]);
        Arguments(tools["start_backup"]).Should().BeEquivalentTo([("plan", true)]);

        tools["list_files"].Description.Should().ContainEquivalentOf("metadata only", "the tool states that it never returns contents");
        tools["compare_versions"].Description.Should().Contain("bytes", "the tool states its units");
        foreach (var name in new[] { "compare_versions", "compare_source_with_version" })
            tools[name].Description.Should().Contain("copiedSize").And.Contain("changed", name + " explains copied bytes");
    }

    [Fact]
    public async Task Read_tool_returns_service_result_as_json()
    {
        var client = await ConnectAsync(new ServiceChannel(Service()));

        var result = await client.CallToolAsync("list_files", new Dictionary<string, object?> { ["plan"] = "Projects", ["sort"] = "name" });

        result.IsError.Should().NotBe(true, Text(result));
        using var json = JsonDocument.Parse(Text(result));
        json.RootElement.GetProperty("plan").GetString().Should().Be("Projects");
        json.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString())
            .Should().Equal("logs", "a.txt");
    }

    [Fact]
    public async Task Tool_arguments_reach_the_service_under_their_names()
    {
        var channel = new RecordingChannel();
        var client = await ConnectAsync(channel);

        await client.CallToolAsync("list_files", new Dictionary<string, object?>
            { ["plan"] = "Projects", ["path"] = "logs", ["in"] = "source", ["sort"] = "size", ["limit"] = 5 });
        await client.CallToolAsync("propose_relocation", new Dictionary<string, object?>
            { ["plan"] = "Projects", ["extract"] = new[] { "logs/" }, ["folder"] = "docs" });
        await client.CallToolAsync("list_plans", new Dictionary<string, object?>());

        channel.Calls.Select(c => c.Op).Should().Equal(ControlOps.ListFiles, ControlOps.ProposeRelocation, ControlOps.ListPlans);
        channel.Calls[0].Args.Should().Be("""{"plan":"Projects","path":"logs","in":"source","sort":"size","limit":5}""");
        channel.Calls[1].Args.Should().Be("""{"plan":"Projects","extract":["logs/"],"folder":"docs"}""",
            "arguments left out are not sent");
        channel.Calls[2].Args.Should().Be("{}");
    }

    [Fact]
    public async Task Control_error_becomes_a_tool_error_with_the_key()
    {
        var client = await ConnectAsync(new ServiceChannel(Service()));

        var result = await client.CallToolAsync("list_versions", new Dictionary<string, object?> { ["plan"] = "Nope" });

        result.IsError.Should().BeTrue();
        Text(result).Should().StartWith("plan_not_found: No plan has the id or name 'Nope'.");
    }

    [Fact]
    public async Task Unavailable_app_gives_app_not_running()
    {
        // The real client against a pipe nobody hosts: what the bridge sees when the app is closed or control is off.
        var pipe = "ReBackup.Control.Test." + Guid.NewGuid().ToString("N");
        await using var connection = new ControlConnection(
            async ct => await ControlClient.ConnectAsync(pipe, TimeSpan.FromMilliseconds(200), ct), McpTools.ReadOps);
        var client = await ConnectAsync(connection);

        var result = await client.CallToolAsync("list_plans", new Dictionary<string, object?>());

        result.IsError.Should().BeTrue();
        Text(result).Should().Be("app_not_running: ReBackup is not running, or MCP control is disabled in Settings.");
    }

    [Fact]
    public async Task Protocol_mismatch_tells_to_restart_the_client()
    {
        await using var connection = new ControlConnection(
            _ => throw new ControlProtocolException("speaks control protocol 2"), McpTools.ReadOps);
        var client = await ConnectAsync(connection);

        var result = await client.CallToolAsync("list_plans", new Dictionary<string, object?>());

        result.IsError.Should().BeTrue();
        Text(result).Should().StartWith(
            "protocol_mismatch: ReBackup and its MCP bridge are different versions; restart Claude after updating ReBackup.");
        Text(result).Should().Contain("speaks control protocol 2", "the detail tells other mismatches (elevation) apart");
    }

    [Fact]
    public async Task Elevation_mismatch_tells_to_start_both_the_same_way()
    {
        await using var connection = new ControlConnection(
            _ => throw new ControlProtocolException("owned by a different identity", identityMismatch: true), McpTools.ReadOps);
        var client = await ConnectAsync(connection);

        var result = await client.CallToolAsync("list_plans", new Dictionary<string, object?>());

        result.IsError.Should().BeTrue();
        Text(result).Should().StartWith(
            "protocol_mismatch: ReBackup and Claude run with different elevation (one as administrator) — start both the same way.");
        Text(result).Should().NotContain("different versions");
    }

    // ------------------------------------------------------------------ the connection

    [Fact]
    public async Task Connection_connects_on_the_first_call_and_reuses_the_channel()
    {
        var connects = 0;
        await using var connection = new ControlConnection(_ =>
        {
            connects++;
            return Task.FromResult<IControlChannel>(new RecordingChannel());
        }, McpTools.ReadOps);
        connects.Should().Be(0, "nothing connects before a tool is called");

        await connection.SendAsync(ControlOps.ListPlans, null, CancellationToken.None);
        await connection.SendAsync(ControlOps.ListPlans, null, CancellationToken.None);

        connects.Should().Be(1);
    }

    [Fact]
    public async Task Connection_tries_to_connect_again_on_the_next_call()
    {
        var connects = 0;
        await using var connection = new ControlConnection(_ => ++connects == 1
            ? throw new ControlUnavailableException(ControlErrors.AppNotRunningMessage)
            : Task.FromResult<IControlChannel>(new RecordingChannel()), McpTools.ReadOps);

        await connection.Invoking(c => c.SendAsync(ControlOps.ListPlans, null, CancellationToken.None))
            .Should().ThrowAsync<ControlUnavailableException>("the app was not running yet");
        var response = await connection.SendAsync(ControlOps.ListPlans, null, CancellationToken.None);

        response.Ok.Should().BeTrue("the app started meanwhile");
        connects.Should().Be(2, "a failed connect is not retried within the same call");
    }

    [Fact]
    public async Task Connection_resends_a_read_on_a_new_client_after_the_app_restarted()
    {
        var broken = new BrokenChannel { Answer = true };
        var fresh = new RecordingChannel();
        var channels = new Queue<IControlChannel>([broken, fresh]);
        await using var connection = new ControlConnection(_ => Task.FromResult(channels.Dequeue()), McpTools.ReadOps);
        await connection.SendAsync(ControlOps.ListPlans, null, CancellationToken.None);
        broken.Answer = false;   // the app restarted: the old pipe is dead

        var response = await connection.SendAsync(ControlOps.ListVersions, new { plan = "Projects" }, CancellationToken.None);

        response.Ok.Should().BeTrue();
        fresh.Calls.Should().ContainSingle().Which.Op.Should().Be(ControlOps.ListVersions);
        broken.Disposed.Should().BeTrue("the broken client is let go");
    }

    [Fact]
    public async Task Connection_never_resends_a_write()
    {
        var broken = new BrokenChannel { Answer = true };
        var fresh = new RecordingChannel();
        var channels = new Queue<IControlChannel>([broken, fresh]);
        await using var connection = new ControlConnection(_ => Task.FromResult(channels.Dequeue()), McpTools.ReadOps);
        await connection.SendAsync(ControlOps.ListPlans, null, CancellationToken.None);
        broken.Answer = false;

        await connection.Invoking(c => c.SendAsync(ControlOps.StartBackup, new { plan = "Projects" }, CancellationToken.None))
            .Should().ThrowAsync<ControlUnavailableException>("the app may have started the run before the pipe broke");
        await connection.SendAsync(ControlOps.ListPlans, null, CancellationToken.None);

        fresh.Calls.Select(c => c.Op).Should().Equal([ControlOps.ListPlans], "the next call reconnects; the write is not repeated");
    }

    [Fact]
    public async Task Connection_resends_a_write_that_never_left_on_a_new_client()
    {
        var broken = new BrokenChannel { Answer = true, NotSent = true };
        var fresh = new RecordingChannel();
        var channels = new Queue<IControlChannel>([broken, fresh]);
        await using var connection = new ControlConnection(_ => Task.FromResult(channels.Dequeue()), McpTools.ReadOps);
        await connection.SendAsync(ControlOps.ListPlans, null, CancellationToken.None);
        broken.Answer = false;

        var response = await connection.SendAsync(ControlOps.StartBackup, new { plan = "Projects" }, CancellationToken.None);

        response.Ok.Should().BeTrue("the request provably never reached the old app");
        fresh.Calls.Should().ContainSingle().Which.Op.Should().Be(ControlOps.StartBackup);
    }

    [Fact]
    public async Task First_write_after_the_app_restarted_reaches_the_new_app()
    {
        // The real client and pipe: the bridge's connection outlives the app, which comes back on the same pipe name.
        var pipe = "ReBackup.Control.Test." + Guid.NewGuid().ToString("N");
        var before = new CountingHandler();
        var after = new CountingHandler();
        await using var connection = new ControlConnection(
            async ct => await ControlClient.ConnectAsync(pipe, TimeSpan.FromSeconds(5), ct), McpTools.ReadOps);
        var first = new ControlServerHost(before, "1", pipe);
        first.Start();
        (await connection.SendAsync(ControlOps.ListPlans, null, CancellationToken.None)).Ok.Should().BeTrue();
        await first.DisposeAsync();
        await using var second = new ControlServerHost(after, "1", pipe);
        second.Start();

        var response = await connection.SendAsync(ControlOps.StartBackup, new { plan = "Projects" }, CancellationToken.None);

        response.Ok.Should().BeTrue("not app_not_running: the write never reached the old app, so it went to the new one");
        before.Ops.Should().Equal(ControlOps.ListPlans);
        after.Ops.Should().Equal(ControlOps.StartBackup);
    }

    [Fact]
    public async Task A_write_the_app_received_before_the_pipe_broke_is_never_resent()
    {
        var pipe = "ReBackup.Control.Test." + Guid.NewGuid().ToString("N");
        var after = new CountingHandler();
        var second = new ControlServerHost(after, "1", pipe);
        ControlServerHost? first = null;
        var dying = new CountingHandler(async request =>
        {
            if (request.Op != ControlOps.StartBackup)
                return;
            // The app goes away while carrying out the write, and a new one is up before the bridge could resend.
            _ = Task.Run(async () =>
            {
                await first!.DisposeAsync();
                second.Start();
            });
            await Task.Delay(TimeSpan.FromSeconds(10));
        });
        first = new ControlServerHost(dying, "1", pipe);
        first.Start();
        await using var connection = new ControlConnection(
            async ct => await ControlClient.ConnectAsync(pipe, TimeSpan.FromSeconds(5), ct), McpTools.ReadOps);
        (await connection.SendAsync(ControlOps.ListPlans, null, CancellationToken.None)).Ok.Should().BeTrue();

        try
        {
            var thrown = await connection.Invoking(c => c.SendAsync(ControlOps.StartBackup, new { plan = "Projects" }, CancellationToken.None))
                .Should().ThrowAsync<ControlUnavailableException>("the old app may have started the run");
            thrown.Which.NotSent.Should().BeFalse();
            dying.Ops.Should().Equal(ControlOps.ListPlans, ControlOps.StartBackup);
            after.Ops.Should().BeEmpty("the write is not repeated on the new app");
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task Connection_lets_concurrent_calls_share_one_connect()
    {
        var connects = 0;
        var gate = new TaskCompletionSource();
        await using var connection = new ControlConnection(async _ =>
        {
            Interlocked.Increment(ref connects);
            await gate.Task;
            return new RecordingChannel();
        }, McpTools.ReadOps);

        var calls = Enumerable.Range(0, 5).Select(_ => connection.SendAsync(ControlOps.ListPlans, null, CancellationToken.None)).ToList();
        gate.SetResult();
        await Task.WhenAll(calls);

        connects.Should().Be(1);
    }

    // ------------------------------------------------------------------ prompt and instructions

    [Fact]
    public async Task Prompt_analyze_growth_mentions_asking_before_reading_contents()
    {
        var client = await ConnectAsync(new RecordingChannel());

        var prompt = (await client.ListPromptsAsync()).Should().ContainSingle().Which.ProtocolPrompt;
        prompt.Name.Should().Be("analyze_growth");
        prompt.Arguments!.Select(a => (a.Name, a.Required == true)).Should().BeEquivalentTo([("plan", true), ("a", false), ("b", false)]);

        var versions = PromptText(await client.GetPromptAsync("analyze_growth",
            new Dictionary<string, object?> { ["plan"] = "Projects", ["a"] = "v1", ["b"] = "v2" }));
        var source = PromptText(await client.GetPromptAsync("analyze_growth",
            new Dictionary<string, object?> { ["plan"] = "Projects" }));

        versions.Should().Contain("compare_versions").And.Contain("\"v1\"").And.Contain("\"v2\"").And.Contain("\"Projects\"");
        source.Should().Contain("compare_source_with_version").And.NotContain("compare_versions(");
        foreach (var text in new[] { versions, source })
        {
            text.Should().Contain("Ask the user before", "step 3: contents only after asking");
            text.Should().Contain("your own file tools");
            text.Should().Contain("preview_ignore_change").And.Contain("propose_relocation").And.Contain("apply_change");
            text.Should().Contain("style of the existing");
        }
    }

    [Fact]
    public async Task Instructions_state_the_two_step_rule()
    {
        var client = await ConnectAsync(new RecordingChannel());

        var instructions = client.ServerInstructions;

        instructions.Should().Be(McpInstructions.Text);
        instructions.Should().Contain("proposalId").And.Contain("apply_change");
        instructions.Should().Contain("Ask the user before").And.Contain("your own file tools");
        instructions.Should().Contain("metadata");
    }

    // ------------------------------------------------------------------ helpers

    private ControlService Service() => new(_fixture.Create(), _ui);

    /// <summary>A client connected to a bridge server (the options the bridge serves with) over in-memory streams.</summary>
    private async Task<McpClient> ConnectAsync(IControlChannel channel)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var transport = new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), McpBridge.ServerName);
        var server = McpServer.Create(transport, McpBridge.CreateOptions(channel));
        var running = server.RunAsync(_stop.Token);
        var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
            cancellationToken: _stop.Token);
        _cleanup.Add(transport);
        _cleanup.Add(server);
        _cleanup.Add(new Ignored(running));
        _cleanup.Add(client);
        return client;
    }

    private static IEnumerable<(string Name, bool Required)> Arguments(Tool tool)
    {
        var schema = tool.InputSchema;
        var required = schema.TryGetProperty("required", out var list)
            ? list.EnumerateArray().Select(e => e.GetString()!).ToHashSet()
            : [];
        return schema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(p => (p.Name, required.Contains(p.Name))).ToList()
            : [];
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static string PromptText(GetPromptResult result) =>
        string.Concat(result.Messages.Select(m => m.Content).OfType<TextContentBlock>().Select(block => block.Text));

    /// <summary>The control service in-process, as the pipe would reach it.</summary>
    private sealed class ServiceChannel(ControlService service) : IControlChannel
    {
        private long _id;

        public Task<ControlResponse> SendAsync(string op, object? args, CancellationToken ct) =>
            service.HandleAsync(new ControlRequest(Interlocked.Increment(ref _id).ToString(), op,
                JsonSerializer.SerializeToElement(args ?? new { }, ControlJson.Options)), ct);
    }

    /// <summary>Records each request's op and serialized args; answers <c>{}</c>.</summary>
    private sealed class RecordingChannel : IControlChannel
    {
        public List<(string Op, string Args)> Calls { get; } = [];

        public Task<ControlResponse> SendAsync(string op, object? args, CancellationToken ct)
        {
            lock (Calls)
                Calls.Add((op, JsonSerializer.Serialize(args ?? new { }, ControlJson.Options)));
            return Task.FromResult(ControlResponse.Success("1", new { }));
        }
    }

    /// <summary>
    /// Answers while <see cref="Answer"/> is set, else fails as a broken pipe does: after sending the request, or before
    /// it left (<see cref="NotSent"/>).
    /// </summary>
    private sealed class BrokenChannel : IControlChannel, IAsyncDisposable
    {
        public bool Answer { get; set; }
        public bool NotSent { get; init; }
        public bool Disposed { get; private set; }

        public Task<ControlResponse> SendAsync(string op, object? args, CancellationToken ct) => Answer
            ? Task.FromResult(ControlResponse.Success("1", new { }))
            : throw new ControlUnavailableException(ControlErrors.AppNotRunningMessage, notSent: NotSent);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A pipe-side handler that records the ops it received (and may act on one first); answers <c>{}</c>.</summary>
    private sealed class CountingHandler(Func<ControlRequest, Task>? before = null) : IControlHandler
    {
        private readonly List<string> _ops = [];

        public IReadOnlyList<string> Ops
        {
            get
            {
                lock (_ops)
                    return _ops.ToList();
            }
        }

        public async Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken ct)
        {
            lock (_ops)
                _ops.Add(request.Op);
            if (before is not null)
                await before(request);
            return ControlResponse.Success(request.Id, new { });
        }
    }

    /// <summary>Awaits the server loop at cleanup, ignoring its cancellation.</summary>
    private sealed class Ignored(Task task) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await task; } catch (OperationCanceledException) { }
        }
    }
}
