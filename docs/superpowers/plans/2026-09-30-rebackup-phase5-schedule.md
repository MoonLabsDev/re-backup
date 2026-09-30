# ReBackup Phase 5 — Schedule Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Plans run by themselves while the app runs: triggers per plan (daily, weekly, monthly, every n hours), a Schedule tab with the next five runs, one catch-up run after startup for plans that missed a trigger, "next run" in the plan list, and Pause/Resume in the tray. The retention preview uses the plan's triggers.

**Architecture:** `ReBackup.Core.Schedule` holds the trigger model (`ScheduleTrigger`, `ScheduleTriggers`), the pure `ScheduleCalculator` (run times in UTC from local wall-clock triggers, DST-aware) and the `Scheduler` (a one-minute timer from the injected `TimeProvider`, catch-up at start, pause). The scheduler only knows plan ids, the enabled flag and triggers; it hands due plans to a delegate that the App marshals to the UI thread, where `MainViewModel` queues the saved plan. `RunLog.ReadLast` gives the last run cheaply, and `BackupQueue.Close` makes shutdown refuse new jobs.

**Tech Stack:** .NET 9, WPF, CommunityToolkit.Mvvm 8.4.2, H.NotifyIcon.Wpf 2.3.2, System.Text.Json, xUnit, FluentAssertions 7.0.0, Microsoft.Extensions.TimeProvider.Testing.

**Spec:** `docs/superpowers/specs/2026-09-30-rebackup-design.md` (this plan covers §4.2 `triggers`, §5 `ScheduleCalculator`/`Scheduler`, §8, §10.1 "next run", §10.2 tab 2 Schedule, §10.3 Pause/Resume, §12 ScheduleCalculator tests, and build phase 5 of §13; plus the Phase 3 and Phase 4 carry-forward entry conditions for this phase)

## Global Constraints

- Target frameworks: `net9.0` for Core and Tests, `net9.0-windows` for App. Nullable and implicit usings are on. Builds must stay at 0 warnings; check with `dotnet build --no-incremental`.
- Trigger JSON: `{ "type": "Daily", "time": "02:00" }`, `{ "type": "Weekly", "days": ["Mon", "Wed"], "time": "18:00" }`, `{ "type": "Monthly", "day": 1, "time": "03:00" }`, `{ "type": "Interval", "everyHours": 4, "from": "08:00", "to": "20:00" }`. Fields a type does not use are not written. Times are `HH:mm` local wall-clock time. Monthly days use the retention anchor encoding (`1..31`, `0` = last day, `-n` = n days before the last day).
- On a DST gap a trigger runs at the first valid minute after it; on a DST overlap it runs once (at the earlier of the two instants).
- Triggers are checked once a minute. A plan is due when a trigger time lies after the point up to which the scheduler has already checked that plan and at or before now. Plans added or saved while the app runs are only run for triggers from then on.
- On startup, a plan that has run before and missed at least one trigger since the start of its last logged run gets exactly one `CatchUp` run about one minute after startup. A plan that never ran is not caught up. Every logged run counts, whatever its status.
- Disabled plans and a paused scheduler never queue scheduled or catch-up runs; triggers that pass while paused are skipped. Manual runs always work. The pause state is not saved.
- Scheduled runs use the plan as saved (never unsaved edits). A plan already queued or running is not queued again.
- Time comes from an injected `TimeProvider` in Core, including its `LocalTimeZone`.
- FluentAssertions stays pinned to 7.0.0.
- Every commit message ends with a `Co-Authored-By: Claude <model> <noreply@anthropic.com>` trailer naming the authoring model, separated from the subject by a blank line (two `-m` arguments).
- Expected values in the tests of this plan were worked out by hand (the DST tests use the Windows zone "W. Europe Standard Time": summer time 2026 from Sunday 29 March 02:00 to Sunday 25 October 03:00, 2027 from 28 March). If a test fails although the code follows the plan, do not change the expectation or the code to make it pass: stop and report which value differs and why.
- The App has no automated tests. App tasks are verified by build, the Core suite, a startup smoke test, and the manual checklist in the task.

Startup smoke test (App tasks): build, start `src/ReBackup.App/bin/Debug/net9.0-windows/ReBackup.App.exe`, wait 5 s, confirm the process is still running, then stop it. Make sure no `ReBackup.App` process is left behind. Never start a backup.

## File Structure

```
src/ReBackup.Core/
  Schedule/ScheduleTrigger.cs       TriggerType, ScheduleTrigger
  Schedule/ScheduleTriggers.cs      validation, time parsing, weekdays, interval window
  Schedule/ScheduleCalculator.cs    occurrences, next runs, last due trigger, local run times
  Schedule/Scheduler.cs             ScheduledPlan, Scheduler
  Plans/BackupPlan.cs               (modify) Triggers
  Plans/PlanValidator.cs            (modify) trigger errors, NameErrors
  Backup/BackupRunner.cs            (modify) name check through PlanValidator.NameErrors
  Backup/RunLog.cs                  (modify) ReadLast
  Backup/BackupQueue.cs             (modify) Close, IsClosed
src/ReBackup.App/
  ViewModels/TriggerRowViewModel.cs      one editable trigger (with WeekdayChoice)
  ViewModels/PlanEditorViewModel.cs      (modify) trigger rows, next runs
  ViewModels/PlanRunViewModel.cs         (modify) NextRunText
  ViewModels/MainViewModel.cs            (modify) scheduler: publish plans, scheduled runs, next run texts
  ViewModels/RetentionPreviewViewModel.cs (modify) simulation from the plan's triggers
  Views/ScheduleView.xaml(.cs)
  Views/RetentionView.xaml               (modify) "Assume" only without triggers
  MainWindow.xaml                        (modify) Schedule tab, next run in the list, scheduler state
  App.xaml.cs                            (modify) scheduler creation, start, tray pause, shutdown order
tests/ReBackup.Core.Tests/
  Schedule/ScheduleTriggersTests.cs
  Schedule/ScheduleCalculatorTests.cs
  Schedule/SchedulerTests.cs
  Plans/PlanValidatorTests.cs, PlanStoreTests.cs     (modify)
  Backup/RunLogTests.cs, BackupQueueTests.cs, BackupRunnerTests.cs   (modify)
```

---

### Task 1: Triggers in the plan

**Files:**
- Create: `src/ReBackup.Core/Schedule/ScheduleTrigger.cs`, `src/ReBackup.Core/Schedule/ScheduleTriggers.cs`
- Modify: `src/ReBackup.Core/Plans/BackupPlan.cs`, `src/ReBackup.Core/Plans/PlanValidator.cs`, `src/ReBackup.Core/Backup/BackupRunner.cs`
- Test: `tests/ReBackup.Core.Tests/Schedule/ScheduleTriggersTests.cs` (create); `tests/ReBackup.Core.Tests/Plans/PlanValidatorTests.cs`, `tests/ReBackup.Core.Tests/Plans/PlanStoreTests.cs`, `tests/ReBackup.Core.Tests/Backup/BackupRunnerTests.cs` (modify)

**Interfaces:**
- Consumes: `RetentionRules.TryGetWeekday(string?, out DayOfWeek)`, `JsonDefaults.Options`, `PlanValidator.Validate`.
- Produces:
  - `enum TriggerType { Daily, Weekly, Monthly, Interval }`
  - `sealed class ScheduleTrigger { TriggerType Type; string? Time; List<string>? Days; int? Day; int? EveryHours; string? From; string? To; }`
  - `static class ScheduleTriggers`: `string? Validate(ScheduleTrigger? trigger)`, `bool TryParseTime(string? text, out TimeOnly time)`, `IReadOnlyList<DayOfWeek> WeekdaysOf(ScheduleTrigger trigger)`, `(TimeOnly From, TimeOnly To) IntervalWindow(ScheduleTrigger trigger)`, `static IReadOnlyList<string> ShortDayNames` (Mon…Sun in that order)
  - `BackupPlan.Triggers` (`List<ScheduleTrigger>`, never null)
  - `PlanValidator.NameErrors(string? name)` → the name rules without the uniqueness check

- [ ] **Step 1: Write the failing tests**

Create `tests/ReBackup.Core.Tests/Schedule/ScheduleTriggersTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using ReBackup.Core.Json;
using ReBackup.Core.Schedule;

namespace ReBackup.Core.Tests.Schedule;

public class ScheduleTriggersTests
{
    private static ScheduleTrigger Daily(string? time) => new() { Type = TriggerType.Daily, Time = time };

    [Fact]
    public void Valid_triggers_pass()
    {
        ScheduleTrigger[] triggers =
        [
            Daily("02:00"),
            Daily("7:05"),
            new() { Type = TriggerType.Weekly, Days = ["Mon", "wednesday"], Time = "18:00" },
            new() { Type = TriggerType.Monthly, Day = 31, Time = "03:00" },
            new() { Type = TriggerType.Monthly, Day = 0, Time = "03:00" },
            new() { Type = TriggerType.Monthly, Day = -30, Time = "03:00" },
            new() { Type = TriggerType.Interval, EveryHours = 4, From = "08:00", To = "20:00" },
            new() { Type = TriggerType.Interval, EveryHours = 24 },
        ];

        triggers.Select(ScheduleTriggers.Validate).Should().OnlyContain(problem => problem == null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("24:00")]
    [InlineData("2 Uhr")]
    [InlineData("02:60")]
    public void A_time_that_is_not_HH_mm_is_reported(string? time)
    {
        ScheduleTriggers.Validate(Daily(time)).Should().Be("the time must be written as HH:mm, for example 02:00.");
    }

    [Fact]
    public void Weekly_needs_valid_weekdays()
    {
        ScheduleTriggers.Validate(new ScheduleTrigger { Type = TriggerType.Weekly, Time = "18:00" })
            .Should().Be("choose at least one weekday.");
        ScheduleTriggers.Validate(new ScheduleTrigger { Type = TriggerType.Weekly, Days = [], Time = "18:00" })
            .Should().Be("choose at least one weekday.");
        ScheduleTriggers.Validate(new ScheduleTrigger { Type = TriggerType.Weekly, Days = ["Mon", "Mo"], Time = "18:00" })
            .Should().Be("\"Mo\" is not a weekday.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(32)]
    [InlineData(-31)]
    public void Monthly_needs_a_day_in_range(int? day)
    {
        ScheduleTriggers.Validate(new ScheduleTrigger { Type = TriggerType.Monthly, Day = day, Time = "03:00" })
            .Should().Be("the day must be from 1 to 31, 0 for the last day of the month, or -1 to -30 for days before the last day.");
    }

    [Theory]
    [InlineData(null, "08:00", "20:00", "the interval must be a whole number of hours from 1 to 24.")]
    [InlineData(0, "08:00", "20:00", "the interval must be a whole number of hours from 1 to 24.")]
    [InlineData(25, "08:00", "20:00", "the interval must be a whole number of hours from 1 to 24.")]
    [InlineData(4, "8 Uhr", "20:00", "the start time must be written as HH:mm, for example 08:00.")]
    [InlineData(4, "08:00", "later", "the end time must be written as HH:mm, for example 20:00.")]
    [InlineData(4, "20:00", "08:00", "the start time must not be after the end time.")]
    public void Interval_problems_are_reported(int? everyHours, string? from, string? to, string expected)
    {
        ScheduleTriggers.Validate(new ScheduleTrigger { Type = TriggerType.Interval, EveryHours = everyHours, From = from, To = to })
            .Should().Be(expected);
    }

    [Fact]
    public void A_missing_or_unknown_trigger_is_reported()
    {
        ScheduleTriggers.Validate(null).Should().Be("the trigger is empty.");
        ScheduleTriggers.Validate(new ScheduleTrigger { Type = (TriggerType)42, Time = "02:00" }).Should().Be("the type is unknown.");
    }

    [Fact]
    public void Weekdays_and_interval_window_are_read()
    {
        ScheduleTriggers.WeekdaysOf(new ScheduleTrigger { Type = TriggerType.Weekly, Days = ["Wed", "mon", "Wednesday"] })
            .Should().Equal(DayOfWeek.Wednesday, DayOfWeek.Monday);
        ScheduleTriggers.IntervalWindow(new ScheduleTrigger { Type = TriggerType.Interval, EveryHours = 4 })
            .Should().Be((new TimeOnly(0, 0), new TimeOnly(23, 59)));
        ScheduleTriggers.IntervalWindow(new ScheduleTrigger { Type = TriggerType.Interval, EveryHours = 4, From = "08:00", To = "20:00" })
            .Should().Be((new TimeOnly(8, 0), new TimeOnly(20, 0)));
        ScheduleTriggers.ShortDayNames.Should().Equal("Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun");
    }

    [Fact]
    public void Triggers_round_trip_in_the_spec_format()
    {
        var json = """
            [ { "type": "Daily", "time": "02:00" },
              { "type": "Weekly", "days": ["Mon", "Wed"], "time": "18:00" },
              { "type": "Interval", "everyHours": 4, "from": "08:00", "to": "20:00" },
              { "type": "Monthly", "day": 1, "time": "03:00" } ]
            """;

        var triggers = JsonSerializer.Deserialize<List<ScheduleTrigger>>(json, JsonDefaults.Options)!;

        triggers.Select(t => t.Type).Should().Equal(TriggerType.Daily, TriggerType.Weekly, TriggerType.Interval, TriggerType.Monthly);
        triggers[1].Days.Should().Equal("Mon", "Wed");
        triggers[2].EveryHours.Should().Be(4);
        triggers[3].Day.Should().Be(1);

        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(triggers, JsonDefaults.Options));
        saved.RootElement[0].EnumerateObject().Select(p => p.Name).Should().Equal("type", "time");
        saved.RootElement[1].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("type", "time", "days");
        saved.RootElement[2].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("type", "everyHours", "from", "to");
        saved.RootElement[3].GetProperty("day").GetInt32().Should().Be(1);
        saved.RootElement[0].GetProperty("type").GetString().Should().Be("Daily");
    }
}
```

