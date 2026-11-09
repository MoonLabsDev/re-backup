# Cleanup Tab Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Cleanup tab that applies a plan's current ignore rules to checked backups: a scan shows what would be removed and freed, a run shortens the manifests and deletes the files (full plans) or collects the pool (incremental plans).

**Architecture:** Core gets three units in `src/ReBackup.Core/Cleanup/`: a pure `CleanupScanner` over new `VersionIndex` queries, a `CleanupRunner` that rewrites manifests with the stamp guard and deletes, and a `CleanupResumer` for the `re-pruning.json` marker. The App runs the cleanup through `VersionsViewModel` as a deletion-like job (`IsDeleting`), so every existing gate (runs deferred, copy/delete/restore blocked, lock release, exit question) applies unchanged. A new `CleanupViewModel` + `CleanupView` holds the grid, scan and by-rule list.

**Tech Stack:** .NET 9, WPF, CommunityToolkit.Mvvm, SQLite (Microsoft.Data.Sqlite), xUnit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-11-08-cleanup-design.md`

## Global Constraints

- Work on branch `feature/cleanup-tab`; when finished merge into `main` with `--no-ff`.
- Every user-visible text goes through `src/ReBackup.App/Localization/en-US.json` and `de-DE.json` (keys under `cleanup.*`), formatted with `Loc.T` / `Loc.F` / `LocText`, sizes with `Formats.Bytes`.
- Marker file name: exactly `re-pruning.json`.
- Manifest writes: `CreateOptions(Overwrite: true, Durable: true, ExpectedStamp: <stamp read before the manifest>)`; a `StorageConflictException` is never retried or overwritten.
- Only versions with `VersionOwnership.Owned` and an index entry whose `Origin != IndexOrigin.Damaged` are touched.
- Rules: `IgnoreMatcher.ForPlan(savedPlan.Ignore, globalDefaults, sourceIndex.IgnoreFiles)`; unsaved plan edits are never used.
- Core tests run in place (`dotnet test tests/ReBackup.Core.Tests`; `RepoPaths` needs the repo). App tests run with `--artifacts-path <scratchpad>/artifacts` when ReBackup is running (it locks `bin/Debug`).
- Never force-kill ReBackup; ask the user to quit it via the tray before a manual test.
- Commit messages without attribution lines.

## Review Focus

- **Case-only path differences** (`Target/` in the manifest, rule `target/`, a case-insensitive target): the pruner must match as `IgnoreMatcher` does and delete the file the manifest names; tested in Task 3.
- **A version whose rules remove everything** (every file ignored): the manifest stays valid with `FileCount = 0`, the version is still listed and restorable as empty; tested in Task 4.
- **The newest version is cleaned while a backup is queued**: the run is deferred until the cleanup ends (via `IsDeletingVersions`); tested in Task 8.
- **Scan while the source scan is still running or the source is unreachable**: Scan shows "Scanning source…" or the source error and computes nothing; tested in Task 9.
- **Pool content shared by two checked versions where only one loses it**: not counted as freed and not collected; tested in Tasks 2 and 5.

---

### Task 1: Index queries for the cleanup

**Files:**
- Modify: `src/ReBackup.Core/Versions/VersionIndex.Queries.cs`
- Modify: `src/ReBackup.Core/Versions/QueryModels.cs`
- Test: `tests/ReBackup.Core.Tests/Versions/VersionIndexCleanupQueriesTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record IndexEntry(string Path, bool IsDirectory, long Size, int Files, long? Hash);` in `QueryModels.cs` (folder: `Size`/`Files` from the `dirs` totals, `Hash` null; file: `Files` = 1).
  - `public IReadOnlyList<IndexEntry> EntriesOf(long versionId)`: every folder (from `dirs`, without the root `""`) and file (from `files`) of the version, ordered by `path` ordinal-ignore-case (a parent always before its children).
  - `public string? StampOf(long versionId)`: the `versions.stamp` column; null for an unknown id.
  - `public IReadOnlySet<long> HashesOf(IReadOnlyCollection<long> versionIds)`: distinct non-null `files.hash` of those versions (empty set for none).

- [ ] **Step 1: Write the failing tests**

Build an index with `VersionIndex.Open(<temp db>)` and `Add(new VersionInfo(name, name, time, VersionOwnership.Owned, null, null), manifest, new InMemoryStorage())` from two manifests (files `src/a.cs` 10 B hash `xxh64:0000000000000001`, `target/x.dll` 100 B hash `…02`, `target/sub/y.dll` 50 B hash `…03`; second version only `target/x.dll`).

```csharp
[Fact] public void EntriesOf_lists_folders_with_totals_and_files_parents_first()
// entries.Select(e => e.Path) equals "src", "src/a.cs", "target", "target/sub", "target/sub/y.dll", "target/x.dll" (the root "" is left out)
// entries.Single(e => e.Path == "target") is (IsDirectory: true, Size: 150, Files: 2, Hash: null)
// entries.Single(e => e.Path == "target/x.dll").Hash == VersionIndex.ParseHash("xxh64:0000000000000002")

