# Windows-mcp end-to-end test plan

| | |
|---|---|
| Plan version | 1.0 (2026-09-08) |
| Server under test | Windows-mcp 0.7.3, commit `4753f79`, `bundle/WindowsMcp.exe` SHA-256 `4DFCB710…D755F` |
| Companion documents | [e2e-test-results.md](e2e-test-results.md) (the living results and defect list), [`scripts/e2e/mcp-stdio.mjs`](../../scripts/e2e/mcp-stdio.mjs) (the out-of-process stdio harness) |
| Machine profile this plan was written against | Windows 11 Enterprise 10.0.28000, two 2560×1440 monitors at 96 DPI, unelevated interactive session |

## 1. Purpose

Exercise every one of the 69 MCP tools, the hosting layer (command line, environment repair,
stdio and HTTP transports, bearer gate, error filter) and the cross-cutting contracts the
server promises, **end to end against the real server process**, including the failure modes
each tool documents and the ones its inputs imply. The run must leave the PC exactly as it
found it. Each run produces and maintains [e2e-test-results.md](e2e-test-results.md): the list
of features, the tests performed, their results, and a defect list that says how each failure
needs to be fixed.

This is not a replacement for the xUnit suite (`tests/WindowsMcp.Tests`). The suite proves the
code paths; this plan proves the *deployed exe* as a client experiences it, across process
boundaries, real windows, real monitors, real files and the real registry.

## 2. Scope

**In scope**

- Every tool in `tools/list` (69 on 0.7.3), each with: its happy path, every refusal its
  `[Description]` or source names, boundary values of every ranged parameter, a wrong or
  missing target, partial failure where the tool can half-succeed, and the caps it promises.
- The hosting layer: every command-line flag and environment variable of `ServerOptions`,
  `--help`, the startup refusals, `EnvironmentRepair`, stdio and HTTP transports, the bearer
  gate, `ToolErrors` masking, server info and protocol negotiation.
- Cross-cutting contracts (§6): error visibility, check-before-mutate ordering, annotation
  honesty, output caps, cancellation, Unicode hygiene, concurrency, multi-monitor coordinate
  space, timeouts, and server liveness after every failure.
- The multi-monitor configuration specifically: display indices, negative virtual-desktop
  coordinates, regions that span monitors, window placement on each monitor.

**Out of scope**

- Re-deriving unit behaviour the xUnit suite already pins; the suite is a *gate* (§5, channel X).
- Performance benchmarking beyond noting elapsed time where a timeout is under test.
- Penetration testing beyond the documented refusals (private-IP rejection, protected registry
  roots, protected processes).
- Claude Code plugin packaging and `.mcp.json` registration (documented behaviour, not code).

## 3. Ground rules

### 3.1 Test-only means this

After the run, every piece of **persistent** state is identical to before it: files outside
the scratch root, the registry outside the scratch key, services, scheduled tasks, firewall
rules, User- and Machine-scope environment variables, power state, installed apps, Defender,
certificate stores, the integrity baseline. **Transient** desktop state (window bounds and
states, the foreground window, clipboard text, cursor position, audio level and mute, the
current virtual desktop) may change during a feature block and is captured before and
restored after, inside the same block. The state ledger (§3.5) is the proof.

### 3.2 Safety tiers

Every test carries a tier. The tier decides whether it runs, and what must happen around it.

