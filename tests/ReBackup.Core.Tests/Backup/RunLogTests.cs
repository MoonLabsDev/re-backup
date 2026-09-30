using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;

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
}
