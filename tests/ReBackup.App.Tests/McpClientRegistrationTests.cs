using FluentAssertions;
using ReBackup.App.Control;

namespace ReBackup.App.Tests;

/// <summary>Registering ReBackup's bridge with Claude Code and Codex: remove the old entry, add this exe.</summary>
public class McpClientRegistrationTests
{
    private const string Exe = @"C:\Program Files\ReBackup\ReBackup-2.3.2-win-x64.exe";

    [Fact]
    public async Task Claude_Code_gets_the_old_entry_removed_and_this_exe_added_for_every_project()
    {
        var runner = new FakeRunner();

        var result = await McpClientRegistration.RegisterAsync(runner, McpClientTool.ClaudeCode, Exe);

        result.Ok.Should().BeTrue();
        runner.Lines.Should().Equal(
            "claude mcp remove rebackup --scope user",
            $"claude mcp add rebackup --scope user -- \"{Exe}\" --mcp");
    }

    [Fact]
    public async Task Codex_gets_the_same_through_its_own_tool()
    {
        var runner = new FakeRunner();

        await McpClientRegistration.RegisterAsync(runner, McpClientTool.Codex, Exe);

        runner.Lines.Should().Equal("codex mcp remove rebackup", $"codex mcp add rebackup -- \"{Exe}\" --mcp");
    }

    [Fact]
    public async Task Nothing_to_remove_is_no_failure()
    {
        var runner = new FakeRunner { ["claude mcp remove"] = new CommandResult(1, "No MCP server found with name: rebackup") };

        var result = await McpClientRegistration.RegisterAsync(runner, McpClientTool.ClaudeCode, Exe);

        result.Ok.Should().BeTrue();
        runner.Lines.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_missing_tool_is_reported_as_not_installed()
    {
        var runner = new FakeRunner { ["codex mcp remove"] = new CommandResult(9009, "'codex' is not recognized") };

        var result = await McpClientRegistration.RegisterAsync(runner, McpClientTool.Codex, Exe);

        result.Should().Be(RegistrationResult.NotInstalled);
        runner.Lines.Should().ContainSingle("nothing is added without the tool");
    }

    [Fact]
    public async Task A_failed_add_reports_the_tool_output()
    {
        var runner = new FakeRunner { ["claude mcp add"] = new CommandResult(1, "error: config is read-only") };

        var result = await McpClientRegistration.RegisterAsync(runner, McpClientTool.ClaudeCode, Exe);

        result.Ok.Should().BeFalse();
        result.ToolMissing.Should().BeFalse();
        result.Error.Should().Be("error: config is read-only");
    }

    /// <summary>Answers every command line with exit code 0, or with the result set for its beginning.</summary>
    private sealed class FakeRunner : ICommandRunner
    {
        private readonly Dictionary<string, CommandResult> _results = [];

        public List<string> Lines { get; } = [];

        public CommandResult this[string prefix]
        {
            set => _results[prefix] = value;
        }

        public Task<CommandResult> RunAsync(string commandLine, CancellationToken ct)
        {
            Lines.Add(commandLine);
            var match = _results.FirstOrDefault(r => commandLine.StartsWith(r.Key, StringComparison.Ordinal));
            return Task.FromResult(match.Value ?? new CommandResult(0, ""));
        }
    }
}
