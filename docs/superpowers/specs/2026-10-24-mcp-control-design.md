# MCP Control Server — Design

Date: 2026-10-24
Status: Approved (brainstorming), pending spec review
Depends on: 2026-10-24-ignore-relocation-design.md (the relocation proposals are exposed as MCP tools).

## 1. Purpose

Let Claude (Claude Code or Claude Desktop on the same machine) work with ReBackup in natural language, for example:

- *"Where did the extra growth between the last two versions come from? Check whether it's important data, otherwise
  suggest ignore rules that fit my current rules."*
- *"Run a backup of Projects now."*
- *"Move the rules for Projects/X into a .backupignore there."*

The server exposes what ReBackup already knows (plans, versions, comparisons, ignore rules and their hits, run history)
and a small set of safe actions. It adds no new analysis logic of its own.

## 2. Decisions

| Topic | Decision |
|---|---|
| Permission level | Read + safe actions: start a backup, change ignore patterns, apply relocations. No deleting versions, no plan settings |
| File contents | **Never through the MCP.** Tools return metadata only (paths, sizes, counts, extensions, dates, deciding rule). The server instructions tell Claude to ask the user before inspecting contents, and to use its own file tools for that |
| Runtime | The ReBackup app must be running; the MCP process is a thin bridge to it |
| Packaging | The same exe: `ReBackup.exe --mcp` is a stdio MCP server (official C# SDK `ModelContextProtocol`) |
| Bridge | Named pipe `\\.\pipe\ReBackup.Control.<UserSid>`, ACL current user only, newline-delimited JSON, versioned handshake |
| Opt-in | Setting "Allow control via MCP", **off by default**; when off, the app does not host the pipe |
| Writes | Always two-step: a proposal tool returns a `proposalId`, `apply_change(proposalId)` executes it |
| Visibility | Every write shows a toast in the app; runs started this way get the trigger `Mcp` in the run history |
| Out of scope | Auto-starting the app, HTTP transport, remote machines, deleting versions, restores, editing plans beyond ignore patterns |

## 3. Architecture

```
Claude ──stdio──▶ ReBackup.exe --mcp ──named pipe──▶ running ReBackup app
                  (bridge, no UI)                     ControlService → existing services
```

### 3.1 `ReBackup.Control` (new project)

- Request/response DTOs for every operation, one JSON object per line, `{"id","op","args"}` →
  `{"id","ok","result"|"error"}`; errors carry a stable key plus an English message.
- `ControlClient` (used by the bridge) and `ControlServerHost` (used by the app): pipe creation with the user-only ACL,
  handshake `{"protocol":1,"appVersion"}`; a protocol mismatch fails with a clear message.
- No dependency on WPF or the app.

### 3.2 App side

- `ControlService` maps each request to existing code: the backup queue/runner, `VersionIndex` queries, the version
  comparison, the Ignore-tab compare, `IgnoreMatcher`, `IgnoreRelocation`, run logs.
- View-model state is touched on the UI thread; long work (scans, comparisons) runs in the background.
- **Source scan cache**: today the scan is owned by `IgnorePreviewViewModel` for the selected plan only. A small
  `SourceScanCache` in the app holds the latest completed scan per plan (at most two plans, released after 10 minutes
  unused, honouring the existing release logic), shared by the Ignore tab and `ControlService`. Tools that need a scan
  start one if none is cached and wait up to 60 s; after that they return `scan_running` with progress so Claude can
  retry.
- Pending proposals live in memory for 5 minutes; `apply_change` rejects unknown, expired or stale ids.
- `RunTrigger` gains `Mcp`.

### 3.3 Bridge mode

- `App.xaml.cs` checks `--mcp` **before** the single-instance mutex; in this mode there is no window, no tray icon, no
  scheduler.
- It hosts the MCP server over stdio and forwards each tool call through `ControlClient`.
- When the pipe is absent, every tool returns: *"ReBackup is not running, or MCP control is disabled in Settings."*
- Risk to verify first in the plan: stdio of a `WinExe` started with redirected handles (expected to work; the plan
  starts with a spike that proves it; if it fails, the bridge moves to a small console project `ReBackup.Mcp.exe` that
  uses the same `ControlClient`, shipped next to the app by `publish.ps1`).

### 3.4 Settings

- Toggle **Allow control via MCP** (off by default); turning it on starts the pipe host at once.
- Button **Copy configuration** puts the client JSON with the exe path on the clipboard:
  `{"mcpServers":{"rebackup":{"command":"<path>\\ReBackup.exe","args":["--mcp"]}}}`.

## 4. Tools

All responses are capped (200 rows, about 50 000 characters); truncated output says so and how to narrow it (`path`,
`top`, `limit`). Sizes are bytes plus a human-readable string.

### 4.1 Read (`readOnlyHint: true`)

| Tool | Arguments | Returns |
|---|---|---|
| `list_plans` | — | id, name, source, target, kind (full/incremental), last run, next run, state (idle/running/deleting) |
| `list_versions` | plan | name, date, size, files, exclusive bytes (pool), warnings |
| `compare_versions` | plan, a, b, path?, top? | growth by folder (top N below `path`) and by extension; added/changed/deleted counts and bytes |
| `compare_source_with_version` | plan, version? (default newest), path?, top? | the same for what the next run would add (Ignore-tab compare) |
| `get_ignore_rules` | plan | plan patterns, global defaults (and whether used), every `.backupignore` with content; hits (entries, bytes) per line |
| `explain_path` | plan, path | ignored or not, deciding pattern, its origin |
| `list_files` | plan, path, in (`source`\|version name), sort, limit | name, kind, size, date, file count, extension, ignored + rule |
| `get_run_history` | plan, n | runs with trigger, result, files, size, skipped count |
| `get_run_status` | runId | state, progress, result |

### 4.2 Write

| Tool | Arguments | Returns |
|---|---|---|
| `preview_ignore_change` | plan, add[], remove[] | entries and bytes that become ignored / included, examples, `proposalId` |
| `propose_relocation` | plan, integrate: file path \| extract: patterns[] + folder | the `RelocationProposal` (diffs, rejected lines, equivalence), `proposalId` |
| `apply_change` | proposalId | result; `destructiveHint: true` so clients ask for permission |
| `start_backup` | plan | `runId`; fails with `plan_running` when a run or deletion is active |

### 4.3 Prompt `analyze_growth`

Arguments: plan, and either two versions or "source vs newest". It guides Claude to:

1. Compare and group the growth by folder and extension, drilling down where one folder dominates.
2. Judge each contributor from metadata (path, extension, count, size, dates): caches, build output, logs, dumps,
   duplicates vs. documents, sources, media.
3. **Ask the user** before looking at any file content, and only then use its own file tools.
4. Suggest rules **in the style of the existing ones**: same notation (`dir/` vs. `**/dir`), placed where similar rules
   already live (plan or the matching `.backupignore`), each with `preview_ignore_change` or `propose_relocation`
   numbers before proposing `apply_change`.

The server instructions repeat rule 3 and the two-step write rule.

## 5. Errors

Stable keys with English messages: `app_not_running`, `protocol_mismatch`, `plan_not_found`, `version_not_found`,
`version_not_indexed` ("indexing, retry later"), `scan_running` (with progress), `plan_running`,
`source_not_folder` (relocation), `proposal_unknown`, `proposal_expired`, `proposal_stale`, `invalid_pattern`.

## 6. Testing

- `ReBackup.Control`: DTO round trips, handshake and protocol mismatch, pipe round trip in-process.
- `ControlService` with fakes for queue, index, scan cache and plan store: each operation, caps and truncation hints,
  proposal expiry and staleness, `plan_running`.
- MCP tools through the SDK's in-memory transport: tool list and annotations, argument validation, the
  `app_not_running` path.
- `SourceScanCache`: sharing with the Ignore tab, eviction after idle, two-plan limit.
- Bridge mode: starting with `--mcp` takes no mutex and creates no window (process-level smoke test).
