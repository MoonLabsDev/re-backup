using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;

namespace ReBackup.App.ViewModels;

/// <summary>Queue state, progress and history of one plan.</summary>
public sealed partial class PlanRunViewModel : ObservableObject
{
    private const string NeverRunText = "Never run";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsRunning))]
    private JobState? _state;

    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _lastRunText = NeverRunText;

    public ObservableCollection<RunHistoryRow> History { get; } = [];

    /// <summary>Queued or running.</summary>
    public bool IsActive => State is JobState.Queued or JobState.Running;

    public bool IsRunning => State is JobState.Running;

    public void Apply(BackupJobUpdate update)
    {
        switch (update.State)
        {
            case JobState.Queued:
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
                State = null;
                IsIndeterminate = false;
                ProgressPercent = 0;
                ProgressText = "";
                break;
        }
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
        IsIndeterminate = progress.Phase == BackupPhase.Indexing;
        ProgressPercent = progress.Fraction * 100;
        ProgressText = progress.Phase switch
        {
            BackupPhase.Indexing => $"Indexing… {progress.FilesDone:N0} files",
            BackupPhase.Copying =>
                $"{progress.FilesDone:N0} / {progress.FilesTotal:N0} files · " +
                $"{ByteSize.Format(progress.BytesDone)} / {ByteSize.Format(progress.BytesTotal)}",
            _ => "Finishing…",
        };
    }
}
