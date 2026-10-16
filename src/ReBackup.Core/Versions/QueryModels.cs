namespace ReBackup.Core.Versions;

/// <summary>Size and file count of an entry in one version; for files also the time and the hash (null: no hash).</summary>
public sealed record IndexStats(long Size, int Files, DateTime? MtimeUtc, long? Hash);

/// <summary>
/// An entry of a folder. <paramref name="InVersion"/> is null when it exists only in the other version of a
/// comparison, <paramref name="InOther"/> when it exists only in the version itself (or nothing is compared).
/// </summary>
public sealed record IndexChild(long PathId, string Name, string Path, bool IsDirectory, IndexStats? InVersion,
    IndexStats? InOther);

/// <summary>How an entry differs between version A and version B (§11): Added = only in B, Deleted = only in A.</summary>
public enum DiffStatus { Unchanged, Added, Changed, Deleted }

/// <summary>A file's state in one version, compared with the previous version that had it.</summary>
public enum HistoryStatus { New, Changed, Unchanged, Deleted, Absent }

public sealed record HistoryEntry(IndexedVersion Version, bool Present, long? Size, DateTime? MtimeUtc, HistoryStatus Status);

/// <summary>
/// The versions an incremental plan's retention preview asks pool numbers for, by name: <paramref name="All"/> read from
/// the target, the <paramref name="Managed"/> ones, those the rules <paramref name="Deleted"/>, and the
/// <paramref name="Owned"/> ones oldest first (the forecast's).
/// </summary>
public sealed record PoolQuery(IReadOnlyList<string> All, IReadOnlyList<string> Managed, IReadOnlyList<string> Deleted,
    IReadOnlyList<string> Owned);

/// <summary>
/// The pool numbers of a <see cref="PoolQuery"/>, over the versions the index has; <paramref name="NotIndexed"/> names
/// the others. <paramref name="Pool"/>: what the managed versions need. <paramref name="Freed"/>: what deleting the
/// deleted ones frees (the content no other version holds); null when a kept version is not indexed.
/// <paramref name="ForecastBase"/>: the newest owned version that has files; null when there is none or the newest
/// owned version is not indexed. <paramref name="Growth"/>: the new content an average further version adds, from the
/// owned versions that have files.
/// </summary>
public sealed record PoolNumbers(long Pool, long? Freed, long? ForecastBase, long Growth, IReadOnlyList<string> NotIndexed);

public sealed record SearchHit(long PathId, string Path, string Name, bool IsDirectory, long Size);
