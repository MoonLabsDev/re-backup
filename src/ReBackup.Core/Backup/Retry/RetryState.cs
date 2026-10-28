using ReBackup.Storage;

namespace ReBackup.Core.Backup.Retry;

/// <summary>
/// The skipped files of one finished run that are still to be tried again, saved as <c>retries\&lt;planId&gt;.json</c>
/// (JSON via <c>JsonDefaults.Options</c>). A plan has at most one.
/// </summary>
public sealed class RetryState
{
    public string PlanId { get; set; } = "";

    /// <summary>The id of the regular run whose skipped files are retried.</summary>
    public string RunId { get; set; } = "";

    /// <summary>The name of the version folder that run created; every attempt copies into it.</summary>
    public string Version { get; set; } = "";

    /// <summary>
    /// When that run ended; a state is dropped once this is more than <see cref="RetryPlanner.MaxAge"/> ago (ruling R15).
    /// Null in a state saved before it was stored: such a state does not expire.
    /// </summary>
    public DateTime? RunEndUtc { get; set; }

    /// <summary>The plan's source when the run was made; a plan saved with another one drops the state.</summary>
    public StorageLocation Source { get; set; } = StorageLocation.FileSystem("");

    /// <summary>The plan's target when the run was made; a plan saved with another one drops the state.</summary>
    public StorageLocation Target { get; set; } = StorageLocation.FileSystem("");

    /// <summary>How many attempts were made so far (a canceled one does not count).</summary>
    public int Attempt { get; set; }

    /// <summary>How many attempts are allowed: the plan's value when the state was created, lowered when the plan is saved with less.</summary>
    public int Attempts { get; set; }

    /// <summary>When the next attempt is due.</summary>
    public DateTime NextDueUtc { get; set; }

    /// <summary>The skipped files still to copy.</summary>
    public List<SkippedEntry> Files { get; set; } = [];

    /// <summary>An independent copy (the entries and locations are immutable).</summary>
    public RetryState Clone() => new()
    {
        PlanId = PlanId,
        RunId = RunId,
        Version = Version,
        RunEndUtc = RunEndUtc,
        Source = Source,
        Target = Target,
        Attempt = Attempt,
        Attempts = Attempts,
        NextDueUtc = NextDueUtc,
        Files = [.. Files],
    };
}
