using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReBackup.Core.Backup.Retry;
using ReBackup.Shared.Json;
using ReBackup.Shared.Schedule;

namespace ReBackup.Core.Backup;

public enum RunStatus { Completed, CompletedWithWarnings, Full, Error, Canceled }

/// <param name="Path">The relative path of the skipped file or folder.</param>
/// <param name="Reason">Why, in English as it is shown and stored.</param>
/// <param name="Key">The <c>CoreTexts</c> key the reason was formatted from; null when it has none (a raw message of the storage) or the line is older than keys.</param>
public sealed record SkippedEntry(string Path, string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null);

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

    /// <summary>How many skipped entries <see cref="RetryPolicy"/> would retry; counts all, also those beyond <see cref="MaxSkippedEntries"/>.</summary>
    public int RetryableCount { get; set; }

    /// <summary>
    /// Every retryable skip of this run, not capped like <see cref="Skipped"/>, so a retry loses none of them. Held in
    /// memory only: it is not written to the log.
    /// </summary>
    [JsonIgnore]
    public List<SkippedEntry> RetryCandidates { get; } = [];

    /// <summary>For a retry run: the id of the run whose skipped files it retries.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RetryOf { get; set; }

    /// <summary>For a retry run: which attempt this is (1-based).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Attempt { get; set; }

    /// <summary>For a retry run: how many attempts the plan allows.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Attempts { get; set; }

    /// <summary>The run left a folder another computer marked alone (see <see cref="Warnings"/>); that alone makes it a run with warnings.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MetForeignMarkers { get; set; }

    /// <summary>The version this run created no longer exists (set when the run is read for retrying, not stored).</summary>
    [JsonIgnore]
    public bool VersionGone { get; set; }

    /// <summary>How many of this run's skipped files later attempts copied (set by the effective-status calculation, not stored).</summary>
    [JsonIgnore]
    public int RetryRecovered { get; set; }

    /// <summary>How many retry attempts were made for this run (set by the effective-status calculation, not stored).</summary>
    [JsonIgnore]
    public int RetryAttemptsMade { get; set; }

    public List<string> RetentionDeleted { get; set; } = [];

    /// <summary>Problems that are not about a source file, e.g. a version retention could not delete. They do not change the status.</summary>
    public List<string> Warnings { get; set; } = [];

    public void AddSkipped(SkippedEntry entry)
    {
        SkippedCount++;
        if (Skipped.Count < MaxSkippedEntries)
            Skipped.Add(entry);
        if (RetryPolicy.IsRetryable(entry))
        {
            RetryableCount++;
            RetryCandidates.Add(entry);
        }
    }

    /// <summary>
    /// For a retry run: a requested file that is still open, counted as skipped and kept as a candidate whatever its
    /// reason now (ruling R14). A file that comes back with a reason <see cref="RetryPolicy"/> would not retry (archived,
    /// or a raw message of the storage) is still not copied: dropping it would show it as recovered.
    /// </summary>
    internal void AddStillOpen(SkippedEntry entry)
    {
        SkippedCount++;
        if (Skipped.Count < MaxSkippedEntries)
            Skipped.Add(entry);
        RetryableCount++;
        RetryCandidates.Add(entry);
    }
}

/// <summary>Append-only JSON Lines file with one entry per run of a plan.</summary>
public sealed class RunLog
{
    private const int AppendAttempts = 5;
    private static readonly TimeSpan AppendRetryDelay = TimeSpan.FromMilliseconds(50);
    private const int TailBytes = 64 * 1024;

    public RunLog(string logFile) => LogFile = logFile;

    public string LogFile { get; }

    public void Append(RunLogEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(LogFile))!);
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonDefaults.Compact) + "\n");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                if (stream.Length > 0 && !EndsWithNewline())
                    stream.WriteByte((byte)'\n');
                stream.Write(bytes);
                return;
            }
            catch (IOException ex) when (attempt < AppendAttempts && IsSharingViolation(ex))
            {
                Thread.Sleep(AppendRetryDelay * attempt);
            }
        }
    }

    /// <summary>Checks the last byte through a separate read handle, because the append handle cannot be read.</summary>
    private bool EndsWithNewline()
    {
        using var reader = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (reader.Length == 0)
            return true;
        reader.Seek(-1, SeekOrigin.End);
        return reader.ReadByte() == '\n';
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    /// <summary>All readable entries, oldest first.</summary>
    public IReadOnlyList<RunLogEntry> ReadAll()
    {
        var entries = new List<RunLogEntry>();
        if (!File.Exists(LogFile))
            return entries;

        using var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (Parse(line) is { } entry)
                entries.Add(entry);
        }
        return entries;
    }

    /// <summary>The newest readable entry; null when there is none. Reads only the end of a large log where possible.</summary>
    public RunLogEntry? ReadLast() => ReadLastWhere(_ => true);

    /// <summary>The newest readable entry that is not a retry run; null when there is none. Reads only the end of a large log where possible.</summary>
    public RunLogEntry? ReadLastRegular() => ReadLastWhere(entry => entry.Trigger != RunTrigger.Retry);

    private RunLogEntry? ReadLastWhere(Func<RunLogEntry, bool> accept)
    {
        if (!File.Exists(LogFile))
            return null;

        using (var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length > TailBytes)
            {
                stream.Seek(-TailBytes, SeekOrigin.End);
                var buffer = new byte[TailBytes];
                var length = stream.ReadAtLeast(buffer, TailBytes, throwOnEndOfStream: false);
                var lines = Encoding.UTF8.GetString(buffer, 0, length).Split('\n');
                // The first piece is usually the cut-off end of an older line.
                for (var i = lines.Length - 1; i >= 1; i--)
                {
                    if (Parse(lines[i].TrimEnd('\r')) is { } entry && accept(entry))
                        return entry;
                }
            }
        }

        // A small log, or no complete readable line in its last part that qualifies.
        return ReadAll().LastOrDefault(accept);
    }

    /// <summary>A readable entry; null for a blank or damaged line.</summary>
    private static RunLogEntry? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            if (JsonSerializer.Deserialize<RunLogEntry>(line, JsonDefaults.Compact) is not { } entry)
                return null;
            entry.Skipped ??= [];
            entry.RetentionDeleted ??= [];
            entry.Warnings ??= [];
            return entry;
        }
        catch (JsonException)
        {
            // A damaged line must not hide the rest of the history.
            return null;
        }
    }
}
