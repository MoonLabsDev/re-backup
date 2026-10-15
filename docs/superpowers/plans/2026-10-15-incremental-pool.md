# Incremental Pool Plans Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Plans can opt (at creation) into an incremental, content-addressed pool format; retention, GC, restore, open, verify and plan copy work on it without ever rebasing versions.

**Architecture:** New Core folder `src/ReBackup.Core/Pool/` holds the pool primitives (layout, store descriptor + mode guard, target lock, blob writer, collector, verifier, read-only version view, plan copier). `BackupRunner` gets a pool copy path in a new partial file; `Restorer` learns per-file hash verification. The App wires plan settings, the Versions tab (temp open, hidden folder actions, exclusive size, damaged rows), lock/verify actions and a copy-plan dialog.

**Tech Stack:** C# / .NET 9, WPF + CommunityToolkit.Mvvm, `System.IO.Hashing` (`XxHash64`, `XxHash128`), Microsoft.Data.Sqlite, xUnit 2.9 + FluentAssertions 7.

**Spec:** `docs/superpowers/specs/2026-10-15-incremental-pool-design.md`

## Global Constraints

- Branch `feature/incremental-pool` from `main`; merge back with `--no-ff` when done.
- After every task: `dotnet build ReBackup.sln` 0 warnings; `dotnet test ReBackup.sln` green. No existing test weakened; full-mode behaviour unchanged.
- File names on the target, verbatim: `re-store.json`, `re-lock.json`, `re-pool/`, blob path `re-pool/<hex[0..2]>/<hex[2..4]>/<hex>` (lower-case hex).
- `re-store.json` content: `{"format":"pool","hash":"xxh128","version":1,"planId":"<plan id>"}`. `planId` is an addition to the spec: it makes a second plan pointing at the same target fail the mode guard.
- Pool manifests: `FormatVersion = 3`, file hashes `xxh128:` + 32 lower-case hex digits. Full plans keep format 2 / `xxh64:`.
- GC delete batches: 1000 paths. Temp-open folder `%TEMP%\ReBackup\open\`, entries older than 1 day removed on app start. "Restore to temp and open" confirms above 1 GB (1_073_741_824 bytes).
- Texts via locales (en-US + de-DE), Core texts through `CoreTexts` keys next to the existing `core.run.*` keys; `LocaleFileTests` green.
- Never start any app exe from the tools (MSIX sandbox); in-process tests only. Never force-kill a running ReBackup.
- Commits conventional, ending with blank line + `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A file that changes between hashing and uploading** (log file being written) — the blob is never committed under a wrong name; the file is skipped like a locked file and the run completes with warnings → Task 4 `Content_changed_between_reads_is_not_committed`.
2. **Two plans (or two PCs) pointing at the same target** — the second plan aborts before writing; a second PC's run is skipped while the lock is held → Task 2 `Store_of_another_plan_aborts`, Task 3 `Second_acquire_fails_while_held`.
3. **Crash at any step of run, GC or copy** — every complete version afterwards restores byte-exact → Task 6 `Fault_at_every_step_keeps_complete_versions_restorable`, Task 10 resume test.
4. **A damaged manifest among many versions** — GC deletes nothing; that version shows as damaged; others restore → Task 6 `Unreadable_manifest_aborts_collection`, Task 8 `Pool_version_without_manifest_is_damaged`.
5. **Retention deletes the version a just-unchanged file was first written in** — the blob stays because later manifests reference it; restore of the newest version still works → Task 6 `Blob_shared_with_kept_version_survives`.

---

### Task 1: Pool layout, hashes, plan settings

**Files:**
- Create: `src/ReBackup.Core/Pool/PoolLayout.cs`
- Modify: `src/ReBackup.Core/Backup/BackupManifest.cs`, `src/ReBackup.Core/Plans/BackupPlan.cs`, `src/ReBackup.Core/Versions/VersionIndex.cs` (`ParseHash`), `src/ReBackup.Core/Backup/BackupRunner.cs` (`IsReservedRootName`)
- Test: `tests/ReBackup.Core.Tests/Pool/PoolLayoutTests.cs`, `tests/ReBackup.Core.Tests/Versions/VersionIndexTests.cs`, `tests/ReBackup.Core.Tests/Plans/` (existing plan round-trip test file)