[Fact] public void StampOf_returns_the_recorded_stamp_and_null_for_unknown_ids()
// StampOf(id).Should().StartWith("m:"); StampOf(9999).Should().BeNull()

[Fact] public void HashesOf_returns_the_distinct_hashes_of_the_given_versions()
// HashesOf([second]).Should().BeEquivalentTo([ParseHash("xxh64:…02")]); HashesOf([]).Should().BeEmpty()
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter FullyQualifiedName~VersionIndexCleanupQueriesTests`
Expected: build error, `EntriesOf` / `StampOf` / `HashesOf` not defined.

- [ ] **Step 3: Implement the three queries in `VersionIndex.Queries.cs`**

`EntriesOf`: one `UNION ALL` of `dirs JOIN paths` and `files JOIN paths` for `$v`, `ORDER BY p.path COLLATE NOCASE`. `HashesOf`: the `json_each($ids)` pattern of `PoolBytesOf`.

- [ ] **Step 4: Run to verify they pass** (same command, expected PASS)

- [ ] **Step 5: Commit** — `git commit -m "feat(core): index queries for the cleanup scan"`

---

### Task 2: CleanupScanner

**Files:**
- Create: `src/ReBackup.Core/Cleanup/CleanupScan.cs` (records)
- Create: `src/ReBackup.Core/Cleanup/CleanupScanner.cs`
- Test: `tests/ReBackup.Core.Tests/Cleanup/CleanupScannerTests.cs`

**Interfaces:**
- Consumes: Task 1 `EntriesOf`, `StampOf`, `HashesOf`; `VersionIndex.Versions()`.
- Produces:
  - `public sealed record CleanupVersionScan(long VersionId, string Name, string Stamp, int Files, long Bytes);`
  - `public sealed record CleanupRuleHit(string Rule, string Origin, int Files, long Bytes, string LargestPath, long LargestBytes);`
  - `public sealed record CleanupScan(IReadOnlyList<CleanupVersionScan> Versions, IReadOnlyList<CleanupRuleHit> ByRule, long? PoolBytesFreed)` with `public int Files => …; public long Bytes => …` (sums).
  - `public static CleanupScan CleanupScanner.Scan(VersionIndex index, IReadOnlyCollection<long> versionIds, IgnoreMatcher matcher, bool pool, CancellationToken ct = default)`

Algorithm (per version, entries from `EntriesOf`):
- Keep `Dictionary<string, IgnoreResult> folders` (ignore case). For each entry, the parent's result is `folders[parent]`, or `matcher.Match(parentPath, true)` when the parent is missing, or `IgnoreResult.NotMatched` at the root.
- Parent ignored → the entry lies inside a counted folder: it adds nothing to counts; a folder records the parent result; a file adds its hash to `removed` (pool only).
- Otherwise `result = matcher.MatchBelow(parentResult, path, isDir)`. Ignored folder → counts `Files`/`Size` from its totals once and is attributed to `(result.Pattern.Text, result.Pattern.Origin)`. Ignored file → counts 1 / `Size`, same attribution. A surviving file adds its hash to `kept` (pool only).
- `LargestPath` / `LargestBytes`: the largest single hit (folder or file) of that rule over all versions.
- Pool: `freed = Σ max size per hash h in removed where h ∉ kept and h ∉ HashesOf(<plan versions not in versionIds>)`; removed files without a hash count their size. Full plans: `PoolBytesFreed = null`.
- `ByRule` sorted by `Bytes` descending; versions in `versionIds` order; `ct` checked per entry block.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void An_ignored_folder_counts_from_its_totals()
// rule "target/" → version scan (Files: 2, Bytes: 150); ByRule single ("target/", IgnoreOrigins.Plan, 2, 150, "target", 150)
[Fact] public void A_file_rule_counts_each_file()
// rule "*.dll" → Files 2, Bytes 150, LargestPath "target/x.dll"
[Fact] public void A_nested_backupignore_applies_below_its_folder()
// NestedIgnoreFile("src", ["*.cs"]) with HonorNestedFiles → removes "src/a.cs", Origin == IgnoreOrigins.ForNestedFile("src")
[Fact] public void Global_defaults_apply_only_when_the_plan_uses_them()
// UseGlobalDefaults false → nothing from a global "target/"
[Fact] public void Pool_estimate_skips_content_an_unchecked_version_still_uses()
// pool: check only the first version, rule "target/" → PoolBytesFreed == 50 (y.dll), x.dll is still in the second
[Fact] public void Pool_estimate_skips_content_a_checked_version_keeps()
// pool: same hash at "target/x.dll" and "lib/x.dll" in one checked version → not freed
[Fact] public void Duplicates_inside_a_version_count_once_in_the_pool()
// two removed files with the same hash, 100 B each → PoolBytesFreed == 100
[Fact] public void Full_plans_have_no_pool_estimate() // PoolBytesFreed null
```

