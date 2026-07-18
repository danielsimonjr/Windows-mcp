---
name: windows
description: "Playbook for driving Windows via the windows-mcp server's 63 tools — UI automation, system inspection, files, registry, services, processes, disk, network, security, and startup analysis. Use when the user says 'automate this Windows app', 'click/type into that window', 'take a screenshot' or 'OCR the screen', 'audit my startup items', 'why is my PC booting slowly', 'clean up orphaned processes', 'what's running', 'check Defender/firewall status', 'run a security audit', 'read/set a registry value', 'inspect a service or scheduled task', 'find/hash/inspect a file', 'check disk or storage health', 'baseline/check file integrity', 'what changed on my C: drive', 'watch a folder for changes', or any Windows desktop-automation or system-inspection task. Steers toward the windows-mcp tools over ad-hoc PowerShell, gives composed multi-tool workflows, and flags destructive tools. Does NOT add tools; it is guidance over the windows-mcp server. Not cross-platform; the server runs unelevated so admin-only operations may need elevation the skill cannot grant."
---

# Windows

A judgment layer over the `windows-mcp` server's 63 atomic tools for Windows desktop automation and system inspection — UI driving, screenshots/OCR, files, registry, services, processes, disk, network, and security/startup analysis. This skill adds no tools of its own: every action below is one of the server's existing MCP tools, composed into the right order with the right safety checks. Its job is to steer tool selection (MCP vs. raw PowerShell), sequence multi-step workflows correctly, and flag which tools are destructive enough to need confirmation first.

**Skill root**: this skill ships inside the `windows-mcp` plugin (repo
`danielsimonjr/windows-mcp`, `skills/windows/`). Slash trigger: `/windows`.

## When to use this skill

Trigger this skill when the user wants any of:

- **Drive a GUI application** — click, type, read UI state, wait for elements to appear
- **Capture or read the screen** — screenshot, OCR a region, extract text/tables from a UI element
- **Diagnose slow boot / startup bloat** — "why is my PC slow to start", "audit autoruns"
- **Clean up processes** — "what's eating memory", "kill orphaned processes" (whitelist-only, never kill-all)
- **Check security posture** — Defender status, firewall rules, certificate trust, a full security audit
- **Inspect or modify system state** — registry values, services, scheduled tasks, environment variables
- **File forensics** — locate, hash, inspect metadata/streams, or verify the signature of a file
- **Disk/network diagnostics** — disk usage vs. drive health, adapters/ports/DNS/ping

Do NOT use this skill for:
- Cross-platform automation (this server and its tools are Windows-only)
- Anything requiring elevation the user hasn't granted — the server itself runs unelevated (see Section 5)

## Tool selection: windows-mcp tools vs. raw PowerShell

**Default to the MCP tool.** It is faster than a PowerShell cold-start, returns structured JSON instead of text to parse, and runs unelevated in one consistent place. Reach for raw PowerShell only when none of the 63 tools express what's needed.

**Fall back to the `powershell` tool** only for one-off scripting the 63 tools don't cover. Gotcha: the `powershell` tool's stdin can arrive empty — pass the script via a temp `.ps1` file and invoke that, rather than piping a heredoc or inline multi-line string.

The MCP server **runs unelevated**. Admin-only operations — `registry_set` under `HKLM`, `service` start/stop, some `scheduled_task` actions — can return access-denied. Recognize that signature and surface it to the user instead of retrying blindly; the skill cannot grant elevation it doesn't have.

| The task | Reach for |
|---|---|
| Inspect OS/memory/disk/GPU/battery | `system_info`, `wmi_query` |
| List/inspect/kill a process | `process`, `process_inspect` |
| Read/search/hash a file | `file_read`, `file_search`, `file_hash`, `file_info` |
| Read a registry value | `registry_get` |
| Drive a GUI app | `get_state` → `click`/`type` → `assert_element` |
| One-off scripting no tool covers | `powershell` (temp `.ps1`) |