**Interfaces:**
- Produces:
  - `static class PoolLayout { const string StoreFile = "re-store.json", LockFile = "re-lock.json", PoolFolder = "re-pool", HashPrefix = "xxh128:"; static string BlobPath(string hash); static string Format(UInt128 hash); static bool TryParseHex(string hash, out string hex); }` — `BlobPath` accepts `xxh128:<32 hex>` and throws `ArgumentException` otherwise.
  - `BackupManifest.PoolFormatVersion = 3`.
  - `enum ChangeDetection { Fast, Safe }`; `BackupPlan.Incremental` (bool, default false), `BackupPlan.ChangeDetection` (default `Fast`).
  - `VersionIndex.ParseHash` also accepts `xxh128:` and folds it to 64 bits (`high ^ low`) — the index only compares and estimates; GC never uses the index.

- [ ] **Step 1: Write the failing tests**
  - `BlobPath_splits_two_levels`: `BlobPath("xxh128:abcdef0123456789abcdef0123456789")` == `"re-pool/ab/cd/abcdef0123456789abcdef0123456789"`.
  - `BlobPath_rejects_xxh64_and_garbage`: `"xxh64:0123456789abcdef"`, `"xxh128:XYZ"`, `""` throw `ArgumentException`.
  - `Format_is_lower_hex_32`: `Format(UInt128.MaxValue)` == `"xxh128:" + new string('f', 32)`.
  - `ParseHash_folds_xxh128`: two `xxh128:` strings equal → equal values; differing in one digit → different; `xxh64:` unchanged.
  - `Plan_round_trips_incremental_settings`: `Incremental = true, ChangeDetection = Safe` survive `Clone()` and `PlanStore` save/load; an old plan JSON without them loads as `false` / `Fast`.
  - `Pool_names_are_reserved_in_source_root`: a source root holding `re-pool/`, `re-store.json`, `re-lock.json` → skipped with `core.skip.reservedName` (extend the existing reserved-name test in `BackupRunnerTests`).
- [ ] **Step 2: Run** `dotnet test tests/ReBackup.Core.Tests --filter "PoolLayout|ParseHash|Plan_round_trips|Pool_names"` → FAIL (types missing).
- [ ] **Step 3: Implement** the members above; `IsReservedRootName` adds the three pool names.
- [ ] **Step 4: Run** the same filter → PASS; full `dotnet test ReBackup.sln` green.
- [ ] **Step 5: Commit** `feat(pool): layout, xxh128 hashes and plan settings`

### Task 2: Store descriptor and mode guard

**Files:**
- Create: `src/ReBackup.Core/Pool/StoreGuard.cs`
- Test: `tests/ReBackup.Core.Tests/Pool/StoreGuardTests.cs`

**Interfaces:**
- Consumes: `PoolLayout`, `VersionCatalog.ListAsync`.
- Produces:
  - `sealed record StoreDescriptor(string Format, string Hash, int Version, string PlanId)`.
  - `enum StoreGuardOutcome { Ok, Created, FullPlanOnPoolTarget, PoolPlanOnFullTarget, OtherPlan, UnsupportedFormat }`.
  - `static class StoreGuard { Task<StoreGuardOutcome> CheckAsync(IStorage target, BackupPlan plan, bool createIfMissing, CancellationToken ct); Task<StoreDescriptor?> ReadAsync(IStorage target, CancellationToken ct); string MessageKey(StoreGuardOutcome outcome); }`.
  - Rules: incremental + store present with same planId, format `pool`, hash `xxh128`, version 1 → `Ok`; incremental + no store + no version folders of this plan + no `re-pool` → create (exclusive, `Durable: true`) when `createIfMissing`, return `Created`; incremental + no store but versions exist → `PoolPlanOnFullTarget`; full plan + store present → `FullPlanOnPoolTarget`; different planId → `OtherPlan`; anything else in the store → `UnsupportedFormat`.

