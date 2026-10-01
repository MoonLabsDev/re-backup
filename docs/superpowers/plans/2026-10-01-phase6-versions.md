# ReBackup Phase 6 — Versions Tab, Local Index, Restorer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Versions tab per plan that lists the target's version folders (managed and not managed), browses a version as a lazy tree, compares two versions (Added / Changed / Deleted / Unchanged with folder roll-up), searches names, shows the history of a file across all versions, and restores files or folders to their original place or a chosen folder without ever deleting anything at the destination; backed by a machine-local SQLite index per plan.

**Architecture:** Core gets `ReBackup.Core.Versions`: `ManifestStream` (reads a manifest's file list entry by entry), `VersionIndex` (one SQLite database per plan: schema, `Sync` from the version folders, `Add` from a finished run, queries `Children`/`Compare`/`History`/`Search`), `VersionIndexSet` (one index per plan id under `%LOCALAPPDATA%\ReBackup\index`, also the `IVersionIndexSink` the runner hands new versions to) and the static `Restorer` (`Plan` then `Run` with a conflict policy). The App gets a `VersionsViewModel` per plan editor (split into three partial files: list + sync, tree/compare/search/history, actions + restore), a flattened lazy tree like the ignore preview, a conflict dialog, and the `VersionsView` tab in the Dark Pro style. Everything that touches the disk or the database runs off the UI thread.

**Tech Stack:** .NET 9, WPF, CommunityToolkit.Mvvm 8.4.2, Microsoft.Data.Sqlite 10.0.12 (new, Core), System.Text.Json (`Utf8JsonReader`), xUnit, FluentAssertions 7.0.0, Microsoft.Extensions.TimeProvider.Testing.

**Spec:** `docs/superpowers/specs/2026-10-01-phase6-versions-design.md` (binding), with `docs/superpowers/specs/2026-09-30-rebackup-design.md` §4.3–4.4 (target layout, manifest), §5 (`VersionCatalog`, `VersionComparer`, `Restorer`), §10.2 tab 6, §11 (change detection), and `docs/superpowers/specs/2026-10-01-dark-pro-redesign-design.md` (UI style).

## Global Constraints

- Target frameworks: `net9.0` for Core and Tests, `net9.0-windows` for App. Nullable and implicit usings are on. Builds stay at **0 warnings**; check with `--no-incremental`.
- New package: `Microsoft.Data.Sqlite` **10.0.12**, in `ReBackup.Core.csproj` only (App and Tests get it through the project reference). No other new packages. FluentAssertions stays pinned to **7.0.0**.
- Index location: `%LOCALAPPDATA%\ReBackup\index\<planId>.db` (`VersionIndexSet.DefaultDirectory`), never the (roaming) config folder. The manifest in each version folder stays the source of truth: the index is a cache, deleting it is safe (it is rebuilt), and the index code never writes into a target.
- Index schema version **1** (table `meta`, key `schema_version`). A corrupt file or another schema version is deleted and rebuilt. Connections use `Pooling=False`, so no file handle outlives a call. Each version is imported in its own transaction; cancellation never leaves a half-imported version.
- Change detection (§11): both entries hashed → by hash; otherwise by size and last write time. Added = only in B, Deleted = only in A.
- Restore never deletes or moves anything at the destination; the only file it replaces is a conflicting one under Overwrite (written to `<name>.rebackup-tmp` first, then renamed over it). Every destination stays inside the destination root, every source inside the version folder; links (junctions, symbolic links) are never followed. Keep-both names: `name (yyyy_MM_dd-HH_mm).ext`, then `name (yyyy_MM_dd-HH_mm) 2.ext`, ` 3` …
- The Versions tab never deletes versions; not-managed folders can be browsed and restored from.
- UI thread: only view-model and collection updates. Listing the target, opening/syncing/querying the index, planning and running a restore, opening Explorer — all through `Task.Run`.
- Theme: brushes only as `{DynamicResource Brush.*}` (ThemeManager swaps the palette dictionaries). **Every new color key goes into both `Theme/Colors.Dark.xaml` and `Theme/Colors.Light.xaml`.** Local styles of themed types use `BasedOn="{StaticResource {x:Type …}}"` (or a keyed theme style). Icon-only buttons carry `ToolTip` and `AutomationProperties.Name`.
- **The user's ReBackup.App may be running.** Implementers never stop, kill or start `ReBackup.App`. Build the App to a temporary output folder: `dotnet build src/ReBackup.App --no-incremental -o "$env:TEMP\rebackup-app-build"` (PowerShell). Never instantiate `ReBackup.App.App` in a harness; visual checks are the controller's job (it hosts views in a plain `Window` with the theme dictionaries merged).
- The App has no automated tests. App tasks are verified by: App build 0/0 to the temp folder, `dotnet test tests/ReBackup.Core.Tests` green, and the manual checklist of the task (performed by the controller/user).
- Tests that depend on timing use events or synchronous `IProgress` callbacks (`TestSupport/SyncProgress`), never `Thread.Sleep` to wait for a state.
- Every commit message ends with a `Co-Authored-By: Claude <model> <noreply@anthropic.com>` trailer naming the authoring model, separated from the subject by a blank line (two `-m` arguments).
- Write code exactly as given. If a test fails although the code follows the plan, do not change the expectation to make it pass: stop and report which value differs.

### Rulings recorded while writing the plan

Ambiguities of the spec, decided here (the plan's code implements them):

1. **Path uniqueness.** Spec §2.1 has `paths.path UNIQUE`. The schema uses `UNIQUE (path, is_dir)` instead, because one name can be a file in one version and a folder in another; with `path` alone the second import would fail.
2. **Stamp of a folder with an unreadable manifest.** The stamp is the manifest's (length + last write) whenever a manifest file exists, even when it could not be parsed and the version was scanned. Otherwise every sync would scan such a folder again; a repaired manifest changes the stamp and is imported.
3. **Comparison direction.** The tree shows version A (selected in the list); "Compare with" picks B. Statuses are always computed from the older to the newer of the two (§11 default "A = older, B = newer"), so "Added" always means "new in the later version"; the Swap button exchanges which version's tree is shown. While comparing, the tree shows the union: entries only in B appear (italic) with B's size.
4. **Restore confirmation.** "Restore to original" always asks for confirmation (it writes into the live source), and adds the running-backup warning when a backup of the plan is queued or running. "Restore to…" needs no extra confirmation: the folder picker is it. The conflict dialog appears only when conflicts exist.
5. **History statuses.** A version without the file right after one that had it is `Deleted`; further versions without it are `Absent` (shown as "—"). A file that reappears is compared with the last version that had it.
6. **"Progress in the plan card area".** A text line under the plan card's detail text ("Restoring 120 of 300 files · 34 %"); the progress ring stays reserved for backups. The footer's status line shows start and result.
7. **Restore failure reasons.** Windows reports both a locked and a read-only destination as "access denied" when replacing a file, so the reason text cannot reliably tell them apart; tests check the failing paths, not the wording.
8. **Sync trigger.** The tab syncs every time it becomes visible and on Refresh (cheap when all stamps match), and after a run of the plan if the tab was shown before.
9. **Search case-insensitivity.** SQLite's `LIKE`/`NOCASE` fold ASCII only; the search registers a .NET function `rb_match` on the connection (substring `OrdinalIgnoreCase`, wildcards as an invariant case-insensitive regex).
10. **Entries only in B.** Actions on them (Open, Show in Explorer, Restore) use version B's folder.

---

## File Structure

```
src/ReBackup.Core/
  ReBackup.Core.csproj                (modify) Microsoft.Data.Sqlite 10.0.12
  Versions/ManifestStream.cs          ManifestSummary, ManifestStream (streaming manifest reader)
  Versions/IndexModels.cs             IndexOrigin, IndexedVersion, IndexSyncProgress, IndexSyncResult, IVersionIndexSink
  Versions/VersionIndex.cs            schema, Open/rebuild, Sync, Add, import (manifest/scan), Versions()
  Versions/VersionIndexSet.cs         one index per plan id; IVersionIndexSink
  Versions/QueryModels.cs             IndexStats, IndexChild, DiffStatus, HistoryStatus, HistoryEntry, SearchHit
  Versions/VersionIndex.Queries.cs    FindPath, Children, Compare, History, Search
  Versions/Restorer.cs                RestoreMode, ConflictPolicy, RestorePlan, RestoreResult, RestoreProgress, Restorer
  Backup/BackupRunner.cs              (modify) IVersionIndexSink hook after a finished run
src/ReBackup.App/
  ViewModels/VersionRowViewModel.cs        a version folder in the list (IndexState)
  ViewModels/VersionsViewModel.cs          VersionsContext; list + sync (partial 1/3)
  ViewModels/VersionTreeNode.cs            a row of the lazy tree
  ViewModels/VersionTreeViewModel.cs       flattened lazy tree, compare statuses, changed only, reveal
  ViewModels/VersionListItems.cs           CompareChoice, SearchHitRow, FileHistoryRow
  ViewModels/VersionsViewModel.Tree.cs     compare, swap, search, history (partial 2/3)
  ViewModels/VersionsViewModel.Restore.cs  open, show in Explorer, restore (partial 3/3)
  ViewModels/PlanEditorViewModel.cs        (modify) Versions property
  ViewModels/MainViewModel.cs              (modify) MainTab.Versions, VersionsContext, reload after runs / target changes
  Views/VersionsView.xaml(.cs)             the tab
  ConflictDialog.xaml(.cs)                 Overwrite / Skip / Keep both / Cancel
  Services/IFolderOpener.cs                (modify) OpenFile, ShowInExplorer
  Services/IDialogService.cs, WpfDialogService.cs   (modify) AskConflictPolicy
  MainWindow.xaml                          (modify) rail + header tab, content, plan card restore line
  App.xaml.cs                              (modify) VersionIndexSet → runner sink + MainViewModel
  Theme/Icons.xaml                         (modify) Icon.Versions, Search, Swap, Document, Folder, OpenFile, Explorer, Restore
  Theme/Colors.Dark.xaml, Colors.Light.xaml (modify) Brush.DiffAdded
  Theme/Controls.xaml                      (modify) ToggleButton.Expander moved here (shared by two trees)
  Views/IgnorePreviewView.xaml             (modify) its local ToggleButton.Expander removed
tests/ReBackup.Core.Tests/
  TestSupport/VersionBuilder.cs, TestSupport/SyncProgress.cs
  Versions/ManifestStreamTests.cs, VersionIndexTests.cs, VersionIndexQueryTests.cs, RestorerTests.cs
  Backup/BackupRunnerIndexTests.cs
```

Test counts for orientation (Core suite): 552 before this plan → 577 after Task 1 → 587 after Task 2 → 608 after Task 3 → 611 after Task 4.

---

### Task 1: Version index store — schema, rebuild, streaming import, scan fallback, Sync

**Files:**
- Modify: `src/ReBackup.Core/ReBackup.Core.csproj`
- Create: `src/ReBackup.Core/Versions/ManifestStream.cs`, `src/ReBackup.Core/Versions/IndexModels.cs`, `src/ReBackup.Core/Versions/VersionIndex.cs`, `src/ReBackup.Core/Versions/VersionIndexSet.cs`
- Create (tests): `tests/ReBackup.Core.Tests/TestSupport/VersionBuilder.cs`, `tests/ReBackup.Core.Tests/TestSupport/SyncProgress.cs`, `tests/ReBackup.Core.Tests/Versions/ManifestStreamTests.cs`, `tests/ReBackup.Core.Tests/Versions/VersionIndexTests.cs`

**Interfaces:**
- Consumes: `VersionInfo`, `VersionOwnership`, `VersionCatalog.List`, `VersionName.ManifestFileName`, `VersionName.TryParseAny`, `BackupManifest`, `ManifestFile(Path, Size, MtimeUtc, Hash)`, `JsonDefaults.Options`; test helpers `TempDir`, `Junction.Create`.
- Produces (namespace `ReBackup.Core.Versions`):
  - `record ManifestSummary(string PlanId, string PlanName, DateTime CreatedUtc, string Source)`; `static class ManifestStream { const int InitialBufferSize = 65536; ManifestSummary Read(string manifestPath, Action<ManifestFile> onFile, CancellationToken = default); ManifestSummary Read(Stream, Action<ManifestFile>, CancellationToken = default) }` (throws `JsonException` for non-manifests)
  - `enum IndexOrigin { Manifest, Scan }`; `record IndexedVersion(long Id, string Name, DateTime LocalTime, VersionOwnership Ownership, string? Source, IndexOrigin Origin, int FileCount, long TotalBytes)`
  - `readonly record struct IndexSyncProgress(int Current, int Total, string VersionName, int FilesImported, bool Finished)`; `record IndexSyncResult(int Imported, int Unchanged, int Removed, IReadOnlyList<string> Errors)` (errors as `"<version name>: <message>"`)
  - `interface IVersionIndexSink { void Add(string planId, VersionInfo version, BackupManifest manifest); }`
  - `sealed partial class VersionIndex`: `const int SchemaVersion = 1`, `const int ProgressEvery = 4096`, `string DatabasePath`, `static VersionIndex Open(string databasePath)`, `IndexSyncResult Sync(IReadOnlyList<VersionInfo> folders, IProgress<IndexSyncProgress>? = null, CancellationToken = default)` (throws `OperationCanceledException`), `void Add(VersionInfo version, BackupManifest manifest)`, `IReadOnlyList<IndexedVersion> Versions()` (oldest first), `static string StampOf(string versionFolder)`, `static long? ParseHash(string? hash)`; private helpers used by Task 2: `OpenConnection()`, `Command(connection, transaction, sql)`, `ReadVersion(reader, first)`
  - `sealed class VersionIndexSet : IVersionIndexSink`: `VersionIndexSet(string directory)`, `static string DefaultDirectory`, `string Directory`, `string PathFor(string planId)` (throws `ArgumentException` for ids that are no file name), `VersionIndex For(string planId)` (opened once per id, case-insensitive)
  - Test helpers: `TestFile(Path, Content, MtimeUtc)`, `VersionBuilder.File(path, content, mtime?)`, `VersionBuilder.Write(target, minute, files, withManifest = true)` → folder path (`"<minute> Projects"`, plan id `plan1`, source `C:\source`, manifest with real xxHash64), `VersionBuilder.List(target)`, constants `PlanId`, `PlanName`, `Source`, `Mtime`; `SyncProgress<T>(Action<T>)`

The schema (spec §2.1 with ruling 1) is in `VersionIndex.Schema`. Paths are relative with forward slashes, `""` is the version root (a `dirs` row for the root is always written, so an empty version still has one). Hashes are stored as the signed 64-bit value of the 16 hex digits.

- [ ] **Step 1: Add the SQLite package**

In `src/ReBackup.Core/ReBackup.Core.csproj`, replace

```xml
    <PackageReference Include="System.IO.Hashing" Version="10.0.12" />
```

with

```xml
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.12" />
    <PackageReference Include="System.IO.Hashing" Version="10.0.12" />
```

Run: `dotnet build src/ReBackup.Core --no-incremental`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 2: Test helpers**

Create `tests/ReBackup.Core.Tests/TestSupport/VersionBuilder.cs`:

```csharp
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using ReBackup.Core.Backup;
using ReBackup.Core.Json;

namespace ReBackup.Core.Tests.TestSupport;

/// <summary>A file of a test version: relative path (forward slashes), content, last write time (UTC).</summary>
public sealed record TestFile(string Path, string Content, DateTime MtimeUtc);

/// <summary>Writes version folders with real files and a manifest that lists them with their xxHash64.</summary>
public static class VersionBuilder
{
    public const string PlanId = "plan1";
    public const string PlanName = "Projects";
    public const string Source = @"C:\source";
    public static readonly DateTime Mtime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static TestFile File(string path, string content, DateTime? mtimeUtc = null) =>
        new(path, content, mtimeUtc ?? Mtime);

    /// <summary>Creates <c>&lt;target&gt;\&lt;minute&gt; Projects</c> with the files and, unless told not to, a manifest.</summary>
    public static string Write(string target, string minute, IEnumerable<TestFile> files, bool withManifest = true)
    {
        var folder = Path.Combine(target, $"{minute} {PlanName}");
        Directory.CreateDirectory(folder);
        var manifest = new BackupManifest
        {
            PlanId = PlanId,
            PlanName = PlanName,
            CreatedUtc = Mtime,
            Source = Source,
        };
        foreach (var file in files)
        {
            var path = Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(file.Content);
            System.IO.File.WriteAllBytes(path, bytes);
            System.IO.File.SetLastWriteTimeUtc(path, file.MtimeUtc);
            manifest.Files.Add(new ManifestFile(file.Path, bytes.Length, file.MtimeUtc,
                "xxh64:" + Convert.ToHexStringLower(XxHash64.Hash(bytes))));
        }
        manifest.FileCount = manifest.Files.Count;
        manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
        if (withManifest)
        {
            System.IO.File.WriteAllText(Path.Combine(folder, VersionName.ManifestFileName),
                JsonSerializer.Serialize(manifest, JsonDefaults.Options));
        }
        return folder;
    }

    /// <summary>The target's versions as the app lists them.</summary>
    public static IReadOnlyList<VersionInfo> List(string target) => VersionCatalog.List(target, PlanId, PlanName);
}
```

Create `tests/ReBackup.Core.Tests/TestSupport/SyncProgress.cs`:

```csharp
namespace ReBackup.Core.Tests.TestSupport;

/// <summary>Reports synchronously on the calling thread (<see cref="Progress{T}"/> would post to the thread pool).</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
```

- [ ] **Step 3: Write the failing tests of the streaming reader**

Create `tests/ReBackup.Core.Tests/Versions/ManifestStreamTests.cs`:

```csharp
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Json;
using ReBackup.Core.Versions;

namespace ReBackup.Core.Tests.Versions;

public class ManifestStreamTests
{
    private static readonly DateTime Mtime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<ManifestFile> ReadAll(string json, out ManifestSummary summary)
    {
        var files = new List<ManifestFile>();
        summary = ManifestStream.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)), files.Add);
        return files;
    }

    [Fact]
    public void Reads_the_fields_and_every_entry()
    {
        var files = ReadAll("""
            { "formatVersion": 1, "planId": "p1", "planName": "Projects", "createdUtc": "2026-09-30T12:05:00Z",
              "source": "D:\\Projects", "fileCount": 2, "totalBytes": 300,
              "files": [ { "path": "a.txt", "size": 100, "mtimeUtc": "2026-01-01T00:00:00Z", "hash": "xxh64:00000000000000ff" },
                         { "path": "sub/b.txt", "size": 200, "mtimeUtc": "2026-01-01T00:00:00Z", "hash": "xxh64:0000000000000001" } ] }
            """, out var summary);

        summary.Should().Be(new ManifestSummary("p1", "Projects", new DateTime(2026, 9, 30, 12, 5, 0, DateTimeKind.Utc), @"D:\Projects"));
        files.Should().Equal(
            new ManifestFile("a.txt", 100, Mtime, "xxh64:00000000000000ff"),
            new ManifestFile("sub/b.txt", 200, Mtime, "xxh64:0000000000000001"));
    }

    [Fact]
    public void Takes_fields_behind_the_file_list_and_skips_unknown_values()
    {
        var files = ReadAll("""
            { "files": [ { "path": "a.txt", "size": 1, "extra": { "x": [1, 2] }, "mtimeUtc": "2026-01-01T00:00:00Z", "hash": "" } ],
              "unknown": { "nested": [ { } ] }, "planId": "p1", "source": "C:\\s" }
            """, out var summary);

        files.Should().ContainSingle().Which.Path.Should().Be("a.txt");
        summary.PlanId.Should().Be("p1");
        summary.Source.Should().Be(@"C:\s");
    }

    [Fact]
    public void Accepts_a_byte_order_mark()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("""{ "planId": "p1", "files": [] }""")).ToArray();

        ManifestStream.Read(new MemoryStream(bytes), _ => { }).PlanId.Should().Be("p1");
    }

    [Theory]
    [InlineData("""[ 1, 2 ]""")]
    [InlineData("""{ "planId": "p1", "files": [ { "path": "a.txt" """)]
    [InlineData("""{ "files": [ 42 ] }""")]
    [InlineData("""{ "files": [ { "size": 1 } ] }""")]
    public void Rejects_what_is_not_a_manifest(string json)
    {
        var read = () => ReadAll(json, out _);

        read.Should().Throw<JsonException>();
    }

    [Fact]
    public void Reads_a_large_manifest_through_a_small_window()
    {
        var manifest = new BackupManifest { PlanId = "p1", PlanName = "Projects", Source = @"C:\s" };
        for (var i = 0; i < 100_000; i++)
            manifest.Files.Add(new ManifestFile($"folder{i % 100}/file{i}.bin", i, Mtime, "xxh64:0123456789abcdef"));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonDefaults.Options);
        bytes.Length.Should().BeGreaterThan(10_000_000, "the test needs a manifest much larger than the read window");
        var stream = new CountingStream(bytes);
        long readWhenFirstFileArrived = -1;
        var count = 0;

        ManifestStream.Read(stream, _ =>
        {
            if (count++ == 0)
                readWhenFirstFileArrived = stream.BytesRead;
        });

        count.Should().Be(100_000);
        readWhenFirstFileArrived.Should().BeLessOrEqualTo(ManifestStream.InitialBufferSize,
            "entries are handed out while the file is read, not after reading all of it");
        stream.LargestRead.Should().BeLessOrEqualTo(ManifestStream.InitialBufferSize);
    }

    /// <summary>A forward-only stream over bytes that records how much has been read.</summary>
    private sealed class CountingStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public long BytesRead { get; private set; }
        public int LargestRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            LargestRead = Math.Max(LargestRead, read);
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 4: Run them to see them fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~ManifestStreamTests"`
Expected: build error CS0234/CS0246 — `ReBackup.Core.Versions` / `ManifestStream` does not exist.

- [ ] **Step 5: Implement the streaming reader**

Create `src/ReBackup.Core/Versions/ManifestStream.cs`:

```csharp
using System.Text.Json;
using ReBackup.Core.Backup;

namespace ReBackup.Core.Versions;

/// <summary>The fields of a manifest other than its file list.</summary>
public sealed record ManifestSummary(string PlanId, string PlanName, DateTime CreatedUtc, string Source);

/// <summary>
/// Reads a <c>re-manifest.json</c> entry by entry: only a small window of the file is in memory at any time, however
/// long the file list is.
/// </summary>
public static class ManifestStream
{
    /// <summary>Size of the read window; it grows only for a single token or entry that does not fit.</summary>
    public const int InitialBufferSize = 64 * 1024;

    private enum Phase { Start, Properties, Files, Done }

    /// <exception cref="IOException">The file is missing or cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="JsonException">The file is not a manifest.</exception>
    public static ManifestSummary Read(string manifestPath, Action<ManifestFile> onFile,
        CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        return Read(stream, onFile, cancellationToken);
    }

    /// <inheritdoc cref="Read(string, Action{ManifestFile}, CancellationToken)"/>
    public static ManifestSummary Read(Stream stream, Action<ManifestFile> onFile,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[InitialBufferSize];
        var length = Fill(stream, buffer, 0, out var endOfStream);
        var start = length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        var state = new JsonReaderState();
        var phase = Phase.Start;
        var fields = new Fields();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reader = new Utf8JsonReader(buffer.AsSpan(start, length - start), endOfStream, state);
            while (phase != Phase.Done && TryStep(ref reader, ref phase, fields, onFile))
            {
            }
            if (phase == Phase.Done)
                return new ManifestSummary(fields.PlanId, fields.PlanName, fields.CreatedUtc, fields.Source);
            if (endOfStream)
                throw new JsonException("The manifest ends too early.");

            // Keep what was not consumed, then read more behind it; grow only when nothing at all was consumed.
            var consumed = start + (int)reader.BytesConsumed;
            state = reader.CurrentState;
            Buffer.BlockCopy(buffer, consumed, buffer, 0, length - consumed);
            length -= consumed;
            start = 0;
            if (length == buffer.Length)
                Array.Resize(ref buffer, buffer.Length * 2);
            length += Fill(stream, buffer, length, out endOfStream);
        }
    }

    private static int Fill(Stream stream, byte[] buffer, int offset, out bool endOfStream)
    {
        var wanted = buffer.Length - offset;
        var read = stream.ReadAtLeast(buffer.AsSpan(offset), wanted, throwOnEndOfStream: false);
        endOfStream = read < wanted;
        return read;
    }

    /// <summary>
    /// Reads one unit (the opening brace, one property, or one file entry) on a copy of the reader and takes it over
    /// only when the unit is complete in the buffer. False: more data is needed.
    /// </summary>
    private static bool TryStep(ref Utf8JsonReader reader, ref Phase phase, Fields fields, Action<ManifestFile> onFile)
    {
        var probe = reader;
        switch (phase)
        {
            case Phase.Start:
                if (!probe.Read())
                    return false;
                if (probe.TokenType != JsonTokenType.StartObject)
                    throw new JsonException("A manifest is a JSON object.");
                phase = Phase.Properties;
                break;

            case Phase.Properties:
            {
                if (!probe.Read())
                    return false;
                if (probe.TokenType == JsonTokenType.EndObject)
                {
                    phase = Phase.Done;
                    break;
                }
                var name = probe.GetString();
                if (!probe.Read())
                    return false;
                if (Is(name, "files") && probe.TokenType == JsonTokenType.StartArray)
                {
                    phase = Phase.Files;
                    break;
                }
                if (probe.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    if (!probe.TrySkip())
                        return false;
                    break;
                }
                fields.Set(name, ref probe);
                break;
            }

            case Phase.Files:
            {
                if (!probe.Read())
                    return false;
                if (probe.TokenType == JsonTokenType.EndArray)
                {
                    phase = Phase.Properties;
                    break;
                }
                if (probe.TokenType == JsonTokenType.Null)
                    break;
                if (probe.TokenType != JsonTokenType.StartObject)
                    throw new JsonException("A file entry is a JSON object.");
                var entry = probe;
                if (!probe.TrySkip())
                    return false;
                onFile(ReadFile(ref entry));   // the whole entry is in the buffer: reading it cannot run dry
                break;
            }
        }

        reader = probe;
        return true;
    }

    private static ManifestFile ReadFile(ref Utf8JsonReader reader)
    {
        string? path = null;
        long size = 0;
        DateTime mtime = default;
        var hash = "";
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            if (Is(name, "path") && reader.TokenType == JsonTokenType.String)
                path = reader.GetString();
            else if (Is(name, "size") && reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var value))
                size = value;
            else if (Is(name, "mtimeUtc") && reader.TokenType == JsonTokenType.String && reader.TryGetDateTime(out var time))
                mtime = time.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : time.ToUniversalTime();
            else if (Is(name, "hash") && reader.TokenType == JsonTokenType.String)
                hash = reader.GetString() ?? "";
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                reader.Skip();
        }
        if (string.IsNullOrEmpty(path))
            throw new JsonException("A file entry has no path.");
        return new ManifestFile(path, size, mtime, hash);
    }

    private static bool Is(string? name, string expected) => string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);

    private sealed class Fields
    {
        public string PlanId = "";
        public string PlanName = "";
        public string Source = "";
        public DateTime CreatedUtc;

        public void Set(string? name, ref Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.String)
                return;
            if (Is(name, "planId"))
                PlanId = reader.GetString() ?? "";
            else if (Is(name, "planName"))
                PlanName = reader.GetString() ?? "";
            else if (Is(name, "source"))
                Source = reader.GetString() ?? "";
            else if (Is(name, "createdUtc") && reader.TryGetDateTime(out var created))
                CreatedUtc = created;
        }
    }
}
```

How it works: every unit (the opening brace, one top-level property, one file entry) is read on a copy of the `Utf8JsonReader`; only when the unit is complete in the buffer is the copy taken over. Otherwise the unconsumed tail moves to the front of the buffer, more bytes are read behind it, and the reader continues with its saved `JsonReaderState`. The buffer grows only for a single entry larger than the buffer.

- [ ] **Step 6: Run the tests to see them pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~ManifestStreamTests"`
Expected: PASS, 8 tests.

- [ ] **Step 7: Write the failing tests of the index store**

Create `tests/ReBackup.Core.Tests/Versions/VersionIndexTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using ReBackup.Core.Backup;
using ReBackup.Core.Json;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using static ReBackup.Core.Tests.TestSupport.VersionBuilder;

namespace ReBackup.Core.Tests.Versions;

public class VersionIndexTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _target;
    private readonly string _db;

    public VersionIndexTests()
    {
        _target = _tmp.CreateDir("target");
        _db = _tmp.PathOf(@"index\plan1.db");
    }

    public void Dispose() => _tmp.Dispose();

    /// <summary>Runs a query against the database file directly; each row as an array of column values.</summary>
    private List<object?[]> Rows(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_db};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private List<(string Path, long Size, long Mtime, long? Hash)> FilesOf(string versionName) =>
        Rows($"""
              SELECT p.path, f.size, f.mtime_ticks, f.hash FROM files f JOIN paths p ON p.id = f.path_id
              JOIN versions v ON v.id = f.version_id WHERE v.name = '{versionName}' ORDER BY p.path
              """)
            .Select(r => ((string)r[0]!, (long)r[1]!, (long)r[2]!, (long?)r[3]))
            .ToList();

    private Dictionary<string, (long Size, long Files)> DirsOf(string versionName) =>
        Rows($"""
              SELECT p.path, d.size, d.files FROM dirs d JOIN paths p ON p.id = d.path_id
              JOIN versions v ON v.id = d.version_id WHERE v.name = '{versionName}'
              """)
            .ToDictionary(r => (string)r[0]!, r => ((long)r[1]!, (long)r[2]!));

    [Fact]
    public void Creates_the_schema()
    {
        VersionIndex.Open(_db);

        Rows("SELECT value FROM meta WHERE key = 'schema_version'").Should().ContainSingle().Which[0].Should().Be("1");
        Rows("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name").Select(r => (string)r[0]!)
            .Should().Equal("dirs", "files", "meta", "paths", "versions");
        Rows("PRAGMA journal_mode").Single()[0].Should().Be("wal");
    }

    [Fact]
    public void Imports_a_version_from_its_manifest()
    {
        var later = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha"), File("sub/b.txt", "bravo!", later), File("sub/deep/c.txt", "c")]);
        var index = VersionIndex.Open(_db);

        var result = index.Sync(List(_target));

        result.Should().BeEquivalentTo(new IndexSyncResult(1, 0, 0, []));
        var version = index.Versions().Should().ContainSingle().Subject;
        version.Name.Should().Be("2026_09_30-14_05 Projects");
        version.LocalTime.Should().Be(new DateTime(2026, 9, 30, 14, 5, 0));
        version.Ownership.Should().Be(VersionOwnership.Owned);
        version.Origin.Should().Be(IndexOrigin.Manifest);
        version.Source.Should().Be(Source);
        version.FileCount.Should().Be(3);
        version.TotalBytes.Should().Be(12);

        var files = FilesOf(version.Name);
        files.Select(f => (f.Path, f.Size, f.Mtime)).Should().Equal(
            ("a.txt", 5L, Mtime.Ticks), ("sub/b.txt", 6L, later.Ticks), ("sub/deep/c.txt", 1L, Mtime.Ticks));
        files.Should().OnlyContain(f => f.Hash != null);
        DirsOf(version.Name).Should().BeEquivalentTo(new Dictionary<string, (long, long)>
        {
            [""] = (12, 3),
            ["sub"] = (7, 2),
            ["sub/deep"] = (1, 1),
        });
    }

    [Fact]
    public void Stores_the_hash_as_a_signed_64_bit_number()
    {
        VersionIndex.ParseHash("xxh64:ffffffffffffffff").Should().Be(-1);
        VersionIndex.ParseHash("xxh64:0000000000000010").Should().Be(16);
        VersionIndex.ParseHash("").Should().BeNull();
        VersionIndex.ParseHash("xxh64:xyz").Should().BeNull();
        VersionIndex.ParseHash("sha1:0000000000000010").Should().BeNull();
    }

    [Fact]
    public void Scans_a_folder_without_a_manifest()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha"), File("sub/b.txt", "bravo!")], withManifest: false);
        var index = VersionIndex.Open(_db);

        index.Sync(List(_target));

        var version = index.Versions().Should().ContainSingle().Subject;
        version.Ownership.Should().Be(VersionOwnership.NoManifest);
        version.Origin.Should().Be(IndexOrigin.Scan);
        version.Source.Should().BeNull();
        FilesOf(version.Name).Should().Equal(("a.txt", 5L, Mtime.Ticks, null), ("sub/b.txt", 6L, Mtime.Ticks, null));
        DirsOf(version.Name)[""].Should().Be((11, 2));
        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public void Scans_a_folder_whose_manifest_cannot_be_read_and_leaves_the_manifest_out()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")], withManifest: false);
        System.IO.File.WriteAllText(Path.Combine(folder, VersionName.ManifestFileName), "{ not json");
        var index = VersionIndex.Open(_db);

        index.Sync(List(_target));

        var version = index.Versions().Should().ContainSingle().Subject;
        version.Ownership.Should().Be(VersionOwnership.Unreadable);
        version.Origin.Should().Be(IndexOrigin.Scan);
        FilesOf(version.Name).Select(f => f.Path).Should().Equal("a.txt");
    }

    [Fact]
    public void A_scan_does_not_follow_links()
    {
        var outside = _tmp.CreateDir("outside");
        _tmp.WriteFile(@"outside\secret.txt", "secret");
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")], withManifest: false);
        Junction.Create(Path.Combine(folder, "link"), outside);
        var index = VersionIndex.Open(_db);

        index.Sync(List(_target));

        FilesOf("2026_09_30-14_05 Projects").Select(f => f.Path).Should().Equal("a.txt");
    }

    [Fact]
    public void Leaves_an_unchanged_version_alone_and_imports_a_changed_one_again()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        var index = VersionIndex.Open(_db);
        index.Sync(List(_target));
        var firstId = index.Versions().Single().Id;

        index.Sync(List(_target)).Should().BeEquivalentTo(new IndexSyncResult(0, 1, 0, []));
        index.Versions().Single().Id.Should().Be(firstId);

        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha"), File("new.txt", "new")]);
        System.IO.File.SetLastWriteTimeUtc(Path.Combine(folder, VersionName.ManifestFileName), DateTime.UtcNow.AddMinutes(1));

        index.Sync(List(_target)).Should().BeEquivalentTo(new IndexSyncResult(1, 0, 0, []));
        FilesOf("2026_09_30-14_05 Projects").Select(f => f.Path).Should().Equal("a.txt", "new.txt");
    }

    [Fact]
    public void Imports_a_scanned_folder_again_when_its_top_level_changes()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")], withManifest: false);
        var index = VersionIndex.Open(_db);
        index.Sync(List(_target));

        System.IO.File.WriteAllText(Path.Combine(folder, "b.txt"), "bravo");

        index.Sync(List(_target)).Imported.Should().Be(1);
        FilesOf("2026_09_30-14_05 Projects").Select(f => f.Path).Should().Equal("a.txt", "b.txt");
    }

    [Fact]
    public void Removes_versions_whose_folder_is_gone()
    {
        var old = Write(_target, "2026_09_29-14_05", [File("a.txt", "alpha")]);
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        var index = VersionIndex.Open(_db);
        index.Sync(List(_target));

        Directory.Delete(old, recursive: true);

        index.Sync(List(_target)).Removed.Should().Be(1);
        index.Versions().Select(v => v.Name).Should().Equal("2026_09_30-14_05 Projects");
        Rows("SELECT COUNT(*) FROM files").Single()[0].Should().Be(1L);
        Rows("SELECT COUNT(*) FROM dirs").Single()[0].Should().Be(1L);
    }

    [Fact]
    public void Shares_paths_between_versions()
    {
        Write(_target, "2026_09_29-14_05", [File("sub/a.txt", "alpha")]);
        Write(_target, "2026_09_30-14_05", [File("sub/a.txt", "alpha2")]);
        var index = VersionIndex.Open(_db);

        index.Sync(List(_target));

        Rows("SELECT path, is_dir FROM paths ORDER BY path").Select(r => ((string)r[0]!, (long)r[1]!))
            .Should().Equal(("", 1L), ("sub", 1L), ("sub/a.txt", 0L));
        Rows("SELECT COUNT(*) FROM files").Single()[0].Should().Be(2L);
    }

    [Fact]
    public void Reports_progress_per_version()
    {
        Write(_target, "2026_09_29-14_05", [File("a.txt", "alpha")]);
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha"), File("b.txt", "bravo")]);
        var index = VersionIndex.Open(_db);
        var reports = new List<IndexSyncProgress>();

        index.Sync(List(_target), new SyncProgress<IndexSyncProgress>(reports.Add));

        reports.Should().Equal(
            new IndexSyncProgress(1, 2, "2026_09_29-14_05 Projects", 0, false),
            new IndexSyncProgress(1, 2, "2026_09_29-14_05 Projects", 1, true),
            new IndexSyncProgress(2, 2, "2026_09_30-14_05 Projects", 0, false),
            new IndexSyncProgress(2, 2, "2026_09_30-14_05 Projects", 2, true));
    }

    [Fact]
    public void Cancellation_during_an_import_keeps_the_versions_already_committed()
    {
        Write(_target, "2026_09_29-14_05", [File("a.txt", "alpha")]);
        var big = Write(_target, "2026_09_30-14_05", []);
        var manifest = new BackupManifest { PlanId = PlanId, PlanName = PlanName, Source = Source };
        for (var i = 0; i < 3 * VersionIndex.ProgressEvery; i++)
            manifest.Files.Add(new ManifestFile($"f{i}.bin", 1, Mtime, "xxh64:0000000000000001"));
        System.IO.File.WriteAllText(Path.Combine(big, VersionName.ManifestFileName), JsonSerializer.Serialize(manifest, JsonDefaults.Options));
        var index = VersionIndex.Open(_db);
        using var cts = new CancellationTokenSource();

        var sync = () => index.Sync(List(_target), new SyncProgress<IndexSyncProgress>(p =>
        {
            if (p.FilesImported > 0 && !p.Finished)
                cts.Cancel();   // in the middle of the big version
        }), cts.Token);

        sync.Should().Throw<OperationCanceledException>();
        index.Versions().Select(v => v.Name).Should().Equal("2026_09_29-14_05 Projects");
        Rows("SELECT COUNT(*) FROM files").Single()[0].Should().Be(1L);
        Rows("SELECT COUNT(*) FROM dirs").Single()[0].Should().Be(1L);

        index.Sync(List(_target)).Imported.Should().Be(1, "the next sync imports the version that was canceled");
        index.Versions().Last().FileCount.Should().Be(3 * VersionIndex.ProgressEvery);
    }

    [Fact]
    public void Rebuilds_a_corrupt_database()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_db)!);
        System.IO.File.WriteAllText(_db, "this is not a database, just text that is long enough to look like a header");
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);

        var index = VersionIndex.Open(_db);
        index.Sync(List(_target));

        index.Versions().Should().ContainSingle();
    }

    [Fact]
    public void Rebuilds_a_database_of_another_schema_version()
    {
        Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        VersionIndex.Open(_db).Sync(List(_target));
        using (var connection = new SqliteConnection($"Data Source={_db};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE meta SET value = '0' WHERE key = 'schema_version'";
            command.ExecuteNonQuery();
        }

        var index = VersionIndex.Open(_db);

        index.Versions().Should().BeEmpty("the old database was thrown away");
        Rows("SELECT value FROM meta WHERE key = 'schema_version'").Single()[0].Should().Be("1");
    }

    [Fact]
    public void Imports_a_large_manifest()
    {
        var folder = Write(_target, "2026_09_30-14_05", []);
        var manifest = new BackupManifest { PlanId = PlanId, PlanName = PlanName, Source = Source };
        for (var i = 0; i < 100_000; i++)
            manifest.Files.Add(new ManifestFile($"folder{i % 100}/file{i}.bin", 2, Mtime, "xxh64:0123456789abcdef"));
        System.IO.File.WriteAllText(Path.Combine(folder, VersionName.ManifestFileName), JsonSerializer.Serialize(manifest, JsonDefaults.Options));
        var index = VersionIndex.Open(_db);

        index.Sync(List(_target));

        index.Versions().Single().FileCount.Should().Be(100_000);
        DirsOf("2026_09_30-14_05 Projects")["folder7"].Should().Be((2_000, 1_000));
    }

    [Fact]
    public void Adds_a_version_from_a_manifest_in_memory()
    {
        var folder = Write(_target, "2026_09_30-14_05", [File("a.txt", "alpha")]);
        var version = List(_target).Single();
        var manifest = new BackupManifest
        {
            PlanId = PlanId, PlanName = PlanName, Source = @"D:\elsewhere",
            Files = [new ManifestFile("x/y.txt", 3, Mtime, "xxh64:0000000000000002")],
        };
        var index = VersionIndex.Open(_db);

        index.Add(version, manifest);

        index.Versions().Single().Source.Should().Be(@"D:\elsewhere");
        FilesOf(version.Name).Should().Equal(("x/y.txt", 3L, Mtime.Ticks, 2L));
        index.Sync(List(_target)).Unchanged.Should().Be(1, "the stamp of the manifest on disk was recorded");
        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public void Index_sets_put_one_database_per_plan_into_their_folder()
    {
        var set = new VersionIndexSet(_tmp.PathOf("indexes"));

        set.PathFor("abc").Should().Be(_tmp.PathOf(@"indexes\abc.db"));
        set.For("abc").Should().BeSameAs(set.For("ABC"));
        System.IO.File.Exists(_tmp.PathOf(@"indexes\abc.db")).Should().BeTrue();
        var bad = () => set.PathFor(@"..\x");
        bad.Should().Throw<ArgumentException>();
    }
}
```

- [ ] **Step 8: Run them to see them fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~VersionIndexTests"`
Expected: build errors — `VersionIndex`, `IndexSyncResult`, `IndexOrigin`, `VersionIndexSet` … do not exist.

- [ ] **Step 9: Implement the models, the index and the index set**

Create `src/ReBackup.Core/Versions/IndexModels.cs`:

```csharp
using ReBackup.Core.Backup;

namespace ReBackup.Core.Versions;

/// <summary>Where the index took a version's file list from.</summary>
public enum IndexOrigin { Manifest, Scan }

/// <summary>A version as the index knows it.</summary>
public sealed record IndexedVersion(long Id, string Name, DateTime LocalTime, VersionOwnership Ownership, string? Source,
    IndexOrigin Origin, int FileCount, long TotalBytes);

/// <summary>
/// Progress of <see cref="VersionIndex.Sync"/>: version <paramref name="Current"/> of <paramref name="Total"/>.
/// <paramref name="Finished"/> is true once that version is up to date in the index (imported or unchanged).
/// </summary>
public readonly record struct IndexSyncProgress(int Current, int Total, string VersionName, int FilesImported, bool Finished);

/// <summary>What a sync did; <paramref name="Errors"/> names versions that could not be read (they stay out of the index).</summary>
public sealed record IndexSyncResult(int Imported, int Unchanged, int Removed, IReadOnlyList<string> Errors);

/// <summary>Takes the manifest of a version a backup run has just finished.</summary>
public interface IVersionIndexSink
{
    void Add(string planId, VersionInfo version, BackupManifest manifest);
}
```

Create `src/ReBackup.Core/Versions/VersionIndex.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReBackup.Core.Backup;

namespace ReBackup.Core.Versions;

/// <summary>
/// A machine-local SQLite cache of the file lists of one plan's versions. The manifests in the version folders stay
/// the source of truth: the database can be deleted at any time and is rebuilt by <see cref="Sync"/>.
/// Thread-safe: every call opens its own connection; writes are serialized, reads run beside them (WAL).
/// </summary>
public sealed partial class VersionIndex
{
    public const int SchemaVersion = 1;

    /// <summary>Progress is reported after this many files of a version.</summary>
    public const int ProgressEvery = 4096;

    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss";

    private const string Schema = """
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS meta     (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS versions (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                                             local_time TEXT NOT NULL, ownership TEXT NOT NULL, source TEXT,
                                             origin TEXT NOT NULL, stamp TEXT NOT NULL,
                                             file_count INTEGER NOT NULL, total_bytes INTEGER NOT NULL, indexed_utc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS paths    (id INTEGER PRIMARY KEY, parent_id INTEGER, name TEXT NOT NULL,
                                             path TEXT NOT NULL COLLATE NOCASE, is_dir INTEGER NOT NULL,
                                             UNIQUE (path, is_dir));
        CREATE INDEX IF NOT EXISTS paths_parent ON paths(parent_id);
        CREATE TABLE IF NOT EXISTS files    (version_id INTEGER NOT NULL, path_id INTEGER NOT NULL, size INTEGER NOT NULL,
                                             mtime_ticks INTEGER NOT NULL, hash INTEGER,
                                             PRIMARY KEY (version_id, path_id)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS files_path ON files(path_id, version_id);
        CREATE TABLE IF NOT EXISTS dirs     (version_id INTEGER NOT NULL, path_id INTEGER NOT NULL, size INTEGER NOT NULL,
                                             files INTEGER NOT NULL,
                                             PRIMARY KEY (version_id, path_id)) WITHOUT ROWID;
        INSERT OR IGNORE INTO meta(key, value) VALUES ('schema_version', '1');
        """;

    private readonly string _connectionString;
    private readonly object _writeGate = new();

    private VersionIndex(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,   // no file handle outlives a call, so the file can be deleted and rebuilt at any time
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Opens the index at <paramref name="databasePath"/>, creating it when missing. A file that is not a database of
    /// this schema version is deleted and created anew.
    /// </summary>
    public static VersionIndex Open(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var index = new VersionIndex(databasePath);
        if (!index.HasCurrentSchema())
            index.DeleteFiles();
        try
        {
            index.CreateSchema();
        }
        catch (SqliteException)
        {
            index.DeleteFiles();   // e.g. a file that only looked like a database
            index.CreateSchema();
        }
        return index;
    }

    /// <summary>
    /// Brings the index in line with the version folders listed by <see cref="VersionCatalog.List"/>: rows of folders
    /// that are no longer listed are removed; listed folders that are new or whose stamp changed are imported, each in
    /// its own transaction. Cancellation throws and leaves every committed version intact.
    /// </summary>
    public IndexSyncResult Sync(IReadOnlyList<VersionInfo> folders, IProgress<IndexSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var known = ReadStamps();
        var listed = new HashSet<string>(folders.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var (name, entry) in known)
        {
            if (listed.Contains(name))
                continue;
            lock (_writeGate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                DeleteVersion(connection, transaction, entry.Id);
                transaction.Commit();
            }
            removed++;
        }

        var imported = 0;
        var unchanged = 0;
        var errors = new List<string>();
        for (var i = 0; i < folders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folders[i];
            var current = i + 1;
            string stamp;
            try
            {
                stamp = StampOf(folder.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{folder.Name}: {ex.Message}");
                continue;
            }

            if (known.TryGetValue(folder.Name, out var row) && row.Stamp == stamp)
            {
                unchanged++;
                progress?.Report(new IndexSyncProgress(current, folders.Count, folder.Name, 0, Finished: true));
                continue;
            }

            progress?.Report(new IndexSyncProgress(current, folders.Count, folder.Name, 0, Finished: false));
            try
            {
                var files = Import(folder, stamp, cancellationToken, count =>
                    progress?.Report(new IndexSyncProgress(current, folders.Count, folder.Name, count, Finished: false)));
                imported++;
                progress?.Report(new IndexSyncProgress(current, folders.Count, folder.Name, files, Finished: true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{folder.Name}: {ex.Message}");
            }
        }
        return new IndexSyncResult(imported, unchanged, removed, errors);
    }

    /// <summary>The indexed versions, oldest first.</summary>
    public IReadOnlyList<IndexedVersion> Versions()
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            SELECT id, name, local_time, ownership, source, origin, file_count, total_bytes
            FROM versions ORDER BY local_time, name
            """);
        using var reader = command.ExecuteReader();
        var versions = new List<IndexedVersion>();
        while (reader.Read())
            versions.Add(ReadVersion(reader, 0));
        return versions;
    }

    /// <summary>
    /// Adds (or replaces) a version from a manifest in memory, e.g. the one a backup run has just written, so that it
    /// does not have to be read again over the network.
    /// </summary>
    public void Add(VersionInfo version, BackupManifest manifest)
    {
        string stamp;
        try
        {
            stamp = StampOf(version.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stamp = "m:unknown";   // the next sync imports it again from the folder
        }

        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var writer = BeginVersion(connection, transaction, version, stamp, IndexOrigin.Manifest);
            foreach (var file in manifest.Files)
            {
                if (file is not null)
                    writer.AddFile(file);
            }
            writer.Finish(manifest.Source);
            transaction.Commit();
        }
    }

    /// <summary>
    /// The change detector of a version folder: the manifest's length and last write time when there is a manifest,
    /// otherwise the folder's last write time and the number of entries directly inside it.
    /// </summary>
    public static string StampOf(string versionFolder)
    {
        var manifest = new FileInfo(Path.Combine(versionFolder, VersionName.ManifestFileName));
        if (manifest.Exists)
            return string.Create(CultureInfo.InvariantCulture, $"m:{manifest.Length}:{manifest.LastWriteTimeUtc.Ticks}");

        var folder = new DirectoryInfo(versionFolder);
        if (!folder.Exists)
            throw new DirectoryNotFoundException($"The version folder \"{versionFolder}\" does not exist.");
        var count = folder.EnumerateFileSystemInfos().Count();
        return string.Create(CultureInfo.InvariantCulture, $"s:{folder.LastWriteTimeUtc.Ticks}:{count}");
    }

    /// <summary>Imports one version (manifest when readable, otherwise a scan); returns the number of files.</summary>
    private int Import(VersionInfo folder, string stamp, CancellationToken cancellationToken, Action<int> onProgress)
    {
        var manifestPath = Path.Combine(folder.Path, VersionName.ManifestFileName);
        if (File.Exists(manifestPath))
        {
            try
            {
                return ImportOnce(folder, stamp, IndexOrigin.Manifest, cancellationToken, onProgress, writer =>
                {
                    var summary = ManifestStream.Read(manifestPath, writer.AddFile, cancellationToken);
                    return summary.Source;
                });
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Unreadable manifest: fall back to the files themselves.
            }
        }

        return ImportOnce(folder, stamp, IndexOrigin.Scan, cancellationToken, onProgress, writer =>
        {
            foreach (var file in ScanFiles(folder.Path, cancellationToken))
                writer.AddFile(file);
            return null;
        });
    }

    private int ImportOnce(VersionInfo folder, string stamp, IndexOrigin origin, CancellationToken cancellationToken,
        Action<int> onProgress, Func<VersionWriter, string?> fill)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var writer = BeginVersion(connection, transaction, folder, stamp, origin);
            writer.CancellationToken = cancellationToken;
            writer.OnProgress = onProgress;
            var source = fill(writer);
            writer.Finish(source);
            transaction.Commit();   // not reached on cancellation: disposing the transaction rolls the version back
            return writer.FileCount;
        }
    }

    /// <summary>The files of a version folder without a usable manifest: sizes and times, no hashes, no links.</summary>
    private static IEnumerable<ManifestFile> ScanFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<(DirectoryInfo Folder, string Prefix)>();
        pending.Push((new DirectoryInfo(root), ""));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (folder, prefix) = pending.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = folder.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (prefix.Length > 0 && ex is IOException or UnauthorizedAccessException)
            {
                continue;   // an unreadable subfolder is left out; an unreadable version folder is an error
            }

            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;   // links are not followed
                var relative = prefix + entry.Name;
                if (entry is DirectoryInfo directory)
                    pending.Push((directory, relative + "/"));
                else if (entry is FileInfo file &&
                         !(prefix.Length == 0 && file.Name.Equals(VersionName.ManifestFileName, StringComparison.OrdinalIgnoreCase)))
                    yield return new ManifestFile(relative, file.Length, file.LastWriteTimeUtc, "");
            }
        }
    }

    private VersionWriter BeginVersion(SqliteConnection connection, SqliteTransaction transaction, VersionInfo version,
        string stamp, IndexOrigin origin)
    {
        using (var find = Command(connection, transaction, "SELECT id FROM versions WHERE name = $name"))
        {
            find.Parameters.AddWithValue("$name", version.Name);
            if (find.ExecuteScalar() is long existing)
                DeleteVersion(connection, transaction, existing);
        }

        using var insert = Command(connection, transaction, """
            INSERT INTO versions (name, local_time, ownership, source, origin, stamp, file_count, total_bytes, indexed_utc)
            VALUES ($name, $time, $ownership, NULL, $origin, $stamp, 0, 0, $now) RETURNING id
            """);
        insert.Parameters.AddWithValue("$name", version.Name);
        insert.Parameters.AddWithValue("$time", FormatTime(version.LocalTime));
        insert.Parameters.AddWithValue("$ownership", version.Ownership.ToString());
        insert.Parameters.AddWithValue("$origin", origin.ToString());
        insert.Parameters.AddWithValue("$stamp", stamp);
        insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        var id = (long)insert.ExecuteScalar()!;
        return new VersionWriter(connection, transaction, id);
    }

    private static void DeleteVersion(SqliteConnection connection, SqliteTransaction transaction, long versionId)
    {
        using var delete = Command(connection, transaction, """
            DELETE FROM files WHERE version_id = $id;
            DELETE FROM dirs WHERE version_id = $id;
            DELETE FROM versions WHERE id = $id;
            """);
        delete.Parameters.AddWithValue("$id", versionId);
        delete.ExecuteNonQuery();
    }

    private Dictionary<string, (long Id, string Stamp)> ReadStamps()
    {
        var stamps = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
        using var connection = OpenConnection();
        using var command = Command(connection, null, "SELECT id, name, stamp FROM versions");
        using var reader = command.ExecuteReader();
        while (reader.Read())
            stamps[reader.GetString(1)] = (reader.GetInt64(0), reader.GetString(2));
        return stamps;
    }

    private bool HasCurrentSchema()
    {
        if (!File.Exists(DatabasePath))
            return true;
        try
        {
            using var connection = OpenConnection();
            using var command = Command(connection, null, "SELECT value FROM meta WHERE key = 'schema_version'");
            return command.ExecuteScalar() is string value &&
                   value == SchemaVersion.ToString(CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private void CreateSchema()
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, Schema);
        command.ExecuteNonQuery();
    }

    private void DeleteFiles()
    {
        foreach (var suffix in (ReadOnlySpan<string>)["", "-wal", "-shm"])
        {
            var path = DatabasePath + suffix;
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var pragma = Command(connection, null, "PRAGMA synchronous = NORMAL;");
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();   // a file that is not a database fails here; its handle must not stay open
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static string FormatTime(DateTime localTime) => localTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static DateTime ParseTime(string text) =>
        DateTime.ParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static IndexedVersion ReadVersion(SqliteDataReader reader, int first) =>
        new(reader.GetInt64(first),
            reader.GetString(first + 1),
            ParseTime(reader.GetString(first + 2)),
            Enum.TryParse<VersionOwnership>(reader.GetString(first + 3), out var ownership) ? ownership : VersionOwnership.Unreadable,
            reader.IsDBNull(first + 4) ? null : reader.GetString(first + 4),
            Enum.TryParse<IndexOrigin>(reader.GetString(first + 5), out var origin) ? origin : IndexOrigin.Scan,
            (int)reader.GetInt64(first + 6),
            reader.GetInt64(first + 7));

    /// <summary>"xxh64:" plus 16 hex digits as a signed 64-bit number; null for anything else.</summary>
    public static long? ParseHash(string? hash)
    {
        const string prefix = "xxh64:";
        if (hash is null || !hash.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || hash.Length != prefix.Length + 16)
            return null;
        return ulong.TryParse(hash.AsSpan(prefix.Length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            ? unchecked((long)value)
            : null;
    }

    /// <summary>Writes the files of one version inside its transaction: interns paths and rolls up folder totals.</summary>
    private sealed class VersionWriter
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly long _versionId;
        private readonly Dictionary<string, long> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<long, long?> _dirParents = [];
        private readonly Dictionary<long, (long Size, int Files)> _dirTotals = [];
        private readonly SqliteCommand _insertPath;
        private readonly SqliteCommand _insertFile;

        public VersionWriter(SqliteConnection connection, SqliteTransaction transaction, long versionId)
        {
            _connection = connection;
            _transaction = transaction;
            _versionId = versionId;

            using (var load = Command(connection, transaction, "SELECT id, path, is_dir, parent_id FROM paths"))
            using (var reader = load.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    var isDir = reader.GetInt64(2) != 0;
                    _paths[Key(reader.GetString(1), isDir)] = id;
                    if (isDir)
                        _dirParents[id] = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                }
            }

            _insertPath = Command(connection, transaction,
                "INSERT INTO paths (parent_id, name, path, is_dir) VALUES ($parent, $name, $path, $dir) RETURNING id");
            _insertPath.Parameters.Add("$parent", SqliteType.Integer);
            _insertPath.Parameters.Add("$name", SqliteType.Text);
            _insertPath.Parameters.Add("$path", SqliteType.Text);
            _insertPath.Parameters.Add("$dir", SqliteType.Integer);

            _insertFile = Command(connection, transaction,
                "INSERT OR REPLACE INTO files (version_id, path_id, size, mtime_ticks, hash) VALUES ($v, $p, $size, $mtime, $hash)");
            _insertFile.Parameters.AddWithValue("$v", versionId);
            _insertFile.Parameters.Add("$p", SqliteType.Integer);
            _insertFile.Parameters.Add("$size", SqliteType.Integer);
            _insertFile.Parameters.Add("$mtime", SqliteType.Integer);
            _insertFile.Parameters.Add("$hash", SqliteType.Integer);

            EnsureDirectory("");
        }

        public CancellationToken CancellationToken { get; set; }
        public Action<int>? OnProgress { get; set; }
        public int FileCount { get; private set; }
        public long TotalBytes { get; private set; }

        public void AddFile(ManifestFile file)
        {
            CancellationToken.ThrowIfCancellationRequested();
            var path = file.Path.Replace('\\', '/').Trim('/');
            if (path.Length == 0)
                return;
            var slash = path.LastIndexOf('/');
            var parentId = EnsureDirectory(slash < 0 ? "" : path[..slash]);
            var pathId = EnsurePath(path, isDir: false, parentId);

            _insertFile.Parameters["$p"].Value = pathId;
            _insertFile.Parameters["$size"].Value = file.Size;
            _insertFile.Parameters["$mtime"].Value = file.MtimeUtc.Ticks;
            _insertFile.Parameters["$hash"].Value = (object?)ParseHash(file.Hash) ?? DBNull.Value;
            _insertFile.ExecuteNonQuery();

            for (long? dir = parentId; dir is { } id; dir = _dirParents[id])
            {
                var (size, files) = _dirTotals.GetValueOrDefault(id);
                _dirTotals[id] = (size + file.Size, files + 1);
            }

            FileCount++;
            TotalBytes += file.Size;
            if (FileCount % ProgressEvery == 0)
                OnProgress?.Invoke(FileCount);
        }

        /// <summary>Writes the folder totals (the root always gets a row) and the version's totals and source.</summary>
        public void Finish(string? source)
        {
            var rootId = EnsureDirectory("");
            if (!_dirTotals.ContainsKey(rootId))
                _dirTotals[rootId] = (0, 0);

            using (var insert = Command(_connection, _transaction,
                       "INSERT INTO dirs (version_id, path_id, size, files) VALUES ($v, $p, $size, $files)"))
            {
                insert.Parameters.AddWithValue("$v", _versionId);
                insert.Parameters.Add("$p", SqliteType.Integer);
                insert.Parameters.Add("$size", SqliteType.Integer);
                insert.Parameters.Add("$files", SqliteType.Integer);
                foreach (var (pathId, (size, files)) in _dirTotals)
                {
                    insert.Parameters["$p"].Value = pathId;
                    insert.Parameters["$size"].Value = size;
                    insert.Parameters["$files"].Value = files;
                    insert.ExecuteNonQuery();
                }
            }

            using var update = Command(_connection, _transaction,
                "UPDATE versions SET source = $source, file_count = $count, total_bytes = $bytes WHERE id = $id");
            update.Parameters.AddWithValue("$source", string.IsNullOrEmpty(source) ? DBNull.Value : source);
            update.Parameters.AddWithValue("$count", FileCount);
            update.Parameters.AddWithValue("$bytes", TotalBytes);
            update.Parameters.AddWithValue("$id", _versionId);
            update.ExecuteNonQuery();
            _insertPath.Dispose();
            _insertFile.Dispose();
        }

        private long EnsureDirectory(string path)
        {
            if (_paths.TryGetValue(Key(path, isDir: true), out var id))
                return id;
            long? parentId = null;
            if (path.Length > 0)
            {
                var slash = path.LastIndexOf('/');
                parentId = EnsureDirectory(slash < 0 ? "" : path[..slash]);
            }
            id = EnsurePath(path, isDir: true, parentId);
            _dirParents[id] = parentId;
            return id;
        }

        private long EnsurePath(string path, bool isDir, long? parentId)
        {
            var key = Key(path, isDir);
            if (_paths.TryGetValue(key, out var id))
                return id;
            _insertPath.Parameters["$parent"].Value = (object?)parentId ?? DBNull.Value;
            _insertPath.Parameters["$name"].Value = path[(path.LastIndexOf('/') + 1)..];
            _insertPath.Parameters["$path"].Value = path;
            _insertPath.Parameters["$dir"].Value = isDir ? 1 : 0;
            id = (long)_insertPath.ExecuteScalar()!;
            _paths[key] = id;
            return id;
        }

        private static string Key(string path, bool isDir) => (isDir ? "d:" : "f:") + path;
    }
}
```

Notes for the reviewer:
- `OpenConnection` disposes the connection when the first statement fails: a file that is not a database fails there, and its handle must be closed before `Open` deletes the file.
- `Import` tries the manifest first and falls back to a scan in a new transaction when the manifest is unreadable (`JsonException`, `IOException`, `UnauthorizedAccessException`); cancellation is not caught and rolls the version back.
- `VersionWriter` loads all known paths once per import (one dictionary, case-insensitive like the `NOCASE` column) and keeps `dirs` totals for every ancestor folder of every file.

Create `src/ReBackup.Core/Versions/VersionIndexSet.cs`:

```csharp
using ReBackup.Core.Backup;

namespace ReBackup.Core.Versions;

/// <summary>The version indexes of all plans: one database per plan id in one folder, opened on first use.</summary>
public sealed class VersionIndexSet : IVersionIndexSink
{
    private readonly Dictionary<string, VersionIndex> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public VersionIndexSet(string directory) => Directory = directory;

    /// <summary><c>%LOCALAPPDATA%\ReBackup\index</c>: a machine-local cache, never in the (roaming) config folder.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReBackup", "index");

    public string Directory { get; }

    /// <summary><c>&lt;directory&gt;\&lt;planId&gt;.db</c>.</summary>
    /// <exception cref="ArgumentException">The id cannot be a file name.</exception>
    public string PathFor(string planId)
    {
        if (string.IsNullOrWhiteSpace(planId) || planId is "." or ".." ||
            planId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"\"{planId}\" is not a usable plan id.", nameof(planId));
        return Path.Combine(Directory, planId + ".db");
    }

    /// <summary>The plan's index, opened (and created or rebuilt when needed) on first use.</summary>
    public VersionIndex For(string planId)
    {
        var path = PathFor(planId);
        lock (_gate)
        {
            if (!_open.TryGetValue(planId, out var index))
            {
                index = VersionIndex.Open(path);
                _open[planId] = index;
            }
            return index;
        }
    }

    public void Add(string planId, VersionInfo version, BackupManifest manifest) => For(planId).Add(version, manifest);
}
```

- [ ] **Step 10: Run the tests to see them pass, then the whole suite**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~ReBackup.Core.Tests.Versions"`
Expected: PASS, 25 tests (`Imports_a_large_manifest` takes about a second).

Run: `dotnet build tests/ReBackup.Core.Tests --no-incremental` then `dotnet test tests/ReBackup.Core.Tests`
Expected: 0 warnings; all 577 tests pass.

- [ ] **Step 11: Commit**

```bash
git add src/ReBackup.Core/ReBackup.Core.csproj src/ReBackup.Core/Versions tests/ReBackup.Core.Tests/TestSupport/VersionBuilder.cs tests/ReBackup.Core.Tests/TestSupport/SyncProgress.cs tests/ReBackup.Core.Tests/Versions
git commit -m "feat(core): local SQLite version index — schema, streaming manifest import, scan fallback, sync" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 2: Index queries — Children, Compare, History, Search

**Files:**
- Create: `src/ReBackup.Core/Versions/QueryModels.cs`, `src/ReBackup.Core/Versions/VersionIndex.Queries.cs`
- Create (tests): `tests/ReBackup.Core.Tests/Versions/VersionIndexQueryTests.cs`

**Interfaces:**
- Consumes (Task 1): `VersionIndex` (`Open`, `Sync`, `Versions`, private `OpenConnection`, `Command`, `ReadVersion`), `IndexedVersion`; test helpers `VersionBuilder`, `TempDir`.
- Produces (namespace `ReBackup.Core.Versions`):
  - `record IndexStats(long Size, int Files, DateTime? MtimeUtc, long? Hash)` (folders: rolled-up size and file count, no time/hash; files: `Files` = 1)
  - `record IndexChild(long PathId, string Name, string Path, bool IsDirectory, IndexStats? InVersion, IndexStats? InOther)`
  - `enum DiffStatus { Unchanged, Added, Changed, Deleted }`; `enum HistoryStatus { New, Changed, Unchanged, Deleted, Absent }`
  - `record HistoryEntry(IndexedVersion Version, bool Present, long? Size, DateTime? MtimeUtc, HistoryStatus Status)`; `record SearchHit(long PathId, string Path, string Name, bool IsDirectory, long Size)`
  - `VersionIndex`: `const int SearchLimit = 500`; `long? FindPath(string relativePath, bool isDirectory)` (`""` + `true` = root); `IReadOnlyList<IndexChild> Children(long versionId, long folderPathId, long? otherVersionId = null)` (folders first, then by name, ignoring case); `IReadOnlyDictionary<long, DiffStatus> Compare(long versionA, long versionB)` (every file and folder of A or B by path id); `IReadOnlyList<HistoryEntry> History(long filePathId)` (oldest version first); `IReadOnlyList<SearchHit> Search(long versionId, string pattern, int limit = SearchLimit)` (ordered by path; blank pattern → empty)

Compare runs four set queries joined on `path_id` and rolls folders up in memory: a folder present in both versions becomes Changed when any file below it is not Unchanged (walking up the parent chain, stopping at the first folder that is already Changed); folders only in A or only in B keep Deleted/Added.

- [ ] **Step 1: Write the failing tests**

Create `tests/ReBackup.Core.Tests/Versions/VersionIndexQueryTests.cs`:

```csharp
using FluentAssertions;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using static ReBackup.Core.Tests.TestSupport.VersionBuilder;

namespace ReBackup.Core.Tests.Versions;

public class VersionIndexQueryTests : IDisposable
{
    private static readonly DateTime Later = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly TempDir _tmp = new();
    private readonly string _target;
    private readonly VersionIndex _index;

    public VersionIndexQueryTests()
    {
        _target = _tmp.CreateDir("target");
        _index = VersionIndex.Open(_tmp.PathOf("index.db"));
    }

    public void Dispose() => _tmp.Dispose();

    private IReadOnlyList<IndexedVersion> Sync()
    {
        _index.Sync(List(_target));
        return _index.Versions();
    }

    private long Path(string path, bool isDirectory = false) =>
        _index.FindPath(path, isDirectory) ?? throw new InvalidOperationException($"{path} is not indexed");

    private Dictionary<string, DiffStatus> ByPath(IReadOnlyDictionary<long, DiffStatus> statuses, params string[] paths) =>
        paths.ToDictionary(p => p, p => statuses[_index.FindPath(p.TrimEnd('/'), p.EndsWith('/'))!.Value]);

    [Fact]
    public void Lists_the_folders_and_files_of_a_folder()
    {
        Write(_target, "2026_09_30-14_05", [File("b.txt", "bravo"), File("A.txt", "alpha", Later), File("sub/c.txt", "charlie"), File("sub/x/d.txt", "d")]);
        var version = Sync().Single();

        var root = _index.Children(version.Id, Path("", isDirectory: true));

        root.Select(c => (c.Name, c.Path, c.IsDirectory)).Should().Equal(
            ("sub", "sub", true), ("A.txt", "A.txt", false), ("b.txt", "b.txt", false));
        root[0].InVersion.Should().Be(new IndexStats(8, 2, null, null));
        root[1].InVersion!.Size.Should().Be(5);
        root[1].InVersion!.MtimeUtc.Should().Be(Later);
        root[1].InVersion!.Hash.Should().NotBeNull();
        root.Should().OnlyContain(c => c.InOther == null);
        _index.Children(version.Id, Path("sub", isDirectory: true)).Select(c => c.Path).Should().Equal("sub/x", "sub/c.txt");
    }

    [Fact]
    public void Lists_the_entries_of_both_versions_of_a_comparison()
    {
        Write(_target, "2026_09_29-14_05", [File("old.txt", "old"), File("both.txt", "1")]);
        Write(_target, "2026_09_30-14_05", [File("new.txt", "new!"), File("both.txt", "22")]);
        var versions = Sync();

        var root = _index.Children(versions[0].Id, Path("", isDirectory: true), versions[1].Id);

        root.Select(c => (c.Name, c.InVersion?.Size, c.InOther?.Size)).Should().Equal(
            ("both.txt", 1L, 2L), ("new.txt", null, 4L), ("old.txt", 3L, null));
    }

    [Fact]
    public void Compares_by_hash_when_both_versions_have_one()
    {
        Write(_target, "2026_09_29-14_05", [File("same.txt", "same"), File("touched.txt", "same"), File("edited.txt", "abcd"), File("gone.txt", "x")]);
        Write(_target, "2026_09_30-14_05", [File("same.txt", "same"), File("touched.txt", "same", Later), File("edited.txt", "abce"), File("added.txt", "y")]);
        var versions = Sync();

        var statuses = _index.Compare(versions[0].Id, versions[1].Id);

        ByPath(statuses, "same.txt", "touched.txt", "edited.txt", "gone.txt", "added.txt").Should().Equal(new Dictionary<string, DiffStatus>
        {
            ["same.txt"] = DiffStatus.Unchanged,
            ["touched.txt"] = DiffStatus.Unchanged,   // only the time differs: the hash decides
            ["edited.txt"] = DiffStatus.Changed,      // same size, other content
            ["gone.txt"] = DiffStatus.Deleted,
            ["added.txt"] = DiffStatus.Added,
        });
    }

    [Fact]
    public void Compares_by_size_and_time_when_a_hash_is_missing()
    {
        Write(_target, "2026_09_29-14_05", [File("same.txt", "same"), File("touched.txt", "same"), File("edited.txt", "abcd")], withManifest: false);
        Write(_target, "2026_09_30-14_05", [File("same.txt", "same"), File("touched.txt", "same", Later), File("edited.txt", "abce")]);
        var versions = Sync();

        var statuses = _index.Compare(versions[0].Id, versions[1].Id);

        ByPath(statuses, "same.txt", "touched.txt", "edited.txt").Should().Equal(new Dictionary<string, DiffStatus>
        {
            ["same.txt"] = DiffStatus.Unchanged,
            ["touched.txt"] = DiffStatus.Changed,
            ["edited.txt"] = DiffStatus.Unchanged,   // same size and time: without hashes it counts as unchanged
        });
    }

    [Fact]
    public void Rolls_folders_up()
    {
        Write(_target, "2026_09_29-14_05", [File("calm/a.txt", "a"), File("busy/deep/b.txt", "b"), File("busy/c.txt", "c"), File("old/d.txt", "d")]);
        Write(_target, "2026_09_30-14_05", [File("calm/a.txt", "a"), File("busy/deep/b.txt", "B!"), File("busy/c.txt", "c"), File("fresh/e.txt", "e")]);
        var versions = Sync();

        var statuses = _index.Compare(versions[0].Id, versions[1].Id);

        ByPath(statuses, "/", "calm/", "busy/", "busy/deep/", "old/", "fresh/").Should().Equal(new Dictionary<string, DiffStatus>
        {
            ["/"] = DiffStatus.Changed,
            ["calm/"] = DiffStatus.Unchanged,
            ["busy/"] = DiffStatus.Changed,
            ["busy/deep/"] = DiffStatus.Changed,
            ["old/"] = DiffStatus.Deleted,
            ["fresh/"] = DiffStatus.Added,
        });
    }

    [Fact]
    public void Compares_in_the_given_direction()
    {
        Write(_target, "2026_09_29-14_05", [File("gone.txt", "x")]);
        Write(_target, "2026_09_30-14_05", [File("added.txt", "y")]);
        var versions = Sync();

        var statuses = _index.Compare(versions[1].Id, versions[0].Id);

        ByPath(statuses, "gone.txt", "added.txt").Should().Equal(new Dictionary<string, DiffStatus>
        {
            ["gone.txt"] = DiffStatus.Added,
            ["added.txt"] = DiffStatus.Deleted,
        });
    }

    [Fact]
    public void Gives_the_history_of_a_file()
    {
        Write(_target, "2026_09_25-14_05", [File("other.txt", "o")]);
        Write(_target, "2026_09_26-14_05", [File("f.txt", "one")]);
        Write(_target, "2026_09_27-14_05", [File("f.txt", "one")]);
        Write(_target, "2026_09_28-14_05", [File("f.txt", "two", Later)]);
        Write(_target, "2026_09_29-14_05", [File("other.txt", "o")]);
        Write(_target, "2026_09_30-14_05", [File("other.txt", "o")]);
        Write(_target, "2026_10_01-14_05", [File("f.txt", "two", Later)]);
        Sync();

        var history = _index.History(Path("f.txt"));

        history.Select(h => (h.Version.Name[..10], h.Present, h.Status)).Should().Equal(
            ("2026_09_25", false, HistoryStatus.Absent),
            ("2026_09_26", true, HistoryStatus.New),
            ("2026_09_27", true, HistoryStatus.Unchanged),
            ("2026_09_28", true, HistoryStatus.Changed),
            ("2026_09_29", false, HistoryStatus.Deleted),
            ("2026_09_30", false, HistoryStatus.Absent),
            ("2026_10_01", true, HistoryStatus.Unchanged));   // against the last version that had it
        history[3].Size.Should().Be(3);
        history[3].MtimeUtc.Should().Be(Later);
        history[4].Size.Should().BeNull();
    }

    [Fact]
    public void Searches_names_by_substring_ignoring_case()
    {
        Write(_target, "2026_09_30-14_05", [File("Report.docx", "r"), File("docs/readme.md", "m"), File("docs/REPORTS/q1.xlsx", "q"), File("other.txt", "o")]);
        var version = Sync().Single();

        var hits = _index.Search(version.Id, "report");

        hits.Select(h => (h.Path, h.IsDirectory)).Should().Equal(("docs/REPORTS", true), ("Report.docx", false));
        hits[0].Size.Should().Be(1);
    }

    [Fact]
    public void Searches_names_by_wildcard_for_the_whole_name()
    {
        Write(_target, "2026_09_30-14_05", [File("a.cs", "1"), File("src/b.CS", "2"), File("src/b.csproj", "3"), File("c1.txt", "4"), File("c12.txt", "5")]);
        var version = Sync().Single();

        _index.Search(version.Id, "*.cs").Select(h => h.Path).Should().Equal("a.cs", "src/b.CS");
        _index.Search(version.Id, "c?.txt").Select(h => h.Path).Should().Equal("c1.txt");
        _index.Search(version.Id, "  ").Should().BeEmpty();
    }

    [Fact]
    public void Limits_the_number_of_search_hits()
    {
        Write(_target, "2026_09_30-14_05", Enumerable.Range(0, 30).Select(i => File($"f{i:00}.txt", "x")));
        var version = Sync().Single();

        _index.Search(version.Id, "f", limit: 10).Select(h => h.Path).Should().HaveCount(10).And.StartWith("f00.txt");
        VersionIndex.SearchLimit.Should().Be(500);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~VersionIndexQueryTests"`
Expected: build errors — `FindPath`, `Children`, `IndexStats`, `DiffStatus`, `Compare`, `History`, `HistoryStatus`, `Search`, `SearchLimit` do not exist.

- [ ] **Step 3: Implement the query models and the queries**

Create `src/ReBackup.Core/Versions/QueryModels.cs`:

```csharp
namespace ReBackup.Core.Versions;

/// <summary>Size and file count of an entry in one version; for files also the time and the hash (null: no hash).</summary>
public sealed record IndexStats(long Size, int Files, DateTime? MtimeUtc, long? Hash);

/// <summary>
/// An entry of a folder. <paramref name="InVersion"/> is null when it exists only in the other version of a
/// comparison, <paramref name="InOther"/> when it exists only in the version itself (or nothing is compared).
/// </summary>
public sealed record IndexChild(long PathId, string Name, string Path, bool IsDirectory, IndexStats? InVersion,
    IndexStats? InOther);

/// <summary>How an entry differs between version A and version B (§11): Added = only in B, Deleted = only in A.</summary>
public enum DiffStatus { Unchanged, Added, Changed, Deleted }

/// <summary>A file's state in one version, compared with the previous version that had it.</summary>
public enum HistoryStatus { New, Changed, Unchanged, Deleted, Absent }

public sealed record HistoryEntry(IndexedVersion Version, bool Present, long? Size, DateTime? MtimeUtc, HistoryStatus Status);

public sealed record SearchHit(long PathId, string Path, string Name, bool IsDirectory, long Size);
```

Create `src/ReBackup.Core/Versions/VersionIndex.Queries.cs`:

```csharp
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ReBackup.Core.Versions;

public sealed partial class VersionIndex
{
    /// <summary>Search results are capped at this many entries.</summary>
    public const int SearchLimit = 500;

    /// <summary>The id of a path ("" = the version root, forward slashes); null when no version has it.</summary>
    public long? FindPath(string relativePath, bool isDirectory)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, "SELECT id FROM paths WHERE path = $path AND is_dir = $dir");
        command.Parameters.AddWithValue("$path", relativePath.Replace('\\', '/').Trim('/'));
        command.Parameters.AddWithValue("$dir", isDirectory ? 1 : 0);
        return command.ExecuteScalar() as long?;
    }

    /// <summary>
    /// The folders and files directly inside a folder of a version, folders first, then by name. With
    /// <paramref name="otherVersionId"/>, entries that exist only in that version are included too
    /// (<see cref="IndexChild.InVersion"/> null).
    /// </summary>
    public IReadOnlyList<IndexChild> Children(long versionId, long folderPathId, long? otherVersionId = null)
    {
        using var connection = OpenConnection();
        var own = ChildStats(connection, versionId, folderPathId);
        var other = otherVersionId is { } otherId ? ChildStats(connection, otherId, folderPathId) : [];

        var children = new List<IndexChild>(own.Count + other.Count);
        foreach (var (pathId, entry) in own)
        {
            other.TryGetValue(pathId, out var inOther);
            children.Add(new IndexChild(pathId, entry.Name, entry.Path, entry.IsDirectory, entry.Stats, inOther.Stats));
        }
        foreach (var (pathId, entry) in other)
        {
            if (!own.ContainsKey(pathId))
                children.Add(new IndexChild(pathId, entry.Name, entry.Path, entry.IsDirectory, null, entry.Stats));
        }

        children.Sort((a, b) => a.IsDirectory != b.IsDirectory
            ? (a.IsDirectory ? -1 : 1)
            : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return children;
    }

    /// <summary>
    /// The status of every file and folder of version A and version B (§11): Added = only in B, Deleted = only in A;
    /// files in both are compared by hash when both have one, otherwise by size and time. A folder in both is Changed
    /// when anything below it differs.
    /// </summary>
    public IReadOnlyDictionary<long, DiffStatus> Compare(long versionA, long versionB)
    {
        using var connection = OpenConnection();
        var status = new Dictionary<long, DiffStatus>();
        var dirParents = new Dictionary<long, long?>();
        var changedFileParents = new List<long>();

        // Folders: in A (and maybe B), then only in B.
        using (var command = Command(connection, null, """
                   SELECT d.path_id, p.parent_id,
                          EXISTS (SELECT 1 FROM dirs o WHERE o.version_id = $b AND o.path_id = d.path_id)
                   FROM dirs d JOIN paths p ON p.id = d.path_id WHERE d.version_id = $a
                   UNION ALL
                   SELECT d.path_id, p.parent_id, -1
                   FROM dirs d JOIN paths p ON p.id = d.path_id WHERE d.version_id = $b
                     AND NOT EXISTS (SELECT 1 FROM dirs o WHERE o.version_id = $a AND o.path_id = d.path_id)
                   """))
        {
            command.Parameters.AddWithValue("$a", versionA);
            command.Parameters.AddWithValue("$b", versionB);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var pathId = reader.GetInt64(0);
                dirParents[pathId] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                status[pathId] = reader.GetInt64(2) switch
                {
                    1 => DiffStatus.Unchanged,
                    0 => DiffStatus.Deleted,
                    _ => DiffStatus.Added,
                };
            }
        }

        // Files: in A (compared with B), then only in B.
        using (var command = Command(connection, null, """
                   SELECT a.path_id, p.parent_id, a.size, a.mtime_ticks, a.hash, b.size, b.mtime_ticks, b.hash
                   FROM files a JOIN paths p ON p.id = a.path_id
                   LEFT JOIN files b ON b.version_id = $b AND b.path_id = a.path_id
                   WHERE a.version_id = $a
                   UNION ALL
                   SELECT b.path_id, p.parent_id, NULL, NULL, NULL, b.size, b.mtime_ticks, b.hash
                   FROM files b JOIN paths p ON p.id = b.path_id
                   WHERE b.version_id = $b
                     AND NOT EXISTS (SELECT 1 FROM files a WHERE a.version_id = $a AND a.path_id = b.path_id)
                   """))
        {
            command.Parameters.AddWithValue("$a", versionA);
            command.Parameters.AddWithValue("$b", versionB);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var pathId = reader.GetInt64(0);
                var inA = !reader.IsDBNull(2);
                var inB = !reader.IsDBNull(5);
                var fileStatus = !inB ? DiffStatus.Deleted
                    : !inA ? DiffStatus.Added
                    : FileDiffers(reader) ? DiffStatus.Changed
                    : DiffStatus.Unchanged;
                status[pathId] = fileStatus;
                if (fileStatus != DiffStatus.Unchanged && !reader.IsDBNull(1))
                    changedFileParents.Add(reader.GetInt64(1));
            }
        }

        // Roll up: a folder present in both versions is Changed when anything below it is not Unchanged.
        foreach (var parent in changedFileParents)
        {
            for (long? dir = parent; dir is { } id; dir = dirParents.GetValueOrDefault(id))
            {
                var current = status.GetValueOrDefault(id, DiffStatus.Unchanged);
                if (current == DiffStatus.Changed)
                    break;   // everything above is Changed already
                if (current == DiffStatus.Unchanged)
                    status[id] = DiffStatus.Changed;
            }
        }
        return status;
    }

    /// <summary>
    /// A file in every indexed version, oldest first, with its status against the previous version that had it.
    /// </summary>
    public IReadOnlyList<HistoryEntry> History(long filePathId)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            SELECT v.id, v.name, v.local_time, v.ownership, v.source, v.origin, v.file_count, v.total_bytes,
                   f.size, f.mtime_ticks, f.hash
            FROM versions v LEFT JOIN files f ON f.version_id = v.id AND f.path_id = $path
            ORDER BY v.local_time, v.name
            """);
        command.Parameters.AddWithValue("$path", filePathId);
        using var reader = command.ExecuteReader();

        var history = new List<HistoryEntry>();
        (long Size, long Mtime, long? Hash)? lastPresent = null;
        var previousPresent = false;
        while (reader.Read())
        {
            var version = ReadVersion(reader, 0);
            if (reader.IsDBNull(8))
            {
                history.Add(new HistoryEntry(version, false, null, null,
                    previousPresent ? HistoryStatus.Deleted : HistoryStatus.Absent));
                previousPresent = false;
                continue;
            }

            var current = (Size: reader.GetInt64(8), Mtime: reader.GetInt64(9), Hash: reader.IsDBNull(10) ? (long?)null : reader.GetInt64(10));
            var historyStatus = lastPresent is not { } last ? HistoryStatus.New
                : Differs(last.Size, last.Mtime, last.Hash, current.Size, current.Mtime, current.Hash) ? HistoryStatus.Changed
                : HistoryStatus.Unchanged;
            history.Add(new HistoryEntry(version, true, current.Size, new DateTime(current.Mtime, DateTimeKind.Utc), historyStatus));
            lastPresent = current;
            previousPresent = true;
        }
        return history;
    }

    /// <summary>
    /// Files and folders of a version whose name matches <paramref name="pattern"/>: a substring, or with <c>*</c> and
    /// <c>?</c> a wildcard pattern for the whole name; both ignore case. At most <paramref name="limit"/> hits, by path.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(long versionId, string pattern, int limit = SearchLimit)
    {
        if (NameMatcher(pattern) is not { } matches)
            return [];

        using var connection = OpenConnection();
        connection.CreateFunction("rb_match", (string name) => matches(name), isDeterministic: true);
        using var command = Command(connection, null, """
            SELECT p.id, p.path, p.name, 1, d.size FROM dirs d JOIN paths p ON p.id = d.path_id
            WHERE d.version_id = $v AND p.path <> '' AND rb_match(p.name)
            UNION ALL
            SELECT p.id, p.path, p.name, 0, f.size FROM files f JOIN paths p ON p.id = f.path_id
            WHERE f.version_id = $v AND rb_match(p.name)
            ORDER BY 2 COLLATE NOCASE
            LIMIT $limit
            """);
        command.Parameters.AddWithValue("$v", versionId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var hits = new List<SearchHit>();
        while (reader.Read())
            hits.Add(new SearchHit(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0, reader.GetInt64(4)));
        return hits;
    }

    /// <summary>Null for a blank pattern.</summary>
    private static Func<string, bool>? NameMatcher(string pattern)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0)
            return null;
        if (pattern.IndexOfAny(['*', '?']) < 0)
            return name => name.Contains(pattern, StringComparison.OrdinalIgnoreCase);

        var regex = new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return regex.IsMatch;
    }

    private static bool FileDiffers(SqliteDataReader reader) =>
        Differs(reader.GetInt64(2), reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.GetInt64(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetInt64(7));

    /// <summary>§11: both hashed → by hash; otherwise by size and time.</summary>
    private static bool Differs(long sizeA, long mtimeA, long? hashA, long sizeB, long mtimeB, long? hashB) =>
        hashA is { } a && hashB is { } b ? a != b : sizeA != sizeB || mtimeA != mtimeB;

    private static Dictionary<long, (string Name, string Path, bool IsDirectory, IndexStats Stats)> ChildStats(
        SqliteConnection connection, long versionId, long folderPathId)
    {
        using var command = Command(connection, null, """
            SELECT p.id, p.name, p.path, 1, d.size, d.files, NULL, NULL
            FROM paths p JOIN dirs d ON d.path_id = p.id AND d.version_id = $v
            WHERE p.parent_id = $folder AND p.is_dir = 1
            UNION ALL
            SELECT p.id, p.name, p.path, 0, f.size, 1, f.mtime_ticks, f.hash
            FROM paths p JOIN files f ON f.path_id = p.id AND f.version_id = $v
            WHERE p.parent_id = $folder AND p.is_dir = 0
            """);
        command.Parameters.AddWithValue("$v", versionId);
        command.Parameters.AddWithValue("$folder", folderPathId);
        using var reader = command.ExecuteReader();
        var children = new Dictionary<long, (string, string, bool, IndexStats)>();
        while (reader.Read())
        {
            var stats = new IndexStats(reader.GetInt64(4), (int)reader.GetInt64(5),
                reader.IsDBNull(6) ? null : new DateTime(reader.GetInt64(6), DateTimeKind.Utc),
                reader.IsDBNull(7) ? null : reader.GetInt64(7));
            children[reader.GetInt64(0)] = (reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0, stats);
        }
        return children;
    }
}
```

- [ ] **Step 4: Run the tests to see them pass, then the whole suite**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~VersionIndexQueryTests"`
Expected: PASS, 10 tests.

Run: `dotnet build tests/ReBackup.Core.Tests --no-incremental` then `dotnet test tests/ReBackup.Core.Tests`
Expected: 0 warnings; all 587 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/ReBackup.Core/Versions/QueryModels.cs src/ReBackup.Core/Versions/VersionIndex.Queries.cs tests/ReBackup.Core.Tests/Versions/VersionIndexQueryTests.cs
git commit -m "feat(core): version index queries — children, compare with roll-up, file history, name search" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 3: Restorer — plan, conflict policies, safety

**Files:**
- Create: `src/ReBackup.Core/Versions/Restorer.cs`
- Create (tests): `tests/ReBackup.Core.Tests/Versions/RestorerTests.cs`

**Interfaces:**
- Consumes: `PathUtil.Normalize`, `PathUtil.IsSameOrInside`, `VersionName.TryParseAny`, `VersionName.ManifestFileName`; test helpers `VersionBuilder`, `TempDir`, `Junction`, `SyncProgress<T>`.
- Produces (namespace `ReBackup.Core.Versions`):
  - `enum RestoreMode { Original, ToFolder }`; `enum ConflictPolicy { Overwrite, Skip, KeepBoth }`
  - `record RestoreFile(string Source, string Destination, long Size)`
  - `sealed class RestorePlan { string VersionFolder; string DestinationRoot; DateTime VersionTime; IReadOnlyList<string> Directories; IReadOnlyList<RestoreFile> Files; IReadOnlyList<RestoreFile> Conflicts; long TotalBytes }`
  - `record RestoreFailure(string Path, string Reason)`; `record RestoreResult(int Copied, int Skipped, int KeptBoth, IReadOnlyList<RestoreFailure> Failures, bool Canceled)`
  - `readonly record struct RestoreProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string CurrentFile)` with `double Fraction`
  - `static class Restorer`: `const string TempSuffix = ".rebackup-tmp"`; `RestorePlan Plan(string versionFolder, IReadOnlyList<string> relativePaths, string destinationRoot, RestoreMode mode)` (throws `ArgumentException` for traversal, rooted/drive paths, missing entries, links, the manifest, a relative destination; `DirectoryNotFoundException` for a missing version folder); `RestoreResult Run(RestorePlan plan, ConflictPolicy policy, IProgress<RestoreProgress>? progress = null, CancellationToken = default)` (never throws for a single file: locked / read-only / access denied become `Failures`); `string KeepBothName(string destination, DateTime versionTime)`

Mapping: Original → `destinationRoot + relative path` (the caller passes the version's source); ToFolder → `destinationRoot + the selected item's name`, a folder keeping its structure below it. `""` selects the whole version (its manifest is left out). Folders, also empty ones, are created; existing files at the destination that the version does not have are never touched.

- [ ] **Step 1: Write the failing tests**

Create `tests/ReBackup.Core.Tests/Versions/RestorerTests.cs`:

```csharp
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using static ReBackup.Core.Tests.TestSupport.VersionBuilder;

namespace ReBackup.Core.Tests.Versions;

public class RestorerTests : IDisposable
{
    private static readonly DateTime Old = new(2025, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    private readonly TempDir _tmp = new();
    private readonly string _version;
    private readonly string _dest;

    public RestorerTests()
    {
        _version = Write(_tmp.CreateDir("target"), "2026_09_30-14_05",
            [File("a.txt", "alpha", Old), File("docs/b.txt", "bravo"), File("docs/sub/c.txt", "charlie")]);
        Directory.CreateDirectory(Path.Combine(_version, "docs", "empty"));
        _dest = _tmp.CreateDir("dest");
    }

    public void Dispose() => _tmp.Dispose();

    private string Dest(string relative) => Path.Combine(_dest, relative);

    private string[] DestFiles() =>
        Directory.GetFiles(_dest, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_dest, f)).Order().ToArray();

    [Fact]
    public void Plans_a_file_and_a_folder_to_their_original_place()
    {
        var plan = Restorer.Plan(_version, ["a.txt", "docs"], _dest, RestoreMode.Original);

        plan.Files.Select(f => (Path.GetRelativePath(_version, f.Source), Path.GetRelativePath(_dest, f.Destination), f.Size))
            .Should().BeEquivalentTo([
                ("a.txt", "a.txt", 5L), (@"docs\b.txt", @"docs\b.txt", 5L), (@"docs\sub\c.txt", @"docs\sub\c.txt", 7L)]);
        plan.Directories.Select(d => Path.GetRelativePath(_dest, d)).Should().BeEquivalentTo([@"docs", @"docs\sub", @"docs\empty"]);
        plan.Conflicts.Should().BeEmpty();
        plan.TotalBytes.Should().Be(17);
        plan.VersionTime.Should().Be(new DateTime(2026, 9, 30, 14, 5, 0));
    }

    [Fact]
    public void Restores_into_a_chosen_folder_under_the_items_name()
    {
        var plan = Restorer.Plan(_version, ["docs/sub", "docs/b.txt"], _dest, RestoreMode.ToFolder);

        var result = Restorer.Run(plan, ConflictPolicy.Overwrite);

        result.Should().BeEquivalentTo(new RestoreResult(2, 0, 0, [], Canceled: false));
        DestFiles().Should().Equal("b.txt", @"sub\c.txt");
    }

    [Fact]
    public void Restores_a_whole_version_without_its_manifest()
    {
        var plan = Restorer.Plan(_version, [""], _dest, RestoreMode.Original);

        Restorer.Run(plan, ConflictPolicy.Overwrite).Copied.Should().Be(3);

        DestFiles().Should().Equal("a.txt", @"docs\b.txt", @"docs\sub\c.txt");
        Directory.Exists(Dest(@"docs\empty")).Should().BeTrue("a folder restore keeps the structure, also empty folders");
    }

    [Fact]
    public void Keeps_the_versions_last_write_time()
    {
        Restorer.Run(Restorer.Plan(_version, ["a.txt"], _dest, RestoreMode.Original), ConflictPolicy.Overwrite);

        System.IO.File.GetLastWriteTimeUtc(Dest("a.txt")).Should().Be(Old);
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("alpha");
    }

    [Fact]
    public void Overwrites_a_conflicting_file_and_leaves_no_temp_file()
    {
        _tmp.WriteFile(@"dest\a.txt", "changed since");
        var plan = Restorer.Plan(_version, ["a.txt"], _dest, RestoreMode.Original);
        plan.Conflicts.Select(c => c.Destination).Should().Equal(Dest("a.txt"));

        var result = Restorer.Run(plan, ConflictPolicy.Overwrite);

        result.Copied.Should().Be(1);
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("alpha");
        DestFiles().Should().Equal("a.txt");
    }

    [Fact]
    public void Skips_a_conflicting_file()
    {
        _tmp.WriteFile(@"dest\a.txt", "mine");

        var result = Restorer.Run(Restorer.Plan(_version, ["a.txt", "docs/b.txt"], _dest, RestoreMode.Original), ConflictPolicy.Skip);

        result.Should().BeEquivalentTo(new RestoreResult(1, 1, 0, [], Canceled: false));
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("mine");
        System.IO.File.ReadAllText(Dest(@"docs\b.txt")).Should().Be("bravo");
    }

    [Fact]
    public void Keeps_both_with_the_versions_time_in_the_name_and_counts_up_on_collisions()
    {
        _tmp.WriteFile(@"dest\a.txt", "mine");
        _tmp.WriteFile(@"dest\a (2026_09_30-14_05).txt", "earlier restore");
        _tmp.WriteFile(@"dest\a (2026_09_30-14_05) 2.txt", "and another");

        var result = Restorer.Run(Restorer.Plan(_version, ["a.txt"], _dest, RestoreMode.Original), ConflictPolicy.KeepBoth);

        result.Should().BeEquivalentTo(new RestoreResult(0, 0, 1, [], Canceled: false));
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("mine");
        System.IO.File.ReadAllText(Dest("a (2026_09_30-14_05) 3.txt")).Should().Be("alpha");
    }

    [Fact]
    public void Keep_both_name_without_an_extension()
    {
        Restorer.KeepBothName(@"C:\x\Makefile", new DateTime(2026, 9, 30, 14, 5, 0)).Should().Be(@"C:\x\Makefile (2026_09_30-14_05)");
    }

    [Fact]
    public void Never_deletes_files_the_version_does_not_have()
    {
        _tmp.WriteFile(@"dest\docs\extra.txt", "mine");
        _tmp.WriteFile(@"dest\docs\sub\extra2.txt", "mine too");

        Restorer.Run(Restorer.Plan(_version, ["docs"], _dest, RestoreMode.Original), ConflictPolicy.Overwrite);

        DestFiles().Should().Equal(@"docs\b.txt", @"docs\extra.txt", @"docs\sub\c.txt", @"docs\sub\extra2.txt");
    }

    [Fact]
    public void A_locked_or_read_only_file_fails_and_the_rest_continues()
    {
        _tmp.WriteFile(@"dest\docs\b.txt", "locked");
        var readOnly = _tmp.WriteFile(@"dest\docs\sub\c.txt", "read-only");
        System.IO.File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var plan = Restorer.Plan(_version, ["a.txt", "docs"], _dest, RestoreMode.Original);

        RestoreResult result;
        using (new FileStream(Dest(@"docs\b.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            result = Restorer.Run(plan, ConflictPolicy.Overwrite);
        System.IO.File.SetAttributes(readOnly, FileAttributes.Normal);

        result.Copied.Should().Be(1);
        // Windows reports both as "access denied" when the file is replaced; the reason is the system's text.
        result.Failures.Select(f => f.Path).Should().BeEquivalentTo([Dest(@"docs\b.txt"), Dest(@"docs\sub\c.txt")]);
        result.Failures.Should().OnlyContain(f => f.Reason.Length > 0);
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("alpha");
        System.IO.File.ReadAllText(Dest(@"docs\b.txt")).Should().Be("locked");
        System.IO.File.ReadAllText(readOnly).Should().Be("read-only");
        DestFiles().Should().NotContain(f => f.EndsWith(Restorer.TempSuffix));
    }

    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData("docs/../../outside.txt")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\Windows")]
    [InlineData("C:relative")]
    [InlineData("missing.txt")]
    [InlineData(VersionName.ManifestFileName)]
    public void Rejects_paths_outside_the_version_and_what_it_does_not_have(string relative)
    {
        var plan = () => Restorer.Plan(_version, [relative], _dest, RestoreMode.Original);

        plan.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Does_not_follow_links()
    {
        var outside = _tmp.CreateDir("outside");
        _tmp.WriteFile(@"outside\secret.txt", "secret");
        Junction.Create(Path.Combine(_version, "docs", "link"), outside);

        var plan = Restorer.Plan(_version, ["docs"], _dest, RestoreMode.Original);
        var linkItself = () => Restorer.Plan(_version, ["docs/link"], _dest, RestoreMode.Original);

        plan.Files.Should().NotContain(f => f.Source.Contains("secret"));
        linkItself.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Requires_an_absolute_destination()
    {
        var plan = () => Restorer.Plan(_version, ["a.txt"], "relative", RestoreMode.ToFolder);

        plan.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Cancellation_stops_before_the_next_file()
    {
        var plan = Restorer.Plan(_version, ["a.txt", "docs"], _dest, RestoreMode.Original);
        using var cts = new CancellationTokenSource();

        var result = Restorer.Run(plan, ConflictPolicy.Overwrite, new SyncProgress<RestoreProgress>(p =>
        {
            if (p.FilesDone == 1)
                cts.Cancel();
        }), cts.Token);

        result.Canceled.Should().BeTrue();
        result.Copied.Should().Be(1);
        DestFiles().Should().HaveCount(1).And.NotContain(f => f.EndsWith(Restorer.TempSuffix));
    }

    [Fact]
    public void Reports_progress_by_bytes()
    {
        var reports = new List<RestoreProgress>();

        Restorer.Run(Restorer.Plan(_version, ["docs"], _dest, RestoreMode.Original), ConflictPolicy.Overwrite,
            new SyncProgress<RestoreProgress>(reports.Add));

        reports[^1].Should().Be(new RestoreProgress(2, 2, 12, 12, ""));
        reports.Select(r => r.BytesDone).Should().BeInAscendingOrder();
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~RestorerTests"`
Expected: build errors — `Restorer`, `RestoreMode`, `ConflictPolicy`, `RestoreResult` … do not exist.

- [ ] **Step 3: Implement the restorer**

Create `src/ReBackup.Core/Versions/Restorer.cs`:

```csharp
using System.Globalization;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;

namespace ReBackup.Core.Versions;

/// <summary>Original: back to where the version came from. ToFolder: into a chosen folder, under the item's name.</summary>
public enum RestoreMode { Original, ToFolder }

/// <summary>What happens to a file that already exists at the destination; asked once per restore.</summary>
public enum ConflictPolicy { Overwrite, Skip, KeepBoth }

/// <summary>One file to copy: its path in the version folder and where it goes.</summary>
public sealed record RestoreFile(string Source, string Destination, long Size);

/// <summary>Everything a restore will do; made by <see cref="Restorer.Plan"/>, nothing is written yet.</summary>
public sealed class RestorePlan
{
    public required string VersionFolder { get; init; }
    public required string DestinationRoot { get; init; }

    /// <summary>The version's time: the stamp of "keep both" names.</summary>
    public required DateTime VersionTime { get; init; }

    /// <summary>Folders to create (the selected folders and everything below them, also empty ones).</summary>
    public required IReadOnlyList<string> Directories { get; init; }

    public required IReadOnlyList<RestoreFile> Files { get; init; }

    /// <summary>Files whose destination exists already.</summary>
    public required IReadOnlyList<RestoreFile> Conflicts { get; init; }

    public long TotalBytes => Files.Sum(f => f.Size);
}

public sealed record RestoreFailure(string Path, string Reason);

/// <summary>
/// <paramref name="Copied"/> counts files written to their own name (also overwritten ones), <paramref name="KeptBoth"/>
/// files written under a "keep both" name.
/// </summary>
public sealed record RestoreResult(int Copied, int Skipped, int KeptBoth, IReadOnlyList<RestoreFailure> Failures, bool Canceled);

public readonly record struct RestoreProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string CurrentFile)
{
    public double Fraction => BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1)
        : FilesTotal > 0 ? Math.Clamp((double)FilesDone / FilesTotal, 0, 1) : 0;
}

/// <summary>
/// Copies files and folders from a version back. Never deletes or moves anything at the destination; the only file
/// it replaces is a conflicting one under <see cref="ConflictPolicy.Overwrite"/>.
/// </summary>
public static class Restorer
{
    public const string TempSuffix = ".rebackup-tmp";
    private const int BufferSize = 1024 * 1024;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    /// <summary>
    /// Lists what restoring <paramref name="relativePaths"/> (files or folders of the version, forward or back slashes,
    /// "" = the whole version) to <paramref name="destinationRoot"/> copies, and which files exist there already.
    /// Original: destination = root + relative path. ToFolder: destination = root + the item's name.
    /// </summary>
    /// <exception cref="ArgumentException">A path leaves the version or the destination, does not exist, or is a link.</exception>
    /// <exception cref="DirectoryNotFoundException">The version folder does not exist.</exception>
    public static RestorePlan Plan(string versionFolder, IReadOnlyList<string> relativePaths, string destinationRoot,
        RestoreMode mode)
    {
        if (!Path.IsPathFullyQualified(destinationRoot))
            throw new ArgumentException("The destination must be an absolute path.", nameof(destinationRoot));
        versionFolder = PathUtil.Normalize(versionFolder);
        destinationRoot = PathUtil.Normalize(destinationRoot);
        if (!Directory.Exists(versionFolder))
            throw new DirectoryNotFoundException($"The version folder \"{versionFolder}\" does not exist.");

        var directories = new List<string>();
        var files = new List<RestoreFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in relativePaths)
        {
            var relative = CheckRelative(relativePath);
            var source = relative.Length == 0 ? versionFolder : Path.GetFullPath(Path.Combine(versionFolder, relative));
            if (!PathUtil.IsSameOrInside(source, versionFolder))
                throw new ArgumentException($"\"{relativePath}\" is outside the version.", nameof(relativePaths));
            var name = relative.Length == 0 ? Path.GetFileName(versionFolder) : Path.GetFileName(relative);
            var destination = mode == RestoreMode.Original
                ? (relative.Length == 0 ? destinationRoot : Path.Combine(destinationRoot, relative))
                : Path.Combine(destinationRoot, name);

            if (File.Exists(source))
            {
                if (IsLink(source))
                    throw new ArgumentException($"\"{relativePath}\" is a link.", nameof(relativePaths));
                if (IsManifest(source, versionFolder))
                    throw new ArgumentException("The manifest is not part of the backup.", nameof(relativePaths));
                AddFile(files, seen, source, destination, new FileInfo(source).Length, destinationRoot);
            }
            else if (Directory.Exists(source))
            {
                if (IsLink(source))
                    throw new ArgumentException($"\"{relativePath}\" is a link.", nameof(relativePaths));
                AddFolder(directories, files, seen, source, destination, versionFolder, destinationRoot);
            }
            else
            {
                throw new ArgumentException($"\"{relativePath}\" does not exist in the version.", nameof(relativePaths));
            }
        }

        return new RestorePlan
        {
            VersionFolder = versionFolder,
            DestinationRoot = destinationRoot,
            VersionTime = VersionTimeOf(versionFolder),
            Directories = directories,
            Files = files,
            Conflicts = files.Where(f => File.Exists(f.Destination) || Directory.Exists(f.Destination)).ToList(),
        };
    }

    /// <summary>
    /// Copies the plan's files. Each file is written to <c>&lt;name&gt;.rebackup-tmp</c> next to its target first and
    /// then renamed; it keeps the version's last write time. A file that cannot be written is recorded as a failure
    /// and the rest continues. Cancellation stops before the next file (a half-written temp file is removed).
    /// </summary>
    public static RestoreResult Run(RestorePlan plan, ConflictPolicy policy, IProgress<RestoreProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<RestoreFailure>();
        int copied = 0, skipped = 0, keptBoth = 0, filesDone = 0;
        long bytesDone = 0;
        var bytesTotal = plan.TotalBytes;
        var buffer = new byte[BufferSize];
        RestoreResult Result(bool canceled) => new(copied, skipped, keptBoth, failures, canceled);

        foreach (var directory in plan.Directories)
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new RestoreFailure(directory, Reason(ex)));
            }
        }

        foreach (var file in plan.Files)
        {
            if (cancellationToken.IsCancellationRequested)
                return Result(canceled: true);

            var bytesBefore = bytesDone;
            progress?.Report(new RestoreProgress(filesDone, plan.Files.Count, bytesDone, bytesTotal, file.Destination));
            try
            {
                var target = file.Destination;
                var overwrite = false;
                var keepBoth = false;
                if (File.Exists(target) || Directory.Exists(target))
                {
                    switch (policy)
                    {
                        case ConflictPolicy.Skip:
                            skipped++;
                            continue;
                        case ConflictPolicy.KeepBoth:
                            target = KeepBothName(target, plan.VersionTime);
                            keepBoth = true;
                            break;
                        default:
                            if (Directory.Exists(target))
                                throw new IOException("A folder with this name exists.");
                            overwrite = true;
                            break;
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + TempSuffix;
                if (!CopyToTemp(file.Source, temp, buffer, count =>
                    {
                        bytesDone += count;
                        progress?.Report(new RestoreProgress(filesDone, plan.Files.Count, bytesDone, bytesTotal, file.Destination));
                    }, cancellationToken))
                {
                    return Result(canceled: true);
                }

                try
                {
                    File.Move(temp, target, overwrite);
                }
                catch
                {
                    TryDelete(temp);
                    throw;
                }

                if (keepBoth)
                    keptBoth++;
                else
                    copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new RestoreFailure(file.Destination, Reason(ex)));
            }
            finally
            {
                filesDone++;
                bytesDone = bytesBefore + file.Size;
            }
        }

        progress?.Report(new RestoreProgress(filesDone, plan.Files.Count, bytesDone, bytesTotal, ""));
        return Result(canceled: false);
    }

    /// <summary>
    /// <c>name (2026_09_30-14_05).ext</c> next to <paramref name="destination"/>; <c>name (2026_09_30-14_05) 2.ext</c>,
    /// <c>… 3.ext</c> … when that exists too.
    /// </summary>
    public static string KeepBothName(string destination, DateTime versionTime)
    {
        var folder = Path.GetDirectoryName(destination)!;
        var stem = Path.GetFileNameWithoutExtension(destination);
        var extension = Path.GetExtension(destination);
        var stamp = versionTime.ToString("yyyy_MM_dd-HH_mm", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(folder, $"{stem} ({stamp}){extension}");
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
            candidate = Path.Combine(folder, $"{stem} ({stamp}) {n}{extension}");
        return candidate;
    }

    /// <summary>False when canceled (the temp file is removed then).</summary>
    private static bool CopyToTemp(string source, string temp, byte[] buffer, Action<int> onBytes,
        CancellationToken cancellationToken)
    {
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                   BufferSize, FileOptions.SequentialScan))
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    output.Dispose();
                    TryDelete(temp);
                    return false;
                }
                output.Write(buffer, 0, read);
                onBytes(read);
            }
        }

        File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(source));
        return true;
    }

    private static void AddFolder(List<string> directories, List<RestoreFile> files, HashSet<string> seen,
        string sourceFolder, string destinationFolder, string versionFolder, string destinationRoot)
    {
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((sourceFolder, destinationFolder));
        while (pending.Count > 0)
        {
            var (source, destination) = pending.Pop();
            CheckInside(destination, destinationRoot);
            directories.Add(destination);
            foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;   // links are not followed
                var target = Path.Combine(destination, entry.Name);
                if (entry is DirectoryInfo)
                    pending.Push((entry.FullName, target));
                else if (entry is FileInfo file && !IsManifest(file.FullName, versionFolder))
                    AddFile(files, seen, file.FullName, target, file.Length, destinationRoot);
            }
        }
    }

    private static void AddFile(List<RestoreFile> files, HashSet<string> seen, string source, string destination,
        long size, string destinationRoot)
    {
        CheckInside(destination, destinationRoot);
        if (seen.Add(destination))
            files.Add(new RestoreFile(source, destination, size));
    }

    private static void CheckInside(string destination, string destinationRoot)
    {
        if (!PathUtil.IsSameOrInside(destination, destinationRoot))
            throw new ArgumentException($"\"{destination}\" is outside the destination.");
    }

    /// <summary>The path with back slashes; rooted paths, drive letters and "." or ".." segments are rejected.</summary>
    private static string CheckRelative(string relativePath)
    {
        var relative = relativePath.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relativePath) || relative.Contains(Path.VolumeSeparatorChar) ||
            relative.Split(Path.DirectorySeparatorChar).Any(part => part is "." or ".." || (relative.Length > 0 && part.Length == 0)))
            throw new ArgumentException($"\"{relativePath}\" is not a path inside the version.", nameof(relativePath));
        return relative;
    }

    private static bool IsManifest(string path, string versionFolder) =>
        string.Equals(path, Path.Combine(versionFolder, VersionName.ManifestFileName), StringComparison.OrdinalIgnoreCase);

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static DateTime VersionTimeOf(string versionFolder) =>
        VersionName.TryParseAny(Path.GetFileName(versionFolder), out var time, out _)
            ? time
            : Directory.GetLastWriteTime(versionFolder);

    private static string Reason(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "access denied (read-only, or in use by another program)",
        IOException io when (io.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation => "locked by another program",
        _ => exception.Message,
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
```

- [ ] **Step 4: Run the tests to see them pass, then the whole suite**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~RestorerTests"`
Expected: PASS, 21 tests (14 facts, one theory with 7 cases).

Run: `dotnet build tests/ReBackup.Core.Tests --no-incremental` then `dotnet test tests/ReBackup.Core.Tests`
Expected: 0 warnings; all 608 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/ReBackup.Core/Versions/Restorer.cs tests/ReBackup.Core.Tests/Versions/RestorerTests.cs
git commit -m "feat(core): restorer — plan and run with overwrite/skip/keep-both, never deletes, path and link safety" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 4: Runner hook, index wiring per plan, Versions tab with the version list

**Files:**
- Modify: `src/ReBackup.Core/Backup/BackupRunner.cs`
- Create (tests): `tests/ReBackup.Core.Tests/Backup/BackupRunnerIndexTests.cs`
- Create: `src/ReBackup.App/ViewModels/VersionRowViewModel.cs`, `src/ReBackup.App/ViewModels/VersionsViewModel.cs`, `src/ReBackup.App/Views/VersionsView.xaml`, `src/ReBackup.App/Views/VersionsView.xaml.cs`
- Modify: `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`, `src/ReBackup.App/ViewModels/MainViewModel.cs`, `src/ReBackup.App/App.xaml.cs`, `src/ReBackup.App/MainWindow.xaml`, `src/ReBackup.App/Theme/Icons.xaml`

**Interfaces:**
- Consumes: `IVersionIndexSink`, `VersionIndexSet` (`DefaultDirectory`, `For`), `VersionIndex.Sync/Versions`, `IndexSyncProgress`, `IndexedVersion` (Task 1); `VersionCatalog.List`; existing `RangeObservableCollection<T>`, `IFolderOpener`, `IDialogService`, `PlanRunViewModel.IsActive`, `MainTab`, `EnumMatchConverter`, styles `Border.Card`, `TextBlock.CardTitle`, `TextBlock.Muted`, `Button.Icon`, `RadioButton.Rail`, `RadioButton.Segment`.
- Produces:
  - Core: `BackupRunner(ITargetVolume? volume = null, TimeProvider? timeProvider = null, Func<string, IReadOnlyList<RetentionRule>?>? currentRules = null, IVersionIndexSink? indexSink = null)`; after a completed run (before retention) the sink gets `(plan.Id, VersionInfo(name, finalPath, localTime, Owned, FileCount, TotalBytes), manifest)`; any exception → warning `"The version index could not be updated: <message>"`, status unchanged.
  - App: `record VersionsContext(VersionIndexSet Indexes, IDialogService Dialogs, Action<string> ReportStatus)`
  - `enum IndexState { NotIndexed, Indexing, Indexed, Failed }`; `VersionRowViewModel(VersionInfo)` with `Info` (settable), `IndexState`, `Indexed` (`IndexedVersion?`), `Name`, `DateText` (`yyyy-MM-dd HH:mm`), `SizeText`, `FilesText`, `IsManaged`, `ReasonText`, `IndexStateText`
  - `VersionsViewModel(Func<BackupPlan?> savedPlan, Func<bool> isBackupActive, IFolderOpener files, VersionsContext context)` (partial; this task is part 1 of 3): `VersionRows` (newest first), `SelectedVersion`, `IsSyncing`, `SyncText`, `Error`, `ShowEmpty`, `RefreshCommand`, `EnsureLoaded()`, `ReloadIfLoaded()`, `OnSavedTargetChanged()`, `Invalidate()`; private `_index` (`VersionIndex?`), `_files`, `_context`, `_savedPlan`, `_isBackupActive`; partial hooks `OnCreated()` (end of constructor) and `OnIndexReady()` (after every successful sync) that Task 5 implements.
  - `PlanEditorViewModel(…, IFolderOpener folders, VersionsContext versions)` and `PlanEditorViewModel.Versions`
  - `MainViewModel(…, IFolderOpener folders, VersionIndexSet versionIndexes)`; `MainTab.Versions` (after History)
  - Resource `Icon.Versions` (`E81E`, layers glyph)

- [ ] **Step 1: Write the failing runner tests**

Create `tests/ReBackup.Core.Tests/Backup/BackupRunnerIndexTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;

namespace ReBackup.Core.Tests.Backup;

public class BackupRunnerIndexTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.Zero));
    private readonly BackupPlan _plan;

    public BackupRunnerIndexTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
        _tmp.WriteFile(@"source\a.txt", "alpha");
        _tmp.WriteFile(@"source\sub\b.bin", "bravo-bravo");
        _plan = new BackupPlan { Id = "p1", Name = "Projects", Source = _tmp.PathOf("source"), Target = _tmp.PathOf("target") };
    }

    public void Dispose() => _tmp.Dispose();

    private Task<RunLogEntry> Run(IVersionIndexSink sink) =>
        new BackupRunner(new PhysicalTargetVolume(), _time, indexSink: sink)
            .RunAsync(new BackupRequest(_plan, [], RunTrigger.Manual));

    [Fact]
    public async Task A_finished_run_adds_its_version_to_the_index()
    {
        var indexes = new VersionIndexSet(_tmp.PathOf("indexes"));

        var entry = await Run(indexes);

        entry.Status.Should().Be(RunStatus.Completed);
        entry.Warnings.Should().BeEmpty();
        var version = indexes.For("p1").Versions().Should().ContainSingle().Subject;
        version.Name.Should().Be("2026_09_30-16_05 Projects");
        version.LocalTime.Should().Be(new DateTime(2026, 9, 30, 16, 5, 0));
        version.Ownership.Should().Be(VersionOwnership.Owned);
        version.Source.Should().Be(_plan.Source);
        version.FileCount.Should().Be(2);
        version.TotalBytes.Should().Be(16);
        indexes.For("p1").Sync(VersionCatalog.List(_plan.Target, "p1", "Projects")).Unchanged
            .Should().Be(1, "the index knows the manifest on disk, so the next sync does not read it again");
    }

    [Fact]
    public async Task An_index_failure_is_a_warning_and_the_backup_stays_completed()
    {
        var entry = await Run(new FailingSink());

        entry.Status.Should().Be(RunStatus.Completed);
        entry.Version.Should().Be("2026_09_30-16_05 Projects");
        entry.Warnings.Should().ContainSingle().Which.Should().Be("The version index could not be updated: disk on fire");
    }

    [Fact]
    public async Task A_run_that_does_not_finish_adds_nothing()
    {
        var sink = new RecordingSink();
        _plan.Source = _tmp.PathOf("missing");

        var entry = await Run(sink);

        entry.Status.Should().Be(RunStatus.Error);
        sink.Calls.Should().Be(0);
    }

    private sealed class FailingSink : IVersionIndexSink
    {
        public void Add(string planId, VersionInfo version, BackupManifest manifest) =>
            throw new InvalidOperationException("disk on fire");
    }

    private sealed class RecordingSink : IVersionIndexSink
    {
        public int Calls { get; private set; }
        public void Add(string planId, VersionInfo version, BackupManifest manifest) => Calls++;
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~BackupRunnerIndexTests"`
Expected: build error CS1739 — `BackupRunner` has no parameter named `indexSink`.

- [ ] **Step 3: Hand finished versions to the sink**

In `src/ReBackup.Core/Backup/BackupRunner.cs`:

Add the using (after `using ReBackup.Core.Retention;`):

```csharp
using ReBackup.Core.Versions;
```

Replace the field block end and the constructor:

```csharp
    private readonly Func<string, IReadOnlyList<RetentionRule>?>? _currentRules;
```

with

```csharp
    private readonly Func<string, IReadOnlyList<RetentionRule>?>? _currentRules;
    private readonly IVersionIndexSink? _indexSink;
```

and

```csharp
    /// and not by those of the request. Without it, the rules of the request are used.
    /// </param>
    public BackupRunner(ITargetVolume? volume = null, TimeProvider? timeProvider = null,
        Func<string, IReadOnlyList<RetentionRule>?>? currentRules = null)
    {
        _volume = volume ?? new PhysicalTargetVolume();
        _time = timeProvider ?? TimeProvider.System;
        _currentRules = currentRules;
    }
```

with

```csharp
    /// and not by those of the request. Without it, the rules of the request are used.
    /// </param>
    /// <param name="indexSink">
    /// Gets the manifest of every version a run finishes, for the plan's version index. Its failures become warnings.
    /// </param>
    public BackupRunner(ITargetVolume? volume = null, TimeProvider? timeProvider = null,
        Func<string, IReadOnlyList<RetentionRule>?>? currentRules = null, IVersionIndexSink? indexSink = null)
    {
        _volume = volume ?? new PhysicalTargetVolume();
        _time = timeProvider ?? TimeProvider.System;
        _currentRules = currentRules;
        _indexSink = indexSink;
    }
```

In `RunAsync`, replace

```csharp
        string? partialPath = null;
        var completed = false;
```

with

```csharp
        string? partialPath = null;
        string? finalPath = null;
        BackupManifest? manifest = null;
        var completed = false;
```

replace

```csharp
            var finalPath = Path.Combine(plan.Target, versionName);
            partialPath = finalPath + VersionName.PartialSuffix;
            var partial = partialPath;
            await Task.Run(() => CopyAndFinish(work, plan, partial, finalPath, entry, progress, cancellationToken),
                cancellationToken);
```

with

```csharp
            var final = finalPath = Path.Combine(plan.Target, versionName);
            partialPath = finalPath + VersionName.PartialSuffix;
            var partial = partialPath;
            manifest = await Task.Run(() => CopyAndFinish(work, plan, partial, final, entry, progress, cancellationToken),
                cancellationToken);
```

and insert directly before the block `if (completed)` that applies retention (`await Task.Run(() => ApplyRetention(…`):

```csharp
        if (completed && _indexSink is not null && manifest is not null && finalPath is not null)
            await Task.Run(() => AddToIndex(plan, entry, finalPath, manifest));

```

Insert this method directly above `/// <summary>Deletes the versions the plan's rules no longer keep.`:

```csharp
    /// <summary>Hands the finished version to the version index. Whatever goes wrong there is a warning only.</summary>
    private void AddToIndex(BackupPlan plan, RunLogEntry entry, string finalPath, BackupManifest manifest)
    {
        try
        {
            var name = Path.GetFileName(finalPath);
            VersionName.TryParseAny(name, out var localTime, out _);
            var version = new VersionInfo(name, finalPath, localTime, VersionOwnership.Owned,
                manifest.FileCount, manifest.TotalBytes);
            _indexSink!.Add(plan.Id, version, manifest);
        }
        catch (Exception ex)
        {
            entry.Warnings.Add($"The version index could not be updated: {ex.Message}");
        }
    }

```

Make `CopyAndFinish` return the manifest it wrote: change its signature line

```csharp
    private void CopyAndFinish(BackupWork work, BackupPlan plan, string partialPath, string finalPath,
```

to

```csharp
    private BackupManifest CopyAndFinish(BackupWork work, BackupPlan plan, string partialPath, string finalPath,
```

and its last lines

```csharp
        cancellationToken.ThrowIfCancellationRequested();
        MoveWithRetry(partialPath, finalPath);
    }
```

to

```csharp
        cancellationToken.ThrowIfCancellationRequested();
        MoveWithRetry(partialPath, finalPath);
        return manifest;
    }
```

- [ ] **Step 4: Run the runner tests, then the whole suite**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~BackupRunner"`
Expected: PASS (the 3 new tests and all existing runner tests).

Run: `dotnet build tests/ReBackup.Core.Tests --no-incremental` then `dotnet test tests/ReBackup.Core.Tests`
Expected: 0 warnings; all 611 tests pass.

- [ ] **Step 5: Icon**

In `src/ReBackup.App/Theme/Icons.xaml`, after `    <sys:String x:Key="Icon.Power">&#xE7E8;</sys:String>` add:

```xml
    <sys:String x:Key="Icon.Versions">&#xE81E;</sys:String>
```

- [ ] **Step 6: Version row**

Create `src/ReBackup.App/ViewModels/VersionRowViewModel.cs`:

```csharp
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;
using ReBackup.Core.Versions;

namespace ReBackup.App.ViewModels;

/// <summary>Whether a version's file list is in the plan's index.</summary>
public enum IndexState { NotIndexed, Indexing, Indexed, Failed }

/// <summary>A version folder in the list of the Versions tab. Kept by name across refreshes.</summary>
public sealed partial class VersionRowViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndexStateText))]
    private IndexState _indexState;

    /// <summary>The version as the index knows it; null until it is indexed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(FilesText))]
    private IndexedVersion? _indexed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(FilesText), nameof(DateText), nameof(IsManaged), nameof(ReasonText))]
    private VersionInfo _info = null!;

    public VersionRowViewModel(VersionInfo info) => Info = info;

    public string Name => Info.Name;

    public string DateText => Info.LocalTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string SizeText => (Info.TotalBytes ?? Indexed?.TotalBytes) is { } bytes ? ByteSize.Format(bytes) : "—";

    public string FilesText =>
        (Info.FileCount ?? Indexed?.FileCount) is { } count ? count.ToString("N0", CultureInfo.CurrentCulture) + " files" : "—";

    /// <summary>Retention manages it; the others are greyed and never deleted.</summary>
    public bool IsManaged => Info.IsOwned;

    /// <summary>Why a folder is not managed; empty for managed versions.</summary>
    public string ReasonText => Info.Ownership switch
    {
        VersionOwnership.NoManifest => "Not managed: no manifest",
        VersionOwnership.Foreign => "Not managed: manifest of another plan",
        VersionOwnership.Unreadable => "Not managed: manifest cannot be read",
        VersionOwnership.Renamed => "Not managed: renamed or copied by hand",
        _ => "",
    };

    public string IndexStateText => IndexState switch
    {
        IndexState.Indexing => "indexing…",
        IndexState.Indexed => "indexed",
        IndexState.Failed => "could not be indexed",
        _ => "not indexed",
    };
}
```

- [ ] **Step 7: The view model's list and sync part**

Create `src/ReBackup.App/ViewModels/VersionsViewModel.cs`:

```csharp
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Versions;

namespace ReBackup.App.ViewModels;

/// <summary>What every plan's Versions tab shares: the index set, the dialogs and the footer's status line.</summary>
public sealed record VersionsContext(VersionIndexSet Indexes, IDialogService Dialogs, Action<string> ReportStatus);

/// <summary>
/// The Versions tab of one plan: the version folders of its saved target and their local index. The target is read
/// and the index synced off the UI thread whenever the tab is shown or refreshed.
/// </summary>
public sealed partial class VersionsViewModel : ObservableObject
{
    private readonly Func<BackupPlan?> _savedPlan;
    private readonly Func<bool> _isBackupActive;
    private readonly IFolderOpener _files;
    private readonly VersionsContext _context;
    private CancellationTokenSource? _syncCts;
    private VersionIndex? _index;
    private bool _loaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isSyncing;

    /// <summary>The sync's progress line, e.g. "Indexing 2 of 5 · 2026_10_01-11_34 MoonLabs · 120,000 files".</summary>
    [ObservableProperty] private string _syncText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private string? _error;

    /// <summary>Version A: the version whose tree is shown.</summary>
    [ObservableProperty] private VersionRowViewModel? _selectedVersion;

    /// <param name="savedPlan">The plan as saved; null while it is new (a new plan has no versions).</param>
    /// <param name="isBackupActive">True while a backup of this plan is queued or running.</param>
    public VersionsViewModel(Func<BackupPlan?> savedPlan, Func<bool> isBackupActive, IFolderOpener files,
        VersionsContext context)
    {
        _savedPlan = savedPlan;
        _isBackupActive = isBackupActive;
        _files = files;
        _context = context;
        OnCreated();
    }

    /// <summary>Runs at the end of the constructor (the tree part subscribes to its tree here).</summary>
    partial void OnCreated();

    /// <summary>The version folders of the target, newest first.</summary>
    public RangeObservableCollection<VersionRowViewModel> VersionRows { get; } = new();

    /// <summary>The target was read and holds no versions.</summary>
    public bool ShowEmpty => _loaded && VersionRows.Count == 0 && !IsSyncing && Error is null;

    /// <summary>Reads the target and syncs the index; called whenever the tab is shown. Does nothing while a sync runs.</summary>
    public void EnsureLoaded()
    {
        if (!IsSyncing)
            _ = RefreshAsync();
    }

    /// <summary>Reads the target again if the tab has been shown before, e.g. after a run of the plan.</summary>
    public void ReloadIfLoaded()
    {
        if (_loaded && !IsSyncing)
            _ = RefreshAsync();
    }

    /// <summary>The saved target changed: forget the versions, and read the new target if the tab was in use.</summary>
    public void OnSavedTargetChanged()
    {
        var wasLoaded = _loaded;
        Invalidate();
        if (wasLoaded)
            _ = RefreshAsync();
    }

    /// <summary>Forgets the versions.</summary>
    public void Invalidate()
    {
        _syncCts?.Cancel();
        _syncCts = null;
        _index = null;
        _loaded = false;
        IsSyncing = false;
        SyncText = "";
        Error = null;
        SelectedVersion = null;
        VersionRows.ReplaceAll([]);
        OnPropertyChanged(nameof(ShowEmpty));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var plan = _savedPlan();
        if (plan is null)
        {
            Invalidate();
            Error = "Save the plan to see its versions.";
            return;
        }

        _syncCts?.Cancel();
        var cts = _syncCts = new CancellationTokenSource();
        IsSyncing = true;
        Error = null;
        SyncText = "Reading the target…";
        try
        {
            var (index, folders, indexed) = await Task.Run(() =>
            {
                var planIndex = _context.Indexes.For(plan.Id);
                var list = VersionCatalog.List(plan.Target, plan.Id, plan.Name, cts.Token);
                return (planIndex, list, planIndex.Versions());
            }, cts.Token);
            if (!ReferenceEquals(_syncCts, cts))
                return;

            _index = index;
            ShowFolders(folders, indexed);
            var progress = new Progress<IndexSyncProgress>(p =>
            {
                if (ReferenceEquals(_syncCts, cts) && IsSyncing)
                    OnSyncProgress(p);
            });
            var result = await Task.Run(() => index.Sync(folders, progress, cts.Token), cts.Token);
            var versions = await Task.Run(index.Versions, cts.Token);
            if (!ReferenceEquals(_syncCts, cts))
                return;

            ApplyIndexed(versions, result.Errors);
            SyncText = result.Errors.Count == 0
                ? ""
                : $"{result.Errors.Count} version(s) could not be indexed: {string.Join("; ", result.Errors)}";
            OnIndexReady();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_syncCts, cts))
            {
                Error = ex.Message;
                SyncText = "";
            }
        }
        finally
        {
            if (ReferenceEquals(_syncCts, cts))
            {
                _loaded = true;
                IsSyncing = false;
            }
        }
    }

    /// <summary>Runs after every successful sync; the tree reloads here (Versions tab, tree part).</summary>
    partial void OnIndexReady();

    /// <summary>Shows the listed folders newest first, reusing rows by name so the selection stays.</summary>
    private void ShowFolders(IReadOnlyList<VersionInfo> folders, IReadOnlyList<IndexedVersion> indexed)
    {
        var known = VersionRows.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var byName = indexed.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        var rows = new List<VersionRowViewModel>();
        foreach (var folder in folders.Reverse())
        {
            if (!known.TryGetValue(folder.Name, out var row))
                row = new VersionRowViewModel(folder);
            row.Info = folder;
            row.Indexed = byName.GetValueOrDefault(folder.Name);
            row.IndexState = row.Indexed is null ? IndexState.NotIndexed : IndexState.Indexed;
            rows.Add(row);
        }

        var selectedName = SelectedVersion?.Name;
        VersionRows.ReplaceAll(rows);
        SelectedVersion = rows.FirstOrDefault(r => r.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
                          ?? rows.FirstOrDefault();
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private void OnSyncProgress(IndexSyncProgress progress)
    {
        var row = VersionRows.FirstOrDefault(r => r.Name.Equals(progress.VersionName, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
            row.IndexState = progress.Finished ? IndexState.Indexed : IndexState.Indexing;
        SyncText = progress.Finished
            ? $"Indexing {progress.Current} of {progress.Total}"
            : string.Create(CultureInfo.CurrentCulture,
                $"Indexing {progress.Current} of {progress.Total} · {progress.VersionName} · {progress.FilesImported:N0} files");
    }

    private void ApplyIndexed(IReadOnlyList<IndexedVersion> versions, IReadOnlyList<string> errors)
    {
        var byName = versions.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var row in VersionRows)
        {
            row.Indexed = byName.GetValueOrDefault(row.Name);
            row.IndexState = row.Indexed is not null ? IndexState.Indexed
                : errors.Any(e => e.StartsWith(row.Name + ":", StringComparison.OrdinalIgnoreCase)) ? IndexState.Failed
                : IndexState.NotIndexed;
        }
    }
}
```

Behaviour to keep in mind (the next two tasks build on it):
- Target listing, `VersionIndexSet.For` (opening/rebuilding the database), `Sync` and `Versions()` all run in `Task.Run`; the `Progress<IndexSyncProgress>` is created on the UI thread, so its callback updates rows there. Late progress callbacks after the sync ended are ignored (`IsSyncing` check).
- Rows are reused by name, so the selection and the tree survive a refresh. Not-managed folders are listed too (they come from `VersionCatalog.List`).
- A new (unsaved) plan shows "Save the plan to see its versions." in `Error`.

- [ ] **Step 8: Plan editor and main view model**

In `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`, change the constructor signature

```csharp
        Func<IReadOnlyList<string>> globalIgnoreDefaults, IFolderOpener folders)
    {
```

to

```csharp
        Func<IReadOnlyList<string>> globalIgnoreDefaults, IFolderOpener folders, VersionsContext versions)
    {
```

after the line `RetentionPreview = new RetentionPreviewViewModel(ToPlan, () => Preview.LastEvaluatedIncludedSize, folders);` add

```csharp
        Versions = new VersionsViewModel(() => IsNew ? null : SavedPlan(), () => Run.IsActive, folders, versions);
```

and before `    /// <summary>The retention rules as edited.</summary>` add

```csharp
    /// <summary>The versions in the saved target, their index, comparison and restore.</summary>
    public VersionsViewModel Versions { get; }

```

In `src/ReBackup.App/ViewModels/MainViewModel.cs`:
- add `using ReBackup.Core.Versions;` after `using ReBackup.Core.Settings;`;
- add `Versions,` after `History,` in `enum MainTab`;
- add the field `    private readonly VersionsContext _versions;` after `    private readonly Action<Action> _runOnUi;`;
- change the constructor's last parameter line and first statement from

```csharp
        IFolderOpener folders)
    {
        _folders = folders;
```

to

```csharp
        IFolderOpener folders, VersionIndexSet versionIndexes)
    {
        _folders = folders;
        _versions = new VersionsContext(versionIndexes, dialogs, text => StatusMessage = text);
```

- in both places that read `AddEditor(new PlanEditorViewModel(plan, isNew: false, AllPlans, GlobalIgnoreDefaults, _folders));` (constructor and `ReloadFromDisk`) pass `_folders, _versions` instead of `_folders`; in `NewPlan` change the continuation line `            _folders);` to `            _folders, _versions);`;
- in `ReloadFromDisk`, replace

```csharp
                    if (!string.Equals(targetBefore, plan.Target, StringComparison.OrdinalIgnoreCase))
                        LoadHistory(editor);
```

with

```csharp
                    if (!string.Equals(targetBefore, plan.Target, StringComparison.OrdinalIgnoreCase))
                    {
                        LoadHistory(editor);
                        editor.Versions.OnSavedTargetChanged();
                    }
```

- in `Save`, replace

```csharp
                if (!string.Equals(targetBefore, editor.SavedPlan().Target, StringComparison.OrdinalIgnoreCase))
                    LoadHistory(editor);   // the folder buttons look in the new target
```

with

```csharp
                if (!string.Equals(targetBefore, editor.SavedPlan().Target, StringComparison.OrdinalIgnoreCase))
                {
                    LoadHistory(editor);   // the folder buttons look in the new target
                    editor.Versions.OnSavedTargetChanged();
                }
```

- in `OnJobUpdate`, after `            editor.RetentionPreview.ReloadIfLoaded();` add `            editor.Versions.ReloadIfLoaded();`.

- [ ] **Step 9: App wiring**

In `src/ReBackup.App/App.xaml.cs` add `using ReBackup.Core.Versions;` after `using ReBackup.Core.Settings;`, and in `LoadConfiguration` replace

```csharp
            // Versions are deleted by the rules as saved at that moment, not as they were when the run was queued.
            var runner = new BackupRunner(currentRules: planId => planStore.TryLoad(planId)?.Retention);
```

with

```csharp
            // One SQLite index per plan in %LOCALAPPDATA%\ReBackup\index; a finished run adds its version right away.
            var versionIndexes = new VersionIndexSet(VersionIndexSet.DefaultDirectory);
            // Versions are deleted by the rules as saved at that moment, not as they were when the run was queued.
            var runner = new BackupRunner(currentRules: planId => planStore.TryLoad(planId)?.Retention,
                indexSink: versionIndexes);
```

and

```csharp
                action => Dispatcher.InvokeAsync(action), themeToggle, new ExplorerFolderOpener());
```

with

```csharp
                action => Dispatcher.InvokeAsync(action), themeToggle, new ExplorerFolderOpener(), versionIndexes);
```

- [ ] **Step 10: The view (version list; the main area follows in Task 5)**

Create `src/ReBackup.App/Views/VersionsView.xaml`:

```xml
<UserControl x:Class="ReBackup.App.Views.VersionsView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVisibility" />

        <!-- A status line that takes no room while it is empty. -->
        <Style x:Key="TextBlock.Line" TargetType="TextBlock">
            <Setter Property="Margin" Value="0,4,0,0" />
            <Setter Property="FontSize" Value="12" />
            <Setter Property="TextWrapping" Value="Wrap" />
            <Style.Triggers>
                <Trigger Property="Text" Value="">
                    <Setter Property="Visibility" Value="Collapsed" />
                </Trigger>
            </Style.Triggers>
        </Style>
    </UserControl.Resources>

    <Grid Margin="24,20">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="300" />
            <ColumnDefinition Width="16" />
            <ColumnDefinition Width="*" MinWidth="360" />
        </Grid.ColumnDefinitions>

        <!-- ======================================================== VERSIONS -->
        <Border Grid.Column="0" Style="{StaticResource Border.Card}" Padding="0,14,0,8">
            <DockPanel>
                <DockPanel DockPanel.Dock="Top" Margin="18,0,10,6">
                    <Button DockPanel.Dock="Right" Style="{StaticResource Button.Icon}" Content="{StaticResource Icon.Refresh}"
                            ToolTip="Read the target again and update the index" AutomationProperties.Name="Refresh versions"
                            Command="{Binding Versions.RefreshCommand}" />
                    <TextBlock Style="{StaticResource TextBlock.CardTitle}" VerticalAlignment="Center" Text="VERSIONS" />
                </DockPanel>

                <StackPanel DockPanel.Dock="Top" Margin="18,0,18,8">
                    <ProgressBar IsIndeterminate="True" Height="4"
                                 Visibility="{Binding Versions.IsSyncing, Converter={StaticResource BoolToVisibility}}" />
                    <TextBlock Style="{StaticResource TextBlock.Line}" Foreground="{DynamicResource Brush.TextMuted}"
                               Text="{Binding Versions.SyncText}" />
                    <TextBlock Style="{StaticResource TextBlock.Line}" Foreground="{DynamicResource Brush.Danger}"
                               Text="{Binding Versions.Error}" />
                </StackPanel>

                <Grid>
                    <ListBox ItemsSource="{Binding Versions.VersionRows}" SelectedItem="{Binding Versions.SelectedVersion}"
                             ScrollViewer.HorizontalScrollBarVisibility="Disabled" AutomationProperties.Name="Versions"
                             VirtualizingPanel.IsVirtualizing="True">
                        <ListBox.ItemContainerStyle>
                            <Style TargetType="ListBoxItem" BasedOn="{StaticResource {x:Type ListBoxItem}}">
                                <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                                <Setter Property="ToolTip" Value="{Binding Name}" />
                            </Style>
                        </ListBox.ItemContainerStyle>
                        <ListBox.ItemTemplate>
                            <DataTemplate>
                                <StackPanel Margin="10,6">
                                    <StackPanel.Style>
                                        <Style TargetType="StackPanel">
                                            <Style.Triggers>
                                                <!-- Not managed: greyed, with the reason below. -->
                                                <DataTrigger Binding="{Binding IsManaged}" Value="False">
                                                    <Setter Property="Opacity" Value="0.6" />
                                                </DataTrigger>
                                            </Style.Triggers>
                                        </Style>
                                    </StackPanel.Style>
                                    <DockPanel>
                                        <TextBlock DockPanel.Dock="Right" Style="{StaticResource TextBlock.Muted}" FontSize="12"
                                                   Typography.NumeralAlignment="Tabular" Text="{Binding SizeText}" />
                                        <TextBlock FontWeight="SemiBold" Typography.NumeralAlignment="Tabular" Text="{Binding DateText}" />
                                    </DockPanel>
                                    <DockPanel Margin="0,2,0,0">
                                        <TextBlock DockPanel.Dock="Right" FontSize="11" Foreground="{DynamicResource Brush.TextFaint}"
                                                   Text="{Binding IndexStateText}" />
                                        <TextBlock Style="{StaticResource TextBlock.Muted}" FontSize="12" Text="{Binding FilesText}" />
                                    </DockPanel>
                                    <TextBlock Margin="0,2,0,0" FontSize="12" Foreground="{DynamicResource Brush.Warning}"
                                               TextWrapping="Wrap" Text="{Binding ReasonText}">
                                        <TextBlock.Style>
                                            <Style TargetType="TextBlock">
                                                <Style.Triggers>
                                                    <DataTrigger Binding="{Binding IsManaged}" Value="True">
                                                        <Setter Property="Visibility" Value="Collapsed" />
                                                    </DataTrigger>
                                                </Style.Triggers>
                                            </Style>
                                        </TextBlock.Style>
                                    </TextBlock>
                                </StackPanel>
                            </DataTemplate>
                        </ListBox.ItemTemplate>
                    </ListBox>
                    <TextBlock Style="{StaticResource TextBlock.Muted}" HorizontalAlignment="Center" VerticalAlignment="Center"
                               Text="No versions in the target yet"
                               Visibility="{Binding Versions.ShowEmpty, Converter={StaticResource BoolToVisibility}}" />
                </Grid>
            </DockPanel>
        </Border>

        <!-- The tree, the comparison and the history of a file come here (next task). -->
        <Border x:Name="MainArea" Grid.Column="2" Style="{StaticResource Border.Card}">
            <TextBlock Style="{StaticResource TextBlock.Muted}" HorizontalAlignment="Center" VerticalAlignment="Center"
                       Text="{Binding Versions.SelectedVersion.Name}" />
        </Border>
    </Grid>
</UserControl>
```

Create `src/ReBackup.App/Views/VersionsView.xaml.cs`:

```csharp
using System.Windows.Controls;
using ReBackup.App.ViewModels;

namespace ReBackup.App.Views;

/// <summary>The Versions tab. Reads the target and syncs the index whenever it becomes visible.</summary>
public partial class VersionsView : UserControl
{
    public VersionsView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => EnsureLoaded();
        DataContextChanged += (_, _) => EnsureLoaded();
    }

    private void EnsureLoaded()
    {
        if (IsVisible && DataContext is PlanEditorViewModel editor)
            editor.Versions.EnsureLoaded();
    }
}
```

- [ ] **Step 11: Rail button, header tab and content in the main window**

In `src/ReBackup.App/MainWindow.xaml`:

After the `RailHistoryTab` radio button (inside the rail's tab `StackPanel`, before its `</StackPanel>`) add:

```xml
                    <RadioButton x:Name="RailVersionsTab" Style="{StaticResource RadioButton.Rail}" GroupName="RailTabs"
                                 Content="{StaticResource Icon.Versions}" ToolTip="Versions" AutomationProperties.Name="Versions"
                                 AutomationProperties.AutomationId="RailVersionsTab"
                                 IsChecked="{Binding SelectedTab, Converter={StaticResource Converter.EnumMatch}, ConverterParameter={x:Static vm:MainTab.Versions}}" />
```

After the header's `HistoryTab` radio button (its closing `</RadioButton>`, inside the `MainTabsBar` `StackPanel`) add:

```xml
                                    <RadioButton x:Name="VersionsTab" Style="{StaticResource RadioButton.Segment}" GroupName="MainTabs"
                                                 Margin="2,0,0,0" AutomationProperties.Name="Versions"
                                                 IsChecked="{Binding DataContext.SelectedTab, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource Converter.EnumMatch}, ConverterParameter={x:Static vm:MainTab.Versions}}">
                                        <StackPanel Orientation="Horizontal">
                                            <TextBlock Style="{StaticResource TextBlock.Icon}" FontSize="13" Text="{StaticResource Icon.Versions}" />
                                            <TextBlock Margin="7,0,0,0" VerticalAlignment="Center" FontWeight="Medium" Text="Versions" />
                                        </StackPanel>
                                    </RadioButton>
```

In the tab content `Grid`, after `<views:HistoryView … />` add:

```xml
                        <views:VersionsView
                            Visibility="{Binding DataContext.SelectedTab, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource Converter.EnumMatch}, ConverterParameter={x:Static vm:MainTab.Versions}}" />
```

(`MainWindow.LayoutHeader` measures the tab bar, so the fifth tab moves to the second header row on narrow windows by itself.)

- [ ] **Step 12: Verify**

Run: `dotnet build src/ReBackup.App --no-incremental -o "$env:TEMP\rebackup-app-build"`
Expected: Build succeeded, 0 warnings, 0 errors.

Run: `dotnet test tests/ReBackup.Core.Tests`
Expected: all 611 tests pass.

Manual checklist (controller/user; do not start the app yourself):
1. The rail shows a fifth button (layers glyph) and the header a "Versions" tab; both select the same tab; the tab stays when another plan is selected.
2. Showing the tab lists the target's versions newest first (date, size, file count); while the index syncs, an indeterminate bar and "Indexing 1 of 3 · … · 12,288 files" are shown and the rows change from "not indexed"/"indexing…" to "indexed". The window stays responsive (also with a network target).
3. A folder named like the plan without a manifest (or renamed by hand) is listed greyed with "Not managed: …".
4. Refresh re-lists; a second show of the tab is fast (all stamps unchanged). Deleting `%LOCALAPPDATA%\ReBackup\index\<planId>.db` and pressing Refresh rebuilds it.
5. A new, unsaved plan shows "Save the plan to see its versions.".
6. After a backup of the plan finishes, a Versions tab that was shown before lists the new version as "indexed" without pressing Refresh.
7. Dark and Light themes: all texts readable; no hard-coded colors.

- [ ] **Step 13: Commit**

```bash
git add src/ReBackup.Core/Backup/BackupRunner.cs tests/ReBackup.Core.Tests/Backup/BackupRunnerIndexTests.cs src/ReBackup.App
git commit -m "feat: runner feeds the version index; Versions tab lists and indexes the target's versions" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 5: Versions tab — tree, compare, search, file history

**Files:**
- Create: `src/ReBackup.App/ViewModels/VersionTreeNode.cs`, `src/ReBackup.App/ViewModels/VersionTreeViewModel.cs`, `src/ReBackup.App/ViewModels/VersionListItems.cs`, `src/ReBackup.App/ViewModels/VersionsViewModel.Tree.cs`
- Modify (replace whole file): `src/ReBackup.App/Services/IFolderOpener.cs`, `src/ReBackup.App/Views/VersionsView.xaml`, `src/ReBackup.App/Views/VersionsView.xaml.cs`
- Modify: `src/ReBackup.App/Theme/Icons.xaml`, `src/ReBackup.App/Theme/Colors.Dark.xaml`, `src/ReBackup.App/Theme/Colors.Light.xaml`, `src/ReBackup.App/Theme/Controls.xaml`, `src/ReBackup.App/Views/IgnorePreviewView.xaml`

**Interfaces:**
- Consumes: Task 2 queries (`FindPath`, `Children`, `Compare`, `History`, `Search`, `SearchLimit`, `IndexChild`, `IndexStats`, `DiffStatus`, `HistoryEntry`, `HistoryStatus`, `SearchHit`); Task 4 `VersionsViewModel` part 1 (`_index`, `_files`, `_context`, `VersionRows`, `SelectedVersion`, hooks `OnCreated`, `OnIndexReady`), `VersionRowViewModel` (`Indexed`, `IndexState`, `Info`, `DateText`, `IsManaged`); `PathUtil.IsSameOrInside`; `RangeObservableCollection<T>`.
- Produces:
  - `IFolderOpener.OpenFile(string versionFolder, string relativePath)` and `IFolderOpener.ShowInExplorer(string versionFolder, string relativePath)` (both `bool`; false unless the entry exists and stays inside the version folder; `ExplorerFolderOpener` starts the associated program / `explorer.exe /select,"<path>"`)
  - `VersionTreeNode`: `Entry` (`IndexChild?`), `Message`, `IsMessage`, `PathId`, `Name`, `Path`, `IsDirectory`, `IsExpandable`, `Depth`, `Indent`, `OnlyInOther`, `Stats`, `SizeText`, `FilesText`, `ModifiedText`, `Status` (`DiffStatus?`, null = not comparing), `StatusText`, `IsExpanded`
  - `VersionTreeViewModel`: `Rows`, `SelectedNode`, `ShowMessage(string)`, `Task ShowAsync(VersionIndex, long versionId, long? otherId, IReadOnlyDictionary<long, DiffStatus>? statuses, bool changedOnly)`, `SetChangedOnly(bool)`, `Task RevealAsync(string path, bool isDirectory)`
  - `CompareChoice(string Label, VersionRowViewModel? Row)`, `SearchHitRow(SearchHit)` (`Name`, `Path`, `IsDirectory`, `SizeText`), `FileHistoryRow(HistoryEntry, string? versionFolder, string relativePath)` (`Status`, `VersionName`, `DateText`, `StatusText`, `SizeText`, `ModifiedText`, `VersionFolder`, `RelativePath`, `CanOpen`)
  - `VersionsViewModel` part 2: `Tree`, `CompareChoices`, `SelectedCompare`, `IsComparing`, `ChangedOnly`, `SwapCommand`, `SearchText`, `IsSearchActive`, `SearchSummary`, `SearchResults`, `SelectedSearchHit`, `ShowHistory`, `HistoryTitle`, `HistoryRows`, `OpenHistoryCopyCommand`; partial hook `OnTreeSelectionChanged()` that Task 6 implements
  - Resources: `Icon.Search` (`E721`), `Icon.Swap` (`E8AB`), `Icon.Document` (`E8A5`), `Icon.Folder` (`E8B7`); `Brush.DiffAdded` in both palettes; `ToggleButton.Expander` now in `Theme/Controls.xaml`

Tree semantics (rulings 3 and 10): the tree shows version A's root entries (no root row); folders load their children with `Children(A, folder, B)` on first expansion (a "Loading…" row meanwhile); expanded folders are remembered by path id, which is the same in every version, so switching versions keeps them open. While comparing, `Compare(older, newer)` gives every row its status; "Changed only" hides Unchanged rows (a revealed search hit turns it off if needed). Search hits replace the tree; selecting a hit clears the search and reveals the entry.

- [ ] **Step 1: Icons, the diff color and the shared expander style**

In `src/ReBackup.App/Theme/Icons.xaml`, after `    <sys:String x:Key="Icon.Versions">&#xE81E;</sys:String>` add:

```xml
    <sys:String x:Key="Icon.Search">&#xE721;</sys:String>
    <sys:String x:Key="Icon.Swap">&#xE8AB;</sys:String>
    <sys:String x:Key="Icon.Document">&#xE8A5;</sys:String>
    <sys:String x:Key="Icon.Folder">&#xE8B7;</sys:String>
```

In `src/ReBackup.App/Theme/Colors.Dark.xaml`, after `    <SolidColorBrush x:Key="Brush.Badge.YearlyText" Color="#F2B84B" po:Freeze="True" />` add:

```xml

    <!-- Versions tab: an entry that is new in the later version of a comparison (Changed uses Warning, Deleted Danger). -->
    <SolidColorBrush x:Key="Brush.DiffAdded" Color="#4FD08B" po:Freeze="True" />
```

In `src/ReBackup.App/Theme/Colors.Light.xaml`, after `    <SolidColorBrush x:Key="Brush.Badge.YearlyText" Color="#7E5300" po:Freeze="True" />` add:

```xml

    <!-- Versions tab: an entry that is new in the later version of a comparison (Changed uses Warning, Deleted Danger). -->
    <SolidColorBrush x:Key="Brush.DiffAdded" Color="#1B7F4B" po:Freeze="True" />
```

In `src/ReBackup.App/Views/IgnorePreviewView.xaml`, delete the whole local style `ToggleButton.Expander` (from the comment `<!-- Expand/collapse chevron of a tree row. -->` through its closing `</Style>`, the last entry of `UserControl.Resources`). In `src/ReBackup.App/Theme/Controls.xaml`, insert directly above the line `    <!-- One item of a segmented control. Put the items in a horizontal StackPanel inside a Border with`:

```xml
    <!-- Expand/collapse chevron of a row of a flattened tree (ignore preview, versions). The row needs IsExpandable. -->
    <Style x:Key="ToggleButton.Expander" TargetType="ToggleButton" BasedOn="{StaticResource {x:Type ToggleButton}}">
        <Setter Property="Width" Value="18" />
        <Setter Property="Height" Value="18" />
        <Setter Property="Margin" Value="0,0,4,0" />
        <Setter Property="Focusable" Value="False" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.TextMuted}" />
        <Setter Property="Content" Value="{StaticResource Icon.ChevronRight}" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ToggleButton">
                    <Border x:Name="Bd" Background="Transparent" CornerRadius="4">
                        <TextBlock Style="{StaticResource TextBlock.Icon}" FontSize="9" HorizontalAlignment="Center"
                                   Foreground="{TemplateBinding Foreground}" Text="{TemplateBinding Content}" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.Raised}" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
        <Style.Triggers>
            <Trigger Property="IsChecked" Value="True">
                <Setter Property="Content" Value="{StaticResource Icon.ChevronDown}" />
            </Trigger>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
            </Trigger>
            <DataTrigger Binding="{Binding IsExpandable}" Value="False">
                <Setter Property="Visibility" Value="Hidden" />
            </DataTrigger>
        </Style.Triggers>
    </Style>
```

(The style is unchanged apart from its comment and indentation; the ignore preview keeps using it by key.)

- [ ] **Step 2: Open and select entries of a version in Explorer**

Replace `src/ReBackup.App/Services/IFolderOpener.cs` with:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;

namespace ReBackup.App.Services;

/// <summary>Shows version folders and the files in them in Explorer.</summary>
public interface IFolderOpener
{
    /// <summary>
    /// Opens the folder <paramref name="versionName"/> directly inside <paramref name="target"/>. Nothing is started
    /// (false) unless the name is one plain folder name and that folder exists right now.
    /// </summary>
    bool OpenVersionFolder(string? target, string? versionName);

    /// <summary>
    /// Opens a file of a version with the program Windows associates with it. False (nothing started) unless the path
    /// stays inside the version folder and the file exists right now.
    /// </summary>
    bool OpenFile(string versionFolder, string relativePath);

    /// <summary>Opens Explorer on the entry's folder with the entry selected; false like <see cref="OpenFile"/>.</summary>
    bool ShowInExplorer(string versionFolder, string relativePath);
}

/// <summary>Starts <c>explorer.exe</c> or the associated program; only for existing entries of a version (never arbitrary text).</summary>
public sealed class ExplorerFolderOpener : IFolderOpener
{
    private static string Explorer =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    public bool OpenVersionFolder(string? target, string? versionName)
    {
        if (VersionName.ExistingFolderIn(target, versionName) is not { } folder)
            return false;
        // Quoted, so commas and spaces in the name are part of the path; a folder name cannot hold a quote.
        return Start(new ProcessStartInfo(Explorer, $"\"{folder}\"") { UseShellExecute = false });
    }

    public bool OpenFile(string versionFolder, string relativePath) =>
        EntryIn(versionFolder, relativePath) is { } path && File.Exists(path) &&
        Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public bool ShowInExplorer(string versionFolder, string relativePath) =>
        EntryIn(versionFolder, relativePath) is { } path && (File.Exists(path) || Directory.Exists(path)) &&
        Start(new ProcessStartInfo(Explorer, $"/select,\"{path}\"") { UseShellExecute = false });

    /// <summary>The full path of the entry; null when it would leave the version folder.</summary>
    private static string? EntryIn(string versionFolder, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(versionFolder) || !Path.IsPathFullyQualified(versionFolder) ||
            Path.IsPathRooted(relativePath) || relativePath.Contains('"'))
            return null;
        var path = Path.GetFullPath(Path.Combine(versionFolder, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return PathUtil.IsSameOrInside(path, versionFolder) ? path : null;
    }

    private static bool Start(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 3: Tree rows and the lazy tree**

Create `src/ReBackup.App/ViewModels/VersionTreeNode.cs`:

```csharp
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.IO;
using ReBackup.Core.Versions;

namespace ReBackup.App.ViewModels;

/// <summary>
/// A row of the version tree: a file or folder of version A (or, while comparing, of version B only), or a message
/// row ("Loading…"). Children are loaded when the folder is first expanded.
/// </summary>
public sealed partial class VersionTreeNode : ObservableObject
{
    private const double IndentPerLevel = 16;
    private readonly VersionTreeViewModel? _tree;
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private DiffStatus? _status;

    internal VersionTreeNode(VersionTreeViewModel tree, IndexChild entry, int depth, DiffStatus? status)
    {
        _tree = tree;
        Entry = entry;
        Depth = depth;
        Status = status;
    }

    /// <summary>A message row ("Loading…", "Indexing…", "Empty folder").</summary>
    internal VersionTreeNode(string message, int depth)
    {
        Message = message;
        Depth = depth;
    }

    public IndexChild? Entry { get; }
    public string? Message { get; }
    public int Depth { get; }

    /// <summary>The folder's children once loaded; null before the first expansion.</summary>
    internal List<VersionTreeNode>? Children { get; set; }

    public bool IsMessage => Entry is null;
    public long PathId => Entry?.PathId ?? -1;
    public string Name => Entry?.Name ?? Message ?? "";
    public string Path => Entry?.Path ?? "";
    public bool IsDirectory => Entry?.IsDirectory ?? false;
    public bool IsExpandable => IsDirectory;
    public Thickness Indent => new(Depth * IndentPerLevel, 0, 0, 0);

    /// <summary>The entry exists only in version B of the comparison.</summary>
    public bool OnlyInOther => Entry is { InVersion: null };

    /// <summary>The values shown: version A's, or B's for entries only in B.</summary>
    public IndexStats? Stats => Entry?.InVersion ?? Entry?.InOther;

    public string SizeText => Stats is { } stats ? ByteSize.Format(stats.Size) : "";

    public string FilesText => IsDirectory && Stats is { } stats ? stats.Files.ToString("N0", CultureInfo.CurrentCulture) : "";

    public string ModifiedText => Stats?.MtimeUtc is { } mtime
        ? mtime.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        : "";

    public string StatusText => Status?.ToString() ?? "";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
                _tree?.OnExpandedChanged(this, value);
        }
    }

    /// <summary>Sets the flag while the tree rebuilds its rows, without asking it to load or rebuild again.</summary>
    internal void SyncExpanded(bool value) => SetProperty(ref _isExpanded, value, nameof(IsExpanded));
}
```

Create `src/ReBackup.App/ViewModels/VersionTreeViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Versions;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The tree of one version (A), flattened to its visible rows for a virtualized list. Folders load their children from
/// the index when first expanded (off the UI thread). While comparing, entries only in B are shown too and every row
/// carries its status; "changed only" hides Unchanged rows. Expanded folders are kept by path id across reloads.
/// </summary>
public sealed partial class VersionTreeViewModel : ObservableObject
{
    private readonly HashSet<long> _expanded = [];
    private List<VersionTreeNode> _roots = [];
    private VersionIndex? _index;
    private long _versionId;
    private long? _otherId;
    private IReadOnlyDictionary<long, DiffStatus>? _statuses;
    private bool _changedOnly;
    private int _generation;

    [ObservableProperty] private VersionTreeNode? _selectedNode;

    public RangeObservableCollection<VersionTreeNode> Rows { get; } = new();

    /// <summary>Shows a message instead of a tree (no version, not indexed yet, an error).</summary>
    public void ShowMessage(string message)
    {
        _generation++;
        _index = null;
        _roots = [new VersionTreeNode(message, 0)];
        Rebuild();
    }

    /// <summary>
    /// Shows version <paramref name="versionId"/>; with <paramref name="otherId"/> and <paramref name="statuses"/> as a
    /// comparison. Folders that were expanded before stay expanded when they exist.
    /// </summary>
    public async Task ShowAsync(VersionIndex index, long versionId, long? otherId,
        IReadOnlyDictionary<long, DiffStatus>? statuses, bool changedOnly)
    {
        var generation = ++_generation;
        var expanded = _expanded.ToHashSet();
        var roots = await Task.Run(() => LoadTree(index, versionId, otherId, statuses, expanded));
        if (generation != _generation)
            return;

        _index = index;
        _versionId = versionId;
        _otherId = otherId;
        _statuses = statuses;
        _changedOnly = changedOnly;
        _roots = roots.Count == 0 ? [new VersionTreeNode("This version holds no files.", 0)] : roots;
        Rebuild();
    }

    /// <summary>Hides or shows Unchanged rows while comparing.</summary>
    public void SetChangedOnly(bool changedOnly)
    {
        _changedOnly = changedOnly;
        Rebuild();
    }

    /// <summary>Expands the folders above <paramref name="path"/> (loading them as needed) and selects its row.</summary>
    public async Task RevealAsync(string path, bool isDirectory)
    {
        if (_index is not { } index)
            return;
        var generation = _generation;
        var parts = path.Split('/');
        var nodes = _roots;
        VersionTreeNode? found = null;
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            var node = nodes.FirstOrDefault(n => !n.IsMessage && n.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase) &&
                                                 (!last || n.IsDirectory == isDirectory));
            if (node is null)
                break;
            found = node;
            if (last)
                break;
            if (node.Children is null)
            {
                var (versionId, otherId, statuses) = (_versionId, _otherId, _statuses);
                var children = await Task.Run(() => Load(index, versionId, otherId, statuses, node.PathId, node.Depth + 1));
                if (generation != _generation)
                    return;
                node.Children = children;
            }
            _expanded.Add(node.PathId);
            nodes = node.Children;
        }

        if (found is not null && _changedOnly && found.Status == DiffStatus.Unchanged)
            _changedOnly = false;   // a revealed entry must be visible
        Rebuild();
        if (found is not null)
            SelectedNode = found;
    }

    internal async void OnExpandedChanged(VersionTreeNode node, bool expanded)
    {
        if (!expanded)
        {
            _expanded.Remove(node.PathId);
            Rebuild();
            return;
        }

        _expanded.Add(node.PathId);
        if (node.Children is not null || _index is not { } index)
        {
            Rebuild();
            return;
        }

        var generation = _generation;
        var (versionId, otherId, statuses) = (_versionId, _otherId, _statuses);
        Rebuild();   // shows "Loading…" below the folder
        try
        {
            var children = await Task.Run(() => Load(index, versionId, otherId, statuses, node.PathId, node.Depth + 1));
            if (generation != _generation)
                return;
            node.Children = children;
        }
        catch (Exception ex)
        {
            if (generation != _generation)
                return;
            node.Children = [new VersionTreeNode($"Cannot be read: {ex.Message}", node.Depth + 1)];
        }
        Rebuild();
    }

    /// <summary>Loads the root level and, recursively, every folder that was expanded before.</summary>
    private List<VersionTreeNode> LoadTree(VersionIndex index, long versionId, long? otherId,
        IReadOnlyDictionary<long, DiffStatus>? statuses, HashSet<long> expanded)
    {
        if (index.FindPath("", isDirectory: true) is not { } rootId)
            return [];
        var roots = Load(index, versionId, otherId, statuses, rootId, depth: 0);
        var pending = new Stack<VersionTreeNode>(roots.Where(n => n.IsDirectory && expanded.Contains(n.PathId)));
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            node.Children = Load(index, versionId, otherId, statuses, node.PathId, node.Depth + 1);
            foreach (var child in node.Children.Where(n => n.IsDirectory && expanded.Contains(n.PathId)))
                pending.Push(child);
        }
        return roots;
    }

    private List<VersionTreeNode> Load(VersionIndex index, long versionId, long? otherId,
        IReadOnlyDictionary<long, DiffStatus>? statuses, long folderId, int depth) =>
        index.Children(versionId, folderId, otherId)
            .Select(child => new VersionTreeNode(this, child, depth,
                statuses is null ? null : statuses.GetValueOrDefault(child.PathId, DiffStatus.Unchanged)))
            .ToList();

    private void Rebuild()
    {
        var selected = SelectedNode;
        var rows = new List<VersionTreeNode>();
        Append(rows, _roots);
        Rows.ReplaceAll(rows);
        SelectedNode = selected is not null && rows.Contains(selected) ? selected : null;
    }

    private void Append(List<VersionTreeNode> rows, List<VersionTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (_changedOnly && _statuses is not null && node.Status == DiffStatus.Unchanged)
                continue;
            var expanded = node.IsDirectory && _expanded.Contains(node.PathId);
            node.SyncExpanded(expanded);
            rows.Add(node);
            if (!expanded)
                continue;
            if (node.Children is null)
                rows.Add(new VersionTreeNode("Loading…", node.Depth + 1));
            else if (node.Children.Count == 0)
                rows.Add(new VersionTreeNode("Empty folder", node.Depth + 1));
            else
                Append(rows, node.Children);
        }
    }
}
```

- [ ] **Step 4: List items of the compare box, the search and the history**

Create `src/ReBackup.App/ViewModels/VersionListItems.cs`:

```csharp
using System.Globalization;
using ReBackup.Core.IO;
using ReBackup.Core.Versions;

namespace ReBackup.App.ViewModels;

/// <summary>An entry of the "Compare with" box: another version, or "—" (<paramref name="Row"/> null) for no comparison.</summary>
public sealed record CompareChoice(string Label, VersionRowViewModel? Row);

/// <summary>A search hit; selecting it reveals the entry in the tree.</summary>
public sealed class SearchHitRow(SearchHit hit)
{
    public SearchHit Hit { get; } = hit;
    public string Name => Hit.Name;
    public string Path => Hit.Path;
    public bool IsDirectory => Hit.IsDirectory;
    public string SizeText => ByteSize.Format(Hit.Size);
}

/// <summary>A file in one version, for the history card (newest version first).</summary>
public sealed class FileHistoryRow(HistoryEntry entry, string? versionFolder, string relativePath)
{
    public HistoryEntry Entry { get; } = entry;
    public HistoryStatus Status => Entry.Status;
    public string VersionName => Entry.Version.Name;
    public string DateText => Entry.Version.LocalTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string StatusText => Entry.Status switch
    {
        HistoryStatus.New => "New",
        HistoryStatus.Changed => "Changed",
        HistoryStatus.Unchanged => "Unchanged",
        HistoryStatus.Deleted => "Deleted",
        _ => "—",
    };

    public string SizeText => Entry.Size is { } size ? ByteSize.Format(size) : "";

    public string ModifiedText => Entry.MtimeUtc is { } mtime
        ? mtime.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        : "";

    /// <summary>The version folder (null when it is no longer listed) and the file's path in it.</summary>
    public string? VersionFolder { get; } = versionFolder;
    public string RelativePath { get; } = relativePath;

    /// <summary>The folder button shows the copy in Explorer; only for versions that hold the file.</summary>
    public bool CanOpen => Entry.Present && VersionFolder is not null;
}
```

- [ ] **Step 5: The view model's tree part**

Create `src/ReBackup.App/ViewModels/VersionsViewModel.Tree.cs`:

```csharp
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Versions;

namespace ReBackup.App.ViewModels;

/// <summary>The Versions tab: tree of version A, comparison with B, search and the history of a file.</summary>
public sealed partial class VersionsViewModel
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);
    private static readonly CompareChoice NoCompare = new("—", null);
    private int _suppressReload;
    private int _treeGeneration;
    private int _historyGeneration;
    private CancellationTokenSource? _searchCts;

    [ObservableProperty] private IReadOnlyList<CompareChoice> _compareChoices = [NoCompare];

    /// <summary>Version B; <see cref="NoCompare"/> (or null) while nothing is compared.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComparing))]
    [NotifyCanExecuteChangedFor(nameof(SwapCommand))]
    private CompareChoice? _selectedCompare = NoCompare;

    /// <summary>Hides Unchanged entries while comparing.</summary>
    [ObservableProperty] private bool _changedOnly;

    /// <summary>Substring or wildcard pattern (<c>*</c>, <c>?</c>); a non-blank text replaces the tree with the hits.</summary>
    [ObservableProperty] private string _searchText = "";

    [ObservableProperty] private bool _isSearchActive;
    [ObservableProperty] private string _searchSummary = "";
    [ObservableProperty] private SearchHitRow? _selectedSearchHit;

    [ObservableProperty] private bool _showHistory;
    [ObservableProperty] private string _historyTitle = "";

    public VersionTreeViewModel Tree { get; } = new();

    public bool IsComparing => SelectedCompare?.Row is not null;

    public RangeObservableCollection<SearchHitRow> SearchResults { get; } = new();

    /// <summary>The selected file in every indexed version, newest first.</summary>
    public RangeObservableCollection<FileHistoryRow> HistoryRows { get; } = new();

    partial void OnCreated() => Tree.PropertyChanged += OnTreePropertyChanged;

    partial void OnIndexReady()
    {
        RebuildCompareChoices();
        _ = ReloadTreeAsync();
    }

    partial void OnSelectedVersionChanged(VersionRowViewModel? value)
    {
        RebuildCompareChoices();
        SearchText = "";
        if (_suppressReload == 0)
            _ = ReloadTreeAsync();
    }

    partial void OnSelectedCompareChanged(CompareChoice? value)
    {
        if (value?.Row is null)
            ChangedOnly = false;
        if (_suppressReload == 0)
            _ = ReloadTreeAsync();
    }

    partial void OnChangedOnlyChanged(bool value) => Tree.SetChangedOnly(value);

    partial void OnSearchTextChanged(string value) => _ = SearchAsync(value);

    partial void OnSelectedSearchHitChanged(SearchHitRow? value)
    {
        if (value is null)
            return;
        var hit = value.Hit;
        SearchText = "";   // back to the tree
        _ = Tree.RevealAsync(hit.Path, hit.IsDirectory);
    }

    /// <summary>Runs whenever the tree's selection changes (the actions of the tab hook in here).</summary>
    partial void OnTreeSelectionChanged();

    /// <summary>Shows B's tree compared with A: the two versions change places.</summary>
    [RelayCommand(CanExecute = nameof(IsComparing))]
    private void Swap()
    {
        if (SelectedVersion is not { } a || SelectedCompare?.Row is not { } b)
            return;
        _suppressReload++;
        try
        {
            SelectedVersion = b;
            SelectedCompare = CompareChoices.FirstOrDefault(c => ReferenceEquals(c.Row, a)) ?? NoCompare;
        }
        finally
        {
            _suppressReload--;
        }
        _ = ReloadTreeAsync();
    }

    /// <summary>Shows the history row's copy of the file in Explorer.</summary>
    [RelayCommand]
    private async Task OpenHistoryCopyAsync(FileHistoryRow? row)
    {
        if (row is not { CanOpen: true, VersionFolder: { } folder })
            return;
        var path = row.RelativePath;
        if (!await Task.Run(() => _files.ShowInExplorer(folder, path)))
            _context.ReportStatus($"The copy in \"{row.VersionName}\" no longer exists.");
    }

    private void RebuildCompareChoices()
    {
        var current = SelectedCompare?.Row;
        var choices = new List<CompareChoice> { NoCompare };
        choices.AddRange(VersionRows
            .Where(row => !ReferenceEquals(row, SelectedVersion))
            .Select(row => new CompareChoice(row.DateText + (row.IsManaged ? "" : " (not managed)"), row)));
        _suppressReload++;
        try
        {
            CompareChoices = choices;
            SelectedCompare = choices.FirstOrDefault(c => c.Row is not null && ReferenceEquals(c.Row, current)) ?? NoCompare;
        }
        finally
        {
            _suppressReload--;
        }
    }

    /// <summary>
    /// Shows version A's tree; while comparing, with the statuses from the older to the newer of the two versions
    /// (§11: Added = only in the newer one), whichever of them is A.
    /// </summary>
    private async Task ReloadTreeAsync()
    {
        var generation = ++_treeGeneration;
        if (SelectedVersion is not { } a)
        {
            Tree.ShowMessage(VersionRows.Count == 0 ? "" : "Select a version.");
            return;
        }
        if (_index is not { } index || a.Indexed is not { } versionA)
        {
            Tree.ShowMessage(a.IndexState == IndexState.Failed ? "This version could not be indexed." : "Indexing…");
            return;
        }

        var other = SelectedCompare?.Row?.Indexed;
        IReadOnlyDictionary<long, DiffStatus>? statuses = null;
        try
        {
            if (other is not null)
            {
                var aFirst = versionA.LocalTime != other.LocalTime
                    ? versionA.LocalTime < other.LocalTime
                    : string.Compare(versionA.Name, other.Name, StringComparison.OrdinalIgnoreCase) <= 0;
                var (older, newer) = aFirst ? (versionA, other) : (other, versionA);
                statuses = await Task.Run(() => index.Compare(older.Id, newer.Id));
                if (generation != _treeGeneration)
                    return;
            }
            await Tree.ShowAsync(index, versionA.Id, other?.Id, statuses, ChangedOnly);
        }
        catch (Exception ex)
        {
            if (generation == _treeGeneration)
                Tree.ShowMessage($"The version cannot be read from the index: {ex.Message}");
        }
    }

    private async Task SearchAsync(string text)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        if (string.IsNullOrWhiteSpace(text))
        {
            IsSearchActive = false;
            SearchResults.ReplaceAll([]);
            SearchSummary = "";
            return;
        }

        try
        {
            await Task.Delay(SearchDelay, cts.Token);
            IsSearchActive = true;
            if (_index is not { } index || SelectedVersion?.Indexed is not { } version)
            {
                SearchResults.ReplaceAll([]);
                SearchSummary = "The version is not indexed yet.";
                return;
            }

            var hits = await Task.Run(() => index.Search(version.Id, text), cts.Token);
            if (!ReferenceEquals(_searchCts, cts))
                return;
            SearchResults.ReplaceAll(hits.Select(hit => new SearchHitRow(hit)));
            SearchSummary = hits.Count == 0 ? "No matches"
                : hits.Count >= VersionIndex.SearchLimit ? $"The first {VersionIndex.SearchLimit} matches"
                : string.Create(CultureInfo.CurrentCulture, $"{hits.Count:N0} matches");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_searchCts, cts))
                SearchSummary = $"The search failed: {ex.Message}";
        }
    }

    private void OnTreePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VersionTreeViewModel.SelectedNode))
            return;
        _ = LoadHistoryAsync(Tree.SelectedNode);
        OnTreeSelectionChanged();
    }

    private async Task LoadHistoryAsync(VersionTreeNode? node)
    {
        var generation = ++_historyGeneration;
        if (node is not { IsMessage: false, IsDirectory: false } || _index is not { } index)
        {
            ShowHistory = false;
            HistoryRows.ReplaceAll([]);
            return;
        }

        HistoryTitle = "HISTORY OF " + node.Name;
        ShowHistory = true;
        try
        {
            var entries = await Task.Run(() => index.History(node.PathId));
            if (generation != _historyGeneration)
                return;
            var folders = VersionRows.ToDictionary(r => r.Name, r => r.Info.Path, StringComparer.OrdinalIgnoreCase);
            HistoryRows.ReplaceAll(entries.Reverse()
                .Select(entry => new FileHistoryRow(entry, folders.GetValueOrDefault(entry.Version.Name), node.Path)));
        }
        catch (Exception ex)
        {
            if (generation != _historyGeneration)
                return;
            HistoryRows.ReplaceAll([]);
            HistoryTitle = $"HISTORY OF {node.Name} — cannot be read: {ex.Message}";
        }
    }
}
```

Notes: `_suppressReload` is a counter because `Swap` sets `SelectedVersion` (which rebuilds the compare choices under its own suppression) and then `SelectedCompare`; only one reload must run at the end. A `ComboBox` pushes `null` into `SelectedCompare` while its items are replaced — that happens inside the suppressed window and is overwritten right after.

- [ ] **Step 6: The view**

Replace `src/ReBackup.App/Views/VersionsView.xaml` with:

```xml
<UserControl x:Class="ReBackup.App.Views.VersionsView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:core="clr-namespace:ReBackup.Core.Versions;assembly=ReBackup.Core">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVisibility" />

        <!-- Right-aligned number cell. -->
        <Style x:Key="TextBlock.Number" TargetType="TextBlock">
            <Setter Property="HorizontalAlignment" Value="Stretch" />
            <Setter Property="TextAlignment" Value="Right" />
            <Setter Property="VerticalAlignment" Value="Center" />
            <Setter Property="Margin" Value="0,0,6,0" />
            <Setter Property="Typography.NumeralAlignment" Value="Tabular" />
        </Style>

        <Style x:Key="Header.Number" TargetType="GridViewColumnHeader" BasedOn="{StaticResource {x:Type GridViewColumnHeader}}">
            <Setter Property="HorizontalContentAlignment" Value="Right" />
            <Setter Property="Padding" Value="6,0,12,0" />
        </Style>

        <!-- A status line that takes no room while it is empty. -->
        <Style x:Key="TextBlock.Line" TargetType="TextBlock">
            <Setter Property="Margin" Value="0,4,0,0" />
            <Setter Property="FontSize" Value="12" />
            <Setter Property="TextWrapping" Value="Wrap" />
            <Style.Triggers>
                <Trigger Property="Text" Value="">
                    <Setter Property="Visibility" Value="Collapsed" />
                </Trigger>
            </Style.Triggers>
        </Style>

        <!-- Status dot of a compared entry: Added DiffAdded, Changed Warning, Deleted Danger, Unchanged TextFaint. -->
        <Style x:Key="Ellipse.Diff" TargetType="Ellipse">
            <Setter Property="Width" Value="8" />
            <Setter Property="Height" Value="8" />
            <Setter Property="VerticalAlignment" Value="Center" />
            <Setter Property="Fill" Value="{DynamicResource Brush.TextFaint}" />
            <Style.Triggers>
                <DataTrigger Binding="{Binding Status}" Value="{x:Static core:DiffStatus.Added}">
                    <Setter Property="Fill" Value="{DynamicResource Brush.DiffAdded}" />
                </DataTrigger>
                <DataTrigger Binding="{Binding Status}" Value="{x:Static core:DiffStatus.Changed}">
                    <Setter Property="Fill" Value="{DynamicResource Brush.Warning}" />
                </DataTrigger>
                <DataTrigger Binding="{Binding Status}" Value="{x:Static core:DiffStatus.Deleted}">
                    <Setter Property="Fill" Value="{DynamicResource Brush.Danger}" />
                </DataTrigger>
                <DataTrigger Binding="{Binding Status}" Value="{x:Null}">
                    <Setter Property="Visibility" Value="Collapsed" />
                </DataTrigger>
            </Style.Triggers>
        </Style>

        <!-- Status dot of a history row: New DiffAdded, Changed Warning, Deleted Danger, the rest TextFaint. -->
        <Style x:Key="Ellipse.History" TargetType="Ellipse">
            <Setter Property="Width" Value="8" />
            <Setter Property="Height" Value="8" />
            <Setter Property="VerticalAlignment" Value="Center" />
            <Setter Property="Fill" Value="{DynamicResource Brush.TextFaint}" />
            <Style.Triggers>
                <DataTrigger Binding="{Binding Status}" Value="{x:Static core:HistoryStatus.New}">
                    <Setter Property="Fill" Value="{DynamicResource Brush.DiffAdded}" />
                </DataTrigger>
                <DataTrigger Binding="{Binding Status}" Value="{x:Static core:HistoryStatus.Changed}">
                    <Setter Property="Fill" Value="{DynamicResource Brush.Warning}" />
                </DataTrigger>
                <DataTrigger Binding="{Binding Status}" Value="{x:Static core:HistoryStatus.Deleted}">
                    <Setter Property="Fill" Value="{DynamicResource Brush.Danger}" />
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </UserControl.Resources>

    <Grid Margin="24,20">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="300" />
            <ColumnDefinition Width="16" />
            <ColumnDefinition Width="*" MinWidth="360" />
        </Grid.ColumnDefinitions>

        <!-- ======================================================== VERSIONS -->
        <Border Grid.Column="0" Style="{StaticResource Border.Card}" Padding="0,14,0,8">
            <DockPanel>
                <DockPanel DockPanel.Dock="Top" Margin="18,0,10,6">
                    <Button DockPanel.Dock="Right" Style="{StaticResource Button.Icon}" Content="{StaticResource Icon.Refresh}"
                            ToolTip="Read the target again and update the index" AutomationProperties.Name="Refresh versions"
                            Command="{Binding Versions.RefreshCommand}" />
                    <TextBlock Style="{StaticResource TextBlock.CardTitle}" VerticalAlignment="Center" Text="VERSIONS" />
                </DockPanel>

                <StackPanel DockPanel.Dock="Top" Margin="18,0,18,8">
                    <ProgressBar IsIndeterminate="True" Height="4"
                                 Visibility="{Binding Versions.IsSyncing, Converter={StaticResource BoolToVisibility}}" />
                    <TextBlock Style="{StaticResource TextBlock.Line}" Foreground="{DynamicResource Brush.TextMuted}"
                               Text="{Binding Versions.SyncText}" />
                    <TextBlock Style="{StaticResource TextBlock.Line}" Foreground="{DynamicResource Brush.Danger}"
                               Text="{Binding Versions.Error}" />
                </StackPanel>

                <Grid>
                    <ListBox ItemsSource="{Binding Versions.VersionRows}" SelectedItem="{Binding Versions.SelectedVersion}"
                             ScrollViewer.HorizontalScrollBarVisibility="Disabled" AutomationProperties.Name="Versions"
                             VirtualizingPanel.IsVirtualizing="True">
                        <ListBox.ItemContainerStyle>
                            <Style TargetType="ListBoxItem" BasedOn="{StaticResource {x:Type ListBoxItem}}">
                                <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                                <Setter Property="ToolTip" Value="{Binding Name}" />
                            </Style>
                        </ListBox.ItemContainerStyle>
                        <ListBox.ItemTemplate>
                            <DataTemplate>
                                <StackPanel Margin="10,6">
                                    <StackPanel.Style>
                                        <Style TargetType="StackPanel">
                                            <Style.Triggers>
                                                <!-- Not managed: greyed, with the reason below. -->
                                                <DataTrigger Binding="{Binding IsManaged}" Value="False">
                                                    <Setter Property="Opacity" Value="0.6" />
                                                </DataTrigger>
                                            </Style.Triggers>
                                        </Style>
                                    </StackPanel.Style>
                                    <DockPanel>
                                        <TextBlock DockPanel.Dock="Right" Style="{StaticResource TextBlock.Muted}" FontSize="12"
                                                   Typography.NumeralAlignment="Tabular" Text="{Binding SizeText}" />
                                        <TextBlock FontWeight="SemiBold" Typography.NumeralAlignment="Tabular" Text="{Binding DateText}" />
                                    </DockPanel>
                                    <DockPanel Margin="0,2,0,0">
                                        <TextBlock DockPanel.Dock="Right" FontSize="11" Foreground="{DynamicResource Brush.TextFaint}"
                                                   Text="{Binding IndexStateText}" />
                                        <TextBlock Style="{StaticResource TextBlock.Muted}" FontSize="12" Text="{Binding FilesText}" />
                                    </DockPanel>
                                    <TextBlock Margin="0,2,0,0" FontSize="12" Foreground="{DynamicResource Brush.Warning}"
                                               TextWrapping="Wrap" Text="{Binding ReasonText}">
                                        <TextBlock.Style>
                                            <Style TargetType="TextBlock">
                                                <Style.Triggers>
                                                    <DataTrigger Binding="{Binding IsManaged}" Value="True">
                                                        <Setter Property="Visibility" Value="Collapsed" />
                                                    </DataTrigger>
                                                </Style.Triggers>
                                            </Style>
                                        </TextBlock.Style>
                                    </TextBlock>
                                </StackPanel>
                            </DataTemplate>
                        </ListBox.ItemTemplate>
                    </ListBox>
                    <TextBlock Style="{StaticResource TextBlock.Muted}" HorizontalAlignment="Center" VerticalAlignment="Center"
                               Text="No versions in the target yet"
                               Visibility="{Binding Versions.ShowEmpty, Converter={StaticResource BoolToVisibility}}" />
                </Grid>
            </DockPanel>
        </Border>

        <!-- ======================================================== Tree, history -->
        <Grid Grid.Column="2">
            <Grid.RowDefinitions>
                <RowDefinition Height="*" MinHeight="200" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>

            <Border Grid.Row="0" Style="{StaticResource Border.Card}" Padding="12">
                <DockPanel>
                    <!-- Toolbar: search, compare, swap, changed only | actions -->
                    <WrapPanel DockPanel.Dock="Top" Margin="0,0,0,8">
                        <Grid Width="220" Margin="0,0,12,6">
                            <TextBox x:Name="SearchBox" Padding="18,0,8,0" VerticalContentAlignment="Center" Height="32"
                                     AutomationProperties.Name="Search names (substring, or * and ?)"
                                     ToolTip="A part of a name, or a pattern with * and ?"
                                     Text="{Binding Versions.SearchText, UpdateSourceTrigger=PropertyChanged}" />
                            <TextBlock Style="{StaticResource TextBlock.Icon}" Margin="10,0,0,0" FontSize="12" IsHitTestVisible="False"
                                       HorizontalAlignment="Left" Foreground="{DynamicResource Brush.TextFaint}" Text="{StaticResource Icon.Search}" />
                        </Grid>
                        <StackPanel Orientation="Horizontal" Margin="0,0,12,6">
                            <TextBlock Style="{StaticResource TextBlock.Muted}" VerticalAlignment="Center" Margin="0,0,8,0" Text="Compare with" />
                            <ComboBox Width="190" ItemsSource="{Binding Versions.CompareChoices}" DisplayMemberPath="Label"
                                      SelectedItem="{Binding Versions.SelectedCompare}" AutomationProperties.Name="Compare with" />
                            <Button Style="{StaticResource Button.Icon}" Margin="4,0,0,0" Content="{StaticResource Icon.Swap}"
                                    ToolTip="Swap: show the other version's tree" AutomationProperties.Name="Swap versions"
                                    Command="{Binding Versions.SwapCommand}" />
                        </StackPanel>
                        <CheckBox Style="{StaticResource CheckBox.Switch}" Margin="0,0,16,6" VerticalAlignment="Center"
                                  Content="Changed only" IsChecked="{Binding Versions.ChangedOnly}"
                                  IsEnabled="{Binding Versions.IsComparing}" />
                    </WrapPanel>

                    <TextBlock DockPanel.Dock="Top" Style="{StaticResource TextBlock.Muted}" Margin="2,0,0,6" FontSize="12"
                               Text="{Binding Versions.SearchSummary}"
                               Visibility="{Binding Versions.IsSearchActive, Converter={StaticResource BoolToVisibility}}" />

                    <Grid>
                        <!-- The tree of version A (with B's entries while comparing). -->
                        <ListView x:Name="TreeList" SelectionMode="Single"
                                  AutomationProperties.Name="Version tree"
                                  ItemsSource="{Binding Versions.Tree.Rows}"
                                  SelectedItem="{Binding Versions.Tree.SelectedNode}"
                                  SelectionChanged="OnTreeSelectionChanged"
                                  SizeChanged="OnTreeSizeChanged"
                                  VirtualizingPanel.IsVirtualizing="True"
                                  VirtualizingPanel.VirtualizationMode="Recycling">
                            <ListView.Style>
                                <Style TargetType="ListView" BasedOn="{StaticResource ListView.Table}">
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Versions.IsSearchActive}" Value="True">
                                            <Setter Property="Visibility" Value="Collapsed" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </ListView.Style>
                            <ListView.ItemContainerStyle>
                                <Style TargetType="ListViewItem" BasedOn="{StaticResource {x:Type ListViewItem}}">
                                    <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                                    <EventSetter Event="PreviewMouseRightButtonDown" Handler="OnRowRightButtonDown" />
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding IsMessage}" Value="True">
                                            <Setter Property="Foreground" Value="{DynamicResource Brush.TextFaint}" />
                                            <Setter Property="FontStyle" Value="Italic" />
                                        </DataTrigger>
                                        <!-- Only in version B: shown with B's values, in italics. -->
                                        <DataTrigger Binding="{Binding OnlyInOther}" Value="True">
                                            <Setter Property="FontStyle" Value="Italic" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </ListView.ItemContainerStyle>
                            <ListView.View>
                                <GridView>
                                    <GridViewColumn x:Name="NameColumn" Header="Name" Width="260">
                                        <GridViewColumn.CellTemplate>
                                            <DataTemplate>
                                                <StackPanel Orientation="Horizontal" Margin="{Binding Indent}">
                                                    <ToggleButton Style="{StaticResource ToggleButton.Expander}"
                                                                  AutomationProperties.Name="Expand" IsChecked="{Binding IsExpanded}" />
                                                    <TextBlock Margin="0,0,6,0" FontSize="12">
                                                        <TextBlock.Style>
                                                            <Style TargetType="TextBlock" BasedOn="{StaticResource TextBlock.Icon}">
                                                                <Setter Property="Text" Value="{StaticResource Icon.Document}" />
                                                                <Setter Property="Foreground" Value="{DynamicResource Brush.TextMuted}" />
                                                                <Style.Triggers>
                                                                    <DataTrigger Binding="{Binding IsDirectory}" Value="True">
                                                                        <Setter Property="Text" Value="{StaticResource Icon.Folder}" />
                                                                        <Setter Property="Foreground" Value="{DynamicResource Brush.Warning}" />
                                                                    </DataTrigger>
                                                                    <DataTrigger Binding="{Binding IsMessage}" Value="True">
                                                                        <Setter Property="Visibility" Value="Collapsed" />
                                                                    </DataTrigger>
                                                                </Style.Triggers>
                                                            </Style>
                                                        </TextBlock.Style>
                                                    </TextBlock>
                                                    <TextBlock VerticalAlignment="Center" TextTrimming="CharacterEllipsis" Text="{Binding Name}" />
                                                </StackPanel>
                                            </DataTemplate>
                                        </GridViewColumn.CellTemplate>
                                    </GridViewColumn>
                                    <GridViewColumn Header="Size" Width="96" HeaderContainerStyle="{StaticResource Header.Number}">
                                        <GridViewColumn.CellTemplate>
                                            <DataTemplate>
                                                <TextBlock Style="{StaticResource TextBlock.Number}" Text="{Binding SizeText}" />
                                            </DataTemplate>
                                        </GridViewColumn.CellTemplate>
                                    </GridViewColumn>
                                    <GridViewColumn Header="Modified" Width="130">
                                        <GridViewColumn.CellTemplate>
                                            <DataTemplate>
                                                <TextBlock Typography.NumeralAlignment="Tabular" Foreground="{DynamicResource Brush.TextMuted}"
                                                           Text="{Binding ModifiedText}" />
                                            </DataTemplate>
                                        </GridViewColumn.CellTemplate>
                                    </GridViewColumn>
                                    <GridViewColumn Header="Status" Width="110">
                                        <GridViewColumn.CellTemplate>
                                            <DataTemplate>
                                                <StackPanel Orientation="Horizontal">
                                                    <Ellipse Style="{StaticResource Ellipse.Diff}" />
                                                    <TextBlock Margin="8,0,0,0" VerticalAlignment="Center" Text="{Binding StatusText}" />
                                                </StackPanel>
                                            </DataTemplate>
                                        </GridViewColumn.CellTemplate>
                                    </GridViewColumn>
                                </GridView>
                            </ListView.View>
                        </ListView>

                        <!-- Search hits replace the tree; selecting one reveals it in the tree. -->
                        <ListView Style="{StaticResource ListView.Table}" SelectionMode="Single" AutomationProperties.Name="Search results"
                                  ItemsSource="{Binding Versions.SearchResults}" SelectedItem="{Binding Versions.SelectedSearchHit}"
                                  Visibility="{Binding Versions.IsSearchActive, Converter={StaticResource BoolToVisibility}}"
                                  VirtualizingPanel.IsVirtualizing="True">
                            <ListView.View>
                                <GridView>
                                    <GridViewColumn Header="Path" Width="460">
                                        <GridViewColumn.CellTemplate>
                                            <DataTemplate>
                                                <StackPanel Orientation="Horizontal">
                                                    <TextBlock Margin="0,0,6,0" FontSize="12">
                                                        <TextBlock.Style>
                                                            <Style TargetType="TextBlock" BasedOn="{StaticResource TextBlock.Icon}">
                                                                <Setter Property="Text" Value="{StaticResource Icon.Document}" />
                                                                <Setter Property="Foreground" Value="{DynamicResource Brush.TextMuted}" />
                                                                <Style.Triggers>
                                                                    <DataTrigger Binding="{Binding IsDirectory}" Value="True">
                                                                        <Setter Property="Text" Value="{StaticResource Icon.Folder}" />
                                                                        <Setter Property="Foreground" Value="{DynamicResource Brush.Warning}" />
                                                                    </DataTrigger>
                                                                </Style.Triggers>
                                                            </Style>
                                                        </TextBlock.Style>
                                                    </TextBlock>
                                                    <TextBlock VerticalAlignment="Center" TextTrimming="CharacterEllipsis" Text="{Binding Path}" />
                                                </StackPanel>
                                            </DataTemplate>
                                        </GridViewColumn.CellTemplate>
                                    </GridViewColumn>
                                    <GridViewColumn Header="Size" Width="96" HeaderContainerStyle="{StaticResource Header.Number}">
                                        <GridViewColumn.CellTemplate>
                                            <DataTemplate>
                                                <TextBlock Style="{StaticResource TextBlock.Number}" Text="{Binding SizeText}" />
                                            </DataTemplate>
                                        </GridViewColumn.CellTemplate>
                                    </GridViewColumn>
                                </GridView>
                            </ListView.View>
                        </ListView>
                    </Grid>
                </DockPanel>
            </Border>

            <!-- ==================================================== HISTORY OF <file> -->
            <Border Grid.Row="1" Style="{StaticResource Border.Card}" Margin="0,12,0,0" Padding="0,14,0,6" Height="240"
                    Visibility="{Binding Versions.ShowHistory, Converter={StaticResource BoolToVisibility}}">
                <DockPanel>
                    <TextBlock DockPanel.Dock="Top" Style="{StaticResource TextBlock.CardTitle}" Margin="18,0,18,8"
                               TextTrimming="CharacterEllipsis" Text="{Binding Versions.HistoryTitle}"
                               ToolTip="{Binding Versions.HistoryTitle}" />
                    <ListView Style="{StaticResource ListView.Table}" ItemsSource="{Binding Versions.HistoryRows}"
                              SelectionMode="Single" AutomationProperties.Name="File history" Margin="8,0">
                        <ListView.View>
                            <GridView>
                                <GridViewColumn Header="Version" Width="140">
                                    <GridViewColumn.CellTemplate>
                                        <DataTemplate>
                                            <TextBlock Typography.NumeralAlignment="Tabular" Text="{Binding DateText}" ToolTip="{Binding VersionName}" />
                                        </DataTemplate>
                                    </GridViewColumn.CellTemplate>
                                </GridViewColumn>
                                <GridViewColumn Header="Status" Width="120">
                                    <GridViewColumn.CellTemplate>
                                        <DataTemplate>
                                            <StackPanel Orientation="Horizontal">
                                                <Ellipse Style="{StaticResource Ellipse.History}" />
                                                <TextBlock Margin="8,0,0,0" VerticalAlignment="Center" Text="{Binding StatusText}" />
                                            </StackPanel>
                                        </DataTemplate>
                                    </GridViewColumn.CellTemplate>
                                </GridViewColumn>
                                <GridViewColumn Header="Size" Width="96" HeaderContainerStyle="{StaticResource Header.Number}">
                                    <GridViewColumn.CellTemplate>
                                        <DataTemplate>
                                            <TextBlock Style="{StaticResource TextBlock.Number}" Text="{Binding SizeText}" />
                                        </DataTemplate>
                                    </GridViewColumn.CellTemplate>
                                </GridViewColumn>
                                <GridViewColumn Header="Modified" Width="140">
                                    <GridViewColumn.CellTemplate>
                                        <DataTemplate>
                                            <TextBlock Typography.NumeralAlignment="Tabular" Foreground="{DynamicResource Brush.TextMuted}"
                                                       Text="{Binding ModifiedText}" />
                                        </DataTemplate>
                                    </GridViewColumn.CellTemplate>
                                </GridViewColumn>
                                <GridViewColumn Width="48">
                                    <GridViewColumn.CellTemplate>
                                        <DataTemplate>
                                            <Button Style="{StaticResource Button.Icon}" Width="28" Height="26"
                                                    ToolTip="Show this copy in Explorer" AutomationProperties.Name="Show this copy in Explorer"
                                                    Visibility="{Binding CanOpen, Converter={StaticResource BoolToVisibility}}"
                                                    Command="{Binding DataContext.Versions.OpenHistoryCopyCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                                                    CommandParameter="{Binding}">
                                                <Image Style="{StaticResource Image.FolderColored}" />
                                            </Button>
                                        </DataTemplate>
                                    </GridViewColumn.CellTemplate>
                                </GridViewColumn>
                            </GridView>
                        </ListView.View>
                    </ListView>
                </DockPanel>
            </Border>
        </Grid>
    </Grid>
</UserControl>
```

Replace `src/ReBackup.App/Views/VersionsView.xaml.cs` with:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReBackup.App.ViewModels;

namespace ReBackup.App.Views;

/// <summary>The Versions tab. Reads the target and syncs the index whenever it becomes visible.</summary>
public partial class VersionsView : UserControl
{
    /// <summary>Width of the theme's thin scroll bar (Theme/Controls.xaml, ScrollBar style), not the system one.</summary>
    private const double ThemeScrollBarWidth = 10;

    /// <summary>The Name column never gets narrower; below that the table scrolls sideways.</summary>
    private const double NameMinWidth = 160;

    public VersionsView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => EnsureLoaded();
        DataContextChanged += (_, _) => EnsureLoaded();
    }

    private void EnsureLoaded()
    {
        if (IsVisible && DataContext is PlanEditorViewModel editor)
            editor.Versions.EnsureLoaded();
    }

    private void OnRowRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-click does not select by itself; make the context menu act on the clicked row.
        ((ListViewItem)sender).IsSelected = true;
    }

    private void OnTreeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The Name column takes the width the fixed columns leave (room kept for the vertical scroll bar).
        if (!e.WidthChanged || TreeList.View is not GridView view)
            return;
        var others = view.Columns.Where(column => column != NameColumn).Sum(column => column.ActualWidth);
        NameColumn.Width = Math.Max(NameMinWidth, TreeList.ActualWidth - others - ThemeScrollBarWidth - 8);
    }

    private void OnTreeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A revealed search hit may be far down the list.
        if (TreeList.SelectedItem is { } row)
            TreeList.ScrollIntoView(row);
    }
}
```

- [ ] **Step 7: Verify**

Run: `dotnet build src/ReBackup.App --no-incremental -o "$env:TEMP\rebackup-app-build"`
Expected: Build succeeded, 0 warnings, 0 errors.

Run: `dotnet test tests/ReBackup.Core.Tests`
Expected: all 611 tests pass.

Manual checklist (controller/user):
1. Selecting a version shows its root folders and files (folders first, folder glyph in Warning, file glyph muted); expanding a folder shows "Loading…" briefly, then its entries with size and modified time. A not-yet-indexed version shows "Indexing…" and fills when its sync finishes.
2. "Compare with" an older version: every row gets a dot and a status (Added green, Changed amber, Deleted red, Unchanged faint); a folder with a changed file deep inside is Changed; entries only in the other version appear in italics with their size there. "Changed only" hides Unchanged rows and is disabled while nothing is compared ("—").
3. Swap shows the other version's tree; statuses keep their meaning (Added = new in the later version).
4. Search "report" (substring) and "*.cs" (wildcard) list hits by path with "N matches" ("The first 500 matches" when capped); clicking a hit returns to the tree with the entry selected and scrolled into view; clearing the search box returns to the tree.
5. Selecting a file shows "HISTORY OF <name>" with one row per version, newest first (New/Changed/Unchanged/Deleted, "—" when absent); the folder button opens Explorer with that version's copy selected.
6. The ignore preview's expand chevrons still work (style moved to Controls.xaml).
7. Dark and Light: status colors readable on Card in both themes.

- [ ] **Step 8: Commit**

```bash
git add src/ReBackup.App
git commit -m "feat(app): versions tree with compare, changed-only, swap, search and file history" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 6: Versions tab — Open, Show in Explorer, Restore (dialog, progress, result)

**Files:**
- Create: `src/ReBackup.App/ViewModels/VersionsViewModel.Restore.cs`, `src/ReBackup.App/ConflictDialog.xaml`, `src/ReBackup.App/ConflictDialog.xaml.cs`
- Modify: `src/ReBackup.App/Services/IDialogService.cs`, `src/ReBackup.App/Services/WpfDialogService.cs`, `src/ReBackup.App/Views/VersionsView.xaml`, `src/ReBackup.App/MainWindow.xaml`, `src/ReBackup.App/Theme/Icons.xaml`

**Interfaces:**
- Consumes: Task 3 `Restorer.Plan/Run`, `RestoreMode`, `ConflictPolicy`, `RestorePlan`, `RestoreResult`, `RestoreProgress`; Task 1 `IndexOrigin`; Task 4 `VersionsViewModel` part 1 (`_savedPlan`, `_isBackupActive`, `_files`, `_context`, `SelectedVersion`), `VersionRowViewModel` (`Info.Path`, `Indexed`, `DateText`); Task 5 part 2 (`Tree.SelectedNode`, `SelectedCompare`, hook `OnTreeSelectionChanged`), `VersionTreeNode` (`IsMessage`, `IsDirectory`, `OnlyInOther`, `Path`), `IFolderOpener.OpenFile/ShowInExplorer`; `DarkTitleBar.Apply`; styles `Button.Accent`, `Button.Icon`.
- Produces:
  - `IDialogService.AskConflictPolicy(string title, string message)` → `ConflictPolicy?` (null = Cancel); `WpfDialogService` shows `ConflictDialog` (owner = the active window)
  - `ConflictDialog(string title, string message)` with `ConflictPolicy Choice` (valid when `ShowDialog()` returned true)
  - `VersionsViewModel` part 3: `OpenSelectedCommand`, `ShowSelectedInExplorerCommand`, `RestoreToOriginalCommand`, `RestoreToCommand`, `CancelRestoreCommand`, `IsRestoring`, `RestoreText`, `RestoreFraction`
  - Resources: `Icon.OpenFile` (`E8E5`), `Icon.Explorer` (`EC50`), `Icon.Restore` (`E777`)

Restore flow (spec §3, §4; rulings 4, 6, 7, 10): Original → destination root = the indexed manifest's `source` (scans: the plan's saved source), confirmation with the running-backup warning when `isBackupActive()`; To… → folder picker. `Restorer.Plan` runs off the UI thread (an `ArgumentException`/IO error is shown and nothing happens). Conflicts → the dialog once; Cancel stops before anything is written. `Restorer.Run` runs in `Task.Run` with a `Progress<RestoreProgress>` created on the UI thread; the footer gets the start and the result ("Restore finished: 12 copied, 3 skipped."), failures (first 20) appear in an error dialog. Commands are disabled while a restore runs; Cancel stops before the next file.

- [ ] **Step 1: Icons**

In `src/ReBackup.App/Theme/Icons.xaml`, after `    <sys:String x:Key="Icon.Folder">&#xE8B7;</sys:String>` add:

```xml
    <sys:String x:Key="Icon.OpenFile">&#xE8E5;</sys:String>
    <sys:String x:Key="Icon.Explorer">&#xEC50;</sys:String>
    <sys:String x:Key="Icon.Restore">&#xE777;</sys:String>
```

- [ ] **Step 2: The conflict question**

In `src/ReBackup.App/Services/IDialogService.cs` add `using ReBackup.Core.Versions;` (with a blank line) above `namespace ReBackup.App.Services;`, and after `    void ShowInfo(string title, string message);` add:

```csharp

    /// <summary>Overwrite / Skip / Keep both; null when the user cancels.</summary>
    ConflictPolicy? AskConflictPolicy(string title, string message);
```

In `src/ReBackup.App/Services/WpfDialogService.cs` add `using ReBackup.Core.Versions;` after `using Microsoft.Win32;`, and directly above `    private static MessageBoxResult Show(` add:

```csharp
    public ConflictPolicy? AskConflictPolicy(string title, string message)
    {
        var dialog = new ConflictDialog(title, message);
        if (Owner is { } owner)
            dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.Choice : null;
    }

```

Create `src/ReBackup.App/ConflictDialog.xaml`:

```xml
<Window x:Class="ReBackup.App.ConflictDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Width="480" SizeToContent="Height" ResizeMode="NoResize"
        WindowStartupLocation="CenterOwner" ShowInTaskbar="False"
        Style="{StaticResource {x:Type Window}}">
    <!-- Asked once per restore: what happens to files that exist at the destination. -->
    <StackPanel Margin="20,18">
        <TextBlock x:Name="MessageText" TextWrapping="Wrap" FontSize="13" />
        <TextBlock Style="{StaticResource TextBlock.Muted}" Margin="0,10,0,0" FontSize="12" TextWrapping="Wrap"
                   Text="Keep both writes the version's copy next to the existing file, with the version's time in its name. Nothing is ever deleted." />
        <StackPanel Margin="0,18,0,0" Orientation="Horizontal" HorizontalAlignment="Right">
            <Button Content="Overwrite" MinWidth="96" Click="OnOverwrite" AutomationProperties.Name="Overwrite" />
            <Button Content="Skip" MinWidth="80" Margin="8,0,0,0" Click="OnSkip" AutomationProperties.Name="Skip" />
            <Button Content="Keep both" MinWidth="96" Margin="8,0,0,0" Style="{StaticResource Button.Accent}" IsDefault="True"
                    Click="OnKeepBoth" AutomationProperties.Name="Keep both" />
            <Button Content="Cancel" MinWidth="80" Margin="8,0,0,0" IsCancel="True" AutomationProperties.Name="Cancel" />
        </StackPanel>
    </StackPanel>
</Window>
```

Create `src/ReBackup.App/ConflictDialog.xaml.cs`:

```csharp
using System.Windows;
using ReBackup.App.Services;
using ReBackup.Core.Versions;

namespace ReBackup.App;

/// <summary>Overwrite / Skip / Keep both / Cancel for the files a restore finds at its destination.</summary>
public partial class ConflictDialog : Window
{
    public ConflictDialog(string title, string message)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Title = title;
        MessageText.Text = message;
    }

    /// <summary>The answer; only meaningful when <see cref="Window.ShowDialog"/> returned true.</summary>
    public ConflictPolicy Choice { get; private set; }

    private void OnOverwrite(object sender, RoutedEventArgs e) => Close(ConflictPolicy.Overwrite);

    private void OnSkip(object sender, RoutedEventArgs e) => Close(ConflictPolicy.Skip);

    private void OnKeepBoth(object sender, RoutedEventArgs e) => Close(ConflictPolicy.KeepBoth);

    private void Close(ConflictPolicy choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}
```

- [ ] **Step 3: The view model's actions and restore**

Create `src/ReBackup.App/ViewModels/VersionsViewModel.Restore.cs`:

```csharp
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Versions;

namespace ReBackup.App.ViewModels;

/// <summary>The Versions tab: Open, Show in Explorer and the two restores of the selected entry.</summary>
public sealed partial class VersionsViewModel
{
    private const int FailuresShown = 20;
    private CancellationTokenSource? _restoreCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreToOriginalCommand), nameof(RestoreToCommand), nameof(CancelRestoreCommand))]
    private bool _isRestoring;

    /// <summary>"Restoring 120 of 300 files · 34 %" while a restore runs (Versions tab, plan card, footer).</summary>
    [ObservableProperty] private string _restoreText = "";

    [ObservableProperty] private double _restoreFraction;

    partial void OnTreeSelectionChanged()
    {
        OpenSelectedCommand.NotifyCanExecuteChanged();
        ShowSelectedInExplorerCommand.NotifyCanExecuteChanged();
        RestoreToOriginalCommand.NotifyCanExecuteChanged();
        RestoreToCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The selected tree entry and the version that holds it (B for entries only in B).</summary>
    private (VersionTreeNode Node, VersionRowViewModel Version)? Selection =>
        Tree.SelectedNode is { IsMessage: false } node &&
        (node.OnlyInOther ? SelectedCompare?.Row : SelectedVersion) is { } version
            ? (node, version)
            : null;

    private bool CanOpenSelected() => Selection is { Node.IsDirectory: false };

    private bool CanActOnSelected() => Selection is not null;

    private bool CanRestore() => Selection is not null && !IsRestoring;

    /// <summary>Opens the copy of the selected file in the version folder with its program (meant read-only).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    private async Task OpenSelectedAsync()
    {
        if (Selection is not { } selection)
            return;
        var (folder, path) = (selection.Version.Info.Path, selection.Node.Path);
        if (!await Task.Run(() => _files.OpenFile(folder, path)))
            _context.ReportStatus($"\"{path}\" cannot be opened: it no longer exists in the version.");
    }

    /// <summary>Shows the selected entry in Explorer, selected in its folder.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private async Task ShowSelectedInExplorerAsync()
    {
        if (Selection is not { } selection)
            return;
        var (folder, path) = (selection.Version.Info.Path, selection.Node.Path);
        if (!await Task.Run(() => _files.ShowInExplorer(folder, path)))
            _context.ReportStatus($"\"{path}\" no longer exists in the version.");
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreToOriginalAsync() => RestoreAsync(RestoreMode.Original);

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreToAsync() => RestoreAsync(RestoreMode.ToFolder);

    [RelayCommand(CanExecute = nameof(IsRestoring))]
    private void CancelRestore() => _restoreCts?.Cancel();

    /// <summary>
    /// Plan → confirm (original location) or pick a folder → ask once about existing files → copy in the background.
    /// Nothing at the destination is ever deleted.
    /// </summary>
    private async Task RestoreAsync(RestoreMode mode)
    {
        if (IsRestoring || Selection is not { } selection)
            return;
        var (node, version) = selection;
        var dialogs = _context.Dialogs;
        var itemText = node.Path.Length == 0 ? "the whole version" : $"\"{node.Path}\"";

        string root;
        if (mode == RestoreMode.Original)
        {
            var source = version.Indexed is { Origin: IndexOrigin.Manifest, Source.Length: > 0 } indexed
                ? indexed.Source
                : _savedPlan()?.Source;
            if (string.IsNullOrWhiteSpace(source))
            {
                dialogs.ShowError("Restore", "The original location of this version is not known. Use \"Restore to…\".");
                return;
            }
            root = source;
            var warning = _isBackupActive()
                ? "\n\nA backup of this plan is queued or running; it may pick up the restored files."
                : "";
            if (!dialogs.Confirm("Restore to the original location",
                    $"Restore {itemText} from the version of {version.DateText} to\n{Path.Combine(root, node.Path.Replace('/', '\\'))}?\n\n" +
                    $"Nothing there is deleted; you choose what happens to files that exist already.{warning}"))
                return;
        }
        else
        {
            if (dialogs.PickFolder("Restore to…", null) is not { } folder)
                return;
            root = folder;
        }

        RestorePlan plan;
        try
        {
            var versionFolder = version.Info.Path;
            var relative = node.Path;
            plan = await Task.Run(() => Restorer.Plan(versionFolder, [relative], root, mode));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            dialogs.ShowError("Restore", ex.Message);
            return;
        }

        var policy = ConflictPolicy.Overwrite;
        if (plan.Conflicts.Count > 0)
        {
            var count = plan.Conflicts.Count.ToString("N0", CultureInfo.CurrentCulture);
            if (dialogs.AskConflictPolicy("Restore", $"{count} file(s) already exist at the destination.\n\nWhat should happen to them?")
                is not { } answer)
                return;
            policy = answer;
        }

        var cts = _restoreCts = new CancellationTokenSource();
        IsRestoring = true;
        RestoreFraction = 0;
        RestoreText = "Restoring…";
        var progress = new Progress<RestoreProgress>(p =>
        {
            if (!IsRestoring || !ReferenceEquals(_restoreCts, cts))
                return;
            RestoreFraction = p.Fraction;
            RestoreText = string.Create(CultureInfo.CurrentCulture,
                $"Restoring {p.FilesDone:N0} of {p.FilesTotal:N0} files · {p.Fraction * 100:0} %");
        });
        _context.ReportStatus($"Restoring {itemText} from {version.DateText}…");
        try
        {
            var result = await Task.Run(() => Restorer.Run(plan, policy, progress, cts.Token));
            _context.ReportStatus(Summary(result));
            if (result.Failures.Count > 0)
            {
                var lines = result.Failures.Take(FailuresShown).Select(f => $"{f.Path}: {f.Reason}");
                var more = result.Failures.Count > FailuresShown ? $"\n… and {result.Failures.Count - FailuresShown:N0} more" : "";
                dialogs.ShowError("Restore", $"{result.Failures.Count:N0} file(s) could not be restored:\n\n" +
                                             string.Join("\n", lines) + more);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.ReportStatus($"The restore stopped: {ex.Message}");
        }
        finally
        {
            IsRestoring = false;
            RestoreText = "";
            _restoreCts = null;
            cts.Dispose();
        }
    }

    private static string Summary(RestoreResult result)
    {
        var parts = new List<string> { $"{result.Copied:N0} copied" };
        if (result.KeptBoth > 0)
            parts.Add($"{result.KeptBoth:N0} kept beside the existing file");
        if (result.Skipped > 0)
            parts.Add($"{result.Skipped:N0} skipped");
        if (result.Failures.Count > 0)
            parts.Add($"{result.Failures.Count:N0} failed");
        return (result.Canceled ? "Restore canceled: " : "Restore finished: ") + string.Join(", ", parts) + ".";
    }
}
```

- [ ] **Step 4: Actions in the view**

In `src/ReBackup.App/Views/VersionsView.xaml`:

1. At the end of `UserControl.Resources` (after the `Ellipse.History` style, before `    </UserControl.Resources>`) add, preceded by a blank line:

```xml
        <!-- The actions on the selected entry: toolbar buttons and the tree's context menu use the same commands. -->
        <ContextMenu x:Key="Menu.Entry">
            <MenuItem Header="Open" Command="{Binding Versions.OpenSelectedCommand}" />
            <MenuItem Header="Show in Explorer" Command="{Binding Versions.ShowSelectedInExplorerCommand}" />
            <Separator />
            <MenuItem Header="Restore to original location…" Command="{Binding Versions.RestoreToOriginalCommand}" />
            <MenuItem Header="Restore to…" Command="{Binding Versions.RestoreToCommand}" />
        </ContextMenu>
```

2. On the tree `ListView` (`x:Name="TreeList"`), add the attribute line directly after `SizeChanged="OnTreeSizeChanged"`:

```xml
                                  ContextMenu="{StaticResource Menu.Entry}"
```

3. In the toolbar `WrapPanel`, after the "Changed only" `CheckBox` (before `</WrapPanel>`) add:

```xml
                        <StackPanel Orientation="Horizontal" Margin="0,0,0,6">
                            <Button Style="{StaticResource Button.Icon}" Content="{StaticResource Icon.OpenFile}"
                                    ToolTip="Open the file (the copy in the version)" AutomationProperties.Name="Open"
                                    Command="{Binding Versions.OpenSelectedCommand}" />
                            <Button Style="{StaticResource Button.Icon}" Content="{StaticResource Icon.Explorer}"
                                    ToolTip="Show in Explorer" AutomationProperties.Name="Show in Explorer"
                                    Command="{Binding Versions.ShowSelectedInExplorerCommand}" />
                            <Button Margin="6,0,0,0" AutomationProperties.Name="Restore to original location"
                                    ToolTip="Copy the selection back to where it came from"
                                    Command="{Binding Versions.RestoreToOriginalCommand}">
                                <StackPanel Orientation="Horizontal">
                                    <TextBlock Style="{StaticResource TextBlock.Icon}" FontSize="13" Text="{StaticResource Icon.Restore}" />
                                    <TextBlock Margin="8,0,0,0" VerticalAlignment="Center" Text="Restore" />
                                </StackPanel>
                            </Button>
                            <Button Margin="6,0,0,0" Content="Restore to…" AutomationProperties.Name="Restore to a folder"
                                    ToolTip="Copy the selection into a folder you choose"
                                    Command="{Binding Versions.RestoreToCommand}" />
                        </StackPanel>
```

4. Directly after the toolbar's `</WrapPanel>` (before the `SearchSummary` `TextBlock`) add, followed by a blank line:

```xml
                    <!-- A running restore: progress and cancel. -->
                    <DockPanel DockPanel.Dock="Top" Margin="0,0,0,8"
                               Visibility="{Binding Versions.IsRestoring, Converter={StaticResource BoolToVisibility}}">
                        <Button DockPanel.Dock="Right" Margin="8,0,0,0" Command="{Binding Versions.CancelRestoreCommand}"
                                AutomationProperties.Name="Cancel the restore">
                            <StackPanel Orientation="Horizontal">
                                <TextBlock Style="{StaticResource TextBlock.Icon}" FontSize="12" Text="{StaticResource Icon.Stop}" />
                                <TextBlock Margin="8,0,0,0" VerticalAlignment="Center" Text="Cancel" />
                            </StackPanel>
                        </Button>
                        <StackPanel VerticalAlignment="Center">
                            <TextBlock FontSize="12" Foreground="{DynamicResource Brush.AccentLight}" Text="{Binding Versions.RestoreText}" />
                            <ProgressBar Margin="0,4,0,0" Height="6" Maximum="1" Value="{Binding Versions.RestoreFraction, Mode=OneWay}" />
                        </StackPanel>
                    </DockPanel>
```

- [ ] **Step 5: The restore on the plan card**

In `src/ReBackup.App/MainWindow.xaml`, in the plan card template, after the detail `TextBlock` (`Text="{Binding Run.DetailText}" ToolTip="{Binding Run.DetailText}" />`) and before the `</StackPanel>` that closes the name/phase/detail column, add:

```xml
                                    <!-- A restore from the Versions tab runs beside the backups. -->
                                    <TextBlock Margin="0,3,0,0" FontSize="12" Foreground="{DynamicResource Brush.AccentLight}"
                                               TextTrimming="CharacterEllipsis" Text="{Binding Versions.RestoreText}"
                                               Visibility="{Binding Versions.IsRestoring, Converter={StaticResource BoolToVisibility}}" />
```

- [ ] **Step 6: Verify**

Run: `dotnet build src/ReBackup.App --no-incremental -o "$env:TEMP\rebackup-app-build"`
Expected: Build succeeded, 0 warnings, 0 errors.

Run: `dotnet test tests/ReBackup.Core.Tests`
Expected: all 611 tests pass.

Manual checklist (controller/user; use a scratch source folder, not real data):
1. With a file selected: Open starts its program on the copy inside the version folder; Show in Explorer opens Explorer with it selected; for a folder, Open is disabled and Show in Explorer works. Message rows ("Loading…") enable nothing.
2. Restore (original) asks "Restore … to <path>?"; while a backup of the plan is queued or running the question adds the warning about the running backup.
3. Restoring a folder whose files partly exist at the destination asks once "N file(s) already exist…": Overwrite replaces them (modified time = the version's), Skip leaves them, Keep both writes `name (yyyy_MM_dd-HH_mm).ext` (then ` 2`, ` 3`), Cancel writes nothing. Files at the destination that the version does not have are never deleted; no `*.rebackup-tmp` files remain.
4. Restore to… asks for a folder and writes the selected item under its own name (a folder with its structure).
5. During a long restore: the tab shows the progress line with a bar and Cancel; the plan card shows "Restoring … %"; the footer shows "Restoring … from …"; the restore buttons are disabled. Cancel stops after the current file; the footer then says "Restore canceled: …".
6. A read-only or open-locked destination file → the restore finishes with the others and lists the failures in an error dialog.
7. An entry that exists only in the compared version (italic) restores from that version.
8. Right-click on a tree row selects it and shows Open / Show in Explorer / Restore to original location… / Restore to….
9. Conflict dialog: dark title bar, readable in Dark and Light, Esc = Cancel, Enter = Keep both.

- [ ] **Step 7: Commit**

```bash
git add src/ReBackup.App
git commit -m "feat(app): open, show in Explorer and restore from the Versions tab, with conflict dialog and progress" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

## Not part of this plan

- Deleting versions from the Versions tab (spec: never; retention does it).
- Multi-selection in the tree (restore works on one selected entry; "" for the whole version is supported by the Core but has no button).
- A restore of the whole version from the list's context menu; previews of file contents; comparing file contents beyond hash/size/time.

## Self-review notes

- Spec §2.1 schema → Task 1 (with ruling 1); §2.2 sync, stamps, transactions, scan fallback, progress → Task 1; runner hand-over and warning → Task 4; §2.3 queries → Task 2; §3 Restorer (modes, policies, temp file + rename, keep-both naming, never deletes, failures, path/link validation, cancel, progress by bytes, mtime) → Task 3; §4 tab (list with not-managed rows and index states, progress line, Refresh; toolbar search / compare / swap / changed only; status column and colors; deleted entries in the tree; search → reveal; history card with folder icon; actions incl. context menu; restore dialog, background run, footer and plan card progress, failures dialog; backup-running warning; works before a version is indexed) → Tasks 4–6; §5 tests → Tasks 1–4.
- Names used across tasks: `VersionIndex.ProgressEvery`, `SearchLimit`, `IndexSyncProgress.Finished`, `IndexedVersion.Origin/Source`, `VersionsContext.ReportStatus`, `VersionsViewModel` hooks `OnCreated`/`OnIndexReady`/`OnTreeSelectionChanged`, `IFolderOpener.OpenFile/ShowInExplorer`, `IDialogService.AskConflictPolicy` — each defined once and consumed with the same signature.
