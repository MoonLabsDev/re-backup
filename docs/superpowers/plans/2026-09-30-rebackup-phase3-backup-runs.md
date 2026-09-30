# ReBackup Phase 3 — Backup Runs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** "Run now" for every plan: a full copy into `<target>/YYYY_MM_DD-hh_mm <PlanName>/` with a `re-manifest.json`, a progress bar and Cancel, one queue for all plans, a run log with duration and abort reason, and a History tab. The app also becomes single-instance.

**Architecture:** `ReBackup.Core.Backup` holds the version naming, the manifest and run-log models, `BackupRunner` (index → evaluate → preflight → copy with xxHash64 → manifest → rename) and `BackupQueue` (one worker, one job per plan). The runner reuses Phase 2's `SourceIndexer`, `IgnoreMatcher` and `IndexEvaluator`. The WPF layer adds run state per plan (`PlanRunViewModel`), Run/Cancel in the plan list, a History tab, and tray integration. Retention (Phase 4) and the scheduler (Phase 5) are not part of this plan: the runner never deletes old versions and `retentionDeleted` stays empty.

**Tech Stack:** .NET 9, WPF, CommunityToolkit.Mvvm 8.4.2, H.NotifyIcon.Wpf 2.3.2, System.IO.Hashing (xxHash64), xUnit, FluentAssertions 7.0.0, Microsoft.Extensions.TimeProvider.Testing.

**Spec:** `docs/superpowers/specs/2026-09-30-rebackup-design.md` (this plan covers §4.3, §4.4, §4.5, §5 `BackupRunner`/`BackupQueue`/`RunLog`, §9 without retention, §10.1 Run now / progress / Cancel / status strip, §10.2 tab 5 History, §10.3 tooltip progress, notification, "Run plan ▸", exit during a run, and build phase 3 of §13)

## Global Constraints

- Target frameworks: `net9.0` for Core and Tests, `net9.0-windows` for App. Nullable and implicit usings are on. Builds must stay at 0 warnings.
- Version folder name: `YYYY_MM_DD-hh_mm <PlanName>` in local time, 24-hour clock (e.g. `2026_09_30-14_05 Projects`). While a run is in progress the folder has the suffix `.partial`. The manifest file is `re-manifest.json` in the version folder.
- A folder counts as a version of a plan only if its name is exactly that timestamp, one space, and the plan name (case-insensitive). Nothing else in the target is ever touched.
- Manifest hashes are xxHash64, written as `xxh64:` followed by 16 lowercase hex digits.
- Run statuses: `Completed`, `CompletedWithWarnings`, `Full`, `Error`, `Canceled`. Run triggers: `Manual`, `Scheduled`, `CatchUp`.
- Preflight: the backup needs the included bytes plus 5 %. If that is more than the free space on the target, the run aborts as `Full` before writing anything.
- On every abort the `.partial` folder is deleted (best effort). Leftover `.partial` folders of the same plan are removed at the start of its next run.
- Locked files and files that vanished are skipped and recorded; the run then ends as `CompletedWithWarnings`. Folders the index could not scan (links, nesting deeper than 256, unreadable) are recorded as skipped too.
- One run at a time across all plans; a plan can be queued or running only once.
- One JSON object per line in `logs/<plan-id>.jsonl`, written with `JsonDefaults.Compact`. All other JSON uses `JsonDefaults.Options`. Config writes go through `AtomicFile.WriteAllText`; appending a log line and writing inside the target folder are exempt.
- Time comes from an injected `TimeProvider` in Core.
- FluentAssertions stays pinned to 7.0.0.
- Every commit message ends with a `Co-Authored-By: Claude <model> <noreply@anthropic.com>` trailer naming the authoring model, separated from the subject by a blank line.
- The App has no automated tests. App tasks are verified by build, the Core suite, a startup smoke test, and the manual checklist in the task.

## File Structure

```
src/ReBackup.Core/
  Json/JsonDefaults.cs             (modify) add Compact
  Ignore/IgnorePattern.cs          (modify) negated class starting with '-'
  Indexing/SourceIndexer.cs        (modify) SourceIndex.UnreadableIgnoreFiles
  Backup/VersionName.cs            folder naming and parsing
  Backup/BackupManifest.cs         re-manifest.json model
  Backup/RunLog.cs                 RunLogEntry, RunStatus, RunTrigger, SkippedEntry, RunLog
  Backup/TargetVolume.cs           ITargetVolume, PhysicalTargetVolume
  Backup/BackupRunner.cs           IBackupRunner, BackupRunner, BackupRequest, BackupProgress
  Backup/BackupQueue.cs            BackupQueue, BackupJobUpdate, JobState
src/ReBackup.App/
  Services/SingleInstance.cs
  ViewModels/PlanRunViewModel.cs   run state + history of one plan
  ViewModels/RunHistoryRow.cs      one row of the History tab
  ViewModels/PlanEditorViewModel.cs (modify) Run, SavedPlan()
  ViewModels/MainViewModel.cs      (modify) queue wiring, RunNow/CancelRun
  ViewModels/PreviewRowViewModel.cs (modify) tooltip text
  Views/HistoryView.xaml(.cs)
  MainWindow.xaml                  (modify) list row, status strip, History tab
  App.xaml.cs                      (modify) single instance, queue, tray
tests/ReBackup.Core.Tests/
  Backup/VersionNameTests.cs
  Backup/RunLogTests.cs
  Backup/BackupRunnerTests.cs
  Backup/BackupQueueTests.cs
  Ignore/IgnorePatternTests.cs     (modify)
  Indexing/SourceIndexerTests.cs   (modify)
```

---

### Task 1: Carry-forward fixes from Phase 2

**Files:**
- Modify: `src/ReBackup.Core/Ignore/IgnorePattern.cs` (`AppendCharacterClass`), `src/ReBackup.Core/Indexing/SourceIndexer.cs`, `src/ReBackup.App/ViewModels/PreviewRowViewModel.cs` (`StatusDetail`)
- Test: `tests/ReBackup.Core.Tests/Ignore/IgnorePatternTests.cs`, `tests/ReBackup.Core.Tests/Indexing/SourceIndexerTests.cs`

**Interfaces:**
- Produces: `SourceIndex.UnreadableIgnoreFiles` (`IReadOnlyList<string>`, relative paths of `.backupignore` files that could not be read; an init-only property with default `[]`, so existing `new SourceIndex(...)` calls keep compiling).

- [ ] **Step 1: Write the failing tests**

In `tests/ReBackup.Core.Tests/Ignore/IgnorePatternTests.cs`, add these rows to the `IsMatch_follows_gitignore_rules` theory (after the existing `a[!x]b` rows):

```csharp
    [InlineData("a[!-x]b", "a-b", false, false)]
    [InlineData("a[!-x]b", "axb", false, false)]
    [InlineData("a[!-x]b", "a0b", false, true)]
    [InlineData("a[!-x]b", "a/b", false, false)]
    [InlineData("a[!x-]b", "a0b", false, true)]
    [InlineData("a[!x-]b", "a-b", false, false)]
```

In `tests/ReBackup.Core.Tests/Indexing/SourceIndexerTests.cs`, add:

```csharp
    [Fact]
    public void Unreadable_ignore_file_is_reported()
    {
        var source = CreateSource();
        var ignoreFile = _tmp.WriteFile(@"src\sub\.backupignore", "*.tmp");
        using var locked = new FileStream(ignoreFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var index = SourceIndexer.Build(source);

        index.UnreadableIgnoreFiles.Should().Equal("sub/.backupignore");
        index.IgnoreFiles.Should().BeEmpty();
    }

    [Fact]
    public void Readable_ignore_files_are_not_reported_as_unreadable()
    {
        var source = CreateSource();
        _tmp.WriteFile(@"src\.backupignore", "*.tmp");

        SourceIndexer.Build(source).UnreadableIgnoreFiles.Should().BeEmpty();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~IgnorePatternTests|FullyQualifiedName~SourceIndexerTests"`
Expected: build error `'SourceIndex' does not contain a definition for 'UnreadableIgnoreFiles'`. (After Step 4 alone, the `a[!-x]b` row for `a0b` fails: `[^/-x]` is read as a range from `/` to `x`.)

- [ ] **Step 3: Fix the negated character class**

In `src/ReBackup.Core/Ignore/IgnorePattern.cs`, `AppendCharacterClass`: keep the slash out of the class itself, so it can never form a range with the class content, and exclude it with a lookahead instead. Replace

```csharp
        sb.Append('[');
        if (negate)
            sb.Append("^/");   // a class never matches a slash
```

with

```csharp
        if (negate)
            sb.Append("(?!/)");   // a class never matches a slash
        sb.Append('[');
        if (negate)
            sb.Append('^');
```

- [ ] **Step 4: Report unreadable ignore files**

In `src/ReBackup.Core/Indexing/SourceIndexer.cs`:

Change the `SourceIndex` record to:

```csharp
/// <summary>One scan of a source folder. <see cref="DirectoryCount"/> does not include the root.</summary>
public sealed record SourceIndex(
    string Root,
    IndexNode RootNode,
    IReadOnlyList<NestedIgnoreFile> IgnoreFiles,
    int FileCount,
    int DirectoryCount)
{
    /// <summary>Relative paths of <c>.backupignore</c> files that could not be read; their patterns are not applied.</summary>
    public IReadOnlyList<string> UnreadableIgnoreFiles { get; init; } = [];
}
```

In `Build`, change the return statement to:

```csharp
        return new SourceIndex(fullRoot, rootNode, walk.IgnoreFiles, walk.Files, walk.Directories)
        {
            UnreadableIgnoreFiles = walk.UnreadableIgnoreFiles,
        };
```

In the `Walk` class add the property next to `IgnoreFiles`:

```csharp
        public List<string> UnreadableIgnoreFiles { get; } = [];
```

and change the `catch` block in `ReadIgnoreFile` to:

```csharp
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                UnreadableIgnoreFiles.Add(IgnoreOrigins.ForNestedFile(directoryRelativePath));
            }
```

- [ ] **Step 5: Fix the tooltip text**

In `src/ReBackup.App/ViewModels/PreviewRowViewModel.cs`, in `StatusDetail`, change `$"{detail}\nNot scanned:{Node.Node.Error}"` to `$"{detail}\nNot scanned: {Node.Node.Error}"`.

- [ ] **Step 6: Build and run all tests**

Run: `dotnet build` (0 warnings, 0 errors), then `dotnet test tests/ReBackup.Core.Tests`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "fix: Phase 2 carry-forward (negated class range, unreadable ignore files, tooltip)" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 2: Version names, manifest model and run log

**Files:**
- Create: `src/ReBackup.Core/Backup/VersionName.cs`, `src/ReBackup.Core/Backup/BackupManifest.cs`, `src/ReBackup.Core/Backup/RunLog.cs`
- Modify: `src/ReBackup.Core/Json/JsonDefaults.cs`
- Test: `tests/ReBackup.Core.Tests/Backup/VersionNameTests.cs`, `tests/ReBackup.Core.Tests/Backup/RunLogTests.cs`

**Interfaces:**
- Produces:
  - `JsonDefaults.Compact` (same as `Options`, not indented)
  - `VersionName.PartialSuffix` (`".partial"`), `VersionName.ManifestFileName` (`"re-manifest.json"`), `VersionName.Format(DateTime localTime, string planName)`, `VersionName.TryParse(string folderName, string planName, out DateTime localTime)`
  - `BackupManifest` (`FormatVersion` = 1, `PlanId`, `PlanName`, `CreatedUtc`, `Source`, `List<ManifestFile> Files`), `record ManifestFile(string Path, long Size, DateTime MtimeUtc, string Hash)`
  - `enum RunTrigger { Manual, Scheduled, CatchUp }`, `enum RunStatus { Completed, CompletedWithWarnings, Full, Error, Canceled }`, `record SkippedEntry(string Path, string Reason)`
  - `RunLogEntry` (`RunId`, `Trigger`, `StartUtc`, `EndUtc`, `DurationMs`, `Status`, `Reason`, `Version`, `FilesCopied`, `BytesCopied`, `SkippedCount`, `List<SkippedEntry> Skipped`, `List<string> RetentionDeleted`), `RunLogEntry.MaxSkippedEntries` (= 1000), `void AddSkipped(SkippedEntry entry)` (always counts; stores only the first 1000)
  - `RunLog(string logFile)` with `LogFile`, `void Append(RunLogEntry entry)`, `IReadOnlyList<RunLogEntry> ReadAll()` (oldest first; unreadable lines are skipped; a missing file gives an empty list)