In `tests/ReBackup.Core.Tests/Plans/PlanValidatorTests.cs`, add `using ReBackup.Core.Schedule;` and these tests:

```csharp
    [Fact]
    public void Invalid_triggers_are_reported_with_their_position()
    {
        var plan = ValidPlan();
        plan.Triggers =
        [
            new ScheduleTrigger { Type = TriggerType.Daily, Time = "02:00" },
            new ScheduleTrigger { Type = TriggerType.Weekly, Time = "18:00" },
        ];

        Validate(plan).Should().Equal("Trigger 2: choose at least one weekday.");
    }

    [Fact]
    public void NameErrors_checks_the_name_alone()
    {
        PlanValidator.NameErrors("Projects").Should().BeEmpty();
        PlanValidator.NameErrors("Projects.").Should().Equal("Name must not end with a dot.");
        PlanValidator.NameErrors(" x ").Should().Equal("Name must not start or end with spaces.");
        PlanValidator.NameErrors(null).Should().Equal("Name is required.");
    }
```

In `tests/ReBackup.Core.Tests/Plans/PlanStoreTests.cs`, add `using ReBackup.Core.Schedule;` and:

```csharp
    [Fact]
    public void Triggers_round_trip()
    {
        using var store = NewStore();
        var plan = new BackupPlan { Name = "Projects" };
        plan.Triggers.Add(new ScheduleTrigger { Type = TriggerType.Weekly, Days = ["Mon", "Wed"], Time = "18:00" });
        store.Save(plan);

        var loaded = store.LoadAll().Plans.Single();

        loaded.Triggers.Should().ContainSingle();
        loaded.Triggers[0].Days.Should().Equal("Mon", "Wed");
        loaded.Triggers[0].Time.Should().Be("18:00");
        loaded.Clone().Triggers.Should().ContainSingle();
    }

    [Fact]
    public void A_null_triggers_section_loads_as_no_triggers()
    {
        var plan = JsonSerializer.Deserialize<BackupPlan>("""{ "id": "p1", "name": "Keep", "triggers": null }""",
            JsonDefaults.Options)!;

        plan.Triggers.Should().BeEmpty();
    }
```

(The existing test `Unknown_properties_survive_load_and_save` must keep passing unchanged: its trigger is now read into `BackupPlan.Triggers` and written back as `{ "type": "Daily", "time": "02:00" }`.)

In `tests/ReBackup.Core.Tests/Backup/BackupRunnerTests.cs`, add:

```csharp
    [Fact]
    public async Task A_plan_name_that_Windows_would_change_aborts_as_Error()
    {
        var plan = Plan();
        plan.Name = "Projects.";

        var entry = await Runner().RunAsync(Request(plan));

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Be("The plan name \"Projects.\" cannot be used: Name must not end with a dot.");
        TargetEntries().Should().BeEmpty();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~ScheduleTriggersTests|FullyQualifiedName~PlanValidatorTests|FullyQualifiedName~PlanStoreTests|FullyQualifiedName~BackupRunnerTests"`
Expected: FAIL — compile errors, `ReBackup.Core.Schedule` does not exist.

- [ ] **Step 3: Write the trigger model**

Create `src/ReBackup.Core/Schedule/ScheduleTrigger.cs`:

```csharp
using System.Text.Json.Serialization;

namespace ReBackup.Core.Schedule;

public enum TriggerType { Daily, Weekly, Monthly, Interval }

/// <summary>One entry of a plan's "triggers" list. Which fields are used depends on <see cref="Type"/>.</summary>
public sealed class ScheduleTrigger
{
    public TriggerType Type { get; set; }

    /// <summary>Daily, Weekly, Monthly: local time of day, "HH:mm".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Time { get; set; }

    /// <summary>Weekly: the weekdays, written "Mon" … "Sun".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Days { get; set; }

    /// <summary>Monthly: 1..31 (at most the last day), 0 = last day, -n = n days before the last day.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Day { get; set; }

    /// <summary>Interval: hours between two runs, 1..24.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EveryHours { get; set; }

    /// <summary>Interval: first run of the day, "HH:mm"; 00:00 when missing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; set; }

    /// <summary>Interval: no run later in the day than this, "HH:mm"; 23:59 when missing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? To { get; set; }
}
```

Create `src/ReBackup.Core/Schedule/ScheduleTriggers.cs`:

```csharp
using System.Globalization;
using ReBackup.Core.Retention;

namespace ReBackup.Core.Schedule;

/// <summary>Checking and reading the fields of schedule triggers.</summary>
public static class ScheduleTriggers
{
    private static readonly string[] TimeFormats = ["HH:mm", "H:mm"];

    /// <summary>The weekday names triggers are written with, Monday first.</summary>
    public static IReadOnlyList<string> ShortDayNames { get; } = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>Null when the trigger can be used; otherwise what is wrong with it.</summary>
    public static string? Validate(ScheduleTrigger? trigger)
    {
        if (trigger is null)
            return "the trigger is empty.";
        if (!Enum.IsDefined(trigger.Type))
            return "the type is unknown.";

        if (trigger.Type != TriggerType.Interval && !TryParseTime(trigger.Time, out _))
            return "the time must be written as HH:mm, for example 02:00.";

        switch (trigger.Type)
        {
            case TriggerType.Weekly:
                if (trigger.Days is not { Count: > 0 })
                    return "choose at least one weekday.";
                foreach (var day in trigger.Days)
                {
                    if (!RetentionRules.TryGetWeekday(day, out _))
                        return $"\"{day}\" is not a weekday.";
                }
                return null;

            case TriggerType.Monthly:
                return trigger.Day is >= -30 and <= 31
                    ? null
                    : "the day must be from 1 to 31, 0 for the last day of the month, or -1 to -30 for days before the last day.";

            case TriggerType.Interval:
                if (trigger.EveryHours is not (>= 1 and <= 24))
                    return "the interval must be a whole number of hours from 1 to 24.";
                if (trigger.From is not null && !TryParseTime(trigger.From, out _))
                    return "the start time must be written as HH:mm, for example 08:00.";
                if (trigger.To is not null && !TryParseTime(trigger.To, out _))
                    return "the end time must be written as HH:mm, for example 20:00.";
                var (from, to) = IntervalWindow(trigger);
                return from > to ? "the start time must not be after the end time." : null;

            default:
                return null;
        }
    }

    public static bool TryParseTime(string? text, out TimeOnly time)
    {
        time = default;
        return !string.IsNullOrWhiteSpace(text) &&
               TimeOnly.TryParseExact(text.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    /// <summary>The distinct weekdays of a weekly trigger, in the order written; unreadable names are left out.</summary>
    public static IReadOnlyList<DayOfWeek> WeekdaysOf(ScheduleTrigger trigger)
    {
        var days = new List<DayOfWeek>();
        foreach (var name in trigger.Days ?? [])
        {
            if (RetentionRules.TryGetWeekday(name, out var day) && !days.Contains(day))
                days.Add(day);
        }
        return days;
    }

    /// <summary>The daily window of an interval trigger; 00:00–23:59 where a bound is missing or unreadable.</summary>
    public static (TimeOnly From, TimeOnly To) IntervalWindow(ScheduleTrigger trigger) =>
        (TryParseTime(trigger.From, out var from) ? from : new TimeOnly(0, 0),
         TryParseTime(trigger.To, out var to) ? to : new TimeOnly(23, 59));
}
```

- [ ] **Step 4: Triggers in the plan, the validator and the runner**

In `src/ReBackup.Core/Plans/BackupPlan.cs`:

1. Add `using ReBackup.Core.Schedule;`.
2. Directly below the `Retention` property, add:

```csharp

    private List<ScheduleTrigger> _triggers = [];

    /// <summary>When the plan runs by itself. Empty means: only when started by hand.</summary>
    public List<ScheduleTrigger> Triggers
    {
        get => _triggers;
        set => _triggers = value ?? [];
    }
```

3. Change the summary of `Extra` to `/// <summary>Plan sections not modelled by this version survive a load/save round trip.</summary>`.

In `src/ReBackup.Core/Plans/PlanValidator.cs`:

1. Add `using ReBackup.Core.Schedule;`.
2. In `Validate`, directly before `return errors;`, add:

```csharp

        for (var i = 0; i < plan.Triggers.Count; i++)
        {
            if (ScheduleTriggers.Validate(plan.Triggers[i]) is { } problem)
                errors.Add($"Trigger {i + 1}: {problem}");
        }
```

3. Replace the method `ValidateName` with:

```csharp
    private static void ValidateName(BackupPlan plan, IEnumerable<BackupPlan> allPlans, List<string> errors)
    {
        var problems = NameErrors(plan.Name);
        errors.AddRange(problems);
        if (problems.Count == 0 &&
            allPlans.Any(p => p.Id != plan.Id && string.Equals(p.Name, plan.Name, StringComparison.OrdinalIgnoreCase)))
            errors.Add($"Another plan is already named \"{plan.Name}\".");
    }

    /// <summary>What is wrong with a plan name on its own (uniqueness is not checked). Empty when it can be used.</summary>
    public static IReadOnlyList<string> NameErrors(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ["Name is required."];

        var errors = new List<string>();
        if (name != name.Trim())
            errors.Add("Name must not start or end with spaces.");
        if (name.EndsWith('.'))
            errors.Add("Name must not end with a dot.");
        if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            errors.Add("Name must not end with \".partial\".");
        if (name.EndsWith(".deleting", StringComparison.OrdinalIgnoreCase))
            errors.Add("Name must not end with \".deleting\".");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            errors.Add("Name contains characters that are not allowed in folder names.");
        return errors;
    }
```

(The uniqueness check keeps its message; it was reported together with other name errors before — it is now skipped when the name has other problems, which the existing tests do not depend on. If an existing `PlanValidatorTests` test expects both at once, report it instead of changing it.)

In `src/ReBackup.Core/Backup/BackupRunner.cs`, in `Prepare`, directly below the two lines that reject a plan name ending in `.deleting`, add:

```csharp
        if (PlanValidator.NameErrors(plan.Name) is [var nameProblem, ..])
            throw new BackupAbortException(RunStatus.Error, $"The plan name \"{plan.Name}\" cannot be used: {nameProblem}");
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~ScheduleTriggersTests|FullyQualifiedName~PlanValidatorTests|FullyQualifiedName~PlanStoreTests|FullyQualifiedName~BackupRunnerTests"`
Expected: PASS.

- [ ] **Step 6: Full build and test run**