- [ ] **Step 2: Run to verify they fail** — `dotnet test tests/ReBackup.Core.Tests --filter FullyQualifiedName~CleanupScannerTests` → build error.

- [ ] **Step 3: Implement `CleanupScanner.Scan`** as described.

- [ ] **Step 4: Run to verify they pass.**

- [ ] **Step 5: Commit** — `feat(core): cleanup scan over the version index`

---

### Task 3: Manifest pruning and the shared manifest writer

**Files:**
- Create: `src/ReBackup.Core/Cleanup/ManifestPruner.cs`
- Create: `src/ReBackup.Core/Backup/ManifestWriter.cs`
- Modify: `src/ReBackup.Core/Backup/BackupRunner.Retry.cs` (use `ManifestWriter.ReplaceAsync`, delete `WriteRetriedManifestAsync`)
- Modify: `src/ReBackup.Core/Backup/VersionName.cs` (`VersionMarkerNames.Pruning = "re-pruning.json"`)
- Test: `tests/ReBackup.Core.Tests/Cleanup/ManifestPrunerTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record PruneResult(IReadOnlyList<string> RemovedFiles, IReadOnlyList<string> RemovedFolders);` (folders deepest first)
  - `public static PruneResult ManifestPruner.Prune(BackupManifest manifest, IgnoreMatcher matcher)`: removes ignored files from `Files` (decided with `matcher.Match(path, false)`), removes ignored folders from `Directories` (`Match(dir, true)`), sets `FileCount`/`TotalBytes`. `RemovedFolders` = ignored folders that appear in `Directories` or as the parent chain of a removed file, outermost ignored folder and its sub-folders.
  - `internal static Task ManifestWriter.ReplaceAsync(IStorage target, string manifestPath, BackupManifest manifest, string? expectedStamp, CancellationToken ct)` — the body of today's `WriteRetriedManifestAsync`, moved unchanged.
  - `VersionMarkerNames.Pruning`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Removes_ignored_files_and_folders_and_recomputes_the_header()
// files src/a.cs(10), target/x.dll(100), target/sub/y.dll(50); Directories ["src","target","target/sub","empty"]; rule "target/"
// → Files paths ["src/a.cs"], FileCount 1, TotalBytes 10, Directories ["src","empty"]
// → RemovedFiles ["target/x.dll","target/sub/y.dll"] (any order), RemovedFolders ["target/sub","target"]
[Fact] public void A_folder_that_only_loses_some_files_stays_listed()
// rule "*.log" removes "logs/a.log", "logs/keep.txt" stays → Directories still has "logs", RemovedFolders empty
[Fact] public void Case_differences_match_as_the_matcher_matches()
// manifest "Target/x.dll", rule "target/" → removed iff IgnoreMatcher.Match("Target/x.dll", false).IsIgnored (assert equality with that)
[Fact] public void Nothing_ignored_leaves_the_manifest_unchanged() // RemovedFiles empty, Files reference-equal count
```

Existing `BackupRunnerRetryTests` must still pass after the move.

- [ ] **Step 2: Run to verify they fail** — `--filter "FullyQualifiedName~ManifestPrunerTests"` → build error.

- [ ] **Step 3: Implement `ManifestPruner`, add `ManifestWriter`, switch the retry over.**

- [ ] **Step 4: Run** `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~ManifestPrunerTests|FullyQualifiedName~BackupRunnerRetryTests"` → PASS.

