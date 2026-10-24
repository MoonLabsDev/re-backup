using System.IO;
using ReBackup.App.Services;
using ReBackup.Control;
using ReBackup.Core.Backup;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;
using ReBackup.Core.Versions;
using ReBackup.Storage;

namespace ReBackup.App.Control;

/// <summary>The read operations: plans, versions, ignore rules, the decision for a path, folder listings, run history.</summary>
public sealed partial class ControlService
{
    private const string SourceListing = "source";
    private const int DefaultRuns = 20;

    // ------------------------------------------------------------------ list_plans

    private async Task<PlanListResult> ListPlansAsync(CancellationToken ct)
    {
        var plans = await OnUiAsync(() => _main.Plans.Where(p => !p.IsNew).Select(p => ContextOf(p, p.SavedPlan())).ToList(), ct);
        return await Task.Run(() =>
        {
            var rows = plans.Take(MaxRows).Select(context => new PlanRow(
                context.Plan.Id,
                context.Plan.Name,
                context.Source,
                context.Target,
                context.Plan.Incremental ? "incremental" : "full",
                LastRunOf(context.Plan.Id),
                context.NextRunUtc is { } next ? AsUtc(next) : null,
                context.State)).ToList();
            return FitToBudget(rows.Count, count => new PlanListResult(rows.Take(count).ToList(), plans.Count,
                count < plans.Count,
                count < plans.Count ? $"Only the first {count} of {plans.Count} plans are listed; address the others by id or name." : null));
        }, ct);
    }

    private LastRunInfo? LastRunOf(string planId)
    {
        try
        {
            return _main.RunLogFor(planId).ReadLast() is { } last
                ? new LastRunInfo(AsUtc(last.StartUtc), AsUtc(last.EndUtc), Name(last.Status), Name(last.Trigger), last.Version)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ list_versions

    private async Task<VersionListResult> ListVersionsAsync(ControlArgs args, CancellationToken ct)
    {
        var context = await ResolvePlanAsync(args, ct);
        var plan = context.Plan;
        var folders = await ListTargetAsync(plan, ct);
        var (indexed, exclusive, runs) = await Task.Run(() =>
        {
            var index = _main.VersionIndexes.For(plan.Id);
            var versions = index.Versions();
            IReadOnlyDictionary<long, long> unique = plan.Incremental
                ? versions.ToDictionary(v => v.Id, v => index.UniqueBytes(v.Id))
                : new Dictionary<long, long>();
            return (versions, unique, TryReadRuns(plan.Id));
        }, ct);

        var byName = indexed.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        var runByVersion = new Dictionary<string, RunLogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in runs.Where(r => r.Version is not null))
            runByVersion[run.Version!] = run;   // the log is oldest first: the newest run wins

        List<VersionRow> rows;
        List<string>? warnings = null;
        if (folders is not null)
        {
            rows = folders.Reverse().Select(folder =>
            {
                var version = byName.GetValueOrDefault(folder.Name);
                return Row(folder.Name, folder.LocalTime, version, version?.TotalBytes ?? folder.TotalBytes,
                    version?.FileCount ?? folder.FileCount, folder.IsOwned);
            }).ToList();
        }
        else
        {
            rows = indexed.Reverse().Select(v => Row(v.Name, v.LocalTime, v, v.TotalBytes, v.FileCount,
                v.Ownership == VersionOwnership.Owned)).ToList();
            warnings = [$"The target {context.Target} could not be read; the versions listed are those the index holds."];
        }

        if (rows.Any(r => !r.Indexed))
            await OnUiAsync(() => { context.Editor.Versions.EnsureLoaded(); return true; }, ct);   // indexes them

        return FitToBudget(Math.Min(rows.Count, MaxRows), count => new VersionListResult(plan.Name, rows.Take(count).ToList(),
            rows.Count, count < rows.Count,
            count < rows.Count ? $"Only the newest {count} of {rows.Count} versions are listed." : null, warnings));

        VersionRow Row(string name, DateTime localTime, IndexedVersion? version, long? bytes, int? files, bool managed)
        {
            var notes = new List<string>();
            if (version is null)
                notes.Add("This version is not indexed yet; indexing has started, retry later for its files.");
            else if (version.Origin == IndexOrigin.Damaged)
                notes.Add("Damaged: its manifest is missing or unreadable, so its files are unknown.");
            if (runByVersion.TryGetValue(name, out var run))
            {
                if (run.SkippedCount > 0)
                    notes.Add(run.SkippedCount == 1
                        ? "1 entry was skipped in its run."
                        : $"{run.SkippedCount} entries were skipped in its run.");
                notes.AddRange(run.Warnings);
            }
            return new VersionRow(name, new DateTimeOffset(DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified)),
                bytes is { } b ? SizeValue.Of(b) : null, files,
                version is not null && exclusive.TryGetValue(version.Id, out var unique) ? SizeValue.Of(unique) : null,
                version is not null, managed, CapWarnings(notes) ?? []);
        }
    }

