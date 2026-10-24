# Ignore Relocation and `.backupignore` Visibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `.backupignore` files visible everywhere and move ignore rules between the plan and a folder's
`.backupignore` in both directions, each behind a preview that proves whether the backup scope stays the same.

**Architecture:** A pure core layer in `ReBackup.Core/Ignore` (line translation, proposal + equivalence check over a
completed `SourceIndex`, apply with stale check) that the app — and later the MCP server — calls. The app adds marking
flags to existing row view models, a chip filter and a detail panel in the Ignore tab, and one confirmation dialog for
both directions, wired through `PlanEditorViewModel`.

**Tech Stack:** .NET 9, WPF, CommunityToolkit.Mvvm (`[RelayCommand]`, `ObservableObject`), xUnit + FluentAssertions,
SQLite version index.

**Spec:** `docs/superpowers/specs/2026-10-24-ignore-relocation-design.md`

## Global Constraints

- Core types stay UI-free; nothing in `ReBackup.Core` references WPF or the app.
- File name check is `IgnoreOrigins.NestedFileName` (`.backupignore`), compared `StringComparison.OrdinalIgnoreCase`.
- Relocation works on folder sources only (`plan.Source.IsFileSystem`); otherwise the actions are disabled with a tooltip.
- Integrated lines go to the end of the plan patterns under `# from <D>/.backupignore` (`# from .backupignore` for the
  root); extracted lines are appended to the file under `# from plan`.
- The integrated file goes to the Windows recycle bin, never deleted permanently.
- Equivalence examples are capped at 20.
- Every new text exists in `src/ReBackup.App/Locales/en-US.json` and `de-DE.json` (nested keys, e.g. `ignore.relocation.*`).
- Paths are relative to the source root with forward slashes, as everywhere in `ReBackup.Core.Ignore`.
- Work on a feature branch `feature/ignore-relocation`; merge into `main` with `--no-ff` when done.

## Review Focus

1. **Folder names with glob characters or spaces** (`Projekte/[alt] X`): the prefix must be escaped with
   `IgnorePattern.EscapeLiteral` on integrate and un-escaped/compared literally on extract — pinned in Task 1.
2. **The root `.backupignore`** (`D == ""`): lines are copied unchanged on integrate, and unanchored plan patterns may be
   extracted to the root (they mean the same there) — pinned in Task 1.
3. **`.backupignore` with CRLF, BOM or trailing spaces**: proposal content and hashes must not flag a stale file just
   because of line endings; written files keep the original line ending style — pinned in Task 3.
4. **Unsaved plan edits**: relocating while the editor is dirty would save half-finished edits; the commands are
   disabled with "Save the plan first" — pinned in Task 7.
5. **File or plan changed between preview and apply** (editor in another tool, a sync client): apply fails with the stale
   message and writes nothing — pinned in Task 3.

---

### Task 1: Line translation

**Files:**
- Create: `src/ReBackup.Core/Ignore/IgnoreTranslation.cs`
- Test: `tests/ReBackup.Core.Tests/Ignore/IgnoreTranslationTests.cs`

**Interfaces:**
- Produces:
  - `public enum RejectReason { NotBelowFolder, MatchesOutsideFolder }`
  - `public sealed record RejectedLine(string Line, RejectReason Reason);`
  - `public static class IgnoreTranslation`
    - `static string ToPlanLine(string nestedLine, string directory)` — one line of `directory/.backupignore` → plan line.
    - `static (string? Line, RejectReason? Reason) ToNestedLine(string planLine, string directory)` — plan line → line in
      `directory/.backupignore`, or a reason.

- [ ] **Step 1: Write the failing tests** — `[Theory]` rows taken verbatim from spec section 3:

```csharp
[Theory]
[InlineData("foo/", "P/X", "/P/X/**/foo/")]
[InlineData("*.tmp", "P/X", "/P/X/**/*.tmp")]
[InlineData("name", "P/X", "/P/X/**/name")]
[InlineData("**/x", "P/X", "/P/X/**/x")]
[InlineData("/build/", "P/X", "/P/X/build/")]
[InlineData("a/b", "P/X", "/P/X/a/b")]
[InlineData("!keep.tmp", "P/X", "!/P/X/**/keep.tmp")]
[InlineData("# note", "P/X", "# note")]
[InlineData("", "P/X", "")]
[InlineData("foo/", "", "foo/")]                     // root file: unchanged
[InlineData("x", "P/[alt] X", "/P/\\[alt] X/**/x")] // escaped prefix
public void ToPlanLine_translates(string line, string dir, string expected) =>
    IgnoreTranslation.ToPlanLine(line, dir).Should().Be(expected);

[Theory]
[InlineData("/P/X/**/foo/", "P/X", "foo/")]
[InlineData("/P/X/**/x/y", "P/X", "**/x/y")]
[InlineData("/P/X/build/", "P/X", "/build/")]
[InlineData("!/P/X/**/keep.tmp", "P/X", "!keep.tmp")]
[InlineData("/P/\\[alt] X/**/x", "P/[alt] X", "x")]
[InlineData("*.tmp", "", "*.tmp")]                  // root: unanchored allowed
public void ToNestedLine_translates(string line, string dir, string expected) =>
    IgnoreTranslation.ToNestedLine(line, dir).Line.Should().Be(expected);

[Theory]
[InlineData("*.tmp", "P/X", RejectReason.MatchesOutsideFolder)]
[InlineData("node_modules/", "P/X", RejectReason.MatchesOutsideFolder)]
[InlineData("/Q/build/", "P/X", RejectReason.NotBelowFolder)]
[InlineData("/P/XY/a", "P/X", RejectReason.NotBelowFolder)]   // prefix must end at a slash
public void ToNestedLine_rejects(string line, string dir, RejectReason reason) =>
    IgnoreTranslation.ToNestedLine(line, dir).Reason.Should().Be(reason);
```

Plus `Round_trip_gives_the_original_line` over every `ToPlanLine` row with a non-empty directory.

- [ ] **Step 2: Run** `dotnet test tests/ReBackup.Core.Tests --filter IgnoreTranslationTests` — expected: build fails (type missing).
- [ ] **Step 3: Implement `IgnoreTranslation`.** Classify the glob the way `IgnorePattern.Parse` does (strip `!`,
  trailing `/`; "anchored" = contains a slash after trimming the trailing one). Unanchored or `**/`-led → `/D/**/<glob>`;
  anchored → `/D/<glob without leading slash>`. Reverse: match the escaped prefix `"/" + EscapeLiteral(D) + "/"`
  case-insensitively; leftover `**/name` without further slash → `name`, other `**/…` kept, anything else gets a leading `/`.
- [ ] **Step 4: Run the tests** — expected: PASS.
- [ ] **Step 5: Commit** `feat(core): translate ignore lines between the plan and a nested .backupignore`

### Task 2: Proposal and equivalence check

**Files:**
- Create: `src/ReBackup.Core/Ignore/RelocationProposal.cs`, `src/ReBackup.Core/Ignore/IgnoreRelocation.cs`
- Test: `tests/ReBackup.Core.Tests/Ignore/IgnoreRelocationProposeTests.cs`

