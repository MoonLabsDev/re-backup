using System.Text.Json;
using ReBackup.Core.Json;

namespace ReBackup.Core.Backup;

public enum RunTrigger { Manual, Scheduled, CatchUp }

public enum RunStatus { Completed, CompletedWithWarnings, Full, Error, Canceled }

public sealed record SkippedEntry(string Path, string Reason);

/// <summary>One line of a plan's run log.</summary>
public sealed class RunLogEntry
{
    /// <summary>At most this many skipped entries are stored per run; <see cref="SkippedCount"/> counts all.</summary>
    public const int MaxSkippedEntries = 1000;

    public string RunId { get; set; } = "";
    public RunTrigger Trigger { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public long DurationMs { get; set; }
    public RunStatus Status { get; set; }

    /// <summary>Why the run ended as Full or Error.</summary>
    public string? Reason { get; set; }

    /// <summary>Name of the version folder; null when the run did not complete.</summary>
    public string? Version { get; set; }

    public int FilesCopied { get; set; }
    public long BytesCopied { get; set; }
    public int SkippedCount { get; set; }
    public List<SkippedEntry> Skipped { get; set; } = [];
    public List<string> RetentionDeleted { get; set; } = [];

    public void AddSkipped(SkippedEntry entry)
    {
        SkippedCount++;
        if (Skipped.Count < MaxSkippedEntries)
            Skipped.Add(entry);
    }
}

/// <summary>Append-only JSON Lines file with one entry per run of a plan.</summary>
public sealed class RunLog
{
    public RunLog(string logFile) => LogFile = logFile;

    public string LogFile { get; }

    public void Append(RunLogEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(LogFile))!);
        File.AppendAllText(LogFile, JsonSerializer.Serialize(entry, JsonDefaults.Compact) + "\n");
    }

    /// <summary>All readable entries, oldest first.</summary>
    public IReadOnlyList<RunLogEntry> ReadAll()
    {
        var entries = new List<RunLogEntry>();
        if (!File.Exists(LogFile))
            return entries;

        foreach (var line in File.ReadLines(LogFile))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<RunLogEntry>(line, JsonDefaults.Compact) is not { } entry)
                    continue;
                entry.Skipped ??= [];
                entry.RetentionDeleted ??= [];
                entries.Add(entry);
            }
            catch (JsonException)
            {
                // A damaged line must not hide the rest of the history.
            }
        }
        return entries;
    }
}
