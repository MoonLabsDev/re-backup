namespace ReBackup.App.Mcp;

/// <summary>The server instructions the client hands the model: what the tools can and cannot do, and the rules for using them.</summary>
public static class McpInstructions
{
    public const string Text = """
        These tools control the ReBackup backup app on this computer. The app must be running with "Allow control via MCP" turned on in its Settings; otherwise every tool answers app_not_running.

        - The tools return metadata only: paths, sizes, counts, extensions, dates, and the ignore rule that decides an entry. They never return file contents. Ask the user before looking at the content of any file, and only then use your own file tools to read it.
        - Every change is two steps. preview_ignore_change and propose_relocation change nothing: they return the effect (files and bytes, diffs) and a proposalId. Show the effect to the user, and call apply_change with that proposalId only after the user agrees. A proposal lives 5 minutes and goes stale when the plan or file changes meanwhile; then make a new one. start_backup starts a run at once and returns a runId for get_run_status.
        - A new rule for the plan's own patterns: preview_ignore_change(add: [rule]) → apply_change. A new rule for a .backupignore goes into the plan first and is then moved: preview_ignore_change(add: [rule]) → apply_change → propose_relocation(extract: [rule], folder: that folder) → apply_change (each apply_change only after the user agrees). propose_relocation alone moves existing rules between the plan and a .backupignore. Never edit .backupignore files with your own file tools.
        - Answers about the source come from a scan; scannedAt says when it was made. Pass rescan: true when files changed since then.
        - Name a plan by its id or its name (list_plans). Paths are relative to the plan's source root (or a version's root), with '/' separators.
        - Sizes are bytes plus a human-readable text. Answers are cut at 200 rows or about 50 000 characters; a cut answer says so and names the argument that narrows it (path, top, limit, n).
        - Errors read "<key>: <message>". scan_running and version_not_indexed mean the app is still working: retry shortly. app_not_running right after ReBackup restarted: retry once.
        - Not possible here: deleting versions, restoring files, and changing plan settings other than ignore patterns.
        """;
}
