using System.Globalization;
using System.Text.Json;
using ReBackup.App.ViewModels;
using ReBackup.Control;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;

namespace ReBackup.App.Control;

/// <summary>
/// The app side of the control pipe: answers the <c>--mcp</c> bridge's requests from the app's own state and services.
/// The view models are read on the UI thread (posted to <c>ui</c>); index queries, scans and log reads run in the
/// background. Every answer is metadata: names, sizes, counts, dates, rules; never a file's content.
/// </summary>
public sealed partial class ControlService : IControlHandler
{
    /// <summary>Lists return at most this many rows; the rest is reported as truncated with a hint how to narrow.</summary>
    public const int MaxRows = 200;

    private readonly MainViewModel _main;
    private readonly SynchronizationContext _ui;
    private readonly Dictionary<string, Func<ControlArgs, CancellationToken, Task<object>>> _ops;

    /// <param name="main">The app's main view model: the plans, the scan cache, the indexes and run logs.</param>
    /// <param name="ui">The UI thread's context (the dispatcher's); the view models are read through it.</param>
    public ControlService(MainViewModel main, SynchronizationContext ui)
    {
        _main = main;
        _ui = ui;
        _ops = new(StringComparer.Ordinal)
        {
            [ControlOps.ListPlans] = async (_, ct) => await ListPlansAsync(ct),
            [ControlOps.ListVersions] = async (args, ct) => await ListVersionsAsync(args, ct),
            [ControlOps.GetIgnoreRules] = async (args, ct) => await GetIgnoreRulesAsync(args, ct),
            [ControlOps.ExplainPath] = async (args, ct) => await ExplainPathAsync(args, ct),
            [ControlOps.ListFiles] = async (args, ct) => await ListFilesAsync(args, ct),
            [ControlOps.GetRunHistory] = async (args, ct) => await GetRunHistoryAsync(args, ct),
        };
    }

    /// <summary>How long an operation that needs the source scan waits for it before answering <c>scan_running</c>.</summary>
    internal TimeSpan ScanWait { get; set; } = TimeSpan.FromSeconds(60);

    public async Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken ct)
    {
        if (!_ops.TryGetValue(request.Op, out var op))
            return ControlResponse.Failure(request.Id, ControlErrors.BadRequest, $"Unknown operation '{request.Op}'.");
        try
        {
            var result = await op(new ControlArgs(request.Args), ct).ConfigureAwait(false);
            return ControlResponse.Success(request.Id, result);
        }
        catch (ControlFailure failure)
        {
            return ControlResponse.Failure(request.Id, failure.Key, failure.Message);
        }
    }

    /// <summary>Runs <paramref name="read"/> on the UI thread (at once when already on it) and hands back its result.</summary>
    private Task<T> OnUiAsync<T>(Func<T> read)
    {
        if (SynchronizationContext.Current == _ui)
            return Task.FromResult(read());
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            try
            {
                done.SetResult(read());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null);
        return done.Task;
    }

    /// <summary>
    /// The saved plan the <c>plan</c> argument names: by id, else by exact name, both case-insensitive. Unsaved new
    /// plans do not count. No match, or several plans of that name, is <c>plan_not_found</c> naming the candidates.
    /// </summary>
    private Task<PlanContext> ResolvePlanAsync(ControlArgs args)
    {
        var key = args.RequiredString("plan").Trim();
        return OnUiAsync(() =>
        {
            var saved = _main.Plans.Where(p => !p.IsNew).Select(p => (Editor: p, Plan: p.SavedPlan())).ToList();
            var byId = saved.Where(p => p.Plan.Id.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
            var matches = byId.Count > 0 ? byId : saved.Where(p => p.Plan.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
                return ContextOf(matches[0].Editor, matches[0].Plan);
            if (matches.Count == 0)
                throw new ControlFailure(ControlErrors.PlanNotFound,
                    $"No plan has the id or name '{key}'. Plans: {Candidates(saved.Select(p => p.Plan))}.");
            throw new ControlFailure(ControlErrors.PlanNotFound,
                $"Several plans are named '{key}': {Candidates(matches.Select(p => p.Plan))}. Name the plan by its id.");
        });
    }

    private static string Candidates(IEnumerable<BackupPlan> plans)
    {
        var list = plans.Select(p => $"'{p.Name}' (id {p.Id})").ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list);
    }

    /// <summary>What the operations need of a plan, read on the UI thread.</summary>
    private PlanContext ContextOf(PlanEditorViewModel editor, BackupPlan plan) => new(
        plan,
        editor,
        _main.Describe(plan.Source),
        _main.Describe(plan.Target),
        StateOf(editor),
        _main.NextRunUtc(plan.Id),
        _main.GlobalIgnoreDefaultsNow());

    private static string StateOf(PlanEditorViewModel editor) =>
        editor.IsDeletingVersions ? "deleting"
        : editor.Run.State == JobState.Running ? "running"
        : editor.Run.State == JobState.Queued ? "queued"
        : "idle";

    /// <summary>An enum value as the results write it: camelCase ("completedWithWarnings").</summary>
    private static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    /// <summary>"1 file", "2 files" (invariant digits).</summary>
    private static string Count(long count, string noun) =>
        count.ToString("N0", CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? "" : "s");

    private static DateTime AsUtc(DateTime time) => DateTime.SpecifyKind(time, DateTimeKind.Utc);

    /// <summary>
    /// A plan as an operation sees it: the saved plan, its editor (UI thread only), the described locations, its state,
    /// next scheduled run and the global ignore defaults as they were when the request came.
    /// </summary>
    private sealed record PlanContext(BackupPlan Plan, PlanEditorViewModel Editor, string Source, string Target, string State,
        DateTime? NextRunUtc, IReadOnlyList<string> GlobalDefaults);
}

/// <summary>An operation's answer is an error: a stable key (<see cref="ControlErrors"/>) and an English message.</summary>
internal sealed class ControlFailure(string key, string message) : Exception(message)
{
    public string Key { get; } = key;
}

/// <summary>The <c>args</c> object of a request; a value of the wrong type is a <c>bad_request</c>.</summary>
internal readonly struct ControlArgs(JsonElement args)
{
    public string? OptionalString(string name)
    {
        if (!TryGet(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new ControlFailure(ControlErrors.BadRequest, $"The argument '{name}' must be a string.");
    }

    public string RequiredString(string name) =>
        OptionalString(name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ControlFailure(ControlErrors.BadRequest, $"The argument '{name}' is required.");

    public int? OptionalInt(string name)
    {
        if (!TryGet(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw new ControlFailure(ControlErrors.BadRequest, $"The argument '{name}' must be a whole number.");
    }

    /// <summary>The argument's value; false when it is missing or null.</summary>
    private bool TryGet(string name, out JsonElement value)
    {
        value = default;
        return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out value) &&
               value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }
}
