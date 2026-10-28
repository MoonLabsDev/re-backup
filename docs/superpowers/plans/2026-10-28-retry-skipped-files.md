# Retry Skipped Files Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** After a run, retry the files it skipped because they were locked, changed or refused, up to N times every M
minutes, adding them to the same version, so that a run whose files were all recovered shows Completed with 0 skipped.

**Architecture:** A per-plan `RetryPlanner` (Core, `TimeProvider` timer, one JSON state file per plan) decides when a
retry is due and hands a `RetryRequest` to a new low-priority lane of `BackupQueue`. `BackupRunner.RetryAsync` copies
only the remembered files, merges them into the version's manifest with a conditional overwrite and logs a `Retry` line.
A pure `EffectiveRun` combines an original run with its retry lines for every place that shows a run.

**Tech Stack:** .NET 9, WPF, CommunityToolkit.Mvvm, xUnit 2.9 + FluentAssertions 7, `FakeTimeProvider`.

**Spec:** `docs/superpowers/specs/2026-10-28-retry-skipped-files-design.md`

## Global Constraints

- Defaults: `Enabled = true`, `Attempts = 3`, `IntervalMinutes = 5`; a plan file without `retrySkipped` loads with them.
- Ranges: `Attempts` 1–20, `IntervalMinutes` 1–720.
- New trigger value `RunTrigger.Retry`, appended last in the enum (values are stored as names, order matters for nothing else).
- The run log stays append-only; every new `RunLogEntry` field is nullable or defaults to 0/false so old lines parse.
- Retry state file: `retries\<planId>.json` under the config root, written with `AtomicFile.WriteAllText`.
- One backup at a time, globally; a retry starts only when no normal job waits; a running retry is never canceled because
  a normal job arrives.
- A retried file's manifest entry is built entirely from the retry's read (size, mtime, hash).
- Every new text in `src/ReBackup.App/Locales/en-US.json` and `de-DE.json` (same keys, placeholders, plurals).
- Core stays UI-free.
- Work on branch `feature/retry-skipped`; merge into `main` with `--no-ff` when done.

## Review Focus

1. **Files in subfolders** (`Projekte/a/b.txt`): a retry builds its `IndexNode` from a path, and `IndexNode.RelativePath`
   is computed from the parent chain; a bare node would write `b.txt` into the version root — pinned in Task 5.
2. **Paths that differ only in case** on a case-insensitive target (`Foo.txt` vs `foo.txt` in the manifest): the merge
   must replace the entry, not add a second one — pinned in Task 5.
3. **More than 1000 skipped files**: `Skipped` is capped, the retry list and the effective count must not be — pinned in
   Tasks 2 and 3.
4. **A file locked on every attempt**: after the last attempt the state is gone and the run stays "Completed with
   warnings" with the open count — pinned in Tasks 3 and 7.
5. **App closed during a retry**: the canceled attempt does not count and the state survives — pinned in Task 7.

---

### Task 1: Plan setting

**Files:**
- Modify: `src/ReBackup.Core/Plans/BackupPlan.cs`
- Modify: `src/ReBackup.Core/Plans/PlanValidator.cs`
- Modify: `src/ReBackup.Core/Config/ConfigPaths.cs`
- Test: `tests/ReBackup.Core.Tests/Plans/RetrySkippedSettingsTests.cs`

**Interfaces:**
- Produces: `public sealed class RetrySkippedSettings { bool Enabled = true; int Attempts = 3; int IntervalMinutes = 5; }`
  (namespace `ReBackup.Core.Plans`), `BackupPlan.RetrySkipped` (never null: setter maps null to `new()`),
  `RetrySkippedSettings.MinAttempts = 1, MaxAttempts = 20, MinIntervalMinutes = 1, MaxIntervalMinutes = 720`,
  `ConfigPaths.RetriesDirectory`, `ConfigPaths.RetryFileFor(string planId)`.

- [ ] **Step 1: Create branch** — `git switch -c feature/retry-skipped`
- [ ] **Step 2: Write the failing tests**

```csharp
[Fact] public void A_plan_without_a_retry_section_retries_three_times_every_five_minutes()
{
    var plan = JsonSerializer.Deserialize<BackupPlan>("""{"name":"P"}""", JsonDefaults.Options)!;
    plan.RetrySkipped.Enabled.Should().BeTrue();
    plan.RetrySkipped.Attempts.Should().Be(3);
    plan.RetrySkipped.IntervalMinutes.Should().Be(5);
}
[Fact] public void Retry_settings_survive_a_save_and_load() // Attempts 7, IntervalMinutes 45, Enabled false round-trip via Clone()
[Theory, InlineData(0, 5), InlineData(21, 5), InlineData(3, 0), InlineData(3, 721)]
public void Retry_settings_outside_their_range_are_invalid(int attempts, int minutes) // PlanValidator.Validate(...) has an error
[Fact] public void Retry_files_live_in_the_retries_folder() // new ConfigPaths(@"C:\r").RetryFileFor("abc") == @"C:\r\retries\abc.json"
```

