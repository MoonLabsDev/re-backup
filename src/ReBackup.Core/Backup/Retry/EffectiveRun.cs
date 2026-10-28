using System.Text.Json;
using ReBackup.Shared.Json;
using ReBackup.Shared.Schedule;

namespace ReBackup.Core.Backup.Retry;

/// <summary>
/// Combines a run's log entry with the retry lines that came after it, so every view shows the status the run
/// effectively has: a run whose skipped files were all copied later is a completed run with nothing skipped.
/// </summary>
public static class EffectiveRun
{
    /// <summary>
    /// Returns <paramref name="log"/> with every run that has retry lines (<see cref="RunLogEntry.RetryOf"/> equal to its id)
    /// replaced by a merged copy. Same order and count; a run without a finished retry and the retry lines themselves
    /// are the very same instances.
    /// </summary>
    public static IReadOnlyList<RunLogEntry> Apply(IReadOnlyList<RunLogEntry> log)
    {
        var retries = new Dictionary<string, List<RunLogEntry>>();
        foreach (var entry in log)
        {
            if (entry.Trigger != RunTrigger.Retry || entry.RetryOf is not { } of)
                continue;
            if (!retries.TryGetValue(of, out var lines))
                retries[of] = lines = [];
            lines.Add(entry);
        }
        if (retries.Count == 0)
            return log;

        var result = new List<RunLogEntry>(log.Count);
        foreach (var entry in log)
        {
            result.Add(entry.Trigger != RunTrigger.Retry && retries.TryGetValue(entry.RunId, out var lines)
                ? Merge(entry, lines)
                : entry);
        }
        return result;
    }

    private static RunLogEntry Merge(RunLogEntry orig, List<RunLogEntry> retries)
    {
        // Only a finished retry has a result: a failed or canceled one leaves the count as it was.
        var latest = retries.LastOrDefault(r => r.Status is RunStatus.Completed or RunStatus.CompletedWithWarnings);
        if (latest is null)
            return orig;

        var merged = JsonSerializer.Deserialize<RunLogEntry>(JsonSerializer.Serialize(orig, JsonDefaults.Compact), JsonDefaults.Compact)!;
        var remaining = latest.SkippedCount;
        merged.SkippedCount = orig.SkippedCount - orig.RetryableCount + remaining;
        merged.Skipped = [.. orig.Skipped.Where(e => !RetryPolicy.IsRetryable(e)).Concat(latest.Skipped).Take(RunLogEntry.MaxSkippedEntries)];
        merged.Status = merged.SkippedCount == 0 && !orig.MetForeignMarkers ? RunStatus.Completed : RunStatus.CompletedWithWarnings;
        merged.RetryRecovered = orig.RetryableCount - remaining;
        merged.RetryAttemptsMade = retries.Count(r => r.Status != RunStatus.Canceled);
        merged.VersionGone = orig.VersionGone;
        return merged;
    }
}
