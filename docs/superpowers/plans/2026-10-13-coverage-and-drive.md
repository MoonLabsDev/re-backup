# Retention Coverage Timeline + Google Drive Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Part A: a per-rule timeline showing which retention slots hold a backup (both apps). Part B: Google Drive (service account) as source and target in ReBackup.

**Architecture:** A: pure slot computation in `ReBackup.Shared.Retention`, a rendered control in `ReBackup.Shared.Wpf.Controls`, wired into the Retention and Versions tabs of ReBackup and ReS3. B: new provider project `ReBackup.Storage.GoogleDrive` (`DriveStorage` over an `IDriveApi` seam, store, tester, factory), composed by the ReBackup app like S3.

**Tech Stack:** C# / .NET 9, WPF + CommunityToolkit.Mvvm, `Google.Apis.Drive.v3` + `Google.Apis.Auth` (latest stable), xUnit 2.9 + FluentAssertions 7, Xunit.SkippableFact.

**Specs:** `docs/superpowers/specs/2026-10-13-retention-coverage-design.md`, `docs/superpowers/specs/2026-10-13-google-drive-design.md`

## Global Constraints

- re-backup: Part A on `feature/retention-coverage`; Part B on `feature/google-drive` from main after the A checkpoint. re-s3: branch `feat/coverage`.
- After every task: `dotnet build` of the touched solution(s) 0 warnings; `dotnet test` green ("Bestanden!"; Docker/LocalStack up).
- Existing behaviour unchanged; no existing test weakened.
- `ReBackup.Core` references neither `ReBackup.Storage.S3` nor `ReBackup.Storage.GoogleDrive`; `ReBackup.Shared.Wpf` must not reference `ReBackup.Storage.GoogleDrive` (ReS3 must not pull Google libraries). Extend `ProjectReferenceTests` for the new project.
- Texts via locales (en-US + de-DE), LocaleFileTests green.
- Colours only via theme keys: `Brush.Accent` (present), `Brush.Warning` (with warnings), `Brush.Danger` (missing), `Brush.TextFaint` (before first: filled, low opacity; pending: outline only).
- Drive kind string `"gdrive"`; connections file `drive-connections.json` in the config folder; delete is permanent; parallel copy 8 when any side is not `"fs"`; export limit 10 MB.
- Never start any app exe from the tools (MSIX sandbox); in-process smoke tests only.
- Commits conventional, ending with blank line + `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Monthly anchor "31" / "0" across February and yearly "02-29"** — slots neither skip nor duplicate a month/year → Task A1 tests `Monthly_31_has_one_slot_per_month_through_february`, `Yearly_0229_in_non_leap_years`.
2. **A plan whose rules changed so the engine keeps versions far back** (gaps) — the lane reaches back to the oldest kept version and every slot inside is green or red, none hidden → Task A1 `Gaps_extend_the_lane_to_the_oldest_kept_version`.
3. **Thousands of daily slots (keep 9999)** — control renders without freezing, newest fit, "+N" label → Task A2 `Many_slots_draw_newest_and_count_the_rest` (render ≤ 200 ms).
4. **Drive folder with two files of the same name** — source run skips and logs it, never copies the wrong one; write to an ambiguous target path fails naming the path → Task B2 tests.
5. **Rate limit storm during a parallel upload** (429 on many calls) — retried with backoff, cancellation still prompt, run does not end as error unless retries run out → Task B2 `Rate_limits_are_retried_and_cancellation_stays_prompt`.

---

## Part A — Retention coverage

### Task A1: Slot computation (Shared)

**Files:** Create `src/ReBackup.Shared/Retention/RetentionCoverage.cs`; test `tests/ReBackup.Shared.Tests/Retention/RetentionCoverageTests.cs` (or the existing test project that covers `ReBackup.Shared.Retention`).

**Interfaces (Produces):** exactly the types of spec §3: `CoverageQuality`, `CoverageVersion(string Id, DateTime LocalTime, CoverageQuality Quality, bool Counted, string? Detail)`, `SlotState { Present, PresentWithWarnings, Missing, Pending, BeforeFirst }`, `CoverageSlot(DateOnly Start, DateOnly End, SlotState State, CoverageVersion? Version)`, `CoverageLane(int RuleIndex, RetentionRule Rule, IReadOnlyList<CoverageSlot> Slots)` (oldest first), `RetentionCoverage.Compute(versions, rules, DateOnly today)`.

- [ ] Tests (today fixed, e.g. 2026-10-13): daily keep 7 with all days → 7 Present; one gap → that slot Missing and lane has 8 slots back to the oldest kept; weekly "Sun" keep 4 slot starts are Sundays and End = next Sunday; `Monthly_31_has_one_slot_per_month_through_february`; monthly "0" and "-2"; `Yearly_0229_in_non_leap_years`; `Gaps_extend_the_lane_to_the_oldest_kept_version`; slots before the oldest version → BeforeFirst; no versions → all BeforeFirst except current Pending; current slot without version → Pending, with version → Present; Warning quality → PresentWithWarnings; a non-counted version alone in a slot → PresentWithWarnings with that version, ignored by the engine; slot version equals the engine's kept representative for that rule (two versions in one slot: the anchor-day one); no rules → empty.
- [ ] Implement using `RetentionRules.SlotStart` and `RetentionEngine.Evaluate` (counted versions only); full suite; commit `feat(shared): retention coverage slots`.

### Task A2: Coverage control (Shared.Wpf)

**Files:** Create `src/ReBackup.Shared.Wpf/Controls/RetentionCoverageControl.cs`; tests in `tests/ReBackup.Shared.Wpf.Tests`.

**Interfaces (Produces):** `RetentionCoverageControl : FrameworkElement` with DPs `Lanes` (`IReadOnlyList<CoverageLane>?`), `LaneLabels` (`IReadOnlyList<string>?`), `SelectCommand` (`ICommand?`, parameter = `CoverageVersion.Id`), `SelectedId` (`string?`), `SlotText` (`Func<CoverageSlot, string>?` for tooltips). Public pure helper `static int? HitTest(Size size, IReadOnlyList<CoverageLane> lanes, Point p, out CoverageSlot? slot)` for tests. Layout per spec §4 (22 px rows, 170 px label column, period colours as `RetentionTimelineControl`, per-lane equal-width cells right-aligned, min 2 px, 1 px gap at ≥ 6 px, "+N" muted label when not all fit).

- [ ] Tests (STA): render in light and dark theme without exception; HitTest maps a point to the right slot of the right lane; click on a Present cell executes `SelectCommand` with its Id, on Missing/Pending/BeforeFirst does nothing; `Many_slots_draw_newest_and_count_the_rest` (9999 daily slots, width 600: label "+N" with correct N, render ≤ 200 ms); tooltip text from `SlotText`.
- [ ] Implement; full suite; commit `feat(wpf): retention coverage control`.

### Task A3: ReBackup integration

**Files:** `RetentionPreviewViewModel`, `RetentionView.xaml` (new card "Coverage" between rules and forecast), `VersionsViewModel` (+ `VersionsView.xaml`, above the version list), a small builder `src/ReBackup.App/ViewModels/CoverageBuilder.cs` (versions + rules + run log → lanes + labels + tooltip func), locales.

**Interfaces:** `CoverageBuilder.Build(IReadOnlyList<VersionInfo> owned, IReadOnlyList<RetentionRule> rules, IReadOnlyList<RunLogEntry> log, DateOnly today)` → `(IReadOnlyList<CoverageLane> Lanes, IReadOnlyList<string> Labels)`; quality Warning when the log entry with `Version == name` has `Status == CompletedWithWarnings` (Detail = `SkippedCount`), Ok otherwise or when not found. Only `VersionOwnership.Owned` versions; all counted. VM properties `CoverageLanes`, `CoverageLabels`, `HasCoverage`, `NoRulesText`, `SelectCoverageCommand`.

- [ ] Tests: builder maps warnings from the log, unknown version → Ok, foreign versions ignored; Retention tab lanes follow rule edits (add/remove rule, keep change) without reload; click selects the `NowRow` with that name; Versions tab uses saved rules and click sets the selected version; no rules → `HasCoverage` false with the hint; load error → hidden; smoke tests for both views.
- [ ] Implement; full suite; commit `feat(app): retention coverage in the Retention and Versions tabs`.

### Task A4: ReS3 integration and checkpoint

**Files (re-s3, branch `feat/coverage`, submodule at the A3 commit):** `RetentionTabViewModel` + `Views/RetentionView.xaml`, `VersionsTabViewModel` + `Views/VersionsView.xaml`, `ViewModels/CoverageBuilder.cs`, locales.

**Interfaces:** `CoverageBuilder.Build(SeriesModel series, IReadOnlyList<RetentionRule> rules, DateOnly today)`: Complete → counted Ok; Incomplete / PartiallyDeleted → not counted, Warning, Detail = localized incomplete reason.

- [ ] Tests: incomplete-only slot is yellow with that version; lanes follow rule/template edits and series switch; click selects the `NowRow` / version row; no rules hint; smoke tests.
- [ ] Implement; full ReS3 suite; commit on `feat/coverage` (do not merge yet).
- [ ] Checkpoint (controller): merge `feature/retention-coverage` into re-backup main (`--no-ff`), full suite, push `origin main` (user approved; includes 1.4.0, no release).

## Part B — Google Drive (re-backup `feature/google-drive`)

### Task B1: Project, accounts, connections, store

**Files:** Create `src/ReBackup.Storage.GoogleDrive/` (csproj in the solution, `Connections/DriveAccount.cs`, `DriveConnectionInfo.cs`, `DriveConnection.cs`, `DriveConnectionStore.cs`), move `SecretProtector` to `ReBackup.Storage` if the S3 one is not reusable (keep its behaviour and tests); test project `tests/ReBackup.Storage.GoogleDrive.Tests`; `ProjectReferenceTests`.

**Interfaces (Produces):** `DriveAccount(string Id, string Name, string ClientEmail, string? ProtectedKey)`; `DriveConnectionInfo(string Id, string Name, string AccountId, string RootId, string RootName, string? SharedDriveId)`; `DriveConnection(DriveConnectionInfo Info, string ClientEmail, string KeyJson)`; `DriveConnectionStore(string path)` with `LoadAccounts`, `TryGetAccount`, `SaveAccount` (reads `client_email` from the key JSON; rejects JSON that is not a service-account key), `DeleteAccount` (refused while used), `LoadAll`, `TryGet`, `Save`, `Delete`, `TryResolve(string id) → DriveConnection?` (null for missing; `StorageIOException` with message for undecryptable key / corrupt / newer format), `ConnectionsUsing(accountId)`; file `{ "formatVersion": 1, "accounts": [...], "connections": [...] }`.

- [ ] Tests: round trip; key from another Windows account → re-enter message; not a service-account key rejected; corrupt / newer format messages; delete refused while used; project-reference rules.
- [ ] Implement; full suite; commit `feat(gdrive): accounts and connections`.

### Task B2: DriveStorage over IDriveApi

**Files:** `IDriveApi.cs`, `DriveItem.cs`, `DriveStorage.cs`, `DriveWriter.cs`, `DriveErrors.cs`, `DriveRetry.cs`; fake `tests/.../Fakes/InMemoryDriveApi.cs`; `src/ReBackup.Storage/StorageExceptions.cs` (`StorageIOException.NotReadable`, `Archived` implies it).

**Interfaces (Produces):** `IDriveApi` (async, `CancellationToken` each): `ListChildrenAsync(folderId)`, `GetAsync(id)`, `CreateFolderAsync(parentId, name)`, `UploadAsync(parentId, name, Stream, DateTime? modifiedUtc)`, `UpdateContentAsync(id, Stream, DateTime? modifiedUtc)`, `DeleteAsync(id)`, `DownloadAsync(id) → Stream`, `ExportAsync(id, mimeType) → Stream`, `ListSharedDrivesAsync()`; errors as `DriveApiException(int Status, string Reason)`. `DriveItem(string Id, string Name, string MimeType, long? Size, DateTime ModifiedUtc, string? Md5, long Version)`. `DriveStorage(IDriveApi api, string rootId, TimeProvider? time = null)` implementing `IStorage` per spec §3 (capabilities `SetModifiedTime | EmptyDirectories`). Export map: document → docx, spreadsheet → xlsx, presentation → pptx, drawing → pdf; other `application/vnd.google-apps.*` (shortcut, form, site, …) left out of listings.

- [ ] Tests (fake): resolve nested paths, cache dropped on not-found; same-name siblings → stat/open/write throw "ambiguous name"; names with `/` left out; Google doc listed as `Name.docx` size 0, open exports, 10 MB export error → `StorageIOException.NotReadable`; exported-name collision with a real file → doc left out; exclusive create conflict and the create race (second file appears → ours deleted, Conflict); overwrite keeps the id; `ExpectedStamp` mismatch → Conflict; delete non-empty folder → Conflict, root never; error mapping table of spec §3; `Rate_limits_are_retried_and_cancellation_stays_prompt` (fake time provider, 6 attempts max); runner treats `NotReadable` like archived (one Core test).
- [ ] Implement; full suite; commit `feat(gdrive): Drive storage`.

### Task B3: Real API, factory, tester, runner kinds

**Files:** `GoogleDriveApi.cs` (wraps `DriveService`, `supportsAllDrives`/`includeItemsFromAllDrives`, resumable upload ≥ 5 MB), `DriveStorageFactory.cs`, `DriveConnectionTester.cs`; `BackupRunner` parallel condition `Kind != "fs"` on either side.

**Interfaces:** `DriveStorageFactory(IStorageFactory inner, Func<string, DriveConnection?> resolve, Func<DriveConnection, IDriveApi>? apiFactory = null)` handles `"gdrive"`; `DriveConnectionTester.RunAsync(DriveConnection, string path, bool asTarget, CancellationToken)` → result with outcome (Ok, AccessDenied, NotFound, NotSharedDrive, Error) and message (incl. "share with <e-mail>" hint).

- [ ] Tests: factory delegates other kinds, unknown connection → StorageNotFound; tester outcomes on the fake (target on My Drive folder → NotSharedDrive; probe file written and removed); runner uses 8 workers for fs→gdrive; optional `SkippableFact` integration test against a real shared drive when `REBACKUP_GDRIVE_KEY_FILE` and `REBACKUP_GDRIVE_SHARED_DRIVE_ID` are set (write, list, read, overwrite, delete, Google-doc export if present).
- [ ] Implement; full suite; commit `feat(gdrive): Drive API, factory and tester`.

### Task B4: ReBackup app

**Files:** App.xaml.cs composition (`DriveStorageFactory(S3StorageFactory(...), drive.TryResolve)`, store at `<config>\drive-connections.json`), `PlanValidator` (Drive rules, target needs `SharedDriveId`, overlap = same RootId/SharedDriveId and nested path), PlanView + VM (Folder | S3 | Drive), Drive account/connection windows + VMs in `ReBackup.App`, SettingsWindow entries, "target full" hidden for Drive, `ContainsConfiguration` counts `drive-connections.json`, locales, README Drive section, version 1.5.0 (`Directory.Build.props`, README badge).

- [ ] Tests: validator (missing connection, invalid path, target not on shared drive, overlap); kind switch with three kinds keeps the other kinds' values until saved; connection dialog VM lists shared drives and accepts a folder link/ID (parses `https://drive.google.com/drive/folders/<id>`); delete refusal names plans; scheduled run of an invalid Drive plan skipped; smoke tests for PlanView and SettingsWindow.
- [ ] Implement; full suite; commit `feat(app): Google Drive as source and target`.
- [ ] Controller: final whole-branch review (Parts A and B together), fixes, merge `feature/google-drive` into main, full suite; then re-s3 `feat/coverage`: bump submodule to that merge, version 0.3.0, full ReS3 suite, merge. Push/release on the user's go-ahead.