- [ ] **Step 5: Commit** — `feat(core): prune a manifest by the ignore rules`

---

### Task 4: CleanupRunner for full plans (with the pruning marker)

**Files:**
- Create: `src/ReBackup.Core/Cleanup/PruningMarker.cs`
- Create: `src/ReBackup.Core/Cleanup/CleanupRunner.cs`
- Create: `src/ReBackup.Core/Cleanup/CleanupResult.cs`
- Test: `tests/ReBackup.Core.Tests/Cleanup/CleanupRunnerFullTests.cs`

**Interfaces:**
- Consumes: Task 3 `ManifestPruner.Prune`, `ManifestWriter.ReplaceAsync`, `VersionMarkerNames.Pruning`; `VersionCatalog.ListAsync`, `StoreGuard.CheckAsync(target, plan, createIfMissing: false, ct)`, `VersionIndex.StampOfAsync`, `IVersionIndexSink.Add`.
- Produces:
  - `public sealed record PruningInfo(int FormatVersion, string PlanId, string Host, DateTime StartedUtc, IReadOnlyList<string> Files, IReadOnlyList<string> Folders);`
  - `public static class PruningMarker { Task WriteAsync(IStorage target, string versionPath, PruningInfo info, CancellationToken ct); Task<PruningInfo?> TryReadAsync(IStorage target, string versionPath, CancellationToken ct); Task RemoveAsync(IStorage target, string versionPath, CancellationToken ct); }` (JSON via `JsonDefaults.Options`, durable write)
  - `public sealed record CleanupTarget(string Name, string Stamp);` (stamp as the index records it, `"m:…"`)
  - `public enum CleanupOutcome { Cleaned, NothingToDo, Changed, Gone, Failed }`
  - `public sealed record CleanupVersionResult(string Name, CleanupOutcome Outcome, int FilesRemoved, long BytesRemoved, string? Error);`
  - `public sealed record CleanupResult(IReadOnlyList<CleanupVersionResult> Versions, long BytesFreed, bool Canceled)` with `FilesRemoved => sum`.
  - `public readonly record struct CleanupProgress(int Current, int Total, string VersionName, CleanupPhase Phase);` `public enum CleanupPhase { Rewriting, Deleting, CollectingPool }`
  - `public static Task<CleanupResult> CleanupRunner.RunAsync(IStorage target, BackupPlan plan, IgnoreMatcher matcher, IReadOnlyList<CleanupTarget> versions, IVersionIndexSink? index, IProgress<CleanupProgress>? progress, CancellationToken ct, string? machineName = null)`

Behaviour (spec §5.2): guard + catalog first (a guard failure → every version `Failed` with `CoreTexts.English(StoreGuard.MessageKey(guard))`, nothing written); versions oldest first by catalog order; per version: not owned/not listed → `Gone`; `StampOfAsync != target.Stamp` → `Changed`; read manifest + its `StatAsync` stamp; `Prune`; nothing removed → `NothingToDo`; write marker; `ReplaceAsync` (conflict → remove marker, `Changed`); delete `RemovedFiles` in batches of `VersionRemover.BatchSize` (missing files fine), then each `RemovedFolders` entry if empty; remove marker; `index?.Add(plan.Id, target, versionInfo, manifest)`; `BytesRemoved` = sum of removed manifest sizes; `BytesFreed` (full) = sum of `BytesRemoved`. `ct` is checked only before a version starts; after the marker is written the version finishes with `CancellationToken.None`; remaining versions are left out and `Canceled = true`. Any other exception for one version → `Failed` with the message, the run goes on.

- [ ] **Step 1: Write the failing tests** (file-system target via `TempDir` + `VersionBuilder.Write`, `FaultyStorage` for faults)