Run: `dotnet build --no-incremental` — expected: 0 warnings, 0 errors.
Run: `dotnet test tests/ReBackup.Core.Tests` — expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): schedule triggers in the plan with validation; runner checks names like the validator" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 2: ScheduleCalculator

**Files:**
- Create: `src/ReBackup.Core/Schedule/ScheduleCalculator.cs`
- Test: `tests/ReBackup.Core.Tests/Schedule/ScheduleCalculatorTests.cs` (create)

**Interfaces:**
- Consumes: `ScheduleTrigger`, `ScheduleTriggers` (Task 1); `RetentionRules.MonthAnchor(int year, int month, int anchor)`.
- Produces: `static class ScheduleCalculator`:
  - `IEnumerable<DateTime> Occurrences(ScheduleTrigger trigger, DateTime afterUtc, TimeZoneInfo zone)` — run instants (UTC) strictly after `afterUtc`, ascending, endless; throws `ArgumentException` at the call for an invalid trigger
  - `IEnumerable<DateTime> NextRuns(IReadOnlyList<ScheduleTrigger> triggers, DateTime afterUtc, TimeZoneInfo zone)` — all triggers merged, ascending, no duplicates; empty for no triggers; throws `ArgumentException` at the call for an invalid trigger
  - `DateTime? LastDue(IReadOnlyList<ScheduleTrigger> triggers, DateTime afterUtc, DateTime nowUtc, TimeZoneInfo zone)` — the latest run instant in (`afterUtc`, `nowUtc`]; looks back at most `LookBack` (400 days) from `nowUtc`
  - `IEnumerable<DateTime> LocalRunTimes(IReadOnlyList<ScheduleTrigger> triggers, DateTime afterUtc, TimeZoneInfo zone)` — `NextRuns` as local wall-clock times (`DateTimeKind.Unspecified`)
  - `static readonly TimeSpan LookBack`

- [ ] **Step 1: Write the failing tests**

Create `tests/ReBackup.Core.Tests/Schedule/ScheduleCalculatorTests.cs`:

```csharp
using FluentAssertions;
using ReBackup.Core.Schedule;

namespace ReBackup.Core.Tests.Schedule;

// Zone: CET (UTC+1), summer time CEST (UTC+2) 2026-03-29 02:00 → 03:00 and 2026-10-25 03:00 → 02:00; 2027-03-28.
// 2026-09-30 is a Wednesday, 2026-10-01 a Thursday.
public class ScheduleCalculatorTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static ScheduleTrigger Daily(string time) => new() { Type = TriggerType.Daily, Time = time };

    private static ScheduleTrigger Interval(int hours, string? from = null, string? to = null) =>
        new() { Type = TriggerType.Interval, EveryHours = hours, From = from, To = to };

    private static ScheduleTrigger Monthly(int day, string time) => new() { Type = TriggerType.Monthly, Day = day, Time = time };

    private static DateTime[] First(ScheduleTrigger trigger, DateTime afterUtc, int count) =>
        ScheduleCalculator.Occurrences(trigger, afterUtc, Zone).Take(count).ToArray();

    [Fact]
    public void Daily_runs_every_day_at_the_local_time()
    {
        First(Daily("02:00"), Utc(2026, 9, 30, 12), 2).Should().Equal(Utc(2026, 10, 1, 0), Utc(2026, 10, 2, 0));
    }

    [Fact]
    public void Occurrences_are_strictly_after_the_given_instant()
    {
        First(Daily("02:00"), Utc(2026, 10, 1, 0), 1).Should().Equal(Utc(2026, 10, 2, 0));
    }

    [Fact]
    public void Weekly_runs_on_the_chosen_days()
    {
        var trigger = new ScheduleTrigger { Type = TriggerType.Weekly, Days = ["Mon", "Wed"], Time = "18:00" };

        First(trigger, Utc(2026, 9, 30, 0), 3).Should().Equal(Utc(2026, 9, 30, 16), Utc(2026, 10, 5, 16), Utc(2026, 10, 7, 16));
    }

    [Fact]
    public void Monthly_uses_the_last_day_for_31_and_0_and_counts_back_for_negative_days()
    {
        First(Monthly(31, "03:00"), Utc(2027, 1, 31, 12), 2).Should().Equal(Utc(2027, 2, 28, 2), Utc(2027, 3, 31, 1));
        First(Monthly(0, "03:00"), Utc(2027, 2, 1, 0), 1).Should().Equal(Utc(2027, 2, 28, 2));
        First(Monthly(-1, "03:00"), Utc(2027, 2, 1, 0), 1).Should().Equal(Utc(2027, 2, 27, 2));
        First(Monthly(1, "03:00"), Utc(2026, 9, 30, 12), 1).Should().Equal(Utc(2026, 10, 1, 1));
    }

    [Fact]
    public void Interval_runs_within_the_daily_window_and_starts_again_the_next_day()
    {
        First(Interval(4, "08:00", "20:00"), Utc(2026, 9, 30, 5), 5).Should().Equal(
            Utc(2026, 9, 30, 6), Utc(2026, 9, 30, 10), Utc(2026, 9, 30, 14), Utc(2026, 9, 30, 18), Utc(2026, 10, 1, 6));
    }

    [Fact]
    public void Interval_without_bounds_covers_the_whole_day()
    {
        First(Interval(6), Utc(2026, 9, 30, 12), 4).Should().Equal(
            Utc(2026, 9, 30, 16), Utc(2026, 9, 30, 22), Utc(2026, 10, 1, 4), Utc(2026, 10, 1, 10));
    }

    [Fact]
    public void A_time_in_the_spring_gap_runs_at_the_first_valid_minute_after_it()
    {
        First(Daily("02:30"), Utc(2026, 3, 28, 12), 2).Should().Equal(Utc(2026, 3, 29, 1), Utc(2026, 3, 30, 0, 30));
    }

    [Fact]
    public void Runs_moved_out_of_the_spring_gap_are_not_doubled()
    {
        // 02:00 does not exist and becomes 03:00, which the trigger has anyway.
        First(Interval(1, "00:00", "05:00"), Utc(2026, 3, 28, 12), 6).Should().Equal(
            Utc(2026, 3, 28, 23), Utc(2026, 3, 29, 0), Utc(2026, 3, 29, 1), Utc(2026, 3, 29, 2), Utc(2026, 3, 29, 3),
            Utc(2026, 3, 29, 22));
    }

    [Fact]
    public void A_time_in_the_autumn_overlap_runs_once_at_the_earlier_instant()
    {
        First(Daily("02:30"), Utc(2026, 10, 24, 12), 2).Should().Equal(Utc(2026, 10, 25, 0, 30), Utc(2026, 10, 26, 1, 30));
        First(Interval(1, "00:00", "04:00"), Utc(2026, 10, 24, 12), 5).Should().Equal(
            Utc(2026, 10, 24, 22), Utc(2026, 10, 24, 23), Utc(2026, 10, 25, 0), Utc(2026, 10, 25, 2), Utc(2026, 10, 25, 3));
    }

    [Fact]
    public void NextRuns_merges_triggers_without_duplicates()
    {
        ScheduleTrigger[] triggers = [Daily("02:00"), new() { Type = TriggerType.Weekly, Days = ["Thu"], Time = "02:00" }];

        ScheduleCalculator.NextRuns(triggers, Utc(2026, 9, 30, 12), Zone).Take(3).Should().Equal(
            Utc(2026, 10, 1, 0), Utc(2026, 10, 2, 0), Utc(2026, 10, 3, 0));
        ScheduleCalculator.NextRuns([], Utc(2026, 9, 30, 12), Zone).Should().BeEmpty();
    }

    [Fact]
    public void LastDue_is_the_latest_trigger_between_the_last_run_and_now()
    {
        ScheduleTrigger[] daily = [Daily("02:00")];

        ScheduleCalculator.LastDue(daily, Utc(2026, 9, 28, 10), Utc(2026, 9, 30, 12), Zone).Should().Be(Utc(2026, 9, 30, 0));
        ScheduleCalculator.LastDue(daily, Utc(2026, 9, 30, 1), Utc(2026, 9, 30, 12), Zone).Should().BeNull();
        ScheduleCalculator.LastDue(daily, Utc(2026, 9, 29, 12), Utc(2026, 9, 30, 0), Zone).Should().Be(Utc(2026, 9, 30, 0),
            "a trigger exactly at now is due");
        ScheduleCalculator.LastDue(daily, Utc(2020, 1, 1, 0), Utc(2026, 9, 30, 12), Zone).Should().Be(Utc(2026, 9, 30, 0));
        ScheduleCalculator.LastDue([], Utc(2026, 9, 28, 10), Utc(2026, 9, 30, 12), Zone).Should().BeNull();
    }

    [Fact]
    public void LocalRunTimes_gives_wall_clock_times()
    {
        ScheduleCalculator.LocalRunTimes([Daily("02:00")], Utc(2026, 9, 30, 12), Zone).First()
            .Should().Be(new DateTime(2026, 10, 1, 2, 0, 0));
    }

    [Fact]
    public void An_invalid_trigger_is_rejected_at_the_call()
    {
        var weekly = new ScheduleTrigger { Type = TriggerType.Weekly, Time = "18:00" };

        var occurrences = () => ScheduleCalculator.Occurrences(weekly, Utc(2026, 9, 30, 0), Zone);
        var nextRuns = () => ScheduleCalculator.NextRuns([Daily("02:00"), weekly], Utc(2026, 9, 30, 0), Zone);

        occurrences.Should().Throw<ArgumentException>();
        nextRuns.Should().Throw<ArgumentException>();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~ScheduleCalculatorTests"`
Expected: FAIL — compile errors, `ScheduleCalculator` does not exist.

- [ ] **Step 3: Write the calculator**

Create `src/ReBackup.Core/Schedule/ScheduleCalculator.cs`:

