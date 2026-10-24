# Ignore Relocation and `.backupignore` Visibility — Design

Date: 2026-10-24
Status: Approved (brainstorming), pending spec review
Prerequisite of the MCP control design (2026-10-24-mcp-control-design.md), which calls the same core operation.

## 1. Purpose

`.backupignore` files are easy to miss today: they look like any other file in every tree, and moving rules between the
plan and a project folder means editing two places by hand without knowing whether the backup still covers the same
files. This design adds:

- **Visibility**: `.backupignore` files (and folders containing one) stand out in every overview; clicking one shows
  its content and what each line currently excludes.
- **Relocation in both directions**, each with a preview and an explicit confirmation:
  - *Integrate*: dissolve a `.backupignore` into the plan's patterns (paths prefixed), then send the file to the
    Windows recycle bin.
  - *Extract*: move selected plan patterns into the `.backupignore` of a folder (prefix removed).
- **Proof of equivalence**: before anything is written, the preview states whether the set of backed-up entries stays
  exactly the same for the current source tree.

## 2. Decisions

| Topic | Decision |
|---|---|
| Where the logic lives | `ReBackup.Core/Ignore/IgnoreRelocation.cs`, pure and UI-free; the app and the MCP server both call it |
| Two-step | `Propose…` returns a `RelocationProposal`; only `Apply(proposal)` writes |
| Equivalence | Old and new matcher compared with `IgnoreMatcher.Match` over every entry of the current completed scan |
| Untranslatable lines | Listed in the proposal, never silently changed; extraction refuses them |
| Integrated file | Sent to the Windows recycle bin (restorable), not renamed in place |
| Placement | Integrated lines go to the end of the plan patterns under a `# from <path>/.backupignore` comment; extracted lines are appended to the file under `# from plan` |
| Sources | Folder sources only; on S3/Drive sources the actions are disabled with a tooltip |
| Concurrency | `Apply` re-checks a hash of the plan patterns and of the file content taken at proposal time; on change it fails with "changed since the preview" |
| Out of scope | Editing `.backupignore` in the app (an "Open in editor" button is enough), relocating global defaults, writing to S3/Drive sources |

## 3. Translation rules

Notation: `D` is the folder of the `.backupignore`, relative to the source root, without leading or trailing slash.

### 3.1 Integrate (`.backupignore` line in `D` → plan pattern)

