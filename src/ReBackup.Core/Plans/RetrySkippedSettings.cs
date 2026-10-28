namespace ReBackup.Core.Plans;

/// <summary>Whether and how a run tries again for the files it had to skip (locked, unreadable, vanished while copying).</summary>
public sealed class RetrySkippedSettings
{
    /// <summary>The fewest retry attempts a plan can ask for.</summary>
    public const int MinAttempts = 1;

    /// <summary>The most retry attempts a plan can ask for.</summary>
    public const int MaxAttempts = 20;

    /// <summary>The shortest wait between two attempts, in minutes.</summary>
    public const int MinIntervalMinutes = 1;

    /// <summary>The longest wait between two attempts, in minutes (12 hours).</summary>
    public const int MaxIntervalMinutes = 720;

    /// <summary>Whether skipped files are tried again after the run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many times the skipped files are tried again before they stay skipped.</summary>
    public int Attempts { get; set; } = 3;

    /// <summary>The wait before each attempt, in minutes.</summary>
    public int IntervalMinutes { get; set; } = 5;
}
