# Retry Skipped Files — Design

Date: 2026-10-28
Status: Approved (brainstorming), pending spec review

## 1. Purpose

Nightly runs often skip files because an AI agent is still writing to them: the file is locked, or it changes while it
is copied. Such a run ends "Completed with warnings" with dozens of skipped files although a few minutes later every
one of them could be copied.

This design adds a per-plan **retry of skipped files**: after a run, ReBackup remembers the files it could not copy
cleanly and tries them again up to *N* times, every *M* minutes, writing them **into the same version**. Other backups
always go first. When every retryable file is eventually copied, the run counts as **Completed with 0 skipped**
everywhere it is shown.

## 2. Decisions

| Topic | Decision |
|---|---|
| Where a retried file goes | Into the version of the original run; its manifest is replaced atomically |
| Scheduling | A retry is a job in a new low-priority lane of `BackupQueue` (`RunTrigger.Retry`) |
| Priority | A retry starts only when nothing runs and no normal job waits; a started retry is not interrupted |
| History | Every retry attempt is its own run-log line; the log stays append-only |
| Shown status | The original run is shown with its **effective** status: combined with its latest retry line |
| Restart | Pending retries are stored per plan and continue after a restart |
| Superseded | A new regular run of the same plan drops pending retries of the older run |
| Defaults | **On**, **3 attempts**, **5 minutes** — also for existing plans whose file has no retry section |
| Out of scope | Freeing space through retention during a retry; retrying folder-level scan errors; a manual "retry now" button |

## 3. Data model

### 3.1 Plan setting

`BackupPlan` gets a section:

```json
"retrySkipped": { "enabled": true, "attempts": 3, "intervalMinutes": 5 }
```

- C#: `RetrySkippedSettings { bool Enabled = true; int Attempts = 3; int IntervalMinutes = 5 }`, property
  `BackupPlan.RetrySkipped`, never null after load (a missing section yields the defaults).
- `PlanValidator`: `Attempts` 1–20, `IntervalMinutes` 1–720.
- `PlanEditorViewModel`: `_retryEnabled`, `_retryAttempts`, `_retryIntervalMinutes`, each calling `Touch`; carried by
  `ToPlan()` and `LoadFrom()`.
- The setting is independent of "Run on schedule": it applies to runs of every trigger except `Retry`.

### 3.2 Retryable skip reasons

`SkippedEntry` gains a stable key: `record SkippedEntry(string Path, string Reason, string? Key = null)`. The runner
fills `Key` with the `CoreTexts` key it used (`core.file.locked`, `core.skip.changed`, …). Old log lines without a key
keep working (`SkippedTree.KindOf` falls back to text matching as today).

| Retried | Not retried |
|---|---|
| `core.file.locked` (incl. incremental `ContentChanged`) | `core.file.noLongerExists` |
| `core.skip.changed` (copied, but changed while copied) | `core.skip.reservedName` |
| `core.file.accessDenied` | `core.skip.ignoreFileUnreadable` |
| `core.file.cannotOpen` for a **file** | `core.file.cannotOpen` for a **folder** name the target cannot hold |
| | `core.file.archived` (cloud placeholder not available locally) |
| | folder scan errors (`core.scan.*`, OS messages for folders) |

The rule lives in one place: `RetryPolicy.IsRetryable(SkippedEntry)` in `ReBackup.Core/Backup/Retry/`.

### 3.3 Full candidate list

`RunLogEntry.Skipped` keeps its 1000-entry cap. The runner additionally fills an in-memory, non-serialized
`[JsonIgnore] IReadOnlyList<SkippedEntry> RetryCandidates` on the entry it returns, holding **every** retryable skip
without a cap.

### 3.4 Retry state

One file per plan: `retries\<planId>.json` (new entry in `ConfigPaths`), written with `AtomicFile.WriteAllText`.

```json
{
  "planId": "…",
  "runId": "…",             // RunId of the original run
  "version": "2026_10_27-03_00 MoonLabs",
  "attempt": 1,             // attempts made so far
  "attempts": 3,            // attempts allowed (from the plan when the state was created, see 6.1)
  "nextDueUtc": "…",
  "files": [ { "path": "…", "reason": "…", "key": "core.file.locked" } ]
}
```

The state is **deleted** when the file list becomes empty, the attempts are used up, the version no longer exists, a
new regular run of the plan starts, the switch is turned off, source or target of the plan change, or the plan is
deleted.

### 3.5 Run log

A retry attempt is appended as a normal `RunLogEntry` with:

- `Trigger = RunTrigger.Retry`, new fields `RetryOf` (RunId of the original run) and `Attempt` (1-based);
- `Version` = the amended version;
- `FilesCopied` / `BytesCopied` = files fetched by this attempt;
- `SkippedCount` / `Skipped` = retryable files **still open** after this attempt;
- `Status` = `Completed` when nothing is open, `CompletedWithWarnings` when files are still open, `Error` / `Full` /
  `Canceled` as for regular runs.

