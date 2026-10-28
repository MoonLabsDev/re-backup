using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Backup.Retry;
using ReBackup.Shared.Schedule;

namespace ReBackup.Core.Tests.Backup.Retry;

public class EffectiveRunTests
{
    private static RunLogEntry Original(int skipped, int retryable, string id = "run-1") => new()
    {
        RunId = id,
        Trigger = RunTrigger.Scheduled,
        Status = RunStatus.CompletedWithWarnings,
        SkippedCount = skipped,
        RetryableCount = retryable,
        Skipped =
        [
            .. Enumerable.Range(0, Math.Min(skipped - retryable, 3)).Select(i => new SkippedEntry($"gone{i}.txt", "gone", "core.file.noLongerExists")),
            .. Enumerable.Range(0, Math.Min(retryable, 3)).Select(i => new SkippedEntry($"locked{i}.txt", "locked", "core.file.locked")),
        ],
    };

    private static RunLogEntry Retry(RunStatus status, int open, string id = "retry-1") => new()
    {
        RunId = id,
        Trigger = RunTrigger.Retry,
        RetryOf = "run-1",
        Status = status,
        SkippedCount = open,
        Skipped = [.. Enumerable.Range(0, Math.Min(open, 3)).Select(i => new SkippedEntry($"open{i}.txt", "locked", "core.file.locked"))],
    };

    private static RunLogEntry Merged(params RunLogEntry[] log) => EffectiveRun.Apply(log)[0];

    [Fact]
    public void A_run_whose_files_were_all_recovered_is_completed_with_nothing_skipped()
    {
        var merged = Merged(Original(73, 73), Retry(RunStatus.Completed, 0));

        merged.Status.Should().Be(RunStatus.Completed);
        merged.SkippedCount.Should().Be(0);
        merged.Skipped.Should().BeEmpty();
        merged.RetryRecovered.Should().Be(73);
        merged.RetryAttemptsMade.Should().Be(1);
    }

    [Fact]
    public void Partly_recovered_keeps_the_warning_with_the_open_count()
    {
        var merged = Merged(Original(73, 73), Retry(RunStatus.CompletedWithWarnings, 5));

        merged.Status.Should().Be(RunStatus.CompletedWithWarnings);
        merged.SkippedCount.Should().Be(5);
        merged.Skipped.Select(e => e.Path).Should().Equal("open0.txt", "open1.txt", "open2.txt");
        merged.RetryRecovered.Should().Be(68);
    }

    [Fact]
    public void Skips_that_are_never_retried_stay_counted()
    {
        var merged = Merged(Original(73, 70), Retry(RunStatus.Completed, 0));

        merged.SkippedCount.Should().Be(3);
        merged.Status.Should().Be(RunStatus.CompletedWithWarnings);
        merged.Skipped.Select(e => e.Path).Should().Equal("gone0.txt", "gone1.txt", "gone2.txt");
        merged.RetryRecovered.Should().Be(70);
    }

    [Fact]
    public void Foreign_markers_keep_the_warning()
    {
        var orig = Original(73, 73);
        orig.MetForeignMarkers = true;

        var merged = Merged(orig, Retry(RunStatus.Completed, 0));

        merged.SkippedCount.Should().Be(0);
        merged.Status.Should().Be(RunStatus.CompletedWithWarnings);
        merged.MetForeignMarkers.Should().BeTrue();
    }

    [Fact]
    public void Retention_warnings_stay_on_a_recovered_run()
    {
        var orig = Original(73, 73);
        orig.Warnings = ["x"];

        var merged = Merged(orig, Retry(RunStatus.Completed, 0));

        merged.Status.Should().Be(RunStatus.Completed);
        merged.Warnings.Should().Equal("x");
    }

    [Fact]
    public void A_failed_retry_does_not_change_the_count()
    {
        var merged = Merged(Original(73, 73),
            Retry(RunStatus.CompletedWithWarnings, 12, "retry-1"),
            Retry(RunStatus.Error, 0, "retry-2"));

        merged.SkippedCount.Should().Be(12);
        merged.Status.Should().Be(RunStatus.CompletedWithWarnings);
        merged.RetryAttemptsMade.Should().Be(2);
    }

    [Fact]
    public void The_latest_retry_wins()
    {
        var merged = Merged(Original(73, 73),
            Retry(RunStatus.CompletedWithWarnings, 61, "retry-1"),
            Retry(RunStatus.CompletedWithWarnings, 12, "retry-2"));

        merged.SkippedCount.Should().Be(12);
        merged.RetryRecovered.Should().Be(61);
        merged.RetryAttemptsMade.Should().Be(2);
    }

    [Fact]
    public void A_canceled_retry_is_not_an_attempt()
    {
        var merged = Merged(Original(73, 73),
            Retry(RunStatus.CompletedWithWarnings, 12, "retry-1"),
            Retry(RunStatus.Canceled, 0, "retry-2"));

        merged.SkippedCount.Should().Be(12);
        merged.RetryAttemptsMade.Should().Be(1);
    }

    [Fact]
    public void A_run_with_only_a_canceled_retry_is_returned_as_is()
    {
        var orig = Original(73, 73);

        var result = EffectiveRun.Apply([orig, Retry(RunStatus.Canceled, 0)]);

        result[0].Should().BeSameAs(orig);
    }

    [Fact]
    public void More_than_a_thousand_recovered_counts_beyond_the_cap()
    {
        var merged = Merged(Original(1500, 1500), Retry(RunStatus.Completed, 0));

        merged.RetryRecovered.Should().Be(1500);
        merged.SkippedCount.Should().Be(0);
        merged.Status.Should().Be(RunStatus.Completed);
    }

    [Fact]
    public void The_merged_skipped_list_is_capped()
    {
        var orig = Original(1500, 1000);
        orig.Skipped = [.. Enumerable.Range(0, 500).Select(i => new SkippedEntry($"gone{i}", "gone", "core.file.noLongerExists"))];
        var retry = Retry(RunStatus.CompletedWithWarnings, 1000);
        retry.Skipped = [.. Enumerable.Range(0, 1000).Select(i => new SkippedEntry($"open{i}", "locked", "core.file.locked"))];

        var merged = Merged(orig, retry);

        merged.SkippedCount.Should().Be(1500);
        merged.Skipped.Should().HaveCount(RunLogEntry.MaxSkippedEntries);
        merged.Skipped[0].Path.Should().Be("gone0");
    }

    [Fact]
    public void A_run_without_retries_is_returned_as_is()
    {
        var orig = Original(3, 3);
        var other = Original(2, 2, "run-2");

        var result = EffectiveRun.Apply([orig, other]);

        result[0].Should().BeSameAs(orig);
        result[1].Should().BeSameAs(other);
    }

    [Fact]
    public void Order_and_retry_lines_are_kept_and_the_input_is_not_modified()
    {
        var orig = Original(73, 73);
        var retry = Retry(RunStatus.Completed, 0);
        var later = Original(1, 1, "run-2");

        var result = EffectiveRun.Apply([orig, retry, later]);

        result.Should().HaveCount(3);
        result[0].RunId.Should().Be("run-1");
        result[0].Should().NotBeSameAs(orig);
        orig.SkippedCount.Should().Be(73);
        result[1].Should().BeSameAs(retry);
        result[2].Should().BeSameAs(later);
    }
}
