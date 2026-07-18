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