- [ ] **Step 1: Write the failing tests**

`tests/ReBackup.Core.Tests/Backup/VersionNameTests.cs`:

```csharp
using FluentAssertions;
using ReBackup.Core.Backup;

namespace ReBackup.Core.Tests.Backup;

public class VersionNameTests
{
    [Fact]
    public void Format_uses_the_spec_pattern_with_a_24_hour_clock()
    {
        VersionName.Format(new DateTime(2026, 9, 30, 14, 5, 59), "Projects").Should().Be("2026_09_30-14_05 Projects");
        VersionName.Format(new DateTime(2026, 1, 2, 3, 4, 0), "My Plan").Should().Be("2026_01_02-03_04 My Plan");
    }

    [Fact]
    public void TryParse_round_trips_Format()
    {
        var name = VersionName.Format(new DateTime(2026, 9, 30, 14, 5, 59), "Projects");

        VersionName.TryParse(name, "Projects", out var time).Should().BeTrue();
        time.Should().Be(new DateTime(2026, 9, 30, 14, 5, 0));
    }

    [Fact]
    public void TryParse_ignores_the_case_of_the_plan_name()
    {
        VersionName.TryParse("2026_09_30-14_05 PROJECTS", "Projects", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("2026_09_30-14_05 Other")]
    [InlineData("2026_09_30-14_05 Projects.partial")]
    [InlineData("2026_09_30-14_05 Projects 2")]
    [InlineData("2026_09_30-14_05  Projects")]
    [InlineData("2026_09_30-14_05Projects")]
    [InlineData("2026_13_40-25_61 Projects")]
    [InlineData("2026-09-30 14:05 Projects")]
    [InlineData("Projects")]
    [InlineData("")]
    public void TryParse_rejects_everything_else(string folderName)
    {
        VersionName.TryParse(folderName, "Projects", out _).Should().BeFalse();
    }
}
```

`tests/ReBackup.Core.Tests/Backup/RunLogTests.cs`:

```csharp
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Backup;

public class RunLogTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static RunLogEntry Entry(string id, RunStatus status) => new()
    {
        RunId = id,
        Trigger = RunTrigger.Manual,
        StartUtc = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
        EndUtc = new DateTime(2026, 9, 30, 12, 1, 21, DateTimeKind.Utc),
        DurationMs = 81_000,
        Status = status,
        Version = "2026_09_30-14_00 Projects",
        FilesCopied = 3,
        BytesCopied = 4096,
    };

    [Fact]
    public void Missing_file_reads_as_empty()
    {
        new RunLog(_tmp.PathOf(@"logs\p1.jsonl")).ReadAll().Should().BeEmpty();
    }

    [Fact]
    public void Append_creates_the_folder_and_ReadAll_returns_entries_oldest_first()
    {
        var log = new RunLog(_tmp.PathOf(@"logs\p1.jsonl"));
        var first = Entry("a", RunStatus.Completed);
        var second = Entry("b", RunStatus.Full);
        second.Reason = "disk full";
        second.AddSkipped(new SkippedEntry("x/y.txt", "locked by another program"));
        second.RetentionDeleted.Add("2026_09_01-02_00 Projects");

        log.Append(first);
        log.Append(second);

        log.ReadAll().Should().BeEquivalentTo([first, second], o => o.WithStrictOrdering());
    }

    [Fact]
    public void Each_entry_is_one_compact_line_with_enums_as_strings()
    {
        var log = new RunLog(_tmp.PathOf("p1.jsonl"));

        log.Append(Entry("a", RunStatus.CompletedWithWarnings));
        log.Append(Entry("b", RunStatus.Canceled));

        var lines = File.ReadAllLines(log.LogFile);
        lines.Should().HaveCount(2);
        lines[0].Should().StartWith("{\"runId\":\"a\"").And.Contain("\"status\":\"CompletedWithWarnings\"")
            .And.Contain("\"trigger\":\"Manual\"").And.Contain("\"durationMs\":81000");
    }

    [Fact]
    public void Unreadable_lines_are_skipped()
    {
        var log = new RunLog(_tmp.PathOf("p1.jsonl"));
        log.Append(Entry("a", RunStatus.Completed));
        File.AppendAllText(log.LogFile, "{ not json\n\nnull\n");
        log.Append(Entry("b", RunStatus.Error));

        log.ReadAll().Select(e => e.RunId).Should().Equal("a", "b");
    }

    [Fact]
    public void AddSkipped_counts_everything_but_stores_only_the_first_thousand()
    {
        var entry = Entry("a", RunStatus.CompletedWithWarnings);

        for (var i = 0; i < RunLogEntry.MaxSkippedEntries + 5; i++)
            entry.AddSkipped(new SkippedEntry($"f{i}", "locked"));

        entry.SkippedCount.Should().Be(1005);
        entry.Skipped.Should().HaveCount(1000);
        entry.Skipped[^1].Path.Should().Be("f999");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~VersionNameTests|FullyQualifiedName~RunLogTests"`
Expected: build error `The type or namespace name 'Backup' does not exist in the namespace 'ReBackup.Core'`.

- [ ] **Step 3: Add `JsonDefaults.Compact`**

Replace the body of `src/ReBackup.Core/Json/JsonDefaults.cs`'s class with:

```csharp
public static class JsonDefaults
{
    /// <summary>Indented JSON for plan, settings and manifest files.</summary>
    public static JsonSerializerOptions Options { get; } = Create(indented: true);

    /// <summary>Single-line JSON for JSON Lines files (the run log).</summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            AllowOutOfOrderMetadataProperties = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
```

- [ ] **Step 4: Create `src/ReBackup.Core/Backup/VersionName.cs`**

```csharp
using System.Globalization;

namespace ReBackup.Core.Backup;

/// <summary>Names of version folders in a target: <c>YYYY_MM_DD-hh_mm PlanName</c>.</summary>
public static class VersionName
{
    public const string PartialSuffix = ".partial";
    public const string ManifestFileName = "re-manifest.json";
    private const string TimestampFormat = "yyyy_MM_dd-HH_mm";

    public static string Format(DateTime localTime, string planName) =>
        localTime.ToString(TimestampFormat, CultureInfo.InvariantCulture) + " " + planName;

    /// <summary>True when the folder name is exactly a timestamp, one space and the plan name.</summary>
    public static bool TryParse(string folderName, string planName, out DateTime localTime)
    {
        localTime = default;
        var stampLength = TimestampFormat.Length;
        if (folderName.Length != stampLength + 1 + planName.Length || folderName[stampLength] != ' ')
            return false;
        if (!folderName.AsSpan(stampLength + 1).Equals(planName, StringComparison.OrdinalIgnoreCase))
            return false;
        return DateTime.TryParseExact(folderName.AsSpan(0, stampLength), TimestampFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out localTime);
    }
}
```

- [ ] **Step 5: Create `src/ReBackup.Core/Backup/BackupManifest.cs`**

```csharp
namespace ReBackup.Core.Backup;

/// <summary>Content of <c>re-manifest.json</c> in a version folder.</summary>
public sealed class BackupManifest
{
    public int FormatVersion { get; set; } = 1;
    public string PlanId { get; set; } = "";
    public string PlanName { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string Source { get; set; } = "";
    public List<ManifestFile> Files { get; set; } = [];
}

/// <summary><paramref name="Path"/> is relative to the version folder, with forward slashes. <paramref name="Hash"/> is <c>xxh64:</c> plus 16 hex digits.</summary>
public sealed record ManifestFile(string Path, long Size, DateTime MtimeUtc, string Hash);
```

- [ ] **Step 6: Create `src/ReBackup.Core/Backup/RunLog.cs`**

```csharp
using System.Text.Json;
using ReBackup.Core.Json;

namespace ReBackup.Core.Backup;

public enum RunTrigger { Manual, Scheduled, CatchUp }

public enum RunStatus { Completed, CompletedWithWarnings, Full, Error, Canceled }

public sealed record SkippedEntry(string Path, string Reason);

/// <summary>One line of a plan's run log.</summary>
public sealed class RunLogEntry
{
    /// <summary>At most this many skipped entries are stored per run; <see cref="SkippedCount"/> counts all.</summary>
    public const int MaxSkippedEntries = 1000;

    public string RunId { get; set; } = "";
    public RunTrigger Trigger { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public long DurationMs { get; set; }
    public RunStatus Status { get; set; }

    /// <summary>Why the run ended as Full or Error.</summary>
    public string? Reason { get; set; }

    /// <summary>Name of the version folder; null when the run did not complete.</summary>
    public string? Version { get; set; }

    public int FilesCopied { get; set; }
    public long BytesCopied { get; set; }
    public int SkippedCount { get; set; }
    public List<SkippedEntry> Skipped { get; set; } = [];
    public List<string> RetentionDeleted { get; set; } = [];

    public void AddSkipped(SkippedEntry entry)
    {
        SkippedCount++;
        if (Skipped.Count < MaxSkippedEntries)
            Skipped.Add(entry);
    }
}

/// <summary>Append-only JSON Lines file with one entry per run of a plan.</summary>
public sealed class RunLog
{
    public RunLog(string logFile) => LogFile = logFile;

    public string LogFile { get; }

    public void Append(RunLogEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(LogFile))!);
        File.AppendAllText(LogFile, JsonSerializer.Serialize(entry, JsonDefaults.Compact) + "\n");
    }

    /// <summary>All readable entries, oldest first.</summary>
    public IReadOnlyList<RunLogEntry> ReadAll()
    {
        var entries = new List<RunLogEntry>();
        if (!File.Exists(LogFile))
            return entries;

        foreach (var line in File.ReadLines(LogFile))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<RunLogEntry>(line, JsonDefaults.Compact) is not { } entry)
                    continue;
                entry.Skipped ??= [];
                entry.RetentionDeleted ??= [];
                entries.Add(entry);
            }
            catch (JsonException)
            {
                // A damaged line must not hide the rest of the history.
            }
        }
        return entries;
    }
}
```

- [ ] **Step 7: Run all Core tests**

Run: `dotnet build` (0 warnings), then `dotnet test tests/ReBackup.Core.Tests`
Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(core): add version names, manifest model and run log" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 3: BackupRunner

**Files:**
- Create: `src/ReBackup.Core/Backup/TargetVolume.cs`, `src/ReBackup.Core/Backup/BackupRunner.cs`
- Modify: `src/ReBackup.Core/ReBackup.Core.csproj` (package `System.IO.Hashing`), `tests/ReBackup.Core.Tests/ReBackup.Core.Tests.csproj` (package `Microsoft.Extensions.TimeProvider.Testing`)
- Test: `tests/ReBackup.Core.Tests/Backup/BackupRunnerTests.cs`

**Interfaces:**
- Consumes: `SourceIndexer.Build`, `SourceIndex.UnreadableIgnoreFiles`, `IgnoreMatcher.ForPlan`, `IndexEvaluator.Evaluate`, `EvaluatedNode`, `IncludeStatus`, `IndexNode`, `VersionName`, `BackupManifest`, `ManifestFile`, `RunLogEntry`, `RunStatus`, `RunTrigger`, `SkippedEntry`, `ByteSize.Format`, `JsonDefaults.Options`, `BackupPlan`
- Produces:
  - `interface ITargetVolume { long GetAvailableFreeSpace(string directory); Stream CreateFile(string path); }`, `PhysicalTargetVolume`
  - `enum BackupPhase { Indexing, Copying, Finishing }`
  - `readonly record struct BackupProgress(BackupPhase Phase, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string CurrentFile)` with `double Fraction` (0..1)
  - `record BackupRequest(BackupPlan Plan, IReadOnlyList<string> GlobalIgnoreDefaults, RunTrigger Trigger)`
  - `interface IBackupRunner { Task<RunLogEntry> RunAsync(BackupRequest request, IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default); }`
  - `BackupRunner(ITargetVolume? volume = null, TimeProvider? timeProvider = null) : IBackupRunner`. `RunAsync` never throws: every outcome, including cancellation, is returned as a `RunLogEntry`. It does not write the run log (the queue does).

- [ ] **Step 1: Add the packages**

```bash
dotnet add src/ReBackup.Core package System.IO.Hashing
dotnet add tests/ReBackup.Core.Tests package Microsoft.Extensions.TimeProvider.Testing
dotnet build
```

