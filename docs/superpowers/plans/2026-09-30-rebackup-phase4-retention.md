# ReBackup Phase 4 — Retention Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retention rules per plan (daily / weekly / monthly / yearly with anchors, several rules combinable), old versions deleted after each successful run, optional freeing of space before a run, and a Retention tab that shows which existing versions would be kept or deleted and how many versions and how much space the plan needs at full extension.

**Architecture:** `ReBackup.Core.Retention` holds the rule model, the pure `RetentionEngine`, the `RetentionSimulator` and the `RetentionPlanner` that maps engine decisions onto version folders. `ReBackup.Core.Backup` gains `VersionCatalog` (which folders in a target are versions of a plan, identified by timestamp name **and** plan id in the manifest), `ManifestReader` (reads a manifest's header without its file list) and `VersionRemover` (rename to `.deleting`, then delete). `BackupRunner` applies retention after a successful run and can free space in the preflight. The WPF layer adds a rule editor, a "Now" list and a "Full extension" summary with a timeline.

**Tech Stack:** .NET 9, WPF, CommunityToolkit.Mvvm 8.4.2, System.Text.Json, xUnit, FluentAssertions 7.0.0, Microsoft.Extensions.TimeProvider.Testing.

**Spec:** `docs/superpowers/specs/2026-09-30-rebackup-design.md` (this plan covers §4.2 `retention`, §4.3 version identification, §4.4 `fileCount`/`totalBytes`, §4.5 `retentionDeleted`/`warnings`, §5 `RetentionEngine`/`RetentionSimulator`/`VersionCatalog` (listing only)/`VersionRemover`, §7, §10.2 tab 4, and build phase 4 of §13)

## Global Constraints

- Target frameworks: `net9.0` for Core and Tests, `net9.0-windows` for App. Nullable and implicit usings are on. Builds must stay at 0 warnings; check with `dotnet build --no-incremental`.
- Retention never deletes a folder unless its name starts with a timestamp (`YYYY_MM_DD-hh_mm `) **and** its `re-manifest.json` carries the plan's id. Folder names ending in `.partial` or `.deleting` are never versions.
- Every deletion of a version goes through `VersionRemover.Remove` (rename to `<name>.deleting`, delete the content, manifest last).
- Retention runs only after a run that ended `Completed` or `CompletedWithWarnings`. It never deletes the version that run made. A failed deletion is a warning in the log, not a failed run.
- An empty rule list keeps everything. The newest version is always kept. A version is kept if at least one rule keeps it.
- Rule JSON: `{ "period": "Daily|Weekly|Monthly|Yearly", "anchor": …, "keep": n }`. Monthly anchors are JSON numbers (`1..31`, `0` = last day, `-1..-30` = days before the last day), Weekly anchors are weekday names, Yearly anchors are `"MM-DD"`, Daily has no anchor. `keep` is 1..9999.
- Version times are the local times in the folder names. Time comes from an injected `TimeProvider` in Core.
- One JSON object per line in the run log (`JsonDefaults.Compact`); all other JSON uses `JsonDefaults.Options`.
- FluentAssertions stays pinned to 7.0.0.
- Expected values in the tests of this plan were worked out by hand from the spec. If a test fails although the code follows the plan, do not change the expectation or the code to make it pass: stop and report which value differs and why.
- Every commit message ends with a `Co-Authored-By: Claude <model> <noreply@anthropic.com>` trailer naming the authoring model, separated from the subject by a blank line (two `-m` arguments).
- The App has no automated tests. App tasks are verified by build, the Core suite, a startup smoke test, and the manual checklist in the task.

Startup smoke test (App tasks): build, start `src/ReBackup.App/bin/Debug/net9.0-windows/ReBackup.App.exe`, wait 5 s, confirm the process is still running, then stop it. Make sure no `ReBackup.App` process is left behind.

## File Structure

```
src/ReBackup.Core/
  Retention/RetentionRule.cs        RetentionPeriod, RetentionRule, RetentionAnchorConverter
  Retention/RetentionRules.cs       validation, anchors, slot start, description
  Retention/RetentionEngine.cs      RetentionVersion, KeepReason, RetentionDecision, RetentionEngine
  Retention/RetentionSimulator.cs   SimulatedVersion, SimulationResult, RetentionSimulator
  Retention/RetentionPlanner.cs     VersionDecision, RetentionPlanner
  Plans/BackupPlan.cs               (modify) Retention
  Plans/PlanValidator.cs            (modify) rule errors, ".deleting" names
  Backup/VersionName.cs             (modify) DeletingSuffix, TryParseAny, IsTransient
  Backup/BackupManifest.cs          (modify) FileCount, TotalBytes
  Backup/ManifestReader.cs          ManifestHeader, ManifestReader
  Backup/VersionCatalog.cs          VersionOwnership, VersionInfo, VersionCatalog
  Backup/VersionRemover.cs
  Backup/TargetVolume.cs            (modify) DeleteDirectory
  Backup/RunLog.cs                  (modify) Warnings
  Backup/BackupRunner.cs            (modify) manifest totals, leftovers, retention after a run, freeing space
src/ReBackup.App/
  ViewModels/RetentionRuleViewModel.cs     one editable rule row
  ViewModels/RetentionPreviewViewModel.cs  Now list, full-extension simulation
  ViewModels/RetentionNowRow.cs            one row of the Now list
  ViewModels/TimelineLane.cs
  ViewModels/PlanEditorViewModel.cs        (modify) rule rows, RetentionPreview
  ViewModels/MainViewModel.cs              (modify) reload the preview after a run
  ViewModels/PlanRunViewModel.cs           (modify) retention phase text
  ViewModels/RunHistoryRow.cs              (modify) warnings
  Controls/RetentionTimelineControl.cs
  Views/RetentionView.xaml(.cs)
  MainWindow.xaml                          (modify) Retention tab
tests/ReBackup.Core.Tests/
  Retention/RetentionRulesTests.cs
  Retention/RetentionEngineTests.cs
  Retention/RetentionSimulatorTests.cs
  Retention/RetentionPlannerTests.cs
  Backup/ManifestReaderTests.cs
  Backup/VersionCatalogTests.cs
  Backup/VersionRemoverTests.cs
  Backup/BackupRunnerRetentionTests.cs
  Backup/VersionNameTests.cs, RunLogTests.cs, BackupRunnerTests.cs   (modify)
  Plans/PlanValidatorTests.cs, PlanStoreTests.cs                     (modify)
  TestSupport/VersionFolder.cs      creates a version folder with a manifest
  TestSupport/ScriptedVolume.cs     ITargetVolume with switches for failures and free space
```

---

### Task 1: Retention rules in the plan

**Files:**
- Create: `src/ReBackup.Core/Retention/RetentionRule.cs`
- Create: `src/ReBackup.Core/Retention/RetentionRules.cs`
- Modify: `src/ReBackup.Core/Plans/BackupPlan.cs`
- Modify: `src/ReBackup.Core/Plans/PlanValidator.cs`
- Test: `tests/ReBackup.Core.Tests/Retention/RetentionRulesTests.cs` (create)
- Test: `tests/ReBackup.Core.Tests/Plans/PlanValidatorTests.cs`, `tests/ReBackup.Core.Tests/Plans/PlanStoreTests.cs` (modify)

**Interfaces:**
- Consumes: `JsonDefaults.Options`, `BackupPlan`, `PlanValidator.Validate(BackupPlan, IEnumerable<BackupPlan>)`.
- Produces:
  - `enum RetentionPeriod { Daily, Weekly, Monthly, Yearly }`
  - `sealed class RetentionRule { RetentionPeriod Period; string? Anchor; int Keep = 1; }`
  - `static class RetentionRules`: `const int MaxKeep = 9999`; `string? Validate(RetentionRule? rule)`; `bool TryGetWeekday(string? anchor, out DayOfWeek day)`; `bool TryGetMonthDay(string? anchor, out int day)`; `bool TryGetYearDate(string? anchor, out int month, out int day)`; `string Describe(RetentionRule rule)`; `DateOnly SlotStart(RetentionRule rule, DateOnly date)`; `DateOnly MonthAnchor(int year, int month, int anchor)`; `DateOnly YearAnchor(int year, int month, int day)`
  - `BackupPlan.Retention` (`List<RetentionRule>`, never null)

- [ ] **Step 1: Write the failing tests**

Create `tests/ReBackup.Core.Tests/Retention/RetentionRulesTests.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using ReBackup.Core.Json;
using ReBackup.Core.Retention;

namespace ReBackup.Core.Tests.Retention;

public class RetentionRulesTests
{
    private static RetentionRule Rule(RetentionPeriod period, string? anchor = null, int keep = 1) =>
        new() { Period = period, Anchor = anchor, Keep = keep };

    private static DateOnly D(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(RetentionPeriod.Daily, null)]
    [InlineData(RetentionPeriod.Weekly, "Sunday")]
    [InlineData(RetentionPeriod.Weekly, "wed")]
    [InlineData(RetentionPeriod.Monthly, "1")]
    [InlineData(RetentionPeriod.Monthly, "31")]
    [InlineData(RetentionPeriod.Monthly, "0")]
    [InlineData(RetentionPeriod.Monthly, "-30")]
    [InlineData(RetentionPeriod.Yearly, "01-01")]
    [InlineData(RetentionPeriod.Yearly, "02-29")]
    public void Valid_rules_pass(RetentionPeriod period, string? anchor)
    {
        RetentionRules.Validate(Rule(period, anchor)).Should().BeNull();
    }

    [Theory]
    [InlineData(RetentionPeriod.Weekly, null)]
    [InlineData(RetentionPeriod.Weekly, "Sonntag")]
    [InlineData(RetentionPeriod.Weekly, "3")]
    [InlineData(RetentionPeriod.Monthly, null)]
    [InlineData(RetentionPeriod.Monthly, "32")]
    [InlineData(RetentionPeriod.Monthly, "-31")]
    [InlineData(RetentionPeriod.Monthly, "first")]
    [InlineData(RetentionPeriod.Yearly, null)]
    [InlineData(RetentionPeriod.Yearly, "1-1")]
    [InlineData(RetentionPeriod.Yearly, "13-01")]
    [InlineData(RetentionPeriod.Yearly, "02-30")]
    [InlineData(RetentionPeriod.Yearly, "00-10")]
    public void Invalid_anchors_are_reported(RetentionPeriod period, string? anchor)
    {
        RetentionRules.Validate(Rule(period, anchor)).Should().Contain("anchor");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10000)]
    public void Keep_must_be_between_1_and_9999(int keep)
    {
        RetentionRules.Validate(Rule(RetentionPeriod.Daily, null, keep)).Should().Be("keep must be a number from 1 to 9999.");
    }

    [Fact]
    public void A_missing_rule_is_reported()
    {
        RetentionRules.Validate(null).Should().Be("the rule is empty.");
    }

    [Theory]
    [InlineData(RetentionPeriod.Daily, null, "Daily")]
    [InlineData(RetentionPeriod.Weekly, "Sunday", "Weekly(Sun)")]
    [InlineData(RetentionPeriod.Weekly, "wed", "Weekly(Wed)")]
    [InlineData(RetentionPeriod.Monthly, "0", "Monthly(0)")]
    [InlineData(RetentionPeriod.Monthly, "-1", "Monthly(-1)")]
    [InlineData(RetentionPeriod.Monthly, "14", "Monthly(14)")]
    [InlineData(RetentionPeriod.Yearly, "01-01", "Yearly(01-01)")]
    [InlineData(RetentionPeriod.Weekly, "Someday", "Weekly(Someday)")]
    public void Describe_gives_a_short_label(RetentionPeriod period, string? anchor, string expected)
    {
        RetentionRules.Describe(Rule(period, anchor)).Should().Be(expected);
    }

    [Theory]
    [InlineData(2026, 2, 1, "2026-02-01")]
    [InlineData(2026, 2, 31, "2026-02-28")]
    [InlineData(2024, 2, 31, "2024-02-29")]
    [InlineData(2026, 9, 0, "2026-09-30")]
    [InlineData(2026, 9, -1, "2026-09-29")]
    [InlineData(2026, 2, -30, "2026-02-01")]
    public void Month_anchor_is_clamped_to_the_month(int year, int month, int anchor, string expected)
    {
        RetentionRules.MonthAnchor(year, month, anchor).Should().Be(D(expected));
    }

    // 2026-09-27 is a Sunday, 2026-09-30 a Wednesday.
    [Theory]
    [InlineData(RetentionPeriod.Daily, null, "2026-09-30", "2026-09-30")]
    [InlineData(RetentionPeriod.Weekly, "Sunday", "2026-09-30", "2026-09-27")]
    [InlineData(RetentionPeriod.Weekly, "Sunday", "2026-09-27", "2026-09-27")]
    [InlineData(RetentionPeriod.Weekly, "Sunday", "2026-09-26", "2026-09-20")]
    [InlineData(RetentionPeriod.Weekly, "Wednesday", "2026-09-30", "2026-09-30")]
    [InlineData(RetentionPeriod.Weekly, "Wednesday", "2026-09-29", "2026-09-23")]
    [InlineData(RetentionPeriod.Monthly, "1", "2026-09-30", "2026-09-01")]
    [InlineData(RetentionPeriod.Monthly, "1", "2026-09-01", "2026-09-01")]
    [InlineData(RetentionPeriod.Monthly, "15", "2026-09-14", "2026-08-15")]
    [InlineData(RetentionPeriod.Monthly, "31", "2026-02-28", "2026-02-28")]
    [InlineData(RetentionPeriod.Monthly, "31", "2026-02-27", "2026-01-31")]
    [InlineData(RetentionPeriod.Monthly, "31", "2026-03-30", "2026-02-28")]
    [InlineData(RetentionPeriod.Monthly, "31", "2024-02-29", "2024-02-29")]
    [InlineData(RetentionPeriod.Monthly, "0", "2026-09-30", "2026-09-30")]
    [InlineData(RetentionPeriod.Monthly, "0", "2026-09-29", "2026-08-31")]
    [InlineData(RetentionPeriod.Monthly, "-1", "2026-09-29", "2026-09-29")]
    [InlineData(RetentionPeriod.Monthly, "-1", "2026-09-28", "2026-08-30")]
    [InlineData(RetentionPeriod.Monthly, "1", "2026-01-01", "2026-01-01")]
    [InlineData(RetentionPeriod.Monthly, "15", "2026-01-10", "2025-12-15")]
    [InlineData(RetentionPeriod.Yearly, "01-01", "2026-09-30", "2026-01-01")]
    [InlineData(RetentionPeriod.Yearly, "03-15", "2026-03-14", "2025-03-15")]
    [InlineData(RetentionPeriod.Yearly, "03-15", "2026-03-15", "2026-03-15")]
    [InlineData(RetentionPeriod.Yearly, "02-29", "2026-03-01", "2026-02-28")]
    [InlineData(RetentionPeriod.Yearly, "02-29", "2024-02-29", "2024-02-29")]
    [InlineData(RetentionPeriod.Yearly, "02-29", "2025-02-27", "2024-02-29")]
    public void Slot_start_is_the_anchor_day_at_or_before_the_date(RetentionPeriod period, string? anchor, string date, string expected)
    {
        RetentionRules.SlotStart(Rule(period, anchor), D(date)).Should().Be(D(expected));
    }

    [Fact]
    public void Slot_start_of_an_invalid_rule_throws()
    {
        var act = () => RetentionRules.SlotStart(Rule(RetentionPeriod.Weekly, "Someday"), D("2026-09-30"));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rules_round_trip_with_numbers_for_day_anchors()
    {
        var json = """
            [ { "period": "Daily", "keep": 7 },
              { "period": "Weekly", "anchor": "Sunday", "keep": 4 },
              { "period": "Monthly", "anchor": 0, "keep": 12 },
              { "period": "Monthly", "anchor": -1, "keep": 2 },
              { "period": "Yearly", "anchor": "01-01", "keep": 3 } ]
            """;

        var rules = JsonSerializer.Deserialize<List<RetentionRule>>(json, JsonDefaults.Options)!;

        rules.Select(r => r.Period).Should().Equal(RetentionPeriod.Daily, RetentionPeriod.Weekly,
            RetentionPeriod.Monthly, RetentionPeriod.Monthly, RetentionPeriod.Yearly);
        rules.Select(r => r.Anchor).Should().Equal(new string?[] { null, "Sunday", "0", "-1", "01-01" });
        rules.Select(r => r.Keep).Should().Equal(7, 4, 12, 2, 3);

        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(rules, JsonDefaults.Options));
        saved.RootElement[0].TryGetProperty("anchor", out _).Should().BeFalse("a daily rule has no anchor");
        saved.RootElement[1].GetProperty("anchor").GetString().Should().Be("Sunday");
        saved.RootElement[2].GetProperty("anchor").GetInt32().Should().Be(0);
        saved.RootElement[3].GetProperty("anchor").GetInt32().Should().Be(-1);
        saved.RootElement[4].GetProperty("anchor").GetString().Should().Be("01-01");
        saved.RootElement[2].GetProperty("period").GetString().Should().Be("Monthly");
        saved.RootElement[2].GetProperty("keep").GetInt32().Should().Be(12);
    }

    [Fact]
    public void An_anchor_that_is_neither_text_nor_a_whole_number_is_rejected()
    {
        var act = () => JsonSerializer.Deserialize<RetentionRule>("""{ "period": "Monthly", "anchor": true, "keep": 1 }""",
            JsonDefaults.Options);
        act.Should().Throw<JsonException>();
    }
}
```

In `tests/ReBackup.Core.Tests/Plans/PlanValidatorTests.cs`:

1. Add `using ReBackup.Core.Retention;` to the usings.
2. Add two rows to the `[Theory]` `Invalid_names_are_reported`, directly after the `"Docs.PARTIAL"` row:

```csharp
    [InlineData("Docs.deleting", "Name must not end with \".deleting\".")]
    [InlineData("Docs.DELETING", "Name must not end with \".deleting\".")]
```

3. Add this test to the class:

```csharp
    [Fact]
    public void Invalid_retention_rules_are_reported_with_their_position()
    {
        var plan = ValidPlan();
        plan.Retention =
        [
            new RetentionRule { Period = RetentionPeriod.Daily, Keep = 7 },
            new RetentionRule { Period = RetentionPeriod.Weekly, Anchor = "Someday", Keep = 4 },
            new RetentionRule { Period = RetentionPeriod.Daily, Keep = 0 },
        ];

        Validate(plan).Should().Equal(
            "Retention rule 2: the anchor must be a weekday, for example Sunday.",
            "Retention rule 3: keep must be a number from 1 to 9999.");
    }
```

In `tests/ReBackup.Core.Tests/Plans/PlanStoreTests.cs`, add `using ReBackup.Core.Retention;` (and `using ReBackup.Core.Json;` if it is not there yet) and these tests to the class:

```csharp
    [Fact]
    public void Retention_rules_round_trip()
    {
        using var store = NewStore();
        var plan = new BackupPlan { Name = "Projects" };
        plan.Retention.Add(new RetentionRule { Period = RetentionPeriod.Monthly, Anchor = "0", Keep = 12 });
        plan.Retention.Add(new RetentionRule { Period = RetentionPeriod.Weekly, Anchor = "Sunday", Keep = 4 });
        store.Save(plan);

        var loaded = store.LoadAll().Plans.Single();

        loaded.Retention.Should().HaveCount(2);
        loaded.Retention[0].Period.Should().Be(RetentionPeriod.Monthly);
        loaded.Retention[0].Anchor.Should().Be("0");
        loaded.Retention[0].Keep.Should().Be(12);
        loaded.Retention[1].Anchor.Should().Be("Sunday");
        loaded.Clone().Retention.Should().HaveCount(2);
    }

    [Fact]
    public void A_null_retention_section_loads_as_no_rules()
    {
        var plan = JsonSerializer.Deserialize<BackupPlan>("""{ "id": "p1", "name": "Keep", "retention": null }""",
            JsonDefaults.Options)!;

        plan.Retention.Should().BeEmpty();
    }
```

The existing test `Unknown_properties_survive_load_and_save` in that file must keep passing unchanged (its `retention` section is now read into `BackupPlan.Retention` and written back with the same JSON).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~RetentionRulesTests|FullyQualifiedName~PlanValidatorTests|FullyQualifiedName~PlanStoreTests"`
Expected: FAIL — compile errors, `ReBackup.Core.Retention` does not exist.

- [ ] **Step 3: Write the rule model**

Create `src/ReBackup.Core/Retention/RetentionRule.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReBackup.Core.Retention;

public enum RetentionPeriod { Daily, Weekly, Monthly, Yearly }

/// <summary>One entry of a plan's "retention" list.</summary>
public sealed class RetentionRule
{
    public RetentionPeriod Period { get; set; }

    /// <summary>
    /// Daily: none. Weekly: a weekday ("Sunday"). Monthly: a day number as text ("1".."31", "0" = last day,
    /// "-1" = the day before the last day, …). Yearly: "MM-DD". Whole numbers are stored as JSON numbers.
    /// </summary>
    [JsonConverter(typeof(RetentionAnchorConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Anchor { get; set; }

    /// <summary>How many slots that have a version this rule keeps.</summary>
    public int Keep { get; set; } = 1;
}

/// <summary>Reads an anchor that is either JSON text or a JSON whole number; writes whole numbers as numbers.</summary>
public sealed class RetentionAnchorConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetInt32(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => throw new JsonException("A retention anchor must be text or a whole number."),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            writer.WriteNumberValue(number);
        else
            writer.WriteStringValue(value);
    }
}
```

Create `src/ReBackup.Core/Retention/RetentionRules.cs`:

```csharp
using System.Globalization;

namespace ReBackup.Core.Retention;

/// <summary>Checking, reading and applying the anchors of retention rules.</summary>
public static class RetentionRules
{
    public const int MaxKeep = 9999;

    private static readonly string[] ShortDayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    /// <summary>Null when the rule can be used; otherwise what is wrong with it.</summary>
    public static string? Validate(RetentionRule? rule)
    {
        if (rule is null)
            return "the rule is empty.";
        if (!Enum.IsDefined(rule.Period))
            return "the period is unknown.";
        if (rule.Keep < 1 || rule.Keep > MaxKeep)
            return $"keep must be a number from 1 to {MaxKeep}.";

        return rule.Period switch
        {
            RetentionPeriod.Weekly when !TryGetWeekday(rule.Anchor, out _) =>
                "the anchor must be a weekday, for example Sunday.",
            RetentionPeriod.Monthly when !TryGetMonthDay(rule.Anchor, out _) =>
                "the anchor must be a day from 1 to 31, 0 for the last day of the month, or -1 to -30 for days before the last day.",
            RetentionPeriod.Yearly when !TryGetYearDate(rule.Anchor, out _, out _) =>
                "the anchor must be a date written as MM-DD, for example 01-01.",
            _ => null,
        };
    }

    /// <summary>Accepts full English weekday names and their three-letter forms, in any case.</summary>
    public static bool TryGetWeekday(string? anchor, out DayOfWeek day)
    {
        day = default;
        var text = anchor?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;

        for (var i = 0; i < ShortDayNames.Length; i++)
        {
            if (text.Equals(ShortDayNames[i], StringComparison.OrdinalIgnoreCase) ||
                text.Equals(((DayOfWeek)i).ToString(), StringComparison.OrdinalIgnoreCase))
            {
                day = (DayOfWeek)i;
                return true;
            }
        }
        return false;
    }

    public static bool TryGetMonthDay(string? anchor, out int day) =>
        int.TryParse(anchor?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out day) &&
        day is >= -30 and <= 31;

    public static bool TryGetYearDate(string? anchor, out int month, out int day)
    {
        month = 0;
        day = 0;
        var text = anchor?.Trim();
        if (text is not { Length: 5 } || text[2] != '-')
            return false;
        if (!int.TryParse(text.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out month) ||
            !int.TryParse(text.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out day))
            return false;

        // 2000 is a leap year, so 02-29 is allowed; it is clamped to 02-28 in other years.
        return month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(2000, month);
    }

    /// <summary>Short text for a rule, e.g. <c>Weekly(Sun)</c> or <c>Monthly(0)</c>.</summary>
    public static string Describe(RetentionRule rule) => rule.Period switch
    {
        RetentionPeriod.Daily => "Daily",
        RetentionPeriod.Weekly when TryGetWeekday(rule.Anchor, out var weekday) => $"Weekly({ShortDayNames[(int)weekday]})",
        RetentionPeriod.Monthly when TryGetMonthDay(rule.Anchor, out var monthDay) =>
            string.Create(CultureInfo.InvariantCulture, $"Monthly({monthDay})"),
        RetentionPeriod.Yearly when TryGetYearDate(rule.Anchor, out var month, out var day) =>
            string.Create(CultureInfo.InvariantCulture, $"Yearly({month:00}-{day:00})"),
        _ => $"{rule.Period}({rule.Anchor})",
    };

    /// <summary>The anchor day of a month: 1..31 is that day (at most the last day), 0 the last day, -n n days before it.</summary>
    public static DateOnly MonthAnchor(int year, int month, int anchor)
    {
        var days = DateTime.DaysInMonth(year, month);
        var day = anchor >= 1 ? Math.Min(anchor, days) : Math.Max(1, days + anchor);
        return new DateOnly(year, month, day);
    }

    /// <summary>The anchor date of a year; a day the month does not have in that year becomes its last day.</summary>
    public static DateOnly YearAnchor(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    /// <summary>The first day of the slot that contains <paramref name="date"/>: the anchor day at or before it.</summary>
    /// <exception cref="ArgumentException">The rule is not valid.</exception>
    public static DateOnly SlotStart(RetentionRule rule, DateOnly date)
    {
        switch (rule.Period)
        {
            case RetentionPeriod.Daily:
                return date;

            case RetentionPeriod.Weekly when TryGetWeekday(rule.Anchor, out var weekday):
                var daysBack = ((int)date.DayOfWeek - (int)weekday + 7) % 7;
                return date.DayNumber >= daysBack ? date.AddDays(-daysBack) : DateOnly.MinValue;

            case RetentionPeriod.Monthly when TryGetMonthDay(rule.Anchor, out var monthDay):
                var thisMonth = MonthAnchor(date.Year, date.Month, monthDay);
                if (date >= thisMonth)
                    return thisMonth;
                if (date is { Year: 1, Month: 1 })
                    return DateOnly.MinValue;
                var previous = date.AddMonths(-1);
                return MonthAnchor(previous.Year, previous.Month, monthDay);

            case RetentionPeriod.Yearly when TryGetYearDate(rule.Anchor, out var month, out var day):
                var thisYear = YearAnchor(date.Year, month, day);
                if (date >= thisYear)
                    return thisYear;
                return date.Year == 1 ? DateOnly.MinValue : YearAnchor(date.Year - 1, month, day);

            default:
                throw new ArgumentException($"The retention rule is not valid: {Validate(rule)}", nameof(rule));
        }
    }
}
```

- [ ] **Step 4: Add the rules to the plan and the validator**

In `src/ReBackup.Core/Plans/BackupPlan.cs`:

1. Add `using ReBackup.Core.Retention;` to the usings.
2. Directly below `public IgnoreSettings Ignore { get; set; } = new();`, add:

```csharp
    private List<RetentionRule> _retention = [];

    /// <summary>Which old versions to keep. Empty means: keep everything.</summary>
    public List<RetentionRule> Retention
    {
        get => _retention;
        set => _retention = value ?? [];
    }
```

3. Change the summary of `Extra` to:

```csharp
    /// <summary>Plan sections not yet modelled by this version (triggers) survive a load/save round trip.</summary>
```

In `src/ReBackup.Core/Plans/PlanValidator.cs`:

1. Add `using ReBackup.Core.Retention;` to the usings.
2. In `Validate`, directly before `return errors;`, add:

```csharp
        for (var i = 0; i < plan.Retention.Count; i++)
        {
            if (RetentionRules.Validate(plan.Retention[i]) is { } problem)
                errors.Add($"Retention rule {i + 1}: {problem}");
        }
```

3. In `ValidateName`, directly after the two lines that report `".partial"`, add:

```csharp
        if (name.EndsWith(".deleting", StringComparison.OrdinalIgnoreCase))
            errors.Add("Name must not end with \".deleting\".");
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~RetentionRulesTests|FullyQualifiedName~PlanValidatorTests|FullyQualifiedName~PlanStoreTests"`
Expected: PASS.

- [ ] **Step 6: Full build and test run**

Run: `dotnet build --no-incremental` — expected: 0 warnings, 0 errors.
Run: `dotnet test tests/ReBackup.Core.Tests` — expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): retention rules in the plan with validation and slot calculation" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 2: RetentionEngine

