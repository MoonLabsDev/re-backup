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

public sealed record SearchHit(long PathId, string Path, string Name, bool IsDirectory, long Size);
