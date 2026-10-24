using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ReBackup.Control;

namespace ReBackup.App.Mcp;

/// <summary>
/// The MCP tools: one per control operation, with the operation's name and arguments, forwarded to the running app
/// through <paramref name="channel"/>. A result is the app's JSON as text; an error is a tool error
/// <c>"&lt;key&gt;: &lt;message&gt;"</c>. The descriptions are read by the model, so they are English and say the units.
/// </summary>
public sealed class McpTools(IControlChannel channel)
{
    /// <summary>The ops that only read: their tools are read-only, and they may be resent after a broken connection.</summary>
    public static IReadOnlySet<string> ReadOps { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ControlOps.ListPlans, ControlOps.ListVersions, ControlOps.CompareVersions, ControlOps.CompareSourceWithVersion,
        ControlOps.GetIgnoreRules, ControlOps.ExplainPath, ControlOps.ListFiles, ControlOps.GetRunHistory,
        ControlOps.GetRunStatus,
    };

    public const string ProtocolMismatchMessage =
        "ReBackup and its MCP bridge are different versions; restart Claude after updating ReBackup.";

    private const string PlanArg = "The plan's id or name (list_plans lists them).";
    private const string Metadata = "Metadata only, never file contents.";
    private const string Sizes = "Sizes are bytes plus a human-readable text.";
    private const string PathArg = "A folder relative to the plan's source root, with '/' separators; empty or left out for the root.";
    private const string TopArg = "How many folders and extensions to list (default 20, at most 200).";

    /// <summary>The tools, built from this instance's methods that carry <see cref="McpServerToolAttribute"/>.</summary>
    public IReadOnlyList<McpServerTool> Create() =>
        GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(method => McpServerTool.Create(method, this))
            .ToList();

    // ------------------------------------------------------------------ read

    [McpServerTool(Name = ControlOps.ListPlans, ReadOnly = true)]
    [Description("Lists the backup plans: id, name, source, target, kind (full or incremental), the last run, the next " +
                 "scheduled run (UTC) and the state (idle, queued, running or deleting). " + Metadata)]
    public Task<CallToolResult> ListPlans(CancellationToken ct) => CallAsync(ControlOps.ListPlans, null, ct);

    [McpServerTool(Name = ControlOps.ListVersions, ReadOnly = true)]
    [Description("Lists a plan's backup versions, newest first: name, date, size, file count, exclusive bytes (what only " +
                 "this version holds in a pool target), indexed or not, warnings. " + Sizes + " " + Metadata)]
    public Task<CallToolResult> ListVersions([Description(PlanArg)] string plan, CancellationToken ct) =>
        CallAsync(ControlOps.ListVersions, new { plan }, ct);

    [McpServerTool(Name = ControlOps.CompareVersions, ReadOnly = true)]
    [Description("Where the growth from version a to version b comes from: the totals, then the direct subfolders of " +
                 "'path' and the file extensions, largest first, each with added, changed and deleted file counts and " +
                 "added and deleted bytes. " + Sizes + " Drill down by calling again with 'path' set to a dominant folder. " +
                 Metadata)]
    public Task<CallToolResult> CompareVersions(
        [Description(PlanArg)] string plan,
        [Description("The older version's name (list_versions).")] string a,
        [Description("The newer version's name (list_versions).")] string b,
        [Description(PathArg)] string? path = null,
        [Description(TopArg)] int? top = null,
        CancellationToken ct = default) =>
        CallAsync(ControlOps.CompareVersions, new { plan, a, b, path, top }, ct);

    [McpServerTool(Name = ControlOps.CompareSourceWithVersion, ReadOnly = true)]
    [Description("What the next run would add: the source as it is now, with the plan's ignore rules, compared with a " +
                 "version (the newest by default), grouped like compare_versions by the direct subfolders of 'path' and " +
                 "by extension, in files and bytes. " + Sizes + " Needs a scan of the source; when the scan takes longer " +
                 "than a minute the answer is scan_running with its progress: retry shortly. " + Metadata)]
    public Task<CallToolResult> CompareSourceWithVersion(
        [Description(PlanArg)] string plan,
        [Description("The version's name; the newest version when left out.")] string? version = null,
        [Description(PathArg)] string? path = null,
        [Description(TopArg)] int? top = null,
        CancellationToken ct = default) =>
        CallAsync(ControlOps.CompareSourceWithVersion, new { plan, version, path, top }, ct);