- [ ] **Step 1: Write the failing tests** (on `InMemoryStorage`), one per rule: `Empty_target_gets_store`, `Same_plan_store_is_ok`, `Store_of_another_plan_aborts`, `Full_plan_on_pool_target_aborts`, `Pool_plan_on_target_with_full_versions_aborts`, `Unknown_version_is_unsupported`, `CheckAsync_without_create_writes_nothing`. Each asserts the outcome and, for aborts, that the storage is unchanged.
- [ ] **Step 2: Run** `--filter StoreGuard` → FAIL.
- [ ] **Step 3: Implement** `StoreGuard`; JSON via `JsonDefaults.Options`. Add locale keys `core.run.storeFullOnPool`, `core.run.storePoolOnFull`, `core.run.storeOtherPlan`, `core.run.storeUnsupported` (en + de).
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(pool): store descriptor and mode guard`

### Task 3: Target lock

**Files:**
- Create: `src/ReBackup.Core/Pool/TargetLock.cs`
- Test: `tests/ReBackup.Core.Tests/Pool/TargetLockTests.cs`, `tests/ReBackup.Storage.GoogleDrive.Tests/` (exclusive create test), `tests/ReBackup.Storage.S3.Tests/` (exclusive create test)

**Interfaces:**
- Produces:
  - `sealed record LockInfo(string Host, int Pid, string Operation, DateTime StartedUtc, string Token)`.
  - `sealed class TargetLock : IAsyncDisposable { static Task<TargetLock?> TryAcquireAsync(IStorage target, string host, string operation, TimeProvider time, CancellationToken ct); static Task<LockInfo?> ReadAsync(IStorage target, CancellationToken ct); static Task ForceReleaseAsync(IStorage target, CancellationToken ct); LockInfo Info { get; } }` — `DisposeAsync` deletes `re-lock.json` only if it still holds its own `Token`.
  - Operation names: `"backup"`, `"delete"`, `"verify"`, `"copy"`.
- Acquire = `CreateAsync(LockFile, new CreateOptions(Overwrite: false, Durable: true))`, write JSON with a fresh GUID token, commit; then re-read and compare the token (covers Drive's check-then-create race); `StorageConflictException` or token mismatch → `null`.

- [ ] **Step 1: Write the failing tests**: `Acquire_writes_lock_file`, `Second_acquire_fails_while_held`, `Dispose_releases`, `Dispose_does_not_delete_a_foreign_lock` (ForceRelease + someone else acquires before dispose), `ForceRelease_removes_any_lock`, `ReadAsync_returns_info_or_null`. In the S3 (LocalStack) and Drive (fake API) test projects: `Exclusive_create_conflicts_when_file_exists` asserting `StorageConflictException` on a second `CreateAsync(Overwrite:false)`.
- [ ] **Step 2: Run** `--filter TargetLock|Exclusive_create` → FAIL for TargetLock; the provider tests show whether each provider already conflicts (fix the provider in this task if not).
- [ ] **Step 3: Implement** `TargetLock`. Locale key `core.run.targetLocked` with `{host}`, `{operation}`, `{since}`.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(pool): exclusive target lock`

### Task 4: Pool writer

**Files:**
- Create: `src/ReBackup.Core/Pool/PoolWriter.cs`
- Test: `tests/ReBackup.Core.Tests/Pool/PoolWriterTests.cs`

**Interfaces:**
- Consumes: `PoolLayout`.
- Produces:
  - `enum PutOutcome { Written, AlreadyPresent, ContentChanged }`.
  - `sealed class PoolWriter { PoolWriter(IStorage target); Task LoadKnownAsync(CancellationToken ct); bool Contains(string hash); static Task<(string Hash, long Size)> HashAsync(Stream input, byte[] buffer, Action<int>? onBytes, CancellationToken ct); Task<PutOutcome> PutAsync(Stream input, string expectedHash, byte[] buffer, Action<int>? onBytes, CancellationToken ct); }` — thread-safe `Contains`/add (parallel copy); `LoadKnownAsync` on a missing `re-pool` = empty set.
  - `PutAsync`: hash while writing to `BlobPath(expectedHash)` (`Overwrite:false`); on mismatch dispose without commit → `ContentChanged`; `StorageConflictException` at create or commit → `AlreadyPresent`; then add to known.