**Files:**
- Create: `src/ReBackup.Core/Retention/RetentionEngine.cs`
- Test: `tests/ReBackup.Core.Tests/Retention/RetentionEngineTests.cs` (create)

**Interfaces:**
- Consumes: `RetentionRule`, `RetentionRules.Validate`, `RetentionRules.SlotStart`, `RetentionRules.Describe` (Task 1); `VersionName.Format(DateTime, string)` (tests only).
- Produces:
  - `sealed record RetentionVersion(string Name, DateTime LocalTime)`
  - `sealed record KeepReason(int RuleIndex, int Slot, string Label)` — `RuleIndex` is the position in the rule list, `-1` for the built-in reasons
  - `sealed record RetentionDecision(RetentionVersion Version, IReadOnlyList<KeepReason> Reasons)` with `bool Keep`
  - `static class RetentionEngine`: `const string NewestLabel = "Newest"`, `const string NoRulesLabel = "No rules"`, `IReadOnlyList<RetentionDecision> Evaluate(IReadOnlyList<RetentionVersion> versions, IReadOnlyList<RetentionRule> rules)` — result ordered oldest first; throws `ArgumentException` when a rule is not valid

Semantics (spec §7.1): a rule puts every version into a slot (`RetentionRules.SlotStart` of its date). The representative of a slot is the last version on the slot's first day; if there is none, the first version in the slot. A rule keeps the representatives of its `Keep` most recent slots that have a version. The newest version is always kept. No rules: everything is kept.

- [ ] **Step 1: Write the failing tests**

Create `tests/ReBackup.Core.Tests/Retention/RetentionEngineTests.cs`:

```csharp
using System.Globalization;
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Retention;

namespace ReBackup.Core.Tests.Retention;

// September 2026: the 6th, 13th, 20th and 27th are Sundays; the 30th is a Wednesday.
public class RetentionEngineTests
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm";

    private static RetentionVersion V(string localTime)
    {
        var time = DateTime.ParseExact(localTime, TimeFormat, CultureInfo.InvariantCulture);
        return new RetentionVersion(VersionName.Format(time, "P"), time);
    }

    private static RetentionVersion[] Versions(params string[] localTimes) => localTimes.Select(V).ToArray();

    private static RetentionRule Rule(RetentionPeriod period, string? anchor, int keep) =>
        new() { Period = period, Anchor = anchor, Keep = keep };

    private static RetentionRule Daily(int keep) => Rule(RetentionPeriod.Daily, null, keep);
    private static RetentionRule Weekly(string day, int keep) => Rule(RetentionPeriod.Weekly, day, keep);
    private static RetentionRule Monthly(int day, int keep) =>
        Rule(RetentionPeriod.Monthly, day.ToString(CultureInfo.InvariantCulture), keep);
    private static RetentionRule Yearly(string date, int keep) => Rule(RetentionPeriod.Yearly, date, keep);

    private static string[] Times(IEnumerable<RetentionDecision> decisions) =>
        decisions.Select(d => d.Version.LocalTime.ToString(TimeFormat, CultureInfo.InvariantCulture)).ToArray();

    private static string[] Kept(IReadOnlyList<RetentionDecision> decisions) => Times(decisions.Where(d => d.Keep));

    private static string[] Deleted(IReadOnlyList<RetentionDecision> decisions) => Times(decisions.Where(d => !d.Keep));

    private static string[] Labels(IReadOnlyList<RetentionDecision> decisions, string localTime) =>
        decisions.Single(d => d.Version == V(localTime)).Reasons.Select(r => r.Label).ToArray();

    [Fact]
    public void No_versions_give_no_decisions()
    {
        RetentionEngine.Evaluate([], [Daily(7)]).Should().BeEmpty();
    }

    [Fact]
    public void Without_rules_everything_is_kept()
    {
        var decisions = RetentionEngine.Evaluate(Versions("2026-09-01 02:00", "2026-09-30 02:00"), []);

        Kept(decisions).Should().Equal("2026-09-01 02:00", "2026-09-30 02:00");
        decisions[0].Reasons.Should().Equal(new KeepReason(-1, 0, "No rules"));
        decisions[1].Reasons.Should().Equal(new KeepReason(-1, 0, "No rules"));
    }

    [Fact]
    public void Daily_keeps_the_last_version_of_each_of_the_most_recent_days()
    {
        var versions = Versions("2026-09-26 10:00", "2026-09-27 08:00", "2026-09-27 20:00", "2026-09-28 09:00",
            "2026-09-29 09:00", "2026-09-30 09:00");

        var three = RetentionEngine.Evaluate(versions, [Daily(3)]);
        Kept(three).Should().Equal("2026-09-28 09:00", "2026-09-29 09:00", "2026-09-30 09:00");
        Deleted(three).Should().Equal("2026-09-26 10:00", "2026-09-27 08:00", "2026-09-27 20:00");
        Labels(three, "2026-09-30 09:00").Should().Equal("Daily #1");
        Labels(three, "2026-09-28 09:00").Should().Equal("Daily #3");

        var four = RetentionEngine.Evaluate(versions, [Daily(4)]);
        Kept(four).Should().Equal("2026-09-27 20:00", "2026-09-28 09:00", "2026-09-29 09:00", "2026-09-30 09:00");
    }

    [Fact]
    public void Days_without_a_version_do_not_use_up_the_count()
    {
        var decisions = RetentionEngine.Evaluate(Versions("2026-06-01 02:00", "2026-09-30 02:00"), [Daily(2)]);

        Kept(decisions).Should().Equal("2026-06-01 02:00", "2026-09-30 02:00");
        Labels(decisions, "2026-06-01 02:00").Should().Equal("Daily #2");
    }

    [Fact]
    public void Weekly_keeps_the_last_version_of_the_anchor_day()
    {
        var versions = Versions("2026-09-20 02:00", "2026-09-20 18:00", "2026-09-21 02:00", "2026-09-27 02:00",
            "2026-09-30 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Weekly("Sunday", 2)]);

        Kept(decisions).Should().Equal("2026-09-20 18:00", "2026-09-27 02:00", "2026-09-30 02:00");
        Deleted(decisions).Should().Equal("2026-09-20 02:00", "2026-09-21 02:00");
        Labels(decisions, "2026-09-27 02:00").Should().Equal("Weekly(Sun) #1");
        Labels(decisions, "2026-09-20 18:00").Should().Equal("Weekly(Sun) #2");
        Labels(decisions, "2026-09-30 02:00").Should().Equal("Newest");
    }

    [Fact]
    public void A_slot_without_a_version_on_the_anchor_day_is_represented_by_the_first_version_after_it()
    {
        // No Sunday backups: Tuesday the 22nd stands for the week of the 20th, Monday the 28th for the week of the 27th.
        var versions = Versions("2026-09-22 02:00", "2026-09-24 02:00", "2026-09-28 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Weekly("Sunday", 2)]);

        Kept(decisions).Should().Equal("2026-09-22 02:00", "2026-09-28 02:00");
        Deleted(decisions).Should().Equal("2026-09-24 02:00");
        Labels(decisions, "2026-09-22 02:00").Should().Equal("Weekly(Sun) #2");
        Labels(decisions, "2026-09-28 02:00").Should().Equal("Weekly(Sun) #1");
    }

    [Fact]
    public void Monthly_on_day_1_keeps_the_last_version_of_that_day()
    {
        var versions = Versions("2026-08-01 02:00", "2026-08-15 02:00", "2026-09-01 10:00", "2026-09-01 20:00",
            "2026-09-10 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Monthly(1, 2)]);

        Kept(decisions).Should().Equal("2026-08-01 02:00", "2026-09-01 20:00", "2026-09-10 02:00");
        Deleted(decisions).Should().Equal("2026-08-15 02:00", "2026-09-01 10:00");
        Labels(decisions, "2026-09-01 20:00").Should().Equal("Monthly(1) #1");
        Labels(decisions, "2026-08-01 02:00").Should().Equal("Monthly(1) #2");
        Labels(decisions, "2026-09-10 02:00").Should().Equal("Newest");
    }

    [Fact]
    public void Monthly_on_day_31_uses_the_last_day_of_shorter_months()
    {
        var versions = Versions("2026-01-31 02:00", "2026-02-27 02:00", "2026-02-28 02:00", "2026-03-30 02:00",
            "2026-03-31 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Monthly(31, 3)]);

        Kept(decisions).Should().Equal("2026-01-31 02:00", "2026-02-28 02:00", "2026-03-31 02:00");
        Deleted(decisions).Should().Equal("2026-02-27 02:00", "2026-03-30 02:00");
        Labels(decisions, "2026-02-28 02:00").Should().Equal("Monthly(31) #2");
    }

    [Fact]
    public void Monthly_anchor_0_is_the_last_day_of_the_month()
    {
        var versions = Versions("2026-08-30 02:00", "2026-08-31 02:00", "2026-09-29 02:00", "2026-09-30 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Monthly(0, 2)]);

        Kept(decisions).Should().Equal("2026-08-31 02:00", "2026-09-30 02:00");
        Deleted(decisions).Should().Equal("2026-08-30 02:00", "2026-09-29 02:00");
        Labels(decisions, "2026-09-30 02:00").Should().Equal("Monthly(0) #1");
    }

    [Fact]
    public void Monthly_anchor_minus_1_is_the_day_before_the_last_day()
    {
        var versions = Versions("2026-09-28 02:00", "2026-09-29 02:00", "2026-09-30 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Monthly(-1, 1)]);

        Kept(decisions).Should().Equal("2026-09-29 02:00", "2026-09-30 02:00");
        Deleted(decisions).Should().Equal("2026-09-28 02:00");
        Labels(decisions, "2026-09-29 02:00").Should().Equal("Monthly(-1) #1");
        Labels(decisions, "2026-09-30 02:00").Should().Equal("Newest");
    }

    [Fact]
    public void Yearly_falls_through_to_the_first_version_after_the_anchor_date()
    {
        var versions = Versions("2025-01-01 02:00", "2025-06-01 02:00", "2026-01-03 02:00", "2026-09-30 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Yearly("01-01", 5)]);

        Kept(decisions).Should().Equal("2025-01-01 02:00", "2026-01-03 02:00", "2026-09-30 02:00");
        Deleted(decisions).Should().Equal("2025-06-01 02:00");
        Labels(decisions, "2026-01-03 02:00").Should().Equal("Yearly(01-01) #1");
        Labels(decisions, "2025-01-01 02:00").Should().Equal("Yearly(01-01) #2");
    }

    [Fact]
    public void A_version_kept_by_several_rules_lists_every_reason_in_rule_order()
    {
        // Saturday the 26th, Sunday the 27th, Monday the 28th.
        var versions = Versions("2026-09-26 02:00", "2026-09-27 02:00", "2026-09-28 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Daily(2), Weekly("Sunday", 2)]);

        Deleted(decisions).Should().BeEmpty();
        decisions.Single(d => d.Version == V("2026-09-27 02:00")).Reasons.Should().Equal(
            new KeepReason(0, 2, "Daily #2"), new KeepReason(1, 1, "Weekly(Sun) #1"));
        Labels(decisions, "2026-09-26 02:00").Should().Equal("Weekly(Sun) #2");
        Labels(decisions, "2026-09-28 02:00").Should().Equal("Daily #1");
    }

    [Fact]
    public void Two_rules_of_the_same_period_are_combined()
    {
        var versions = Versions("2026-09-01 02:00", "2026-09-05 02:00", "2026-09-14 02:00", "2026-09-20 02:00",
            "2026-09-30 02:00");

        var decisions = RetentionEngine.Evaluate(versions, [Monthly(1, 1), Monthly(14, 1)]);

        Kept(decisions).Should().Equal("2026-09-01 02:00", "2026-09-14 02:00", "2026-09-30 02:00");
        Deleted(decisions).Should().Equal("2026-09-05 02:00", "2026-09-20 02:00");
        Labels(decisions, "2026-09-01 02:00").Should().Equal("Monthly(1) #1");
        Labels(decisions, "2026-09-14 02:00").Should().Equal("Monthly(14) #1");
    }

    [Fact]
    public void The_newest_version_is_not_marked_Newest_when_a_rule_keeps_it()
    {
        var decisions = RetentionEngine.Evaluate(Versions("2026-09-29 02:00", "2026-09-30 02:00"), [Daily(1)]);

        Kept(decisions).Should().Equal("2026-09-30 02:00");
        Labels(decisions, "2026-09-30 02:00").Should().Equal("Daily #1");
    }

    [Fact]
    public void Decisions_are_ordered_oldest_first_whatever_the_input_order()
    {
        var decisions = RetentionEngine.Evaluate(Versions("2026-09-30 02:00", "2026-09-01 02:00", "2026-09-15 02:00"), [Daily(9)]);

        Times(decisions).Should().Equal("2026-09-01 02:00", "2026-09-15 02:00", "2026-09-30 02:00");
    }

    [Fact]
    public void An_invalid_rule_is_rejected_with_its_position()
    {
        var act = () => RetentionEngine.Evaluate(Versions("2026-09-30 02:00"), [Daily(7), Weekly("Someday", 4)]);

        act.Should().Throw<ArgumentException>().WithMessage("Retention rule 2: the anchor must be a weekday*");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~RetentionEngineTests"`
