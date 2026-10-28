namespace ReBackup.Core.Backup.Retry;

/// <summary>Which skipped files a later attempt can still copy.</summary>
public static class RetryPolicy
{
    /// <summary>
    /// The skips that may pass later: a file locked by another program, one that changed while it was copied, one the
    /// source refused or could not open. Everything else stays skipped: a file that is gone, a reserved name, an unreadable
    /// ignore file, a folder, an archived object, and an entry without a key (a raw message of the storage).
    /// </summary>
    private static readonly HashSet<string> RetryableKeys =
    [
        "core.file.locked",
        "core.skip.changed",
        "core.file.accessDenied",
        "core.file.cannotOpenFile",
    ];

    public static bool IsRetryable(SkippedEntry entry) => entry.Key is { } key && RetryableKeys.Contains(key);
}