| Line in `D/.backupignore` | Meaning (gitignore) | Plan pattern |
|---|---|---|
| `foo/`, `*.tmp`, `name` (no slash except a trailing one) | any depth below `D` | `/D/**/foo/`, `/D/**/*.tmp`, `/D/**/name` |
| `**/x` | any depth below `D` | `/D/**/x` |
| `/build/`, `a/b` (leading or middle slash) | relative to `D` | `/D/build/`, `/D/a/b` |
| `!line` | negation of the above | `!` + translation of `line` |
| blank, `# comment` | — | copied as is (keeps the author's grouping) |

Special characters in `D` are escaped with `IgnorePattern.EscapeLiteral`.

### 3.2 Extract (plan pattern → `.backupignore` line in `D`)

| Plan pattern | Line in `D/.backupignore` |
|---|---|
| `/D/**/x` where `x` has no slash (except trailing) | `x` |
| `/D/**/x/y` | `**/x/y` |
| `/D/rest` (anchored, below `D`) | `/rest` |
| `!` + any of the above | `!` + translation |
| unanchored without slash (`*.tmp`, `node_modules/`) | refused: it also matches outside `D` |
| anchored outside `D` | refused: not below `D` |

When nothing is selected, the extract dialog pre-selects every plan pattern that translates under the rules above.

### 3.3 Precedence

Plan patterns rank below every `.backupignore`, and deeper files rank above shallower ones. Moving a line therefore
also moves its rank: an integrated line now ranks below other `.backupignore` files that previously lost to it, and an
extracted line now ranks above plan lines that previously beat it. The translation tables above are syntactic; the
equivalence check (section 4) is what proves a proposal correct for the actual tree.

## 4. Proposal and equivalence

```csharp
public sealed record RelocationProposal(
    RelocationKind Kind,                       // Integrate | Extract
    string Directory,                          // D
    IReadOnlyList<string> PlanPatternsBefore,
    IReadOnlyList<string> PlanPatternsAfter,
    string? FileContentBefore,                 // null when the file does not exist yet
    string? FileContentAfter,                  // null when the file is removed (integrate)
    IReadOnlyList<RejectedLine> Rejected,      // line + reason key
    EquivalenceResult Equivalence,             // Identical | Differs(count, examples) | NotChecked(reason)
    string PlanPatternsHash, string? FileHash);
```

- The equivalence check builds the old matcher (current settings and nested files) and the new matcher (patterns and
  nested files after the change) and compares `Match(path, isDirectory).IsIgnored` for every entry of the completed
  scan. It reports the number of differing entries and up to 20 examples (path, before → after, deciding pattern).
- It requires a completed scan of the source. While the scan is still running, the proposal carries
  `NotChecked("scan running")` and the dialog's Apply button stays disabled until the scan finishes and the proposal is
  recomputed.
- The check covers only entries that exist now; the dialog says so in one line.
- With `HonorNestedFiles` off, extraction is disabled (the file would have no effect) and integration shows the
  resulting difference like any other.

`Apply(proposal)`:

1. Re-read the plan and the file; compare hashes; on mismatch fail with `ignore.relocation.stale`.
2. Write the new `.backupignore` atomically (temp file + replace) or send it to the recycle bin
   (`Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., RecycleOption.SendToRecycleBin)`).
3. Save the plan through `PlanStore`.
4. If step 3 fails after step 2 succeeded, restore the file content from the proposal and report the error.

## 5. UI

### 5.1 Marking

A `.backupignore` row is recognised by name (`IgnoreOrigins.NestedFileName`, case-insensitive) and gets:

- a filter icon in the accent colour and a bold name, in the Ignore tab (tree and compare), the Versions tab (tree,
  version comparison, file history) and the restore selection;
- a fixed, distinct tile colour in the treemap instead of the extension colour.

A folder containing one gets a small badge on its folder icon, so collapsed trees still show where they are. The row
view models get one property (`IsIgnoreFile` / `ContainsIgnoreFile`); the templates read it.

The Ignore tab gets a chip **".backupignore (n)"** next to *In backup / Total* that switches the tree to a flat list of
all `.backupignore` files of the source.

### 5.2 Detail panel

Selecting a `.backupignore` in the Ignore tab opens a panel on the right of the tree:

- the content with line numbers;
- per pattern line: entries and bytes it currently decides (from `Match(...).Pattern` over the scan); lines with zero
  hits are greyed out;
- buttons *Open in editor* and *Integrate into plan…*.

### 5.3 Context menus

- On a `.backupignore`: **Integrate into plan…**
- On a folder: **Move selected rules to `<folder>/.backupignore`…** (uses the rows selected in the plan's pattern
  list; with no selection, the dialog pre-selects as in 3.2).

Both are disabled for S3/Drive sources, with a tooltip.

### 5.4 Confirmation dialog

One dialog for both directions, built from the `RelocationProposal`:

- file diff (before/after) and plan patterns before/after, changed lines highlighted;
- rejected lines in yellow with their reason;
- equivalence: green *"Backup scope unchanged"*, red *"n entries change"* with the example list, or grey *"Waiting for
  the scan…"*;
- buttons *Apply* / *Cancel*; in the red case *Apply* becomes *Apply anyway* and needs an extra checkbox.

After applying, the cached scan is re-evaluated without scanning again (the same path as editing patterns today).

All new texts in English and German.

## 6. Testing

- `IgnoreRelocationTests`: every row of both translation tables; escaping of special characters in `D`; round trip
  (extract then integrate gives the original plan modulo comments); refused patterns.
- Equivalence: identical case; precedence change caught (negation in a deeper file vs. plan line); nested files at
  several depths; `HonorNestedFiles` off; scan not complete → `NotChecked`.
- `Apply`: stale plan, stale file, write failure rolls back the file, recycle-bin call behind an interface (faked in
  tests).
- View models: marking flags on preview, version and compare rows; chip filter; dialog view model states (green, red,
  waiting, rejected lines).
