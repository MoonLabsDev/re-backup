using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using ReBackup.App.Services;
using ReBackup.Shared.Wpf.Services;

namespace ReBackup.App.Control;

/// <summary>A place that starts ReBackup's exe: Windows' autostart, or an MCP client's registration of the bridge.</summary>
public enum RegistrationKind { Autostart, ClaudeCode, Codex }

/// <summary>
/// An entry that starts an older ReBackup: <see cref="ExePath"/> is what it starts, <see cref="Version"/> that exe's
/// version (null when the file no longer exists).
/// </summary>
public sealed record StaleRegistration(RegistrationKind Kind, string ExePath, Version? Version);

/// <summary>An entry <see cref="StaleRegistrations.SwitchAsync"/> could not switch, with the reason.</summary>
public sealed record SwitchFailure(RegistrationKind Kind, RegistrationResult Result);

/// <summary>Whether an exe exists and its file version (seam: tests use a fake, never the disk).</summary>
public interface IExeInfo
{
    bool Exists(string path);

    /// <summary>The file version; null when the file has none (or cannot be read).</summary>
    Version? VersionOf(string path);
}

/// <summary>The exe files on disk.</summary>
public sealed class FileExeInfo : IExeInfo
{
    public bool Exists(string path) => File.Exists(path);

    public Version? VersionOf(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileMajorPart == 0 && info.FileMinorPart == 0 && info.FileBuildPart == 0
                ? null
                : new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Where the entries are read from: the Run key, the files (a missing file reads as null), the exes, and the paths of
/// Claude Code's <c>.claude.json</c> and Codex's <c>config.toml</c>.
/// </summary>
public sealed record StaleCheckSources(IRunKey RunKey, Func<string, string?> ReadFile, IExeInfo Exes,
    string ClaudeConfig, string CodexConfig)
{
    /// <summary>The real registry, files and exes of this Windows account.</summary>
    public static StaleCheckSources ForThisUser() => new(new RegistryRunKey(), ReadIfExists, new FileExeInfo(),
        RegisteredExes.ClaudeConfigPath(), RegisteredExes.CodexConfigPath());

    private static string? ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
}

/// <summary>Reads the exe an entry starts, from the Run key's value and the MCP clients' configuration files.</summary>
public static class RegisteredExes
{
    /// <summary>Claude Code's user configuration (<c>claude mcp add --scope user</c> writes there).</summary>
    public static string ClaudeConfigPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    /// <summary>Codex's configuration: <c>%CODEX_HOME%\config.toml</c>, by default in <c>.codex</c> of the profile.</summary>
    public static string CodexConfigPath()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return Path.Combine(home, "config.toml");
    }

    /// <summary>The exe of a Run value: <c>"C:\Path\ReBackup.exe" --minimized</c> (quoted, or up to the first space).</summary>
    public static string? FromRunValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        if (value[0] == '"')
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }
        var space = value.IndexOf(' ');
        return space < 0 ? value : value[..space];
    }

    /// <summary><c>mcpServers.rebackup.command</c> at the top of <c>.claude.json</c> (the user scope); null when absent.</summary>
    public static string? FromClaudeJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object
                && servers.TryGetProperty(McpClientRegistration.ServerName, out var server) && server.ValueKind == JsonValueKind.Object
                && server.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String
                ? command.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>command</c> in the table <c>[mcp_servers.rebackup]</c> of Codex's <c>config.toml</c>; null when absent.</summary>
    public static string? FromCodexToml(string toml)
    {
        var inTable = false;
        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inTable = IsReBackupTable(line);
                continue;
            }
            if (!inTable || !line.StartsWith("command", StringComparison.Ordinal))
                continue;
            var rest = line["command".Length..].TrimStart();
            if (!rest.StartsWith('='))
                continue;
            return StringValue(rest[1..].TrimStart());
        }
        return null;
    }

    /// <summary><c>[mcp_servers.rebackup]</c>, also with spaces or the name quoted; not its sub-tables (<c>.env</c>).</summary>
    private static bool IsReBackupTable(string header)
    {
        if (header.StartsWith("[[", StringComparison.Ordinal) || !header.EndsWith(']'))
            return false;
        var parts = header[1..^1].Split('.').Select(p => p.Trim().Trim('"', '\'')).ToArray();
        return parts is ["mcp_servers", McpClientRegistration.ServerName];
    }

    /// <summary>A TOML string: <c>'literal'</c> as is, <c>"basic"</c> with its escapes; anything else is null.</summary>
    private static string? StringValue(string value)
    {
        if (value.StartsWith('\''))
        {
            var end = value.IndexOf('\'', 1);
            return end > 0 ? value[1..end] : null;
        }
        if (!value.StartsWith('"'))
            return null;
        var text = new StringBuilder();
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '"')
                return text.ToString();
            if (c == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                text.Append(next switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => next });
                continue;
            }
            text.Append(c);
        }
        return null;
    }
}

