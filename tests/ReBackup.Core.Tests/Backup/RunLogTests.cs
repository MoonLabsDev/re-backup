using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Schedule;

namespace ReBackup.Core.Tests.Backup;

public class RunLogTests : IDisposable
{
    private readonly TempDir _tmp = new();

    [Fact]
    public void Warnings_round_trip_and_are_empty_for_older_lines()
    {
        var log = new RunLog(_tmp.PathOf("warnings.jsonl"));
        File.WriteAllText(log.LogFile, "{\"runId\":\"old\",\"status\":\"Completed\"}\n");
        var entry = new RunLogEntry { RunId = "new", Status = RunStatus.Completed };
        entry.Warnings.Add("Retention could not delete \"x\": in use");
        log.Append(entry);

        var entries = log.ReadAll();

        entries[0].Warnings.Should().BeEmpty();
        entries[1].Warnings.Should().Equal("Retention could not delete \"x\": in use");
    }

    public void Dispose() => _tmp.Dispose();

    private static RunLogEntry Entry(string id, RunStatus status) => new()
    {
        RunId = id,
        Trigger = RunTrigger.Manual,
        StartUtc = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
        EndUtc = new DateTime(2026, 9, 30, 12, 1, 21, DateTimeKind.Utc),
        DurationMs = 81_000,
        Status = status,
        Version = "2026_09_30-14_00 Projects",
        FilesCopied = 3,
        BytesCopied = 4096,
    };

    [Fact]
    public void Retry_candidates_are_not_written_to_the_log()
    {
        var log = new RunLog(_tmp.PathOf("candidates.jsonl"));
        var entry = Entry("a", RunStatus.CompletedWithWarnings);
        entry.AddSkipped(new SkippedEntry("x.txt", "locked by another program", "core.file.locked"));
        entry.RetryOf = "first";
        entry.Attempt = 2;
        entry.Attempts = 3;
        entry.MetForeignMarkers = true;

        log.Append(entry);
        var read = log.ReadAll().Single();

        read.RetryCandidates.Should().BeEmpty();
        read.RetryableCount.Should().Be(1);
        read.Skipped.Should().Equal(new SkippedEntry("x.txt", "locked by another program", "core.file.locked"));
        read.RetryOf.Should().Be("first");
        read.Attempt.Should().Be(2);
        read.Attempts.Should().Be(3);
        read.MetForeignMarkers.Should().BeTrue();
    }

    [Fact]
    public void The_last_regular_entry_skips_retry_lines()
    {
        var log = new RunLog(_tmp.PathOf("regular.jsonl"));
        log.Append(Entry("manual", RunStatus.Completed));
        var retry = Entry("r1", RunStatus.Completed);
        retry.Trigger = RunTrigger.Retry;
        log.Append(retry);
        retry = Entry("r2", RunStatus.Completed);
        retry.Trigger = RunTrigger.Retry;
        log.Append(retry);

        log.ReadLastRegular()!.RunId.Should().Be("manual");
        log.ReadLast()!.RunId.Should().Be("r2");
    }

    [Fact]
    public void The_last_regular_entry_is_null_without_one()
    {
        new RunLog(_tmp.PathOf("missing.jsonl")).ReadLastRegular().Should().BeNull();
        var log = new RunLog(_tmp.PathOf("only-retries.jsonl"));
        var retry = Entry("r1", RunStatus.Completed);
        retry.Trigger = RunTrigger.Retry;
        log.Append(retry);

        log.ReadLastRegular().Should().BeNull();
    }

    [Fact]
    public void The_last_regular_entry_is_found_behind_a_tail_full_of_retry_lines()
    {
        var log = new RunLog(_tmp.PathOf("big-retries.jsonl"));
        log.Append(Entry("manual", RunStatus.Completed));
        for (var i = 0; i < 400; i++)
        {
            var retry = Entry("r" + i, RunStatus.Completed);
            retry.Trigger = RunTrigger.Retry;
            retry.Warnings.Add(new string('w', 300));
            log.Append(retry);
        }

        new FileInfo(log.LogFile).Length.Should().BeGreaterThan(64 * 1024);
        log.ReadLastRegular()!.RunId.Should().Be("manual");
    }

    [Fact]
    public void A_log_line_written_before_retries_existed_still_reads()
    {
        var log = new RunLog(_tmp.PathOf("old.jsonl"));
        File.WriteAllText(log.LogFile,
            "{\"runId\":\"old\",\"trigger\":\"Manual\",\"status\":\"CompletedWithWarnings\",\"skippedCount\":1," +
            "\"skipped\":[{\"path\":\"a.txt\",\"reason\":\"locked by another program\"}]}\n");

        var entry = log.ReadAll().Single();

        entry.RetryOf.Should().BeNull();
        entry.Attempt.Should().BeNull();
        entry.Attempts.Should().BeNull();
        entry.RetryableCount.Should().Be(0);
        entry.MetForeignMarkers.Should().BeFalse();
        entry.Skipped.Should().ContainSingle().Which.Key.Should().BeNull();
        entry.RetryCandidates.Should().BeEmpty();
    }

    [Fact]
    public void Missing_file_reads_as_empty()
    {
        new RunLog(_tmp.PathOf(@"logs\p1.jsonl")).ReadAll().Should().BeEmpty();
    }

