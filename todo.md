# Windows-mcp — todo

Cross-session task tracker. Done items kept briefly for context; see `CHANGELOG.md` for the
full record.

## 🧪 Live e2e coverage sweep — ACTIVE (20/60 tools ever exercised against a live server)

**Why this exists:** all prior e2e testing was ad-hoc and unrecorded. A transcript audit
(2026-07-12) found **no checklist ever existed** — 20 of the 60 tools have been invoked against a
live server at some point, 40 **never once**. Every e2e-only bug we've shipped a fix for
(`storage_health` empty/timeout, `defender_status` fault, and now the `process` name-filter) was
invisible to the unit suite. This table is the resumable record so the sweep survives a session.

**Before trusting ANY live result — verify the running image.** The served exe is the committed
`bundle/WindowsMcp.exe` cloned into `~/.claude/plugins/cache/local-marketplace/windows-mcp/<version>/`.
A `dotnet publish -o dist` deploys **nothing**. This trap already cost us once: v0.6.0 was tagged,
pushed, and believed shipped on 2026-07-08, but the cache never re-cloned it — the live server ran
**0.5.0 for four days**, and `process orphans` was recorded as "errored" against a binary that
didn't have it. Check first:
`Get-CimInstance Win32_Process -Filter "Name='WindowsMcp.exe'" | Select ExecutablePath, CreationDate`

**Hazards — do not walk these blind:**
- `storage_health include_usage:true` **wakes sleeping USB/external devices and can stall.** The
  default metadata-only path is safe; the deep path is opt-in for a reason. Don't pass it casually.
- Destructive: `power_action` (now really does enable `SeShutdownPrivilege` — it will shut down),
  `registry_set`, `file_write`, `service` (stop/restart), `scheduled_task` (create/delete),
  `archive`. Exercise against throwaway targets only.
- The 19 UI-automation tools need an **interactive foreground desktop**; they fail headless.

### ✅ Verified live (v0.6.x)
- [x] `process` — `list`, `list includeLineage`, `orphans`, `list groupByRoot`. Lineage asserted
  against independent WMI ground truth (PID/parent/orphan-state all matched). **Found the
  name-filter bug** (fixed in 0.6.1). Orphan detection verified both directions: no false positive
  (explorer's dead parent → correctly orphaned) and no false negative (WindowsMcp's live parent →
  correctly not orphaned).
- [x] **`process orphans` + the kill guards — fully e2e-verified (2026-07-12).** Cross-checked the
  tool's orphan set against an independently computed one (recycle-aware rule, raw WMI, 385 procs):
  **zero false positives, zero false negatives**. All 4 recycled-PID-parent cases caught — incl.
  `Secure System`/`Registry`, whose parent (PID 4) is *alive*, so a naive alive-check would clear
  them; catching them proves the recycle rule is really running. Then **manufactured a real orphan**
  (spawner exits, child survives) — tool reported it with every field exact (pid/ppid/`ParentName:
  null`/`Orphaned`/`RuntimeKind: shell`/`RootPid: self`/start time to the microsecond), matched via
  **command line** (the name is just `powershell.exe`). Kill guards: a **wrong** `startTime` aborted
  the kill and the process survived; the **correct** `startTime` killed it. **Found the error-message
  masking bug** (fixed in 0.6.1). Gotcha for future sweeps: a WMI `CommandLine -like '*marker*'`
  query **matches its own process chain** — build the marker from parts at runtime, or you will
  "find" phantom leftovers and kill your own shell.
- [x] **MCP handshake / `serverInfo`** — **found it misreporting `0.4.1` for three releases**
  (fixed in 0.6.1: version now derives from `<Version>` in `Directory.Build.props` and is pinned to
  `plugin.json` by `ServerInfoTests`). Re-verified over stdio: the rebuilt bundle reports `0.6.1`.

**Reusable harness:** drive any tool against the *rebuilt* `bundle/WindowsMcp.exe` over MCP stdio
without a marketplace round-trip — spawn the exe, `initialize` → `notifications/initialized` →
`tools/call`. This is how the 0.6.1 fix was verified before merge (reproduce the original failure,
then watch it not happen), and it sidesteps the cache-clone deploy lag entirely.

### 🔁 Re-run against 0.6.1 (previously errored, never re-verified after redeploy)
- [ ] `process orphans` — errored 2026-07-08; almost certainly the stale-0.5.0-binary trap. Now
  passing on 0.6.0, but re-confirm on the 0.6.1 bundle.
- [ ] `screenshot format:"png"` — errored 2026-07-08, around the `output="file"` default change.
  Re-check the param shape.