**Interfaces:**
- Consumes: `IgnoreTranslation` (Task 1); `IgnoreMatcher.ForPlan`, `SourceIndex` (`IgnoreFiles`, `RootNode`), `IndexNode`.
- Produces:
  - `public enum RelocationKind { Integrate, Extract }`
  - `public sealed record EquivalenceExample(string Path, bool IgnoredBefore, bool IgnoredAfter, string? DecidingPattern);`
  - `public abstract record EquivalenceResult` with `Identical`, `Differs(int Count, IReadOnlyList<EquivalenceExample> Examples)`,
    `NotChecked(string ReasonKey)` (`"ignore.relocation.scanRunning"`, `"ignore.relocation.noScan"`).
  - `RelocationProposal` exactly as in spec section 4 (fields `Kind, Directory, PlanPatternsBefore, PlanPatternsAfter,
    FileContentBefore, FileContentAfter, Rejected, Equivalence, PlanPatternsHash, FileHash`) plus
    `IReadOnlyList<string> MovedLines` (the plan lines removed or added).
  - `public sealed record RelocationContext(IgnoreSettings Settings, IReadOnlyList<string> GlobalDefaults, SourceIndex? Index, string SourceRoot);`
  - `static RelocationProposal IgnoreRelocation.ProposeIntegrate(RelocationContext context, string directory)`
  - `static RelocationProposal IgnoreRelocation.ProposeExtract(RelocationContext context, IReadOnlyList<int> planLineIndexes, string directory)`
  - `static IReadOnlyList<int> IgnoreRelocation.ExtractableLines(IgnoreSettings settings, string directory)`
  - `static string IgnoreRelocation.Hash(IEnumerable<string> lines)` (xxHash64 hex of the lines joined with `\n`)

- [ ] **Step 1: Write the failing tests** (build a `SourceIndex` with the existing test helpers or a small local builder;
  the file content is read from a `TempDir` that mirrors the nested file):
  - `Integrate_moves_lines_to_the_end_of_the_plan_under_a_comment` — `PlanPatternsAfter` ends with
    `"# from P/X/.backupignore", "/P/X/**/foo/"`; `FileContentAfter` is null.
  - `Integrate_of_a_plain_file_is_identical` — `Equivalence` is `Identical`.
  - `Integrate_detects_a_precedence_change` — `P/.backupignore` has `*.log`, `P/X/.backupignore` has `!keep.log`;
    integrating `P/X` moves the negation into the plan, below `P/.backupignore` → `Differs` with `Count == 1`, example
    `P/X/keep.log` (included → ignored).
  - `Extract_detects_a_precedence_change` — plan `/P/X/**/*.log`, `!/P/X/Y/keep.log`; extracting only the first line to
    `P/X` puts `*.log` above the plan's negation → `Differs`, example `P/X/Y/keep.log` (included → ignored).
  - `Extract_appends_under_a_comment_and_removes_plan_lines` — existing file `a/` → `"a/\n# from plan\nfoo/\n"`.
  - `Extract_creates_the_file_when_missing` — `FileContentBefore` null.
  - `Extract_lists_rejected_lines_and_keeps_them_in_the_plan` — `*.tmp` selected → in `Rejected`, still in `PlanPatternsAfter`.
  - `ExtractableLines_preselects_translatable_lines_only`.
  - `Extract_without_HonorNestedFiles_throws_InvalidOperationException`.
  - `Without_an_index_equivalence_is_NotChecked` — `NotChecked("ignore.relocation.noScan")`.
  - `Equivalence_examples_are_capped_at_20`.
- [ ] **Step 2: Run** `dotnet test tests/ReBackup.Core.Tests --filter IgnoreRelocationProposeTests` — expected: build fails.
- [ ] **Step 3: Implement.** Before-matcher = `IgnoreMatcher.ForPlan(settings, defaults, index.IgnoreFiles)`; after-matcher
  = same with the new patterns and the nested list with that one file replaced/removed/added. Walk `index.RootNode`
  depth-first, building each path while descending (do not call `RelativePath` per node), compare
  `Match(path, isDirectory).IsIgnored`, collect the count and the first 20 examples (`DecidingPattern` = after-pattern text).
  File content comes from disk (`Path.Combine(SourceRoot, D, ".backupignore")`), not from the index, so the hash matches
  what `Apply` re-reads.
- [ ] **Step 4: Run the tests** — expected: PASS.
- [ ] **Step 5: Commit** `feat(core): relocation proposals with an equivalence check over the scanned source`

### Task 3: Apply with stale check and rollback