Both new fields are nullable, so older readers and older log lines are unaffected.

### 3.6 Effective status

`EffectiveRun.Of(original, retries)` (Core, pure) combines an original entry with its retry lines (those whose
`RetryOf` equals its `RunId`):

- `remaining` = open files of the **latest** retry line that has a file result (Error/Full/Canceled lines do not change
  the count), or all retryable skips of the original if no such line exists;
- `effectiveSkipped = remaining + non-retryable skips of the original`;
- `effectiveStatus = Completed` when `effectiveSkipped == 0` and the original has no other warnings (foreign markers,
  retention warnings); otherwise the original status (`CompletedWithWarnings`);
- `recovered = retryable skips of the original − remaining`.

Everything that shows a run uses the effective values: the "Last run" card, the history row of the original run, the
coverage calendar (`CoverageBuilder` maps a version to its original run, not to a retry line), the plan card, the
footer, and the MCP run status (`ControlService`).

A file that is gone at retry time leaves the list and no longer counts as skipped. A copy that an earlier run marked
"changed" stays in the version in that case.

## 4. Flow

### 4.1 After a run

`MainViewModel.OnJobUpdate` (Finished) hands the entry to `RetryPlanner.OnRunFinished(plan, entry)`:

- trigger is `Retry` → see 4.4;
- `plan.RetrySkipped.Enabled` and `entry.RetryCandidates` not empty and the run produced a version → create the state,
  `nextDueUtc = entry.EndUtc + interval`;
- otherwise nothing.

### 4.2 When a retry is due

`RetryPlanner` (Core, `TimeProvider`-based timer, like `Scheduler`) calls an enqueue delegate with a `RetryRequest`
(plan id, run id, version, files, attempt). `MainViewModel.RunRetry` applies the same blockers as `RunScheduled`
(scheduler paused, `IsDeletingVersions`, `CopyFromPlanId`, `IsCopying`) and otherwise puts the job into the
**low-priority lane** of `BackupQueue`. A blocked retry is retried at the next timer tick; waiting never costs an
attempt.

### 4.3 Queue

`BackupQueue` keeps one global worker and gains a second list `_lowQueued`:

- The worker takes the next normal job first; it takes a low job only when `_queued` is empty.
- A running low job is never canceled because a normal job arrives; the normal job waits for it.
- `Enqueue` of a regular request for plan X:
  - a **waiting** retry of X is removed (`JobState.Removed`) and the regular job is queued;
  - a **running** retry of X does not block it any more: the regular job is queued normally behind it.
- `Enqueue` of a retry for plan X is refused when any job of X is queued or running.
- `Changed` events, `PositionOf`, `QueuedPlanIds`, `QueuedCount` include low jobs; `BackupJobUpdate` carries the
  trigger so the UI can show "Retry waiting / running".
- `IBackupRunner` gets `Task<RunLogEntry> RetryAsync(RetryRequest, IProgress<BackupProgress>?, CancellationToken)`;
  the queue dispatches by request type.

### 4.4 A retry attempt (`BackupRunner.RetryAsync`, new partial `BackupRunner.Retry.cs`)

1. Resolve the plan's target; check that the version folder exists and carries no pending/deleting marker. If not, the
   attempt ends with a `VersionGone` result: the planner drops the state, **no** log line is written.
2. Incremental: acquire `TargetLock` for the whole attempt, exactly as a regular run.
3. Read the current manifest and remember its stamp.
4. For each file, with the same read and change detection as a regular run (`ChangedAfterReadAsync`, pool hash check):
   - source gone → drop from the list (resolved);
   - copied cleanly → add or replace the manifest entry;
     - **Full copies**: write into the version folder with `CreateOptions(Overwrite: true, …)` (temp + move);
     - **Incremental**: `PoolWriter.PutAsync`, entry refers to the blob;
   - still locked / changed / denied → stays in the list.
5. If at least one entry changed: recompute `FileCount` and `TotalBytes` and replace the manifest with
   `CreateOptions(Overwrite: true, ExpectedStamp: <stamp from 3>, Durable: true)`.
6. Push the amended version through `IVersionIndexSink.Add`.
7. Return the retry `RunLogEntry` (3.5). The queue appends it to the run log as for every job.

`RetryPlanner.OnRunFinished` for a retry line: remove resolved files, `attempt++`; if the list is empty or
`attempt == attempts` delete the state, else `nextDueUtc = EndUtc + interval` (interval read from the current plan).
A `Canceled` attempt does not count.

### 4.5 A regular run of the same plan

When a regular job of plan X **starts** (`JobState.Running`, trigger ≠ Retry), the planner deletes X's state. The new
run creates its own state from its own skips (4.1).

### 4.6 Exit and restart

