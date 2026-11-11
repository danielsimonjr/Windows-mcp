# Windows-mcp — todo

Cross-session task tracker. Shipped work is recorded in `CHANGELOG.md`. The feature backlog
against the upstream Python server, plus the agent-efficiency backlog in its section E (73
prioritized items with implementation sketches), is
`docs/upstream-parity-checklist.md` — new capability work starts there, not here.

## 🧪 Live e2e coverage sweep — ACTIVE (20/69 tools ever exercised against a live server)

**Why this exists:** all prior e2e testing was ad-hoc and unrecorded. A transcript audit
(2026-07-12) found **no checklist ever existed** — 20 of the tools have been invoked against a
live server at some point, the rest **never once**. Every e2e-only bug we've shipped a fix for
(`storage_health` empty/timeout, `defender_status` fault, the `process` name-filter) was
invisible to the unit suite. This table is the resumable record so the sweep survives a session.

> **2026-07-26 — the sharpest example yet, and a lesson beyond "run it live."**
> `disk_inspect mode:reclaimable` failed live because `PowerShellService` piped scripts to
> `powershell -Command -`, which evaluates stdin **line by line** and silently broke every
> multi-line script (exit 0, empty stdout). Fixed in **v0.7.1** via `-EncodedCommand`.
> **It was not merely "untested live" — it was actively mocked green.** `DiskServiceTests` mocks
> `IPowerShellService` and feeds `GetReclaimableAsync` a hand-written JSON string, so the suite
> exercised only the parsing half while the real invocation returned nothing.
> ⚠ **Audit implication: every service tested solely through a mocked `IPowerShellService` is
> still unverified.** A green unit test over a mocked collaborator is not evidence the collaborator
> works. Prefer at least one `Category=Integration` test per PowerShell-backed service (see
> `DiskServiceReclaimableIntegrationTests`).

> **2026-09-04 — two more mocked-only paths, logged by the A-9 GREEN pass.** (1) The stdio
> host wiring `Program.RunStdioAsync` → `AddWindowsMcp(options)` has no in-process test; a
> regression to `AddWindowsMcp(ServerOptions.Stdio)` would keep the suite green while
> `--screenshot-scale` silently stopped working under the default transport. Candidate:
> a `BuildStdioHost(options, configureServices?)` seam like `BuildHttpApp`'s, or a live-exe
> smoke here. (2) `OcrService`'s real path (`BitmapDecoder` → `OcrEngine`) has never run under
> a test — `OcrServiceTests` stop at the mocked capture. Add a `UIAutomation` OCR test when A-8
> touches the shared region parser. *(Closed 2026-09-04: `OcrServiceLiveTests` runs the real
> chain; the stdio seam is still open.)* A-13 adds a third: `get_table`'s GridPattern/TablePattern
> reads have no live test at all — the Notepad fixture has no grid — so exercise `get_table` on an
> Explorer details view or Task Manager grid in the sweep; the string projection (`BuildTable`) is
> unit-tested, the pattern reads are not.

**Before trusting ANY live result — verify the running image.** The served exe is whatever the
MCP registration points at (see `CLAUDE.md` "Testing a change against the LIVE MCP server"); a
`dotnet publish -o bundle` on its own changes **nothing** for a server that is already running.
This trap already cost us once: v0.6.0 was tagged, pushed, and believed shipped on 2026-07-08,
but the live server kept running **0.5.0 for four days**, and `process orphans` was recorded as
"errored" against a binary that didn't have it. Check first:
`Get-CimInstance Win32_Process -Filter "Name='WindowsMcp.exe'" | Select ExecutablePath, CreationDate`

**Hazards — do not walk these blind:**
- `storage_health include_usage:true` **wakes sleeping USB/external devices and can stall.** The
  default metadata-only path is safe; the deep path is opt-in for a reason. Don't pass it casually.
- Destructive: `power_action` (really does enable `SeShutdownPrivilege` — it will shut down),
  `registry_set`, `registry_delete`, `file_write`, `service` (stop/restart), `scheduled_task`
  (create/delete), `archive`. Exercise against throwaway targets only.