**Files:**
- Modify: `src/ReBackup.Core/Ignore/IgnoreRelocation.cs`
- Create: `src/ReBackup.Core/Ignore/IRecycleBin.cs`, `src/ReBackup.App/Services/ShellRecycleBin.cs`
- Test: `tests/ReBackup.Core.Tests/Ignore/IgnoreRelocationApplyTests.cs`

**Interfaces:**
- Consumes: `RelocationProposal`, `IgnoreRelocation.Hash` (Task 2).
- Produces:
  - `public interface IRecycleBin { void Recycle(string path); }`
  - `public sealed class RelocationStaleException : Exception` (message key `ignore.relocation.stale`)
  - `static void IgnoreRelocation.Apply(RelocationProposal proposal, string sourceRoot, IReadOnlyList<string> currentPlanPatterns, Action<IReadOnlyList<string>> savePlanPatterns, IRecycleBin recycleBin)`
  - App: `ShellRecycleBin : IRecycleBin` — `SHFileOperationW` with `FO_DELETE` and
    `FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI`; throws `IOException` on a non-zero result.

- [ ] **Step 1: Write the failing tests:**
  - `Apply_writes_the_file_and_saves_the_patterns`.
  - `Apply_integrate_recycles_the_file` — fake bin records the path.
  - `Apply_fails_when_the_plan_changed` and `Apply_fails_when_the_file_changed` — `RelocationStaleException`, file
    untouched, `savePlanPatterns` not called.
  - `Apply_restores_the_file_when_saving_the_plan_fails` — saver throws; file content equals `FileContentBefore`
    (for integrate: recycle happens after a successful save, so the file is still there).
  - `Crlf_file_is_not_stale_and_keeps_crlf` — on-disk `a/\r\n`; proposal then apply; written file uses `\r\n`.
  - `Bom_is_ignored_for_the_hash`.
- [ ] **Step 2: Run** `dotnet test tests/ReBackup.Core.Tests --filter IgnoreRelocationApplyTests` — expected: build fails.
- [ ] **Step 3: Implement.** Order: stale check → write file atomically (temp file in the same folder + `File.Move(…, overwrite: true)`)
  for extract → `savePlanPatterns` → for integrate `recycleBin.Recycle` last. On a save failure after the write,
  restore `FileContentBefore` (or delete the newly created file) and rethrow. Hash normalises `\r\n` → `\n` and strips a BOM;
  the writer reuses the line ending found in `FileContentBefore` (`\n` when new).
- [ ] **Step 4: Run the tests** — expected: PASS.
- [ ] **Step 5: Commit** `feat(core): apply a relocation with a stale check and rollback`

### Task 4: Hits per ignore line

**Files:**
- Create: `src/ReBackup.Core/Ignore/IgnoreLineStats.cs`
- Test: `tests/ReBackup.Core.Tests/Ignore/IgnoreLineStatsTests.cs`

**Interfaces:**
- Consumes: `IndexEvaluator.Evaluate` result (`EvaluatedNode`: `Pattern`, `IgnoredByParent`, `TotalSize`, `TotalFiles`).
- Produces:
  - `public readonly record struct LineHits(int LineNumber, string Text, int Entries, long Bytes);`
  - `static IReadOnlyList<LineHits> IgnoreLineStats.ForOrigin(EvaluatedNode root, string origin, IReadOnlyList<string> lines)`
    — one row per non-blank, non-comment line (1-based line numbers), zero hits included.

- [ ] **Step 1: Write the failing tests:** `Counts_entries_decided_by_each_line` (`foo/` deciding a folder of 3 files /
  300 bytes → `Entries == 1, Bytes == 300`), `Entries_ignored_only_by_a_parent_are_not_counted_again`,
  `Unused_line_has_zero_hits`, `Other_origins_are_ignored`.
- [ ] **Step 2: Run** — expected: build fails.
- [ ] **Step 3: Implement** a walk over `EvaluatedNode` grouping by `Pattern` (reference) where `!IgnoredByParent` and
  `Pattern.Origin == origin`; map patterns back to lines by order of appearance among parsed lines.
