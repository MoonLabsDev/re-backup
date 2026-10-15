# Incremental Plans (Content-Addressed Pool) in ReBackup — Design

Date: 2026-10-15
Status: Approved (brainstorming), pending spec review
Amends the base design (2026-09-30, "Full copy per run. No hardlinks, no archives, no increments."): full copies stay the
default; a plan can now opt into an incremental pool format at creation.

## 1. Purpose

A plan can store its versions **incrementally**: every distinct file content is stored once in a per-plan pool, and a
version is only its manifest pointing at those contents. Every version stays *logically full*:

- Retention deletes any version on its own — no other version is ever rebased or "lifted".
- Restore reads one manifest and the blobs it names — no chain replay, no merge.
- It works on every backend (folder/NAS, S3, Google Drive) with today's `IStorage` operations (create, read, list,
  delete); no copy/move/rename/hardlink is needed.

## 2. Decisions

| Topic | Decision |
|---|---|
| Format | Content-addressed pool + one manifest per version (restic/kopia style), whole-file deduplication |
| Pool scope | One pool per plan, inside the plan's target |
| Hash | XxHash128 (`xxh128:`) for pool plans; full plans keep xxh64 |
| Mode | `BackupPlan.Incremental` chosen at creation, immutable afterwards; enforced on the target by `re-store.json` |
| Change detection | Per-plan setting **Fast** (default) or **Safe**, changeable at any time |
| Deleting versions | Remove the manifest/version folder as today, then garbage-collect unreferenced blobs |
| Concurrency | Exclusive target lock `re-lock.json` for run, GC, verify and copy; never broken automatically |
| Switching modes | "Copy plan…" to a different, empty target, converting full ↔ incremental; source never modified |
| Opening files | Materialise to a temp folder, verify, then shell-open |
| Out of scope | Chunking within files, compression, encryption, a pool shared across plans, in-place conversion |

## 3. Storage layout

```
<target>/
  re-store.json                    {"format":"pool","hash":"xxh128","version":1}
  re-lock.json                     only while locked: {"host","pid","operation","startedUtc"}
  re-pool/ab/cd/abcd…ef            one immutable blob per content, file name = hex hash
  2026_10_15-14_00 Plan/
    re-manifest.json               the only file in a pool version folder
```

- Version folders, names (`VersionName`) and markers (`re-pending.json`, `re-deleting.json`) are unchanged, so
  `VersionCatalog`, `LeftoverCleaner`, retention planning and the coverage timeline work as today.
- `re-pool` and the `re-*.json` root files are never listed as versions.
- Manifest format 3 = format 2 with `Hash = "xxh128:<hex>"`. Full plans keep writing format 2.
- **Mode guard** before the first write of any run/copy/GC:
  - Incremental plan: target must have `re-store.json` with a supported format, or be empty (then it is created).
  - Full plan: target must not have `re-store.json`.
  - Otherwise the operation aborts with a clear message; nothing is written.
- `BackupPlan.Incremental` is shown in the plan editor only while `IsNew`; afterwards read-only.

## 4. Backup run (incremental plan)