- The 21 UI-automation/input tools need an **interactive foreground desktop**; they fail headless.

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
  `plugin.json` by `ServerInfoTests`). Re-verified over stdio: the rebuilt exe reports `0.6.1`.

**Reusable harness:** drive any tool against a freshly published `bundle/WindowsMcp.exe` over MCP
stdio without touching the registered server — spawn the exe, `initialize` →
`notifications/initialized` → `tools/call`. This is how the 0.6.1 fix was verified before merge
(reproduce the original failure, then watch it not happen), and it sidesteps any deploy lag.

### 🔁 Re-run (previously errored, never re-verified after redeploy)
- [ ] `process orphans` — errored 2026-07-08; almost certainly the stale-0.5.0-binary trap. Passing
  on 0.6.0; re-confirm on the current build.
- [ ] `screenshot format:"png"` — errored 2026-07-08, around the `output="file"` default change.
  Re-check the param shape.

### 🟡 Exercised incidentally, never deliberately verified (18)
`powershell` · `storage_health` · `startup_report` · `process_inspect` · `file_read` · `file_info` ·
`system_info` · `start_process` · `file_search` · `wmi_query` · `file_streams` · `defender_status` ·
`scheduled_task` · `file_manage` · `event_log` · `verify_signature` · `cert_store` · `driver_list`

### 🔴 Never invoked live (49)
- **Safe / read-only — sweep these first (15):** `file_hash` · `reliability` · `env` · `network` ·
  `firewall` · `disk_inspect` · `security_audit` · `registry_get` · `http_request` · `scrape` ·
  `multi_monitor` · `fs_changes` (needs elevation) · `watch` · `job` · `wait`
- **Write / destructive — throwaway targets only (10):** `file_write` · `registry_set` ·
  `registry_delete` · `archive` · `service` · `power_action` · `notification` · `audio` ·
  `clipboard` · `integrity` (writes a baseline under `%LOCALAPPDATA%\windows-mcp\integrity`)
- **UI-automation / input — needs interactive foreground desktop (24):** `click` · `type` · `key` ·
  `shortcut` · `hover` · `drag` · `scroll` · `focus` · `launch` · `window` · `switch_to_window` ·
  `snapshot` · `get_state` · `get_element` · `find_element` · `interact_element` · `assert_element` ·
  `wait_for` · `get_text` · `get_table` · `ocr` · `file_dialog` · `multi_select` · `multi_edit`

## 🟢 Ready / candidates (none blocking)

- [x] **OVERVIEW.md tool-catalog reconciliation** — done 2026-09-04: all four `docs/architecture/`
  docs re-aligned to the 64-tool / 36-interface surface (Integrity, USN, Watch, Job sections added;
  Window/Web/Network rows corrected; interface and DTO tables regenerated from the code).
- [ ] **Plugin delivery from a fresh clone.** Decided 2026-09-04: **no binaries in the repo** —
  `bundle/` is gitignored and `scripts/build-release.ps1` writes the single-file exe there locally.
  `.mcp.json` still launches `${CLAUDE_PLUGIN_ROOT}/bundle/WindowsMcp.exe`, so a clone cannot be
  installed as a plugin until either a build step precedes install or the manifest points at
  another delivery mechanism (release asset, remote host). Meanwhile register
  `bundle/WindowsMcp.exe` directly (README "Register with Claude Code").
