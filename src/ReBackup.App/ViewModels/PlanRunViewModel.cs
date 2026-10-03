using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.App.Localization;
using ReBackup.Core.Backup;

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
    private LocText _nextRun = LocText.Empty;
    private TimeSpan? _remaining;
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
    [NotifyPropertyChangedFor(nameof(HasRun), nameof(LastRunText))]
    private RunHistoryRow? _lastRun;

    /// <summary>"Last run …: …" above the History tab's table; "Never run" before the first run.</summary>
    public string LastRunText => LastRun is null
        ? Loc.T("card.neverRun")
        : Loc.F("card.lastRun", ("when", LastRun.StartText), ("status", LastRun.StatusText));

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
    [ObservableProperty] private string _runToolTip = "";

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
                SetRemaining(null);
                break;

            case JobState.Running:
                State = JobState.Running;
                if (update.Progress is { } progress)
                {
                    _progress = progress;
                    SetRemaining(progress.Phase == BackupPhase.Copying && !_canceling ? Remaining(progress) : null);
                }
                break;

            default:
                _canceling = false;
                _copyStarted = null;
                _progress = null;
                _queuePosition = null;
                _queueAhead = null;
                State = null;
                SetRemaining(null);
                break;
        }
        UpdateCard();
    }

    /// <summary>The remaining copy time; null while it is not known. <see cref="EtaText"/> is its text.</summary>
    public TimeSpan? RemainingTime => _remaining;

    private void SetRemaining(TimeSpan? remaining)
    {
        _remaining = remaining;
        EtaText = remaining is { } time ? FormatRemaining(time) : "";
    }

    /// <summary>The language changed: the card, the remaining time and the history rows are built again.</summary>
    public void RefreshTexts()
    {
        SetRemaining(_remaining);
        OnPropertyChanged(nameof(LastRunText));
        foreach (var row in History)
            row.Refresh();
        UpdateCard();
    }

    /// <summary>Shows at once that the cancel was requested; the run may still need a moment to stop.</summary>
    public void MarkCanceling()
    {
        if (State != JobState.Running)
            return;
        _canceling = true;
        SetRemaining(null);
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
    /// only for a valid schedule; <paramref name="nextRun"/> e.g. "next Fri 03:00" or "no schedule".
    /// </summary>
    public void SetSchedule(bool enabled, bool schedulerPaused, LocText nextRun)
    {
        _enabled = enabled;
        _schedulerPaused = schedulerPaused;
        _nextRun = nextRun;
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
            return Formats.Time(local);
        if (Math.Abs((local.Date - now.Date).TotalDays) < 7)
            return Formats.WeekdayAndTime(local);
        return Formats.Date(local);
    }

    private void UpdateCard()
    {
        // Set in every state, so it follows a language switch while the plan is queued or running.
        var last = LastRun?.Entry;
        RunToolTip = last?.Status is RunStatus.Error or RunStatus.Full ? Loc.T("card.retryNow") : Loc.T("card.runNow");
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
            Loc.T("card.queued"),
            _queueAhead is { } ahead ? Loc.F("card.after", ("plan", ahead)) : Loc.T("card.startsNext"));

    private void ShowRunning()
    {
        var progress = _progress;
        if (_canceling || progress?.Phase == BackupPhase.CleaningUp)
        {
            Show(PlanCardState.Canceling, PlanCardAction.Canceling, 0, true, "",
                _canceling ? Loc.T("card.canceling") : Loc.T("card.stopping"),
                progress?.Phase == BackupPhase.CleaningUp ? Loc.T("card.removingCopy") : Loc.T("card.stoppingBackup"));
            return;
        }

        if (progress is not { } p)
        {
            Show(PlanCardState.Indexing, PlanCardAction.Cancel, 0, true, "", Loc.T("card.starting"), "");
            return;
        }

        // Never 100 % while the run is still going.
        var percent = $"{Math.Min(99, (int)Math.Floor(p.Fraction * 100))}%";
        switch (p.Phase)
        {
            case BackupPhase.Indexing:
                Show(PlanCardState.Indexing, PlanCardAction.Cancel, 0, true, Loc.T("card.scan"), Loc.T("card.indexing"),
                    Loc.F("common.fileCount", ("count", p.FilesDone)));
                break;
            case BackupPhase.RemovingLeftovers:
                // An unfinished copy or the remains of a deleted version (e.g. after the app was ended mid-run).
                Show(PlanCardState.Indexing, PlanCardAction.Cancel, 0, true, "", Loc.T("card.removingLeftovers"),
                    Loc.F("card.leftoversDetail", ("count", p.FilesDone)));
                break;
            case BackupPhase.CreatingFolders:
                Show(PlanCardState.CreatingFolders, PlanCardAction.Cancel, p.Fraction, false, percent,
                    Loc.T("card.creatingFolders"), string.Create(Loc.Culture, $"{p.FilesDone:N0} / {p.FilesTotal:N0}"));
                break;
            case BackupPhase.Copying:
                Show(PlanCardState.Copying, PlanCardAction.Cancel, p.Fraction, false, percent,
                    EtaText.Length > 0 ? EtaText : Loc.T("card.copying"),
                    Loc.F("card.copyDetail", ("bytes", BytesOf(p.BytesDone, p.BytesTotal)), ("count", p.FilesDone)));
                break;
            case BackupPhase.Retention:
                Show(PlanCardState.Finishing, PlanCardAction.Cancel, 1, false, percent, Loc.T("card.finishing"),
                    Loc.T("card.removingOld"));
                break;
            default:
                Show(PlanCardState.Finishing, PlanCardAction.Cancel, 1, false, percent, Loc.T("card.finishing"),
                    Loc.T("card.completingVersion"));
                break;
        }
    }

    private void ShowIdle()
    {
        var last = LastRun?.Entry;
        var nextRun = _nextRun.ToString();

        if (!_enabled)
        {
            Show(PlanCardState.Disabled, PlanCardAction.Run, 0, false, "", Loc.T("card.manualOnly"), LastRunShort());
            return;
        }
        if (_schedulerPaused)
        {
            Show(PlanCardState.Paused, PlanCardAction.Run, 0, false, "", Loc.T("card.schedulerPaused"), LastRunShort());
            return;
        }
        if (last is null)
        {
            Show(PlanCardState.NeverRun, PlanCardAction.Run, 0, false, "—", Loc.T("card.neverRun"), nextRun);
            return;
        }

        var when = ShortWhen(last.EndUtc.ToLocalTime(), _time.GetLocalNow().DateTime);
        switch (last.Status)
        {
            case RunStatus.Canceled:
                Show(PlanCardState.Canceled, PlanCardAction.Run, 0.29, false, "", Loc.F("card.canceledAt", ("when", when)), nextRun);
                break;
            case RunStatus.Full:
                Show(PlanCardState.Failed, PlanCardAction.Run, 1, false, "", Loc.T("card.targetFull"), nextRun,
                    last.Reason is { Length: > 0 } fullReason ? Loc.Known(fullReason) : Loc.T("card.targetFullToolTip"));
                break;
            case RunStatus.Error:
                Show(PlanCardState.Failed, PlanCardAction.Run, 1, false, "",
                    last.Reason is { Length: > 0 } reason
                        ? Loc.F("card.failedReason", ("reason", Loc.Known(reason)))
                        : Loc.T("card.failed"),
                    nextRun);
                break;
            default:
                if (LastRun!.Outcome == RunOutcome.Warning)
                {
                    Show(PlanCardState.Warning, PlanCardAction.Run, 1, false, "!",
                        last.SkippedCount > 0 ? Loc.F("card.skipped", ("count", last.SkippedCount)) : Loc.T("card.retentionWarnings"),
                        nextRun, LastRun.StatusText);
                }
                else
                {
                    Show(PlanCardState.Completed, PlanCardAction.Run, 1, false, "", Loc.F("card.backedUpAt", ("when", when)), nextRun);
                }
                break;
        }
    }

    /// <summary>"last 11:34 · OK" for the disabled and paused cards.</summary>
    private string LastRunShort()
    {
        if (LastRun is not { } row)
            return Loc.T("card.neverRunShort");
        var result = row.Outcome switch
        {
            RunOutcome.Ok => Loc.T("card.result.ok"),
            RunOutcome.Warning => Loc.T("card.result.warnings"),
            _ => row.Entry.Status == RunStatus.Canceled ? Loc.T("card.result.canceled") : Loc.T("card.result.failed"),
        };
        return Loc.F("card.lastShort",
            ("when", ShortWhen(row.Entry.EndUtc.ToLocalTime(), _time.GetLocalNow().DateTime)), ("result", result));
    }

    /// <summary>"2.2 / 13.6 GB" when both share a unit, else "512.0 MB / 13.6 GB" (decimal separator of the language).</summary>
    private static string BytesOf(long done, long total)
    {
        var doneText = Formats.Bytes(done);
        var totalText = Formats.Bytes(total);
        var doneUnit = doneText[(doneText.LastIndexOf(' ') + 1)..];
        var totalUnit = totalText[(totalText.LastIndexOf(' ') + 1)..];
        return doneUnit == totalUnit ? $"{doneText[..doneText.LastIndexOf(' ')]} / {totalText}" : $"{doneText} / {totalText}";
    }

    /// <summary>Remaining copy time from the average rate since the copying began; null while it is not known yet.</summary>
    private TimeSpan? Remaining(BackupProgress progress)
    {
        var now = _time.GetTimestamp();
        if (_copyStarted is not { } started)
        {
            _copyStarted = now;
            _copyStartBytes = progress.BytesDone;
            return null;
        }

        var elapsed = _time.GetElapsedTime(started, now);
        var copied = progress.BytesDone - _copyStartBytes;
        if (elapsed < EtaWarmUp || copied <= 0)
            return null;
        return TimeSpan.FromSeconds(Math.Max(0, progress.BytesTotal - progress.BytesDone) * elapsed.TotalSeconds / copied);
    }

    public static string FormatRemaining(TimeSpan remaining) => remaining.TotalSeconds switch
    {
        < 60 => Loc.T("card.remaining.underMinute"),
        < 3600 => Loc.F("card.remaining.minutes", ("minutes", (int)Math.Ceiling(remaining.TotalMinutes))),
        _ => Loc.F("card.remaining.hours", ("hours", (int)remaining.TotalHours), ("minutes", remaining.Minutes)),
    };
}
