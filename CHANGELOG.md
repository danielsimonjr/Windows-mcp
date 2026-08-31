## [Unreleased]

### Added

- Added an MCP 2.0 conformance smoke suite in
  `tests/WindowsMcp.Tests/Protocol/Mcp20ProtocolTests.cs` that exercises the real stdio
  server through the official C# client SDK, covering handshake, `ping`, `tools/list`,
  `tools/call`, caller-facing tool errors, and JSON-RPC method-not-found handling.
- Documented the repo's supported MCP surface as a **tools-only stdio server** pinned to
  the SDK's 2026-07-28 protocol revision tests.

## [0.7.3] - 2026-08-23

> **Numbered 0.7.3, not 0.8.0.** This release was first cut as 0.8.0 and the tag was refused:
> `v0.8.0` **already exists in this repo and belongs to UPSTREAM** (jeomon's Windows-MCP,
> tagged 2026-05-19, "Add --stateless-http support for streamable-http transport"), which is not
> an ancestor of this history. This fork inherited upstream's tag namespace, and its own tags stop
> at `v0.4.1` while `plugin.json` had already drifted to 0.7.x - so 0.6.9 through 0.8.5 are all
> upstream's. Nothing was force-moved. 0.7.3 is free and adjacent to this plugin's actual lineage.
> **The next release must check `git tag -l` before choosing a number**; upstream is at v0.8.5, so
> 0.8.x and 0.9.x are contested ground.

### Changed

- **Upgrade `ModelContextProtocol` 1.0.0 -> 2.2.0, adopting the MCP 2026-07-28 specification.**
  The 2026-07-28 revision makes MCP stateless: it removes protocol-level sessions and the
  `Mcp-Session-Id` header, drops the `initialize`/`notifications/initialized` handshake in favour
  of per-request `_meta` (`io.modelcontextprotocol/protocolVersion`,
  `io.modelcontextprotocol/clientCapabilities`), adds a mandatory `server/discover` RPC, and
  introduces Multi Round-Trip Requests. The C# SDK 2.0 line implements that revision, so the
  protocol work comes from the SDK rather than from this repo.
  - **No source changes were required.** The build is clean at 0 warnings / 0 errors across a
    major version bump, because this server only consumes `AddMcpServer`,
    `WithRequestFilters`/`AddCallToolFilter` and `WithStdioServerTransport`, none of which changed
    shape. The deprecated features (Roots, Sampling, Logging) were never used here.
  - **The upgrade is verified as HAVING TAKEN EFFECT, not merely as building.** A successful build
    proves nothing about which package resolved: `project.assets.json` reports
    `ModelContextProtocol/2.2.0` and `ModelContextProtocol.Core/2.2.0`, and the emitted
    `ModelContextProtocol.dll` reports `FileVersion 2.2.0.0`,
    `ProductVersion 2.2.0+6fa3825973949a9c4f0cd8af344e15a8db09dc35`.
  - **No regression.** Tests before: 240 passed / 3 failed / 243 total. After: 240 passed /
    3 failed / 243 total, and the three failures are the SAME tests by name -
    `InputServiceTests.ClickAsync...`, `InputServiceTests.TypeAsync...`,
    `ScreenshotServiceTests.CaptureAsync...` - i.e. the documented environmental set that needs an
    interactive desktop (UIPI blocks simulated input; `CopyFromScreen` gets an invalid handle).
    The baseline was measured on this machine BEFORE the bump rather than assumed from the
    CHANGELOG, so "unchanged" is a comparison and not a claim.
  - **STDIO transport is unchanged and remains the default.** Statelessness is a property of the
    Streamable HTTP transport; a stdio server is a local child process and has no session to
    remove. Exposing this server over HTTP is a separate change with a security dimension - it
    would put a network listener in front of tools that run PowerShell and terminate processes -
    and is deliberately NOT part of this commit.

### Fixed