Expected: 0 warnings. If the newest version of either package produces a restore warning for `net9.0`, pin the newest `9.0.x` version of that package instead (`--version 9.0.0` is known to exist for both) and say so in the report.

- [ ] **Step 2: Write the failing tests** `tests/ReBackup.Core.Tests/Backup/BackupRunnerTests.cs`

```csharp
using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Json;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Backup;

public class BackupRunnerTests : IDisposable
{
    private const string Minute = "2026_09_30-14_05";
    private readonly TempDir _tmp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.Zero));
    private readonly string _source;
    private readonly string _target;

    public BackupRunnerTests()
    {
        _source = _tmp.CreateDir("source");
        _target = _tmp.PathOf("target");
        _tmp.WriteFile(@"source\a.txt", "alpha");
        _tmp.WriteFile(@"source\sub\b.bin", "bravo-bravo");
        _tmp.CreateDir(@"source\empty");
    }

    public void Dispose() => _tmp.Dispose();

    private BackupPlan Plan(params string[] patterns) => new()
    {
        Id = "p1",
        Name = "Projects",
        Source = _source,
        Target = _target,
        Ignore = new IgnoreSettings { Patterns = [.. patterns] },
    };

    private BackupRunner Runner(ITargetVolume? volume = null) => new(volume ?? new PhysicalTargetVolume(), _time);

    private static BackupRequest Request(BackupPlan plan, params string[] globalDefaults) =>
        new(plan, globalDefaults, RunTrigger.Manual);

    private string VersionPath(string minute = Minute) => Path.Combine(_target, $"{minute} Projects");

    private string[] TargetEntries() =>
        Directory.Exists(_target) ? Directory.GetFileSystemEntries(_target).Select(Path.GetFileName).ToArray()! : [];

    [Fact]
    public async Task Copies_everything_into_a_named_version_folder()
    {
        var mtime = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(_source, "a.txt"), mtime);

        var entry = await Runner().RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Completed);
        entry.Reason.Should().BeNull();
        entry.Trigger.Should().Be(RunTrigger.Manual);
        entry.Version.Should().Be($"{Minute} Projects");
        entry.FilesCopied.Should().Be(2);
        entry.BytesCopied.Should().Be(16);
        entry.SkippedCount.Should().Be(0);
        entry.RunId.Should().HaveLength(32);
        entry.StartUtc.Should().Be(_time.GetUtcNow().UtcDateTime);

        TargetEntries().Should().Equal($"{Minute} Projects");
        File.ReadAllText(Path.Combine(VersionPath(), "a.txt")).Should().Be("alpha");
        File.ReadAllText(Path.Combine(VersionPath(), "sub", "b.bin")).Should().Be("bravo-bravo");
        Directory.Exists(Path.Combine(VersionPath(), "empty")).Should().BeTrue();
        File.GetLastWriteTimeUtc(Path.Combine(VersionPath(), "a.txt")).Should().Be(mtime);
    }

    [Fact]
    public async Task Writes_a_manifest_with_sizes_times_and_xxhash64()
    {
        await Runner().RunAsync(Request(Plan()));

        var json = File.ReadAllText(Path.Combine(VersionPath(), "re-manifest.json"));
        var manifest = JsonSerializer.Deserialize<BackupManifest>(json, JsonDefaults.Options)!;
        manifest.FormatVersion.Should().Be(1);
        manifest.PlanId.Should().Be("p1");
        manifest.PlanName.Should().Be("Projects");
        manifest.Source.Should().Be(_source);
        manifest.CreatedUtc.Should().Be(_time.GetUtcNow().UtcDateTime);
        manifest.Files.Select(f => f.Path).Should().BeEquivalentTo("a.txt", "sub/b.bin");

        var a = manifest.Files.Single(f => f.Path == "a.txt");
        a.Size.Should().Be(5);
        a.MtimeUtc.Should().Be(File.GetLastWriteTimeUtc(Path.Combine(_source, "a.txt")));
        a.Hash.Should().Be("xxh64:" + Convert.ToHexStringLower(XxHash64.Hash(Encoding.UTF8.GetBytes("alpha"))));
        json.Should().Contain("\"mtimeUtc\"").And.Contain("\"formatVersion\": 1");
    }

    [Fact]
    public async Task Applies_plan_global_and_nested_ignore_patterns()
    {
        _tmp.WriteFile(@"source\note.tmp", "x");
        _tmp.WriteFile(@"source\cache\big.dat", "x");
        _tmp.WriteFile(@"source\sub\.backupignore", "secret.txt");
        _tmp.WriteFile(@"source\sub\secret.txt", "x");

        var entry = await Runner().RunAsync(Request(Plan("*.tmp"), "cache/"));

        entry.Status.Should().Be(RunStatus.Completed);
        File.Exists(Path.Combine(VersionPath(), "note.tmp")).Should().BeFalse();
        Directory.Exists(Path.Combine(VersionPath(), "cache")).Should().BeFalse();
        File.Exists(Path.Combine(VersionPath(), "sub", "secret.txt")).Should().BeFalse();
        File.Exists(Path.Combine(VersionPath(), "sub", ".backupignore")).Should().BeTrue("ignore files are backed up");
        File.Exists(Path.Combine(VersionPath(), "a.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Locked_file_is_skipped_and_the_run_completes_with_warnings()
    {
        using var locked = new FileStream(Path.Combine(_source, "a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var entry = await Runner().RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings);
        entry.SkippedCount.Should().Be(1);
        entry.Skipped.Should().ContainSingle().Which.Should().Be(new SkippedEntry("a.txt", "locked by another program"));
        entry.FilesCopied.Should().Be(1);
        File.Exists(Path.Combine(VersionPath(), "a.txt")).Should().BeFalse();
        File.Exists(Path.Combine(VersionPath(), "sub", "b.bin")).Should().BeTrue();
    }

    [Fact]
    public async Task Folder_that_was_not_scanned_is_recorded_as_skipped()
    {
        var link = Path.Combine(_source, "link");
        RunMklink(link, Path.Combine(_source, "sub"));
        try
        {
            var entry = await Runner().RunAsync(Request(Plan()));

            entry.Status.Should().Be(RunStatus.CompletedWithWarnings);
            entry.Skipped.Should().ContainSingle().Which.Should().Be(new SkippedEntry("link", "Link is not followed."));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task Source_file_named_like_the_manifest_is_skipped()
    {
        _tmp.WriteFile(@"source\re-manifest.json", "mine");

        var entry = await Runner().RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings);
        entry.Skipped.Should().ContainSingle().Which.Path.Should().Be("re-manifest.json");
        File.ReadAllText(Path.Combine(VersionPath(), "re-manifest.json")).Should().Contain("\"planId\"");
    }

    [Fact]
    public async Task Cancellation_before_the_start_yields_Canceled_and_writes_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var entry = await Runner().RunAsync(Request(Plan()), cancellationToken: cts.Token);

        entry.Status.Should().Be(RunStatus.Canceled);
        entry.Version.Should().BeNull();
        TargetEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_during_the_copy_removes_the_partial_folder()
    {
        using var cts = new CancellationTokenSource();
        var volume = new FakeVolume { OnCreateFile = _ => cts.Cancel() };

        var entry = await Runner(volume).RunAsync(Request(Plan()), cancellationToken: cts.Token);

        entry.Status.Should().Be(RunStatus.Canceled);
        entry.Version.Should().BeNull();
        TargetEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Too_little_free_space_aborts_as_Full_before_writing()
    {
        var volume = new FakeVolume { FreeSpace = 16 };   // 16 bytes needed + 5 % does not fit

        var entry = await Runner(volume).RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Full);
        entry.Reason.Should().Contain("is free on the target");
        volume.FilesCreated.Should().Be(0);
        TargetEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Just_enough_free_space_passes_the_preflight()
    {
        var entry = await Runner(new FakeVolume { FreeSpace = 17 }).RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Completed);
    }

    [Fact]
    public async Task Disk_full_during_the_copy_aborts_as_Full_and_removes_the_partial_folder()
    {
        var volume = new FakeVolume { FailWritesWithDiskFull = true };

        var entry = await Runner(volume).RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Full);
        entry.Reason.Should().NotBeNullOrEmpty();
        TargetEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_source_aborts_as_Error()
    {
        var plan = Plan();
        plan.Source = _tmp.PathOf("nope");

        var entry = await Runner().RunAsync(Request(plan));

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Contain("does not exist");
        Directory.Exists(_target).Should().BeFalse();
    }

    [Fact]
    public async Task Unexpected_write_error_aborts_as_Error_and_removes_the_partial_folder()
    {
        var volume = new FakeVolume { OnCreateFile = _ => throw new IOException("device not ready") };

        var entry = await Runner(volume).RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Be("device not ready");
        TargetEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Leftover_partial_folders_of_the_plan_are_removed_and_other_folders_kept()
    {
        _tmp.WriteFile(@"target\2026_09_29-10_00 Projects.partial\x.txt", "stale");
        _tmp.WriteFile(@"target\2026_09_29-10_00 Other.partial\x.txt", "foreign");
        _tmp.WriteFile(@"target\notes\x.txt", "foreign");
        _tmp.WriteFile(@"target\2026_09_28-09_00 Projects\a.txt", "older version");

        var entry = await Runner().RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Completed);
        TargetEntries().Should().BeEquivalentTo(
            "2026_09_28-09_00 Projects", "2026_09_29-10_00 Other.partial", "notes", $"{Minute} Projects");
    }

    [Fact]
    public async Task Waits_for_the_next_minute_when_the_version_folder_already_exists()
    {
        Directory.CreateDirectory(VersionPath());

        var run = Runner().RunAsync(Request(Plan()));
        var waited = Stopwatch.StartNew();
        while (!run.IsCompleted && waited.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(25);
            _time.Advance(TimeSpan.FromSeconds(10));
        }

        run.IsCompleted.Should().BeTrue();
        var entry = await run;
        entry.Status.Should().Be(RunStatus.Completed);
        entry.Version.Should().NotBe($"{Minute} Projects", "that folder already existed");
        VersionName.TryParse(entry.Version!, "Projects", out var versionTime).Should().BeTrue();
        versionTime.Should().BeAfter(new DateTime(2026, 9, 30, 14, 5, 0));
        File.Exists(Path.Combine(_target, entry.Version!, "a.txt")).Should().BeTrue();
        Directory.GetFileSystemEntries(VersionPath()).Should().BeEmpty("the existing folder is not touched");
    }

    [Fact]
    public async Task Reports_progress_up_to_the_end()
    {
        var reports = new List<BackupProgress>();

        await Runner().RunAsync(Request(Plan()), new SyncProgress(reports.Add));

        reports.Should().Contain(p => p.Phase == BackupPhase.Copying);
        var last = reports[^1];
        last.Phase.Should().Be(BackupPhase.Finishing);
        last.FilesDone.Should().Be(2);
        last.FilesTotal.Should().Be(2);
        last.BytesDone.Should().Be(16);
        last.BytesTotal.Should().Be(16);
        last.Fraction.Should().Be(1);
    }

    [Fact]
    public async Task Duration_is_measured_with_the_time_provider()
    {
        var volume = new FakeVolume { OnCreateFile = _ => _time.Advance(TimeSpan.FromSeconds(2)) };

        var entry = await Runner(volume).RunAsync(Request(Plan()));

        entry.DurationMs.Should().Be(4000);
        entry.EndUtc.Should().Be(entry.StartUtc.AddSeconds(4));
    }

    private static void RunMklink(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Fail($"mklink failed: {output}");
    }

    private sealed class SyncProgress(Action<BackupProgress> onReport) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => onReport(value);
    }

    /// <summary>Writes to the real disk, with switches to simulate a small or full target.</summary>
    private sealed class FakeVolume : ITargetVolume
    {
        private readonly PhysicalTargetVolume _inner = new();

        public long? FreeSpace { get; init; }
        public Action<string>? OnCreateFile { get; init; }
        public bool FailWritesWithDiskFull { get; init; }
        public int FilesCreated { get; private set; }

        public long GetAvailableFreeSpace(string directory) => FreeSpace ?? _inner.GetAvailableFreeSpace(directory);

        public Stream CreateFile(string path)
        {
            OnCreateFile?.Invoke(path);
            FilesCreated++;
            var stream = _inner.CreateFile(path);
            return FailWritesWithDiskFull ? new DiskFullStream(stream) : stream;
        }
    }

    private sealed class DiskFullStream(Stream inner) : Stream
    {
        private const int ErrorDiskFull = unchecked((int)0x80070070);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("There is not enough space on the disk.", ErrorDiskFull);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~BackupRunnerTests"`
