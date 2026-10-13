# Google Drive as Source and Target in ReBackup — Design

Date: 2026-10-13
Status: Approved (brainstorming), pending spec review
Sub-project 5 of the storage programme. ReS3 is not affected (no Drive support there for now).

## 1. Purpose

A ReBackup plan's source and target can be a Google Drive location, in every combination with folder and S3
(folder→Drive, Drive→folder, Drive→S3, …). Versions on Drive keep the local format (full copies, markers, manifest),
so retention, the versions tab and restore work unchanged.

## 2. Decisions

| Topic | Decision |
|---|---|
| Directions | Source and target, all combinations with folder and S3; ReS3: none |
| Authentication | Service account (JSON key file), scope `drive`; key stored DPAPI-protected like the S3 secret |
| Targets | Only on shared drives (a service account has no storage quota); validated |
| Sources | Any shared drive or folder shared with the service account's e-mail, including "My Drive" folders |
| Google Docs/Sheets/Slides/Drawings | Exported on read as `.docx` / `.xlsx` / `.pptx` / `.pdf`; exports over Google's 10 MB limit are skipped and logged (warnings); shortcuts skipped |
| Delete | Permanent (no trash), like folder and S3 |
| Parallel copy | 8 workers as for S3; rate limits (403 `rateLimitExceeded`/`userRateLimitExceeded`, 429) retried with exponential backoff |
| Library | Official `Google.Apis.Drive.v3` (+ `Google.Apis.Auth`) |
| Connections file | `drive-connections.json` in ReBackup's config folder |

## 3. Provider `ReBackup.Storage.GoogleDrive` (new project)

**Accounts and connections**
- `DriveAccount(Id, Name, ClientEmail, ProtectedKey?)`: the key JSON, DPAPI-protected via the existing
  `SecretProtector` (moved to `ReBackup.Storage` if needed so both providers share it); `ClientEmail` is read from the
  key and shown in the UI. A key that cannot be decrypted (other Windows account) → "re-enter key".
- `DriveConnectionInfo(Id, Name, AccountId, RootId, RootName, SharedDriveId?)`: the root is a shared drive
  (`RootId = SharedDriveId`) or a folder chosen by link/ID (`SharedDriveId` = the drive it lives in, null for My
  Drive). Resolved and stored when the connection is saved.
- `DriveConnectionStore` (load/save/delete accounts and connections, delete refused while in use, `TryResolve` →
  `DriveConnection` with credential, root and `SharedDriveId`), file format version 1, same validation and corrupt /
  newer-format behaviour as `S3ConnectionStore`.
- `DriveConnectionTester.RunAsync(connection, path, ct)`: lists the root, resolves the path, and for a shared drive
  writes and deletes a probe file; reports access denied / not found / not a shared drive (target) clearly.

**Storage** `DriveStorage : IStorage` over a thin seam `IDriveApi` (list children, get, create folder, upload
(simple < 5 MB, resumable otherwise), update content, update modified time, delete, export, list shared drives). The
real implementation wraps `DriveService` with `supportsAllDrives=true`, `includeItemsFromAllDrives=true`.
- **Paths → IDs:** folder paths are resolved segment by segment from the root and cached per storage instance; the
  cache entry is dropped on a not-found.
- **Same-name siblings** (Drive allows them): `StatAsync`, `OpenReadAsync` and path resolution throw
  `StorageIOException` "ambiguous name: <path>"; `ListAsync` returns the first and logs nothing (callers then hit the
  error when opening it). Names containing `/` or that are not valid storage path segments are left out of listings.
- **Google files:** listed under their name plus the export extension, size reported as 0 (unknown until exported),
  `Stamp` = `version`; `OpenReadAsync` exports to a temp stream. Export larger than 10 MB (API error
  `exportSizeLimitExceeded`) → `StorageIOException` with a new `NotReadable` flag (set for archived objects too;
  the runner skips and logs any file whose read fails with it, with the exception's message as the reason). If a real file already has the exported name, the Google
  file is left out of the listing and a warning is logged.
- **Writes:** `CreateAsync` buffers to a temp file and uploads on commit; `ModifiedUtc` is set (`modifiedTime`).
  Exclusive create = stat, create, then re-list the parent: if a second file with that name appeared, the new file is
  deleted and `StorageConflictException` thrown. Overwrite updates the existing file's content (same ID; old content
  visible until the upload completes). `ExpectedStamp` is compared right before the update (not atomic, as on the file
  system). `Stamp` = `md5Checksum` (fallback `version`).