```csharp
using ReBackup.Core.Retention;

namespace ReBackup.Core.Schedule;

/// <summary>
/// Turns triggers (local wall-clock times) into run instants (UTC). A time that does not exist because the clocks
/// jump forward runs at the first valid minute after it; a time that exists twice runs once, at the earlier instant.
/// </summary>
public static class ScheduleCalculator
{
    /// <summary>How far back <see cref="LastDue"/> looks at most; it only needs to know whether a trigger was missed.</summary>
    public static readonly TimeSpan LookBack = TimeSpan.FromDays(400);

    /// <summary>The run instants of one trigger strictly after <paramref name="afterUtc"/>, ascending and endless.</summary>
    /// <exception cref="ArgumentException">The trigger is not valid.</exception>
    public static IEnumerable<DateTime> Occurrences(ScheduleTrigger trigger, DateTime afterUtc, TimeZoneInfo zone)
    {
        if (ScheduleTriggers.Validate(trigger) is { } problem)
            throw new ArgumentException($"The trigger is not valid: {problem}", nameof(trigger));
        return Iterate(trigger, DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), zone);
    }

    /// <summary>The run instants of all triggers, merged, ascending and without duplicates.</summary>
    /// <exception cref="ArgumentException">A trigger is not valid.</exception>
    public static IEnumerable<DateTime> NextRuns(IReadOnlyList<ScheduleTrigger> triggers, DateTime afterUtc, TimeZoneInfo zone)
    {
        var sources = triggers.Select(t => Occurrences(t, afterUtc, zone)).ToList();
        return Merge(sources);
    }

    /// <summary>The latest run instant after <paramref name="afterUtc"/> and at or before <paramref name="nowUtc"/>; null when there is none.</summary>
    /// <exception cref="ArgumentException">A trigger is not valid.</exception>
    public static DateTime? LastDue(IReadOnlyList<ScheduleTrigger> triggers, DateTime afterUtc, DateTime nowUtc, TimeZoneInfo zone)
    {
        var earliest = nowUtc - LookBack;
        var start = afterUtc < earliest ? earliest : afterUtc;
        DateTime? due = null;
        foreach (var run in NextRuns(triggers, start, zone))
        {
            if (run > nowUtc)
                break;
            due = run;
        }
        return due;
    }

    /// <summary><see cref="NextRuns"/> as local wall-clock times of <paramref name="zone"/>.</summary>
    public static IEnumerable<DateTime> LocalRunTimes(IReadOnlyList<ScheduleTrigger> triggers, DateTime afterUtc, TimeZoneInfo zone) =>
        NextRuns(triggers, afterUtc, zone)
            .Select(utc => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, zone), DateTimeKind.Unspecified));

    private static IEnumerable<DateTime> Iterate(ScheduleTrigger trigger, DateTime afterUtc, TimeZoneInfo zone)
    {
        // Start a day early: the local date of the instant can be behind the date of a run that is still ahead.
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone));
        date = date == DateOnly.MinValue ? date : date.AddDays(-1);
        var last = afterUtc;
        while (true)
        {
            foreach (var local in LocalTimesOn(trigger, date))
            {
                var utc = ToUtc(local, zone);
                if (utc > last)
                {
                    last = utc;
                    yield return utc;
                }
            }
            if (date == DateOnly.MaxValue)
                yield break;
            date = date.AddDays(1);
        }
    }

    private static IEnumerable<DateTime> LocalTimesOn(ScheduleTrigger trigger, DateOnly date)
    {
        switch (trigger.Type)
        {
            case TriggerType.Daily:
                yield return At(date, trigger.Time);
                break;

            case TriggerType.Weekly:
                if (ScheduleTriggers.WeekdaysOf(trigger).Contains(date.DayOfWeek))
                    yield return At(date, trigger.Time);
                break;

            case TriggerType.Monthly:
                if (date == RetentionRules.MonthAnchor(date.Year, date.Month, trigger.Day!.Value))
                    yield return At(date, trigger.Time);
                break;

            case TriggerType.Interval:
                var (from, to) = ScheduleTriggers.IntervalWindow(trigger);
                var step = trigger.EveryHours!.Value * 60;
                for (var minute = from.Hour * 60 + from.Minute; minute <= to.Hour * 60 + to.Minute; minute += step)
                    yield return date.ToDateTime(new TimeOnly(minute / 60, minute % 60));
                break;
        }
    }

    private static DateTime At(DateOnly date, string? time)
    {
        ScheduleTriggers.TryParseTime(time, out var parsed);
        return date.ToDateTime(parsed);
    }

    /// <summary>Local wall-clock time to UTC: forward out of a gap, the earlier instant in an overlap.</summary>
    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
        {
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private static IEnumerable<DateTime> Merge(List<IEnumerable<DateTime>> sources)
    {
        var active = new List<IEnumerator<DateTime>>();
        try
        {
            foreach (var source in sources)
            {
                var enumerator = source.GetEnumerator();
                if (enumerator.MoveNext())
                    active.Add(enumerator);
                else
                    enumerator.Dispose();
            }

            DateTime? last = null;
            while (active.Count > 0)
            {
                var next = active.MinBy(e => e.Current)!;
                var value = next.Current;
                if (last is null || value > last)
                {
                    last = value;
                    yield return value;
                }
                if (!next.MoveNext())
                {
                    next.Dispose();
                    active.Remove(next);
                }
            }
        }
        finally
        {
            foreach (var enumerator in active)
                enumerator.Dispose();
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~ScheduleCalculatorTests"`
Expected: PASS (13 tests).

- [ ] **Step 5: Full build and test run**

Run: `dotnet build --no-incremental` — expected: 0 warnings, 0 errors.
Run: `dotnet test tests/ReBackup.Core.Tests` — expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): schedule calculator with DST handling" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 3: Last run from the log, closing the queue

**Files:**
- Modify: `src/ReBackup.Core/Backup/RunLog.cs`, `src/ReBackup.Core/Backup/BackupQueue.cs`
- Test: `tests/ReBackup.Core.Tests/Backup/RunLogTests.cs`, `tests/ReBackup.Core.Tests/Backup/BackupQueueTests.cs` (modify)

**Interfaces:**
- Consumes: `RunLog`, `RunLogEntry`, `BackupQueue` (existing).
- Produces: `RunLogEntry? RunLog.ReadLast()` (reads only the last 64 KB of a large log where possible); `void BackupQueue.Close()` and `bool BackupQueue.IsClosed` — after `Close`, everything queued is removed, the running job is canceled and `Enqueue` returns false.

- [ ] **Step 1: Write the failing tests**

In `tests/ReBackup.Core.Tests/Backup/RunLogTests.cs`, add:

```csharp
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
```

In `tests/ReBackup.Core.Tests/Backup/BackupQueueTests.cs`, add:

```csharp
    [Fact]
    public async Task Close_cancels_everything_and_refuses_new_jobs()
    {
        _queue.Enqueue(Request("a"));
        _queue.Enqueue(Request("b"));
        await _runner.Started("a").WaitAsync(Timeout);

        _queue.Close();
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        _queue.IsClosed.Should().BeTrue();
        _queue.Enqueue(Request("c")).Should().BeFalse();
        _queue.IsBusy.Should().BeFalse();
        States("a").Should().Equal("Queued", "Running", "Finished");
        _updates.Last(u => u.PlanId == "a").Result!.Status.Should().Be(RunStatus.Canceled);
        States("b").Should().Equal("Queued", "Removed");
        States("c").Should().BeEmpty();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~RunLogTests|FullyQualifiedName~BackupQueueTests"`
Expected: FAIL — compile errors (`ReadLast`, `Close`, `IsClosed` do not exist).

- [ ] **Step 3: ReadLast**

In `src/ReBackup.Core/Backup/RunLog.cs`:

1. Add `using System.Text;` to the usings.
2. Add `private const int TailBytes = 64 * 1024;` below `AppendRetryDelay`.
3. Replace the whole method `ReadAll` with:

```csharp
    /// <summary>All readable entries, oldest first.</summary>
    public IReadOnlyList<RunLogEntry> ReadAll()
    {
        var entries = new List<RunLogEntry>();
        if (!File.Exists(LogFile))
            return entries;

        using var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (Parse(line) is { } entry)
                entries.Add(entry);
        }
        return entries;
    }

    /// <summary>The newest readable entry; null when there is none. Reads only the end of a large log where possible.</summary>
    public RunLogEntry? ReadLast()
    {
        if (!File.Exists(LogFile))
            return null;

        using (var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length > TailBytes)
            {
                stream.Seek(-TailBytes, SeekOrigin.End);
                var buffer = new byte[TailBytes];
                var length = stream.ReadAtLeast(buffer, TailBytes, throwOnEndOfStream: false);
                var lines = Encoding.UTF8.GetString(buffer, 0, length).Split('\n');
                // The first piece is usually the cut-off end of an older line.
                for (var i = lines.Length - 1; i >= 1; i--)
                {
                    if (Parse(lines[i].TrimEnd('\r')) is { } entry)
                        return entry;
                }
            }
        }

        // A small log, or no complete readable line in its last part.
        var all = ReadAll();
        return all.Count == 0 ? null : all[^1];
    }

    /// <summary>A readable entry; null for a blank or damaged line.</summary>
    private static RunLogEntry? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            if (JsonSerializer.Deserialize<RunLogEntry>(line, JsonDefaults.Compact) is not { } entry)
                return null;
            entry.Skipped ??= [];
            entry.RetentionDeleted ??= [];
            entry.Warnings ??= [];
            return entry;
        }
        catch (JsonException)
        {
            // A damaged line must not hide the rest of the history.
            return null;
        }
    }
```

- [ ] **Step 4: Close**

In `src/ReBackup.Core/Backup/BackupQueue.cs`:

1. Add the field `private bool _closed;` below `private bool _workerActive;`.
2. Add below the `RunningPlanId` property:

```csharp

    /// <summary>True after <see cref="Close"/>: no new jobs are accepted.</summary>
    public bool IsClosed
    {
        get { lock (_gate) return _closed; }
    }
```

3. In `Enqueue`, as the first statement inside `lock (_gate)`, add:

```csharp
            if (_closed)
            {
                job.Cancellation.Dispose();
                return false;
            }
```

4. Add below `CancelAll`:

```csharp

    /// <summary>Cancels everything and refuses new jobs from now on. Used when the app exits or restarts.</summary>
    public void Close()
    {
        lock (_gate)
            _closed = true;
        CancelAll();
    }
```

5. Change the summary of `Enqueue` to `/// <summary>False when that plan is already queued or running, or the queue is closed.</summary>`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~RunLogTests|FullyQualifiedName~BackupQueueTests"`
Expected: PASS.

- [ ] **Step 6: Full build and test run**

Run: `dotnet build --no-incremental` — expected: 0 warnings, 0 errors.
Run: `dotnet test tests/ReBackup.Core.Tests` — expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): read the last run cheaply, close the backup queue" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 4: Scheduler

**Files:**
- Create: `src/ReBackup.Core/Schedule/Scheduler.cs`
- Test: `tests/ReBackup.Core.Tests/Schedule/SchedulerTests.cs` (create)

**Interfaces:**
- Consumes: `ScheduleCalculator.NextRuns`, `ScheduleCalculator.LastDue` (Task 2); `ScheduleTrigger`; `RunTrigger` (`ReBackup.Core.Backup`).
- Produces:
  - `sealed record ScheduledPlan(string Id, bool Enabled, IReadOnlyList<ScheduleTrigger> Triggers)`
  - `sealed class Scheduler : IDisposable`: `Scheduler(Action<string, RunTrigger> enqueue, TimeProvider? timeProvider = null)`; `void UpdatePlans(IEnumerable<ScheduledPlan> plans)`; `void Start(Func<string, DateTime?> lastRunStartUtc)` (once; throws `InvalidOperationException` otherwise); `bool IsPaused { get; set; }`; `DateTime? NextRunUtc(string planId)`; `event Action? Changed`; `Dispose()` stops it for good; `static readonly TimeSpan CheckInterval` and `CatchUpDelay` (1 minute each)

Behaviour: see the Global Constraints. Checks run one second after every full minute (so a trigger on that minute is never seen a moment too early). `enqueue` is called outside the scheduler's lock and must not block; exceptions from it and from `Changed` handlers are swallowed. A plan whose triggers are invalid is never run by the scheduler and does not disturb the others. `Changed` is raised after every check, after `UpdatePlans`, `Start` and a change of `IsPaused`, on whatever thread did it.

- [ ] **Step 1: Write the failing tests**