- [ ] **Step 3: Run** `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~RetrySkippedSettingsTests"` — expected FAIL (type missing).
- [ ] **Step 4: Implement** the class and property; validator error keys `core.plan.retryAttempts` and
  `core.plan.retryInterval` (add to `CoreTexts.cs` and both locale files, see how other `core.plan.*` keys are added);
  the two `ConfigPaths` members.
- [ ] **Step 5: Run** the same filter plus `--filter "FullyQualifiedName~LocaleFileTests"` — expected PASS.
- [ ] **Step 6: Commit** — `feat(plans): retry skipped files setting`

---

### Task 2: Skip keys, retry candidates and new log fields

**Files:**
- Create: `src/ReBackup.Core/Backup/Retry/RetryPolicy.cs`
- Modify: `src/ReBackup.Core/Backup/RunLog.cs`
- Modify: `src/ReBackup.Core/Backup/BackupRunner.cs`, `BackupRunner.Pool.cs`
- Modify: `src/ReBackup.Shared/Schedule/RunTrigger.cs`
- Test: `tests/ReBackup.Core.Tests/Backup/Retry/RetryPolicyTests.cs`, `tests/ReBackup.Core.Tests/Backup/BackupRunnerTests.cs`, `tests/ReBackup.Core.Tests/Backup/RunLogTests.cs`

**Interfaces:**
- Produces:
  - `record SkippedEntry(string Path, string Reason, string? Key = null)` — `Key` is the `CoreTexts` key.
  - `RunLogEntry` new persisted members: `string? RetryOf`, `int? Attempt`, `int? Attempts`, `int RetryableCount`,
    `bool MetForeignMarkers`.
  - `RunLogEntry` new `[JsonIgnore]` members: `List<SkippedEntry> RetryCandidates` (uncapped), `bool VersionGone`,
    `int RetryRecovered`, `int RetryAttemptsMade` (the last two are set by `EffectiveRun`, Task 3).
  - `RunLogEntry.AddSkipped(SkippedEntry)` also appends to `RetryCandidates` and increments `RetryableCount` when
    `RetryPolicy.IsRetryable(entry)`.
  - `static class RetryPolicy { bool IsRetryable(SkippedEntry entry); }` — true for keys `core.file.locked`,
    `core.skip.changed`, `core.file.accessDenied`, `core.file.cannotOpenFile`; false for everything else and for `Key == null`.
  - `RunTrigger.Retry`.
  - `RunLog.ReadLastRegular()` — newest entry whose `Trigger != Retry`; null when none.

`core.file.cannotOpen` is used for both files and folder names; split the folder use in `CopyAndFinishAsync` (the
`EnsureDirectoryAsync` catch) so it records `Key = "core.file.cannotOpenFolder"` while keeping the same English text,
and file uses record `Key = "core.file.cannotOpenFile"`. Keys are not templates, so `CoreTexts` and the locale files do
not change. Every `new SkippedEntry(...)` in the runner passes the key it formatted; `SourceSkipReason` returns
`(string Reason, string Key)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// RetryPolicyTests
[Theory, InlineData("core.file.locked", true), InlineData("core.skip.changed", true),
 InlineData("core.file.accessDenied", true), InlineData("core.file.cannotOpenFile", true),
 InlineData("core.file.noLongerExists", false), InlineData("core.skip.reservedName", false),
 InlineData("core.skip.ignoreFileUnreadable", false), InlineData("core.file.cannotOpenFolder", false),
 InlineData("core.file.archived", false), InlineData(null, false)]
public void Only_skips_that_may_pass_later_are_retried(string? key, bool retried)

// BackupRunnerTests (existing locked-file setup)
[Fact] public async Task A_locked_file_is_a_retry_candidate_with_its_key()
    // entry.RetryCandidates.Should().ContainSingle(c => c.Path == "locked.txt" && c.Key == "core.file.locked");
    // entry.RetryableCount.Should().Be(1);
[Fact] public async Task A_file_that_changed_while_copied_is_a_retry_candidate() // key core.skip.changed
[Fact] public async Task More_than_a_thousand_retryable_skips_are_all_candidates()
    // entry built directly: 1200 × AddSkipped(locked) → Skipped.Count 1000, RetryCandidates.Count 1200, RetryableCount 1200
[Fact] public async Task Foreign_markers_are_recorded_on_the_entry() // MetForeignMarkers true (existing foreign-marker setup)

// RunLogTests
[Fact] public void Retry_candidates_are_not_written_to_the_log() // Append + ReadAll → RetryCandidates empty, RetryableCount kept
[Fact] public void The_last_regular_entry_skips_retry_lines() // Manual, Retry, Retry → ReadLastRegular().Trigger == Manual
[Fact] public void A_log_line_written_before_retries_existed_still_reads() // JSON without the new fields → RetryOf null, Key null
```

