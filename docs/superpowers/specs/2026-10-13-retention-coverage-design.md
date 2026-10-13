# Retention Coverage Timeline — Design

Date: 2026-10-13
Status: Approved (brainstorming), pending spec review
Applies to ReBackup and ReS3 (shared code in `ReBackup.Shared` / `ReBackup.Shared.Wpf`).

## 1. Purpose

Show, per retention rule, which slots the rule wants to keep and whether a backup actually exists for each slot, so
a missing day/week/month (deleted by accident, or never written) is visible at a glance. The existing timeline in the
Retention tab is a *forecast*; this one shows the *current stock*.

## 2. Decisions

| Topic | Decision |
|---|---|
| Where | Both apps, in the Retention tab (new card "Coverage" between rules and forecast) and the Versions tab (above the version list) |
| Rules used | Retention tab: the rules currently in the editor (live). Versions tab: the saved rules (ReS3: of the selected series) |
| Click | Clicking a slot that holds a version selects that version: Retention tab → row in the "Now" table (scrolled into view); Versions tab → the version in the list (the file tree loads it) |
| Colours | Green = version present; yellow = present with warnings; red = missing; grey filled (faint) = before the first backup; grey outline = current slot, not yet run |
| Yellow in ReBackup | The run that wrote the version ended `CompletedWithWarnings` (looked up in the plan's run log by version name). Not found in the log → green |
| Yellow in ReS3 | The slot's only version(s) are `Incomplete` or `PartiallyDeleted` |

## 3. Slot model (ReBackup.Shared.Retention)

```
public enum CoverageQuality { Ok, Warning }
public sealed record CoverageVersion(string Id, DateTime LocalTime, CoverageQuality Quality, bool Counted, string? Detail);
public enum SlotState { Present, PresentWithWarnings, Missing, Pending, BeforeFirst }
public sealed record CoverageSlot(DateOnly Start, DateOnly End, SlotState State, CoverageVersion? Version);
public sealed record CoverageLane(int RuleIndex, RetentionRule Rule, IReadOnlyList<CoverageSlot> Slots); // oldest first
public static class RetentionCoverage
{
    public static IReadOnlyList<CoverageLane> Compute(IReadOnlyList<CoverageVersion> versions,
        IReadOnlyList<RetentionRule> rules, DateOnly today);
}
```

- A slot of a rule runs from `RetentionRules.SlotStart(rule, d)` to the next slot start (End exclusive). The current
  slot is the one containing `today`; earlier slots step back with `SlotStart(rule, start.AddDays(-1))`.
- `Counted` versions are the ones retention manages (ReBackup: owned; ReS3: Complete). They go through
  `RetentionEngine.Evaluate`; a slot's version is the one the engine keeps for that rule (its `KeepReason.RuleIndex`
  matches), so the timeline and retention always agree.
- **Span** of a lane: the larger of `rule.Keep` calendar slots back from the current slot and the slots back to the
  oldest version this rule keeps (the engine skips empty slots, so with gaps a rule reaches further back).
- **States**, for each slot in the span:
  - a kept counted version → `Present` (Quality Ok) or `PresentWithWarnings` (Quality Warning);
  - no counted version but a non-counted one (ReS3 incomplete / partially deleted) → `PresentWithWarnings` with the
    newest such version;
  - nothing, slot is the current one → `Pending`;
  - nothing, slot starts before the slot of the oldest version passed in (or no versions at all) → `BeforeFirst`;
  - otherwise → `Missing`.
- Dates are local wall-clock dates, like the engine. No rules → empty list.

## 4. Control (ReBackup.Shared.Wpf.Controls)

`RetentionCoverageControl` (FrameworkElement, `OnRender`, like `RetentionTimelineControl`):

