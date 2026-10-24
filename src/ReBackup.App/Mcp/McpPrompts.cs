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
            (null, null) => ("between the newest version and what the next run would back up (or between the last two " +
                             "updates, when the user means those)",
                $"compare_source_with_version(plan: {p}). If the user means the growth between the last two updates " +
                $"(backups), call list_versions(plan: {p}) first and instead call compare_versions with the two newest " +
                "versions (a: the second newest, b: the newest)"),
            _ => ($"between version {Quote(older ?? newer!)} and what the next run would back up",
                $"compare_source_with_version(plan: {p}, version: {Quote(older ?? newer!)})"),
        };
        return $"""
            Analyze where the growth of the ReBackup plan {p} comes from, {scope}, and suggest how to keep unneeded data out of the backup.

            1. Compare: call {compare}. Group the growth by folder and by extension. Where one folder dominates, call the same tool again with 'path' set to that folder and drill down until the contributors are specific; list_files with sort "size" shows the largest entries of a folder. For an incremental plan (list_plans shows the kind) look at the changed counts and copiedSize, not only net: a run copies a changed file whole, so a large file rewritten in place (a mailbox, a VM disk, a database, a rebuilt archive) costs its full size in every version although its net growth is ~0; the results are sorted by copiedSize for such plans.
            2. Judge each contributor from its metadata only (path, extension, file count, size, dates): caches, build output, logs, dumps, temporary files and duplicates are candidates for ignoring; documents, sources, photos and other media usually belong in the backup.
            3. Ask the user before looking at the content of any file. The ReBackup tools never return file contents; only when the user agrees, use your own file tools to look.
            4. Suggest ignore rules in the style of the existing ones: read get_ignore_rules first, use the same notation (for example 'dir/' versus '**/dir'), and place each rule where similar rules already live (the plan's own patterns, or the matching .backupignore). Back every suggestion with numbers, show them to the user, and call apply_change with a proposalId only after the user agrees:
               - a rule for the plan's own patterns: preview_ignore_change(add: [rule]) → apply_change;
               - a new rule for a .backupignore: it goes into the plan first and is then moved, preview_ignore_change(add: [rule]) → apply_change → propose_relocation(extract: [rule], folder: that folder) → apply_change;
               - existing rules that belong elsewhere: propose_relocation → apply_change.
               Never edit .backupignore files with your own file tools; only these tools change rules.
            """;
    }

    private static string? Given(string? version) => string.IsNullOrWhiteSpace(version) ? null : version.Trim();

    /// <summary>A value as a JSON string literal, as the model passes it to a tool.</summary>
    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
