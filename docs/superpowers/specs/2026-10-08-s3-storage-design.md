# S3 Storage Provider — Design

Date: 2026-10-08
Status: Approved (brainstorming), pending spec review

## 1. Purpose

Add an Amazon S3 implementation of `IStorage` (from the storage abstraction, spec 2026-10-07) plus the connection model, its encrypted local store, a connection tester and a shared connection dialog. It is sub-project 2 of five; re-s3 (3) and S3 as source/target in ReBackup (4) build on it.

## 2. Decisions

| Topic | Decision |
|---|---|
| Provider | AWS S3 only, official `AWSSDK.S3` |
| Authentication | Access key ID + secret access key; secret stored with DPAPI (CurrentUser) |
| Connection scope | Region + bucket + credentials; the location's `Path` is the prefix inside that bucket |
| Connection storage | Per app (`connections.json` in the app's own config folder); no sharing between apps |
| Timestamps | `ModifiedUtc` = object `LastModified` (upload time); no custom metadata |
| Integration tests | MinIO in Docker via Testcontainers; skipped visibly when Docker is unavailable |
| Connection UI | Shared dialog in `ReBackup.Shared.Wpf`; each app builds its own connection list later |

Out of scope: connection lists in the apps, S3 as source/target in ReBackup, re-s3, other S3-compatible providers, IAM roles/profiles/SSO, host-aware marker rules (needed before sub-project 4).

## 3. Projects

```
src/ReBackup.Storage.S3/          net9.0; refs ReBackup.Storage, ReBackup.Shared, AWSSDK.S3,
                                  System.Security.Cryptography.ProtectedData
  Connections/S3Connection.cs
  Connections/S3ConnectionStore.cs
  Connections/SecretProtector.cs
  S3Storage.cs
  S3Writer.cs
  S3Keys.cs                       path ↔ key mapping and validation
  S3Errors.cs
  S3StorageFactory.cs
  S3ConnectionTester.cs
src/ReBackup.Shared.Wpf/          + S3ConnectionDialog (XAML) + S3ConnectionDialogViewModel; new ref ReBackup.Storage.S3
tests/ReBackup.Storage.S3.Tests/  unit + MinIO integration tests
tests/ReBackup.Shared.Wpf.Tests/  net9.0-windows; view-model tests
```

Architecture test rules (extend `ProjectReferenceTests`):
- `ReBackup.Storage.S3` → `ReBackup.Storage`, `ReBackup.Shared`; packages only `AWSSDK.S3`, `System.Security.Cryptography.ProtectedData`.
- `ReBackup.Shared.Wpf` → `ReBackup.Shared`, `ReBackup.Storage.S3` (and transitively Storage).
- `ReBackup.Storage` stays BCL-only; nothing references `ReBackup.Core` or `ReBackup.App`.

## 4. Connections

```csharp
public sealed record S3Connection(string Id, string Name, string Region, string Bucket,
                                  string AccessKeyId, string? Secret);
// Secret == null: not decryptable on this machine/account → "re-enter secret"
```

`connections.json` (written atomically via `AtomicFile`, `JsonDefaults`):

```json
{ "formatVersion": 1, "connections": [
  { "id": "…", "name": "Elvora Backups", "region": "eu-central-1", "bucket": "my-bucket",
    "accessKeyId": "AKIA…", "secretProtected": "<base64 DPAPI blob>" } ] }
```

- `SecretProtector` uses `ProtectedData.Protect/Unprotect` with `DataProtectionScope.CurrentUser` and a fixed entropy `"ReBackup.S3.v1"` (UTF-8). A `CryptographicException` or bad Base64 on load yields `Secret = null`; the entry is kept.
- `S3ConnectionStore(string filePath)`: `LoadAll()`, `TryGet(id)`, `Save(S3Connection)` (insert or replace by id; a `null` secret keeps the stored blob), `Delete(id)`. A missing file is an empty list; a corrupt file throws `JsonException` (the app reports it, nothing is overwritten).
- The plain secret exists only in memory; it is never logged, never put in exception messages and never written unencrypted.
- Each app passes its own file path (ReBackup: its config folder in sub-project 4; re-s3: its own).

## 5. S3Storage

`S3Storage(S3Connection connection, string prefix, IAmazonS3? client = null)` — the client is created from region + credentials when not given (tests inject one). The prefix is validated with `StoragePath.Validate` (`""` = whole bucket).

**Capabilities:** `CaseSensitive` only.

**Keys** (`S3Keys`): key = prefix + `/` + path (no leading `/`, no double `/`). Keys longer than 1024 UTF-8 bytes → `ArgumentException`. Objects whose key ends in `/` (console "folder" placeholders) are directories, never files.

| Member | Behaviour |
|---|---|
| `StatAsync` | `HeadObject` → file entry (`Size`, `ModifiedUtc` = `LastModified` UTC, `Stamp` = ETag). Not found → `ListObjectsV2(prefix = key + "/", MaxKeys = 1)`: any result → directory entry, else `null`. `""` → directory entry (bucket verified by a `ListObjectsV2` with `MaxKeys = 1`). |
| `ListAsync` | Paged `ListObjectsV2`. Non-recursive: `Delimiter = "/"`, `CommonPrefixes` → directories. Recursive: no delimiter, each distinct parent prefix emitted once as a directory. Paths are relative to the storage prefix. A non-root folder with no objects → `StorageNotFoundException`; the root prefix with no objects is an existing empty folder. Results reflect the listing pages as fetched (may or may not include concurrent changes). |
| `OpenReadAsync` | `GetObject` response stream wrapped so read errors map through `S3Errors`. Not seekable. `InvalidObjectState` (Glacier/Deep Archive) → `StorageIOException` whose message says the object is archived and must be restored first. |
| `CreateAsync` | `Overwrite = false`: `HeadObject` first; existing → `StorageConflictException`. Returns an `S3Writer`. `ModifiedUtc` and `Durable` are ignored. |
| `DeleteAsync` | `DeleteObjects` in batches of 1000; missing keys are not errors. A path that is a directory with objects under it → `StorageConflictException`; a lone placeholder `x/` is deleted. `""` → `StorageConflictException`. Never recursive. |
| `EnsureDirectoryAsync` | No-op. |
| `GetFreeSpaceAsync` | `null`. |

**S3Writer**
- Buffers in memory up to 16 MiB. Commit with ≤ 16 MiB written → `PutObject`.
- Beyond 16 MiB → `InitiateMultipartUpload` and parts of 16 MiB; the part size doubles after every 1000 parts (stays under the 10 000-part limit up to the 5 TiB object limit). At most one part in memory plus the one being uploaded.
- `CommitAsync`: `PutObject` / `CompleteMultipartUpload`, with `If-None-Match: *` when `Overwrite = false`. HTTP 412 → `StorageConflictException`. The object becomes visible only on success; with `Overwrite` the old object stays until then.
- Dispose without a successful commit → `AbortMultipartUpload` (best effort, never throws).

## 6. Errors (`S3Errors`)

| AWS condition | StorageException |
|---|---|
| `NoSuchKey`, `NoSuchBucket`, HTTP 404 | `StorageNotFoundException` |
| `AccessDenied`, HTTP 403, `InvalidAccessKeyId`, `SignatureDoesNotMatch` | `StorageAccessDeniedException` |
| HTTP 412, `PreconditionFailed` | `StorageConflictException` |
| DNS/socket/timeout (`HttpRequestException`, `TaskCanceledException` without the caller's token), HTTP 5xx after SDK retries | `StorageUnavailableException` |
| `InvalidObjectState` | `StorageIOException` (archived) |
| anything else from the SDK | `StorageIOException` |

`OperationCanceledException` for the caller's token propagates unchanged. Messages never contain the secret.

## 7. S3StorageFactory

`S3StorageFactory(IStorageFactory inner, Func<string, S3Connection?> connections)`: kind `"s3"` → looks up `location.ConnectionId`; missing → `StorageNotFoundException` ("connection not found"); `Secret == null` → `StorageAccessDeniedException` ("secret must be re-entered"); else `new S3Storage(connection, location.Path)`. Other kinds → `inner.Open(location)`.

## 8. Connection tester

`S3ConnectionTester.RunAsync(S3Connection, bool checkWrite, CancellationToken) → IReadOnlyList<S3CheckResult>`; `S3CheckResult(S3Check Check, S3CheckState State, string? MessageKey, string? Detail)`, states `Ok | Failed | Warning | Skipped`.

1. **Credentials + bucket:** `HeadBucket`. A region mismatch (bucket region from the response header ≠ connection region) → `Warning` with `Detail` = the bucket's region.
2. **List:** `ListObjectsV2(MaxKeys = 1)`.
3. **Write + delete** (only when `checkWrite`, else `Skipped`): put `.rebackup-connection-test-<guid>` (empty), then delete it.
4. **Lifecycle:** `GetLifecycleConfiguration`; no rule with `AbortIncompleteMultipartUpload` → `Warning`; access denied → `Skipped` ("cannot be checked").

A failed check stops the following ones (they become `Skipped`), except that 4 runs whenever 1 succeeded.

## 9. Connection dialog (`ReBackup.Shared.Wpf`)

Fields: Name (required, unique among the app's connections — uniqueness check supplied by the caller), Region (editable combo of AWS regions), Bucket (required, S3 naming rules: 3–63 chars, lowercase letters, digits, `.`, `-`, starts/ends with letter or digit), Access key ID (required), Secret (`PasswordBox`).

- Editing: Secret empty with placeholder "unchanged" keeps the stored secret; when the stored secret is not decryptable the placeholder says "re-enter secret" and the field is required.
- "Test connection": runs the tester asynchronously with a Cancel button, checkbox "Check write and delete permission" (default on), shows one row per check with ✓ / ✗ / ! / – and the translated reason; a region warning offers "Use <region>".
- Save is enabled when all required fields are valid; testing is optional.
- All texts in `wpf.en-US.json` / `wpf.de-DE.json`. The view model holds the logic and is testable without a window.

## 10. Testing

- **Unit (no Docker):** `S3Errors` mapping per row of section 6; `S3Keys` (prefix joining, 1024-byte limit, placeholders); `SecretProtector` round trip and undecryptable blob → `Secret = null`; `S3ConnectionStore` (insert, replace, null secret keeps blob, delete, missing file, corrupt file); writer part sizing; dispose without commit aborts the multipart upload (fake `IAmazonS3`).
- **Integration (MinIO via Testcontainers, pinned image tag that supports conditional writes):** `S3StorageContractTests : StorageContractTests` with a fresh prefix per test; multipart above 16 MiB round trip; two concurrent exclusive writers → exactly one wins; placeholder `x/` listed as directory and deletable; non-root missing folder → NotFound; tester with and without a lifecycle rule. Without Docker these tests report as skipped (`Xunit.SkippableFact`), never as passed.
- **View model (`ReBackup.Shared.Wpf.Tests`):** validation rules, "unchanged" secret, re-enter state, "Use <region>" applies the region, Save enablement.
- **Architecture:** rules of section 3.

## 11. Documentation

README section "Amazon S3": minimum IAM permissions (`s3:ListBucket`, `s3:GetObject`, `s3:PutObject`, `s3:DeleteObject`, optional `s3:GetLifecycleConfiguration`), the recommended lifecycle rule "abort incomplete multipart uploads after 7 days", and that secrets are bound to the Windows account (DPAPI).

## 12. Implementation order

Branch `feature/s3-storage`; build and all tests green after each step.

1. Project, `S3Connection`, `SecretProtector`, `S3ConnectionStore`, architecture test rules.
2. `S3Errors`, `S3Keys`, read side of `S3Storage` (Stat, List, OpenRead).
3. `S3Writer` and `DeleteAsync`.
4. MinIO infrastructure, contract tests, S3-specific integration tests.
5. `S3StorageFactory`, `S3ConnectionTester`.
6. Dialog + view model + texts + view-model tests + README section.