Create `tests/ReBackup.Core.Tests/Schedule/SchedulerTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Schedule;

namespace ReBackup.Core.Tests.Schedule;

// The clock starts at 2026-09-30 01:58:30 UTC, and local time is UTC. Checks run at hh:mm:01.
public class SchedulerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 1, 58, 30, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Start);
    private readonly List<(string PlanId, RunTrigger Trigger)> _enqueued = [];
    private readonly Scheduler _scheduler;

    public SchedulerTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _scheduler = new Scheduler((planId, trigger) => _enqueued.Add((planId, trigger)), _time);
    }

    public void Dispose() => _scheduler.Dispose();

    private static ScheduleTrigger DailyAt(string time) => new() { Type = TriggerType.Daily, Time = time };

    private static ScheduledPlan Plan(string id, params ScheduleTrigger[] triggers) => new(id, true, triggers);

    private static DateTime Utc(int month, int day, int hour, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    private void StartWith(params ScheduledPlan[] plans) => StartWith(_ => null, plans);

    private void StartWith(Func<string, DateTime?> lastRunStartUtc, params ScheduledPlan[] plans)
    {
        _scheduler.UpdatePlans(plans);
        _scheduler.Start(lastRunStartUtc);
    }

    private void Minutes(int count) => _time.Advance(TimeSpan.FromMinutes(count));

    [Fact]
    public void Queues_a_plan_once_when_its_trigger_time_has_come()
    {
        StartWith(Plan("p1", DailyAt("02:00")));

        Minutes(1);   // 01:59:30
        _enqueued.Should().BeEmpty();

        Minutes(1);   // 02:00:30
        _enqueued.Should().Equal(("p1", RunTrigger.Scheduled));

        Minutes(10);
        _enqueued.Should().HaveCount(1);

        _time.Advance(TimeSpan.FromDays(1));
        _enqueued.Should().Equal(("p1", RunTrigger.Scheduled), ("p1", RunTrigger.Scheduled));
    }

    [Fact]
    public void A_disabled_plan_is_not_queued()
    {
        StartWith(new ScheduledPlan("p1", false, [DailyAt("02:00")]));

        Minutes(5);

        _enqueued.Should().BeEmpty();
        _scheduler.NextRunUtc("p1").Should().BeNull();
    }

    [Fact]
    public void Triggers_that_pass_while_paused_are_skipped()
    {
        StartWith(Plan("p1", DailyAt("02:00")));
        _scheduler.IsPaused = true;

        Minutes(5);
        _scheduler.IsPaused = false;
        Minutes(5);
        _enqueued.Should().BeEmpty();

        _time.Advance(TimeSpan.FromDays(1));
        _enqueued.Should().Equal(("p1", RunTrigger.Scheduled));
    }

    [Fact]
    public void A_plan_that_missed_a_trigger_gets_one_catch_up_run_about_a_minute_after_start()
    {
        StartWith(_ => Utc(9, 20, 10), Plan("p1", DailyAt("12:00")));

        Minutes(1);   // 01:59:30, check at 01:59:01 came before the catch-up delay was over
        _enqueued.Should().BeEmpty();

        Minutes(1);   // 02:00:30
        _enqueued.Should().Equal(("p1", RunTrigger.CatchUp));

        Minutes(60);
        _enqueued.Should().HaveCount(1);
    }

    [Fact]
    public void A_trigger_due_together_with_the_catch_up_is_covered_by_it()
    {
        StartWith(_ => Utc(9, 28, 10), Plan("p1", DailyAt("02:00")));

        Minutes(2);

        _enqueued.Should().Equal(("p1", RunTrigger.CatchUp));
    }

    [Fact]
    public void No_catch_up_without_a_missed_trigger_or_for_a_plan_that_never_ran()
    {
        var lastRuns = new Dictionary<string, DateTime?> { ["ran"] = Utc(9, 29, 13), ["never"] = null };
        StartWith(id => lastRuns[id], Plan("ran", DailyAt("12:00")), Plan("never", DailyAt("12:00")));

        Minutes(5);

        _enqueued.Should().BeEmpty();
    }

    [Fact]
    public void No_catch_up_for_disabled_plans_or_when_the_log_cannot_be_read()
    {
        StartWith(
            id => id == "broken" ? throw new IOException("log is locked") : Utc(9, 20, 10),
            new ScheduledPlan("off", false, [DailyAt("12:00")]),
            Plan("broken", DailyAt("12:00")),
            Plan("fine", DailyAt("12:00")));

        Minutes(5);

        _enqueued.Should().Equal(("fine", RunTrigger.CatchUp));
    }

    [Fact]
    public void A_paused_scheduler_drops_the_catch_up_runs()
    {
        StartWith(_ => Utc(9, 20, 10), Plan("p1", DailyAt("12:00")));
        _scheduler.IsPaused = true;

        Minutes(5);
        _scheduler.IsPaused = false;
        Minutes(5);

        _enqueued.Should().BeEmpty();
    }

    [Fact]
    public void A_plan_added_later_is_not_run_for_the_past()
    {
        StartWith();
        Minutes(3);   // 02:01:30

        _scheduler.UpdatePlans([Plan("p2", DailyAt("02:00"))]);
        Minutes(5);
        _enqueued.Should().BeEmpty();

        _time.Advance(TimeSpan.FromDays(1));
        _enqueued.Should().Equal(("p2", RunTrigger.Scheduled));
    }

    [Fact]
    public void A_removed_plan_is_no_longer_run()
    {
        StartWith(Plan("p1", DailyAt("02:00")));

        _scheduler.UpdatePlans([]);
        Minutes(5);

        _enqueued.Should().BeEmpty();
        _scheduler.NextRunUtc("p1").Should().BeNull();
    }

    [Fact]
    public void NextRunUtc_is_the_next_trigger_time()
    {
        StartWith(Plan("p1", DailyAt("02:00")), Plan("none"));

        _scheduler.NextRunUtc("p1").Should().Be(Utc(9, 30, 2));
        Minutes(3);
        _scheduler.NextRunUtc("p1").Should().Be(Utc(10, 1, 2));
        _scheduler.NextRunUtc("none").Should().BeNull("a plan without triggers only runs by hand");
        _scheduler.NextRunUtc("unknown").Should().BeNull();
    }

    [Fact]
    public void A_plan_with_an_invalid_trigger_does_not_disturb_the_others()
    {
        StartWith(Plan("bad", new ScheduleTrigger { Type = TriggerType.Weekly, Time = "02:00" }), Plan("good", DailyAt("02:00")));

        Minutes(3);

        _enqueued.Should().Equal(("good", RunTrigger.Scheduled));
        _scheduler.NextRunUtc("bad").Should().BeNull();
    }

    [Fact]
    public void A_failing_enqueue_does_not_stop_the_scheduler()
    {
        var calls = new List<string>();
        using var scheduler = new Scheduler((planId, _) =>
        {
            calls.Add(planId);
            throw new InvalidOperationException("queue is gone");
        }, _time);
        scheduler.UpdatePlans([Plan("p1", DailyAt("02:00")), Plan("p2", DailyAt("02:00"))]);
        scheduler.Start(_ => null);

        Minutes(3);
        _time.Advance(TimeSpan.FromDays(1));

        calls.Should().Equal("p1", "p2", "p1", "p2");
    }

    [Fact]
    public void Nothing_is_queued_after_Dispose()
    {
        StartWith(Plan("p1", DailyAt("02:00")));

        _scheduler.Dispose();
        Minutes(5);

        _enqueued.Should().BeEmpty();
    }

    [Fact]
    public void Changed_is_raised_after_every_check()
    {
        var changes = 0;
        _scheduler.Changed += () => changes++;
        StartWith(Plan("p1", DailyAt("02:00")));
        var afterStart = changes;

        Minutes(3);

        (changes - afterStart).Should().Be(3);
    }

    [Fact]
    public void Start_can_only_be_called_once()
    {
        StartWith(Plan("p1", DailyAt("02:00")));

        var again = () => _scheduler.Start(_ => null);

        again.Should().Throw<InvalidOperationException>();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~SchedulerTests"`
Expected: FAIL — compile errors, `Scheduler` does not exist.

- [ ] **Step 3: Write the scheduler**

Create `src/ReBackup.Core/Schedule/Scheduler.cs`:

```csharp
using ReBackup.Core.Backup;

namespace ReBackup.Core.Schedule;

/// <summary>What the scheduler needs to know about a saved plan.</summary>
public sealed record ScheduledPlan(string Id, bool Enabled, IReadOnlyList<ScheduleTrigger> Triggers);

/// <summary>
/// Starts scheduled and catch-up runs while the app runs, by checking the triggers once a minute. It does not run
/// backups: it hands the plan id and the kind of run to the enqueue delegate, which must not block.
/// </summary>
public sealed class Scheduler : IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CatchUpDelay = TimeSpan.FromMinutes(1);

    /// <summary>Checks run this long after a full minute, so that a trigger on that minute is never seen a moment too early.</summary>
    private static readonly TimeSpan CheckOffset = TimeSpan.FromSeconds(1);

    private readonly Action<string, RunTrigger> _enqueue;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, ScheduledPlan> _plans = new(StringComparer.OrdinalIgnoreCase);

    // Per plan: the instant up to which its triggers have been handled.
    private readonly Dictionary<string, DateTime> _checkedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _catchUps = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _catchUpAtUtc;
    private ITimer? _timer;
    private bool _paused;
    private bool _stopped;

    public Scheduler(Action<string, RunTrigger> enqueue, TimeProvider? timeProvider = null)
    {
        _enqueue = enqueue;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised after every check and whenever the plans or the pause state change; on the thread that caused it.</summary>
    public event Action? Changed;

    /// <summary>A paused scheduler queues nothing; triggers that pass meanwhile are skipped. Not saved.</summary>
    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
        set
        {
            lock (_gate)
            {
                if (_paused == value)
                    return;
                _paused = value;
                var now = UtcNow;
                foreach (var id in _plans.Keys)
                    _checkedUntil[id] = now;
            }
            RaiseChanged();
        }
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>Replaces the known plans (their saved state). A plan seen for the first time is only run for triggers from now on.</summary>
    public void UpdatePlans(IEnumerable<ScheduledPlan> plans)
    {
        var list = plans.ToList();
        lock (_gate)
        {
            var now = UtcNow;
            _plans.Clear();
            foreach (var plan in list)
            {
                _plans[plan.Id] = plan;
                _checkedUntil.TryAdd(plan.Id, now);
            }
            foreach (var id in _checkedUntil.Keys.Where(id => !_plans.ContainsKey(id)).ToList())
                _checkedUntil.Remove(id);
            _catchUps.RemoveWhere(id => !_plans.ContainsKey(id));
        }
        RaiseChanged();
    }

    /// <summary>
    /// Plans one catch-up run, <see cref="CatchUpDelay"/> from now, for every enabled plan that missed a trigger
    /// since the start of its last run, and starts the checks.
    /// </summary>
    /// <param name="lastRunStartUtc">Start of a plan's last logged run; null when it never ran (it is then not caught up).</param>
    /// <exception cref="InvalidOperationException">The scheduler was started or disposed before.</exception>
    public void Start(Func<string, DateTime?> lastRunStartUtc)
    {
        List<ScheduledPlan> plans;
        lock (_gate)
        {
            if (_timer is not null || _stopped)
                throw new InvalidOperationException("The scheduler can be started only once.");
            plans = [.. _plans.Values];
        }

        var now = UtcNow;
        var missed = new List<string>();
        foreach (var plan in plans.Where(p => p.Enabled && p.Triggers.Count > 0))
        {
            DateTime? lastRun;
            try
            {
                lastRun = lastRunStartUtc(plan.Id);
            }
            catch (Exception)
            {
                continue;   // an unreadable log: no catch-up rather than a guess
            }
            if (lastRun is { } since && LastDue(plan, since, now) is not null)
                missed.Add(plan.Id);
        }

        lock (_gate)
        {
            foreach (var id in missed)
                _catchUps.Add(id);
            _catchUpAtUtc = now + CatchUpDelay;
            foreach (var id in _plans.Keys)
                _checkedUntil[id] = now;
            var nextMinute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(1);
            _timer = _time.CreateTimer(_ => Check(), null, nextMinute + CheckOffset - now, CheckInterval);
        }
        RaiseChanged();
    }

    /// <summary>
    /// When the plan's triggers start it next; null when it is unknown, disabled, has no (valid) triggers. The value
    /// does not take the pause into account.
    /// </summary>
    public DateTime? NextRunUtc(string planId)
    {
        ScheduledPlan? plan;
        DateTime after;
        lock (_gate)
        {
            if (!_plans.TryGetValue(planId, out plan) || !plan.Enabled || plan.Triggers.Count == 0)
                return null;
            after = _checkedUntil.TryGetValue(planId, out var checkedUntil) ? checkedUntil : UtcNow;
        }

        try
        {
            foreach (var run in ScheduleCalculator.NextRuns(plan.Triggers, after, _time.LocalTimeZone))
                return run;
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        ITimer? timer;
        lock (_gate)
        {
            _stopped = true;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
    }

    private void Check()
    {
        var due = new List<(string PlanId, RunTrigger Trigger)>();
        lock (_gate)
        {
            if (_stopped)
                return;
            var now = UtcNow;

            if (_catchUps.Count > 0 && now >= _catchUpAtUtc)
            {
                if (!_paused)
                {
                    foreach (var id in _catchUps)
                    {
                        if (_plans.TryGetValue(id, out var plan) && plan.Enabled)
                            due.Add((id, RunTrigger.CatchUp));
                    }
                }
                _catchUps.Clear();
            }

            foreach (var plan in _plans.Values)
            {
                var since = _checkedUntil.TryGetValue(plan.Id, out var checkedUntil) ? checkedUntil : now;
                _checkedUntil[plan.Id] = now;
                if (_paused || !plan.Enabled || due.Exists(d => string.Equals(d.PlanId, plan.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (LastDue(plan, since, now) is not null)
                    due.Add((plan.Id, RunTrigger.Scheduled));
            }
        }

        foreach (var (planId, trigger) in due)
        {
            try
            {
                _enqueue(planId, trigger);
            }
            catch (Exception)
            {
                // The next plan must still be started.
            }
        }
        RaiseChanged();
    }

    private DateTime? LastDue(ScheduledPlan plan, DateTime sinceUtc, DateTime nowUtc)
    {
        if (plan.Triggers.Count == 0)
            return null;
        try
        {
            return ScheduleCalculator.LastDue(plan.Triggers, sinceUtc, nowUtc, _time.LocalTimeZone);
        }
        catch (ArgumentException)
        {
            return null;   // a plan with a broken trigger (edited by hand) is never started by the schedule
        }
    }

    private void RaiseChanged()
    {
        if (Changed is null)
            return;
        foreach (var handler in Changed.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception)
            {
                // A failing listener must not stop the scheduler.
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~SchedulerTests"`
Expected: PASS (16 tests).

