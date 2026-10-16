using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Core.Versions;
using ReBackup.Shared.Schedule;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests;

/// <summary>
/// Deleting versions by hand, as the Versions tab and the plan card show it: the phases of the deletion, of the pool's
/// cleanup and of the indexing after it; the confirmation first; and the deletions the app resumes at its next start.
/// </summary>
public class VersionsDeleteProgressTests : IDisposable
{
    private const string First = "2026_09_28-14_05 Projects";
    private const string Second = "2026_09_29-14_05 Projects";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly MainViewModelFixture _fixture = new();
    private readonly TableStorageFactory _storages = new();
    private readonly InMemoryStorage _target = new();
    private readonly StepStorage _step;

    public VersionsDeleteProgressTests()
    {
        _step = new StepStorage(_target);
        _storages.Storages[("pool", "")] = _step;
        _fixture.Storages = _storages;
        _fixture.AddConnection("pool", "Pool bucket", "bucket-a");
    }

    public void Dispose()
    {
        _step.Gate.TrySetResult();
        _fixture.Queue?.CancelAll();
        _fixture.Dispose();
    }

    /// <summary>The plan "Projects" (p1) with two versions in a pool, and a second plan "Other" that is never touched.</summary>
    private MainViewModel Create(bool incremental = true)
    {
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = StorageLocation.FileSystem(Directory.CreateDirectory(Path.Combine(_fixture.Root, "source")).FullName),
            Target = new StorageLocation("s3", "", "pool"),
            Incremental = incremental,
        });
        _fixture.AddPlan("p2", "Other");
        PoolTargets.WriteStore(_target, "p1");
        PoolTargets.WriteVersion(_target, First, "p1", "Projects", new PoolFile("a.txt", "only in the first"));
        PoolTargets.WriteVersion(_target, Second, "p1", "Projects", new PoolFile("b.txt", "in the second"));
        return _fixture.Create();
    }

    private static PlanEditorViewModel Projects(MainViewModel vm) => vm.Plans.Single(p => p.Id == "p1");

    private static PlanEditorViewModel Other(MainViewModel vm) => vm.Plans.Single(p => p.Id == "p2");

    private static string Blob(string content) => PoolLayout.BlobPath(PoolTargets.HashOf(content));

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

    private static async Task MarkAsync(VersionsViewModel versions, params string[] names)
    {
        await versions.RefreshCommand.ExecuteAsync(null);
        versions.SetMarkedVersions(versions.VersionRows.Where(r => names.Contains(r.Name)));
    }

    [Fact]
    public async Task Pool_delete_shows_each_phase_in_the_tab_and_on_the_plan_card_and_resets_afterwards()
    {
        // As on the UI thread: the progress reports are applied one after the other, in order, and the test reads the view
        // models between them (without a context, Progress<T> runs them on the thread pool, concurrently and out of order).
        using var ui = new SerialContext();
        await ui.RunAsync(async () =>
        {
            var vm = Create();
            var editor = Projects(vm);
            var versions = editor.Versions;
            await MarkAsync(versions, First);
            var otherState = Other(vm).Run.CardState;
            _step.HoldList = path => path == PoolLayout.PoolFolder;

            var deleting = versions.DeleteVersionsCommand.ExecuteAsync(null);
            await _step.Reached.Task.WaitAsync(Timeout);

            // Listing the pool: no total yet.
            var listing = Loc.F("versions.delete.cleanup.listing", ("files", Formats.Files(0)));
            await WaitUntilAsync(() => versions.DeleteText == listing, "the tab shows the listing");
            versions.DeleteIndeterminate.Should().BeTrue();
            vm.StatusMessage.Should().Be(listing);
            await WaitUntilAsync(() => editor.Run.CardState == PlanCardState.Deleting && editor.Run.DetailText == listing,
                "the card shows the listing");
            editor.Run.PhaseText.Should().Be(Loc.T("card.delete.cleaning"));
            editor.Run.RingIndeterminate.Should().BeTrue();
            Other(vm).Run.CardState.Should().Be(otherState, "only the plan whose versions are deleted shows it");
            editor.IsDeletingVersions.Should().BeTrue();

            // The indexing after the deletion waits on the remaining version's manifest.
            _step.Reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = _step.Gate;
            _step.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _step.HoldList = _ => false;
            _step.HoldRead = path => path == $"{Second}/re-manifest.json";
            gate.SetResult();
            await deleting.WaitAsync(Timeout);
            await _step.Reached.Task.WaitAsync(Timeout);

            await WaitUntilAsync(() => editor.Run.PhaseText == Loc.T("card.indexing"), "the card shows the indexing");
            editor.Run.CardState.Should().Be(PlanCardState.Deleting);
            versions.IsSyncing.Should().BeTrue();
            versions.SyncText.Should().NotBeEmpty("the tab shows the sync's own progress");

            _step.Gate.SetResult();
            await WaitUntilAsync(() => versions.DeletionStatus is null && !versions.IsSyncing, "the indexing ends");
            await WaitUntilAsync(() => editor.Run.CardState == otherState, "the card returns to its normal state");
            versions.VersionRows.Select(r => r.Name).Should().Equal(Second);
            _target.Files.Should().NotContain([Blob("only in the first"), OwnCleanup, PoolLayout.LockFile]);
            _target.Files.Should().Contain(Blob("in the second"));
            editor.IsDeletingVersions.Should().BeFalse();
        }).WaitAsync(Timeout * 4);
    }

    [Fact]
    public async Task Pool_delete_shows_the_manifests_and_the_deletions_of_the_cleanup()
    {
        var vm = Create();
        var versions = Projects(vm).Versions;
        await MarkAsync(versions, First);
        var texts = new List<string>();
        versions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VersionsViewModel.DeleteText))
                lock (texts)
                    texts.Add(versions.DeleteText);
        };

        // As on the UI thread: the reports arrive one after the other, in order.
        using var ui = new SerialContext();
        await ui.RunAsync(() => versions.DeleteVersionsCommand.ExecuteAsync(null)).WaitAsync(Timeout);

        await WaitUntilAsync(() => versions.DeletionStatus is null && !versions.IsSyncing, "the indexing ends");
        var manifests = Loc.F("versions.delete.cleanup.manifests", ("done", 0), ("total", 1));
        var unreferenced = Loc.F("versions.delete.cleanup.deleting", ("done", 1), ("total", 1));
        var files = Loc.F("versions.delete.progress", ("done", 1), ("count", 1), ("current", 1), ("versions", 1));
        lock (texts)
        {
            texts.Should().Contain(manifests);
            texts.Should().Contain(unreferenced);
            texts.Should().Contain(files, "the version's folder holds one file, its manifest");
        }
    }

    [Fact]
    public async Task Declined_confirmation_deletes_nothing()
    {
        var vm = Create();
        var versions = Projects(vm).Versions;
        await MarkAsync(versions, First);
        var before = _target.Files.ToList();
        _fixture.Dialogs.ConfirmAnswer = false;

        await versions.DeleteVersionsCommand.ExecuteAsync(null);

        var question = _fixture.Dialogs.Confirms.Should().ContainSingle().Subject;
        question.Should().Contain(First).And.Contain(Loc.T("versions.delete.poolCleanupNote"));
        _target.Files.Should().BeEquivalentTo(before);
        versions.IsDeleting.Should().BeFalse();
        versions.DeletionStatus.Should().BeNull();
    }

    [Fact]
    public async Task Confirmation_names_a_few_versions_and_sums_up_the_rest()
    {
        var vm = Create();
        for (var day = 1; day <= 7; day++)
            PoolTargets.WriteVersion(_target, $"2026_09_{day:00}-14_05 Projects", "p1", "Projects", new PoolFile("a.txt", "x"));
        var versions = Projects(vm).Versions;
        await versions.RefreshCommand.ExecuteAsync(null);
        versions.SetMarkedVersions(versions.VersionRows);
        versions.VersionRows.Should().HaveCount(9).And.OnlyContain(r => r.IsManaged);
        var before = _target.Files.ToList();
        _fixture.Dialogs.ConfirmAnswer = false;

        await versions.DeleteVersionsCommand.ExecuteAsync(null);

        var question = _fixture.Dialogs.Confirms.Should().ContainSingle().Subject;
        question.Should().Contain(Loc.F("versions.delete.andMore", ("count", 4)));
        versions.VersionRows.Take(5).Should().OnlyContain(r => question.Contains(r.Name));
        versions.VersionRows.Skip(5).Should().OnlyContain(r => !question.Contains(r.Name));
        _target.Files.Should().BeEquivalentTo(before);
    }

    [Fact]
    public void A_run_shows_the_cleanup_of_the_pool_on_the_card()
    {
        var run = new PlanRunViewModel(_storages);
        BackupJobUpdate Running(CollectProgress cleanup) => new("p1", "Projects", JobState.Running,
            new BackupProgress(BackupPhase.CollectingGarbage, (int)cleanup.Done, (int)cleanup.Total, 0, 0, "", cleanup), null);

        run.Apply(Running(new CollectProgress(CollectPhase.ListingPool, 84_120, 0)));

        run.PhaseText.Should().Be(Loc.T("card.removingLeftovers"));
        run.DetailText.Should().Be(Loc.F("versions.delete.cleanup.listing", ("files", Formats.Files(84_120))));
        run.RingIndeterminate.Should().BeTrue();

        run.Apply(Running(new CollectProgress(CollectPhase.DeletingUnreferenced, 1_204, 3_880)));

        run.DetailText.Should().Be(Loc.F("versions.delete.cleanup.deleting", ("done", 1_204), ("total", 3_880)));
        run.RingIndeterminate.Should().BeFalse();
        run.RingText.Should().Be("31%");
        run.CardAction.Should().Be(PlanCardAction.Cancel);
    }

    // ---- Resuming at the next start ----

    private static byte[] DeletingMarker(string host) => Encoding.UTF8.GetBytes(
        "{\"formatVersion\":1,\"planId\":\"p1\",\"planName\":\"Projects\",\"startedUtc\":\"2026-10-15T08:00:00Z\",\"host\":\"" + host + "\"}");

    private static readonly string OwnCleanup = CleanupMarker.FileNameFor(Environment.MachineName);

    private static byte[] CleanupFile(string host) =>
        Encoding.UTF8.GetBytes("{\"host\":\"" + host + "\",\"startedUtc\":\"2026-10-15T08:00:00Z\"}");

    [Fact]
    public async Task Unfinished_delete_is_resumed_at_start_while_runs_of_the_plan_wait()
    {
        var vm = Create();
        // The app was quit after the first version was marked and its manifest deleted.
        _target.AddFile($"{First}/re-deleting.json", DeletingMarker(Environment.MachineName));
        await _target.DeleteAsync([$"{First}/re-manifest.json"], CancellationToken.None);
        _target.AddFile(CleanupMarker.FileNameFor(Environment.MachineName), CleanupFile(Environment.MachineName));
        var editor = Projects(vm);
        _step.HoldList = path => path == PoolLayout.PoolFolder;

        var resuming = vm.ResumeDeletionsAsync();
        await _step.Reached.Task.WaitAsync(Timeout);

        await WaitUntilAsync(() => editor.Run.CardState == PlanCardState.Deleting, "the card shows the resumed deletion");
        editor.Run.CardAction.Should().Be(PlanCardAction.None, "the card offers no run while versions are deleted");
        editor.IsDeletingVersions.Should().BeTrue();
        vm.RunnablePlans.Single(p => p.Id == "p1").CanRun.Should().BeFalse();
        vm.IsAnyPoolJobRunning.Should().BeTrue("the app waits for it when it exits");
        vm.RunNowCommand.Execute(editor);
        _fixture.Queue.IsBusy.Should().BeFalse("a manual run waits for the deletion");
        vm.StatusMessage.Should().Be(Loc.F("shell.status.deleteRunning", ("plan", "Projects")));
        vm.SelectedPlan = editor;
        vm.DeletePlanCommand.Execute(null);
        vm.Plans.Should().Contain(editor, "a plan is not deleted while its versions are");
        vm.StatusMessage.Should().Be(Loc.F("shell.status.deleteWaitDelete", ("plan", "Projects")));

        _step.Gate.SetResult();
        await resuming.WaitAsync(Timeout);

        _target.Files.Should().NotContain(f => f.StartsWith(First + "/", StringComparison.Ordinal));
        _target.Files.Should().NotContain([Blob("only in the first"), OwnCleanup, PoolLayout.LockFile]);
        _target.Files.Should().Contain(Blob("in the second"));
        editor.IsDeletingVersions.Should().BeFalse();
        vm.IsAnyPoolJobRunning.Should().BeFalse();
        await WaitUntilAsync(() => editor.Run.CardState != PlanCardState.Deleting, "the card returns to its normal state");
        vm.StatusMessage.Should().Be(Loc.T("versions.delete.resumed"));
        vm.RunnablePlans.Single(p => p.Id == "p1").CanRun.Should().BeTrue();
    }

    [Fact]
    public async Task Scheduled_run_during_a_resume_is_deferred_and_starts_afterwards()
    {
        var vm = Create();
        _target.AddFile(OwnCleanup, CleanupFile(Environment.MachineName));
        _step.HoldList = path => path == PoolLayout.PoolFolder;
        var resuming = vm.ResumeDeletionsAsync();
        await _step.Reached.Task.WaitAsync(Timeout);
        await WaitUntilAsync(() => Projects(vm).IsDeletingVersions, "the resume runs");

        vm.RunScheduled("p1", RunTrigger.Scheduled);

        _fixture.Queue.IsBusy.Should().BeFalse("the run waits for the deletion");
        new RunLog(_fixture.Paths.LogFileFor("p1")).ReadAll().Should().BeEmpty("a deferred run is no error");
        vm.StatusMessage.Should().Be(Loc.F("shell.status.runDeferred", ("plan", "Projects")));

        _step.HoldList = _ => false;
        _step.Gate.SetResult();
        await resuming.WaitAsync(Timeout);
        await WaitUntilAsync(() => new RunLog(_fixture.Paths.LogFileFor("p1")).ReadAll().Count == 1, "the deferred run ran");
        new RunLog(_fixture.Paths.LogFileFor("p1")).ReadAll().Single().Trigger.Should().Be(RunTrigger.Scheduled);
    }

    [Fact]
    public async Task Looking_for_unfinished_deletions_does_not_hold_up_runs_and_stops_on_exit()
    {
        var vm = Create();
        _step.HoldList = path => path.Length == 0;   // the target's root, as the resume looks at it

        var resuming = vm.ResumeDeletionsAsync();
        await _step.Reached.Task.WaitAsync(Timeout);

        Projects(vm).IsDeletingVersions.Should().BeFalse("nothing was found to resume yet");
        vm.RunnablePlans.Single(p => p.Id == "p1").CanRun.Should().BeTrue();
        vm.StopPoolJobs(Timeout).Should().BeTrue();
        await resuming.WaitAsync(Timeout);
        Projects(vm).IsDeletingVersions.Should().BeFalse();
    }

    [Fact]
    public async Task No_resume_while_the_plan_runs()
    {
        _target.AddFile(OwnCleanup, CleanupFile(Environment.MachineName));
        PoolTargets.WriteStore(_target, "p1");
        PoolTargets.WriteVersion(_target, First, "p1", "Projects", new PoolFile("a.txt", "only in the first"));
        AddOrphan();
        var plan = new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = StorageLocation.FileSystem(_fixture.Root),
            Target = new StorageLocation("s3", "", "pool"),
            Incremental = true,
        };
        var worker = new VersionIndexWorker(new VersionIndexSet(Path.Combine(_fixture.Root, "index")));
        var context = new VersionsContext(worker, _fixture.Dialogs, _ => { }, _storages);
        var versions = new VersionsViewModel(() => plan, () => true, new FakeFolderOpener(), context);
        var before = _target.Files.ToList();

        await versions.ResumeDeletionAsync().WaitAsync(Timeout);

        _target.Files.Should().BeEquivalentTo(before, "a queued or running backup goes first");
        versions.IsResuming.Should().BeFalse();
    }

    private void AddOrphan() => _target.AddFile(Blob("orphan"), Encoding.UTF8.GetBytes("orphan"));

    [Fact]
    public async Task Markers_of_another_computer_are_not_resumed()
    {
        var vm = Create();
        _target.AddFile($"{First}/re-deleting.json", DeletingMarker("OTHER-PC"));
        _target.AddFile(CleanupMarker.FileNameFor("OTHER-PC"), CleanupFile("OTHER-PC"));
        var before = _target.Files.ToList();

        await vm.ResumeDeletionsAsync().WaitAsync(Timeout);

        _target.Files.Should().BeEquivalentTo(before);
        Projects(vm).Versions.IsDeleting.Should().BeFalse();
    }

    [Fact]
    public async Task Exit_stops_a_resumed_cleanup_releases_the_lock_and_keeps_the_mark_for_the_next_start()
    {
        var vm = Create();
        _target.AddFile(CleanupMarker.FileNameFor(Environment.MachineName), CleanupFile(Environment.MachineName));
        _step.HoldList = path => path == PoolLayout.PoolFolder;

        var resuming = vm.ResumeDeletionsAsync();
        await _step.Reached.Task.WaitAsync(Timeout);
        await WaitUntilAsync(() => vm.IsAnyPoolJobRunning, "the cleanup is a job the app waits for");

        vm.StopPoolJobs(Timeout).Should().BeTrue();
        await resuming.WaitAsync(Timeout);

        _target.Files.Should().Contain(OwnCleanup).And.NotContain(PoolLayout.LockFile);
        Projects(vm).IsDeletingVersions.Should().BeFalse();
    }

    /// <summary>A synchronization context that runs what is posted to it one after the other on one thread, like a dispatcher.</summary>
    private sealed class SerialContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public SerialContext()
        {
            new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                    callback(state);
            }) { IsBackground = true }.Start();
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!_queue.IsAddingCompleted)
                _queue.TryAdd((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        /// <summary>Starts <paramref name="work"/> on the context's thread; completes when it has.</summary>
        public Task RunAsync(Func<Task> work)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try
                {
                    await work();
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            }, null);
            return done.Task;
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    /// <summary>
    /// Passes every call to an <see cref="InMemoryStorage"/>; listings of the folders <see cref="HoldList"/> picks and reads
    /// of the files <see cref="HoldRead"/> picks wait for <see cref="Gate"/> (or their token).
    /// </summary>
    private sealed class StepStorage(InMemoryStorage inner) : IStorage
    {
        public Func<string, bool> HoldList { get; set; } = _ => false;

        public Func<string, bool> HoldRead { get; set; } = _ => false;

        public TaskCompletionSource Gate { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Reached { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StorageCapabilities Capabilities => inner.Capabilities;

        private async Task WaitAsync(CancellationToken ct)
        {
            var gate = Gate;
            Reached.TrySetResult();
            await gate.Task.WaitAsync(ct);
        }

        public Task<StorageEntry?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);

        public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive, [EnumeratorCancellation] CancellationToken ct)
        {
            if (HoldList(folder))
                await WaitAsync(ct);
            await foreach (var entry in inner.ListAsync(folder, recursive, ct))
                yield return entry;
        }

        public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
        {
            if (HoldRead(path))
                await WaitAsync(ct);
            return await inner.OpenReadAsync(path, ct);
        }

        public Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct) =>
            inner.CreateAsync(path, options, ct);

        public Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct) => inner.DeleteAsync(paths, ct);

        public Task EnsureDirectoryAsync(string path, CancellationToken ct) => inner.EnsureDirectoryAsync(path, ct);

        public Task<long?> GetFreeSpaceAsync(CancellationToken ct) => inner.GetFreeSpaceAsync(ct);
    }
}
