using ReBackup.Core.Backup;

namespace ReBackup.Core.Versions;

/// <summary>Where the index took a version's file list from.</summary>
public enum IndexOrigin { Manifest, Scan }

/// <summary>A version as the index knows it.</summary>
public sealed record IndexedVersion(long Id, string Name, DateTime LocalTime, VersionOwnership Ownership, string? Source,
    IndexOrigin Origin, int FileCount, long TotalBytes);

/// <summary>
/// Progress of <see cref="VersionIndex.Sync"/>: version <paramref name="Current"/> of <paramref name="Total"/>.
/// <paramref name="Finished"/> is true once that version is up to date in the index (imported or unchanged).
/// </summary>
public readonly record struct IndexSyncProgress(int Current, int Total, string VersionName, int FilesImported, bool Finished);

/// <summary>What a sync did; <paramref name="Errors"/> names versions that could not be read (they stay out of the index).</summary>
public sealed record IndexSyncResult(int Imported, int Unchanged, int Removed, IReadOnlyList<string> Errors);

/// <summary>Takes the manifest of a version a backup run has just finished.</summary>
public interface IVersionIndexSink
{
    void Add(string planId, VersionInfo version, BackupManifest manifest);
}
