# ReBackup — Design

Date: 2026-09-30
Status: Approved (brainstorming), pending spec review

## 1. Purpose

A small Windows desktop backup manager. The user defines backup plans (source, target, schedule, ignore list, retention), previews what will be saved and what retention will keep, runs backups manually or on schedule, and browses/compares/restores past versions.

## 2. Scope decisions

| Topic | Decision |
|---|---|
| UI stack | WPF, .NET 9, MVVM (CommunityToolkit.Mvvm) |
| Background running | Only while the app runs. Close = minimize to tray by default. No Windows service. |
| Version storage | Full copy per run. No hardlinks, no archives, no increments. |
| Version folder name | `YYYY_MM_DD-hh_mm <PlanName>` (e.g. `2026_09_30-14_05 Projects`) |
| Sources per plan | Exactly one source folder |
| Ignore | gitignore syntax in the plan + optional global defaults + nested `.backupignore` files |
| Retention | Multiple anchored rules; a version is kept if any rule keeps it |
| Schedule | List of simple triggers per plan (no cron) |
| Locked files | Skipped and logged; no VSS |
| Version browser | Status + per-file history + open/restore. No in-app text diff. |
| Source preview | Tree with size bars **and** a linked treemap |

Out of scope for v1: VSS, running without the UI, cron triggers, text diff, multiple sources per plan, compression, encryption, network/cloud targets beyond what a Windows path can reach.

## 3. Architecture

```
ReBackup.sln
  src/ReBackup.Core          class library, no WPF dependency
  src/ReBackup.App           WPF shell, tray icon (H.NotifyIcon), MVVM
  tests/ReBackup.Core.Tests  xUnit + FluentAssertions
```

All logic lives in `ReBackup.Core`. View models are thin adapters. Time is injected via `TimeProvider` everywhere so scheduling and retention are deterministic under test.

## 4. Storage

### 4.1 Config folder

Default `%AppData%\ReBackup\`. The location is changeable. The pointer to it is kept in `%AppData%\ReBackup\location.json` when it differs from the default.

```
<config>/
  settings.json            global settings
  plans/<plan-id>.json     one file per plan
  logs/<plan-id>.jsonl     one JSON object per run, append-only
```

`settings.json`: default ignore patterns (seeded with `Thumbs.db`, `desktop.ini`, `$RECYCLE.BIN/`, `System Volume Information/`), close-to-tray (default true), start with Windows (default false; uses the HKCU Run key).

### 4.2 Plan file

```json
{
  "id": "b3f1c2…",
  "name": "Projects",
  "source": "D:\\Projects",
  "target": "F:\\Backups\\Projects",
  "enabled": true,
  "triggers": [
    { "type": "Daily",    "time": "02:00" },
    { "type": "Weekly",   "days": ["Mon", "Wed"], "time": "18:00" },
    { "type": "Interval", "everyHours": 4, "from": "08:00", "to": "20:00" },
    { "type": "Monthly",  "day": 1, "time": "03:00" }
  ],
  "ignore": {
    "useGlobalDefaults": true,
    "honorNestedFiles": true,
    "patterns": ["node_modules/", "*.tmp", "!keep.tmp"]
  },
  "retention": [
    { "period": "Daily",   "keep": 7 },
    { "period": "Weekly",  "anchor": "Sunday", "keep": 4 },
    { "period": "Monthly", "anchor": 0, "keep": 12 },
    { "period": "Yearly",  "anchor": "01-01", "keep": 3 }
  ],
  "freeSpaceByRetention": false
}
```

The plan name must be a valid folder-name fragment and unique across plans. Validation rejects: a missing source, a target inside the source, a source inside the target, and an empty name.

### 4.3 Target layout

```
<target>/
  2026_09_30-14_05 Projects/          completed version: full copy + re-manifest.json
  2026_09_30-18_00 Projects.partial/  in-progress run (never counted as a version)
