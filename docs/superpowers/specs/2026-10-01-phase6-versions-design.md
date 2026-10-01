# ReBackup — Phase 6: Versions (browse, compare, history, restore) with a local index

Date: 2026-10-01
Status: Approved (decisions by the user in chat, 2026-10-01)
Builds on: `2026-09-30-rebackup-design.md` §4.3–4.4, §5 (`VersionCatalog`, `VersionComparer`, `Restorer`), §10.2 tab 6, §11, §12;
UI style: `2026-10-01-dark-pro-redesign-design.md` (Dark/Light theme, cards, icons).

## 1. Decisions

| Topic | Decision |
|---|---|
| Which folders the tab lists | The plan's own versions **and** "not managed" folders (no manifest, renamed, foreign, unreadable — `VersionCatalog` ownership). Not managed ones are greyed with the reason; they can be browsed (scan) and restored from, never deleted. |
| Restoring a folder | Copies the version's files back; **never deletes** anything at the destination. Conflicts (a file exists at the destination) are asked once per operation: Overwrite / Skip / Keep both / Cancel. |
| Loading | On demand. A **local SQLite index per plan** makes history, search and compare fast; the JSON manifest in the version folder stays the source of truth. |
| Index location | `%LOCALAPPDATA%\ReBackup\index\<planId>.db` (machine-local cache, not in the roaming config folder). Deleting it is safe: it is rebuilt. |

## 2. The version index (Core, `ReBackup.Core.Versions`)

Package: `Microsoft.Data.Sqlite` (Core project).

### 2.1 Schema

```sql
PRAGMA journal_mode = WAL;
CREATE TABLE meta     (key TEXT PRIMARY KEY, value TEXT NOT NULL);           -- schema_version = 1
CREATE TABLE versions (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                       local_time TEXT NOT NULL, ownership TEXT NOT NULL, source TEXT,
                       origin TEXT NOT NULL,              -- 'manifest' | 'scan'
                       stamp TEXT NOT NULL,               -- change detector: manifest size+mtime, or folder mtime+count for scans
                       file_count INTEGER NOT NULL, total_bytes INTEGER NOT NULL, indexed_utc TEXT NOT NULL);
CREATE TABLE paths    (id INTEGER PRIMARY KEY, parent_id INTEGER, name TEXT NOT NULL,
                       path TEXT NOT NULL UNIQUE COLLATE NOCASE, is_dir INTEGER NOT NULL);
CREATE INDEX paths_parent ON paths(parent_id);
CREATE TABLE files    (version_id INTEGER NOT NULL, path_id INTEGER NOT NULL, size INTEGER NOT NULL,
                       mtime_ticks INTEGER NOT NULL, hash INTEGER,          -- xxh64 as signed 64-bit; NULL = no hash (scan)
                       PRIMARY KEY (version_id, path_id)) WITHOUT ROWID;
CREATE INDEX files_path ON files(path_id, version_id);
CREATE TABLE dirs     (version_id INTEGER NOT NULL, path_id INTEGER NOT NULL, size INTEGER NOT NULL, files INTEGER NOT NULL,
                       PRIMARY KEY (version_id, path_id)) WITHOUT ROWID;    -- rolled-up totals per folder per version
```

Paths are relative with forward slashes (as in manifests), "" = root (id of the root row is fixed by insertion). Paths are
shared by all versions (interned), so 30 versions × 300 k files store each path once. A schema version mismatch or a corrupt
file → the db is deleted and rebuilt.

### 2.2 Sync

`VersionIndex.Sync(IReadOnlyList<VersionInfo> folders, IProgress<IndexSyncProgress>?, CancellationToken)`:
- For each folder from `VersionCatalog.List` (links excluded as there): if the db has no row with that name, or the
  stamp differs, (re)import it; rows for folders no longer listed are deleted (with their files/dirs; unused paths may stay).
- Import from the manifest when it is readable (streaming `Utf8JsonReader`, never the whole file in memory), stamp =
  manifest length + last-write ticks; otherwise scan the folder (files only, sizes + mtimes, `re-manifest.json` skipped,
  links not followed), stamp = folder last-write ticks + top-level entry count, hash NULL. Each version is one transaction;
  cancellation leaves the db consistent (the half-imported version is not committed).
- `dirs` totals are computed during import.
- Progress: version i of n, its name, files imported.
- After a successful backup run, the runner hands the new version's manifest to the index directly (no re-read over the
  network): `IVersionIndexSink.Add(planId, versionInfo, BackupManifest)`; the App wires it to the plan's index. Failure to
  update the index never fails a backup (logged as a warning in the run entry).

### 2.3 Queries

