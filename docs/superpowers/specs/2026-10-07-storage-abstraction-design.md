# Storage Abstraction & Shared Libraries — Design

Date: 2026-10-07
Status: Approved (brainstorming), pending spec review

## 1. Purpose

Make ReBackup's source and target pluggable storage locations instead of local Windows paths, and extract the code that a second app (re-s3) will reuse. This sub-project changes **no visible behaviour**: after it, ReBackup still only offers local folders, but every source/target access goes through one storage interface that S3 and Google Drive can implement later.

## 2. Programme context

This spec is sub-project 1 of five. Each gets its own spec, plan and implementation.

| # | Sub-project | Depends on |
|---|---|---|
| **1** | **Storage abstraction + shared libraries (this spec)** | – |
| 2 | S3 storage provider (AWS only, access key + secret, DPAPI), shared | 1 |
| 3 | re-s3 app: retention manager for S3 backups uploaded by other systems (pattern-based version grouping, compare, delete, tray + `--apply-retention` CLI, new logo) | 1, 2 |
| 4 | S3 as source/target in ReBackup | 1, 2 |
| 5 | Google Drive provider (service account + shared drive or desktop OAuth with loopback redirect; decided in its own spec) | 1 |

Design choices in this spec must not block 2–5. In particular, nothing may assume atomic directory rename, directory timestamps, or empty directories.

## 3. Scope

In scope:
- New projects `ReBackup.Storage`, `ReBackup.Shared`, `ReBackup.Shared.Wpf`.
- `IStorage` with `FileSystemStorage` and `InMemoryStorage`.
- Marker-based version lifecycle replacing `.partial`/`.deleting` renames, for all storage kinds.
- `StorageLocation` in the plan model with migration from plain path strings.
- Async conversion of the Core code paths that touch source or target.

Out of scope: S3, Google Drive, connection management, any UI change, re-s3, the logo.

## 4. Projects after the refactor

```
ReBackup.sln
  src/ReBackup.Storage      net9.0      IStorage, StorageEntry, StorageWriter, StorageLocation,
                                         storage exceptions, FileSystemStorage, InMemoryStorage
  src/ReBackup.Shared       net9.0      Retention (rule, engine, simulator, rules), Schedule
                                         (trigger, calculator, scheduler, RunTrigger), AtomicFile,
                                         PathUtil, ByteSize, JsonDefaults, localization mechanism,
                                         TreemapLayout, IPreviewEntry, ThemeMode, AppLanguages
  src/ReBackup.Shared.Wpf   net9.0-win  Theme/*, Controls.xaml, converters, ThemeManager,
                                         ProgressRing, TileGrid, TreemapControl,
                                         RetentionTimelineControl + TimelineLane, Loc + markup
                                         extensions, Formats, tray scaffolding
  src/ReBackup.Core         net9.0      Backup, Plans, Versions, Indexing, Ignore, Restore,
                                         Config, Settings, RetentionPlanner (VersionInfo-based)
  src/ReBackup.App          net9.0-win  Views, ViewModels, app locales
  tests/ReBackup.Core.Tests
```

Dependency rules (enforced by an architecture test that inspects assembly references):
- `Storage` → BCL only.
- `Shared` → BCL, System.Text.Json.
- `Shared.Wpf` → `Shared`, WPF.
- No Shared/Storage project references `Core` or `App`.

### 4.1 Decoupling required for extraction

- `RetentionPlanner` depends on `VersionInfo` and stays in Core. `RetentionEngine` already works on the generic `RetentionVersion(Name, LocalTime)` and moves to Shared unchanged; consumers filter owned versions themselves, as Core's planner does today.
- `RunTrigger` moves from `Backup/RunLog.cs` to Shared/Schedule.
- Retention and trigger texts (`core.retention.*`, `core.trigger.*`) move out of `CoreTexts.Templates` into `SharedTexts` in `ReBackup.Shared`, renamed `shared.retention.*` / `shared.trigger.*`, with embedded label files `shared.en-US.json` / `shared.de-DE.json`. App-specific `core.*` texts stay in Core.
- Theme XAML files move into `ReBackup.Shared.Wpf`, so `ThemeManager` loads them from the fixed pack URI `/ReBackup.Shared.Wpf;component/Theme/…`.
- `Loc` loads label files from several sources (Shared, Shared.Wpf, the app assembly) instead of the hard-coded `ReBackup.App.Locales.` prefix, and recognizes English texts through registered recognizers (`CoreTexts`, `SharedTexts`) instead of calling `CoreTexts` directly.
- `ThemeMode` and `AppLanguages` move from Core/Settings to Shared.