- [ ] **Step 1: Write the failing tests**: `HashAsync_matches_XxHash128`, `Put_writes_blob_at_its_path`, `Put_of_known_hash_is_already_present` (second writer instance after `LoadKnownAsync`), `Content_changed_between_reads_is_not_committed` (expected hash of "a", stream "b" → `ContentChanged`, no blob), `LoadKnown_on_empty_target_is_empty`, `Concurrent_puts_of_same_content_leave_one_blob` (8 parallel puts).
- [ ] **Step 2: Run** `--filter PoolWriter` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(pool): content-addressed blob writer`

### Task 5: Incremental backup run

**Files:**
- Create: `src/ReBackup.Core/Backup/BackupRunner.Pool.cs` (make `BackupRunner` `partial`)
- Modify: `src/ReBackup.Core/Backup/BackupRunner.cs` (`RunAsync`, `PrepareAsync` free-space estimate, `CopyAndFinishAsync` dispatch, parallel copy dispatch)
- Test: `tests/ReBackup.Core.Tests/Backup/BackupRunnerPoolTests.cs`

**Interfaces:**
- Consumes: `StoreGuard`, `TargetLock`, `PoolWriter`, `ManifestStream.Read`, `VersionCatalog.ListAsync`.
- Produces: no new public API; incremental runs write pool versions (manifest format 3, only `re-manifest.json` in the version folder, `Directories` always filled, no `EnsureDirectoryAsync` inside the version).
- Flow for `plan.Incremental` (spec 4): acquire lock `"backup"` first (null → `RunStatus.Error` with `core.run.targetLocked`, nothing written) → `StoreGuard.CheckAsync(createIfMissing: true)` (abort → `Error`) → existing leftover cleanup → reserve name → `PoolWriter.LoadKnownAsync` → previous manifest = newest owned version's manifest into `Dictionary<string, ManifestFile>` (`StringComparer.Ordinal` when the target is `CaseSensitive`, else `OrdinalIgnoreCase`) → per file `CopyFileToPoolAsync` → manifest durable → remove pending → retention → GC (Task 6) → release lock in `finally`.
- `CopyFileToPoolAsync(...) -> FileOutcome`: Fast + same path/size/mtime + `Contains(prev.Hash)` → reuse entry without opening; else open, `HashAsync`; unknown → reopen, `PutAsync`; `ContentChanged` → skip with `core.file.locked`. The existing change-while-copied check (`ChangedWhileCopied`) still applies after the second read.
- Free-space estimate for pool plans: bytes of files not matching the previous manifest by path/size/mtime (times `FreeSpaceMargin`).

- [ ] **Step 1: Write the failing tests** (`InMemoryStorage` source and target, `FakeTimeProvider`):
  - `First_run_writes_store_blobs_and_manifest_only` — version folder holds exactly `re-manifest.json`; one blob per distinct content; two identical files → one blob.
  - `Second_run_unchanged_uploads_nothing` — no new `CreateAsync` under `re-pool` (count via `WrappedStorage`).
  - `Fast_mode_does_not_open_unchanged_files` — source `OpenReadAsync` count for unchanged files = 0.
  - `Safe_mode_reads_every_file_but_uploads_nothing_new`.
  - `Mtime_only_change_rehashes_and_records_new_mtime` — no new blob, manifest has the new mtime.
  - `Restored_mtime_with_new_content_is_missed_in_fast_and_caught_in_safe`.
  - `Locked_target_skips_run_without_writing` (pre-acquired lock).
  - `Full_plan_runs_unchanged_on_pool_free_target` — existing runner tests stay green (no lock file ever written for full plans).
  - `Parallel_copy_path_writes_same_result` (S3-like target flag via `TestStorageFactory`).
- [ ] **Step 2: Run** `--filter BackupRunnerPool` → FAIL.
- [ ] **Step 3: Implement** as described; lock release in the outer `finally` of `RunAsync`.
- [ ] **Step 4: Run** → PASS; full suite green.
- [ ] **Step 5: Commit** `feat(backup): incremental runs into the pool`

### Task 6: Garbage collection, retention and fault injection

**Files:**
- Create: `src/ReBackup.Core/Pool/PoolCollector.cs`, `src/ReBackup.Core/Pool/PoolUsage.cs`
- Modify: `src/ReBackup.Core/Backup/BackupRunner.Pool.cs` (call collector after `ApplyRetentionAsync`), `src/ReBackup.Core/Backup/BackupRunner.cs` (`FreeSpaceByRetentionAsync` uses `PoolUsage` for pool plans), `src/ReBackup.Core/Backup/VersionDeleter.cs`
- Test: `tests/ReBackup.Core.Tests/Pool/PoolCollectorTests.cs`, `tests/ReBackup.Core.Tests/Pool/PoolFaultTests.cs`

**Interfaces:**
- Consumes: `VersionCatalog.ListAsync`, `VersionCatalog.ListForeignMarkedAsync`, `ManifestStream.Read`, `TargetLock`.
- Produces:
  - `sealed record CollectResult(int BlobsDeleted, long BytesFreed, string? SkippedReason)`.
  - `static class PoolCollector { Task<CollectResult> CollectAsync(IStorage target, string planId, string planName, CancellationToken ct); }` — caller holds the lock. Skips (`SkippedReason` = locale key) when any folder of the plan still has `re-pending.json`/`re-deleting.json` or a foreign marker exists; aborts when any manifest is unreadable; otherwise deletes unmarked blobs in batches of 1000, then empty `xx/yy` folders.
  - `static class PoolUsage { Task<IReadOnlyDictionary<string, long>> UniqueBytesAsync(IStorage target, IReadOnlyList<VersionInfo> versions, CancellationToken ct); }` — version name → bytes only it references.
  - `VersionDeleter.DeleteAsync` gains `bool pool = false` and `string host`-based lock: for pool plans it acquires `"delete"`, deletes, runs `CollectAsync`, releases; lock held → every requested version gets an error outcome with `core.run.targetLocked`.
- Free space: for pool plans, delete candidates in retention order until summed `UniqueBytes` covers the shortfall, then `CollectAsync`, then re-read free space.

- [ ] **Step 1: Write the failing tests**:
  - `Unreferenced_blobs_are_deleted`, `Blob_shared_with_kept_version_survives`, `Unreadable_manifest_aborts_collection` (nothing deleted), `Pending_marker_skips_collection`, `Foreign_marker_skips_collection`, `Empty_shard_folders_are_removed`, `Batches_of_1000` (2500 orphans → 3 `DeleteAsync` calls on the blobs).
  - `Retention_then_collection_frees_space_in_run` (runner-level).
  - `UniqueBytes_counts_only_exclusive_content`; `FreeSpace_deletes_until_unique_bytes_cover_shortfall`.
  - `Manual_delete_collects_and_releases_lock`; `Manual_delete_refused_while_locked`.
  - `Fault_at_every_step_keeps_complete_versions_restorable`: for N = 1..k, a `FaultyStorage` that throws on the N-th write/delete during a run with retention+GC; afterwards every version listed by `VersionCatalog` restores (Task 7 `Restorer` path) byte-exact; loop until a run completes without hitting the fault.
- [ ] **Step 2: Run** `--filter PoolCollector|PoolFault|UniqueBytes|Manual_delete` → FAIL.
- [ ] **Step 3: Implement.** Locale keys `core.gc.skippedMarkers`, `core.gc.manifestUnreadable` (`{version}`), shown as run warnings.
- [ ] **Step 4: Run** → PASS (the fault test may land after Task 7; if so mark it `[Fact(Skip = "Task 7")]` here and un-skip in Task 7).
- [ ] **Step 5: Commit** `feat(pool): garbage collection and space-aware retention`

### Task 7: Read-only version view and verified restore

**Files:**
- Create: `src/ReBackup.Core/Pool/PoolVersionView.cs`, `src/ReBackup.Core/Versions/VersionViews.cs`
- Modify: `src/ReBackup.Core/Versions/Restorer.cs` (`RestoreFile.Hash`, manifest hashes in `ReadManifestAsync`, verification in `CopyAsync`, `Reason` mapping)
- Test: `tests/ReBackup.Core.Tests/Pool/PoolVersionViewTests.cs`, `tests/ReBackup.Core.Tests/Versions/RestorerTests.cs`

**Interfaces:**
- Produces:
  - `sealed class PoolVersionView : IStorage` — read-only view over a target for one version: `StatAsync`/`ListAsync`/`OpenReadAsync` below `<versionName>/` answer from the manifest (files → blob reads, folders from `Directories` and file parents); `<versionName>/re-manifest.json` and everything outside the version pass through to the target; `CreateAsync`/`DeleteAsync`/`EnsureDirectoryAsync` throw `NotSupportedException`. `Capabilities` = target's minus `Links`.
  - `static class VersionViews { Task<IStorage> OpenAsync(IStorage target, string versionName, CancellationToken ct); }` — `PoolVersionView` when `StoreGuard.ReadAsync` finds a pool store, else `target` itself. This is the spec's `IVersionSource`, expressed as an `IStorage` so `Restorer` and copy code stay unchanged.
  - `RestoreFile` gains `string? Hash = null`; `Restorer.CopyAsync` hashes while copying (xxh64 or xxh128 by prefix) and throws `ContentMismatchException : StorageException` before commit on mismatch; a missing blob surfaces as `StorageNotFoundException` → failure for that file only.
- Locale key `core.restore.contentMismatch`.

- [ ] **Step 1: Write the failing tests**:
  - View: `Lists_version_tree_from_manifest`, `Opens_file_from_blob`, `Stat_of_missing_path_is_null`, `Writes_are_not_supported`, `Manifest_passes_through`.
  - Restorer: `Pool_version_restores_byte_exact` (via `VersionViews.OpenAsync`), `Corrupt_blob_fails_only_that_file`, `Missing_blob_fails_only_that_file`, `Full_version_with_tampered_file_fails_verification`, `Mismatch_leaves_no_file_at_destination`.
  - Un-skip Task 6's fault test.
- [ ] **Step 2: Run** `--filter PoolVersionView|Restorer` → new tests FAIL, existing pass.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → PASS; full suite green.
- [ ] **Step 5: Commit** `feat(restore): pool version view and hash-verified restore`

### Task 8: Version index: damaged versions, unique bytes, cleanup

**Files:**
- Modify: `src/ReBackup.Core/Versions/VersionIndex.cs` (`Sync`), `src/ReBackup.Core/Versions/VersionIndex.Queries.cs`, `src/ReBackup.Core/Versions/IndexModels.cs`, `src/ReBackup.Core/Versions/VersionIndexSet.cs`
- Test: `tests/ReBackup.Core.Tests/Versions/VersionIndexTests.cs`, `VersionIndexQueryTests.cs`

**Interfaces:**
- Produces:
  - `IndexOrigin.Damaged` (version row with 0 files).
  - `VersionIndex.Sync(..., bool scanFallback = true)` — pool plans pass `false`: an unreadable/missing manifest → `Damaged` instead of a storage scan.
  - `long VersionIndex.UniqueBytes(long versionId)` — sum of `size` of files in this version whose `hash` does not occur in any other version (NULL hashes count as unique).
  - `VersionIndexSet.Delete(string planId)` (closes and deletes `<planId>.db` plus `-wal`/`-shm`), `int VersionIndexSet.PruneOrphans(IReadOnlySet<string> planIds)`.

- [ ] **Step 1: Write the failing tests**: `Pool_version_without_manifest_is_damaged`, `Pool_manifest_imports_like_full`, `Compare_works_on_xxh128`, `UniqueBytes_excludes_shared_content`, `Delete_removes_database_files`, `PruneOrphans_keeps_known_plans`.
- [ ] **Step 2: Run** `--filter VersionIndex` → new tests FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(index): damaged pool versions, unique bytes, index cleanup`

