# Storage Abstraction & Shared Libraries Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route every source/target access in ReBackup through one `IStorage` interface with a marker-based version lifecycle, and extract the reusable code into `ReBackup.Storage`, `ReBackup.Shared` and `ReBackup.Shared.Wpf`, without visible behaviour change.

**Architecture:** Three new libraries under `src/`. Core keeps the backup domain but talks to source and target only through `IStorage` (async, `/`-relative paths, typed `StorageException`s). Versions are committed by writing `re-manifest.json` and removing `re-pending.json`; deletion is guarded by `re-deleting.json`. Atomic rename is no longer used anywhere.

**Tech Stack:** C# / .NET 9, WPF, CommunityToolkit.Mvvm, xUnit 2.9, FluentAssertions 7, Microsoft.Extensions.TimeProvider.Testing.

**Spec:** `docs/superpowers/specs/2026-10-07-storage-abstraction-design.md`

## Global Constraints

- Branch `refactor/storage-abstraction`. After **every task**: `dotnet build ReBackup.sln` has no errors and `dotnet test ReBackup.sln` is fully green.
- No visible UI change. Both locales (en-US, de-DE) stay complete.
- `ReBackup.Storage` → BCL only. `ReBackup.Shared` → BCL + System.Text.Json. `ReBackup.Shared.Wpf` → `ReBackup.Shared` + WPF. None of them references `ReBackup.Core` or `ReBackup.App`.
- Storage paths: relative to the storage root, `/`-separated, no leading `/`, `""` = root.
- Marker file names: `re-manifest.json` (unchanged), `re-pending.json`, `re-deleting.json`. Marker JSON: `{ "formatVersion": 1, "planId", "planName", "startedUtc", "host" }`.
- Manifest `FormatVersion` becomes 2 with `Directories: List<string>`; readers accept 1 and 2.
- Temp suffix for uncommitted files: `.rebackup-tmp` (existing constant).
- Core catches only `StorageException` subtypes from storage calls, never raw `System.IO` exceptions.
- Commit style: conventional (`refactor(core):`, `feat(storage):`, `test:`), ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Existing tests keep their intent; they are adapted to new signatures (async, `IStorage`), not deleted. A test may only be removed when the behaviour it pins is explicitly replaced by the spec (e.g. "rename to `.deleting`"), and its replacement test is added in the same task.

## Review Focus

1. **Upgrading a real 1.0.5 target** (versions without markers, leftover `*.partial` / `*.deleting`): all old versions still listed as owned, leftovers cleaned by the old rules → test in Task 7.
2. **Cancelling or crashing a backup mid-copy**: the half version never appears in the versions list or retention, and the next run removes it → tests in Tasks 7 and 9.
3. **Unplugged drive vs. not-yet-created target folder**: versions/retention views show "unreachable" vs. "not created yet", and nothing is deleted while unreachable → test in Task 4 (`StorageUnavailableException` mapping) and Task 11.
4. **Long and non-ASCII paths** (> 260 chars, umlauts, spaces) through the relative-path mapping of `FileSystemStorage` → test in Task 4.
5. **Version index after upgrade** rebuilds exactly once (stamp format changed), not on every open → test in Task 6.

---

## File Structure

```
src/ReBackup.Storage/                 (new, net9.0)
  IStorage.cs                         IStorage, StorageCapabilities, StorageEntry, CreateOptions
  StorageWriter.cs                    abstract write stream with CommitAsync
  StorageExceptions.cs                StorageException + 6 subtypes
  StoragePath.cs                      Combine/Parent/Name/Validate for '/'-paths
  StorageLocation.cs                  record + StorageLocationJsonConverter
  IStorageFactory.cs                  IStorageFactory, StorageFactory ("fs" only)
  FileSystem/FileSystemStorage.cs     + FileSystemErrors.cs (HResult → StorageException)
  InMemory/InMemoryStorage.cs
src/ReBackup.Shared/                  (new, net9.0) moved: Retention/ (minus Planner), Schedule/, IO/, Json/,
                                      Localization/ (mechanism + SharedTexts), Settings/ThemeMode.cs,
                                      Settings/AppLanguages.cs, Indexing/TreemapLayout.cs, Indexing/IPreviewEntry.cs,
                                      Locales/shared.en-US.json, Locales/shared.de-DE.json
src/ReBackup.Shared.Wpf/              (new, net9.0-windows) moved: Theme/*, Controls/*, Localization/*,
                                      Services/ThemeManager.cs, Services/SingleInstance.cs,
                                      Services/StartupRegistration.cs, Services/DarkTitleBar.cs,
                                      Locales/wpf.en-US.json, Locales/wpf.de-DE.json
src/ReBackup.Core/Backup/
  VersionMarkers.cs                   (new) MarkerInfo, read/write pending & deleting markers
  LeftoverCleaner.cs                  (new) crash cleanup, spec 6.4
  TargetVolume.cs                     (deleted in Task 9)
tests/ReBackup.Storage.Tests/         (new) StorageContractTests (abstract, public), InMemoryStorageTests,
                                      FileSystemStorageTests
tests/ReBackup.Core.Tests/
  Architecture/ProjectReferenceTests.cs (new)
  TestSupport/FaultyStorage.cs        (new, replaces ScriptedVolume + BackupRunnerTests.FakeVolume)
```