/// <summary>
/// Finds the entries (autostart, Claude Code, Codex) that still start an older ReBackup, and switches them to the exe
/// that is running. Only older ones count: starting an older version or a test build never takes the entries over.
/// </summary>
public static class StaleRegistrations
{
    /// <summary>
    /// The entry starts an older ReBackup than <paramref name="current"/>, or an exe that no longer exists; null for
    /// this exe, the same or a newer version, an exe without a version, or no entry.
    /// </summary>
    public static StaleRegistration? Check(RegistrationKind kind, string? registered, string currentExe, Version current,
        IExeInfo exes)
    {
        if (string.IsNullOrWhiteSpace(registered) || SamePath(registered, currentExe))
            return null;
        if (!exes.Exists(registered))
            return new StaleRegistration(kind, registered, null);
        var version = exes.VersionOf(registered);
        if (version is null)
            return null;
        version = Normalize(version);
        return version < Normalize(current) ? new StaleRegistration(kind, registered, version) : null;
    }

    /// <summary>
    /// The stale entries, autostart first: autostart only when <paramref name="autostart"/> (StartWithWindows), the MCP
    /// clients only when <paramref name="mcp"/> (Allow control via MCP). A file that cannot be read counts as no entry.
    /// </summary>
    public static IReadOnlyList<StaleRegistration> Find(StaleCheckSources sources, bool autostart, bool mcp,
        string currentExe, Version current)
    {
        var found = new List<StaleRegistration>();
        void Add(RegistrationKind kind, Func<string?> registered)
        {
            string? path;
            try
            {
                path = registered();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return;
            }
            if (Check(kind, path, currentExe, current, sources.Exes) is { } stale)
                found.Add(stale);
        }

        if (autostart)
            Add(RegistrationKind.Autostart, () => RegisteredExes.FromRunValue(sources.RunKey.GetValue(AppStartup.ValueName)));
        if (mcp)
        {
            Add(RegistrationKind.ClaudeCode,
                () => sources.ReadFile(sources.ClaudeConfig) is { } json ? RegisteredExes.FromClaudeJson(json) : null);
            Add(RegistrationKind.Codex,
                () => sources.ReadFile(sources.CodexConfig) is { } toml ? RegisteredExes.FromCodexToml(toml) : null);
        }
        return found;
    }

    /// <summary>Points each entry at <paramref name="exePath"/>; what failed is returned, the others are switched anyway.</summary>
    public static async Task<IReadOnlyList<SwitchFailure>> SwitchAsync(IReadOnlyList<StaleRegistration> stale,
        string exePath, IRunKey runKey, ICommandRunner commands, CancellationToken ct = default)
    {
        var failures = new List<SwitchFailure>();
        foreach (var entry in stale)
        {
            RegistrationResult result;
            try
            {
                if (entry.Kind == RegistrationKind.Autostart)
                {
                    AppStartup.Apply(true, runKey, exePath);
                    result = RegistrationResult.Registered;
                }
                else
                {
                    var client = entry.Kind == RegistrationKind.ClaudeCode ? McpClientTool.ClaudeCode : McpClientTool.Codex;
                    result = await McpClientRegistration.RegisterAsync(commands, client, exePath, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = RegistrationResult.Failed(ex.Message);
            }
            if (!result.Ok)
                failures.Add(new SwitchFailure(entry.Kind, result));
        }
        return failures;
    }

    /// <summary>Major.Minor.Build: the file version's fourth part is always 0.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
