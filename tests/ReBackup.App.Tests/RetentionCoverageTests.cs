using System.IO;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Backup.Retry;
using ReBackup.Core.Plans;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.App.Tests;

/// <summary>
/// The coverage timeline in the Retention and Versions tabs: lanes from the owned versions, the rules and the run log;
/// clicking a slot selects its version.
/// </summary>
public class RetentionCoverageTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly MainViewModelFixture _fixture = new();
    private readonly string _target;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Now);

    public RetentionCoverageTests()
    {
        _target = Path.Combine(_fixture.Root, "target");
    }

    public void Dispose() => _fixture.Dispose();

    private DateTime DaysAgo(int days) => _today.AddDays(-days).ToDateTime(new TimeOnly(2, 0));

    private static VersionInfo Owned(DateTime time, long? bytes = 1000) =>
        new(VersionName.Format(time, "Projects"), VersionName.Format(time, "Projects"), time, VersionOwnership.Owned, 1, bytes);

    private static RunLogEntry Run(string version, RunStatus status, int skipped = 0) =>
        new() { Version = version, Status = status, SkippedCount = skipped };

    private static RetentionRule Daily(int keep) => new() { Period = RetentionPeriod.Daily, Keep = keep };

    // ------------------------------------------------------------------ builder

    [Fact]
    public void The_builder_marks_versions_whose_run_ended_with_warnings()
    {
        var older = Owned(DaysAgo(2));
        var newer = Owned(DaysAgo(1));
        var log = new[] { Run(older.Name, RunStatus.CompletedWithWarnings, skipped: 3), Run(newer.Name, RunStatus.Completed) };

        var (lanes, labels) = CoverageBuilder.Build([older, newer], [Daily(3)], log, _today);

        var slots = lanes.Should().ContainSingle().Subject.Slots;
        slots.Select(s => s.State).Should().Equal(SlotState.PresentWithWarnings, SlotState.Present, SlotState.Pending);
        slots[0].Version!.Id.Should().Be(older.Name);
        slots[0].Version!.Detail.Should().Be("3");
        labels.Should().Equal("Daily · 3");
    }

    /// <summary>A run that skipped <paramref name="skipped"/> locked files, and retry lines for it.</summary>
    private static RunLogEntry SkippingRun(string version, int skipped) => new()
    {
        RunId = "r1",
        Version = version,
        Status = RunStatus.CompletedWithWarnings,
        SkippedCount = skipped,
        RetryableCount = skipped,
        Skipped = [.. Enumerable.Range(0, skipped).Select(i => new SkippedEntry($"f{i}", "locked", "core.file.locked"))],
    };

    private static RunLogEntry RetryLine(string version, int attempt, RunStatus status, int open) => new()
    {
        RunId = "retry" + attempt,
        Trigger = RunTrigger.Retry,
        RetryOf = "r1",
        Attempt = attempt,
        Attempts = 3,
        Version = version,
        Status = status,
        SkippedCount = open,
    };

    [Fact]
    public void A_recovered_version_is_ok_in_the_coverage()
    {
        var version = Owned(DaysAgo(1));
        var log = EffectiveRun.Apply([SkippingRun(version.Name, 3), RetryLine(version.Name, 1, RunStatus.Completed, 0)]);

        var (lanes, _) = CoverageBuilder.Build([version], [Daily(2)], log, _today);

        lanes[0].Slots[0].Version!.Quality.Should().Be(CoverageQuality.Ok);
    }

    [Fact]
    public void A_version_whose_retries_failed_keeps_its_warnings_in_the_coverage()
    {
        var version = Owned(DaysAgo(1));
        var log = EffectiveRun.Apply([SkippingRun(version.Name, 3), RetryLine(version.Name, 1, RunStatus.Error, 3)]);

        var (lanes, _) = CoverageBuilder.Build([version], [Daily(2)], log, _today);

        lanes[0].Slots[0].Version!.Quality.Should().Be(CoverageQuality.Warning, "the retry line is no run of its own");
        lanes[0].Slots[0].Version!.Detail.Should().Be("3");
    }

    [Fact]
    public void A_version_the_log_does_not_know_is_ok()
    {
        var version = Owned(DaysAgo(1));

        var (lanes, _) = CoverageBuilder.Build([version], [Daily(2)], [Run("other", RunStatus.CompletedWithWarnings, 5)], _today);

        lanes[0].Slots[0].State.Should().Be(SlotState.Present);
        lanes[0].Slots[0].Version!.Quality.Should().Be(CoverageQuality.Ok);
    }

    [Fact]
    public void Versions_the_plan_does_not_own_are_left_out()
    {
        var owned = Owned(DaysAgo(3));
        var foreign = owned with { Name = "x", Path = "x", LocalTime = DaysAgo(1), Ownership = VersionOwnership.Foreign };

        var (lanes, _) = CoverageBuilder.Build([owned, foreign], [Daily(4)], [], _today);

        lanes[0].Slots.Select(s => s.State).Should()
            .Equal(SlotState.Present, SlotState.Missing, SlotState.Missing, SlotState.Pending);
        lanes[0].Slots.Should().NotContain(s => s.Version != null && s.Version.Id == "x");
    }

    [Fact]
    public void Lane_labels_use_the_rule_texts()
    {
        var weekly = new RetentionRule { Period = RetentionPeriod.Weekly, Anchor = "Sunday", Keep = 4 };

        var (_, labels) = CoverageBuilder.Build([], [Daily(7), weekly], [], _today);

        labels.Should().Equal("Daily · 7", "Weekly(Sun) · 4");
    }

    [Fact]
    public void Slot_texts_name_the_period_and_what_is_there()
    {
        var version = new CoverageVersion("v", new DateTime(2026, 10, 12, 2, 0, 0), CoverageQuality.Warning, true, "3");
        var day = new DateOnly(2026, 10, 12);

        CoverageBuilder.SlotText(new CoverageSlot(day, day.AddDays(1), SlotState.PresentWithWarnings, version), _ => 2048)
            .Should().Be("2026-10-12\n2026-10-12 02:00 · 2.0 KB\nWith warnings: 3 skipped");
        CoverageBuilder.SlotText(new CoverageSlot(day, day.AddDays(7), SlotState.Missing, null), _ => null)
            .Should().Be("2026-10-12 – 2026-10-18\nMissing");
        CoverageBuilder.SlotText(new CoverageSlot(day, day.AddDays(1), SlotState.BeforeFirst, null), _ => null)
            .Should().EndWith("\nBefore the first backup");
        CoverageBuilder.SlotText(new CoverageSlot(day, day.AddDays(1), SlotState.Pending, null), _ => null)
            .Should().EndWith("\nNot yet run");
        CoverageBuilder.SlotText(new CoverageSlot(day, day.AddDays(1), SlotState.Present, version with { Quality = CoverageQuality.Ok, Detail = null }), _ => null)
            .Should().Be("2026-10-12\n2026-10-12 02:00");
    }

    // ------------------------------------------------------------------ Retention tab

    /// <summary>Saves a plan "Projects" on the target with <paramref name="rules"/> and owned versions at the given times.</summary>
    private async Task<BackupPlan> PlanWithVersionsAsync(IReadOnlyList<RetentionRule> rules, params DateTime[] times)
    {
        var source = Path.Combine(_fixture.Root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "alpha");
        var plan = new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = StorageLocation.FileSystem(source),
            Target = StorageLocation.FileSystem(_target),
        };
        var run = await new BackupRunner(new StorageFactory()).RunAsync(new BackupRequest(plan, [], RunTrigger.Manual));
        run.Status.Should().Be(RunStatus.Completed, run.Reason);
        var written = Path.Combine(_target, run.Version!);
        foreach (var time in times)
            CopyFolder(written, Path.Combine(_target, VersionName.Format(time, plan.Name)));
        Directory.Delete(written, recursive: true);
        plan.Retention = rules.ToList();
        _fixture.Store.Save(plan);
        return plan;
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var folder in Directory.GetDirectories(from))
            CopyFolder(folder, Path.Combine(to, Path.GetFileName(folder)));
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

    [Fact]
    public async Task Retention_lanes_follow_the_edited_rules_without_reading_the_target_again()
    {
        await PlanWithVersionsAsync([Daily(3)], DaysAgo(2), DaysAgo(1));
        var editor = _fixture.Create().Plans.Single();
        var preview = editor.RetentionPreview;
        await preview.RefreshCommand.ExecuteAsync(null);

        preview.HasCoverage.Should().BeTrue();
        preview.NoRulesText.Should().BeNull();
        preview.CoverageLanes.Should().ContainSingle().Which.Slots.Select(s => s.State).Should()
            .Equal(SlotState.Present, SlotState.Present, SlotState.Pending);
        preview.CoverageLabels.Should().Equal("Daily · 3");

        editor.AddRetentionRuleCommand.Execute(null);
        await WaitUntilAsync(() => preview.CoverageLanes.Count == 2);
        preview.CoverageLabels.Should().Equal("Daily · 3", "Daily · 7");

        editor.RetentionRuleRows[0].KeepText = "5";
        await WaitUntilAsync(() => preview.CoverageLanes[0].Slots.Count == 5);
        preview.CoverageLabels[0].Should().Be("Daily · 5");

        editor.RemoveRetentionRuleCommand.Execute(editor.RetentionRuleRows[1]);
        await WaitUntilAsync(() => preview.CoverageLanes.Count == 1);
    }

    [Fact]
    public async Task Retention_lanes_show_the_warnings_of_the_run_log()
    {
        await PlanWithVersionsAsync([Daily(3)], DaysAgo(2), DaysAgo(1));
        var warned = VersionName.Format(DaysAgo(2), "Projects");
        new RunLog(_fixture.Paths.LogFileFor("p1")).Append(Run(warned, RunStatus.CompletedWithWarnings, skipped: 2));
        var preview = _fixture.Create().Plans.Single().RetentionPreview;

        await preview.RefreshCommand.ExecuteAsync(null);

        var slot = preview.CoverageLanes[0].Slots[0];
        slot.State.Should().Be(SlotState.PresentWithWarnings);
        preview.CoverageSlotText(slot).Should().EndWith("With warnings: 2 skipped");
    }

    [Fact]
    public async Task Clicking_a_slot_selects_the_version_in_the_now_table()
    {
        await PlanWithVersionsAsync([Daily(3)], DaysAgo(2), DaysAgo(1));
        var preview = _fixture.Create().Plans.Single().RetentionPreview;
        await preview.RefreshCommand.ExecuteAsync(null);
        var name = VersionName.Format(DaysAgo(2), "Projects");

        preview.SelectCoverageCommand.Execute(name);

        preview.SelectedNowRow.Should().NotBeNull();
        preview.SelectedNowRow!.Name.Should().Be(name);
        preview.SelectedNowRow.Should().BeSameAs(preview.NowRows.Single(r => r.Name == name));
    }

    [Fact]
    public async Task Without_rules_the_coverage_is_replaced_by_a_hint()
    {
        await PlanWithVersionsAsync([], DaysAgo(1));
        var preview = _fixture.Create().Plans.Single().RetentionPreview;

        await preview.RefreshCommand.ExecuteAsync(null);

        preview.HasCoverage.Should().BeFalse();
        preview.CoverageLanes.Should().BeEmpty();
        preview.NoRulesText.Should().Be("No retention rules");
    }

    [Fact]
    public async Task A_target_that_cannot_be_read_hides_the_coverage()
    {
        var plan = await PlanWithVersionsAsync([Daily(3)], DaysAgo(1));
        var fail = false;
        var storages = new LambdaStorageFactory(location =>
            fail ? throw new InvalidOperationException("broken") : new StorageFactory().Open(location));
        var preview = new RetentionPreviewViewModel(() => plan, () => null, new FakeFolderOpener(), storages, () => []);
        await preview.RefreshCommand.ExecuteAsync(null);
        preview.HasCoverage.Should().BeTrue();

        fail = true;
        await preview.RefreshCommand.ExecuteAsync(null);

        preview.Error.Should().NotBeNull();
        preview.HasCoverage.Should().BeFalse();
        preview.NoRulesText.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_unreachable_target_shows_no_coverage_and_no_hint(bool withRules)
    {
        var plan = new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Target = StorageLocation.FileSystem(_target),
            Retention = withRules ? [Daily(3)] : [],
        };
        var storages = new LambdaStorageFactory(_ => OfflineStorage.Create());
        var preview = new RetentionPreviewViewModel(() => plan, () => null, new FakeFolderOpener(), storages, () => []);

        await preview.RefreshCommand.ExecuteAsync(null);

        preview.Error.Should().BeNull("an offline target is shown as missing, as before");
        preview.EmptyText.Should().Be("The target folder does not exist (yet).");
        preview.HasCoverage.Should().BeFalse();
        preview.CoverageLanes.Should().BeEmpty();
        preview.NoRulesText.Should().BeNull();
    }

    [Fact]
    public async Task A_target_that_is_not_created_yet_shows_the_lanes()
    {
        var plan = new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Target = StorageLocation.FileSystem(_target),   // does not exist yet
            Retention = [Daily(3)],
        };
        var preview = new RetentionPreviewViewModel(() => plan, () => null, new FakeFolderOpener(), new StorageFactory(), () => []);

        await preview.RefreshCommand.ExecuteAsync(null);

        preview.HasCoverage.Should().BeTrue();
        preview.CoverageLanes.Should().ContainSingle().Which.Slots.Select(s => s.State).Should()
            .Equal(SlotState.BeforeFirst, SlotState.BeforeFirst, SlotState.Pending);
    }

    [Fact]
    public async Task A_finished_run_reloads_the_log_into_the_retention_coverage()
    {
        var plan = await PlanWithVersionsAsync([Daily(3)], DaysAgo(2), DaysAgo(1));
        var vm = _fixture.Create();
        var preview = vm.Plans.Single().RetentionPreview;
        await preview.RefreshCommand.ExecuteAsync(null);
        preview.CoverageLanes[0].Slots[0].State.Should().Be(SlotState.Present);

        var warned = VersionName.Format(DaysAgo(2), "Projects");
        new RunLog(_fixture.Paths.LogFileFor("p1")).Append(Run(warned, RunStatus.CompletedWithWarnings, skipped: 4));
        _fixture.Queue.Enqueue(new BackupRequest(plan, [], RunTrigger.Manual)).Should().BeTrue();
        await _fixture.Queue.WhenIdleAsync();

        await WaitUntilAsync(() => preview.CoverageLanes.Count == 1 &&
                                   preview.CoverageLanes[0].Slots[0].State == SlotState.PresentWithWarnings);
        preview.CoverageSlotText(preview.CoverageLanes[0].Slots[0]).Should().EndWith("With warnings: 4 skipped");
    }

    /// <summary>A storage whose root cannot be reached: listing it throws <see cref="StorageUnavailableException"/>.</summary>
    public class OfflineStorage : System.Reflection.DispatchProxy
    {
        public static IStorage Create() => Create<IStorage, OfflineStorage>();

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IStorage.ListAsync)
                ? Offline()
                : throw new StorageUnavailableException("", "offline");

        private static async IAsyncEnumerable<StorageEntry> Offline()
        {
            await Task.Yield();
            throw new StorageUnavailableException("", "offline");
#pragma warning disable CS0162 // an iterator needs a yield
            yield break;
#pragma warning restore CS0162
        }
    }

    // ------------------------------------------------------------------ Versions tab

    [Fact]
    public async Task The_versions_tab_shows_the_warnings_of_the_run_log()
    {
        await PlanWithVersionsAsync([Daily(3)], DaysAgo(2), DaysAgo(1));
        var warned = VersionName.Format(DaysAgo(2), "Projects");
        new RunLog(_fixture.Paths.LogFileFor("p1")).Append(Run(warned, RunStatus.CompletedWithWarnings, skipped: 2));
        var versions = _fixture.Create().Plans.Single().Versions;

        await versions.RefreshCommand.ExecuteAsync(null);

        var slot = versions.CoverageLanes.Should().ContainSingle().Subject.Slots[0];
        slot.State.Should().Be(SlotState.PresentWithWarnings);
        slot.Version!.Id.Should().Be(warned);
        versions.CoverageSlotText(slot).Should().EndWith("With warnings: 2 skipped");
    }

    [Fact]
    public async Task The_versions_tab_uses_the_saved_rules_and_a_click_selects_the_version()
    {
        await PlanWithVersionsAsync([Daily(3)], DaysAgo(2), DaysAgo(1));
        var editor = _fixture.Create().Plans.Single();
        editor.AddRetentionRuleCommand.Execute(null);   // edited, not saved
        var versions = editor.Versions;

        await versions.RefreshCommand.ExecuteAsync(null);

        versions.HasCoverage.Should().BeTrue();
        versions.CoverageLanes.Should().ContainSingle().Which.Slots.Select(s => s.State).Should()
            .Equal(SlotState.Present, SlotState.Present, SlotState.Pending);
        versions.CoverageLabels.Should().Equal("Daily · 3");

        var name = VersionName.Format(DaysAgo(2), "Projects");
        versions.SelectCoverageCommand.Execute(name);
        versions.SelectedVersion!.Name.Should().Be(name);
    }

    [Fact]
    public async Task The_versions_tab_shows_the_hint_without_saved_rules()
    {
        await PlanWithVersionsAsync([], DaysAgo(1));
        var versions = _fixture.Create().Plans.Single().Versions;

        await versions.RefreshCommand.ExecuteAsync(null);

        versions.HasCoverage.Should().BeFalse();
        versions.NoRulesText.Should().Be("No retention rules");
    }

    [Fact]
    public async Task The_versions_tab_hides_the_coverage_when_the_target_cannot_be_read()
    {
        var plan = await PlanWithVersionsAsync([Daily(3)], DaysAgo(1));
        var fail = false;
        var storages = new LambdaStorageFactory(location =>
            fail ? throw new InvalidOperationException("broken") : new StorageFactory().Open(location));
        var worker = new Core.Versions.VersionIndexWorker(new Core.Versions.VersionIndexSet(Path.Combine(_fixture.Root, "index")));
        var context = new VersionsContext(worker, _fixture.Dialogs, _ => { }, storages, MachineName: "THIS-PC");
        var versions = new VersionsViewModel(() => plan, () => false, new FakeFolderOpener(), context, () => []);
        await versions.RefreshCommand.ExecuteAsync(null);
        versions.HasCoverage.Should().BeTrue();

        fail = true;
        await versions.RefreshCommand.ExecuteAsync(null);

        versions.Error.Should().NotBeNull();
        versions.HasCoverage.Should().BeFalse();
        versions.NoRulesText.Should().BeNull();
    }

    // ------------------------------------------------------------------ History tab

    /// <summary>A plan with versions 2 and 1 days ago; the log names the older one (with warnings) and a failed run.</summary>
    private async Task<(PlanEditorViewModel Editor, string Logged, string Unlogged)> HistoryPlanAsync(
        IReadOnlyList<RetentionRule> rules)
    {
        await PlanWithVersionsAsync(rules, DaysAgo(2), DaysAgo(1));
        var logged = VersionName.Format(DaysAgo(2), "Projects");
        var log = new RunLog(_fixture.Paths.LogFileFor("p1"));
        log.Append(new RunLogEntry
        {
            Version = logged, Status = RunStatus.CompletedWithWarnings, SkippedCount = 1, StartUtc = DaysAgo(2).ToUniversalTime(),
        });
        log.Append(new RunLogEntry { Status = RunStatus.Error, Reason = "boom", StartUtc = DaysAgo(1).ToUniversalTime() });
        var editor = _fixture.Create().Plans.Single();
        await editor.HistoryCoverage.LoadAsync();
        return (editor, logged, VersionName.Format(DaysAgo(1), "Projects"));
    }

    [Fact]
    public async Task The_history_tab_shows_the_coverage_of_the_saved_rules_with_the_log_warnings()
    {
        var (editor, logged, _) = await HistoryPlanAsync([Daily(3)]);
        var coverage = editor.HistoryCoverage;

        coverage.HasCoverage.Should().BeTrue();
        coverage.NoRulesText.Should().BeNull();
        coverage.CoverageLabels.Should().Equal("Daily · 3");
        var slots = coverage.CoverageLanes.Should().ContainSingle().Subject.Slots;
        slots.Select(s => s.State).Should().Equal(SlotState.PresentWithWarnings, SlotState.Present, SlotState.Pending);
        slots[0].Version!.Id.Should().Be(logged);
        coverage.CoverageSlotText(slots[0]).Should().EndWith("With warnings: 1 skipped");
    }

    [Fact]
    public async Task Selecting_a_run_rings_the_version_it_wrote()
    {
        var (editor, logged, _) = await HistoryPlanAsync([Daily(3)]);
        var coverage = editor.HistoryCoverage;
        coverage.SelectedId.Should().BeNull();

        coverage.SelectedRun = editor.Run.History.Single(r => r.VersionName == logged);
        coverage.SelectedId.Should().Be(logged);

        coverage.SelectedRun = editor.Run.History.Single(r => !r.HasVersion);   // the failed run wrote none
        coverage.SelectedId.Should().BeNull();
    }

    [Fact]
    public async Task Clicking_a_slot_selects_the_run_that_wrote_its_version()
    {
        var (editor, logged, _) = await HistoryPlanAsync([Daily(3)]);
        var coverage = editor.HistoryCoverage;

        coverage.SelectCoverageCommand.Execute(logged.ToUpperInvariant());

        coverage.SelectedRun.Should().BeSameAs(editor.Run.History.Single(r => r.VersionName == logged));
        coverage.SelectedId.Should().Be(logged);
    }

    [Fact]
    public async Task Clicking_a_slot_selects_the_run_that_wrote_its_version_not_a_later_retry_line()
    {
        var (editor, logged, _) = await HistoryPlanAsync([Daily(3)]);
        var original = editor.Run.Entries.Single(e => e.Version == logged);
        var retry = new RunLogEntry
        {
            RunId = "retry-1", Trigger = RunTrigger.Retry, RetryOf = original.RunId, Attempt = 1, Attempts = 3,
            Version = logged, Status = RunStatus.Completed, StartUtc = DaysAgo(1).ToUniversalTime().AddMinutes(-1),
        };
        editor.Run.LoadHistory([.. editor.Run.Entries, retry]);   // the newest row is the retry line of that version
        editor.Run.History[0].IsRetryLine.Should().BeTrue();
        var coverage = editor.HistoryCoverage;

        coverage.SelectCoverageCommand.Execute(logged);

        coverage.SelectedRun.Should().BeSameAs(editor.Run.History.Single(r => r.VersionName == logged && !r.IsRetryLine));
    }

    [Fact]
    public async Task Reloading_keeps_the_selected_run_and_not_its_retry_line_of_the_same_start()
    {
        var (editor, logged, _) = await HistoryPlanAsync([Daily(3)]);
        var original = editor.Run.Entries.Single(e => e.Version == logged);
        var retry = new RunLogEntry
        {
            RunId = "retry-1", Trigger = RunTrigger.Retry, RetryOf = original.RunId, Attempt = 1, Attempts = 3,
            Version = logged, Status = RunStatus.Completed, StartUtc = original.StartUtc,   // same start, still told apart
        };
        editor.Run.LoadHistory([.. editor.Run.Entries, retry]);
        var coverage = editor.HistoryCoverage;
        coverage.SelectedRun = editor.Run.History.Single(r => r.VersionName == logged && !r.IsRetryLine);

        editor.Run.LoadHistory(editor.Run.Entries.ToList());

        coverage.SelectedRun.Should().BeSameAs(editor.Run.History.Single(r => r.VersionName == logged && !r.IsRetryLine));
    }

    [Fact]
    public async Task Clicking_a_slot_whose_version_no_run_wrote_changes_nothing()
    {
        var (editor, logged, unlogged) = await HistoryPlanAsync([Daily(3)]);
        var coverage = editor.HistoryCoverage;
        var selected = coverage.SelectedRun = editor.Run.History.Single(r => r.VersionName == logged);

        coverage.SelectCoverageCommand.Execute(unlogged);

        coverage.SelectedRun.Should().BeSameAs(selected);
    }

    [Fact]
    public async Task The_history_tab_shows_the_hint_without_saved_rules()
    {
        var (editor, _, _) = await HistoryPlanAsync([]);

        editor.HistoryCoverage.HasCoverage.Should().BeFalse();
        editor.HistoryCoverage.NoRulesText.Should().Be("No retention rules");
    }

    [Fact]
    public async Task The_history_tab_shows_no_coverage_for_an_unreachable_target()
    {
        var plan = new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Target = StorageLocation.FileSystem(_target),
            Retention = [Daily(3)],
        };
        var run = new PlanRunViewModel(new StorageFactory());
        var coverage = new HistoryCoverageViewModel(() => plan, new LambdaStorageFactory(_ => OfflineStorage.Create()), run);

        await coverage.LoadAsync();

        coverage.HasCoverage.Should().BeFalse();
        coverage.NoRulesText.Should().BeNull();
    }

    [Fact]
    public async Task Reloading_the_history_drops_a_run_that_is_gone_and_reads_the_log_again()
    {
        var (editor, logged, _) = await HistoryPlanAsync([Daily(3)]);
        var coverage = editor.HistoryCoverage;
        coverage.SelectedRun = editor.Run.History.Single(r => r.VersionName == logged);

        editor.Run.LoadHistory([Run(logged, RunStatus.Completed)]);   // another start: not the same run

        coverage.SelectedRun.Should().BeNull();
        coverage.CoverageLanes[0].Slots[0].State.Should().Be(SlotState.Present);   // the warning is gone from the log
    }

    [Fact]
    public async Task Reloading_the_same_runs_keeps_the_selected_run()
    {
        var (editor, logged, _) = await HistoryPlanAsync([Daily(3)]);
        var coverage = editor.HistoryCoverage;
        var before = coverage.SelectedRun = editor.Run.History.Single(r => r.VersionName == logged);

        editor.Run.LoadHistory(editor.Run.Entries.ToList());

        coverage.SelectedRun.Should().NotBeNull().And.NotBeSameAs(before);
        coverage.SelectedRun.Should().BeSameAs(editor.Run.History.Single(r => r.VersionName == logged));
        coverage.SelectedId.Should().Be(logged);
    }

    [Fact]
    public async Task The_selected_run_gives_the_date_to_mark()
    {
        var (editor, logged, _) = await HistoryPlanAsync([Daily(3)]);
        var coverage = editor.HistoryCoverage;
        coverage.SelectedDate.Should().BeNull();

        coverage.SelectedRun = editor.Run.History.Single(r => r.VersionName == logged);
        coverage.SelectedDate.Should().Be(DaysAgo(2));                     // the time in the version's name

        coverage.SelectedRun = editor.Run.History.Single(r => !r.HasVersion);
        coverage.SelectedDate.Should().Be(DaysAgo(1));                     // the start of a run without a version
    }

    [Fact]
    public async Task Saving_new_rules_rebuilds_the_history_coverage()
    {
        var (editor, _, _) = await HistoryPlanAsync([Daily(3)]);
        var coverage = editor.HistoryCoverage;

        editor.AddRetentionRuleCommand.Execute(null);
        coverage.CoverageLanes.Should().ContainSingle("unsaved rules do not count");
        editor.TrySave(_fixture.Store).Should().BeTrue();

        coverage.CoverageLanes.Should().HaveCount(2);
        coverage.CoverageLabels.Should().Equal("Daily · 3", "Daily · 7");
    }

    [Fact]
    public async Task Rules_read_again_from_disk_rebuild_the_history_coverage()
    {
        var (editor, _, _) = await HistoryPlanAsync([Daily(3)]);
        var plan = editor.SavedPlan();
        plan.Retention = [Daily(5)];

        editor.ReplaceSaved(plan);

        editor.HistoryCoverage.CoverageLabels.Should().Equal("Daily · 5");
    }

    private sealed class LambdaStorageFactory(Func<StorageLocation, IStorage> open) : IStorageFactory
    {
        public IStorage Open(StorageLocation location) => open(location);
    }
}
