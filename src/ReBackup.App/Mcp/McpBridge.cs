using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ReBackup.App.Localization;
using ReBackup.Control;

namespace ReBackup.App.Mcp;

/// <summary>
/// The <c>--mcp</c> mode: an MCP server on stdin/stdout for a client that started the exe. It opens no window and takes no
/// mutex; stdout carries only the protocol, so nothing is logged. Each tool call is forwarded to the running app over the
/// control pipe, which is opened on the first call and reopened after it broke.
/// </summary>
public static class McpBridge
{
    /// <summary>The name the server reports to clients, and the key in their configuration.</summary>
    public const string ServerName = "rebackup";

    /// <summary>How long a tool call waits for the app's pipe before answering <c>app_not_running</c>.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Serves until the client closes stdin or <paramref name="ct"/> is cancelled; returns the process exit code.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        await using var connection = new ControlConnection(
            async connectCt => await ControlClient.ConnectAsync(null, ConnectTimeout, connectCt).ConfigureAwait(false),
            McpTools.ReadOps);
        var options = CreateOptions(connection);

        await using var transport = new StdioServerTransport(options);
        await using var server = McpServer.Create(transport, options);
        await server.RunAsync(ct);
        return 0;
    }

    /// <summary>The server: its tools forward to <paramref name="channel"/>, plus the prompt and the instructions.</summary>
    public static McpServerOptions CreateOptions(IControlChannel channel)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Version = LocVersionExtension.Version },
            ServerInstructions = McpInstructions.Text,
            ToolCollection = [],
            PromptCollection = [],
        };
        foreach (var tool in new McpTools(channel).Create())
            options.ToolCollection.Add(tool);
        foreach (var prompt in McpPrompts.Create())
            options.PromptCollection.Add(prompt);
        return options;
    }
}
