namespace ReBackup.Core.Cleanup;

/// <summary>
/// What a cleanup removes from one version: <paramref name="Files"/> files of <paramref name="Bytes"/> bytes.
/// <paramref name="Stamp"/> is the version's manifest stamp in the index when it was scanned ("" when none is recorded).
/// </summary>
public sealed record CleanupVersionScan(long VersionId, string Name, string Stamp, int Files, long Bytes);

/// <summary>
/// What one rule (its text and origin) removes over all scanned versions, with its largest single hit (a folder or a
/// file) as an example.
/// </summary>
public sealed record CleanupRuleHit(string Rule, string Origin, int Files, long Bytes, string LargestPath, long LargestBytes);

/// <summary>
/// The result of a cleanup scan: per version in the order asked for, by rule with the most bytes first, and for pool
/// plans the pool content the cleanup really frees (null for full plans).
/// </summary>
public sealed record CleanupScan(IReadOnlyList<CleanupVersionScan> Versions, IReadOnlyList<CleanupRuleHit> ByRule,
    long? PoolBytesFreed)
{
    /// <summary>The files removed over all scanned versions.</summary>
    public int Files => Versions.Sum(v => v.Files);

    /// <summary>The bytes removed over all scanned versions (in the version folders, not the pool).</summary>
    public long Bytes => Versions.Sum(v => v.Bytes);
}