- [ ] **Step 5: Full build and test run**

Run: `dotnet build --no-incremental` — expected: 0 warnings, 0 errors.
Run: `dotnet test tests/ReBackup.Core.Tests` — expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): scheduler with catch-up runs and pause" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 5: Schedule tab

**Files:**
- Create: `src/ReBackup.App/ViewModels/TriggerRowViewModel.cs`
- Create: `src/ReBackup.App/Views/ScheduleView.xaml`, `src/ReBackup.App/Views/ScheduleView.xaml.cs`
- Modify: `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`, `src/ReBackup.App/MainWindow.xaml`

**Interfaces:**
- Consumes: `ScheduleTrigger`, `TriggerType`, `ScheduleTriggers.Validate/ShortDayNames/WeekdaysOf` (Task 1); `ScheduleCalculator.LocalRunTimes` (Task 2); `BackupPlan.Triggers`.
- Produces:
  - `WeekdayChoice` (`Name`, `IsChecked`), `TriggerRowViewModel` (`Type`, `TimeText`, `DayText`, `EveryHoursText`, `FromText`, `ToText`, `Weekdays`, `Error`, static `Types`, event `Changed`, `ScheduleTrigger ToTrigger()`)
  - `PlanEditorViewModel.TriggerRows`, `AddTriggerCommand`, `RemoveTriggerCommand` (parameter: the row), `NextRuns` (`IReadOnlyList<string>`), `NextRunsNote`, `void RefreshNextRuns()`, private `OnTriggersEdited()` (Task 7 adds a line to it); `ToPlan()` carries the edited triggers

- [ ] **Step 1: Trigger row view model**

Create `src/ReBackup.App/ViewModels/TriggerRowViewModel.cs`:

```csharp
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Schedule;

namespace ReBackup.App.ViewModels;

/// <summary>A weekday check box of a weekly trigger.</summary>
public sealed partial class WeekdayChoice : ObservableObject
{
    [ObservableProperty] private bool _isChecked;

    public WeekdayChoice(string name, DayOfWeek day)
    {
        Name = name;
        Day = day;
    }

    public string Name { get; }
    public DayOfWeek Day { get; }
}

/// <summary>One trigger of a plan while it is edited. Fields of other trigger types keep their values while hidden.</summary>
public sealed partial class TriggerRowViewModel : ObservableObject
{
    private readonly bool _ready;

    [ObservableProperty] private TriggerType _type;
    [ObservableProperty] private string _timeText = "02:00";
    [ObservableProperty] private string _dayText = "1";
    [ObservableProperty] private string _everyHoursText = "4";
    [ObservableProperty] private string _fromText = "08:00";
    [ObservableProperty] private string _toText = "20:00";
    [ObservableProperty] private string? _error;

    public TriggerRowViewModel(ScheduleTrigger trigger)
    {
        // Mon … Sun; DayOfWeek counts from Sunday.
        Weekdays = ScheduleTriggers.ShortDayNames
            .Select((name, index) => new WeekdayChoice(name, (DayOfWeek)((index + 1) % 7)))
            .ToList();

        Type = trigger.Type;
        if (trigger.Time is not null)
            TimeText = trigger.Time;
        var chosen = ScheduleTriggers.WeekdaysOf(trigger);
        foreach (var choice in Weekdays)
        {
            choice.IsChecked = chosen.Contains(choice.Day);
            choice.PropertyChanged += (_, _) => OnEdited();
        }
        if (trigger.Day is { } day)
            DayText = day.ToString(CultureInfo.InvariantCulture);
        if (trigger.EveryHours is { } hours)
            EveryHoursText = hours.ToString(CultureInfo.InvariantCulture);
        if (trigger.Type == TriggerType.Interval)
        {
            // Empty bounds mean the whole day; showing the defaults of a new row would change the trigger.
            FromText = trigger.From ?? "";
            ToText = trigger.To ?? "";
        }

        _ready = true;
        Error = ScheduleTriggers.Validate(ToTrigger());
    }

    /// <summary>Raised after every edit of this trigger.</summary>
    public event Action? Changed;

    public static IReadOnlyList<TriggerType> Types { get; } = Enum.GetValues<TriggerType>();

    public IReadOnlyList<WeekdayChoice> Weekdays { get; }

    partial void OnTypeChanged(TriggerType value) => OnEdited();
    partial void OnTimeTextChanged(string value) => OnEdited();
    partial void OnDayTextChanged(string value) => OnEdited();
    partial void OnEveryHoursTextChanged(string value) => OnEdited();
    partial void OnFromTextChanged(string value) => OnEdited();
    partial void OnToTextChanged(string value) => OnEdited();

    /// <summary>The trigger as edited, with only the fields its type uses.</summary>
    public ScheduleTrigger ToTrigger() => Type switch
    {
        TriggerType.Daily => new ScheduleTrigger { Type = Type, Time = Trimmed(TimeText) },
        TriggerType.Weekly => new ScheduleTrigger
        {
            Type = Type,
            Time = Trimmed(TimeText),
            Days = Weekdays.Where(w => w.IsChecked).Select(w => w.Name).ToList(),
        },
        TriggerType.Monthly => new ScheduleTrigger { Type = Type, Time = Trimmed(TimeText), Day = ParseNumber(DayText, NumberStyles.AllowLeadingSign) },
        _ => new ScheduleTrigger
        {
            Type = Type,
            EveryHours = ParseNumber(EveryHoursText, NumberStyles.None),
            From = Trimmed(FromText),
            To = Trimmed(ToText),
        },
    };

    private void OnEdited()
    {
        if (!_ready)
            return;
        Error = ScheduleTriggers.Validate(ToTrigger());
        Changed?.Invoke();
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static int? ParseNumber(string? text, NumberStyles styles) =>
        int.TryParse(text?.Trim(), styles, CultureInfo.InvariantCulture, out var value) ? value : null;
}
```

- [ ] **Step 2: Triggers in the plan editor**

In `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`:

1. Add `using System.Globalization;` and `using ReBackup.Core.Schedule;`.
2. Add these fields next to the other `[ObservableProperty]` fields:

```csharp
    [ObservableProperty] private IReadOnlyList<string> _nextRuns = [];
    [ObservableProperty] private string _nextRunsNote = "";
```

3. Below the property `HasNoRetentionRules`, add:

```csharp

    /// <summary>The triggers as edited.</summary>
    public ObservableCollection<TriggerRowViewModel> TriggerRows { get; } = [];
```

4. Replace `partial void OnEnabledChanged(bool value) => Touch();` with:

```csharp
    partial void OnEnabledChanged(bool value)
    {
        RefreshNextRuns();
        Touch();
    }
```

5. In `ToPlan()`, directly before `return plan;`, add `plan.Triggers = TriggerRows.Select(row => row.ToTrigger()).ToList();`.

6. Directly above `public void Validate()`, add:

```csharp
    [RelayCommand]
    private void AddTrigger()
    {
        AddTriggerRow(new ScheduleTrigger { Type = TriggerType.Daily, Time = "02:00" });
        OnTriggersEdited();
    }

    [RelayCommand]
    private void RemoveTrigger(TriggerRowViewModel? row)
    {
        if (row is null || !TriggerRows.Remove(row))
            return;
        row.Changed -= OnTriggersEdited;
        OnTriggersEdited();
    }

    private void AddTriggerRow(ScheduleTrigger trigger)
    {
        var row = new TriggerRowViewModel(trigger);
        row.Changed += OnTriggersEdited;
        TriggerRows.Add(row);
    }

    private void OnTriggersEdited()
    {
        RefreshNextRuns();
        Touch();
    }

    /// <summary>Recomputes the next five runs of the edited triggers from the current time.</summary>
    public void RefreshNextRuns()
    {
        var triggers = TriggerRows.Select(row => row.ToTrigger()).ToList();
        if (triggers.Count == 0)
        {
            NextRuns = [];
            NextRunsNote = "No triggers: this plan runs only when started by hand.";
            return;
        }
        if (triggers.Any(t => ScheduleTriggers.Validate(t) is not null))
        {
            NextRuns = [];
            NextRunsNote = "Correct the triggers above to see the next runs.";
            return;
        }

        NextRuns = ScheduleCalculator.LocalRunTimes(triggers, DateTime.UtcNow, TimeZoneInfo.Local)
            .Take(5)
            .Select(time => time.ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture))
            .ToList();
        NextRunsNote = Enabled
            ? "Runs happen only while ReBackup is running (it keeps running in the tray when the window is closed)."
            : "The plan is disabled: it runs only when started by hand until it is enabled again.";
    }
```

7. In `LoadFrom`, inside the `try` block directly after the loop that adds the retention rows, add:

```csharp

            foreach (var row in TriggerRows)
                row.Changed -= OnTriggersEdited;
            TriggerRows.Clear();
            foreach (var trigger in plan.Triggers)
                AddTriggerRow(trigger);
```

   and directly after the line `OnPropertyChanged(nameof(HasNoRetentionRules));` at the end of `LoadFrom`, add `RefreshNextRuns();`.

- [ ] **Step 3: The view**

Create `src/ReBackup.App/Views/ScheduleView.xaml`:

```xml
<UserControl x:Class="ReBackup.App.Views.ScheduleView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:ReBackup.App.ViewModels">
    <Grid Margin="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <DockPanel Grid.Row="0" Margin="0,0,0,6">
            <Button DockPanel.Dock="Right" Content="Add trigger" Padding="10,2" Command="{Binding AddTriggerCommand}" />
            <TextBlock FontWeight="SemiBold" VerticalAlignment="Center" TextWrapping="Wrap"
                       Text="Triggers — the plan runs at each of these local times" />
        </DockPanel>

        <ScrollViewer Grid.Row="1" MaxHeight="260" VerticalScrollBarVisibility="Auto">
            <ItemsControl ItemsSource="{Binding TriggerRows}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <StackPanel Margin="0,0,0,6">
                            <StackPanel Orientation="Horizontal">
                                <ComboBox Width="90" ItemsSource="{x:Static vm:TriggerRowViewModel.Types}"
                                          SelectedItem="{Binding Type}" />

                                <StackPanel Orientation="Horizontal" Margin="8,0,0,0">
                                    <StackPanel.Style>
                                        <Style TargetType="StackPanel">
                                            <Style.Triggers>
                                                <DataTrigger Binding="{Binding Type}" Value="Interval">
                                                    <Setter Property="Visibility" Value="Collapsed" />
                                                </DataTrigger>
                                            </Style.Triggers>
                                        </Style>
                                    </StackPanel.Style>
                                    <TextBlock Text="at" VerticalAlignment="Center" Margin="0,0,6,0" />
                                    <TextBox Width="55" Text="{Binding TimeText, UpdateSourceTrigger=PropertyChanged}" />
                                </StackPanel>

                                <StackPanel Orientation="Horizontal" Margin="8,0,0,0">
                                    <StackPanel.Style>
                                        <Style TargetType="StackPanel">
                                            <Setter Property="Visibility" Value="Collapsed" />
                                            <Style.Triggers>
                                                <DataTrigger Binding="{Binding Type}" Value="Monthly">
                                                    <Setter Property="Visibility" Value="Visible" />
                                                </DataTrigger>
                                            </Style.Triggers>
                                        </Style>
                                    </StackPanel.Style>
                                    <TextBlock Text="on day" VerticalAlignment="Center" Margin="0,0,6,0" />
                                    <TextBox Width="40" Text="{Binding DayText, UpdateSourceTrigger=PropertyChanged}" />
                                    <TextBlock Text="(0 = last day, -1 = the day before)" Foreground="Gray"
                                               VerticalAlignment="Center" Margin="6,0,0,0" />
                                </StackPanel>

                                <StackPanel Orientation="Horizontal" Margin="8,0,0,0">
                                    <StackPanel.Style>
                                        <Style TargetType="StackPanel">
                                            <Setter Property="Visibility" Value="Collapsed" />
                                            <Style.Triggers>
                                                <DataTrigger Binding="{Binding Type}" Value="Interval">
                                                    <Setter Property="Visibility" Value="Visible" />
                                                </DataTrigger>
                                            </Style.Triggers>
                                        </Style>
                                    </StackPanel.Style>
                                    <TextBlock Text="every" VerticalAlignment="Center" Margin="0,0,6,0" />
                                    <TextBox Width="35" Text="{Binding EveryHoursText, UpdateSourceTrigger=PropertyChanged}" />
                                    <TextBlock Text="hours from" VerticalAlignment="Center" Margin="6,0,6,0" />
                                    <TextBox Width="55" Text="{Binding FromText, UpdateSourceTrigger=PropertyChanged}" />
                                    <TextBlock Text="to" VerticalAlignment="Center" Margin="6,0,6,0" />
                                    <TextBox Width="55" Text="{Binding ToText, UpdateSourceTrigger=PropertyChanged}" />
                                    <TextBlock Text="(empty = whole day)" Foreground="Gray" VerticalAlignment="Center" Margin="6,0,0,0" />
                                </StackPanel>

                                <Button Content="Remove" Padding="8,1" Margin="8,0,0,0"
                                        Command="{Binding DataContext.RemoveTriggerCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                                        CommandParameter="{Binding}" />
                            </StackPanel>

                            <ItemsControl Margin="98,4,0,0" ItemsSource="{Binding Weekdays}">
                                <ItemsControl.Style>
                                    <Style TargetType="ItemsControl">
                                        <Setter Property="Visibility" Value="Collapsed" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding Type}" Value="Weekly">
                                                <Setter Property="Visibility" Value="Visible" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </ItemsControl.Style>
                                <ItemsControl.ItemsPanel>
                                    <ItemsPanelTemplate>
                                        <StackPanel Orientation="Horizontal" />
                                    </ItemsPanelTemplate>
                                </ItemsControl.ItemsPanel>
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate>
                                        <CheckBox Content="{Binding Name}" IsChecked="{Binding IsChecked}" Margin="0,0,10,0" />
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>

                            <TextBlock Foreground="Firebrick" Text="{Binding Error}">
                                <TextBlock.Style>
                                    <Style TargetType="TextBlock">
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding Error}" Value="{x:Null}">
                                                <Setter Property="Visibility" Value="Collapsed" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </TextBlock.Style>
                            </TextBlock>
                        </StackPanel>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>

        <StackPanel Grid.Row="2" Margin="0,12,0,0">
            <TextBlock FontWeight="SemiBold" Text="Next 5 runs" />
            <ItemsControl Margin="0,4,0,0" ItemsSource="{Binding NextRuns}" />
            <TextBlock Margin="0,6,0,0" Foreground="Gray" TextWrapping="Wrap" Text="{Binding NextRunsNote}" />
        </StackPanel>
    </Grid>
</UserControl>
```

Create `src/ReBackup.App/Views/ScheduleView.xaml.cs`:

```csharp
using System.Windows.Controls;
using ReBackup.App.ViewModels;

namespace ReBackup.App.Views;

public partial class ScheduleView : UserControl
{
    public ScheduleView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => RefreshNextRuns();
        DataContextChanged += (_, _) => RefreshNextRuns();
    }

    /// <summary>The next runs are relative to now: recompute them whenever the tab is shown.</summary>
    private void RefreshNextRuns()
    {
        if (IsVisible && DataContext is PlanEditorViewModel editor)
            editor.RefreshNextRuns();
    }
}
```

In `src/ReBackup.App/MainWindow.xaml`, add this tab directly after the "General" `TabItem` (spec order: General, Schedule, Ignore & Preview, Retention, History):

```xml
                    <TabItem Header="Schedule">
                        <views:ScheduleView />
                    </TabItem>
```

- [ ] **Step 4: Build, test, smoke-test**

Run: `dotnet build --no-incremental` (0 warnings, 0 errors), `dotnet test tests/ReBackup.Core.Tests` (all pass), and the startup smoke test.

Manual checklist (for the user; do not click through it as an agent):
1. The Schedule tab of a plan without triggers shows "No triggers: this plan runs only when started by hand." **Add trigger** adds "Daily at 02:00", marks the plan as changed (•) and lists the next five nights at 02:00.
2. Weekly shows seven day boxes; ticking Mon and Wed lists only Mondays and Wednesdays. With no day ticked the row shows "choose at least one weekday." and Save is refused ("Trigger 1: …").
3. Monthly with day 0 lists the last day of each month; Interval every 4 hours from 08:00 to 20:00 lists 08:00, 12:00, 16:00, 20:00, 08:00.
4. Switching a row between types keeps the values of the other types (switching back shows them again).
5. After Save and a restart of the app the triggers are shown again; the plan file has a `triggers` array in the spec format (only the fields of each type).
6. Unticking **Enabled** on the General tab changes the note under the next runs.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): schedule tab with trigger editor and next runs" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 6: Scheduler in the app

**Files:**
- Modify: `src/ReBackup.App/ViewModels/MainViewModel.cs`, `src/ReBackup.App/ViewModels/PlanRunViewModel.cs`
- Modify: `src/ReBackup.App/App.xaml.cs`, `src/ReBackup.App/MainWindow.xaml`

**Interfaces:**
- Consumes: `Scheduler`, `ScheduledPlan` (Task 4); `RunLog.ReadLast`, `BackupQueue.Close` (Task 3); `PlanEditorViewModel.SavedPlan()`, `IsNew`, `Id`; `BackupRequest`, `RunTrigger`.
- Produces: `MainViewModel(PlanStore, ConfigPaths, AppSettings, IDialogService, Action openSettings, BackupQueue queue, Scheduler scheduler, Action<Action> runOnUi)`; `MainViewModel.RunScheduled(string planId, RunTrigger trigger)`; `MainViewModel.RefreshSchedule()`; `MainViewModel.SchedulerStatus`; `PlanRunViewModel.NextRunText`.

Behaviour: the scheduler gets the saved state of every saved plan whenever plans are loaded, reloaded, saved or deleted. It calls back on a timer thread; the App marshals the call to the UI thread with `Dispatcher.InvokeAsync` (never `Invoke`: the UI thread may be waiting for the queue during shutdown), where `RunScheduled` queues the saved plan (unsaved edits do not matter). The scheduler is started after the configuration is loaded, with the start of each plan's last logged run from `RunLog.ReadLast`. On exit, restart and Windows logoff, the scheduler is disposed before the queue is closed.

- [ ] **Step 1: Main view model**

In `src/ReBackup.App/ViewModels/PlanRunViewModel.cs`, add next to the other fields:

```csharp
    [ObservableProperty] private string _nextRunText = "";
```

In `src/ReBackup.App/ViewModels/MainViewModel.cs`:

1. Add `using System.Globalization;` and `using ReBackup.Core.Schedule;`.
2. Add the field `private readonly Scheduler _scheduler;` below `_queue`, and next to `_queueStatus`:

```csharp
    [ObservableProperty] private string _schedulerStatus = "";
```

3. Change the constructor signature to

```csharp
    public MainViewModel(PlanStore store, ConfigPaths paths, AppSettings settings, IDialogService dialogs,
        Action openSettings, BackupQueue queue, Scheduler scheduler, Action<Action> runOnUi)
```

   assign `_scheduler = scheduler;` next to `_queue = queue;`, and add as the last two statements of the constructor:

```csharp
        PublishPlans();
        RefreshSchedule();
```

4. At the end of `ReloadFromDisk` (after the `StatusMessage = …` line), add `PublishPlans();`.
5. In `Save`, change the `try` block to

```csharp
            var saved = editor.TrySave(_store);
            StatusMessage = saved
                ? $"Saved \"{editor.Name}\"."
                : "Not saved: fix the errors shown in the plan.";
            RevalidateAll();
            if (saved)
                PublishPlans();
```

6. At the end of `DeletePlan` (after the line that sets `SelectedPlan`), add `PublishPlans();`.
7. Add these members (for example below `RunPlan`):

```csharp
    /// <summary>Queues a run the scheduler started. It uses the plan as saved; unsaved edits do not matter.</summary>
    public void RunScheduled(string planId, RunTrigger trigger)
    {
        var editor = Plans.FirstOrDefault(p => p.Id.Equals(planId, StringComparison.OrdinalIgnoreCase));
        if (editor is null || editor.IsNew)
            return;
        var plan = editor.SavedPlan();
        if (!plan.Enabled)
            return;   // disabled after the scheduler decided
        _queue.Enqueue(new BackupRequest(plan, _settings.DefaultIgnorePatterns.ToList(), trigger));
    }

    /// <summary>Updates the "next run" texts and the scheduler state. Called whenever the scheduler reports a change.</summary>
    public void RefreshSchedule()
    {
        var paused = _scheduler.IsPaused;
        SchedulerStatus = paused ? "Scheduler paused" : "";
        foreach (var editor in Plans)
            editor.Run.NextRunText = NextRunText(editor, paused);
    }

    private string NextRunText(PlanEditorViewModel editor, bool paused)
    {
        if (editor.IsNew)
            return "";
        var plan = editor.SavedPlan();
        if (!plan.Enabled)
            return "Disabled: runs only by hand";
        if (plan.Triggers.Count == 0)
            return "No schedule";
        if (paused)
            return "Scheduler paused";
        return _scheduler.NextRunUtc(editor.Id) is { } next
            ? "Next run " + next.ToLocalTime().ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : "No schedule";
    }

    /// <summary>Hands the saved state of all saved plans to the scheduler.</summary>
    private void PublishPlans() =>
        _scheduler.UpdatePlans(Plans
            .Where(p => !p.IsNew)
            .Select(p => p.SavedPlan())
            .Select(plan => new ScheduledPlan(plan.Id, plan.Enabled, plan.Triggers))
            .ToList());
```

- [ ] **Step 2: Main window**

In `src/ReBackup.App/MainWindow.xaml`:

1. In the plan list's item template, directly below the `TextBlock` bound to `Run.LastRunText`, add:

```xml
                                    <TextBlock Text="{Binding Run.NextRunText}" Foreground="Gray" FontSize="11"
                                               TextTrimming="CharacterEllipsis" />
```

2. In the `StatusBar`, directly after the `StatusBarItem` that shows `QueueStatus`, add:

```xml
            <StatusBarItem DockPanel.Dock="Right">
                <TextBlock Text="{Binding SchedulerStatus}" Foreground="DarkOrange" />
            </StatusBarItem>
```

- [ ] **Step 3: App**

In `src/ReBackup.App/App.xaml.cs`:

1. Add `using ReBackup.Core.Schedule;` and the field `private Scheduler _scheduler = null!;` below `_queue`.

2. In `LoadConfiguration`, replace the block from `var runner = …` up to and including `var mainViewModel = new MainViewModel(…);` with:

```csharp
            // Versions are deleted by the rules as saved at that moment, not as they were when the run was queued.
            var runner = new BackupRunner(currentRules: planId => planStore.TryLoad(planId)?.Retention);
            var queue = new BackupQueue(runner, planId => new RunLog(paths.LogFileFor(planId)));
            MainViewModel? created = null;
            // Called on a timer thread: InvokeAsync, never Invoke — the UI thread may be waiting for the queue.
            var scheduler = new Scheduler((planId, trigger) =>
                Dispatcher.InvokeAsync(() => created?.RunScheduled(planId, trigger)));
            var mainViewModel = new MainViewModel(planStore, paths, settings, _dialogs, ShowSettings, queue, scheduler,
                action => Dispatcher.InvokeAsync(action));
            created = mainViewModel;
            scheduler.Changed += () => Dispatcher.InvokeAsync(mainViewModel.RefreshSchedule);
```

   then add `_scheduler = scheduler;` next to `_queue = queue;`.

3. In `OnStartup`, directly after `CreateTrayIcon();`, add:

```csharp
        _scheduler.Start(planId => new RunLog(_paths.LogFileFor(planId)).ReadLast()?.StartUtc);
```