Expected: FAIL — compile errors, `RetentionEngine` does not exist.

- [ ] **Step 3: Write the engine**

Create `src/ReBackup.Core/Retention/RetentionEngine.cs`:

```csharp
namespace ReBackup.Core.Retention;

/// <summary>A version as retention sees it: its folder name and the local time in that name.</summary>
public sealed record RetentionVersion(string Name, DateTime LocalTime);

/// <summary>
/// Why a version is kept. <paramref name="RuleIndex"/> is the position of the rule in the plan's list, or -1 for
/// the built-in reasons. <paramref name="Slot"/> counts the rule's slots from the most recent one (1).
/// </summary>
public sealed record KeepReason(int RuleIndex, int Slot, string Label);

public sealed record RetentionDecision(RetentionVersion Version, IReadOnlyList<KeepReason> Reasons)
{
    /// <summary>False means: retention deletes this version.</summary>
    public bool Keep => Reasons.Count > 0;
}

/// <summary>Decides which versions a plan's rules keep. Pure: it looks at names and times only.</summary>
public static class RetentionEngine
{
    public const string NewestLabel = "Newest";
    public const string NoRulesLabel = "No rules";

    /// <summary>One decision per version, oldest first.</summary>
    /// <exception cref="ArgumentException">A rule is not valid.</exception>
    public static IReadOnlyList<RetentionDecision> Evaluate(IReadOnlyList<RetentionVersion> versions,
        IReadOnlyList<RetentionRule> rules)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            if (RetentionRules.Validate(rules[i]) is { } problem)
                throw new ArgumentException($"Retention rule {i + 1}: {problem}", nameof(rules));
        }

        var ordered = versions
            .OrderBy(v => v.LocalTime)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var reasons = new List<KeepReason>[ordered.Length];
        for (var i = 0; i < reasons.Length; i++)
            reasons[i] = [];

        if (rules.Count == 0)
        {
            foreach (var list in reasons)
                list.Add(new KeepReason(-1, 0, NoRulesLabel));
        }
        else
        {
            for (var i = 0; i < rules.Count; i++)
                Claim(ordered, rules[i], i, reasons);

            if (ordered.Length > 0 && reasons[^1].Count == 0)
                reasons[^1].Add(new KeepReason(-1, 0, NewestLabel));
        }

        var decisions = new RetentionDecision[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
            decisions[i] = new RetentionDecision(ordered[i], reasons[i]);
        return decisions;
    }

    private static void Claim(RetentionVersion[] ordered, RetentionRule rule, int ruleIndex, List<KeepReason>[] reasons)
    {
        // Slot start → index of the slot's representative. The versions are visited oldest first, so versions on
        // the slot's first day come before the rest of the slot: the last of them wins; without any, the first
        // version of the slot stays.
        var representatives = new SortedDictionary<DateOnly, int>();
        for (var i = 0; i < ordered.Length; i++)
        {
            var date = DateOnly.FromDateTime(ordered[i].LocalTime);
            var slot = RetentionRules.SlotStart(rule, date);
            if (date == slot || !representatives.ContainsKey(slot))
                representatives[slot] = i;
        }

        var label = RetentionRules.Describe(rule);
        var number = 0;
        foreach (var index in representatives.Values.Reverse())
        {
            if (++number > rule.Keep)
                break;
            reasons[index].Add(new KeepReason(ruleIndex, number, $"{label} #{number}"));
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~RetentionEngineTests"`
Expected: PASS (16 tests).

- [ ] **Step 5: Full build and test run**

Run: `dotnet build --no-incremental` — expected: 0 warnings, 0 errors.
Run: `dotnet test tests/ReBackup.Core.Tests` — expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): retention engine with anchored slots, fall-through and keep reasons" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 3: RetentionSimulator

**Files:**
- Create: `src/ReBackup.Core/Retention/RetentionSimulator.cs`
- Test: `tests/ReBackup.Core.Tests/Retention/RetentionSimulatorTests.cs` (create)

**Interfaces:**
- Consumes: `RetentionEngine.Evaluate`, `RetentionVersion`, `KeepReason`, `RetentionRule` (Tasks 1–2).
- Produces:
  - `sealed record SimulatedVersion(DateTime LocalTime, bool Existing, IReadOnlyList<KeepReason> Reasons)`
  - `sealed record SimulationResult(int SteadyStateCount, long? EstimatedBytes, IReadOnlyList<SimulatedVersion> Survivors, int RunsSimulated, bool Truncated, DateTime Horizon)`
  - `static class RetentionSimulator`: `const int MaxRuns = 20_000`, `const int HorizonYears = 2`, `IEnumerable<DateTime> Every(DateTime first, TimeSpan interval)` (endless), `SimulationResult Simulate(IReadOnlyList<RetentionVersion> existing, IReadOnlyList<RetentionRule> rules, IEnumerable<DateTime> futureRuns, DateTime now, long? averageVersionBytes, CancellationToken cancellationToken = default)`

Semantics (spec §7.3): starting from the existing versions, every future run time after `now` and up to `now + 2 years` adds a version and then applies retention (deleted versions are gone for good). `SteadyStateCount` is the largest number of versions after any run in the second year; without a run in the second year it is the number of versions at the end. `Survivors` are the versions at the end, oldest first, with their keep reasons. `EstimatedBytes` is `averageVersionBytes × SteadyStateCount`. `futureRuns` is expected in ascending order and may be endless.

- [ ] **Step 1: Write the failing tests**

Create `tests/ReBackup.Core.Tests/Retention/RetentionSimulatorTests.cs`:

```csharp
using FluentAssertions;
using ReBackup.Core.Retention;

namespace ReBackup.Core.Tests.Retention;

public class RetentionSimulatorTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0);

    private static RetentionRule Rule(RetentionPeriod period, string? anchor, int keep) =>
        new() { Period = period, Anchor = anchor, Keep = keep };

    private static RetentionVersion Existing(int month, int day) =>
        new($"2026_{month:00}_{day:00}-02_00 P", new DateTime(2026, month, day, 2, 0, 0));

    private static IEnumerable<DateTime> DailyAt2() => RetentionSimulator.Every(new DateTime(2026, 9, 30, 2, 0, 0), TimeSpan.FromDays(1));

    [Fact]
    public void Reference_plan_with_a_daily_backup_settles_at_22_versions()
    {
        RetentionRule[] rules =
        [
            Rule(RetentionPeriod.Daily, null, 7),
            Rule(RetentionPeriod.Weekly, "Sunday", 4),
            Rule(RetentionPeriod.Monthly, "0", 12),
        ];

        var result = RetentionSimulator.Simulate([], rules, DailyAt2(), Now, averageVersionBytes: 1000);

        // 7 days + 4 Sundays (one of them within the 7 days) + 12 month ends, when none of those coincide.
        result.SteadyStateCount.Should().Be(22);
        result.EstimatedBytes.Should().Be(22_000);
        result.RunsSimulated.Should().Be(731);   // 2026-10-01 … 2028-09-30, 2028 is a leap year
        result.Truncated.Should().BeFalse();
        result.Horizon.Should().Be(new DateTime(2028, 9, 30, 12, 0, 0));

        // On 2028-09-30 (a Saturday and a month end) the month end is one of the 7 days: 7 + 3 + 11.
        result.Survivors.Should().HaveCount(21);
        result.Survivors.Should().OnlyContain(s => !s.Existing && s.Reasons.Count > 0);
        result.Survivors[^1].LocalTime.Should().Be(new DateTime(2028, 9, 30, 2, 0, 0));
        result.Survivors.Select(s => s.LocalTime).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Without_rules_every_version_stays()
    {
        var result = RetentionSimulator.Simulate([Existing(9, 28), Existing(9, 29)], [], DailyAt2().Take(6), Now, null);

        // DailyAt2 starts on 09-30 02:00, which is before Now and therefore ignored: 5 runs remain.
        result.RunsSimulated.Should().Be(5);
        result.Survivors.Should().HaveCount(7);
        result.SteadyStateCount.Should().Be(7);
        result.EstimatedBytes.Should().BeNull();
        result.Survivors.Count(s => s.Existing).Should().Be(2);
        result.Survivors[0].Reasons.Should().Equal(new KeepReason(-1, 0, "No rules"));
    }

    [Fact]
    public void Without_future_runs_nothing_is_deleted()
    {
        var result = RetentionSimulator.Simulate([Existing(9, 28), Existing(9, 29), Existing(9, 30)],
            [Rule(RetentionPeriod.Daily, null, 2)], [], Now, 500);

        result.RunsSimulated.Should().Be(0);
        result.Survivors.Should().HaveCount(3);
        result.Survivors.Should().OnlyContain(s => s.Existing);
        result.Survivors[0].Reasons.Should().BeEmpty("the next run would delete it");
        result.Survivors[2].Reasons.Should().Equal(new KeepReason(0, 1, "Daily #1"));
        result.SteadyStateCount.Should().Be(3);
        result.EstimatedBytes.Should().Be(1500);
    }

    [Fact]
    public void Existing_versions_are_deleted_by_the_simulated_runs()
    {
        var result = RetentionSimulator.Simulate([Existing(9, 28), Existing(9, 29), Existing(9, 30)],
            [Rule(RetentionPeriod.Daily, null, 2)], DailyAt2().Take(8), Now, null);

        result.RunsSimulated.Should().Be(7);
        result.Survivors.Select(s => s.LocalTime).Should().Equal(
            new DateTime(2026, 10, 6, 2, 0, 0), new DateTime(2026, 10, 7, 2, 0, 0));
        result.Survivors.Should().OnlyContain(s => !s.Existing);
        result.SteadyStateCount.Should().Be(2, "there is no run in the second year, so the final count is used");
    }

    [Fact]
    public void Runs_at_or_before_now_and_after_the_horizon_are_ignored()
    {
        DateTime[] runs = [Now.AddDays(-1), Now, Now.AddDays(1), Now.AddYears(3)];

        var result = RetentionSimulator.Simulate([], [Rule(RetentionPeriod.Daily, null, 5)], runs, Now, null);

        result.RunsSimulated.Should().Be(1);
        result.Survivors.Should().ContainSingle().Which.LocalTime.Should().Be(Now.AddDays(1));
    }

    [Fact]
    public void Stops_after_the_maximum_number_of_runs()
    {
        var everyMinute = RetentionSimulator.Every(Now.AddMinutes(1), TimeSpan.FromMinutes(1));

        var result = RetentionSimulator.Simulate([], [Rule(RetentionPeriod.Daily, null, 1)], everyMinute, Now, null);

        result.Truncated.Should().BeTrue();
        result.RunsSimulated.Should().Be(RetentionSimulator.MaxRuns);
        result.Survivors.Should().ContainSingle();
    }

    [Fact]
    public void Cancellation_stops_the_simulation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => RetentionSimulator.Simulate([], [Rule(RetentionPeriod.Daily, null, 1)], DailyAt2(), Now, null, cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Every_rejects_an_interval_that_does_not_advance()
    {
        var act = () => RetentionSimulator.Every(Now, TimeSpan.Zero).First();

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~RetentionSimulatorTests"`
Expected: FAIL — compile errors, `RetentionSimulator` does not exist.

- [ ] **Step 3: Write the simulator**

Create `src/ReBackup.Core/Retention/RetentionSimulator.cs`:

```csharp
using System.Globalization;

namespace ReBackup.Core.Retention;

/// <summary>A version that is still there at the end of a simulation. No reasons: the next run would delete it.</summary>
public sealed record SimulatedVersion(DateTime LocalTime, bool Existing, IReadOnlyList<KeepReason> Reasons);

/// <summary>
/// <paramref name="SteadyStateCount"/> is the largest number of versions after a run in the last simulated year.
/// <paramref name="Survivors"/> are the versions at the horizon, oldest first.
/// </summary>
public sealed record SimulationResult(int SteadyStateCount, long? EstimatedBytes,
    IReadOnlyList<SimulatedVersion> Survivors, int RunsSimulated, bool Truncated, DateTime Horizon);

/// <summary>Plays future runs against the retention rules to show what a plan needs at full extension.</summary>
public static class RetentionSimulator
{
    public const int MaxRuns = 20_000;
    public const int HorizonYears = 2;

    private const string SimulatedPrefix = "simulated ";

    /// <summary>An endless series of run times.</summary>
    public static IEnumerable<DateTime> Every(DateTime first, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "The interval must be positive.");
        return Series(first, interval);
    }

    private static IEnumerable<DateTime> Series(DateTime first, TimeSpan interval)
    {
        for (var time = first; ; time += interval)
            yield return time;
    }

    /// <param name="futureRuns">Run times in ascending order; times up to <paramref name="now"/> are ignored.</param>
    /// <param name="averageVersionBytes">Size of one version for the estimate; null when unknown.</param>
    /// <exception cref="ArgumentException">A rule is not valid.</exception>
    public static SimulationResult Simulate(IReadOnlyList<RetentionVersion> existing, IReadOnlyList<RetentionRule> rules,
        IEnumerable<DateTime> futureRuns, DateTime now, long? averageVersionBytes,
        CancellationToken cancellationToken = default)
    {
        var horizon = now.AddYears(HorizonYears);
        var lastYear = horizon.AddYears(-1);
        var current = existing.ToList();
        var steady = 0;
        var sawLastYear = false;
        var runs = 0;
        var truncated = false;

        foreach (var run in futureRuns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (run <= now)
                continue;
            if (run > horizon)
                break;
            if (runs == MaxRuns)
            {
                truncated = true;
                break;
            }

            runs++;
            current.Add(new RetentionVersion(SimulatedPrefix + run.Ticks.ToString(CultureInfo.InvariantCulture), run));
            if (rules.Count > 0)
            {
                // Without rules nothing is ever deleted, so there is nothing to evaluate per run.
                current = RetentionEngine.Evaluate(current, rules).Where(d => d.Keep).Select(d => d.Version).ToList();
            }

            if (run > lastYear)
            {
                steady = Math.Max(steady, current.Count);
                sawLastYear = true;
            }
        }

        var final = RetentionEngine.Evaluate(current, rules);
        if (!sawLastYear)
            steady = final.Count;

        var survivors = final
            .Select(d => new SimulatedVersion(d.Version.LocalTime,
                !d.Version.Name.StartsWith(SimulatedPrefix, StringComparison.Ordinal), d.Reasons))
            .ToArray();
        long? estimated = averageVersionBytes is { } average ? average * steady : null;
        return new SimulationResult(steady, estimated, survivors, runs, truncated, horizon);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~RetentionSimulatorTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Full build and test run**

Run: `dotnet build --no-incremental` — expected: 0 warnings, 0 errors.
Run: `dotnet test tests/ReBackup.Core.Tests` — expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): retention simulator for the full-extension preview" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 4: Manifest header and VersionCatalog

**Files:**
- Modify: `src/ReBackup.Core/Backup/VersionName.cs`
- Modify: `src/ReBackup.Core/Backup/BackupManifest.cs`
- Create: `src/ReBackup.Core/Backup/ManifestReader.cs`
- Create: `src/ReBackup.Core/Backup/VersionCatalog.cs`
- Modify: `src/ReBackup.Core/Backup/BackupRunner.cs` (write the totals into the manifest)
- Create: `tests/ReBackup.Core.Tests/TestSupport/VersionFolder.cs`
- Test: `tests/ReBackup.Core.Tests/Backup/ManifestReaderTests.cs`, `tests/ReBackup.Core.Tests/Backup/VersionCatalogTests.cs` (create)
- Test: `tests/ReBackup.Core.Tests/Backup/VersionNameTests.cs`, `tests/ReBackup.Core.Tests/Backup/BackupRunnerTests.cs` (modify)

**Interfaces:**
- Consumes: `VersionName` (`PartialSuffix`, `ManifestFileName`, `TryParse`), `BackupManifest`, `ManifestFile`, `JsonDefaults.Options`, `TempDir`.
- Produces:
  - `VersionName.DeletingSuffix = ".deleting"`, `bool VersionName.TryParseAny(string folderName, out DateTime localTime, out string planName)`, `bool VersionName.IsTransient(string folderName)`
  - `BackupManifest.FileCount` (`int?`), `BackupManifest.TotalBytes` (`long?`), declared before `Files`
  - `sealed record ManifestHeader(string PlanId, string PlanName, DateTime CreatedUtc, int? FileCount, long? TotalBytes)`
  - `static class ManifestReader`: `ManifestHeader ReadHeader(string manifestPath)`, `(int FileCount, long TotalBytes) ReadTotals(string manifestPath)` — both throw `IOException`, `UnauthorizedAccessException` or `JsonException`
  - `enum VersionOwnership { Owned, NoManifest, Foreign, Unreadable }`
  - `sealed record VersionInfo(string Name, string Path, DateTime LocalTime, VersionOwnership Ownership, int? FileCount, long? TotalBytes)` with `bool IsOwned`
  - `static class VersionCatalog`: `IReadOnlyList<VersionInfo> List(string target, string planId, string planName, CancellationToken cancellationToken = default)` (oldest first; throws `IOException`/`UnauthorizedAccessException` when the target cannot be listed), `(VersionOwnership Ownership, ManifestHeader? Header) Probe(string versionDirectory, string planId)` (never throws; the header is returned only for `Owned`)
  - Test helper `VersionFolder.Create(string target, string name, string planId, long bytes = 10, bool withTotals = true)` → path of the created folder

Which folders `List` returns: a folder whose name is a timestamp plus any name, not ending in `.partial` or `.deleting`, and whose manifest carries `planId` → `Owned` (whatever the name part is). A folder whose name part equals `planName` (ignoring case) but which is not owned → listed as `NoManifest`, `Foreign` or `Unreadable`. Everything else is left out.

- [ ] **Step 1: Write the test helper**

Create `tests/ReBackup.Core.Tests/TestSupport/VersionFolder.cs`:

```csharp
using System.Text.Json;
using ReBackup.Core.Backup;
using ReBackup.Core.Json;