- **`powershell` reported SUCCESSFUL commands as failures.** `Success` required `ExitCode == 0`
  **and** an empty stderr, but with stderr redirected PowerShell serialises *progress* records to
  it as CLIXML ("Preparing modules for first use"). A real `dotnet build` printing
  "Build succeeded. 0 Warning(s) 0 Error(s)" and exiting 0 came back `Success:false`. A wrapper
  that calls a green build failed teaches its caller to ignore the verdict, which is worse than
  having no verdict.
  - **The first fix was WRONG and the tests caught it.** Keying `Success` on the exit code alone
    made a genuinely failed command report success: this service invokes via `-EncodedCommand`,
    where a non-terminating failure (unknown cmdlet) still **exits 0** and announces itself only
    on stderr. The exit code is not sufficient here.
  - **Root cause, one level down:** a single CLIXML `<Objs>` document carries **both** progress and
    error records on one line, so filtering `<Objs>` lines wholesale discards the real diagnostics.
    `ParseErrors` now EXTRACTS `<S S="Error">` payloads, decodes the `_x000D_`/`_x000A_`
    placeholders and HTML entities, and drops progress-only documents. Errors are now readable
    text instead of a wall of XML - strictly better than before the bug existed.

- **`defender_status` returned an all-null security posture on any machine that had never
  completed a full scan.** Found by the EVO-X2 agent, not here. `Get-MpComputerStatus` is projected
  through calculated properties, and a scriptblock that emits NOTHING serialises as an empty
  OBJECT `{}` - not `null`. `{}` cannot bind to `DateTime?`, so deserialisation of the WHOLE
  `DefenderStatusDto` failed and every field came back null.
  - Reported as `"could not be converted ... Path: $.FullScanEndTime"`, while `security_audit`
    independently confirmed `DefenderRunning=true`. **A tool that renders a parse failure as an
    all-null security posture is more dangerous than one that errors outright** - the output is
    indistinguishable from "Defender is off".
  - Fixed on **all three** affected properties (`AntivirusSignatureLastUpdated`,
    `QuickScanEndTime`, `FullScanEndTime`), not only the one that was hit; any of them being
    absent had the same total effect.

### Verified

- **The shipped `bundle/WindowsMcp.exe` was EXECUTED, not assumed** (0.7.1 shipped a stale binary;
  that is the failure mode this check exists for). A real stdio session against the committed
  artifact reports `serverInfo {name: Windows-mcp, version: 0.8.0}` and enumerates **63 tools**.
- **The 2026-07-28 protocol is genuinely live in that binary**, evidenced by behaviour rather than
  by the package reference: `server/discover` (mandatory only in the new revision) is recognised
  and correctly *refuses* a call lacking per-request metadata -
  "requires per-request metadata declaring a supported protocol version" - then answers once
  `io.modelcontextprotocol/protocolVersion` is supplied. `tools/list` returns the new required
  `resultType: "complete"` plus the new `CacheableResult` fields (`ttlMs`, `cacheScope`).
- **Tests: 243 passed / 247 total.** The 4 failures are environmental and documented: three need an
  interactive desktop (UIPI blocks simulated input; `CopyFromScreen` gets an invalid handle), and
  `UIAutomationServiceTests.FindElementAsync_finds_notepad_text_area` failed only in the full run
  because a leftover Notepad contended with the fixture's foregrounding - **proven green when run
  in isolation (3/3)**, so it is flake, not regression.

### Not done, deliberately

- **No HTTP transport.** Statelessness is a property of Streamable HTTP; a stdio server is a local
  child process with no session to remove, so the upgrade alone does not make this server
  remotely reachable. Putting a network listener in front of tools that run PowerShell and
  terminate processes is a security decision for the operator, not a side effect of a dependency
  bump.


## [0.7.2] - 2026-08-16

### Fixed