### Task 9: Verify backup

**Files:**
- Create: `src/ReBackup.Core/Pool/PoolVerifier.cs`
- Test: `tests/ReBackup.Core.Tests/Pool/PoolVerifierTests.cs`

**Interfaces:**
- Produces:
  - `sealed record PoolProblem(string Version, string Path, string Hash, PoolProblemKind Kind)`; `enum PoolProblemKind { Missing, Corrupt, ManifestUnreadable }`.
  - `readonly record struct VerifyProgress(int Done, int Total, string Current)`.
  - `static class PoolVerifier { Task<IReadOnlyList<PoolProblem>> VerifyAsync(IStorage target, string planId, string planName, bool thorough, IProgress<VerifyProgress>? progress, CancellationToken ct); }` — caller holds lock `"verify"`. Quick = one pool listing vs. all manifest hashes; thorough additionally reads and re-hashes every referenced blob once.

- [ ] **Step 1: Write the failing tests**: `Healthy_pool_has_no_problems`, `Missing_blob_reported_for_every_version_using_it`, `Corrupt_blob_found_only_when_thorough`, `Unreadable_manifest_reported`, `Verify_never_writes` (`WrappedStorage` counts zero creates/deletes).
- [ ] **Step 2: Run** `--filter PoolVerifier` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(pool): verify backup`

### Task 10: Plan copy (switch mode)

**Files:**
- Create: `src/ReBackup.Core/Pool/PlanCopier.cs`
- Modify: `src/ReBackup.Core/Plans/PlanValidator.cs` (expose the source/target overlap check as `public static bool Overlaps(StorageLocation a, StorageLocation b, ...)` with whatever lookup parameters it needs today)
- Test: `tests/ReBackup.Core.Tests/Pool/PlanCopierTests.cs`

**Interfaces:**
- Consumes: `VersionViews.OpenAsync`, `PoolWriter`, `StoreGuard`, `TargetLock`, `VersionCatalog`, `PlanValidator.Overlaps`.
- Produces:
  - `enum CopyRefusal { None, SameOrNestedTarget, TargetNotEmpty, SourceLocked }`.
  - `sealed record PlanCopyResult(int VersionsCopied, int VersionsSkipped, IReadOnlyList<string> Failures, bool Canceled)`.
  - `readonly record struct PlanCopyProgress(int Version, int VersionCount, string VersionName, long BytesDone, long BytesTotal)`.
  - `sealed class PlanCopier { PlanCopier(IStorageFactory storages, string host, TimeProvider time); Task<CopyRefusal> CheckAsync(BackupPlan source, StorageLocation newTarget, CancellationToken ct); Task<PlanCopyResult> CopyAsync(BackupPlan source, BackupPlan copy, IProgress<PlanCopyProgress>? progress, CancellationToken ct); static BackupPlan MakeCopy(BackupPlan source, string name, StorageLocation target, bool incremental); }`.
  - `MakeCopy`: new id, all settings and retention cloned, `Enabled = false`.
  - `CopyAsync`: holds source lock `"copy"` (and target lock when the copy is incremental) for the whole job; `StoreGuard` on target with `createIfMissing`; versions oldest → newest; complete versions on the target (same folder name, manifest, no markers) are skipped; per version: pending marker → read each file through the view, verify against the source manifest hash, write in target format (pool: `PoolWriter.PutAsync` with xxh128 computed on a first pass; full: plain file + xxh64 while streaming, mtime set where supported) → manifest with original folder name and `CreatedUtc`, format of the target mode → remove marker. Never writes to the source.

- [ ] **Step 1: Write the failing tests**: `Refuses_same_target`, `Refuses_nested_target_both_ways`, `Refuses_non_empty_target`, `MakeCopy_is_disabled_with_same_settings`, `Full_to_pool_keeps_names_times_and_content`, `Pool_to_full_keeps_names_times_and_content`, `Resume_skips_complete_versions` (cancel after version 2 of 4, rerun → 2 skipped, 2 copied), `Corrupt_source_file_is_reported_and_not_written`, `Source_is_never_written` (`WrappedStorage` on source: zero creates/deletes besides `re-lock.json`).
- [ ] **Step 2: Run** `--filter PlanCopier` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(pool): copy a plan to another target and switch mode`

