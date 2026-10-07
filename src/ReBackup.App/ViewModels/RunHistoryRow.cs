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
        IsCheckingVersion ? Loc.T("history.openVersion.checking")
        : CanOpenVersion ? Loc.F("history.openVersion.open", ("version", _entry.Version))
        : Loc.T("history.openVersion.missing");

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
    public bool HasDetails => VersionText.Length > 0 || HasSkipped || FooterText.Length > 0;

    /// <summary>"Version: …"; empty when the run produced none.</summary>
    public string VersionText => _entry.Version is null ? "" : Loc.F("history.details.version", ("version", _entry.Version));

    public bool HasSkipped => _entry.SkippedCount > 0;

    /// <summary>"Skipped (1,234):".</summary>
    public string SkippedTitle => Loc.F("history.details.skipped", ("count", _entry.SkippedCount));

    /// <summary>"… and 234 more" when the log keeps only the first entries; empty otherwise.</summary>
    public string SkippedMoreText => _entry.SkippedCount > _entry.Skipped.Count
        ? Loc.F("history.details.more", ("count", _entry.SkippedCount - _entry.Skipped.Count))
        : "";

    /// <summary>The skipped entries as a tree, built when the run's details are first shown.</summary>
    public SkippedTreeViewModel SkippedTree => _skippedTree ??= new SkippedTreeViewModel(_entry.Skipped);

    private SkippedTreeViewModel? _skippedTree;

    /// <summary>
    /// Versions deleted by retention and warnings. Warnings are stored in English; those Core wrote are shown in the
    /// applied language (<see cref="Loc.Known"/>), messages from Windows as stored.
    /// </summary>
    public string FooterText
    {
        get
        {
            var lines = new List<string>();
            if (_entry.RetentionDeleted.Count > 0)
            {
                lines.Add(Loc.T("history.details.deleted"));
                lines.AddRange(_entry.RetentionDeleted.Select(v => "  " + v));
            }
            if (_entry.Warnings.Count > 0)
            {
                lines.Add(Loc.T("history.details.warnings"));
                lines.AddRange(_entry.Warnings.Select(w => "  " + Loc.Known(w)));
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
    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        _skippedTree?.RefreshTexts();
    }
}