```

A folder counts as a version of the plan only if its name matches exactly `^\d{4}_\d{2}_\d{2}-\d{2}_\d{2} <PlanName>$`. Everything else in the target is ignored and never deleted.

If a folder for the same minute already exists, the run waits until the next minute boundary before starting (manual runs show "starting at hh:mm").

### 4.4 `re-manifest.json`

Written into each version folder at the end of a successful copy.

```json
{
  "formatVersion": 1,
  "planId": "b3f1c2…",
  "planName": "Projects",
  "createdUtc": "2026-09-30T12:05:00Z",
  "source": "D:\\Projects",
  "files": [
    { "path": "src/app.cs", "size": 1234, "mtimeUtc": "…", "hash": "xxh64:9f…" }
  ]
}
```

Hashes are xxHash64 (System.IO.Hashing), computed while copying. The manifest is excluded from its own file list.

### 4.5 Run log line

```json
{ "runId": "…", "trigger": "Manual|Scheduled|CatchUp",
  "startUtc": "…", "endUtc": "…", "durationMs": 81234,
  "status": "Completed|CompletedWithWarnings|Full|Error|Canceled",
  "reason": "text for Full/Error",
  "version": "2026_09_30-14_05 Projects",
  "filesCopied": 12034, "bytesCopied": 5368709120,
  "skippedCount": 1,
  "skipped": [ { "path": "…", "reason": "locked by another program" } ],
  "retentionDeleted": ["2026_09_01-02_00 Projects"] }
```

`version` is null when the run did not complete. `skipped` stores at most the first 1000 entries of a run; `skippedCount` counts all of them.

## 5. Core components

| Component | Responsibility |
|---|---|
| `PlanStore` | Load/save/validate plans; watches `plans/` for external edits |
| `SettingsStore` | Load/save `settings.json` and the config location |
| `IgnoreMatcher` | gitignore semantics; `Match(relativePath, isDir) → IgnoreResult(ignored, pattern, originFile)` |
| `SourceIndexer` | Async enumeration into an `IndexNode` tree (name, size, mtime, counts); progress + cancellation; does not apply ignores |
| `IndexEvaluator` | Applies an `IgnoreMatcher` to an index → per-node `Included / Ignored / Partial` + rolled-up included/ignored sizes |
| `RetentionEngine` | Pure: `(versions, rules, now) → per-version Keep/Delete + reasons` |
| `RetentionSimulator` | Generates future run times from triggers, applies the engine run by run over a 2-year horizon → steady-state count, estimated size, timeline |
| `ScheduleCalculator` | Pure: `(triggers, lastRun, now) → nextRun, missedSinceLastRun` |
| `BackupRunner` | Preflight → copy + hash → manifest → rename → retention; reports `IProgress<BackupProgress>` |
| `BackupQueue` | Single worker; one job per plan at a time (duplicate enqueue is a no-op) |
| `Scheduler` | Checks triggers every minute; on startup enqueues one catch-up run per plan that missed a trigger |
| `RunLog` | Append/read JSONL per plan |
| `VersionCatalog` | Finds version folders; loads manifests; falls back to a scan (size + mtime, no hash) |
| `VersionComparer` | `(versionA, versionB) → diff tree`; `FileHistory(path) → per-version status` |
| `Restorer` | Copy a file/folder from a version to its original path or a chosen folder; conflict policy asked per operation (overwrite / skip / keep both) |

## 6. Ignore semantics

Patterns follow gitignore rules, relative to the source root:

- `#` comments, blank lines ignored, trailing spaces trimmed unless escaped
- `!pattern` re-includes; a file cannot be re-included if a parent directory is excluded (git behaviour)
- leading `/` anchors to the root; a `/` in the middle also anchors
- trailing `/` matches directories only
- `*`, `?`, `[a-z]`, `**` (leading, trailing, middle)
- Matching is case-insensitive (Windows)

Precedence (lowest → highest): global defaults → plan patterns → nested `.backupignore` files from the root down to the file's directory. Later rules win, as in git. `.backupignore` files themselves are backed up.

## 7. Retention semantics

### 7.1 Rules

| Period | Anchor | Slot |
|---|---|---|
| `Daily` | none | calendar day |
| `Weekly` | day of week (`Monday`…`Sunday`) | the week starting at the anchor day (e.g. Sun 00:00 → next Sun 00:00) |
| `Monthly` | day of month: `1..31` = that day (clamped to the last day); `0` = last day; `-n` = n days before the last day | from the anchor day in one month to the anchor day in the next month |
| `Yearly` | `MM-DD` | from the anchor date in one year to the anchor date in the next year |