- [ ] **Step 2: Run** `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~RetryPolicyTests|FullyQualifiedName~RunLogTests|FullyQualifiedName~BackupRunnerTests"` — expected FAIL.
- [ ] **Step 3: Implement** as in Interfaces; `RunAsync` sets `entry.MetForeignMarkers = prepared.MetForeignMarkers`.
  `ReadLastRegular` reads the tail like `ReadLast` and falls back to `ReadAll`.
- [ ] **Step 4: Run** the same filter and the whole Core test project — expected PASS.
- [ ] **Step 5: Commit** — `feat(backup): skip keys, retry candidates and retry log fields`

---

### Task 3: Effective run

**Files:**
- Create: `src/ReBackup.Core/Backup/Retry/EffectiveRun.cs`
- Test: `tests/ReBackup.Core.Tests/Backup/Retry/EffectiveRunTests.cs`

**Interfaces:**
- Consumes: Task 2 fields.
- Produces: `static class EffectiveRun { IReadOnlyList<RunLogEntry> Apply(IReadOnlyList<RunLogEntry> log); }` —
  same order and count as `log`; every original that has retry lines (`RetryOf == RunId`) is replaced by a merged copy
  (via JSON clone) with:
  - `SkippedCount = (orig.SkippedCount − orig.RetryableCount) + remaining`, where `remaining` is `SkippedCount` of the
    latest retry line whose status is `Completed` or `CompletedWithWarnings`; no such line → original unchanged.
  - `Skipped` = the original's non-retryable entries followed by that retry line's `Skipped`, capped at
    `MaxSkippedEntries`.
  - `Status = Completed` when the new `SkippedCount == 0 && !orig.MetForeignMarkers`, else `CompletedWithWarnings`.
  - `RetryRecovered = orig.RetryableCount − remaining`, `RetryAttemptsMade` = number of retry lines of any status
    except `Canceled`.
  - Retry lines themselves are returned unchanged.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void A_run_whose_files_were_all_recovered_is_completed_with_nothing_skipped()
    // orig: CompletedWithWarnings, SkippedCount 73, RetryableCount 73; retry: Completed, SkippedCount 0
    // merged.Status.Should().Be(RunStatus.Completed); merged.SkippedCount.Should().Be(0); merged.RetryRecovered.Should().Be(73);
[Fact] public void Partly_recovered_keeps_the_warning_with_the_open_count() // retry SkippedCount 5 → 5, CompletedWithWarnings
[Fact] public void Skips_that_are_never_retried_stay_counted() // orig 73 skipped, 70 retryable; retry 0 → SkippedCount 3
[Fact] public void Foreign_markers_keep_the_warning() // MetForeignMarkers true, retry 0 → CompletedWithWarnings, 0 skipped
[Fact] public void Retention_warnings_stay_on_a_recovered_run() // orig.Warnings ["x"] → merged Completed, Warnings ["x"]
[Fact] public void A_failed_retry_does_not_change_the_count() // retries: CWW 12 open, then Error → 12, RetryAttemptsMade 2
[Fact] public void The_latest_retry_wins() // 61 open, then 12 open → 12
[Fact] public void A_canceled_retry_is_not_an_attempt() // Canceled line → RetryAttemptsMade 0
[Fact] public void More_than_a_thousand_recovered_counts_beyond_the_cap() // RetryableCount 1500, retry 0 open → RetryRecovered 1500
[Fact] public void A_run_without_retries_is_returned_as_is() // same instance
```

- [ ] **Step 2: Run** `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~EffectiveRunTests"` — FAIL.
- [ ] **Step 3: Implement** `Apply` (one pass building `RunId → retry lines`, then a second pass replacing originals).
- [ ] **Step 4: Run** the filter — PASS.
- [ ] **Step 5: Commit** — `feat(backup): effective status of a run and its retries`

---

### Task 4: Low-priority lane in the queue

**Files:**
- Create: `src/ReBackup.Core/Backup/Retry/RetryRequest.cs`
- Modify: `src/ReBackup.Core/Backup/BackupQueue.cs`, `src/ReBackup.Core/Backup/BackupRunner.cs` (interface only)
- Test: `tests/ReBackup.Core.Tests/Backup/BackupQueueTests.cs` (extend `FakeRunner` with `RetryAsync`, gated like `RunAsync`)
- Modify: every other `IBackupRunner` implementation in tests (search `: IBackupRunner`)

