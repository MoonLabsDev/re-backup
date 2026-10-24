using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ReBackup.App.Localization;

namespace ReBackup.App.Mcp;

/// <summary>
/// The <c>--mcp</c> mode: an MCP server on stdin/stdout for a client that started the exe. It opens no window and takes no
/// mutex; stdout carries only the protocol, so nothing is logged.
/// </summary>
public static class McpBridge
{
    /// <summary>The name the server reports to clients, and the key in their configuration.</summary>
    public const string ServerName = "rebackup";

    /// <summary>Serves until the client closes stdin or <paramref name="ct"/> is cancelled; returns the process exit code.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Version = LocVersionExtension.Version },
            ToolCollection =
            [
                McpServerTool.Create(() => "pong", new McpServerToolCreateOptions
                {
                    Name = "ping",
                    Description = "Answers \"pong\"; shows that the ReBackup MCP server is reachable.",
                    ReadOnly = true,
                }),
            ],
        };

        await using var transport = new StdioServerTransport(options);
        await using var server = McpServer.Create(transport, options);
        await server.RunAsync(ct);
        return 0;
    }
}