For `Daily`, the representative of a slot is the last version of that day. For the anchored periods, the **representative** of a slot is the version on the anchor day. If there are several, it's the last one that day. If there is none, it's the first version after the anchor within the same slot (fall-through). A slot with no versions has no representative and does not count.

A rule keeps the representatives of its **`keep` most recent slots that have one**. Slots before the oldest version are ignored.

A version is kept if at least one rule keeps it. Its "kept by" reasons list every rule and slot index that claims it (e.g. `Weekly(Sun) #2`, `Monthly(0) #5`).

The newest completed version is always kept, even if no rule claims it. An empty rule list means keep everything; the UI warns about this.

### 7.2 Execution

Retention runs only after a successful run (`Completed` or `CompletedWithWarnings`). It deletes folders marked Delete. A folder that fails to delete is logged as a warning and doesn't fail the run.

When `freeSpaceByRetention` is true and preflight finds too little space, the runner deletes Delete-marked versions first (oldest first), then re-checks. It never deletes versions that retention would keep.

### 7.3 Preview

- **Now**: the versions in the target, each marked Keep (with reasons) or Delete, plus totals (count, size before/after). This recomputes live while rules are edited.
- **Full extension**: `RetentionSimulator` generates run times from the plan's triggers for 2 years from now, seeded with the existing versions. It applies retention after each simulated run, then reports the steady-state count (the maximum over the last simulated year), the estimated size (count × average size of existing versions, or the current indexed included size if there are none), and the surviving versions at the horizon for a timeline chart, colored by the rule that keeps each one.

## 8. Schedule semantics

Trigger types: `Daily(time)`, `Weekly(days[], time)`, `Monthly(day, time)` with the same day encoding as retention anchors, and `Interval(everyHours, from, to)`.

- Times are local wall-clock time. On a DST gap the run happens at the first valid minute after. On a DST overlap it runs once.
- A plan is due when a trigger time is at or before now and after the plan's last run start (from the log).
- On startup, a plan with at least one missed trigger since its last run gets **one** `CatchUp` run, enqueued about 1 minute after startup.
- Disabled plans and a paused scheduler (tray option; not persisted, resumes on restart) never enqueue scheduled runs. Manual runs always work.

## 9. Backup run

1. **Preflight**: check the source exists and is reachable, and create the target folder if it does not exist. Index the source, evaluate ignores, and compute the included bytes. If included bytes + 5 % exceed the free space on the target → optional retention freeing (7.2) → otherwise abort `Full` before writing anything.
2. **Copy**: create `<name>.partial`, walk the included files, and copy each with a streaming read that feeds the xxHash64. Preserve the relative path and last-write time. Report progress (files done/total, bytes done/total, current file, ETA).
3. **Locked or unreadable files** (sharing violation, access denied): skip and record them. The run continues.
4. **Finish**: write `re-manifest.json`, rename `.partial` → final name, and run retention.
5. **Abort**: a disk-full IOException → `Full`. Any other unexpected exception → `Error` with its message. Cancellation → `Canceled`. In all abort cases, delete the `.partial` folder (best effort; leftovers are cleaned up on the next run of that plan).
6. Write the log line in every case, including preflight aborts.

Final status: `Completed` if nothing was skipped, otherwise `CompletedWithWarnings`.

## 10. UI

### 10.1 Main window

Left: plan list. Each row shows the name, a status dot (idle / queued / running / last failed), last run (time + result), next run, and a **Run now** button. A running plan shows a progress bar and a **Cancel** button in its row. New/Delete plan buttons sit below the list; deleting a plan asks for confirmation, removes the JSON (and optionally its log), and never touches the backups.

Right: tabs for the selected plan. Edits mark the plan dirty (•). **Save** / **Revert**. Unsaved edits are kept per plan while switching between plans; exiting (or restarting after a config-folder change) with unsaved edits asks first.

Bottom status strip: queue state and the current job's progress.

### 10.2 Tabs