**Interfaces:**
- Produces:
  - `sealed record RetryRequest(BackupPlan Plan, string RunId, string Version, IReadOnlyList<SkippedEntry> Files, int Attempt, int Attempts)`.
  - `IBackupRunner.RetryAsync(RetryRequest request, IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default) : Task<RunLogEntry>` — never throws, like `RunAsync`.
  - `BackupJobUpdate` gains a last positional parameter `bool IsRetry = false`.
  - `BackupQueue.EnqueueRetry(RetryRequest request) : bool` — false when closed or any job of that plan is queued or running.
  - `BackupQueue.Enqueue(BackupRequest)` — removes a **waiting** retry of the plan (raising `Removed` with
    `IsRetry: true`) and accepts the request even while a retry of the plan **runs**; still false when a regular job of
    the plan is queued or running.
  - `BackupQueue.IsRetryRunning(string planId) : bool`.
  - Worker: takes `_queued[0]` if any, else `_lowQueued[0]`. A failed-outright retry yields an entry with
    `Trigger = Retry`, `RetryOf`, `Attempt`, `Attempts` set. A result with `VersionGone` is **not** appended to the log
    but still raises `Finished`.
  - `Cancel(planId)` targets the plan's regular job first, else its retry job. `IsBusy`, `QueuedCount`, `QueuedPlanIds`
    and `PositionOf` count low jobs after normal ones.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task A_retry_waits_while_a_normal_job_is_queued()
    // A running, B queued, retry of C queued → after A: B starts, not C; after B: C starts
[Fact] public async Task A_normal_job_waits_for_a_running_retry() // retry C running, enqueue A → A starts only after C completes; C not canceled
[Fact] public async Task A_regular_run_replaces_a_waiting_retry_of_its_plan()
    // retry of A waiting behind B → Enqueue(A) true; Removed raised with IsRetry true; A runs as regular
[Fact] public async Task A_regular_run_is_queued_behind_a_running_retry_of_its_plan() // Enqueue(A) true while retry A runs
[Fact] public void A_retry_is_refused_while_its_plan_is_queued_or_running() // EnqueueRetry false
[Fact] public async Task A_retry_whose_version_is_gone_is_not_logged() // result.VersionGone → log empty, Finished raised
[Fact] public async Task Close_cancels_a_running_retry() // FakeRunner observes cancellation; result Canceled
```

- [ ] **Step 2: Run** `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~BackupQueueTests"` — FAIL.
- [ ] **Step 3: Implement**: `Job` carries either request (`PlanId`, `PlanName`, `IsRetry`); `RunAsync(Job)` dispatches
  by type. Keep the existing lock/raise ordering rules (doc comment on `Changed`).
- [ ] **Step 4: Run** the whole Core and App test projects (the interface change touches fakes) — PASS.
- [ ] **Step 5: Commit** — `feat(queue): low-priority lane for retries`

---

### Task 5: Retry for full copies

**Files:**
- Create: `src/ReBackup.Core/Backup/BackupRunner.Retry.cs`
- Modify: `src/ReBackup.Core/Backup/BackupRunner.cs` (`CopyFileAsync` gains `bool overwrite = false` → `CreateOptions(Overwrite: overwrite, ModifiedUtc: …)`)
- Modify: `src/ReBackup.Core/Indexing/SourceIndexer.cs` (factory below)
- Test: `tests/ReBackup.Core.Tests/Backup/BackupRunnerRetryTests.cs`

**Interfaces:**
- Consumes: `RetryRequest`, `RetryPolicy`, Task 2 fields.
- Produces: `BackupRunner.RetryAsync` (full plans); `internal static IndexNode IndexNode.ForFile(string relativePath, StorageEntry entry)`
  — builds the folder chain so `RelativePath` equals `relativePath`; sets `Stamp` only when `entry.Size == 0`.

Algorithm (spec 4.4), all inside `Task.Run` like `RunAsync`:
1. Entry: new `RunId`, `Trigger = Retry`, `RetryOf`, `Attempt`, `Attempts`, `Version`, `StartUtc`.
2. Open storages; `VersionCatalog.ListAsync(target, plan.Id, plan.Name)`; no owned version named `request.Version` →
   `VersionGone = true`, return.
3. Stat the manifest for its `Stamp`; deserialize `BackupManifest`.
4. Per file: `source.StatAsync` → null or `StorageNotFoundException` = resolved (dropped, nothing written). Otherwise
   `CopyFileAsync(..., overwrite: true)`.
   - copy returned → replace the manifest entry (dictionary keyed by path with the target's comparer, see
     `ReadPreviousManifestAsync`); `FilesCopied++`, `BytesCopied +=`; if `changed` the file stays open with
     `core.skip.changed` (the entry is still replaced: the file on disk is the new copy).
   - no copy → stays open with the new reason/key via `AddSkipped`.
5. Any entry replaced → recompute `FileCount`/`TotalBytes`, write `re-manifest.json` with
   `CreateOptions(Overwrite: true, Durable: true, ExpectedStamp: stamp)`; then `AddToIndex(plan, target, entry, version, manifest)`.