    [McpServerTool(Name = ControlOps.GetIgnoreRules, ReadOnly = true)]
    [Description("A plan's ignore rules: its own patterns, the global defaults and whether the plan uses them, and every " +
                 ".backupignore file in the source with its text; for each line the entries it ignores and their bytes. " +
                 "Read these before suggesting rules, to match their notation and placement. " + Sizes + " The rule files " +
                 "are the only text returned; never other file contents.")]
    public Task<CallToolResult> GetIgnoreRules(
        [Description(PlanArg)] string plan,
        [Description("Only the .backupignore files in this folder and below (relative to the source root, '/' separators).")]
        string? path = null,
        CancellationToken ct = default) =>
        CallAsync(ControlOps.GetIgnoreRules, new { plan, path }, ct);

    [McpServerTool(Name = ControlOps.ExplainPath, ReadOnly = true)]
    [Description("Whether one entry of the source is ignored, and the rule that decides it: the pattern, its origin (the " +
                 "plan, the global defaults or a .backupignore) and line, or the ignored folder that contains it. " + Metadata)]
    public Task<CallToolResult> ExplainPath(
        [Description(PlanArg)] string plan,
        [Description("The entry, relative to the source root with '/' separators; end a folder with '/' when it may not exist.")]
        string path,
        CancellationToken ct) =>
        CallAsync(ControlOps.ExplainPath, new { plan, path }, ct);

    [McpServerTool(Name = ControlOps.ListFiles, ReadOnly = true)]
    [Description("Lists one folder of the source or of a version: name, kind (file or folder), size, modification date " +
                 "(UTC), file count of a folder, extension, and for the source whether the entry is ignored and by which " +
                 "rule. " + Sizes + " At most 200 entries. " + Metadata)]
    public Task<CallToolResult> ListFiles(
        [Description(PlanArg)] string plan,
        [Description("The folder, relative to the source or version root with '/' separators; the root when left out.")]
        string? path = null,
        [Description("'source' (default) for the source as it is now, or a version's name.")] string? @in = null,
        [Description("'name' (default; folders first), 'size' (largest first) or 'date' (newest first).")] string? sort = null,
        [Description("How many entries to return (default and at most 200).")] int? limit = null,
        CancellationToken ct = default) =>
        CallAsync(ControlOps.ListFiles, new { plan, path, @in, sort, limit }, ct);

    [McpServerTool(Name = ControlOps.GetRunHistory, ReadOnly = true)]
    [Description("A plan's last runs, newest first: run id, trigger (manual, scheduled, catchUp, mcp), start and end (UTC), " +
                 "duration in milliseconds, result, version, files copied, bytes copied, skipped count, warnings. " + Sizes)]
    public Task<CallToolResult> GetRunHistory(
        [Description(PlanArg)] string plan,
        [Description("How many runs (default 20, at most 200).")] int? n = null,
        CancellationToken ct = default) =>
        CallAsync(ControlOps.GetRunHistory, new { plan, n }, ct);

    [McpServerTool(Name = ControlOps.GetRunStatus, ReadOnly = true)]
    [Description("The state of a run started with start_backup: queued (with its queue position), running (with phase, " +
                 "files and bytes done of total, fraction 0–1) or finished (with its result). " + Sizes)]
    public Task<CallToolResult> GetRunStatus([Description("The runId that start_backup returned.")] string runId, CancellationToken ct) =>
        CallAsync(ControlOps.GetRunStatus, new { runId }, ct);

    // ------------------------------------------------------------------ write