- Dependency properties `Lanes` (`IReadOnlyList<CoverageLane>`), `LaneLabels` (`IReadOnlyList<string>`, the app's
  localized rule labels such as "Daily · 7"), `SelectCommand` (`ICommand`, parameter = version Id), `SelectedId`
  (the selected version, see below), `PresentColoring` (`CoveragePresentColoring.Status`, the default: present cells
  in `Brush.Accent`; `Period`: present cells in the lane's period colour, the `Color.Chart.*` of its label — ReS3).
- One row per lane, 26 px (18 px cells), 170 px label column, label coloured with the period colour as in the existing
  timeline.
- Selection: every drawn cell of the `SelectedId` version gets a 2 px ring in `Color.Text`, 2 px away from the cell;
  behind the cells, every lane gets a faint full-height band (translucent `Color.Text`) over its drawn slot that
  contains the version's date. A version whose cell is hidden behind the "+N" label rings that label instead.
  Ids are compared ignoring case. `SelectedDate` (`DateTime?`): when no slot holds `SelectedId`, the bands mark the
  slots that contain that date, without a ring (History: the run's version time or start; Versions: the version's time).
- Each lane has its own scale: equal-width cells, newest on the right edge. Cell width = available width / slot
  count, at least 2 px; 1 px gap when cells are ≥ 6 px. If the slots do not fit at 2 px, the newest that fit are
  drawn and a muted "+N" label marks the left edge.
- Colours: `Brush.Accent` or the period colour (present, see `PresentColoring`), `Brush.Warning` (with warnings), `Brush.Danger` (missing), `Brush.TextFaint` at
  low opacity (before first), `Brush.TextFaint` outline only (pending). Re-resolved on theme change.
- Tooltip per cell (localized via the app's text callback `Func<CoverageSlot, string>` property `SlotText`): period,
  and version date/size/"with warnings: n skipped" or "missing" / "before the first backup" / "not yet run".
- Clicking a cell with a version executes `SelectCommand`; other cells do nothing. Keyboard: not required.

## 5. App integration

**ReBackup**
- Retention tab: `RetentionPreviewViewModel` builds `CoverageLanes` from its loaded owned versions, the editor's rules
  and the run log (`RunLog.ReadAll()` → map Version → Status/SkippedCount); recomputed whenever rules or versions
  change. Click → `SelectedNowRow` set and scrolled into view.
- Versions tab: `VersionsViewModel` builds lanes from the owned versions it lists and the saved plan's rules. Click
  → `SelectedVersion`; the selected version is the control's `SelectedId`.
- History tab: `HistoryCoverageViewModel` lists the saved target's owned versions (`VersionCatalog`, no index sync)
  and builds lanes with the saved rules and the run log. Selecting a run that wrote a version rings that version;
  clicking a slot selects the run whose log entry names that version (nothing when no run in the history has it).
- Not shown (card hidden / replaced by a muted line "No retention rules") when the plan has no rules; hidden when the
  target could not be loaded (the tab's existing error stays).

**ReS3**
- Retention tab: `RetentionTabViewModel` builds lanes for the selected series from `SeriesModel.Versions` (Complete →
  counted Ok; Incomplete / PartiallyDeleted → not counted, Warning, detail = the incomplete reason) and the editor's
  rules. Click → selects the `NowRow`.
- Versions tab: same for the selected series with its saved rules. Click → selects the version row.

Texts in en-US and de-DE (card title, labels, tooltips, "No retention rules").

## 6. Testing

- `RetentionCoverage` unit tests: each period (daily, weekly with anchor, monthly with "31"/"0"/"-n", yearly
  "02-29"); gaps → Missing and the lane reaches back to the oldest kept version; before-first slots; pending current
  slot; current slot with a version → Present; warning quality; ReS3 non-counted version filling a slot; no rules;
  no versions (all BeforeFirst + Pending); slot version equals the engine's kept representative.
- Control: smoke render in both themes; hit test maps a click to the right version Id; "+N" when slots do not fit.
- View models (both apps): lanes follow rule edits; warnings from the run log; click selects the right row/version;
  hidden with no rules.

## 7. Versions

Shipped together with the Google Drive provider (separate spec): ReBackup 1.5.0, ReS3 0.3.0. This feature is
committed and pushed on its own before the Drive work starts.
