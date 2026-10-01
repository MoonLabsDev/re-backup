using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.App.Localization;
using ReBackup.Core.Backup;

namespace ReBackup.App.ViewModels;

/// <summary>How a run ended, for status dots.</summary>
public enum RunOutcome
{
    Ok,
    Warning,
    Failed,
}

/// <summary>One run of a plan, formatted for the History tab.</summary>
public sealed partial class RunHistoryRow : ObservableObject
{
    private readonly RunLogEntry _entry;

    /// <summary>
    /// Whether the run's version folder was found in the target. False until the check of the history load
    /// (<see cref="PlanRunViewModel.LoadHistory"/>, off the UI thread) has answered.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenVersionToolTip))]
    private bool _canOpenVersion;

    /// <summary>True while the target is still being looked at for this row's version.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenVersionToolTip))]
    private bool _isCheckingVersion;

    /// <param name="target">The plan's target, where the version folder is looked up.</param>
    public RunHistoryRow(RunLogEntry entry, string? target = null)
    {
        _entry = entry;
        Target = target;
        _isCheckingVersion = HasVersion;
    }

    /// <summary>The log entry of the run.</summary>
    public RunLogEntry Entry => _entry;

    /// <summary>The target the version was looked up in.</summary>
    public string? Target { get; }

    /// <summary>The version folder the run produced; null when it produced none.</summary>
    public string? VersionName => _entry.Version;

    public bool HasVersion => !string.IsNullOrEmpty(_entry.Version);

    public string OpenVersionToolTip =>
        IsCheckingVersion ? "Checking whether the version still exists…"
        : CanOpenVersion ? $"Open {_entry.Version} in Explorer"
        : "Version no longer exists";

    /// <summary>The answer of the existence check.</summary>
    public void ApplyVersionCheck(bool exists)
    {
        CanOpenVersion = HasVersion && exists;
        IsCheckingVersion = false;
    }

    /// <summary>The folder was found missing when it was to be opened.</summary>
    public void MarkVersionMissing() => CanOpenVersion = false;

    public string StartText => Formats.DateAndTimeSeconds(_entry.StartUtc.ToLocalTime());
    public string DurationText => FormatDuration(_entry.DurationMs);
    public string TriggerText => Loc.T("enum.runTrigger." + _entry.Trigger);

    public string StatusText => _entry.Status switch
    {
        RunStatus.CompletedWithWarnings => Loc.T("run.status.completedWithWarnings"),
        RunStatus.Full => Loc.T("run.status.full"),
        RunStatus.Error => Loc.T("run.status.error"),
        RunStatus.Canceled => Loc.T("run.status.canceled"),
        RunStatus.Completed when _entry.Warnings.Count > 0 => Loc.T("run.status.completedRetentionWarnings"),
        _ => Loc.T("run.status.completed"),
    };

    public RunOutcome Outcome => _entry.Status switch
    {
        RunStatus.CompletedWithWarnings => RunOutcome.Warning,
        RunStatus.Completed => _entry.Warnings.Count > 0 ? RunOutcome.Warning : RunOutcome.Ok,
        _ => RunOutcome.Failed,
    };

    /// <summary>Why the run failed, in the applied language when Core wrote it (stored in English).</summary>
    public string Reason => Loc.Known(_entry.Reason);
    public string FilesText => Formats.Count(_entry.FilesCopied);
    public string SizeText => Formats.Bytes(_entry.BytesCopied);
    public string SkippedText => Formats.Count(_entry.SkippedCount);
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
                lines.Add($"Skipped ({Formats.Count(_entry.SkippedCount)}):");
                lines.AddRange(_entry.Skipped.Select(s => $"  {s.Path} — {s.Reason}"));
                if (_entry.SkippedCount > _entry.Skipped.Count)
                    lines.Add($"  … and {Formats.Count(_entry.SkippedCount - _entry.Skipped.Count)} more");
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
        {
            return Loc.F("common.duration.hms", ("hours", (int)duration.TotalHours), ("minutes", duration.Minutes),
                ("seconds", duration.Seconds));
        }
        if (duration.TotalMinutes >= 1)
            return Loc.F("common.duration.ms", ("minutes", duration.Minutes), ("seconds", duration.Seconds));
        return duration.TotalSeconds >= 1
            ? Loc.F("common.duration.s", ("seconds", duration.Seconds))
            : Loc.T("common.duration.underSecond");
    }

    /// <summary>The language changed: every text of the row is read again.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