Namespaces follow folders: `ReBackup.Storage`, `ReBackup.Storage.FileSystem`, `ReBackup.Shared.Retention`, `ReBackup.Shared.Schedule`, `ReBackup.Shared.IO`, `ReBackup.Shared.Json`, `ReBackup.Shared.Localization`, `ReBackup.Shared.Settings`, `ReBackup.Shared.Indexing`, `ReBackup.Shared.Wpf.Theme`, `ReBackup.Shared.Wpf.Controls`, `ReBackup.Shared.Wpf.Localization`, `ReBackup.Shared.Wpf.Services`.

---

### Task 1: Extract `ReBackup.Shared`

**Files:**
- Create: `src/ReBackup.Shared/ReBackup.Shared.csproj` (net9.0, no package refs), `src/ReBackup.Shared/Localization/SharedTexts.cs`, `src/ReBackup.Shared/Locales/shared.en-US.json`, `shared.de-DE.json`
- Move (git mv, then fix namespace): `Core/Retention/{RetentionEngine,RetentionRule,RetentionRules,RetentionSimulator}.cs` → `Shared/Retention/`; `Core/Schedule/*` → `Shared/Schedule/`; `Core/IO/{AtomicFile,ByteSize,PathUtil}.cs` → `Shared/IO/`; `Core/Json/JsonDefaults.cs` → `Shared/Json/`; `Core/Localization/{LabelFormat,Labels,LabelSet,Message}.cs` → `Shared/Localization/`; `Core/Settings/{ThemeMode,AppLanguages}.cs` → `Shared/Settings/`; `Core/Indexing/TreemapLayout.cs` and `IPreviewEntry` → `Shared/Indexing/`
- Move `RunTrigger` enum out of `Core/Backup/RunLog.cs` into `Shared/Schedule/RunTrigger.cs`
- Modify: `Core/Localization/CoreTexts.cs` (remove `core.retention.*`, `core.trigger.*`), App `Locales/en-US.json`/`de-DE.json` (remove the same keys), `ReBackup.sln`, Core/App/Tests csproj (add ProjectReference), usings everywhere
- Create test: `tests/ReBackup.Core.Tests/Architecture/ProjectReferenceTests.cs`, extend `Localization/LocaleFileTests.cs`
- Modify `tests/.../TestSupport/RepoPaths.cs`: add `SharedDirectory`, `SharedWpfDirectory`, `StorageDirectory`

**Interfaces:**
- Produces: `ReBackup.Shared.Localization.SharedTexts` with the same public surface as `CoreTexts`: `Templates`, `English(string key, params (string, object?)[] args)`, `Recognize(string text) → Message?`. Keys renamed `core.retention.X` → `shared.retention.X`, `core.trigger.X` → `shared.trigger.X`, English texts unchanged.
- `RetentionPlanner` stays in `ReBackup.Core.Retention` and uses `ReBackup.Shared.Retention.RetentionEngine`.

- [ ] **Step 1: Write the failing architecture test**

```csharp
public class ProjectReferenceTests
{
    [Theory]
    [InlineData("ReBackup.Shared", new string[0])]
    [InlineData("ReBackup.Shared.Wpf", new[] { "ReBackup.Shared" })]
    [InlineData("ReBackup.Storage", new string[0])]
    public void Library_references_only_what_is_allowed(string project, string[] allowed)
    {
        ProjectReferencesOf(project).Should().BeSubsetOf(allowed);
    }

    [Theory]
    [InlineData("ReBackup.Shared")] [InlineData("ReBackup.Storage")]
    public void Library_has_no_package_references(string project) =>
        PackageReferencesOf(project).Should().BeEmpty();
    // helpers parse src/<project>/<project>.csproj with XDocument; a missing csproj fails the test
}
```
Only the `ReBackup.Shared` rows are expected to pass after this task; mark the `Shared.Wpf` / `Storage` rows `Skip = "Task 2"` / `Skip = "Task 3"` and remove the skips in those tasks.

- [ ] **Step 2: Run** `dotnet test --filter ProjectReferenceTests` → FAIL (csproj missing).
- [ ] **Step 3: Create the project, move the files, fix namespaces and usings.** `RetentionRules`/`ScheduleTriggers`/`ScheduleCalculator` switch from `CoreTexts` to `SharedTexts`. `Shared.csproj` embeds `Locales/*.json` with `LogicalName="ReBackup.Shared.Locales.%(Filename)%(Extension)"`. `CoreTexts` and `SharedTexts` stay independent; until Task 2, `Loc.Known` calls `CoreTexts.Recognize(text) ?? SharedTexts.Recognize(text)`.
- [ ] **Step 4: Extend `LocaleFileTests`** with the same checks (one file per language, same keys, same placeholders, same plurals) for `src/ReBackup.Shared/Locales/shared.*.json`, plus:

