using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReBackup.App.Mcp;

namespace ReBackup.App.Tests;

/// <summary>
/// The built app started with <c>--mcp</c>, as an MCP client starts it: redirected stdin and stdout carry the protocol,
/// and no window opens. Only ever started in this mode — the test never launches the app normally.
/// </summary>
public class McpProcessTests
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Mcp_mode_answers_initialize_and_lists_tools_over_stdio()
    {
        using var process = Process.Start(new ProcessStartInfo(AppExecutable(), "--mcp")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true,
        })!;
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
            tools["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).Should().Contain("ping");

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
