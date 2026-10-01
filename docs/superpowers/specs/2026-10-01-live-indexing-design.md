# ReBackup — Live, parallel indexing of the preview

Date: 2026-10-01
Status: Approved in brainstorming, pending spec review
Extends: `2026-09-30-rebackup-design.md` §5 (`SourceIndexer`, `IndexEvaluator`) and §10.2 tab 3 (Ignore & Preview)

## 1. Goal

Indexing on the Ignore & Preview tab works like WinDirStat: folders appear at once, marked as still loading, several folders are scanned at the same time, and the tree can be expanded and browsed while the scan runs.

## 2. Scope

| Topic | Decision |
|---|---|
| Where | Only the Ignore & Preview tab. The backup run keeps `SourceIndexer` unchanged. |
| Parallelism | Fixed: 4 folders at a time. Not configurable. |
| Live during the scan | Tree structure, sizes, backup sizes, file counts, percentages and ignore status (background colour, status dot). |
| Only after the scan | Treemap; applying pattern edits made during the scan. |
| Search | No search field. "Browsable" means expanding and clicking through folders during the scan. |

## 3. Core: `LiveScan` and `LiveNode`

`LiveScan` (namespace `ReBackup.Core.Indexing`) is started with the source folder, a snapshot of the plan's `IgnoreSettings` and the global default patterns, and a cancellation token.

`LiveNode`: name, relative path, file or folder, size (files), an append-only child list guarded by a per-node lock, running totals (`IncludedSize`, `IgnoredSize`, `IncludedFiles`, `IgnoredFiles`, updated with atomic operations and added up to the root as files are found), the ignore decision (included / ignored, matching pattern, ignored because of a parent), an error text, and a state:

- **Waiting** — the folder is known (listed by its parent) but not listed yet.
- **Scanning** — it has been listed; some folder below it is not finished.
- **Done** — the folder and everything below it is finished (tracked with a counter of unfinished subfolders).

Files are Done when created.

Scanning:

- The root folder is listed first, so the top-level entries appear immediately.
- Four workers take folders from a shared queue. Order: breadth first (shallower folders first). A folder the user asks for (expanding it while it is Waiting) and the folders below it move to the front.
- Listing a folder: read its `.backupignore` first (when nested files are honored), then add the files (evaluated at once against the patterns known at scan start) and the subfolders (evaluated, state Waiting, queued).
- Ignored folders are still scanned so that their size is visible, as today.
- The rules of `SourceIndexer` apply unchanged: links are not followed, at most 256 levels, a folder that cannot be read is marked with its error and counts as Done.

When everything is Done, the live tree is converted into the existing `SourceIndex` (`IndexNode` tree, nested ignore files, unreadable ignore files). From then on the preview works exactly as today: pattern edits re-evaluate the cached index with `IndexEvaluator`, and the treemap is drawn from the evaluated tree.

Errors:

- A folder that cannot be read → Done with an error, the scan continues.
- An unreadable `.backupignore` → recorded as today.
- The source folder itself unreadable or gone → the scan ends with an error.
- An unexpected exception in a worker → the scan ends with that error; the tree seen so far stays visible.

Cancellation stops all workers.

## 4. App: Ignore & Preview during a scan

- About four times a second the preview reads the live tree and refreshes the visible rows only: size, backup size, files, both percentages, status. New subfolders appear in expanded folders; sorting by size is updated. Selection and expanded folders are kept.
- A folder that is not Done shows an hourglass (⏳) with "loading" (Scanning) or "waiting" (Waiting) instead of the status dot, and its sizes in grey with a leading "≥". Done folders get their normal dot and background.
- Any folder can be expanded at any time. A Waiting folder shows a "loading …" placeholder row and is moved to the front of the queue.
- The treemap stays empty during the scan with the note "The treemap appears after the scan" and is drawn from the finished index.
- The pattern editor stays editable. During the scan a note says "Changes are applied after the scan"; when the scan completes, the patterns as they are then are evaluated once.
- Progress line: files and folders so far, plus "x folders waiting".
- Cancel: if a complete index existed before this scan, it is shown again (as today). Otherwise the partial tree stays with the note "Scan canceled — incomplete"; treemap and pattern re-evaluation stay off until a scan completes.

## 5. Concurrency

- Per-node locks for child lists, atomic totals, one lock for the queue.
- The UI only reads snapshots of values. A row may lag behind but is never inconsistent: the children's totals never exceed their parent's.
- Workers never touch UI objects.

## 6. Testing

`ReBackup.Core.Tests`, with temporary folders:

- Result equivalence: a finished `LiveScan` converted to `SourceIndex`, evaluated with `IndexEvaluator`, equals `SourceIndexer.Build` + `IndexEvaluator` on the same folder: sizes, file counts, ignore status, nested `.backupignore`, links, depth limit, unreadable folders.
- States: folders go Waiting → Scanning → Done, and a folder is Done only when everything below it is.
- Prioritisation: with one worker and a controlled order, a requested folder is scanned before others.
- At most four folders are listed at the same time; cancellation stops all workers; a large tree completes without deadlock.

App: build, startup smoke test and a manual checklist.