- [ ] **Step 4: Run** — expected: PASS.
- [ ] **Step 5: Commit** `feat(core): hits per line of an ignore file`

### Task 5: Marking in all overviews

**Files:**
- Modify: `src/ReBackup.App/ViewModels/PreviewRowViewModel.cs`, `src/ReBackup.App/ViewModels/PreviewTreeViewModel.cs`,
  `src/ReBackup.App/ViewModels/IgnorePreviewViewModel.cs`, `src/ReBackup.Core/Indexing/LiveScan.cs`,
  `src/ReBackup.App/ViewModels/VersionTreeNode.cs`, `src/ReBackup.App/ViewModels/VersionListItems.cs`
  (`SearchHitRow`, `FileHistoryRow`), `src/ReBackup.Core/Versions/VersionIndex.Queries.cs`,
  `src/ReBackup.App/Views/IgnorePreviewView.xaml`, `src/ReBackup.App/Views/VersionsView.xaml`,
  `src/ReBackup.Shared.Wpf/Controls/TreemapControl.cs`, the restore selection template wherever it shows rows
- Test: `tests/ReBackup.App.Tests/IgnoreFileMarkingTests.cs`, `tests/ReBackup.Core.Tests/Versions/IgnoreFileFoldersTests.cs`

**Interfaces:**
- Produces:
  - `public static bool IgnoreOrigins.IsNestedFileName(string name)` (Core, `IgnoreMatcher.cs`).
  - `bool IsIgnoreFile` and `bool ContainsIgnoreFile` on `PreviewRowViewModel` and `VersionTreeNode`; `bool IsIgnoreFile`
    on `SearchHitRow` and `FileHistoryRow`.
  - `IReadOnlySet<string> PreviewTreeViewModel.IgnoreFileDirectories` (set by the Ignore VM from the completed
    `SourceIndex.IgnoreFiles`, and during a scan from the new `IReadOnlyList<string> LiveScan.IgnoreFileDirectories` snapshot).
  - `IReadOnlySet<long> VersionIndex.FoldersWithIgnoreFile(long versionId)` — parent path ids of `.backupignore` entries
    present in the version (`name = '.backupignore' COLLATE NOCASE`).
  - `ITreemapEntry` tiles named `.backupignore` get `Paint.IgnoreFile` (theme accent brush) in `TreemapControl.BrushFor`.
  - XAML: a filter-glyph icon in the accent colour + `FontWeight="SemiBold"` on `IsIgnoreFile`; a small badge dot on the
    folder icon on `ContainsIgnoreFile`.

- [ ] **Step 1: Write the failing tests:** `Preview_row_of_a_backupignore_is_marked` (also `.BackupIgnore`),
  `Folder_with_a_backupignore_is_marked_while_scanning_and_after`, `Version_tree_marks_file_and_folder`,
  `Search_and_history_rows_mark_the_file`, `FoldersWithIgnoreFile_returns_parent_ids_of_the_version_only`.
- [ ] **Step 2: Run** `dotnet test tests/ReBackup.App.Tests --filter IgnoreFileMarkingTests` and the core test — expected: build fails.
- [ ] **Step 3: Implement** flags, the `LiveScan` snapshot (copy under the existing lock), the index query, templates and
  treemap brush.
- [ ] **Step 4: Run** the two test classes plus the full App and Core suites — expected: PASS.
- [ ] **Step 5: Check visually**: run the app on a source with nested `.backupignore` files, dark and light theme, Ignore
  tab tree + treemap and Versions tab; take screenshots.
- [ ] **Step 6: Commit** `feat(app): mark .backupignore files and the folders holding them in every overview`

### Task 6: Chip filter and detail panel