```csharp
[Fact] public async Task Shortens_the_manifest_then_deletes_the_files_and_ignored_folders()
// manifest Files == ["src/a.cs"], FileCount 1; target/ folder gone on disk; re-pruning.json gone; result Cleaned, FilesRemoved 2
[Fact] public async Task Hands_the_cleaned_version_to_the_index() // fake sink got (plan.Id, name, manifest with 1 file)
[Fact] public async Task A_version_changed_since_the_scan_is_skipped() // wrong stamp → Changed, files untouched
[Fact] public async Task A_conflicting_manifest_write_deletes_nothing() // FaultyStorage throws StorageConflictException on create of re-manifest.json → Changed, no marker, files there
[Fact] public async Task A_version_left_empty_stays_valid() // rule "*" → FileCount 0, VersionCatalog lists it Owned
[Fact] public async Task Cancel_finishes_the_current_version_and_stops() // cancel from progress on version 1 → version 1 Cleaned, version 2 absent, Canceled true
[Fact] public async Task A_wrong_store_changes_nothing() // StoreGuard mismatch (pool store file in a full target) → all Failed
```

- [ ] **Step 2: Run to verify they fail** — `--filter FullyQualifiedName~CleanupRunnerFullTests`.

- [ ] **Step 3: Implement `PruningMarker` and `CleanupRunner.RunAsync` (full-plan path).**

- [ ] **Step 4: Run to verify they pass.**

- [ ] **Step 5: Commit** — `feat(core): clean up full-plan versions by the ignore rules`

---

### Task 5: CleanupRunner for incremental plans

**Files:**
- Modify: `src/ReBackup.Core/Cleanup/CleanupRunner.cs`
- Test: `tests/ReBackup.Core.Tests/Cleanup/CleanupRunnerPoolTests.cs`

**Interfaces:**
- Consumes: Task 4 types; `TargetLock.TryAcquireAsync(target, host, "cleanup", TimeProvider.System, ct)`, `TargetLock.LockedMessageAsync`, `VersionDeleter.ReleaseAsync`, `PoolCollector.CollectAsync`.
- Produces: same `RunAsync`; for `plan.Incremental` it takes the lock first (held elsewhere → every version `Failed` with the locked text), writes no marker and deletes no version files, and after the last version, when at least one was `Cleaned`, runs `PoolCollector.CollectAsync` reporting `CleanupPhase.CollectingPool`; `BytesFreed = collect.BytesFreed` (0 when skipped; a skipped reason or collect error is traced only). Lock released in `finally`.

- [ ] **Step 1: Write the failing tests** (`InMemoryStorage` + `PoolVersions.Write` / `AddBlob` / `Blobs`)

```csharp
[Fact] public async Task Rewrites_the_manifests_and_collects_only_unreferenced_blobs()
// v1 {a, target/x}, v2 {target/x}; clean v1 and v2 with "target/" → blob of x gone, blob of a stays, BytesFreed == size of x
[Fact] public async Task A_blob_an_unchecked_version_uses_stays()
// clean only v1 → blob of x stays, BytesFreed 0
[Fact] public async Task A_target_locked_by_another_holder_changes_nothing() // pre-acquire lock as "other-host" → all Failed, manifests unchanged
[Fact] public async Task No_rewritten_manifest_means_no_collect() // nothing ignored → collect not run (unreferenced test blob stays)
```

- [ ] **Step 2: Run to verify they fail** — `--filter FullyQualifiedName~CleanupRunnerPoolTests`.
- [ ] **Step 3: Implement the incremental path.**
- [ ] **Step 4: Run** `--filter FullyQualifiedName~Cleanup` → all PASS.
- [ ] **Step 5: Commit** — `feat(core): clean up incremental versions and collect the pool`

---

### Task 6: Resume an interrupted cleanup

**Files:**
- Create: `src/ReBackup.Core/Cleanup/CleanupResumer.cs`
- Modify: `src/ReBackup.App/ViewModels/VersionsViewModel.Delete.cs` (`ResumeDeletionAsync`)
- Test: `tests/ReBackup.Core.Tests/Cleanup/CleanupResumerTests.cs`, add cases to `tests/ReBackup.Core.Tests/Backup/LeftoverCleanerTests.cs` and `tests/ReBackup.Core.Tests/Pool/PoolCollectorTests.cs`