Expected: build errors such as `The type or namespace name 'BackupRunner' could not be found`.

- [ ] **Step 4: Create `src/ReBackup.Core/Backup/TargetVolume.cs`**

```csharp
namespace ReBackup.Core.Backup;

/// <summary>The part of the file system a backup writes to. Replaced in tests to simulate a full disk.</summary>
public interface ITargetVolume
{
    /// <summary>Free bytes available for the folder; <see cref="long.MaxValue"/> when it cannot be determined.</summary>
    long GetAvailableFreeSpace(string directory);

    /// <summary>Creates a new file for writing; fails when it already exists.</summary>
    Stream CreateFile(string path);
}

public sealed class PhysicalTargetVolume : ITargetVolume
{
    private const int BufferSize = 1024 * 1024;

    public long GetAvailableFreeSpace(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            return string.IsNullOrEmpty(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Network shares (UNC paths) have no drive letter: skip the preflight; a full disk is still caught while copying.
            return long.MaxValue;
        }
    }

    public Stream CreateFile(string path) =>
        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan);
}
```

- [ ] **Step 5: Create `src/ReBackup.Core/Backup/BackupRunner.cs`**

```csharp
using System.Diagnostics;
using System.IO.Hashing;
using System.Text.Json;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.IO;
using ReBackup.Core.Json;
using ReBackup.Core.Plans;

namespace ReBackup.Core.Backup;

public enum BackupPhase { Indexing, Copying, Finishing }

public readonly record struct BackupProgress(
    BackupPhase Phase, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string CurrentFile)
{
    /// <summary>0..1, by bytes; by files when there are no bytes to copy.</summary>
    public double Fraction =>
        BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1)
        : FilesTotal > 0 ? Math.Clamp((double)FilesDone / FilesTotal, 0, 1)
        : Phase == BackupPhase.Finishing ? 1 : 0;
}

/// <summary>A plan as saved, the global ignore defaults at the time of the request, and why the run starts.</summary>
public sealed record BackupRequest(BackupPlan Plan, IReadOnlyList<string> GlobalIgnoreDefaults, RunTrigger Trigger);

public interface IBackupRunner
{
    /// <summary>Runs one backup. Never throws: every outcome, including cancellation, is returned as an entry.</summary>
    Task<RunLogEntry> RunAsync(BackupRequest request, IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class BackupRunner : IBackupRunner
{
    /// <summary>The target must have room for the included bytes times this factor.</summary>
    public const double FreeSpaceMargin = 1.05;

    private const int BufferSize = 1024 * 1024;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorDiskFull = 112;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly ITargetVolume _volume;
    private readonly TimeProvider _time;

    public BackupRunner(ITargetVolume? volume = null, TimeProvider? timeProvider = null)
    {
        _volume = volume ?? new PhysicalTargetVolume();
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<RunLogEntry> RunAsync(BackupRequest request, IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = request.Plan;
        var started = _time.GetTimestamp();
        var entry = new RunLogEntry
        {
            RunId = Guid.NewGuid().ToString("N"),
            Trigger = request.Trigger,
            StartUtc = _time.GetUtcNow().UtcDateTime,
        };

        string? partialPath = null;
        var completed = false;
        try
        {
            var work = await Task.Run(() => Prepare(request, progress, cancellationToken), cancellationToken);
            foreach (var skipped in work.Skipped)
                entry.AddSkipped(skipped);

            var versionName = await ReserveVersionNameAsync(plan, cancellationToken);
            var finalPath = Path.Combine(plan.Target, versionName);
            partialPath = finalPath + VersionName.PartialSuffix;
            var partial = partialPath;
            await Task.Run(() => CopyAndFinish(work, plan, partial, finalPath, entry, progress, cancellationToken),
                cancellationToken);

            entry.Version = versionName;
            entry.Status = entry.SkippedCount > 0 ? RunStatus.CompletedWithWarnings : RunStatus.Completed;
            completed = true;
        }
        catch (OperationCanceledException)
        {
            entry.Status = RunStatus.Canceled;
        }
        catch (BackupAbortException ex)
        {
            entry.Status = ex.Status;
            entry.Reason = ex.Message;
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            entry.Status = RunStatus.Full;
            entry.Reason = "The target ran out of space during the backup.";
        }
        catch (Exception ex)
        {
            entry.Status = RunStatus.Error;
            entry.Reason = ex.Message;
        }

        if (!completed && partialPath is not null)
            TryDeleteDirectory(partialPath);

        entry.DurationMs = (long)_time.GetElapsedTime(started).TotalMilliseconds;
        entry.EndUtc = entry.StartUtc.AddMilliseconds(entry.DurationMs);
        return entry;
    }

    /// <summary>Index, evaluate and preflight. Writes nothing except creating the target folder and removing leftovers.</summary>
    private BackupWork Prepare(BackupRequest request, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        var plan = request.Plan;
        if (string.IsNullOrWhiteSpace(plan.Source) || !Directory.Exists(plan.Source))
            throw new BackupAbortException(RunStatus.Error, $"Source folder \"{plan.Source}\" does not exist.");
        if (string.IsNullOrWhiteSpace(plan.Target))
            throw new BackupAbortException(RunStatus.Error, "No target folder is set.");

        Directory.CreateDirectory(plan.Target);
        DeleteLeftovers(plan);

        var indexProgress = progress is null ? null : new IndexProgressAdapter(progress);
        var index = SourceIndexer.Build(plan.Source, indexProgress, cancellationToken);
        var matcher = IgnoreMatcher.ForPlan(plan.Ignore, request.GlobalIgnoreDefaults, index.IgnoreFiles);
        var root = IndexEvaluator.Evaluate(index, matcher, cancellationToken);

        var work = new BackupWork(index.Root);
        foreach (var path in index.UnreadableIgnoreFiles)
            work.Skipped.Add(new SkippedEntry(path, "ignore file could not be read; its patterns were not applied"));
        Collect(root, work);

        var required = (long)Math.Ceiling(work.TotalBytes * FreeSpaceMargin);
        var free = _volume.GetAvailableFreeSpace(plan.Target);
        if (required > free)
        {
            throw new BackupAbortException(RunStatus.Full,
                $"The backup needs {ByteSize.Format(required)} but only {ByteSize.Format(free)} is free on the target.");
        }
        return work;
    }

    private static void Collect(EvaluatedNode node, BackupWork work)
    {
        if (node.Status == IncludeStatus.Ignored)
            return;

        var path = node.Node.RelativePath;
        if (!node.Node.IsDirectory)
        {
            if (path.Equals(VersionName.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                work.Skipped.Add(new SkippedEntry(path, "the name is reserved for the backup manifest"));
                return;
            }
            work.Files.Add(node.Node);
            work.TotalBytes += node.Node.Size;
            return;
        }

        if (path.Length > 0)
            work.Directories.Add(node.Node);
        if (node.Node.Error is { } error)
            work.Skipped.Add(new SkippedEntry(path.Length == 0 ? "." : path, error));
        foreach (var child in node.Children)
            Collect(child, work);
    }

    private void DeleteLeftovers(BackupPlan plan)
    {
        foreach (var directory in Directory.EnumerateDirectories(plan.Target, "*" + VersionName.PartialSuffix))
        {
            var name = Path.GetFileName(directory);
            var versionName = name[..^VersionName.PartialSuffix.Length];
            if (VersionName.TryParse(versionName, plan.Name, out _))
                TryDeleteDirectory(directory);
        }
    }

    /// <summary>Picks the folder name for the current minute; waits for the next minute if that name is taken.</summary>
    private async Task<string> ReserveVersionNameAsync(BackupPlan plan, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = _time.GetLocalNow().DateTime;
            var name = VersionName.Format(now, plan.Name);
            var path = Path.Combine(plan.Target, name);
            if (!Directory.Exists(path) && !Directory.Exists(path + VersionName.PartialSuffix))
                return name;

            var nextMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddMinutes(1);
            await Task.Delay(nextMinute - now + TimeSpan.FromMilliseconds(50), _time, cancellationToken);
        }
    }

    private void CopyAndFinish(BackupWork work, BackupPlan plan, string partialPath, string finalPath,
        RunLogEntry entry, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(partialPath);
        foreach (var directory in work.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(partialPath, ToLocalPath(directory.RelativePath)));
        }

        var manifest = new BackupManifest
        {
            PlanId = plan.Id,
            PlanName = plan.Name,
            CreatedUtc = _time.GetUtcNow().UtcDateTime,
            Source = work.SourceRoot,
        };

        var buffer = new byte[BufferSize];
        var sinceReport = Stopwatch.StartNew();
        var filesDone = 0;
        long bytesDone = 0;

        void Report(BackupPhase phase, string currentFile, bool force)
        {
            if (progress is null || (!force && sinceReport.Elapsed < ProgressInterval))
                return;
            sinceReport.Restart();
            progress.Report(new BackupProgress(phase, filesDone, work.Files.Count, bytesDone, work.TotalBytes, currentFile));
        }

        Report(BackupPhase.Copying, "", force: true);
        foreach (var file in work.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesBefore = bytesDone;
            var localPath = ToLocalPath(file.RelativePath);
            var copied = CopyFile(Path.Combine(work.SourceRoot, localPath), Path.Combine(partialPath, localPath), file,
                buffer, count =>
                {
                    bytesDone += count;
                    Report(BackupPhase.Copying, file.RelativePath, force: false);
                }, cancellationToken, out var skipReason);

            if (copied is null)
            {
                entry.AddSkipped(new SkippedEntry(file.RelativePath, skipReason!));
            }
            else
            {
                manifest.Files.Add(copied);
                entry.FilesCopied++;
                entry.BytesCopied += copied.Size;
            }

            filesDone++;
            bytesDone = bytesBefore + file.Size;   // keeps the bar moving for skipped or changed files
            Report(BackupPhase.Copying, file.RelativePath, force: false);
        }

        Report(BackupPhase.Finishing, "", force: true);
        using (var stream = File.Create(Path.Combine(partialPath, VersionName.ManifestFileName)))
            JsonSerializer.Serialize(stream, manifest, JsonDefaults.Options);

        cancellationToken.ThrowIfCancellationRequested();
        Directory.Move(partialPath, finalPath);
        Report(BackupPhase.Finishing, "", force: true);
    }

    /// <summary>Copies one file while hashing it. Returns null and a reason when the source cannot be read; target errors propagate.</summary>
    private ManifestFile? CopyFile(string sourcePath, string targetPath, IndexNode node, byte[] buffer,
        Action<int> onBytes, CancellationToken cancellationToken, out string? skipReason)
    {
        skipReason = null;
        FileStream input;
        try
        {
            input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                BufferSize, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (SourceSkipReason(ex) is { } reason)
        {
            skipReason = reason;
            return null;
        }

        var hash = new XxHash64();
        long size = 0;
        using (input)
        using (var output = _volume.CreateFile(targetPath))
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read;
                try
                {
                    read = input.Read(buffer, 0, buffer.Length);
                }
                catch (IOException ex) when (IsLocked(ex))
                {
                    skipReason = "locked by another program";
                    break;
                }

                if (read == 0)
                    break;
                hash.Append(buffer.AsSpan(0, read));
                output.Write(buffer, 0, read);
                size += read;
                onBytes(read);
            }
        }

        if (skipReason is not null)
        {
            TryDeleteFile(targetPath);
            return null;
        }

        try
        {
            File.SetLastWriteTimeUtc(targetPath, node.LastWriteUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // The copy itself is fine; the manifest still records the source time.
        }

        return new ManifestFile(node.RelativePath, size, node.LastWriteUtc,
            "xxh64:" + Convert.ToHexStringLower(hash.GetCurrentHash()));
    }

    private static string? SourceSkipReason(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException => "no longer exists",
        UnauthorizedAccessException => "access denied",
        IOException io when IsLocked(io) => "locked by another program",
        _ => null,
    };

    private static bool IsLocked(IOException exception) =>
        (exception.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    private static bool IsDiskFull(IOException exception) =>
        (exception.HResult & 0xFFFF) is ErrorDiskFull or ErrorHandleDiskFull;

    private static string ToLocalPath(string relativePath) => relativePath.Replace('/', Path.DirectorySeparatorChar);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the next run of the plan removes leftovers.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class BackupWork(string sourceRoot)
    {
        public string SourceRoot { get; } = sourceRoot;
        public List<IndexNode> Directories { get; } = [];
        public List<IndexNode> Files { get; } = [];
        public List<SkippedEntry> Skipped { get; } = [];
        public long TotalBytes { get; set; }
    }

    private sealed class BackupAbortException(RunStatus status, string message) : Exception(message)
    {
        public RunStatus Status { get; } = status;
    }

    private sealed class IndexProgressAdapter(IProgress<BackupProgress> progress) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) =>
            progress.Report(new BackupProgress(BackupPhase.Indexing, value.Files, 0, 0, 0, value.CurrentDirectory));
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~BackupRunnerTests"` three times (the collision test uses real waiting), then the full suite and `dotnet build` (0 warnings).
Expected: PASS (17 tests) each time.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): add BackupRunner with manifest, preflight and abort handling" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 4: BackupQueue