4. In `CreateTrayIcon`, directly after `_trayMenu.Items.Add(runMenu);`, add:

```csharp
        var pauseItem = CreateTrayMenuItem("Pause scheduler", () => _scheduler.IsPaused = !_scheduler.IsPaused);
        _trayMenu.Items.Add(pauseItem);
        _trayMenu.Opened += (_, _) => pauseItem.Header = _scheduler.IsPaused ? "Resume scheduler" : "Pause scheduler";
```

   and in the `_mainViewModel.PropertyChanged` handler of the tooltip, change the condition and the text to:

```csharp
            if (e.PropertyName is not (nameof(MainViewModel.QueueStatus) or nameof(MainViewModel.SchedulerStatus))
                || _exitRequested || _tray is null)
                return;
            try
            {
                var text = "ReBackup — " + _mainViewModel.QueueStatus +
                           (_mainViewModel.SchedulerStatus.Length > 0 ? " · " + _mainViewModel.SchedulerStatus : "");
                _tray.ToolTipText = text.Length > 120 ? text[..119] + "…" : text;
            }
```

5. Replace the method `StopBackups` with:

```csharp
    /// <summary>
    /// Stops the scheduler, closes the queue (canceling queued and running backups) and waits briefly for the running
    /// one to clean up its partial folder. False when it did not stop in time.
    /// </summary>
    private bool StopBackups(TimeSpan wait)
    {
        _scheduler?.Dispose();
        _queue.Close();
        try
        {
            return _queue.WhenIdleAsync().Wait(wait);
        }
        catch (AggregateException)
        {
            // The worker never faults; this only guards the wait itself.
            return false;
        }
    }
```

6. In `ExitApp`, add an `else` branch to the `if (_queue.IsBusy)` block so that the scheduler and the queue are stopped on every exit:

```csharp
        if (_queue.IsBusy)
        {
            // … unchanged: confirmation, then StopBackups(TimeSpan.FromSeconds(15));
        }
        else
        {
            StopBackups(TimeSpan.Zero);
        }
```

(`Restart` and `OnSessionEnding` already call `StopBackups`.)

- [ ] **Step 4: Build, test, smoke-test**

Run: `dotnet build --no-incremental` (0 warnings, 0 errors), `dotnet test tests/ReBackup.Core.Tests` (all pass), and the startup smoke test. After the smoke test, also confirm that exiting through the tray is not needed for the process to end cleanly when it is stopped from outside (no orphaned process).

Manual checklist (for the user; do not click through it as an agent — these start real backups):
1. A saved plan with an Interval trigger "every 1 hour" shows "Next run …" in the plan list; a plan without triggers shows "No schedule", a disabled one "Disabled: runs only by hand".
2. A trigger two minutes ahead: the backup starts by itself within a minute of that time; History lists it with trigger "Scheduled".
3. Close ReBackup, wait until a trigger time has passed, start it again: about one to two minutes after startup one backup starts with trigger "Catch-up" (only one, even if several triggers were missed).
4. Tray menu → **Pause scheduler**: the status bar shows "Scheduler paused", the plan list says "Scheduler paused", and a trigger passing meanwhile does not start a backup. **Resume scheduler** brings back the next-run texts; the missed trigger is not made up for.
5. Unsaved edits of a plan are not part of a scheduled run (the History row's result matches the saved settings).
6. Exiting during a scheduled run asks for confirmation as before; after exit no ReBackup process remains.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): scheduler with catch-up runs, next run in the plan list, pause in the tray" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 7: Retention preview from the plan's triggers

**Files:**
- Modify: `src/ReBackup.App/ViewModels/RetentionPreviewViewModel.cs`, `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`, `src/ReBackup.App/Views/RetentionView.xaml`

**Interfaces:**
- Consumes: `ScheduleCalculator.LocalRunTimes`, `ScheduleTriggers.Validate` (Tasks 1–2); `RetentionPreviewViewModel.Evaluate/SimulateAsync/ShowSimulation` (Phase 4); `PlanEditorViewModel.OnTriggersEdited` (Task 5).
- Produces: `RetentionPreviewViewModel.UsesPlanSchedule` (bool). The assumed frequency is used only for plans without triggers.

- [ ] **Step 1: View model**

In `src/ReBackup.App/ViewModels/RetentionPreviewViewModel.cs`:

1. Add `using ReBackup.Core.Schedule;`.
2. Change the summary of `AssumedSchedule` to `/// <summary>A backup frequency assumed for the full-extension preview of a plan without triggers.</summary>`.
3. Add next to the other `[ObservableProperty]` fields: `[ObservableProperty] private bool _usesPlanSchedule;`
4. In `Evaluate`, replace `var rules = _plan().Retention;` with

```csharp
        var plan = _plan();
        var rules = plan.Retention;
        UsesPlanSchedule = plan.Triggers.Count > 0;
```

   and replace the last statement `_ = SimulateAsync(versions, rules);` with `_ = SimulateAsync(versions, rules, plan.Triggers);`.

5. Change the signature of `SimulateAsync` to

```csharp
    private async Task SimulateAsync(IReadOnlyList<VersionInfo> versions, IReadOnlyList<RetentionRule> rules,
        IReadOnlyList<ScheduleTrigger> triggers)
```

   and inside its `try`, replace the lines from `var schedule = SelectedSchedule;` down to and including the `if (ReferenceEquals(_simulateCts, cts)) ShowSimulation(…);` statement with:

```csharp
            var now = DateTime.Now;
            IEnumerable<DateTime> runs;
            string frequency;
            if (triggers.Count > 0)
            {
                if (triggers.Any(t => ScheduleTriggers.Validate(t) is not null))
                {
                    ClearSimulation();
                    FullSummary = "Correct the plan's triggers on the Schedule tab to see the full extension.";
                    return;
                }
                runs = ScheduleCalculator.LocalRunTimes(triggers, DateTime.UtcNow, TimeZoneInfo.Local);
                frequency = "the plan's schedule";
            }
            else
            {
                // The assumed backups run at 02:00 and then every interval.
                var schedule = SelectedSchedule;
                runs = RetentionSimulator.Every(now.Date.AddHours(2), schedule.Interval);
                frequency = schedule.Label;
            }

            var owned = versions.Where(v => v.IsOwned).ToList();
            var seeds = owned.Select(v => new RetentionVersion(v.Name, v.LocalTime)).ToList();
            var sizes = owned.Where(v => v.TotalBytes is not null).Select(v => v.TotalBytes!.Value).ToList();
            long? average = sizes.Count > 0 ? (long)sizes.Average() : _fallbackVersionBytes();

            var result = await Task.Run(() => RetentionSimulator.Simulate(seeds, rules, runs, now, average, cts.Token), cts.Token);
            if (ReferenceEquals(_simulateCts, cts))
                ShowSimulation(result, rules, frequency, now);
```

   (If the lines between differ slightly from this description, keep the intent: the same seeds, sizes and error handling as before; only the run times and the frequency label depend on the triggers.)

6. Change the signature of `ShowSimulation` to `private void ShowSimulation(SimulationResult result, IReadOnlyList<RetentionRule> rules, string frequency, DateTime now)` and in its two `FullSummary` texts replace `{schedule.Label}` with `{frequency}`.

In `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`, in `OnTriggersEdited`, add `RetentionPreview.RequestEvaluate();` directly above `Touch();`.

- [ ] **Step 2: View**

In `src/ReBackup.App/Views/RetentionView.xaml`, in the "Full extension" header `DockPanel` (Row 2), give the `ComboBox` and the "Assume" `TextBlock` this style each (for the `ComboBox` with `TargetType="ComboBox"`), so they are shown only for plans without triggers:

```xml
                    <TextBlock.Style>
                        <Style TargetType="TextBlock">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding RetentionPreview.UsesPlanSchedule}" Value="True">
                                    <Setter Property="Visibility" Value="Collapsed" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
```

and add, directly after the "Assume" `TextBlock` (also docked right), a `TextBlock` that is shown only when `RetentionPreview.UsesPlanSchedule` is True:

```xml
                <TextBlock DockPanel.Dock="Right" VerticalAlignment="Center" Foreground="Gray"
                           Text="using the plan's triggers">
                    <TextBlock.Style>
                        <Style TargetType="TextBlock">
                            <Setter Property="Visibility" Value="Collapsed" />
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding RetentionPreview.UsesPlanSchedule}" Value="True">
                                    <Setter Property="Visibility" Value="Visible" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>
```

- [ ] **Step 3: Build, test, smoke-test**

Run: `dotnet build --no-incremental` (0 warnings, 0 errors), `dotnet test tests/ReBackup.Core.Tests` (all pass), and the startup smoke test.

Manual checklist (for the user; do not click through it as an agent):
1. A plan with a Daily 02:00 trigger and the rules Daily 7, Weekly Sunday 4, Monthly 0 12: the Retention tab says "using the plan's triggers" instead of the "Assume" list and "With the plan's schedule the target holds up to 22 versions …".
2. Adding an Interval trigger "every 1 hour" on the Schedule tab changes the Retention summary after a short pause without saving.
3. A plan without triggers still shows the "Assume" list.
4. An invalid trigger makes the Retention summary say "Correct the plan's triggers on the Schedule tab …".

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(app): retention preview uses the plan's triggers" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

## Not part of this plan

- Version browser, comparison and restore (phase 6).
- Phase 3 UI items still open: status dot per plan row, current file and ETA, "starting at hh:mm".
- Phase 4 later items (see the carry-forward of the Phase 4 plan).

---

## Carry-forward from Phase 5 execution (final review triage)

Changed against the plan text above during reviews (the code is the reference now):
- The scheduler uses a one-shot timer that is re-armed after every check for the next hh:mm:01, so checks realign after sleep or clock corrections. Checks and pausing share one method that collects due runs.
- Pausing first queues what is already due (triggers and a due catch-up), then pauses. `_checkedUntil` never moves backwards (checks, pause, resume, start). A plan re-published with changed triggers or enabled flag starts from now; an unchanged one keeps its position. A scheduled run removes a pending catch-up of the same plan.
- Scheduler callbacks cannot crash the timer thread (delegate snapshot, try/catch around each check); `RefreshSchedule` and `RunScheduled` on the UI thread catch their own errors.
- Time-zone changes: `SystemEvents.TimeChanged` clears the cached zone and refreshes the schedule texts.
- Plan files with a `null` entry in `triggers` or `retention` are reported as load errors instead of crashing the app.
- An existing trigger with a missing field shows the field empty and its error; the plan list says "Schedule has errors: does not run".
- "Next 5 runs" is refreshed every minute and says when the scheduler is paused or the plan has unsaved changes.

Not verified by anyone: the manual checklists of Tasks 5, 6 and 7; whether Windows raises `SystemEvents.TimeChanged` for a time-zone change made in Settings.

Known limitations:
- Scheduled runs happen only while ReBackup runs (spec). Sleep: a trigger missed while the PC slept runs once at the first check after waking.
- A time-zone change westward can make a local-time trigger that already ran today come due again.
- Scheduled runs are queued without a validity check; an invalid saved plan fails in the runner and shows up as an Error in History.
- A scheduled run can be queued while the delete-plan dialogs are open (the delete then happens while it runs; the run's log file is recreated).
- A plan edited in the editor whose file changes on disk keeps using the older saved copy for scheduled runs until it is saved or reverted.
- The run log of every plan is read on the UI thread at startup (one tail read per plan).

Later / nice to have:
- Name rules exist twice in the runner (old checks plus `PlanValidator.NameErrors`).
- Unknown fields inside a trigger are dropped on save (unknown plan sections survive).
- `ScheduleCalculator`: `afterUtc` of Kind Local is treated as UTC; per-day re-evaluation of weekdays and interval window; edge tests (MinValue/MaxValue, LookBack limit, gap collision of two triggers).
- `RunLog.ReadLast`: a file truncated between reading its length and seeking throws (the scheduler then does no catch-up for that plan).
- `ArmNextCheck` swallows an exception and would then stop re-arming (not reachable with the system clock).
- The Retention tab's "using the plan's triggers" label is stale before the target has been read.
- Phase 3 UI items still open: status dot per plan row, current file and ETA, "starting at hh:mm".
