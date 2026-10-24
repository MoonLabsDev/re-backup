using ReBackup.Core.Ignore;
using ReBackup.Core.Versions;
using ReBackup.Shared.IO;

namespace ReBackup.App.Control;

// The results of the read operations, serialized with ControlJson.Options (camelCase, nulls left out). They mirror the
// columns of spec section 4.1. Times are ISO 8601: UTC for run and file times, local with offset for version dates.
// Enum-like values are camelCase strings. Nothing here ever carries a file's content.

/// <summary>A size: the bytes and the same as people read it ("1.2 GB", invariant, as logs write it).</summary>
public sealed record SizeValue(long Bytes, string Text)
{
    public static SizeValue Of(long bytes) => new(bytes, ByteSize.Format(bytes));

    /// <summary>A size change: negative ones read "-1.2 GB".</summary>
    public static SizeValue Signed(long bytes) => bytes < 0 ? new(bytes, "-" + ByteSize.Format(-bytes)) : Of(bytes);
}

/// <summary>The pattern that decided an entry, where it is written (origin and 1-based line) and whether it re-includes.</summary>
public sealed record RuleInfo(string Pattern, string Origin, int? Line, bool Negated);

public sealed record PlanListResult(IReadOnlyList<PlanRow> Plans, int Total, bool Truncated, string? Hint);

/// <summary><paramref name="State"/>: idle, queued, running or deleting (versions are being deleted).</summary>
public sealed record PlanRow(string Id, string Name, string Source, string Target, string Kind, LastRunInfo? LastRun,
    DateTime? NextRun, string State);

public sealed record LastRunInfo(DateTime Start, DateTime End, string Status, string Trigger, string? Version);

/// <summary>
/// The versions newest first. <paramref name="Warnings"/>: about the listing as a whole (e.g. the target could not be
/// read, so only the index is shown).
/// </summary>
public sealed record VersionListResult(string Plan, IReadOnlyList<VersionRow> Versions, int Total, bool Truncated,
    string? Hint, IReadOnlyList<string>? Warnings);

/// <summary>
/// <paramref name="Exclusive"/>: the bytes only this version holds in the pool (incremental plans, indexed versions).
/// <paramref name="Managed"/>: retention manages it (its manifest names this plan).
/// </summary>
public sealed record VersionRow(string Name, DateTimeOffset Date, SizeValue? Size, int? Files, SizeValue? Exclusive,
    bool Indexed, bool Managed, IReadOnlyList<string> Warnings);

public sealed record IgnoreRulesResult(string Plan, bool UseGlobalDefaults, bool HonorNestedFiles,
    IReadOnlyList<LineHitRow> PlanPatterns, GlobalDefaultsInfo GlobalDefaults, IReadOnlyList<IgnoreFileInfo> IgnoreFiles,
    IReadOnlyList<string> UnreadableIgnoreFiles, int ScannedFiles, int ScannedFolders, bool Truncated, string? Hint);

/// <summary>A pattern line and what it decides in the source: entries and their total size.</summary>
public sealed record LineHitRow(int Line, string Pattern, int Entries, SizeValue Size)
{
    public static LineHitRow Of(LineHits hits) => new(hits.LineNumber, hits.Text, hits.Entries, SizeValue.Of(hits.Bytes));
}

/// <summary>The global defaults; their lines have no hits while <paramref name="Used"/> is false.</summary>
public sealed record GlobalDefaultsInfo(bool Used, IReadOnlyList<LineHitRow> Lines);

/// <summary>
/// A <c>.backupignore</c> file: its path in the source, its text (rules, not data), how many lines it has and the hits per
/// pattern line. <paramref name="Truncated"/>: the text or the pattern lines were cut to the limits.
/// </summary>
public sealed record IgnoreFileInfo(string Path, string Content, int LineCount, IReadOnlyList<LineHitRow> Lines, bool Truncated);

/// <summary>
/// <paramref name="IgnoredFolder"/>: the parent folder whose rule ignores the entry (everything below an ignored folder
/// is ignored); null when the entry's own rule decided.
/// </summary>
public sealed record ExplainResult(string Path, bool Exists, string Kind, bool Ignored, RuleInfo? Rule, string? IgnoredFolder);

public sealed record FileListResult(string Plan, string Path, string In, IReadOnlyList<FileRow> Entries, int Total,
    bool Truncated, string? Hint);

/// <summary>
/// One entry of a folder. <paramref name="Files"/>: the files below a folder (null for a file). <paramref name="Ignored"/>,
/// <paramref name="PartlyIgnored"/> and <paramref name="Rule"/> are for source listings only (a version holds what was backed up).
/// </summary>
public sealed record FileRow(string Name, string Kind, SizeValue Size, DateTime? Date, int? Files, string? Extension,
    bool? Ignored, bool? PartlyIgnored, RuleInfo? Rule);

public sealed record RunHistoryResult(string Plan, IReadOnlyList<RunRow> Runs, int Total, bool Truncated, string? Hint);

public sealed record RunRow(string RunId, string Trigger, DateTime Start, DateTime End, long DurationMs, string Status,
    string? Reason, string? Version, int Files, SizeValue Size, int Skipped, IReadOnlyList<string>? Warnings);

/// <summary>
/// Where the growth from <paramref name="From"/> (a version) to <paramref name="To"/> (a version, or "source": what the
/// next run would back up) comes from below <paramref name="Path"/>. <paramref name="ByFolder"/>: one row per direct
/// child of the folder ("name/" for a folder); <paramref name="ByExtension"/>: per extension ("" = none); both the largest
/// net growth first and cut to <c>top</c>. <paramref name="FolderRows"/> and <paramref name="ExtensionRows"/> count all
/// rows; <paramref name="Total"/> sums them all.
/// </summary>
public sealed record GrowthResult(string Plan, string From, string To, string Path, GrowthRowInfo Total,
    IReadOnlyList<GrowthRowInfo> ByFolder, int FolderRows, IReadOnlyList<GrowthRowInfo> ByExtension, int ExtensionRows,
    bool Truncated, string? Hint);

/// <summary>
/// Files added, changed and deleted under <paramref name="Key"/> (null for the total), the sizes of the added and the
/// deleted files, and the net change of all of them (a changed file counts with its size change).
/// </summary>
public sealed record GrowthRowInfo(string? Key, int Added, int Changed, int Deleted, SizeValue AddedSize, SizeValue DeletedSize,
    SizeValue Net)
{
    public static GrowthRowInfo Of(GrowthRow row) => new(row.Key, row.Added, row.Changed, row.Deleted,
        SizeValue.Of(row.AddedBytes), SizeValue.Of(row.DeletedBytes), SizeValue.Signed(row.NetBytes));

    /// <summary>The sums of <paramref name="rows"/>, without a key.</summary>
    public static GrowthRowInfo TotalOf(IReadOnlyList<GrowthRow> rows) => new(null, rows.Sum(r => r.Added),
        rows.Sum(r => r.Changed), rows.Sum(r => r.Deleted), SizeValue.Of(rows.Sum(r => r.AddedBytes)),
        SizeValue.Of(rows.Sum(r => r.DeletedBytes)), SizeValue.Signed(rows.Sum(r => r.NetBytes)));
}