    [Fact]
    public void Append_creates_the_folder_and_ReadAll_returns_entries_oldest_first()
    {
        var log = new RunLog(_tmp.PathOf(@"logs\p1.jsonl"));
        var first = Entry("a", RunStatus.Completed);
        var second = Entry("b", RunStatus.Full);
        second.Reason = "disk full";
        second.AddSkipped(new SkippedEntry("x/y.txt", "locked by another program"));
        second.RetentionDeleted.Add("2026_09_01-02_00 Projects");

        log.Append(first);
        log.Append(second);

        log.ReadAll().Should().BeEquivalentTo([first, second], o => o.WithStrictOrdering());
    }

    [Fact]
    public void Each_entry_is_one_compact_line_with_enums_as_strings()
    {
        var log = new RunLog(_tmp.PathOf("p1.jsonl"));

        log.Append(Entry("a", RunStatus.CompletedWithWarnings));
        log.Append(Entry("b", RunStatus.Canceled));

        var lines = File.ReadAllLines(log.LogFile);
        lines.Should().HaveCount(2);
        lines[0].Should().StartWith("{\"runId\":\"a\"").And.Contain("\"status\":\"CompletedWithWarnings\"")
            .And.Contain("\"trigger\":\"Manual\"").And.Contain("\"durationMs\":81000");
    }

    [Fact]
    public void Unreadable_lines_are_skipped()
    {
        var log = new RunLog(_tmp.PathOf("p1.jsonl"));
        log.Append(Entry("a", RunStatus.Completed));
        File.AppendAllText(log.LogFile, "{ not json\n\nnull\n");
        log.Append(Entry("b", RunStatus.Error));

        log.ReadAll().Select(e => e.RunId).Should().Equal("a", "b");
    }

    [Fact]
    public void Append_succeeds_while_another_handle_reads_the_file()
    {
        var log = new RunLog(_tmp.PathOf("p1.jsonl"));
        log.Append(Entry("a", RunStatus.Completed));

        using (new FileStream(log.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            log.Append(Entry("b", RunStatus.Error));

        log.ReadAll().Select(e => e.RunId).Should().Equal("a", "b");
    }

    [Fact]
    public void ReadAll_succeeds_while_another_handle_appends_to_the_file()
    {
        var log = new RunLog(_tmp.PathOf("p1.jsonl"));
        log.Append(Entry("a", RunStatus.Completed));

        using (new FileStream(log.LogFile, FileMode.Append, FileAccess.Write, FileShare.Read))
            log.ReadAll().Select(e => e.RunId).Should().Equal("a");
    }

    [Fact]
    public void A_torn_last_line_does_not_glue_to_the_next_entry()
    {
        var log = new RunLog(_tmp.PathOf("p1.jsonl"));
        File.WriteAllText(log.LogFile, "{\"runId\":\"x\"");

        log.Append(Entry("b", RunStatus.Completed));

        log.ReadAll().Select(e => e.RunId).Should().Equal("b");
    }

    [Fact]
    public void AddSkipped_counts_everything_but_stores_only_the_first_thousand()
    {
        var entry = Entry("a", RunStatus.CompletedWithWarnings);

        for (var i = 0; i < RunLogEntry.MaxSkippedEntries + 5; i++)
            entry.AddSkipped(new SkippedEntry($"f{i}", "locked"));

        entry.SkippedCount.Should().Be(1005);
        entry.Skipped.Should().HaveCount(1000);
        entry.Skipped[^1].Path.Should().Be("f999");
    }

    [Fact]
    public void ReadLast_of_a_missing_or_empty_log_is_null()
    {
        new RunLog(_tmp.PathOf("missing.jsonl")).ReadLast().Should().BeNull();
        File.WriteAllText(_tmp.PathOf("empty.jsonl"), "");
        new RunLog(_tmp.PathOf("empty.jsonl")).ReadLast().Should().BeNull();
    }

    [Fact]
    public void ReadLast_returns_the_newest_entry_and_skips_a_damaged_last_line()
    {
        var log = new RunLog(_tmp.PathOf("p1.jsonl"));
        log.Append(Entry("a", RunStatus.Completed));
        log.Append(Entry("b", RunStatus.Error));

        log.ReadLast()!.RunId.Should().Be("b");

        File.AppendAllText(log.LogFile, "{ not json\n");
        log.ReadLast()!.RunId.Should().Be("b");
    }

    [Fact]
    public void ReadLast_reads_the_end_of_a_large_log()
    {
        var log = new RunLog(_tmp.PathOf("big.jsonl"));
        for (var i = 0; i < 2000; i++)
            log.Append(Entry(i.ToString(System.Globalization.CultureInfo.InvariantCulture), RunStatus.Completed));
        new FileInfo(log.LogFile).Length.Should().BeGreaterThan(64 * 1024);

        log.ReadLast()!.RunId.Should().Be("1999");
    }

    [Fact]
    public void ReadLast_finds_an_entry_longer_than_the_part_it_reads_first()
    {
        var log = new RunLog(_tmp.PathOf("long.jsonl"));
        for (var i = 0; i < 400; i++)
            log.Append(Entry("small" + i, RunStatus.Completed));
        var big = Entry("big", RunStatus.CompletedWithWarnings);
        for (var i = 0; i < 1000; i++)
            big.AddSkipped(new SkippedEntry($"some/rather/long/path/to/a/file/number/{i}.txt", "locked by another program"));
        log.Append(big);

        log.ReadLast()!.RunId.Should().Be("big");
    }
}