    /// <summary>The plan's versions in its target, oldest first; empty before the first run; null when the target cannot be read.</summary>
    private async Task<IReadOnlyList<VersionInfo>?> ListTargetAsync(BackupPlan plan, CancellationToken ct)
    {
        try
        {
            var (state, storage) = await TargetStates.OpenAndProbeAsync(_main.Storages, plan.Target, ct).ConfigureAwait(false);
            return state switch
            {
                TargetState.Present => await VersionCatalog.ListAsync(storage!, plan.Id, plan.Name, ct).ConfigureAwait(false),
                TargetState.NotCreatedYet => [],
                _ => null,
            };
        }
        catch (StorageException)
        {
            return null;
        }
    }

    /// <summary>
    /// The indexed version of that name. A version the target has but the index not yet is <c>version_not_indexed</c>,
    /// and the plan's Versions tab is asked to index it; any other name is <c>version_not_found</c>.
    /// </summary>
    private async Task<IndexedVersion> ResolveVersionAsync(PlanContext context, string name, CancellationToken ct)
    {
        var plan = context.Plan;
        var indexed = await Task.Run(() => _main.VersionIndexes.For(plan.Id).Versions(), ct);
        if (indexed.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } version)
            return version;

        var folders = await ListTargetAsync(plan, ct);
        if (folders is not null && folders.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            await OnUiAsync(() => { context.Editor.Versions.EnsureLoaded(); return true; }, ct);
            throw new ControlFailure(ControlErrors.VersionNotIndexed,
                $"The version '{name}' is not indexed yet; indexing has started, retry later.");
        }
        throw new ControlFailure(ControlErrors.VersionNotFound, folders is null
            ? $"The version '{name}' is not in the index, and the target {context.Target} could not be read."
            : $"The plan '{plan.Name}' has no version '{name}'; list_versions names its versions.");
    }

    // ------------------------------------------------------------------ get_ignore_rules

    private async Task<IgnoreRulesResult> GetIgnoreRulesAsync(ControlArgs args, CancellationToken ct)
    {
        var context = await ResolvePlanAsync(args, ct);
        var folder = NormalizePath(args.OptionalString("path") ?? "");
        var index = await ScanAsync(context, ct);
        return await Task.Run(() =>
        {
            var plan = context.Plan;
            var root = IndexEvaluator.Evaluate(index, Matcher(context, index), ct);
            var cut = false;   // some origin's lines or some file's text was cut
            (IReadOnlyList<LineHitRow> Rows, bool Cut) Hits(string origin, IReadOnlyList<string> lines)
            {
                var hits = IgnoreLineStats.ForOrigin(root, origin, lines);
                cut |= hits.Count > MaxRulesPerOrigin;
                return (hits.Take(MaxRulesPerOrigin).Select(LineHitRow.Of).ToList(), hits.Count > MaxRulesPerOrigin);
            }

            var inFolder = index.IgnoreFiles
                .Select(file => (Origin: NestedOrigin(file), Directory: NestedDirectory(file), file.Lines))
                .Where(file => folder.Length == 0 || file.Directory.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                               file.Directory.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.Origin, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var files = inFolder.Take(MaxRows).Select(file =>
            {
                var content = string.Join("\n", file.Lines);
                var (hits, linesCut) = Hits(file.Origin, file.Lines);
                var textCut = content.Length > MaxIgnoreFileChars;
                cut |= textCut;
                return new IgnoreFileInfo(file.Origin, textCut ? content[..MaxIgnoreFileChars] : content, file.Lines.Count, hits,
                    linesCut || textCut);
            }).ToList();
            var planPatterns = Hits(IgnoreOrigins.Plan, plan.Ignore.Patterns).Rows;
            var globals = new GlobalDefaultsInfo(plan.Ignore.UseGlobalDefaults, Hits(IgnoreOrigins.GlobalDefaults, context.GlobalDefaults).Rows);

            return FitToBudget(files.Count, count =>
            {
                var truncated = cut || count < inFolder.Count;
                return new IgnoreRulesResult(plan.Name, plan.Ignore.UseGlobalDefaults, plan.Ignore.HonorNestedFiles,
                    planPatterns, globals, files.Take(count).ToList(), index.UnreadableIgnoreFiles, index.FileCount,
                    index.DirectoryCount, truncated,
                    truncated
                        ? $"Cut to fit: at most {MaxRulesPerOrigin} pattern lines per origin and {MaxIgnoreFileChars} characters per ignore file; " +
                          $"{count} of {inFolder.Count} ignore files listed. Narrow with 'path' (a folder: only the .backupignore files in it and below), " +
                          "or ask explain_path for the rule that decides one entry."
                        : null);
            });
        }, ct);
    }

    // ------------------------------------------------------------------ explain_path

    private async Task<ExplainResult> ExplainPathAsync(ControlArgs args, CancellationToken ct)
    {
        var context = await ResolvePlanAsync(args, ct);
        var raw = args.RequiredString("path");
        var path = NormalizePath(raw);
        if (path.Length == 0)
            throw new ControlFailure(ControlErrors.BadRequest, "The argument 'path' must name an entry below the source root.");
        var index = await ScanAsync(context, ct);
        return await Task.Run(() =>
        {
            var node = FindNode(index.RootNode, path);
            var exists = node is not null;
            var isDirectory = node?.IsDirectory ?? (raw.TrimEnd().EndsWith('/') || raw.TrimEnd().EndsWith('\\'));
            if (node is not null)
                path = node.RelativePath;

            var matcher = Matcher(context, index);
            string? ignoredFolder = null;
            var result = IgnoreResult.NotMatched;
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
            {
                var parent = matcher.MatchEntry(path[..slash], isDirectory: true);
                if (parent.IsIgnored)
                {
                    (result, ignoredFolder) = (parent, path[..slash]);
                    break;
                }
            }
            if (ignoredFolder is null)
                result = matcher.MatchEntry(path, isDirectory);

            return new ExplainResult(path, exists, isDirectory ? "folder" : "file", result.IsIgnored,
                new RuleLocator(context, index).Of(result.Pattern), ignoredFolder);
        }, ct);
    }

    // ------------------------------------------------------------------ list_files

    private async Task<FileListResult> ListFilesAsync(ControlArgs args, CancellationToken ct)
    {
        var context = await ResolvePlanAsync(args, ct);
        var path = NormalizePath(args.OptionalString("path") ?? "");
        var listing = args.OptionalString("in") is { Length: > 0 } given ? given.Trim() : SourceListing;
        var sort = (args.OptionalString("sort") ?? "name").Trim().ToLowerInvariant();
        if (sort is not ("name" or "size" or "date"))
            throw new ControlFailure(ControlErrors.BadRequest, $"The argument 'sort' must be name, size or date, not '{sort}'.");
        var limit = Math.Clamp(args.OptionalInt("limit") ?? MaxRows, 1, MaxRows);

        IReadOnlyList<FileRow> rows;
        if (listing.Equals(SourceListing, StringComparison.OrdinalIgnoreCase))
        {
            listing = SourceListing;
            var index = await ScanAsync(context, ct);
            rows = await Task.Run(() => SourceRows(context, index, path, ct), ct);
        }
        else
        {
            var version = await ResolveVersionAsync(context, listing, ct);
            listing = version.Name;
            rows = await Task.Run(() => VersionRows(context, version, path), ct);
        }

        var sorted = sort switch
        {
            "size" => rows.OrderByDescending(r => r.Size.Bytes).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            "date" => rows.OrderByDescending(r => r.Date ?? DateTime.MinValue).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(r => r.Kind == "folder" ? 0 : 1).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        };
        var shown = sorted.Take(limit).ToList();
        return FitToBudget(shown.Count, count => new FileListResult(context.Plan.Name, path, listing, shown.Take(count).ToList(),
            rows.Count, count < rows.Count,
            count < rows.Count
                ? $"Showing {count} of {rows.Count} entries. Narrow with 'path' (a subfolder), or raise 'limit' (at most {MaxRows})."
                : null));
    }

    private IReadOnlyList<FileRow> SourceRows(PlanContext context, SourceIndex index, string path, CancellationToken ct)
    {
        var folder = IndexEvaluator.Evaluate(index, Matcher(context, index), ct);
        foreach (var name in Segments(path))
        {
            folder = Pick(folder.Children, name, child => child.Name) is { IsDirectory: true } child
                ? child
                : throw new ControlFailure(ControlErrors.BadRequest, $"The source has no folder '{path}'.");
        }

        var rules = new RuleLocator(context, index);
        return folder.Children.Select(child => new FileRow(
            child.Name,
            child.IsDirectory ? "folder" : "file",
            SizeValue.Of(child.TotalSize),
            child.Node.LastWriteUtc == default ? null : AsUtc(child.Node.LastWriteUtc),
            child.IsDirectory ? child.TotalFiles : null,
            child.IsDirectory ? null : ExtensionOf(child.Name),
            child.Status == IncludeStatus.Ignored,
            child.Status == IncludeStatus.Partial ? true : null,
            rules.Of(child.Pattern))).ToList();
    }

    private IReadOnlyList<FileRow> VersionRows(PlanContext context, IndexedVersion version, string path)
    {
        var index = _main.VersionIndexes.For(context.Plan.Id);
        if (index.FindPath(path, isDirectory: true) is not { } folderId)
            throw new ControlFailure(ControlErrors.BadRequest, $"The version '{version.Name}' has no folder '{path}'.");
        return index.Children(version.Id, folderId)
            .Where(child => child.InVersion is not null)
            .Select(child => new FileRow(
                child.Name,
                child.IsDirectory ? "folder" : "file",
                SizeValue.Of(child.InVersion!.Size),
                child.InVersion.MtimeUtc is { } mtime ? AsUtc(mtime) : null,
                child.IsDirectory ? child.InVersion.Files : null,
                child.IsDirectory ? null : ExtensionOf(child.Name),
                null, null, null))
            .ToList();
    }

    // ------------------------------------------------------------------ get_run_history

    private async Task<RunHistoryResult> GetRunHistoryAsync(ControlArgs args, CancellationToken ct)
    {
        var context = await ResolvePlanAsync(args, ct);
        var n = Math.Clamp(args.OptionalInt("n") ?? DefaultRuns, 1, MaxRows);
        var runs = await Task.Run(() => ReadRuns(context.Plan.Id), ct);
        var rows = runs.Reverse().Take(n).Select(run => new RunRow(run.RunId, Name(run.Trigger), AsUtc(run.StartUtc),
            AsUtc(run.EndUtc), run.DurationMs, Name(run.Status), run.Reason, run.Version, run.FilesCopied,
            SizeValue.Of(run.BytesCopied), run.SkippedCount, CapWarnings(run.Warnings))).ToList();
        return FitToBudget(rows.Count, count => new RunHistoryResult(context.Plan.Name, rows.Take(count).ToList(), runs.Count,
            count < runs.Count,
            count < runs.Count
                ? $"Showing the newest {count} of {runs.Count} runs; ask with a smaller or larger 'n' (at most {MaxRows}) as needed."
                : null));
    }

    /// <summary>The plan's run log, oldest first; an unreadable log is an <c>internal_error</c>.</summary>
    private IReadOnlyList<RunLogEntry> ReadRuns(string planId)
    {
        try
        {
            return _main.RunLogFor(planId).ReadAll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ControlFailure(ControlErrors.InternalError, $"The run log could not be read: {ex.Message}");
        }
    }

    /// <summary>The plan's run log, oldest first; empty when it cannot be read (its runs then add no warnings).</summary>
    private IReadOnlyList<RunLogEntry> TryReadRuns(string planId)
    {
        try
        {
            return _main.RunLogFor(planId).ReadAll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // ------------------------------------------------------------------ the source scan and its rules

    /// <summary>
    /// The finished scan of the plan's source from the scan cache (started when none is held). Not finished within
    /// <see cref="ScanWait"/>: <c>scan_running</c> with the progress, the scan goes on and a retry joins it.
    /// </summary>
    private async Task<SourceIndex> ScanAsync(PlanContext context, CancellationToken ct)
    {
        ScanLookup lookup;
        try
        {
            lookup = await _main.ScanCache.GetAsync(context.Plan, ScanWait, ct);
        }
        catch (StorageNotFoundException)
        {
            throw new ControlFailure(ControlErrors.BadRequest, $"The plan's source does not exist: {context.Source}");
        }
        catch (StorageException ex)
        {
            throw new ControlFailure(ControlErrors.BadRequest, $"The plan's source could not be read: {ex.Message}");
        }
        return lookup.Index ?? throw new ControlFailure(ControlErrors.ScanRunning,
            $"The source is still being scanned ({Count(lookup.Files, "file")}, {Count(lookup.Directories, "folder")} so far); retry in a moment.");
    }

    private static IgnoreMatcher Matcher(PlanContext context, SourceIndex index) =>
        IgnoreMatcher.ForPlan(context.Plan.Ignore, context.GlobalDefaults, index.IgnoreFiles);

    /// <summary>The origin the matcher gives the patterns of a nested ignore file ("docs/.backupignore").</summary>
    private static string NestedOrigin(NestedIgnoreFile file) => IgnoreOrigins.ForNestedFile(NestedDirectory(file));

    /// <summary>The folder of a nested ignore file, as the matcher normalizes it ("docs"; "" for the root).</summary>
    private static string NestedDirectory(NestedIgnoreFile file) => file.DirectoryRelativePath.Replace('\\', '/').Trim('/');

    /// <summary>Finds where a deciding pattern is written: its origin's lines, and the line (the last equal one wins).</summary>
    private sealed class RuleLocator(PlanContext context, SourceIndex index)
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _lines = new(StringComparer.Ordinal);

        public RuleInfo? Of(IgnorePattern? pattern)
        {
            if (pattern is null)
                return null;
            var lines = LinesOf(pattern.Origin);
            int? line = null;
            for (var i = lines.Count - 1; i >= 0 && line is null; i--)
            {
                if (lines[i] is not null && IgnorePattern.Parse(lines[i], pattern.Origin)?.Text == pattern.Text)
                    line = i + 1;
            }
            return new RuleInfo(pattern.Text, pattern.Origin, line, pattern.IsNegated);
        }

        private IReadOnlyList<string> LinesOf(string origin)
        {
            if (_lines.TryGetValue(origin, out var known))
                return known;
            IReadOnlyList<string> lines = origin switch
            {
                IgnoreOrigins.Plan => context.Plan.Ignore.Patterns,
                IgnoreOrigins.GlobalDefaults => context.GlobalDefaults,
                _ => index.IgnoreFiles.FirstOrDefault(file => NestedOrigin(file) == origin)?.Lines ?? [],
            };
            _lines[origin] = lines;
            return lines;
        }
    }

    // ------------------------------------------------------------------ paths

    /// <summary>A path relative to the root with forward slashes and no leading or trailing slash ("" = the root).</summary>
    private static string NormalizePath(string path) => path.Trim().Replace('\\', '/').Trim('/');

    private static string[] Segments(string path) =>
        path.Length == 0 ? [] : path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static IndexNode? FindNode(IndexNode root, string path)
    {
        var node = root;
        foreach (var name in Segments(path))
        {
            if (Pick(node.Children, name, child => child.Name) is not { } child)
                return null;
            node = child;
        }
        return node;
    }

    /// <summary>The child of that name: the exact spelling, else one that differs in case only.</summary>
    private static T? Pick<T>(IReadOnlyList<T> children, string name, Func<T, string> nameOf) where T : class =>
        children.FirstOrDefault(c => string.Equals(nameOf(c), name, StringComparison.Ordinal)) ??
        children.FirstOrDefault(c => string.Equals(nameOf(c), name, StringComparison.OrdinalIgnoreCase));

    private static string? ExtensionOf(string name) => Path.GetExtension(name) is { Length: > 0 } extension ? extension : null;
}
