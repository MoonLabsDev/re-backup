using System.IO;
using FluentAssertions;
using ReBackup.App.Control;
using ReBackup.App.Tests.TestSupport;
using Xunit;

namespace ReBackup.App.Tests;

public sealed class StaleRegistrationsTests
{
    private const string Current = @"E:\dist\ReBackup-2.5.0-win-x64.exe";
    private const string Old = @"E:\dist\ReBackup-2.4.0-win-x64.exe";
    private const string Gone = @"E:\dist\ReBackup-2.2.2-win-x64.exe";
    private const string Claude = @"C:\Users\me\.claude.json";
    private const string Codex = @"C:\Users\me\.codex\config.toml";
    private static readonly Version Running = new(2, 5, 0, 0);

    // ---- reading the registered exe

    [Theory]
    [InlineData("\"E:\\dist\\ReBackup-2.4.0-win-x64.exe\" --minimized", Old)]
    [InlineData("\"E:\\dist\\ReBackup-2.4.0-win-x64.exe\"", Old)]
    [InlineData("E:\\dist\\ReBackup-2.4.0-win-x64.exe --minimized", Old)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_autostart_value_gives_its_exe(string? value, string? expected)
    {
        RegisteredExes.FromRunValue(value).Should().Be(expected);
    }

    [Fact]
    public void Claude_Code_gives_the_user_scope_command()
    {
        const string json = """
            { "projects": { "x": { "mcpServers": { "rebackup": { "command": "C:\\project.exe" } } } },
              "mcpServers": { "other": { "command": "npx" },
                              "rebackup": { "type": "stdio", "command": "E:\\dist\\ReBackup-2.4.0-win-x64.exe", "args": ["--mcp"] } } }
            """;

        RegisteredExes.FromClaudeJson(json).Should().Be(Old);
    }

    [Theory]
    [InlineData("""{ "mcpServers": { "other": { "command": "npx" } } }""")]
    [InlineData("""{ "numStartups": 3 }""")]
    [InlineData("not json")]
    public void Claude_Code_without_an_entry_gives_nothing(string json)
    {
        RegisteredExes.FromClaudeJson(json).Should().BeNull();
    }

    [Fact]
    public void Codex_gives_a_literal_string_command()
    {
        const string toml = """
            [mcp_servers.playwright]
            command = "npx"

            [mcp_servers.rebackup]
            command = 'E:\dist\ReBackup-2.4.0-win-x64.exe'
            args = ["--mcp"]

            [shell_environment_policy.set]
            command = 'C:\not-this.exe'
            """;

        RegisteredExes.FromCodexToml(toml).Should().Be(Old);
    }

    [Theory]
    [InlineData("[mcp_servers.rebackup]\ncommand = \"E:\\\\dist\\\\ReBackup-2.4.0-win-x64.exe\"  # comment")]
    [InlineData("[mcp_servers.\"rebackup\"]\r\ncommand=\"E:\\\\dist\\\\ReBackup-2.4.0-win-x64.exe\"")]
    [InlineData("[ mcp_servers.rebackup ]\nargs = []\ncommand = 'E:\\dist\\ReBackup-2.4.0-win-x64.exe'")]
    public void Codex_reads_basic_strings_and_header_variants(string toml)
    {
        RegisteredExes.FromCodexToml(toml).Should().Be(Old);
    }

    [Theory]
    [InlineData("[mcp_servers.playwright]\ncommand = \"npx\"")]
    [InlineData("[mcp_servers.rebackup.env]\ncommand = 'E:\\x.exe'")]
    [InlineData("")]
    public void Codex_without_an_entry_gives_nothing(string toml)
    {
        RegisteredExes.FromCodexToml(toml).Should().BeNull();
    }

    // ---- what counts as stale

    [Fact]
    public void An_older_exe_is_stale()
    {
        var exes = new FakeExes { [Old] = new Version(2, 4, 0, 0) };

        StaleRegistrations.Check(RegistrationKind.ClaudeCode, Old, Current, Running, exes)
            .Should().Be(new StaleRegistration(RegistrationKind.ClaudeCode, Old, new Version(2, 4, 0)));
    }

    [Fact]
    public void A_missing_exe_is_stale()
    {
        StaleRegistrations.Check(RegistrationKind.Codex, Gone, Current, Running, new FakeExes())
            .Should().Be(new StaleRegistration(RegistrationKind.Codex, Gone, null));
    }

    [Theory]
    [InlineData(2, 5, 0)]
    [InlineData(2, 6, 0)]
    public void The_same_or_a_newer_version_elsewhere_is_not_stale(int major, int minor, int build)
    {
        var exes = new FakeExes { [Old] = new Version(major, minor, build, 0) };

        StaleRegistrations.Check(RegistrationKind.Autostart, Old, Current, Running, exes).Should().BeNull();
    }

    [Fact]
    public void This_exe_is_not_stale_whatever_its_case()
    {
        StaleRegistrations.Check(RegistrationKind.Autostart, Current.ToUpperInvariant(), Current, Running, new FakeExes())
            .Should().BeNull();
    }

    [Fact]
    public void An_exe_without_a_version_is_left_alone()
    {
        var exes = new FakeExes { [Old] = null };

        StaleRegistrations.Check(RegistrationKind.Autostart, Old, Current, Running, exes).Should().BeNull();
    }

    [Fact]
    public void No_registration_is_not_stale()
    {
        StaleRegistrations.Check(RegistrationKind.Autostart, null, Current, Running, new FakeExes()).Should().BeNull();
    }

    // ---- finding all of them

    [Fact]
    public void Find_lists_every_stale_entry_in_order()
    {
        var sources = Sources(out var runKey, out _, out var exes);
        runKey.Values["ReBackup"] = "\"" + Old + "\" --minimized";
        exes[Old] = new Version(2, 4, 0, 0);

        var stale = StaleRegistrations.Find(sources, autostart: true, mcp: true, Current, Running);

        stale.Select(s => s.Kind).Should().Equal(RegistrationKind.Autostart, RegistrationKind.ClaudeCode, RegistrationKind.Codex);
        stale[2].Version.Should().BeNull();
    }

    [Fact]
    public void Find_skips_autostart_and_MCP_when_they_are_off()
    {
        var sources = Sources(out var runKey, out _, out var exes);
        runKey.Values["ReBackup"] = "\"" + Old + "\" --minimized";
        exes[Old] = new Version(2, 4, 0, 0);

        StaleRegistrations.Find(sources, autostart: false, mcp: false, Current, Running).Should().BeEmpty();
    }

    [Fact]
    public void Find_ignores_config_files_that_are_missing_or_unreadable()
    {
        var sources = Sources(out _, out var files, out _);
        files.Clear();
        sources = sources with { ReadFile = path => path == Codex ? throw new IOException("locked") : null };

        StaleRegistrations.Find(sources, autostart: true, mcp: true, Current, Running).Should().BeEmpty();
    }

    // ---- switching

    [Fact]
    public async Task Switch_points_every_entry_at_this_exe()
    {
        var runKey = new InMemoryRunKey { Values = { ["ReBackup"] = "\"" + Old + "\" --minimized" } };
        var commands = new RecordingCommands();
        var stale = new[]
        {
            new StaleRegistration(RegistrationKind.Autostart, Old, new Version(2, 4, 0)),
            new StaleRegistration(RegistrationKind.ClaudeCode, Old, new Version(2, 4, 0)),
            new StaleRegistration(RegistrationKind.Codex, Gone, null),
        };

        var failures = await StaleRegistrations.SwitchAsync(stale, Current, runKey, commands);

        failures.Should().BeEmpty();
        runKey.Values["ReBackup"].Should().Be("\"" + Current + "\" --minimized");
        commands.Lines.Should().Contain($"claude mcp add rebackup --scope user -- \"{Current}\" --mcp")
            .And.Contain($"codex mcp add rebackup -- \"{Current}\" --mcp");
    }

    [Fact]
    public async Task Switch_reports_what_failed_and_goes_on()
    {
        var commands = new RecordingCommands { Fail = line => line.StartsWith("claude mcp add") };
        var stale = new[]
        {
            new StaleRegistration(RegistrationKind.ClaudeCode, Old, new Version(2, 4, 0)),
            new StaleRegistration(RegistrationKind.Codex, Old, new Version(2, 4, 0)),
        };

        var failures = await StaleRegistrations.SwitchAsync(stale, Current, new InMemoryRunKey(), commands);

        failures.Should().ContainSingle().Which.Kind.Should().Be(RegistrationKind.ClaudeCode);
        failures[0].Result.Error.Should().Be("boom");
        commands.Lines.Should().Contain(l => l.StartsWith("codex mcp add"));
    }

    private StaleCheckSources Sources(out InMemoryRunKey runKey, out Dictionary<string, string> files, out FakeExes exes)
    {
        runKey = new InMemoryRunKey();
        files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Claude] = """{ "mcpServers": { "rebackup": { "command": "E:\\dist\\ReBackup-2.4.0-win-x64.exe" } } }""",
            [Codex] = "[mcp_servers.rebackup]\ncommand = 'E:\\dist\\ReBackup-2.2.2-win-x64.exe'\n",
        };
        exes = new FakeExes();
        var read = files;
        return new StaleCheckSources(runKey, path => read.TryGetValue(path, out var text) ? text : null, exes, Claude, Codex);
    }

    private sealed class FakeExes : Dictionary<string, Version?>, IExeInfo
    {
        public FakeExes() : base(StringComparer.OrdinalIgnoreCase) { }

        public bool Exists(string path) => ContainsKey(path);

        public Version? VersionOf(string path) => TryGetValue(path, out var v) ? v : null;
    }

    private sealed class RecordingCommands : ICommandRunner
    {
        public List<string> Lines { get; } = [];

        public Func<string, bool> Fail { get; init; } = _ => false;

        public Task<CommandResult> RunAsync(string commandLine, CancellationToken ct)
        {
            Lines.Add(commandLine);
            return Task.FromResult(Fail(commandLine) ? new CommandResult(1, "boom") : new CommandResult(0, ""));
        }
    }
}
