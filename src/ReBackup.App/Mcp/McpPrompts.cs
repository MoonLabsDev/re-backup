using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace ReBackup.App.Mcp;

/// <summary>The MCP prompts: <c>analyze_growth</c>, the guided growth analysis of the spec (section 4.3).</summary>
public static class McpPrompts
{
    public static IReadOnlyList<McpServerPrompt> Create() =>
        [McpServerPrompt.Create(typeof(McpPrompts).GetMethod(nameof(AnalyzeGrowth), BindingFlags.Public | BindingFlags.Static)!)];

    [McpServerPrompt(Name = "analyze_growth")]
    [Description("Find out where a plan's backup growth comes from and suggest ignore rules that fit the existing ones: " +
                 "between two versions, or between the newest (or one) version and what the next run would back up.")]
    public static string AnalyzeGrowth(
        [Description("The plan's id or name.")] string plan,
        [Description("The older version; with b, the two versions are compared. Alone: the source is compared with it.")]
        string? a = null,
        [Description("The newer version. Leave out a and b to compare the source with the newest version.")]
        string? b = null)
    {
        var p = Quote(plan);
        var (older, newer) = (Given(a), Given(b));
        var (scope, compare) = (older, newer) switch
        {
            ({ }, { }) => ($"between version {Quote(older)} and version {Quote(newer)}",
                $"compare_versions(plan: {p}, a: {Quote(older)}, b: {Quote(newer)})"),
            (null, null) => ("between the newest version and what the next run would back up",
                $"compare_source_with_version(plan: {p})"),
            _ => ($"between version {Quote(older ?? newer!)} and what the next run would back up",
                $"compare_source_with_version(plan: {p}, version: {Quote(older ?? newer!)})"),
        };
        return $"""
            Analyze where the growth of the ReBackup plan {p} comes from, {scope}, and suggest how to keep unneeded data out of the backup.

            1. Compare: call {compare}. Group the growth by folder and by extension. Where one folder dominates, call the same tool again with 'path' set to that folder and drill down until the contributors are specific; list_files with sort "size" shows the largest entries of a folder.
            2. Judge each contributor from its metadata only (path, extension, file count, size, dates): caches, build output, logs, dumps, temporary files and duplicates are candidates for ignoring; documents, sources, photos and other media usually belong in the backup.
            3. Ask the user before looking at the content of any file. The ReBackup tools never return file contents; only when the user agrees, use your own file tools to look.
            4. Suggest ignore rules in the style of the existing ones: read get_ignore_rules first, use the same notation (for example 'dir/' versus '**/dir'), and place each rule where similar rules already live (the plan's own patterns, or the matching .backupignore). Back every suggestion with numbers: preview_ignore_change for plan patterns, propose_relocation for moving rules between the plan and a .backupignore. Show those numbers to the user, and call apply_change with the proposalId only after the user agrees.
            """;
    }

    private static string? Given(string? version) => string.IsNullOrWhiteSpace(version) ? null : version.Trim();

    /// <summary>A value as a JSON string literal, as the model passes it to a tool.</summary>
    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