6. Status: `Completed` if nothing open, else `CompletedWithWarnings`; `StorageConflictException` → `Error` with its
   message; `StorageFullException` → `Full` (`core.run.targetFull`); cancel → `Canceled`; other → `Error`.

- [ ] **Step 1: Write the failing tests** (temp folders and `FileShare.None` lock as in `BackupRunnerTests`)

```csharp
[Fact] public async Task A_file_locked_during_the_run_is_added_to_its_version_by_the_retry()
    // run with "a/b/locked.txt" locked → unlock → RetryAsync
    // File.Exists(Path.Combine(target, version, "a", "b", "locked.txt")).Should().BeTrue();
    // manifest.Files.Should().ContainSingle(f => f.Path == "a/b/locked.txt" && f.Hash == "xxh64:" + <hash of content>);
    // manifest.FileCount.Should().Be(<run count + 1>); retry.Status.Should().Be(RunStatus.Completed); retry.FilesCopied.Should().Be(1);
[Fact] public async Task A_file_that_changed_while_copied_gets_the_new_content_and_hash()
    // version file content and manifest hash both equal the content at retry time; manifest has one entry for the path
[Fact] public async Task A_path_that_differs_only_in_case_replaces_its_entry() // manifest "Foo.txt", retry path "foo.txt" → one entry
[Fact] public async Task A_file_still_locked_stays_open_and_the_version_is_unchanged()
    // retry CompletedWithWarnings, RetryCandidates single, manifest bytes unchanged
[Fact] public async Task A_file_deleted_since_the_run_is_resolved() // retry Completed, FilesCopied 0, SkippedCount 0
[Fact] public async Task A_version_that_is_gone_is_reported_and_nothing_is_written() // delete version folder → VersionGone true
[Fact] public async Task A_manifest_changed_meanwhile_fails_the_retry()
    // WrappedStorage that rewrites the manifest between stat and commit → Status Error, the other manifest kept
[Fact] public async Task The_amended_version_is_sent_to_the_index() // fake IVersionIndexSink receives the version with the new file
```

- [ ] **Step 2: Run** `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~BackupRunnerRetryTests"` — FAIL.
- [ ] **Step 3: Implement** `RetryAsync` and `IndexNode.ForFile`.
- [ ] **Step 4: Run** the filter and `BackupRunnerTests` — PASS.
- [ ] **Step 5: Commit** — `feat(backup): retry skipped files into a full-copy version`

---

### Task 6: Retry for incremental plans

**Files:**
- Modify: `src/ReBackup.Core/Backup/BackupRunner.Retry.cs`
- Test: `tests/ReBackup.Core.Tests/Backup/BackupRunnerRetryTests.cs` (incremental section, use `PoolVersions` helpers)

**Interfaces:**
- Consumes: Task 5 algorithm. For `plan.Incremental`: acquire `TargetLock.TryAcquireAsync(target, _host, LockOperation, _time, …)`
  before step 2 (null → `Error` with `TargetLock.LockedMessageAsync`), release in `finally` with `ReleaseLockAsync`;
  copy through `CopyFileToPoolAsync` with `new PoolRun(new PoolWriter(target), ChangeDetection.Safe, previous: new())`
  after `LoadKnownAsync` (Safe: never reuse the old entry). No retention, no GC.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task An_incremental_retry_stores_the_blob_and_points_the_manifest_at_it()
    // PoolLayout blob for the new xxh128 exists; manifest entry Hash == that hash
[Fact] public async Task An_incremental_retry_holds_the_target_lock() // GatedStorage pauses in the copy → TargetLock present
[Fact] public async Task An_incremental_retry_refuses_a_locked_target() // foreign lock → Status Error, nothing written
[Fact] public async Task The_old_blob_of_a_retried_file_is_collected_by_the_next_run()
    // retry a "changed" file, then a regular run → old blob gone (no other manifest refers to it)
