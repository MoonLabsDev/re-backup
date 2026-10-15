using System.IO;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Shared.Schedule;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests;

/// <summary>
/// The actions on a plan's target (spec 9–11): the lock banner of an incremental plan with "Release lock…", "Verify
/// backup…", and "Copy plan…", which copies the versions to a new target and adds the copy as a disabled plan. While a
/// copy runs, the source plan does not run and its versions are not deleted.
/// </summary>
public class PoolActionsTests : IDisposable
{
    private const string Version = "2026_09_29-14_05 Projects";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly MainViewModelFixture _fixture = new();
    private readonly TableStorageFactory _storages = new();
    private readonly InMemoryStorage _sourceTarget = new();
    private readonly InMemoryStorage _newTarget = new();
    private readonly GatedStorage _gated;

    public PoolActionsTests()
    {
        _gated = new GatedStorage(_sourceTarget);
        _storages.Storages[("src", "")] = _gated;
        _storages.Storages[("src-too", "")] = _gated;   // another connection to the same bucket
        _storages.Storages[("dst", "")] = _newTarget;
        _fixture.Storages = _storages;
        _fixture.AddConnection("src", "Source bucket", "bucket-a");
        _fixture.AddConnection("src-too", "Source bucket again", "bucket-a");
        _fixture.AddConnection("dst", "Copy bucket", "bucket-b");
    }

    public void Dispose()
    {
        _gated.Gate.TrySetResult();
        _fixture.Dispose();
    }

