using System.Globalization;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;

namespace ReBackup.App.ViewModels;

/// <summary>One run of a plan, formatted for the History tab.</summary>
public sealed class RunHistoryRow
{
    private readonly RunLogEntry _entry;

    public RunHistoryRow(RunLogEntry entry) => _entry = entry;

    public string StartText => _entry.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string DurationText => FormatDuration(_entry.DurationMs);
    public string TriggerText => _entry.Trigger == RunTrigger.CatchUp ? "Catch-up" : _entry.Trigger.ToString();

    public string StatusText => _entry.Status switch
    {
        RunStatus.CompletedWithWarnings => "Completed with warnings",
        RunStatus.Full => "Aborted: target full",
        RunStatus.Error => "Aborted: error",
        RunStatus.Canceled => "Canceled",
        RunStatus.Completed when _entry.Warnings.Count > 0 => "Completed (retention warnings)",
        _ => "Completed",
    };

    public string Reason => _entry.Reason ?? "";
    public string FilesText => _entry.FilesCopied.ToString("N0", CultureInfo.CurrentCulture);
    public string SizeText => ByteSize.Format(_entry.BytesCopied);
    public bool HasDetails => Details.Length > 0;

    public string Details
    {
        get
        {
            var lines = new List<string>();
            if (_entry.Version is not null)
                lines.Add($"Version: {_entry.Version}");
            if (_entry.SkippedCount > 0)
            {
                lines.Add($"Skipped ({_entry.SkippedCount:N0}):");
                lines.AddRange(_entry.Skipped.Select(s => $"  {s.Path} — {s.Reason}"));
                if (_entry.SkippedCount > _entry.Skipped.Count)
                    lines.Add($"  … and {_entry.SkippedCount - _entry.Skipped.Count:N0} more");
            }
            if (_entry.RetentionDeleted.Count > 0)
            {
                lines.Add("Deleted by retention:");
                lines.AddRange(_entry.RetentionDeleted.Select(v => "  " + v));
            }
            if (_entry.Warnings.Count > 0)
            {
                lines.Add("Warnings:");
                lines.AddRange(_entry.Warnings.Select(w => "  " + w));
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    public static string FormatDuration(long milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours} h {duration.Minutes:00} min {duration.Seconds:00} s";
        if (duration.TotalMinutes >= 1)
            return $"{duration.Minutes} min {duration.Seconds:00} s";
        return duration.TotalSeconds >= 1 ? $"{duration.Seconds} s" : "under 1 s";
    }
}