**Interfaces:**
- Consumes: Task 4 `PruningMarker`, `PruningInfo`.
- Produces:
  - `public static Task<IReadOnlyList<string>> CleanupResumer.FindAsync(IStorage target, string planId, string host, CancellationToken ct)`: root version folders (same name filter as `DeletionResumer.FindAsync`) with a `re-pruning.json` whose `PlanId == planId` and `Host == host`.
  - `public static Task<int> CleanupResumer.ResumeAsync(IStorage target, IReadOnlyList<string> folders, CancellationToken ct)`: per folder reads the marker and the manifest; deletes the marker's `Files` the manifest no longer lists (case per target capabilities), then its `Folders` if empty, then the marker; returns the number of files deleted. No manifest readable → remove nothing but the marker is kept (trace warning).
- `ResumeDeletionAsync` also calls `CleanupResumer.FindAsync`, and when it finds folders runs `ResumeAsync` inside the same `IsResuming` / `RunAsJobAsync` block, then `_ = IndexAfterDeleteAsync()`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Finishes_the_deletion_after_the_manifest_was_rewritten()
// rewritten manifest without target/x.dll, file still there, marker lists it → file deleted, folder "target" deleted, marker gone
[Fact] public async Task A_marker_without_a_rewritten_manifest_deletes_nothing()
// manifest still lists target/x.dll → file stays, marker gone
[Fact] public async Task Markers_of_other_computers_are_left_alone() // Host "OTHER" → FindAsync empty
// LeftoverCleanerTests:
[Fact] public async Task A_folder_with_a_pruning_marker_is_no_leftover() // CleanAsync leaves folder & files, no warning
// PoolCollectorTests:
[Fact] public async Task A_pruning_marker_does_not_stop_a_collection() // SkippedReason null
```

- [ ] **Step 2: Run to verify they fail** — `--filter "FullyQualifiedName~CleanupResumerTests|FullyQualifiedName~LeftoverCleanerTests|FullyQualifiedName~PoolCollectorTests"`.
- [ ] **Step 3: Implement `CleanupResumer` and wire it into `ResumeDeletionAsync`.**
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `feat: finish an interrupted cleanup at start`

---

### Task 7: Run the cleanup as a Versions job

**Files:**
- Create: `src/ReBackup.App/ViewModels/VersionsViewModel.Cleanup.cs`
- Test: `tests/ReBackup.App.Tests/VersionsCleanupTests.cs`

**Interfaces:**
- Consumes: Task 4/5 `CleanupRunner.RunAsync`, `CleanupTarget`, `CleanupResult`, `CleanupProgress`; existing `StartDeleting`, `EndDeleting`, `RunAsJobAsync`, `_deleteCts`, `IndexAfterDeleteAsync`, `VersionsDeleted`, `_context.Worker` (as `IVersionIndexSink`), `_context.Host`.
- Produces:
  - `public bool CanStartCleanup => !IsDeleting && !IsResuming && !IsRestoring && !_isBackupActive() && !_isCopyActive();`
  - `public async Task<CleanupResult?> CleanUpAsync(IgnoreMatcher matcher, IReadOnlyList<CleanupTarget> versions, IProgress<CleanupProgress>? progress = null)`: null when refused (not `CanStartCleanup`, or no saved plan); otherwise `_syncCts?.Cancel()`, `StartDeleting(LocText.Of("cleanup.running", ("count", versions.Count)))`, runs `CleanupRunner.RunAsync` through `RunAsJobAsync`, `EndDeleting()` in `finally`, reports `cleanup.done` / `cleanup.canceled` via `_context.ReportStatus`, raises `VersionsDeleted`, starts `IndexAfterDeleteAsync()`.
  - `public void CancelCleanup()` → cancels `_deleteCts`.
  - Quitting during a cleanup uses the existing exit question for `IsDeletingVersions` (spec §6.5); no new exit text.

- [ ] **Step 1: Write the failing tests** (`MainViewModelFixture`, full plan on disk as in `VersionsCompareTests`)

```csharp
[Fact] public async Task Cleanup_marks_the_plan_as_deleting_while_it_runs()
// GatedStorage holds the manifest write → vm.IsDeleting true, editor.IsDeletingVersions true; released → false
[Fact] public async Task Cleanup_is_refused_while_a_backup_is_active() // isBackupActive => true → CleanUpAsync returns null, nothing changed
[Fact] public async Task Cleanup_updates_the_index() // after run, index FileCount of the version == 1
```

- [ ] **Step 2: Run to verify they fail** — `dotnet test tests/ReBackup.App.Tests --artifacts-path <scratchpad>/artifacts --filter FullyQualifiedName~VersionsCleanupTests`.
- [ ] **Step 3: Implement `VersionsViewModel.Cleanup.cs`; add `cleanup.running`, `cleanup.done`, `cleanup.canceled` to both locale files.**
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `feat(app): run a cleanup as a versions job`

---

### Task 8: CleanupViewModel (grid, scan, staleness)

**Files:**
- Create: `src/ReBackup.App/ViewModels/CleanupViewModel.cs`
- Create: `src/ReBackup.App/ViewModels/CleanupRowViewModel.cs`
- Modify: `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs` (`public CleanupViewModel Cleanup { get; }`, created after `Versions`)
- Test: `tests/ReBackup.App.Tests/CleanupViewModelTests.cs`

**Interfaces:**
- Consumes: Task 2 `CleanupScanner.Scan`, Task 7 `CleanUpAsync`/`CanStartCleanup`/`CancelCleanup`; `VersionsViewModel.VersionRows` (`VersionRowViewModel` with `Info`, `Indexed`, `IsManaged`, `IsDamaged`), `SourceScanCache.GetAsync(plan, maxWait, ct)`, `_context.Worker.RunAsync(planId, index => …)`, `IDialogService.ConfirmDefaultNo`.
- Produces:
  - `CleanupRowViewModel : ObservableObject` — `Name`, `DateText`, `SizeText`, `FilesText`, `[ObservableProperty] bool IsChecked`, `bool IsSelectable`, `string? NotSelectableReason` (`versions.notManaged`-style texts: not managed / damaged / `cleanup.notIndexed`), `RemovesText`, `FreesText` ("—" for pool), `StatusText`.
  - `CleanupViewModel` ctor: `(Func<BackupPlan?> savedPlan, Func<bool> hasUnsavedChanges, VersionsViewModel versions, VersionsContext context, Func<IReadOnlyList<string>> globalDefaults)`.
  - Members: `ObservableCollection<CleanupRowViewModel> Rows` (newest first, rebuilt from `VersionRows`), `bool? AllChecked`, `ObservableCollection<CleanupRuleHit> ByRule`, `LocText TotalsText`, `bool IsScanning`, `bool IsStale`, `LocText? Hint` (`cleanup.saveFirst`, `cleanup.scanAgain`, `cleanup.scanningSource`, source error), `IAsyncRelayCommand ScanCommand`, `IAsyncRelayCommand CleanUpCommand`, `IRelayCommand CancelCommand`, `void Reload()`.
  - Scan: plan saved and no unsaved changes; source scan via cache (`Running` → hint `cleanup.scanningSource` with files count, retry when done); fingerprint `string RuleFingerprint(BackupPlan plan, IReadOnlyList<string> globalDefaults, IReadOnlyList<NestedIgnoreFile> nested)` (patterns + flags + each nested file's path and lines, joined); runs `CleanupScanner.Scan` on the worker; stores fingerprint, the checked ids and their stamps (`CleanupVersionScan.Stamp`).
  - Stale when: the plan is saved (`savedPlan()` fingerprint differs), checked set changes, or after `versions.IndexSyncEnded` a checked version's `StampOf` differs → `IsStale = true`, `CleanUpCommand` disabled, `Hint = cleanup.scanAgain`.
  - CleanUp: `CanExecute = scan exists && scan.Files > 0 && !IsStale && versions.CanStartCleanup`; confirmation `cleanup.confirm` (versions, files, `Formats.Bytes`, pool prefix "≈"); then `versions.CleanUpAsync(matcher, scan.Versions.Select(v => new CleanupTarget(v.Name, v.Stamp)).ToList(), progress)`; fills each row's `StatusText` from `CleanupOutcome` (`cleanup.status.cleaned|nothing|changed|gone|failed`), totals from measured `CleanupResult`, drops the scan.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Unmanaged_damaged_and_unindexed_versions_cannot_be_checked() // IsSelectable false, reason set; AllChecked=true leaves them unchecked
[Fact] public void AllChecked_checks_and_unchecks_the_selectable_rows() // null when mixed
[Fact] public async Task Scan_is_disabled_with_unsaved_rules_or_nothing_checked() // Hint == cleanup.saveFirst
[Fact] public async Task Scan_fills_removes_frees_and_the_rule_list() // rule "target/" → row RemovesText "2", FreesText Formats.Bytes(150), ByRule.Single().Rule == "target/"
[Fact] public async Task Saving_rules_makes_the_scan_stale() // change pattern + save → IsStale, CleanUpCommand.CanExecute false
[Fact] public async Task Changing_the_selection_makes_the_scan_stale()
[Fact] public async Task A_changed_version_stamp_makes_the_scan_stale() // rewrite manifest on disk, sync → IsStale
[Fact] public async Task CleanUp_is_disabled_while_a_backup_runs() // isBackupActive → CanExecute false
[Fact] public async Task A_queued_run_waits_for_the_cleanup() // MainViewModel: queue run during GatedStorage-held cleanup → deferred until IsDeletingVersions false
[Fact] public async Task A_source_scan_still_running_shows_its_progress() // GatedStorage on source → Hint cleanup.scanningSource, no rows filled
```

