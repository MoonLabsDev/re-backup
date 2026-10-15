using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;

namespace ReBackup.App.ViewModels;

/// <summary>One finding of a verify: the version, the file in it and what is wrong.</summary>
public sealed record VerifyProblemRow(string Version, string Path, string Problem);

/// <summary>
/// "Verify backup…" of an incremental plan (spec 9): Quick (every file's content is in the pool) or Thorough (every
/// content is also read and hashed again), with progress and the list of what is wrong. It holds the target's "verify"
/// lock while it runs; a target someone else holds is not verified. Nothing is repaired.
/// </summary>
public sealed partial class VerifyViewModel : ObservableObject, IPoolJob
{
    /// <summary>What a lock holder is doing while it verifies (<see cref="LockInfo.Operation"/>).</summary>
    private const string LockOperation = "verify";

    private readonly BackupPlan _plan;
    private readonly IStorageFactory _storages;
    private readonly string _host;
    private readonly TimeProvider _time;
    private readonly Action<bool> _running;
    private CancellationTokenSource? _cts;
    private Task _work = Task.CompletedTask;

    /// <summary>Thorough (read and hash every content) rather than Quick.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuick))]
    private bool _thorough;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChoose))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty] private string? _progressText;

    [ObservableProperty] private double _progressFraction;

    /// <summary>Why the verify did not run or stopped (the lock is held, the target cannot be read); null otherwise.</summary>
    [ObservableProperty] private string? _message;

    /// <summary>"No problems found." / "3 problems found." after a verify that ran to its end; null otherwise.</summary>
    [ObservableProperty] private string? _summary;

    /// <param name="plan">The saved incremental plan whose target is verified.</param>
    /// <param name="host">This computer, as the lock names it.</param>
    /// <param name="running">Called with true when a verify starts and with false when it has ended.</param>
    public VerifyViewModel(BackupPlan plan, IStorageFactory storages, string host, TimeProvider time, Action<bool>? running = null)
    {
        _running = running ?? (_ => { });
        _plan = plan;
        _storages = storages;
        _host = host;
        _time = time;
    }

    /// <summary>"Verify the backup of "{plan}"".</summary>
    public string Heading => Loc.F("verify.heading", ("plan", _plan.Name));

    public bool IsQuick => !Thorough;

    /// <summary>The verify's work off the UI thread; completed while none runs. Its lock is released when it ends.</summary>
    public Task Work => _work;

    /// <summary>The mode can be chosen while nothing runs.</summary>
    public bool CanChoose => !IsRunning;

    /// <summary>What the last verify found, by version and in manifest order.</summary>
    public ObservableCollection<VerifyProblemRow> Problems { get; } = [];

    /// <summary>The Quick | Thorough segments ("Quick" or "Thorough"); ignored while a verify runs.</summary>
    [RelayCommand]
    private void ChooseMode(string? mode)
    {
        if (!IsRunning)
            Thorough = mode == "Thorough";
    }

    private bool CanStart() => !IsRunning;

    /// <summary>Takes the "verify" lock, verifies off the UI thread, lists the problems and releases the lock.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var cts = _cts = new CancellationTokenSource();
        IsRunning = true;
        _running(true);
        Problems.Clear();
        Message = null;
        Summary = null;
        ProgressFraction = 0;
        ProgressText = Loc.T("verify.starting");
        try
        {
            var thorough = Thorough;
            var progress = Progress(cts);
            var work = Task.Run(() => RunAsync(thorough, progress, cts.Token));
            _work = work;
            var outcome = await work;
            if (outcome.Message is not null)
            {
                Message = outcome.Message;
            }
            else
            {
                foreach (var problem in outcome.Problems)
                    Problems.Add(new VerifyProblemRow(problem.Version, problem.Path, KindText(problem.Kind)));
                Summary = Problems.Count == 0 ? Loc.T("verify.healthy") : Loc.F("verify.problems", ("count", Problems.Count));
            }
            if (outcome.ReleaseError is { } error)
                Message = (Message is null ? "" : Message + "\n") + Loc.F("verify.lockNotReleased", ("error", Loc.Known(error)));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Message = Loc.T("verify.canceled");
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            IsRunning = false;
            ProgressText = null;
            ProgressFraction = 0;
            _running(false);
        }
    }

    /// <summary>Stops the verify; what it found so far is not listed.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        if (_cts is { IsCancellationRequested: false } cts)
        {
            cts.Cancel();
            ProgressText = Loc.T("verify.canceling");
        }
    }

    /// <summary>The dialog closes or the app exits: a verify still running stops (and releases its lock).</summary>
    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It ended meanwhile.
        }
    }

    private sealed record Outcome(IReadOnlyList<PoolProblem> Problems, string? Message, string? ReleaseError);

    /// <summary>Lock, verify, release. Problems with the target become the outcome's message; a cancel throws.</summary>
    private async Task<Outcome> RunAsync(bool thorough, IProgress<VerifyProgress> progress, CancellationToken ct)
    {
        IStorage target;
        TargetLock? held;
        try
        {
            target = _storages.Open(_plan.Target);
            held = await TargetLock.TryAcquireAsync(target, _host, LockOperation, _time, ct);
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            return new Outcome([], Loc.F("verify.failed", ("error", Loc.Known(ex.Message))), null);
        }
        if (held is null)
            return new Outcome([], await LockedTextAsync(target, ct), null);

        Outcome outcome;
        string? releaseError;
        try
        {
            outcome = await VerifyHeldAsync(target, thorough, progress, ct);
        }
        finally
        {
            // Also after a cancel: the lock must not outlive the verify.
            releaseError = await ReleaseAsync(held);
        }
        return outcome with { ReleaseError = releaseError };
    }

    /// <summary>The verify under the lock; a target that cannot be read becomes the outcome's message.</summary>
    private async Task<Outcome> VerifyHeldAsync(IStorage target, bool thorough, IProgress<VerifyProgress> progress,
        CancellationToken ct)
    {
        try
        {
            return new Outcome(await PoolVerifier.VerifyAsync(target, _plan.Id, _plan.Name, thorough, progress, ct), null, null);
        }
        catch (Exception ex) when (ex is StorageException or JsonException or ArgumentException)
        {
            return new Outcome([], Loc.F("verify.failed", ("error", Loc.Known(ex.Message))), null);
        }
    }

    /// <summary>Releases the verify lock; why it could not be released, or null.</summary>
    private async Task<string?> ReleaseAsync(TargetLock held)
    {
        try
        {
            await held.DisposeAsync();
            return null;
        }
        catch (StorageException ex)
        {
            Trace.TraceWarning($"The verify lock of plan {_plan.Id} could not be released: {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>"The target is in use by … since …" for the holder that kept the verify from running.</summary>
    private static async Task<string> LockedTextAsync(IStorage target, CancellationToken ct)
    {
        LockInfo? holder;
        try
        {
            holder = await TargetLock.ReadAsync(target, ct);
        }
        catch (Exception ex) when (ex is StorageException or JsonException)
        {
            holder = null;
        }
        return holder is null
            ? Loc.T("verify.lockedUnknown")
            : Loc.F("verify.locked", ("host", holder.Host), ("operation", holder.Operation),
                ("time", Formats.DateAndTime(DateTime.SpecifyKind(holder.StartedUtc, DateTimeKind.Utc).ToLocalTime())));
    }

    /// <summary>The progress line; reports come on the UI thread.</summary>
    private Progress<VerifyProgress> Progress(CancellationTokenSource cts) => new(p =>
    {
        if (!IsRunning || cts.IsCancellationRequested)
            return;
        ProgressFraction = p.Total == 0 ? 0 : (double)p.Done / p.Total;
        ProgressText = Loc.F("verify.progress", ("done", p.Done), ("count", p.Total), ("current", p.Current));
    });

    private static string KindText(PoolProblemKind kind) => kind switch
    {
        PoolProblemKind.Missing => Loc.T("verify.kind.missing"),
        PoolProblemKind.Corrupt => Loc.T("verify.kind.corrupt"),
        _ => Loc.T("verify.kind.manifestUnreadable"),
    };
}
