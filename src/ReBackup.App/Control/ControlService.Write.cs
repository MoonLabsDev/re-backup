using System.Globalization;
using System.IO;
using ReBackup.App.ViewModels;
using ReBackup.Control;
using ReBackup.Core.Backup;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;
using ReBackup.Shared.Schedule;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;

namespace ReBackup.App.Control;

/// <summary>
/// The writes: ignore-change and relocation proposals, <c>apply_change</c>, <c>start_backup</c>, and
/// <c>get_run_status</c> for the runs it starts. Changes go to the saved plan; a plan with unsaved edits in the app is never
/// written (its proposal is stale). Every write tells the user (<see cref="MainViewModel.NotifyControl"/>).
/// </summary>
public sealed partial class ControlService
{
    /// <summary>Places listed per side of an ignore change.</summary>
    public const int MaxScopeExamples = 20;

    // ------------------------------------------------------------------ preview_ignore_change

    private async Task<IgnoreChangeResult> PreviewIgnoreChangeAsync(ControlArgs args, CancellationToken ct)
    {
        var context = await ResolvePlanAsync(args, ct);
        var add = args.OptionalStringArray("add") ?? [];
        var remove = args.OptionalStringArray("remove") ?? [];
        if (add.Count == 0 && remove.Count == 0)
            throw new ControlFailure(ControlErrors.BadRequest, "Give the pattern lines to 'add' or to 'remove'.");
        foreach (var line in add)
            CheckPattern(line);

        var before = context.Plan.Ignore.Patterns.ToList();
        var after = before.ToList();
        foreach (var line in remove)
        {
            if (after.RemoveAll(existing => SameLine(existing, line)) == 0)
                throw new ControlFailure(ControlErrors.BadRequest,
                    $"The plan has no pattern line '{line}'; get_ignore_rules lists its lines.");
        }
        after.AddRange(add);

        var (index, scannedAt) = await ScanAsync(context, Rescan(args), ct);
        var (ignored, included) = await Task.Run(() =>
        {
            var changed = new IgnoreSettings
            {
                UseGlobalDefaults = context.Plan.Ignore.UseGlobalDefaults,
                HonorNestedFiles = context.Plan.Ignore.HonorNestedFiles,
                Patterns = after,
            };
            var rootBefore = IndexEvaluator.Evaluate(index, Matcher(context, index), ct);
            var rootAfter = IndexEvaluator.Evaluate(index, IgnoreMatcher.ForPlan(changed, context.GlobalDefaults, index.IgnoreFiles), ct);
            var newlyIgnored = new ScopeTally();
            var newlyIncluded = new ScopeTally();
            CompareScope(rootBefore, rootAfter, newlyIgnored, newlyIncluded);
            return (newlyIgnored.Info(), newlyIncluded.Info());
        }, ct);

        var id = _proposals.Add(new IgnoreChangeProposal(context.Plan.Id,
            IgnoreBasis.Of(context.Plan.Ignore, context.GlobalDefaults), before, after));
        var truncated = after.Count > MaxRulesPerOrigin;
        return new IgnoreChangeResult(id, context.Plan.Name, after.Take(MaxRulesPerOrigin).ToList(), ignored, included,
            index.FileCount, index.DirectoryCount, truncated,
            truncated ? $"Only the first {MaxRulesPerOrigin} of the plan's {after.Count} pattern lines are listed." : null, scannedAt);
    }

    /// <summary>A line to add: a comment, or a pattern that parses (a blank line or a lone "!" or "/" is no pattern).</summary>
    private static void CheckPattern(string line)
    {
        if (line.Contains('\n') || line.Contains('\r'))
            throw new ControlFailure(ControlErrors.InvalidPattern, $"'{line}' is more than one line; give each pattern on its own.");
        if (line.StartsWith('#'))
            return;
        IgnorePattern? pattern;
        try
        {
            pattern = IgnorePattern.Parse(line, IgnoreOrigins.Plan);
        }
        catch (ArgumentException)
        {
            pattern = null;
        }
        if (pattern is null)
            throw new ControlFailure(ControlErrors.InvalidPattern,
                $"'{line}' is no ignore pattern (gitignore syntax: '*.log', 'build/', '/docs/tmp/', '!keep.txt').");
    }