- **Folders:** real folders; `EnsureDirectoryAsync` creates missing ones; deleting a non-empty folder →
  `StorageConflictException`; delete is permanent (`files.delete`).
- **Capabilities:** `SetModifiedTime | EmptyDirectories` (no `FreeSpace`, no `Links`, not `CaseSensitive`).
- **Errors:** 401 / invalid key / 403 `insufficientPermissions` → `StorageAccessDeniedException`; 404 root or path →
  `StorageNotFoundException`; network / 5xx after retries → `StorageUnavailableException`; 403
  `storageQuotaExceeded` / `teamDriveFileLimitExceeded` → `StorageIOException` that stops the run (storage-level).
  Retries: 429 / 403 rate limit / 5xx, up to 6 attempts, exponential backoff with jitter, honouring cancellation.
- `DriveStorageFactory(IStorageFactory inner, Func<string, DriveConnection?> resolve, Func<DriveConnection, IDriveApi>? apiFactory = null)`
  handles kind `"gdrive"` and delegates other kinds to `inner`.

## 4. ReBackup

- `StorageLocation` kind `"gdrive"` (Path = path below the connection root, ConnectionId = Drive connection id).
- App composition: `DriveStorageFactory(S3StorageFactory(new StorageFactory(), …), drive.TryResolve)`.
- Parallel copy: 8 workers whenever source or target kind is not `"fs"` (was: `"s3"`).
- `PlanValidator`: a Drive location needs an existing connection and a valid path; a Drive **target** needs a
  connection with `SharedDriveId` (else "Drive targets must be on a shared drive"); source and target overlap when
  they share the root's drive/folder and one path is equal to or inside the other.
- Plan tab: the kind switch becomes **Folder | S3 | Drive**; Drive shows a connection combo ("New connection…") and a
  path box with "Test".
- Settings: "Google accounts…" (add: pick the key JSON file; shows the service-account e-mail with a hint "share
  folders with this address") and "Drive connections…" (account, then a list of shared drives the account is a member
  of, or "folder by link/ID"); delete refused while a plan uses it (lists the plans). Dialogs live in `ReBackup.App`
  (ReS3 must not pull the Google libraries).
- "Target full" toggle hidden for Drive targets (no free-space information).
- Restore to the original location writes back to Drive; exported documents come back as Office files (README).
- README: Drive section (create a service account, download the key, share a shared drive / folder with its e-mail,
  costs/limits, export behaviour).

## 5. Errors

| Condition | Behaviour |
|---|---|
| Key missing / must be re-entered / file unreadable | Location unreachable with reason; run ends as error; nothing deleted |
| Folder not shared with the service account | Not found / access denied with a hint to share it with `<e-mail>` |
| Drive target outside a shared drive | Plan validation error |
| Ambiguous name | File skipped and logged (source); write/restore error naming the path (target) |
| Export over 10 MB | Skipped, logged, warnings |
| Quota exceeded on the shared drive | Run ends as error, usual cleanup |
| Rate limit | Retried with backoff; after the last attempt like unavailable |

## 6. Testing

- Unit, against an in-memory `IDriveApi` fake: path resolution and cache, same-name siblings, invalid names, Google
  file export and the 10 MB skip, export-name collision, exclusive create race, overwrite with stamp, delete rules,
  error mapping, retry/backoff (with a fake clock), store load/save/migrate errors, tester outcomes.
- Runner with a Drive-backed fake: folder→Drive and Drive→folder full run, retention, restore.
- App: validator rules, kind switch with three kinds, connection dialogs' view models, delete refusal.
- Optional integration test against a real shared drive, `SkippableFact` that runs only when
  `REBACKUP_GDRIVE_KEY_FILE` and `REBACKUP_GDRIVE_SHARED_DRIVE_ID` are set.
- Manual (human): real shared drive and a My-Drive source folder.

## 7. Versions

Together with the retention coverage timeline: ReBackup 1.5.0 (ReS3 0.3.0 bumps the submodule for the timeline).