- **0.7.1 shipped a stale binary.** `Directory.Build.props` and `.claude-plugin/plugin.json`
  both declared `0.7.1`, but the committed `bundle/WindowsMcp.exe` was built **2026-07-26** and
  reported itself as `0.7.0`. The version plumbing added in 0.6.1 is correct — `ServerVersion`
  derives from `<Version>`, and `ServerInfoTests` pins it to the manifest — but **none of that
  runs against the committed artifact**, so the release bumped the declarations and left the
  exe behind.
  - Found by handshaking every deployed MCP server and comparing what each reported against
    what was installed. `serverInfo.version` is the field used to prove a deploy landed, so
    while the binary under-reported, a stale deploy and a healthy one were indistinguishable.
  - Rebuilt and released as **0.7.2** rather than replacing the binary in place: the plugin
    cache is keyed on version, so a same-version swap is a no-op and would never have deployed.
  - Verified by **executing** the artifact that ships: real MCP handshake reports
    `Windows-mcp 0.7.2` and enumerates all 63 tools.

### Known (environmental, pre-existing)

- **Three tests fail without an interactive desktop session** — two `InputServiceTests` (UIPI
  blocks simulated input), `ScreenshotServiceTests.CaptureAsync…` (invalid screen handle), and
  intermittently `UIAutomationServiceTests.FindElementAsync…` (UIAutomation COM). **Proven
  pre-existing**: the same failures occur on the unmodified tree at `9f283a3`. They are *not*
  skipped — a desktop-automation server failing loudly where there is no desktop is the correct
  and informative result, the same call `ui-mcp` makes for its window tests. 240 of 243 pass.

## [0.7.1] - 2026-07-26

### Fixed
- **`PowerShellService` mangled every multi-line script — silently, on exit 0.** The service ran
  `powershell.exe -Command -` and wrote the script to **stdin**. PowerShell evaluates piped stdin
  **line by line as independent statements**, so any multi-line construct (hashtable literal,
  `try/catch`, `foreach`, `function`, wrapped assignment) was broken apart — producing **empty
  stdout with exit code 0**. Now passed as a single unit via **`-EncodedCommand`** (base64
  UTF-16LE).
  - **Reported symptom:** `disk_inspect mode:reclaimable` returned
    `"reclaimable-space query returned no output (exit 0)"`. Its script ends in a multi-line
    `[PSCustomObject]@{...} | ConvertTo-Json`. The service's empty-output guard was working
    correctly and faithfully reporting a real failure — the defect was one layer below it.
  - **Blast radius was every PowerShell-backed tool**, not just `disk_inspect`. Any caller whose
    script contained a multi-line block was affected.
  - **Root-caused by controlled comparison**, not inspection: the identical script produced 0 bytes
    via `-Command -`/stdin and 136 bytes of valid JSON via `-File`.
- **Non-ASCII output was corrupted** (`café` -> `caf?`). Two independent causes, both fixed:
  stdin was written using the console default encoding (gone — the script no longer travels via
  stdin), and Windows PowerShell 5.1 **writes** stdout in the OEM codepage while the service
  **reads** it as UTF-8. A one-line `[Console]::OutputEncoding` preamble now aligns writer with
  reader. Verified at the byte level: `caf 82 20 fb` (OEM) -> `caf c3 a9 20 e2 9c 93` (UTF-8).
- **Large scripts no longer regress.** stdin had no length limit but a command line is capped at
  ~32767 chars, so an oversized script falls back to a temp `.ps1` run with `-File` (written
  UTF-8 **with BOM**, since PS 5.1 assumes ANSI for a BOM-less file).

### Changed
- `RedirectStandardInput` is kept (and closed immediately) even though stdin is no longer written.
  This process is an MCP **stdio** server, so its own stdin is the JSON-RPC channel; an
  un-redirected child would inherit that handle and could consume protocol bytes.

### Added
- 7 regression tests. 5 pin the invocation itself (multi-line hashtable / `try-catch` / `foreach`,
  oversized-script fallback, non-ASCII round-trip); 2 are **integration** tests driving
  `GetReclaimableAsync` through a **real** `PowerShellService`.
  **Why the bug shipped:** the existing `DiskServiceTests` mock `IPowerShellService` and feed it a
  hand-written JSON string, so they only ever exercised the parsing half and stayed green while the
  real invocation returned nothing. *Mocking the collaborator that is broken hides the bug.*
  Suite: 237 -> 239 (excluding UIAutomation).
