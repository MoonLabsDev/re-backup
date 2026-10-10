# S3 as Source and Target Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ReBackup plans can use a folder or an S3 location as source and as target, in any combination.

**Architecture:** Plans already hold `StorageLocation`s; the remaining `System.IO` paths for the source and the restore destination move behind `IStorage`, the app's storage factory gains S3 (connections in the config folder), the runner copies in parallel when S3 is involved, and the leftover cleaner respects markers of other hosts.

**Tech Stack:** C# / .NET 9, WPF + CommunityToolkit.Mvvm, AWSSDK.S3 (via ReBackup.Storage.S3), xUnit 2.9 + FluentAssertions 7, LocalStack (Testcontainers).

**Spec:** `docs/superpowers/specs/2026-10-09-s3-source-target-design.md`

## Global Constraints

- re-backup branch `feature/s3-source-target` from `main` (a5012e9). Task 6 also touches re-s3 (branch `chore/rebackup-1.4`).
- After every task: `dotnet build ReBackup.sln` 0 warnings; `dotnet test ReBackup.sln` green ("Bestanden!"; LocalStack runs, Docker is up).
- Folder behaviour must not change: every existing Core/App test stays green without weakening.
- `ReBackup.Core` must not reference `ReBackup.Storage.S3` (it sees only `IStorage`/`IStorageFactory`); the App composes the S3 factory. Update `ProjectReferenceTests` only if a rule genuinely changes, and say why.
- Version on S3 = exact local format (version folder name, `re-pending.json`, `re-manifest.json`, `re-deleting.json`).
- Parallelism: 8 workers when source or target kind is `"s3"`; folder→folder stays sequential.
- Markers: own when `Host` equals `Environment.MachineName` (case-insensitive) or `Host` is null/empty; foreign markers are never cleaned or deleted automatically.
- Texts via locales (en-US + de-DE), LocaleFileTests green.
- Never start any app exe from the tools (MSIX sandbox redirects AppData); in-process smoke tests only.
- Commits conventional, ending with blank line + `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A run against a target where another PC is mid-run** (its `re-pending.json`, foreign host) — nothing of it is deleted, retention skips it, run ends with warnings → Task 2 test `Foreign_pending_folder_is_never_touched`.
2. **Network drop while 8 uploads are in flight** — all workers stop, the run ends as error, the next run cleans the version → Task 4 test `Storage_failure_stops_all_workers_and_cleans_up`.
3. **S3 source with archived (Glacier) objects** — skipped, logged, warnings, other files copied → Task 4 test with a fake storage.
4. **Plan whose S3 target lies inside its S3 source** (same bucket, prefix nested) — validation error, never runs → Task 5 test.
5. **Restore of an S3-source version to the original location** — writes into the bucket, never deletes, conflicts ask; modification times not attempted → Task 3 test via InMemoryStorage (capabilities without SetModifiedTime).

---

### Task 1: Startup registration arguments and refresh (re-backup)

**Files:** `src/ReBackup.Shared.Wpf/Services/StartupRegistration.cs`, `src/ReBackup.App/App.xaml.cs`, tests in `tests/ReBackup.Shared.Wpf.Tests`, `tests/ReBackup.App.Tests`.

**Interfaces (Produces):** `StartupRegistration.Apply(string appId, bool enabled, string arguments)` (no hard-coded `--minimized`); a testable seam for the registry (e.g. `IRunKey` with an in-memory fake) and for the exe path.

- [ ] Tests: the Run value is `"<exe>" <arguments>`; disabled removes it; ReBackup passes `--minimized`; on startup with StartWithWindows on, the value is re-applied with the current exe path (stale path replaced); off → untouched.
- [ ] Implement; full suite; commit `fix(shared): startup registration takes its arguments and follows the exe`.

### Task 2: Markers of other hosts (Core)

**Files:** `src/ReBackup.Core/Backup/VersionMarkers.cs`, the leftover cleaner, retention planning/versions listing in `src/ReBackup.Core/Versions/*`, VersionsTab VM + context menu in App, locales.

**Interfaces (Produces):** `MarkerInfo.IsOwn(string machineName)`; target state/listing exposes foreign marked folders (e.g. `ForeignMarkedFolder(string Name, string Host, DateTime StartedUtc, MarkerKind Kind)`); `TakeOverAsync(folder)` cleans it with the normal rules.

- [ ] Tests: `Foreign_pending_folder_is_never_touched` (Review Focus 1: cleaner, retention and target-full deletion skip it; run ends Warnings); foreign deleting marker same; host-less marker treated as own (compat); own marker cleaned as today; take-over cleans; versions tab shows "being written/deleted by <host> since <time>" (en+de).
- [ ] Implement; full suite; commit `feat(core): leave markers of other hosts alone`.

### Task 3: Source and restore destination through IStorage (Core)

**Files:** `src/ReBackup.Core/Indexing/LiveScan.cs` (+ SourceIndexer), `src/ReBackup.Core/Backup/BackupRunner.cs` (source reads), `src/ReBackup.Core/Versions/Restorer.cs` (destination writes), tests.

**Interfaces:** scan and runner take the source as an `IStorage` opened from `plan.Source` via `IStorageFactory`; `.backupignore` read through `IStorage`; Restorer writes via `IStorage.CreateAsync` (Overwrite per user choice, `ModifiedUtc` only when `Capabilities` has `SetModifiedTime`), never deletes.

- [ ] Tests: existing folder tests unchanged and green; new: scan/run/restore with an InMemoryStorage source and destination; restore to an "S3-like" storage without SetModifiedTime (Review Focus 5); locked/unreadable file mapping unchanged for folders.
- [ ] Implement; full suite; commit `refactor(core): source and restore destination through IStorage`.

### Task 4: Parallel copy, archived objects, no free space (Core)

**Files:** `BackupRunner.cs` (+ a small copy-worker unit), tests.

**Interfaces:** degree of parallelism 8 when `plan.Source.Kind == "s3" || plan.Target.Kind == "s3"`, else 1 (constant `ParallelS3Copies = 8`); manifest order = sorted relative path; archived = `StorageIOException` whose message marks it archived (see ReBackup.Storage.S3 `S3Errors`) → skipped + logged; targets without `FreeSpace` capability skip the pre-check.

- [ ] Tests: `Storage_failure_stops_all_workers_and_cleans_up` (Review Focus 2); one failing file is skipped and logged while others finish; cancellation stops promptly; manifest order deterministic; archived objects skipped with warnings (Review Focus 3); no free-space check for S3 targets; folder→folder still sequential.
- [ ] Implement; full suite; commit `feat(core): parallel copy for S3, archived objects skipped`.

### Task 5: App composition and UI (App)

**Files:** App.xaml.cs (factory: `S3StorageFactory(new StorageFactory(), store.TryResolve)` with store at `<config folder>\connections.json`, store failures → `StorageIOException` with cause), `PlanValidator` (S3 rules), PlanView + VM (Folder|S3 switch, connection combo with "New connection…", prefix box, Test), SettingsWindow (S3 accounts…, S3 connections… list with delete refused while used), hide "target full" toggle for S3 targets, locales, README S3 section (connections, full-copy cost note), version 1.4.0.

- [ ] Tests: validator — S3 needs existing connection and valid prefix; overlap same bucket+region with nested prefix rejected (Review Focus 4), different bucket fine; VM — switch keeps the other kind's last value until saved, connection list, prefix validation, toggle hidden; connections list delete refusal names plans; store failure message surfaces; smoke tests for PlanView and SettingsWindow.
- [ ] Implement; full suite; commit `feat(app): S3 as source and target`.

### Task 6: Integration, release prep, ReS3 bump

**Files:** `tests/ReBackup.Core.Tests/Integration/*` (LocalStack via S3ServerFixture from ReBackup.Storage.S3.Tests), README, then re-s3 submodule + `--minimized` argument.

- [ ] LocalStack tests: folder→S3 full run + retention + versions index + restore to a temp folder (bytes and times equal); S3→folder full run; interrupted folder→S3 run cleaned by the next run.
- [ ] Merge `feature/s3-source-target` into re-backup main locally (`--no-ff`), full suite on main, delete branch; do not push.
- [ ] re-s3: bump submodule to that merge, pass `--minimized` to `StartupRegistration.Apply`, startup refresh; full ReS3 suite; commit on `chore/rebackup-1.4`; do not merge.