```

- [ ] **Step 2: Run** the filter — FAIL. **Step 3: Implement.** **Step 4: Run** filter + `Pool` tests — PASS.
- [ ] **Step 5: Commit** — `feat(backup): retry skipped files into an incremental version`

---

### Task 7: Retry planner

**Files:**
- Create: `src/ReBackup.Core/Backup/Retry/RetryState.cs`, `src/ReBackup.Core/Backup/Retry/RetryPlanner.cs`
- Test: `tests/ReBackup.Core.Tests/Backup/Retry/RetryPlannerTests.cs`

**Interfaces:**
- Consumes: Tasks 1–4.
- Produces:
  - `sealed class RetryState { string PlanId, RunId, Version; StorageLocation Source, Target; int Attempt; int Attempts; DateTime NextDueUtc; List<SkippedEntry> Files; }` (JSON via `JsonDefaults.Options`).
  - `sealed class RetryPlanner : IDisposable`
    - `RetryPlanner(string directory, Action<RetryState> due, TimeProvider? timeProvider = null)`
    - `void Start(Func<string, BackupPlan?> planOf, Func<string, RunLogEntry?> lastRegularRunOf)` — loads every state;
      deletes those whose plan is null, `!RetrySkipped.Enabled`, source/target differ, or whose last regular run's
      `RunId != state.RunId`; sets `NextDueUtc = max(NextDueUtc, now + 1 min)`; arms the timer. The version is checked by
      the attempt itself (no storage I/O at start).
    - `void OnRunStarted(string planId, bool isRetry)` — regular start deletes the plan's state.
    - `void OnRunFinished(BackupPlan plan, RunLogEntry entry)` — regular run: creates a state when
      `plan.RetrySkipped.Enabled && entry.Version is not null && entry.RetryCandidates.Count > 0`
      (`Attempt 0`, `Attempts = plan.RetrySkipped.Attempts`, `NextDueUtc = entry.EndUtc + interval`); retry line:
      `VersionGone` → delete; `Canceled` → keep, not counted, due again in 1 min; otherwise `Files = entry.RetryCandidates`,
      `Attempt++`, delete when `Files` empty or `Attempt >= Attempts`, else `NextDueUtc = entry.EndUtc + plan interval`.
    - `void Defer(string planId)` — a due state that could not be queued: due again in 1 minute.
    - `void OnPlanSaved(BackupPlan plan)` — delete when disabled or source/target changed; `Attempts = min(Attempts, plan.RetrySkipped.Attempts)`.
    - `void OnPlanDeleted(string planId)`.
    - `RetryState? StateOf(string planId)`; `event Action<string>? Changed` (plan id; raised after every change).
  - Timer: one `ITimer` from `TimeProvider.CreateTimer`, re-armed to the earliest `NextDueUtc` of states not in flight;
    a fired state is marked in flight (not persisted) until `OnRunFinished`/`Defer`/deletion.

- [ ] **Step 1: Write the failing tests** (`FakeTimeProvider`, `TempDir`)

```csharp
[Fact] public void A_run_with_retryable_skips_is_retried_after_the_interval()
    // OnRunFinished(plan{3, 5 min}, entry with 2 candidates, EndUtc t) → Advance(4:59) no due; Advance(0:01) due once
[Fact] public void A_run_without_candidates_or_with_retries_off_plans_nothing()
[Fact] public void Recovered_files_leave_the_list_and_an_attempt_is_counted() // retry line with 1 open → Files 1, Attempt 1
[Fact] public void The_state_is_deleted_when_everything_was_recovered()
[Fact] public void The_state_is_deleted_after_the_last_attempt() // 3 lines with 1 open each → StateOf null, file gone
[Fact] public void A_canceled_attempt_is_not_counted() // Attempt stays, due again after 1 min
[Fact] public void A_gone_version_drops_the_state()
[Fact] public void A_regular_run_of_the_plan_drops_its_state() // OnRunStarted(id, isRetry: false)
[Fact] public void A_deferred_retry_is_due_again_a_minute_later()
[Fact] public void Pending_retries_survive_a_restart() // new planner on same dir, Start(...) → StateOf equal, due ≥ now + 1 min
[Fact] public void A_restart_drops_states_superseded_disabled_or_moved() // three plans, one per reason
[Fact] public void Turning_retries_off_or_moving_the_target_drops_the_state() // OnPlanSaved
[Fact] public void Lowering_the_attempts_caps_a_pending_state() // Attempts 3 → plan 1 → state Attempts 1
[Fact] public void The_interval_of_the_saved_plan_is_used_for_the_next_attempt() // 5 → 30 min between attempts
```

- [ ] **Step 2: Run** `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~RetryPlannerTests"` — FAIL.
- [ ] **Step 3: Implement.** **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `feat(backup): retry planner with persisted state`

---

### Task 8: App wiring

**Files:**
- Modify: `src/ReBackup.App/App.xaml.cs` (create planner next to the scheduler, L273-288; `Start` after the scheduler, L143; dispose on exit)
- Modify: `src/ReBackup.App/ViewModels/MainViewModel.cs`
- Modify: `src/ReBackup.App/Control/ControlService.Read.cs` (L45, L369, L382)
- Modify: `tests/ReBackup.App.Tests/TestSupport/MainViewModelFixture.cs`
- Test: `tests/ReBackup.App.Tests/ViewModels/MainViewModelRetryTests.cs`

