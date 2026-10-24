using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReBackup.App.Mcp;

namespace ReBackup.App.Tests;

/// <summary>
/// The built app started with <c>--mcp</c>, as an MCP client starts it: redirected stdin and stdout carry the protocol,
/// and no window opens. Only ever started in this mode — the test never launches the app normally. Every start sets
/// <see cref="TestGuard"/>, so that a regressed <c>--mcp</c> branch exits at once instead of starting the user's app.
/// </summary>
public class McpProcessTests
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The environment variable that makes a normal (non <c>--mcp</c>) start exit with code 3 at once.</summary>
    private const string TestGuard = App.McpTestGuard;

    private static readonly string[] SpecTools =
    [
        "list_plans", "list_versions", "compare_versions", "compare_source_with_version", "get_ignore_rules",
        "explain_path", "list_files", "get_run_history", "get_run_status",
        "preview_ignore_change", "propose_relocation", "apply_change", "start_backup",
    ];

    [Fact]
    public async Task Mcp_mode_answers_initialize_and_lists_tools_over_stdio()
    {
        using var process = Start("--mcp");
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            process.StandardInput.AutoFlush = true;

            await SendAsync(process, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"rebackup-tests","version":"1.0"}}}""");
            var initialize = await ReadReplyAsync(process, id: 1);
            initialize["result"]!["serverInfo"]!["name"]!.GetValue<string>().Should().Be("rebackup");

            await SendAsync(process, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await SendAsync(process, """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
            var tools = await ReadReplyAsync(process, id: 2);
            tools["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>())
                .Should().BeEquivalentTo(SpecTools, "listing tools does not need the app");

            process.Refresh();
            process.MainWindowHandle.Should().Be(IntPtr.Zero);

            process.StandardInput.Close();
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(exit.Token);
            process.ExitCode.Should().Be(0, "stderr: " + await stderr);
        }
        finally
        {
            // Only this --mcp child, started above, is ever killed.
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Test_guard_stops_a_normal_start_before_anything_else()
    {
        // A normal start, as a regressed --mcp branch would make it: the guard must end it before the mutex,
        // settings, tray or window. Never killed while it could be a real app: it exits by itself or the test fails.
        using var process = Start("");
        var exited = process.WaitForExitAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!exited.IsCompleted)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the guarded start exits within 10 s (quit it from the tray if not)");
            process.Refresh();
            WindowOf(process).Should().Be(IntPtr.Zero, "the guard exits before any window exists");
            await Task.WhenAny(exited, Task.Delay(50));
        }

        process.ExitCode.Should().Be(App.McpTestGuardExitCode);
    }

    /// <summary>The process's main window; none once it has exited.</summary>
    private static IntPtr WindowOf(Process process)
    {
        try
        {
            return process.MainWindowHandle;
        }
        catch (InvalidOperationException)
        {
            return IntPtr.Zero;   // exited meanwhile
        }
    }

    /// <summary>The app with redirected stdio and <see cref="TestGuard"/> set.</summary>
    private static Process Start(string arguments)
    {
        var info = new ProcessStartInfo(AppExecutable(), arguments)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true,
        };
        info.Environment[TestGuard] = "1";
        return Process.Start(info)!;
    }

    /// <summary>The app's exe, copied next to its assembly into this test's output folder.</summary>
    private static string AppExecutable()
    {
        var path = Path.ChangeExtension(typeof(McpBridge).Assembly.Location, ".exe");
        File.Exists(path).Should().BeTrue("the app's exe is copied into the test output: " + path);
        return path;
    }

    private static Task SendAsync(Process process, string message) => process.StandardInput.WriteLineAsync(message);

    /// <summary>The next reply that carries <paramref name="id"/>; notifications in between are skipped.</summary>
    private static async Task<JsonNode> ReadReplyAsync(Process process, int id)
    {
        using var timeout = new CancellationTokenSource(ReplyTimeout);
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token)
                ?? throw new InvalidOperationException("stdout closed before reply " + id);
            var message = JsonNode.Parse(line)!;
            if (message["id"]?.GetValue<int>() == id)
                return message;
        }
    }
}
