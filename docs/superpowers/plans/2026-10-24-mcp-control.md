# MCP Control Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Claude work with ReBackup through MCP: `ReBackup.exe --mcp` is a stdio MCP server that forwards tool calls over a
user-only named pipe to the running app, which answers from its existing services and runs a small set of safe actions.

**Architecture:** A new UI-free project `ReBackup.Control` holds the pipe contract (DTOs, client, server host). The app hosts the
pipe (opt-in setting) and a `ControlService` that maps requests to existing code (queue, version index, scan, ignore relocation).
The `--mcp` mode of the same exe hosts the MCP server (official C# SDK) and forwards every tool call through the client.

**Tech Stack:** .NET 9, WPF, `ModelContextProtocol` NuGet (official C# SDK, stdio transport), `System.IO.Pipes` with ACL,
System.Text.Json, xUnit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-10-24-mcp-control-design.md`

## Global Constraints

- Tools return **metadata only** — never file contents. The server instructions tell Claude to ask the user before inspecting
  contents and to use its own file tools for that.
- No deleting versions, no restores, no plan edits beyond ignore patterns.
- Pipe name `\\.\pipe\ReBackup.Control.<UserSid>`, ACL: current user only; newline-delimited JSON
  `{"id","op","args"}` → `{"id","ok","result"|"error"}`; handshake `{"protocol":1,"appVersion"}`.
- Setting "Allow control via MCP" (`AppSettings.AllowMcpControl`) is **off by default**; when off the app does not host the pipe.
- Writes are two-step: proposal tools return a `proposalId`; `apply_change(proposalId)` executes; proposals live 5 minutes in memory.
- Response caps: 200 rows, about 50 000 characters; truncated output says so and names the narrowing argument (`path`, `top`, `limit`).
- Sizes are bytes plus a human-readable string.
- `RunTrigger` gains `Mcp`; every write shows a tray notification in the app.
- Error keys (stable, English message): `app_not_running`, `protocol_mismatch`, `plan_not_found`, `version_not_found`,
  `version_not_indexed`, `scan_running`, `plan_running`, `source_not_folder`, `proposal_unknown`, `proposal_expired`,
  `proposal_stale`, `invalid_pattern`.
- `app_not_running` message: "ReBackup is not running, or MCP control is disabled in Settings."
- Client config JSON: `{"mcpServers":{"rebackup":{"command":"<path>\\ReBackup.exe","args":["--mcp"]}}}`.
- Scan cache: at most two plans, released after 10 minutes unused; tools needing a scan wait up to 60 s, then `scan_running` with progress.
- New texts in EN and DE. The app is never launched by implementers (it runs in an MSIX container here); process-level tests
  start the built exe only in `--mcp` mode, which opens no window and takes no mutex.
- Branch `feature/mcp-control`, merged into `main` with `--no-ff` when done.

## Review Focus

1. **A second `--mcp` process while one is connected** (Claude Desktop and Claude Code both configured): the app must serve
   several pipe clients at once — pinned in Task 3.
2. **The app quits while a bridge is connected**: the bridge must answer the next call with `app_not_running`, not hang — pinned in Task 3.
3. **A plan name that matches several plans or differs in case**: plans are addressed by id or exact name, case-insensitive;
   ambiguity is `plan_not_found` with the candidates listed — pinned in Task 5.
4. **A huge comparison** (100 000 changed files): capped output with the truncation hint, within the 50 000-character budget — pinned in Task 6.
5. **`apply_change` twice with the same id**: the second call is `proposal_unknown` (consumed), nothing applied twice — pinned in Task 7.

---

### Task 1: Faster equivalence walk

**Files:**
- Modify: `src/ReBackup.Core/Ignore/IgnoreMatcher.cs`, `src/ReBackup.Core/Ignore/IgnoreRelocation.cs`
- Test: `tests/ReBackup.Core.Tests/Ignore/IgnoreMatcherTests.cs`, `tests/ReBackup.Core.Tests/Ignore/IgnoreRelocationProposeTests.cs`

**Interfaces:**
- Produces: `public IgnoreResult IgnoreMatcher.MatchBelow(IgnoreResult parent, string relativePath, bool isDirectory)` — the parent
  folder's full result is passed in: `parent.IsIgnored ? parent : MatchEntry(relativePath, isDirectory)`.

- [ ] **Step 1: Write the failing tests:** `MatchBelow_agrees_with_Match_for_every_entry` (a tree of nested folders with plan +
  nested patterns incl. negations; walk it with MatchBelow from the root's result and compare with `Match` per entry);
  `Equivalence_prunes_folders_ignored_on_both_sides` and `Equivalence_counts_entries_below_a_folder_whose_status_changes`
  (the two deferred cases from the relocation review).
- [ ] **Step 2: Run** `dotnet test tests/ReBackup.Core.Tests --filter "IgnoreMatcherTests|IgnoreRelocationProposeTests"` — expected: build fails.
- [ ] **Step 3: Implement `MatchBelow`** and switch the equivalence walk in `IgnoreRelocation` to pass both matchers' parent results down.
- [ ] **Step 4: Run** the tests and the full Core suite — expected: PASS.
- [ ] **Step 5: Commit** `perf(core): the equivalence walk reuses the parent folder's result`

### Task 2: `--mcp` bridge skeleton over stdio

Proves the riskiest assumption first: a `WinExe` started with redirected handles serves MCP over stdio.

**Files:**
- Modify: `src/ReBackup.App/App.xaml.cs`, `src/ReBackup.App/ReBackup.App.csproj` (add `ModelContextProtocol`, newest stable, exact version)
- Create: `src/ReBackup.App/Mcp/McpBridge.cs`
- Test: `tests/ReBackup.App.Tests/McpProcessTests.cs`

**Interfaces:**
- Produces: `static class McpBridge { static Task<int> RunAsync(string[] args, CancellationToken ct); }` — hosts the MCP server on
  stdio with server name `rebackup`, version = app version, and (for now) one tool `ping` returning `"pong"`.
- `App.OnStartup`: if `e.Args` contains `--mcp` (case-insensitive), **before** `SingleInstance.TryAcquire`, run `McpBridge.RunAsync`
  and `Shutdown(exitCode)` — no window, no tray, no scheduler, no settings writes.

- [ ] **Step 1: Write the failing test** `Mcp_mode_answers_initialize_and_lists_tools_over_stdio`: start the built `ReBackup.exe`
  (path from the test's output dir, as other process tests locate binaries) with `--mcp`, redirected stdin/stdout; send a JSON-RPC
  `initialize` then `tools/list`; assert the server name `rebackup` and a tool `ping`; close stdin; assert exit within 10 s and that no
  window handle was created (`Process.MainWindowHandle == IntPtr.Zero`).
- [ ] **Step 2: Run** `dotnet test tests/ReBackup.App.Tests --filter McpProcessTests` — expected: FAIL.
- [ ] **Step 3: Implement.** If stdio cannot work from the `WinExe` (handles not inherited), stop and report BLOCKED — the
  fallback (a console project `ReBackup.Mcp` shipped next to the app by `publish.ps1`) is a controller decision.
- [ ] **Step 4: Run** — expected: PASS; full App suite PASS.
- [ ] **Step 5: Commit** `feat(app): --mcp starts a stdio MCP server without UI`

### Task 3: `ReBackup.Control` — pipe contract, server host, client

**Files:**
- Create: `src/ReBackup.Control/ReBackup.Control.csproj` (net9.0, no WPF; add to `ReBackup.sln`), `ControlMessages.cs`,
  `ControlPipe.cs`, `ControlServerHost.cs`, `ControlClient.cs`, `ControlErrors.cs`
- Create: `tests/ReBackup.Control.Tests/` (xUnit + FluentAssertions, add to the solution)

**Interfaces:**
- Produces:
  - `record ControlRequest(string Id, string Op, JsonElement Args)`, `record ControlResponse(string Id, bool Ok, JsonElement? Result, ControlError? Error)`,
    `record ControlError(string Key, string Message)`; `static class ControlErrors` with the spec's keys as constants.
  - `interface IControlHandler { Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken ct); }`
  - `static string ControlPipe.NameForCurrentUser()` → `ReBackup.Control.<SID>`; `const int ControlPipe.Protocol = 1`.
  - `sealed class ControlServerHost : IAsyncDisposable` — `ControlServerHost(IControlHandler handler, string appVersion, string? pipeName = null)`,
    `void Start()`, serves **many clients concurrently** (one listener loop, a task per connection), ACL current user only
    (`NamedPipeServerStreamAcl.Create`), sends the handshake line first.
  - `sealed class ControlClient : IAsyncDisposable` — `static Task<ControlClient> ConnectAsync(string? pipeName, TimeSpan timeout, CancellationToken ct)`
    (throws `ControlUnavailableException` when nothing listens, `ControlProtocolException` on a protocol mismatch),
    `Task<ControlResponse> SendAsync(string op, object? args, CancellationToken ct)`; a broken pipe → `ControlUnavailableException`.

- [ ] **Step 1: Write the failing tests** (unique pipe names per test): `Request_round_trips_through_the_pipe`,
  `Two_clients_are_served_at_the_same_time` (handler blocks client A; client B still answered), `Connect_without_server_is_unavailable`,
  `Protocol_mismatch_is_reported`, `Server_disposed_mid_session_makes_the_next_call_unavailable`, `Pipe_acl_grants_only_the_current_user`
  (read `GetAccessControl()` of the server stream).
- [ ] **Step 2: Run** `dotnet test tests/ReBackup.Control.Tests` — expected: build fails.
- [ ] **Step 3: Implement.** One JSON object per line, UTF-8, `JsonSerializerDefaults.Web`.
- [ ] **Step 4: Run** — expected: PASS.
- [ ] **Step 5: Commit** `feat(control): named-pipe contract, server host and client`

### Task 4: Source scan cache shared by the Ignore tab

**Files:**
- Create: `src/ReBackup.App/Services/SourceScanCache.cs`
- Modify: `src/ReBackup.App/ViewModels/IgnorePreviewViewModel.cs` (+ `.Release.cs`), composition in `App.xaml.cs` / `MainViewModel`
- Test: `tests/ReBackup.App.Tests/SourceScanCacheTests.cs`

**Interfaces:**
- Produces: `sealed class SourceScanCache` —
  `Task<ScanLookup> GetAsync(BackupPlan plan, TimeSpan maxWait, CancellationToken ct)` with
  `record ScanLookup(SourceIndex? Index, bool Running, int Files, int Directories)`; `void Put(string planId, SourceIndex index)`;
  `void Invalidate(string planId)`; capacity 2 plans, entries unused for 10 minutes are dropped (`TimeProvider` injected).
  A miss starts a `LiveScan` with the plan's current ignore settings and global defaults.
- The Ignore VM `Put`s its completed index and reads a cached one for the same plan instead of rescanning; `Release()` still frees
  the VM's own references (the cache's idle timeout frees the rest).

- [ ] **Step 1: Write the failing tests:** `Ignore_tab_index_is_reused_by_the_cache`, `Miss_starts_a_scan_and_waits_up_to_max`,
  `Running_scan_returns_progress_after_max_wait`, `Holds_at_most_two_plans`, `Idle_entries_are_dropped_after_ten_minutes`,
  `Invalidate_forgets_the_plan`.
- [ ] **Step 2: Run** `dotnet test tests/ReBackup.App.Tests --filter SourceScanCacheTests` — expected: build fails.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expected: PASS; full App suite PASS (the Ignore tab release tests in particular).
- [ ] **Step 5: Commit** `feat(app): a source scan cache shared by the Ignore tab and the control service`

### Task 5: ControlService — read operations

**Files:**
- Create: `src/ReBackup.App/Control/ControlService.cs` (+ partials `ControlService.Read.cs`), `src/ReBackup.App/Control/ControlResults.cs`
- Test: `tests/ReBackup.App.Tests/ControlReadTests.cs`

**Interfaces:**
- Consumes: `IControlHandler` (Task 3), `SourceScanCache` (Task 4), `IgnoreMatcher`, `IgnoreLineStats`, `VersionIndexSet.For`, `RunLog`.
- Produces: `ControlService : IControlHandler` with ops `list_plans`, `list_versions`, `get_ignore_rules`, `explain_path`, `list_files`,
  `get_run_history`, `get_run_status`. Plan argument `plan` = id or exact name (case-insensitive); none → `plan_not_found`;
  several → `plan_not_found` listing the candidates' ids and names. Result records in `ControlResults.cs` mirror the spec's
  section 4.1 columns; every size is `{ "bytes": n, "text": "1.2 GB" }` (`Formats.Bytes`). Rows capped at 200 with
  `truncated: true` and `hint`. Version not in the index → `version_not_indexed`.
  `list_files(in: "source")` uses the scan cache; `in: <version name>` uses `VersionIndex.Children`.

- [ ] **Step 1: Write the failing tests** (MainViewModelFixture, in-memory source/target): one per op with exact field values;
  `Plan_by_name_is_case_insensitive`, `Ambiguous_plan_name_lists_candidates`, `List_files_caps_at_200_with_a_hint`,
  `No_result_contains_file_content` (serialize every result of a source with known file text and assert the text never appears).
- [ ] **Step 2: Run** `dotnet test tests/ReBackup.App.Tests --filter ControlReadTests` — expected: build fails.
- [ ] **Step 3: Implement.** UI state is read on the dispatcher where the VMs require it; index queries run in the background.
- [ ] **Step 4: Run** — expected: PASS.
- [ ] **Step 5: Commit** `feat(app): control service read operations`

### Task 6: ControlService — growth comparisons

**Files:**
- Modify: `src/ReBackup.Core/Versions/VersionIndex.Queries.cs`, `src/ReBackup.App/Control/` (new partial `ControlService.Compare.cs`)
- Test: `tests/ReBackup.Core.Tests/Versions/GrowthQueryTests.cs`, `tests/ReBackup.App.Tests/ControlCompareTests.cs`

**Interfaces:**
- Produces (Core): `IReadOnlyList<GrowthRow> VersionIndex.GrowthByFolder(long versionA, long versionB, long folderPathId)` (one row per
  direct child folder/file: added/changed/deleted counts and bytes, net delta) and
  `IReadOnlyList<GrowthRow> VersionIndex.GrowthByExtension(long versionA, long versionB)`;
  `record GrowthRow(string Key, int Added, int Changed, int Deleted, long AddedBytes, long DeletedBytes, long NetBytes)`.
- Produces (App): ops `compare_versions(plan, a, b, path?, top?)` and `compare_source_with_version(plan, version?, path?, top?)`
  (default `top` 20, max 200; rows sorted by `NetBytes` descending; totals included). Source comparison uses the scan cache and
  the existing `PreviewComparison` against the newest version by default.

- [ ] **Step 1: Write the failing tests:** Core — `Growth_by_folder_sums_added_changed_deleted`, `Growth_by_extension_groups_case_insensitively`;
  App — `Compare_versions_drills_down_by_path`, `Source_comparison_defaults_to_the_newest_version`,
  `Huge_comparison_stays_within_the_character_budget` (100 000 changed files → ≤ 50 000 chars serialized, `truncated` + hint).
- [ ] **Step 2: Run** both filters — expected: build fails.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expected: PASS.
- [ ] **Step 5: Commit** `feat: growth comparisons for the control service`

### Task 7: ControlService — proposals, apply, start backup

**Files:**
- Create: `src/ReBackup.App/Control/ProposalStore.cs`, partial `ControlService.Write.cs`
- Modify: `src/ReBackup.Shared/Schedule/RunTrigger.cs` (`Mcp`), `src/ReBackup.App/ViewModels/MainViewModel.cs` (a start path usable
  without dialogs), both locale files (`runTrigger.Mcp` = "MCP"/"MCP", notification texts)
- Test: `tests/ReBackup.App.Tests/ControlWriteTests.cs`

**Interfaces:**
- Consumes: `IgnoreRelocation.ProposeIntegrate/ProposeExtract/Apply`, `IRecycleBin`, `PlanStore`, `BackupQueue.Enqueue`.
- Produces:
  - `ProposalStore` — `string Add(object proposal)`, `bool TryTake(string id, out object proposal, out string? errorKey)` (consumes;
    expired after 5 minutes → `proposal_expired`; unknown → `proposal_unknown`), `TimeProvider` injected.
  - ops `preview_ignore_change(plan, add[], remove[])` (entries/bytes newly ignored and newly included with up to 20 examples,
    computed with the scan cache; invalid pattern → `invalid_pattern`), `propose_relocation(plan, integrate | extract+folder)`,
    `apply_change(proposalId)`, `start_backup(plan)` → `runId` = `"<planId>:<enqueue UTC ticks>"` (`plan_running` when a run,
    copy or deletion is active; editor dirty → the saved plan is used, unsaved edits untouched). `get_run_status(runId)` (Task 5 op,
    finished here) reports `queued`/`running` with progress from the queue while that plan's job is live, else the first run-log
    entry of that plan started at or after the ticks (`finished` + result), else `unknown`.
  - Relocation on a non-folder source → `source_not_folder`.
  - Ignore changes and relocations are applied to the **saved** plan through `PlanStore`; if the plan's editor is open and dirty, the
    apply fails with `proposal_stale` (never overwrite the user's unsaved edits); otherwise the editor reloads from disk.
  - Every successful write: tray notification `control.notification.*` ("Claude started a backup of {plan}", "Claude changed the ignore
    rules of {plan}", …) and `RunTrigger.Mcp` for runs.

- [ ] **Step 1: Write the failing tests:** `Preview_counts_newly_ignored_and_included`, `Apply_twice_is_unknown_the_second_time`,
  `Expired_proposal_is_rejected`, `Apply_refuses_when_the_editor_has_unsaved_edits`, `Relocation_through_control_matches_the_dialog_result`,
  `Start_backup_enqueues_with_trigger_Mcp`, `Start_backup_while_running_is_plan_running`, `Writes_raise_a_notification`.
- [ ] **Step 2: Run** `dotnet test tests/ReBackup.App.Tests --filter ControlWriteTests` — expected: build fails.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expected: PASS; full App + Core suites PASS.
- [ ] **Step 5: Commit** `feat(app): control proposals, apply and start backup`

### Task 8: MCP tools, prompt and instructions

**Files:**
- Create: `src/ReBackup.App/Mcp/McpTools.cs`, `src/ReBackup.App/Mcp/McpPrompts.cs`, `src/ReBackup.App/Mcp/McpInstructions.cs`
- Modify: `src/ReBackup.App/Mcp/McpBridge.cs` (drop `ping`)
- Test: `tests/ReBackup.App.Tests/McpToolsTests.cs`

**Interfaces:**
- Consumes: `ControlClient` (Task 3) behind `interface IControlChannel { Task<ControlResponse> SendAsync(string op, object? args, CancellationToken ct); }`
  (the client implements it; tests use an in-process channel around `ControlService`).
- Produces: one MCP tool per op of spec section 4 with the same names and arguments; read tools `ReadOnly = true`,
  `apply_change` `Destructive = true`, `start_backup` and the proposal tools not destructive. A `ControlError` becomes an MCP tool
  error with text `"<key>: <message>"`; `ControlUnavailableException` → `app_not_running` with the exact message from Global Constraints;
  `ControlProtocolException` → `protocol_mismatch` ("ReBackup and its MCP bridge are different versions; restart Claude after updating ReBackup.").
  Prompt `analyze_growth(plan, a?, b?)` with the four steps of spec 4.3; server instructions repeat "ask before reading contents,
  use your own file tools" and the two-step write rule.

- [ ] **Step 1: Write the failing tests** (SDK in-memory transport / direct server invocation): `Tool_list_matches_the_spec_with_annotations`,
  `Read_tool_returns_service_result_as_json`, `Control_error_becomes_a_tool_error_with_the_key`, `Unavailable_app_gives_app_not_running`,
  `Prompt_analyze_growth_mentions_asking_before_reading_contents`, `Instructions_state_the_two_step_rule`.
- [ ] **Step 2: Run** `dotnet test tests/ReBackup.App.Tests --filter "McpToolsTests|McpProcessTests"` — expected: build fails.
- [ ] **Step 3: Implement.** The bridge connects lazily on the first tool call and reconnects after `ControlUnavailableException`.
- [ ] **Step 4: Run** — expected: PASS; update `McpProcessTests` to expect the real tool list.
- [ ] **Step 5: Commit** `feat(app): MCP tools, analyze_growth prompt and server instructions`

### Task 9: Setting, pipe lifecycle, copy configuration

**Files:**
- Modify: `src/ReBackup.Core/Settings/AppSettings.cs` (`bool AllowMcpControl` = false), `SettingsWindow.xaml(.cs)`,
  `ViewModels/SettingsViewModel.cs`, `App.xaml.cs` (start/stop `ControlServerHost` with `ControlService`), both locale files
- Test: `tests/ReBackup.App.Tests/McpSettingsTests.cs`, `tests/ReBackup.Core.Tests/Settings/` (default + round trip)

**Interfaces:**
- Produces: `SettingsViewModel.AllowMcpControl` (toggling starts/stops the host at once), `IRelayCommand CopyMcpConfigurationCommand`
  (puts the Global Constraints JSON with `Environment.ProcessPath` on the clipboard through an injectable clipboard seam).
  Keys `settings.mcp.title`, `settings.mcp.allow`, `settings.mcp.allowHint`, `settings.mcp.copy`, `settings.mcp.copied`.

- [ ] **Step 1: Write the failing tests:** `Mcp_control_is_off_by_default`, `Turning_it_on_hosts_the_pipe_and_off_stops_it`
  (a `ControlClient` connects / gets unavailable), `Copy_configuration_puts_the_exe_path_json_on_the_clipboard`,
  `Setting_round_trips`.
- [ ] **Step 2: Run** — expected: build fails.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expected: PASS; full solution PASS; `dotnet build ReBackup.sln --no-incremental` 0 warnings; MainWindowSmokeTests PASS.
- [ ] **Step 5: Commit** `feat(app): MCP control setting, pipe lifecycle and copied client configuration`

### Task 10: Docs

**Files:** Modify `README.md` (new "Claude / MCP" section: enabling, copying the configuration, tool list in one table, the
"metadata only" and "two-step writes" rules), `publish.ps1` only if Task 2 fell back to a console project.

- [ ] **Step 1:** Write the README section.
- [ ] **Step 2:** `dotnet test ReBackup.sln` — all PASS.
- [ ] **Step 3: Commit** `docs: MCP control in the README`