**Interfaces:**
- Consumes: Tasks 4 and 7.
- Produces:
  - `MainViewModel` constructor takes a `RetryPlanner` after `scheduler`.
  - `MainViewModel.RunRetry(RetryState state)` (UI thread, via `Dispatcher.InvokeAsync` from the planner callback): same
    blockers as `RunScheduled` (scheduler paused, `IsCopying`, `CopyFromPlanId`, `IsDeletingVersions`,
    `ScheduledRunBlocker`) → `planner.Defer`; otherwise
    `queue.EnqueueRetry(new RetryRequest(savedPlan, state.RunId, state.Version, state.Files, state.Attempt + 1, state.Attempts))`
    (the request's `Attempt` is 1-based), `false` → `Defer`.
    Blocked retries are never logged.
  - `OnJobUpdate`: `Running` → `planner.OnRunStarted(id, update.IsRetry)`; `Finished` → `planner.OnRunFinished(savedPlan, result)`.
    For a retry the footer uses `shell.status.retryFinished` (`plan`, `recovered`, `open`) and `RunFinished` is not
    raised (no tray notification per attempt).
  - Plan save → `planner.OnPlanSaved(saved)`; `DeletePlan` → `OnPlanDeleted`; `ReloadFromDisk` → `OnPlanSaved` for
    every reloaded plan, `OnPlanDeleted` for removed ones.
  - `planner.Changed` → `Dispatcher.InvokeAsync(() => editor.Run.SetRetry(planner.StateOf(id)))` (Task 9).
  - `App.xaml.cs` L143: `ReadLastRegular()` instead of `ReadLast()`.
  - `ControlService.Read`: run lists go through `EffectiveRun.Apply`; the "last run" at L45 is the last non-retry entry
    of `EffectiveRun.Apply(ReadAll())`.

- [ ] **Step 1: Write the failing tests** (fixture with a real queue and planner on a `TempDir`)

```csharp
[Fact] public async Task A_due_retry_runs_and_recovers_a_file_that_was_locked() // end-to-end: run, unlock, advance → history shows effective Completed
[Fact] public void A_due_retry_is_deferred_while_versions_are_deleted() // nothing queued, nothing logged, Defer
[Fact] public async Task A_finished_retry_raises_no_run_notification() // RunFinished not invoked
[Fact] public void Saving_the_plan_with_retries_off_drops_the_pending_state()
[Fact] public async Task Catch_up_ignores_retry_lines() // scheduler start function returns the regular run's start
```

- [ ] **Step 2: Run** `dotnet test tests/ReBackup.App.Tests --filter "FullyQualifiedName~MainViewModelRetryTests"` — FAIL.
- [ ] **Step 3: Implement**; add `shell.status.retryFinished` and `enum.runTrigger.Retry` ("Retry" / "Wiederholung") to both locales.
- [ ] **Step 4: Run** both test projects (`dotnet test ReBackup.sln`) — PASS.
- [ ] **Step 5: Commit** — `feat(app): wire the retry planner into runs, plan edits and control`

---

### Task 9: UI

**Files:**
- Modify: `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs` (L51-52, `Touch` handlers L377-383, `ToPlan` L449-464, `LoadFrom` L709-731)
- Modify: `src/ReBackup.App/Views/PlanView.xaml` (below L203-207; Last run card L290-315)
- Modify: `src/ReBackup.App/ViewModels/PlanRunViewModel.cs`, `RunHistoryRow.cs`, `CoverageBuilder.cs`, `MainViewModel.cs` (`LoadHistory` L897-909)
- Modify: `src/ReBackup.App/Locales/en-US.json`, `de-DE.json`
- Test: `tests/ReBackup.App.Tests/ViewModels/PlanEditorRetryTests.cs`, `RunHistoryRowRetryTests.cs`, `CoverageBuilderTests.cs`

**Interfaces:**
- Consumes: `EffectiveRun.Apply`, `RetryState`, `BackupJobUpdate.IsRetry`.
- Produces:
  - `PlanEditorViewModel`: `[ObservableProperty] bool _retryEnabled; int _retryAttempts; int _retryIntervalMinutes;` each `Touch`ing.
  - `MainViewModel.LoadHistory` passes `EffectiveRun.Apply(log.ReadAll())`.
  - `PlanRunViewModel.SetRetry(RetryState? state)`; retry `BackupJobUpdate`s set `RetryJobState` (`JobState?`) and never
    change `State`, so `IsActive` and the Run button stay about regular runs. `LastRun` is the newest row whose trigger
    is not `Retry`.
  - `RunHistoryRow`: `RetryHintText` (null when none) — pending state → `plan.lastRun.retryPending` (`attempt` = next
    attempt, `attempts`, `time` local `HH:mm`, `open`); no state and `RetryRecovered > 0 && SkippedCount == 0` →
    `plan.lastRun.retryRecovered` (`count`); no state, attempts made and `SkippedCount > 0` →
    `plan.lastRun.retryExhausted` (`count`, `attempts`). Retry lines: `StatusText` uses `history.retryLine`
    (`attempt`, `attempts`, `recovered` = `FilesCopied`, `open` = `SkippedCount`).
  - Plan card: `card.retryWaiting` (job queued), `card.retryRunning` (job running), `card.retryAt` (`time`) while a state
    exists and no retry job is active.
  - `CoverageBuilder.Build`: ignores entries with `Trigger == Retry` when mapping versions (input is the effective log).
  - XAML: switch `CheckBox.Switch` bound to `RetryEnabled`, tooltip `plan.source.retryToolTip`; indented row with two
    `TextBox` (width as other numeric boxes in `PlanView.xaml`, `IsEnabled="{Binding RetryEnabled}"`) inside the
    `plan.source.retryAttempts` sentence; Last run card: one `TextBlock` under the `UniformGrid`, collapsed when
    `RetryHintText` is null.

Copy (en-US / de-DE):

| Key | en-US | de-DE |
|---|---|---|
| `plan.source.retry` | Retry skipped files later | Übersprungene Dateien später erneut versuchen |
| `plan.source.retryToolTip` | Files that were locked or changed while copied are copied again after the run and added to the same version. Other backups go first. | Gesperrte oder beim Kopieren geänderte Dateien werden nach dem Lauf erneut kopiert und in dieselbe Version nachgetragen. Andere Backups haben Vorrang. |
| `plan.source.retryAttempts` | Up to {attempts} attempts, every {minutes} minutes | Bis zu {attempts} Versuche, alle {minutes} Minuten |
| `plan.lastRun.retryPending` | Retry {attempt}/{attempts} at {time} · {open} open | Retry {attempt}/{attempts} um {time} · {open} offen |
| `plan.lastRun.retryRecovered` | {count} files recovered by retry | {count} Dateien per Retry nachgeholt |
| `plan.lastRun.retryExhausted` | {count} still skipped after {attempts} retries | Nach {attempts} Retries noch {count} übersprungen |
| `history.retryLine` | Retry {attempt}/{attempts} · {recovered} recovered · {open} open | Retry {attempt}/{attempts} · {recovered} nachgeholt · {open} offen |
| `card.retryWaiting` | Retry waiting | Retry wartet |
| `card.retryRunning` | Retry running | Retry läuft |
| `card.retryAt` | Retry at {time} | Retry um {time} |
| `shell.status.retryFinished` | Retry of {plan}: {recovered} recovered, {open} open | Retry von {plan}: {recovered} nachgeholt, {open} offen |

Plural forms follow how existing count strings in the locale files are written (check `card.skipped`).
How the XAML splits `plan.source.retryAttempts` around the two text boxes: follow an existing sentence-with-inputs in
`PlanView.xaml` (e.g. the retention rules), or split into `retryAttemptsBefore/Middle/After` keys if none exists.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Retry_settings_round_trip_through_the_editor() // LoadFrom(plan{false,7,45}) → ToPlan() equal; edits make IsDirty
[Fact] public void A_recovered_run_shows_completed_with_zero_skipped_and_a_hint()
    // row of EffectiveRun output: StatusText "Completed", SkippedText "0", RetryHintText "73 files recovered by retry"
[Fact] public void A_pending_retry_shows_when_it_runs_next() // SetRetry(state Attempt 1/3, 04:30, 12 files) → "Retry 2/3 at 04:30 · 12 open"
[Fact] public void Exhausted_retries_show_what_is_still_skipped() // "5 still skipped after 3 retries"
[Fact] public void The_last_run_card_never_shows_a_retry_line() // log [run, retry] → LastRun.Entry.Trigger != Retry
[Fact] public void A_running_retry_does_not_block_the_run_button() // Apply(Running, IsRetry: true) → IsActive false
[Fact] public void A_recovered_version_is_ok_in_the_coverage() // CoverageBuilder → CoverageQuality.Ok
```

- [ ] **Step 2: Run** `dotnet test tests/ReBackup.App.Tests --filter "FullyQualifiedName~Retry|FullyQualifiedName~CoverageBuilderTests"` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `dotnet test ReBackup.sln` — PASS (includes `LocaleFileTests`).
- [ ] **Step 5: Look at it in the app** (`run` skill): the switch, the two inputs greyed out when off, the hint line, in
  both themes and both languages.
- [ ] **Step 6: Commit** — `feat(app): retry settings, effective last run and retry hints`

---

### Task 10: Finish

- [ ] **Step 1:** `dotnet build ReBackup.sln -warnaserror` and `dotnet test ReBackup.sln` — both clean.
- [ ] **Step 2:** README: one paragraph under the plan settings on retrying skipped files (defaults 3 × 5 min).
- [ ] **Step 3:** Commit — `docs: retry skipped files in the README`
- [ ] **Step 4:** `git switch main && git merge --no-ff feature/retry-skipped`