```csharp
[Fact]
public void Shared_english_file_holds_exactly_the_shared_texts() =>
    LoadShared(AppLanguages.English).Entries.Should().Equal(SharedTexts.Templates);
```
and adapt the existing CoreTexts-vs-en-US test to the reduced key set.
- [ ] **Step 5: Run** `dotnet build ReBackup.sln` and `dotnet test ReBackup.sln` → all green.
- [ ] **Step 6: Commit** `refactor(shared): extract ReBackup.Shared`

---

### Task 2: Extract `ReBackup.Shared.Wpf`

**Files:**
- Create: `src/ReBackup.Shared.Wpf/ReBackup.Shared.Wpf.csproj` (net9.0-windows, `UseWPF`, ref `ReBackup.Shared`), `Locales/wpf.en-US.json`, `wpf.de-DE.json`
- Move: App `Theme/*` → `Shared.Wpf/Theme/`; App `Controls/*` → `Shared.Wpf/Controls/`; `TimelineLane` (from App ViewModels) → `Shared.Wpf/Controls/TimelineLane.cs`; App `Localization/*` → `Shared.Wpf/Localization/`; App `Services/{ThemeManager,SingleInstance,StartupRegistration,DarkTitleBar}.cs` → `Shared.Wpf/Services/`
- Modify: `App.xaml` (merged dictionaries with `pack://application:,,,/ReBackup.Shared.Wpf;component/Theme/...`), all XAML `xmlns` for controls/converters/`l:` markup, `App.xaml.cs` startup, App csproj, `LocaleFileTests`

**Interfaces:**
- Produces:
  - `Loc.Configure(IReadOnlyList<LabelSource> sources, IReadOnlyList<Func<string, Message?>> recognizers)`, called once in `App.OnStartup` before `Apply`. `public sealed record LabelSource(Assembly Assembly, string ResourcePrefix);` Labels of later sources override earlier ones on equal keys. `Loc.Known(text)` tries the recognizers in order.
  - ReBackup registers sources `[Shared ("ReBackup.Shared.Locales.shared."), Shared.Wpf ("ReBackup.Shared.Wpf.Locales.wpf."), App ("ReBackup.App.Locales.")]` and recognizers `[CoreTexts.Recognize, SharedTexts.Recognize]`.
  - `ThemeManager` loads from `/ReBackup.Shared.Wpf;component/Theme/{file}`.
  - `SingleInstance.TryAcquire(string appId, TimeSpan waitForPrevious)` → mutex `Local\{appId}.SingleInstance`, event `Local\{appId}.Activate`. `StartupRegistration.Apply(string valueName, bool enabled)`. ReBackup passes `"ReBackup"` (same names as today).
- Label keys used by moved controls (Treemap, RetentionTimeline, Formats) move from App locales to `wpf.*.json` **without renaming** (they keep their current keys; only the file changes).

- [ ] **Step 1:** Remove `Skip` from the `ReBackup.Shared.Wpf` row in `ProjectReferenceTests`; add `LocaleFileTests` checks for `wpf.*.json` (same four checks) plus a test that no key appears in more than one of the three English files.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Create the project and move the files**; fix namespaces, XAML xmlns and pack URIs; add `Loc.Configure`; parameterize `SingleInstance` / `StartupRegistration`; move control label keys into `wpf.*.json`.
- [ ] **Step 4: Run** build + tests → green.
- [ ] **Step 5: Manual smoke:** start the app, switch Dark/Light and en/de in Settings, open Ignore (treemap) and Retention (timeline) tabs; every text is translated (no raw keys), theme switches, second start activates the first window.
- [ ] **Step 6: Commit** `refactor(shared): extract ReBackup.Shared.Wpf`

---

### Task 3: `ReBackup.Storage` contract and `InMemoryStorage`

**Files:**
- Create: `src/ReBackup.Storage/ReBackup.Storage.csproj` (net9.0), `IStorage.cs`, `StorageWriter.cs`, `StorageExceptions.cs`, `StoragePath.cs`, `InMemory/InMemoryStorage.cs`
- Create: `tests/ReBackup.Storage.Tests/ReBackup.Storage.Tests.csproj` (same packages as Core.Tests), `StorageContractTests.cs`, `InMemoryStorageTests.cs`; add to sln

**Interfaces (Produces):** exactly the spec section 5 types, plus:
```csharp
public abstract class StorageException : IOException { protected StorageException(string path, string message, Exception? inner); public string Path { get; } }
public sealed class StorageNotFoundException, StorageAccessDeniedException, StorageFullException,
                    StorageUnavailableException, StorageConflictException, StorageLockedException : StorageException
public static class StoragePath
{
    public static string Combine(params string[] parts);   // skips "", joins with '/'
    public static string Parent(string path);               // "" for top level
    public static string Name(string path);
    public static string Validate(string path);             // throws ArgumentException on '\', leading '/', "..", "." segments, empty segments
}
public sealed class InMemoryStorage : IStorage
{
    public InMemoryStorage(StorageCapabilities capabilities = StorageCapabilities.EmptyDirectories | StorageCapabilities.SetModifiedTime,
                           TimeProvider? time = null);
    public long? FreeSpace { get; set; }                   // null → GetFreeSpaceAsync returns null
    public IReadOnlyCollection<string> Files { get; }     // committed file paths, for assertions
    public byte[] ReadAllBytes(string path);
    public void AddFile(string path, byte[] content, DateTime? modifiedUtc = null);   // test arrangement
}
public abstract class StorageContractTests   // public: reused by later providers
{
    protected abstract IStorage CreateEmpty();   // each test gets a fresh, empty storage
}
```
Without `EmptyDirectories`, `InMemoryStorage` derives directories from file paths (implied prefixes) and `EnsureDirectoryAsync` is a no-op. `Stamp` = `"{length}:{modifiedTicks}"`.