### Task 11: App — plan settings

**Files:**
- Modify: `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`, `src/ReBackup.App/Views/PlanView.xaml`, `src/ReBackup.App/Locales/en-US.json`, `de-DE.json`
- Test: `tests/ReBackup.App.Tests/` (plan editor view-model tests)

**Interfaces:**
- Produces: `PlanEditorViewModel.Incremental` (bool, editable only while `IsNew`; `CanEditIncremental => IsNew`), `PlanEditorViewModel.ChangeDetection` (always editable, shown only when `Incremental`). Both saved into `BackupPlan`.

- [ ] **Step 1: Write the failing tests**: `Incremental_is_editable_only_for_new_plans`, `Saved_plan_keeps_incremental_after_reload`, `ChangeDetection_round_trips`, `ChangeDetection_hidden_for_full_plans`.
- [ ] **Step 2: Run** `dotnet test tests/ReBackup.App.Tests --filter PlanEditor` → FAIL.
- [ ] **Step 3: Implement**: Plan tab gets a "Storage format" row (Full copies / Incremental, with a one-line hint "Cannot be changed later — use Copy plan…") and a "Compare files" choice (Fast / Safe) under it.
- [ ] **Step 4: Run** → PASS; `LocaleFileTests` green.
- [ ] **Step 5: Commit** `feat(app): incremental and change-detection settings`