**Files:**
- Create: `src/ReBackup.Core/Backup/BackupQueue.cs`
- Test: `tests/ReBackup.Core.Tests/Backup/BackupQueueTests.cs`

**Interfaces:**
- Consumes: `IBackupRunner`, `BackupRequest`, `BackupProgress`, `RunLog`, `RunLogEntry`, `RunStatus`
- Produces:
  - `enum JobState { Queued, Running, Finished, Removed }` (`Removed` = canceled while still queued; nothing ran and nothing is logged)
  - `record BackupJobUpdate(string PlanId, string PlanName, JobState State, BackupProgress? Progress, RunLogEntry? Result)`
  - `BackupQueue(IBackupRunner runner, Func<string, RunLog> logForPlan, TimeProvider? timeProvider = null)` with
    - `bool Enqueue(BackupRequest request)`: false when that plan is already queued or running
    - `bool Cancel(string planId)`: cancels the running job or removes the queued one
    - `void CancelAll()`
    - `bool IsBusy`, `int QueuedCount`, `string? RunningPlanId`
    - `Task WhenIdleAsync()`: completes when nothing is queued or running
    - `event Action<BackupJobUpdate>? Changed`: raised on the enqueuing thread (Queued) and on worker threads, in order per job (Queued → Running (repeated with progress) → Finished). Handlers must not block or call back into the queue.

- [ ] **Step 1: Write the failing tests** `tests/ReBackup.Core.Tests/Backup/BackupQueueTests.cs`

```csharp
using System.Collections.Concurrent;
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Backup;

public class BackupQueueTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly TempDir _tmp = new();
    private readonly FakeRunner _runner = new();
    private readonly ConcurrentQueue<BackupJobUpdate> _updates = new();
    private readonly BackupQueue _queue;

    public BackupQueueTests()
    {
        _queue = new BackupQueue(_runner, planId => new RunLog(_tmp.PathOf($"{planId}.jsonl")));
        _queue.Changed += _updates.Enqueue;
    }

    public void Dispose() => _tmp.Dispose();

    private static BackupRequest Request(string planId) =>
        new(new BackupPlan { Id = planId, Name = "Plan " + planId }, [], RunTrigger.Manual);

    private string[] States(string planId) =>
        _updates.Where(u => u.PlanId == planId && u.Progress is null).Select(u => u.State.ToString()).ToArray();

    [Fact]
    public async Task Runs_jobs_one_at_a_time_in_order()
    {
        _queue.Enqueue(Request("a")).Should().BeTrue();
        _queue.Enqueue(Request("b")).Should().BeTrue();
        await _runner.Started("a").WaitAsync(Timeout);

        _queue.IsBusy.Should().BeTrue();
        _queue.RunningPlanId.Should().Be("a");
        _queue.QueuedCount.Should().Be(1);
        _runner.HasStarted("b").Should().BeFalse();

        _runner.Complete("a", RunStatus.Completed);
        await _runner.Started("b").WaitAsync(Timeout);
        _runner.Complete("b", RunStatus.Completed);
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        _queue.IsBusy.Should().BeFalse();
        States("a").Should().Equal("Queued", "Running", "Finished");
        States("b").Should().Equal("Queued", "Running", "Finished");
        _updates.Last(u => u.PlanId == "a").Result!.Status.Should().Be(RunStatus.Completed);
        _updates.First(u => u.PlanId == "a").PlanName.Should().Be("Plan a");
    }

    [Fact]
    public async Task A_plan_can_be_queued_or_running_only_once()
    {
        _queue.Enqueue(Request("a")).Should().BeTrue();
        _queue.Enqueue(Request("b")).Should().BeTrue();
        await _runner.Started("a").WaitAsync(Timeout);

        _queue.Enqueue(Request("a")).Should().BeFalse("it is running");
        _queue.Enqueue(Request("b")).Should().BeFalse("it is queued");

        _runner.Complete("a", RunStatus.Completed);
        await _runner.Started("b").WaitAsync(Timeout);
        _queue.Enqueue(Request("a")).Should().BeTrue("it has finished");
        _queue.CancelAll();
        _runner.Complete("b", RunStatus.Completed);
        await _queue.WhenIdleAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task Canceling_a_queued_job_removes_it_without_running_or_logging()
    {
        _queue.Enqueue(Request("a"));
        _queue.Enqueue(Request("b"));
        await _runner.Started("a").WaitAsync(Timeout);

        _queue.Cancel("b").Should().BeTrue();
        _runner.Complete("a", RunStatus.Completed);
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        States("b").Should().Equal("Queued", "Removed");
        _runner.HasStarted("b").Should().BeFalse();
        File.Exists(_tmp.PathOf("b.jsonl")).Should().BeFalse();
        _queue.Cancel("b").Should().BeFalse("nothing to cancel any more");
    }

    [Fact]
    public async Task Canceling_the_running_job_cancels_its_token()
    {
        _queue.Enqueue(Request("a"));
        await _runner.Started("a").WaitAsync(Timeout);

        _queue.Cancel("a").Should().BeTrue();
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        _updates.Last(u => u.PlanId == "a").Result!.Status.Should().Be(RunStatus.Canceled);
    }

    [Fact]
    public async Task Result_is_appended_to_the_log_of_that_plan()
    {
        _queue.Enqueue(Request("a"));
        await _runner.Started("a").WaitAsync(Timeout);
        _runner.Complete("a", RunStatus.CompletedWithWarnings);
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        new RunLog(_tmp.PathOf("a.jsonl")).ReadAll().Should().ContainSingle()
            .Which.Status.Should().Be(RunStatus.CompletedWithWarnings);
    }

    [Fact]
    public async Task Progress_is_forwarded_while_running()
    {
        _queue.Enqueue(Request("a"));
        await _runner.Started("a").WaitAsync(Timeout);

        _runner.ReportProgress("a", new BackupProgress(BackupPhase.Copying, 1, 2, 10, 20, "x"));
        _runner.Complete("a", RunStatus.Completed);
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        var progress = _updates.Single(u => u.Progress is not null);
        progress.State.Should().Be(JobState.Running);
        progress.Progress!.Value.BytesDone.Should().Be(10);
    }

    [Fact]
    public async Task A_runner_that_throws_is_reported_as_Error_and_the_queue_continues()
    {
        _queue.Enqueue(Request("a"));
        _queue.Enqueue(Request("b"));
        await _runner.Started("a").WaitAsync(Timeout);

        _runner.Fail("a", new InvalidOperationException("boom"));
        await _runner.Started("b").WaitAsync(Timeout);
        _runner.Complete("b", RunStatus.Completed);
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        var failed = _updates.Last(u => u.PlanId == "a").Result!;
        failed.Status.Should().Be(RunStatus.Error);
        failed.Reason.Should().Be("boom");
        States("b").Should().Equal("Queued", "Running", "Finished");
    }

    [Fact]
    public async Task WhenIdleAsync_completes_immediately_when_nothing_is_queued()
    {
        await _queue.WhenIdleAsync().WaitAsync(Timeout);

        _queue.IsBusy.Should().BeFalse();
        _queue.RunningPlanId.Should().BeNull();
    }

    /// <summary>A runner whose runs finish when the test says so.</summary>
    private sealed class FakeRunner : IBackupRunner
    {
        private readonly ConcurrentDictionary<string, Run> _runs = new();

        private Run For(string planId) => _runs.GetOrAdd(planId, _ => new Run());

        public Task Started(string planId) => For(planId).Started.Task;
        public bool HasStarted(string planId) => For(planId).Started.Task.IsCompleted;

        public void Complete(string planId, RunStatus status) =>
            For(planId).Result.TrySetResult(new RunLogEntry { RunId = planId, Status = status });

        public void Fail(string planId, Exception exception) => For(planId).Result.TrySetException(exception);

        public void ReportProgress(string planId, BackupProgress progress) => For(planId).Progress!.Report(progress);

        public async Task<RunLogEntry> RunAsync(BackupRequest request, IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var run = For(request.Plan.Id);
            run.Progress = progress;
            using var registration = cancellationToken.Register(() =>
                run.Result.TrySetResult(new RunLogEntry { RunId = request.Plan.Id, Status = RunStatus.Canceled }));
            run.Started.TrySetResult();
            var result = await run.Result.Task;
            _runs.TryRemove(request.Plan.Id, out _);
            return result;
        }

        private sealed class Run
        {
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<RunLogEntry> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public IProgress<BackupProgress>? Progress { get; set; }
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~BackupQueueTests"`
Expected: build error `The type or namespace name 'BackupQueue' could not be found`.

- [ ] **Step 3: Implement `src/ReBackup.Core/Backup/BackupQueue.cs`**

```csharp
namespace ReBackup.Core.Backup;

public enum JobState
{
    Queued,
    Running,
    Finished,
    /// <summary>Canceled while still queued: nothing ran and nothing was logged.</summary>
    Removed,
}

public sealed record BackupJobUpdate(string PlanId, string PlanName, JobState State, BackupProgress? Progress,
    RunLogEntry? Result);

/// <summary>Runs backups one at a time. A plan can be queued or running only once.</summary>
public sealed class BackupQueue
{
    private readonly IBackupRunner _runner;
    private readonly Func<string, RunLog> _logForPlan;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<Job> _queued = [];
    private Job? _running;
    private bool _workerActive;
    private Task _worker = Task.CompletedTask;

    public BackupQueue(IBackupRunner runner, Func<string, RunLog> logForPlan, TimeProvider? timeProvider = null)
    {
        _runner = runner;
        _logForPlan = logForPlan;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised on worker threads. Per job: Queued, Running (repeated with progress), then Finished or Removed.</summary>
    public event Action<BackupJobUpdate>? Changed;

    public bool IsBusy
    {
        get { lock (_gate) return _running is not null || _queued.Count > 0; }
    }

    public int QueuedCount
    {
        get { lock (_gate) return _queued.Count; }
    }

    public string? RunningPlanId
    {
        get { lock (_gate) return _running?.PlanId; }
    }

    /// <summary>False when that plan is already queued or running.</summary>
    public bool Enqueue(BackupRequest request)
    {
        var job = new Job(request);
        lock (_gate)
        {
            if (_running?.PlanId == job.PlanId || _queued.Any(q => q.PlanId == job.PlanId))
            {
                job.Cancellation.Dispose();
                return false;
            }
            _queued.Add(job);

            // Raised inside the lock so that the worker cannot report Running before Queued.
            // Handlers must not block or call back into the queue.
            Raise(job, JobState.Queued);

            if (!_workerActive)
            {
                _workerActive = true;
                _worker = Task.Run(ProcessAsync);
            }
        }
        return true;
    }

    /// <summary>Cancels the running job of that plan or removes its queued job. False when there is neither.</summary>
    public bool Cancel(string planId)
    {
        Job? removed = null;
        lock (_gate)
        {
            if (_running?.PlanId == planId)
            {
                _running.Cancellation.Cancel();
                return true;
            }
            var index = _queued.FindIndex(q => q.PlanId == planId);
            if (index < 0)
                return false;
            removed = _queued[index];
            _queued.RemoveAt(index);
        }

        Raise(removed, JobState.Removed);
        removed.Cancellation.Dispose();
        return true;
    }

    public void CancelAll()
    {
        List<Job> removed;
        lock (_gate)
        {
            removed = [.. _queued];
            _queued.Clear();
            _running?.Cancellation.Cancel();
        }

        foreach (var job in removed)
        {
            Raise(job, JobState.Removed);
            job.Cancellation.Dispose();
        }
    }

    /// <summary>Completes when nothing is queued or running.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
            return _worker;
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            Job job;
            lock (_gate)
            {
                if (_queued.Count == 0)
                {
                    _workerActive = false;
                    return;
                }
                job = _queued[0];
                _queued.RemoveAt(0);
                _running = job;
            }

            Raise(job, JobState.Running);
            var result = await RunAsync(job);

            try
            {
                _logForPlan(job.PlanId).Append(result);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The run itself is over; a log that cannot be written must not stop the queue.
            }

            lock (_gate)
                _running = null;
            Raise(job, JobState.Finished, result: result);
            job.Cancellation.Dispose();
        }
    }

    private async Task<RunLogEntry> RunAsync(Job job)
    {
        try
        {
            var progress = new JobProgress(this, job);
            return await _runner.RunAsync(job.Request, progress, job.Cancellation.Token);
        }
        catch (Exception ex)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            return new RunLogEntry
            {
                RunId = Guid.NewGuid().ToString("N"),
                Trigger = job.Request.Trigger,
                StartUtc = now,
                EndUtc = now,
                Status = ex is OperationCanceledException ? RunStatus.Canceled : RunStatus.Error,
                Reason = ex is OperationCanceledException ? null : ex.Message,
            };
        }
    }

    private void Raise(Job job, JobState state, BackupProgress? progress = null, RunLogEntry? result = null) =>
        Changed?.Invoke(new BackupJobUpdate(job.PlanId, job.Request.Plan.Name, state, progress, result));

    private sealed class Job(BackupRequest request)
    {
        public BackupRequest Request { get; } = request;
        public string PlanId => Request.Plan.Id;
        public CancellationTokenSource Cancellation { get; } = new();
    }

    /// <summary>Forwards progress synchronously, so updates keep their order.</summary>
    private sealed class JobProgress(BackupQueue queue, Job job) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => queue.Raise(job, JobState.Running, value);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~BackupQueueTests"` three times, then the full suite and `dotnet build` (0 warnings).