1. **General**: name, source, target (folder pickers), enabled, free-space-by-retention.
2. **Schedule**: trigger list (add/edit/remove) and a "next 5 runs" preview.
3. **Ignore & Preview**
   - Left: pattern editor (multiline), global-defaults toggle, nested-files toggle.
   - Right: **Index now** (progress + cancel). Tree columns: name, size, files, % of parent, size bar, status (Included / Ignored / Partial). The tooltip on an ignored node shows the matching pattern and its origin.
   - Below the tree: treemap (squarified) of the whole source, with ignored areas greyed out and the selected entry outlined. Clicking a rectangle selects that entry and reveals it in the tree; selecting a tree row outlines it in the treemap.
   - Summary: included vs. ignored size and count.
   - Context menu on a node: *Ignore this*, *Ignore all \*.ext*, *Un-ignore* (adds a `!` pattern).
   - Pattern edits re-evaluate against the cached index after a 300 ms debounce, with no rescan. The index is cached per plan for the session.
4. **Retention**: rule grid (period, anchor, keep) with add/remove. Below it: the **Now** list and the **Full extension** summary + timeline (see 7.3).
5. **History**: run log grid (start, duration, trigger, status, reason, files, bytes). Expanding a row shows skipped files and retention deletions.
6. **Versions**
   - Version list: date, size, file count, manifest present y/n.
   - Tree of the selected version: search box (substring or wildcard), "changed only" toggle.
   - *Compare with…* picks version B. Nodes are marked Added / Changed / Deleted / Unchanged; folders roll up (Changed if any child differs).
   - Selecting a file shows its history across all versions (status per version, size, mtime).
   - Actions: Open, Show in Explorer, Restore to original, Restore to…

### 10.3 Tray

The close button minimizes to the tray when close-to-tray is on. Tray menu: Open, Run plan ▸ (submenu), Pause/Resume scheduler, Exit. The tooltip shows running progress. A balloon notification appears when a run ends (status + duration). Exiting during a run asks whether to cancel it.

### 10.4 Settings dialog

Config folder location (moving it copies the files), default ignore patterns, close-to-tray, start with Windows.

## 11. Change detection (Versions tab)

- Both manifests hashed: equal hash → Unchanged, otherwise Changed.
- One or both without a hash (scan fallback): equal size and mtime → Unchanged, otherwise Changed.
- Present only in B → Added; only in A → Deleted (A = older, B = newer by default; the user can swap).

## 12. Testing

`ReBackup.Core.Tests` (xUnit + FluentAssertions):

- **IgnoreMatcher**: table-driven cases for every rule in §6, including nested-file precedence and the parent-excluded negation rule.
- **RetentionEngine**: anchors `1`, `31` in February, `0`, `-1`; weekly anchors; fall-through; last-of-day tie-break; overlapping rules; newest-always-kept; empty rules; empty target.
- **RetentionSimulator**: the steady-state count for the reference plan (daily 7, weekly Sun 4, monthly 0 12) with a daily trigger.
- **ScheduleCalculator**: each trigger type, DST gap and overlap, month ends, catch-up detection.
- **BackupRunner** (temp dirs): happy path + manifest content, ignore applied, a locked file → `CompletedWithWarnings`, cancellation → `.partial` removed + `Canceled`, a simulated full disk via an injected file-system abstraction → `Full`, same-minute collision.
- **VersionComparer / Restorer** (temp dirs): the A/C/D/U classification, the scan fallback, the restore conflict policies.

UI: manual verification per phase.

## 13. Build phases

Each phase ends with a runnable app.

1. Solution skeleton, `SettingsStore`, `PlanStore`, plan list + General tab, tray shell
2. `IgnoreMatcher`, `SourceIndexer`, `IndexEvaluator`, Ignore & Preview tab (tree first, then treemap)
3. `BackupRunner`, `BackupQueue`, progress/cancel, `RunLog`, History tab
4. `RetentionEngine`, `RetentionSimulator`, Retention tab + previews, retention after runs
5. `ScheduleCalculator`, `Scheduler`, Schedule tab, catch-up, tray pause
6. `VersionCatalog`, `VersionComparer`, `Restorer`, Versions tab