- [ ] **Step 1: Write `StorageContractTests`** (all `[Fact] async Task`):
  - `Created_file_is_invisible_until_committed` — after `CreateAsync` + write, `StatAsync` is null and `ListAsync` empty; after `CommitAsync` both see it with the written size.
  - `Disposing_without_commit_discards_the_file`
  - `Exclusive_create_of_an_existing_file_throws_conflict` → `StorageConflictException`
  - `Overwrite_replaces_content_on_commit_and_keeps_old_content_until_then`
  - `Modified_time_is_kept_when_supported` — `CreateOptions(ModifiedUtc: 2026-01-02T03:04:05Z)` → `StatAsync(...).ModifiedUtc` equals it when `SetModifiedTime` is set.
  - `List_non_recursive_returns_direct_children_with_directories`
  - `List_recursive_returns_all_files_with_relative_paths` — paths like `"a/b/c.txt"`.
  - `Open_read_of_missing_file_throws_not_found`
  - `Delete_removes_files_and_ignores_missing_paths`
  - `Delete_of_non_empty_directory_throws` (`IOException` subtype `StorageException`)
  - `Stamp_changes_when_content_changes`
  - `Invalid_paths_are_rejected` — `"a\\b"`, `"/a"`, `"a/../b"` → `ArgumentException`.
- [ ] **Step 2:** `InMemoryStorageTests : StorageContractTests` with `CreateEmpty() => new InMemoryStorage()`, plus one class variant without `EmptyDirectories` (`InMemoryPrefixStorageTests`) to pin implied-directory listing.
- [ ] **Step 3: Run** → FAIL (types missing).
- [ ] **Step 4: Implement** the types and `InMemoryStorage` (thread-safe via one lock; `ConcurrentDictionary` not required).
- [ ] **Step 5:** Remove `Skip` on the `ReBackup.Storage` rows of `ProjectReferenceTests`. **Run** all tests → green.
- [ ] **Step 6: Commit** `feat(storage): storage contract and in-memory storage`

---

### Task 4: `FileSystemStorage`