- [ ] **Step 2: Run to verify they fail** — `--filter FullyQualifiedName~CleanupViewModelTests`.
- [ ] **Step 3: Implement `CleanupRowViewModel`, `CleanupViewModel`, the `PlanEditorViewModel.Cleanup` property, and the `cleanup.*` locale keys used.**
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `feat(app): cleanup tab view model`

---

### Task 9: The Cleanup tab view

**Files:**
- Modify: `src/ReBackup.App/ViewModels/MainViewModel.cs:25-32` (`MainTab.Cleanup` after `Versions`)
- Modify: `src/ReBackup.App/MainWindow.xaml` (rail ~305, header ~594, content ~671: same pattern as `MainTab.Versions`, `DataContext="{Binding SelectedPlan.Cleanup}"`, hidden for new plans like the Versions tab)
- Create: `src/ReBackup.App/Views/CleanupView.xaml`, `CleanupView.xaml.cs`
- Modify: `src/ReBackup.App/Localization/en-US.json`, `de-DE.json` (`tab.cleanup` and the remaining `cleanup.*` labels/tooltips)
- Modify: `README.md` (short "Cleanup" section)
- Test: `tests/ReBackup.App.Tests/MainWindowSmokeTests.cs` (add case)

Layout (spec §3): toolbar with Scan / Clean up… / Cancel and progress bar bound to `versions.DeleteFraction`/`DeleteProgress`; `DataGrid` with columns checkbox (header `CheckBox` bound to `AllChecked`, `IsThreeState`), Date, Size, Files, Removes, Frees, Status; greyed rows with `ToolTip="{Binding NotSelectableReason}"`; below: `TotalsText`, `Hint`, and an `ItemsControl` "By rule" (rule, origin, files, bytes, largest path). Follow `VersionsView.xaml` styles; no new resources.

