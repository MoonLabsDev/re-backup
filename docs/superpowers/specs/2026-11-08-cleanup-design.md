# Cleanup Tab — Design

Date: 2026-11-08
Status: Draft

## 1. Purpose

Rules added to a plan only affect the next backups. Versions made before keep everything the new rules now exclude
(`target/`, `dist/`, build output, downloads), and on the NAS that space stays used until retention removes them.

This design adds a **Cleanup** tab: the user checks one or more backups of a plan in a grid, **scans** to see how much
the plan's *current* ignore rules would remove and free in them, and then **runs** the cleanup, which removes those
files from the backups and shortens their manifests. Afterwards each cleaned version looks as if it had been made with
today's rules.

## 2. Decisions

| Topic | Decision |
|---|---|
| Rules | What the next backup would use: the saved plan patterns + the global defaults (if the plan uses them) + the `.backupignore` files of the current source tree (if the plan honors them) |
| Unsaved rules | Not used; Scan is disabled with "Save first" while the plan has unsaved changes |
| Plan types | Both. Full plans: files are deleted from the version folders. Pool plans: manifests are shortened, then one pool collect |
| "Frees" for pool plans | Only content no remaining version references — checked or not — shown as "≈ frees X in the pool" (total only) |
| Scan source | The local version index plus the rules; no access to the target |
| Run source | Each manifest is re-read from the target, the rules re-applied, and written back with a stamp guard |
| Changed since scan | The version is skipped ("Changed since the scan"); a new scan is needed for it |
| Order per version | Manifest first, files afterwards — an interruption only leaves unlisted files behind, never a listed file that is missing |
| Which versions | Only managed, intact, indexed versions of a saved plan; others are greyed out with the reason as tooltip |
| Gating | Like deletion: no cleanup while a backup, retry, copy, delete or resume runs, and none of those while a cleanup runs |
| Scan detail | Per version (files, bytes) and by rule (rule, origin, files, bytes, largest folder) |
| Out of scope | Unsaved or ad-hoc rules; rules that would *add* files; cleaning unmanaged/foreign versions; MCP tool for cleanup |

## 3. UI

### 3.1 Tab

A new `MainTab.Cleanup` after **Versions**, wired in the three places of `MainWindow.xaml` (rail, header, content),
shown for saved plans only.

### 3.2 Grid

Newest first. Columns:

| Column | Content |
|---|---|
| ☐ | Checkbox; header checkbox checks/unchecks all selectable rows |
| Date | Version date |
| Size | Version size |
| Files | File count |
| Removes | After a scan: files to remove |
| Frees | After a scan: bytes (full plans); pool plans show "—" per row and the total below |
| Status | After a run: ✓, skipped (reason) or failed (error) |

Rows of versions that are not managed, damaged or not indexed have no checkbox, are greyed out and explain why in a
tooltip.

### 3.3 Buttons and progress

- **Scan** — enabled with at least one checked row and no unsaved plan changes ("Save first" otherwise).
- **Clean up…** — enabled only after a scan that found something, while the scan is still current (§4.4).
- During the run: progress bar, current version, **Cancel**.

### 3.4 Below the grid

- Totals: versions, files to remove, bytes freed (full) or "≈ frees X in the pool" (pool).
- **By rule** list: rule, origin (`global`, `plan`, or the `.backupignore` path), files, bytes, largest folder as an
  example.

All texts go through `en-US.json` / `de-DE.json`.

## 4. Scan

### 4.1 Rules

`IgnoreMatcher.ForPlan(settings, globalDefaults, nestedFiles)` with the saved plan settings and the `.backupignore`
files taken from the shared source scan cache (the one used by the Ignore tab and MCP). If no scan is cached, the
Cleanup tab starts one and shows its progress ("Scanning source…").

### 4.2 Per version

For each checked version, the index tree of that version is walked and the matcher applied:

- A folder that is ignored counts as a whole from the folder totals in the index (`FilesBelow` and the stored folder
  sizes) without walking its files.
- A file that is ignored counts with its size.
- The deciding rule of each hit is recorded for the by-rule list (§4.3).

Result per version: files and bytes to remove.

### 4.3 By rule

Hits are summed per (rule, origin) across all checked versions. For each rule the largest removed folder (or file) is
kept as an example.

### 4.4 Pool estimate

For pool plans the freed amount is reckoned from the hashes in the index: a hash counts as freed when every version of
the plan that references it is checked and none of its references in those versions survive the rules. It is counted
once with its stored size. Duplicates inside one version count once.

### 4.5 Staying current