### 🟡 Exercised incidentally, never deliberately verified (18)
`powershell` · `storage_health` · `startup_report` · `process_inspect` · `file_read` · `file_info` ·
`system_info` · `start_process` · `file_search` · `wmi_query` · `file_streams` · `defender_status` ·
`scheduled_task` · `file_manage` · `event_log` · `verify_signature` · `cert_store` · `driver_list`

### 🔴 Never invoked live (40)
- **Safe / read-only — sweep these first (11):** `file_hash` · `reliability` · `env` · `network` ·
  `firewall` · `disk_inspect` · `security_audit` · `registry_get` · `http_request` · `scrape` ·
  `multi_monitor`
- **Write / destructive — throwaway targets only (9):** `file_write` · `registry_set` · `archive` ·
  `service` · `power_action` · `shortcut` · `notification` · `audio` · `clipboard`
- **UI-automation — needs interactive foreground desktop (19):** `click` · `type` · `key` · `hover` ·
  `drag` · `scroll` · `focus` · `launch` · `window` · `switch_to_window` · `get_state` ·
  `get_element` · `find_element` · `interact_element` · `assert_element` · `wait_for` · `get_text` ·
  `get_table` · `ocr` · `file_dialog`

## 🚀 Claude-in-Actions (Phase 2) pilot — ACTIVE (blocked on Daniel for 2 setup items)

Human-gated pilot: a doc-drift bot opens PRs, Daniel merges. Design survived two adversarial review
rounds (Claude-opus + cross-model Gemini/OpenAI). Spec: `docs/superpowers/specs/2026-07-09-claude-in-actions-design.md` ·
Plan: `docs/superpowers/plans/2026-07-09-claude-in-actions-pilot.md` · Setup: `.../claude-bot-setup-checklist.md`.

### ✅ Done
- [x] **Guard foundation (Tasks 1–3, PR#20).** `claude-guard.sh`+tests (allowlist / `src/**` capability
  guard / one-concern caps / `..`-reject, 13 unit tests), agent-immutable `claude-guard.yml`
  (`workflow_run`, always-posts a check, fail-closed on error), `guard-tests.yml`, CODEOWNERS, runbook.
  Review caught + fixed a `${{ }}` script-injection, a rename bypass (`previous_filename`), added the error-trap.