- [ ] **Step 1: Write the failing smoke test**

```csharp
[Fact] public void The_cleanup_tab_opens_for_a_saved_plan() // SelectedTab = MainTab.Cleanup → a CleanupView is visible, no binding errors traced
```

- [ ] **Step 2: Run to verify it fails** — `--filter FullyQualifiedName~MainWindowSmokeTests`.
- [ ] **Step 3: Add the enum value, the three XAML places, `CleanupView`, locale keys, README section.**
- [ ] **Step 4: Run the whole suite** — `dotnet test tests/ReBackup.Core.Tests` and `dotnet test tests/ReBackup.App.Tests --artifacts-path <scratchpad>/artifacts` → all PASS.
- [ ] **Step 5: Commit** — `feat(app): cleanup tab`

---

### Task 10: Manual check on the NAS

- [ ] **Step 1:** Ask the user to quit ReBackup via the tray; build and start the new exe via Explorer.
- [ ] **Step 2:** In the Cleanup tab of a real plan: check 2 old versions, Scan, note "Removes"/"Frees" and the by-rule list.
- [ ] **Step 3:** Clean up…; compare the measured result with the scan (full: equal; pool: freed ≤ estimate), check the Versions tab sizes and that a restore of one cleaned file still works.
- [ ] **Step 4:** Report to the user; on approval merge `feature/cleanup-tab` into `main` with `--no-ff`.