### 4.2 Consumption by re-s3

re-s3 adds re-backup as a git submodule under `external/re-backup` and references `ReBackup.Storage`, `ReBackup.Shared` and `ReBackup.Shared.Wpf` via `ProjectReference`. The submodule commit pins the version; re-s3 only picks up ReBackup changes through a deliberate submodule bump.

## 5. Storage interface

```csharp
public interface IStorage                        // bound to one root location
{
    StorageCapabilities Capabilities { get; }
    Task<StorageEntry?> StatAsync(string path, CancellationToken ct);
    IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive, CancellationToken ct);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);
    Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct);
    Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct);
    Task EnsureDirectoryAsync(string path, CancellationToken ct);
    Task<long?> GetFreeSpaceAsync(CancellationToken ct);
}

[Flags] public enum StorageCapabilities
{ None = 0, FreeSpace = 1, Links = 2, EmptyDirectories = 4, SetModifiedTime = 8, CaseSensitive = 16 }

public sealed record StorageEntry(string Path, bool IsDirectory, long Size,
                                  DateTime ModifiedUtc, bool IsLink, string? Stamp);

public sealed record CreateOptions(bool Overwrite = false, DateTime? ModifiedUtc = null, bool Durable = false);

public abstract class StorageWriter : Stream     // write-only
{
    public abstract Task CommitAsync(CancellationToken ct);  // makes the file visible
    // Dispose without Commit discards the file.
}
```