Expected: PASS (8 tests) each time.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): add BackupQueue" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 5: Single-instance guard

**Files:**
- Create: `src/ReBackup.App/Services/SingleInstance.cs`
- Modify: `src/ReBackup.App/App.xaml.cs`

**Interfaces:**
- Produces: `SingleInstance.TryAcquire(TimeSpan waitForPrevious) → SingleInstance?` (null when another instance holds the lock), `SingleInstance.SignalRunningInstance()`, `void ListenForActivation(Action onActivate)` (called on a thread-pool thread), `Dispose()`. Command-line switch `--restarted`: the new process waits up to 10 seconds for the old one to exit.

Two running copies would each run the queue and, from Phase 5, the scheduler, so the same plan would be backed up twice. A second start now only brings the first window to the front.

- [ ] **Step 1: Create `src/ReBackup.App/Services/SingleInstance.cs`**

```csharp
namespace ReBackup.App.Services;

/// <summary>Holds a per-session mutex for as long as this copy of the app runs, and lets a second start wake the first.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\ReBackup.SingleInstance";
    private const string ActivateEventName = @"Local\ReBackup.Activate";

    private readonly Mutex _mutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _registration;
    private bool _disposed;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Null when another instance is running. Call <see cref="Dispose"/> on the same thread.</summary>
    public static SingleInstance? TryAcquire(TimeSpan waitForPrevious)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(waitForPrevious);
        }
        catch (AbandonedMutexException)
        {
            owned = true;   // the previous instance died without releasing it
        }

        if (owned)
            return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    /// <summary>Asks the instance that is already running to show its window.</summary>
    public static void SignalRunningInstance()
    {
        if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var activate))
        {
            using (activate)
                activate.Set();
        }
    }

    /// <summary><paramref name="onActivate"/> runs on a thread-pool thread each time another start signals this instance.</summary>
    public void ListenForActivation(Action onActivate)
    {
        _activateEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivateEventName);
        _registration = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) => onActivate(), null,
            System.Threading.Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _registration?.Unregister(null);
        _activateEvent?.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released from another thread than the one that acquired it; disposing the handle frees it as well.
        }
        _mutex.Dispose();
    }
}
```

- [ ] **Step 2: Use it in `App.xaml.cs`**

Read the current file first. Add the field:

```csharp
    private SingleInstance? _singleInstance;
```

In `OnStartup`, directly after `base.OnStartup(e);`, add:

```csharp
        var restarted = e.Args.Contains("--restarted", StringComparer.OrdinalIgnoreCase);
        _singleInstance = SingleInstance.TryAcquire(restarted ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        if (_singleInstance is null)
        {
            SingleInstance.SignalRunningInstance();
            Shutdown();
            return;
        }
```

In `OnStartup`, directly after `CreateTrayIcon();`, add:

```csharp
        _singleInstance.ListenForActivation(() => Dispatcher.InvokeAsync(ShowMainWindow));
```

In `OnStartup`, in the existing block that calls `Shutdown()` when `BootstrapConfiguration()` returns false, release the lock first:

```csharp
        if (!BootstrapConfiguration())
        {
            _singleInstance.Dispose();
            Shutdown();
            return;
        }
```

In `Restart()`: add `_singleInstance?.Dispose();` directly before the `try` that starts the new process, and start the new process with the switch:

```csharp
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restarted") { UseShellExecute = false });
```

In `OnSessionEnding` and in `ExitApp`, add `_singleInstance?.Dispose();` directly after `_planStore.Dispose();`.

- [ ] **Step 3: Build, test, smoke-test**

Run: `dotnet build` (0 warnings, 0 errors), `dotnet test tests/ReBackup.Core.Tests` (all pass).
Smoke test: start the built exe (A), wait 3 s; start it again (B) and wait 3 s. Expected: B has exited (`HasExited` is true) and A is still running. Stop A.

Manual checklist (for the user; do not click through it as an agent):
1. Start the app, close the window to the tray, start the exe again: the first window comes back and no second tray icon appears.
2. Settings → Change… config folder: the app restarts exactly once and comes up.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(app): run as a single instance" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 6: Run now, progress, cancel and the History tab

**Files:**
- Create: `src/ReBackup.App/ViewModels/RunHistoryRow.cs`, `src/ReBackup.App/ViewModels/PlanRunViewModel.cs`, `src/ReBackup.App/Views/HistoryView.xaml`, `src/ReBackup.App/Views/HistoryView.xaml.cs`
- Modify: `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`, `src/ReBackup.App/ViewModels/MainViewModel.cs`, `src/ReBackup.App/MainWindow.xaml`, `src/ReBackup.App/App.xaml.cs`, `docs/superpowers/specs/2026-09-30-rebackup-design.md` (§4.5)

**Interfaces:**
- Consumes: `BackupQueue` (`Enqueue`, `Cancel`, `IsBusy`, `QueuedCount`, `Changed`), `BackupJobUpdate`, `JobState`, `BackupProgress`, `BackupPhase`, `BackupRequest`, `BackupRunner`, `RunLog`, `RunLogEntry`, `RunStatus`, `RunTrigger`, `ByteSize.Format`, `ConfigPaths.LogFileFor`, `AppSettings.DefaultIgnorePatterns`
- Produces:
  - `RunHistoryRow(RunLogEntry entry)` with `StartText`, `DurationText`, `TriggerText`, `StatusText`, `Reason`, `FilesText`, `SizeText`, `Details`, `HasDetails`, and `static string FormatDuration(long milliseconds)`
  - `PlanRunViewModel` with `State` (`JobState?`, null = idle), `IsActive`, `IsRunning`, `IsIndeterminate`, `ProgressPercent`, `ProgressText`, `LastRunText`, `History` (newest first), `Apply(BackupJobUpdate)`, `LoadHistory(IReadOnlyList<RunLogEntry>)`
  - `PlanEditorViewModel.Run` and `PlanEditorViewModel.SavedPlan()` (a copy of the plan as last saved)
  - `MainViewModel` constructor: `MainViewModel(PlanStore store, ConfigPaths paths, AppSettings settings, IDialogService dialogs, Action openSettings, BackupQueue queue, Action<Action> runOnUi)`; new members `QueueStatus`, `RunNowCommand` and `CancelRunCommand` (parameter: a `PlanEditorViewModel`, null = the selected plan), `bool RunPlan(string planId)`, `event Action<string, RunLogEntry>? RunFinished` (plan name, result; raised on the UI thread)
  - `App` fields `_queue` (`BackupQueue`)

- [ ] **Step 1: Create `src/ReBackup.App/ViewModels/RunHistoryRow.cs`**

```csharp
using System.Globalization;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;

namespace ReBackup.App.ViewModels;

/// <summary>One run of a plan, formatted for the History tab.</summary>
public sealed class RunHistoryRow
{
    private readonly RunLogEntry _entry;

    public RunHistoryRow(RunLogEntry entry) => _entry = entry;

    public string StartText => _entry.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string DurationText => FormatDuration(_entry.DurationMs);
    public string TriggerText => _entry.Trigger == RunTrigger.CatchUp ? "Catch-up" : _entry.Trigger.ToString();

    public string StatusText => _entry.Status switch
    {
        RunStatus.CompletedWithWarnings => "Completed with warnings",
        RunStatus.Full => "Aborted: target full",
        RunStatus.Error => "Aborted: error",
        RunStatus.Canceled => "Canceled",
        _ => "Completed",
    };

    public string Reason => _entry.Reason ?? "";
    public string FilesText => _entry.FilesCopied.ToString("N0", CultureInfo.CurrentCulture);
    public string SizeText => ByteSize.Format(_entry.BytesCopied);
    public bool HasDetails => Details.Length > 0;

    public string Details
    {
        get
        {
            var lines = new List<string>();
            if (_entry.Version is not null)
                lines.Add($"Version: {_entry.Version}");
            if (_entry.SkippedCount > 0)
            {
                lines.Add($"Skipped ({_entry.SkippedCount:N0}):");
                lines.AddRange(_entry.Skipped.Select(s => $"  {s.Path} — {s.Reason}"));
                if (_entry.SkippedCount > _entry.Skipped.Count)
                    lines.Add($"  … and {_entry.SkippedCount - _entry.Skipped.Count:N0} more");
            }
            if (_entry.RetentionDeleted.Count > 0)
            {
                lines.Add("Deleted by retention:");
                lines.AddRange(_entry.RetentionDeleted.Select(v => "  " + v));
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    public static string FormatDuration(long milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours} h {duration.Minutes:00} min {duration.Seconds:00} s";
        if (duration.TotalMinutes >= 1)
            return $"{duration.Minutes} min {duration.Seconds:00} s";
        return duration.TotalSeconds >= 1 ? $"{duration.Seconds} s" : "under 1 s";
    }
}
```

- [ ] **Step 2: Create `src/ReBackup.App/ViewModels/PlanRunViewModel.cs`**

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;

namespace ReBackup.App.ViewModels;

