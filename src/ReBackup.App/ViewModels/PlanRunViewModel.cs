using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;

namespace ReBackup.App.ViewModels;

/// <summary>
/// What the plan card shows (progress ring, phase and detail lines). Active states come first, then a disabled plan,
/// then a paused scheduler, then the outcome of the last run.
/// </summary>
public enum PlanCardState
{
    NeverRun,
    Completed,
    Warning,
    /// <summary>The last run ended with an error or a full target.</summary>
    Failed,
    Canceled,
    /// <summary>The plan is disabled: it runs only by hand.</summary>
    Disabled,
    /// <summary>The scheduler is paused (the plan has a valid schedule).</summary>
    Paused,
    Queued,
    Indexing,
    CreatingFolders,
    Copying,
    /// <summary>Finishing the version or removing old versions (retention).</summary>
    Finishing,
    /// <summary>Stopping: canceled by the user, or the incomplete copy is being removed.</summary>
    Canceling,
}

/// <summary>The action button of the plan card.</summary>
public enum PlanCardAction
{
    Run,
    Cancel,
    RemoveFromQueue,
    /// <summary>The cancel button, disabled while the run stops.</summary>
    Canceling,
}

/// <summary>Queue state, progress and history of one plan, and the plan card built from them.</summary>
public sealed partial class PlanRunViewModel : ObservableObject
{
    private const string NeverRunText = "Never run";

    /// <summary>No estimate before this much copying time: the first seconds are too noisy.</summary>
    private static readonly TimeSpan EtaWarmUp = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time;
    private long? _copyStarted;
    private long _copyStartBytes;
    private bool _canceling;
    private BackupProgress? _progress;
    private int? _queuePosition;
    private string? _queueAhead;
    private bool _enabled = true;
    private bool _schedulerPaused;
    private string _nextRunText = "";
    private int _historyLoads;   // tells a version check whether its rows are still the shown ones

