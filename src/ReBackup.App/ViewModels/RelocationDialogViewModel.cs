using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Ignore;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.ViewModels;

public enum DiffKind { Same, Added, Removed }

/// <summary>One line of a before/after comparison.</summary>
public sealed record DiffLine(string Text, DiffKind Kind)
{
    public string Marker => Kind switch
    {
        DiffKind.Added => "+",
        DiffKind.Removed => "−",
        _ => " ",
    };
}

/// <summary>Whether the move keeps the backup scope: proven identical, different, or not checked (yet).</summary>
public enum EquivalenceState { Identical, Differs, Waiting }

/// <summary>A plan line that stays in the plan, with why.</summary>
public sealed record RejectedLineRow(string Line, string Reason);

/// <summary>An entry the move would change: its path, "included → ignored" or back, and the pattern deciding afterwards.</summary>
public sealed record EquivalenceExampleRow(string Path, string Change, string? Pattern);

/// <summary>The confirmation dialog of a <see cref="RelocationProposal"/>: both diffs, the rejected lines and the equivalence.</summary>
public sealed partial class RelocationDialogViewModel : ObservableObject
{
    /// <summary>Ticked to apply a move that changes the backup scope.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private bool _applyAnyway;

    public RelocationDialogViewModel(RelocationProposal proposal)
    {
        Proposal = proposal;
        PatternDiff = Diff(proposal.PlanPatternsBefore, proposal.PlanPatternsAfter);
        FileDiff = Diff(LinesOf(proposal.FileContentBefore), LinesOf(proposal.FileContentAfter));
        Rejected = proposal.Rejected.Select(r => new RejectedLineRow(r.Line, Loc.T(r.Reason switch
        {
            RejectReason.NotBelowFolder => "ignore.relocation.rejected.notBelowFolder",
            _ => "ignore.relocation.rejected.matchesOutsideFolder",
        }))).ToList();

        FilePath = IgnoreOrigins.ForNestedFile(proposal.Directory);
        Heading = Loc.F(proposal.Kind == RelocationKind.Integrate ? "ignore.relocation.integrateHeading" : "ignore.relocation.extractHeading",
            ("path", FilePath));
        switch (proposal.Equivalence)
        {
            case EquivalenceResult.Identical:
                EquivalenceState = EquivalenceState.Identical;
                EquivalenceText = Loc.T("ignore.relocation.identical");
                break;
            case EquivalenceResult.Differs differs:
                EquivalenceState = EquivalenceState.Differs;
                EquivalenceText = Loc.F("ignore.relocation.differs", ("count", differs.Count));
                Examples = differs.Examples.Select(e => new EquivalenceExampleRow(e.Path,
                    Loc.T(e.IgnoredAfter ? "ignore.relocation.nowIgnored" : "ignore.relocation.nowIncluded"), e.DecidingPattern)).ToList();
                break;
            case EquivalenceResult.NotChecked notChecked:
                EquivalenceState = EquivalenceState.Waiting;
                var running = notChecked.ReasonKey == EquivalenceResult.NotChecked.ScanRunning;
                EquivalenceText = Loc.T(running ? "ignore.relocation.waiting" : notChecked.ReasonKey);
                EquivalenceDetail = running ? Loc.T(notChecked.ReasonKey) : null;
                break;
        }
    }

    public RelocationProposal Proposal { get; }

    /// <summary>The plan patterns before and after, as one list.</summary>
    public IReadOnlyList<DiffLine> PatternDiff { get; }

    /// <summary>The <c>.backupignore</c> before and after, as one list.</summary>
    public IReadOnlyList<DiffLine> FileDiff { get; }

    public IReadOnlyList<RejectedLineRow> Rejected { get; }

    public bool HasRejected => Rejected.Count > 0;

    /// <summary>The file's path relative to the source root, e.g. <c>sub/.backupignore</c>.</summary>
    public string FilePath { get; }

    public string Heading { get; }

    public EquivalenceState EquivalenceState { get; }

    /// <summary>"Backup scope unchanged", "3 entries change", "Waiting for the scan…" or why it was not checked.</summary>
    public string EquivalenceText { get; } = "";

    /// <summary>A second line under the waiting banner; null otherwise.</summary>
    public string? EquivalenceDetail { get; }

    /// <summary>Up to 20 entries that change; empty unless <see cref="EquivalenceState.Differs"/>.</summary>
    public IReadOnlyList<EquivalenceExampleRow> Examples { get; } = [];

    public bool IsIdentical => EquivalenceState == EquivalenceState.Identical;

    public bool IsWaiting => EquivalenceState == EquivalenceState.Waiting;

    /// <summary>The red state: Apply becomes "Apply anyway" and needs the checkbox.</summary>
    public bool ShowApplyAnyway => EquivalenceState == EquivalenceState.Differs;

    /// <summary>No line can move (all refused, or the file is empty): there is nothing to apply.</summary>
    public bool NothingToMove => Proposal.MovedLines.Count == 0;

    public bool CanApply => !NothingToMove && (EquivalenceState == EquivalenceState.Identical ||
                                               (EquivalenceState == EquivalenceState.Differs && ApplyAnyway));

    public string ApplyText => Loc.T(ShowApplyAnyway ? "ignore.relocation.applyAnyway" : "ignore.relocation.apply");

    /// <summary>The lines of file content as the scanner reads them (BOM dropped, no trailing empty line).</summary>
    public static IReadOnlyList<string> LinesOf(string? content)
    {
        var lines = new List<string>();
        if (content is null)
            return lines;
        using var reader = new StringReader(content.TrimStart('﻿'));
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines;
    }

    /// <summary>A line diff by longest common subsequence: removed lines before added ones at each change.</summary>
    private static List<DiffLine> Diff(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var n = before.Count;
        var m = after.Count;
        var common = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                common[i, j] = string.Equals(before[i], after[j], StringComparison.Ordinal)
                    ? common[i + 1, j + 1] + 1
                    : Math.Max(common[i + 1, j], common[i, j + 1]);

        var result = new List<DiffLine>(n + m);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(before[x], after[y], StringComparison.Ordinal))
            {
                result.Add(new DiffLine(before[x++], DiffKind.Same));
                y++;
            }
            else if (common[x + 1, y] >= common[x, y + 1])
            {
                result.Add(new DiffLine(before[x++], DiffKind.Removed));
            }
            else
            {
                result.Add(new DiffLine(after[y++], DiffKind.Added));
            }
        }
        while (x < n)
            result.Add(new DiffLine(before[x++], DiffKind.Removed));
        while (y < m)
            result.Add(new DiffLine(after[y++], DiffKind.Added));
        return result;
    }
}