    /// <summary>Saves the plan "Projects" (p1) on the connection "src"; an incremental one gets a pool with one version.</summary>
    private MainViewModel Create(bool incremental = true)
    {
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = StorageLocation.FileSystem(Directory.CreateDirectory(Path.Combine(_fixture.Root, "source")).FullName),
            Target = new StorageLocation("s3", "", "src"),
            Incremental = incremental,
        });
        if (incremental)
        {
            PoolTargets.WriteStore(_sourceTarget, "p1");
            PoolTargets.WriteVersion(_sourceTarget, Version, "p1", "Projects",
                new PoolFile("a.txt", "alpha"), new PoolFile("b.txt", "beta"));
        }
        return _fixture.Create();
    }

    private bool SourceLocked => _sourceTarget.Files.Contains(PoolLayout.LockFile);

    [Fact]
    public async Task Lock_banner_shows_foreign_lock()
    {
        await TargetLock.TryAcquireAsync(_sourceTarget, "OTHER-PC", "backup", TimeProvider.System, CancellationToken.None);
        var editor = Create().Plans.Single();

        await editor.RefreshLockAsync();

        editor.ShowPoolActions.Should().BeTrue();
        editor.HasLock.Should().BeTrue();
        editor.LockBanner.Should().Contain("OTHER-PC").And.Contain("backup");
    }

    [Fact]
    public async Task Full_plan_shows_a_lock_a_copy_left_and_can_release_it()
    {
        // A copy that crashed leaves its "copy" lock on the source plan's target, whatever the plan's mode.
        await TargetLock.TryAcquireAsync(_sourceTarget, "OTHER-PC", "copy", TimeProvider.System, CancellationToken.None);
        var vm = Create(incremental: false);
        var editor = vm.Plans.Single();

        await editor.RefreshLockAsync();

        editor.ShowPoolActions.Should().BeFalse("verify stays for incremental plans");
        editor.HasLock.Should().BeTrue();
        editor.ShowReleaseLock.Should().BeTrue();
        editor.LockBanner.Should().Contain("OTHER-PC").And.Contain("copy");
        _fixture.Dialogs.ConfirmAnswer = true;
        await vm.ReleaseLockCommand.ExecuteAsync(editor);

        SourceLocked.Should().BeFalse();
        editor.HasLock.Should().BeFalse();
        editor.ShowReleaseLock.Should().BeFalse("a full plan without a lock has nothing to release");
    }

    [Fact]
    public async Task Release_is_not_offered_while_the_versions_tab_deletes()
    {
        await TargetLock.TryAcquireAsync(_sourceTarget, Environment.MachineName, "delete", TimeProvider.System, CancellationToken.None);
        var editor = Create().Plans.Single();
        await editor.RefreshLockAsync();
        editor.ReleaseLockCommand.CanExecute(null).Should().BeTrue();

        editor.Versions.IsDeleting = true;   // this app's own delete and garbage collection hold the lock

        editor.ReleaseLockCommand.CanExecute(null).Should().BeFalse();
        editor.Versions.IsDeleting = false;
        editor.ReleaseLockCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Release_requires_confirmation()
    {
        await TargetLock.TryAcquireAsync(_sourceTarget, "OTHER-PC", "backup", TimeProvider.System, CancellationToken.None);
        var vm = Create();
        var editor = vm.Plans.Single();
        await editor.RefreshLockAsync();

        _fixture.Dialogs.ConfirmAnswer = false;
        await vm.ReleaseLockCommand.ExecuteAsync(editor);

        _fixture.Dialogs.Confirms.Should().ContainSingle().Which.Should().Contain("OTHER-PC");
        SourceLocked.Should().BeTrue("the release was not confirmed");
        editor.HasLock.Should().BeTrue();

        _fixture.Dialogs.ConfirmAnswer = true;
        await vm.ReleaseLockCommand.ExecuteAsync(editor);

        SourceLocked.Should().BeFalse();
        editor.HasLock.Should().BeFalse();
        editor.LockBanner.Should().BeNull();
    }

    [Fact]
    public async Task Unreadable_lock_file_is_shown_and_can_be_released()
    {
        _sourceTarget.AddFile(PoolLayout.LockFile, "{ not a lock"u8.ToArray());
        var editor = Create().Plans.Single();

        await editor.RefreshLockAsync();

        editor.HasLock.Should().BeTrue();
        editor.LockBanner.Should().Be(Loc.T("plan.lock.unreadable"));
        await editor.ReleaseLockCommand.ExecuteAsync(null);
        _fixture.Dialogs.Confirms.Should().ContainSingle();
        SourceLocked.Should().BeFalse();
        editor.HasLock.Should().BeFalse();
    }

    [Fact]
    public async Task Verify_lists_problems()
    {
        var vm = Create();
        await _sourceTarget.DeleteAsync([PoolLayout.BlobPath(PoolTargets.HashOf("beta"))], CancellationToken.None);
        vm.VerifyBackupCommand.Execute(vm.Plans.Single());
        var verify = _fixture.Dialogs.VerifyDialogs.Should().ContainSingle().Subject;

        await verify.StartCommand.ExecuteAsync(null);

        var problem = verify.Problems.Should().ContainSingle().Subject;
        (problem.Version, problem.Path, problem.Problem).Should().Be((Version, "b.txt", Loc.T("verify.kind.missing")));
        verify.Summary.Should().Be(Loc.F("verify.problems", ("count", 1)));
        SourceLocked.Should().BeFalse("verify releases its lock");
    }

    [Fact]
    public async Task Verify_thorough_finds_corrupt_blob_and_healthy_pool_says_so()
    {
        var vm = Create();
        vm.VerifyBackupCommand.Execute(vm.Plans.Single());
        var verify = _fixture.Dialogs.VerifyDialogs.Single();

        verify.ChooseModeCommand.Execute("Thorough");
        await verify.StartCommand.ExecuteAsync(null);
        verify.Problems.Should().BeEmpty();
        verify.Summary.Should().Be(Loc.T("verify.healthy"));

        _sourceTarget.AddFile(PoolLayout.BlobPath(PoolTargets.HashOf("alpha")), "tampered"u8.ToArray());
        await verify.StartCommand.ExecuteAsync(null);
        verify.Problems.Should().ContainSingle().Which.Problem.Should().Be(Loc.T("verify.kind.corrupt"));
    }

    [Fact]
    public async Task Verify_refuses_while_target_locked()
    {
        await TargetLock.TryAcquireAsync(_sourceTarget, "OTHER-PC", "backup", TimeProvider.System, CancellationToken.None);
        var vm = Create();
        await _sourceTarget.DeleteAsync([PoolLayout.BlobPath(PoolTargets.HashOf("beta"))], CancellationToken.None);
        vm.VerifyBackupCommand.Execute(vm.Plans.Single());
        var verify = _fixture.Dialogs.VerifyDialogs.Single();

        await verify.StartCommand.ExecuteAsync(null);

        verify.Message.Should().Contain("OTHER-PC").And.Contain("backup");
        verify.Problems.Should().BeEmpty("nothing ran");
        verify.Summary.Should().BeNull();
        SourceLocked.Should().BeTrue("the other holder's lock stays");
    }

    [Fact]
    public void Verify_is_not_offered_for_full_plans()
    {
        var vm = Create(incremental: false);

        vm.VerifyBackupCommand.Execute(vm.Plans.Single());

        _fixture.Dialogs.VerifyDialogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Copy_dialog_refuses_same_target()
    {
        var vm = Create();
        vm.CopyPlanCommand.Execute(vm.Plans.Single());
        var copy = _fixture.Dialogs.CopyDialogs.Should().ContainSingle().Subject;
        copy.ChooseTargetKindCommand.Execute("S3");
        copy.TargetS3.SelectedConnection = copy.TargetS3.Connections.Single(c => c.Id == "src");

        await copy.StartCommand.ExecuteAsync(null);

        copy.Message.Should().Be(Loc.T("copy.refusal.sameOrNested"));
        copy.Result.Should().BeNull("nothing was copied");

        // Another connection to the same bucket is the same place too: the copier gets the app's connection lookups.
        copy.TargetS3.SelectedConnection = copy.TargetS3.Connections.Single(c => c.Id == "src-too");
        await copy.StartCommand.ExecuteAsync(null);

        copy.Message.Should().Be(Loc.T("copy.refusal.sameOrNested"));
        copy.Result.Should().BeNull();
        vm.Plans.Should().ContainSingle();
    }

    [Fact]
    public async Task Copy_dialog_refuses_target_that_is_not_empty()
    {
        var vm = Create();
        _newTarget.AddFile("2026_01_01-00_00 Other/re-manifest.json", "{}"u8.ToArray());
        vm.CopyPlanCommand.Execute(vm.Plans.Single());
        var copy = _fixture.Dialogs.CopyDialogs.Single();
        ChooseNewTarget(copy);

        await copy.StartCommand.ExecuteAsync(null);

        copy.Message.Should().Be(Loc.T("copy.refusal.targetNotEmpty"));
        vm.Plans.Should().ContainSingle();
    }

    [Fact]
    public async Task Copy_creates_disabled_plan_and_runs_copier()
    {
        var vm = Create();
        var source = vm.Plans.Single();
        vm.CopyPlanCommand.Execute(source);
        var copy = _fixture.Dialogs.CopyDialogs.Single();
        copy.Name.Should().Be(Loc.F("copy.defaultName", ("plan", "Projects")));
        copy.Incremental.Should().BeFalse("the copy of an incremental plan defaults to full copies");
        ChooseNewTarget(copy);

        await copy.StartCommand.ExecuteAsync(null);

        copy.Message.Should().BeNull();
        copy.Result!.VersionsCopied.Should().Be(1);
        copy.Result.Canceled.Should().BeFalse();
        copy.Summary.Should().NotBeNullOrEmpty();
        _newTarget.Files.Should().Contain(path => path.EndsWith("/a.txt")).And.Contain(path => path.EndsWith("/b.txt"));

        var added = vm.Plans.Should().HaveCount(2).And.Subject.Single(p => p != source);
        added.Name.Should().Be(copy.Name);
        added.Enabled.Should().BeFalse();
        added.IsNew.Should().BeFalse();
        added.IsDirty.Should().BeFalse();
        var saved = _fixture.Store.TryLoad(added.Id)!;
        saved.Enabled.Should().BeFalse();
        saved.Incremental.Should().BeFalse();
        saved.Target.Should().Be(new StorageLocation("s3", "", "dst"));
        saved.CopyFromPlanId.Should().BeNull("the copy has finished");
        added.IsCopyUnfinished.Should().BeFalse();
        added.IsCopying.Should().BeFalse();
        source.IsCopying.Should().BeFalse();
        SourceLocked.Should().BeFalse();
        copy.StartCommand.CanExecute(null).Should().BeFalse("the copy is done");
    }

    [Fact]
    public async Task Copy_dialog_cancel_stops_copier()
    {
        var vm = Create();
        var source = vm.Plans.Single();
        vm.CopyPlanCommand.Execute(source);
        var copy = _fixture.Dialogs.CopyDialogs.Single();
        ChooseNewTarget(copy);
        _gated.Holds = path => path.StartsWith(PoolLayout.PoolFolder + "/", StringComparison.Ordinal);

        var running = copy.StartCommand.ExecuteAsync(null);
        await _gated.Reached.Task.WaitAsync(Timeout);
        copy.IsRunning.Should().BeTrue();
        source.IsCopying.Should().BeTrue();
        copy.CancelCommand.Execute(null);
        await running.WaitAsync(Timeout);

        copy.IsRunning.Should().BeFalse();
        copy.Result!.Canceled.Should().BeTrue();
        copy.Summary.Should().Be(Loc.T("copy.canceled"));
        source.IsCopying.Should().BeFalse();
        SourceLocked.Should().BeFalse("the copier releases the source's lock");

        // Saved before the first write, marked unfinished: the copy can be continued after the dialog has closed.
        var unfinished = vm.Plans.Should().HaveCount(2).And.Subject.Single(p => p != source);
        unfinished.IsCopyUnfinished.Should().BeTrue();
        unfinished.IsCopying.Should().BeFalse();
        unfinished.Enabled.Should().BeFalse();
        _fixture.Store.TryLoad(unfinished.Id)!.CopyFromPlanId.Should().Be("p1");
    }

    [Fact]
    public async Task Continue_copy_copies_the_rest_and_clears_the_mark()
    {
        var vm = Create();
        var source = vm.Plans.Single();
        vm.CopyPlanCommand.Execute(source);
        var first = _fixture.Dialogs.CopyDialogs.Single();
        ChooseNewTarget(first);
        _gated.Holds = path => path.StartsWith(PoolLayout.PoolFolder + "/", StringComparison.Ordinal);
        var running = first.StartCommand.ExecuteAsync(null);
        await _gated.Reached.Task.WaitAsync(Timeout);
        first.CancelCommand.Execute(null);
        await running.WaitAsync(Timeout);
        _gated.Holds = _ => false;
        var unfinished = vm.Plans.Single(p => p != source);

        // Neither run nor enabled while unfinished.
        vm.RunNowCommand.Execute(unfinished);
        _fixture.Queue.IsBusy.Should().BeFalse();
        vm.StatusMessage.Should().Be(Loc.F("shell.status.copyUnfinished", ("plan", unfinished.Name)));
        vm.RunnablePlans.Single(p => p.Id == unfinished.Id).CanRun.Should().BeFalse();
        unfinished.CanEnable.Should().BeFalse();
        unfinished.Enabled = true;
        unfinished.Errors.Should().Contain(Loc.T("plan.copyUnfinished.enable"));
        unfinished.Revert();
        // Nor renamed: "Continue copy…" finds the copy's versions on the target by its name.
        unfinished.CanEditName.Should().BeFalse();
        unfinished.Name = "Renamed";
        unfinished.Errors.Should().Contain(Loc.T("plan.copyUnfinished.rename"));
        unfinished.Revert();

        vm.ContinueCopyCommand.Execute(unfinished);
        var resume = _fixture.Dialogs.CopyDialogs.Should().HaveCount(2).And.Subject.Last();
        resume.IsResume.Should().BeTrue();
        resume.CanEditSettings.Should().BeFalse();
        resume.Name.Should().Be(unfinished.Name);
        resume.TargetIsS3.Should().BeTrue();
        await resume.StartCommand.ExecuteAsync(null);

        resume.Message.Should().BeNull("a target holding the copy's own versions is no refusal here");
        resume.Result!.Canceled.Should().BeFalse();
        (resume.Result.VersionsCopied + resume.Result.VersionsSkipped).Should().Be(1);
        resume.Result.Failures.Should().BeEmpty();
        _newTarget.Files.Should().Contain(path => path.EndsWith("/a.txt")).And.Contain(path => path.EndsWith("/b.txt"));
        vm.Plans.Should().HaveCount(2, "the copy's plan was added once");
        unfinished.IsCopyUnfinished.Should().BeFalse();
        unfinished.CanEnable.Should().BeTrue();
        unfinished.CanEditName.Should().BeTrue();
        _fixture.Store.TryLoad(unfinished.Id)!.CopyFromPlanId.Should().BeNull();
        vm.RunnablePlans.Single(p => p.Id == unfinished.Id).CanRun.Should().BeTrue();
    }

    [Fact]
    public async Task Copy_that_stops_on_a_full_target_keeps_the_unfinished_mark_until_continued()
    {
        var vm = Create();
        var source = vm.Plans.Single();
        var full = new GatedStorage(_newTarget) { FullOnCreate = path => path.EndsWith("/a.txt", StringComparison.Ordinal) };
        _storages.Storages[("dst", "")] = full;
        vm.CopyPlanCommand.Execute(source);
        var first = _fixture.Dialogs.CopyDialogs.Single();
        ChooseNewTarget(first);

        await first.StartCommand.ExecuteAsync(null);

        first.Result!.Stopped.Should().BeTrue();
        var unfinished = vm.Plans.Single(p => p != source);
        first.Summary.Should().Be(Loc.F("copy.notFinished", ("plan", unfinished.Name)));
        unfinished.IsCopyUnfinished.Should().BeTrue("the version is still pending on the new target");
        _fixture.Store.TryLoad(unfinished.Id)!.CopyFromPlanId.Should().Be("p1");

        full.FullOnCreate = _ => false;   // space freed
        vm.ContinueCopyCommand.Execute(unfinished);
        var resume = _fixture.Dialogs.CopyDialogs.Last();
        await resume.StartCommand.ExecuteAsync(null);

        resume.Result!.Finished.Should().BeTrue();
        resume.Result.VersionsCopied.Should().Be(1);
        unfinished.IsCopyUnfinished.Should().BeFalse();
        _fixture.Store.TryLoad(unfinished.Id)!.CopyFromPlanId.Should().BeNull();
    }

    [Fact]
    public void Continue_copy_of_a_plan_whose_source_is_gone_says_so()
    {
        var vm = Create();
        _fixture.Store.Save(new BackupPlan
        {
            Id = "orphan",
            Name = "Orphan",
            Source = vm.Plans.Single().SavedPlan().Source,
            Target = new StorageLocation("s3", "", "dst"),
            Enabled = false,
            CopyFromPlanId = "gone",
        });
        vm.ReloadFromDisk();
        var orphan = vm.Plans.Single(p => p.Id == "orphan");
        orphan.IsCopyUnfinished.Should().BeTrue();

        vm.ContinueCopyCommand.Execute(orphan);

        _fixture.Dialogs.CopyDialogs.Should().BeEmpty();
        _fixture.Dialogs.Errors.Should().ContainSingle().Which.Should().Be(Loc.F("copy.sourceGone", ("plan", "Orphan")));
        vm.SelectedPlan = orphan;
        vm.DeletePlanCommand.Execute(null);
        vm.Plans.Should().NotContain(orphan, "the unfinished copy can be deleted");
    }

    [Fact]
    public async Task Copy_refuses_to_start_when_a_run_was_queued_while_the_dialog_was_open()
    {
        var vm = Create();
        var source = vm.Plans.Single();
        vm.CopyPlanCommand.Execute(source);
        var copy = _fixture.Dialogs.CopyDialogs.Single();
        ChooseNewTarget(copy);
        _gated.Holds = path => path == PoolLayout.StoreFile;   // the run waits once it has taken the lock

        vm.RunNowCommand.Execute(source);
        source.Run.IsActive.Should().BeTrue();
        await copy.StartCommand.ExecuteAsync(null);

        copy.Message.Should().Be(Loc.F("copy.backupRunning", ("plan", "Projects")));
        copy.Result.Should().BeNull();
        source.IsCopying.Should().BeFalse();
        vm.Plans.Should().ContainSingle();

        _fixture.Queue.Cancel("p1");
        _gated.Gate.SetResult();
        await WaitUntilAsync(() => !_fixture.Queue.IsBusy);
    }

    [Fact]
    public async Task Exit_stops_running_copies_and_verifies_and_releases_their_locks()
    {
        var vm = Create();
        var source = vm.Plans.Single();
        vm.CopyPlanCommand.Execute(source);
        var copy = _fixture.Dialogs.CopyDialogs.Single();
        ChooseNewTarget(copy);
        copy.ChooseStorageFormatCommand.Execute("Incremental");   // the new target is locked as well
        _gated.Holds = path => path.StartsWith(PoolLayout.PoolFolder + "/", StringComparison.Ordinal);
        var copying = copy.StartCommand.ExecuteAsync(null);
        await _gated.Reached.Task.WaitAsync(Timeout);
        vm.IsAnyPoolJobRunning.Should().BeTrue();
        _newTarget.Files.Should().Contain(PoolLayout.LockFile);

        vm.StopPoolJobs(Timeout).Should().BeTrue();

        SourceLocked.Should().BeFalse();
        _newTarget.Files.Should().NotContain(PoolLayout.LockFile);
        await copying.WaitAsync(Timeout);
        vm.IsAnyPoolJobRunning.Should().BeFalse();

        // A thorough verify that is reading the pool.
        var verifyGate = new GatedStorage(_sourceTarget)
        {
            Holds = path => path.StartsWith(PoolLayout.PoolFolder + "/", StringComparison.Ordinal),
        };
        _storages.Storages[("src", "")] = verifyGate;
        vm.VerifyBackupCommand.Execute(source);
        var verify = _fixture.Dialogs.VerifyDialogs.Single();
        verify.ChooseModeCommand.Execute("Thorough");
        var verifying = verify.StartCommand.ExecuteAsync(null);
        await verifyGate.Reached.Task.WaitAsync(Timeout);
        vm.IsAnyPoolJobRunning.Should().BeTrue();
        source.IsVerifying.Should().BeTrue();
        SourceLocked.Should().BeTrue();

        vm.StopPoolJobs(Timeout).Should().BeTrue();

        SourceLocked.Should().BeFalse("the verify releases its lock before the app exits");
        await verifying.WaitAsync(Timeout);
        vm.IsAnyPoolJobRunning.Should().BeFalse();
        source.IsVerifying.Should().BeFalse();
    }

    [Fact]
    public async Task Release_lock_is_not_offered_while_this_app_holds_the_lock()
    {
        var vm = Create();
        var source = vm.Plans.Single();
        vm.VerifyBackupCommand.Execute(source);
        var verify = _fixture.Dialogs.VerifyDialogs.Single();
        verify.ChooseModeCommand.Execute("Thorough");
        _gated.Holds = path => path.StartsWith(PoolLayout.PoolFolder + "/", StringComparison.Ordinal);
        var verifying = verify.StartCommand.ExecuteAsync(null);
        await _gated.Reached.Task.WaitAsync(Timeout);
        await source.RefreshLockAsync();

        source.HasLock.Should().BeTrue();
        source.ReleaseLockCommand.CanExecute(null).Should().BeFalse("this app's own verify holds it");
        await source.ReleaseLockCommand.ExecuteAsync(null);
        _fixture.Dialogs.Confirms.Should().BeEmpty();
        SourceLocked.Should().BeTrue();

        _gated.Gate.SetResult();
        await verifying.WaitAsync(Timeout);
        SourceLocked.Should().BeFalse();
    }

    [Fact]
    public async Task Copy_blocks_runs_and_version_deletes_of_the_source()
    {
        var vm = Create();
        var source = vm.Plans.Single();
        vm.CopyPlanCommand.Execute(source);
        var copy = _fixture.Dialogs.CopyDialogs.Single();
        ChooseNewTarget(copy);
        _gated.Holds = path => path.StartsWith(PoolLayout.PoolFolder + "/", StringComparison.Ordinal);
        var running = copy.StartCommand.ExecuteAsync(null);
        await _gated.Reached.Task.WaitAsync(Timeout);

        vm.RunScheduled("p1", RunTrigger.Scheduled);
        _fixture.Queue.IsBusy.Should().BeFalse("a scheduled run of the source is skipped");
        new RunLog(_fixture.Paths.LogFileFor("p1")).ReadAll().Should().ContainSingle()
            .Which.Reason.Should().Be(CoreTexts.English("core.run.copyRunning"));
        vm.StatusMessage.Should().Contain("Projects");

        vm.RunNowCommand.Execute(source);
        _fixture.Queue.IsBusy.Should().BeFalse("a manual run of the source is refused");
        vm.StatusMessage.Should().Be(Loc.F("shell.status.copyRunning", ("plan", "Projects")));
        vm.RunnablePlans.Should().HaveCount(2).And.OnlyContain(p => !p.CanRun, "neither the source nor its copy runs");

        await source.Versions.RefreshCommand.ExecuteAsync(null);
        source.Versions.SetMarkedVersions(source.Versions.VersionRows);
        await source.Versions.DeleteVersionsCommand.ExecuteAsync(null);
        _fixture.Dialogs.Errors.Should().Contain(Loc.T("versions.delete.copyRunning"));
        _sourceTarget.Files.Should().Contain($"{Version}/re-manifest.json");

        vm.SelectedPlan = source;
        vm.DeletePlanCommand.Execute(null);
        vm.Plans.Should().Contain(source, "a plan is not deleted while it is copied");

        _gated.Gate.SetResult();
        await running.WaitAsync(Timeout);
        source.IsCopying.Should().BeFalse();
        vm.RunnablePlans.Single(p => p.Id == "p1").CanRun.Should().BeTrue("the source runs again once the copy has ended");
    }

    [Fact]
    public void Copy_is_offered_for_full_plans()
    {
        var vm = Create(incremental: false);

        vm.CopyPlanCommand.Execute(vm.Plans.Single());

        var copy = _fixture.Dialogs.CopyDialogs.Should().ContainSingle().Subject;
        copy.Incremental.Should().BeTrue("the copy of a full plan defaults to incremental");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(20);
        }
    }

    private static void ChooseNewTarget(CopyPlanViewModel copy)
    {
        copy.ChooseTargetKindCommand.Execute("S3");
        copy.TargetS3.SelectedConnection = copy.TargetS3.Connections.Single(c => c.Id == "dst");
    }
}
