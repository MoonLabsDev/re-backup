using System.IO;
using System.Text.Json;
using FluentAssertions;
using ReBackup.App.Control;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Control;
using ReBackup.Core.Settings;

namespace ReBackup.App.Tests;

/// <summary>Settings → "Allow control via MCP": the switch hosts and releases the control pipe; "Copy configuration".</summary>
public class McpSettingsTests : IAsyncDisposable
{
    private readonly MainViewModelFixture _fixture = new();
    private readonly string _pipe = "ReBackup.Control.Test." + Guid.NewGuid().ToString("N");
    private readonly McpControlHost _host;
    private readonly List<string> _clipboard = [];

    public McpSettingsTests()
    {
        _host = new McpControlHost(new EchoHandler(), "9.9.9", _pipe);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _fixture.Dispose();
    }

    private SettingsViewModel Create(IMcpControlHost? host = null) =>
        new(_fixture.SettingsStore, _fixture.Settings, _fixture.Paths, _fixture.Root, _fixture.Dialogs,
            () => true, () => false, _ => { }, "en-US", _ => { },
            mcpControl: host ?? _host, copyToClipboard: _clipboard.Add);

    [Fact]
    public void Mcp_control_is_off_by_default()
    {
        new AppSettings().AllowMcpControl.Should().BeFalse();
        var settings = Create();

        settings.AllowMcpControl.Should().BeFalse();
        _host.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Turning_it_on_hosts_the_pipe_and_off_stops_it()
    {
        var settings = Create();

        settings.AllowMcpControl = true;
        await settings.McpApplyTask;

        await using (var client = await ControlClient.ConnectAsync(_pipe, TimeSpan.FromSeconds(5), default))
            client.AppVersion.Should().Be("9.9.9");

        settings.AllowMcpControl = false;
        await settings.McpApplyTask;

        var act = () => ControlClient.ConnectAsync(_pipe, TimeSpan.FromMilliseconds(300), default);
        await act.Should().ThrowAsync<Exception>("the pipe is gone");
        _host.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Closing_without_saving_puts_the_pipe_back_to_the_saved_setting()
    {
        var settings = Create();
        settings.AllowMcpControl = true;
        await settings.McpApplyTask;

        settings.OnClosed();
        await settings.McpApplyTask;

        _host.IsRunning.Should().BeFalse();
        _fixture.Settings.AllowMcpControl.Should().BeFalse();
    }

    [Fact]
    public async Task Saving_keeps_the_pipe_and_stores_the_setting()
    {
        var settings = Create();
        settings.AllowMcpControl = true;
        await settings.McpApplyTask;

        settings.SaveCommand.Execute(null);
        settings.OnClosed();
        await settings.McpApplyTask;

        _host.IsRunning.Should().BeTrue();
        _fixture.Settings.AllowMcpControl.Should().BeTrue();
    }

    [Fact]
    public async Task A_taken_pipe_name_is_reported_and_the_switch_goes_back_off()
    {
        await using var squatter = new McpControlHost(new EchoHandler(), "1", _pipe);
        (await squatter.ApplyAsync(true)).Should().BeNull();
        var settings = Create();

        settings.AllowMcpControl = true;
        await settings.McpApplyTask;

        settings.AllowMcpControl.Should().BeFalse();
        _fixture.Dialogs.Errors.Should().ContainSingle();
        _host.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Copy_configuration_puts_the_exe_path_json_on_the_clipboard()
    {
        var settings = Create();

        settings.CopyMcpConfigurationCommand.Execute(null);

        var json = _clipboard.Should().ContainSingle().Subject;
        var command = JsonDocument.Parse(json).RootElement.GetProperty("mcpServers").GetProperty("rebackup");
        command.GetProperty("command").GetString().Should().Be(Environment.ProcessPath);
        command.GetProperty("args").EnumerateArray().Select(a => a.GetString()).Should().Equal("--mcp");
        json.Should().Be("{\"mcpServers\":{\"rebackup\":{\"command\":" + JsonSerializer.Serialize(Environment.ProcessPath)
            + ",\"args\":[\"--mcp\"]}}}");
        settings.McpCopied.Should().BeTrue();
    }

    [Fact]
    public void Setting_round_trips()
    {
        _fixture.Settings.AllowMcpControl = true;
        var path = Path.Combine(_fixture.Root, "mcp-settings.json");
        var store = new SettingsStore(path);

        store.Save(_fixture.Settings);

        store.Load().AllowMcpControl.Should().BeTrue();
    }

    private sealed class EchoHandler : IControlHandler
    {
        public Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken ct) =>
            Task.FromResult(ControlResponse.Success(request.Id, 1));
    }
}