- `Children(versionId, folderPathId)` → child folders (with dirs totals) and files (size, mtime, hash) of one folder.
- `Compare(versionA, versionB)` → per path status Added / Deleted / Changed / Unchanged by §11 (both hashes → hash;
  otherwise size + mtime); folders roll up (Changed if any descendant differs; Added/Deleted if the whole folder is new/gone).
  Done with SQL joins on `path_id`, result kept as a dictionary path_id → status for the lazy tree; "changed only" filters it.
- `History(pathId)` → for every indexed version (oldest → newest): present or missing, size, mtime, and the status versus the
  previous version that had it (New, Changed, Unchanged, Deleted = missing after being present).
- `Search(versionId, pattern, limit 500)` → files and folders whose name matches: a plain substring (case-insensitive) or a
  wildcard pattern with `*` and `?`; results as paths, the UI reveals them in the tree.

## 3. Restorer (Core, `ReBackup.Core.Versions`)

`Restorer.Plan(versionFolder, IReadOnlyList<string> relativePaths, destinationRoot, mode)` → a `RestorePlan` listing every
file to copy (source, destination) and the conflicts (destination exists). `Restorer.Run(plan, ConflictPolicy, progress, ct)`:
- `mode` Original: destination = the version's source (manifest `source`; for scans: the plan's current source) + relative path.
  `mode` To folder: destination = chosen folder + the selected item's name (a folder keeps its structure below it).
- Policies: Overwrite (write to `<name>.<8 hex>.rebackup-tmp` next to the target, then replace; keep the version's mtime),
  Skip, KeepBoth (`name (2026_09_30-14_05).ext`, adding ` 2`, ` 3`… if that exists too).
- Never deletes or moves anything at the destination other than replacing a conflicting file under Overwrite.
- Locked / read-only / access denied → recorded in `RestoreResult.Failures` (path + reason), the rest continues.
- Paths are validated: every destination must stay inside the destination root (no `..`, no rooted names); source files
  must stay inside the version folder; links are not followed.
- Result: copied, skipped, kept-both, failed counts + failure list; cancelable, progress by bytes.

## 4. Versions tab (App)

New tab "Versions" (icon `Icon.Versions` = Layers glyph) in the header tabs and the left rail, Dark Pro cards:

- **Left card "VERSIONS"**: list of `VersionCatalog` folders, newest first: date, size, files; not-managed rows greyed
  with the reason; the index state per row (indexed / indexing / not indexed); Refresh button (re-lists and syncs); a
  progress line while the index syncs ("Indexing 2 of 5 · 2026_10_01-11_34 MoonLabs · 120,000 files").
- **Main card**: toolbar — search box (substring or `*`/`?`), "Compare with" combo (another version or "—"), swap button
  A↔B, "Changed only" switch (enabled while comparing); the lazy tree of version A: Name, Size, Modified, and while
  comparing a Status column (Added green-ish accent, Changed warning, Deleted danger, Unchanged faint; folders roll up).
  Deleted entries (only in A when B is newer…) appear in the tree with their old size. Search results replace the tree
  with a flat result list; clicking one reveals it in the tree.
- **Bottom card "HISTORY OF <file>"** (when a file is selected): table of every version: date, status, size, modified,
  with the folder icon to open that version's copy.
- **Actions** (toolbar buttons + context menu, for the selected file or folder): Open (files: shell-open the copy in the
  version folder — read-only intention, not enforced), Show in Explorer (select it), Restore to original…, Restore to…
  (folder picker). Restore: plan → if conflicts, a dialog "N files already exist" with Overwrite / Skip / Keep both /
  Cancel → run in the background with progress in the footer and the plan card area; result in the status line, failures
  listed in a dialog.
- A restore to the original location while a backup of the same plan runs is allowed (the backup reads the source; the
  user is warned in the confirmation that the running backup may pick up restored files).
- The tab works without an index row for a version (a not-yet-indexed version shows "Indexing…" and fills when done).

## 5. Testing (Core, temp dirs + temp db)

- Index: schema creation; import from manifest (sizes, mtimes, hashes, dirs totals); scan fallback (no manifest, unreadable
  manifest); stamp change → re-import; removed folder → rows deleted; cancellation mid-import leaves the db consistent;
  corrupt db / wrong schema version → rebuilt; streaming parse of a large manifest (100 k entries) within memory bounds.
- Queries: Children; Compare A/C/D/U incl. hash vs size+mtime fallback and folder roll-up; History statuses; Search
  substring and wildcard, case-insensitive, limit.
- Restorer: each conflict policy; keep-both naming incl. collisions; never deletes extra destination files; folder restore
  keeps structure; locked/read-only file → failure, others continue; path traversal rejected; cancellation; mtime kept.
- Runner hook: a successful run adds the version to the index; an index failure does not change the run status.

UI: manual checklist + the controller's offscreen screenshots in Dark and Light.