**Files:**
- Create: `src/ReBackup.Storage/FileSystem/FileSystemStorage.cs`, `FileSystem/FileSystemErrors.cs`
- Create: `tests/ReBackup.Storage.Tests/FileSystemStorageTests.cs` (+ copy of `TempDir`/`Junction` helpers into this test project's `TestSupport/`)

**Interfaces (Produces):**
```csharp
public sealed class FileSystemStorage : IStorage
{
    public FileSystemStorage(string rootPath);   // absolute; does not need to exist yet
    public string RootPath { get; }
    public string FullPathOf(string relativePath); // for IFolderOpener and messages only
}
internal static class FileSystemErrors { public static StorageException? Map(Exception ex, string path, string rootPath); }
```
Capabilities: `FreeSpace | Links | EmptyDirectories | SetModifiedTime` (not `CaseSensitive`). Read: `FileShare.ReadWrite | FileShare.Delete`, 1 MiB buffer, `SequentialScan`. Write: temp `"{target}.{random:x8}.rebackup-tmp"` with `FileMode.CreateNew`; commit sets mtime then `File.Move(temp, target, overwrite)`; exclusive commit uses `File.Move(..., overwrite:false)` and maps "exists" to `StorageConflictException`; the exclusive check also happens at `CreateAsync`. `ListAsync` skips `*.rebackup-tmp`, reports links with `IsLink = true` and never descends into them. Free space: current `PhysicalTargetVolume` logic, but `null` instead of `long.MaxValue`. `StorageUnavailableException`: the root's drive/share root does not exist (the logic of `VersionsViewModel.TargetStateOf`). `StorageNotFoundException` when the root exists but the path does not. HResult mapping moved verbatim from `BackupRunner` (112/39 → Full, 32/33 → Locked).

- [ ] **Step 1: Write tests:** `FileSystemStorageTests : StorageContractTests` (temp dir root) plus:
  - `Junction_is_listed_as_link_and_not_descended` (uses `Junction` helper)
  - `Temp_files_are_not_listed`
  - `Missing_drive_root_throws_unavailable` — root `"Q:\\nope"` when `Q:` does not exist (skip if it does) → `StorageUnavailableException` from `ListAsync("")`.
  - `Missing_folder_under_existing_drive_throws_not_found`
  - `Long_and_non_ascii_paths_round_trip` — relative path of 300 chars with `"Ünïcode ordner/ä ö ü.txt"` segments: create, commit, list, read back equal bytes.
  - `Free_space_is_null_for_unc_roots` — root `@"\\server\share\x"`.
  - `Source_file_open_for_writing_by_another_process_can_still_be_read` — open a `FileStream` with `FileShare.ReadWrite`, then `OpenReadAsync` succeeds.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → green.
- [ ] **Step 5: Commit** `feat(storage): file system storage`

---

### Task 5: `StorageLocation` in the plan model

**Files:**
- Create: `src/ReBackup.Storage/StorageLocation.cs`, `src/ReBackup.Storage/IStorageFactory.cs`
- Modify: `Core/Plans/BackupPlan.cs:13-14`, `Core/Plans/PlanValidator.cs`, `App/ViewModels/PlanEditorViewModel.cs` (`ToPlan`, `LoadFrom`), any other reader of `plan.Source`/`plan.Target` (compile errors guide you; call `.Path` where a local path is still needed — those call sites are converted in Tasks 6–11)
- Test: `tests/ReBackup.Core.Tests/Plans/PlanStoreTests.cs`, `PlanValidatorTests.cs`, `tests/ReBackup.Storage.Tests/StorageLocationTests.cs`

**Interfaces (Produces):**
```csharp
[JsonConverter(typeof(StorageLocationJsonConverter))]
public sealed record StorageLocation(string Kind, string Path, string? ConnectionId = null)
{
    public const string FileSystemKind = "fs";
    public static StorageLocation FileSystem(string path);
    public bool IsFileSystem { get; }
}
public interface IStorageFactory { IStorage Open(StorageLocation location); }   // NotSupportedException for unknown kinds
public sealed class StorageFactory : IStorageFactory { }                        // "fs" → new FileSystemStorage(location.Path)
```
`BackupPlan.Source` / `Target` become `StorageLocation` (default `StorageLocation.FileSystem("")`). `PlanEditorViewModel` keeps its string properties `Source`/`Target` and maps via `StorageLocation.FileSystem(...)` / `.Path`. `PlanValidator` runs today's path checks only when `IsFileSystem`; for other kinds it returns a `core.plan.unsupportedLocation` error (new key in CoreTexts + both App locales: en "This kind of location is not supported yet.", de "Diese Art von Speicherort wird noch nicht unterstützt.").

- [ ] **Step 1: Tests:**
  - `Legacy_string_location_is_read_as_file_system` — JSON `"source": "D:\\Data"` → `StorageLocation("fs", "D:\\Data")`.
  - `Location_is_written_as_object` — `{"kind":"fs","path":"D:\\Data"}` (connectionId omitted when null).
  - `Legacy_plan_round_trip_keeps_extra_sections` — a 1.0.5 plan file with an unknown section survives load → save.
  - `Unknown_kind_is_rejected_by_validator`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** all → green.
- [ ] **Step 5: Commit** `refactor(core): storage locations in plans`

---

### Task 6: Read paths — catalog, manifest, index

**Files:**
- Modify: `Core/Backup/VersionCatalog.cs`, `Core/Backup/ManifestReader.cs`, `Core/Versions/ManifestStream.cs`, `Core/Backup/BackupManifest.cs`, `Core/Versions/VersionIndex.cs`, `VersionIndexSet.cs`, `VersionIndexWorker.cs`, `Core/Backup/VersionName.cs` (`FolderNamesIn`, `ExistingFolderIn`), App call sites (`VersionsViewModel*.cs`, `RetentionPreviewViewModel.cs`, `PlanRunViewModel.cs`)
- Test: `VersionCatalogTests.cs`, `ManifestReaderTests.cs`, `ManifestStreamTests.cs`, `VersionIndexTests.cs`, `VersionIndexQueryTests.cs`, `TestSupport/VersionFolder.cs`, `VersionBuilder.cs`

**Interfaces:**
- Consumes: `IStorage`, `StorageEntry`, exceptions (Task 3/4).
- Produces:
```csharp
public static class VersionMarkerNames { public const string Manifest = "re-manifest.json", Pending = "re-pending.json", Deleting = "re-deleting.json"; }  // in VersionName.cs
public sealed record VersionInfo(string Name, string Path, DateTime LocalTime, VersionOwnership Ownership, int? FileCount, long? TotalBytes);
    // Path is now the storage-relative folder path (== Name for direct children)
public static class VersionCatalog
{
    public static Task<IReadOnlyList<VersionInfo>> ListAsync(IStorage target, string planId, string planName, CancellationToken ct = default);
        // StorageNotFoundException on the root → empty list; StorageUnavailableException propagates
    public static Task<(VersionOwnership Ownership, ManifestHeader? Header)> ProbeAsync(IStorage target, string versionPath, string planId, CancellationToken ct = default);
}
public static class ManifestReader
{
    public static Task<ManifestHeader> ReadHeaderAsync(IStorage storage, string manifestPath, CancellationToken ct = default);   // opens twice when the 64 KiB window is not enough
    public static Task<(int FileCount, long TotalBytes)> ReadTotalsAsync(IStorage storage, string manifestPath, CancellationToken ct = default);
}
// ManifestStream.Read(Stream, ...) stays; the path overload is removed.
// BackupManifest: FormatVersion = 2, List<string> Directories (empty for format 1).
public IndexSyncResult VersionIndex.Sync(IReadOnlyList<VersionInfo> folders, IStorage target, IProgress<IndexSyncProgress>? progress = null, CancellationToken ct = default);
public static Task<string> VersionIndex.StampOfAsync(IStorage target, string versionPath, CancellationToken ct);
    // manifest present: "m:" + manifest StorageEntry.Stamp; otherwise "s:{count}:{newestModifiedTicks}" from a recursive ListAsync
```
`VersionIndex` stores `stamp_format = 2` in its db meta table; on `Open`, a db with a missing/other value is wiped and rebuilt. Sync stays synchronous inside the worker; it calls the async storage methods with `.GetAwaiter().GetResult()` on the worker thread (the worker is never the UI thread). A child with `re-pending.json` or `re-deleting.json` is not a version (spec 6.2). `VersionInfo.LocalTime` fallback (no parsable name) uses the manifest's `ModifiedUtc` instead of the directory mtime.

- [ ] **Step 1: Adapt existing tests** to async + `FileSystemStorage(target)`; add:
  - `Folder_with_pending_marker_is_not_a_version`, `Folder_with_deleting_marker_is_not_a_version` (on `InMemoryStorage`)
  - `Catalog_of_missing_root_is_empty`, `Catalog_of_unavailable_root_throws_unavailable`
  - `Format_1_manifest_reads_with_empty_directories`, `Format_2_manifest_round_trips_directories`
  - `Index_with_old_stamp_format_is_rebuilt_once` — open old db → rebuilt; close; open again → not rebuilt (assert via a counter/the sync result's `Added` count = 0 on second sync).
  - `Versions_from_1_0_5_are_listed_as_owned` — `VersionBuilder` writes a format-1 manifest without markers.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** and update App call sites (`await`, `FileSystemStorage` via `StorageFactory.Open(plan.Target)`). **Step 4: Run** all → green.
- [ ] **Step 5: Commit** `refactor(core): read versions through IStorage`

---

### Task 7: Marker lifecycle — markers, remover, deleter, crash cleanup

**Files:**
- Create: `Core/Backup/VersionMarkers.cs`, `Core/Backup/LeftoverCleaner.cs`, `tests/.../TestSupport/FaultyStorage.cs`
- Modify: `Core/Backup/VersionRemover.cs`, `Core/Backup/VersionDeleter.cs`, `App/ViewModels/VersionsViewModel.Delete.cs`
- Test: `VersionRemoverTests.cs`, `VersionDeleterTests.cs`, new `VersionMarkersTests.cs`, `LeftoverCleanerTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed record MarkerInfo(int FormatVersion, string PlanId, string PlanName, DateTime StartedUtc, string Host);
public static class VersionMarkers
{
    public static Task WritePendingAsync(IStorage target, string versionPath, MarkerInfo marker, CancellationToken ct); // exclusive → StorageConflictException
    public static Task WriteDeletingAsync(IStorage target, string versionPath, MarkerInfo marker, CancellationToken ct); // overwrite
    public static Task<MarkerInfo?> TryReadAsync(IStorage target, string markerPath, CancellationToken ct);             // null when missing or unreadable JSON
}
public static class VersionRemover
{
    public static Task RemoveAsync(IStorage target, string versionPath, MarkerInfo marker, Action<int>? onFileDeleted = null, CancellationToken ct = default);
        // spec 6.3 steps 1–5; failure after step 1 → VersionRemainsException(versionPath, inner)
    public static Task FinishRemovalAsync(IStorage target, string versionPath, Action<int>? onFileDeleted = null, CancellationToken ct = default);   // steps 2–5
    public static Task RemoveLegacyFolderAsync(IStorage target, string folderPath, Action<int>? onFileDeleted = null, CancellationToken ct = default);
        // old .partial / .deleting folders: files first, manifest last, then directories bottom-up; links deleted as links
}
public static class VersionDeleter
{
    public static Task<IReadOnlyList<VersionDeletion>> DeleteAsync(IStorage target, string planId, string planName,
        IReadOnlyList<string> versionNames, IProgress<VersionDeletionProgress>? progress = null, CancellationToken ct = default);
}
public static class LeftoverCleaner
{
    public static Task<IReadOnlyList<string>> CleanAsync(IStorage target, string planId, string planName,
        string? currentVersionName, CancellationToken ct);   // returns warnings (English, for the run log); spec 6.4 table
}
public sealed class FaultyStorage : IStorage   // decorator for tests
{
    public FaultyStorage(IStorage inner);
    public Func<long?>? FreeSpace { get; init; }
    public Func<string, bool> FailCreate { get; init; }   // throws StorageAccessDeniedException
    public Func<string, bool> FailCommit { get; init; }
    public Func<string, bool> FailDelete { get; init; }
    public Func<string, bool> DiskFullOnWrite { get; init; }  // writer throws StorageFullException
    public Action<string, string>? Before { get; init; }      // (operation, path) before every call
    public IReadOnlyList<string> Created { get; }
}
```
Batches in `RemoveAsync`: 1000 paths per `DeleteAsync` call; `onFileDeleted(n)` after each batch with the batch size; cancellation checked between batches.

- [ ] **Step 1: Tests** (on `InMemoryStorage` unless stated):
  - `Remove_writes_deleting_marker_then_hides_version_before_deleting_files` — `FaultyStorage.Before` records order: deleting marker write, manifest delete, file batches, marker delete.
  - `Failure_after_marker_leaves_remains_and_throws_VersionRemainsException`
  - `Cleanup_removes_pending_folder_of_this_plan` / `Cleanup_keeps_pending_folder_of_current_run` / `Cleanup_finishes_deleting_folder_of_this_plan`
  - `Cleanup_never_touches_foreign_or_unreadable_markers_and_warns`
  - `Cleanup_handles_legacy_partial_and_deleting_folders_as_before` (on `FileSystemStorage`, folders built like 1.0.5 incl. a `.partial` **with** manifest that stays) — Review Focus 1.
  - `Deleter_reports_deleted_gone_notManaged_failed` (port of existing cases).
  - Port existing `VersionRemoverTests` intents; replace "rename to .deleting" assertions with marker assertions. Junction test: a link inside a version is deleted as a link, its target untouched (`FileSystemStorage`).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement**; update `VersionsViewModel.Delete.cs` to `await VersionDeleter.DeleteAsync(...)`. `BackupRunner` keeps compiling by calling the new async methods (its own flow is converted in Task 9). **Step 4: Run** all → green.
- [ ] **Step 5: Commit** `feat(core): marker-based version removal and crash cleanup`

---

### Task 8: Source side — indexer, live scan, ignore files

**Files:**
- Modify: `Core/Indexing/SourceIndexer.cs`, `Core/Indexing/LiveScan.cs`, `App/ViewModels/IgnorePreviewViewModel.cs`
- Test: `SourceIndexerTests.cs`, `LiveScanTests.cs`

**Interfaces (Produces):**
```csharp
public static Task<SourceIndex> SourceIndexer.BuildAsync(IStorage source, IProgress<IndexProgress>? progress = null, CancellationToken ct = default);   // sync Build removed
public static LiveScan LiveScan.Start(IStorage source, string rootName, IgnoreSettings settings, IReadOnlyList<string> globalDefaults, LiveScanOptions? options = null, CancellationToken ct = default);
// SourceIndex.Root becomes the root display name; StorageNotFoundException replaces DirectoryNotFoundException for a missing root.
```
Children sort `OrdinalIgnoreCase` unless `source.Capabilities` has `CaseSensitive` (then `Ordinal`). Link folders: `Error = "core.scan.link"`, not entered. `.backupignore` read via `OpenReadAsync` + `StreamReader`; unreadable → `UnreadableIgnoreFiles` as today. LiveScan workers become `Task`s (still `MaxParallel = 4`), `Prioritize` and partial-tree reads unchanged.

- [ ] **Step 1:** Adapt existing tests to `FileSystemStorage`; add `Indexes_in_memory_source_with_nested_ignore_file` and `Case_sensitive_storage_sorts_ordinal` (both `InMemoryStorage`).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement**; App preview opens the source via `StorageFactory`. **Step 4: Run** all → green. **Step 5: Manual:** Ignore tab of a real plan fills live, treemap shows.
- [ ] **Step 6: Commit** `refactor(core): scan sources through IStorage`

---

### Task 9: `BackupRunner` on `IStorage`

**Files:**
- Modify: `Core/Backup/BackupRunner.cs`, `App/App.xaml.cs:136`
- Delete: `Core/Backup/TargetVolume.cs`, `tests/.../TestSupport/ScriptedVolume.cs`, private `FakeVolume`/`DiskFullStream` in `BackupRunnerTests.cs`
- Test: `BackupRunnerTests.cs`, `BackupRunnerRetentionTests.cs`, `BackupRunnerIndexTests.cs`, new `BackupRunnerLifecycleTests.cs`

**Interfaces:**
- Consumes: `IStorageFactory`, `VersionMarkers`, `VersionRemover`, `LeftoverCleaner`, `SourceIndexer.BuildAsync`, `VersionCatalog.ListAsync`.
- Produces: `public BackupRunner(IStorageFactory? storages = null, TimeProvider? timeProvider = null, Func<string, IReadOnlyList<RetentionRule>?>? currentRules = null, IVersionIndexSink? indexSink = null, string? host = null)` (`storages` defaults to `new StorageFactory()`, `host` to `Environment.MachineName`). `RunAsync` signature unchanged.

Flow (spec 6.1): Prepare (fs-only overlap/exists checks stay for `IsFileSystem`) → `LeftoverCleaner.CleanAsync` (warnings into the run log) → index source → free-space preflight only when `GetFreeSpaceAsync` is non-null → reserve name via `WritePendingAsync` (conflict or existing manifest → next minute) → `EnsureDirectoryAsync` for all included directories when `EmptyDirectories` → copy each file via `OpenReadAsync` + `CreateAsync(..., new CreateOptions(ModifiedUtc: srcMtime))` + xxh64 + `CommitAsync` → re-`StatAsync` source for changed-during-copy → write manifest v2 (with `Directories`) + commit → delete pending marker → index + retention (`VersionRemover.RemoveAsync`). On failure/cancel: delete written files, then marker last; swallow and warn on cleanup failure. `StorageFullException` → `RunStatus.Full`; `StorageLockedException`/`NotFound`/`AccessDenied`/other `StorageException` on a source file → skip reasons as today. Source root gone after a failed open (`StorageNotFoundException` from `StatAsync("")`) → abort.

- [ ] **Step 1: Port** every `FakeVolume`/`ScriptedVolume` test to `FaultyStorage` over `FileSystemStorage` (same assertions; "move fails" cases become "commit of pending-marker removal fails"). Add `BackupRunnerLifecycleTests` (target = `InMemoryStorage` via a test `IStorageFactory` mapping kinds `"mem-src"`/`"mem-tgt"`):
  - `Version_becomes_visible_only_after_pending_marker_is_removed` — `Before` hook lists the catalog right before the marker delete → version absent; after run → present.
  - `Cancel_during_copy_leaves_no_files_and_no_marker` — Review Focus 2.
  - `Crash_after_manifest_before_marker_delete_is_cleaned_next_run` — simulate by `FailDelete` on the pending marker, then run again without faults: old folder removed, warning logged.
  - `Empty_source_directory_is_restorable_from_storage_without_directories` — manifest `Directories` contains it.
  - `Disk_full_reports_full_status` (via `DiskFullOnWrite`).
  - `Free_space_preflight_is_skipped_when_unknown`.
  - `Name_reservation_waits_for_next_minute_on_conflict` (`FakeTimeProvider`).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement**; delete `TargetVolume.cs`. **Step 4: Run** all → green.
- [ ] **Step 5: Commit** `refactor(core): run backups through IStorage with marker commit`

---

### Task 10: `Restorer` on `IStorage`

**Files:**
- Modify: `Core/Versions/Restorer.cs`, `App/ViewModels/VersionsViewModel.Restore.cs`
- Test: `RestorerTests.cs`

**Interfaces (Produces):**
```csharp
public static Task<RestorePlan> Restorer.PlanAsync(IStorage versions, string versionPath, IReadOnlyList<string> relativePaths,
    IStorage destination, RestoreMode mode, CancellationToken ct = default);
public static Task<RestoreResult> Restorer.RunAsync(RestorePlan plan, ConflictPolicy policy, IProgress<RestoreProgress>? progress = null, CancellationToken ct = default);
// RestorePlan keeps its shape; VersionFolder/DestinationRoot become (IStorage, string) pairs: Versions, VersionPath, Destination.
```
Files are written with `CreateOptions(Overwrite: policy == Overwrite, ModifiedUtc: manifest MtimeUtc)`; the restorer's own temp-file code and `.rebackup-tmp` handling are removed (KeepBoth naming stays, existence via `StatAsync`). Directories from `manifest.Directories` (or from file parents for format 1) are created via `EnsureDirectoryAsync`. Link refusal on destination: any existing path component with `IsLink` → plan failure, as today. Version time: folder name, else manifest `CreatedUtc`.

- [ ] **Step 1:** Adapt existing tests; add `Restores_empty_directories_from_manifest` and `Restore_from_in_memory_versions_to_file_system`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** all → green.
- [ ] **Step 5: Commit** `refactor(core): restore through IStorage`

---

### Task 11: App reachability states and final cleanup

**Files:**
- Modify: `App/ViewModels/VersionsViewModel.cs:245-262` (`TargetStateOf`), `RetentionPreviewViewModel.cs:218`, `App/Services/IFolderOpener.cs`, `PlanEditorViewModel.cs:95-102`
- Test: `tests/.../Versions/TargetStateTests.cs` (new, tests a Core helper)

**Interfaces (Produces):**
```csharp
public enum TargetState { Present, NotCreatedYet, Unreachable }          // moved to Core/Versions/TargetState.cs
public static Task<TargetState> TargetStates.ProbeAsync(IStorage target, CancellationToken ct);
    // ListAsync("") ok → Present; StorageNotFoundException → NotCreatedYet; StorageUnavailableException → Unreachable
```
`IFolderOpener` is offered only for `IsFileSystem` locations and gets the full path from `FileSystemStorage.FullPathOf`. Retention preview and versions view never delete or index while `Unreachable`.

- [ ] **Step 1: Tests:** three `ProbeAsync` cases (`InMemoryStorage` + a `FaultyStorage` throwing unavailable) — Review Focus 3.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement**; grep `src/` for remaining `System.IO.Directory`/`File.` uses on source/target paths — only `FileSystemStorage`, local app data (config, logs, index db) and `IFolderOpener` may remain. **Step 4: Run** all → green.
- [ ] **Step 5: Manual end-to-end check** with the built app (`dotnet run --project src/ReBackup.App`):
  1. Copy a 1.0.5 target (with a version and leftover `X.partial` without manifest and `Y.deleting`) to a test drive; create a plan on it. Versions tab lists the old version as owned. Run backup → leftovers gone, new version present, no `re-pending.json` left.
  2. Cancel a running backup → no new folder remains.
  3. Restore one file to a folder → content and mtime match.
  4. Delete a version manually → folder gone.
  5. Unplug/disconnect the target drive → Versions tab shows "unreachable", nothing deleted.
- [ ] **Step 6: Commit** `refactor(app): reachability through storage exceptions`
