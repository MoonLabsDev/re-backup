# S3 Accounts — Design

Date: 2026-10-08
Status: Approved (brainstorming), pending spec review
Repos: re-backup (shared S3 layer and dialogs), re-s3 (adoption)

## 1. Purpose

Separate credentials from buckets. An **account** holds an AWS access key; a **connection** names a bucket and region
and refers to an account. Several buckets share one account, and rotating a secret happens in one place. The change
lives in the shared S3 layer (`ReBackup.Storage.S3`, `ReBackup.Shared.Wpf`), so re-s3 uses it now and ReBackup uses it
when S3 becomes a source/target (sub-project 4). The UI term "Backups" in re-s3 stays.

## 2. Model

```csharp
namespace ReBackup.Storage.S3.Connections;

/// Stored account: credentials only, no region.
public sealed record S3Account(string Id, string Name, string AccessKeyId, string? Secret)
{ public bool NeedsSecret => Secret is null; public override string ToString(); }   // never contains Secret

/// Stored connection: bucket + region + account reference.
public sealed record S3ConnectionInfo(string Id, string Name, string Region, string Bucket, string AccountId);

/// Resolved connection used by S3Storage, S3StorageFactory and S3ConnectionTester — unchanged type.
public sealed record S3Connection(string Id, string Name, string Region, string Bucket, string AccessKeyId, string? Secret);

public static class S3ConnectionResolver
{   // null when the account is missing; Secret null when the account's secret is not decryptable
    public static S3Connection? Resolve(S3ConnectionInfo connection, S3Account? account);
}
```

`S3Connection` keeps its shape so `S3Storage`, `S3Writer`, the tester and their tests do not change.

## 3. Storage

One file per app, `connections.json`, now `formatVersion: 2`:

```json
{ "formatVersion": 2,
  "accounts":    [ { "id": "…", "name": "Moon Labs AWS", "accessKeyId": "AKIA…", "secretProtected": "<DPAPI>" } ],
  "connections": [ { "id": "…", "name": "Elvora Backups", "region": "eu-central-1", "bucket": "elvora-backups",
                     "accountId": "…" } ] }
```

`S3ConnectionStore(string filePath)` gains account members and keeps its rules (AtomicFile, JsonDefaults, DPAPI
CurrentUser with entropy "ReBackup.S3.v1", a rejected file is never overwritten, `Save` validates before writing):

- `LoadAccounts()`, `TryGetAccount(id)`, `SaveAccount(S3Account)` (null secret keeps the stored blob),
  `DeleteAccount(id)` → `InvalidOperationException` when a connection refers to it.
- `LoadAll()` / `TryGet(id)` return `S3ConnectionInfo`; `Save(S3ConnectionInfo)` requires an existing account
  (`ArgumentException` otherwise); `Delete(id)` unchanged.
- `TryResolve(connectionId)` → `S3Connection?` (via `S3ConnectionResolver`).
- Validation on load: account and connection fields non-blank, ids unique (case-insensitive), names unique per kind
  (case-insensitive), every `accountId` exists, regions valid (`S3Regions.IsValid`). Any violation → `JsonException`.
- **Migration from `formatVersion: 1`** on load: every v1 entry becomes an account (name = connection name, same
  access key id and blob) plus a connection referring to it; entries with the same access key id share one account
  (the first entry's id and name; the first decryptable blob, else the first). The file is rewritten as v2 on the next `Save*`, not on load. `formatVersion > 2`
  → `JsonException` ("newer version"), never overwritten.

## 4. Factory and tester

- `S3StorageFactory(IStorageFactory inner, Func<string, S3Connection?> connections)` is unchanged; apps pass
  `store.TryResolve`. Missing account → the resolver returns null → `StorageNotFoundException` ("connection not
  found"), undecryptable secret → `StorageAccessDeniedException` (unchanged messages). The client cache key stays
  (Id, Region, AccessKeyId, Secret), so an account change gives a new client.
- `S3ConnectionTester` is unchanged for connections (it works on the resolved `S3Connection`). New
  `RunAccountAsync(S3Account account, CancellationToken ct)` checks only the credentials with `ListBuckets`
  (region `us-east-1`): success → Ok; `AccessDenied` → Warning `s3.check.accountNoList` (the key is valid but may not
  list buckets — normal for bucket-scoped policies); `InvalidAccessKeyId` / `SignatureDoesNotMatch` → Failed
  `s3.check.accessDenied`; network → Failed `s3.check.unavailable`. The secret never appears in results.

## 5. Dialogs (`ReBackup.Shared.Wpf.S3`)

- **`S3AccountDialog` + `S3AccountDialogViewModel(S3Account? existing, Func<string,bool> isNameTaken, S3ConnectionTester tester)`**:
  Name, Access key ID, Secret (PasswordBox, "unchanged"/"re-enter" placeholders as today), "Test" (`RunAccountAsync`, §4), Save. `Result` like today (Secret null when unchanged).
- **`S3AccountsDialog` + `S3AccountsViewModel(S3ConnectionStore store, …)`**: list of accounts (name, key id, "secret
  must be re-entered" badge, number of connections using it); Add / Edit (opens `S3AccountDialog`) / Delete (refused
  with the list of connections using it).
- **`S3ConnectionDialog`** changes: the access-key and secret fields are replaced by an **Account** combo (names from
  the store) and a "New account…" button that opens `S3AccountDialog` and selects the new account. Validation:
  account required (`s3.error.accountRequired`). The connection test resolves the chosen account; an account that
  needs its secret re-entered disables Test with the hint `s3.account.needsSecret`. `Result` is an `S3ConnectionInfo`.
- All texts in `wpf.en-US.json` / `wpf.de-DE.json`; locale tests stay green.

## 6. re-s3

- `AppServices`: the factory uses `connections.TryResolve`.
- Settings window: new button "S3 accounts…" opening `S3AccountsDialog`.
- Wizard and Backup tab: the connection picker uses `S3ConnectionInfo` and the changed `S3ConnectionDialog`.
- Existing `connections.json` (v1) migrates automatically (§3); no re-s3 data file changes.
- Tray/CLI unchanged.

## 7. ReBackup

No visible change now (no S3 UI yet). Sub-project 4 builds its S3 source/target UI on these dialogs.

## 8. Testing

- Store: v2 round trip; migration v1 → v2 (two entries with the same key id share one account; blobs preserved;
  file unchanged until the first save); account in use cannot be deleted; connection with unknown account rejected on
  save and on load; newer format refused and not overwritten; secrets never written in plain text; resolver cases.
- Factory: missing account → NotFound; undecryptable → AccessDenied; account change → new cached client.
- View models: account dialog validation and secret states; accounts list delete refusal; connection dialog account
  required, "New account…" selects the new account, Test disabled for an account that needs its secret.
- re-s3: settings opens the accounts dialog; wizard creates a connection with an existing account; v1 file migrates.
- Architecture rules unchanged.

## 9. Implementation order

1. re-backup: model, store v2 + migration, resolver, factory wiring tests (`ReBackup.Storage.S3`).
2. re-backup: account dialogs, connection dialog change, texts (`ReBackup.Shared.Wpf`); version 1.3.0.
3. re-s3: submodule bump, adoption (§6), version 0.2.0.