/// <summary>Queue state, progress and history of one plan.</summary>
public sealed partial class PlanRunViewModel : ObservableObject
{
    private const string NeverRunText = "Never run";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsRunning))]
    private JobState? _state;

    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _lastRunText = NeverRunText;

    public ObservableCollection<RunHistoryRow> History { get; } = [];

    /// <summary>Queued or running.</summary>
    public bool IsActive => State is JobState.Queued or JobState.Running;

    public bool IsRunning => State is JobState.Running;

    public void Apply(BackupJobUpdate update)
    {
        switch (update.State)
        {
            case JobState.Queued:
                State = JobState.Queued;
                IsIndeterminate = true;
                ProgressPercent = 0;
                ProgressText = "Queued";
                break;

            case JobState.Running:
                State = JobState.Running;
                if (update.Progress is { } progress)
                    ShowProgress(progress);
                else
                    ProgressText = "Starting…";
                break;

            default:
                State = null;
                IsIndeterminate = false;
                ProgressPercent = 0;
                ProgressText = "";
                break;
        }
    }

    /// <summary>Entries oldest first, as read from the log; shown newest first.</summary>
    public void LoadHistory(IReadOnlyList<RunLogEntry> entries)
    {
        History.Clear();
        for (var i = entries.Count - 1; i >= 0; i--)
            History.Add(new RunHistoryRow(entries[i]));

        LastRunText = History.Count == 0
            ? NeverRunText
            : $"Last run {History[0].StartText}: {History[0].StatusText}";
    }

    private void ShowProgress(BackupProgress progress)
    {
        IsIndeterminate = progress.Phase == BackupPhase.Indexing;
        ProgressPercent = progress.Fraction * 100;
        ProgressText = progress.Phase switch
        {
            BackupPhase.Indexing => $"Indexing… {progress.FilesDone:N0} files",
            BackupPhase.Copying =>
                $"{progress.FilesDone:N0} / {progress.FilesTotal:N0} files · " +
                $"{ByteSize.Format(progress.BytesDone)} / {ByteSize.Format(progress.BytesTotal)}",
            _ => "Finishing…",
        };
    }
}
```

- [ ] **Step 3: Extend `PlanEditorViewModel`**

In `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs` add next to the `Preview` property:

```csharp
    /// <summary>Queue state, progress and history of this plan.</summary>
    public PlanRunViewModel Run { get; } = new();

    /// <summary>A copy of the plan as last saved (unsaved edits are not part of a run).</summary>
    public BackupPlan SavedPlan() => _saved.Clone();
```

- [ ] **Step 4: Wire the queue into `MainViewModel`**

Read the current `src/ReBackup.App/ViewModels/MainViewModel.cs` first. Add `using ReBackup.Core.Backup;`.

Add fields and the observable property:

```csharp
    private readonly BackupQueue _queue;
    private readonly Action<Action> _runOnUi;

    [ObservableProperty] private string _queueStatus = "No backup running";
```

Change the constructor signature to take the two new parameters last, store them, and subscribe **before** the plans are loaded:

```csharp
    public MainViewModel(PlanStore store, ConfigPaths paths, AppSettings settings, IDialogService dialogs,
        Action openSettings, BackupQueue queue, Action<Action> runOnUi)
    {
        _store = store;
        _paths = paths;
        _settings = settings;
        _dialogs = dialogs;
        _openSettings = openSettings;
        _queue = queue;
        _runOnUi = runOnUi;
        _queue.Changed += update => _runOnUi(() => OnJobUpdate(update));
```

(the rest of the constructor stays as it is).

Add the event next to the other public members:

```csharp
    /// <summary>Plan name and result of a finished run; raised on the UI thread.</summary>
    public event Action<string, RunLogEntry>? RunFinished;
```

In `AddEditor`, load the history before `Plans.Add(editor);`:

```csharp
        LoadHistory(editor);
```

At the start of `DeletePlan`, after the `if (editor is null) return;` check, add:

```csharp
        if (editor.Run.IsActive)
        {
            StatusMessage = $"Cancel the backup of \"{editor.Name}\" before deleting the plan.";
            return;
        }
```

Add these members (commands next to the other commands, helpers next to the other private methods):

```csharp
    /// <summary>Queues a run of the saved plan. False when it is dirty, invalid, unknown or already queued.</summary>
    public bool RunPlan(string planId)
    {
        var editor = Plans.FirstOrDefault(p => p.Id.Equals(planId, StringComparison.OrdinalIgnoreCase));
        return editor is not null && Start(editor);
    }

    [RelayCommand]
    private void RunNow(PlanEditorViewModel? editor)
    {
        editor ??= SelectedPlan;
        if (editor is not null)
            Start(editor);
    }

    [RelayCommand]
    private void CancelRun(PlanEditorViewModel? editor)
    {
        editor ??= SelectedPlan;
        if (editor is not null && _queue.Cancel(editor.Id))
            StatusMessage = $"Canceling the backup of \"{editor.Name}\"…";
    }

    private bool Start(PlanEditorViewModel editor)
    {
        if (editor.IsNew || editor.IsDirty || editor.Errors.Count > 0)
        {
            StatusMessage = $"Save \"{editor.Name}\" without errors before running it.";
            return false;
        }

        var request = new BackupRequest(editor.SavedPlan(), _settings.DefaultIgnorePatterns.ToList(), RunTrigger.Manual);
        if (!_queue.Enqueue(request))
        {
            StatusMessage = $"\"{editor.Name}\" is already queued or running.";
            return false;
        }
        return true;
    }

    private void OnJobUpdate(BackupJobUpdate update)
    {
        var editor = Plans.FirstOrDefault(p => p.Id.Equals(update.PlanId, StringComparison.OrdinalIgnoreCase));
        editor?.Run.Apply(update);
        UpdateQueueStatus(update);

        if (update is not { State: JobState.Finished, Result: { } result })
            return;

        if (editor is not null)
            LoadHistory(editor);
        StatusMessage = result.Status switch
        {
            RunStatus.Completed => $"Backup of \"{update.PlanName}\" completed in {RunHistoryRow.FormatDuration(result.DurationMs)}.",
            RunStatus.CompletedWithWarnings =>
                $"Backup of \"{update.PlanName}\" completed with {result.SkippedCount:N0} skipped entries.",
            RunStatus.Canceled => $"Backup of \"{update.PlanName}\" was canceled.",
            _ => $"Backup of \"{update.PlanName}\" was aborted: {result.Reason}",
        };
        RunFinished?.Invoke(update.PlanName, result);
    }

    private void UpdateQueueStatus(BackupJobUpdate update)
    {
        var queued = _queue.QueuedCount;
        var waiting = queued > 0 ? $" · {queued} queued" : "";
        if (update.State == JobState.Running)
        {
            var percent = update.Progress is { } progress ? $" — {progress.Fraction * 100:0} %" : "";
            QueueStatus = $"Backing up \"{update.PlanName}\"{percent}{waiting}";
        }
        else if (!_queue.IsBusy)
        {
            QueueStatus = "No backup running";
        }
    }

    private void LoadHistory(PlanEditorViewModel editor)
    {
        try
        {
            editor.Run.LoadHistory(new RunLog(_paths.LogFileFor(editor.Id)).ReadAll());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"The run history of \"{editor.Name}\" could not be read: {ex.Message}";
        }
    }
```

- [ ] **Step 5: Create the queue in `App.xaml.cs`**

Add `using ReBackup.Core.Backup;` and the field `private BackupQueue _queue = null!;`.

In `LoadConfiguration`, create the queue before the view model and pass it in. Replace the line that constructs `MainViewModel` with:

```csharp
            var queue = new BackupQueue(new BackupRunner(), planId => new RunLog(paths.LogFileFor(planId)));
            var mainViewModel = new MainViewModel(planStore, paths, settings, _dialogs, ShowSettings, queue,
                action => Dispatcher.InvokeAsync(action));
```

and add `_queue = queue;` next to the other field assignments in that block.

- [ ] **Step 6: Create `src/ReBackup.App/Views/HistoryView.xaml`**

```xml
<UserControl x:Class="ReBackup.App.Views.HistoryView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <DockPanel Margin="12">
        <TextBlock DockPanel.Dock="Top" Margin="0,0,0,6" Text="{Binding Run.LastRunText}" />
        <DataGrid ItemsSource="{Binding Run.History}" AutoGenerateColumns="False" IsReadOnly="True"
                  HeadersVisibility="Column" GridLinesVisibility="Horizontal" SelectionMode="Single"
                  CanUserResizeRows="False" RowDetailsVisibilityMode="VisibleWhenSelected">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Start" Width="140" Binding="{Binding StartText}" />
                <DataGridTextColumn Header="Duration" Width="110" Binding="{Binding DurationText}" />
                <DataGridTextColumn Header="Trigger" Width="80" Binding="{Binding TriggerText}" />
                <DataGridTextColumn Header="Result" Width="170" Binding="{Binding StatusText}" />
                <DataGridTextColumn Header="Files" Width="70" Binding="{Binding FilesText}" />
                <DataGridTextColumn Header="Size" Width="80" Binding="{Binding SizeText}" />
                <DataGridTextColumn Header="Reason" Width="*" Binding="{Binding Reason}" />
            </DataGrid.Columns>
            <DataGrid.RowDetailsTemplate>
                <DataTemplate>
                    <TextBox Margin="12,4" IsReadOnly="True" BorderThickness="0" Background="Transparent"
                             FontFamily="Consolas" MaxHeight="200" VerticalScrollBarVisibility="Auto"
                             Text="{Binding Details, Mode=OneWay}">
                        <TextBox.Style>
                            <Style TargetType="TextBox">
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding HasDetails}" Value="False">
                                        <Setter Property="Visibility" Value="Collapsed" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </TextBox.Style>
                    </TextBox>
                </DataTemplate>
            </DataGrid.RowDetailsTemplate>
        </DataGrid>
    </DockPanel>
</UserControl>
```

`src/ReBackup.App/Views/HistoryView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace ReBackup.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 7: Update `MainWindow.xaml`**

Read the current file first.

1. Replace the `<StatusBar>` with one that also shows the queue:

```xml
        <StatusBar DockPanel.Dock="Bottom">
            <StatusBarItem DockPanel.Dock="Right">
                <TextBlock Text="{Binding QueueStatus}" />
            </StatusBarItem>
            <StatusBarItem>
                <TextBlock Text="{Binding StatusMessage}" TextTrimming="CharacterEllipsis" />
            </StatusBarItem>
        </StatusBar>
```

2. Replace the plan `<ListBox>` (its `ItemTemplate` included) with:

```xml
                <ListBox ItemsSource="{Binding Plans}" SelectedItem="{Binding SelectedPlan}"
                         ScrollViewer.HorizontalScrollBarVisibility="Disabled">
                    <ListBox.ItemContainerStyle>
                        <Style TargetType="ListBoxItem">
                            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                        </Style>
                    </ListBox.ItemContainerStyle>
                    <ListBox.ItemTemplate>
                        <DataTemplate>
                            <Grid Margin="2,4">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>

                                <StackPanel Grid.Column="0">
                                    <TextBlock Text="{Binding DisplayName}" FontWeight="SemiBold"
                                               TextTrimming="CharacterEllipsis" />
                                    <TextBlock Text="{Binding Source}" Foreground="Gray" FontSize="11"
                                               TextTrimming="CharacterEllipsis" />
                                    <TextBlock Text="{Binding Run.LastRunText}" Foreground="Gray" FontSize="11"
                                               TextTrimming="CharacterEllipsis" />
                                    <StackPanel>
                                        <StackPanel.Style>
                                            <Style TargetType="StackPanel">
                                                <Style.Triggers>
                                                    <DataTrigger Binding="{Binding Run.IsActive}" Value="False">
                                                        <Setter Property="Visibility" Value="Collapsed" />
                                                    </DataTrigger>
                                                </Style.Triggers>
                                            </Style>
                                        </StackPanel.Style>
                                        <ProgressBar Height="6" Margin="0,3,0,1" Maximum="100"
                                                     Value="{Binding Run.ProgressPercent, Mode=OneWay}"
                                                     IsIndeterminate="{Binding Run.IsIndeterminate}" />
                                        <TextBlock Text="{Binding Run.ProgressText}" FontSize="11"
                                                   TextTrimming="CharacterEllipsis" />
                                    </StackPanel>
                                </StackPanel>

                                <Grid Grid.Column="1" VerticalAlignment="Center" Margin="6,0,0,0">
                                    <Button Content="Run now" Padding="8,2"
                                            Command="{Binding DataContext.RunNowCommand, RelativeSource={RelativeSource AncestorType=Window}}"
                                            CommandParameter="{Binding}">
                                        <Button.Style>
                                            <Style TargetType="Button">
                                                <Style.Triggers>
                                                    <DataTrigger Binding="{Binding Run.IsActive}" Value="True">
                                                        <Setter Property="Visibility" Value="Collapsed" />
                                                    </DataTrigger>
                                                </Style.Triggers>
                                            </Style>
                                        </Button.Style>
                                    </Button>
                                    <Button Content="Cancel" Padding="8,2"
                                            Command="{Binding DataContext.CancelRunCommand, RelativeSource={RelativeSource AncestorType=Window}}"
                                            CommandParameter="{Binding}">
                                        <Button.Style>
                                            <Style TargetType="Button">
                                                <Style.Triggers>
                                                    <DataTrigger Binding="{Binding Run.IsActive}" Value="False">
                                                        <Setter Property="Visibility" Value="Collapsed" />
                                                    </DataTrigger>
                                                </Style.Triggers>
                                            </Style>
                                        </Button.Style>
                                    </Button>
                                </Grid>
                            </Grid>
                        </DataTemplate>
                    </ListBox.ItemTemplate>
                </ListBox>
```