### Task 12: App — Versions tab for pool plans

**Files:**
- Create: `src/ReBackup.App/Services/TempOpener.cs`
- Modify: `src/ReBackup.App/ViewModels/VersionsViewModel.cs`, `VersionsViewModel.Restore.cs`, `VersionsViewModel.Tree.cs`, `VersionsViewModel.Delete.cs`, `src/ReBackup.App/Views/VersionsView.xaml`, `src/ReBackup.App/App.xaml.cs`, locales
- Test: `tests/ReBackup.App.Tests/` (versions view-model tests)

**Interfaces:**
- Consumes: `VersionViews.OpenAsync`, `Restorer`, `VersionIndex.UniqueBytes`, `IndexOrigin.Damaged`, `VersionDeleter.DeleteAsync(pool: true)`, `VersionIndexSet.Delete/PruneOrphans`.
- Produces:
  - `interface ITempOpener { Task<bool> OpenFileAsync(IStorage versions, string versionName, string relativePath, CancellationToken ct); Task<bool> OpenFolderAsync(IStorage versions, string versionName, string relativePath, CancellationToken ct); void CleanUp(TimeSpan olderThan); }` — restores via `Restorer` (`ToFolder`, `Overwrite`) into `%TEMP%\ReBackup\open\<versionName>\`, then shell-opens the file / Explorer on the folder; executable types refused as today.
  - Pool plans: "Show in Explorer" and "Open version folder" hidden; "Open" uses `ITempOpener.OpenFileAsync`; new "Restore to temp and open" on folders/versions, confirming above 1 GB; full plans on non-fs targets also use `OpenFileAsync`.
  - Restore dialogs pass `VersionViews.OpenAsync(target, version)` as `versions`.
  - Version list: "Exclusive size" column (pool plans), damaged rows with a warning icon and tooltip.
  - App start: `ITempOpener.CleanUp(TimeSpan.FromDays(1))`; `VersionIndexSet.PruneOrphans(plan ids)`; deleting a plan calls `VersionIndexSet.Delete(planId)`.

- [ ] **Step 1: Write the failing tests**: `Pool_plan_hides_explorer_actions`, `Open_on_pool_plan_uses_temp_opener`, `Open_on_full_fs_plan_opens_in_place`, `Folder_above_1GB_asks_before_temp_restore`, `Damaged_version_row_is_flagged`, `Exclusive_size_shown_for_pool_plans`, `Deleting_plan_deletes_its_index`; `TempOpenerTests`: `Restores_and_verifies_before_opening` (fake process starter), `CleanUp_removes_only_old_entries`.
- [ ] **Step 2: Run** `--filter Versions|TempOpener` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → PASS; full suite green.
- [ ] **Step 5: Commit** `feat(app): versions tab for incremental plans`

### Task 13: App — lock status, verify, copy plan

**Files:**
- Create: `src/ReBackup.App/ViewModels/CopyPlanViewModel.cs`, `src/ReBackup.App/Views/CopyPlanDialog.xaml(.cs)`, `src/ReBackup.App/ViewModels/VerifyViewModel.cs`
- Modify: `src/ReBackup.App/ViewModels/MainViewModel.cs` (plan context menu: "Copy plan…", "Verify backup…", "Release lock…"), `PlanEditorViewModel.cs` (lock banner), locales
- Test: `tests/ReBackup.App.Tests/`

**Interfaces:**
- Consumes: `TargetLock.ReadAsync/ForceReleaseAsync`, `PoolVerifier.VerifyAsync`, `PlanCopier`.
- Produces: plan banner "Locked by {host} ({operation}) since {time}" with "Release lock…" (confirmation dialog naming host and time); "Verify backup…" (Quick / Thorough, progress, problem list); "Copy plan…" dialog (name, target picker reused from the Plan tab, Full / Incremental, refusal message from `CopyRefusal`, progress, summary; the new plan appears disabled in the plan list).

- [ ] **Step 1: Write the failing tests**: `Lock_banner_shows_foreign_lock`, `Release_requires_confirmation`, `Verify_lists_problems`, `Copy_dialog_refuses_same_target`, `Copy_creates_disabled_plan_and_runs_copier`, `Copy_dialog_cancel_stops_copier`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → PASS; full suite green; `dotnet build ReBackup.sln` 0 warnings.
- [ ] **Step 5: Commit** `feat(app): lock status, verify backup and copy plan`

### Task 14: End-to-end on the file system and docs

**Files:**
- Test: `tests/ReBackup.Core.Tests/Integration/PoolEndToEndTests.cs`
- Modify: `README.md` (storage formats section: full vs incremental, Fast/Safe, copy plan, no Explorer browsing for pool versions)

- [ ] **Step 1: Write the test** `Pool_plan_on_file_system_round_trip` (`TempDir` source and target): 3 runs with changes between them, retention removing the middle version, GC, verify thorough = no problems, restore newest and oldest byte-exact, copy to a full target and restore from it byte-exact.
- [ ] **Step 2: Run** `--filter PoolEndToEnd` → PASS (all parts exist; a failure here is a bug in an earlier task — fix there).
- [ ] **Step 3: Update README.**
- [ ] **Step 4: Run** full `dotnet test ReBackup.sln` → green.
- [ ] **Step 5: Commit** `test(pool): end-to-end on the file system; docs: incremental plans`
