using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;

namespace ReBackup.App.ViewModels;

/// <summary>What the status dot of a plan shows.</summary>
public enum PlanDot
{
    /// <summary>Not active; never run or the last run completed.</summary>
    Idle,

    /// <summary>Queued or running.</summary>
    Active,

    /// <summary>The last run completed with warnings.</summary>
    Warning,

    /// <summary>The last run failed, was canceled or found the target full.</summary>
    Failed,
}

/// <summary>Queue state, progress and history of one plan.</summary>
public sealed partial class PlanRunViewModel : ObservableObject
{
    private const string NeverRunText = "Never run";

    /// <summary>No estimate before this much copying time: the first seconds are too noisy.</summary>
    private static readonly TimeSpan EtaWarmUp = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time;
    private long? _copyStarted;
    private long _copyStartBytes;
    private bool _canceling;

    public PlanRunViewModel(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsRunning), nameof(Dot))]
    private JobState? _state;

    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = "";

    /// <summary>The remaining copy time alone, e.g. "about 12 min left"; "" while it is not known.</summary>
    [ObservableProperty] private string _etaText = "";

    /// <summary>The newest run, or null when the plan has never run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRun), nameof(Dot))]
    private RunHistoryRow? _lastRun;

    [ObservableProperty] private string _lastRunText = NeverRunText;
    [ObservableProperty] private string _nextRunText = "";

    public ObservableCollection<RunHistoryRow> History { get; } = [];

    /// <summary>Queued or running.</summary>
    public bool IsActive => State is JobState.Queued or JobState.Running;

    public bool IsRunning => State is JobState.Running;

    public bool HasRun => LastRun is not null;

    /// <summary>The plan's state for its status dot: active first, otherwise the outcome of the last run.</summary>
    public PlanDot Dot => IsActive
        ? PlanDot.Active
        : LastRun?.Outcome switch
        {
            RunOutcome.Failed => PlanDot.Failed,
            RunOutcome.Warning => PlanDot.Warning,
            _ => PlanDot.Idle,
        };

    public void Apply(BackupJobUpdate update)
    {
        switch (update.State)
        {
            case JobState.Queued:
                _canceling = false;
                _copyStarted = null;
                State = JobState.Queued;
                IsIndeterminate = true;
                ProgressPercent = 0;
                ProgressText = "Queued";
                EtaText = "";
                break;

            case JobState.Running:
                State = JobState.Running;
                if (update.Progress is { } progress)
                    ShowProgress(progress);
                else
                    ProgressText = "Starting…";
                break;

            default:
                _canceling = false;
                _copyStarted = null;
                State = null;
                IsIndeterminate = false;
                ProgressPercent = 0;
                ProgressText = "";
                EtaText = "";
                break;
        }
    }

    /// <summary>Shows at once that the cancel was requested; the run may still need a moment to stop.</summary>
    public void MarkCanceling()
    {
        if (State != JobState.Running)
            return;
        _canceling = true;
        IsIndeterminate = true;
        ProgressText = "Canceling…";
        EtaText = "";
    }

    /// <summary>Entries oldest first, as read from the log; shown newest first.</summary>
    public void LoadHistory(IReadOnlyList<RunLogEntry> entries)
    {
        History.Clear();
        for (var i = entries.Count - 1; i >= 0; i--)
            History.Add(new RunHistoryRow(entries[i]));

        LastRun = History.Count == 0 ? null : History[0];
        LastRunText = LastRun is null
            ? NeverRunText
            : $"Last run {LastRun.StartText}: {LastRun.StatusText}";
    }

    private void ShowProgress(BackupProgress progress)
    {
        if (progress.Phase == BackupPhase.CleaningUp)
            _canceling = false;
        else if (_canceling)
            return;   // keep "Canceling…" until the run has stopped or starts cleaning up

        IsIndeterminate = progress.Phase is BackupPhase.Indexing or BackupPhase.CleaningUp;
        ProgressPercent = progress.Fraction * 100;
        EtaText = progress.Phase == BackupPhase.Copying ? Remaining(progress) : "";
        ProgressText = progress.Phase switch
        {
            BackupPhase.Indexing => $"Indexing… {progress.FilesDone:N0} files",
            BackupPhase.CreatingFolders => $"Creating folders… {progress.FilesDone:N0} / {progress.FilesTotal:N0}",
            BackupPhase.Copying =>
                $"{progress.FilesDone:N0} / {progress.FilesTotal:N0} files · " +
                $"{ByteSize.Format(progress.BytesDone)} / {ByteSize.Format(progress.BytesTotal)}",
            BackupPhase.CleaningUp => "Stopped — removing the incomplete copy…",
            BackupPhase.Retention => "Removing old versions…",
            _ => "Finishing…",
        };
    }

    /// <summary>Remaining copy time from the average rate since the copying began; "" while it is not known yet.</summary>
    private string Remaining(BackupProgress progress)
    {
        var now = _time.GetTimestamp();
        if (_copyStarted is not { } started)
        {
            _copyStarted = now;
            _copyStartBytes = progress.BytesDone;
            return "";
        }

        var elapsed = _time.GetElapsedTime(started, now);
        var copied = progress.BytesDone - _copyStartBytes;
        if (elapsed < EtaWarmUp || copied <= 0)
            return "";
        var remaining = TimeSpan.FromSeconds(
            Math.Max(0, progress.BytesTotal - progress.BytesDone) * elapsed.TotalSeconds / copied);
        return FormatRemaining(remaining);
    }

    public static string FormatRemaining(TimeSpan remaining) => remaining.TotalSeconds switch
    {
        < 60 => "less than a minute left",
        < 3600 => $"about {Math.Ceiling(remaining.TotalMinutes):0} min left",
        _ => $"about {(int)remaining.TotalHours} h {remaining.Minutes:00} min left",
    };
}
