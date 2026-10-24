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

    /// <summary>
    /// A serialized response (the envelope with its result) stays at or below this many characters; rows are dropped from
    /// the end of a result until it fits.
    /// </summary>
    public const int MaxChars = 50_000;

    /// <summary>The longest request id the envelope reserves room for (the bridge sends a counter).</summary>
    private const int MaxIdChars = 32;

    /// <summary>
    /// The characters a response adds around its result, <c>{"id":"…","ok":true,"result":…}</c>, for an id of up to
    /// <see cref="MaxIdChars"/> characters: the result of <c>0</c> (one character) is taken off again.
    /// </summary>
    private static readonly int EnvelopeChars =
        JsonSerializer.Serialize(ControlResponse.Success(new string('9', MaxIdChars), 0), ControlJson.Options).Length - 1;

    /// <summary>What a serialized result may take so that its response stays within <see cref="MaxChars"/>.</summary>
    internal static int ResultBudget => MaxChars - EnvelopeChars;

    /// <summary>Pattern lines listed per origin (plan, global defaults, one ignore file) in <c>get_ignore_rules</c>.</summary>
    public const int MaxRulesPerOrigin = 200;

    /// <summary>The characters of one ignore file's text listed in <c>get_ignore_rules</c>.</summary>
    public const int MaxIgnoreFileChars = 10_000;

    /// <summary>Warnings listed per row (a version, a run); the rest are counted in one more line.</summary>
    public const int MaxWarningsPerRow = 5;

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
            [ControlOps.CompareVersions] = async (args, ct) => await CompareVersionsAsync(args, ct),
            [ControlOps.CompareSourceWithVersion] = async (args, ct) => await CompareSourceWithVersionAsync(args, ct),
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

    /// <summary>
    /// Runs <paramref name="read"/> on the UI thread (at once when already on it) and hands back its result. Canceling
    /// <paramref name="ct"/> stops the wait (the posted read then does nothing when it comes).
    /// </summary>
    private async Task<T> OnUiAsync<T>(Func<T> read, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (SynchronizationContext.Current == _ui)
            return read();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => done.TrySetCanceled(ct));
        _ui.Post(_ =>
        {
            if (done.Task.IsCompleted)
                return;   // canceled meanwhile
            try
            {
                done.TrySetResult(read());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        }, null);
        return await done.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// The saved plan the <c>plan</c> argument names: by id, else by exact name, both case-insensitive. Unsaved new
    /// plans do not count. No match, or several plans of that name, is <c>plan_not_found</c> naming the candidates.
    /// </summary>
    private Task<PlanContext> ResolvePlanAsync(ControlArgs args, CancellationToken ct)
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
        }, ct);
    }

    /// <summary>
    /// The result <paramref name="build"/> makes from the first n of <paramref name="rowCount"/> rows, with the largest n
    /// whose serialization stays within <see cref="ResultBudget"/>, so that the response stays within
    /// <see cref="MaxChars"/> (at least one row when there are rows). The builder sets
    /// <c>truncated</c> and the hint itself when n is less than what it lists. Shared by every list result.
    /// </summary>
    internal static T FitToBudget<T>(int rowCount, Func<int, T> build)
    {
        var all = build(rowCount);
        if (rowCount == 0 || Length(all) <= ResultBudget)
            return all;
        // Binary search for the largest count that fits; the length grows with the count.
        int low = 1, high = rowCount - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Length(build(middle)) <= ResultBudget)
                low = middle;
            else
                high = middle - 1;
        }
        return build(low);
    }

    private static int Length<T>(T result) => JsonSerializer.Serialize(result, ControlJson.Options).Length;

    /// <summary>At most <see cref="MaxWarningsPerRow"/> warnings, then "… and N more warnings."; null for none.</summary>
    internal static IReadOnlyList<string>? CapWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
            return null;
        if (warnings.Count <= MaxWarningsPerRow)
            return warnings;
        var more = warnings.Count - MaxWarningsPerRow;
        return [.. warnings.Take(MaxWarningsPerRow), $"… and {more} more warning{(more == 1 ? "" : "s")}."];
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
