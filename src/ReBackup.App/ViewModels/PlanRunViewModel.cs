using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;

namespace ReBackup.App.ViewModels;

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
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsRunning))]
    private JobState? _state;

    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _lastRunText = NeverRunText;
    [ObservableProperty] private string _nextRunText = "";

    public ObservableCollection<RunHistoryRow> History { get; } = [];

    /// <summary>Queued or running.</summary>
    public bool IsActive => State is JobState.Queued or JobState.Running;

    public bool IsRunning => State is JobState.Running;

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
    }

    /// <summary>Entries oldest first, as read from the log; shown newest first.</summary>
    public void LoadHistory(IReadOnlyList<RunLogEntry> entries)
    {
        History.Clear();
        for (var i = entries.Count - 1; i >= 0; i--)
            History.Add(new RunHistoryRow(entries[i]));

        LastRunText = History.Count == 0
            ? NeverRunText
            : $"Last run {History[0].StartText}: {History[0].StatusText}";
    }

    private void ShowProgress(BackupProgress progress)
    {
        if (progress.Phase == BackupPhase.CleaningUp)
            _canceling = false;
        else if (_canceling)
            return;   // keep "Canceling…" until the run has stopped or starts cleaning up

        IsIndeterminate = progress.Phase is BackupPhase.Indexing or BackupPhase.CleaningUp;
        ProgressPercent = progress.Fraction * 100;
        ProgressText = progress.Phase switch
        {
            BackupPhase.Indexing => $"Indexing… {progress.FilesDone:N0} files",
            BackupPhase.CreatingFolders => $"Creating folders… {progress.FilesDone:N0} / {progress.FilesTotal:N0}",
            BackupPhase.Copying =>
                $"{progress.FilesDone:N0} / {progress.FilesTotal:N0} files · " +
                $"{ByteSize.Format(progress.BytesDone)} / {ByteSize.Format(progress.BytesTotal)}" + EtaText(progress),
            BackupPhase.CleaningUp => "Stopped — removing the incomplete copy…",
            BackupPhase.Retention => "Removing old versions…",
            _ => "Finishing…",
        };
    }

    /// <summary>Remaining copy time from the average rate since the copying began; "" while it is not known yet.</summary>
    private string EtaText(BackupProgress progress)
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
        return " · " + FormatRemaining(remaining);
    }

    public static string FormatRemaining(TimeSpan remaining) => remaining.TotalSeconds switch
    {
        < 60 => "less than a minute left",
        < 3600 => $"about {Math.Ceiling(remaining.TotalMinutes):0} min left",
        _ => $"about {(int)remaining.TotalHours} h {remaining.Minutes:00} min left",
    };
}
