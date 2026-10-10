# S3 as Source and Target in ReBackup — Design

Date: 2026-10-09
Status: Approved (brainstorming), pending spec review
Sub-project 4 of the storage programme (after the storage abstraction, the S3 provider, ReS3 and S3 accounts).

## 1. Purpose

A ReBackup plan's source and target can each be a local/network **folder** or an **S3 location** (connection +
prefix), in any combination: folder→S3 (offsite copy), S3→folder (pull a bucket down to the NAS), S3→S3. Versions on
S3 keep the exact format of local versions (full copies, `re-manifest.json`, markers), so retention, the versions tab
and restore work unchanged and a bucket can be restored without ReBackup.

## 2. Decisions

| Topic | Decision |
|---|---|
| Directions | Source and target each folder or S3; all four combinations |
| Version format on S3 | Full copies, same as local (no deduplication, no server-side copy for now) |
| Connections | `connections.json` (format 2, accounts + connections) in ReBackup's config folder; secrets DPAPI-bound to the Windows account, so a synced config on another PC shows "re-enter secret" |
| Shared targets | A target belongs to one PC/plan. Markers of another host are never cleaned up automatically; "Take over and clean up…" is manual |
| Parallel copy | 8 files at a time when the source or the target is S3; folder→folder unchanged |
| Out of scope | Deduplication / CopyObject optimisation, multi-host shared targets, non-AWS providers |

## 3. Plans and UI

- `BackupPlan.Source` / `Target` are already `StorageLocation(Kind, Path, ConnectionId)`. Kind `"fs"` (path) or
  `"s3"` (Path = prefix, ConnectionId = S3 connection id). The plan JSON format does not change.
- `PlanValidator`: an S3 location needs an existing connection and a valid prefix (`StoragePath.Validate`, `""`
  allowed for the whole bucket); source and target must not overlap — same connection *bucket+region* and one prefix
  equal to or inside the other (`a` vs `a/b`); folder rules unchanged.
- App composition: the storage factory is `S3StorageFactory(new StorageFactory(), connections.TryResolve)` over the
  `S3ConnectionStore` at `<config folder>\connections.json`; store failures (corrupt, newer format) surface as
  `StorageIOException` with the store's message (as in ReS3).
- Plan tab: source and target each get a segmented switch **Folder | S3**.
  - Folder: today's path box + browse button.
  - S3: connection combo (names; "New connection…" opens the shared `S3ConnectionDialog`) and a prefix box
    (placeholder `backups/moonlabs`), with "Test" running the shared connection tester on the chosen connection and
    prefix.
- Settings: "S3 accounts…" (shared `S3AccountsDialog`) and "S3 connections…" (simple list: add/edit via the shared
  dialog, delete refused while a plan uses the connection — list the plans).
- The toggle "When the target is full, first delete versions that retention would delete anyway" is hidden for S3
  targets (no free-space information).
- Restore: "original location" of an S3 source writes back into the bucket through `IStorage` (never deletes; existing
  files → Overwrite / Skip / Keep both as today); "choose a folder" restores to a local folder. Modification times are
  set only where the destination supports it (`StorageCapabilities.SetModifiedTime`).

## 4. Backup run

All source and target access goes through `IStorage` (today `LiveScan`, `BackupRunner` and `Restorer` still touch
`System.IO` for the source / restore destination — they move to `IStorage`; behaviour for folders stays identical,
proven by the existing tests).

**Source S3:**
- Scan = recursive `ListAsync`: files with size and `ModifiedUtc` (upload time); folder placeholders ignored. Ignore
  patterns apply to the relative keys; `.backupignore` files are honoured if present as objects. The Ignore & Preview
  tab (tree, treemap, live counts) is fed from the listing.
- Read via `OpenReadAsync`; archived objects (`StorageIOException` "archived") are skipped and logged like locked
  files → run "completed with warnings".

**Target S3:**
- Same version folder name, markers, manifest and leftover rules as local.
- No free-space pre-check (capability `FreeSpace` absent); the "target full" path never triggers.
- Original modification times are recorded in the manifest (they cannot be set on S3).
- Large files use the existing multipart writer; an interrupted upload is aborted.

**Parallel copy:** when source or target kind is `"s3"`, files are copied by 8 workers (bounded channel); per-file
hashing (xxHash64), skip logging, progress and cancellation stay per file; the manifest lists files in the same
deterministic order as today (sorted by relative path) regardless of completion order. A failing file is logged and
skipped as today; a storage-level failure (`StorageUnavailableException`, `StorageAccessDeniedException`) cancels the
remaining workers and ends the run as error, with the usual cleanup. A source file refused on open
(`StorageAccessDeniedException`: an ACL, an SSE-KMS key) counts as storage-level only when the source's root is refused
or unreachable too (revoked credentials); otherwise the file is skipped like any other failing file. If at least one
file was skipped as refused and no file was copied (e.g. ListBucket without GetObject, or no `kms:Decrypt`), the run
ends as error with the usual cleanup (no version, no retention, reason `core.run.sourceReadDenied`).

## 5. Markers of other hosts

Applies to every target kind.

- `re-pending.json` / `re-deleting.json` already carry `Host` and `StartedUtc`. The leftover cleaner treats a marked
  folder as its own leftover only when `Host` equals this machine (`Environment.MachineName`, case-insensitive) or the
  marker has no host (written by 1.0.x/1.1 — treated as own for compatibility).
- A folder with a foreign marker is never cleaned, deleted or counted by retention. The versions tab lists it as
  "being written by <host> since <time>" / "being deleted by <host> since <time>"; a run that meets one ends "completed
  with warnings" (never error).
- Context menu "Take over and clean up…" (confirmation naming host and time) cleans it with the normal rules.

## 6. Startup registration

`StartupRegistration.Apply(appId, enabled, arguments)` takes the arguments from the app instead of hard-coding
`--minimized` (ReBackup and ReS3 pass `--minimized`). When "Start with Windows" is on, the app re-applies the
registration on every start, so the Run entry points to the exe that is running (fixes stale paths after updates).

## 7. Errors

| Condition | Behaviour |
|---|---|
| Connection missing / secret must be re-entered / connections.json unreadable | Plan shows the target/source as unreachable with the reason; run ends as error; nothing deleted |
| Network drop mid-run | Error; written files cleaned up by marker rules (now or next run) |
| Archived source object | Skipped, logged, warnings |
| Foreign marker in target | Never touched; warnings; manual take-over |
| Source and target overlap (S3) | Plan validation error |

## 8. Testing

- Unit (InMemoryStorage for "S3" and FileSystemStorage): every source/target combination through the runner,
  restore and scan; foreign markers (never cleaned, retention skips, take-over); parallel copy (cancellation, one
  failing file, storage failure stops all, deterministic manifest order); plan validation of S3 overlap; startup
  registration arguments and refresh.
- LocalStack: folder→S3 and S3→folder full runs incl. retention, versions index and restore; an interrupted run
  cleaned up by the next one.
- Fake: archived objects.
- View models: Folder|S3 switch, connection picker, prefix validation, hidden "target full" toggle, connections list
  delete refusal.
- Manual (human): real bucket.

## 9. Versions

ReBackup 1.4.0 (README: S3 section, connections, costs note for full copies). ReS3 bumps the submodule (no feature
change; it passes `--minimized` explicitly and gets the startup refresh).
