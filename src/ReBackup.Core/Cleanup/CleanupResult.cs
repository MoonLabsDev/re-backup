namespace ReBackup.Core.Cleanup;

/// <summary>
/// A version to clean up: its folder name and its manifest stamp as the index recorded it when it was scanned
/// (<c>"m:…"</c>, as <see cref="Versions.VersionIndex.StampOfAsync"/> makes it). A version whose stamp differs now is
/// skipped as <see cref="CleanupOutcome.Changed"/>.
/// </summary>
public sealed record CleanupTarget(string Name, string Stamp);

/// <summary>What a cleanup did with one version.</summary>
public enum CleanupOutcome
{
    /// <summary>The manifest was shortened and the ignored files and folders were deleted.</summary>
    Cleaned,

    /// <summary>The rules ignore nothing in the version: nothing was written.</summary>
    NothingToDo,

    /// <summary>The version changed since the scan (another stamp, or its manifest was replaced meanwhile): nothing was deleted.</summary>
    Changed,

    /// <summary>The version is no longer one of the plan's (deleted, being written or deleted, renamed): nothing was touched.</summary>
    Gone,

    /// <summary>Something went wrong; <see cref="CleanupVersionResult.Error"/> says what.</summary>
    Failed,
}

/// <summary>
/// The result of one version: <paramref name="FilesRemoved"/> files of <paramref name="BytesRemoved"/> bytes (their
/// sizes in the manifest) went out of it; <paramref name="Error"/> is set for <see cref="CleanupOutcome.Failed"/>.
/// </summary>
public sealed record CleanupVersionResult(string Name, CleanupOutcome Outcome, int FilesRemoved, long BytesRemoved,
    string? Error);

/// <summary>
/// The result of a cleanup run: one entry per version that was started, oldest first (a canceled run leaves out the
/// versions it did not start, and sets <paramref name="Canceled"/>). <paramref name="BytesFreed"/> is what the run freed
/// on the target: for a full plan the sum of <see cref="CleanupVersionResult.BytesRemoved"/>, for an incremental plan what
/// the pool collect freed (0 when it was skipped or did not run).
/// </summary>
public sealed record CleanupResult(IReadOnlyList<CleanupVersionResult> Versions, long BytesFreed, bool Canceled)
{
    /// <summary>The files removed over all versions.</summary>
    public int FilesRemoved => Versions.Sum(v => v.FilesRemoved);
}

/// <summary>What a cleanup run does now: version <paramref name="Current"/> of <paramref name="Total"/> (1-based).</summary>
public readonly record struct CleanupProgress(int Current, int Total, string VersionName, CleanupPhase Phase);

/// <summary>The step of a cleanup run.</summary>
public enum CleanupPhase
{
    /// <summary>Reading the version's manifest and writing the shortened one.</summary>
    Rewriting,

    /// <summary>Deleting the ignored files and folders of the version.</summary>
    Deleting,

    /// <summary>Collecting the pool content no manifest references any more (incremental plans).</summary>
    CollectingPool,
}