- [x] **Auth pivoted to `ANTHROPIC_API_KEY` (PR#21)** — service credential, metered, Console spend-limit = cost cap.
- [x] **Guard proven live** on PR#21 (`claude-guard` = success via `workflow_run`).

### ⏸ BLOCKED on Daniel (only he can do these — then the rest unblocks)
- [ ] **Set the API-key secret:** `gh secret set ANTHROPIC_API_KEY --repo danielsimonjr/Windows-mcp`
  (dedicated Console key + spend limit recommended). The auto-mode classifier blocks me from setting an `sk-ant` secret.
- [ ] **Create the `claude-bot` GitHub App** — Contents + Pull requests = write ONLY, install on Windows-mcp;
  add `CLAUDE_BOT_APP_ID` + `CLAUDE_BOT_APP_PRIVATE_KEY` secrets. (Needed so the bot's PR is bot-authored and its push retriggers CI → the guard.)
- [ ] **Telegram digest secrets:** `TELEGRAM_BOT_TOKEN` + `TELEGRAM_CHAT_ID`.

### 🔧 Pending me (execute subagent-driven once the above exist)
- [ ] **Task 4** — `claude-maintenance.yml` doc-drift bot (`workflow_dispatch`, SHA-pinned action, own-branch PR, idempotent).
- [ ] **Task 5** — `claude-digest.yml` weekly Telegram digest.
- [ ] **Task 6** — pilot validation against the 3 success criteria.
- [ ] **Runbook Step 2** — add `claude-guard` as a REQUIRED check (after it has run once; it has — PR#21).

### 🔮 Deferred (later phases, not now)
- [ ] **Phase 2b** — the Dependabot-CI-fix bot (hardened: no-build diagnosis off existing CI logs, patch-validation
  in a zero-secret job, real push-permission fix, attempt limiter). Deferred as unsafe/mechanically-hard for now.
- [ ] **Earned auto-merge** for doc-only PRs → then enable CODEOWNERS `require_code_owner_reviews` (reconcile Dependabot first).

## ✅ Fixed 2026-07-08 (found during process-lineage work; fixed at root, not deferred)

- [x] **`PowerShellService` backstop consumed by queue-wait** (was surfacing as the
  `RunAsync_50_serialized_calls` deterministic timeout). Root cause: the per-call backstop CTS was
  created *before* `_gate.WaitAsync`, so a queued caller burned its runaway-script budget while
  waiting. **Fixed:** backstop CTS now created *after* the gate is acquired (bounds execution, not
  queue time). The serialized stress test is right-sized (the property is N-independent; a large N
  only measured Defender cold-start scan time). 6/6 `PowerShellServiceTests` green. (The remaining
  ~real-`powershell.exe` cold-start slowness under AV is inherent to these integration tests and
  not code-fixable — excluding system PowerShell from Defender would be a bad security trade.)
- [x] **`ProcessService` cancellation-check consistency** (Task 2 review Minor): added an entry
  `ct.ThrowIfCancellationRequested()` in the shared `SnapshotAsync`, covering `ListLineageAsync`,
  `GroupByRootAsync`, and `KillTreeAsync` in one place (`KillGuardedAsync` already had its own).
- [x] **Stale `ScreenToolsTests` base64 assertion** (preexisting, unrelated) after the
  `Screenshot output="file"` default — test now opts into `output:"base64"`.
- [x] **Stale redeploy recipe** in this repo's `CLAUDE.md` (described the dead `dist/`+`_RETRY`
  path) — rewrote to the actual url-sourced `bundle/` + `/plugin marketplace update` flow.

## ✅ Recently done

- [x] **`startup_report` + `storage_health` released.** `v0.3.0` (`ecafe9d`) shipped both;
  `v0.3.1` (`3f1e75f`, 2026-06-26) is the storage_health live-fix — temp-`.ps1` MCP path +
  opt-in SMART/physical (`include_usage`). **Both storage_health paths E2E-verified against the
  live server** (fast default never wakes devices; deep path returns real SMART + free space).

## 🔧 Audit backlog (2026-06-26 — 3-agent codebase audit; full sweep approved)

Ordered for safety/atomicity. Each is its own dev-workflow task + atomic commit.

### Batch 1 — clear defects
- [x] **D1 ProcessService handle leak** (`ProcessService.cs:13-21,29,74`) — `Process` objects from
  `GetProcesses`/`GetProcessById`/`Start` never disposed → native handle leak per `process list` /
  `startup_report`. Wrap in `using`/dispose after projecting DTO.
- [x] **D2 WmiService COM leak** (`WmiService.cs:20-26`) — `ManagementObjectCollection` + each
  `ManagementObject` not disposed. Dispose collection + per-row objects.
- [x] **D3 WindowService Process leak** (`WindowService.cs:73`) — `Process.Start` result not disposed.
- [x] **D4 get_table empty headers** (`UIAutomationService.cs:272-283`) — `headers[]` allocated, never
  populated; table always returns null headers. Populate from header cells.
- [x] **D5 PowerAction false-success** (`PowerService.cs:16-22`) — `SE_SHUTDOWN_NAME` never enabled,
  `ExitWindowsEx` bool ignored → unelevated no-op reported as "executed". Enable privilege; throw on false.
- [x] **D6 HashFile aborts find_duplicates** (`FileSystemService.cs:105-119`) — locked/denied file throws
  out of the grouping and kills the whole search. Guard per-file, skip failures.
- [x] **D7 PowerShell orphan-on-cancel** (`PowerShellService.cs:57-66`) — `ct.Register(kill)` installed
  after stdin write; cancel during write orphans the child. Register kill before the write.

### Batch 2 — cross-cutting (both agents flagged)
- [x] **X1 PowerShellService default timeout** — the no-timeout `SemaphoreSlim` gate lets one runaway
  script wedge ALL PS-backed tools. Add a default per-call timeout (the storage budget pattern).
- [x] **X2 Plumb CancellationToken through tools** — services accept `ct`; most tools drop it
  (`ShellTools`, `DiskTools`, `NetworkTools`, `ProcessTools`, `FileTools`). Add `ct` params + forward.

### Batch 3 — service refactors (restore thin-tool pattern)
- [x] **R1 IDiskService** — extract aggregation + reclaimable script out of `DiskTools.cs:28-107` into a
  service + typed DTOs (`DiskUsageEntry`…); white-box test helpers via InternalsVisibleTo.
- [x] **R2 ISecurityService** — move `SystemTools.SecurityAudit` inline PS (`:103-121`) behind a service
  + `SecurityAuditDto`; replace hardcoded JSON fallback literal.
- [x] **R3 IFirewallService** — move `NetworkTools.Firewall` inline PS (`:66-116`) behind a service.
- [x] **R4 empty-output guards** — `DiskTools.reclaimable` + `NetworkTools` raw-`Stdout` returns lack
  the empty-output guard that hid the storage bug; add guard or stage-to-file.
- [x] **R5 StorageService temp-path quote** (`StorageService.cs:49`) — `& '{tempScript}'` breaks if the
  profile path contains a `'`. Escape or use `-File`.

### Batch 4 — missing tests (10 services have none)
- [x] **T1 WebService tests** — SSRF/private-IP guard + HTML→markdown (highest value).
- [x] **T2 NetworkService tests**, **T3 ProcessService tests** — pure-logic paths.

### Batch 5 — expansions
- [x] **E1 network ports → owning PID/name/path** (`PortInfoDto` completeness defect; `Get-NetTCPConnection -OwningProcess`).
- [x] **E2 verify_signature** — expose existing catalog-aware `AuthenticodeInspector` as a standalone tool.
- [x] **E3 file_hash (SHA256/SHA1/MD5)** — upgrade `FileSystemService.HashFile` (MD5-only) + expose.
- [x] **E4 process_inspect** — parent PID / cmdline / owner / loaded modules (WMI `Win32_Process` + `Process.Modules`).
- [ ] **E5 defender_status** [DONE] (`Get-MpComputerStatus`/`Get-MpThreat`), **E6 cert_store** [DONE] (rogue root CAs),
  **E7 reliability/minidump list** [DONE], **E8 driver_list** [DONE] (BYOVD), **E9 NTFS ADS + reparse** [DONE].

## 🟢 Ready / candidates (none blocking)

- [ ] **OVERVIEW.md tool-catalog reconciliation** — per-tool tables have pre-existing drift (SystemTools lists ProcessTools tools; WindowTools lists StartProcess; missing Disk/Storage/Security/Network/Registry/Web sections; ARCHITECTURE ServerInfo "0.2.0"). Counts are correct now; do a full pass against COMPONENTS.md.

- [ ] **`startup_report` — scheduled-task COM-handler resolution.** ComHandler tasks (NGEN,
  CertificateServicesClient, …) expose a CLSID, not an exec path; currently reported with no
  action path (and excluded from summary flags). Could resolve the CLSID → handler DLL for
  fuller coverage. Low priority.
- [ ] **`startup_report` — summary severity tiers.** The `summary` flagged list could rank
  untrusted-third-party vs missing-target vs MS-file-missing, instead of a flat list. Nice-to-have.
- [ ] **Dependabot dev-dep advisories** in `tools/*` (JS). Banner 12→4 after `npm audit fix`;
  remaining need major bumps — let Dependabot PRs handle them.

## ⚪ Deliberately out of scope (decisions, not todos)

- `startup_report` skips IE-era sections (BHO / toolbars / IE search scopes / IE MenuExt) —
  obsolete on Win11; they'd add noise, not signal.
- Full `format=json|text|both` reports are large (~110 KB) and spill to a file by design; the
  default `format=summary` is the inline path. Not worth shrinking the full dump.

## 🔴 Known environmental test flakes (NOT code defects — do not "fix" by disabling)

- `UIAutomationServiceTests.GetStateAsync_returns_tree_with_notepad_root` — needs an interactive
  foreground desktop with Notepad; fails headless. (Fixture documents this.)
- `ClipboardServiceTests.SetTextAsync_then_GetTextAsync_roundtrips` — TextCopy `OpenClipboard`
  access-denied when another app holds the clipboard; transient. Gate headlessly with
  `dotnet test --filter "Category!=UIAutomation"` and treat a lone clipboard failure as environmental.
- `ScreenshotServiceTests.CaptureAsync_returns_non_empty_png_with_dimensions` — fails only under
  full-suite contention (no/contended desktop surface during a parallel run); **passes in isolation**
  (`--filter FullyQualifiedName~ScreenshotServiceTests`). Same screen-capture environmental class as
  the UIAutomation tests — not a regression.

## ✅ Done (shipped in v0.3.0 / v0.3.1 — see CHANGELOG)

- `startup_report` MCP tool: HiJackThis-style boot/persistence report, catalog-aware code-signing
  trust, enabled-state decode, file-missing detection — meets/beats HiJackThis on every actionable
  persistence category, plus IFEO / Winlogon / AppInit_DLLs / Active Setup that HJT lacks.
- Coverage expansion + `format=summary` (default, inline) + `includeProcesses`; Control-Panel
  `System32`/`SysWOW64` `*.cpl` scan; per-SID `HKU` Run; DNS; proxy/trusted-zone.
- All e2e-found bugs fixed (catalog `hCatAdmin`, full-path signer resolution, accessibility noise
  filter, ComHandler-task flagging). `npm audit fix` on `tools/*`.