1. Acquire `re-lock.json` (exclusive create, no overwrite). Held → skip the run with a message.
2. Mode guard; `LeftoverCleaner` as today.
3. Write the pending marker for the reserved version name (as today).
4. List `re-pool` once → set of known hashes.
5. For each source file:
   - **Fast:** path, size and mtime equal to the previous manifest's entry *and* its hash in the known set → reuse the
     hash without reading. Otherwise read and hash.
   - **Safe:** always read and hash.
   - Hash unknown → read the file a second time and write it to `re-pool/xx/yy/<hash>` (no overwrite; "already
     exists" counts as success), hashing the written stream; then add it to the known set. If the second read hashes
     differently (file changed between reads), the writer is not committed and the file fails like a locked file.
     (`IStorage` has no rename, so the blob name must be known before writing.)
6. Write the manifest durably, delete the pending marker.
7. Retention (section 5), then GC (section 6), then release the lock.

**Invariant:** a blob is complete before any manifest references it. A crash leaves at most unreferenced blobs and a
pending folder, never a manifest with missing blobs.

**Change-detection notes:** the first run reads and hashes everything. In Fast mode a changed mtime with identical
content re-hashes, uploads nothing and records the new mtime. Content changed with mtime restored is not detected in
Fast mode (same as restic/rsync); Safe mode covers it at the cost of local read time, not upload bandwidth.

## 5. Retention and freeing space

- `RetentionEngine` / `RetentionPlanner` decisions are unchanged.
- Removing a version uses `VersionRemover` / `VersionDeleter` as today; a pool version folder contains only its
  manifest, so this is fast. No other version is touched.
- `FreeSpaceByRetention`: deleting a manifest frees almost nothing, so the planner estimates each candidate's
  freed bytes with `VersionIndex.UniqueBytes` (section 8), deletes candidates in retention order until the estimate
  covers the shortfall, then runs GC. If all permitted candidates are not enough, abort as today.
- Manual deletes from the Versions tab acquire the lock, delete, run GC, release.

## 6. Garbage collection

Runs after retention in a backup run and after a manual delete, always under the lock.

1. **Preconditions:** no `re-pending.json` / `re-deleting.json` remains after `LeftoverCleaner`; otherwise skip GC
   (log a warning) — nothing is deleted.
2. **Mark:** read every version's `re-manifest.json` **from the target** (not the local index) and collect all hashes.
   Any manifest unreadable or unparsable → abort GC, delete nothing, log which version.
3. **Sweep:** list `re-pool`; delete every blob whose hash is not marked, in batches of 1000; then delete empty
   `xx/yy` folders.
4. Interruption is harmless: only unreferenced blobs are ever deleted; the next GC continues.

## 7. Reading versions: restore, open, folders

New abstraction `IVersionSource` (in `ReBackup.Core.Versions`): `OpenReadAsync(relativePath)` plus the manifest entry
(size, mtime, hash).

- `FolderVersionSource` — reads `<version>/<relativePath>` (full plans, today's behaviour).
- `PoolVersionSource` — looks up the hash in the manifest and reads `re-pool/xx/yy/<hash>`.
- A factory picks the source from `re-store.json` / the plan mode. `Restorer`, file opening and plan copy read only
  through `IVersionSource`.

**Restore:** `Restorer.PlanAsync` builds the plan from the manifest as today; `ConflictPolicy`, `RestoreMode` and the
"never delete at the destination" rule are unchanged. While copying, the stream is hashed; a mismatch means the writer
is not committed and the file is reported as failed. A missing blob fails only that file. The result lists all failed
files. Hash verification also applies to full plans (xxh64).

**Open file:** materialise to `%TEMP%\ReBackup\open\<versionId>\<file name>` (verified), then shell-open. Executable
types stay blocked as today. Used for pool plans and for full plans on non-file-system targets; full plans on file
system targets keep opening in place. On app start, entries older than one day are deleted.

**Folders:** for pool plans "Show in Explorer" and "Open version folder" are hidden. Replacement: "Restore to temp
and open" for a folder or version, asking for confirmation above 1 GB.

## 8. Version index

- `VersionIndex.Sync` imports pool manifests exactly like full ones; the hash column holds `xxh128:` values.
  `Compare` keeps comparing hash strings (one algorithm per plan).
- A pool version without a readable manifest has no scan fallback: it is indexed as **damaged** and shown as such.
- New query `UniqueBytes(versionId)`: sum of sizes of distinct hashes referenced by this version and no other.
  Used by section 5 and shown as an "exclusive size" column in the versions list for pool plans.
- Cleanup: versions missing on the target are removed as today. New: deleting a plan deletes its `<planId>.db`; on
  app start, index files without a matching plan are deleted.

## 9. Verify backup

Manual action per incremental plan, under the lock:

- **Quick:** every hash in every manifest exists in the `re-pool` listing.
- **Thorough:** additionally read and re-hash every blob.
- Result: list of affected versions and files. No automatic repair.

## 10. Copy plan (and switch mode)

Dialog "Copy plan…": new name, new target, mode (full / incremental).

- **Target checks:** not the same location and not nested in either direction (folder: normalised path; S3: same
  bucket+region and prefix equal/inside; Drive: same folder id or ancestor). Target must be empty (no `re-store.json`,
  no version folders), else the dialog refuses.
- The new plan copies all settings and retention rules, is created **disabled** and enabled only by the user.
- Source lock is held for the whole copy so GC cannot remove blobs; scheduled runs of the source plan are skipped
  with a message meanwhile.
- Versions are copied oldest → newest; per version: pending marker, read via `IVersionSource` (verified), write in the
  target format (pool dedups; target hash computed while streaming), manifest with the original folder name and
  `CreatedUtc`, remove the marker.
- **Resumable:** complete versions on the target are skipped on restart; half-written ones are cleaned by
  `LeftoverCleaner`.
- Ends with a summary. Nothing is deleted or disabled on the source automatically.

## 11. Locking

- `re-lock.json` is created with `CreateOptions { Overwrite = false }`. Each backend must make this exclusive:
  file system (CreateNew), S3 (`If-None-Match: *`), Google Drive (best effort: check-then-create, re-read to confirm
  own lock content). The implementation plan verifies each provider.
- Held lock → the operation is skipped with a message naming host, operation and start time.
- Stale locks are never broken automatically; the plan shows "Locked by … since …" with a "Release lock…" action
  after confirmation.
- Full plans do not use the lock (unchanged behaviour).

## 12. Error handling

| Situation | Behaviour |
|---|---|
| Crash during a run | Unreferenced blobs + pending folder; `LeftoverCleaner` and the next GC clean up |
| Crash during GC | Only surplus blobs remain; next GC continues |
| Unreadable manifest | Version shown as damaged; GC deletes nothing |
| Missing blob / hash mismatch | Only that file fails, listed in the result; Verify shows the extent |
| Lock held or stale | Run / GC / copy skipped with message; release only by the user |
| Mode does not match target | Hard abort before the first write |

## 13. Testing

- Unit tests on `InMemoryStorage`: mode guard, dedup, Fast/Safe (incl. mtime-only change), GC mark/sweep incl.
  abort on unreadable manifest and leftover markers, lock exclusivity, `UniqueBytes`, restore with missing/corrupt
  blob, copy full → pool and pool → full incl. resume.
- Fault injection: a storage wrapper that fails at every step of run, GC and copy; afterwards every complete version
  must restore byte-exact.
- Integration: one end-to-end run on `FileSystemStorage` in a temp folder.
- All existing full-mode tests stay unchanged and green.
