using System.IO;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.App.Tests.ViewModels;

/// <summary>The "Retry skipped files later" switch and its two numbers in the plan editor.</summary>
public class PlanEditorRetryTests : IDisposable
{
    private readonly MainViewModelFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private PlanEditorViewModel Editor(RetrySkippedSettings? retry = null)
    {
        var plan = new BackupPlan
        {
            Id = "p1",
            Name = "Docs",
            Source = StorageLocation.FileSystem(Directory.CreateDirectory(Path.Combine(_fixture.Root, "src")).FullName),
            Target = StorageLocation.FileSystem(Path.Combine(_fixture.Root, "dst")),
        };
        if (retry is not null)
            plan.RetrySkipped = retry;
        _fixture.Store.Save(plan);
        return _fixture.Create().Plans.Single();
    }

    [Fact]
    public void Retry_settings_round_trip_through_the_editor()
    {
        var editor = Editor(new RetrySkippedSettings { Enabled = false, Attempts = 7, IntervalMinutes = 45 });

        editor.RetryEnabled.Should().BeFalse();
        editor.RetryAttempts.Should().Be(7);
        editor.RetryIntervalMinutes.Should().Be(45);
        editor.RetryAttemptsText.Should().Be("7");
        editor.RetryIntervalMinutesText.Should().Be("45");
        editor.ToPlan().RetrySkipped.Should().BeEquivalentTo(new RetrySkippedSettings { Enabled = false, Attempts = 7, IntervalMinutes = 45 });
        editor.IsDirty.Should().BeFalse();

        editor.RetryEnabled = true;
        editor.IsDirty.Should().BeTrue();
        editor.ToPlan().RetrySkipped.Enabled.Should().BeTrue();
    }

    [Fact]
    public void A_plan_without_the_setting_shows_the_defaults()
    {
        var editor = Editor();

        editor.RetryEnabled.Should().BeTrue();
        editor.RetryAttemptsText.Should().Be("3");
        editor.RetryIntervalMinutesText.Should().Be("5");
    }

    [Fact]
    public void Typed_retry_numbers_reach_the_plan_and_mark_it_edited()
    {
        var editor = Editor();

        editor.RetryAttemptsText = "10";
        editor.IsDirty.Should().BeTrue();
        editor.RetryIntervalMinutesText = " 30 ";

        editor.RetryAttempts.Should().Be(10);
        editor.RetryIntervalMinutes.Should().Be(30);
        editor.ToPlan().RetrySkipped.Should().BeEquivalentTo(new RetrySkippedSettings { Enabled = true, Attempts = 10, IntervalMinutes = 30 });
        editor.Errors.Should().BeEmpty();
    }

    [Fact]
    public void A_retry_number_out_of_range_or_not_a_number_is_an_error()
    {
        var editor = Editor();

        editor.RetryAttemptsText = "21";
        editor.Errors.Should().Equal("Retry attempts must be between 1 and 20.");

        editor.RetryAttemptsText = "3";
        editor.RetryIntervalMinutesText = "five";
        editor.Errors.Should().Equal("The wait between retries must be between 1 and 720 minutes.");

        editor.RetryIntervalMinutesText = "";
        editor.Errors.Should().Equal("The wait between retries must be between 1 and 720 minutes.");
    }

    [Fact]
    public void The_last_run_card_shows_the_run_as_its_retries_left_it()
    {
        var log = new RunLog(_fixture.Paths.LogFileFor("p1"));
        log.Append(new RunLogEntry
        {
            RunId = "r1",
            Trigger = RunTrigger.Manual,
            Status = RunStatus.CompletedWithWarnings,
            Version = "2026_10_20-03_00 Docs",
            SkippedCount = 2,
            RetryableCount = 2,
            Skipped = [new SkippedEntry("a.txt", "locked", "core.file.locked"), new SkippedEntry("b.txt", "locked", "core.file.locked")],
        });
        log.Append(new RunLogEntry
        {
            RunId = "r2",
            Trigger = RunTrigger.Retry,
            RetryOf = "r1",
            Attempt = 1,
            Attempts = 3,
            Status = RunStatus.Completed,
            Version = "2026_10_20-03_00 Docs",
            FilesCopied = 2,
        });

        var run = Editor().Run;

        run.LastRun!.Entry.RunId.Should().Be("r1");
        run.LastRun.StatusText.Should().Be("Completed");
        run.LastRun.RetryHintText.Should().Be("2 files recovered by retry");
        run.History.Select(row => row.StatusText).Should().Equal("Retry 1/3 · 2 recovered · 0 open", "Completed");
    }

    [Fact]
    public void Setting_a_retry_number_shows_it_in_its_box()
    {
        var editor = Editor();

        editor.RetryAttempts = 4;
        editor.RetryIntervalMinutes = 60;

        editor.RetryAttemptsText.Should().Be("4");
        editor.RetryIntervalMinutesText.Should().Be("60");
        editor.IsDirty.Should().BeTrue();
    }
}