**Files:**
- Modify: `src/ReBackup.App/ViewModels/IgnorePreviewViewModel.cs` (+ new partial `IgnorePreviewViewModel.IgnoreFiles.cs`),
  `src/ReBackup.App/Views/IgnorePreviewView.xaml`, both locale files
- Test: `tests/ReBackup.App.Tests/IgnoreFilePanelTests.cs`

**Interfaces:**
- Consumes: `IgnoreLineStats.ForOrigin` (Task 4), `IgnoreOrigins.IsNestedFileName` (Task 5).
- Produces on `IgnorePreviewViewModel`:
  - `public SourceIndex? CompletedIndex` (null while indexing or released) — also used by Task 7.
  - `bool ShowIgnoreFilesOnly` (chip), `int IgnoreFileCount`, `string IgnoreFilesChipText` (`ignore.files.chip` = ".backupignore ({count})").
  - `IgnoreFileDetail? SelectedIgnoreFile` — `record IgnoreFileDetail(string Directory, string Path, IReadOnlyList<IgnoreFileLine> Lines)`,
    `record IgnoreFileLine(int Number, string Text, int? Entries, string BytesText, bool IsUnused)` (comments/blank: `Entries` null).
  - `IRelayCommand OpenIgnoreFileInEditorCommand` (shell-open the file path; folder sources only).
  - Keys: `ignore.files.chip`, `ignore.files.panelTitle`, `ignore.files.openInEditor`, `ignore.files.integrate`,
    `ignore.files.hits` ("{entries} entries · {bytes}"), `ignore.files.unused` ("no hits").

- [ ] **Step 1: Write the failing tests:** `Chip_counts_and_lists_only_backupignore_files`,
  `Selecting_a_backupignore_fills_the_panel_with_hits`, `Unused_lines_are_flagged`, `Panel_closes_when_selection_moves_away`,
  `Open_in_editor_is_disabled_for_s3_sources`.
- [ ] **Step 2: Run** — expected: build fails.
- [ ] **Step 3: Implement.** The chip swaps the tree's roots for a flat list of the `.backupignore` rows (sorted by path);
  the panel is a right-hand column in `IgnorePreviewView.xaml` visible when `SelectedIgnoreFile` is set; hits come from
  evaluating `CompletedIndex` with the current matcher (reuse the evaluation the VM already holds — do not evaluate twice).
- [ ] **Step 4: Run** — expected: PASS; full App suite PASS.
- [ ] **Step 5: Commit** `feat(app): .backupignore chip and detail panel with hits per line in the Ignore tab`

### Task 7: Relocation dialog and commands

**Files:**
- Create: `src/ReBackup.App/ViewModels/RelocationDialogViewModel.cs`, `src/ReBackup.App/Views/RelocationDialog.xaml(.cs)`
- Modify: `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs` (+ new partial `PlanEditorViewModel.Relocation.cs`),
  `src/ReBackup.App/Services/IDialogService.cs`, `src/ReBackup.App/Services/WpfDialogService.cs`,
  `src/ReBackup.App/Views/IgnorePreviewView.xaml(.cs)`, both locale files, the App composition root (`App.xaml.cs`)
- Test: `tests/ReBackup.App.Tests/RelocationTests.cs`

**Interfaces:**
- Consumes: `IgnoreRelocation.ProposeIntegrate/ProposeExtract/ExtractableLines/Apply`, `IRecycleBin`,
  `IgnorePreviewViewModel.CompletedIndex`, `PlanEditorViewModel.CurrentIgnoreSettings()`, `IsDirty`, the editor's existing save path.