    /// <summary>The same pattern line: trailing blanks (which a pattern ignores) do not count.</summary>
    private static bool SameLine(string a, string b) => string.Equals(a.TrimEnd(), b.TrimEnd(), StringComparison.Ordinal);

    /// <summary>
    /// Walks two evaluations of the same scan side by side and tallies what changes side: a folder that is wholly ignored
    /// on one side is one place (with the files the other side includes); otherwise its children are compared.
    /// </summary>
    private static void CompareScope(EvaluatedNode before, EvaluatedNode after, ScopeTally newlyIgnored, ScopeTally newlyIncluded)
    {
        if (!before.IsDirectory)
        {
            if (before.Status == IncludeStatus.Included && after.Status == IncludeStatus.Ignored)
                newlyIgnored.Add(before.RelativePath, "file", 1, before.TotalSize);
            else if (before.Status == IncludeStatus.Ignored && after.Status == IncludeStatus.Included)
                newlyIncluded.Add(before.RelativePath, "file", 1, before.TotalSize);
            return;
        }
        var ignoredBefore = before.Status == IncludeStatus.Ignored;
        var ignoredAfter = after.Status == IncludeStatus.Ignored;
        if (ignoredBefore && ignoredAfter || before.Status == IncludeStatus.Included && after.Status == IncludeStatus.Included)
            return;
        if (ignoredAfter)
        {
            newlyIgnored.Add(before.RelativePath + "/", "folder", before.IncludedFiles, before.IncludedSize);
            return;
        }
        if (ignoredBefore)
        {
            newlyIncluded.Add(before.RelativePath + "/", "folder", after.IncludedFiles, after.IncludedSize);
            return;
        }
        for (var i = 0; i < before.Children.Count; i++)
            CompareScope(before.Children[i], after.Children[i], newlyIgnored, newlyIncluded);
    }

    private sealed class ScopeTally
    {
        private readonly List<(string Path, string Kind, int Files, long Bytes)> _places = [];
        private int _files;
        private long _bytes;

        public void Add(string path, string kind, int files, long bytes)
        {
            if (files == 0)
                return;   // an empty folder changes nothing that is backed up
            _places.Add((path, kind, files, bytes));
            _files += files;
            _bytes += bytes;
        }

