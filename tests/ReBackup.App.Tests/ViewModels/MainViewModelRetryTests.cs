using System.IO;
using System.Text;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Backup.Retry;
using ReBackup.Core.Plans;
using ReBackup.Shared.Schedule;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests.ViewModels;

/// <summary>
/// The retry of skipped files as the app runs it: the planner hands a due attempt to the view model, which queues it in
/// the queue's low lane or defers it; the runs, plan edits and deletions of a plan reach the planner.
/// </summary>
public class MainViewModelRetryTests : IDisposable
{
    private const string Locked = "locked.txt";
    private const string Deleted = "2026_09_28-14_05 Projects";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly MainViewModelFixture _fixture = new();
    private readonly TableStorageFactory _storages = new();
    private readonly HoldStorage _target = new(new InMemoryStorage());
    private readonly string _source;

    public MainViewModelRetryTests()
    {
        _source = Directory.CreateDirectory(Path.Combine(_fixture.Root, "source")).FullName;
        File.WriteAllText(Path.Combine(_source, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(_source, Locked), "locked-content");
        _storages.Storages[("pool", "")] = _target;
        _fixture.Storages = _storages;
        _fixture.AddConnection("pool", "Pool bucket", "bucket-a");
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = StorageLocation.FileSystem(_source),
            Target = new StorageLocation("s3", "", "pool"),
        });
    }

    public void Dispose()
    {
        _target.Gate.TrySetResult();
        _fixture.Queue?.CancelAll();
        _fixture.Queue?.WhenIdleAsync().Wait(Timeout);
        _fixture.Dispose();
    }

    private static PlanEditorViewModel Projects(MainViewModel vm) => vm.Plans.Single(p => p.Id == "p1");

    private IReadOnlyList<RunLogEntry> Log() => new RunLog(_fixture.Paths.LogFileFor("p1")).ReadAll();

    /// <summary>Holds the file open without sharing, as a program that has it open does.</summary>
    private FileStream Lock(string relative) =>
        new(Path.Combine(_source, relative), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    /// <summary>A run of the plan by hand while <see cref="Locked"/> is locked; it ends with that file skipped.</summary>
    private async Task<RunLogEntry> RunWithLockedFileAsync(MainViewModel vm)
    {
        using (Lock(Locked))
        {
            vm.RunPlan("p1").Should().BeTrue();
            await _fixture.Queue.WhenIdleAsync().WaitAsync(Timeout);
        }
        var run = Log().Should().ContainSingle().Subject;
        run.Status.Should().Be(RunStatus.CompletedWithWarnings, "the locked file was skipped");
        return run;
    }

    /// <summary>Moves the planner's clock to just after the plan's next attempt is due; the attempt is queued meanwhile.</summary>
    private void AdvanceToDue()
    {
        var due = _fixture.Retries.StateOf("p1")!.NextDueUtc;
        _fixture.Time.SetUtcNow(new DateTimeOffset(DateTime.SpecifyKind(due, DateTimeKind.Utc)) + TimeSpan.FromSeconds(1));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Not met in time: {because}");
    }

    [Fact]
    public async Task A_due_retry_runs_and_recovers_a_file_that_was_locked()
    {
        var vm = _fixture.Create();
        var editor = Projects(vm);
        var run = await RunWithLockedFileAsync(vm);
        editor.Run.Retry.Should().NotBeNull("the plan's card learns of the pending retry");
        editor.Run.Retry!.RunId.Should().Be(run.RunId);

        AdvanceToDue();
        await _fixture.Queue.WhenIdleAsync().WaitAsync(Timeout);

        var retry = Log().Should().HaveCount(2).And.Subject.Last();
        retry.Trigger.Should().Be(RunTrigger.Retry);
        retry.RetryOf.Should().Be(run.RunId);
        retry.Attempt.Should().Be(1);
        retry.Status.Should().Be(RunStatus.Completed);
        retry.FilesCopied.Should().Be(1);
        _target.Inner.Files.Should().Contain($"{run.Version}/{Locked}");
        Encoding.UTF8.GetString(_target.Inner.ReadAllBytes($"{run.Version}/{Locked}")).Should().Be("locked-content");

        editor.Run.Entries.Should().HaveCount(2, "the history was read again");
        var effective = EffectiveRun.Apply(editor.Run.Entries)[0];
        effective.Status.Should().Be(RunStatus.Completed);
        effective.SkippedCount.Should().Be(0);
        _fixture.Retries.StateOf("p1").Should().BeNull("nothing is left to retry");
        File.Exists(_fixture.Paths.RetryFileFor("p1")).Should().BeFalse();
        editor.Run.Retry.Should().BeNull();
        editor.Run.RetryJobState.Should().BeNull();
        vm.StatusMessage.Should().Be(Loc.F("shell.status.retryFinished", ("plan", "Projects"), ("recovered", 1), ("open", 0)));
    }

    [Fact]
    public async Task A_due_retry_is_deferred_while_versions_are_deleted()
    {
        var vm = _fixture.Create();
        var editor = Projects(vm);
        await RunWithLockedFileAsync(vm);
        // The app was quit while it deleted an older version: the resume finishes that first.
        _target.Inner.AddFile($"{Deleted}/old.txt", Encoding.UTF8.GetBytes("old"));
        _target.Inner.AddFile($"{Deleted}/re-deleting.json", Encoding.UTF8.GetBytes(
            "{\"formatVersion\":1,\"planId\":\"p1\",\"planName\":\"Projects\",\"startedUtc\":\"2026-10-15T08:00:00Z\",\"host\":\"" +
            Environment.MachineName + "\"}"));
        _target.HoldDelete = paths => paths.Any(p => p.StartsWith(Deleted + "/", StringComparison.Ordinal));
        var resuming = vm.ResumeDeletionsAsync();
        await _target.Reached.Task.WaitAsync(Timeout);
        await WaitUntilAsync(() => editor.IsDeletingVersions, "the resume runs");

        AdvanceToDue();

        _fixture.Queue.IsBusy.Should().BeFalse("nothing is queued while versions are deleted");
        Log().Should().ContainSingle("a deferred retry is not logged");
        var state = _fixture.Retries.StateOf("p1")!;
        state.Attempt.Should().Be(0, "waiting costs no attempt");
        state.NextDueUtc.Should().Be(_fixture.Time.GetUtcNow().UtcDateTime + RetryPlanner.RetryDelay);

        _target.Gate.SetResult();
        await resuming.WaitAsync(Timeout);
        AdvanceToDue();
        await _fixture.Queue.WhenIdleAsync().WaitAsync(Timeout);

        var retry = Log().Should().HaveCount(2).And.Subject.Last();
        retry.Trigger.Should().Be(RunTrigger.Retry);
        retry.Attempt.Should().Be(1, "the deferred attempt is still the first");
    }

    [Fact]
    public async Task A_due_retry_is_deferred_while_the_scheduler_is_paused()
    {
        var vm = _fixture.Create();
        await RunWithLockedFileAsync(vm);
        _fixture.Scheduler.IsPaused = true;

        AdvanceToDue();

        _fixture.Queue.IsBusy.Should().BeFalse();
        Log().Should().ContainSingle();
        _fixture.Retries.StateOf("p1")!.NextDueUtc.Should().Be(_fixture.Time.GetUtcNow().UtcDateTime + RetryPlanner.RetryDelay);
    }

    [Fact]
    public async Task A_finished_retry_raises_no_run_notification()
    {
        var vm = _fixture.Create();
        var notified = new List<RunLogEntry>();
        vm.RunFinished += (_, entry) => notified.Add(entry);
        var run = await RunWithLockedFileAsync(vm);
        notified.Should().ContainSingle().Which.RunId.Should().Be(run.RunId);

        using (Lock(Locked))
        {
            AdvanceToDue();
            await _fixture.Queue.WhenIdleAsync().WaitAsync(Timeout);
        }

        var retry = Log().Should().HaveCount(2).And.Subject.Last();
        retry.Status.Should().Be(RunStatus.CompletedWithWarnings, "the file is still locked");
        notified.Should().ContainSingle("a retry attempt raises no notification");
        vm.StatusMessage.Should().Be(Loc.F("shell.status.retryFinished", ("plan", "Projects"), ("recovered", 0), ("open", 1)));
        var state = _fixture.Retries.StateOf("p1")!;
        state.Attempt.Should().Be(1);
        Projects(vm).Run.Retry!.Attempt.Should().Be(1, "the card follows the state");
        Projects(vm).Run.Entries.Should().HaveCount(2, "the history was read again");
    }

    [Fact]
    public async Task A_run_queued_behind_its_running_retry_waits_for_it_and_not_for_itself()
    {
        var vm = _fixture.Create();
        var editor = Projects(vm);
        var run = await RunWithLockedFileAsync(vm);
        _target.HoldCreate = path => path == $"{run.Version}/{Locked}";
        JobState? stateWhenRetryEnded = null;
        var retryEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Queue.Changed += update =>
        {
            if (update is not { IsRetry: true, State: JobState.Finished })
                return;
            stateWhenRetryEnded = editor.Run.State;   // the view model took the update before this handler
            retryEnded.TrySetResult();
        };

        AdvanceToDue();
        await _target.Reached.Task.WaitAsync(Timeout);

        editor.Run.RetryJobState.Should().Be(JobState.Running);
        editor.Run.IsActive.Should().BeFalse("the run button stays about regular runs");
        editor.Run.State.Should().BeNull();
        await editor.Versions.RefreshCommand.ExecuteAsync(null);
        editor.Versions.SetMarkedVersions(editor.Versions.VersionRows);
        await editor.Versions.DeleteVersionsCommand.ExecuteAsync(null);
        _fixture.Dialogs.Errors.Should().ContainSingle().Which.Should().Be(Loc.T("versions.delete.backupRunning"),
            "a retry is writing into a version");

        vm.RunPlan("p1").Should().BeTrue("a regular run may wait behind the plan's retry");
        editor.Run.State.Should().Be(JobState.Queued);
        editor.Run.RingText.Should().Be("#1");
        editor.Run.DetailText.Should().Be(Loc.T("card.startsNext"), "it waits for no other plan");

        _target.Gate.SetResult();
        await retryEnded.Task.WaitAsync(Timeout);
        stateWhenRetryEnded.Should().Be(JobState.Queued, "the retry's end leaves the waiting run alone");
        await WaitUntilAsync(() => editor.Run.State == JobState.Running, "the waiting run starts after the retry");
        // It waits for the next minute (the first run's version has this minute's name, unless the minute just turned):
        // it is canceled.
        vm.CancelRunCommand.Execute(editor);
        await _fixture.Queue.WhenIdleAsync().WaitAsync(Timeout);

        Log().Select(e => e.Trigger).Should().Equal(RunTrigger.Manual, RunTrigger.Retry, RunTrigger.Manual);
        Log()[1].Status.Should().Be(RunStatus.Completed);
        editor.Run.State.Should().BeNull();
        editor.Run.RetryJobState.Should().BeNull();
        _fixture.Retries.StateOf("p1").Should().BeNull("the retry copied the file");
    }

    [Fact]
    public async Task Saving_the_plan_with_retries_off_drops_the_pending_state()
    {
        var vm = _fixture.Create();
        await RunWithLockedFileAsync(vm);
        File.Exists(_fixture.Paths.RetryFileFor("p1")).Should().BeTrue();

        var plan = Projects(vm).SavedPlan();
        plan.RetrySkipped.Enabled = false;
        _fixture.Store.Save(plan);   // saved elsewhere: the app reloads it
        vm.ReloadFromDisk();

        _fixture.Retries.StateOf("p1").Should().BeNull();
        File.Exists(_fixture.Paths.RetryFileFor("p1")).Should().BeFalse();
        Projects(vm).Run.Retry.Should().BeNull();
    }

    [Fact]
    public async Task Saving_the_plan_with_another_target_drops_the_pending_state()
    {
        var vm = _fixture.Create();
        var editor = Projects(vm);
        await RunWithLockedFileAsync(vm);

        editor.TargetIsFolder = true;
        editor.Target = Directory.CreateDirectory(Path.Combine(_fixture.Root, "other")).FullName;
        vm.SaveCommand.Execute(null);

        editor.IsDirty.Should().BeFalse();
        _fixture.Retries.StateOf("p1").Should().BeNull("the files would go into a version of another target");
        editor.Run.Retry.Should().BeNull();
    }

    [Fact]
    public async Task Deleting_the_plan_drops_its_pending_state()
    {
        var vm = _fixture.Create();
        await RunWithLockedFileAsync(vm);

        vm.DeletePlanCommand.Execute(null);

        vm.Plans.Should().BeEmpty();
        _fixture.Retries.StateOf("p1").Should().BeNull();
        File.Exists(_fixture.Paths.RetryFileFor("p1")).Should().BeFalse();
    }

    [Fact]
    public async Task A_saved_retry_is_loaded_again_at_start()
    {
        var vm = _fixture.Create();
        var run = await RunWithLockedFileAsync(vm);

        var restarted = _fixture.Create();
        restarted.StartRetries();

        _fixture.Retries.StateOf("p1")!.RunId.Should().Be(run.RunId);
        Projects(restarted).Run.Retry!.RunId.Should().Be(run.RunId);
    }

    [Fact]
    public void Catch_up_ignores_retry_lines()
    {
        var regularStart = new DateTime(2026, 10, 27, 3, 0, 0, DateTimeKind.Utc);
        var log = new RunLog(_fixture.Paths.LogFileFor("p1"));
        log.Append(new RunLogEntry
        {
            RunId = "r1", Trigger = RunTrigger.Scheduled, Status = RunStatus.CompletedWithWarnings,
            StartUtc = regularStart, EndUtc = regularStart.AddMinutes(1), Version = "2026_10_27-03_00 Projects",
        });
        log.Append(new RunLogEntry
        {
            RunId = "r2", Trigger = RunTrigger.Retry, RetryOf = "r1", Attempt = 1, Attempts = 3, Status = RunStatus.Completed,
            StartUtc = regularStart.AddMinutes(6), EndUtc = regularStart.AddMinutes(7), Version = "2026_10_27-03_00 Projects",
        });
        var vm = _fixture.Create();

        vm.LastRegularRunStartUtc("p1").Should().Be(regularStart, "a retry line would hide a missed scheduled run");
    }

    /// <summary>
    /// Passes every call to an <see cref="InMemoryStorage"/>; creating the files <see cref="HoldCreate"/> picks and deleting
    /// the paths <see cref="HoldDelete"/> picks wait for <see cref="Gate"/> (or their token).
    /// </summary>
    private sealed class HoldStorage(InMemoryStorage inner) : IStorage
    {
        public InMemoryStorage Inner { get; } = inner;

        public Func<string, bool> HoldCreate { get; set; } = _ => false;

        public Func<IReadOnlyList<string>, bool> HoldDelete { get; set; } = _ => false;

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StorageCapabilities Capabilities => Inner.Capabilities;

        private async Task WaitAsync(CancellationToken ct)
        {
            Reached.TrySetResult();
            await Gate.Task.WaitAsync(ct);
        }

        public Task<StorageEntry?> StatAsync(string path, CancellationToken ct) => Inner.StatAsync(path, ct);

        public IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive, CancellationToken ct) =>
            Inner.ListAsync(folder, recursive, ct);

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => Inner.OpenReadAsync(path, ct);

        public async Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct)
        {
            if (HoldCreate(path))
                await WaitAsync(ct);
            return await Inner.CreateAsync(path, options, ct);
        }

        public async Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct)
        {
            if (HoldDelete(paths))
                await WaitAsync(ct);
            await Inner.DeleteAsync(paths, ct);
        }

        public Task EnsureDirectoryAsync(string path, CancellationToken ct) => Inner.EnsureDirectoryAsync(path, ct);

        public Task<long?> GetFreeSpaceAsync(CancellationToken ct) => Inner.GetFreeSpaceAsync(ct);
    }
}