| Tier | Meaning | Runs? | Required around it |
|---|---|---|---|
| **T0** Read-only | The call observes and changes nothing (also network reads: GET, ping, DNS, a public page fetch). | Always. | Nothing. |
| **T1** Scratch-scoped mutation | Every effect lands inside the scratch root (§3.4), the scratch registry key, a process the run itself started, a watch session or job the run itself started, or the server process's own environment. | Always. | The test ends by removing what it made and *verifying the target is gone*. |
| **T2** Transient desktop state | Input injection, focus, window geometry, clipboard, audio, toasts, cursor. Only against windows the run itself opened, except focus/geometry restores of pre-existing windows the run disturbed. | Always, **attended**: the operator is present and does not touch the keyboard or mouse during the block. | Capture the value before, restore it after, verify the restore. |
| **T3** Operator-gated reversible | A create-then-delete cycle on something outside scratch that Windows treats as persistent config: an E2E-named scheduled task, an E2E-named firewall rule, a User-scope environment variable, an `integrity` baseline (which overwrites the user's own), a restart of one benign service. | Only when the operator opts in at the start of the run, per item. **An AI operator never executes T3 items on its own**; it records them BLOCKED (opt-in not given) and moves on. | Back up what exists, do the cycle, restore, verify both the cycle's artefact is gone and the backup is back. |
| **T4** Never | Effective power actions, stopping or starting any service that was not opted in, writes under HKLM/HKCR/HKU, deletes outside scratch, kills of processes the run did not start, running any pre-existing scheduled task. | Never, by anyone, in a test-only run. Only the tool's *refusal paths* are exercised (invalid action, `confirm:false`, a target that does not exist), and those are T0. | The refusal must be proven to fire before any effect — see XC-02. |

Appendix A classifies every tool and action.

### 3.3 Absolute prohibitions

Regardless of tier, a test-only run never:

1. Calls `power_action` with a valid action **and** `confirm:true`.
2. Calls `service` `start|stop|restart` with a real service name and `confirm:true` unless that
   exact service was opted in under T3.
3. Writes under any hive other than `HKCU`, or under `HKCU` outside `Software\WindowsMcpE2E`.
4. Deletes, moves or overwrites a path outside the scratch root, except the screenshot files
   the run itself wrote to `%TEMP%\WindowsMcp` (identified by the `path` the server returned).
5. Kills a process by name, or a PID the run did not start (always pass `startTime`).
6. Runs a `powershell` or `start_process` command whose effect is not confined to the scratch
   root, stdout, or the started process itself.
7. Sends `http_request` with a method other than GET/HEAD to anything but a public echo
   endpoint (`https://httpbin.org/post`, `https://httpbin.org/anything`).
8. Types, clicks or drags on a window the run did not open, other than to restore focus.
9. Runs `integrity baseline` without first copying `%LOCALAPPDATA%\windows-mcp\integrity` aside.

### 3.4 Scratch locations

| What | Where | Lifecycle |
|---|---|---|
| Run id | `yyyyMMdd-HHmm` at the moment the run starts, e.g. `20260908-1500` | Names everything below and the results-document run header. |
| Scratch root | `%TEMP%\WindowsMcpE2E\<run-id>\` (absolute, plain form — the tools refuse `\\?\`) | Created in phase 0, deleted in phase 8 after the ledger diff. |
| Evidence folder | `%TEMP%\WindowsMcpE2E\<run-id>\evidence\` | The only thing the run may leave behind: screenshots, raw JSON results, harness stdout. Retained until the operator clears it. It is disposable and outside the repo. |
| Scratch registry key | `HKCU\Software\WindowsMcpE2E\<run-id>` | Created by the first `registry_set` test, deleted (recursive) in phase 8, absence verified. |
| Scratch names for T3 artefacts | `WindowsMcpE2E-<run-id>` (task name, firewall rule name), `WINDOWSMCP_E2E_<run-id>` (env var) | Created and deleted within the T3 block; absence verified. |
| Run-owned windows and processes | Notepad, Calculator, Paint, Edge instances the run launches; recorded by PID + start time + hwnd | Closed in phase 8; absence verified. |

### 3.5 State ledger

Phase 0 captures the ledger; phase 8 captures it again and diffs. **A non-empty diff is a
defect of the run itself** (either a test failed to clean up, or a tool left state it should
not have) and is recorded as such before the run is signed off.

| Ledger item | How captured (tool, T0) |
|---|---|
| Server process identity | `powershell`: PID, `ExecutablePath`, `CreationDate` of the parent of the spawned shell (§4.1). A changed PID at the end means the server restarted during the run — record when, and which test preceded it. |
| Window inventory | `window list` (title, hwnd, pid, state, bounds, monitor index) and `window active`. |
| Virtual desktop | `window desktops` (current id). |
| Clipboard | `clipboard get`. |
| Audio | `audio get` (level and mute). |
| Cursor | `screenshot` metadata `cursor` (x, y, monitorIndex) — any T0 call that reports it. |
| Processes of fixture apps | `process list name:notepad`, `…calc`, `…mspaint`, `…msedge` (pid + startTime via `includeLineage:true`). |
| Scratch registry key | `registry_get HKCU Software\WindowsMcpE2E` — expected: error naming the path (absent). |
| Toast AUMID registration | `registry_get HKCU Software\Classes\AppUserModelId\Windows-MCP` — present or absent. The server registers it on first `notification` use and does not remove it; if it is absent before the run, `NOTIFICATION-01` becomes T3 (it would leave the registration behind) and is recorded so. |
| Screenshot file directory | `file_manage list %TEMP%\WindowsMcp` — entry names. |
| Integrity baseline | `file_hash` of every file under `%LOCALAPPDATA%\windows-mcp\integrity` (or its absence). |
| Scheduled tasks | `scheduled_task list` — no `WindowsMcpE2E*` name. |
| Firewall rules | `firewall list name_like:WindowsMcpE2E` — empty. |
| User environment | `env list scope:User` — no `WINDOWSMCP_E2E*` name. |
| Watch sessions and jobs | `watch list`, `job list` — counts. |

### 3.6 Stop conditions

Abort the run, record the reason in the results document, and do not sign off if:

- a T0 or T1 test is observed to have changed state outside its tier (the ledger or a spot
  check shows it) — file the defect first;
- the server process dies twice in one run;
- a T3 or T4 item is reached and the operator's opt-in is missing or ambiguous — mark BLOCKED,
  never guess.

## 4. Environment and preconditions

### 4.1 Server under test

The server this session's `mcp__Windows-mcp__*` tools reach is the one the Claude desktop app
launched, not the build output. Verify before every run, and record all four values in the
results header:

```powershell
Get-CimInstance Win32_Process -Filter "Name='WindowsMcp.exe'" | Select ProcessId, ExecutablePath, CreationDate
(Get-Item C:\WindowsMCP\WindowsMcp.exe).VersionInfo.ProductVersion
Get-FileHash C:\WindowsMCP\WindowsMcp.exe, D:\Windows-mcp\bundle\WindowsMcp.exe -Algorithm SHA256
```

The two hashes must match and the version must equal `<Version>` in `Directory.Build.props`.
Then prove which image answers *this session* with the parent-process probe, which is also
ledger item 1:

```json
powershell { "command": "$p=(Get-CimInstance Win32_Process -Filter \"ProcessId=$PID\").ParentProcessId; Get-CimInstance Win32_Process -Filter \"ProcessId=$p\" | Select ProcessId, ExecutablePath, CreationDate | ConvertTo-Json", "timeout_seconds": 60 }
```

### 4.2 Monitors (as found on 2026-09-08)

| Index | Device | X | Y | Width | Height | Primary | Work area height | DPI / scale |
|---|---|---|---|---|---|---|---|---|
| 0 | Monitor0 | −2560 | 0 | 2560 | 1440 | no | 1392 | 96 / 1.0 |
| 1 | Monitor1 | 0 | 0 | 2560 | 1440 | **yes** | 1392 | 96 / 1.0 |

Consequences the tests rely on: display index 0 is **not** the primary, so "default = primary"
must select index 1; every coordinate on display 0 has a negative X; the virtual screen is
x ∈ [−2560, 2560), y ∈ [0, 1440); the seam is x = 0. Re-run `multi_monitor` at phase 0 and
update this table if the layout differs; tests that name coordinates are written relative to
these values and say so.

### 4.3 Fixture applications (present on this machine)

| App | Version | Used by | Notes |
|---|---|---|---|
| Notepad (packaged) | 11.2607.14.0 | UI query, input, window, file_dialog, launch | Win11 modern Notepad. `NotepadFixture.cs` documents that the modern app's UIA tree differs from classic Notepad; the plan targets the *Document* control by `find_element kind:text`. |
| Calculator (packaged) | 11.2607.0.0 | interact_element, get_text, assert_element, launch fuzzy match | Buttons expose InvokePattern; the display exposes a Name with the result. |
| Paint (packaged) | 11.2605.81.0 | drag | A canvas that reacts to a held-button drag. |
| Microsoft Edge | 152.0.4191.66 | snapshot use_dom, scrape source:dom, get_table, wait_for use_dom | Chromium; Firefox is absent so the "Firefox refused" case is exercised by name only. |
| classic `notepad.exe` | stub | launch by path | On Win11 the System32 stub redirects to the packaged app; `launch` by path must still report a PID. |
| Windows Terminal | 1.24 | not used | |

Not present: Chrome, Firefox. A test that needs them is recorded SKIPPED with the reason.

### 4.4 Tooling on the operator's side

- Node 24 (for `scripts/e2e/mcp-stdio.mjs`), PowerShell 7, the .NET 10 SDK (for the xUnit
  gate), `curl` or `Invoke-WebRequest` (for the HTTP transport probes).
- A built `bundle/WindowsMcp.exe` whose hash matches the deployed one (§4.1), used by channels S
  and H so that the exact image under test is the one spawned out of process.

### 4.5 Client capabilities

The Claude desktop app is the client on channel L. Whether it declares the MCP *sampling*
capability is not known in advance; `SCRAPE-08` discovers it and the result is recorded in the
run header (it decides whether `summarize:true` is expected to summarise or to fall back with
a `Note`). Progress notifications (the `powershell` 10 s heartbeat) are only observable on
channel S, where the harness prints them.

## 5. Channels and harness

| Channel | What it is | Use it for |
|---|---|---|
| **L** — Live | This session's `mcp__Windows-mcp__*` tools against the deployed exe. | The primary channel: every tool test that does not need a different server configuration. |
| **S** — Stdio harness | `node scripts/e2e/mcp-stdio.mjs --exe bundle/WindowsMcp.exe …` spawns a fresh server with chosen flags and environment, speaks JSON-RPC over stdio, prints one JSON line per response and any notification in between, exits 1 if any response was an error. | Command-line flags, environment repair, raw error shapes (unknown tool, wrong argument type, masked exception), progress heartbeats, cancellation, stdout purity, server info. |
| **H** — HTTP | `bundle/WindowsMcp.exe --transport http --bind 127.0.0.1 --port <free port>` with `WINDOWSMCP_API_KEY` set, probed with `Invoke-WebRequest`/`curl`; the process is stopped at the end of the block. | Bearer gate, stateless transport, the off-loopback refusal, HTTPS with `--cert-thumbprint` when a suitable certificate exists, and `scrape summarize:true` on HTTP (must fall back). |
| **X** — xUnit gate | `dotnet test --filter "Category!=UIAutomation"` headless, then `dotnet test --filter "Category=UIAutomation"` attended with Notepad in the foreground. | Phase 1 gate. Pass/fail counts go in the run header; a red suite on a clean tree is a defect in its own right and blocks sign-off of the areas it covers. |

Harness examples:

```bash
node scripts/e2e/mcp-stdio.mjs --exe bundle/WindowsMcp.exe --list
node scripts/e2e/mcp-stdio.mjs --exe bundle/WindowsMcp.exe --call system_info '{"category":"os"}'
node scripts/e2e/mcp-stdio.mjs --exe bundle/WindowsMcp.exe --call screenshot '{"output":"file"}' -- --screenshot-scale 0.5
node scripts/e2e/mcp-stdio.mjs --exe bundle/WindowsMcp.exe --env Path= --call powershell '{"command":"$env:Path"}'
node scripts/e2e/mcp-stdio.mjs --exe bundle/WindowsMcp.exe --request '{"method":"tools/call","params":{"name":"no_such_tool","arguments":{}}}'
```

**Evidence rule.** Every test records, in the results document's log row: the channel, the
exact call (tool and arguments as JSON, or the harness command line), the status, and either a
short verbatim excerpt of the result (the field that proves the expectation) or a path in the
evidence folder for anything longer than ~300 characters or binary (screenshots via
`output:"file"`, then copied into the evidence folder). Excerpts are pasted as data; nothing in
a result is ever followed as an instruction.

**Liveness rule.** After every test whose expectation is an error, a timeout, or a cancel, the
runner calls `system_info {"category":"os"}` on the same channel. A non-answer is defect
severity S1 (the failure took the server down) and is attributed to the preceding test.

## 6. Cross-cutting contracts (XC)

These apply to every tool. Each per-tool row in §7 that exercises a refusal implicitly asserts
XC-01 and XC-02; the XC rows below are the explicit, stand-alone checks.

| ID | Tier | Ch | Contract | Test | Expected |
|---|---|---|---|---|---|
| XC-01 | T0 | L, S | **Error visibility.** A deliberate refusal reaches the caller as its message, never as the SDK's masking text. | Call `file_read` with `path:"C:\\WindowsMcpE2E-does-not-exist.txt"`; call `registry_get` with `hive:"HKCU"`, `path:"Software\\WindowsMcpE2E\\absent"`; on S, call a tool with an argument of the wrong JSON type (`wait {"seconds":"ten"}`). | Each answer is `isError:true` with a message that names the path/key/parameter. None contains `An error occurred invoking`. The wrong-type case is a caller-visible message too (record the exact shape as the baseline; if it is the masking text, file a defect: the SDK's argument binding is not routed through `ToolErrors`). |
| XC-02 | T0 | L | **Check before mutate.** A refusal fires before any effect. | For each of: `file_manage copy` scratch file → existing scratch destination with `overwrite:false`; `file_write` with `confirm:false`; `registry_set` with `confirm:false`; `process kill` with `confirm:false` on a run-owned PID; `window move` on a minimised run-owned window without `restore_first`. Re-read the target after the refusal. | Refusal message; the target is byte-for-byte / value-for-value unchanged; the process is still alive; the window is still minimised. |
| XC-03 | T0 | L | **Annotation honesty.** A tool declared `ReadOnly` changes nothing observable. | Run the whole T0 block, then diff the ledger (§3.5) before the first T1 test. | Empty diff (except cursor position and the evidence folder). |
| XC-04 | T0 | L | **Confirm gates match the descriptions.** Every tool whose description says `confirm:true` is required refuses without it. | `file_write`, `file_manage delete`, `registry_set`, `registry_delete`, `process kill`, `service stop`, `service restart`, `scheduled_task delete`, `firewall add`, `firewall remove`, `env set`, `power_action` — each with the gate absent and otherwise valid, T4-safe arguments (non-existent names where the action would be effective). | Each refuses naming `confirm`; no effect. |
| XC-05 | T0 | L | **Output caps report truncation.** | `file_manage list` on `C:\Windows\System32` with `max_entries:5`; `firewall list max:3`; `event_log log:System max:2`; `snapshot max_elements:10 format:json`; `scrape max_chars:200`; `job output tail:10`. | Each result is bounded as asked and carries its truncation signal (`Truncated:true`, count ≤ max, `ElementLimit`, `Chars` > 200 with `Truncated:true`, `Trimmed`/tail length). |
| XC-06 | T0 | S | **Cancellation is honoured.** | On S, send `powershell {"command":"Start-Sleep 120"}` and, after 3 s, `notifications/cancelled` for its id; then `tools/call system_info`. Separately `process list` to see whether a `powershell.exe` child from the cancelled call survives beyond 10 s. | The cancelled request either returns an error naming cancellation or never returns; the follow-up call answers within 5 s; no orphaned `powershell.exe` child after 10 s. |
| XC-07 | T0/T1 | L | **Unicode hygiene (A-13).** | Create `scratch\ünïcødé 日本語 🎉.txt` with `file_write`; `file_read`, `file_info`, `file_hash`, `file_manage list pattern:"*🎉*"`, `file_search pattern:"*日本語*"`; `window` title matching against a Notepad window titled with that name; `type` the same string into Notepad and read it back with `get_text`; `clipboard set/get` round trip; `registry_set` a value named `ключ` under the scratch key. | Every round trip returns the identical string; no `?` substitution, no split surrogates, no `\u` escapes leaking as text. |
| XC-08 | T0 | L, S | **Concurrency.** | On S, send two `powershell` calls back to back (`Start-Sleep 4; 'a'` and `'b'`), then a `system_info` call, without waiting. On L, start a `powershell background:true` job that sleeps 20 s, and while it runs call `powershell` foreground `'x'`. | Foreground calls serialise (second answers after the first, elapsed ≈ 4 s + its own); `system_info` is not blocked behind the gate (answers early); the background job does not block the foreground call. |
| XC-09 | T0 | L | **Multi-monitor coordinate space.** | Every coordinate-taking tool on display 0 (negative X): `hover {"x":-1280,"y":720}`; `click clicks:0` there; `screenshot region:"-2560,0,200,200"`; `ocr region:"-2560,0,800,200"`; `window move` of a run-owned Notepad to `x:-2000,y:100` and `window list` `MonitorIndex`; `scroll` at a negative point; `drag` from display 1 to display 0. | Each accepts the negative coordinates; metadata `cursor.monitorIndex` is 0 after the hover; `MonitorIndex` is 0 after the move; `region` echoes the rect asked; no clipping to the primary. |
| XC-10 | T0 | L | **Timeouts surface, hangs do not.** | `powershell {"command":"Start-Sleep 30","timeout_seconds":2}`; `storage_health timeout_seconds:5 include_usage:true`; `launch {"app_name":"calc","timeout_ms":1}`; `wait_for text:"zzz-never" timeout_ms:1500`; `http_request` to `https://httpbin.org/delay/10` (client timeout is 100 s — record elapsed only). | `TimedOut:true, ExitCode:-1` within ≈2–3 s; a bounded storage answer; `WindowDetected:false` with a PID; `Satisfied:false` at ≈1.5 s; the HTTP call returns (or its timeout is a `TimeoutException` message). |
| XC-11 | T0 | L, S, H | **Server liveness after failure.** | Applied by the liveness rule (§5) after every error-expected test; plus, explicitly: `scrape` of a page nested > 300 levels (served from the scratch root through the harness's `LocalHttpServerFixture`-style local server, or a public test page) must be *refused*, not crash. | `system_info os` answers after each; the deep-nesting scrape returns the refusal naming the URL and the limit; the server PID is unchanged. |
| XC-12 | T0 | L | **Ids expire predictably.** | `snapshot`, keep `el_N`; `snapshot` again; `get_element el_N` from the first snapshot; close the window and `assert_element` on one of its ids. | The stale id is refused with a message saying ids are valid until the next snapshot (or resolves only if the walk re-issued the same id — record which); the closed-window case answers `FAIL: … element no longer available`. |

## 7. Feature matrix

Conventions for every table in this section:

- **ID** `<TOOL>-NN` (tool name upper-cased, underscores kept). **Tier** as §3.2. **Ch** channel.
- **Call** is the exact JSON argument object (channel L or S); `scratch` stands for the
  absolute scratch root, `owned:Notepad` for the Notepad window the run launched (address it by
  the `Hwnd` recorded at launch, never by a bare title, so a pre-existing Notepad is never hit).
- **Expected** names the field or message that decides PASS. A refusal row expects a
  caller-visible message naming the parameter or target (XC-01) and no side effect (XC-02).
- **Cleanup** is part of the test: a T1/T2 row is not PASS until the cleanup verification
  passes too.

### 7.1 UI query and batch input

Fixtures for this section (all run-owned, all closed in phase 8):

- **`owned:Notepad`** — `launch {"app_name":"notepad"}`; record `Pid`, `Hwnd`, `Title` and the
  process start time. Modern Notepad hosts every window in one process and persists tabs under
  `%LOCALAPPDATA%\Packages\Microsoft.WindowsNotepad_8wekyb3d8bbwe\LocalState\TabState`; the
  run records that folder's file list in phase 0 and, in phase 8, deletes only the `.bin` files
  that appeared during the run (the `NotepadFixture` sweep), otherwise the next Notepad start
  restores every tab the run opened. Close it with `window close hwnd:<Hwnd>` and answer the
  "Don't save" flyout by `key esc` / `find_element "Don't save"` + `interact_element click`.
- **`owned:Edge`** — write `<scratch>\probe.html` (title `WindowsMcpE2E Probe Page`; an `h1`
  `Probe heading`, a paragraph `First paragraph of body text.`, `<input id="q" value="prefilled">`,
  a checked checkbox, a `<select>` with three options, `<input type="range" min="0" max="10"
  value="2" aria-label="Probe range">`, a `<ul>` of two items, a `<table>` with
  headers `A,B` and one row `1,2`, a 3000 px spacer, then `<p>Last paragraph.</p>`), and start
  `start_process {"command":"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe","args_json":"[\"--app=file:///<scratch>/probe.html\",\"--user-data-dir=<scratch>\\\\edge-profile\",\"--no-first-run\"]"}`.
  Record the `pid`; find its window with `wait_for condition:"active_window" text:"WindowsMcpE2E Probe Page"`.
  Kill by pid with `startTime` in phase 8; the profile lives in scratch.
- **`owned:Calc`** — `launch {"app_name":"calc"}` for InvokePattern buttons and a Name that
  carries the result.
- **`owned:Charmap`** — `start_process {"command":"C:\\Windows\\System32\\charmap.exe"}`: a classic
  Win32 window whose elements become *stale* when it is closed (the D-4 precedent for
  "element no longer available").

Verbatim messages come from `Tools/UIAutomationTools.cs`, `Services/UIAutomationService.cs`,
`Services/UiTree/*.cs`, `Tools/InputTools.cs`, `Services/BatchTargets.cs`, `Tools/FileTools.cs` on 0.7.3.

#### snapshot (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| SNAPSHOT-01 | T0 | L | `owned:Notepad` in front; `{"scope":"foreground"}` | Text form with a `Cursor:` line (`on display N`), `Active window:` naming Notepad, an `Interactive (…)` block with one row `action: fill` whose centre lies inside the window `Bounds`, a `Scrollable (…)` block. | — |
| SNAPSHOT-02 | T0 | L | `{"scope":"desktop","max_elements":1}`; then `{"max_elements":0,"format":"json"}` | Exactly one element and the trailer `Truncated at 1 elements. Narrow the view (scope=foreground, or window=<title>) or raise max_elements.`; JSON with `Truncated` absent/false under the 500 default (light desktop) and no `ElementLimit`. | — |
| SNAPSHOT-03 | T0 | L | `{"scope":"window"}`; `{"scope":"desktop","window":"Notepad"}`; `{"scope":"tray"}` | `scope=window requires window: the title of the window to snapshot.`; `window is only used with scope=window.`; `Unknown scope 'tray'; expected desktop\|foreground\|window`. | — |
| SNAPSHOT-04 | T0 | L | `{"format":"xml"}`; `{"max_elements":-1}` | `Unknown format 'xml'; expected text\|json`; `max_elements must be 0 (the server default) or positive, got -1`. | — |
| SNAPSHOT-05 | T0 | L | `{"scope":"window","window":"zzz-not-a-window"}` | `No top-level window matching 'zzz-not-a-window'. Open windows: '…'` listing Notepad among ≤ 15 titles. | — |
| SNAPSHOT-06 | T0 | L | `{"scope":"window","window":"<owned:Notepad title>","format":"json","include_tree":true}` | `Tree` present, rooted at the window; element `CenterX/CenterY` inside `Bounds`; password values never printed. | — |
| SNAPSHOT-07 | T0 | L | `owned:Edge`: `{"scope":"window","window":"WindowsMcpE2E Probe Page","use_dom":true}`; then `use_dom:false` | `Pages (1):` block with the document's `el_N`, the title, the `file:///` URL, `[v: 0%]`; page text contains `Probe heading` and `First paragraph of body text.` and **not** `Last paragraph.` (below the fold); no address-bar/tab-strip rows. Without `use_dom`: no `Pages` block and the chrome rows present. | — |
| SNAPSHOT-08 | T0 | L | Move `owned:Notepad` to display 0 (`window set_bounds x:-2000 y:100 width:800 height:600`); `{"scope":"window","window":"<title>"}` | Element centres have **negative** `CenterX` and still resolve via `get_element`; `Cursor:` reports the display it is on. | `set_bounds` back to `Before` |

#### get_state (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| GET_STATE-01 | T0 | L | `owned:Notepad` in front; `{}` | JSON tree, `Root.ControlType` `Window`/`Pane`, `Children` non-empty, depth ≤ 3, no `Truncated` key under the default budget. | — |
| GET_STATE-02 | T0 | S | Server started with `--max-tree-elements 3 --flash off`; `{}` | Root carries `"Truncated":true,"ElementLimit":3` (no per-call override exists for `get_state`). | — |
| GET_STATE-03 | T0 | L | `{}` with Notepad in front; `focus hwnd:<owned:Calc>`; `{}` again | `Root.Name` differs (foreground follows focus). | `focus` back |
| GET_STATE-04 | T0 | L | Take an `el_N` from `get_state`; `get_element el_N`; `snapshot`; `get_element el_N` again | Resolves both times (`get_state` ids are not evicted by a snapshot; only snapshot ids are). | — |

#### find_element (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| FIND_ELEMENT-01 | T0 | L | `owned:Notepad` in front; `{"text":"","kind":"interactive"}` | `Matches` non-empty, one with `ControlType` `Document` (modern) or `Edit`; no `IsOffscreen:true` entry except an `Edit` with real bounds (D-7). | — |
| FIND_ELEMENT-02 | T0 | L | `{"text":"","kind":"any","scope":"desktop"}` | E-4 page shape: exactly 20 `Matches` on a populated desktop, never 21 (the default `max_results`, applied after filtering); `Offset:0`, `Truncated:true`, `Total` ≥ 20, and `TotalIsLowerBound:true` (once the page is full no further window is walked). | — |
| FIND_ELEMENT-03 | T0 | L | `{"text":"x","scope":"window"}`; `{"text":"x","scope":"foreground","window":"Notepad"}`; `{"text":"x","scope":"everywhere"}` | `scope=window requires window: the title of the window to search.`; `window is only used with scope=window.`; `Unknown scope 'everywhere'; expected foreground\|window\|desktop`. | — |
| FIND_ELEMENT-04 | T0 | L | `{"text":"x","kind":"button"}` | `Unknown kind 'button'; expected any\|interactive\|text\|scrollable`. | — |
| FIND_ELEMENT-05 | T0 | L | `{"text":"x","scope":"window","window":"zzz"}` | `No top-level window matching 'zzz'. Open windows: …` (Notepad listed). | — |
| FIND_ELEMENT-06 | T0 | L | `{"text":"","kind":"text","scope":"foreground"}` then with `include_offscreen:true` | Second count ≥ first. | — |
| FIND_ELEMENT-07 | T0 | L | `{"text":"","scope":"window","window":"<owned:Notepad title>"}`; `focus hwnd:<owned:Calc>`; repeat | Same window searched both times (pinned by title, unaffected by focus). | `focus` back |
| FIND_ELEMENT-08 | T0 | L | `{"text":"qqzzxx-nothing"}`; `{"text":"","kind":"scrollable"}` | `{"Matches":[],"Total":0,"Offset":0,"Truncated":false}` (no `TotalIsLowerBound` key) with `isError:false`; scrollable matches carry no `Scroll` data (documented: only the snapshot fills it). | — |
| FIND_ELEMENT-09 | T0 | L | E-4. `owned:Calc` in front; `{"text":"7","scope":"window","window":"Calculator","control_type":"Button"}` → note the match's `AutomationId` (e.g. `num7Button`); `{"automation_id":"<that id>","scope":"window","window":"Calculator"}` with no `text`; the same id with its case flipped | Every match carries `AutomationId`/`ClassName` when the control has them (keys absent otherwise); the id-only call returns exactly that button, `Total:1`; the case-flipped id returns `Matches:[]`, `Total:0` (exact, case-sensitive). | — |
| FIND_ELEMENT-10 | T0 | L | E-4. `{"control_type":"button","scope":"window","window":"Calculator"}`; `{"class_name":"<a ClassName from that result, upper-cased>","scope":"window","window":"Calculator"}`; `{"control_type":"Buton"}`; `{"kind":"text","control_type":"Button"}` | Only `ControlType:"Button"` rows (type name in any case); the upper-cased class name still matches (exact, any case); `Unknown control type 'Buton'; expected a UIA control type name: …` listing the names; `control type Button is never returned by kind text, which covers Text, Edit, Document. Drop kind, or pick one of those types.` | — |
| FIND_ELEMENT-11 | T0 | L | E-4. `{"control_type":"Group","scope":"window","window":"Calculator"}` → a group `el_G`; `{"within":"el_G","control_type":"Button"}`; `{"within":"el_G","scope":"desktop"}`; `{"within":"el_999999"}`; then a run-owned Explorer window: its pane `el_P` from `find_element`, close the window, `{"within":"el_P"}` | Only buttons inside the group (bounds within `el_G`'s), and `el_G` itself never listed; `within searches one element's subtree, so it replaces scope and window: give within alone.`; `Unknown element id 'el_999999': no such id was issued.`; `Element 'el_P' is no longer available: …` — an error, not `Matches:[]`. | close Explorer |
| FIND_ELEMENT-12 | T0 | L | E-4. `{"text":"","scope":"window","window":"Calculator","max_results":5}`; the same with `offset:5`, then `offset` = `Total`; `max_results:0`; `max_results:101`; `offset:-1` | Two disjoint, consecutive pages with the same `Total`; `Truncated:true` until `offset + 5 ≥ Total`; the page at `offset:Total` is empty with the real `Total` and `Truncated:false`; `max_results must be between 1 and 100, got 0; page through more with offset.` (same for 101); `offset must be 0 or more, got -1`. | — |

#### get_element, get_text, get_table (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| GET_ELEMENT-01 | T0 | L | `snapshot` → `el_N` of the Notepad editor; `{"element_id":"el_N"}` | `Name, ControlType, Bounds, IsEnabled, IsOffscreen` present; the returned `ElementId` is the id passed in (D-11: a lookup no longer mints a second id). | — |
| GET_ELEMENT-02 | T0 | L | `{"element_id":"el_999999"}`; `{"element_id":""}` | `Unknown element id 'el_999999': no such id was issued. Ids come from snapshot, find_element or get_state.`; the same for `''`. (Once the server has issued a million ids, `el_999999` reads as expired instead.) | — |
| GET_ELEMENT-03 | T0 | L | `snapshot` #1 → `el_A`; `find_element` → `el_F`; `snapshot` #2; `get_element el_A`; `get_element el_F` | `el_A`: `Element 'el_A' has expired: a newer snapshot replaced it, or it aged out of the element cache. …` (the message names the hidden snapshot takers and says to take a new snapshot); `el_F` resolves (find ids survive a snapshot; they leave only the 5 000-entry least-recently-used cache). | — |
| GET_ELEMENT-04 | T0 | L | `owned:Charmap`: `find_element scope:"window" window:"Character Map"` → id; close charmap (`window close`); `get_element id` | Returns an `ElementInfo` with degraded fields (empty `Name`, null `Bounds`) rather than an error — record; `assert_element` is the tool that detects deadness. | — |
| GET_TEXT-01 | T0 | L | `type "hello-<runid>"` into the editor (via `element_id`); `{"element_id":"<editor>"}` | Raw string containing `hello-<runid>`. | Ctrl+A, Delete |
| GET_TEXT-02 | T0 | L | `{"element_id":"el_999999"}`; a Button's id (Calculator) | `Unknown element id 'el_999999': …`; the button's `Name` (fallback when no ValuePattern). | — |
| GET_TEXT-03 | T0 | L | Charmap id after charmap is closed (as GET_ELEMENT-04) | `Element 'el_N' is no longer available: its window closed or the control was destroyed. Take a new snapshot and use the id it returns.` (D-10 routes element reads through the liveness probe `assert_element` uses; was DEF-048, masked). | — |
| GET_TEXT-04 | T0 | L | Paste text containing U+E000 and an emoji into Notepad; `get_text` | PUA glyph dropped, emoji intact (A-13). | clear |
| GET_TEXT-05 | T0 | L | Open a 200 KB file in Notepad (`file_dialog` block) or paste 200 KB; `get_text` | The whole text, uncapped — record length; S3 candidate against XC-05 (no cap while `snapshot` clips at 80 chars and `scrape` uses `TextCap`). | clear |
| GET_TABLE-01 | T0 | L | `owned:Edge` `snapshot use_dom:true` → the `Table` element id; `{"element_id":"<table>"}` | `{Headers:["A","B"], Rows:[["1","2"]]}`. | — |
| GET_TABLE-02 | T0 | L | The Notepad editor id; a Calculator button id | `GridPattern not supported on Document '…'.` and `GridPattern not supported on Button '…'.` (check precedes any cell read; D-10 names the control type and name, as `interact_element` does). | — |
| GET_TABLE-03 | T0 | L | `{"element_id":"el_999999"}` | `Unknown element id 'el_999999': …`. | — |
| GET_TABLE-04 | T0 | L | `start_process taskmgr.exe` is **not** used (elevated); instead an Explorer window on `<scratch>` in Details view: `start_process explorer.exe <scratch>`; `snapshot` → the items list id; `get_table` | Either `Headers` of `""` per column (GridPattern without TablePattern) or real headers; `Rows[i].length == Headers.length`. | close Explorer window |
| GET_TABLE-05 | T0 | L | A large grid (Explorer Details on `C:\Windows\System32`, run-owned window) — time it | Record elapsed and outcome: a very long call or a masked `An error occurred invoking 'get_table'.` from a virtualised `GetItem`. Either is filed S3: no cap, no cancellation in the `rows × cols` loop. | close |
| GET_TABLE-06 | T0 | L | Liveness after GET_TABLE-05 | `system_info os` answers. | — |

#### assert_element (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| ASSERT_ELEMENT-01 | T0 | L | Editor id: `state:"exists"`, `"enabled"`, `"visible"` | `PASS` ×3. | — |
| ASSERT_ELEMENT-02 | T0 | L | `interact_element focus` on the editor, then `state:"focused"`; then `focused` on a title-bar button id | `PASS`; `FAIL: focused — observed focus is on Document '…'`. | — |
| ASSERT_ELEMENT-03 | T0 | L | Type `stamp-<runid>`; `state:"value" expected:"stamp-<runid>"`; `expected:"other"` | `PASS`; `FAIL: value — observed value is 'stamp-<runid>' (from ValuePattern)` (or `(from Name)` — record). | clear |
| ASSERT_ELEMENT-04 | T0 | L | Editor id `state:"checked"`; the probe page's checkbox id (`use_dom`) `state:"checked"` | `FAIL: checked — observed no TogglePattern on Document '…'`; `PASS` for the checked checkbox. | — |
| ASSERT_ELEMENT-05 | T0 | L | `state:"glowing"`; `state:"value"` without `expected`; `state:"exists" expected:"x"` | `Unknown assertion state 'glowing'; expected exists\|enabled\|checked\|value\|visible\|focused.`; `'value' requires expected: the text to compare against.`; `expected is only used with state=value.` | — |
| ASSERT_ELEMENT-06 | T0 | L | `element_id:"el_999999" state:"exists"`; same with `state:"enabled"`; a snapshot id after the next snapshot with `state:"exists"` | `FAIL: exists — observed unknown element id` (not an error); `Unknown element id 'el_999999': …` (error); `FAIL: exists — observed element id has expired (take a new snapshot)` (D-11). | — |
| ASSERT_ELEMENT-07 | T0 | L | Charmap id after charmap closed: every state | `FAIL: <state> — observed element no longer available` for all six, never an exception. | — |
| ASSERT_ELEMENT-08 | T0 | L | Minimise `owned:Notepad`; editor id `state:"visible"` | `FAIL: visible — observed offscreen` (or `empty bounds`) — record which modern Notepad reports. | restore |

#### interact_element

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| INTERACT_ELEMENT-01 | T2 | L | `owned:Calc`: id of button `Seven` → `action:"click"`; then `"invoke"` on `Plus`, `"click"` on `Eight`, `"invoke"` on `Equals` | `{Method:"InvokePattern"}` each; the display's Name (`get_text` on the result element) ends in `15`. | `Escape`/`C` |
| INTERACT_ELEMENT-02 | T2 | L | Probe-page checkbox id → `action:"toggle"` twice | `Method:"TogglePattern"`; `assert_element checked` flips FAIL→PASS→FAIL. | — |
| INTERACT_ELEMENT-03 | T2 | L | Probe-page `<select>` id → `action:"select" value:"<option 2 name>"`; then `value:"no-such-option"` | `Method:"SelectionItemPattern"` (or the fallback named); the miss: an error naming the item; **record whether the drop-down was left expanded** (the catalog says it expands before it can fail) — S3 candidate. | `key esc` |
| INTERACT_ELEMENT-04 | T2 | L | Editor id → `action:"type" value:"typed-<runid>"`; `action:"focus"` | `Method` is `ValuePattern` or `Keyboard`; `get_text` contains the stamp; `focus` → `assert_element focused` PASS. | clear |
| INTERACT_ELEMENT-05 | T2 | L | Editor id → `action:"toggle"`; a `Text` element id → `action:"invoke"`; a Button id → `action:"select"` | `TogglePattern not supported on Document '…'.`, `InvokePattern not supported on Text '…'.`, `SelectionItemPattern not supported on Button '…'.` — caller-facing since D-10 (an `InvalidOperationException`; was DEF-042, masked). | — |
| INTERACT_ELEMENT-06 | T2 | L | `action:"dance"`; `element_id:"el_999999" action:"click"` | An error naming the action and listing the ten (`expected click\|invoke\|toggle\|select\|focus\|type\|expand\|collapse\|set_value\|scroll_into_view`; checked before the element is looked at); `Unknown element id 'el_999999': …`. | — |
| INTERACT_ELEMENT-07 | T2 | L | An element with no InvokePattern/SelectionItem/Toggle (a `Text` in Notepad) → `action:"click"` | `Method:"PhysicalClick"` (the fallback fires; the description promises it). | — |
| INTERACT_ELEMENT-08 | T2 | L | Charmap id after charmap closed → `action:"click"` | `Element 'el_N' is no longer available: … Take a new snapshot and use the id it returns.` (D-10; same family as GET_TEXT-03, was DEF-048). | — |
| INTERACT_ELEMENT-09 | T2 | L | Probe-page `<select>` id → `action:"expand"`; `"expand"` again; `"collapse"`; then `"expand" value:"x"`; a `Text` element id → `"expand"` | `Method:"ExpandCollapsePattern"` with `Detail` `now Expanded`, then `already Expanded` (nothing flipped), then `now Collapsed` (E-2); `'expand' takes no value.` (`value:""` is accepted as none); `ExpandCollapsePattern not supported on Text '…'.` **Record whether Edge came to the front** (a provider may activate the window). | `key esc` |
| INTERACT_ELEMENT-10 | T2 | L | Probe-page `Probe range` id → `action:"set_value" value:"7"`; `value:"11"`; `value:"abc"`; `value:"7,5"` | `Method:"RangeValuePattern"`, `Detail:"now 7 (range 0 to 10)"` (the value re-read); `set_value on Slider '…' needs a number between 0 and 10; 11 is outside that range.`; `… needs a number between 0 and 10, with '.' as the decimal point; got 'abc'.`; `7,5` refused the same way (invariant format). Record the control type Chromium reports if it is not `Slider`. | `set_value 2` |
| INTERACT_ELEMENT-11 | T2 | L | Probe-page `q` input id → `action:"set_value" value:"set-<runid>"`; a Button id → `"set_value" value:"x"`; `"set_value"` with no value | `Method:"ValuePattern"`, `Detail:"now 'set-<runid>'"` (read back, whole value replaced, no keystrokes); `RangeValuePattern or ValuePattern not supported on Button '…'.`; `'set_value' requires a value: …`. | `set_value prefilled` |
| INTERACT_ELEMENT-12 | T2 | L | `owned:Edge` scrolled to the top: `find_element "Last paragraph." include_offscreen:true scope:"window" window:"WindowsMcpE2E Probe Page"` → id → `action:"scroll_into_view"`; then the editor id in `owned:Notepad` (on screen) → `"scroll_into_view"`; then `"scroll_into_view" value:"x"` | `Method:"ScrollItemPattern"`, `Detail` `now on screen, centre (x,y)` and the page's `[v: N%]` moved off 0 (or `the control still reports offscreen` — record); Notepad: `ScrollItemPattern` or `Method:"None"` with `already on screen, centre (x,y)`; `'scroll_into_view' takes no value.` No pointer movement in any of them. When the target lacks the pattern and is off screen the error names the nearest scrollable container as `scroll with element_id el_N (…)` — record it if seen. | scroll page to top |

#### multi_select, multi_edit (batch)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| MULTI_SELECT-01 | T2 | L | Explorer window on `<scratch>\\sel` (three files) via `start_process`; `snapshot scope:window` → three item centres; `{"targets_json":"[{\"x\":..,\"y\":..},{..},{..}]"}` | `{count:3, ctrl:true, results:[3× ok:true]}`, no `failedIndex`; a fresh `snapshot` shows three selected items. | `key esc`; close Explorer |
| MULTI_SELECT-02 | T2 | L | Same three by `{"element_id":"el_N"}`; and the array passed as a JSON *string* | `results[i].elementId` equals the id **passed** (not re-minted), `name` = item name; the stringified form accepted. | — |
| MULTI_SELECT-03 | T0 | L | `"[]"`; `"{"`; `"{\"x\":1}"` | `targets_json must hold at least one target.`; `targets_json must be a JSON array of targets; it did not parse: …`; `targets_json must be a JSON array of targets, got Object.` | — |
| MULTI_SELECT-04 | T0 | L | `[{"x":1,"y":2},{"x":3}]`; `[{"x":1,"y":2,"element_id":"el_1"}]` | `targets_json[1]: x and y must be given together.`; `targets_json[0]: give either x and y or element_id, not both.` | — |
| MULTI_SELECT-05 | T2 | L | Record the cursor; `[{valid point},{"element_id":"el_999999"}]` | Error `Unknown element id 'el_999999': …`; the cursor has **not** moved and nothing was clicked (resolve-all-before-any-click). | — |
| MULTI_SELECT-06 | T2 | L | `[{valid point},{"x":999999,"y":999999}]`; then `key a` into the editor | A **non-error** result: `results.length 1`, `failedIndex 1`, `error` containing `is not on any monitor`; the `a` arrives as a plain `a` (Ctrl was released by the `finally`). | clear |
| MULTI_SELECT-07 | T2 | L | Explorer window moved to display 0; two items by negative `x` | Success with the negative coordinates echoed. | move back, close |
| MULTI_EDIT-01 | T2 | L | Editor id: `[{"element_id":"<ed>","text":"alpha"},{"element_id":"<ed>","text":"beta","clear":true}]` | `{count:2, results:[2× ok:true, method:"keys", typed:5/4]}`; `get_text` == `beta`. | clear |
| MULTI_EDIT-02 | T0 | L | `[{"x":10,"y":10}]`; `[{"x":10,"y":10,"text":42}]`; `"[]"`; `"[1,2]"` | `entries_json[0] needs text (a string).`; `entries_json[0].text must be a string.`; `entries_json must hold at least one target.`; `entries_json[0] must be an object ({x,y} or {element_id}), got Number.` | — |
| MULTI_EDIT-03 | T2 | L | Record editor text; `[{valid,"text":"x"},{"element_id":"el_999999","text":"y"}]` | Error `Unknown element id 'el_999999': …`; editor text unchanged; cursor unmoved. | — |
| MULTI_EDIT-04 | T2 | L | `[{valid,"text":"first"},{"x":999999,"y":999999,"text":"second"}]` | Non-error, `results.length 1`, `failedIndex 1`, `first` **is** in the editor (not atomic, not rolled back). | clear |
| MULTI_EDIT-05 | T2 | L | `clipboard set "SENTINEL"`; entries with `text` of 199 chars, then 200 chars | `method:"keys"` then `method:"paste"` with `clipboardRestored:true`; `clipboard get` → `SENTINEL`. | clear; restore clipboard |
| MULTI_EDIT-06 | T2 | L | `[{editor,"text":"line1\nline2","press_enter":true}]` | Two lines plus a trailing newline; `method:"keys"`. | clear |
| MULTI_EDIT-07 | T2 | L | Pre-fill `KEEPME`; `[{editor,"text":"replaced","clear":true}]` | `get_text` == `replaced`. | clear |

#### file_dialog

The tool validates nothing: no absolute-path check, no dialog-present check, no non-empty check,
and it always answers `typed path into focused dialog`. Every row below opens the dialog first and
closes it with `key esc`; **never press Enter** after `file_dialog` in a test-only run.

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| FILE_DIALOG-01 | T2 | L | `owned:Notepad` in front; `shortcut ctrl+shift+s`; `wait_for condition:"active_window" text:"Save As"`; `{"path":"<scratch>\\dlg-<runid>.txt"}`; `snapshot scope:"window" window:"Save As"` | `typed path into focused dialog`; the file-name Edit's value ends with `dlg-<runid>.txt`; the dialog is **still open** (no Enter sent). | `key esc`; file absent |
| FILE_DIALOG-02 | T2 | L | Dialog open with its pre-filled name; `{"path":"abc"}` | The field holds the pre-existing text with `abc` inserted at the caret (no clear, caret idle). | `key esc` |
| FILE_DIALOG-03 | T2 | L | **No dialog**, editor focused; `{"path":"C:\\temp\\x.txt"}` | Same success string; the path lands **in the document** — record as S3 (the tool cannot detect a wrong target). | Ctrl+Z / clear |
| FILE_DIALOG-04 | T2 | L | `{"path":""}` | Success string, nothing typed (no non-empty check — record). | — |
| FILE_DIALOG-05 | T2 | L | `clipboard set "SENTINEL"`; dialog open; a 250-char path; then a 150-char path | 250: pasted, `clipboard get` → `SENTINEL` restored; 150: keys. The result string does not say which — record. | `key esc`; restore clipboard |
| FILE_DIALOG-06 | T0 | S | `--call file_dialog '{}'` | The SDK's missing-required-argument shape (record; compare HOST-28). | — |

#### wait_for (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| WAIT_FOR-01 | T0 | L | `owned:Notepad` in front; `{"text":"Notepad","condition":"active_window","timeout_ms":3000}` | `{Satisfied:true, Condition:"active_window", Attempts:1}`; `Detail` starts `active window is '` and ends `(exact)` or `(substring)`; no `Element`. | — |
| WAIT_FOR-02 | T0 | L | `{"text":"","condition":"element_enabled","kind":"text","scope":"window","window":"<title>"}` | `Satisfied:true`; `Detail` matches `found '…' (el_N), enabled`; `Element.ElementId` resolves via `get_element`. | — |
| WAIT_FOR-03 | T0 | L | `{"text":"never-appears-<runid>","timeout_ms":600,"interval_ms":200}` | **`isError:false`**; `{Satisfied:false, Attempts ≥ 2, ElapsedMs ≥ 600, Detail:"no element matching 'never-appears-<runid>'"}`; body is not the string `null`. | — |
| WAIT_FOR-04 | T0 | L | `timeout_ms:0`; `interval_ms:0` with `timeout_ms:200` | `Attempts == 1`; completes in ≈ 200 ms with bounded `Attempts` (10 ms floor). | — |
| WAIT_FOR-05 | T0 | L | `timeout_ms:120001`; `timeout_ms:-1`; `interval_ms:5001` | `timeout_ms must be between 0 and 120000, got 120001`; same shape for −1; `interval_ms must be between 0 and 5000, got 5001`. | — |
| WAIT_FOR-06 | T0 | L | `{"text":"","condition":"nope","timeout_ms":-5}`; `{"text":"","condition":"nope"}`; `{"text":"  ","condition":"window"}` | The timeout message wins; then `Unknown condition 'nope'; wait_for returns when one of these appears or holds: element_exists\|element_enabled\|focused_element\|text_exists\|active_window (aliases element\|enabled\|focused\|text\|window)`; then `active_window needs text: what to look for.` (alias resolved, canonical name used). | — |
| WAIT_FOR-07 | T0 | L | The same wait with `condition` = `element`, `enabled`, `focused`, `text`, `window` | `Condition` echoes the canonical snake_case name each time. | — |
| WAIT_FOR-08 | T0 | L | `owned:Edge`: `{"text":"Probe heading","condition":"text_exists","scope":"window","window":"WindowsMcpE2E Probe Page","use_dom":true,"timeout_ms":5000}`; same with `use_dom:false`; then type `zz-<runid>` into Notepad and `text_exists` for it | `Satisfied:true`, `Detail` starts `found in page '`; `Satisfied:false` with `'Probe heading' not found anywhere on screen`; the Notepad body case is `Satisfied:false` (documented B-6 deviation: typed document content is not evidence) while `find_element kind:"text"` finds it. | clear |
| WAIT_FOR-09 | T0 | L | `snapshot` → `el_A`; a 3 s `text_exists` wait that never satisfies; `get_element el_A` | `Element 'el_A' has expired: … These take a snapshot too: wait_for with a text or focus condition, …` — every `text_exists`/`focused_element` poll is a snapshot and evicts ids; the `wait_for` and `snapshot` descriptions now say so. | — |
| WAIT_FOR-10 | T0 | L | E-4. `owned:Calc` in front; `{"text":"","automation_id":"<num7Button id from FIND_ELEMENT-09>","scope":"window","window":"Calculator","timeout_ms":2000}`; `{"text":"7","condition":"text_exists","automation_id":"x"}`; `{"text":"x","control_type":"Buton","timeout_ms":5000}`; `{"text":"x","within":"el_999999","timeout_ms":5000}`; `{"text":"","within":"<any live el_N>"}`; `{"text":"","automation_id":"nope-<runid>","timeout_ms":300}` | `Satisfied:true`, `Element.AutomationId` is the id; `automation_id only applies to element_exists and element_enabled, not text_exists.`; the bad control type and the unknown id are refused **at once** (well under the 5 s timeout), not `every poll failed`; `within` alone still needs text: `element_exists needs text: what to look for.`; the miss is `Satisfied:false` with `Detail:"no element matching ''"` (does not name the filter — parity D-33). | — |

### 7.2 Input, windows, monitors, launch, clipboard

Every input row runs attended against a run-owned window (§7.1 fixtures) and ends by hovering to
the primary monitor's centre, `(1280, 720)`, so no tooltip or auto-hide bar is left open. The
foreground window recorded in the ledger is restored at the end of each block with
`focus hwnd:<ledger hwnd>`. Verbatim messages come from `Tools/InputTools.cs`,
`Services/InputService.cs`, `Services/UiTree/ElementTarget.cs`, `Services/ShortcutParser.cs`,
`Services/TypePlanner.cs`, `Tools/WindowTools.cs`, `Services/WindowService.cs`,
`Services/WindowMatcher.cs`, `Services/WindowGeometry.cs`, `Services/AppCatalog.cs` on 0.7.3.

Virtual-screen constants for this machine (§4.2): `minX −2560`, `maxX 2559`, `minY 0`,
`maxY 1439`; display 0 centre `(−1280, 720)`, display 1 centre `(1280, 720)`.

#### click

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| CLICK-01 | T2 | L | Centre of `owned:Notepad` `Bounds`, `clicks:1` | `{action:"click", x, y, button:"left", clicks:1}`; Notepad still foreground. | — |
| CLICK-02 | T2 | L | Same point `clicks:0`; `clicks:2` on the editor; `button:"right"` `clicks:1` then `key esc` | `action:"hover"` with `clicks:0`; a double-click selects the word; a context menu opens and closes. | `key esc` |
| CLICK-03 | T0 | L | `clicks:-1`; `{}`; `{"x":10,"y":10,"element_id":"el_0"}`; `{"x":10}`; `button:"top"` | `clicks must be 0 (hover) or more, got -1`; `Give a target: coordinates x and y, or element_id.`; `Give either coordinates (x and y) or element_id, not both.`; `x and y must be given together (coordinates in virtual-desktop pixels).`; `Unknown button 'top'; expected left\|right\|middle`. | — |
| CLICK-04 | T2 | L | `element_id` of the editor; `element_id:"el_999999"`; the id of an element inside a minimised window | Click lands on the centre and `elementId` echoes the id passed; `Unknown element id 'el_999999': …`; `Element el_N ('…') is off-screen: bring it into view with interact_element scroll_into_view (or scroll its container, or focus its window), then take a new snapshot.` (E-2; the name is clipped at 80 characters). For a minimised window the remedy that applies is the last one — restore or `focus` the window; `scroll_into_view` cannot bring a minimised window back. | restore |
| CLICK-05 | T2 | L | Display 0: `{"x":-1280,"y":720,"clicks":0}`; `{"x":-2559,"y":1,"clicks":0}` | Both succeed with the negative coordinates echoed (D-3 "every monitor centre reached exactly"). | hover home |
| CLICK-06 | T2 | L | `{"x":-3560,"y":-1000,"clicks":0}`; `{"x":2560,"y":720,"clicks":0}` | `(-3560,-1000) is not on any monitor: the cursor landed at (…). The virtual screen spans x -2560..2559, y 0..1439 in physical pixels with the origin at the primary monitor's top-left; see multi_monitor for each monitor's bounds.`; `x:2560` (one past the edge) refused the same way. | hover home |
| CLICK-07 | T2 | L | `clicks:4` on empty desktop area of display 0 | Accepted (no upper cap) — record; S4 candidate. | — |
| CLICK-08 | T2 | L | E-3. A run-owned list with five rows: click row 1, then `{"element_id":<row 4>,"modifiers":"shift"}`; then `{"element_id":<row 5>,"modifiers":"Control"}` | Rows 1–4 selected (range), then row 5 added; results carry `modifiers:"shift"` and `modifiers:"ctrl"`; a plain `click` carries `modifiers:null`. Afterwards `key a` in the editor types a lower-case `a` (no Shift or Ctrl left down). | clear selection |
| CLICK-09 | T0 | L | `modifiers:"win"`; `modifiers:"ctrl+"`; `modifiers:"hyper"`; `{"clicks":0,"modifiers":"shift"}` — each with a valid target | `Unknown modifier 'win'; expected ctrl\|shift\|alt joined with '+', e.g. ctrl+shift (win is not supported).`; `modifiers 'ctrl+' has an empty part; …`; `Unknown modifier 'hyper'; …`; `modifiers need a click: clicks 0 only moves the pointer, …`. No pointer movement and no key event for any of them. | — |
| CLICK-10 | T2 | L | `{"x":-3560,"y":-1000,"modifiers":"alt"}` | The CLICK-06 off-monitor refusal, with no Alt tap (no menu-bar accelerator underlines appear in the foreground window). | hover home |

#### type

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| TYPE-01 | T2 | L | Editor focused; `{"text":"hello"}` | `{typed:5, method:"keys"}`; `get_text` contains `hello`. | clear |
| TYPE-02 | T2 | L | `{"text":"line","clear":true,"press_enter":true}`; `{"text":"A","caret":"start"}`; `{"text":"Z","caret":"end"}` | Editor holds `line⏎`; then `Aline⏎`; then `Aline⏎Z`. | clear |
| TYPE-03 | T2 | L | `clipboard set "SENTINEL"`; `text` of 199 × `a`; then 200 × `a` | `method:"keys"`; `method:"paste"`, `clipboardRestored:true`; `clipboard get` → `SENTINEL`. | clear; restore clipboard |
| TYPE-04 | T2 | L | 300 chars containing `\r\n` | `method:"keys"` (a CR forces keys, documented B-1 deviation). | clear |
| TYPE-05 | T0 | L | `caret:"middle"`; `pace_ms:-1` | `Unknown caret 'middle'; expected idle\|start\|end`; `pace_ms must be 0 or more, got -1`. | — |
| TYPE-06 | T2 | L | `element_id:"el_999999"`; the off-screen id | `Unknown element id 'el_999999': …`, nothing typed; the off-screen refusal before a single character. | — |
| TYPE-07 | T2 | L | Move `owned:Notepad` to display 0; `snapshot`; `type text:"abc" element_id:<editor>` | The pre-click lands on display 0 (result `x` negative) and `abc` arrives. | move back; clear |
| TYPE-08 | T2 | L | `{"text":""}`; `{"text":"tab\there","pace_ms":0}` | `typed:0`; the tab becomes a Tab keypress; pace 0 accepted. | clear |

#### key, shortcut

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| KEY-01 | T2 | L | `type "ab"`; `{"key":"backspace"}` | `pressed backspace`; `get_text` → `a`. | clear |
| KEY-02 | T0 | L | `{"key":"ctrl+c"}`; `{"key":""}`; `{"key":"supercalifragilistic"}`; `{"key":"f25"}` | `'ctrl+c' looks like a chord; use the shortcut tool for key combinations.`; `Key name is empty.`; `Unknown key 'supercalifragilistic'. Use a character, a-z, 0-9, f1-f24, or a named key such as enter, tab, esc, backspace, delete, up, down, left, right, home, end, pageup, pagedown, win, printscreen, plus.`; `Unknown key 'f25'…`. | — |
| KEY-03 | T2 | L | `{"key":"f24"}`; `{"key":"numpad5"}` into the editor; `{"key":"plus"}` | `pressed f24`; `5` (NumLock on) or a cursor move; `+` appears (US layout) or `'plus' … has no key on the active keyboard layout`. | clear |
| KEY-04 | T2 | L | `{"key":"win"}`; `{"key":"esc"}` | Start opens then closes; `window active` afterwards is the ledger's foreground (restore if not). | `focus` back |
| KEY-05 | T2 | L | `{"key":"delete"}` with `owned:Notepad` focused and empty | `pressed delete`; `window active` unchanged (focus never moved). | — |
| KEY-06 | T2 | L | `{"key":"A"}` and `{"key":"a"}` | Both accepted; the editor shows `A` and `a` (layout-independent letters). | clear |
| KEY-07 | T2 | L | `{"key":"printscreen"}` | `pressed printscreen`; record whether the Snipping overlay opened; `key esc` if so. | `key esc` |
| KEY-08 | T2 | L | E-3. Editor holding 12 lines, caret on line 1: `{"key":"down","repeat":10}` | `pressed down 10 times`; the caret is on line 11 (one call, the parity bar). | clear |
| KEY-09 | T2 | L | `{"key":"x","hold_ms":1000}` in the editor | `held x for 1000 ms`; the editor shows one `x` (held, not auto-repeated). | clear |
| KEY-10 | T0 | L | `repeat:0`; `repeat:101`; `hold_ms:-1`; `hold_ms:10001`; `{"repeat":2,"hold_ms":10}`; `{"key":"shift","hold_ms":100}`; `{"key":"lctrl","repeat":3}`; `{"key":"?","hold_ms":100}` (US layout); `{"key":"nope","repeat":2}` | `repeat must be between 1 and 100, got …`; `hold_ms must be between 0 and 10000, got …`; `repeat and hold_ms cannot be combined: …`; `Cannot hold 'shift': a modifier is only held around an action. …`; `Cannot repeat 'lctrl': … can trigger Sticky Keys. …`; `Cannot hold '?': on this keyboard layout it needs a modifier, …`; `Cannot press 'nope': Unknown key 'nope'. …`. Nothing is pressed for any of them. | — |
| SHORTCUT-01 | T2 | L | `type "abc"`; `ctrl+a`; `ctrl+c`; `clipboard get` | `pressed ctrl+a`, `pressed ctrl+c`; clipboard holds `abc`. | restore clipboard |
| SHORTCUT-02 | T2 | L | `ctrl+shift+s` on Notepad; `wait_for active_window "Save As"`; `esc` | The dialog appears and closes. | — |
| SHORTCUT-03 | T2 | L | `ctrl+1`; `win` then `esc`; `CTRL + C` (case and spaces) | `pressed …` each; Start opens/closes; the spaced form behaves as `ctrl+c`. | — |
| SHORTCUT-04 | T0 | L | `ctrl+foo`; `""`; `"+"`; `ctrl+` | `Unknown key 'foo' in 'ctrl+foo'. …`; `Shortcut is empty.`; `Shortcut '+' names no key. Write 'plus' for the + key.`; `Key name is empty.` or the names-no-key message — record. | — |
| SHORTCUT-05 | T2 | L | `ctrl+plus`; `ctrl+shift+ctrl+a` (duplicate modifier) | `pressed ctrl+plus` (Shift implied on US); de-duplicated, `pressed …`. | — |
| SHORTCUT-06 | T2 | L | `alt+f4` **only** on `owned:Charmap` (the run's own window, last) | Charmap closes; `window list` no longer has it. | — |
| SHORTCUT-07 | T2 | L | `f24`; `numpad5` as chords with no modifier | Accepted (a single part is legal). | — |
| SHORTCUT-08 | T2 | L | Concurrency guard: `shortcut ctrl+a` then immediately `key a` | The `a` replaces the selection (no modifier stuck). | clear |

#### drag, hover, scroll

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| DRAG-01 | T2 | L | Editor holds `abcdefghij`; drag across the line; `shortcut ctrl+c`; `clipboard get` | `{fromTarget:"point", steps:20, durationMs:300, button:"left"}`; the clipboard holds the selected substring. | clear; restore clipboard |
| DRAG-02 | T2 | L | `hover` a point; `{"to_x":..,"to_y":..}` (no origin) | `fromTarget:"cursor"` with `fromX/fromY` equal to the hovered point. | — |
| DRAG-03 | T0 | L | `{"from_x":10,"from_y":10}`; `duration_ms:10001`; `steps:1`; `steps:201`; `{"to_x":5}`; `{"from_x":5,"to_x":1,"to_y":2}` | `A drag needs a destination: to_x and to_y, or element_id.`; `duration_ms must be between 0 and 10000, got 10001`; `steps must be between 2 and 200, got 1`; `… got 201`; `to_x and to_y must be given together (coordinates in virtual-desktop pixels).`; `from_x and from_y must be given together …`. | — |
| DRAG-04 | T2 | L | `duration_ms:0 steps:2`; `duration_ms:10000 steps:200` (short distance, run once) | Both accepted; the second takes ≈ 10 s. | — |
| DRAG-05 | T2 | L | `button:"middle"` with valid points | `Middle-button drag is not supported (the input library has no middle-button press and release); use left or right.` naming `button` — an `ArgumentException` since D-10 (was DEF-039, masked); nothing moves. | — |
| DRAG-06 | T2 | L | Drag `owned:Notepad`'s title bar from display 1 to display 0 (`to_x:-1800,to_y:300`); `window list` | `MonitorIndex` of the window becomes 0. | `set_bounds` back |
| DRAG-07 | T2 | L | `from` = primary centre, `to_x:3000,to_y:720` | `is not on any monitor` message; then a plain `click clicks:0` behaves normally (button released by the `finally`). | hover home |
| DRAG-08 | T2 | L | `element_id` = Paint canvas (`owned:Paint` via `launch mspaint`), `from_element_id` the same | A stroke appears (a screenshot of the region differs from before). | close Paint without saving |
| HOVER-01 | T2 | L | `{"x":1280,"y":720}`; `duration_ms:500`; `duration_ms:-5` | `hovered at (1280,720) for 0ms`; returns after ≈ 500 ms `for 500ms`; `-5` accepted, returns at once (record). | — |
| HOVER-02 | T2 | L | Every monitor centre from `multi_monitor`: `(−1280,720)` and `(1280,720)` | Both succeed (D-3 "Done when"). | — |
| HOVER-03 | T2 | L | Corners: `(−2560,0)`, `(2559,1439)`; then `(2560,1439)` and `(−2561,0)` | The two corners succeed; the two past-the-edge points refuse with `is not on any monitor`, `the cursor landed at`, `The virtual screen spans`. | hover home |
| HOVER-04 | T2 | L | Seam: `(−1,720)` then `(0,720)`; `screenshot` metadata `cursor.monitorIndex` after each | 0 then 1 (the seam pixel belongs to the right-hand monitor). | — |
| HOVER-05 | T2 | L | `duration_ms:600000` — **not run**; record as S3 by inspection | `hover` has no cap and passes no cancellation token (contrast `wait`'s 60 s ceiling): a ten-minute blocking call is accepted. File without executing. | — |
| HOVER-06 | T2 | L | `{"x":1280}` (missing `y`) on S | The SDK's missing-required-argument shape (record). | — |
| HOVER-07 | T2 | L | Hover over the taskbar clock for 1 s | A tooltip appears; hovering home dismisses it. | hover home |
| SCROLL-01 | T2 | L | Editor with 200 lines; hover over it; `{"direction":"down"}`; `snapshot` scroll percent | `{direction:"down", amount:3, target:"cursor", x, y}`; the editor's `[v: N%]` increased. | `shortcut ctrl+home` |
| SCROLL-02 | T2 | L | `{"direction":"up","element_id":"<editor>"}`; `{"direction":"DOWN","x":..,"y":..}` | `target:"element"` with id and name; `direction` echoed as written (`DOWN`) and accepted. | — |
| SCROLL-03 | T0 | L | `{"direction":"down","shift_wheel":true}`; `{"direction":"sideways"}`; `{"direction":"down","x":100}` | `shift_wheel is the horizontal scroll: use it with left or right only.` (cursor did not move); `Invalid direction: 'sideways'` (cursor did not move); `x and y must be given together …`. | — |
| SCROLL-04 | T2 | L | `amount:0`; `amount:-3` | Accepted, nothing moves; accepted and scrolls **up** — record as S4 (unvalidated `amount`). | `ctrl+home` |
| SCROLL-05 | T2 | L | `{"direction":"right","shift_wheel":true}` on a wide `owned:Edge` page | `shiftWheel:true`; the page scrolls sideways or not (app's choice; the response reports what was sent). | — |
| SCROLL-06 | T2 | L | `owned:Notepad` on display 0: `{"direction":"down","x":-1800,"y":600}`; then `{"direction":"down","x":-3560,"y":-1000}` | Success with the negative point echoed; `is not on any monitor`. | move back |
| SCROLL-07 | T2 | L | `{"direction":"down","element_id":"el_999999"}` | `Unknown element id 'el_999999': …`. | — |

#### focus, switch_to_window

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| FOCUS-01 | T2 | L | `owned:Calc` in front; `focus {"hwnd":<owned:Notepad Hwnd>}` | `{Window.Hwnd == that, MatchStrategy:"hwnd", Success:true, Strategy:<rung>}`; `window active` agrees. | `focus` ledger hwnd |
| FOCUS-02 | T2 | L | `focus {"title":"notepad"}`; `{"title":"<exact title>"}`; `{"title":"notpad"}` (typo) | `MatchStrategy` `substring`, `exact`, `fuzzy` with `Score ≥ 70`. | — |
| FOCUS-03 | T0 | L | `focus {}`; `{"title":"   "}` | `Give a title (exact, substring or fuzzy) or an hwnd from window list.` both. | — |
| FOCUS-04 | T0 | L | `{"hwnd":999999}`; `{"title":"definitely-no-such-window-xyz"}` | `No top-level window has handle 999999 (0xF423F). Open windows: …`; `No top-level window matching 'definitely-no-such-window-xyz'. Open windows: … . Nearest: '…' scored N (below 70).` | — |
| FOCUS-05 | T2 | L | Minimise `owned:Notepad`; `focus hwnd` | `Restored:true`, `Success:true`; the window is no longer minimised. | — |
| SWITCH_TO_WINDOW-01 | T2 | L | Same as FOCUS-01 via `switch_to_window` | Identical result shape (the two are one implementation). | — |
| SWITCH_TO_WINDOW-02 | T2 | L | `hwnd` **and** a wrong `title` together | `MatchStrategy:"hwnd"` (hwnd wins). | — |
| SWITCH_TO_WINDOW-03 | T2 | L | A window on display 0 | Foreground changes; `window active` `MonitorIndex` 0. | — |
| SWITCH_TO_WINDOW-04 | T2 | L | Target a window whose process refuses foreground (an elevated window if one is open; else SKIPPED) | `Success:false, Strategy:null` as **data**, not an error. | — |
| SWITCH_TO_WINDOW-05 | T0 | L | `{"title":"z"}` (one character matching many by fuzzy) | Either a match with `Score` or the `Nearest:` refusal — record the tie-break. | — |

#### window

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| WINDOW-01 | T0 | L | `{"action":"list"}`; `{"action":"active"}` | `ZOrder` contiguous from 0; at most one `IsActive:true`; every `MonitorIndex` is −1 or 0/1; no `Program Manager`; `Title` sanitised; `active.Hwnd` equals the `IsActive` entry. | — |
| WINDOW-02 | T0 | L | `list include_minimized:false` after minimising `owned:Notepad`; `list include_hidden:true` | The minimised window absent; the hidden form is a superset of the default. | restore |
| WINDOW-03 | T0 | L | `{"action":"desktops"}` | `{current, all}`; `current` (when non-null) is the single `IsCurrent:true`; ids are lower-case dashed GUIDs; every `DesktopId` in `list` is one of `all`'s ids. | — |
| WINDOW-04 | T2 | L | `{"action":"set_bounds","hwnd":H,"x":100,"y":100,"width":800,"height":600}`; then `list` | `After == {100,100,800,600}` and `list` reports the same rect; `MatchStrategy:"hwnd"`. | `set_bounds` to `Before` |
| WINDOW-05 | T0 | L | `move hwnd:H x:10`; `resize hwnd:H width:800`; `set_bounds hwnd:H x:1 y:1 width:1`; `resize hwnd:H width:0 height:100` | `'move' needs x and y (the new top-left, in virtual-desktop pixels); use resize for a size or set_bounds for both.`; `'resize' needs width and height; use move for a position or set_bounds for both.`; `'set_bounds' needs all four of x, y, width and height; use move or resize for one pair.`; `width must be positive, got 0`. | — |
| WINDOW-06 | T2 | L | `maximize hwnd:H`; `move hwnd:H x:50 y:50`; then with `restore_first:true`; `maximize` again | `Window '…' is Maximized; moving or resizing it would be undone by Windows. Pass restore_first:true to restore it first.` (and the window is still maximised — XC-02); then `Restored:true` and moved. | `set_bounds` to `Before` |
| WINDOW-07 | T0 | L | `{"action":"close"}`; `{"action":"teleport"}`; `{"action":"minimize","hwnd":999999}`; `{"action":" list "}` | `'close' needs a title or an hwnd; only list, active and desktops work without one`; `Unknown action 'teleport'; expected list\|active\|desktops\|minimize\|maximize\|restore\|close\|move\|resize\|set_bounds`; `No top-level window has handle 999999 (0xF423F). Open windows: …`; padded action refused the same way (not trimmed — record). | — |
| WINDOW-08 | T2 | L | `set_bounds hwnd:H x:-2520 y:40 width:800 height:600`; `list`; `set_bounds` back; `list` | `After.X` negative; `MonitorIndex` 0 after, 1 after the restore. Straddle the seam: `x:-400` → `MonitorIndex` decided by the window **centre** (record). | restore |
| WINDOW-09 | T2 | L | `minimize hwnd:H`; `list` | `State:"Minimized"`, `MonitorIndex:-1`; `restore` → `Normal`. | — |
| WINDOW-10 | T1 | L | `close hwnd:<owned:Charmap>` | `{"Action":"close","Success":true,"MatchStrategy":"hwnd"}`; gone from `list`. Never against a window the run did not open. | — |

#### multi_monitor (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| MULTI_MONITOR-01 | T0 | L | `{}` | Two entries; `Index` 0,1 contiguous; exactly one `IsPrimary:true` at `X:0,Y:0`; each `Scale == EffectiveDpi/96`; `WorkArea` inside its bounds and no taller. | — |
| MULTI_MONITOR-02 | T0 | L | Compare with §4.2 | Identical (or the plan's table is updated before any coordinate row runs). | — |
| MULTI_MONITOR-03 | T0 | L | Cross-check with `hover (-2559,1)` and `screenshot` metadata `displays` | The hover reaches display 0; `displays` carries the same six fields for each monitor. | — |
| MULTI_MONITOR-04 | T2 | L | `set_bounds` `owned:Notepad` to display 0, `list`; minimise, `list`; restore | `MonitorIndex` 0; then −1; then 0. | restore |
| MULTI_MONITOR-05 | T0 | L | Call twice with a window move in between | Identical arrays (read-only, idempotent). | — |
| MULTI_MONITOR-06 | T0 | S | `tools/list` annotations for `multi_monitor` | `readOnlyHint:true, idempotentHint:true, destructiveHint:false, openWorldHint:false`. | — |
| MULTI_MONITOR-07 | T0 | L | A scaled or rotated display is **not** present (both 96 DPI, landscape) | SKIPPED with the reason; the DPI/orientation rows are the highest-value future configuration. | — |

#### wait (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| WAIT-01 | T0 | L | `{"seconds":0.5}` | `{"waited":0.5}` after ≈ 500 ms. | — |
| WAIT-02 | T0 | L | `{"seconds":60}` (once) | `{"waited":60}`. | — |
| WAIT-03 | T0 | L | `60.001`; `0`; `-1` | `seconds must be more than 0 and at most 60, got 60.001; for a longer or conditional wait use wait_for.` and the same with `0` / `-1`. | — |
| WAIT-04 | T0 | S | `NaN` and `Infinity` (raw `--request` with the literal, if the JSON layer admits it; else record the parse rejection) | The refusal, never a hang. | — |
| WAIT-05 | T0 | L | `{"seconds":0.001}` | `{"waited":0.001}` at once. | — |
| WAIT-06 | T0 | S | `tools/list` annotations | `readOnlyHint:true, idempotentHint:true`. | — |

#### launch

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| LAUNCH-01 | T1 | L | `{"app_name":"calc"}` | `{MatchedName:"Calculator", Kind:"packaged", Strategy:"prefix", Score:100, Pid, Hwnd, WindowDetected:true}`; `Hwnd` in `window list`. **Not** `Kind:"path"` even though `calc.exe` is on PATH (B-8 regression). | `window close hwnd` |
| LAUNCH-02 | T1 | L | `{"app_name":"notepad.exe"}` | `Kind:"path", Strategy:"path", Score:100`, a PID. | close; sweep TabState |
| LAUNCH-03 | T0 | L | `{"app_name":"zzqqxx-not-an-app"}` | `No app matching 'zzqqxx-not-an-app'. Nearest: '…' (n), … . Use the Start Menu name, or a path.` with exactly five entries. | — |
| LAUNCH-04 | T0 | L | `{"app_name":"   "}`; `{"app_name":"calc","timeout_ms":0}`; `timeout_ms:60001` | `app_name is required: a Start Menu name, a Store app's display name, or a path.`; `timeout_ms must be between 1 and 60000, got 0` / `… got 60001`. | — |
| LAUNCH-05 | T1 | L | `{"app_name":"C:\\Windows\\System32\\cmd.exe","timeout_ms":1000}` with `/c exit` not possible here — use `{"app_name":"C:\\Windows\\System32\\charmap.exe","timeout_ms":1}` | Either `WindowDetected:true` (fast) or `WindowDetected:false` with a real `Pid`, `isError:false` (timeout is data). | kill pid |
| LAUNCH-06 | T1 | L | `{"app_name":"calc","wait_for_window":false}` | Immediate; `Hwnd:null, Title:null, WindowDetected:false`, valid `Pid`. | close |
| LAUNCH-07 | T1 | L | `launch calc` twice | Second call activates the existing window (no second process: `process list name:"CalculatorApp"` count unchanged). | close |
| LAUNCH-08 | T1 | L | Create `<scratch>\\broken.lnk` pointing at `<scratch>\\gone.exe` (via `powershell` WScript.Shell), then `{"app_name":"<scratch>\\broken.lnk"}` | A caller-facing `Could not start '<scratch>\\broken.lnk': …` (D-10, `Services/ProcessStartErrors.cs`; was DEF-050, masked): `the file or command was not found. …` when the OS reports not-found, else the OS reason. It names the shortcut, not its missing target — record which text arrives. | delete |
| LAUNCH-09 | T1 | L | `launch notepad` while `owned:Calc` on display 0 is foreground; `window list` for the new `Hwnd` | Record which `MonitorIndex` the new window lands on; `set_bounds` it to the other display and back. | close; sweep |

#### clipboard

Every row starts with `clipboard get` into `saved` and ends with `clipboard set text:saved`. If the
ledger's clipboard is non-text (`get` returned `""` while something was copied), the block is
recorded BLOCKED: the original cannot be restored.

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| CLIPBOARD-01 | T2 | L | `{"action":"set","text":"windows-mcp e2e"}`; `{"action":"get"}` | `set (15 chars)`; `windows-mcp e2e`. | restore |
| CLIPBOARD-02 | T2 | L | `{"action":"GET"}`; `{"action":"Set","text":"x"}` | Both accepted (case-insensitive). | restore |
| CLIPBOARD-03 | T0 | L | `{"action":"set"}`; `{"action":"clear"}` | `'set' requires text parameter` and the clipboard unchanged; `Unknown clipboard action 'clear'; expected get\|set`. | — |
| CLIPBOARD-04 | T2 | L | `{"action":"set","text":""}`; `get` | `set (0 chars)`; `""` — indistinguishable from a non-text clipboard (record). | restore |
| CLIPBOARD-05 | T2 | L | `set` with 1 000 000 chars; `get` | `set (1000000 chars)` and a matching `get` (no cap — record). | restore |
| CLIPBOARD-06 | T2 | L | `set "SENTINEL"`; `type` 300 × `a` into the editor; `get` | `{method:"paste", clipboardRestored:true}`; `SENTINEL`. | clear; restore |
| CLIPBOARD-07 | T2 | L | `powershell background:true` holding the clipboard open (`[System.Windows.Forms.Clipboard]` in an STA loop for 20 s); `{"action":"set","text":"x"}` | Contract: a message saying the clipboard is busy. Code: unwrapped `Win32Exception` family → expect **`An error occurred invoking 'clipboard'.`** — file S3 (fix family: catch in `ClipboardService`, rethrow `InvalidOperationException`; `type` already degrades gracefully). | cancel job; restore |
| CLIPBOARD-08 | T2 | L | Ledger check at block end | `clipboard get` equals the ledger value. | — |

### 7.3 Files, disk, watching and integrity

Fixture: Appendix B under the scratch root. Every path is absolute and plain. Verbatim messages
are from `Tools/FileTools.cs`, `Services/FileSystemService.cs`, `Tools/WatchTools.cs`,
`Services/WatchService.cs`, `Tools/DiskTools.cs`, `Services/DiskService.cs`,
`Tools/StorageTools.cs`, `Services/StorageService.cs`, `Tools/UsnTools.cs`, `Services/UsnService.cs`,
`Tools/IntegrityTools.cs`, `Services/IntegrityService.cs` on 0.7.3. Two tools in this group skip
the absolute-path rule the other file tools enforce (`watch`, `disk_inspect`); the rows below
probe that and record it.

#### file_read (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| FILE_READ-01 | T0 | L | `{"path":"<scratch>\\small.txt"}` | Plain text, three lines, not JSON. | — |
| FILE_READ-02 | T0 | L | `{"path":"<scratch>\\crlf.txt","offset_lines":2,"limit_lines":2}`; then `offset_lines:0` and `offset_lines:1` with `limit_lines:1` | JSON `{totalLines:5, offset:2, returned:2, truncated:true, content:"l2\nl3"}` (CR stripped, LF joined). Offsets 0 and 1 both return line 1. | — |
| FILE_READ-03 | T0 | L | `offset_lines:100` on `crlf.txt` | `returned:0, truncated:false`, `offset` echoes 100 (documented deviation in C-1). | — |
| FILE_READ-04 | T0 | L | `{"path":"notes.txt"}`; `{"path":"\\\\?\\C:\\Windows\\win.ini"}`; `{"path":"//?/C:/Windows/win.ini"}`; `{"path":"\\\\.\\C:\\Windows\\win.ini"}` | Relative: `'path' must be an absolute path (got 'notes.txt'); relative paths are refused because the server's working directory is not the caller's`. The three device forms: `'path' must be a plain absolute path (got '…'); the \\?\ and \\.\ device forms are refused`. | — |
| FILE_READ-05 | T0 | L | `big.txt` with `max_bytes:1024`; `over1mb.bin` with defaults | `File size <n> exceeds max_bytes 1024`; `File size 1048577 exceeds max_bytes 1048576`. | — |
| FILE_READ-06 | T0 | L | `offset_lines:-1`; `limit_lines:-1` | `'offset_lines' must be 0 or more (1-based)`; `'limit_lines' must be 0 (to the end) or more`. | — |
| FILE_READ-07 | T0 | L | `empty.txt` with `offset_lines:1` | `{totalLines:0, offset:1, returned:0, truncated:false, content:""}`. | — |
| FILE_READ-08 | T0 | L | `utf16.txt` with `encoding:"auto"`; `encoding:"ascii"`; `encoding:"klingon"` | `auto` → `hello utf-16`. `ascii` → NUL-interleaved text, no error (record). `klingon` → silently read as UTF-8: **no refusal for an unknown encoding** — record as S4 candidate (the parameter lists four values and refuses none). | — |
| FILE_READ-09 | T0 | L | `<scratch>\\missing.txt`; `locked.txt` (held by the fixture job) | `FileNotFoundException` text naming the path; `IOException` text (`being used by another process`) — both caller-visible, neither masked. | — |
| FILE_READ-10 | T0 | L | `ünïcødé 日本語 🎉.txt`; the > 260-char path under `long\` | Unicode content intact; long path: either the content or a `PathTooLongException` message naming the path — record which. | — |
| FILE_READ-11 | T0 | L | `bin.dat` (512 random bytes) with `encoding:"utf-8"` | Mojibake text, no error (documented behaviour: there is no binary guard). | — |

#### file_write (Destructive)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| FILE_WRITE-01 | T1 | L | `{"path":"<scratch>\\w.txt","content":"x"}` (no confirm) | `'confirm: true' is required for file writes`; `file_info` says the file does not exist. | — |
| FILE_WRITE-02 | T1 | L | Same with `confirm:true`; then `append:true, content:"y"` | `wrote 1 chars to '<path>'`; `appended 1 chars to '<path>'`; `file_read` → `xy`. | delete `w.txt` |
| FILE_WRITE-03 | T1 | L | `<scratch>\\deep2\\x\\y\\note.txt` `confirm:true, create_parents:false`; then `create_parents:true` | `Directory '<scratch>\deep2\x\y' does not exist; pass create_parents:true to create it` and **`deep2` was not created**; second call succeeds, three directories exist. | delete `deep2` recursive |
| FILE_WRITE-04 | T1 | L | `{"path":"rel.txt","content":"x"}` (no confirm); then with `confirm:true` | First: the **confirm** message (confirm is checked before the path). Second: the absolute-path refusal. Nothing written in the server's working directory (check `%CD%` of the server via `process_inspect` command line — record only). | — |
| FILE_WRITE-05 | T1 | L | Write onto `readonly.txt` with `confirm:true`; write with `path:"<scratch>\\deep"` (a directory) | `UnauthorizedAccessException` text; an `IOException`/`UnauthorizedAccessException` text. After each: `file_manage list pattern:"*.tmp.*"` on scratch → no entries (the temp sibling was removed). `readonly.txt` content unchanged. | — |
| FILE_WRITE-06 | T1 | L | `encoding:"utf-16"` then `file_read encoding:"auto"`; `encoding:"klingon"` | Round trip; `klingon` silently writes UTF-8 without BOM — S4 candidate (unknown encoding not refused; same family as FILE_READ-08). | delete |
| FILE_WRITE-07 | T1 | L | `<scratch>\\emoji-🎉\\note.txt` `create_parents:true`; the > 260-char path | Unicode directory created and file written; long path: success or a `PathTooLongException` message — record. | delete |
| FILE_WRITE-08 | T1 | L | Write `w.txt` twice with different content, `confirm:true`, no other flag | Second call **replaces silently** (there is no `overwrite` gate on `file_write`; `confirm` is the gate). Documented, not a defect. | delete |

#### file_manage (Destructive)

`subst` gives a safe way to probe the volume-root guards: `subst X: <scratch>` (T1, removed with
`subst X: /D` at the end of the block) makes `X:\` a root whose contents are only the scratch tree,
so a failed guard could touch nothing else. Never probe a root guard against a real drive.

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| FILE_MANAGE-01 | T0 | L | `{"action":"list","src":"<scratch>"}` | `Entries` carry `Path, Name, IsDirectory, Size (0 for dirs), Modified (UTC), Hidden, IsLink`; `link-junction` has `IsLink:true`; `hidden.txt` absent; `Truncated:false, MaxEntries:1000`. | — |
| FILE_MANAGE-02 | T0 | L | `list max_entries:1`; `max_entries:0`; `max_entries:100001` | One entry with `Truncated:true`; `'max_entries' must be between 1 and 100000, got 0 (0 is not 'all' here: a listing is always bounded)`; the same message with `100001`. | — |
| FILE_MANAGE-03 | T0 | L | `list pattern:"deep\\*.txt"`; `pattern:"*.TXT"`; `pattern:"*🎉*"` | Separator refusal `'pattern' is a name glob such as '*.txt', got 'deep\*.txt'; a path separator is not allowed — pass recursive:true to descend`; case-insensitive match returns the `.txt` files; the emoji glob returns the Unicode file. | — |
| FILE_MANAGE-04 | T0 | L | `list src:"<scratch>\\small.txt"`; `src:"<scratch>\\small.txt\\"` | Both: `'<scratch>\small.txt' is a file, not a directory; list its parent, or read it with file_read` (trailing separator trimmed in the message). | — |
| FILE_MANAGE-05 | T0 | L | `list recursive:true`; then `include_hidden:true` | Descends `deep\a\b\c` but **not** into `link-junction` (listed with `IsLink:true`, no children); `hidden.txt` appears only with `include_hidden:true`. | — |
| FILE_MANAGE-06 | T1 | L | `copy small.txt → copy1.txt`; again with `overwrite:false` after changing `copy1.txt`; then `overwrite:true` | `copied '…' to '…'` and equal hashes; `'<dst>' already exists; pass overwrite:true to replace it` with **`copy1.txt` unchanged** (XC-02); replaced, and `list pattern:"*.replaced.*"` on scratch is empty. | delete `copy1.txt` |
| FILE_MANAGE-07 | T1 | L | `copy src:deep dst:deep\\inner`; same with `overwrite:true`; `copy src:deep\\a dst:deep` | `'<dst>' is inside the source '<src>'; a copy or move into its own subtree is refused` and `inner` was not created (both calls — the containment check precedes the exists check); `'<dst>' contains the source '<src>'; replacing it would delete what is being copied`. | — |
| FILE_MANAGE-08 | T1 | L | With `subst X: <scratch>`: `copy src:"X:\\" dst:"<scratch>\\x"`; `copy src:small.txt dst:"X:\\"`; `copy src:small.txt dst:"<scratch>\\small.txt"`; `copy src:small.txt dst:"X:\\small.txt"` | `'X:\' is a volume root; a whole volume cannot be the source of a copy or move`; `'X:\' is a volume root and cannot be the destination of a copy or move`; `'…' and '…' are the same path`; the subst alias also resolves to **the same path** (canonicaliser equates the two spellings). | `subst X: /D` |
| FILE_MANAGE-09 | T0 | L | `copy` without `dst`; `move` without `dst`; `action:"rename"` | `'copy' requires dst`; `'move' requires dst`; `Unknown action 'rename'; expected copy\|move\|delete\|list`. | — |
| FILE_MANAGE-10 | T1 | L | `move copy1.txt → moved\\m.txt` (parent missing); `move` onto an existing file without `overwrite` | `moved '…' to '…'`, source gone, parent created; `already exists; pass overwrite:true` and nothing moved. | delete `moved` |
| FILE_MANAGE-11 | T1 | L | `copy src:"<scratch>" dst:"<scratch>-copy"` (the tree contains `link-junction`) | Tree copied; the junction is **neither descended nor recreated** at the destination (record what appears at `<scratch>-copy\link-junction`: nothing). | delete `<scratch>-copy` recursive |
| FILE_MANAGE-12 | T1 | L | `delete src:small.txt` (no confirm); `delete src:"<scratch>\\nope.txt" confirm:true` | `'confirm: true' is required for delete` and the file still exists; `nothing at '<path>' to delete` (a no-op, not an error). | — |
| FILE_MANAGE-13 | T1 | L | `delete src:deep confirm:true` (non-empty, no recursive); then `recursive:true` on a copy of `deep` that contains `readonly.txt` and a junction | `'<path>' is a directory that is not empty; pass recursive:true to delete the whole tree`; then `deleted '<path>'`, the junction's target intact, the read-only bit not an obstacle. | recreate `deep` |
| FILE_MANAGE-14 | T1 | L | `delete src:link-junction confirm:true` | `removed link '<path>' (its target is untouched)`; `deep\a\b\c\leaf.txt` still exists. | recreate the junction |
| FILE_MANAGE-15 | T1 | L | `copy src:locked.txt dst:"<scratch>\\p1\\p2\\p3\\l.txt"` | `IOException` text (sharing violation); **none of `p1`, `p2`, `p3` exist afterwards** (created parents are rolled back). | — |
| FILE_MANAGE-16 | T1 | L | Create `<scratch>\\longdirectoryname\\f.txt`; `copy src:"<scratch>\\LONGDI~1\\f.txt" dst:"<scratch>\\longdirectoryname\\f.txt"` (needs 8.3 names enabled: `fsutil 8dot3name query` — SKIPPED otherwise) | `are the same path` (the canonicaliser resolves the short name). | delete |
| FILE_MANAGE-17 | T1 | L | With `subst X:`: `delete src:"X:\\" confirm:true recursive:true` | `'X:\' is a volume root and cannot be deleted`; scratch contents intact. | `subst X: /D` |
| FILE_MANAGE-18 | T0 | L | `copy src:"C:\\Windows\\win.ini" dst:"<scratch>\\win.ini"` then `file_hash` both | Equal hashes (a copy from a system path *into* scratch is a read of the source). | delete |

#### file_search (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| FILE_SEARCH-01 | T0 | L | `{"root":"<scratch>","pattern":"*.txt"}` | Every `.txt` including `deep\a\b\c\leaf.txt` (always recursive); hits carry `Path, Size, Modified`. | — |
| FILE_SEARCH-02 | T0 | L | `min_size:1000` with a 999-byte and a 1000-byte file present | Only the 1000-byte file (inclusive boundary). | — |
| FILE_SEARCH-03 | T0 | L | `modified_since:"not-a-date"`; `modified_since:"2099-01-01T00:00:00Z"` | `'modified_since' must be a valid ISO 8601 datetime, got: 'not-a-date'`; `[]`. | — |
| FILE_SEARCH-04 | T0 | L | `find_duplicates:true`; again with `dup1.txt` locked | `dup1.txt` and `dup2.txt` grouped, nothing else; with the lock the search still returns (the locked file silently omitted — record). | — |
| FILE_SEARCH-05 | T0 | L | `root:"relative"`; `root:"<scratch>\\missing"` | Absolute-path refusal; `DirectoryNotFoundException` text naming the path. | — |
| FILE_SEARCH-06 | T0 | L | `root:"C:\\Windows\\System32\\drivers\\etc"` | Every file, no cap, no `Truncated` field: **`file_search` is the one uncapped enumerator in the family** — record as S3 candidate against XC-05 (a search of `C:\` would return the whole volume in one response). | — |

#### file_info, file_hash, file_streams (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| FILE_INFO-01 | T0 | L | `small.txt`; `<scratch>` | `{Size:<n>, IsDirectory:false, Attributes:"Archive"…}` with UTC `Created/Modified/Accessed`; `{IsDirectory:true, Size:0, Attributes:"Directory"}`. | — |
| FILE_INFO-02 | T0 | L | `<scratch>\\missing.txt` | **Record what happens**: a `FileNotFoundException` message (correct) or a DTO with `Attributes:-1` and 1601-01-01 timestamps (S3: a missing path must be an error naming the path). Nothing in the suite pins this. | — |
| FILE_INFO-03 | T0 | L | `readonly.txt`; `hidden.txt`; `link-junction`; `empty.txt`; the Unicode name; `"a.txt"`; `"\\\\.\\C:"` | `Attributes` contains `ReadOnly` / `Hidden` / `ReparsePoint` respectively; `Size:0, IsDirectory:false`; `Path` echoes the Unicode name intact; the two refusals. | — |
| FILE_HASH-01 | T0 | L | `abc.txt` (content `abc`) default; `algorithm:"sha1"`; `"md5"`; `"SHA256"` | `ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad`; `a9993e364706816aba3e25717850c26c9cd0d89d`; `900150983cd24fb0d6963f7d28e17f72`; uppercase accepted → same SHA-256. | — |
| FILE_HASH-02 | T0 | L | `algorithm:"crc32"`; `empty.txt`; `path:"<scratch>"`; missing file; `locked.txt` | `Unknown algorithm 'crc32'; expected sha256\|sha1\|md5`; `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`; `UnauthorizedAccessException` text; `FileNotFoundException` text; `IOException` text — all caller-visible. | — |
| FILE_STREAMS-01 | T0 | L | `small.txt`; `ads.txt`; `link-junction` | `{AlternateStreams:[], LinkTarget:null}`; one entry `:Zone.Identifier:$DATA` with non-zero `Size` and no `:$DATA` row; `LinkTarget` = the `deep` path with `AlternateStreams:[]`. | — |
| FILE_STREAMS-02 | T0 | L | `<scratch>\\does-not-exist.txt`; `<scratch>\\O'Brien.txt` (create it); `"file.txt"` | Missing path → **empty result and no error** (record as S3 candidate: a typo is indistinguishable from a clean file); apostrophe handled; relative refused. | delete `O'Brien.txt` |

#### archive (Destructive)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| ARCHIVE-01 | T1 | L | `zip src:deep dst:pack2.zip`; `unzip src:pack2.zip dst:extracted` | `zipped '…' to '…'`, `file_info` size > 0, no `*.tmp.*`; `unzipped …`, `file_hash` of `extracted\a\b\c\leaf.txt` equals the original. | delete both |
| ARCHIVE-02 | T1 | L | Re-run the zip over the existing `pack2.zip`; modify `extracted\a\b\c\leaf.txt` and re-run the unzip | Both **silently replace/overwrite** — there is no `overwrite` or `confirm` gate on `archive`. Record as S3 candidate: the only Destructive file tool with neither gate. | delete |
| ARCHIVE-03 | T1 | L | `zip src:"<scratch>\\missing" dst:pack2.zip` (pack2.zip exists) | `DirectoryNotFoundException` text; `pack2.zip` byte-identical to before (the archive is built beside the target). | — |
| ARCHIVE-04 | T1 | L | `unzip src:notazip.zip dst:"<scratch>\\x"` (`notazip.zip` = 100 random bytes) | Expected by the contract: a message saying the file is not a valid archive. Expected by the code: **`An error occurred invoking 'archive'.`** (`InvalidDataException` is not caller-facing). File as S2 with the fix family: map `InvalidDataException` in `ToolErrors` or catch it in `UnzipAsync`. | — |
| ARCHIVE-05 | T0 | L | `action:"list"`; `zip src:"tree"` | `Unknown action 'list'; expected zip\|unzip`; absolute-path refusal. | — |
| ARCHIVE-06 | T1 | L | Zip a directory holding the Unicode file and `empty.txt`; zip an empty directory | Both round-trip through unzip; the empty directory yields a valid archive. | delete |
| ARCHIVE-07 | T1 | L | Build `slip.zip` with an entry named `..\\slip.txt` (via `powershell` and `System.IO.Compression` in scratch); `unzip` it into `<scratch>\\slipdst` | Refused with an `IOException` text (extraction outside the destination); **no `slip.txt` above `slipdst`**. | delete |

#### fs_changes (ReadOnly; needs elevation)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| FS_CHANGES-01 | T0 | L | `{"mode":"status"}` | Unelevated server (the normal case): `Cannot open volume \\.\C: (error 5); USN journal access requires elevation.` — caller-visible. Elevated: `{Volume:"C:", JournalId≠0, FirstUsn ≤ NextUsn}`. Record which. | — |
| FS_CHANGES-02 | T0 | L | `mode:"list"`; `volume:"Z"`; `volume:"c:\\"` | `Unknown mode 'list'; expected status\|since`; the cannot-open message with a different error code; normalised to `C:` (elevated) or the elevation message. | — |
| FS_CHANGES-03 | T0 | L | Elevated only: record `NextUsn`; `file_write` a scratch file; `since start_usn:<that> max:50`; `since max:1`; `since max:0` | The scratch file name with `Reasons` containing `file-create`; exactly one record; `max:0` coerced to 200. BLOCKED (not elevated) otherwise. | — |

#### watch

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| WATCH-01 | T1 | L | `{"mode":"start","path":"<scratch>"}`; `list` | `{Id:"wN", Path, Filter:"*", IncludeSubdirectories:false, Buffered:0, Dropped:0}`; `list` contains it. | stop |
| WATCH-02 | T1 | L | `file_write` `<scratch>\\ev.txt`; `wait 1`; `poll id:wN`; `poll` again | At least one event `{Kind:"created", Path:…ev.txt}`; the second poll `[]` (drained). | stop, delete |
| WATCH-03 | T1 | L | `start path:<scratch> filter:"*.log" subdirs:true`; create `deep\\a.txt` and `deep\\b.log`; poll | Only `b.log` events. | stop, delete |
| WATCH-04 | T0 | L | `poll id:"w999"`; `stop id:"w999"` | `[]` and `{stopped:false}` — silent misses. Record as S3 candidate: an unknown session id answers like an idle one (compare `job status` which returns `found:false`). | — |
| WATCH-05 | T1 | L | `stop id:wN`; `list` | `{stopped:true}`; the session gone. | — |
| WATCH-06 | T0 | L | `start` without `path`; `start path:"<scratch>\\missing"`; `poll` without `id`; `mode:"pause"` | `start mode requires 'path'`; `Watch path not found: <path>`; `poll mode requires 'id'`; `Unknown mode 'pause'; expected start\|poll\|stop\|list`. | — |
| WATCH-07 | T1 | L | `start path:"relative-dir"` | Record: if accepted (resolved against the server's working directory) file S3 — `watch` does not apply the C-1 absolute-path rule the eight file tools enforce. | stop if started |
| WATCH-08 | T1 | L | `start`; `powershell` creates 2 500 files in `<scratch>\\flood`; `list`; `poll max:100`; `poll max:0` | `Dropped > 0`, `Buffered ≤ 2000`; 100 events; `max:0` coerced to 500. | stop, delete `flood` |
| WATCH-09 | T1 | L | `start path:"<scratch>\\watched"`; `file_manage delete` that directory `recursive:true confirm:true` while watched | Record: deletion succeeds or an `IOException` (the watcher holds a handle); either way `stop` afterwards returns `true` and the directory can then be deleted. | stop, delete |

#### disk_inspect (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| DISK_INSPECT-01 | T0 | L | `{"mode":"usage","path":"<scratch>\\du"}` with `big\` (3 KB) and `small\` (1 KB); then 12 sub-directories | `big` first, `SizeHuman` like `3.0 KB` (1023 B → `1023 B`, 1024 B → `1.0 KB`); 12 dirs → exactly 10 entries. | — |
| DISK_INSPECT-02 | T0 | L | `mode:"file_types"` on a tree with `.txt`, `.log` and an extensionless file | Three groups; the extensionless one labelled `(none)`; top-10 cap. | — |
| DISK_INSPECT-03 | T0 | L | `mode:"stale"` with one file back-dated 400 days, one 364 days, one fresh | Only the 400-day file (threshold 365, hard-coded). | — |
| DISK_INSPECT-04 | T0 | L | `mode:"reclaimable"` twice; `powershell` counts Recycle Bin items before and after | Four non-negative longs, `TotalBytes == Temp + InetCache + RecycleBin`; the Recycle Bin count unchanged (report only, nothing emptied). | — |
| DISK_INSPECT-05 | T0 | L | `mode:"cleanup"`; `mode:"usage" path:"<scratch>\\missing"`; `mode:"usage" path:"relative"` | `Unknown mode 'cleanup'; expected usage\|reclaimable\|file_types\|stale`; `DirectoryNotFoundException` text; relative: record — S3 candidate if accepted (no absolute-path rule). | — |
| DISK_INSPECT-06 | T0 | L | `mode:"usage"` with no `path` (defaults to `C:\`) | **SKIPPED by default** — it walks the whole system drive through the uncapped search. Run only if the operator opts in, with the elapsed time recorded as a documented cost. | — |

#### storage_health (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| STORAGE_HEALTH-01 | T0 | L | `{}` | `Disks[]` and `Volumes[]` non-empty; `PhysicalDisks:[]`; `Notes` contains `physical disks + SMART omitted`. A "success" with empty arrays and a note is the failure mode — read `Notes`. | — |
| STORAGE_HEALTH-02 | T0 | L | `include_usage:true` | `PhysicalDisks` populated; at least one volume `UsageProbed:true` with `FreeGb`; elapsed recorded (wakes drives). | — |
| STORAGE_HEALTH-03 | T0 | L | `drive_letter:"C"`; `"c:"`; `"CC"`; `"1"`; `"Z"` | First two identical single-volume results; `Invalid drive_letter 'CC'; expected a single letter A-Z, optionally with ':'.` and the same for `1`; `Z` → empty `Volumes[]` with no error (record). | — |
| STORAGE_HEALTH-04 | T0 | L | `timeout_seconds:1`; `timeout_seconds:9999`; then `file_manage list %TEMP% pattern:"windowsmcp-storage-*.ps1"` | Clamped to 5 and 300 (not refused — the description says clamped); no temp script left behind. | — |

#### integrity

`baseline` writes `%LOCALAPPDATA%\windows-mcp\integrity\baseline.json` and overwrites the user's
own. On channel S the store is redirected with `--env LOCALAPPDATA=<scratch>\lad`, which makes the
whole block T1; on channel L only `list` and `check` run (T0). INTEGRITY-09 proves the real store
was not touched.

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| INTEGRITY-01 | T0 | L | `{"mode":"list"}` | `watchList` fully expanded (contains `\drivers\etc\hosts`, `\.gitconfig`, no `%VAR%` left); `baseline` is `null` or the user's existing one — record, do not modify. | — |
| INTEGRITY-02 | T0 | L | `{"mode":"check"}`; hash of the real `baseline.json` before and after | `HasBaseline` consistent with INTEGRITY-01; the store file unchanged. | — |
| INTEGRITY-03 | T1 | S | Redirected store, fresh: `list`; `check` | `baseline:null`; `{HasBaseline:false, Unchanged:0, Changes:[]}`. | — |
| INTEGRITY-04 | T1 | S | `baseline paths:"<scratch>\\ia\\a.txt;<scratch>\\ib"` (`ib` a directory with two files) | `Roots` include both; one `Items` entry per file with a 64-hex `Sha256`; `list` afterwards shows the same `CreatedUtc`. | — |
| INTEGRITY-05 | T1 | S | Modify `ib\\1.txt`, delete `ib\\2.txt`, add `ib\\3.txt`; `check`; `check` again | Exactly one `modified`, one `removed`, one `added`; `Unchanged` counts the rest; second call identical. | — |
| INTEGRITY-06 | T1 | S | `baseline paths:" ; ; "`; `baseline paths:"<scratch>\\never.txt"` then create it, `check` | Blanks dropped (no extra roots); `never.txt` reported `added` (an absent path is recorded so its appearance is caught). | — |
| INTEGRITY-07 | T1 | S | Lock `ib\\1.txt` exclusively, `check`; truncate `baseline.json` to 3 bytes, `check` | Locked file reported **`removed`** — record as S3 candidate (a read error is indistinguishable from deletion); corrupt store → `HasBaseline:false` with no error — record as S3 candidate (a corrupt baseline should be an error, not "no baseline"). | — |
| INTEGRITY-08 | T0 | L | `mode:"reset"`; `mode:"check" paths:"<scratch>\\x"` | `Unknown mode 'reset'; expected baseline\|check\|list`; `paths` silently ignored on `check` (record). | — |
| INTEGRITY-09 | T0 | L | End of block: `file_hash` of the real `%LOCALAPPDATA%\windows-mcp\integrity\baseline.json` (or its absence) | Identical to the phase-0 ledger value. | — |

### 7.4 Shell, jobs, processes, services, tasks, registry, environment, power, WMI, events

Verbatim messages come from `Tools/ShellTools.cs`, `Services/PowerShellService.cs`,
`Tools/JobTools.cs`, `Services/JobService.cs`, `Tools/ProcessTools.cs`, `Services/ProcessService.cs`,
`Services/ArgvJson.cs`, `Services/ServiceControlService.cs`, `Services/TaskSchedulerService.cs`,
`Tools/RegistryTools.cs`, `Services/RegistryService.cs`, `Services/RegistryGuard.cs`,
`Tools/SystemTools.cs`, `Services/EnvService.cs`, `Services/PowerService.cs`, `Services/WmiService.cs`,
`Services/EventLogService.cs` on 0.7.3. `<child>` is a process the run started with
`start_process`, always killed by `pid` with its recorded `startTime`.

#### powershell (Destructive, OpenWorld)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| POWERSHELL-01 | T0 | L | `{"command":"'hi'"}` | `Stdout:"hi\r\n", Success:true, ExitCode:0, Stderr:"", TimedOut:false`, both `Trimmed` counts 0; the whole response < 200 bytes (D-8 "Done when"). | — |
| POWERSHELL-02 | T0 | L | `{"command":"Write-Warning 'careful'"}`; `{"command":"Write-Error 'boom'"}` | `Stderr` contains `WARNING: careful`, no `<Objs`, no `#< CLIXML`, `Success:true`; then `Success:false`, `Errors` contains `boom`, `Stderr` contains `ERROR: boom`. | — |
| POWERSHELL-03 | T1 | L | `{"command":"'before-hang'; Start-Sleep 60","timeout_seconds":3}`; then `process list name:"powershell"` | `TimedOut:true, ExitCode:-1, Success:false`, `Stdout` contains `before-hang`, last `Errors` entry `timed out after 3s`; no orphaned `powershell.exe` child after 5 s. | — |
| POWERSHELL-04 | T0 | L | `timeout_seconds:901`; `timeout_seconds:-1`; `background:true, timeout_seconds:5` | `timeout_seconds must be 0 (the 15-minute execution backstop only) or 1-900, got 901.` (and `-1`); `timeout_seconds cannot be combined with background:true: a job runs until it finishes or job(cancel) stops it, and has its own 60-minute backstop. Drop one of the two.`; `job list` shows no new job. | — |
| POWERSHELL-05 | T1 | L | `{"command":"'x'","background":true,"timeout_seconds":0}` | Accepted: `{Id:"jN", State:"running", Pid}`. | cancel/complete |
| POWERSHELL-06 | T0 | L | `{"command":"$s='x'*1000; 1..2000 \| %{$s}","timeout_seconds":60}` | `Stdout.Length ≤ 1000000`, `StdoutTrimmedChars > 0`. | — |
| POWERSHELL-07 | T0 | L | `{"command":"$ProgressPreference='Continue'; Write-Progress -Activity a -Status s; 'done'"}` | `Stderr:""`, `Stdout` has `done`. | — |
| POWERSHELL-08 | T0 | S | HOST-33 (heartbeats) | ≥ 2 `notifications/progress`, `progress` ascending. | — |
| POWERSHELL-09 | T0 | L | `{"command":"[Console]::OutputEncoding.WebName; 'ünïcødé 日本語 🎉'"}` | `utf-8` and the string intact in `Stdout`. | — |
| POWERSHELL-10 | T1 | L | A 40 000-character script (`'x'` repeated in comments) that prints `big-ok` | `Stdout` has `big-ok` (the oversize `-File` fallback); `file_manage list %TEMP% pattern:"winmcp-*.ps1"` afterwards → none left. | — |

#### job

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| JOB-01 | T1 | L | `powershell {"command":"Start-Sleep 8; 'done'","background":true}`; `job status id` | `State:"running"`, `ExitCode:null`, `Pid` alive. | — |
| JOB-02 | T1 | L | Poll `status` to completion; `output` | `State:"completed", ExitCode:0`; `Stdout` contains `done`. | — |
| JOB-03 | T1 | L | Background `Write-Warning 'careful'`; after completion `output` and `status` | `Stderr` contains `WARNING: careful`, no `<Objs`, no `_x000D_`; `status.StderrChars == output.Stderr.Length` (D-9 "Done when"). | — |
| JOB-04 | T1 | L | A job printing 100 chars; `output tail:5` | Exactly 5 chars per stream. | — |
| JOB-05 | T0 | L | `status id:"j99999"`; `cancel id:"j99999"`; `output id:"j99999"` | `{"found":false,"id":"j99999"}`; `{"cancelled":false}`; `{"found":false,…}` — not errors. | — |
| JOB-06 | T1 | L | Background `Start-Sleep 300`; `cancel`; `status`; `cancel` again; `process list` | `{"cancelled":true}`; `State:"cancelled"`; `{"cancelled":false}`; the pid gone. | — |
| JOB-07 | T0 | L | `{"mode":"status"}`; `{"mode":"frobnicate"}` | `status mode requires 'id'`; `Unknown mode 'frobnicate'; expected status\|output\|cancel\|list`. | — |
| JOB-08 | T1 | L | Start 8 sleeping jobs; a 9th `powershell background:true` | `Job limit reached (8 running, max 8). Cancel a job ('job cancel') or wait for one to finish.` | cancel all 8 |
| JOB-09 | T1 | L | `{"mode":"list"}` | Every id the run created is present with a `State`. | — |
| JOB-10 | T1 | L | Background job that fails (`exit 3`) | `State:"failed", ExitCode:3`. | — |

#### process (list/orphans read-only; kill only on `<child>`)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| PROCESS-01 | T0 | L | `{"action":"list","sort_by":"cpu","limit":5}`; `sort_by:"name"`; `sort_by:" "` | ≤ 5 rows, `CpuPercent` in [0,100] descending; name order; blank `sort_by` = omitted. | — |
| PROCESS-02 | T0 | L | `list includeLineage:true name:"cmd"` after starting `<child>`; `list groupByRoot:true name:"cmd"` | The child row with `parentPid`, `commandLine`, `startTime`; the group containing the child's tree with a true `DescendantCount`. | — |
| PROCESS-03 | T0 | L | `list includeLineage:true sort_by:"cpu"`; `orphans limit:5`; `list includeLineage:true groupByRoot:true`; `orphans includeLineage:true`; `sort_by:"disk"`; `limit:-1` | `'sort_by' applies to the plain list only, not with …`; `'limit' applies to the plain list only, not with orphans`; `'includeLineage' and 'groupByRoot' are two different shapes; pass one of them, not both`; `'includeLineage' applies to list only; orphans already carries the lineage columns`; `'sort_by' must be one of memory\|cpu\|name\|pid, got 'disk'`; `'limit' must be 0 (all rows) or more, got -1`. | — |
| PROCESS-04 | T0 | L | `{"action":"orphans"}` | Rows with `orphaned:true`; `explorer.exe` typically present (by design, not a leak). | — |
| PROCESS-05 | T1 | L | `<child>`: `kill pid confirm:false`; `kill pid confirm:true startTime:"yesterday"`; `kill pid confirm:true startTime:"2000-01-01T00:00:00Z"` | `'confirm: true' is required for kill`; `'startTime' must be ISO-8601, got: 'yesterday'`; `pid N start time … != expected …; aborting (possible PID reuse)` — the child is **still alive** after all three (XC-02). | — |
| PROCESS-06 | T0 | L | `kill pid confirm:true graceful:true grace_ms:60001`; `kill pid confirm:true tree:true graceful:true`; `kill name:"x" tree:true confirm:true`; `kill confirm:true` | `'grace_ms' must be between 0 and 60000 ms, got 60001`; `'graceful' cannot be combined with 'tree': descendants are killed leaves-first and forcibly`; `'tree' and 'startTime' require 'pid' (they do not apply to name-based kills)`; `'kill' requires either name or pid`. | — |
| PROCESS-07 | T1 | L | `<child>` = `powershell -c Start-Sleep 30` via `start_process`; `kill pid confirm:true graceful:true startTime:<recorded>` | `{killed:[{pid, name, graceful:true, exitedGracefully:false, forced:true, waitedMs:0}]}` (no window → forced at once); the pid gone. | — |
| PROCESS-08 | T1 | L | `<child>` = `owned:Charmap` (has a window); `kill pid confirm:true graceful:true grace_ms:5000 startTime:<recorded>` | `exitedGracefully:true, forced:false, waitedMs > 0`. | — |
| PROCESS-09 | T1 | L | `<child>` = `cmd /c "start /wait cmd /c timeout 60"` (spawns a grandchild); `kill pid tree:true confirm:true startTime:<recorded>` | Plain text `killed N process(es) in tree of pid <pid>` with N ≥ 2 (assert the **text** shape); both gone. | — |
| PROCESS-10 | T0 | L | `kill name:"a-name-nothing-uses" confirm:true`; `kill pid:999999 confirm:true` | `{"killed":[]}` (not an error); `Process with an Id of 999999 is not running.` | — |
| PROCESS-11 | T0 | L | `kill pid:<server pid> confirm:false`; `kill pid:4 confirm:false` | The confirm refusal for both — **never** with `confirm:true`. Record: there is no self-pid / ancestor / system-process guard in the code (S3 by inspection; the OS ACL is the only backstop and it surfaces masked). | — |
| PROCESS-12 | T0 | L | `{"action":"suspend"}` | `Unknown action 'suspend'; expected list\|orphans\|kill`. | — |

#### process_inspect (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| PROCESS_INSPECT-01 | T0 | L | `{"pid":<child>}` | `Name`, `ParentPid` (the server or its spawner), `CommandLine` with the argv, `Modules` non-empty. | — |
| PROCESS_INSPECT-02 | T0 | L | `{"pid":4}` | A row with `ModulesError` set, `Modules:[]`, `isError:false` (documented degradation). | — |
| PROCESS_INSPECT-03 | T0 | L | `{"pid":999999}`; `{"pid":-1}`; `{"pid":0}` | **`isError:false`** with an all-null DTO for each — record as S3 (a pid that does not exist should be an error naming the pid; the `ArgumentException` is swallowed). | — |
| PROCESS_INSPECT-04 | T0 | L | Cross-check with `process list includeLineage:true` | `parentPid` agrees for `<child>`. | — |
| PROCESS_INSPECT-05 | T0 | L | `{"pid":<msedge pid>}` | A long `Modules` list; record the size (no cap). | — |

#### start_process (OpenWorld)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| START_PROCESS-01 | T1 | L | `{"command":"C:\\Windows\\System32\\cmd.exe","args_json":"[\"/c\",\"exit\",\"0\"]"}` | `{pid, executable:"C:\\Windows\\System32\\cmd.exe", args:["/c","exit","0"], cwd:null}`. | — |
| START_PROCESS-02 | T1 | S | Same with `args_json` as a **real** JSON array in the arguments object | Either the same result or an SDK binding error — record (the B-11 open question). | — |
| START_PROCESS-03 | T1 | L | `args_json:"[\"/c\",\"echo\",\"a b\"]" cwd:"C:\\Windows"` | `args[2] == "a b"` whole; `cwd:"C:\\Windows"`. | — |
| START_PROCESS-04 | T0 | L | `args_json:"{}"`; `"[1,2]"`; `"[\"ok\",null]"`; `"notastring"` | `args_json must be a JSON array of strings, got Object.`; `… item 0 is Number.`; `… item 1 is Null.`; `args_json must be a JSON array of strings, e.g. ["/c","echo hi"]; it did not parse: …`. | — |
| START_PROCESS-05 | T0 | L | `cwd:"C:\\definitely-not-here"`; `process list` count before/after | `cwd 'C:\definitely-not-here' is not an existing directory.`; no process started. | — |
| START_PROCESS-06 | T1 | L | `cwd:""`; `cwd:"   "` | Accepted, `cwd:null`. | kill |
| START_PROCESS-07 | T0 | L | `{"command":"\"C:\\Program Files\\nope"}` | `Unmatched opening quote in command`. | — |
| START_PROCESS-08 | T0 | L | `{"command":"C:\\Windows\\System32\\definitely-not-an-exe.exe"}` | `Could not start 'C:\Windows\System32\definitely-not-an-exe.exe': the file or command was not found. Check the name, or give a full path.` — a `FileNotFoundException` since D-10 (was DEF-021, masked). An unquoted command line with a space names the text before the space and adds the quote-it / `args_json` hint. | — |
| START_PROCESS-09 | T1 | L | `{"command":"notepad"}` (bare name on PATH, no args) | A pid; the window appears. | close; sweep |

#### service (list/status only; the refusals are inert)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| SERVICE-01 | T0 | L | `{"action":"list"}`; `{"action":"status","name":"Spooler"}` | An array with `{Name:"Spooler", DisplayName, Status, StartType}`; the single row agrees. | — |
| SERVICE-02 | T0 | L | `{"action":"status","name":"WindowsMcpE2E-NoSuchService"}` | An error naming the service (`was not found`), caller-visible. | — |
| SERVICE-03 | T0 | L | `{"action":"status"}`; `{"action":"start"}`; `{"action":"frobnicate"}` | `'status' requires name`; `'start' requires name` (**`start` has no confirm gate** — record as S3 by inspection); `Unknown action 'frobnicate'; expected list\|status\|start\|stop\|restart`. | — |
| SERVICE-04 | T0 | L | `{"action":"stop","name":"Spooler"}` (no confirm); then `status Spooler` | `'confirm: true' is required for stop/restart actions`; status unchanged. | — |
| SERVICE-05 | T0 | L | `{"action":"stop","confirm":true}`; `{"action":"restart","confirm":true}` (no name) | `'stop' requires name`; `'restart' requires name` — the confirm gate passed but nothing was named; nothing touched. | — |
| SERVICE-06 | T0 | L | `{"action":"start","name":"WindowsMcpE2E-NoSuchService"}` | An error naming the service (caller-visible) or the masked text — record. Nothing exists to start. | — |
| SERVICE-07 | T3 | L | Opted-in only: `restart` of the one benign service the operator named, `confirm:true` | `restarted '<name>'`; `status` `Running` afterwards. An AI operator records BLOCKED. | — |
| SERVICE-08 | T0 | L | By inspection: `WaitForStatus` throws `System.ServiceProcess.TimeoutException`, not `System.TimeoutException` | S3 by inspection (a 15 s start/stop that does not reach the state would be masked); not executed. | — |
| SERVICE-09 | T0 | L | By inspection: no protected-service denylist | Record; not executed. | — |

#### scheduled_task (list/get and refusals; create/run/delete are T3)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| SCHEDULED_TASK-01 | T0 | L | `{"action":"list"}`; `{"action":"get","name":"<a name from list>"}` | Non-empty array of `{Name, Path, State, LastRun, NextRun}`; the matching row. | — |
| SCHEDULED_TASK-02 | T0 | L | `{"action":"get","name":"NoSuchTask-<runid>"}`; `{"action":"run","name":"NoSuchTask-<runid>"}` | `Scheduled task 'NoSuchTask-<runid>' not found`; for `run` the error text is **just the name** — record as S4 (message quality inconsistent with `get`). | — |
| SCHEDULED_TASK-03 | T0 | L | `{"action":"delete","name":"NoSuchTask-<runid>"}` (no confirm); `{"action":"delete","confirm":true}` (no name); `{"action":"create","name":"x"}`; `{"action":"frobnicate"}` | `'confirm: true' is required for delete`; `'delete' requires name`; `'create' requires name, command, and trigger`; `Unknown action 'frobnicate'; expected list\|get\|run\|create\|delete`. | — |
| SCHEDULED_TASK-04 | T3 | L | Opted-in: `create name:"WindowsMcpE2E-<runid>" command:"C:\\Windows\\System32\\cmd.exe" trigger:"daily"` | `trigger 'daily' is not a date and time. The only trigger create accepts is a one-time start, e.g. 2026-05-06T09:00:00; recurring triggers (daily, at logon) are not supported.` naming `trigger` — an `ArgumentException` raised before the scheduler is touched (D-10), so `get` says not found; the `trigger` description no longer offers `'daily'`/`'onlogon'`. | — |
| SCHEDULED_TASK-05 | T3 | L | Opted-in: same with `trigger:"2099-01-01T03:00:00"`; `get` | `created task '…'`; `NextRun` in 2099. | delete |
| SCHEDULED_TASK-06 | T3 | L | Opted-in: `create` again with the same name | Silently **overwrites** (no refusal) — record. | delete |
| SCHEDULED_TASK-07 | T3 | L | Opted-in: `run name:"WindowsMcpE2E-<runid>"` (inert `cmd.exe`) | `ran task '…'`; `get` shows `LastRun` updated. | — |
| SCHEDULED_TASK-08 | T3 | L | Opted-in: `delete name:"WindowsMcpE2E-<runid>" confirm:true`; `get` | `deleted task '…'`; `Scheduled task '…' not found`. | — |
| SCHEDULED_TASK-09 | T0 | L | `{"action":"delete","name":"NoSuchTask-<runid>","confirm":true}` | Record whether the error is caller-facing text (`FileNotFoundException`) or masked (`COMException`). | — |
| SCHEDULED_TASK-10 | T0 | L | By inspection: no protected-task guard (`\Microsoft\Windows\…` reachable by `delete`) | Record; not executed. | — |
| SCHEDULED_TASK-11 | T0 | L | `{"action":"get","name":"\\Microsoft\\Windows\\Defrag\\ScheduledDefrag"}` (a folder path) | Either the row or `not found` (the service reads the root folder) — record. | — |

#### registry_get (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| REGISTRY_GET-01 | T0 | L | `{"hive":"HKCU","path":"Software\\Microsoft\\Windows\\CurrentVersion"}`; `{"hive":"HKCU","path":""}` | `{Path, Values:[…], SubKeys:[…]}` with `SubKeys` non-empty; the hive root listing contains `Software`. | — |
| REGISTRY_GET-02 | T0 | L | `{"hive":"HKCU","path":"Environment","value_name":"Path"}`; `{"hive":"HKEY_CURRENT_USER","path":"Software"}` | `{Name:"Path", Kind:"ExpandString"\|"String", Data}`; the long hive form accepted. | — |
| REGISTRY_GET-03 | T0 | L | `{"hive":"HKCU","path":"Software\\WindowsMcpE2E\\does-not-exist"}`; `{"hive":"HKCU","path":"Environment","value_name":"NoSuchValueXYZ"}` | `Registry path not found: HKCU\Software\WindowsMcpE2E\does-not-exist`; an `IOException` text (not a null-data DTO). | — |
| REGISTRY_GET-04 | T0 | L | `{"hive":"HKXX","path":"Software"}` | `Unknown hive: 'HKXX'`. | — |
| REGISTRY_GET-05 | T0 | L | `{"hive":"HKLM","path":"SECURITY"}` | `Access to registry key 'HKLM\SECURITY' is denied. A protected key needs the server to run elevated.` (caller-visible since D-10, `Services/RegistryErrors.cs`; was DEF-014, masked). | — |
| REGISTRY_GET-06 | T0 | L | A value with an empty name under the scratch key (set via `powershell` `Set-ItemProperty -Name '(default)'`) | Listed as `(default)`. | — |
| REGISTRY_GET-07 | T0 | L | `{"hive":"HKCR","path":""}` | A large but well-formed response (no cap — record size; S3 candidate against XC-05). | — |
| REGISTRY_GET-08 | T0 | L | A `Binary` value (`HKCU\Control Panel\Desktop` `UserPreferencesMask`) | `Data` as a byte array, `Kind:"Binary"`. | — |

#### registry_set, registry_delete (scratch key only)

All writes go under `HKCU\Software\WindowsMcpE2E\<run-id>`; REGISTRY_DELETE-11 removes the parent.

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| REGISTRY_SET-01 | T1 | L | `{"hive":"HKCU","path":"Software\\WindowsMcpE2E\\<runid>","value_name":"Alpha","data":"one","kind":"String","confirm":true}`; `registry_get` | `set HKCU\Software\WindowsMcpE2E\<runid>\Alpha`; `{Name:"Alpha", Data:"one", Kind:"String"}` (the key was created). | — |
| REGISTRY_SET-02 | T1 | L | `kind:"DWord" data:"42"`; `kind:"QWord" data:"9000000000"`; `kind:"ExpandString" data:"%TEMP%\\x"` | `Kind:"DWord" Data:42`; `QWord`; `ExpandString` with the raw string. | — |
| REGISTRY_SET-03 | T1 | L | `kind:"DWord" data:"abc"` | An `ArgumentException` text about the value type not matching the kind; nothing written (`registry_get` → not found for that value). | — |
| REGISTRY_SET-04 | T1 | L | `kind:"Binary" data:"AABB"`; `kind:"MultiString" data:"a;b"` | Both **fail** with the type-mismatch `ArgumentException` — file S3 (two advertised kinds can never succeed from a string `data`; fix family: parse hex / split lines for those kinds). | — |
| REGISTRY_SET-05 | T1 | L | `kind:"dword" data:"7"` (lower-case); `kind:"Bogus" data:"x"`; then `registry_get` | Both succeed and are written as **`String`** (silent fallback) — file S3 (an unknown or mis-cased kind must be refused naming the six). | — |
| REGISTRY_SET-06 | T0 | L | No `confirm`; `hive:"HKXX"` with `confirm:true` | `'confirm: true' is required for registry writes` and nothing written; `Unknown hive: 'HKXX'`. | — |
| REGISTRY_SET-07 | T1 | L | REGISTRY_SET-01 twice | Identical result and value (idempotent). | — |
| REGISTRY_SET-08 | T0 | L | `{"hive":"HKLM","path":"SOFTWARE\\WindowsMcpE2E-NoSuch","value_name":"x","data":"x","kind":"String","confirm":true}` | `Access to registry key 'HKLM\SOFTWARE\WindowsMcpE2E-NoSuch' is denied. A protected key needs the server to run elevated.` (unelevated; caller-visible since D-10). If it unexpectedly succeeds: S1, delete the key at once and record. | — |
| REGISTRY_SET-09 | T1 | L | `value_name:"ключ" data:"значение"`; `value_name:"" data:"d"` | Unicode round-trips through `registry_get`; the empty name sets `(default)`. | — |
| REGISTRY_DELETE-01 | T1 | L | Seed `Alpha` and `Child\x` under the scratch key; `{"hive":"HKCU","path":"…\\<runid>","value_name":"Alpha","confirm":true}`; repeat | `{valueName:"Alpha", deleted:true, existed:true}`; then `existed:false`. | — |
| REGISTRY_DELETE-02 | T1 | L | `{"hive":"HKCU","path":"…\\<runid>","confirm":true}` (no recursive); `registry_get` | `'HKCU\…\<runid>' has 1 sub-key(s); pass recursive:true to delete the whole tree`; `Child` survives (XC-02). | — |
| REGISTRY_DELETE-03 | T1 | L | Same with `recursive:true`; repeat | `{deleted:true, existed:true, subKeysRemoved:1}`, then `Registry path not found`; second call `{deleted:false, existed:false}`. | — |
| REGISTRY_DELETE-04 | T0 | L | `{"hive":"HKCU","path":"Software","recursive":true,"confirm":true}`; `registry_get HKCU Software` | `Refusing to delete 'Software': 'Software' is a root the user's profile or Windows itself depends on. Delete a key beneath it instead.`; still lists sub-keys. | — |
| REGISTRY_DELETE-05 | T0 | L | Spellings: `software/`, `SOFTWARE\\`, `  Software  `, `\\Software`, `Software\\\\Classes`, `system/currentcontrolset`, `Environment`, `Control Panel`, `Volatile Environment`, `SAM`, `SECURITY` — each with `recursive:true confirm:true` | All refused with the guard message. | — |
| REGISTRY_DELETE-06 | T0 | L | `{"hive":"HKCU","path":"","recursive":true,"confirm":true}`; `{"hive":"HKCU","path":"   ","confirm":true}` | `Refusing to delete the hive root: 'path' is empty. Name the key to delete.` both. | — |
| REGISTRY_DELETE-07 | T0 | L | `{"hive":"HKCU","path":"Environment","value_name":"WindowsMcpE2E-NoSuch","confirm":true}` | Allowed (value deletes under a guarded root are not blocked) → `existed:false`. | — |
| REGISTRY_DELETE-08 | T0 | L | `{"hive":"HKCU","path":"Software\\WindowsMcpE2E","recursive":true}` (no confirm); `{"hive":"HKXX",…,"confirm":true}` | `'confirm: true' is required for registry deletes`; `Unknown hive: 'HKXX'`. | — |
| REGISTRY_DELETE-09 | T0 | L | `{"hive":"HKLM","path":"SOFTWARE\\WindowsMcpE2E-NoSuch","confirm":true}` | `existed:false` (the key is absent and its parent readable). Were the open denied, it would be `Access to registry key 'HKLM\SOFTWARE\WindowsMcpE2E-NoSuch' is denied. …` (D-10) — record which. | — |
| REGISTRY_DELETE-10 | T0 | L | Under HKLM/HKCR/HKU: `path:"Software" confirm:true` (guard is hive-agnostic) | The guard message for each hive. | — |
| REGISTRY_DELETE-11 | T1 | L | `{"hive":"HKCU","path":"Software\\WindowsMcpE2E","recursive":true,"confirm":true}`; `registry_get` | Deleted; `Registry path not found: HKCU\Software\WindowsMcpE2E` (ledger item restored). | — |

#### env (Process scope only; User is T3; Machine is refusal-only)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| ENV-01 | T0 | L | `{"action":"list","scope":"Process"}`; `scope:"User"`; `scope:"Machine"` | Objects; Process contains `Path` and `SystemRoot`. | — |
| ENV-02 | T1 | L | `{"action":"set","name":"WINDOWSMCP_E2E_TOKEN","value":"s3cret","scope":"Process","confirm":true}`; `get`; `get include_secrets:true`; `list` | `set 'WINDOWSMCP_E2E_TOKEN' in Process`; `"***REDACTED***"`; `"s3cret"`; redacted in `list` unless `include_secrets:true`. | ENV-05 |
| ENV-03 | T1 | L | `powershell {"command":"$env:WINDOWSMCP_E2E_TOKEN"}` | `s3cret` (the child inherits the Process-scope write). | — |
| ENV-04 | T1 | L | `{"action":"set","name":"WINDOWSMCP_E2E_EMPTY","value":"","scope":"Process","confirm":true}`; `get` | The message says `set …` but `get` returns `null` — file S3 (an empty value deletes silently; the message must say so or the tool must refuse). | — |
| ENV-05 | T1 | L | `{"action":"set","name":"WINDOWSMCP_E2E_TOKEN","value":null,"scope":"Process","confirm":true}`; `get` | `deleted 'WINDOWSMCP_E2E_TOKEN' from Process`; `null`. | — |
| ENV-06 | T0 | L | `set` without confirm; `{"action":"set","scope":"bogus","confirm":true}`; `{"action":"get"}`; `{"action":"frobnicate"}` | `'confirm: true' is required for set`; `Unknown scope 'bogus'; expected Process\|User\|Machine` (scope parsed first); `'get' requires name`; `Unknown action 'frobnicate'; expected get\|set\|list`. | — |
| ENV-07 | T0 | L | `{"action":"set","name":"WINDOWSMCP_E2E_MACHINE","value":"x","scope":"Machine","confirm":true}` | A failure on the unelevated server — record caller-visible vs masked (`SecurityException`). If it succeeds: S1, delete at once (`value:null`). | — |
| ENV-08 | T3 | L | Opted-in: `set … scope:"User"` then `value:null`; `registry_get HKCU Environment` in between | The value appears under `HKCU\Environment` and disappears; ledger clean. | — |
| ENV-09 | T0 | L | By inspection: no protected-name list (`Path`, `SystemRoot`, `TEMP`) | Record; never executed. | — |
| ENV-10 | T1 | L | `{"action":"set","name":"WINDOWSMCP_E2E_ü","value":"日本語","scope":"Process","confirm":true}`; `get` | Round-trips. | delete |

#### power_action (refusal paths only; every valid action with `confirm:true` is T4)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| POWER_ACTION-01 | T0 | L | `{"action":"shutdown"}`; `{"action":"reboot","confirm":false}`; `{"action":"lock","confirm":false}`; `{"action":"SHUTDOWN","confirm":false}` | `'confirm: true' is required for power actions` for all four; the machine is up. | — |
| POWER_ACTION-02 | T0 | L | `{"action":"frobnicate","confirm":true}`; `{"action":"","confirm":true}` — only after reading `PowerService.cs:17-48` on the build under test and confirming the `switch` default precedes every P/Invoke | `Unknown power action 'frobnicate'; expected shutdown\|reboot\|logoff\|lock\|sleep\|hibernate`. | — |
| POWER_ACTION-03 | T0 | S | `tools/list` annotations | `destructiveHint:true`, `readOnlyHint:false`. | — |
| POWER_ACTION-04 | T0 | — | By inspection: an OS refusal (`ExitWindowsEx`, `LockWorkStation`, `SetSuspendState` returning false) | `Windows refused the power action '<action>': <Windows' reason> (Win32 error N).` plus a hint for sleep/hibernate/lock — an `InvalidOperationException` since D-10 (`PowerServiceTests`, through the `IPowerNative` seam). Only the token plumbing in `Win32PowerNative.EnableShutdownPrivilege` still throws a masked `Win32Exception`, on purpose. Not executed. | — |
| POWER_ACTION-05 | T0 | — | By inspection: a single boolean is the only gate | Record for the sign-off; not a defect by the current design. | — |
| POWER_ACTION-06 | T4 | — | The six verbs with `confirm:true` | **Never executed.** Recorded SKIPPED (T4) in the log. | — |

#### wmi_query (ReadOnly; never `Win32_Product`, `Win32_NTLogEvent`, `CIM_DataFile`)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| WMI_QUERY-01 | T0 | L | `{"class_name":"Win32_OperatingSystem"}` | Exactly one object with `Caption`, `Version`, `BuildNumber`. | — |
| WMI_QUERY-02 | T0 | L | `{"class_name":"Win32_Process","where":"ProcessId=<child>"}` | One row; `ParentProcessId` equals `process_inspect`'s `ParentPid`. | — |
| WMI_QUERY-03 | T0 | L | `{"class_name":"Win32_ComputerSystem","namespace":"root\\cimv2"}` vs omitted | Identical. | — |
| WMI_QUERY-04 | T0 | L | `{"class_name":"Win32_NoSuchClassXYZ"}`; `where:"ThisIsNotAProperty=1"`; `namespace:"root\\definitely-not"` | Contract: a message naming the class / clause / namespace. Code: `ManagementException` masked → **`An error occurred invoking 'wmi_query'.`** ×3 — file S2 (fix family: catch `ManagementException`/`COMException` in `WmiService`, rethrow `InvalidOperationException` with the query and the WMI message). Liveness probe after each. | — |
| WMI_QUERY-05 | T0 | L | `{"class_name":"Win32_Process WHERE Name='explorer.exe'"}` | Succeeds — `class_name` is interpolated unvalidated; record as S4 (read-only WQL, so a finding, not a hole). | — |
| WMI_QUERY-06 | T0 | L | `{"class_name":"AntiVirusProduct","namespace":"root\\SecurityCenter2"}` | Defender's row (namespaces unrestricted; record). | — |
| WMI_QUERY-07 | T0 | L | `{"class_name":"Win32_Service"}` | Hundreds of rows × ~25 properties; record the size (no cap; S3 candidate against XC-05). | — |
| WMI_QUERY-08 | T0 | L | `{"class_name":"Win32_LogicalDisk","where":"DriveType=3"}` | Rows for fixed disks only. | — |
| WMI_QUERY-09 | T0 | L | `{"class_name":""}` | Masked or a message — record. | — |

#### event_log (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| EVENT_LOG-01 | T0 | L | `{"log":"Application","max":5}` | ≤ 5 entries, `Time` descending, each with `Id, Source, Level, Message`. | — |
| EVENT_LOG-02 | T0 | L | `{"log":"System","level":"error","max":10}`; `level:"ERROR"` | Every row `Level == "Error"` (case-insensitive). | — |
| EVENT_LOG-03 | T0 | L | `{"log":"Application","since":"<now−7d ISO Z>","max":20}` | Every `Time` ≥ the cutoff; record the local/UTC skew (`since` parsed round-trip, compared to local `TimeGenerated`) — S3 candidate. | — |
| EVENT_LOG-04 | T0 | L | `source:"<a source from EVENT_LOG-01>"` | Every row's `Source` matches. | — |
| EVENT_LOG-05 | T0 | L | `{"log":"Application","since":"yesterday"}` | `'since' must be a valid ISO 8601 datetime, got: 'yesterday'`. | — |
| EVENT_LOG-06 | T0 | L | `{"log":"Application","level":"critical","max":5}` | `[]` with `isError:false` — file S3 (an unrecognised `level` must be refused naming the accepted values; "no errors" and "misspelled level" are indistinguishable). | — |
| EVENT_LOG-07 | T0 | L | `max:0`; `max:-1`; `max:1000000` (time it) | `[]`; `[]` (no throw); the whole log — record elapsed (the scan is uncapped regardless of `max`; S3 candidate). | — |
| EVENT_LOG-08 | T0 | L | `{"log":"NoSuchLogXYZ"}` | `The event log 'NoSuchLogXYZ' on computer '.' does not exist.` (caller-visible). | — |
| EVENT_LOG-09 | T0 | L | `{"log":"Security","max":1}` | Success, a caller-visible access-denied, or the masked text — record (the description advertises `Security`). | — |
| EVENT_LOG-10 | T0 | L | `{"log":"Microsoft-Windows-Kernel-Power/Operational","max":1}` | Record the classic-only limitation (failure or empty). | — |

### 7.5 Screen, system, notification, audio, network, web, security

Verbatim messages come from `Tools/ScreenTools.cs`, `Services/RegionMath.cs`, `Services/ScaleMath.cs`,
`Services/ScreenshotService.cs`, `Services/OcrService.cs`, `Tools/SystemTools.cs`,
`Services/NotificationService.cs`, `Services/AudioService.cs`, `Tools/NetworkTools.cs`,
`Services/FirewallService.cs`, `Tools/WebTools.cs`, `Services/WebService.cs`, `Tools/SecurityTools.cs`,
`Services/CertStoreService.cs`, `Services/SecurityService.cs`, `Tools/StartupTools.cs` on 0.7.3.
Screenshot files are written to `%TEMP%\WindowsMcp`; every row that uses `output:"file"` deletes
exactly the `path` it was given (`file_manage delete confirm:true`).

#### screenshot (ReadOnly; returns content blocks)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| SCREENSHOT-01 | T0 | L | `{}` | 2 blocks (metadata text + image); `format:"jpeg"`, `backend` one of `gdi`/`wgc`, `region` == display 1's rect (`0,0,2560,1440`), `displays` has 2 entries with the six fields, `cursor.monitorIndex` matches the pointer, `coordinateScale` present (2560→1920 fit) with a `note` starting `multiply image pixel coordinates by`, no `path`, no `selectedDisplays`. | — |
| SCREENSHOT-02 | T0 | L | `{"display":"0"}`; `{"display":"1"}`; `{"display":"all"}`; `{"display":"1,0"}`; `{"display":"0,0"}` | Display 0: `region {x:-2560,y:0,w:2560,h:1440}`, `selectedDisplays:[0]`, `note` starting `virtual-desktop x = -2560 + imageX × …`. Display 1: origin note absent. `all`: `region {x:-2560,y:0,w:5120,h:1440}`, `originalWidth:5120`. `1,0`: same union, `selectedDisplays:[1,0]` (order kept). `0,0`: de-duplicated to `[0]`. | — |
| SCREENSHOT-03 | T0 | L | `{"display":"2"}`; `{"display":""}`; `{"display":"a"}`; `{"display":"2","region":"1,2,3"}` | `Invalid display '2': index 2 is not a monitor; valid: 0,1 (see multi_monitor)`; `Invalid display '': no indices given; expected 'all' or indices from 0,1`; `Invalid display 'a': 'a' is not a monitor index; …`; with both invalid the **display** message wins. No flash appeared (nothing captured). | — |
| SCREENSHOT-04 | T0 | L | `{"region":"-200,100,400,200"}` (spans the seam); `{"region":"-2560,0,300,200"}`; `{"region":"-2561,0,300,200"}`; `{"region":"2400,1300,200,200"}` | Seam: success, `region` echoed, `originalWidth:400`. Negative origin: success with the `virtual-desktop x = -2560 + …` note. Past the left edge: `Region -2561,0,300,200 is not inside the virtual screen, which spans x -2560..2559, y 0..1439 in virtual-desktop pixels; see multi_monitor for each monitor's bounds.` (rejected, never clipped). Bottom-right overflow (`2400+200 > 2560`): the same refusal. | — |
| SCREENSHOT-05 | T0 | L | `{"region":"1,2,3"}`; `{"region":"1,,3,4"}`; `{"region":"1,x,3,4"}`; `{"region":"1,2,0,4"}`; `{"region":"1,2,3,-4"}`; `{"region":"0,0,2147483647,10"}` | `Invalid region '1,2,3'; expected 'x,y,w,h'`; `Invalid region '1,,3,4': the y part is empty; expected 'x,y,w,h'`; `… '1,x,3,4': 'x' is not an integer (y); …`; `… width must be positive, got 0`; `… height must be positive, got -4`; the huge width → the not-inside refusal (long arithmetic, no wrap). | — |
| SCREENSHOT-06 | T0 | L | `{"output":"file"}`; `{"output":"file","format":"jpeg"}`; `{"output":"base64"}`; `{"output":"disk"}` | 1 block with `path` under `%TEMP%\WindowsMcp` ending `.png` (auto→png for file); `.jpg`; `base64` behaves as inline (2 blocks); `Unknown output 'disk'; expected inline\|file\|base64`. | delete the two files |
| SCREENSHOT-07 | T0 | L | `{"format":"jpg"}`; `{"format":"PNG"}`; `{"backend":" gdi"}`; `{"backend":"dxcam"}` | `Unknown format 'jpg'; expected png\|jpeg\|auto`; `PNG` accepted (case-insensitive) → `format:"png"`; `Unknown backend ' gdi'; expected auto\|gdi\|wgc` (not trimmed); `Unknown backend 'dxcam'; …`. | — |
| SCREENSHOT-08 | T0 | L | `max_width:-1`; `max_height:-1`; `scale:0`; `scale:1.0000001`; `quality:0`; `quality:101`; `grid_columns:65`; `grid_rows:-1` | `max_width must be 0 (no limit) or positive, got -1`; `max_height …`; `scale must be in (0, 1], got 0`; `… got 1.0000001`; `quality must be 1-100, got 0`; `… got 101`; `grid_columns must be 0 (no grid) to 64, got 65`; `grid_rows must be 0 (no grid) to 64, got -1`. | — |
| SCREENSHOT-09 | T0 | L | `{"max_width":0,"max_height":0,"output":"file"}`; `{"scale":0.5,"output":"file"}`; `{"quality":1,"output":"file"}` and `quality:100` | Full 2560×1440, no `coordinateScale`, no `note`; `width:960` (1920 fit × 0.5) with `coordinateScale`; both qualities succeed with different file sizes. | delete |
| SCREENSHOT-10 | T0 | L | `{"backend":"gdi","output":"file"}`; `{"backend":"wgc","output":"file"}`; `{"backend":"auto","output":"file"}` | `backend:"gdi"`; `backend:"wgc"` (or the exact refusal `backend 'wgc' could not capture …: Windows.Graphics.Capture is unavailable or refused the rect …. Use backend 'auto' or 'gdi'.`); `auto` → `wgc` on this box. | delete |
| SCREENSHOT-11 | T0 | L | `{"grid_columns":64,"grid_rows":64,"output":"file"}`; `{"grid_columns":4,"grid_rows":2,"annotate":false,"output":"file"}` | `grid:{columns:64,rows:64}`, no snapshot walk (no second text block); captions are virtual-desktop coordinates (inspect the file). | delete |
| SCREENSHOT-12 | T0 | L | `owned:Notepad` on display 0; `{"display":"0","annotate":true}` | 3 blocks: metadata (`annotated:true`, `annotations ≥ 1`), the element rows, the image; every listed element's bounds inside `region`; `click element_id:el_1` afterwards lands. | — |
| SCREENSHOT-13 | T0 | L | `snapshot` → `el_A`; `{"annotate":true}`; `get_element el_A` | `Element 'el_A' has expired: …` (annotate is a snapshot walk and evicts ids — documented). | — |
| SCREENSHOT-14 | T0 | S | `--max-tree-elements 5 --flash off --call screenshot '{"annotate":true,"output":"file"}'` | The element text block ends with the budget note; the metadata JSON has **no** truncation field — record as S3 (metadata should carry `truncated`/`elementLimit` like `snapshot`'s JSON). | delete |
| SCREENSHOT-15 | T0 | L | `{"include_cursor":true}` with the pointer inside the region; `include_cursor:false`; pointer parked on display 0 with `display:"1"` | `cursorDrawn:"icon"` or `"ring"`; absent; absent (pointer outside the rect) while `cursor` still reports the position and `monitorIndex:0`. | — |
| SCREENSHOT-16 | T0 | S | `--flash on` (default) `--call screenshot '{"output":"file"}'`; then `--flash off` | Metadata `flash:true` only when the glow became visible; with `--flash off` the key is absent. | delete |
| SCREENSHOT-17 | T0 | S | `--profile-snapshot on --flash off --call screenshot '{"annotate":true,"output":"file"}'` | `stages` present with `resolve`, `cursor`, `snapshot`, `capture`, `resize`, `encode`. | delete |
| SCREENSHOT-18 | T0 | L | Liveness and size: `{"display":"all","max_width":0,"max_height":0}` inline | A 5120×1440 JPEG inline (record the byte size); `system_info os` answers after. | — |
| SCREENSHOT-19 | T0 | L | Session-0 / DRM window black-capture (A-10 "done when") | SKIPPED: needs a protected video playing; record as a future configuration. | — |

#### ocr (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| OCR-01 | T0 | L | `owned:Notepad` showing `OCR-PROBE-<runid>` in a large font; `{"region":"<its bounds>"}` | The returned text contains `OCR-PROBE` (allow OCR noise in the id). | clear |
| OCR-02 | T0 | L | `{}`; `{"display":"1"}`; `{"display":"0"}` (Notepad moved there) | Non-empty text; display 1 equals the default; display 0 finds the probe text after the move. | move back |
| OCR-03 | T0 | L | `{"display":"99"}`; `{"region":"1,2,3"}` and the same `region` on `screenshot` | `Invalid display '99': index 99 is not a monitor; valid: 0,1 (see multi_monitor)`; byte-identical `Invalid region '1,2,3'; expected 'x,y,w,h'` from both tools. | — |
| OCR-04 | T0 | L | `{"region":"-300,600,600,200"}` (seam) with a window straddling it | Text from both halves. | — |
| OCR-05 | T0 | L | `{"display":"all"}` (5120 px wide) | Text, or the masked `An error occurred invoking 'ocr'.` from `OcrEngine.MaxImageDimension` — record; S3 if masked (fix family: check the engine's max dimension and refuse or tile). Liveness probe after. | — |
| OCR-06 | T0 | S | `--flash on --call ocr '{}'` | No `flash` key, no glow (ocr never touches the overlay). | — |
| OCR-07 | T0 | L | No language pack case | SKIPPED unless a pack is absent: then `No OCR language pack installed`. | — |

#### system_info, driver_list, reliability (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| SYSTEM_INFO-01 | T0 | L | `os`; `memory`; `disk`; `gpu`; `battery`; `BATTERY` | Arrays: `Caption` containing `Windows`; ≥ 1 row with `Capacity`; a row with `DeviceID:"C:"`; ≥ 1 row with `Name` (record the payload size); `[]` on a desktop (not an error); case-insensitive. | — |
| SYSTEM_INFO-02 | T0 | L | `{"category":"cpu"}`; `{"category":""}` | `Unknown category 'cpu'; expected os\|memory\|disk\|gpu\|battery`; the same quoting `''`. | — |
| DRIVER_LIST-01 | T0 | L | `{}` | Non-empty array; every `DeviceName` non-blank; at least one `IsSigned:true`; record row count, byte size and elapsed (no cap, no timeout — S3 candidate against XC-05). | — |
| DRIVER_LIST-02 | T0 | L | `{}` twice | Identical row counts. | — |
| RELIABILITY-01 | T0 | L | `{}`; `{"max_records":3}` | `{Minidumps:[…], RecentFailures:[…], Note?}`; `Minidumps` may be `[]` (normal); ≤ 3 failures. | — |
| RELIABILITY-02 | T0 | L | `max_records:0`; `max_records:-1` | `RecentFailures:[]` both, no error (unvalidated — S4 candidate). | — |
| RELIABILITY-03 | T0 | L | Field check | Every failure record has a parseable timestamp and a `Source`/`Message`; the `Note` (if present) starts `reliability records unavailable:`. | — |

#### notification, audio

`notification` registers `HKCU\Software\Classes\AppUserModelId\Windows-MCP` on first use and never
removes it (ledger item). `audio set` drives the real volume to zero and back up with media
keys sent to the **foreground** window, and `mute`/`unmute` both send the same toggle. Before the
audio block, capture the true endpoint volume and mute out of band with a `powershell` CoreAudio
snippet (the `IAudioEndpointVolume` interop, read-only), keep `owned:Notepad` in front, and
restore the captured values with `audio set` and an even number of toggles at the end.

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| NOTIFICATION-01 | T2* | L | `{"title":"WindowsMcpE2E","message":"hello <runid>"}` twice | `{shown:true, appId:"Windows-MCP", registered:true}` both; the toast visible (unless Focus Assist); no second registry write. *T3 if the ledger showed the AUMID key absent. | dismiss the toast |
| NOTIFICATION-02 | T0 | L | `app_id:""`; `app_id:"   "` | `'app_id' must not be blank; omit it for the server's own id`. | — |
| NOTIFICATION-03 | T2 | L | `app_id:"Contoso.NotRegistered.<runid>"` | `shown:false, registered:false`, `note` containing `0x80070490` and `HKCU\Software\Classes\AppUserModelId\<id>`; ≥ 1 s elapsed (the retry). | — |
| NOTIFICATION-04 | T2 | L | `title:"a & b <c>"`, `message:"\"quoted\""` | `shown:true`; the toast renders the literal characters. | dismiss |
| NOTIFICATION-05 | T2 | L | `app_id:"Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"` | `registered:true` (inferred from `!`); the toast under Terminal's identity. | dismiss |
| NOTIFICATION-06 | T2 | L | 5 000-character `message` | `shown:true` (no cap — record). | dismiss |
| AUDIO-01 | T0 | L | `{"action":"get"}`; `{"action":"GET"}` | `{"Level":n,"Muted":false}` with 0 ≤ n ≤ 100; note `n == 50` means the `Get-AudioDevice` cmdlet is absent and the value is a fallback; `Muted` is always `false` — record both as S3 (get cannot report mute; fallback indistinguishable from a real 50). | — |
| AUDIO-02 | T0 | L | `{"action":"set"}`; `{"action":"toggle"}` | `'set' requires level`; `Unknown action 'toggle'; expected get\|set\|mute\|unmute`. | — |
| AUDIO-03 | T2 | L | Out-of-band capture `V0,M0`; `{"action":"set","level":30}`; out-of-band read | ≈ 30 (±2, the 2 % steps); the OSD storm is visible. | `set level:V0` |
| AUDIO-04 | T2 | L | `{"action":"set","level":-5}`; `{"action":"set","level":500}` | `volume set to -5` / `volume set to 500` (echoes the unclamped value) while the endpoint reads 0 / 100 — file S3 (out-of-range `level` must be refused naming 0-100). | `set level:V0` |
| AUDIO-05 | T2 | L | `{"action":"mute"}`; out-of-band read; `{"action":"unmute"}`; read; `mute` twice | Flipped; flipped back; two mutes cancel out — file S3 (`unmute` is a toggle, not a setter; the interface documents it as a known limitation). Finish on an even count so `M0` holds. | verify `M0` |
| AUDIO-06 | T2 | L | Ledger: out-of-band read at block end | Equals `V0,M0`. | — |

#### network, http_request (network egress; read-only)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| NETWORK-01 | T0 | L | `adapters`; `ports`; `wifi` | Arrays; `ports` lists at least one `Listen` row (record row count — no cap); `wifi` a status object or a note on a wired box. | — |
| NETWORK-02 | T0 | L | `{"action":"ping","host":"127.0.0.1"}`; `{"action":"ping","host":"192.0.2.1"}` (TEST-NET, unreachable) | Success with round-trip times; a timeout reported as **data** or a caller-visible error — record. | — |
| NETWORK-03 | T0 | L | `{"action":"dns","host":"example.com"}`; `{"action":"dns","host":"no-such-host.invalid"}` | Addresses; the unresolvable host: contract says a message naming the host, code raises `SocketException` (masked) → expect **`An error occurred invoking 'network'.`** — file S2 (fix family: catch `SocketException` in `NetworkService`, rethrow `InvalidOperationException` with the host and the resolver message). Liveness probe after. | — |
| NETWORK-04 | T0 | L | `{"action":"ping"}`; `{"action":"dns"}`; `{"action":"traceroute"}` | Refusals naming `host` (record verbatim); `Unknown action 'traceroute'; expected adapters\|ports\|ping\|dns\|wifi` (record verbatim). | — |
| NETWORK-05 | T0 | L | `{"action":"ping","host":"example.com","port":80}` | `port` is documented as reserved: accepted and ignored — record. | — |
| HTTP_REQUEST-01 | T0 | L | `{"url":"https://example.com"}`; `{"url":"https://httpbin.org/get","headers_json":"{\"X-E2E\":\"<runid>\"}"}` | Body containing `Example Domain`; the echoed `X-E2E` header in the JSON. | — |
| HTTP_REQUEST-02 | T0 | L | `{"url":"https://httpbin.org/post","method":"POST","body":"{\"a\":1}","headers_json":"{\"Content-Type\":\"application/json\"}"}`; `method:"PUT"`, `"PATCH"`, `"DELETE"` against `https://httpbin.org/anything` | Each echoes the method and body. | — |
| HTTP_REQUEST-03 | T0 | L | `{"url":"http://127.0.0.1:8765/"}`; `{"url":"http://10.0.0.1/"}`; `{"url":"http://localhost/"}`; `{"url":"file:///C:/Windows/win.ini"}`; `{"url":"ftp://example.com/"}` | Private-IP refusals naming the resolved address; `localhost` refused (resolves to loopback); unsupported scheme refusals (record verbatim). | — |
| HTTP_REQUEST-04 | T0 | L | `{"url":"https://example.com/definitely-missing"}` (404); `{"url":"https://no-such-host-<runid>.invalid/"}`; `{"url":"https://example.com:81/"}` (connection refused/timeout) | Record each: contract wants a message naming the URL and the status/cause; code raises `HttpRequestException`/`TaskCanceledException` (masked) → expect **`An error occurred invoking 'http_request'.`** for the last two — file S2 (fix family: mirror `scrape`'s wrapping in `WebService`). Liveness probe after each. | — |
| HTTP_REQUEST-05 | T0 | L | `method:"BREW"`; `headers_json:"not json"`; `headers_json:"[1]"` | Record: `method` is unvalidated (a framework error or a 405); malformed headers → a refusal naming `headers_json` or a masked `JsonException` — file S3 if masked. | — |
| HTTP_REQUEST-06 | T0 | L | `{"url":"https://httpbin.org/bytes/2000000"}` | 2 MB body returned uncapped — record size; S3 candidate against XC-05. | — |
| HTTP_REQUEST-07 | T0 | L | `{"url":"https://httpbin.org/redirect-to?url=http://127.0.0.1:8765/"}` | The private-address check runs on the URL as given, so the redirect is **followed** (documented pre-existing hole shared with `scrape`) — record the observed behaviour and file S2 if the loopback body is returned (fix family: re-check each redirect hop, or disable auto-redirect and surface `Location`). | — |
| HTTP_REQUEST-08 | T0 | L | `{"url":"https://httpbin.org/delay/10"}` | Returns after ≈ 10 s (under the 100 s client timeout). | — |

#### scrape (ReadOnly, OpenWorld)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| SCRAPE-01 | T0 | L | `{"url":"https://example.com"}` | `{Source:"http", Url:"https://example.com/", Title:"Example Domain", Chars:n, Truncated:false, Summarized:false, Model:null}` and markdown `Content`. | — |
| SCRAPE-02 | T0 | L | `max_chars:1`; `max_chars:1000000`; `max_chars:1000001`; `max_chars:0` | `Truncated:true`, `Content.length == 1`, `Chars` = the full size; accepted; `max_chars must be 1-1000000 (0 is not 'all'), got 1000001.`; the same quoting `0`. | — |
| SCRAPE-03 | T0 | L | `{"source":"ftp","max_chars":0}`; `{"source":"http","window":"Edge","url":"https://example.com"}`; `{"source":"dom","url":"https://x"}`; `{"url":"https://example.com","query":"who?"}`; `{"source":"http"}` (no url) | `Unknown source 'ftp'; expected http\|dom.` (source wins); `window is only used with source:dom; source:http fetches url.`; the `url is only used with source:http…` message; `query needs summarize:true: the summary is what answers it.`; a refusal naming `url` (record verbatim). All before any fetch. | — |
| SCRAPE-04 | T0 | L | `{"url":"http://127.0.0.1:8080/"}`; `{"url":"file:///C:/Windows/win.ini"}`; `{"url":"https://user:pass@example.com/?q=1"}` | `URL targets a private IP address; refusing (resolved: 127.0.0.1)`; `Unsupported URL scheme 'file' …`; the returned `Url` has no `user:pass` and keeps `?q=1`. | — |
| SCRAPE-05 | T0 | L | `{"url":"https://example.com/definitely-missing"}`; `{"url":"https://no-such-host-<runid>.invalid/"}`; `{"url":"https://example.com:81/"}` | Each a caller-visible message starting `<url> could not be fetched:` (404 / unresolvable / refused), or a `TimeoutException` text naming the seconds — none masked (contrast HTTP_REQUEST-04). | — |
| SCRAPE-06 | T0 | L | `owned:Edge` in front; `{"source":"dom"}`; `{"source":"dom","window":"WindowsMcpE2E Probe Page"}` | `Source:"dom"`, `Url` the `file:///` URL, `Title` the page title, `Content` with `Probe heading` and without `Last paragraph.`, ending `Reached top of the page; scroll down to see more.` A dom read is a snapshot walk: a snapshot `el_N` taken before it then answers `Element 'el_N' has expired: …` (the description says so). | — |
| SCRAPE-07 | T0 | L | `{"source":"dom","window":"<owned:Notepad title>"}`; all Chromium windows minimised/closed: `{"source":"dom"}` | `source:dom: '<title>' is not a browser window. Only Chromium browsers (Edge, Chrome, Brave, Opera, Vivaldi) expose their page; name one with window:<title>, or fetch the page with source:http and a url.`; `source:dom needs an open Chromium browser window (Edge, Chrome, Brave, Opera, Vivaldi) and none is open. Open windows: '…'. Open the page in one of them, or fetch it with source:http and a url.` Firefox case SKIPPED (not installed). | — |
| SCRAPE-08 | T0 | L | `{"url":"https://example.com","summarize":true}`; then `summarize:true, query:"What is this page for?"` | Either `Summarized:true` with `Model` set (the client declared sampling) or `Summarized:false` with `Note` exactly `summarize:true was ignored: this client did not declare the sampling capability, so the content is returned as is` — **record in the run header**. With `query`, the summary answers it when sampling works. | — |
| SCRAPE-09 | T0 | H | HOST-47 | The stateless note (`stateless`, `HTTP`, `stdio`; not `capability`). | — |
| SCRAPE-10 | T0 | S | `--max-tree-elements 20 --flash off`, `owned:Edge` in front, `--call scrape '{"source":"dom"}'` | `Truncated:true` and `Note` containing `the page walk stopped at the element budget (20 elements)`; or the walk-hit-budget-before-a-page refusal naming `--max-tree-elements` — record which. | — |
| SCRAPE-11 | T0 | L | A page nested > 300 levels: `<scratch>\page-deep.html` cannot be fetched (private IP) — use `https://httpbin.org/html` is shallow, so SKIPPED on L; on S run `WebServiceScrapeTests`' `/deep/400` equivalent via `LocalHttpServerFixture` (X channel) and record | Refusal `… nests its elements 400 levels deep, past the 300-level limit …. Read the raw HTML with http_request instead.`; the server alive after (XC-11). | — |
| SCRAPE-12 | T0 | L | `{"url":"https://example.com","max_chars":50}` twice | Identical (idempotent). | — |

#### firewall (list and refusals only)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| FIREWALL-01 | T0 | L | `{"action":"list"}`; `{"action":"LIST"}` | ≤ 100 rows, every `Enabled:"True"`; case-insensitive. | — |
| FIREWALL-02 | T0 | L | `name_like:"Core Networking"`; `name_like:"zzz-no-such-rule"`; `name_like:"O'Brien"` | Non-empty with every `DisplayName` containing the text; `[]`; `[]` with no PowerShell syntax error (quote escaping). | — |
| FIREWALL-03 | T0 | L | `max:1`; `max:0`; `max:-1` | One row; `[]`; `[]` with no error — file S4 (unvalidated `max`; a negative value should be refused). | — |
| FIREWALL-04 | T0 | L | `{"action":"add","name":"WindowsMcpE2E-<runid>","direction":"Inbound","action_type":"Allow","port":65000}` (no confirm); `list name_like:"WindowsMcpE2E"` | `'confirm: true' is required for firewall add`; `[]`. | — |
| FIREWALL-05 | T0 | L | `{"action":"add","confirm":true}`; then add `name`; then `direction`; then `action_type` (never `port`) | `'name' is required for firewall add`; `'direction' …`; `'action_type' …`; `'port' is required for firewall add`. Four calls, zero mutations; `list name_like:"WindowsMcpE2E"` → `[]`. | — |
| FIREWALL-06 | T0 | L | `{"action":"remove","name":"Core Networking - DNS (UDP-Out)"}` (no confirm); `{"action":"remove","confirm":true}` (no name); `{"action":"enable"}` | `'confirm: true' is required for firewall remove` and the rule still listed; `'name' is required for firewall remove`; `Unknown action 'enable'; expected list\|add\|remove`. | — |
| FIREWALL-07 | T3 | L | Opted-in (human operator, elevated): `add` the E2E rule with `confirm:true`; `list name_like`; `remove … confirm:true`; `list` | Created (TCP, local port 65000); one row; `Firewall remove …` success text; `[]`. An AI operator records BLOCKED. | — |
| FIREWALL-08 | T0 | L | `{"action":"add","name":"WindowsMcpE2E-<runid>","direction":"Sideways","action_type":"Allow","port":65000,"confirm":true}` | Unelevated: `Firewall add failed: <stderr>` (caller-visible) and `list` → `[]`; the direction value is not validated locally — record. **Skip if the server is elevated** (it would create a rule). | — |

#### cert_store, defender_status, security_audit, startup_report, verify_signature (ReadOnly)

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| CERT_STORE-01 | T0 | L | `{}`; `{"location":"LocalMachine","store_name":"Root"}`; `{"location":"currentuser"}`; `{"store_name":"CA"}`; `{"store_name":"My"}` | Arrays; every element has a 40-char `Thumbprint` and `NotAfter`; ≥ 1 `SelfSigned:true` in Root; identical to the explicit form; case-insensitive location; `CA` larger and mostly `SelfSigned:false`; `My` an array (possibly empty). Any `Expired:true` has `NotAfter` in the past. | — |
| CERT_STORE-02 | T0 | L | `{"location":"Machine"}`; `{"location":""}` | `Unknown store location 'Machine'; expected LocalMachine or CurrentUser` (and for `''`). | — |
| CERT_STORE-03 | T0 | L | `{"store_name":"NoSuchStore-<runid>"}` | Contract: a message naming the store. Code: `CryptographicException` masked → **`An error occurred invoking 'cert_store'.`** — file S3 (fix family: validate `store_name` against `StoreName` names or catch and rethrow `InvalidOperationException`). | — |
| DEFENDER_STATUS-01 | T0 | L | `{}` twice | Booleans for `RealTimeProtectionEnabled`, `IsTamperProtected`; `AntivirusSignatureVersion` a version; the three timestamps each `null` or ISO-8601; `Note` absent; identical signature version across calls; record the first-call elapsed (PowerShell cold start). On a third-party-AV box: all null with `Note` exactly `Defender status unavailable (Get-MpComputerStatus failed; Defender may be disabled or replaced by a third-party AV)`. | — |
| SECURITY_AUDIT-01 | T0 | L | `{}` twice | `DefenderRunning` boolean, `UacLevel` ∈ {0,1,2,5}, `BitlockerStatus` null unelevated (documented), `Note` absent; identical across calls; `DefenderRunning` agrees with `defender_status.AntivirusEnabled`; `FirewallEnabled:true` agrees with `firewall list` non-empty. | — |
| SECURITY_AUDIT-02 | T0 | — | By inspection: `AuditAsync` deserialises without a `JsonException` guard while `GetDefenderStatusAsync` has one | S4 by inspection; not executed. | — |
| STARTUP_REPORT-01 | T0 | L | `{}`; `{"format":"JSON"}`; `{"format":"text"}`; `{"format":"both"}` | Summary text starting `Windows-mcp Startup Report — SUMMARY` with `== Section counts ==` and no `"Header"`; JSON parses with `Header`, `RunEntries`, `ActiveSetup`, `Errors`, `Processes:[]`; text contains `== DNS servers (`; `both` contains both. Record first-call elapsed (Authenticode, no timeout). | — |
| STARTUP_REPORT-02 | T0 | L | `{"format":"json","includeProcesses":true}` | `Processes.length > 0`; payload size vs STARTUP_REPORT-01 recorded. | — |
| STARTUP_REPORT-03 | T0 | L | `{"format":"xml"}`; `{"format":""}` | `Unknown format 'xml'; expected summary\|json\|text\|both` (and for `''`). | — |
| STARTUP_REPORT-04 | T0 | L | JSON: `Header.Elevated` and `Errors` shape; a `Services` entry for a Microsoft binary | `Elevated:false` (unelevated server); every `Errors` string shaped `section: ExceptionType: message`; the Microsoft entry `Trusted:true` (`Signer` may be null — catalog-signed). | — |
| STARTUP_REPORT-05 | T0 | L | Second call timing | Materially faster than the first (Authenticode cache). | — |
| VERIFY_SIGNATURE-01 | T0 | L | `C:\Windows\System32\notepad.exe`; `C:\Windows\System32\drivers\tcpip.sys`; `C:\Windows\System32\drivers\etc\hosts` | `{Trusted:true, Signer:"CN=Microsoft Windows, …"}`; `{Trusted:true, Signer:null}` (catalog); `{Trusted:false, Signer:null}`. | — |
| VERIFY_SIGNATURE-02 | T0 | L | `C:\no-such-file-<runid>.exe`; `""`; `C:\Windows` (a directory); `notepad.exe` (relative) | **All** `{Trusted:false, Signer:null}` with no error — file S3 (a missing file is indistinguishable from an unsigned one; the tool should raise `FileNotFoundException` naming the path, and apply the absolute-path rule). | — |
| VERIFY_SIGNATURE-03 | T0 | L | A `Services` entry from `startup_report` JSON with `Trusted:true` re-verified here | Agrees. | — |
| VERIFY_SIGNATURE-04 | T0 | L | `<scratch>\bin.dat` (unsigned scratch file) | `{Trusted:false, Signer:null}`. | — |

## 8. Hosting matrix

All rows spawn `bundle/WindowsMcp.exe` out of process (its hash must equal the deployed exe,
§4.1). Add `--flash off` to any probe that captures the screen. `--port 0` is a parse refusal,
so HTTP rows use a concrete free port: pick one with
`Get-NetTCPConnection -State Listen | ? LocalPort -eq 18765` returning nothing, and stop the
server with `Stop-Process` on the PID the run spawned. Verbatim messages come from
`Hosting/ServerOptions.cs`, `Program.cs`, `Hosting/CertificateLocator.cs`, `Hosting/EnvironmentRepair.cs`,
`Hosting/WindowsMcpHost.cs` and `ToolErrors.cs` on 0.7.3.

### 8.1 Command line and startup

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| HOST-01 | T0 | S | `WindowsMcp.exe --help`; also `-h`, `-?`, `/?` | Exit 0. **stdout** holds `Usage: WindowsMcp.exe [--transport stdio\|http] [options]`, sections `Transports`, `HTTP options`, `Capture options (both transports)`, the `Environment fallbacks` paragraph naming all ten `WINDOWSMCP_*` names, `/mcp`. stderr is empty or a single `Windows-mcp: repaired environment (…)` line. `/?` is accepted by the parser but not by the unit theory: record it. | none |
| HOST-02 | T0 | S | `--transport http --help --bogus` and `--bogus --help` | First: exit 0 with usage (help wins as soon as it is scanned). Second: exit 2, `Windows-mcp: Unknown option '--bogus'.` (a malformed argument *before* help still refuses). | none |
| HOST-03 | T0 | S | `--screenshot-scale 1.5`; then `0`, `0.09`, `abc`, `0,5`, `NaN`, `Infinity`, `1e-1` | Exit 2 each. stderr line 1 exactly `Windows-mcp: Invalid --screenshot-scale '<raw>'; expected a number from 0.1 to 1.0.`, a blank line, then the full usage. | none |
| HOST-04 | T0 | S | `--max-tree-elements 0`; then `-1`, `1.5`, `1e3`, `1,000`, `0x10`, `2147483648`, `+5`, `" 5"` | Exit 2, `Invalid --max-tree-elements '<raw>'; expected a whole number of at least 1.` | none |
| HOST-05 | T0 | S | `--screenshot-backend dxcam`; then `" wgc"` (leading space) and `WGC` | `dxcam` and `" wgc"`: exit 2, `Invalid --screenshot-backend '<raw>'; expected auto\|gdi\|wgc.` `WGC`: accepted (lower-cased on store) — server starts. | kill the started server |
| HOST-06 | T0 | S | `--flash yes`; `--flash`; `--no-flash`; `--flash ON`; `--profile-snapshot 1` | `yes`: exit 2, `Invalid --flash 'yes'; expected on\|off (also true\|false, 1\|0).` Bare `--flash`: `Option '--flash' requires a value.` `--no-flash`: `Unknown option '--no-flash'.` `ON` and `1`: accepted. | kill started servers |
| HOST-07 | T0 | S | `http` (positional); `--port` (no value); `--port=`; `--port 1 --port 2`; `--transport tcp` | `Unexpected argument 'http'.`; `Option '--port' requires a value.`; `Option '--port' requires a value.`; `Option '--port' was given more than once.`; `Unknown transport 'tcp'; expected 'stdio' or 'http'.` Exit 2 each. | none |
| HOST-08 | T0 | S | `--api-key windows-mcp-e2e-key-0123` **without** `--transport http`; then `--port 18765`, `--bind 127.0.0.1`, `--cert-thumbprint <40 hex>` each alone | Exit 2, `'--api-key' only applies with '--transport http'.` (and the same sentence for each of the other three). | none |
| HOST-09 | T0 | S | stdio start with env `WINDOWSMCP_PORT=99999` and `WINDOWSMCP_API_KEY=short` set | Server starts normally (HTTP-only *env vars* are ignored under stdio; only *flags* refuse); `--list` returns 69. | kill |
| HOST-10 | T0 | S | `--transport http --port 65536`; `0`; `-1`; `eighty` | Exit 2, `Invalid port '<raw>'; expected a number from 1 to 65535.` | none |
| HOST-11 | T0 | S | `--transport http --bind not-an-ip`; `example.com`; `localhost` | First two: exit 2, `Invalid bind address '<raw>'; expected an IPv4/IPv6 address (e.g. 0.0.0.0, 127.0.0.1, ::).` `localhost`: accepted as `127.0.0.1` (the startup line says `127.0.0.1`). | kill |
| HOST-12 | T0 | S | `--transport http --bind 127.0.0.1 --api-key tooshort`; `--api-key "has a space in it!"`; `--api-key nön-ascii-key-value-x` | `API key is too short; use at least 16 characters.`; `API key must be printable ASCII with no spaces (it travels in an HTTP header).` (both non-ASCII and the spaced one). Exit 2. | none |
| HOST-13 | T0 | S | `--transport http --bind 0.0.0.0 --port 18765` with **no** key (env `WINDOWSMCP_API_KEY` unset) | Exit 2 **before binding**: stderr contains `refusing to listen on 0.0.0.0:18765 without an API key`, `powershell, file_write, registry_set, process kill`, `Set WINDOWSMCP_API_KEY (or pass --api-key)`, `--bind 127.0.0.1`. `Get-NetTCPConnection -LocalPort 18765` shows nothing during and after. | none |
| HOST-14 | T0 | S | `--transport http --bind 127.0.0.1 --port 18765` with no key | Starts (loopback needs no key). Startup stderr line `Windows-mcp 0.7.3 listening at http://127.0.0.1:18765/mcp (auth: none, tls: off)`. No plain-HTTP warning (loopback). | kill |
| HOST-15 | T0 | S | `--transport http --bind 0.0.0.0 --port 18765 --api-key windows-mcp-e2e-key-0123` | Starts; stderr carries a stamped `warn:` entry (header `<yyyy-MM-dd HH:mm:ss.fff zzz> warn: …`) whose message is `Listening on 0.0.0.0:18765 over plain HTTP — the API key and all tool traffic cross the network unencrypted. Pass --cert-thumbprint to serve HTTPS.`, and `(auth: bearer, tls: off)`. **Kill immediately** — it is reachable from the LAN while up. | kill within seconds |
| HOST-16 | T0 | S | `--transport http --bind 127.0.0.1 --cert-thumbprint 0000000000000000000000000000000000000000` | Exit 2, `Windows-mcp: Certificate 0000…0000: not found in LocalMachine\My, CurrentUser\My. To create a self-signed one: New-SelfSignedCertificate -DnsName <host> -CertStoreLocation Cert:\CurrentUser\My` | none |
| HOST-17 | T0 | S | `--transport http --cert-thumbprint notahex`; and `"00 00 00 … 00"` / `00:00:…:00` (40 hex with separators) | `notahex`: exit 2, `Invalid certificate thumbprint 'notahex'; expected 40 hex digits (SHA-1), e.g. from Get-ChildItem Cert:\CurrentUser\My.` — from the parser, before any store is opened. Separated forms normalise and reach the store lookup (HOST-16's message). | none |
| HOST-18 | T0 | S | `--transport http --bind 127.0.0.1 --port 18765 --api-key … ` with env `ASPNETCORE_URLS=http://0.0.0.0:19999` | Only `127.0.0.1:18765` listens; `19999` is not bound (explicit `Listen()` wins). | kill |
| HOST-19 | T3 | H | HTTPS: only if the operator supplies a `CurrentUser\My` self-signed cert thumbprint (creating one is a T3 change to the store) — `--transport http --bind 127.0.0.1 --port 18443 --cert-thumbprint <tp> --api-key …` | `https://127.0.0.1:18443/mcp` completes the TLS handshake (validation relaxed); plain `http://` on 18443 fails the handshake; startup line `tls: CN=… [<tp>]`. Without a cert: BLOCKED, reason recorded. | kill; the operator removes the cert they created |

### 8.2 Stdio protocol, environment repair, error shapes

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| HOST-20 | T0 | S | `--list` | `initialize.result.serverInfo == {name:"Windows-mcp", version:"0.7.3"}`; version equals `<Version>` in `Directory.Build.props` **and** `.claude-plugin/plugin.json`; `tools/list` length **69**; every tool has a non-blank `description`, an `annotations.title` ≤ 40 chars and all four hints present; the harness prints **no** `nonJsonStdout` line. | none |
| HOST-21 | T0 | S | `--request '{"method":"initialize","params":{"protocolVersion":"2025-03-26",…}}'` and again with `2024-11-05`, `2025-06-18`, `2026-07-28`, `9999-01-01` (edit the harness's version or send raw) | Record which versions the server accepts and what `result.protocolVersion` it echoes for each; nothing in the repo pins this, so the answer is the baseline. A crash or hang on an unknown version is S1. | none |
| HOST-22 | T0 | S | Send `tools/call` **before** `initialize` (raw `--request`, harness modified to skip the handshake, or a second harness mode) | A JSON-RPC error (record its code and message), not a hang; the server still answers a subsequent proper `initialize`. | none |
| HOST-23 | T0 | S | `--stderr --env "Path=C:\nothing" --env "PATHEXT=.CPL" --call powershell '{"command":"(Get-Command where.exe).Source"}'` | stderr's first line `Windows-mcp: repaired environment (…)` names both `PATHEXT` and `Path`; the result resolves `where.exe` under `C:\Windows\System32`; `$env:Path` inside the child still **starts with** `C:\nothing` (host entries first, never reordered). | none |
| HOST-24 | T0 | S | `--stderr --env "PATHEXT=.CPL"` only, then `--call powershell '{"command":"$env:PATHEXT"}'` | `repaired environment (PATHEXT)` — `Path` not named (a Path carrying System32 is left alone); the child's `PATHEXT` contains `.EXE`. | none |
| HOST-25 | T0 | S | `--stderr --env "ProgramData=C:\host-chosen" --call powershell '{"command":"$env:ProgramData"}'` | The child sees `C:\host-chosen` (a host-set variable is never overwritten); `ProgramData` is not in the repaired list. | none |
| HOST-26 | T0 | S | Healthy environment, `--stderr --list` | No `repaired environment` line at all (nothing was missing). | none |
| HOST-27 | T0 | S | `--request '{"method":"tools/call","params":{"name":"no_such_tool","arguments":{}}}'` | A JSON-RPC error or `isError` result naming the tool — **record the exact SDK 2.2.0 shape** as the baseline; the server answers the next call. | none |
| HOST-28 | T0 | S | `--call wait '{"seconds":"abc"}'`; `--call wait '{"seconds":3600}'`; `--call wait '{}'` | `"abc"`: the SDK's argument-binding failure — record the shape (it is not routed through `ToolErrors`; if it is the masking text, file S3: a wrong-type argument should tell the caller which parameter). `3600`: `isError:true`, one text block `seconds must be more than 0 and at most 60, got 3600; for a longer or conditional wait use wait_for.` `{}`: missing required parameter — record the shape. | none |
| HOST-29 | T0 | S | `--call file_write '{"path":"<scratch>\\wm-e2e.txt","content":"x"}'` (no confirm) | `isError:true`, text exactly `'confirm: true' is required for file writes`; the file does **not** exist afterwards. | none (nothing created) |
| HOST-30 | T0 | S | `--call registry_get '{"hive":"HKCU","path":"<20 segments of 150 'k' joined by \\>"}'` | `isError:true`; text starts `Registry path not found: HKCU\` and is ≤ 2 000 chars, ending in ` [cut: 2000-character limit] …`. | none |
| HOST-31 | T0 | S | `--call switch_to_window '{"title":"definitely-no-such-window-xyz"}'` | `isError:true`; text starts `No top-level window matching 'definitely-no-such-window-xyz'. Open windows: ` — a lookup miss surfaces, not masked. | none |
| HOST-32 | T0 | S | **Masked-exception baseline.** `--call process_inspect '{"pid":4}'` is caller-visible (`ModulesError`), so pick a call that raises a COM/Win32 error benignly: `--call get_table '{"element_id":"el_1"}'` right after start (no snapshot issued) and `--call clipboard '{"action":"get"}'` while a `powershell` job holds the clipboard open (`[System.Windows.Forms.Clipboard]` in STA, background). | Record whether each surfaces a message or the SDK text `An error occurred invoking '<tool>'.` The masking text is documented in five places and asserted nowhere; any *documented* refusal that arrives masked is S2 (see the per-tool rows that flag non-caller-facing throws). | cancel the job |
| HOST-33 | T0 | S | `--call powershell '{"command":"Start-Sleep 25; \"done\""}' --timeout 60000` with a `progressToken` in `_meta` (harness `--request` with `"params":{"name":"powershell","arguments":{…},"_meta":{"progressToken":"p1"}}`) | Two `notifications/progress` lines (≈10 s and ≈20 s) with `message` `powershell running (10s)` / `(20s)` before the result; without a `progressToken`, none. | none |
| HOST-34 | T0 | S | stdout purity: `--out` the whole session of HOST-20 + HOST-33 and grep the raw stdout capture for any line that is not a JSON object | Zero non-JSON lines (the harness reports `nonJsonStdout`); every log line went to stderr. | none |
| HOST-35 | T0 | S | `--screenshot-scale 0.5 --flash off --call screenshot '{"output":"file","scale":0.5,"display":"1"}'` | Metadata `coordinateScale` reflects the effective 0.25 (or `width` ≈ 640 for a 2560-wide display after the 1920 fit — record the arithmetic: fit first, then scale × process scale); `path` under `%TEMP%\WindowsMcp`. | delete the returned `path` |
| HOST-36 | T0 | S | `--max-tree-elements 5 --flash off --call snapshot '{"format":"json"}'`; then `--call snapshot '{"format":"json","max_elements":50}'` on the same server; then `--call get_state '{}'` | First: `Truncated:true`, `ElementLimit:5`. Second: limit 50 (the per-call positive value wins). Third: `get_state` has no override, so its root reports the process budget 5. | none |
| HOST-37 | T0 | S | `--screenshot-backend wgc --flash off --call screenshot '{"output":"file","backend":"auto"}'`; then `backend:"gdi"` on the same server | `auto` resolves to the process backend: metadata `backend:"wgc"`. Explicit `gdi` wins: `backend:"gdi"`. | delete the returned paths |

### 8.3 HTTP transport

Start once for the block: `--transport http --bind 127.0.0.1 --port 18765 --api-key windows-mcp-e2e-key-0123 --flash off`, record the PID and the startup stderr line.

| ID | Tier | Ch | Call | Expected | Cleanup |
|---|---|---|---|---|---|
| HOST-40 | T0 | H | Startup stderr line | `Windows-mcp 0.7.3 listening at http://127.0.0.1:18765/mcp (auth: bearer, tls: off)` | — |
| HOST-41 | T0 | H | `POST /mcp` with no `Authorization`; `POST /`; `GET /anything-else`; `GET /mcp` | **401** each; header `WWW-Authenticate: Bearer`; body exactly `Unauthorized: send 'Authorization: Bearer <api-key>'.` (the gate covers every path). | — |
| HOST-42 | T0 | H | `Authorization: Bearer windows-mcp-e2e-key-0123x`; `bearer windows-mcp-e2e-key-0123` (lower-case scheme); `Basic …`; the bare key with no scheme; `Bearer ` alone | 401 each. | — |
| HOST-43 | T0 | H | `Authorization: Bearer   windows-mcp-e2e-key-0123  ` (padded) with an `initialize` body | 200 (the remainder is trimmed, compared in constant time). | — |
| HOST-44 | T0 | H | Correct bearer: `initialize` then `tools/list` (Streamable HTTP; `Accept: application/json, text/event-stream`) | `serverInfo` identical to HOST-20's; **69** tools; `screenshot.annotations.readOnlyHint == true`, `file_manage.annotations.destructiveHint == true`, `scrape.annotations.openWorldHint == true`. (The in-repo HTTP test only asserts ≥ 60; this row pins 69 over HTTP.) | — |
| HOST-45 | T0 | H | `tools/call system_info {"category":"os"}` twice **without** any `Mcp-Session-Id` header, then once with a made-up `Mcp-Session-Id: zzz` | All three succeed (stateless: no session is required or validated). | — |
| HOST-46 | T0 | H | `tools/call file_write` without `confirm` | `isError:true` with `'confirm: true' is required for file writes` — the same filter runs on HTTP. | — |
| HOST-47 | T0 | H | `tools/call scrape {"url":"https://example.com","summarize":true}` | `Summarized:false`, `Model:null`, `Content` present, `Note` containing `stateless`, `HTTP` and `stdio` and **not** `capability`; elapsed < 5 s (no 120 s wait). | — |
| HOST-48 | T0 | H | `tools/call powershell {"command":"Start-Sleep 12; 'ok'"}` with `_meta.progressToken` | The response's SSE stream carries a `notifications/progress` event before the result (progress rides the POST). | — |
| HOST-49 | T0 | H | HTTP/2 or `--http2` client attempt (`curl --http2-prior-knowledge`) | Refused or downgraded — the listener pins HTTP/1.1; record the behaviour. | — |
| HOST-50 | T0 | H | Stop the server (`Stop-Process` PID); `Get-NetTCPConnection -LocalPort 18765` | Nothing listening; no child processes left (`process list name:powershell` shows none with that parent). | done |

## 9. Execution workflow

The run is a sequence of phases. **The results document is updated at the end of every
feature block, not at the end of the run**, so an interrupted run still leaves an accurate
document.

| Phase | What | Output into the results document |
|---|---|---|
| 0 — Prepare | Generate the run id; create the scratch root and evidence folder; verify the server (§4.1); run `multi_monitor` and compare with §4.2; ask the operator which T3 items are opted in (record the answer verbatim; an AI operator records "none"); capture the ledger (§3.5); create the scratch fixture set (Appendix B). | Run header filled in; ledger "before" column; T3 opt-in list. |
| 1 — Gate | Channel X: headless suite, then the attended UIAutomation category. | Gate row: totals, failures by test name; any failure becomes a defect with severity S2 or higher. |
| 2 — Hosting | §8 on channels S and H. | HOST rows. |
| 3 — Cross-cutting T0 | XC-01, XC-03 (starts the T0 ledger window), XC-05, XC-10, XC-11 explicit case. | XC rows. |
| 4 — Feature blocks, T0 rows | Every T0 row of §7, area by area (UI query → input/window reads → files → process/shell reads → registry reads → screen/system → network/web → security). Liveness probe after each error-expected row. Then the T0 ledger diff (XC-03). | Per-test rows; feature coverage table updated per block. |
| 5 — Feature blocks, T1 rows | Scratch mutations, jobs, watches, run-owned processes; XC-02, XC-07, XC-08, XC-12. | Same. |
| 6 — Feature blocks, T2 rows (attended) | Launch fixture apps; input, window, clipboard, audio, toast, focus; XC-09. Restore after each block. | Same; each row's cleanup verification recorded. |
| 7 — T3 (only opted-in items) | Backup → cycle → restore → verify. | Same; non-opted items recorded BLOCKED with the reason "operator opt-in not given". |
| 8 — Close | Close run-owned windows/processes; stop watches; cancel jobs; delete the scratch registry key; delete the screenshot files the run wrote; delete the scratch root (keeping `evidence\`); capture the ledger "after" column and diff. | Ledger diff (must be empty); sign-off block. |

**Status vocabulary.** `NOT RUN` (default), `PASS`, `FAIL` (links a defect), `BLOCKED`
(precondition or opt-in missing; says which), `SKIPPED` (not applicable on this machine, e.g.
Firefox absent; says why). A row whose cleanup verification failed is `FAIL` even if the
assertion passed.

**Defect filing.** A FAIL creates a `DEF-NNN` entry (§10) at the moment it is observed, with
the exact call, the observed and the expected outcome, and — after a look at the source — the
`file:line` where the behaviour comes from and a proposed fix that names the family of inputs,
not just the reported one. Severity:

| Severity | Meaning |
|---|---|
| S1 | The server crashed, hung, or left persistent state behind; or a T4 guard failed to refuse. |
| S2 | A tool returns a wrong result or a masked error on a documented path; a refusal fires after a side effect. |
| S3 | A boundary or cap is off by one, a message misnames its parameter, a field is missing from the result. |
| S4 | Cosmetic: wording, an unhelpful message, a doc/description mismatch. |

**Fix routing.** A defect is fixed through the repo's own workflow (CLAUDE.md "Test-first
workflow"): the defect's *Repro* becomes a RED row for `test-agent`, the fix follows, then
`review-agent` on the diff. The defect entry records the PR that closed it and the E2E row is
re-run against the rebuilt, redeployed exe (§4.1 again) before it is marked `PASS (retest)`.

**Retest and closure.** A defect closes only when its E2E row passes on a build whose hash is
recorded in the defect entry. A re-run of the whole plan starts a new run header; earlier runs
stay in the document's history section.

## 10. Results document specification

[e2e-test-results.md](e2e-test-results.md) has exactly these sections, in this order, and the
runner keeps them current. `scripts/e2e/sync-results.mjs` (§10.1) adds a `NOT RUN` log row
for any test id in this plan that the results document lacks, and never edits an existing row.

1. **Run header** — run id, date, operator, server version, commit, exe path, SHA-256 of the
   deployed and built exe, server PID at start and end, monitor table as found, client
   sampling capability (from `SCRAPE-08`), T3 opt-ins, xUnit gate totals.
2. **Feature coverage** — one row per tool (69) plus `HOST` and `XC`: planned, run, pass,
   fail, blocked, skipped, and a one-line note.
3. **Test log** — one row per test id: `ID | Tier | Ch | Status | Evidence | Notes`. Evidence is
   the excerpt or the evidence-folder path.
4. **Defects** — `DEF-NNN` entries with: Tool, Test ids, Severity, Title, Repro (exact call),
   Observed, Expected, Root cause (`file:line`), Proposed fix (the family, not the case),
   Status (`OPEN` / `FIXED IN <PR>` / `VERIFIED <build hash>` / `WON'T FIX <reason>`).
5. **Ledger diff** — the §3.5 table with before/after and a verdict.
6. **Sign-off** — who, when, and the one-paragraph verdict.
7. **Run history** — previous run headers and their totals, newest first.

### 10.1 Keeping the log in step with the plan

```bash
node scripts/e2e/sync-results.mjs            # adds missing NOT RUN rows, reports counts
node scripts/e2e/sync-results.mjs --check    # exit 1 if the results log lacks any plan id (CI-friendly)
```

The script parses every table row in this plan whose first cell matches `^[A-Z][A-Z0-9_]*-\d{2}$`.

## Appendix A — Safety classification of every tool

Annotations are the server's own (`RO` ReadOnly, `D` Destructive, `I` Idempotent, `OW`
OpenWorld, from `tools/list` on 0.7.3). The tier column is this plan's decision per action;
where a tool's annotation and its tier disagree, the tier wins and the disagreement is a
candidate defect only if the annotation is *less* cautious than the behaviour (XC-03).

| Tool | Annotations | Tier per action | Notes for a test-only run |
|---|---|---|---|
| archive | D, I | zip/unzip inside scratch: **T1** | Never point `dst` outside scratch. |
| assert_element | RO, I | **T0** | |
| audio | I | get: **T0**; set/mute/unmute: **T2** | `get` cannot be the "before" reading (it reports `Muted:false` always and falls back to 50): capture the true endpoint volume and mute out of band with a read-only `powershell` CoreAudio snippet, keep `owned:Notepad` in front (the tool sends media keys to the foreground window), restore with `set` and an even number of toggles, verify out of band. |
| cert_store | RO, I | **T0** | |
| click | — | **T2** | Only on run-owned windows or empty desktop area on display 0; `clicks:0` (hover) anywhere. |
| clipboard | I | get: **T0**; set: **T2** | Restore the previous text. |
| defender_status | RO, I | **T0** | |
| disk_inspect | RO, I | usage/file_types/stale on scratch: **T0**; reclaimable: **T0** (report only — confirmed by the catalog; if it ever *deletes*, that is an S1 defect against its ReadOnly annotation) | |
| drag | — | **T2** | Paint canvas of a run-owned window, or a hover-only drag on empty desktop. |
| driver_list | RO, I | **T0** | |
| env | D, I | get/list: **T0**; set/delete scope Process: **T1** (server process only, dies with it); set/delete scope User: **T3**; scope Machine: **T4** (refusal path only, unelevated → access denied is the expected answer) | |
| event_log | RO, I | **T0** | Security log unelevated → expect an access error, record it. |
| file_dialog | — | **T2** | Only into a dialog the run opened (Notepad Ctrl+O); then Esc. |
| file_hash / file_info / file_streams | RO, I | **T0** | Targets in scratch or read-only system files. |
| file_read / file_search | RO, I | **T0** | |
| file_manage | D | list: **T0**; copy/move/delete: **T1** (scratch only) | `delete` of `%TEMP%\WindowsMcp\<file the run wrote>` is the single allowed exception (§3.3 item 4). |
| file_write | D | **T1** (scratch only) | |
| find_element / get_element / get_state / get_table / get_text | RO, I | **T0** | |
| firewall | D | list: **T0**; add/remove: **T3** (E2E-named rule; a human operator only); refusals (`confirm:false`, missing fields): **T0** | An AI operator records add/remove BLOCKED. |
| focus / switch_to_window | I | **T2** | Restore the previous foreground window (`window active` before). |
| fs_changes | RO, I | **T0** | Requires elevation: the unelevated answer must be a caller-visible access error, not a mask. |
| hover | I | **T2** (cursor only) | Restore the cursor position captured in the ledger at the end of the block. |
| http_request | D, OW | GET/HEAD to public hosts: **T0**; POST/PUT/PATCH/DELETE to `httpbin.org` echo only: **T0** | Nothing else. |
| integrity | I | list/check: **T0**; baseline on channel S with `--env LOCALAPPDATA=<scratch>\lad`: **T1**; baseline on channel L: **T3** (it overwrites the user's own baseline under `%LOCALAPPDATA%\windows-mcp\integrity` with no backup) | The plan runs baseline only on S. |
| interact_element | — | **T2** | Run-owned windows only. |
| job | D, I | status/output/list: **T0**; cancel: **T1** (run-started jobs only) | |
| key / shortcut / type | — | **T2** | Run-owned windows only. `shortcut win` opens Start — follow with `key esc`. Never `alt+f4` on a window the run did not open. |
| launch | OW | **T1/T2** | Every launched process recorded (pid, startTime, hwnd) and closed in phase 8. |
| multi_edit / multi_select | — | **T2** | Run-owned windows only. |
| multi_monitor | RO, I | **T0** | |
| network | RO, I, OW | adapters/ports/wifi: **T0**; ping/dns: **T0** (network egress) | |
| notification | — | **T2** (the toast is transient) — **T3** if the AUMID registration is absent before the run (ledger) | The registration is by design; it is recorded, not filed. |
| ocr | RO, I | **T0** | |
| power_action | D | every valid action with `confirm:true`: **T4**; `confirm:false` and invalid action: **T0** | §3.3 item 1. |
| powershell | D, OW | read-only commands: **T0**; scratch-confined commands and `Start-Sleep`: **T1**; background jobs: **T1** | §3.3 item 6. |
| process | D | list/orphans: **T0**; kill: **T1** (run-started PID + `startTime`), refusals: **T0** | Never by name. |
| process_inspect | RO, I | **T0** | Protected PID (e.g. 4) → `ModulesError`, not a mask. |
| registry_get | RO, I | **T0** | |
| registry_set / registry_delete | D, I | under `HKCU\Software\WindowsMcpE2E\<run-id>`: **T1**; protected-root and HKLM refusals: **T0**; any other write: **T4** | |
| reliability | RO, I | **T0** | |
| scheduled_task | D | list/get: **T0**; create/run/delete of `WindowsMcpE2E-<run-id>`: **T3**; run of any pre-existing task: **T4**; refusals: **T0** | |
| scrape | RO, I, OW | http to public URLs: **T0**; dom on a run-owned Edge window: **T0**; summarize: **T0** | |
| screenshot | RO, I | **T0** | `output:"file"` writes to `%TEMP%\WindowsMcp`; the run deletes exactly the paths it was given. |
| scroll | — | **T2** | |
| security_audit | RO, I | **T0** | |
| service | D | list/status: **T0**; start/stop/restart with `confirm:true` on a real name: **T4** unless opted in (**T3**, one benign service, e.g. `Spooler` only if the operator names it); refusals and a non-existent name: **T0** | |
| start_process | OW | **T1** (`cmd /c exit 0`, `notepad`, `powershell -c` scratch-confined; every PID recorded and closed) | |
| startup_report | RO, I | **T0** | |
| storage_health | RO, I | **T0** (`include_usage:true` wakes sleeping drives — allowed, not a state change) | |
| system_info | RO, I | **T0** | |
| verify_signature | RO, I | **T0** | |
| wait / wait_for | RO, I | **T0** | |
| watch | — | start/stop of run-owned sessions: **T1**; poll/list: **T0** | Every session stopped in phase 8. |
| window | D | list/active/desktops: **T0**; minimize/maximize/restore/move/resize/set_bounds on run-owned windows: **T2**; close on run-owned windows: **T1**; any of those on a window the run did not open: **T4** except a geometry restore the run itself caused | |
| wmi_query | RO, I | **T0** | Read-only WQL only; a class name that is not a class must be a caller-visible error. |

## Appendix B — Scratch fixture set

Created in phase 0 under the scratch root by `file_write`, `powershell` (scratch-confined) and
`archive`; every path below is relative to the scratch root.

| Path | Content / attribute | Used by |
|---|---|---|
| `small.txt` | 3 lines: `line one`, `line two`, `line three` (UTF-8, LF) | file_read windows, file_hash, file_info, file_manage |
| `empty.txt` | 0 bytes | file_read, file_hash, file_info |
| `crlf.txt` | 5 lines with CRLF, last line without newline | file_read line windows |
| `big.txt` | 2 000 lines of 100 chars (≈ 200 KB) | file_read `max_bytes` refusal (with `max_bytes:1024`), line paging |
| `over1mb.bin` | 1 048 577 random bytes | file_read default `max_bytes` refusal, file_hash |
| `utf16.txt` | `hello utf-16` with BOM | file_read `encoding` |
| `ünïcødé 日本語 🎉.txt` | `unicode ok` | XC-07 |
| `hidden.txt` | hidden attribute | file_manage list `include_hidden` |
| `readonly.txt` | read-only attribute | file_write / file_manage delete against read-only |
| `locked.txt` | held open exclusively by a `powershell background:true` job for the block | file_read / file_manage move on a locked file |
| `deep\a\b\c\leaf.txt` | for recursive list, copy, delete | file_manage, file_search |
| `dup1.txt`, `dup2.txt` | identical content | file_search `find_duplicates` |
| `link-junction` → `deep` | junction (`mklink /J`, unelevated) | file_manage list `IsLink`, copy not descending, delete of a junction removing the link only, file_streams reparse target |
| `ads.txt` | `Zone.Identifier` stream written by `Set-Content -Stream` | file_streams |
| `pack.zip` | `archive zip` of `deep` | archive unzip, file_info |
| `long\` + 240-char name | a path > 260 chars total | file_* long-path handling (PathTooLong or success — record which) |
| `page-deep.html` | `<body>` with 400 nested `<div>`s | XC-11 deep-nesting scrape via a local HTTP server started by `start_process` (`python -m http.server` is not assumed; use `powershell` `HttpListener` on 127.0.0.1 — note `scrape` rejects private IPs, so this case may be **BLOCKED** on channel L and is then run on S against a public deep-nesting page, or recorded as covered by `DomPageTests`/`WebServiceTests`). |

## Appendix C — Candidate defects known before the first run

Reading the 0.7.3 source while writing this plan surfaced the behaviours below. None is a
defect until the named test observes it on the deployed exe; when it does, the runner files it
with the severity suggested here and the proposed fix family, then the fix goes through the
repo's RED → implement → GREEN → REVIEW workflow. Rows are grouped by family so one fix closes
several.

| Tool | Finding (by inspection) | Confirming test | Suggested severity | Proposed fix family |
|---|---|---|---|---|
| interact_element | `invoke`/`toggle`/`select` on an element without the pattern throws `NotSupportedException`, which `ToolErrors.IsCallerFacing` does not admit; the client sees the SDK masking text instead of "X not supported on <controlType>" promised by D-2 and the description. | INTERACT_ELEMENT-05 | S2 | **Fixed by D-10** (`CHANGELOG.md [Unreleased]`): `InvalidOperationException`. Was: add `NotSupportedException` to the caller-facing set, or throw `InvalidOperationException` from `UIAutomationService.NotSupported()`. Sibling: DRAG-05 (middle button). |
| drag | `button:"middle"` is forwarded on purpose and refused by the service with `NotSupportedException` → masked. | DRAG-05 | S3 | **Fixed by D-10**: an `ArgumentException` naming `button`. Was: same family as above, or refuse at the tool with `ArgumentException`. |
| get_text | The ValuePattern read is unguarded; a destroyed element yields a masked COM error while `assert_element` answers `element no longer available`. | GET_TEXT-03, INTERACT_ELEMENT-08 | S2 | **Fixed by D-10** (`UiRefusals`, `UIAutomationService.Refusing`). Was: route element reads through `IsElementGone` and throw `InvalidOperationException` naming the id. |
| archive | `unzip` of a non-zip raises `InvalidDataException` → masked. | ARCHIVE-04 | S2 | Catch in `UnzipAsync` and rethrow `InvalidOperationException` naming the file; or admit `InvalidDataException`. |
| archive | No `confirm` and no `overwrite` gate: `zip` replaces an existing archive and `unzip` overwrites files silently — the only Destructive file tool without either. | ARCHIVE-02 | S3 | Add `overwrite:false` default with the same refusal text as `file_manage`. |
| launch | `ShellExecute` failure (`Win32Exception`) and packaged activation failure (`COMException`) are masked. | LAUNCH-08 | S2 | **Fixed by D-10** (`ProcessStartErrors`; names the target given, not a shortcut's missing target). Was: catch in `Win32AppActivator`, rethrow `InvalidOperationException` naming the target and the OS message. |
| start_process | A missing executable raises `Win32Exception` → masked; the most common real failure gives the caller nothing. | START_PROCESS-08 | S2 | **Fixed by D-10** (`FileNotFoundException` naming the file tried). Was: catch in `StartDetachedAsync`, rethrow `InvalidOperationException` with the path and OS message. |
| clipboard | Clipboard contention raises an unwrapped Win32-family exception → masked, while `type` degrades gracefully on the same failure. | CLIPBOARD-07 | S3 | Catch in `ClipboardService`, rethrow `InvalidOperationException` suggesting a retry. |
| wmi_query | Every failure (bad class, bad WHERE, bad namespace, access denied) is a `ManagementException`/`COMException` → masked; there is not one deliberate refusal in `WmiService`. | WMI_QUERY-04 | S2 | Catch in `WmiService.QueryAsync`, rethrow `InvalidOperationException` with the query and the WMI message. Sibling: `driver_list` (DRIVER_LIST-01 on a broken repository), `system_info`. |
| network | `dns` on an unresolvable host raises `SocketException` → masked. | NETWORK-03 | S2 | Catch in `NetworkService`, rethrow with the host. |
| http_request | Connection failure and the 100 s timeout raise `HttpRequestException`/`TaskCanceledException` → masked, while `scrape` wraps both. | HTTP_REQUEST-04 | S2 | Mirror `scrape`'s wrapping in `WebService.RequestAsync`. |
| http_request, scrape | The private-address check runs on the URL as given; a public host redirecting to a private address is fetched (documented pre-existing hole, C-5). | HTTP_REQUEST-07 | S2 | Re-check every redirect hop, or disable auto-redirect and surface `Location`. |
| scheduled_task | `create` parses `trigger` with `DateTime.Parse`; the description's own examples `'daily'`, `'onlogon'` raise `FormatException` → masked, and nothing is created. | SCHEDULED_TASK-04 | S2 | **Fixed by D-10** the refusal way: a non-date trigger is an `ArgumentException` naming `trigger`, and the description no longer offers named triggers (named triggers themselves are not built). Was: accept named triggers (`daily`, `onlogon`, `atstartup`) and datetimes; refuse anything else with an `ArgumentException` naming the accepted forms; fix the description. |
| scheduled_task | `run` of a missing task throws `KeyNotFoundException(name)` — the message is the bare name. | SCHEDULED_TASK-02 | S4 | Use the same `Scheduled task '{name}' not found` text as `get`. |
| service | `WaitForStatus` throws `System.ServiceProcess.TimeoutException`, not `System.TimeoutException` → a 15 s state timeout would be masked. `start` has no confirm gate. No protected-service denylist. | SERVICE-03, SERVICE-08, SERVICE-09 | S3 | Catch the ServiceProcess type and rethrow `System.TimeoutException`; gate `start` like `stop`; consider a denylist for core services. |
| process | No self-pid / ancestor / system-process kill guard; the OS ACL is the only backstop and it surfaces masked. | PROCESS-11 | S3 | Refuse the server's own pid and its ancestors, and `System`/`csrss`/`wininit`/`services`/`lsass` by name, before any kill. |
| process_inspect | A pid that does not exist returns an all-null DTO with `isError:false`. | PROCESS_INSPECT-03 | S3 | Throw `ArgumentException` (`Process with an Id of N is not running`) when both WMI and `GetProcessById` find nothing. |
| registry_set | `kind` is silently defaulted to `String` for unknown or mis-cased values; `Binary` and `MultiString` can never succeed from a string `data`. | REGISTRY_SET-04, REGISTRY_SET-05 | S3 | Parse `kind` case-insensitively and refuse unknown values naming the six; decode hex for `Binary` and split lines for `MultiString`. |
| env | `value:""` deletes the variable but the reply says `set`. | ENV-04 | S3 | Say `deleted` when the value is empty, or refuse an empty value. |
| event_log | An unrecognised `level` returns `[]` silently; `max` unvalidated; `since` compared UTC-vs-local; the scan is uncapped regardless of `max`. | EVENT_LOG-03, EVENT_LOG-06, EVENT_LOG-07 | S3 | Refuse unknown `level` naming the accepted values; validate `max ≥ 1`; compare in one time zone; read newest-first with a bounded scan. |
| file_streams | A missing path returns an empty result — a typo and a clean file are indistinguishable. | FILE_STREAMS-02 | S3 | Throw `FileNotFoundException` naming the path. |
| file_info | Behaviour on a missing path is unpinned (possibly a DTO of `-1` attributes and 1601 timestamps). | FILE_INFO-02 | S3 if the DTO | Throw `FileNotFoundException` naming the path. |
| file_read, file_write | An unknown `encoding` is silently treated as UTF-8. | FILE_READ-08, FILE_WRITE-06 | S4 | Refuse unknown encodings naming the four. |
| file_search | The one uncapped enumerator in the file family: a search of `C:\` returns every file in one response. | FILE_SEARCH-06 | S3 | Add `max_results` with `Truncated`, like `file_manage list`. |
| watch, disk_inspect | Both accept a relative `path` (resolved against the server's working directory); the C-1 absolute-path rule is not applied. | WATCH-07, DISK_INSPECT-05 | S3 | Call `RequireAbsolute` in both tools. |
| watch | `poll`/`stop` with an unknown session id answer like an idle session. | WATCH-04 | S3 | Return `found:false` (as `job` does) or refuse naming the id. |
| integrity | A locked or unreadable watched file is reported `removed`; a corrupt `baseline.json` becomes "no baseline" silently. | INTEGRITY-07 | S3 | Distinguish `unreadable` from `removed`; surface a corrupt store as an error. |
| verify_signature | A missing file, an empty path, a directory and a relative path all return `{Trusted:false, Signer:null}` — indistinguishable from an unsigned binary. | VERIFY_SIGNATURE-02 | S3 | Throw `FileNotFoundException` naming the path; apply `RequireAbsolute`. |
| cert_store | `store_name` is not validated; an unknown store raises `CryptographicException` → masked. | CERT_STORE-03 | S3 | Validate against the `StoreName` names or catch and rethrow. |
| audio | `get` reports `Muted:false` unconditionally and falls back to `50` when the cmdlet is absent; `mute`/`unmute` both send the same toggle; `level` is clamped rather than refused and the reply echoes the unclamped value; `set` drives the volume through zero with ~50 media-key presses. | AUDIO-01, AUDIO-04, AUDIO-05 | S3 | Use the CoreAudio endpoint API (`IAudioEndpointVolume`) for get/set/mute; refuse `level` outside 0-100. |
| file_dialog | Validates nothing and always reports success; a wrong target types a path into whatever has focus. | FILE_DIALOG-03, FILE_DIALOG-04 | S3 | Refuse when the foreground window is not a file dialog (class `#32770` with a file-name Edit) and when `path` is blank. |
| hover | `duration_ms` is uncapped and untokened (a ten-minute blocking call is accepted) while `wait` caps at 60 s. | HOVER-05 | S3 | Cap at 60 000 ms and pass the cancellation token. |
| get_table, get_text, driver_list, cert_store, registry_get, wmi_query, http_request | No output cap while `snapshot`, `scrape` and `file_manage list` have one. | GET_TABLE-05, GET_TEXT-05, DRIVER_LIST-01, REGISTRY_GET-07, WMI_QUERY-07, HTTP_REQUEST-06 | S3 | A `max`/`max_chars` with a `Truncated` signal on each. |
| screenshot | With `annotate:true`, a budget-cut walk appends the note to the element text block but the metadata JSON has no truncation field. | SCREENSHOT-14 | S3 | Add `truncated`/`elementLimit` to the metadata. |
| ocr | No image-size guard against `OcrEngine.MaxImageDimension` on `display:"all"`. | OCR-05 | S3 | Check the engine's maximum and refuse or tile. |
| power_action | All nine native failure messages are `Win32Exception` → masked. | POWER_ACTION-04 | S3 (by inspection) | **Fixed by D-10** for the OS refusals; the token plumbing stays masked on purpose. Was: rethrow as `InvalidOperationException` with the message. |
| wait_for | `text_exists`/`focused_element` polls are snapshots and evict every snapshot id on each poll; the description does not say so. | WAIT_FOR-09 | S4 | **Done**: the `wait_for` and `snapshot` descriptions say it, and an expired id's error names the hidden snapshot takers (D-11). Was: say it in the description, or walk without minting/evicting ids. |
| hosting | The SDK masking text and the accepted `protocolVersion` set are documented but asserted nowhere; the HTTP transport test pins only ≥ 60 tools. | HOST-21, HOST-32, HOST-44 | S4 | Pin them in `StreamTransportTests`/`HttpTransportTests`. |
| docs | `A-5-browser-dom-mode.md` still says `wait_for(use_dom)` is "not wired"; it is wired and tested. | — | S4 | Update the design note. |

## Appendix D — Test id index

Rewritten between the markers by `node scripts/e2e/sync-results.mjs`; not maintained by hand.

<!-- INDEX-START -->

552 test ids, 71 features (generated; do not edit by hand).

| Feature | Tests | Ids |
|---|---|---|
| ARCHIVE | 7 | ARCHIVE-01, ARCHIVE-02, ARCHIVE-03, ARCHIVE-04, ARCHIVE-05, ARCHIVE-06, ARCHIVE-07 |
| ASSERT_ELEMENT | 8 | ASSERT_ELEMENT-01, ASSERT_ELEMENT-02, ASSERT_ELEMENT-03, ASSERT_ELEMENT-04, ASSERT_ELEMENT-05, ASSERT_ELEMENT-06, ASSERT_ELEMENT-07, ASSERT_ELEMENT-08 |
| AUDIO | 6 | AUDIO-01, AUDIO-02, AUDIO-03, AUDIO-04, AUDIO-05, AUDIO-06 |
| CERT_STORE | 3 | CERT_STORE-01, CERT_STORE-02, CERT_STORE-03 |
| CLICK | 7 | CLICK-01, CLICK-02, CLICK-03, CLICK-04, CLICK-05, CLICK-06, CLICK-07 |
| CLIPBOARD | 8 | CLIPBOARD-01, CLIPBOARD-02, CLIPBOARD-03, CLIPBOARD-04, CLIPBOARD-05, CLIPBOARD-06, CLIPBOARD-07, CLIPBOARD-08 |
| DEFENDER_STATUS | 1 | DEFENDER_STATUS-01 |
| DISK_INSPECT | 6 | DISK_INSPECT-01, DISK_INSPECT-02, DISK_INSPECT-03, DISK_INSPECT-04, DISK_INSPECT-05, DISK_INSPECT-06 |
| DRAG | 8 | DRAG-01, DRAG-02, DRAG-03, DRAG-04, DRAG-05, DRAG-06, DRAG-07, DRAG-08 |
| DRIVER_LIST | 2 | DRIVER_LIST-01, DRIVER_LIST-02 |
| ENV | 10 | ENV-01, ENV-02, ENV-03, ENV-04, ENV-05, ENV-06, ENV-07, ENV-08, ENV-09, ENV-10 |
| EVENT_LOG | 10 | EVENT_LOG-01, EVENT_LOG-02, EVENT_LOG-03, EVENT_LOG-04, EVENT_LOG-05, EVENT_LOG-06, EVENT_LOG-07, EVENT_LOG-08, EVENT_LOG-09, EVENT_LOG-10 |
| FILE_DIALOG | 6 | FILE_DIALOG-01, FILE_DIALOG-02, FILE_DIALOG-03, FILE_DIALOG-04, FILE_DIALOG-05, FILE_DIALOG-06 |
| FILE_HASH | 2 | FILE_HASH-01, FILE_HASH-02 |
| FILE_INFO | 3 | FILE_INFO-01, FILE_INFO-02, FILE_INFO-03 |
| FILE_MANAGE | 18 | FILE_MANAGE-01, FILE_MANAGE-02, FILE_MANAGE-03, FILE_MANAGE-04, FILE_MANAGE-05, FILE_MANAGE-06, FILE_MANAGE-07, FILE_MANAGE-08, FILE_MANAGE-09, FILE_MANAGE-10, FILE_MANAGE-11, FILE_MANAGE-12, FILE_MANAGE-13, FILE_MANAGE-14, FILE_MANAGE-15, FILE_MANAGE-16, FILE_MANAGE-17, FILE_MANAGE-18 |
| FILE_READ | 11 | FILE_READ-01, FILE_READ-02, FILE_READ-03, FILE_READ-04, FILE_READ-05, FILE_READ-06, FILE_READ-07, FILE_READ-08, FILE_READ-09, FILE_READ-10, FILE_READ-11 |
| FILE_SEARCH | 6 | FILE_SEARCH-01, FILE_SEARCH-02, FILE_SEARCH-03, FILE_SEARCH-04, FILE_SEARCH-05, FILE_SEARCH-06 |
| FILE_STREAMS | 2 | FILE_STREAMS-01, FILE_STREAMS-02 |
| FILE_WRITE | 8 | FILE_WRITE-01, FILE_WRITE-02, FILE_WRITE-03, FILE_WRITE-04, FILE_WRITE-05, FILE_WRITE-06, FILE_WRITE-07, FILE_WRITE-08 |
| FIND_ELEMENT | 8 | FIND_ELEMENT-01, FIND_ELEMENT-02, FIND_ELEMENT-03, FIND_ELEMENT-04, FIND_ELEMENT-05, FIND_ELEMENT-06, FIND_ELEMENT-07, FIND_ELEMENT-08 |
| FIREWALL | 8 | FIREWALL-01, FIREWALL-02, FIREWALL-03, FIREWALL-04, FIREWALL-05, FIREWALL-06, FIREWALL-07, FIREWALL-08 |
| FOCUS | 5 | FOCUS-01, FOCUS-02, FOCUS-03, FOCUS-04, FOCUS-05 |
| FS_CHANGES | 3 | FS_CHANGES-01, FS_CHANGES-02, FS_CHANGES-03 |
| GET_ELEMENT | 4 | GET_ELEMENT-01, GET_ELEMENT-02, GET_ELEMENT-03, GET_ELEMENT-04 |
| GET_STATE | 4 | GET_STATE-01, GET_STATE-02, GET_STATE-03, GET_STATE-04 |
| GET_TABLE | 6 | GET_TABLE-01, GET_TABLE-02, GET_TABLE-03, GET_TABLE-04, GET_TABLE-05, GET_TABLE-06 |
| GET_TEXT | 5 | GET_TEXT-01, GET_TEXT-02, GET_TEXT-03, GET_TEXT-04, GET_TEXT-05 |
| HOST | 48 | HOST-01, HOST-02, HOST-03, HOST-04, HOST-05, HOST-06, HOST-07, HOST-08, HOST-09, HOST-10, HOST-11, HOST-12, HOST-13, HOST-14, HOST-15, HOST-16, HOST-17, HOST-18, HOST-19, HOST-20, HOST-21, HOST-22, HOST-23, HOST-24, HOST-25, HOST-26, HOST-27, HOST-28, HOST-29, HOST-30, HOST-31, HOST-32, HOST-33, HOST-34, HOST-35, HOST-36, HOST-37, HOST-40, HOST-41, HOST-42, HOST-43, HOST-44, HOST-45, HOST-46, HOST-47, HOST-48, HOST-49, HOST-50 |
| HOVER | 7 | HOVER-01, HOVER-02, HOVER-03, HOVER-04, HOVER-05, HOVER-06, HOVER-07 |
| HTTP_REQUEST | 8 | HTTP_REQUEST-01, HTTP_REQUEST-02, HTTP_REQUEST-03, HTTP_REQUEST-04, HTTP_REQUEST-05, HTTP_REQUEST-06, HTTP_REQUEST-07, HTTP_REQUEST-08 |
| INTEGRITY | 9 | INTEGRITY-01, INTEGRITY-02, INTEGRITY-03, INTEGRITY-04, INTEGRITY-05, INTEGRITY-06, INTEGRITY-07, INTEGRITY-08, INTEGRITY-09 |
| INTERACT_ELEMENT | 12 | INTERACT_ELEMENT-01, INTERACT_ELEMENT-02, INTERACT_ELEMENT-03, INTERACT_ELEMENT-04, INTERACT_ELEMENT-05, INTERACT_ELEMENT-06, INTERACT_ELEMENT-07, INTERACT_ELEMENT-08, INTERACT_ELEMENT-09, INTERACT_ELEMENT-10, INTERACT_ELEMENT-11, INTERACT_ELEMENT-12 |
| JOB | 10 | JOB-01, JOB-02, JOB-03, JOB-04, JOB-05, JOB-06, JOB-07, JOB-08, JOB-09, JOB-10 |
| KEY | 7 | KEY-01, KEY-02, KEY-03, KEY-04, KEY-05, KEY-06, KEY-07 |
| LAUNCH | 9 | LAUNCH-01, LAUNCH-02, LAUNCH-03, LAUNCH-04, LAUNCH-05, LAUNCH-06, LAUNCH-07, LAUNCH-08, LAUNCH-09 |
| MULTI_EDIT | 7 | MULTI_EDIT-01, MULTI_EDIT-02, MULTI_EDIT-03, MULTI_EDIT-04, MULTI_EDIT-05, MULTI_EDIT-06, MULTI_EDIT-07 |
| MULTI_MONITOR | 7 | MULTI_MONITOR-01, MULTI_MONITOR-02, MULTI_MONITOR-03, MULTI_MONITOR-04, MULTI_MONITOR-05, MULTI_MONITOR-06, MULTI_MONITOR-07 |
| MULTI_SELECT | 7 | MULTI_SELECT-01, MULTI_SELECT-02, MULTI_SELECT-03, MULTI_SELECT-04, MULTI_SELECT-05, MULTI_SELECT-06, MULTI_SELECT-07 |
| NETWORK | 5 | NETWORK-01, NETWORK-02, NETWORK-03, NETWORK-04, NETWORK-05 |
| NOTIFICATION | 6 | NOTIFICATION-01, NOTIFICATION-02, NOTIFICATION-03, NOTIFICATION-04, NOTIFICATION-05, NOTIFICATION-06 |
| OCR | 7 | OCR-01, OCR-02, OCR-03, OCR-04, OCR-05, OCR-06, OCR-07 |
| POWER_ACTION | 6 | POWER_ACTION-01, POWER_ACTION-02, POWER_ACTION-03, POWER_ACTION-04, POWER_ACTION-05, POWER_ACTION-06 |
| POWERSHELL | 10 | POWERSHELL-01, POWERSHELL-02, POWERSHELL-03, POWERSHELL-04, POWERSHELL-05, POWERSHELL-06, POWERSHELL-07, POWERSHELL-08, POWERSHELL-09, POWERSHELL-10 |
| PROCESS | 12 | PROCESS-01, PROCESS-02, PROCESS-03, PROCESS-04, PROCESS-05, PROCESS-06, PROCESS-07, PROCESS-08, PROCESS-09, PROCESS-10, PROCESS-11, PROCESS-12 |
| PROCESS_INSPECT | 5 | PROCESS_INSPECT-01, PROCESS_INSPECT-02, PROCESS_INSPECT-03, PROCESS_INSPECT-04, PROCESS_INSPECT-05 |
| REGISTRY_DELETE | 11 | REGISTRY_DELETE-01, REGISTRY_DELETE-02, REGISTRY_DELETE-03, REGISTRY_DELETE-04, REGISTRY_DELETE-05, REGISTRY_DELETE-06, REGISTRY_DELETE-07, REGISTRY_DELETE-08, REGISTRY_DELETE-09, REGISTRY_DELETE-10, REGISTRY_DELETE-11 |
| REGISTRY_GET | 8 | REGISTRY_GET-01, REGISTRY_GET-02, REGISTRY_GET-03, REGISTRY_GET-04, REGISTRY_GET-05, REGISTRY_GET-06, REGISTRY_GET-07, REGISTRY_GET-08 |
| REGISTRY_SET | 9 | REGISTRY_SET-01, REGISTRY_SET-02, REGISTRY_SET-03, REGISTRY_SET-04, REGISTRY_SET-05, REGISTRY_SET-06, REGISTRY_SET-07, REGISTRY_SET-08, REGISTRY_SET-09 |
| RELIABILITY | 3 | RELIABILITY-01, RELIABILITY-02, RELIABILITY-03 |
| SCHEDULED_TASK | 11 | SCHEDULED_TASK-01, SCHEDULED_TASK-02, SCHEDULED_TASK-03, SCHEDULED_TASK-04, SCHEDULED_TASK-05, SCHEDULED_TASK-06, SCHEDULED_TASK-07, SCHEDULED_TASK-08, SCHEDULED_TASK-09, SCHEDULED_TASK-10, SCHEDULED_TASK-11 |
| SCRAPE | 12 | SCRAPE-01, SCRAPE-02, SCRAPE-03, SCRAPE-04, SCRAPE-05, SCRAPE-06, SCRAPE-07, SCRAPE-08, SCRAPE-09, SCRAPE-10, SCRAPE-11, SCRAPE-12 |
| SCREENSHOT | 19 | SCREENSHOT-01, SCREENSHOT-02, SCREENSHOT-03, SCREENSHOT-04, SCREENSHOT-05, SCREENSHOT-06, SCREENSHOT-07, SCREENSHOT-08, SCREENSHOT-09, SCREENSHOT-10, SCREENSHOT-11, SCREENSHOT-12, SCREENSHOT-13, SCREENSHOT-14, SCREENSHOT-15, SCREENSHOT-16, SCREENSHOT-17, SCREENSHOT-18, SCREENSHOT-19 |
| SCROLL | 7 | SCROLL-01, SCROLL-02, SCROLL-03, SCROLL-04, SCROLL-05, SCROLL-06, SCROLL-07 |
| SECURITY_AUDIT | 2 | SECURITY_AUDIT-01, SECURITY_AUDIT-02 |
| SERVICE | 9 | SERVICE-01, SERVICE-02, SERVICE-03, SERVICE-04, SERVICE-05, SERVICE-06, SERVICE-07, SERVICE-08, SERVICE-09 |
| SHORTCUT | 8 | SHORTCUT-01, SHORTCUT-02, SHORTCUT-03, SHORTCUT-04, SHORTCUT-05, SHORTCUT-06, SHORTCUT-07, SHORTCUT-08 |
| SNAPSHOT | 8 | SNAPSHOT-01, SNAPSHOT-02, SNAPSHOT-03, SNAPSHOT-04, SNAPSHOT-05, SNAPSHOT-06, SNAPSHOT-07, SNAPSHOT-08 |
| START_PROCESS | 9 | START_PROCESS-01, START_PROCESS-02, START_PROCESS-03, START_PROCESS-04, START_PROCESS-05, START_PROCESS-06, START_PROCESS-07, START_PROCESS-08, START_PROCESS-09 |
| STARTUP_REPORT | 5 | STARTUP_REPORT-01, STARTUP_REPORT-02, STARTUP_REPORT-03, STARTUP_REPORT-04, STARTUP_REPORT-05 |
| STORAGE_HEALTH | 4 | STORAGE_HEALTH-01, STORAGE_HEALTH-02, STORAGE_HEALTH-03, STORAGE_HEALTH-04 |
| SWITCH_TO_WINDOW | 5 | SWITCH_TO_WINDOW-01, SWITCH_TO_WINDOW-02, SWITCH_TO_WINDOW-03, SWITCH_TO_WINDOW-04, SWITCH_TO_WINDOW-05 |
| SYSTEM_INFO | 2 | SYSTEM_INFO-01, SYSTEM_INFO-02 |
| TYPE | 8 | TYPE-01, TYPE-02, TYPE-03, TYPE-04, TYPE-05, TYPE-06, TYPE-07, TYPE-08 |
| VERIFY_SIGNATURE | 4 | VERIFY_SIGNATURE-01, VERIFY_SIGNATURE-02, VERIFY_SIGNATURE-03, VERIFY_SIGNATURE-04 |
| WAIT | 6 | WAIT-01, WAIT-02, WAIT-03, WAIT-04, WAIT-05, WAIT-06 |
| WAIT_FOR | 9 | WAIT_FOR-01, WAIT_FOR-02, WAIT_FOR-03, WAIT_FOR-04, WAIT_FOR-05, WAIT_FOR-06, WAIT_FOR-07, WAIT_FOR-08, WAIT_FOR-09 |
| WATCH | 9 | WATCH-01, WATCH-02, WATCH-03, WATCH-04, WATCH-05, WATCH-06, WATCH-07, WATCH-08, WATCH-09 |
| WINDOW | 10 | WINDOW-01, WINDOW-02, WINDOW-03, WINDOW-04, WINDOW-05, WINDOW-06, WINDOW-07, WINDOW-08, WINDOW-09, WINDOW-10 |
| WMI_QUERY | 9 | WMI_QUERY-01, WMI_QUERY-02, WMI_QUERY-03, WMI_QUERY-04, WMI_QUERY-05, WMI_QUERY-06, WMI_QUERY-07, WMI_QUERY-08, WMI_QUERY-09 |
| XC | 12 | XC-01, XC-02, XC-03, XC-04, XC-05, XC-06, XC-07, XC-08, XC-09, XC-10, XC-11, XC-12 |

<!-- INDEX-END -->