        public ScopeChangeInfo Info() => new(_files, SizeValue.Of(_bytes),
            _places.OrderByDescending(p => p.Bytes).ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxScopeExamples)
                .Select(p => new ScopeExample(p.Path, p.Kind, p.Files, SizeValue.Of(p.Bytes)))
                .ToList(),
            _places.Count);
    }

    // ------------------------------------------------------------------ propose_relocation

    private async Task<RelocationResult> ProposeRelocationAsync(ControlArgs args, CancellationToken ct)
    {
        var context = await ResolvePlanAsync(args, ct);
        var integrate = args.OptionalString("integrate");
        var extract = args.OptionalStringArray("extract");
        var folder = args.OptionalString("folder");
        var extracting = extract is not null || folder is not null;
        if ((integrate is not null) == extracting)
            throw new ControlFailure(ControlErrors.BadRequest,
                "Give either 'integrate' (the path of a .backupignore) or 'extract' (plan lines) with 'folder'.");
        if (extract is not null && folder is null)
            throw new ControlFailure(ControlErrors.BadRequest, "'extract' needs the 'folder' whose .backupignore takes the lines.");
        if (context.Plan.Source is not { IsFileSystem: true, Path: { } root } || string.IsNullOrWhiteSpace(root))
            throw new ControlFailure(ControlErrors.SourceNotFolder,
                $"The source of '{context.Plan.Name}' is no folder ({context.Source}); rules move only between a folder source's plan and its .backupignore files.");
        if (extracting && !context.Plan.Ignore.HonorNestedFiles)
            throw new ControlFailure(ControlErrors.BadRequest,
                $"'{context.Plan.Name}' does not honor .backupignore files, so rules moved into one would do nothing.");

        var (index, _) = await ScanAsync(context, rescan: false, ct);
        var proposal = await Task.Run(() =>
        {
            var relocation = new RelocationContext(context.Plan.Ignore, context.GlobalDefaults, index, root);
            try
            {
                if (integrate is not null)
                    return IgnoreRelocation.ProposeIntegrate(relocation, IgnoreFileDirectory(integrate));
                var directory = NormalizePath(folder!);
                var patterns = context.Plan.Ignore.Patterns;
                var lines = extract!.Count == 0
                    ? IgnoreRelocation.ExtractableLines(context.Plan.Ignore, directory)
                    : extract.Select(line => IndexOfLine(patterns, line)).ToList();
                return IgnoreRelocation.ProposeExtract(relocation, lines, directory);
            }
            catch (ArgumentException ex)
            {
                throw new ControlFailure(ControlErrors.BadRequest, $"The folder is not below the source: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ControlFailure(ControlErrors.BadRequest, $"The .backupignore could not be read: {ex.Message}");
            }
        }, ct);

        if (proposal.Kind == RelocationKind.Integrate && proposal.FileContentBefore is null)
            throw new ControlFailure(ControlErrors.BadRequest,
                $"There is no {IgnoreOrigins.ForNestedFile(proposal.Directory)} in the source; get_ignore_rules lists the files.");
        var id = proposal.MovedLines.Count > 0
            ? _proposals.Add(new RelocationPlanProposal(context.Plan.Id, context.Plan.Source,
                IgnoreBasis.Of(context.Plan.Ignore, context.GlobalDefaults), proposal))
            : null;
        return RelocationResultOf(id, context.Plan.Name, proposal);
    }

    /// <summary>The folder of a <c>.backupignore</c> given by its path ("docs/.backupignore") or by its folder ("docs").</summary>
    private static string IgnoreFileDirectory(string path)
    {
        var normalized = NormalizePath(path);
        var slash = normalized.LastIndexOf('/');
        var name = slash < 0 ? normalized : normalized[(slash + 1)..];
        return IgnoreOrigins.IsNestedFileName(name) ? (slash < 0 ? "" : normalized[..slash]) : normalized;
    }

    private static int IndexOfLine(IReadOnlyList<string> patterns, string line)
    {
        for (var i = 0; i < patterns.Count; i++)
        {
            if (SameLine(patterns[i], line))
                return i;
        }
        throw new ControlFailure(ControlErrors.BadRequest, $"The plan has no pattern line '{line}'; get_ignore_rules lists its lines.");
    }

    /// <summary>The proposal as Claude reads it, its lines and file texts cut until it fits the response budget.</summary>
    private static RelocationResult RelocationResultOf(string? id, string plan, RelocationProposal proposal)
    {
        var added = proposal.Kind == RelocationKind.Integrate ? proposal.MovedLines : [];
        var removed = proposal.Kind == RelocationKind.Extract ? proposal.MovedLines : [];
        var equivalence = proposal.Equivalence switch
        {
            EquivalenceResult.Identical => new EquivalenceInfo("identical", null, null, null),
            EquivalenceResult.Differs differs => new EquivalenceInfo("differs", differs.Count,
                differs.Examples.Select(e => new EquivalenceExampleInfo(e.Path, e.IgnoredBefore, e.IgnoredAfter, e.DecidingPattern)).ToList(),
                null),
            EquivalenceResult.NotChecked notChecked => new EquivalenceInfo("notChecked", null, null,
                notChecked.ReasonKey[(notChecked.ReasonKey.LastIndexOf('.') + 1)..]),
            _ => new EquivalenceInfo("notChecked", null, null, null),
        };
        var rejected = proposal.Rejected.Select(r => new RejectedLineInfo(r.Line, Name(r.Reason))).Take(MaxRulesPerOrigin).ToList();

        RelocationResult Build(int lines, int chars)
        {
            var truncated = added.Count > lines || removed.Count > lines || proposal.Rejected.Count > rejected.Count ||
                            proposal.FileContentBefore?.Length > chars || proposal.FileContentAfter?.Length > chars;
            return new RelocationResult(id, plan, Name(proposal.Kind), IgnoreOrigins.ForNestedFile(proposal.Directory),
                added.Take(lines).ToList(), removed.Take(lines).ToList(), Cut(proposal.FileContentBefore, chars),
                Cut(proposal.FileContentAfter, chars), rejected, equivalence, truncated,
                truncated
                    ? "Lines or file texts were cut to fit; get_ignore_rules (with 'path') shows the plan's lines and the file in full."
                    : null);
        }

        var (maxLines, maxChars) = (MaxRulesPerOrigin, MaxIgnoreFileChars);
        var result = Build(maxLines, maxChars);
        while (Length(result) > ResultBudget && (maxLines > 1 || maxChars > 100))
        {
            (maxLines, maxChars) = (Math.Max(1, maxLines / 2), Math.Max(100, maxChars / 2));
            result = Build(maxLines, maxChars);
        }
        return result;
    }

    private static string? Cut(string? text, int chars) => text is not null && text.Length > chars ? text[..chars] : text;

    // ------------------------------------------------------------------ apply_change

    private async Task<ApplyResult> ApplyChangeAsync(ControlArgs args, CancellationToken ct)
    {
        var id = args.RequiredString("proposalId").Trim();
        if (!_proposals.TryTake(id, out var proposal, out var error))
        {
            throw new ControlFailure(error!, error == ControlErrors.ProposalExpired
                ? $"The proposal '{id}' has expired (proposals can be applied for {ProposalStore.Lifetime.TotalMinutes:0} minutes); make it again."
                : $"No pending proposal has the id '{id}': it was applied already, or never made. Make a new proposal.");
        }
        return proposal switch
        {
            IgnoreChangeProposal change => await OnUiAsync(() => ApplyIgnoreChange(change), ct),
            RelocationPlanProposal relocation => await OnUiAsync(() => ApplyRelocation(relocation), ct),
            _ => throw new ControlFailure(ControlErrors.InternalError, "The proposal is of an unknown kind."),
        };
    }

    /// <summary>On the UI thread: the plan's patterns as proposed, saved.</summary>
    private ApplyResult ApplyIgnoreChange(IgnoreChangeProposal change)
    {
        var (editor, plan) = EditorForApply(change.PlanId, change.Basis);
        if (!plan.Ignore.Patterns.SequenceEqual(change.Before, StringComparer.Ordinal))
            throw new ControlFailure(ControlErrors.ProposalStale,
                $"The ignore patterns of '{plan.Name}' changed since the proposal; make a new one.");
        try
        {
            _main.SaveIgnorePatterns(editor, change.After);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ControlFailure(ControlErrors.InternalError, $"The plan could not be saved; nothing was changed: {ex.Message}");
        }
        _main.NotifyControl(LocText.Of("control.notification.ignoreChanged", ("plan", plan.Name)));
        return AppliedResult("ignoreChange", plan.Name, change.After, null, null);
    }

    /// <summary>On the UI thread: the relocation as proposed, through the same steps and the same recycle bin as the dialog.</summary>
    private ApplyResult ApplyRelocation(RelocationPlanProposal relocation)
    {
        var (editor, plan) = EditorForApply(relocation.PlanId, relocation.Basis);
        var proposal = relocation.Proposal;
        if (plan.Source != relocation.Source || plan.Source.Path is not { } root)
            throw new ControlFailure(ControlErrors.ProposalStale, $"The source of '{plan.Name}' changed since the proposal; make a new one.");

        var file = IgnoreOrigins.ForNestedFile(proposal.Directory);
        // Only used when the drive has no recycle bin and the file is renamed instead (the first free name).
        var renamedTo = IgnoreRelocation.IntegratedFileName(Path.Combine(root, proposal.Directory.Replace('/', Path.DirectorySeparatorChar)));
        RecycleOutcome? outcome;
        try
        {
            outcome = IgnoreRelocation.Apply(proposal, root, plan.Ignore.Patterns,
                patterns => _main.SaveIgnorePatterns(editor, patterns), _main.RecycleBin);
        }
        catch (RelocationStaleException)
        {
            throw new ControlFailure(ControlErrors.ProposalStale,
                $"The patterns of '{plan.Name}' or {file} changed since the proposal; make a new one.");
        }
        catch (ArgumentException ex)
        {
            throw new ControlFailure(ControlErrors.BadRequest, $"The folder is not below the source: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ControlFailure(ControlErrors.InternalError, $"The rules could not be moved; what was done is undone: {ex.Message}");
        }

        _main.ReplaceNestedIgnoreFile(editor, plan, proposal.Directory,
            proposal.Kind == RelocationKind.Integrate ? null : RelocationDialogViewModel.LinesOf(proposal.FileContentAfter));
        _main.NotifyControl(LocText.Of(
            proposal.Kind == RelocationKind.Integrate ? "control.notification.integrated" : "control.notification.extracted",
            ("plan", plan.Name), ("file", file)));
        var fileOutcome = outcome switch
        {
            RecycleOutcome.Recycled => "recycled",
            RecycleOutcome.Renamed => "renamed",
            _ => "written",
        };
        return AppliedResult("relocation", plan.Name, proposal.PlanPatternsAfter, fileOutcome,
            outcome == RecycleOutcome.Renamed ? Path.GetRelativePath(root, renamedTo).Replace('\\', '/') : null);
    }

    /// <summary>
    /// On the UI thread: the saved plan's editor, which must have no unsaved edits (they would be overwritten), and its
    /// saved plan, whose ignore settings besides the patterns (and the global defaults it uses) must still be those the
    /// proposal was made with: otherwise its numbers no longer hold.
    /// </summary>
    private (PlanEditorViewModel Editor, BackupPlan Plan) EditorForApply(string planId, IgnoreBasis basis)
    {
        var editor = _main.Plans.FirstOrDefault(p => !p.IsNew && p.Id.Equals(planId, StringComparison.OrdinalIgnoreCase))
                     ?? throw new ControlFailure(ControlErrors.PlanNotFound, "The plan of the proposal no longer exists.");
        var plan = editor.SavedPlan();
        if (editor.IsDirty)
            throw new ControlFailure(ControlErrors.ProposalStale,
                $"The plan '{plan.Name}' has unsaved edits in ReBackup; ask the user to save or revert the plan first, then make a new proposal.");
        if (!basis.Matches(IgnoreBasis.Of(plan.Ignore, _main.GlobalIgnoreDefaultsNow())))
            throw new ControlFailure(ControlErrors.ProposalStale,
                $"The ignore settings of '{plan.Name}' (global defaults, honoring .backupignore files) changed since the proposal; make a new one.");
        return (editor, plan);
    }

    private static ApplyResult AppliedResult(string applied, string plan, IReadOnlyList<string> patterns, string? fileOutcome,
        string? renamedTo) =>
        new(applied, plan, patterns.Take(MaxRulesPerOrigin).ToList(), fileOutcome, renamedTo, patterns.Count > MaxRulesPerOrigin);

    // ------------------------------------------------------------------ start_backup

    private async Task<StartBackupResult> StartBackupAsync(ControlArgs args, CancellationToken ct)
    {
        var context = await ResolvePlanAsync(args, ct);
        return await OnUiAsync(() =>
        {
            if (_main.StartForControl(context.Editor, out var busy, out var queuedUtc) is { } reason)
                throw new ControlFailure(busy ? ControlErrors.PlanRunning : ControlErrors.BadRequest, reason);
            return new StartBackupResult(RunIdOf(context.Plan.Id, queuedUtc), context.Plan.Name, "queued");
        }, ct);
    }

    /// <summary>"&lt;planId&gt;:&lt;UTC ticks just before the run was queued&gt;".</summary>
    private static string RunIdOf(string planId, DateTime queuedUtc) =>
        planId + ":" + queuedUtc.Ticks.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ get_run_status

    private async Task<RunStatusResult> GetRunStatusAsync(ControlArgs args, CancellationToken ct)
    {
        var runId = args.RequiredString("runId").Trim();
        var colon = runId.LastIndexOf(':');
        if (colon <= 0 || !long.TryParse(runId[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks > DateTime.MaxValue.Ticks)
            throw new ControlFailure(ControlErrors.BadRequest, $"'{runId}' is no run id; start_backup returns them.");
        var planId = runId[..colon];
        var since = new DateTime(ticks, DateTimeKind.Utc);
        var name = await OnUiAsync(() => _main.Plans.FirstOrDefault(p => !p.IsNew && p.Id.Equals(planId, StringComparison.OrdinalIgnoreCase))
            ?.SavedPlan().Name, ct) ?? throw new ControlFailure(ControlErrors.PlanNotFound, $"No plan has the id '{planId}'.");

        // The log first: a finished run is written there before the plan's job is cleared.
        if (await Task.Run(() => RunSince(planId, since), ct) is { } done)
            return Finished(done);
        var live = await OnUiAsync(() =>
        {
            var editor = _main.Plans.FirstOrDefault(p => p.Id.Equals(planId, StringComparison.OrdinalIgnoreCase));
            return editor?.Run.State switch
            {
                JobState.Queued => new RunStatusResult(runId, name, "queued", _main.QueuePositionOf(editor.Id)?.Position, null, null),
                JobState.Running => new RunStatusResult(runId, name, "running", null,
                    editor.Run.Progress is { } progress ? ProgressOf(progress) : null, null),
                _ => null,
            };
        }, ct);
        if (live is not null)
            return live;
        // It may have finished between the two looks.
        return await Task.Run(() => RunSince(planId, since), ct) is { } finished
            ? Finished(finished)
            : new RunStatusResult(runId, name, "unknown", null, null, null);

        RunStatusResult Finished(RunLogEntry entry) => new(runId, name, "finished", null, null, RunRowOf(entry));
    }

    /// <summary>The plan's first logged MCP run that started at or after <paramref name="since"/>; null when there is none yet.</summary>
    private RunLogEntry? RunSince(string planId, DateTime since) =>
        ReadRuns(planId).FirstOrDefault(run => run.Trigger == RunTrigger.Mcp && AsUtc(run.StartUtc) >= since);

    private static RunProgressInfo ProgressOf(BackupProgress progress) => new(Name(progress.Phase), progress.FilesDone,
        progress.FilesTotal, SizeValue.Of(progress.BytesDone), SizeValue.Of(progress.BytesTotal), progress.Fraction);

    /// <summary>The proposed new plan patterns of <c>preview_ignore_change</c>, and the patterns they were made from.</summary>
    private sealed record IgnoreChangeProposal(string PlanId, IgnoreBasis Basis, IReadOnlyList<string> Before,
        IReadOnlyList<string> After);

    /// <summary>A relocation proposal of <c>propose_relocation</c>, with the plan and the source it was made for.</summary>
    private sealed record RelocationPlanProposal(string PlanId, StorageLocation Source, IgnoreBasis Basis,
        RelocationProposal Proposal);

    /// <summary>
    /// What a proposal's numbers rest on besides the plan patterns: whether the plan uses the global defaults (and which
    /// they are, while it does) and whether it honors <c>.backupignore</c> files.
    /// </summary>
    private sealed record IgnoreBasis(bool UseGlobalDefaults, bool HonorNestedFiles, IReadOnlyList<string> GlobalDefaults)
    {
        public static IgnoreBasis Of(IgnoreSettings settings, IReadOnlyList<string> globalDefaults) =>
            new(settings.UseGlobalDefaults, settings.HonorNestedFiles, settings.UseGlobalDefaults ? [.. globalDefaults] : []);

        public bool Matches(IgnoreBasis other) =>
            UseGlobalDefaults == other.UseGlobalDefaults && HonorNestedFiles == other.HonorNestedFiles &&
            GlobalDefaults.SequenceEqual(other.GlobalDefaults, StringComparer.Ordinal);
    }
}