Rules:
- Paths are relative to the root, `/`-separated, no leading `/`. `""` is the root. Drive letters, `\` and `Path.GetFullPath` live only inside `FileSystemStorage`.
- `CreateAsync` with `Overwrite = false` is exclusive: if the path exists it throws `StorageConflictException`.
- A committed file is visible atomically. `FileSystemStorage` writes to a temp file next to the target and renames on commit; temp names use the existing `.rebackup-tmp` suffix and are invisible to `ListAsync`.
- `Stamp` is an opaque change token (filesystem: `"{length}:{mtimeTicks}"`; S3 later: ETag). It replaces `VersionIndex.StampOf`.
- `ListAsync` returns entries in no guaranteed order; callers sort. Directory entries are reported where the storage has real directories; storages without them report implied prefixes as directories.
- `IsLink` is only ever true on storages with `Links`. `FileSystemStorage` reports junctions/symlinks with `IsLink = true` and never descends into them in recursive listings.
- `GetFreeSpaceAsync` returns `null` when unknown (UNC paths, storages without `FreeSpace`); the free-space preflight is then skipped, as today.
- `DeleteAsync` deletes files and empty directories; it never deletes recursively. Missing paths are not an error. The root (`""`) is never deleted: `StorageConflictException`, as for a non-empty directory.
- An address the storage cannot hold throws `ArgumentException` from every member that takes a path: an invalid storage path, and in `FileSystemStorage` also a segment ending in a space or a dot, a character Windows forbids in names (`:` included), or a non-empty path whose full path leaves the root or resolves to the root itself. Containment is checked in `FileSystemStorage`, not in `StoragePath.Validate`, because S3 keys may contain `:`. Callers treat it as an entry they cannot use (a skip), never as an unreachable storage.
- `FileSystemStorage` requires a fully qualified root (`Path.IsPathFullyQualified`); a relative one (`relative`, `C:relative`) throws `ArgumentException` instead of resolving against the process directory.
- `CreateOptions.Durable` flushes the content to stable storage before the commit makes the file visible. Only the manifest is written durably (6.1 step 4); storages whose commit is durable anyway ignore it.

### 5.1 Exceptions

Every implementation maps its native errors to:

| Exception | Meaning | Filesystem source |
|---|---|---|
| `StorageNotFoundException` | path or root missing | FileNotFound, DirectoryNotFound |
| `StorageAccessDeniedException` | permission | UnauthorizedAccess |
| `StorageFullException` | no space | HResult 112, 39 |
| `StorageUnavailableException` | root unreachable (drive/share offline) | root's drive/share missing |
| `StorageConflictException` | exclusive create hit an existing path | IOException on CreateNew |
| `StorageLockedException` | file in use | HResult 32, 33 |
| `StorageIOException` | any other I/O failure (unclassified) | other IOException while the root's drive/share is reachable |

An unclassified `IOException` while the root's drive or share is gone maps to `StorageUnavailableException`.

All derive from `StorageException : IOException`. Callers in Core only catch these types; the existing HResult checks in `BackupRunner`, `VersionCatalog.Probe` and `Restorer` move into `FileSystemStorage`.

### 5.2 StorageLocation

```csharp
public sealed record StorageLocation(string Kind, string Path, string? ConnectionId = null);
// Kind: "fs" only in this sub-project
```

`BackupPlan.Source` and `BackupPlan.Target` become `StorageLocation`. A JSON converter reads the legacy plain string as `{ Kind: "fs", Path: <string> }` and always writes the object form. A `StorageFactory` turns a location into an `IStorage`; in this sub-project it knows only `"fs"`.

`PlanValidator` keeps its current rules for `"fs"` (absolute path, source exists, no overlap of source and target); these checks move behind the location kind so other kinds can supply their own later.

## 6. Version lifecycle (all storage kinds)

File names: `re-manifest.json` (unchanged), new `re-pending.json`, new `re-deleting.json`. Marker content:

```json
{ "formatVersion": 1, "planId": "…", "planName": "…", "startedUtc": "…", "host": "MACHINE" }
```

### 6.1 Backup run

1. **Reserve the name.** Create `<version>/re-pending.json` exclusively. On `StorageConflictException` (or an existing manifest under that name) wait for the next minute, as today.
2. **Copy.** Files are copied with xxh64 hashing as today, each through a `StorageWriter` with `ModifiedUtc` set when the storage supports it. Empty directories are created only on storages with `EmptyDirectories`. A folder or file whose name the target cannot hold (`ArgumentException`, e.g. a WSL name ending in a dot or a space) is skipped with `core.file.cannotOpen`; a folder goes with everything in it, as one entry, and is left out of `Directories`.
3. **Manifest.** `BackupManifest` gains `Directories: List<string>` (all included directories, `/`-separated, relative) so empty directories can be restored from storages without them. `FormatVersion` becomes 2; readers accept 1 and 2.
4. **Commit.** Write and commit `re-manifest.json` (durably), then delete `re-pending.json`. Only now is the version complete. Once the manifest is committed the run is no longer canceled: removing the marker is the shortest way out.
5. Index and retention follow as today; the version just created is never deleted.

On failure or cancellation in the same run, the runner deletes what it wrote (files, then marker last). If that fails, the next run cleans up (6.4).

### 6.2 What counts as a version

`VersionCatalog.ListAsync` lists the direct children of the target root. A child is a version when it contains `re-manifest.json` and neither `re-pending.json` nor `re-deleting.json`, and is not a link. Ownership rules stay as today (manifest `PlanId` matches, folder name matches the manifest's `PlanName`). Transient legacy names (`*.partial`, `*.deleting`) remain excluded, and so are names that are not plain folder names (trailing space or dot): no run writes them, and a storage may not be able to address their content.

### 6.3 Deleting a version

Used by retention, free-space deletion and manual deletion:

1. Write `re-deleting.json` (overwrite allowed). Its `planName` is the plan name the folder is named after, not the plan's current name: a version made before the plan was renamed must stay cleanable by 6.4.
2. Delete `re-manifest.json`. The version is now invisible.
3. Delete all other files in batches, reporting progress per file and honouring cancellation between batches.
4. Delete directories bottom-up (only where the storage has directories).
5. Delete `re-deleting.json`.

A failure after step 1 raises `VersionRemainsException` → `VersionDeletionOutcome.RemainsLeft`, as today. Links are never followed: a link inside a version is deleted as a link.

### 6.4 Crash cleanup (start of the next run of the plan)

| Found under the target root | Action |
|---|---|
| Folder with `re-pending.json` of this plan, not the current run | Delete everything, marker last |
| Folder with `re-deleting.json` of this plan | Finish deletion (6.3 steps 2–5) |
| Legacy `*.partial` without manifest, name matches the plan | Delete as today, via `IStorage` |
| Legacy `*.deleting` that is own remains (rules as today) | Delete as today, via `IStorage` |
| Marker with another plan id, unreadable marker, `.partial` with manifest | Never touched; warning in the run log |
| Empty folder without markers or manifest, named like a version of this plan | Deleted (a deletion whose final folder removal failed) |

Markers are looked for only in folders whose name parses as a version (`VersionName`). A marker of this plan is acted on only when the folder is named after the plan's current name or the marker's `planName`; otherwise the folder was renamed by a person and is left with a warning. This is stricter than ownership by plan id alone, in the safe direction.

### 6.5 Restore

`Restorer` reads from the target `IStorage` and writes to the destination `IStorage` (the source location in Original mode, a local folder in ToFolder mode). Files are written with `CreateOptions(Overwrite: policy == Overwrite, ModifiedUtc: manifest mtime)`; the restorer's own temp-file logic is removed because `StorageWriter` provides it. KeepBoth keeps its naming. Directories from `Manifest.Directories` are created where supported. Link refusal on the destination stays, implemented through `StatAsync(...).IsLink`.

### 6.6 Version index

`VersionIndex` uses the manifest's `Stamp` from `StatAsync` instead of file length + mtime ticks. The fallback for versions without a usable manifest (directory mtime + child count) is replaced by a recursive `ListAsync` with a stamp built from the entry count and the newest `ModifiedUtc`. Existing index databases are rebuilt on first open when their stamp format differs (stamp format version stored in the db).

## 7. Source side

`SourceIndexer` and `LiveScan` take an `IStorage` instead of a root path.
- One `ListAsync(folder, recursive: false)` per folder replaces `EnumerateFileSystemInfos`.
- Link detection uses `IsLink`; linked folders keep `Error = "core.scan.link"`.
- Nested `.backupignore` files are read through `OpenReadAsync`.
- `LiveScan` keeps up to 4 parallel workers, now as async tasks; prioritisation and partial results are unchanged.
- Child sorting uses ordinal-ignore-case unless the storage is `CaseSensitive`.
- `BackupRunner` opens source files with `OpenReadAsync`. `FileSystemStorage` keeps today's share mode (`ReadWrite | Delete`) and `SequentialScan`. A file whose size or mtime changed during the copy is still marked changed, using `StatAsync` after the copy; its copy and manifest entry keep the indexed (pre-copy) mtime, so possibly torn content is never stamped as the newest.

## 8. App

ViewModels switch to `StorageLocation` and the async Core APIs. The UI looks identical: the plan editor still shows a folder text box and a browse button for both source and target, and edits the `Path` of an `"fs"` location. `IFolderOpener` stays local-only and is offered only for `"fs"` locations. "Target unreachable" versus "not created yet" in the versions and retention views is derived from `StorageUnavailableException` versus `StorageNotFoundException`.

## 9. Error handling summary

- Core never catches `System.IO` exceptions from storage calls; only `StorageException` subtypes.
- `RunStatus.Full` ← `StorageFullException`.
- Skip reasons in the run log ← `StorageNotFoundException`, `StorageAccessDeniedException`, `StorageLockedException`, and other `StorageException` when a source file is opened; `ArgumentException` for a name the source or the target cannot hold.
- A read error in the middle of copying a file other than `StorageLockedException` fails the run (`RunStatus.Error`, the unfinished folder is removed); a file that turns out locked while it is read is skipped.
- Ownership probe: `StorageNotFoundException` → NoManifest; other `StorageException` or `JsonException` → Unreadable.

## 10. Implementation order

Branch `refactor/storage-abstraction`. After every step the solution builds and the full test suite is green.

1. Extract `ReBackup.Shared` and `ReBackup.Shared.Wpf` (pure moves plus the decoupling in 4.1). Add the architecture test.
2. Add `ReBackup.Storage` with `FileSystemStorage`, `InMemoryStorage` and `StorageContractTests` (an abstract xUnit test class run against both; S3 and Drive will reuse it).
3. Read paths: `VersionCatalog`, `ManifestReader`, `ManifestStream`, `VersionIndex`, read side of `Restorer`.
4. Write paths: `BackupRunner`, `VersionRemover`, `VersionDeleter`, marker lifecycle, legacy cleanup. Remove `ITargetVolume`.
5. Source side: `SourceIndexer`, `LiveScan`, `.backupignore` reading.
6. Plan model: `StorageLocation`, JSON migration, `PlanValidator`, `StorageFactory`.
7. App: ViewModels on async APIs and `StorageLocation`.

## 11. Testing

- All existing tests (~520) stay and run against `FileSystemStorage` on temp directories. Junction tests stay filesystem-only.
- `ScriptedVolume` and the private `FakeVolume` are replaced by `FaultyStorage`, a decorator over any `IStorage` with hooks for free space, failing create/commit/delete, disk-full writes and callbacks before each call.
- New tests:
  - `StorageContractTests` for both implementations (exclusive create, commit visibility, discard on dispose, delete semantics, listing, stamps, exceptions).
  - Lifecycle on `InMemoryStorage`: crash after each step of 6.1 and 6.3 followed by cleanup; foreign and unreadable markers untouched; name reservation conflict.
  - Manifest format 1 and 2 reading; empty-directory round trip via `Directories`.
  - Plan JSON migration from plain strings.
  - Architecture test for the dependency rules in section 4.
- Manual check at the end: backup, restore and delete through the app on a local drive; a target containing 1.0.5 versions plus leftover `.partial`/`.deleting` folders is listed and cleaned up correctly.