If a `windows-mcp` tool isn't loaded, fetch its schema via `ToolSearch select:mcp__plugin_windows-mcp_Windows-mcp__<tool>`.

## The 63 tools, grouped by domain

**UI automation / input (24)** — drive and read a foreground GUI application: `click`, `drag`, `hover`, `key`, `type`, `scroll`, `focus`, `get_state`, `get_element`, `get_text`, `get_table`, `find_element`, `assert_element`, `interact_element`, `wait_for`, `switch_to_window`, `window`, `multi_monitor`, `screenshot`, `ocr`, `clipboard`, `file_dialog`, `notification`, `launch`

**Processes / shell (4)** — enumerate, inspect, start, or kill processes; run arbitrary scripts: `process`, `process_inspect`, `start_process`, `powershell`

**System (7)** — machine-level inspection and power control: `system_info`, `wmi_query`, `env`, `reliability`, `event_log`, `driver_list`, `power_action`

**Files (7)** — read, write, search, and forensically inspect files: `file_read`, `file_write`, `file_manage`, `file_search`, `file_info`, `file_hash`, `file_streams`

**Disk / storage (2)** — usage vs. drive health: `disk_inspect`, `storage_health`

**Services / tasks (2)** — Windows services and scheduled tasks: `service`, `scheduled_task`

**Registry (2)** — read and write registry values: `registry_get`, `registry_set`

**Network / web (4)** — connectivity and HTTP: `network`, `firewall`, `http_request`, `scrape`

**Security (5)** — trust and posture checks: `security_audit`, `defender_status`, `cert_store`, `verify_signature`, `startup_report`

**Monitoring / integrity (3)** — file-integrity tripwire, NTFS USN change journal, and live directory watching: `integrity`, `fs_changes`, `watch`

**Misc (3)** — utility operations: `shortcut`, `archive`, `audio`

(24+4+7+7+2+2+2+4+5+3+3 = 63.) If a `windows-mcp` tool isn't loaded, fetch its schema via `ToolSearch select:mcp__plugin_windows-mcp_Windows-mcp__<tool>`.

## Workflow playbooks

### 1. Startup / boot-slowness triage

```
startup_report
  → for each suspicious entry:
      verify_signature + cert_store    (is it signed / trusted?)
      reliability + event_log          (recent crashes/hangs, boot/service errors)
```

`startup_report` gives a HiJackThis-style read-only inventory of autoruns, startup folders, services, and shell extensions, each already carrying a catalog-aware trust flag — start there rather than re-deriving it. For anything unsigned, untrusted, or unfamiliar, cross-check `verify_signature`/`cert_store` directly, then corroborate with `reliability` (crash minidumps and failure records) and `event_log` (boot/service errors). Decision: unsigned or untrusted autoruns that line up with recent reliability drops are the prime suspects.

### 2. Process cleanup (whitelist — never kill-all)

```
process (action: orphans)                     — recycle-aware orphans + signals, one call
  → (or) process (action: list, groupByRoot: true)   — see which root spawned a pile
  → process (action: kill, pid, confirm: true[, tree: true][, startTime])
```

Prefer `action: orphans` (or `list` with `includeLineage: true`) over the old `list → process_inspect` dance — it returns parent lineage, command line, `ageMinutes`, `runtimeKind`, `orphaned`, and `isSystemAdjacent` for every process in a single call, so you can rank candidates without inspecting each one. `groupByRoot: true` collapses processes under their root ancestor — the fast way to see, e.g., five stale sessions each holding a server fleet. **Read the signals, don't trust the label:** `orphaned` is COMMON and by-design on Windows (`explorer.exe` and anything launched from a since-closed shell are orphaned) — it is NOT a leak signal; `isSystemAdjacent: true` flags the boot/session processes to leave alone. **Hard rail:** never terminate `csrss`, `wininit`, `winlogon`, `services`, `lsass`, `explorer`, or any user-facing application. To reap a stale session and its whole fleet in one guarded call, use `kill` with `tree: true` (kills the pid + its descendants, each re-validated before killing) and pass `startTime` to guard against PID reuse. Always confirm the kill list with the user; `confirm: true` is required.