- On exit `BackupQueue.Close` cancels a running retry; the attempt does not count and the state stays on disk.
- On start `RetryPlanner.Start` loads all states and drops those whose plan is missing or has the switch off, whose
  version is gone, or whose plan has a regular run-log entry newer than `runId`. Overdue states become due no earlier
  than one minute after start (as catch-up).
- `Scheduler` catch-up (`App.xaml.cs` L143) must take the last entry whose trigger is **not** `Retry`; otherwise a
  retry line would hide a missed scheduled run. Add `RunLog.ReadLastRegular()`.

## 5. UI and texts

### 5.1 Source → Target card (`PlanView.xaml`)

Below the two existing switches, for every target kind:

- Switch **"Retry skipped files later"** / **"Übersprungene Dateien später erneut versuchen"**, tooltip: files that
  were locked or changed while copied are copied again after the run and added to the same version; other backups go
  first.
- Indented below, disabled while the switch is off: **"Up to [3] attempts, every [5] minutes"** /
  **"Bis zu [3] Versuche, alle [5] Minuten"** with two narrow numeric text boxes bound to the editor properties.

### 5.2 Last run card

- Status line and the Skipped tile show the effective values (3.6).
- One line below the tiles, only when the run has retry information:
  - pending: "Retry 2/3 at 04:30 · 12 open"
  - done, all recovered: "73 files recovered by retry"
  - done, attempts used up: "5 still skipped after 3 retries"

### 5.3 Plan card, history

- Plan card (`PlanRunViewModel`): "Retry waiting", "Retry running", "Retry at 04:30" while a state exists.
- History: retry lines show trigger "Retry" and "Retry 1/3 · 61 recovered · 12 open"; the original line shows its
  effective status.

### 5.4 Locale keys

Added to `en-US.json` and `de-DE.json` with matching placeholders and plurals: `plan.source.retry`,
`plan.source.retryToolTip`, `plan.source.retryAttempts`, `plan.lastRun.retryPending`, `plan.lastRun.retryRecovered`,
`plan.lastRun.retryExhausted`, `card.retryWaiting`, `card.retryRunning`, `card.retryAt`, `history.retryLine`,
`enum.runTrigger.Retry`. No new `CoreTexts` templates are needed (keys are reused).

## 6. Error handling

| Case | Behaviour |
|---|---|
| Version deleted meanwhile (retention, user) | State dropped, no log line |
| Target unreachable, manifest unreadable, `ExpectedStamp` mismatch | Retry line with `Error`; counts as an attempt; state kept |
| Target full | Retry line with `Full`; counts as an attempt; no space is freed |
| Crash during an incremental retry | Blobs without a manifest reference are removed by the next GC |
| Crash between overwriting a "changed" file and the manifest swap (full copies) | The file is newer than its manifest hash. The state is still on disk, so the next attempt after restart copies it again and fixes the manifest. Only if the state is dropped first (a new regular run starts) does the mismatch stay in that old version; a verify reports it. Accepted residual risk. |
| Source file gone at retry time | Removed from the list; not counted as skipped |
| Plan settings changed while a state exists | Switch off, source or target changed → state dropped; new numbers apply from the next attempt (`attempts` is capped at the new value) |

### 6.1 Attempt count when the setting changes

`attempts` in the state is set from the plan when the state is created. Saving the plan with a lower value lowers it
(`attempts = min(attempts, new)`); a higher value applies to the next run only.

## 7. Testing

`tests/ReBackup.Core.Tests`:

- `Backup/Retry/RetryPolicyTests` — every reason key from 3.2.
- `Backup/Retry/RetryPlannerTests` (`FakeTimeProvider`) — state created with the right due time; attempts counted,
  canceled attempts not; state saved and reloaded; dropped for each condition in 3.4 and 4.6; overdue start waits a
  minute.
- `Backup/BackupQueueTests` — low job runs only when no normal job waits; a normal job enqueued during a running retry
  waits for it; a regular request replaces a waiting retry of the same plan and is queued behind a running one; a retry
  is refused while the plan is queued or running.
- `Backup/BackupRunnerRetryTests` (full copies and incremental) — a locked file is unlocked and the retry adds it:
  manifest entry, `FileCount`, `TotalBytes`, index updated; a "changed" file is replaced; a gone source file is
  resolved; a missing version yields `VersionGone`; a stamp mismatch yields `Error`; incremental attempt holds the
  target lock.
- `Backup/EffectiveRunTests` — all recovered → Completed with 0 skipped; partly recovered; other warnings keep
  `CompletedWithWarnings`; Error lines do not change the count.
- `Schedule` — catch-up ignores retry lines (`ReadLastRegular`).
- `Plans` — missing `retrySkipped` section loads with the defaults (on, 3, 5); validator ranges.

`tests/ReBackup.App.Tests`:

- `PlanEditorViewModel` round-trip of the three settings.
- `RunHistoryRow` / Last run card: effective status, Skipped tile, hint line for pending, recovered and exhausted.
- `LocaleFileTests` cover the new keys automatically.