namespace ReBackup.Core.Tests.TestSupport;

public static class VersionFolder
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Creates a version folder holding one file of <paramref name="bytes"/> bytes and a manifest for <paramref name="planId"/>.</summary>
    public static string Create(string target, string name, string planId, long bytes = 10, bool withTotals = true)
    {
        var path = Path.Combine(target, name);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "data.bin"), new byte[bytes]);

        var manifest = new BackupManifest
        {
            PlanId = planId,
            PlanName = "Any",
            CreatedUtc = Stamp,
            Source = @"C:\source",
            Files = [new ManifestFile("data.bin", bytes, Stamp, "xxh64:0000000000000000")],
        };
        if (withTotals)
        {
            manifest.FileCount = 1;
            manifest.TotalBytes = bytes;
        }
        File.WriteAllText(Path.Combine(path, VersionName.ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonDefaults.Options));
        return path;
    }
}
```

- [ ] **Step 2: Write the failing tests**

In `tests/ReBackup.Core.Tests/Backup/VersionNameTests.cs`, add to the class:

```csharp
    [Theory]
    [InlineData("2026_09_30-14_05 Projects", true, "Projects")]
    [InlineData("2026_09_30-14_05 My plan v2", true, "My plan v2")]
    [InlineData("2026_09_30-14_05 ", false, "")]
    [InlineData("2026_09_30-14_05", false, "")]
    [InlineData("2026_13_30-14_05 Projects", false, "")]
    [InlineData("2026_09_30-14_05_Projects", false, "")]
    [InlineData("notes", false, "")]
    public void TryParseAny_accepts_a_timestamp_followed_by_any_name(string folder, bool expected, string expectedName)
    {
        VersionName.TryParseAny(folder, out var time, out var name).Should().Be(expected);

        name.Should().Be(expectedName);
        if (expected)
            time.Should().Be(new DateTime(2026, 9, 30, 14, 5, 0));
    }

    [Theory]
    [InlineData("2026_09_30-14_05 Projects.partial", true)]
    [InlineData("2026_09_30-14_05 Projects.PARTIAL", true)]
    [InlineData("2026_09_30-14_05 Projects.deleting", true)]
    [InlineData("2026_09_30-14_05 Projects", false)]
    public void IsTransient_recognises_folders_that_are_being_written_or_removed(string folder, bool expected)
    {
        VersionName.IsTransient(folder).Should().Be(expected);
    }
```

Create `tests/ReBackup.Core.Tests/Backup/ManifestReaderTests.cs`:

```csharp
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Backup;

public class ManifestReaderTests : IDisposable
{
    private const string TwoFiles = """
        [ { "path": "a.txt", "size": 100, "mtimeUtc": "2026-01-01T00:00:00Z", "hash": "xxh64:0000000000000000" },
          { "path": "b.txt", "size": 200, "mtimeUtc": "2026-01-01T00:00:00Z", "hash": "xxh64:0000000000000000" } ]
        """;

    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private string Write(string content) => _tmp.WriteFile("re-manifest.json", content);

    [Fact]
    public void Reads_the_fields_in_front_of_the_file_list()
    {
        var path = Write($$"""
            { "formatVersion": 1, "planId": "p1", "planName": "Projects", "createdUtc": "2026-09-30T12:05:00Z",
              "source": "D:\\Projects", "fileCount": 2, "totalBytes": 300, "files": {{TwoFiles}} }
            """);

        var header = ManifestReader.ReadHeader(path);

        header.Should().Be(new ManifestHeader("p1", "Projects", new DateTime(2026, 9, 30, 12, 5, 0, DateTimeKind.Utc), 2, 300));
    }

    [Fact]
    public void Does_not_parse_the_file_list_when_the_header_is_complete()
    {
        var path = Write("""{ "planId": "p1", "planName": "Projects", "fileCount": 2, "totalBytes": 300, "files": [ this is not json""");

        ManifestReader.ReadHeader(path).PlanId.Should().Be("p1");
    }

    [Fact]
    public void A_manifest_without_totals_has_none_in_its_header_and_can_be_summed_up()
    {
        var path = Write($$"""{ "formatVersion": 1, "planId": "p1", "planName": "Projects", "files": {{TwoFiles}} }""");

        var header = ManifestReader.ReadHeader(path);

        header.PlanId.Should().Be("p1");
        header.FileCount.Should().BeNull();
        header.TotalBytes.Should().BeNull();
        ManifestReader.ReadTotals(path).Should().Be((2, 300L));
    }

    [Fact]
    public void A_manifest_whose_file_list_comes_first_is_read_in_full()
    {
        var path = Write($$"""{ "files": {{TwoFiles}}, "planId": "p1", "planName": "Projects" }""");

        var header = ManifestReader.ReadHeader(path);

        header.PlanId.Should().Be("p1");
        header.FileCount.Should().Be(2);
        header.TotalBytes.Should().Be(300);
    }

    [Fact]
    public void A_header_larger_than_the_read_buffer_is_read_in_full()
    {
        var longSource = new string('x', 70_000);
        var path = Write($$"""{ "source": "{{longSource}}", "planId": "p1", "planName": "Projects", "fileCount": 2, "totalBytes": 300, "files": {{TwoFiles}} }""");

        ManifestReader.ReadHeader(path).Should().Be(new ManifestHeader("p1", "Projects", default, 2, 300));
    }

    [Fact]
    public void A_byte_order_mark_is_accepted()
    {
        var path = _tmp.PathOf("re-manifest.json");
        File.WriteAllText(path, """{ "planId": "p1", "planName": "Projects", "fileCount": 0, "totalBytes": 0, "files": [] }""",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        ManifestReader.ReadHeader(path).PlanId.Should().Be("p1");
    }

    [Fact]
    public void A_manifest_without_a_plan_id_has_an_empty_one()
    {
        ManifestReader.ReadHeader(Write("{}")).PlanId.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ \"planId\": ")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Damaged_manifests_throw_JsonException(string content)
    {
        var path = Write(content);

        var act = () => ManifestReader.ReadHeader(path);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void A_missing_manifest_throws_FileNotFoundException()
    {
        var act = () => ManifestReader.ReadHeader(_tmp.PathOf("re-manifest.json"));

        act.Should().Throw<FileNotFoundException>();
    }
}
```

Create `tests/ReBackup.Core.Tests/Backup/VersionCatalogTests.cs`:

```csharp
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Backup;

public class VersionCatalogTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _target;

    public VersionCatalogTests() => _target = _tmp.CreateDir("target");

    public void Dispose() => _tmp.Dispose();

    private IReadOnlyList<VersionInfo> List() => VersionCatalog.List(_target, "p1", "Projects");

    [Fact]
    public void Lists_the_versions_of_the_plan_oldest_first_with_their_totals()
    {
        var newer = VersionFolder.Create(_target, "2026_09_30-14_05 Projects", "p1", bytes: 30);
        VersionFolder.Create(_target, "2026_09_28-02_00 Projects", "p1", bytes: 10);

        var versions = List();

        versions.Select(v => v.Name).Should().Equal("2026_09_28-02_00 Projects", "2026_09_30-14_05 Projects");
        versions.Should().OnlyContain(v => v.IsOwned && v.Ownership == VersionOwnership.Owned);
        versions[0].LocalTime.Should().Be(new DateTime(2026, 9, 28, 2, 0, 0));
        versions[1].Path.Should().Be(newer);
        versions.Select(v => v.TotalBytes).Should().Equal(10L, 30L);
        versions.Select(v => v.FileCount).Should().Equal(1, 1);
    }

    [Fact]
    public void Versions_made_under_an_earlier_plan_name_stay_with_the_plan()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 Old name", "p1");

        List().Should().ContainSingle().Which.Ownership.Should().Be(VersionOwnership.Owned);
    }

    [Fact]
    public void Folders_named_like_the_plan_but_not_owned_are_listed_as_such()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 Projects", "other");
        _tmp.WriteFile(@"target\2026_09_02-02_00 projects\a.txt", "x");
        _tmp.WriteFile(@"target\2026_09_03-02_00 Projects\re-manifest.json", "not json");

        var versions = List();