- Verified in the **shipped single-file exe** over MCP stdio before release, not just in `dotnet
  test`: `disk_inspect mode:reclaimable` returned real data (3.58 GB reclaimable).

## [0.7.0] - 2026-07-18

### Added
- **Monitoring / integrity domain — 3 new tools (60 -> 63), for the maintain-and-protect mandate:**
  - **`integrity`** (baseline/check/list): a file-integrity **tripwire**. SHA-256 snapshots a curated
    watch-list (hosts file, user+machine Startup folders, `~/.claude/settings.json`, `~/.gitconfig`,
    the `C:\` governance files) to `%LOCALAPPDATA%\windows-mcp\integrity` (outside the plugin cache,
    survives upgrades); `check` diffs current vs baseline into added/removed/modified.
  - **`fs_changes`** (status/since): NTFS **USN change-journal** reader — whole-volume file-change
    tracking via native `DeviceIoControl` (`FSCTL_QUERY/READ_USN_JOURNAL`), raw byte-buffer parsing
    (no fragile struct marshalling). `status` gives the journal id + FirstUsn/NextUsn range; `since`
    reads change records forward from a USN. Requires elevation. Native path live-verified against C:.
  - **`watch`** (start/poll/stop/list): live **FileSystemWatcher** sessions; created/changed/deleted/
    renamed events buffer server-side in a bounded ring (oldest dropped when full) between polls.
- 21 new unit tests (integrity temp-dir diff, USN buffer parser + reason flags, bounded ring buffer,
  watch lifecycle). Full suite: 232 passing. Docs/skill updated to 63 tools / 18 tool classes.

## [0.6.2] - 2026-07-17

### Changed
- **`windows` skill: added a "disk-saturation storm" gotcha** to the *Safety rails & gotchas*
  section. Documents that long `powershell`/heavy tool calls (>~120s) are safe on their own — the
  Claude Code harness detaches them at 120s (benign) and delivers the result on completion, and the
  server already allows a 10-min PowerShell backstop — but stacking heavy ops (`DISM` + `service`
  stop + bulk deletes) **during an already-saturated disk** (e.g. a concurrent large hash/copy) can
  fail the MCP call transiently with `"An error occurred invoking 'powershell'"`. Clarifies this is
  I/O starvation, **not** a 120s limit or a `MCP_TOOL_TIMEOUT` issue (that env var defaults to ~28 h),
  and that the mitigation is to run the heaviest ops via Claude Code's own `run_in_background`.
  Verified 2026-07-17 by controlled probes (lone 150s and two concurrent ~135s calls all succeeded;
  no server crash). Docs-only; no code or tool-surface change.

## [0.6.1] - 2026-07-12

### Fixed
- **`process list` silently ignored the `name` filter on two of its three paths** — found by live
  e2e testing against the 0.6.0 server. `ProcessTools.Process` forwarded `name` only on the
  `includeLineage` path; the plain-`list` and `groupByRoot` paths called `ListAsync(ct)` /
  `GroupByRootAsync(ct)`, which had **no filter parameter at all** on `IProcessService`. A filter
  matching nothing therefore returned the **entire process table** (~360 rows) instead of an empty
  result — silent, and the opposite of the safe failure direction: a caller narrowing to
  `name: "chrome"` to pick a PID to kill was handed the whole machine. Root-caused at the
  interface (the tool had nowhere to pass the filter), not patched at the call site:
  - `IProcessService.ListAsync` and `GroupByRootAsync` now take `string? nameFilter = null`.
  - Plain `list` matches a case-insensitive substring of the **name only** (a `ProcessDto` carries
    no command line); `orphans` / `includeLineage` / `groupByRoot` match name **or** command line.
    The tool description previously over-promised command-line matching on every path; corrected.
  - `groupByRoot` + filter returns the **whole trees that contain a match** — full membership and
    a true `DescendantCount`. It deliberately does not trim the tree: a trimmed count still reads
    as "descendants" and would mislead.
  - The name-based `kill` path stays on **exact** matching (it passes no filter), so
    `kill --name node` cannot also kill `node-inspector`.
  - The bug shipped because `Process_list_groupByRoot_calls_GroupByRootAsync` asserted only that
    the method *was called*, never that the argument arrived. Tests now assert on the forwarded
    argument. +9 tests (205 pass, 0 fail).

- **The server misreported its own version over MCP** — the handshake returned
  `serverInfo.version = "0.4.1"`, a hardcoded literal in `Program.cs` that had been stale for
  **three releases** (0.5.0, 0.6.0 and 0.6.1 all shipped announcing 0.4.1). Surfaced while
  e2e-testing the rebuilt bundle. Not cosmetic: this plugin is served from a per-version cache
  clone of the committed `bundle/`, so a stale bundle is otherwise invisible and `serverInfo` is
  the natural thing to check — a server that lies about its version is what let v0.6.0 sit
  undeployed for four days while 0.5.0 kept answering. Root cause was three disagreeing sources of
  truth (the literal, an unset `<Version>` leaving the assembly at 1.0.0, and `plugin.json`). Now
  `<Version>` in `Directory.Build.props` is the single build-side source, `Program.ServerVersion`
  reads it off the assembly (no literal to rot), and `ServerInfoTests` pins it to
  `.claude-plugin/plugin.json` so a bump that misses one of them fails the test gate.

- **Every caller-facing error message in the server was being thrown away** — found by e2e-testing
  the orphan/kill features. The MCP SDK masks any exception that isn't an `McpException`, returning
  a bare `"An error occurred invoking '<tool>'."`. Sensible for unexpected faults; actively harmful
  for our **deliberate refusals**, whose messages are the whole point. The worst case is the
  PID-reuse start-time guard: it aborts a kill with
  `"pid N start time … != expected …; aborting (possible PID reuse)"` — and that was flattened to
  the generic string, making a guard abort **indistinguishable from a crash**. A caller could
  reasonably "retry" the kill without the guard, causing precisely the kill the guard exists to
  prevent. This affected all 54 intentional throws across 11 tool classes.
  Fixed at the boundary with a single `AddCallToolFilter` middleware (`Program.cs` + `ToolErrors`)
  that surfaces caller-facing refusals (`ArgumentException` / `InvalidOperationException`) verbatim
  with `isError: true`, while unexpected faults keep the SDK's masking (no internals leak). Services
  stay MCP-agnostic and no call sites changed. Verified live over stdio — the guard, the missing
  `confirm`, bad `startTime`, bad param combos, unknown actions, and dead PIDs all now report why.

### Changed
- `ProcessService.ListAsync` filters by name **before** projecting to DTOs — `MainModule` access
  opens a native handle and throws on protected processes, so skipping non-matches is cheaper and
  quieter. Extracted the duplicated name-or-command-line predicate into `ProcessLineage.Matches`.

## [Unreleased]

### Security

- **`PowerShellService.ValidateCommand`** — block high-risk patterns (Invoke-Expression/IEX,
  Start-Process, disk wipe, nested `-EncodedCommand`, download cradles) before spawning
  `powershell.exe`. Architecture docs previously claimed this existed; it now does.
- **`powershell` tool** — requires `confirm:true` (same friction model as other destructive tools).
- **`start_process` tool** — requires `confirm:true`.
- **`scheduled_task` run/create** — require `confirm:true` (delete already did).
- **`WebService` SSRF hardening** — manual redirect following re-validates each hop; DNS failures
  fail closed; connect-time private-IP blocking; expanded reserved ranges (100.64/10, 198.18/15);
  30s timeout and 10 MB response cap.
- **`WmiService` query validation** — class/namespace/WHERE syntax checks block injection.
- **`RegistryService.SetAsync`** — unknown `kind` is rejected instead of silently coerced to String.
- **`RegistryPolicy`** — writes to IFEO, Winlogon, AppInit_DLLs, Services, and Policies keys are refused.
- **`PathPolicy`** — device/`\\?\` paths refused; `file_search` capped at 10 000 hits; missing search root rejected.
- **`launch`, `file_manage` copy/move, `archive`, `service` start** — require `confirm:true`.
- **`env include_secrets`** — requires `confirm:true` so secret values cannot leak without an explicit gate.
- **`CertStore`** — store name allowlisted (`Root`/`CA`/`My`/…).
- **Watch sessions** — hard cap of 16 concurrent `FileSystemWatcher`s.

### Fixed

- **`ToolErrors`** — surfaces `KeyNotFoundException`, `DirectoryNotFoundException`,
  `NotSupportedException`, and `Win32Exception` to callers (not masked by the MCP SDK).
- **`watch` poll** — unknown session id throws `KeyNotFoundException` instead of returning an
  empty array indistinguishable from "no events yet".
- **`event_log` max** — clamped to 1–1000 at tool and service layers.
- **`UIAutomationService` element cache** — bounded LRU (10k entries) replaces unbounded growth.
- **Input validation** — `process_inspect` rejects non-positive PIDs; `audio set` rejects level
  outside 0–100; `screenshot`/`ocr` region parsing uses `TryParse` with bounds checks;
  `http_request` validates HTTP method and maps bad `headers_json` to `ArgumentException`.
- **`AudioService`** — Core Audio `IAudioEndpointVolume` replaces SendKeys; mute is a real setter
  and `GetAsync` reports the actual muted state.
- **`NetworkService.GetWifiAsync`** — parses `netsh wlan show interfaces` (no more placeholder).
- **`scheduled_task` create** — `daily` / `onlogon` / `onboot` / `onidle` triggers work; COM-handler
  tasks resolve CLSID → InprocServer32 DLL.
- **`startup_report` summary** — severity tiers: HIGH missing-target / persistence hooks,
  MEDIUM untrusted-third-party, LOW ms-file-missing.
- **`UsnService.NormalizeVolume`** — rejects non-letter volume arguments.
- **Architecture docs** — OVERVIEW / COMPONENTS / ARCHITECTURE / DATAFLOW reconciled to 18 tool
  classes, 35 services, and current PowerShell / Screenshot / Network / Web / Window behavior.

### Tests
- Tool-layer coverage for every previously untested class (`WatchTools`, `IntegrityTools`,
  `UsnTools`, `ShellTools`) and dispatch/validation paths across System/Window/File/Process/
  Screen/Security/Network/Disk/Input/UIAutomation/Web tools.
- Service unit tests for PathPolicy, RegistryPolicy, Audio scalar math, EnvService, PowerService
  unknown-action, Notification XML escape, TaskScheduler named triggers, USN volume normalize,
  FileSystem copy/move/zip, Disk file-types grouping.

### Added
- **Claude-in-Actions guard foundation (CI / dev infrastructure).** An agent-immutable
  `claude-guard` workflow (`workflow_run`-triggered so it always runs from `main`, a PR cannot edit
  its own gate) that checks any future automation PR against a docs-only allowlist, an `src/**`
  capability guard, one-concern caps, and a `..`-traversal reject — posting a `claude-guard`
  check-run (fail-closed on error). Backed by a unit-tested policy script
  (`.github/scripts/claude-guard.sh`, 13 tests, run in CI via `guard-tests.yml`), plus `CODEOWNERS`,
  an activation runbook, and the Phase-2 design spec/plan under `docs/superpowers/`. Pilot Claude
  auth is `ANTHROPIC_API_KEY` (a service credential with a Console spend cap). Part of the
  human-gated "Claude-in-Actions" doc-drift bot pilot; the bot itself (maintenance workflow +
  digest) is pending credential provisioning. Design survived two adversarial review rounds
  (Claude-opus + cross-model Gemini/OpenAI), which caught and fixed a workflow script-injection and
  a rename bypass before merge.

## [0.6.0] - 2026-07-08

### Added
- Process tool: recycle-aware lineage (`list includeLineage:true`), orphan enumeration
  (`orphans`) with `ageMinutes`/`runtimeKind`/`isSystemAdjacent` signals, root-grouping
  (`list groupByRoot:true`), name/command-line filtering, and a recycle-safe fleet kill
  (`kill tree:true`, `startTime` PID-reuse guard). Orphan detection is recycle-aware (a parent
  whose PID was reused and started after its child counts as gone), and the "orphaned is common
  and by-design on Windows" caveat is documented — the tool describes, it does not judge.

### Changed
- **`Screenshot` tool defaults to `output="file"` instead of inline base64** — saves image to
  `%TEMP%\WindowsMcp\screenshot_<timestamp>.<ext>` and returns the file path. A full-screen
  1080p PNG was embedding ~240k tokens of base64 directly in the conversation history; the file
  path response is ~4 tokens. Pass `output="base64"` to restore the previous inline behavior.

### Fixed
- **`PowerShellService` backstop was consumed by queue-wait.** The per-call backstop
  `CancellationTokenSource` was created before acquiring the serialization gate, so a caller
  queued behind many others could burn its entire runaway-script budget just waiting and be
  cancelled before its own command ran. The backstop now starts *after* the gate is acquired, so
  it bounds execution time only (its documented intent). The serialized-calls stress test is
  right-sized (the property is independent of the call count; a large count only measured
  antivirus cold-start scan time).
- **Stale `ScreenToolsTests` base64 assertion** after the `output="file"` default — the test now
  opts into `output:"base64"` to exercise the mode it asserts.

## [0.5.0] - 2026-07-04

### Added
- **Companion `windows` skill** (`skills/windows/`, loads as `windows-mcp:windows`, slash
  `/windows`) — a guidance/playbook over the server's 60 tools: tool selection (prefer the MCP
  over raw PowerShell), a 60-tool domain map, five workflow playbooks (startup/boot triage,
  process cleanup, security sweep, UI-automation loop, file forensics), and safety rails for
  destructive tools. No new tools; the server binary is unchanged (still reports 0.4.1).

## [0.4.1] - 2026-06-26

### Fixed
- **`defender_status` faulted instead of returning data** — found by live end-to-end testing right
  after the v0.4.0 release. Windows PowerShell 5.1 `ConvertTo-Json` emits `/Date(ms)/` for
  `DateTime`, which `System.Text.Json` cannot parse into `DateTime?`. The script now forces ISO
  8601 (`.ToString('o')`), and deserialization now degrades to a `Note` instead of faulting.

## [0.4.0] - 2026-06-26

Codebase-audit sweep: fixes every defect a 3-agent audit found, restores the thin-tool pattern
across the last hold-outs, closes service test-coverage gaps, and adds 8 inspection tools
(tool count 52 → 60).

### Added
- `file_streams` tool (NTFS ADS + reparse target), new `IFileStreamService`.
- `driver_list` tool (PnP signed drivers), new `IDriverService`.
- `reliability` tool (minidumps + reliability records), new `IReliabilityService`.
- `cert_store` tool (Windows cert-store enumeration), new `ICertStoreService`.
- `defender_status` tool (`Get-MpComputerStatus` posture snapshot).
- `process_inspect` tool (parent/command line/start time/module inventory), with `ProcessService`
  now depending on `IWmiService`.
- `verify_signature` tool exposing `AuthenticodeInspector` trust checks.
- `file_hash` tool exposing SHA256/SHA1/MD5 hashing.

### Changed
- `network ports` now includes owning process PID/name (via `Get-NetTCPConnection`).
- Added broad unit coverage for previously under-tested services (`WebService`, `NetworkService`,
  `ProcessService`, `WmiService`).
- Extracted `firewall` logic into `IFirewallService`/`FirewallService` (typed DTO parsing +
  explicit failure handling).
- Extracted `security_audit` logic into `ISecurityService`/`SecurityService` (typed parse +
  note on probe-wide failure).
- Extracted `disk_inspect` logic into `IDiskService`/`DiskService` (typed DTOs, PS 5.1-safe
  reclaimable script, empty-output guard).
- Plumbed `CancellationToken` from tools into service-layer operations for PowerShell-backed,
  process/service/scheduled-task/event-log, and file flows.

### Fixed
- `storage_health` temp-script invocation now escapes apostrophes in staged `.ps1` paths.
- `PowerShellService` semaphore starvation risk fixed with a linked 10-minute backstop CTS and
  earlier cancellation-kill callback registration.
- `file_search find_duplicates` now skips unreadable files instead of aborting the run.
- `power_action` now enables `SeShutdownPrivilege` and checks native return values for all actions.
- `get_table` now reads headers from `TablePattern.ColumnHeaders` when available.
- Fixed native handle/COM leaks in process/WMI paths:
  - `ProcessService.ListAsync` now disposes `Process` wrappers.
  - `KillAsync`/`StartDetachedAsync` and `WindowService.LaunchAsync` dispose process handles.
  - `WmiService.QueryAsync` now disposes collection and each `ManagementObject` row.

## [0.3.1] - 2026-06-26

### Fixed
- **`storage_health` returned empty / timed out against the live MCP server** due to two e2e-only
  defects:
  1. Large generated script produced no stdout over `powershell -Command -` (stdin). Fixed by
     staging to temp `.ps1` and invoking as file.
  2. `Get-PhysicalDisk` + SMART could wake sleeping USB/SD devices and stall. Physical disk + SMART
     probing is now opt-in (`include_usage`), with default path using fast metadata-only probes.
- Default budget increased from 30s to 45s; both default and `include_usage:true` paths verified
  live.

## [0.3.0] - 2026-06-25

### Added
- **`storage_health` MCP tool** — disk/drive health diagnostics (physical disks, SMART reliability,
  volume↔disk mapping, recent disk-stack Error/Warning events), with metadata-first defaults,
  opt-in usage probing, and cancellation-safe execution. Backed by `IStorageService`/`StorageService`.
  - Docs counts refreshed (51→52 tools, 13→14 tool classes), and stale OVERVIEW service counts fixed.
  - Added `InternalsVisibleTo("WindowsMcp.Tests")` for white-box helper tests.
- **`startup_report` Control Panel parity + `summary` format**.
- **`startup_report` coverage expansion** (DNS, HKU Run/RunOnce, applets, AT hooks, IFEO,
  Winlogon hooks, AppInit_DLLs, Active Setup, proxy, trusted zones).
- **`startup_report` MCP tool** and supporting abstractions/services:
  `IRegistryService` enumerate helpers, `ITaskSchedulerService.ListDetailedAsync`,
  `IAuthenticodeInspector`, `ILspEnumerator`, `IShortcutResolver`, `IStartupReportService`,
  report DTOs/helpers (`StartupApproval`, `CommandTarget`, `StartupReportRenderer`), and DI wiring.

### Changed
- Docs updates for `startup_report` behavior and architecture counts.
- `tools/create-dependency-graph` gained C# support, auto language detect, C# parsing/categorization,
  C# dependency matrix, namespace-root inference from `.csproj`, `--lang=auto|typescript|csharp`,
  and `Statistics.totalTypeScriptFiles` rename to `totalSourceFiles`.
- Rewrote architecture docs (`OVERVIEW.md`, `ARCHITECTURE.md`, `COMPONENTS.md`, `DATAFLOW.md`) for
  current C#/.NET 9 architecture.

### Fixed
- `startup_report` signer resolution for bare-name targets via `CommandTarget.ResolveFullPath`.
- `startup_report` accessibility section now filters non-executable numeric `StartExe` entries.
- `AuthenticodeInspector` catalog verification now passes `hCatAdmin` (fixing false negatives on
  SHA-256 catalog members).
- `CommandTarget.Exists` now PATH-resolves bare executable names.
- `UIAutomationService.GetStateAsync` now roots at foreground top-level window (with fallbacks),
  and Notepad fixture foregrounding improved determinism.

### Security
- `tools/` dev deps audit fixed high-severity transitive advisories (`tar`, `picomatch` via
  `tinyglobby`) in lockfiles only; tool build/run behavior unchanged.