- [ ] **A redirect can still reach a private address (`scrape`, `http_request`).** The
  private-address check runs on the URL as given and `HttpClient` follows redirects, so a public
  host that answers `302 → http://192.168.1.1/` is fetched anyway: the SSRF guard is one hop deep.
  Both web tools share it (recorded by C-5's review round, 2026-09-08, as pre-existing). The fix
  is re-checking every hop — a redirect handler, or `AllowAutoRedirect:false` plus a checked loop.
- [ ] **`SnapshotRequest` has no `Hwnd`, so `scrape(source:"dom")` resolves the window twice.**
  The tool picks the frontmost Chromium window out of the A-1 inventory and then names it to
  `SnapshotAsync` by *title*, which re-resolves it (exact, then substring) against an inventory
  that also holds minimised windows. Two windows with the same exact title, or a `window:`
  substring that matches a non-browser walked first, can yield the wrong page or an element budget
  spent before the browser is reached (C-5 review, F3). The fix is a snapshot contract change —
  an `Hwnd` on `SnapshotRequest` — and belongs to its own item.
- [ ] **`.claude/settings.json` hooks are Python-era.** The `PostToolUse` hook runs `ruff` on every
  Edit/Write; it is a silent no-op on `.cs` files but spawns a process each time. Replace with
  `dotnet format` on `*.cs`, or drop it.
- [ ] **Decide the fate of the `claude-guard` CI set** (`.github/workflows/claude-guard.yml`,
  `guard-tests.yml`, `CODEOWNERS`, `claude-guard.env`, `.github/scripts/claude-guard.*`). It gates
  PRs from a `claude-bot[bot]` GitHub App that only the upstream maintainer provisioned; its design
  docs were removed on 2026-09-04. Keep only if a bot will be set up for this fork.
- [ ] **`startup_report` — scheduled-task COM-handler resolution.** ComHandler tasks (NGEN,
  CertificateServicesClient, …) expose a CLSID, not an exec path; currently reported with no
  action path (and excluded from summary flags). Could resolve the CLSID → handler DLL for
  fuller coverage. Low priority.
- [ ] **`startup_report` — summary severity tiers.** The `summary` flagged list could rank
  untrusted-third-party vs missing-target vs MS-file-missing, instead of a flat list. Nice-to-have.
- [ ] **Dependabot dev-dep advisories** in `tools/*` (JS). Banner 12→4 after `npm audit fix`;
  remaining need major bumps — let Dependabot PRs handle them.

- [ ] **`UIAutomationService.WaitLoopAsync` spins in its last millisecond.** The loop clamps its
  10 ms floor to the remaining budget, so the final poll interval shrinks to 0 and the loop spins
  until the deadline; `WaitForServiceTests.Wait_loop_floors_a_zero_interval_at_ten_milliseconds`
  flakes on it. Floor the interval at 10 ms regardless of what remains (found in PR #26).

## ⚪ Deliberately out of scope (decisions, not todos)

- `startup_report` skips IE-era sections (BHO / toolbars / IE search scopes / IE MenuExt) —
  obsolete on Win11; they'd add noise, not signal.
- Full `format=json|text|both` reports are large (~110 KB) and spill to a file by design; the
  default `format=summary` is the inline path. Not worth shrinking the full dump.
- The upstream features listed under "X — Deliberately not porting" in
  `docs/upstream-parity-checklist.md` (telemetry, SSE, UIA watchdog, stateless toggle,
  Desktop-relative paths).

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
- `PowerShellServiceTests` — real `powershell.exe` cold-starts under Defender scanning; minutes, not
  a regression (see `CLAUDE.md`).
- Live-desktop flakes that pass alone and fail only under the parallel full suite (a foreground or
  window-inventory race with the real-process PowerShell tests spawning consoles):
  `VirtualDesktopServiceIntegrationTests.GetWindowDesktopIdAsync_returns_exactly_the_id_the_com_object_reported`,
  `WaitForFindPathIntegrationTests.The_pre_B6_overload_still_throws_a_timeout_when_every_poll_failed`,
  `WindowServiceTests.ListAsync_without_minimized_windows_is_a_subset_with_no_minimized_state`.
- `WaitForFindPathIntegrationTests.Element_exists_scoped_to_a_window_that_is_not_open_reports_that_every_poll_failed`
  (`WaitForServiceTests.cs`) — timing-fragile, not a regression: it polls live UIA inside a 400 ms
  budget and asserts `Attempts >= 2`, which a slow window lookup on a loaded box can miss (the
  likely cause; the failing assertion was not recorded). Confirmed failing on `main` too (4 of 5 runs alone, 2026-11-11, during D-10/D-11).
