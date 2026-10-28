using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Backup.Retry;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.App.Tests.ViewModels;

/// <summary>
/// How a run whose skipped files are retried looks: the Last run card and the history row show the effective result
/// with a hint line, a retry line shows what it recovered, and the plan card names the pending retry.
/// </summary>
public class RunHistoryRowRetryTests : IDisposable
{
    private const string Version = "2026_10_20-03_00 Projects";
    private readonly MainViewModelFixture _fixture = new();   // the English labels

    public void Dispose() => _fixture.Dispose();

    private static List<SkippedEntry> Locked(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new SkippedEntry($"f{i}.txt", "locked", "core.file.locked"))];

    /// <summary>A regular run that left <paramref name="skipped"/> locked files behind.</summary>
    private static RunLogEntry Original(int skipped) => new()
    {
        RunId = "r1",
        Trigger = RunTrigger.Scheduled,
        Status = RunStatus.CompletedWithWarnings,
        Version = Version,
        FilesCopied = 500,
        SkippedCount = skipped,
        RetryableCount = skipped,
        Skipped = Locked(skipped),
    };

    /// <summary>Attempt <paramref name="attempt"/> of 3 for run r1: <paramref name="recovered"/> copied, <paramref name="open"/> still open.</summary>
    private static RunLogEntry RetryLine(int attempt, int recovered, int open, RunStatus? status = null) => new()
    {
        RunId = "retry" + attempt,
        Trigger = RunTrigger.Retry,
        RetryOf = "r1",
        Attempt = attempt,
        Attempts = 3,
        Status = status ?? (open == 0 ? RunStatus.Completed : RunStatus.CompletedWithWarnings),
        Version = Version,
        FilesCopied = recovered,
        SkippedCount = open,
        Skipped = Locked(open),
    };

    private static RetryState Pending(int attempt, int attempts, DateTime dueLocal, int files) => new()
    {
        PlanId = "p1",
        RunId = "r1",
        Version = Version,
        Attempt = attempt,
        Attempts = attempts,
        NextDueUtc = dueLocal.ToUniversalTime(),
        Files = Locked(files),
    };

    private static DateTime Local(int hour, int minute) => new(2026, 10, 20, hour, minute, 0, DateTimeKind.Local);

    private static PlanRunViewModel RunWith(params RunLogEntry[] log)
    {
        var run = new PlanRunViewModel(new StorageFactory(), new FakeTimeProvider(new DateTimeOffset(Local(3, 10))));
        run.LoadHistory(EffectiveRun.Apply(log));
        return run;
    }

    [Fact]
    public void A_recovered_run_shows_completed_with_zero_skipped_and_a_hint()
    {
        var row = new RunHistoryRow(EffectiveRun.Apply([Original(73), RetryLine(1, 73, 0)])[0]);

        row.StatusText.Should().Be("Completed");
        row.Outcome.Should().Be(RunOutcome.Ok);
        row.SkippedText.Should().Be("0");
        row.RetryHintText.Should().Be("73 files recovered by retry");
    }

    [Fact]
    public void One_recovered_file_is_one_file()
    {
        new RunHistoryRow(EffectiveRun.Apply([Original(1), RetryLine(1, 1, 0)])[0]).RetryHintText
            .Should().Be("1 file recovered by retry");
    }

    [Fact]
    public void A_pending_retry_shows_when_it_runs_next()
    {
        var run = RunWith(Original(12));

        run.SetRetry(Pending(1, 3, Local(4, 30), 12));

        run.LastRun!.RetryHintText.Should().Be("Retry 2/3 at 04:30 · 12 open");
    }

    [Fact]
    public void A_pending_retry_of_another_run_is_not_shown()
    {
        var run = RunWith(Original(12));
        var other = Pending(1, 3, Local(4, 30), 12);
        other.RunId = "older";

        run.SetRetry(other);

        run.LastRun!.RetryHintText.Should().BeNull();
    }

    [Fact]
    public void Exhausted_retries_show_what_is_still_skipped()
    {
        var row = new RunHistoryRow(EffectiveRun.Apply(
            [Original(5), RetryLine(1, 0, 5), RetryLine(2, 0, 5), RetryLine(3, 0, 5)])[0]);

        row.StatusText.Should().Be("Completed with warnings");
        row.RetryHintText.Should().Be("5 still skipped after 3 retries");
    }

    [Fact]
    public void One_attempt_is_one_retry()
    {
        new RunHistoryRow(EffectiveRun.Apply([Original(5), RetryLine(1, 0, 5)])[0]).RetryHintText
            .Should().Be("5 still skipped after 1 retry");
    }

    /// <summary>A run that skipped <paramref name="locked"/> locked files and <paramref name="gone"/> files never retried.</summary>
    private static RunLogEntry OriginalWithGone(int locked, int gone)
    {
        var entry = new RunLogEntry
        {
            RunId = "r1",
            Trigger = RunTrigger.Scheduled,
            Status = RunStatus.CompletedWithWarnings,
            Version = Version,
        };
        foreach (var file in Locked(locked))
            entry.AddSkipped(file);
        for (var i = 0; i < gone; i++)
            entry.AddSkipped(new SkippedEntry($"gone{i}.txt", "gone", "core.file.noLongerExists"));
        return entry;
    }

    [Fact]
    public void Recovered_retryable_files_show_the_recovered_line_beside_skips_never_retried()
    {
        var row = new RunHistoryRow(EffectiveRun.Apply([OriginalWithGone(locked: 3, gone: 2), RetryLine(1, 3, 0)])[0]);

        row.StatusText.Should().Be("Completed with warnings", "the files that are gone stay skipped");
        row.SkippedText.Should().Be("2");
        row.RetryHintText.Should().Be("3 files recovered by retry");
    }

    [Fact]
    public void Exhausted_retries_count_only_the_retryable_files_still_open()
    {
        var row = new RunHistoryRow(EffectiveRun.Apply(
            [OriginalWithGone(locked: 3, gone: 2), RetryLine(1, 2, 1), RetryLine(2, 0, 1), RetryLine(3, 0, 1)])[0]);

        row.SkippedText.Should().Be("3");
        row.RetryHintText.Should().Be("1 still skipped after 3 retries");
    }

    [Fact]
    public void A_run_without_retries_has_no_hint()
    {
        new RunHistoryRow(Original(5)).RetryHintText.Should().BeNull();
        new RunHistoryRow(new RunLogEntry { RunId = "r2", Status = RunStatus.Completed }).RetryHintText.Should().BeNull();
    }

    [Fact]
    public void A_retry_line_in_the_history_shows_what_it_recovered()
    {
        var row = new RunHistoryRow(RetryLine(1, 61, 12));

        row.TriggerText.Should().Be("Retry");
        row.StatusText.Should().Be("Retry 1/3 · 61 recovered · 12 open");
        row.RetryHintText.Should().BeNull();
    }

    [Fact]
    public void A_failed_retry_line_shows_its_failure()
    {
        new RunHistoryRow(RetryLine(2, 0, 12, RunStatus.Error)).StatusText.Should().Be("Aborted: error");
    }

    [Fact]
    public void The_last_run_card_never_shows_a_retry_line()
    {
        var run = RunWith(Original(12), RetryLine(1, 0, 12));

        run.History.Should().HaveCount(2);
        run.History[0].Entry.Trigger.Should().Be(RunTrigger.Retry, "the history shows the retry line, newest first");
        run.LastRun!.Entry.Trigger.Should().NotBe(RunTrigger.Retry);
        run.LastRun.Entry.RunId.Should().Be("r1");
    }

    [Fact]
    public void A_running_retry_does_not_block_the_run_button()
    {
        var run = RunWith(Original(12));
        run.SetRetry(Pending(1, 3, Local(4, 30), 12));

        run.Apply(new BackupJobUpdate("p1", "Projects", JobState.Running, null, null, IsRetry: true));

        run.IsActive.Should().BeFalse();
        run.IsRetryActive.Should().BeTrue();
        run.CardAction.Should().Be(PlanCardAction.Run);
        run.DetailText.Should().Be("Retry running");
    }

    [Fact]
    public void The_plan_card_names_the_pending_retry()
    {
        var run = RunWith(Original(12));

        run.SetRetry(Pending(1, 3, Local(4, 30), 12));
        run.DetailText.Should().Be("Retry at 04:30");

        run.Apply(new BackupJobUpdate("p1", "Projects", JobState.Queued, null, null, IsRetry: true));
        run.DetailText.Should().Be("Retry waiting");

        run.Apply(new BackupJobUpdate("p1", "Projects", JobState.Finished, null, null, IsRetry: true));
        run.SetRetry(null);
        run.DetailText.Should().NotStartWith("Retry");
    }
}
