using System.IO;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
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

    // ------------------------------------------------------------------ Versions tab

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

    private sealed class LambdaStorageFactory(Func<StorageLocation, IStorage> open) : IStorageFactory
    {
        public IStorage Open(StorageLocation location) => open(location);
    }
}
