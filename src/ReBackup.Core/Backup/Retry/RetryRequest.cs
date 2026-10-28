using ReBackup.Core.Plans;

namespace ReBackup.Core.Backup.Retry;

/// <summary>
/// One attempt to copy the skipped files of an earlier run into the version that run created.
/// </summary>
/// <param name="Plan">The plan as saved.</param>
/// <param name="RunId">The id of the run whose skipped files are retried.</param>
/// <param name="Version">The name of the version folder that run created.</param>
/// <param name="Files">The skipped files still to copy.</param>
/// <param name="Attempt">Which attempt this is (1-based).</param>
/// <param name="Attempts">How many attempts are allowed.</param>
public sealed record RetryRequest(BackupPlan Plan, string RunId, string Version, IReadOnlyList<SkippedEntry> Files,
    int Attempt, int Attempts);