### 3. Security audit sweep

```
security_audit + defender_status + firewall (action: list) + startup_report
```

Run all four and compose the results into one health summary with a per-area verdict (firewall, Defender/AV, UAC/BitLocker where available, and autorun/startup hygiene). Note that `security_audit`'s admin-gated fields (BitLocker, some firewall profiles) return null when run unelevated — report those as "unknown," not "failed."

### 4. UI-automation loop

```
get_state                      (read the element tree)
  → click / type / key         (act)
  → assert_element / wait_for  (confirm the state changed)
  → repeat
```

Read the tree before acting, act, then confirm the action landed before moving on — don't chain blind actions. **The target app must be foregrounded on an interactive desktop; these tools fail headless or when the app is in the background.** Prefer `find_element`/`get_element` to locate targets by name/role over hardcoded coordinates, which break when the window moves or resizes.

### 5. File forensics

```
file_search (locate)
  → file_info (size/timestamps/attributes)
  → file_hash (SHA-256)
  → file_streams (alternate data streams)
  → verify_signature (Authenticode/catalog trust)
```

Locate the file(s) first, then layer on metadata, a hash suitable for IOC/VirusTotal lookups, a check for hidden alternate-data-stream payloads, and a signature/trust verdict. Useful both for vetting a suspicious binary found elsewhere (a process path, an autorun entry) and for general integrity checks.

## Safety rails & gotchas

- **Confirm before destructive tools:** `registry_set`, `service`, `scheduled_task`, `power_action`, `file_write`, `file_manage`, `firewall`. Each of these is gated behind a `confirm: true` parameter on the write/destructive path — treat that gate as a place to pause and get user sign-off, not just a required field to fill in. Run the read-only counterpart first: `registry_get` before `registry_set`; `process_inspect` before killing; `service` (status/list) before start/stop.
- **`storage_health` can wedge on external / USB drives.** Its default mode is fast and never wakes sleeping drives, but `include_usage: true` wakes sleeping/USB drives to collect SMART data — scope to internal disks by default, or warn the user before running it with `include_usage: true` against removable media.
- **Runs unelevated.** Admin-only operations (`registry_set` under `HKLM`, `service` start/stop, some `scheduled_task` actions) fail with access-denied. Surface that signature to the user; don't loop retrying.
- **UIAutomation tools need the target app foregrounded** on an interactive desktop — they fail headless or against a backgrounded window.
- **`powershell` stdin can arrive empty** — write the script to a temp `.ps1` and invoke that file rather than piping a heredoc (see Section "Tool selection" above).
- **Long jobs are fine; disk-saturation storms are not.** A single long `powershell`/heavy tool call (>~120s — e.g. `DISM`, a big hash, a bulk delete) is safe: the Claude Code harness "moves it to the background" (benign) and the result is delivered on completion, and the server allows a 10-min PowerShell backstop. What *does* break it is running several heavy ops (`DISM` + a `service` stop + bulk deletes) **while the disk is already saturated** by a concurrent large hash/copy — the MCP call can fail transiently with `"An error occurred invoking 'powershell'"`. That is **I/O starvation, not a timeout or a 120s limit** (verified 2026-07-17: lone 150s and two concurrent ~135s calls all succeeded; the only failures came during a 42 GB SHA-256 storm, with no server crash). Mitigation: for the heaviest/longest ops, prefer Claude Code's own `run_in_background` over the `powershell` tool, and don't stack heavy windows-mcp ops during a saturation storm. Raising `MCP_TOOL_TIMEOUT` is **not** the fix — its default is ~28 h, not 120s.