- Produces:
  - `bool IDialogService.ConfirmRelocation(RelocationDialogViewModel model)` (true = apply).
  - `RelocationDialogViewModel(RelocationProposal proposal)`: `FileDiff` / `PatternDiff` (lines with `Added/Removed/Same`),
    `Rejected`, `EquivalenceState` (`Identical | Differs | Waiting`), `Examples`, `bool ApplyAnyway` (checkbox), `bool CanApply`
    (`Identical`, or `Differs && ApplyAnyway`; never `Waiting`), `string ApplyText` (`ignore.relocation.apply` / `applyAnyway`).
  - On `PlanEditorViewModel`: `IRelayCommand IntegrateIgnoreFileCommand` (selected preview row is a `.backupignore`),
    `IRelayCommand<IReadOnlyList<int>> ExtractToFolderCommand` (parameter = selected pattern line indexes from the TextBox;
    empty → `ExtractableLines`), `string? RelocationDisabledReason` (`ignore.relocation.saveFirst`, `ignore.relocation.notFolder`,
    `ignore.relocation.honorOff`), constructor dependency `IRecycleBin`.
  - Code-behind of `IgnorePreviewView`: maps the patterns TextBox `SelectionStart/SelectionLength` to the touched line indexes.
  - Context menu items `ignore.tree.integrate` ("Integrate into plan…") and `ignore.tree.extract`
    ("Move selected rules to {folder}/.backupignore…"); keys also `ignore.relocation.title`, `identical`
    ("Backup scope unchanged"), `differs` ("{count} entries change"), `waiting` ("Waiting for the scan…"),
    `existingOnly` ("Checked against the files that exist now."), `rejected.notBelowFolder`, `rejected.matchesOutsideFolder`,
    `stale` ("The plan or the file changed since the preview. Nothing was written."), `applyAnyway`, `applyAnywayCheck`.

- [ ] **Step 1: Write the failing tests** (fake dialog service returns true/false and captures the model; fake bin):
  - `Integrate_applies_saves_the_plan_and_reevaluates` — patterns text updated, `IsDirty == false`, file recycled,
    `Preview.RequestReevaluate` observed (no new scan started).
  - `Extract_with_selected_lines_moves_them` and `Extract_without_selection_preselects_extractable_lines`.
  - `Commands_are_disabled_while_dirty` — reason `ignore.relocation.saveFirst`.
  - `Commands_are_disabled_for_s3_sources` and `Extract_is_disabled_when_nested_files_are_off`.
  - `Dialog_cannot_apply_while_waiting_for_the_scan` and `Differs_needs_apply_anyway`.
  - `Stale_apply_shows_the_stale_error_and_changes_nothing`.
  - `Cancel_changes_nothing`.
- [ ] **Step 2: Run** `dotnet test tests/ReBackup.App.Tests --filter RelocationTests` — expected: build fails.
- [ ] **Step 3: Implement.** Apply passes `currentPlanPatterns = CurrentIgnoreSettings().Patterns` and a saver that sets
  `IgnorePatternsText` and runs the editor's normal save; on `RelocationStaleException` show
  `ShowError(ignore.relocation.title, ignore.relocation.stale)`. The dialog renders both diffs side by side (monospace,
  added green / removed red), the equivalence banner and the `existingOnly` line; `Apply anyway` appears only in the red state.
  Register `ShellRecycleBin` in `App.xaml.cs`.
- [ ] **Step 4: Run** — expected: PASS; full App and Core suites PASS.
- [ ] **Step 5: Check by hand** on a real folder source: integrate a `.backupignore` (file in recycle bin, scope unchanged),
  extract two plan lines into a new `.backupignore`, provoke a precedence difference and see the red state; dark/light, EN/DE.
- [ ] **Step 6: Commit** `feat(app): integrate a .backupignore into the plan or move plan rules into one, with a preview`

### Task 8: Docs and finish

**Files:**
- Modify: `README.md` (Ignore & Preview section: marking, panel, relocation), `docs/images/` if a screenshot changes

- [ ] **Step 1:** Add three bullets to "Ignore & Preview" in `README.md` describing marking, the panel and relocation with
  the equivalence check.
- [ ] **Step 2:** Run the full test suite `dotnet test ReBackup.sln` — expected: all PASS.
- [ ] **Step 3: Commit** `docs: .backupignore marking and relocation in the README`, then merge `feature/ignore-relocation`
  into `main` with `--no-ff`.