The scan result keeps the rule fingerprint (plan patterns, global defaults, the `.backupignore` contents used) and each
version's manifest stamp. It becomes stale — **Clean up…** disabled, "Scan again" shown — when the rules are saved,
the selection changes, or a version's stamp in the index changes (retry, sync from another machine).

## 5. Run

### 5.1 Start

**Clean up…** asks for confirmation: number of versions, files, bytes (≈ for pool plans), "This cannot be undone."
The run is a job like a deletion (`RunAsJobAsync` / `TrackJob`), shown in the status bar. Pool plans take the target
lock (`TargetLock`).

Before anything is written: `VersionCatalog` ownership check and `StoreGuard.CheckAsync`. A failure stops the run
before the first version.

### 5.2 Per version, oldest first

1. Read `re-manifest.json` from the target and keep its stamp. If the stamp differs from the one the scan saw, skip
   the version: "Changed since the scan".
2. Apply the rules to the file list. Compute the paths to remove.
3. **Full plans:** write `re-pruning.json` into the version folder with the list of paths to remove (§6.2).
4. Write the shortened manifest: `Files` filtered, header (`FileCount`, `TotalBytes`) recomputed, `Directories`
   without the folders the rules ignore (a folder that only lost some files stays: the next backup lists it too). Written with `CreateOptions(Overwrite: true, Durable: true,
   ExpectedStamp: stamp)`. A `StorageConflictException` skips the version (and removes the marker); nothing is deleted.
5. **Full plans:** delete the listed files, then the ignored folders once they are empty; a file already missing is fine. Remove
   `re-pruning.json`.
6. Update the index with `VersionIndex.Add` so Versions, Ignore and Retention see the new state.

### 5.3 Pool collect

After the last version, if at least one manifest was rewritten: one `PoolCollector.CollectAsync`. It deletes only
blobs no manifest references, so content any other version uses stays.

### 5.4 Result

Per version: ✓, skipped (reason) or failed (error). Totals: files removed and bytes freed, measured (for pool plans the
collect's freed bytes). The grid reloads, the scan result is dropped.

### 5.5 Cancel

The current version finishes through step 4 (and step 5 for full plans, since the marker would resume it anyway); then
the run stops. Finished versions stay clean. The pool collect runs if at least one manifest was rewritten.

## 6. Safety and interruption

### 6.1 Consistency

The manifest never lists a file that is gone: it is shortened before anything is deleted. Restore, `PlanCopier` and
`PoolVerifier` stay correct at every point.

### 6.2 Interrupted full plan

`re-pruning.json` (in the version folder) holds the paths to delete. On the next start `DeletionResumer` finds it and
deletes only those listed paths **the manifest no longer lists**; then it removes the marker. A marker whose manifest
was not rewritten therefore deletes nothing and is just removed.

`LeftoverCleaner` and `PoolCollector.HasFoldersInTransitAsync` know the marker: a folder with `re-pruning.json` is
neither treated as a broken leftover nor as a folder in transit.

### 6.3 Interrupted pool plan

Once manifests are rewritten only the collect is missing; the next collect (after a backup or deletion) catches up. No
marker is needed.

### 6.4 Newest version

It stays the base of the next backup; its manifest is only shorter, and the next backup re-checks the source against the
same rules anyway.

### 6.5 Quitting during a run

The usual question ("A cleanup is running. Quit anyway?"). Quitting anyway leaves the marker to finish the work (§6.2).

## 7. Testing

### 7.1 Core (xUnit, in-memory store as in the deletion tests)

- Scan: ignored folder counted from folder totals; file rules; `.backupignore` in a subfolder; global defaults on/off.
- Pool estimate: content used by another version (checked or not) is not counted; duplicates in a version count once.
- Full run: manifest (header, `Directories`) shortened, files and empty folders deleted, index updated.
- Pool run: manifests rewritten; collect deletes only unreferenced blobs; a blob shared with an unchecked version stays.
- Stamp changed since the scan → skipped, nothing deleted; conflict on write → the same.
- Interruption after the manifest write: marker present → `DeletionResumer` finishes; marker without a rewritten
  manifest deletes nothing.
- `LeftoverCleaner` / `HasFoldersInTransitAsync` respect `re-pruning.json`.
- Cancel: current version finishes, then the run stops.

### 7.2 App (ViewModel tests)

- Checkbox and "all" behaviour; greyed-out versions cannot be checked.
- Scan disabled with unsaved rules ("Save first") or nothing checked.
- **Clean up…** disabled after the rules, the selection or a version's stamp change.
- Gating both ways with backup, retry, copy and delete.
- By-rule list totals and largest folder.

### 7.3 Manual

One real plan on the NAS: compare what the scan predicted with what the run freed.
