using System.Text.Json;

namespace ReBackup.App.Control;

/// <summary>The JSON an MCP client (Claude Desktop, Claude Code) needs to start ReBackup's bridge.</summary>
public static class McpClientConfiguration
{
    /// <summary>The <c>mcpServers</c> entry <c>rebackup</c>: the exe path (escaped for JSON) and the argument <c>--mcp</c>.</summary>
    public static string Json(string exePath) =>
        "{\"mcpServers\":{\"rebackup\":{\"command\":" + JsonSerializer.Serialize(exePath) + ",\"args\":[\"--mcp\"]}}}";
}