    [McpServerTool(Name = ControlOps.PreviewIgnoreChange, ReadOnly = false, Destructive = false)]
    [Description("Previews adding and removing patterns in a plan's own ignore patterns, changing nothing: the files and " +
                 "bytes that become ignored and that become included, with the largest examples, the plan's patterns " +
                 "after the change, and a proposalId. Show the numbers to the user; apply_change(proposalId) makes the " +
                 "change, within 5 minutes. " + Sizes + " " + Metadata)]
    public Task<CallToolResult> PreviewIgnoreChange(
        [Description(PlanArg)] string plan,
        [Description("Pattern lines to add, in .gitignore syntax like the existing rules.")] string[]? add = null,
        [Description("Existing pattern lines of the plan to remove, exactly as get_ignore_rules lists them.")] string[]? remove = null,
        CancellationToken ct = default) =>
        CallAsync(ControlOps.PreviewIgnoreChange, new { plan, add, remove }, ct);

    [McpServerTool(Name = ControlOps.ProposeRelocation, ReadOnly = false, Destructive = false)]
    [Description("Proposes moving ignore rules between a plan's own patterns and a .backupignore in its folder source, " +
                 "changing nothing. Either 'integrate' (move all lines of one .backupignore into the plan) or 'extract' " +
                 "with 'folder' (move plan lines into that folder's .backupignore). Returns the diffs of both, lines that " +
                 "cannot move with the reason, whether the result ignores exactly the same entries, and a proposalId when " +
                 "something can move; apply_change(proposalId) carries it out, within 5 minutes.")]
    public Task<CallToolResult> ProposeRelocation(
        [Description(PlanArg)] string plan,
        [Description("The .backupignore to fold into the plan (or its folder), relative to the source root.")] string? integrate = null,
        [Description("The plan's pattern lines to move, exactly as listed; an empty list moves every line that fits 'folder'.")]
        string[]? extract = null,
        [Description("With 'extract': the folder, relative to the source root, whose .backupignore takes the lines.")] string? folder = null,
        CancellationToken ct = default) =>
        CallAsync(ControlOps.ProposeRelocation, new { plan, integrate, extract, folder }, ct);

    [McpServerTool(Name = ControlOps.ApplyChange, ReadOnly = false, Destructive = true)]
    [Description("Carries out a proposal from preview_ignore_change or propose_relocation: changes the plan's ignore " +
                 "patterns and, for a relocation, writes the .backupignore (or moves an emptied one to the recycle bin). Only after the user has seen the " +
                 "proposal's numbers and agreed. Fails with proposal_unknown, proposal_expired (older than 5 minutes) or " +
                 "proposal_stale (the plan or file changed since): make a new proposal then.")]
    public Task<CallToolResult> ApplyChange([Description("The proposalId the proposal returned.")] string proposalId, CancellationToken ct) =>
        CallAsync(ControlOps.ApplyChange, new { proposalId }, ct);

    [McpServerTool(Name = ControlOps.StartBackup, ReadOnly = false, Destructive = false)]
    [Description("Starts a backup run of the plan now (queued behind other runs) and returns its runId for " +
                 "get_run_status. Fails with plan_running when a run or a deletion of versions is active.")]
    public Task<CallToolResult> StartBackup([Description(PlanArg)] string plan, CancellationToken ct) =>
        CallAsync(ControlOps.StartBackup, new { plan }, ct);

    // ------------------------------------------------------------------ forwarding

    private async Task<CallToolResult> CallAsync(string op, object? args, CancellationToken ct)
    {
        try
        {
            var response = await channel.SendAsync(op, args, ct).ConfigureAwait(false);
            return response is { Ok: true }
                ? Text(response.Result?.GetRawText() ?? "null", isError: false)
                : Error(response.Error?.Key ?? ControlErrors.InternalError, response.Error?.Message ?? "The app sent no error.");
        }
        catch (ControlUnavailableException)
        {
            return Error(ControlErrors.AppNotRunning, ControlErrors.AppNotRunningMessage);
        }
        catch (ControlProtocolException ex)
        {
            return Error(ControlErrors.ProtocolMismatch, ProtocolMismatchMessage + " Detail: " + ex.Message);
        }
    }

    private static CallToolResult Error(string key, string message) => Text(key + ": " + message, isError: true);

    private static CallToolResult Text(string text, bool isError) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError,
    };
}