    public PlanRunViewModel(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        UpdateCard();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsRunning))]
    private JobState? _state;

    /// <summary>The remaining copy time alone, e.g. "12 min left"; "" while it is not known.</summary>
    [ObservableProperty] private string _etaText = "";

    /// <summary>The newest run, or null when the plan has never run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRun))]
    private RunHistoryRow? _lastRun;

    [ObservableProperty] private string _lastRunText = NeverRunText;

    // ------------------------------------------------------------------ the plan card
    [ObservableProperty] private PlanCardState _cardState;
    [ObservableProperty] private PlanCardAction _cardAction;

    /// <summary>Fill of the ring, 0..1 (ignored while <see cref="RingIndeterminate"/>).</summary>
    [ObservableProperty] private double _ringValue;
    [ObservableProperty] private bool _ringIndeterminate;

    /// <summary>Text in the ring ("41%", "#2", "scan", "—", "!"); icon states set their glyph in the view.</summary>
    [ObservableProperty] private string _ringText = "";

    /// <summary>The line under the name: the phase, the last result or the remaining copy time.</summary>
    [ObservableProperty] private string _phaseText = "";

    /// <summary>Tool tip of the phase line: the full reason of a failed run, otherwise the phase text.</summary>
    [ObservableProperty] private string _phaseToolTip = "";

    /// <summary>The third line: counts while running, the next or last run while idle.</summary>
    [ObservableProperty] private string _detailText = "";

    /// <summary>Tool tip of the run button: "Retry now" after a failed run.</summary>
    [ObservableProperty] private string _runToolTip = "Run now";

    public ObservableCollection<RunHistoryRow> History { get; } = [];

    /// <summary>Queued or running.</summary>
    public bool IsActive => State is JobState.Queued or JobState.Running;

    public bool IsRunning => State is JobState.Running;

    public bool HasRun => LastRun is not null;

    public void Apply(BackupJobUpdate update)
    {
        switch (update.State)
        {
            case JobState.Queued:
                _canceling = false;
                _copyStarted = null;
                _progress = null;
                _queuePosition = null;
                _queueAhead = null;
                State = JobState.Queued;
                EtaText = "";
                break;

            case JobState.Running:
                State = JobState.Running;
                if (update.Progress is { } progress)
                {
                    _progress = progress;
                    EtaText = progress.Phase == BackupPhase.Copying && !_canceling ? Remaining(progress) : "";
                }
                break;

            default:
                _canceling = false;
                _copyStarted = null;
                _progress = null;
                _queuePosition = null;
                _queueAhead = null;
                State = null;
                EtaText = "";
                break;
        }
        UpdateCard();
    }

    /// <summary>Shows at once that the cancel was requested; the run may still need a moment to stop.</summary>
    public void MarkCanceling()
    {
        if (State != JobState.Running)
            return;
        _canceling = true;
        EtaText = "";
        UpdateCard();
    }

    /// <summary>Where the queued job stands: <paramref name="position"/> 1 starts next, after <paramref name="aheadName"/>.</summary>
    public void SetQueuePosition(int position, string? aheadName)
    {
        _queuePosition = position;
        _queueAhead = aheadName;
        UpdateCard();
    }

    /// <summary>
    /// The plan's schedule as saved: <paramref name="enabled"/> false = runs only by hand; <paramref name="schedulerPaused"/>
    /// only for a valid schedule; <paramref name="nextRunText"/> e.g. "next Fri 03:00" or "no schedule".
    /// </summary>
    public void SetSchedule(bool enabled, bool schedulerPaused, string nextRunText)
    {
        _enabled = enabled;
        _schedulerPaused = schedulerPaused;
        _nextRunText = nextRunText;
        UpdateCard();
    }

    /// <summary>
    /// Entries oldest first, as read from the log; shown newest first. Whether the version of each run still exists in
    /// <paramref name="target"/> is checked once per load, off the UI thread (the target may be an unreachable share):
    /// the rows can open their version only after that check has answered.
    /// </summary>
    public void LoadHistory(IReadOnlyList<RunLogEntry> entries, string? target = null)
    {
        History.Clear();
        for (var i = entries.Count - 1; i >= 0; i--)
            History.Add(new RunHistoryRow(entries[i], target));
        VersionCheck = CheckVersionsAsync(++_historyLoads, target, History.Where(row => row.HasVersion).ToList());

        LastRun = History.Count == 0 ? null : History[0];
        LastRunText = LastRun is null
            ? NeverRunText
            : $"Last run {LastRun.StartText}: {LastRun.StatusText}";
        UpdateCard();
    }

    /// <summary>The running check of the last <see cref="LoadHistory"/>; completes when its rows were updated.</summary>
    public Task VersionCheck { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Lists the target's folder names once on a worker thread, then marks the rows on the calling (UI) thread.
    /// A missing or unreadable target marks every row missing. A newer load makes the answer obsolete.
    /// </summary>
    private async Task CheckVersionsAsync(int load, string? target, IReadOnlyList<RunHistoryRow> rows)
    {
        if (rows.Count == 0)
            return;
        IReadOnlySet<string> names;
        try
        {
            names = await Task.Run(() => Core.Backup.VersionName.FolderNamesIn(target));
        }
        catch (Exception)
        {
            names = new HashSet<string>();
        }
        if (load != _historyLoads)
            return;
        foreach (var row in rows)
            row.ApplyVersionCheck(Core.Backup.VersionName.FolderIn(target, row.VersionName) is not null &&
                                  names.Contains(row.VersionName!));
    }

    /// <summary>A time on the card: "14:05" today, "Fri 03:00" within a week either way, else the date.</summary>
    public static string ShortWhen(DateTime local, DateTime now)
    {
        if (local.Date == now.Date)
            return local.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (Math.Abs((local.Date - now.Date).TotalDays) < 7)
            return local.ToString("ddd HH:mm", CultureInfo.CurrentCulture);
        return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private void UpdateCard()
    {
        if (State == JobState.Queued)
            ShowQueued();
        else if (State == JobState.Running)
            ShowRunning();
        else
            ShowIdle();
        if (PhaseToolTip.Length == 0)
            PhaseToolTip = PhaseText;
    }

    private void Show(PlanCardState state, PlanCardAction action, double ring, bool indeterminate, string ringText,
        string phase, string detail, string phaseToolTip = "")
    {
        CardState = state;
        CardAction = action;
        RingValue = ring;
        RingIndeterminate = indeterminate;
        RingText = ringText;
        PhaseText = phase;
        PhaseToolTip = phaseToolTip;
        DetailText = detail;
    }

    private void ShowQueued() =>
        Show(PlanCardState.Queued, PlanCardAction.RemoveFromQueue, 0, false,
            _queuePosition is { } position ? $"#{position}" : "#",
            "Queued",
            _queueAhead is { } ahead ? $"after “{ahead}”" : "starts next");

    private void ShowRunning()
    {
        var progress = _progress;
        if (_canceling || progress?.Phase == BackupPhase.CleaningUp)
        {
            Show(PlanCardState.Canceling, PlanCardAction.Canceling, 0, true, "",
                _canceling ? "Canceling" : "Stopping",
                progress?.Phase == BackupPhase.CleaningUp ? "removing the incomplete copy…" : "stopping the backup…");
            return;
        }

        if (progress is not { } p)
        {
            Show(PlanCardState.Indexing, PlanCardAction.Cancel, 0, true, "", "Starting…", "");
            return;
        }

        // Never 100 % while the run is still going.
        var percent = $"{Math.Min(99, (int)Math.Floor(p.Fraction * 100))}%";
        switch (p.Phase)
        {
            case BackupPhase.Indexing:
                Show(PlanCardState.Indexing, PlanCardAction.Cancel, 0, true, "scan", "Indexing…",
                    $"{p.FilesDone:N0} files");
                break;
            case BackupPhase.CreatingFolders:
                Show(PlanCardState.CreatingFolders, PlanCardAction.Cancel, p.Fraction, false, percent, "Creating folders",
                    $"{p.FilesDone:N0} / {p.FilesTotal:N0}");
                break;
            case BackupPhase.Copying:
                Show(PlanCardState.Copying, PlanCardAction.Cancel, p.Fraction, false, percent,
                    EtaText.Length > 0 ? EtaText : "Copying…",
                    $"{BytesOf(p.BytesDone, p.BytesTotal)} · {p.FilesDone:N0} files");
                break;
            case BackupPhase.Retention:
                Show(PlanCardState.Finishing, PlanCardAction.Cancel, 1, false, percent, "Finishing",
                    "removing old versions…");
                break;
            default:
                Show(PlanCardState.Finishing, PlanCardAction.Cancel, 1, false, percent, "Finishing",
                    "completing the version…");
                break;
        }
    }

    private void ShowIdle()
    {
        var last = LastRun?.Entry;
        RunToolTip = last?.Status is RunStatus.Error or RunStatus.Full ? "Retry now" : "Run now";

        if (!_enabled)
        {
            Show(PlanCardState.Disabled, PlanCardAction.Run, 0, false, "", "Manual only", LastRunShort());
            return;
        }
        if (_schedulerPaused)
        {
            Show(PlanCardState.Paused, PlanCardAction.Run, 0, false, "", "Scheduler paused", LastRunShort());
            return;
        }
        if (last is null)
        {
            Show(PlanCardState.NeverRun, PlanCardAction.Run, 0, false, "—", NeverRunText, _nextRunText);
            return;
        }

        var when = ShortWhen(last.EndUtc.ToLocalTime(), _time.GetLocalNow().DateTime);
        switch (last.Status)
        {
            case RunStatus.Canceled:
                Show(PlanCardState.Canceled, PlanCardAction.Run, 0.29, false, "", $"Canceled · {when}", _nextRunText);
                break;
            case RunStatus.Full:
                Show(PlanCardState.Failed, PlanCardAction.Run, 1, false, "", "Target full", _nextRunText,
                    last.Reason is { Length: > 0 } fullReason ? fullReason : "The target is full.");
                break;
            case RunStatus.Error:
                Show(PlanCardState.Failed, PlanCardAction.Run, 1, false, "",
                    last.Reason is { Length: > 0 } reason ? $"Failed: {reason}" : "Failed", _nextRunText);
                break;
            default:
                if (LastRun!.Outcome == RunOutcome.Warning)
                {
                    Show(PlanCardState.Warning, PlanCardAction.Run, 1, false, "!",
                        last.SkippedCount > 0 ? $"{last.SkippedCount:N0} skipped" : "Retention warnings", _nextRunText,
                        LastRun.StatusText);
                }
                else
                {
                    Show(PlanCardState.Completed, PlanCardAction.Run, 1, false, "", $"Backed up · {when}", _nextRunText);
                }
                break;
        }
    }

    /// <summary>"last 11:34 · OK" for the disabled and paused cards.</summary>
    private string LastRunShort()
    {
        if (LastRun is not { } row)
            return "never run";
        var result = row.Outcome switch
        {
            RunOutcome.Ok => "OK",
            RunOutcome.Warning => "warnings",
            _ => row.Entry.Status == RunStatus.Canceled ? "canceled" : "failed",
        };
        return $"last {ShortWhen(row.Entry.EndUtc.ToLocalTime(), _time.GetLocalNow().DateTime)} · {result}";
    }

    /// <summary>"2.2 / 13.6 GB" when both share a unit, else "512.0 MB / 13.6 GB".</summary>
    private static string BytesOf(long done, long total)
    {
        var doneText = ByteSize.Format(done);
        var totalText = ByteSize.Format(total);
        var doneUnit = doneText[(doneText.LastIndexOf(' ') + 1)..];
        var totalUnit = totalText[(totalText.LastIndexOf(' ') + 1)..];
        return doneUnit == totalUnit ? $"{doneText[..doneText.LastIndexOf(' ')]} / {totalText}" : $"{doneText} / {totalText}";
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
        < 3600 => $"{Math.Ceiling(remaining.TotalMinutes):0} min left",
        _ => $"{(int)remaining.TotalHours} h {remaining.Minutes:00} min left",
    };
}