3. Add the third tab after the "Ignore &amp; Preview" `TabItem`:

```xml
                    <TabItem Header="History">
                        <views:HistoryView />
                    </TabItem>
```

4. Widen the plan list column: change the first `ColumnDefinition Width="280"` of the main grid to `Width="320"`.

- [ ] **Step 8: Update the spec**

In `docs/superpowers/specs/2026-09-30-rebackup-design.md` §4.5, replace the JSON example block with:

```json
{ "runId": "…", "trigger": "Manual|Scheduled|CatchUp",
  "startUtc": "…", "endUtc": "…", "durationMs": 81234,
  "status": "Completed|CompletedWithWarnings|Full|Error|Canceled",
  "reason": "text for Full/Error",
  "version": "2026_09_30-14_05 Projects",
  "filesCopied": 12034, "bytesCopied": 5368709120,
  "skippedCount": 1,
  "skipped": [ { "path": "…", "reason": "locked by another program" } ],
  "retentionDeleted": ["2026_09_01-02_00 Projects"] }
```

and add this sentence directly below the block:

```
`version` is null when the run did not complete. `skipped` stores at most the first 1000 entries of a run; `skippedCount` counts all of them.
```

- [ ] **Step 9: Build, test, smoke-test**

Run: `dotnet build` (0 warnings, 0 errors), `dotnet test tests/ReBackup.Core.Tests` (all pass), and the startup smoke test.

Manual checklist (for the user; do not click through it as an agent):
1. **Run now** on a saved plan: the row shows a progress bar and text, the status strip on the right shows the plan name and percentage, and the button turns into **Cancel**.
2. When it finishes, `<target>\YYYY_MM_DD-hh_mm <Name>\` exists with the files and `re-manifest.json`; the row says "Last run …: Completed"; the History tab has a new first row.
3. **Run now** on a plan with unsaved edits is refused with a status message.
4. Two plans started one after the other: the second shows "Queued" until the first is done.
5. **Cancel** during a large copy: the run ends as "Canceled", no `.partial` folder is left, and History shows it.
6. A plan whose target has too little space ends as "Aborted: target full" with the reason, before anything is written.
7. A file held open exclusively (for example an open Outlook PST) gives "Completed with warnings"; selecting the row in History lists the skipped file.
8. Ignored entries from the Ignore & Preview tab are not in the version folder.
9. Deleting a plan while its backup runs is refused.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(app): run now with progress, cancel, queue status and history tab" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 7: Tray integration and exiting during a run

**Files:**
- Modify: `src/ReBackup.App/App.xaml.cs`, `src/ReBackup.App/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `MainViewModel.QueueStatus`, `MainViewModel.RunFinished`, `MainViewModel.RunPlan`, `MainViewModel.Plans`, `BackupQueue.IsBusy`, `BackupQueue.CancelAll`, `BackupQueue.WhenIdleAsync`, `TaskbarIcon.ShowNotification`
- Produces: tray tooltip with the queue status, a notification when a run ends, a "Run plan" submenu in the tray menu, and a prompt when exiting while a backup is queued or running. `MainViewModel.RunnablePlans` (`IReadOnlyList<(string Id, string Name, bool CanRun)>`).

- [ ] **Step 1: Expose the plans for the tray menu**

In `src/ReBackup.App/ViewModels/MainViewModel.cs` add:

```csharp
    /// <summary>Saved plans for the tray menu; <c>CanRun</c> is false for unsaved, invalid or already active plans.</summary>
    public IReadOnlyList<(string Id, string Name, bool CanRun)> RunnablePlans =>
        Plans.Where(p => !p.IsNew)
            .Select(p => (p.Id, p.Name, CanRun: !p.IsDirty && p.Errors.Count == 0 && !p.Run.IsActive))
            .ToList();
```

- [ ] **Step 2: Tooltip and notification**

In `src/ReBackup.App/App.xaml.cs` (read the current file first), add `using H.NotifyIcon.Core;` (for `NotificationIcon`); `using ReBackup.App.ViewModels;` and `using ReBackup.Core.Backup;` are already there.

At the end of `CreateTrayIcon`, after `_tray.ForceCreate(...)`, add:

```csharp
        _mainViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.QueueStatus) && _tray is not null)
                _tray.ToolTipText = "ReBackup — " + _mainViewModel.QueueStatus;
        };
        _mainViewModel.RunFinished += ShowRunNotification;
```

Add the method:

```csharp
    private void ShowRunNotification(string planName, RunLogEntry result)
    {
        var duration = RunHistoryRow.FormatDuration(result.DurationMs);
        var (icon, message) = result.Status switch
        {
            RunStatus.Completed => (NotificationIcon.Info, $"Completed in {duration}."),
            RunStatus.CompletedWithWarnings =>
                (NotificationIcon.Warning, $"Completed in {duration}, {result.SkippedCount:N0} entries were skipped."),
            RunStatus.Canceled => (NotificationIcon.Info, "Canceled."),
            RunStatus.Full => (NotificationIcon.Error, $"Aborted, the target is full. {result.Reason}"),
            _ => (NotificationIcon.Error, $"Aborted with an error. {result.Reason}"),
        };
        _tray?.ShowNotification($"Backup \"{planName}\"", message, icon);
    }
```

`NotificationIcon` and `ShowNotification(string title, string message, NotificationIcon icon)` come from H.NotifyIcon 2.3.2 (namespaces `H.NotifyIcon` / `H.NotifyIcon.Core`). If the installed API differs, adapt the call and keep the behaviour (title, message, severity icon), and list the change as a deviation.

- [ ] **Step 3: "Run plan" submenu**

In `CreateTrayIcon`, replace the three `_trayMenu.Items.Add(...)` lines with:

```csharp
        var runMenu = new MenuItem { Header = "Run plan" };
        _trayMenu.Items.Add(CreateTrayMenuItem("Open", ShowMainWindow));
        _trayMenu.Items.Add(runMenu);
        _trayMenu.Items.Add(new Separator());
        _trayMenu.Items.Add(CreateTrayMenuItem("Exit", ExitApp));
        _trayMenu.Opened += (_, _) => FillRunMenu(runMenu);
        FillRunMenu(runMenu);
```

Add the method:

```csharp
    private void FillRunMenu(MenuItem runMenu)
    {
        runMenu.Items.Clear();
        foreach (var (id, name, canRun) in _mainViewModel.RunnablePlans)
        {
            var item = CreateTrayMenuItem(name, () => _mainViewModel.RunPlan(id));
            item.IsEnabled = canRun;
            runMenu.Items.Add(item);
        }
        runMenu.IsEnabled = runMenu.Items.Count > 0;
    }
```

- [ ] **Step 4: Exit, restart and session end while a backup is active**

Add this helper to `App`:

```csharp
    /// <summary>Cancels queued and running backups and waits briefly for the running one to clean up its partial folder.</summary>
    private void StopBackups(TimeSpan wait)
    {
        _queue.CancelAll();
        try
        {
            _queue.WhenIdleAsync().Wait(wait);
        }
        catch (AggregateException)
        {
            // The worker never faults; this only guards the wait itself.
        }
    }
```

In `ExitApp`, after the `ConfirmDiscardUnsaved()` check and before `_exitRequested = true;`, add:

```csharp
        if (_queue.IsBusy)
        {
            if (!_dialogs.Confirm("Backup in progress",
                    "A backup is running or queued.\n\nCancel it and exit ReBackup?"))
                return;
            StopBackups(TimeSpan.FromSeconds(15));
        }
```

In `Restart()`, add `StopBackups(TimeSpan.FromSeconds(15));` directly after `_exitRequested = true;`.

In `OnSessionEnding`, add `StopBackups(TimeSpan.FromSeconds(3));` directly after `_exitRequested = true;`.

In `SettingsViewModel`'s restart confirmation path nothing changes: `ConfirmDiscardUnsaved` is still the confirmation; a running backup is canceled by `Restart()`.

`OnSessionEnding` can run before the configuration is loaded (the startup dialog is open). Guard the three calls there so they tolerate that: use `_queue?.CancelAll()` semantics by checking `if (_queue is not null) StopBackups(...)`, and `_planStore?.Dispose()`.

- [ ] **Step 5: Build, test, smoke-test**

Run: `dotnet build` (0 warnings, 0 errors), `dotnet test tests/ReBackup.Core.Tests` (all pass), and the startup smoke test (also confirm the second-start behaviour from Task 5 still holds).

Manual checklist (for the user; do not click through it as an agent):
1. While a backup runs, hovering the tray icon shows "ReBackup — Backing up "…" — 42 %".
2. When a run ends, a Windows notification shows the plan name and result.
3. Tray menu → **Run plan** lists the saved plans; choosing one starts it. A plan with unsaved edits or a running backup is greyed out.
4. **Exit** during a backup asks whether to cancel it. "No" keeps everything running; "Yes" cancels, removes the `.partial` folder and exits. The run is in the History as "Canceled" at the next start.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(app): tray status, run notifications, run-plan menu and exit during a backup" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

## Carry-forward from Phase 3 execution (final review triage)

Entry conditions for Phase 4 (retention deletes version folders):
- Before retention or preflight freeing deletes a folder, require both `VersionName.TryParse` and a `re-manifest.json` whose `planId` matches the plan. A version is "ours" by name and content, not by name alone.
- Decide what happens to versions stored under a plan's old name after a rename (they are orphans today; a different plan later given that name would inherit them by name).
- Put version enumeration in one place (`VersionCatalog`) and use it for the collision check, retention and leftovers.
- `BackupRunner.DeleteLeftovers` probes for a manifest with `File.Exists`, which returns false on an I/O error; make that check fail-safe (do not delete what cannot be probed).
- Retention after a successful run, `retentionDeleted`, and `freeSpaceByRetention` in the preflight.

Entry conditions for Phase 5 (scheduler enqueues from a timer thread):
- `BackupQueue.Close()`: cancel everything and make `Enqueue` return false; stop the scheduler before it on exit and restart.
- A cheap "last entry" read on `RunLog` (today the whole log is parsed on the UI thread at startup and after each run).
- Validate a plan before enqueuing it, or share `PlanValidator.ValidateName` with the runner (the runner's own checks do not reject names ending in a dot or space, or reserved device names).
- Define which run statuses count as "last run". `StartUtc` is taken before indexing and the same-minute wait, so it can differ from the minute in the version name.
- Scheduled and catch-up runs, "Pause scheduler" in the tray.

Phase 3 UI items from the spec that were not built:
- A status dot per plan row (idle / queued / running / last failed); today the row has text only.
- Current file and ETA in the progress text (`BackupProgress.CurrentFile` is reported by Core but not shown).
- "Starting at hh:mm" while a run waits for the next minute because that minute's folder already exists.

Known limitations:
- Free space on network shares (UNC paths) and on volumes mounted into a folder is not checked before the run; a full target is only detected while copying.
- File symbolic links are copied as the file they point to, and always produce a "changed while it was copied" warning, because the index records the link's own size.
- "Changed while it was copied" also fires for a file that changed between the index scan and its copy, and can fire for files another program holds open.
- Only the manifest is flushed to disk before the rename. A power loss right after a run can leave a version whose data is not yet on disk.
- A run that crashes between writing the manifest and the rename leaves a `.partial` folder with a manifest. It is never cleaned up automatically (it could be another plan's version) and blocks that minute's name.
- The single-instance lock is per Windows session, not per user: the same user logged on twice (console and remote desktop) can run two copies.
- A plan file removed from disk while its backup is running: the run continues, but the row disappears; if the file comes back, the row shows "Run now" although the run is still active.
- Counters `filesCopied` and `bytesCopied` of an aborted run show what was copied before the abort, although that data was deleted.
- Quota-exceeded errors end a run as Error, not Full.

Later / nice to have:
- Anchor the `Backup*/` rule in `.gitignore` (today two exceptions keep the source folders tracked).
- Tests: a known-answer xxHash64 vector, files larger than the 1 MB buffer, the mid-read lock path, access denied, cancel during the same-minute wait, disk full while writing the manifest.
- A second start during the first instance's startup is not signalled; `_exiting` stays set if the exit confirmation throws; the row's action column changes width between "Run now" and "Cancel".