        versions.Select(v => v.Ownership).Should().Equal(
            VersionOwnership.Foreign, VersionOwnership.NoManifest, VersionOwnership.Unreadable);
        versions.Should().OnlyContain(v => !v.IsOwned && v.TotalBytes == null && v.FileCount == null);
    }

    [Fact]
    public void Other_folders_are_not_listed()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 Other", "other");
        VersionFolder.Create(_target, "2026_09_02-02_00 Projects.partial", "p1");
        VersionFolder.Create(_target, "2026_09_03-02_00 Projects.deleting", "p1");
        VersionFolder.Create(_target, "2026_13_04-02_00 Projects", "p1");
        VersionFolder.Create(_target, "notes", "p1");
        _tmp.WriteFile(@"target\2026_09_05-02_00 Projects", "a file, not a folder");

        List().Should().BeEmpty();
    }

    [Fact]
    public void Totals_of_a_manifest_without_them_are_summed_up_from_its_file_list()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 Projects", "p1", bytes: 25, withTotals: false);

        var version = List().Single();

        version.TotalBytes.Should().Be(25);
        version.FileCount.Should().Be(1);
    }

    [Fact]
    public void A_missing_target_has_no_versions()
    {
        VersionCatalog.List(_tmp.PathOf("nowhere"), "p1", "Projects").Should().BeEmpty();
        VersionCatalog.List("", "p1", "Projects").Should().BeEmpty();
    }

    [Fact]
    public void An_empty_plan_id_owns_nothing()
    {
        _tmp.WriteFile(@"target\2026_09_01-02_00 Projects\re-manifest.json", "{}");

        VersionCatalog.List(_target, "", "Projects").Should().ContainSingle()
            .Which.Ownership.Should().Be(VersionOwnership.Foreign);
    }

    [Fact]
    public void Probe_tells_whose_version_a_folder_is()
    {
        var own = VersionFolder.Create(_target, "2026_09_01-02_00 Projects", "p1", bytes: 7);
        var empty = _tmp.CreateDir(@"target\empty");

        var (ownership, header) = VersionCatalog.Probe(own, "P1");
        ownership.Should().Be(VersionOwnership.Owned, "plan ids are compared without regard to case");
        header!.TotalBytes.Should().Be(7);

        VersionCatalog.Probe(own, "p2").Should().Be((VersionOwnership.Foreign, (ManifestHeader?)null));
        VersionCatalog.Probe(empty, "p1").Should().Be((VersionOwnership.NoManifest, (ManifestHeader?)null));
        VersionCatalog.Probe(_tmp.PathOf("nowhere"), "p1").Should().Be((VersionOwnership.NoManifest, (ManifestHeader?)null));
    }
}
```

In `tests/ReBackup.Core.Tests/Backup/BackupRunnerTests.cs`, add to the class:

```csharp
    [Fact]
    public async Task Manifest_carries_the_file_count_and_total_size_in_front_of_the_file_list()
    {
        await Runner().RunAsync(Request(Plan()));

        var json = File.ReadAllText(Path.Combine(VersionPath(), "re-manifest.json"));
        var manifest = JsonSerializer.Deserialize<BackupManifest>(json, JsonDefaults.Options)!;
        manifest.FileCount.Should().Be(2);
        manifest.TotalBytes.Should().Be(16);
        json.IndexOf("\"totalBytes\"", StringComparison.Ordinal).Should().BeLessThan(json.IndexOf("\"files\"", StringComparison.Ordinal));
        ManifestReader.ReadHeader(Path.Combine(VersionPath(), "re-manifest.json")).TotalBytes.Should().Be(16);
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~VersionNameTests|FullyQualifiedName~ManifestReaderTests|FullyQualifiedName~VersionCatalogTests|FullyQualifiedName~BackupRunnerTests"`
Expected: FAIL — compile errors (`TryParseAny`, `ManifestReader`, `VersionCatalog`, `FileCount` do not exist).

- [ ] **Step 4: Extend VersionName and BackupManifest**

In `src/ReBackup.Core/Backup/VersionName.cs`:

1. Directly below `public const string PartialSuffix = ".partial";`, add:

```csharp
    /// <summary>Suffix of a version folder that retention is removing.</summary>
    public const string DeletingSuffix = ".deleting";
```

2. Add these two methods at the end of the class:

```csharp
    /// <summary>True when the folder name is a timestamp, one space and any non-empty name.</summary>
    public static bool TryParseAny(string folderName, out DateTime localTime, out string planName)
    {
        localTime = default;
        planName = "";
        var stampLength = TimestampFormat.Length;
        if (folderName.Length <= stampLength + 1 || folderName[stampLength] != ' ')
            return false;
        if (!DateTime.TryParseExact(folderName.AsSpan(0, stampLength), TimestampFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out localTime))
            return false;

        planName = folderName[(stampLength + 1)..];
        return true;
    }

    /// <summary>True for folders that are being written (".partial") or removed (".deleting"); they are never versions.</summary>
    public static bool IsTransient(string folderName) =>
        folderName.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase) ||
        folderName.EndsWith(DeletingSuffix, StringComparison.OrdinalIgnoreCase);
```

In `src/ReBackup.Core/Backup/BackupManifest.cs`, directly above `public List<ManifestFile> Files { get; set; } = [];`, add:

```csharp
    /// <summary>Number of entries in <see cref="Files"/>. Stands in front of the list so that it can be read without it.</summary>
    public int? FileCount { get; set; }

    /// <summary>Sum of the sizes in <see cref="Files"/>.</summary>
    public long? TotalBytes { get; set; }

```

In `src/ReBackup.Core/Backup/BackupRunner.cs`, in `CopyAndFinish`, directly before the line `using (var stream = File.Create(Path.Combine(partialPath, VersionName.ManifestFileName)))`, add:

```csharp
        manifest.FileCount = manifest.Files.Count;
        manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
```

- [ ] **Step 5: Write ManifestReader**

Create `src/ReBackup.Core/Backup/ManifestReader.cs`:

```csharp
using System.Text.Json;
using ReBackup.Core.Json;

namespace ReBackup.Core.Backup;

/// <summary>
/// The fields of a manifest that stand in front of its file list. The totals are null in manifests written
/// before those fields existed.
/// </summary>
public sealed record ManifestHeader(string PlanId, string PlanName, DateTime CreatedUtc, int? FileCount, long? TotalBytes);

/// <summary>Reads <c>re-manifest.json</c> files, without their (possibly huge) file list where that is possible.</summary>
public static class ManifestReader
{
    private const int HeaderBufferSize = 64 * 1024;

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Reads the plan id, the name, the time and the totals of a manifest.</summary>
    /// <exception cref="IOException">The file is missing or cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="JsonException">The file is not a manifest.</exception>
    public static ManifestHeader ReadHeader(string manifestPath)
    {
        using var stream = Open(manifestPath);
        var buffer = new byte[(int)Math.Min(HeaderBufferSize, Math.Max(1L, stream.Length))];
        var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (TryReadHeader(buffer.AsSpan(0, length), isFinalBlock: length >= stream.Length) is { } header)
            return header;

        // Unusual layout or a header that does not fit the buffer: read everything.
        stream.Position = 0;
        var manifest = ReadManifest(stream);
        return new ManifestHeader(manifest.PlanId, manifest.PlanName, manifest.CreatedUtc,
            manifest.FileCount ?? manifest.Files.Count, manifest.TotalBytes ?? SumSizes(manifest));
    }

    /// <summary>Counts and sums the file list; for manifests without totals in their header.</summary>
    public static (int FileCount, long TotalBytes) ReadTotals(string manifestPath)
    {
        using var stream = Open(manifestPath);
        var manifest = ReadManifest(stream);
        return (manifest.Files.Count, SumSizes(manifest));
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static BackupManifest ReadManifest(Stream stream)
    {
        var manifest = JsonSerializer.Deserialize<BackupManifest>(stream, JsonDefaults.Options)
                       ?? throw new JsonException("The manifest is empty.");
        manifest.PlanId ??= "";
        manifest.PlanName ??= "";
        manifest.Files ??= [];
        return manifest;
    }

    private static long SumSizes(BackupManifest manifest)
    {
        long total = 0;
        foreach (var file in manifest.Files)
            total += file?.Size ?? 0;
        return total;
    }

    /// <summary>Null when the header does not end within <paramref name="json"/> or has an unexpected shape.</summary>
    private static ManifestHeader? TryReadHeader(ReadOnlySpan<byte> json, bool isFinalBlock)
    {
        if (json.StartsWith(Utf8Bom))
            json = json[Utf8Bom.Length..];

        string? planId = null;
        var planName = "";
        DateTime createdUtc = default;
        int? fileCount = null;
        long? totalBytes = null;
        try
        {
            var reader = new Utf8JsonReader(json, isFinalBlock, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;

            while (true)
            {
                if (!reader.Read())
                    return null;   // the header does not end within the buffer
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    return null;

                var name = reader.GetString();
                if (Is(name, "files"))
                    break;
                if (!reader.Read())
                    return null;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    if (!reader.TrySkip())
                        return null;
                    continue;
                }

                if (Is(name, "planId") && reader.TokenType == JsonTokenType.String)
                    planId = reader.GetString();
                else if (Is(name, "planName") && reader.TokenType == JsonTokenType.String)
                    planName = reader.GetString() ?? "";
                else if (Is(name, "createdUtc") && reader.TokenType == JsonTokenType.String && reader.TryGetDateTime(out var created))
                    createdUtc = created;
                else if (Is(name, "fileCount") && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var count))
                    fileCount = count;
                else if (Is(name, "totalBytes") && reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var bytes))
                    totalBytes = bytes;
            }
        }
        catch (JsonException)
        {
            return null;   // the full read reports what is wrong
        }

        return planId is null ? null : new ManifestHeader(planId, planName, createdUtc, fileCount, totalBytes);
    }

    private static bool Is(string? name, string expected) => string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 6: Write VersionCatalog**

Create `src/ReBackup.Core/Backup/VersionCatalog.cs`:

```csharp
using System.Text.Json;

namespace ReBackup.Core.Backup;

public enum VersionOwnership
{
    /// <summary>The manifest carries the plan's id: retention manages this version.</summary>
    Owned,

    /// <summary>Named like a version of the plan, but there is no manifest.</summary>
    NoManifest,

    /// <summary>Named like a version of the plan, but the manifest carries another plan's id.</summary>
    Foreign,

    /// <summary>Named like a version of the plan, but the manifest cannot be read.</summary>
    Unreadable,
}

/// <summary>A version folder in a target. Only <see cref="VersionOwnership.Owned"/> versions are ever deleted.</summary>
public sealed record VersionInfo(string Name, string Path, DateTime LocalTime, VersionOwnership Ownership,
    int? FileCount, long? TotalBytes)
{
    public bool IsOwned => Ownership == VersionOwnership.Owned;
}

/// <summary>Finds the versions of a plan in its target folder.</summary>
public static class VersionCatalog
{
    /// <summary>
    /// The versions of a plan, oldest first. A folder belongs to the plan when its name starts with a timestamp
    /// and its manifest carries the plan's id, whatever plan name the folder ends with: versions made before a
    /// rename stay with the plan. Folders that only carry the plan's current name are listed as not owned.
    /// </summary>
    /// <exception cref="IOException">The target cannot be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the target is denied.</exception>
    public static IReadOnlyList<VersionInfo> List(string target, string planId, string planName,
        CancellationToken cancellationToken = default)
    {
        var versions = new List<VersionInfo>();
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
            return versions;

        foreach (var directory in Directory.EnumerateDirectories(target))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = System.IO.Path.GetFileName(directory);
            if (VersionName.IsTransient(name) || !VersionName.TryParseAny(name, out var localTime, out var folderPlanName))
                continue;

            var (ownership, header) = Probe(directory, planId);
            if (ownership != VersionOwnership.Owned && !folderPlanName.Equals(planName, StringComparison.OrdinalIgnoreCase))
                continue;

            var fileCount = header?.FileCount;
            var totalBytes = header?.TotalBytes;
            if (ownership == VersionOwnership.Owned && (fileCount is null || totalBytes is null))
            {
                try
                {
                    (fileCount, totalBytes) = ManifestReader.ReadTotals(
                        System.IO.Path.Combine(directory, VersionName.ManifestFileName));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // The version is still ours; its size is just not known.
                }
            }

            versions.Add(new VersionInfo(name, directory, localTime, ownership, fileCount, totalBytes));
        }

        versions.Sort((a, b) => a.LocalTime != b.LocalTime
            ? a.LocalTime.CompareTo(b.LocalTime)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return versions;
    }

    /// <summary>Whether the manifest in a folder carries the plan's id. Never throws. The header is returned for owned folders only.</summary>
    public static (VersionOwnership Ownership, ManifestHeader? Header) Probe(string versionDirectory, string planId)
    {
        try
        {
            var header = ManifestReader.ReadHeader(System.IO.Path.Combine(versionDirectory, VersionName.ManifestFileName));
            return planId.Length > 0 && string.Equals(header.PlanId, planId, StringComparison.OrdinalIgnoreCase)
                ? (VersionOwnership.Owned, header)
                : (VersionOwnership.Foreign, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (VersionOwnership.NoManifest, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return (VersionOwnership.Unreadable, null);
        }
    }
}
```

(`System.IO.Path` is written out to keep it apart from the `Path` property of `VersionInfo` in the same file.)

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~VersionNameTests|FullyQualifiedName~ManifestReaderTests|FullyQualifiedName~VersionCatalogTests|FullyQualifiedName~BackupRunnerTests"`
Expected: PASS.

- [ ] **Step 8: Full build and test run**

Run: `dotnet build --no-incremental` — expected: 0 warnings, 0 errors.
Run: `dotnet test tests/ReBackup.Core.Tests` — expected: all tests pass.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat(core): manifest totals, header reader and version catalog" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 5: Removing versions and retention after a run

**Files:**
- Create: `src/ReBackup.Core/Backup/VersionRemover.cs`
- Create: `src/ReBackup.Core/Retention/RetentionPlanner.cs`
- Modify: `src/ReBackup.Core/Backup/TargetVolume.cs`, `src/ReBackup.Core/Backup/RunLog.cs`, `src/ReBackup.Core/Backup/BackupRunner.cs`
- Create: `tests/ReBackup.Core.Tests/TestSupport/ScriptedVolume.cs`
- Test: `tests/ReBackup.Core.Tests/Backup/VersionRemoverTests.cs`, `tests/ReBackup.Core.Tests/Retention/RetentionPlannerTests.cs`, `tests/ReBackup.Core.Tests/Backup/BackupRunnerRetentionTests.cs` (create)
- Test: `tests/ReBackup.Core.Tests/Backup/BackupRunnerTests.cs`, `tests/ReBackup.Core.Tests/Backup/RunLogTests.cs` (modify)

**Interfaces:**
- Consumes: `VersionCatalog.List`, `VersionCatalog.Probe`, `VersionInfo`, `VersionOwnership`, `VersionName.DeletingSuffix`, `VersionName.TryParseAny`, `VersionFolder.Create` (Task 4); `RetentionEngine.Evaluate`, `RetentionVersion`, `RetentionDecision`, `RetentionRule` (Tasks 1–2); `BackupRunner`, `ITargetVolume`, `RunLogEntry`.
- Produces:
  - `ITargetVolume.DeleteDirectory(string path)` — removes a folder and everything in it
  - `static class VersionRemover`: `void Remove(string versionPath, ITargetVolume volume)`, `void RemoveRemains(string doomedPath, ITargetVolume volume)` — both throw `IOException`/`UnauthorizedAccessException`
  - `sealed record VersionDecision(VersionInfo Version, RetentionDecision? Decision)` with `bool Delete`; `static class RetentionPlanner`: `IReadOnlyList<VersionDecision> Decide(IReadOnlyList<VersionInfo> versions, IReadOnlyList<RetentionRule> rules, DateTime? upcomingRun = null)` — same order as `versions`; versions that are not owned get `Decision == null`; throws `ArgumentException` for an invalid rule
  - `RunLogEntry.Warnings` (`List<string>`, never null after `ReadAll`)
  - `BackupPhase.Retention`
  - Test helper `ScriptedVolume : ITargetVolume` with `Func<long>? FreeSpace`, `Func<string, bool> FailMove`, `Func<string, bool> FailDelete`

- [ ] **Step 1: Write the test volume**

Create `tests/ReBackup.Core.Tests/TestSupport/ScriptedVolume.cs`:

```csharp
using ReBackup.Core.Backup;

namespace ReBackup.Core.Tests.TestSupport;

/// <summary>Works on the real disk, with switches to make renames or deletions fail and to dictate the free space.</summary>
public sealed class ScriptedVolume : ITargetVolume
{
    private readonly PhysicalTargetVolume _inner = new();

    /// <summary>Free space to report; the real value when null.</summary>
    public Func<long>? FreeSpace { get; init; }

    /// <summary>Gets the source path of a rename; true makes it fail.</summary>
    public Func<string, bool> FailMove { get; init; } = _ => false;

    /// <summary>Gets the path of a folder to delete; true makes it fail.</summary>
    public Func<string, bool> FailDelete { get; init; } = _ => false;

    public long GetAvailableFreeSpace(string directory) => FreeSpace?.Invoke() ?? _inner.GetAvailableFreeSpace(directory);

    public Stream CreateFile(string path) => _inner.CreateFile(path);

    public void MoveDirectory(string source, string destination)
    {
        if (FailMove(source))
            throw new IOException("the folder is in use");
        _inner.MoveDirectory(source, destination);
    }

    public void DeleteDirectory(string path)
    {
        if (FailDelete(path))
            throw new IOException("a file is in use");
        _inner.DeleteDirectory(path);
    }
}
```

In `tests/ReBackup.Core.Tests/Backup/BackupRunnerTests.cs`, add this member to the nested class `FakeVolume` (it implements `ITargetVolume`, which gains a method in this task):

```csharp
        public void DeleteDirectory(string path) => _inner.DeleteDirectory(path);
```

If any other class in the test project implements `ITargetVolume`, give it the same member.

- [ ] **Step 2: Write the failing tests**

Create `tests/ReBackup.Core.Tests/Backup/VersionRemoverTests.cs`:

```csharp
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Backup;

public class VersionRemoverTests : IDisposable
{
    private const string Name = "2026_09_01-02_00 Projects";
    private readonly TempDir _tmp = new();
    private readonly string _target;
    private readonly string _version;

    public VersionRemoverTests()
    {
        _target = _tmp.CreateDir("target");
        _version = VersionFolder.Create(_target, Name, "p1");
        _tmp.WriteFile($@"target\{Name}\sub\deep\c.txt", "x");
    }

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Removes_the_folder_with_everything_in_it()
    {
        VersionRemover.Remove(_version, new PhysicalTargetVolume());

        Directory.GetFileSystemEntries(_target).Should().BeEmpty();
    }

    [Fact]
    public void A_failing_rename_leaves_the_version_untouched()
    {
        var volume = new ScriptedVolume { FailMove = _ => true };

        var act = () => VersionRemover.Remove(_version, volume);

        act.Should().Throw<IOException>();
        File.Exists(Path.Combine(_version, "data.bin")).Should().BeTrue();
        File.Exists(Path.Combine(_version, "sub", "deep", "c.txt")).Should().BeTrue();
        Directory.Exists(_version + ".deleting").Should().BeFalse();
    }

    [Fact]
    public void A_removal_that_fails_half_way_leaves_a_deleting_folder_that_still_has_its_manifest()
    {
        var doomed = _version + ".deleting";
        var volume = new ScriptedVolume { FailDelete = path => path == doomed };

        var act = () => VersionRemover.Remove(_version, volume);

        act.Should().Throw<IOException>();
        Directory.Exists(_version).Should().BeFalse("it must no longer look like a version");
        File.Exists(Path.Combine(doomed, "re-manifest.json")).Should().BeTrue("the manifest goes last");
        File.Exists(Path.Combine(doomed, "data.bin")).Should().BeFalse();
        Directory.Exists(Path.Combine(doomed, "sub")).Should().BeFalse();

        VersionRemover.RemoveRemains(doomed, new PhysicalTargetVolume());
        Directory.GetFileSystemEntries(_target).Should().BeEmpty();
    }
}
```

Create `tests/ReBackup.Core.Tests/Retention/RetentionPlannerTests.cs`:

```csharp
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Retention;

namespace ReBackup.Core.Tests.Retention;

public class RetentionPlannerTests
{
    private static readonly RetentionRule[] KeepTwoDays = [new() { Period = RetentionPeriod.Daily, Keep = 2 }];

    private static VersionInfo Version(int day, VersionOwnership ownership = VersionOwnership.Owned)
    {
        var name = $"2026_09_{day:00}-02_00 Projects";
        return new VersionInfo(name, @"T:\" + name, new DateTime(2026, 9, day, 2, 0, 0), ownership, 1, 10);
    }

    [Fact]
    public void Decides_for_owned_versions_in_the_given_order()
    {
        VersionInfo[] versions = [Version(27), Version(28), Version(29)];

        var decisions = RetentionPlanner.Decide(versions, KeepTwoDays);

        decisions.Select(d => d.Version).Should().Equal(versions);
        decisions.Select(d => d.Delete).Should().Equal(true, false, false);
        decisions[2].Decision!.Reasons.Select(r => r.Label).Should().Equal("Daily #1");
    }

    [Fact]
    public void Versions_that_are_not_owned_get_no_decision_and_do_not_count()
    {
        VersionInfo[] versions = [Version(27), Version(28, VersionOwnership.Foreign), Version(29, VersionOwnership.NoManifest), Version(30)];

        var decisions = RetentionPlanner.Decide(versions, KeepTwoDays);

        decisions.Select(d => d.Decision is null).Should().Equal(false, true, true, false);
        decisions.Select(d => d.Delete).Should().Equal(false, false, false, false);
    }

    [Fact]
    public void An_upcoming_run_is_counted_as_if_its_version_already_existed()
    {
        VersionInfo[] versions = [Version(28), Version(29)];

        RetentionPlanner.Decide(versions, KeepTwoDays).Select(d => d.Delete).Should().Equal(false, false);

        var decisions = RetentionPlanner.Decide(versions, KeepTwoDays, upcomingRun: new DateTime(2026, 9, 30, 2, 0, 0));
        decisions.Should().HaveCount(2, "the upcoming version itself is not part of the result");
        decisions.Select(d => d.Delete).Should().Equal(true, false);
    }

    [Fact]
    public void Without_rules_nothing_is_deleted()
    {
        RetentionPlanner.Decide([Version(28), Version(29)], []).Should().OnlyContain(d => !d.Delete);
    }

    [Fact]
    public void An_invalid_rule_is_rejected()
    {
        var act = () => RetentionPlanner.Decide([Version(28)], [new RetentionRule { Period = RetentionPeriod.Daily, Keep = 0 }]);

        act.Should().Throw<ArgumentException>();
    }
}
```

Create `tests/ReBackup.Core.Tests/Backup/BackupRunnerRetentionTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Retention;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Backup;

public class BackupRunnerRetentionTests : IDisposable
{
    private const string NewVersion = "2026_09_30-16_05 Projects";   // 14:05 UTC in the fixed +02:00 test zone
    private readonly TempDir _tmp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.Zero));
    private readonly string _source;
    private readonly string _target;

    public BackupRunnerRetentionTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
        _source = _tmp.CreateDir("source");
        _target = _tmp.CreateDir("target");
        _tmp.WriteFile(@"source\a.txt", "alpha");
        _tmp.WriteFile(@"source\sub\b.bin", "bravo-bravo");
    }

    public void Dispose() => _tmp.Dispose();

    private static RetentionRule Daily(int keep) => new() { Period = RetentionPeriod.Daily, Keep = keep };

    private BackupPlan Plan(params RetentionRule[] rules) => new()
    {
        Id = "p1",
        Name = "Projects",
        Source = _source,
        Target = _target,
        Retention = [.. rules],
    };

    private Task<RunLogEntry> Run(BackupPlan plan, ITargetVolume? volume = null, IProgress<BackupProgress>? progress = null) =>
        new BackupRunner(volume ?? new PhysicalTargetVolume(), _time)
            .RunAsync(new BackupRequest(plan, [], RunTrigger.Manual), progress);

    /// <summary>An existing version folder of 2026-09-<paramref name="day"/> 02:00.</summary>
    private string Old(int day, string planId = "p1", string name = "Projects", long bytes = 10) =>
        VersionFolder.Create(_target, OldName(day, name), planId, bytes);

    private static string OldName(int day, string name = "Projects") => $"2026_09_{day:00}-02_00 {name}";

    private string[] TargetEntries() =>
        Directory.GetFileSystemEntries(_target).Select(e => Path.GetFileName(e)!).ToArray();

    [Fact]
    public async Task Retention_deletes_the_versions_the_rules_no_longer_keep()
    {
        Old(26);
        Old(27);
        Old(28);
        Old(29);

        var entry = await Run(Plan(Daily(2)));

        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().Equal(OldName(26), OldName(27), OldName(28));
        entry.Warnings.Should().BeEmpty();
        TargetEntries().Should().BeEquivalentTo(OldName(29), NewVersion);
    }

    [Fact]
    public async Task Without_rules_nothing_is_deleted()
    {
        Old(26);
        Old(27);

        var entry = await Run(Plan());

        entry.RetentionDeleted.Should().BeEmpty();
        TargetEntries().Should().BeEquivalentTo(OldName(26), OldName(27), NewVersion);
    }

    [Fact]
    public async Task Only_versions_whose_manifest_carries_the_plan_id_are_deleted()
    {
        Old(1, planId: "other");
        _tmp.WriteFile($@"target\{OldName(2)}\a.txt", "no manifest");
        Old(3);

        var entry = await Run(Plan(Daily(1)));

        entry.RetentionDeleted.Should().Equal(OldName(3));
        TargetEntries().Should().BeEquivalentTo(OldName(1), OldName(2), NewVersion);
    }

    [Fact]
    public async Task Versions_made_under_an_earlier_plan_name_are_managed_too()
    {
        Old(3, name: "Old name");

        var entry = await Run(Plan(Daily(1)));

        entry.RetentionDeleted.Should().Equal(OldName(3, "Old name"));
        TargetEntries().Should().BeEquivalentTo(NewVersion);
    }

    [Fact]
    public async Task A_version_that_cannot_be_deleted_is_a_warning_and_the_run_stays_completed()
    {
        Old(26);
        var stubborn = Old(27);
        var volume = new ScriptedVolume { FailMove = source => source == stubborn };

        var entry = await Run(Plan(Daily(1)), volume);

        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().Equal(OldName(26));
        entry.Warnings.Should().ContainSingle().Which.Should()
            .Be($"Retention could not delete \"{OldName(27)}\": the folder is in use");
        File.Exists(Path.Combine(stubborn, "data.bin")).Should().BeTrue();
    }

    [Fact]
    public async Task Retention_does_not_run_after_a_failed_run()
    {
        Old(26);
        Old(27);
        var volume = new ScriptedVolume { FreeSpace = () => 0 };

        var entry = await Run(Plan(Daily(1)), volume);

        entry.Status.Should().Be(RunStatus.Full);
        entry.RetentionDeleted.Should().BeEmpty();
        TargetEntries().Should().BeEquivalentTo(OldName(26), OldName(27));
    }

    [Fact]
    public async Task Invalid_rules_skip_retention_with_a_warning()
    {
        Old(26);

        var entry = await Run(Plan(Daily(0)));

        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().BeEmpty();
        entry.Warnings.Should().ContainSingle().Which.Should().StartWith("Retention was skipped: Retention rule 1: keep must be");
        TargetEntries().Should().BeEquivalentTo(OldName(26), NewVersion);
    }

    [Fact]
    public async Task The_version_just_made_is_never_deleted_even_when_a_later_dated_one_exists()
    {
        VersionFolder.Create(_target, "2026_10_05-10_00 Projects", "p1");

        var entry = await Run(Plan(Daily(1)));

        entry.RetentionDeleted.Should().BeEmpty();
        TargetEntries().Should().BeEquivalentTo("2026_10_05-10_00 Projects", NewVersion);
    }

    [Fact]
    public async Task Remains_of_an_interrupted_removal_are_cleaned_up_at_the_next_run()
    {
        VersionFolder.Create(_target, OldName(1) + ".deleting", "p1");
        VersionFolder.Create(_target, OldName(2) + ".deleting", "other");
        _tmp.WriteFile($@"target\{OldName(3)}.deleting\a.txt", "remains without a manifest");
        _tmp.WriteFile($@"target\{OldName(4, "Other")}.deleting\a.txt", "remains of another plan");

        var entry = await Run(Plan());

        entry.Status.Should().Be(RunStatus.Completed);
        TargetEntries().Should().BeEquivalentTo(OldName(2) + ".deleting", OldName(4, "Other") + ".deleting", NewVersion);
    }

    [Fact]
    public async Task A_plan_name_ending_in_deleting_aborts_as_Error()
    {
        var plan = Plan();
        plan.Name = "Projects.deleting";

        var entry = await Run(plan);

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Be("The plan name must not end with \".deleting\".");
        TargetEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Reports_a_retention_phase_when_there_are_rules()
    {
        Old(26);
        var phases = new List<BackupPhase>();

        await Run(Plan(Daily(1)), progress: new SyncProgress(p => phases.Add(p.Phase)));

        phases.Last().Should().Be(BackupPhase.Retention);
        new BackupProgress(BackupPhase.Retention, 0, 0, 0, 0, "").Fraction.Should().Be(1);
    }

    private sealed class SyncProgress(Action<BackupProgress> onReport) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => onReport(value);
    }
}
```

In `tests/ReBackup.Core.Tests/Backup/RunLogTests.cs`, add to the class:

```csharp
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
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~VersionRemoverTests|FullyQualifiedName~RetentionPlannerTests|FullyQualifiedName~BackupRunnerRetentionTests|FullyQualifiedName~RunLogTests"`
Expected: FAIL — compile errors (`DeleteDirectory`, `VersionRemover`, `RetentionPlanner`, `Warnings`, `BackupPhase.Retention` do not exist).

- [ ] **Step 4: Volume, log entry and remover**

In `src/ReBackup.Core/Backup/TargetVolume.cs`:

1. Add to the interface `ITargetVolume`:

```csharp

    /// <summary>Removes a folder and everything in it.</summary>
    void DeleteDirectory(string path);
```

2. Add to `PhysicalTargetVolume`:

```csharp

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);
```

In `src/ReBackup.Core/Backup/RunLog.cs`:

1. Directly below `public List<string> RetentionDeleted { get; set; } = [];`, add:

```csharp

    /// <summary>Problems that are not about a source file, e.g. a version retention could not delete. They do not change the status.</summary>
    public List<string> Warnings { get; set; } = [];
```

2. In `ReadAll`, directly below `entry.RetentionDeleted ??= [];`, add:

```csharp
                entry.Warnings ??= [];
```

Create `src/ReBackup.Core/Backup/VersionRemover.cs`:

```csharp
namespace ReBackup.Core.Backup;

/// <summary>Removes version folders in a way that never leaves something that still looks like a complete version.</summary>
public static class VersionRemover
{
    /// <summary>
    /// Renames the folder to <c>&lt;name&gt;.deleting</c> and then deletes it. When the rename fails, nothing has
    /// changed. When the deletion fails half-way, the remains keep the ".deleting" name and are cleaned up by the
    /// next run of the plan.
    /// </summary>
    /// <exception cref="IOException">The folder is in use or the disk reports an error.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static void Remove(string versionPath, ITargetVolume volume)
    {
        var doomed = versionPath + VersionName.DeletingSuffix;
        volume.MoveDirectory(versionPath, doomed);
        RemoveRemains(doomed, volume);
    }

    /// <summary>
    /// Deletes a folder that already has the ".deleting" name. The manifest goes last: while it exists, the remains
    /// can still be attributed to a plan.
    /// </summary>
    public static void RemoveRemains(string doomedPath, ITargetVolume volume)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(doomedPath).ToList())
        {
            if (Directory.Exists(entry))
                volume.DeleteDirectory(entry);
            else if (!Path.GetFileName(entry).Equals(VersionName.ManifestFileName, StringComparison.OrdinalIgnoreCase))
                File.Delete(entry);
        }
        volume.DeleteDirectory(doomedPath);
    }
}
```

- [ ] **Step 5: Write RetentionPlanner**

Create `src/ReBackup.Core/Retention/RetentionPlanner.cs`:

```csharp
using ReBackup.Core.Backup;

namespace ReBackup.Core.Retention;

/// <summary>A version folder and what retention decides for it. No decision: retention does not manage the folder.</summary>
public sealed record VersionDecision(VersionInfo Version, RetentionDecision? Decision)
{
    /// <summary>True when retention deletes this version.</summary>
    public bool Delete => Decision is { Keep: false };
}

/// <summary>Applies retention rules to the version folders of a target.</summary>
public static class RetentionPlanner
{
    private const string UpcomingName = "\0upcoming";

    /// <summary>
    /// Decides for every owned version; the result has the order of <paramref name="versions"/>. With
    /// <paramref name="upcomingRun"/> the decision is made as if a version of that time already existed.
    /// </summary>
    /// <exception cref="ArgumentException">A rule is not valid.</exception>
    public static IReadOnlyList<VersionDecision> Decide(IReadOnlyList<VersionInfo> versions,
        IReadOnlyList<RetentionRule> rules, DateTime? upcomingRun = null)
    {
        var input = versions.Where(v => v.IsOwned).Select(v => new RetentionVersion(v.Name, v.LocalTime)).ToList();
        if (upcomingRun is { } time)
            input.Add(new RetentionVersion(UpcomingName, time));

        var byName = new Dictionary<string, RetentionDecision>(StringComparer.OrdinalIgnoreCase);
        foreach (var decision in RetentionEngine.Evaluate(input, rules))
            byName[decision.Version.Name] = decision;

        return versions.Select(v => new VersionDecision(v, v.IsOwned ? byName[v.Name] : null)).ToArray();
    }
}
```

- [ ] **Step 6: Retention in the runner**

In `src/ReBackup.Core/Backup/BackupRunner.cs`:

1. Add `using ReBackup.Core.Retention;` to the usings.

2. Replace the `BackupPhase` enum and the `Fraction` property of `BackupProgress`:

```csharp
public enum BackupPhase { Indexing, Copying, Finishing, Retention }
```

```csharp
    /// <summary>0..1, by bytes; by files when there are no bytes to copy.</summary>
    public double Fraction =>
        BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1)
        : FilesTotal > 0 ? Math.Clamp((double)FilesDone / FilesTotal, 0, 1)
        : Phase is BackupPhase.Finishing or BackupPhase.Retention ? 1 : 0;
```

3. In `RunAsync`, directly below

```csharp
        if (!completed && partialPath is not null)
            TryDeleteDirectory(partialPath);
```

add:

```csharp

        if (completed)
        {
            try
            {
                await Task.Run(() => ApplyRetention(plan, entry, progress, cancellationToken));
            }
            catch (Exception ex)
            {
                // The backup itself is done; whatever goes wrong here must not turn it into a failure.
                entry.Warnings.Add($"Retention was skipped: {ex.Message}");
            }
        }
```

4. In `Prepare`, directly below the two lines that reject a plan name ending in `.partial`, add:

```csharp
        if (plan.Name.EndsWith(VersionName.DeletingSuffix, StringComparison.OrdinalIgnoreCase))
            throw new BackupAbortException(RunStatus.Error, "The plan name must not end with \".deleting\".");
```

5. Replace the whole method `DeleteLeftovers` with:

```csharp
    /// <summary>Removes what earlier runs of this plan left behind: unfinished ".partial" folders and ".deleting" remains.</summary>
    private void DeleteLeftovers(BackupPlan plan)
    {
        foreach (var directory in Directory.EnumerateDirectories(plan.Target).ToList())
        {
            var name = Path.GetFileName(directory);
            if (name.EndsWith(VersionName.PartialSuffix, StringComparison.OrdinalIgnoreCase))
            {
                if (!VersionName.TryParse(name[..^VersionName.PartialSuffix.Length], plan.Name, out _))
                    continue;
                // A manifest is written last, right before the rename: such a folder is a finished version of a
                // plan whose name ends in ".partial" (or a crash just before the rename). Leaving it is the safe
                // choice, and so is leaving a folder that cannot be examined.
                if (HasManifest(directory) != false)
                    continue;
                TryDeleteDirectory(directory);
            }
            else if (name.EndsWith(VersionName.DeletingSuffix, StringComparison.OrdinalIgnoreCase))
            {
                if (!VersionName.TryParseAny(name[..^VersionName.DeletingSuffix.Length], out _, out var folderPlanName) ||
                    !IsOwnRemains(directory, plan, folderPlanName))
                    continue;
                try
                {
                    VersionRemover.RemoveRemains(directory, _volume);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort; the next run tries again.
                }
            }
        }
    }

    /// <summary>True or false when it is known; null when the folder cannot be examined.</summary>
    private static bool? HasManifest(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, VersionName.ManifestFileName).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Remains belong to the plan by their manifest; without one (it is deleted last), by their name.</summary>
    private static bool IsOwnRemains(string directory, BackupPlan plan, string folderPlanName) =>
        VersionCatalog.Probe(directory, plan.Id).Ownership switch
        {
            VersionOwnership.Owned => true,
            VersionOwnership.NoManifest =>
                HasManifest(directory) == false && folderPlanName.Equals(plan.Name, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    /// <summary>Deletes the versions the plan's rules no longer keep. Problems become warnings; the run stays successful.</summary>
    private void ApplyRetention(BackupPlan plan, RunLogEntry entry, IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (plan.Retention.Count == 0)
            return;

        progress?.Report(new BackupProgress(BackupPhase.Retention, entry.FilesCopied, entry.FilesCopied,
            entry.BytesCopied, entry.BytesCopied, ""));

        List<VersionInfo> doomed;
        try
        {
            var versions = VersionCatalog.List(plan.Target, plan.Id, plan.Name);
            doomed = RetentionPlanner.Decide(versions, plan.Retention).Where(d => d.Delete).Select(d => d.Version).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            entry.Warnings.Add($"Retention was skipped: {ex.Message}");
            return;
        }

        foreach (var version in doomed)
        {
            if (cancellationToken.IsCancellationRequested)
                return;   // the next successful run deletes the rest
            if (version.Name.Equals(entry.Version, StringComparison.OrdinalIgnoreCase))
                continue;   // never the version this run just made, whatever the clock or the rules say

            try
            {
                VersionRemover.Remove(version.Path, _volume);
                entry.RetentionDeleted.Add(version.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entry.Warnings.Add($"Retention could not delete \"{version.Name}\": {ex.Message}");
            }
        }
    }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~VersionRemoverTests|FullyQualifiedName~RetentionPlannerTests|FullyQualifiedName~BackupRunner|FullyQualifiedName~RunLogTests"`
Expected: PASS, including every existing `BackupRunnerTests` test.

- [ ] **Step 8: Full build and test run**

Run: `dotnet build --no-incremental` — expected: 0 warnings, 0 errors. (The App compiles unchanged: the new enum value falls into the existing default branch of its progress text.)
Run: `dotnet test tests/ReBackup.Core.Tests` — expected: all tests pass.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat(core): retention after a successful run, safe version removal and cleanup of remains" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 6: Freeing space by retention before a run

**Files:**
- Modify: `src/ReBackup.Core/Backup/BackupRunner.cs`
- Test: `tests/ReBackup.Core.Tests/Backup/BackupRunnerRetentionTests.cs` (modify)

**Interfaces:**
- Consumes: `RetentionPlanner.Decide(versions, rules, upcomingRun)`, `VersionRemover.Remove`, `VersionCatalog.List`, `RunLogEntry.RetentionDeleted`, `RunLogEntry.Warnings`, `ScriptedVolume` (Tasks 4–5); `BackupPlan.FreeSpaceByRetention`.
- Produces: no new public API. Behaviour (spec §7.2): when `FreeSpaceByRetention` is on and the preflight finds too little space, the runner deletes the versions retention would delete after this run (decided as if the new version already existed), oldest first, re-checking the free space after each. It deletes nothing unless the known sizes of those versions add up to enough room, and it never deletes the newest existing version. Deleted names go to `RetentionDeleted` even if the run then fails.

The source in these tests holds 16 bytes, so the preflight needs 17 bytes (16 × 1.05, rounded up).

- [ ] **Step 1: Write the failing tests**

In `tests/ReBackup.Core.Tests/Backup/BackupRunnerRetentionTests.cs`, add these members to the class (above the nested `SyncProgress` class):

```csharp
    private BackupPlan FreeingPlan(params RetentionRule[] rules)
    {
        var plan = Plan(rules);
        plan.FreeSpaceByRetention = true;
        return plan;
    }

    /// <summary>Reports 2 free bytes plus 10 for each of the given old versions that is gone.</summary>
    private Func<long> FreedBy(params int[] days) =>
        () => 2 + 10 * days.Count(day => !Directory.Exists(Path.Combine(_target, OldName(day))));

    [Fact]
    public async Task Frees_space_by_deleting_the_oldest_versions_retention_would_delete_anyway()
    {
        for (var day = 25; day <= 29; day++)
            Old(day);
        var volume = new ScriptedVolume { FreeSpace = FreedBy(25, 26, 27) };

        var entry = await Run(FreeingPlan(Daily(3)), volume);

        entry.Status.Should().Be(RunStatus.Completed);
        // 25 and 26 made room before the run; 27 went in the normal retention pass after it.
        entry.RetentionDeleted.Should().Equal(OldName(25), OldName(26), OldName(27));
        entry.Warnings.Should().BeEmpty();
        TargetEntries().Should().BeEquivalentTo(OldName(28), OldName(29), NewVersion);
    }

    [Fact]
    public async Task Does_not_free_space_when_the_option_is_off()
    {
        for (var day = 25; day <= 29; day++)
            Old(day);
        var volume = new ScriptedVolume { FreeSpace = FreedBy(25, 26, 27) };

        var entry = await Run(Plan(Daily(3)), volume);

        entry.Status.Should().Be(RunStatus.Full);
        entry.RetentionDeleted.Should().BeEmpty();
        TargetEntries().Should().HaveCount(5);
    }

    [Fact]
    public async Task Deletes_nothing_when_even_all_deletable_versions_would_not_make_enough_room()
    {
        for (var day = 25; day <= 29; day++)
            Old(day, bytes: 1);
        var volume = new ScriptedVolume { FreeSpace = () => 2 };

        var entry = await Run(FreeingPlan(Daily(3)), volume);

        entry.Status.Should().Be(RunStatus.Full);
        entry.RetentionDeleted.Should().BeEmpty();
        TargetEntries().Should().HaveCount(5);
    }

    [Fact]
    public async Task Never_deletes_the_newest_existing_version_to_make_room()
    {
        Old(29, bytes: 100);
        var volume = new ScriptedVolume { FreeSpace = () => 2 };

        var entry = await Run(FreeingPlan(Daily(1)), volume);

        entry.Status.Should().Be(RunStatus.Full);
        entry.RetentionDeleted.Should().BeEmpty();
        TargetEntries().Should().BeEquivalentTo(OldName(29));
    }

    [Fact]
    public async Task Records_what_it_deleted_even_when_the_space_is_still_not_enough()
    {
        for (var day = 25; day <= 29; day++)
            Old(day);
        var volume = new ScriptedVolume { FreeSpace = () => 2 };   // deleting does not help on this volume

        var entry = await Run(FreeingPlan(Daily(3)), volume);

        entry.Status.Should().Be(RunStatus.Full);
        entry.RetentionDeleted.Should().Equal(OldName(25), OldName(26), OldName(27));
        TargetEntries().Should().BeEquivalentTo(OldName(28), OldName(29));
    }

    [Fact]
    public async Task Without_rules_there_is_nothing_to_free()
    {
        Old(28);
        Old(29);
        var volume = new ScriptedVolume { FreeSpace = () => 2 };

        var entry = await Run(FreeingPlan(), volume);

        entry.Status.Should().Be(RunStatus.Full);
        TargetEntries().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_version_that_cannot_be_deleted_to_make_room_is_a_warning_and_the_next_one_is_tried()
    {
        for (var day = 25; day <= 29; day++)
            Old(day);
        var stubborn = Path.Combine(_target, OldName(25));
        var volume = new ScriptedVolume { FreeSpace = FreedBy(25, 26, 27), FailMove = source => source == stubborn };

        var entry = await Run(FreeingPlan(Daily(3)), volume);

        entry.Status.Should().Be(RunStatus.Completed);
        entry.RetentionDeleted.Should().Equal(OldName(26), OldName(27));
        entry.Warnings.Should().HaveCount(2, "once before the run and once in the retention pass after it");
        entry.Warnings[0].Should().Be($"\"{OldName(25)}\" could not be deleted to free space: the folder is in use");
        Directory.Exists(stubborn).Should().BeTrue();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~BackupRunnerRetentionTests"`
Expected: the new tests that expect deletions or `Completed` FAIL (`Frees_space_…`, `Records_what_it_deleted_…`, `A_version_that_cannot_be_deleted_to_make_room_…`); the others pass.

- [ ] **Step 3: Free space in the preflight**

In `src/ReBackup.Core/Backup/BackupRunner.cs`:

1. `Prepare` needs the log entry. Change its signature to

```csharp
    private BackupWork Prepare(BackupRequest request, RunLogEntry entry, IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
```

and the call in `RunAsync` to

```csharp
            var work = await Task.Run(() => Prepare(request, entry, progress, cancellationToken), cancellationToken);
```

2. In `Prepare`, replace

```csharp
        var free = _volume.GetAvailableFreeSpace(plan.Target);
        if (required > free)
```

with

```csharp
        var free = _volume.GetAvailableFreeSpace(plan.Target);
        if (required > free && plan.FreeSpaceByRetention)
            free = FreeSpaceByRetention(plan, required, free, entry, cancellationToken);
        if (required > free)
```

3. Add this method below `Prepare`:

```csharp
    /// <summary>
    /// Makes room by deleting versions that retention would delete after this run anyway, oldest first. Nothing is
    /// deleted unless that can make the run fit, and the newest existing version always stays. Returns the free space.
    /// </summary>
    private long FreeSpaceByRetention(BackupPlan plan, long required, long free, RunLogEntry entry,
        CancellationToken cancellationToken)
    {
        if (plan.Retention.Count == 0)
            return free;

        List<VersionInfo> candidates;
        try
        {
            var versions = VersionCatalog.List(plan.Target, plan.Id, plan.Name, cancellationToken);
            var newest = versions.LastOrDefault(v => v.IsOwned);
            candidates = RetentionPlanner.Decide(versions, plan.Retention, upcomingRun: _time.GetLocalNow().DateTime)
                .Where(d => d.Delete && !ReferenceEquals(d.Version, newest))
                .Select(d => d.Version)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            entry.Warnings.Add($"Old versions could not be examined to free space: {ex.Message}");
            return free;
        }

        if (free + candidates.Sum(v => v.TotalBytes ?? 0) < required)
            return free;

        foreach (var version in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                VersionRemover.Remove(version.Path, _volume);
                entry.RetentionDeleted.Add(version.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entry.Warnings.Add($"\"{version.Name}\" could not be deleted to free space: {ex.Message}");
                continue;
            }

            free = _volume.GetAvailableFreeSpace(plan.Target);
            if (required <= free)
                break;
        }
        return free;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~BackupRunner"`
Expected: PASS.

- [ ] **Step 5: Full build and test run**

Run: `dotnet build --no-incremental` — expected: 0 warnings, 0 errors.
Run: `dotnet test tests/ReBackup.Core.Tests` — expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): free space by retention in the preflight" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 7: Retention tab — rule editor

**Files:**
- Create: `src/ReBackup.App/ViewModels/RetentionRuleViewModel.cs`
- Create: `src/ReBackup.App/Views/RetentionView.xaml`, `src/ReBackup.App/Views/RetentionView.xaml.cs`
- Modify: `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`
- Modify: `src/ReBackup.App/ViewModels/RunHistoryRow.cs`, `src/ReBackup.App/ViewModels/PlanRunViewModel.cs`
- Modify: `src/ReBackup.App/MainWindow.xaml`

**Interfaces:**
- Consumes: `RetentionRule`, `RetentionPeriod`, `RetentionRules.Validate`, `RetentionRules.TryGetWeekday`, `BackupPlan.Retention` (Task 1); `RunLogEntry.Warnings`, `BackupPhase.Retention` (Task 5).
- Produces:
  - `RetentionRuleViewModel` (`Period`, `AnchorText`, `KeepText`, `Error`, `AnchorHint`, static `Periods`, static `Weekdays`, event `Changed`, `RetentionRule ToRule()`)
  - `PlanEditorViewModel.RetentionRuleRows` (`ObservableCollection<RetentionRuleViewModel>`), `HasNoRetentionRules`, `AddRetentionRuleCommand`, `RemoveRetentionRuleCommand` (parameter: the row), and `ToPlan()` now carries the edited rules in `Retention`
  - `RetentionView` with a three-row grid; rows 1 and 2 are filled by Tasks 8 and 9

- [ ] **Step 1: Rule row view model**

Create `src/ReBackup.App/ViewModels/RetentionRuleViewModel.cs`:

```csharp
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Retention;

namespace ReBackup.App.ViewModels;

/// <summary>One retention rule of a plan while it is edited.</summary>
public sealed partial class RetentionRuleViewModel : ObservableObject
{
    private readonly bool _ready;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnchorHint))]
    private RetentionPeriod _period;

    [ObservableProperty] private string _anchorText = "";
    [ObservableProperty] private string _keepText = "1";
    [ObservableProperty] private string? _error;

    public RetentionRuleViewModel(RetentionRule rule)
    {
        Period = rule.Period;
        // The weekday list of the view holds full names.
        AnchorText = rule.Period == RetentionPeriod.Weekly && RetentionRules.TryGetWeekday(rule.Anchor, out var day)
            ? day.ToString()
            : rule.Anchor ?? "";
        KeepText = rule.Keep.ToString(CultureInfo.InvariantCulture);
        _ready = true;
        Refresh();
    }

    /// <summary>Raised after every edit of this rule.</summary>
    public event Action? Changed;

    public static IReadOnlyList<RetentionPeriod> Periods { get; } = Enum.GetValues<RetentionPeriod>();

    public static IReadOnlyList<string> Weekdays { get; } =
        ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    public string AnchorHint => Period switch
    {
        RetentionPeriod.Daily => "the last backup of each day",
        RetentionPeriod.Weekly => "the backup of that weekday, otherwise the first one after it",
        RetentionPeriod.Monthly => "day of the month: 1 to 31, 0 = last day, -1 = the day before the last day, …",
        _ => "date as MM-DD, for example 01-01",
    };

    partial void OnPeriodChanged(RetentionPeriod value)
    {
        if (!_ready)
            return;
        AnchorText = value switch
        {
            RetentionPeriod.Weekly => "Sunday",
            RetentionPeriod.Monthly => "1",
            RetentionPeriod.Yearly => "01-01",
            _ => "",
        };
        OnEdited();
    }

    partial void OnAnchorTextChanged(string value) => OnEdited();

    partial void OnKeepTextChanged(string value) => OnEdited();

    public RetentionRule ToRule() => new()
    {
        Period = Period,
        Anchor = Period == RetentionPeriod.Daily || string.IsNullOrWhiteSpace(AnchorText) ? null : AnchorText.Trim(),
        Keep = int.TryParse((KeepText ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var keep) ? keep : 0,
    };

    private void OnEdited()
    {
        if (!_ready)
            return;
        Refresh();
        Changed?.Invoke();
    }

    private void Refresh() => Error = RetentionRules.Validate(ToRule());
}
```

- [ ] **Step 2: Rules in the plan editor**

In `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`:

1. Add to the usings:

```csharp
using System.Collections.ObjectModel;
using ReBackup.Core.Retention;
```

2. Directly below the `Run` property, add:

```csharp
    /// <summary>The retention rules as edited.</summary>
    public ObservableCollection<RetentionRuleViewModel> RetentionRuleRows { get; } = [];

    public bool HasNoRetentionRules => RetentionRuleRows.Count == 0;
```

3. In `ToPlan()`, directly before `return plan;`, add:

```csharp
        plan.Retention = RetentionRuleRows.Select(row => row.ToRule()).ToList();
```

4. Directly above `public void Validate()`, add:

```csharp
    [RelayCommand]
    private void AddRetentionRule()
    {
        AddRetentionRow(new RetentionRule { Period = RetentionPeriod.Daily, Keep = 7 });
        OnRetentionRulesEdited();
    }

    [RelayCommand]
    private void RemoveRetentionRule(RetentionRuleViewModel? row)
    {
        if (row is null || !RetentionRuleRows.Remove(row))
            return;
        row.Changed -= OnRetentionRulesEdited;
        OnRetentionRulesEdited();
    }

    private void AddRetentionRow(RetentionRule rule)
    {
        var row = new RetentionRuleViewModel(rule);
        row.Changed += OnRetentionRulesEdited;
        RetentionRuleRows.Add(row);
    }

    private void OnRetentionRulesEdited()
    {
        OnPropertyChanged(nameof(HasNoRetentionRules));
        Touch();
    }
```

5. In `LoadFrom`, inside the `try` block directly below `HonorNestedIgnoreFiles = plan.Ignore.HonorNestedFiles;`, add:

```csharp

            foreach (var row in RetentionRuleRows)
                row.Changed -= OnRetentionRulesEdited;
            RetentionRuleRows.Clear();
            foreach (var rule in plan.Retention)
                AddRetentionRow(rule);
```

and directly after the closing brace of the `finally` block (still inside `LoadFrom`), add:

```csharp
        OnPropertyChanged(nameof(HasNoRetentionRules));
```

- [ ] **Step 3: Warnings in the history, retention phase in the progress text**

In `src/ReBackup.App/ViewModels/RunHistoryRow.cs`:

1. In `StatusText`, add this arm directly above `_ => "Completed",`:

```csharp
        RunStatus.Completed when _entry.Warnings.Count > 0 => "Completed (retention warnings)",
```

2. In `Details`, directly above `return string.Join(Environment.NewLine, lines);`, add:

```csharp
            if (_entry.Warnings.Count > 0)
            {
                lines.Add("Warnings:");
                lines.AddRange(_entry.Warnings.Select(w => "  " + w));
            }
```

In `src/ReBackup.App/ViewModels/PlanRunViewModel.cs`, in `ShowProgress`, add this arm directly above `_ => "Finishing…",`:

```csharp
            BackupPhase.Retention => "Removing old versions…",
```

- [ ] **Step 4: The view**

Create `src/ReBackup.App/Views/RetentionView.xaml`:

```xml
<UserControl x:Class="ReBackup.App.Views.RetentionView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:ReBackup.App.ViewModels">
    <Grid Margin="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <StackPanel Grid.Row="0">
            <DockPanel Margin="0,0,0,6">
                <Button DockPanel.Dock="Right" Content="Add rule" Padding="10,2"
                        Command="{Binding AddRetentionRuleCommand}" />
                <TextBlock FontWeight="SemiBold" VerticalAlignment="Center" TextWrapping="Wrap"
                           Text="Retention rules — a version is kept if at least one rule keeps it; the newest version is always kept" />
            </DockPanel>

            <TextBlock Foreground="DarkOrange" TextWrapping="Wrap"
                       Text="No rules: every version is kept and the target fills up over time.">
                <TextBlock.Style>
                    <Style TargetType="TextBlock">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding HasNoRetentionRules}" Value="False">
                                <Setter Property="Visibility" Value="Collapsed" />
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>

            <ScrollViewer MaxHeight="170" VerticalScrollBarVisibility="Auto">
                <ItemsControl ItemsSource="{Binding RetentionRuleRows}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <StackPanel Margin="0,0,0,4">
                                <StackPanel Orientation="Horizontal">
                                    <ComboBox Width="90" ItemsSource="{x:Static vm:RetentionRuleViewModel.Periods}"
                                              SelectedItem="{Binding Period}" />

                                    <Grid Width="130" Margin="8,0,0,0">
                                        <ComboBox ItemsSource="{x:Static vm:RetentionRuleViewModel.Weekdays}"
                                                  SelectedItem="{Binding AnchorText}">
                                            <ComboBox.Style>
                                                <Style TargetType="ComboBox">
                                                    <Setter Property="Visibility" Value="Collapsed" />
                                                    <Style.Triggers>
                                                        <DataTrigger Binding="{Binding Period}" Value="Weekly">
                                                            <Setter Property="Visibility" Value="Visible" />
                                                        </DataTrigger>
                                                    </Style.Triggers>
                                                </Style>
                                            </ComboBox.Style>
                                        </ComboBox>
                                        <TextBox Text="{Binding AnchorText, UpdateSourceTrigger=PropertyChanged}">
                                            <TextBox.Style>
                                                <Style TargetType="TextBox">
                                                    <Setter Property="Visibility" Value="Collapsed" />
                                                    <Style.Triggers>
                                                        <DataTrigger Binding="{Binding Period}" Value="Monthly">
                                                            <Setter Property="Visibility" Value="Visible" />
                                                        </DataTrigger>
                                                        <DataTrigger Binding="{Binding Period}" Value="Yearly">
                                                            <Setter Property="Visibility" Value="Visible" />
                                                        </DataTrigger>
                                                    </Style.Triggers>
                                                </Style>
                                            </TextBox.Style>
                                        </TextBox>
                                    </Grid>

                                    <TextBlock Text="keep" Margin="8,0,6,0" VerticalAlignment="Center" />
                                    <TextBox Width="50" Text="{Binding KeepText, UpdateSourceTrigger=PropertyChanged}" />
                                    <Button Content="Remove" Padding="8,1" Margin="8,0,0,0"
                                            Command="{Binding DataContext.RemoveRetentionRuleCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                                            CommandParameter="{Binding}" />
                                    <TextBlock Margin="10,0,0,0" VerticalAlignment="Center" Foreground="Gray"
                                               Text="{Binding AnchorHint}" />
                                </StackPanel>
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
        </StackPanel>

        <!-- Row 1: versions in the target now (Task 8) -->
        <!-- Row 2: full extension (Task 9) -->
    </Grid>
</UserControl>
```

Create `src/ReBackup.App/Views/RetentionView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace ReBackup.App.Views;

public partial class RetentionView : UserControl
{
    public RetentionView()
    {
        InitializeComponent();
    }
}
```

In `src/ReBackup.App/MainWindow.xaml`, add this tab between the "Ignore &amp; Preview" `TabItem` and the "History" `TabItem`:

```xml
                    <TabItem Header="Retention">
                        <views:RetentionView />
                    </TabItem>
```

- [ ] **Step 5: Build, test, smoke-test**

Run: `dotnet build --no-incremental` (0 warnings, 0 errors), `dotnet test tests/ReBackup.Core.Tests` (all pass), and the startup smoke test.

Manual checklist (for the user; do not click through it as an agent):
1. The Retention tab of a plan without rules shows the orange "No rules" note. **Add rule** adds "Daily keep 7" and marks the plan as changed (•).
2. Changing the period to Weekly shows a weekday list with Sunday selected; Monthly shows a text box with `1`; Yearly a text box with `01-01`; Daily shows no anchor.
3. A Monthly anchor of `32` or an empty keep shows a red message under the rule, and **Save** is refused with the same message on the General tab ("Retention rule 1: …").
4. Two Monthly rules (1 and 14) and two Weekly rules (Sunday and Wednesday) can be saved; after a restart of the app they are shown again, and the plan file contains a `retention` array with numbers for the monthly anchors.
5. **Remove** deletes a rule; **Revert** restores the saved rules.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(app): retention tab with rule editor, retention warnings in the history" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 8: Retention tab — versions in the target now

**Files:**
- Create: `src/ReBackup.App/ViewModels/RetentionNowRow.cs`
- Create: `src/ReBackup.App/ViewModels/RetentionPreviewViewModel.cs`
- Modify: `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`, `src/ReBackup.App/ViewModels/MainViewModel.cs`
- Modify: `src/ReBackup.App/Views/RetentionView.xaml`, `src/ReBackup.App/Views/RetentionView.xaml.cs`

**Interfaces:**
- Consumes: `VersionCatalog.List`, `VersionInfo`, `VersionOwnership` (Task 4); `RetentionPlanner.Decide`, `VersionDecision` (Task 5); `PlanEditorViewModel.ToPlan()`, `RetentionRuleRows`, `OnRetentionRulesEdited` (Task 7); `RangeObservableCollection<T>.ReplaceAll`; `ByteSize.Format`.
- Produces:
  - `RetentionNowRow` (`Name`, `DateText`, `SizeText`, `FilesText`, `DecisionText`, `ReasonText`, `IsDelete`, `IsManaged`)
  - `RetentionPreviewViewModel(Func<BackupPlan> plan)`: `NowRows`, `NowSummary`, `IsLoading`, `Error`, `RefreshCommand`, `void EnsureLoaded()`, `void ReloadIfLoaded()`, `void Invalidate()`, `void RequestEvaluate()`; private `void Evaluate()` that Task 9 extends
  - `PlanEditorViewModel.RetentionPreview`

Behaviour (spec §7.3 "Now"): the list shows every version folder of the plan, newest first, as Keep (with the rules that keep it), Delete, or Not managed (no manifest / another plan's manifest / unreadable manifest). It is recomputed from the cached folder list 300 ms after a rule edit; the target is read when the tab is first shown, on **Refresh**, and after a run of the plan.

- [ ] **Step 1: Row and view model**

Create `src/ReBackup.App/ViewModels/RetentionNowRow.cs`:

```csharp
using System.Globalization;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;
using ReBackup.Core.Retention;

namespace ReBackup.App.ViewModels;

/// <summary>One version folder in the "now" list of the Retention tab.</summary>
public sealed class RetentionNowRow
{
    public RetentionNowRow(VersionDecision decision)
    {
        var version = decision.Version;
        Name = version.Name;
        DateText = version.LocalTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        SizeText = version.TotalBytes is { } bytes ? ByteSize.Format(bytes) : "";
        FilesText = version.FileCount is { } files ? files.ToString("N0", CultureInfo.CurrentCulture) : "";
        IsManaged = decision.Decision is not null;
        IsDelete = decision.Delete;
        DecisionText = !IsManaged ? "Not managed" : IsDelete ? "Delete" : "Keep";
        ReasonText = decision.Decision is { } made
            ? string.Join(", ", made.Reasons.Select(r => r.Label))
            : version.Ownership switch
            {
                VersionOwnership.NoManifest => "no manifest in the folder",
                VersionOwnership.Foreign => "the manifest belongs to another plan",
                _ => "the manifest cannot be read",
            };
    }

    public string Name { get; }
    public string DateText { get; }
    public string SizeText { get; }
    public string FilesText { get; }
    public string DecisionText { get; }
    public string ReasonText { get; }
    public bool IsDelete { get; }
    public bool IsManaged { get; }
}
```

Create `src/ReBackup.App/ViewModels/RetentionPreviewViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;
using ReBackup.Core.Plans;
using ReBackup.Core.Retention;

namespace ReBackup.App.ViewModels;

/// <summary>Shows what a plan's retention rules do with the versions in its target.</summary>
public sealed partial class RetentionPreviewViewModel : ObservableObject
{
    private const string NotLoadedText = "The versions in the target have not been read yet.";
    private static readonly TimeSpan EvaluateDelay = TimeSpan.FromMilliseconds(300);

    private readonly Func<BackupPlan> _plan;
    private IReadOnlyList<VersionInfo>? _versions;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _evaluateCts;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _nowSummary = NotLoadedText;

    /// <param name="plan">Gives the plan as currently edited.</param>
    public RetentionPreviewViewModel(Func<BackupPlan> plan)
    {
        _plan = plan;
    }

    /// <summary>The version folders in the target, newest first.</summary>
    public RangeObservableCollection<RetentionNowRow> NowRows { get; } = new();

    /// <summary>Reads the target if that has not happened yet. Called when the tab is shown.</summary>
    public void EnsureLoaded()
    {
        if (_versions is null && !IsLoading)
            _ = LoadAsync();
    }

    /// <summary>Reads the target again if its versions are shown, e.g. after a run of the plan.</summary>
    public void ReloadIfLoaded()
    {
        if (_versions is not null || IsLoading)
            _ = LoadAsync();
    }

    /// <summary>Forgets the versions, e.g. after the target folder was edited.</summary>
    public void Invalidate()
    {
        _loadCts?.Cancel();
        _loadCts = null;   // the aborted load must not touch the state below any more
        _evaluateCts?.Cancel();
        _versions = null;
        IsLoading = false;
        Error = null;
        NowRows.ReplaceAll([]);
        NowSummary = NotLoadedText;
    }

    /// <summary>Applies the edited rules again after a short pause in typing. The target is not read again.</summary>
    public async void RequestEvaluate()
    {
        if (_versions is null)
            return;

        _evaluateCts?.Cancel();
        var cts = _evaluateCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(EvaluateDelay, cts.Token);
            Evaluate();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var plan = _plan();   // a snapshot, because the target is read on a worker thread
        IsLoading = true;
        Error = null;
        try
        {
            var versions = await Task.Run(
                () => VersionCatalog.List(plan.Target, plan.Id, plan.Name, cts.Token), cts.Token);
            if (!ReferenceEquals(_loadCts, cts))
                return;
            _versions = versions;
            Evaluate();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _versions = null;
                NowRows.ReplaceAll([]);
                NowSummary = "";
                Error = $"The target folder could not be read: {ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
                IsLoading = false;
        }
    }

    private void Evaluate()
    {
        if (_versions is not { } versions)
            return;

        var rules = _plan().Retention;
        IReadOnlyList<VersionDecision> decisions;
        try
        {
            decisions = RetentionPlanner.Decide(versions, rules);
        }
        catch (ArgumentException)
        {
            NowRows.ReplaceAll([]);
            NowSummary = "Correct the rules above to see what they keep.";
            return;
        }

        NowRows.ReplaceAll(decisions.Reverse().Select(d => new RetentionNowRow(d)));

        var managed = decisions.Where(d => d.Decision is not null).ToList();
        var deleted = managed.Where(d => d.Delete).ToList();
        var unmanaged = decisions.Count - managed.Count;
        if (decisions.Count == 0)
        {
            NowSummary = "There are no versions in the target yet.";
            return;
        }

        NowSummary =
            $"{managed.Count:N0} versions, {ByteSize.Format(SizeOf(managed))}  →  the rules keep " +
            $"{managed.Count - deleted.Count:N0} and delete {deleted.Count:N0} (frees {ByteSize.Format(SizeOf(deleted))})" +
            (unmanaged > 0 ? $"  ·  {unmanaged:N0} not managed" : "");
    }

    private static long SizeOf(IEnumerable<VersionDecision> decisions) =>
        decisions.Sum(d => d.Version.TotalBytes ?? 0);
}
```

- [ ] **Step 2: Wire it into the plan editor and the main view model**

In `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`:

1. In the constructor, directly below the statement that assigns `Preview` (before the `Preview.PropertyChanged += …` block), add:

```csharp
        RetentionPreview = new RetentionPreviewViewModel(ToPlan);
```

2. Directly below the `Run` property, add:

```csharp
    /// <summary>What the retention rules do with the versions in the target.</summary>
    public RetentionPreviewViewModel RetentionPreview { get; }
```

3. Replace `partial void OnTargetChanged(string value) => Touch();` with:

```csharp
    partial void OnTargetChanged(string value)
    {
        RetentionPreview.Invalidate();
        Touch();
    }
```

4. In `OnRetentionRulesEdited`, add `RetentionPreview.RequestEvaluate();` directly above `Touch();`.

5. In `LoadFrom`, directly below the line `OnPropertyChanged(nameof(HasNoRetentionRules));` added in Task 7, add:

```csharp
        RetentionPreview.RequestEvaluate();
```

In `src/ReBackup.App/ViewModels/MainViewModel.cs`, in `OnJobUpdate`, change

```csharp
        if (editor is not null)
            LoadHistory(editor);
```

to

```csharp
        if (editor is not null)
        {
            LoadHistory(editor);
            editor.RetentionPreview.ReloadIfLoaded();
        }
```

- [ ] **Step 3: The view**

In `src/ReBackup.App/Views/RetentionView.xaml`, replace the comment `<!-- Row 1: versions in the target now (Task 8) -->` with:

```xml
        <DockPanel Grid.Row="1" Margin="0,12,0,0">
            <DockPanel DockPanel.Dock="Top" Margin="0,0,0,4">
                <Button DockPanel.Dock="Right" Content="Refresh" Padding="10,2"
                        Command="{Binding RetentionPreview.RefreshCommand}" />
                <ProgressBar DockPanel.Dock="Right" Width="80" Height="12" Margin="0,0,8,0"
                             IsIndeterminate="{Binding RetentionPreview.IsLoading}" />
                <TextBlock FontWeight="SemiBold" VerticalAlignment="Center" Text="Versions in the target now" />
            </DockPanel>

            <TextBlock DockPanel.Dock="Top" Foreground="Firebrick" TextWrapping="Wrap"
                       Text="{Binding RetentionPreview.Error}">
                <TextBlock.Style>
                    <Style TargetType="TextBlock">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding RetentionPreview.Error}" Value="{x:Null}">
                                <Setter Property="Visibility" Value="Collapsed" />
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>

            <TextBlock DockPanel.Dock="Bottom" Margin="0,6,0,0" FontWeight="SemiBold" TextWrapping="Wrap"
                       Text="{Binding RetentionPreview.NowSummary}" />

            <ListView ItemsSource="{Binding RetentionPreview.NowRows}" MinHeight="80" SelectionMode="Single">
                <ListView.ItemContainerStyle>
                    <Style TargetType="ListViewItem">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding IsDelete}" Value="True">
                                <Setter Property="Foreground" Value="Firebrick" />
                            </DataTrigger>
                            <DataTrigger Binding="{Binding IsManaged}" Value="False">
                                <Setter Property="Foreground" Value="Gray" />
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </ListView.ItemContainerStyle>
                <ListView.View>
                    <GridView>
                        <GridViewColumn Header="Date" Width="120" DisplayMemberBinding="{Binding DateText}" />
                        <GridViewColumn Header="Decision" Width="90" DisplayMemberBinding="{Binding DecisionText}" />
                        <GridViewColumn Header="Kept by" Width="240" DisplayMemberBinding="{Binding ReasonText}" />
                        <GridViewColumn Header="Size" Width="80" DisplayMemberBinding="{Binding SizeText}" />
                        <GridViewColumn Header="Files" Width="70" DisplayMemberBinding="{Binding FilesText}" />
                        <GridViewColumn Header="Folder" Width="240" DisplayMemberBinding="{Binding Name}" />
                    </GridView>
                </ListView.View>
            </ListView>
        </DockPanel>
```

Replace the content of `src/ReBackup.App/Views/RetentionView.xaml.cs` with:

```csharp
using System.Windows.Controls;
using ReBackup.App.ViewModels;

namespace ReBackup.App.Views;

public partial class RetentionView : UserControl
{
    public RetentionView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => EnsureLoaded();
        DataContextChanged += (_, _) => EnsureLoaded();
    }

    /// <summary>Reads the target of the shown plan the first time its Retention tab is visible.</summary>
    private void EnsureLoaded()
    {
        if (IsVisible && DataContext is PlanEditorViewModel editor)
            editor.RetentionPreview.EnsureLoaded();
    }
}
```

- [ ] **Step 4: Build, test, smoke-test**

Run: `dotnet build --no-incremental` (0 warnings, 0 errors), `dotnet test tests/ReBackup.Core.Tests` (all pass), and the startup smoke test.

Manual checklist (for the user; do not click through it as an agent):
1. Opening the Retention tab of a plan that has backups lists them, newest first, with size and file count; the newest one says "Keep".
2. With no rules every row says "Keep — No rules". Adding "Daily keep 2" turns older rows red with "Delete" after a short pause, without reading the disk again; the summary line shows how many are kept and deleted and how much space that frees.
3. A rule with an invalid anchor empties the list and shows "Correct the rules above…"; fixing it brings the list back.
4. A folder in the target that is named like a version of the plan but has no `re-manifest.json` is shown grey as "Not managed".
5. After **Run now** finishes, the list shows the new version (and misses the ones retention deleted) without pressing Refresh.
6. After renaming the plan and saving, the versions made under the old name are still listed and managed.
7. With "Daily keep 2" saved and three or more versions on different days: after a run, the target holds only the versions the list showed as "Keep" plus the new one, and the History tab lists the deleted folders under "Deleted by retention".

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): retention preview of the versions in the target" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 9: Retention tab — full extension and timeline

**Files:**
- Create: `src/ReBackup.App/ViewModels/TimelineLane.cs`
- Create: `src/ReBackup.App/Controls/RetentionTimelineControl.cs`
- Modify: `src/ReBackup.App/ViewModels/RetentionPreviewViewModel.cs`, `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`
- Modify: `src/ReBackup.App/Views/RetentionView.xaml`

**Interfaces:**
- Consumes: `RetentionSimulator.Simulate`, `RetentionSimulator.Every`, `SimulationResult`, `SimulatedVersion` (Task 3); `RetentionRules.Describe` (Task 1); `RetentionPreviewViewModel` with its private `Evaluate()`, `Invalidate()`, `LoadAsync()` (Task 8); `IgnorePreviewViewModel.Root` (`EvaluatedNode?` with `IncludedSize`).
- Produces:
  - `sealed record TimelineLane(string Label, IReadOnlyList<DateTime> Times)`
  - `sealed record AssumedSchedule(string Label, TimeSpan Interval)`
  - `RetentionPreviewViewModel(Func<BackupPlan> plan, Func<long?> fallbackVersionBytes)` with static `Schedules`, `SelectedSchedule`, `FullSummary`, `TimelineLanes`, `TimelineFrom`, `TimelineTo`
  - `RetentionTimelineControl` (dependency properties `Lanes`, `From`, `To`)

Behaviour (spec §7.3 "Full extension"): after every evaluation of the "now" list, the simulator runs on a worker thread over two years, seeded with the plan's existing versions. Until the Schedule tab exists (phase 5), the backup frequency is chosen in a list on this tab and is not saved. The summary shows the largest number of versions in the second year and the estimated size (that number × the average size of the existing versions, or the included size of the last source index when there are none). The timeline shows the versions left at the end, one lane per rule.

- [ ] **Step 1: Lane record and timeline control**

Create `src/ReBackup.App/ViewModels/TimelineLane.cs`:

```csharp
namespace ReBackup.App.ViewModels;

/// <summary>One row of the retention timeline: the versions one rule keeps.</summary>
public sealed record TimelineLane(string Label, IReadOnlyList<DateTime> Times);
```

Create `src/ReBackup.App/Controls/RetentionTimelineControl.cs`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ReBackup.App.ViewModels;

namespace ReBackup.App.Controls;

/// <summary>Draws the versions that survive retention on a time axis, one lane per rule.</summary>
public sealed class RetentionTimelineControl : FrameworkElement
{
    private const double LabelWidth = 170;
    private const double LaneHeight = 22;
    private const double AxisHeight = 22;
    private const double MarkerWidth = 3;
    private const double RightPadding = 8;

    private static readonly Brush[] Palette =
    [
        Frozen(0x4E, 0x79, 0xA7), Frozen(0xF2, 0x8E, 0x2B), Frozen(0x59, 0xA1, 0x4F), Frozen(0xE1, 0x57, 0x59),
        Frozen(0x76, 0xB7, 0xB2), Frozen(0xED, 0xC9, 0x48), Frozen(0xB0, 0x7A, 0xA1), Frozen(0x9C, 0x75, 0x5F),
    ];
    private static readonly Pen LanePen = FrozenPen(Color.FromRgb(0xDD, 0xDD, 0xDD));
    private static readonly Pen AxisPen = FrozenPen(Colors.Gray);
    private static readonly Typeface TextFace = new("Segoe UI");

    public static readonly DependencyProperty LanesProperty = DependencyProperty.Register(
        nameof(Lanes), typeof(IReadOnlyList<TimelineLane>), typeof(RetentionTimelineControl),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(DateTime), typeof(RetentionTimelineControl),
        new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(DateTime), typeof(RetentionTimelineControl),
        new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<TimelineLane>? Lanes
    {
        get => (IReadOnlyList<TimelineLane>?)GetValue(LanesProperty);
        set => SetValue(LanesProperty, value);
    }

    /// <summary>Left end of the time axis.</summary>
    public DateTime From
    {
        get => (DateTime)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    /// <summary>Right end of the time axis.</summary>
    public DateTime To
    {
        get => (DateTime)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var lanes = Lanes?.Count ?? 0;
        var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        return new Size(width, lanes == 0 ? 0 : lanes * LaneHeight + AxisHeight);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var lanes = Lanes;
        var from = From;
        var to = To;
        var plotWidth = ActualWidth - LabelWidth - RightPadding;
        if (lanes is null || lanes.Count == 0 || plotWidth < 20 || to <= from)
            return;

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var totalDays = (to - from).TotalDays;
        double X(DateTime time) => LabelWidth + Math.Clamp((time - from).TotalDays / totalDays, 0, 1) * plotWidth;

        for (var i = 0; i < lanes.Count; i++)
        {
            var top = i * LaneHeight;
            var middle = top + LaneHeight / 2;
            var label = Text(lanes[i].Label, 12, Brushes.Black, pixelsPerDip);
            label.MaxTextWidth = LabelWidth - 8;
            label.MaxLineCount = 1;
            label.Trimming = TextTrimming.CharacterEllipsis;
            drawingContext.DrawText(label, new Point(0, middle - label.Height / 2));
            drawingContext.DrawLine(LanePen, new Point(LabelWidth, middle), new Point(LabelWidth + plotWidth, middle));

            var brush = Palette[i % Palette.Length];
            foreach (var time in lanes[i].Times)
            {
                drawingContext.DrawRectangle(brush, null,
                    new Rect(X(time) - MarkerWidth / 2, top + 4, MarkerWidth, LaneHeight - 8));
            }
        }

        var axisTop = lanes.Count * LaneHeight;
        drawingContext.DrawLine(AxisPen, new Point(LabelWidth, axisTop), new Point(LabelWidth + plotWidth, axisTop));

        var months = (to.Year - from.Year) * 12 + to.Month - from.Month + 1;
        var pixelsPerMonth = plotWidth / months;
        var step = pixelsPerMonth >= 50 ? 1 : pixelsPerMonth >= 17 ? 3 : pixelsPerMonth >= 9 ? 6 : 12;
        var labelFormat = step == 12 ? "yyyy" : "MMM yy";
        for (var tick = new DateTime(from.Year, from.Month, 1).AddMonths(1); tick <= to; tick = tick.AddMonths(1))
        {
            if ((tick.Month - 1) % step != 0)
                continue;
            var x = X(tick);
            drawingContext.DrawLine(AxisPen, new Point(x, axisTop), new Point(x, axisTop + 4));
            var text = Text(tick.ToString(labelFormat, CultureInfo.CurrentCulture), 10, Brushes.Gray, pixelsPerDip);
            if (x + 2 + text.Width <= ActualWidth)
                drawingContext.DrawText(text, new Point(x + 2, axisTop + 5));
        }
    }

    private static FormattedText Text(string text, double size, Brush brush, double pixelsPerDip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, size, brush, pixelsPerDip);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color)
    {
        var pen = new Pen(new SolidColorBrush(color), 1);
        pen.Freeze();
        return pen;
    }
}
```

- [ ] **Step 2: Simulation in the preview view model**

In `src/ReBackup.App/ViewModels/RetentionPreviewViewModel.cs`:

1. Directly above the class declaration (below its namespace line), add:

```csharp
/// <summary>A backup frequency assumed for the full-extension preview until plans have a schedule.</summary>
public sealed record AssumedSchedule(string Label, TimeSpan Interval);

```

2. Replace everything from the line `private readonly Func<BackupPlan> _plan;` down to and including the constructor (the four fields, the three `[ObservableProperty]` lines and the constructor from Task 8) with:

```csharp
    private readonly Func<BackupPlan> _plan;
    private readonly Func<long?> _fallbackVersionBytes;
    private IReadOnlyList<VersionInfo>? _versions;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _evaluateCts;
    private CancellationTokenSource? _simulateCts;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _nowSummary = NotLoadedText;
    [ObservableProperty] private AssumedSchedule _selectedSchedule = Schedules[1];
    [ObservableProperty] private string _fullSummary = "";
    [ObservableProperty] private IReadOnlyList<TimelineLane> _timelineLanes = [];
    [ObservableProperty] private DateTime _timelineFrom;
    [ObservableProperty] private DateTime _timelineTo;

    /// <param name="plan">Gives the plan as currently edited.</param>
    /// <param name="fallbackVersionBytes">Size of one version when the target has none yet; null when unknown.</param>
    public RetentionPreviewViewModel(Func<BackupPlan> plan, Func<long?> fallbackVersionBytes)
    {
        _plan = plan;
        _fallbackVersionBytes = fallbackVersionBytes;
    }

    public static IReadOnlyList<AssumedSchedule> Schedules { get; } =
    [
        new("one backup a week", TimeSpan.FromDays(7)),
        new("one backup a day", TimeSpan.FromDays(1)),
        new("two backups a day", TimeSpan.FromHours(12)),
        new("a backup every 4 hours", TimeSpan.FromHours(4)),
        new("a backup every hour", TimeSpan.FromHours(1)),
    ];

    partial void OnSelectedScheduleChanged(AssumedSchedule value) => RequestEvaluate();
```

The constants `NotLoadedText` and `EvaluateDelay` above that block stay as they are.

3. In `Invalidate`, add `ClearSimulation();` as the last statement.

4. In `LoadAsync`, in the `catch (Exception ex)` block, add `ClearSimulation();` directly below `NowSummary = "";`.

5. In `Evaluate`, in the `catch (ArgumentException)` block, add `ClearSimulation();` directly above `return;`. Then replace the block

```csharp
        if (decisions.Count == 0)
        {
            NowSummary = "There are no versions in the target yet.";
            return;
        }

        NowSummary =
            $"{managed.Count:N0} versions, {ByteSize.Format(SizeOf(managed))}  →  the rules keep " +
            $"{managed.Count - deleted.Count:N0} and delete {deleted.Count:N0} (frees {ByteSize.Format(SizeOf(deleted))})" +
            (unmanaged > 0 ? $"  ·  {unmanaged:N0} not managed" : "");
```

with

```csharp
        NowSummary = decisions.Count == 0
            ? "There are no versions in the target yet."
            : $"{managed.Count:N0} versions, {ByteSize.Format(SizeOf(managed))}  →  the rules keep " +
              $"{managed.Count - deleted.Count:N0} and delete {deleted.Count:N0} (frees {ByteSize.Format(SizeOf(deleted))})" +
              (unmanaged > 0 ? $"  ·  {unmanaged:N0} not managed" : "");

        _ = SimulateAsync(versions, rules);
```

6. Add these methods to the class:

```csharp
    private void ClearSimulation()
    {
        _simulateCts?.Cancel();
        _simulateCts = null;
        FullSummary = "";
        TimelineLanes = [];
    }

    private async Task SimulateAsync(IReadOnlyList<VersionInfo> versions, IReadOnlyList<RetentionRule> rules)
    {
        _simulateCts?.Cancel();
        var cts = _simulateCts = new CancellationTokenSource();
        var schedule = SelectedSchedule;
        var now = DateTime.Now;
        var owned = versions.Where(v => v.IsOwned).ToList();
        var seeds = owned.Select(v => new RetentionVersion(v.Name, v.LocalTime)).ToList();
        var sizes = owned.Where(v => v.TotalBytes is not null).Select(v => v.TotalBytes!.Value).ToList();
        long? average = sizes.Count > 0 ? (long)sizes.Average() : _fallbackVersionBytes();

        try
        {
            // The assumed backups run at 02:00 and then every interval.
            var result = await Task.Run(() => RetentionSimulator.Simulate(seeds, rules,
                RetentionSimulator.Every(now.Date.AddHours(2), schedule.Interval), now, average, cts.Token), cts.Token);
            if (ReferenceEquals(_simulateCts, cts))
                ShowSimulation(result, rules, schedule, now);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ArgumentException)
        {
            // Evaluate() checked the rules already; if they are not valid after all there is nothing to show.
        }
    }

    private void ShowSimulation(SimulationResult result, IReadOnlyList<RetentionRule> rules, AssumedSchedule schedule,
        DateTime now)
    {
        var size = result.EstimatedBytes is { } bytes ? $", about {ByteSize.Format(bytes)}" : "";
        var cut = result.Truncated ? $" The simulation stopped after {result.RunsSimulated:N0} runs." : "";
        FullSummary = rules.Count == 0
            ? $"No rules, so nothing is ever deleted: with {schedule.Label} there are {result.SteadyStateCount:N0} versions after two years{size}.{cut}"
            : $"With {schedule.Label} the target holds up to {result.SteadyStateCount:N0} versions{size}.{cut}";

        var lanes = new List<TimelineLane>();
        for (var i = 0; i < rules.Count; i++)
        {
            var ruleIndex = i;
            lanes.Add(new TimelineLane($"{RetentionRules.Describe(rules[i])}, keep {rules[i].Keep:N0}",
                result.Survivors.Where(s => s.Reasons.Any(r => r.RuleIndex == ruleIndex)).Select(s => s.LocalTime).ToList()));
        }

        var others = result.Survivors.Where(s => s.Reasons.All(r => r.RuleIndex < 0)).Select(s => s.LocalTime).ToList();
        if (others.Count > 0)
            lanes.Add(new TimelineLane(rules.Count == 0 ? "All versions" : "Newest", others));

        TimelineFrom = result.Survivors.Count > 0 ? result.Survivors[0].LocalTime : now;
        TimelineTo = result.Horizon;
        TimelineLanes = lanes;
    }
```

In `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`, change the line added in Task 8

```csharp
        RetentionPreview = new RetentionPreviewViewModel(ToPlan);
```

to

```csharp
        RetentionPreview = new RetentionPreviewViewModel(ToPlan, () => Preview.Root?.IncludedSize);
```

- [ ] **Step 3: The view**

In `src/ReBackup.App/Views/RetentionView.xaml`:

1. Add this namespace to the `UserControl` element:

```xml
             xmlns:controls="clr-namespace:ReBackup.App.Controls"
```

2. Replace the comment `<!-- Row 2: full extension (Task 9) -->` with:

```xml
        <StackPanel Grid.Row="2" Margin="0,12,0,0">
            <DockPanel Margin="0,0,0,4">
                <ComboBox DockPanel.Dock="Right" Width="190" DisplayMemberPath="Label"
                          ItemsSource="{x:Static vm:RetentionPreviewViewModel.Schedules}"
                          SelectedItem="{Binding RetentionPreview.SelectedSchedule}" />
                <TextBlock DockPanel.Dock="Right" Margin="0,0,8,0" VerticalAlignment="Center" Text="Assume" />
                <TextBlock FontWeight="SemiBold" VerticalAlignment="Center"
                           Text="Full extension — two years from now" />
            </DockPanel>
            <TextBlock TextWrapping="Wrap" Text="{Binding RetentionPreview.FullSummary}" />
            <ScrollViewer MaxHeight="180" Margin="0,6,0,0" VerticalScrollBarVisibility="Auto">
                <controls:RetentionTimelineControl Lanes="{Binding RetentionPreview.TimelineLanes}"
                                                   From="{Binding RetentionPreview.TimelineFrom}"
                                                   To="{Binding RetentionPreview.TimelineTo}" />
            </ScrollViewer>
        </StackPanel>
```

- [ ] **Step 4: Build, test, smoke-test**

Run: `dotnet build --no-incremental` (0 warnings, 0 errors), `dotnet test tests/ReBackup.Core.Tests` (all pass), and the startup smoke test.

Manual checklist (for the user; do not click through it as an agent):
1. With the rules Daily 7, Weekly Sunday 4, Monthly 0 12 and "one backup a day", the summary reads "… up to 22 versions, about …" and the timeline shows three lanes: seven markers close together at the right end, four weekly markers, and twelve monthly markers spread over a year.
2. Changing a rule (for example Monthly keep 12 → 24) updates the summary and the timeline after a short pause.
3. Switching "Assume" to "a backup every hour" keeps the count unchanged for these rules (the rules keep one version per slot) and still answers within a few seconds.
4. With no rules, the summary says that nothing is ever deleted and gives the number of versions after two years.
5. A plan without any backups in the target still gets an estimate once its source has been indexed on the Ignore & Preview tab; before that the summary shows the count without a size.
6. Two Monthly rules (1 and 14) give two separate lanes.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): full-extension estimate and retention timeline" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

## Not part of this plan

- Real triggers for the simulation (phase 5 replaces the assumed frequency with the plan's schedule).
- `VersionCatalog` loading full manifests and the scan fallback, version comparison, restore (phase 6).
- The Phase 3 UI items that are still open: status dot per plan row, current file and ETA, "starting at hh:mm".
