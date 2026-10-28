using System.Diagnostics;
using System.Text;

namespace ReBackup.App.Control;

/// <summary>An MCP client whose command line tool can register ReBackup's bridge.</summary>
public enum McpClientTool { ClaudeCode, Codex }

/// <summary>The exit code and the combined output of a command line.</summary>
public sealed record CommandResult(int ExitCode, string Output);

/// <summary>Runs a command line the way a console would (PATH, <c>.cmd</c> shims of npm tools).</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string commandLine, CancellationToken ct);
}

/// <summary>Runs a command line through <c>cmd.exe</c> without a window.</summary>
public sealed class ShellCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string commandLine, CancellationToken ct)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            // /s: the outer quotes go, the line keeps its own quotes (the exe path may hold spaces).
            Arguments = "/d /s /c \"" + commandLine + "\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return new CommandResult(process.ExitCode, ((await output) + (await error)).Trim());
    }
}

/// <summary>
/// Registers ReBackup's bridge (<c>ReBackup.exe --mcp</c>) with an MCP client's command line tool: the old entry
/// <c>rebackup</c> is removed first, so that a client that still starts an older exe (each version has its own file)
/// starts this one from now on.
/// </summary>
public static class McpClientRegistration
{
    public const string ServerName = "rebackup";

    /// <summary>cmd.exe's exit code for a command it cannot find.</summary>
    private const int CommandNotFound = 9009;

    /// <summary>The name of the client's command line tool.</summary>
    public static string Tool(McpClientTool client) => client == McpClientTool.ClaudeCode ? "claude" : "codex";

    /// <summary>The command lines that remove the old entry and add the new one.</summary>
    public static (string Remove, string Add) Commands(McpClientTool client, string exePath) => client switch
    {
        // --scope user: in every project, as the README describes.
        McpClientTool.ClaudeCode => ($"claude mcp remove {ServerName} --scope user",
            $"claude mcp add {ServerName} --scope user -- \"{exePath}\" --mcp"),
        _ => ($"codex mcp remove {ServerName}", $"codex mcp add {ServerName} -- \"{exePath}\" --mcp"),
    };

    /// <summary>Removes and adds the entry; the result says whether it worked, and why not.</summary>
    public static async Task<RegistrationResult> RegisterAsync(ICommandRunner runner, McpClientTool client, string exePath,
        CancellationToken ct = default)
    {
        var (remove, add) = Commands(client, exePath);
        // Not registered yet is fine; only a missing tool stops here.
        var removed = await runner.RunAsync(remove, ct).ConfigureAwait(false);
        if (removed.ExitCode == CommandNotFound)
            return RegistrationResult.NotInstalled;
        var added = await runner.RunAsync(add, ct).ConfigureAwait(false);
        return added.ExitCode == 0 ? RegistrationResult.Registered
            : added.ExitCode == CommandNotFound ? RegistrationResult.NotInstalled
            : RegistrationResult.Failed(added.Output);
    }
}

/// <summary>The outcome of <see cref="McpClientRegistration.RegisterAsync"/>; <see cref="Error"/> holds the tool's output on failure.</summary>
public sealed record RegistrationResult(bool Ok, bool ToolMissing, string? Error)
{
    public static readonly RegistrationResult Registered = new(true, false, null);
    public static readonly RegistrationResult NotInstalled = new(false, true, null);
    public static RegistrationResult Failed(string output) => new(false, false, output);
}
