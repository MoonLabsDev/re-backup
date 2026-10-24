using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;

namespace ReBackup.Core.Ignore;

public enum RelocationKind { Integrate, Extract }

/// <summary>An entry the move would change from included to ignored or the other way round.</summary>
public sealed record EquivalenceExample(string Path, bool IgnoredBefore, bool IgnoredAfter, string? DecidingPattern);

/// <summary>Whether the backup scope stays identical after a relocation, checked over the entries of a scan.</summary>
public abstract record EquivalenceResult
{
    private EquivalenceResult()
    {
    }

    /// <summary>Every scanned entry is decided the same way before and after.</summary>
    public sealed record Identical : EquivalenceResult;

    /// <summary><paramref name="Count"/> entries differ; <paramref name="Examples"/> holds up to 20 of them.</summary>
    public sealed record Differs(int Count, IReadOnlyList<EquivalenceExample> Examples) : EquivalenceResult;

    /// <summary>Not compared; <paramref name="ReasonKey"/> is a locale key.</summary>
    public sealed record NotChecked(string ReasonKey) : EquivalenceResult
    {
        public const string ScanRunning = "ignore.relocation.scanRunning";
        public const string NoScan = "ignore.relocation.noScan";
        public const string UnreadableFile = "ignore.relocation.unreadableFile";
    }
}

/// <summary>What <see cref="IgnoreRelocation"/> works on. <paramref name="Index"/> is the completed scan, null when there is none.</summary>
public sealed record RelocationContext(
    IgnoreSettings Settings,
    IReadOnlyList<string> GlobalDefaults,
    SourceIndex? Index,
    string SourceRoot);

/// <summary>A proposed move of ignore lines between the plan and the <c>.backupignore</c> in <see cref="Directory"/>; nothing is written yet.</summary>
public sealed record RelocationProposal(
    RelocationKind Kind,
    string Directory,
    IReadOnlyList<string> PlanPatternsBefore,
    IReadOnlyList<string> PlanPatternsAfter,
    string? FileContentBefore,
    string? FileContentAfter,
    IReadOnlyList<RejectedLine> Rejected,
    EquivalenceResult Equivalence,
    string PlanPatternsHash,
    string? FileHash)
{
    /// <summary>The plan lines removed (extract) or added (integrate, with their comment header).</summary>
    public IReadOnlyList<string> MovedLines { get; init; } = [];
}
