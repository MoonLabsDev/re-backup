using System.Text;
using System.Text.Json;
using ReBackup.Shared.Json;
using ReBackup.Shared.Schedule;

namespace ReBackup.Core.Backup;

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

    /// <summary>Problems that are not about a source file, e.g. a version retention could not delete. They do not change the status.</summary>
    public List<string> Warnings { get; set; } = [];

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
    public RunLogEntry? ReadLast()
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
                    if (Parse(lines[i].TrimEnd('\r')) is { } entry)
                        return entry;
                }
            }
        }

        // A small log, or no complete readable line in its last part.
        var all = ReadAll();
        return all.Count == 0 ? null : all[^1];
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
