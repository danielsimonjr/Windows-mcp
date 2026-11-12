## [Unreleased]

### Added

- **Every client is told how to drive the server when it connects** (E-1). The `initialize`
  result now carries `instructions` — `McpServerOptions.ServerInstructions`, set in
  `WindowsMcpHost.AddWindowsMcp`, so stdio and HTTP send the same text — a 16-line condensation of
  `skills/windows/SKILL.md`, which only a Claude Code plugin install ever loads: `snapshot` first,
  act by `el_N` id, confirm with `assert_element` / `wait_for` (a timeout is `Satisfied:false`),
  which calls retire snapshot ids, read `Truncated` and the error text, stop on a takeover note,
  which changes are refused without `confirm: true` and which never ask (`powershell`, `archive`,
  `file_manage` with overwrite, `scheduled_task` create or run, `service` start, `job` cancel,
  `window` close, `http_request`), and `powershell` with `background:true` for long scripts. The
  text is `Hosting/ServerInstructions.Text`: 1 980 characters of printable ASCII with `\n` breaks,
  under a 2 000-character ceiling. Every sentence is a claim `ServerInstructionsTests` pins against
  the code — back-ticked names against the discovered tool set, each parameter against its tool's
  schema, the never-ask list against every `Destructive` tool with no `confirm` parameter (and
  each item really running without one), the takeover wording against the real note, the
  snapshot-evicting calls against the service, the quoted error mask against what an unexpected
  fault reads as over the production wiring — so changing a tool's parameters, its confirm gate or
  what evicts snapshot ids means changing the sentence. `HttpTransportTests` and
  `StreamTransportTests` read it back from `initialize` on each transport. No tool added (still
  69), no tool parameter or description changed.
- **An "AI is in control" indicator and a takeover hotkey** (parity S-11, a deliberate deviation
  from upstream). While a driving tool runs — `click`, `drag`, `hover`, `type`, `key`, `shortcut`,
  `scroll`, `multi_select`, `multi_edit`, `interact_element`, `file_dialog`, `focus`,
  `switch_to_window` (`Hosting/DrivingTools.cs`) — a blue ring follows the pointer and a dark pill
  banner, centred just below the top of the work area of the pointer's monitor and scaled to its
  DPI, reads `AI is controlling mouse & keyboard · Ctrl+Alt+Shift+Backspace to take over`. It stays
  up while any driving call is in flight (refcounted: HTTP clients can overlap) and fades 5 s
  after the last one ends. Pressing the combination hides it at once, and the next tool result —
  from any tool, an error result included — carries one extra, last text block, once:
  `Note: the user pressed Ctrl+Alt+Shift+Backspace at 14:03:12 (4 s ago) and took over the mouse
  and keyboard; the screen may have changed since your last snapshot.` A call the client
  cancelled, or one that faulted into the SDK's masking, leaves the note pending for the next.
  **Nothing is refused, delayed or cancelled, and physical input is never blocked**: upstream
  0.8.8's desktop-control gate refused the agent whenever the user touched the mouse (#457) and
  latched on a missed key-up (#456), so only the visible half is ported and moving the mouse is
  never a takeover. The combination is registered only while the indicator is up, so other
  applications have it the rest of the time; when it cannot be registered (another application
  holds it, or Windows reserves it) the banner drops the hint and a warning is logged once.
  Neither window is ever in a capture — `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` on
  Windows 10 2004+ (build ≥ 19041; verified on real pixels with both `gdi` and `wgc`), and below
  that build `ScreenshotService.CaptureAsync`, the one path `screenshot`, its annotated form and
  `ocr` share, hides them around each grab through `IControlIndicator.SuspendForCapture()`. Both
  are click-through, non-activating, untitled tool windows, so `window(action:"list")` and
  `snapshot` never see them; with no interactive window station everything is a silent no-op, a
  show waits at most ~50 ms, and a fault in the indicator can never fail a call. New options,
  both transports: `--control-indicator on|off` / `WINDOWSMCP_CONTROL_INDICATOR` (default on; off
  creates nothing and registers nothing) and `--control-hotkey <combo>` /
  `WINDOWSMCP_CONTROL_HOTKEY` (default `ctrl+alt+shift+backspace`; ctrl, alt or win must be
  written — shift alone, or a modifier the keyboard layout implies for the key, does not count, so
  the combination never takes a key people type; implied modifiers are still registered). New
  `IControlIndicator`/`ControlIndicator`, registered always (39 → 40 services) with nothing created
  until the first driving call; `Models/ControlIndicatorDtos.cs` (`HotkeyChord`,
  `ControlIndicatorOptions`, `TakeoverRecord`); the pure `Services/ControlIndicatorState.cs`
  (refcount, a fade measured on the monotonic clock, the pending takeover) and
  `Services/ControlIndicatorArt.cs` (SkiaSharp); `Services/LayeredWindow.cs` (see *Changed*);
  `Hosting/HotkeyParser.cs` and `Hosting/DrivingTools.cs`. The call-tool filter in
  `WindowsMcpHost.AddWindowsMcp` brackets driving calls and appends the note; `RegisterHotKey`,
  `UnregisterHotKey` and `SetWindowDisplayAffinity` join `NativeMethods.txt`. No new tool (still
  69) and no tool parameter changed. Tests: `HttpTransportTests`' harness runs with the indicator
  off; `ControlIndicatorDesktopTests` and `ControlIndicatorPointerTests` set per-monitor-v2 DPI
  awareness process-wide (the server's mode), so UIAutomation classes that run after them in the
  same process inherit it, and `ControlIndicatorPointerTests` moves the real pointer (attended
  runs only). Known limitations — the agent's own `shortcut` with exactly the takeover chord reads
  as a takeover, and over HTTP with several clients the note goes to whichever call completes
  first — are in the design note: `docs/design/S-11-control-indicator.md`.
- **`build.ps1` at the repo root (dev infrastructure).** `.\build.ps1` is a shortcut that runs
  `scripts/build-release.ps1` — still the one place the publish flags live — and passes on its
  exit code, producing `bundle/WindowsMcp.exe`; README "Build" now leads with it.
- **`scrape` reads the open browser tab, caps what it returns, and can summarise through your own
  client's model** (parity C-5, roadmap R8). `scrape(url?, query?, source = "http",
  summarize = false, max_chars = 100000, window?)` answers with JSON
  `{Source, Url, Title, Chars, Truncated, Content, Summarized, Model, Note}` instead of a bare
  markdown string (see *Changed*). `source:"http"` fetches `url` as before — now reporting the
  document's own `Title`, the `Url` **after** redirects (any credentials in it stripped, every
  escape intact) and the text cut at `max_chars` (1–1000000), `Chars` being the size before the
  cut. `source:"dom"` reads the page already open in a Chromium browser — the window named by
  `window`, else the frontmost Chromium window; Firefox is refused by name, since it exposes no
  page document to UI Automation — through A-5's page walk, ending in one scroll hint ("Reached
  top of the page; scroll down to see more." / "Reached bottom …" / "Scrolled N% down the page
  …"); a walk the element budget cut short comes back `Truncated: true` with a `Note` naming
  `--max-tree-elements`. `summarize:true` asks **your client's** model, through MCP sampling, to
  condense the page and to answer `query` from it when one is given, waiting at most 120 s: a
  client that never declared the sampling capability, this server's stateless HTTP transport, a
  client that never answers and a client whose handler fails all return the text unchanged with
  `Note` saying which — the page is never lost because the summary failed — and the model is told
  when the text it was given is only the beginning of a longer page. Every refusal (`source`,
  `max_chars`, `url` with `dom`, `window` with `http`, `query` without `summarize`) names its
  parameter and fires before anything is fetched or walked; a 404 or a refused connection is a
  caller-facing `InvalidOperationException` naming the URL and the client's own 100-second
  timeout a `TimeoutException`, instead of the SDK's masking; and HTML nested more than 300
  levels below `<body>` is refused naming the URL and the limit, because `ReverseMarkdown`
  recurses once per level and overflowed the stack at ~900, taking the whole server down mid-call.
  New pure `Services/DomPage.cs` (the render and the hint), `Services/ScrapeSummary.cs` (the
  sampling prompt), `Services/TextCap.cs` (the one surrogate-safe cut both sources share), the
  internal `Tools/ISamplingClient.cs` seam over `McpServer.SampleAsync`, the `ScrapeResult` and
  `TransportOptions` records, and `WindowFilter.IsChromium` beside `IsBrowser`. MCP sampling is
  deprecated in the SDK (analyzer `MCP9005`, specification revision 2026-07-28) and nothing
  replaces asking the *client's* model, so the warning is suppressed narrowly with its reason in
  the files that touch the types. No new service — the count stays 39. Design note:
  `docs/design/C-5-scrape-dom-summary.md`.
- **`powershell` takes a per-call timeout that returns the partial output, and `Path` is repaired
  at startup** (parity C-6, roadmap R9). `powershell(command, background = false,
  timeout_seconds = 0)` bounds one call to 1–900 seconds; `0` (the default) is the 15-minute
  execution backstop only, and `timeout_seconds` with `background:true` is refused — a job has
  `job(cancel)` and its own 60-minute backstop. Expiry does **not** throw: the child tree is
  killed and the call returns `TimedOut: true`, `Success: false`, `ExitCode: -1`, the stdout the
  script wrote before it hung — usually the diagnosis — and `Errors` holding the errors the script
  wrote with `"timed out after Ns"` last. The clock starts **after** the serialization gate, like
  the backstop, so a queued caller does not burn its budget waiting behind another script.
  `IPowerShellService.RunAsync(command, TimeSpan? timeout, ct)` sits beside the old overload.
  Only stdout survives the kill — Windows PowerShell 5.1 buffers its CLIXML stderr records until
  host shutdown (measured 2026-09-08) — so the harvest decodes what arrived and never hands the
  bare `#< CLIXML` header to the model. Separately, `Hosting/EnvironmentRepair` now repairs
  `Path`: one that the host set empty, or without a `System32` entry, gets the registry's machine
  and then user `Path` **appended** behind the host's own entries — never reordered, never
  trimmed, de-duplicated by the pure new `Hosting/PathMerge.cs` — with a stock-four fallback
  (`<SystemRoot>\System32`, `<SystemRoot>`, `…\Wbem`, `…\WindowsPowerShell\v1.0`) when the
  registry gave nothing and `SystemRoot` is known, so `git`, `node` and `where.exe` resolve
  inside a tool call even under a host that stripped the block. `Path` joins `PATHEXT` as the
  only value the repair will touch, and the startup line on stderr names it. No new service — the
  count stays 39 — and no new tool: still 69. Design note:
  `docs/design/C-6-powershell-timeout-path.md`.
- **`review-agent` subagent (dev infrastructure).** `.claude/agents/review-agent.md` — an
  Opus-model Claude Code subagent that runs after the GREEN pass and before the PR. It reads the
  finished diff cold and hunts, per changed behaviour, for the inputs the tests never named:
  existing state at the target, containment and aliasing in every direction, cancellation in
  every loop, refusals that must run before any mutation, partial failure, changed defaults, error
  types the client never sees. Each finding names a concrete input, the wrong outcome, the
  `file:line` and the sibling cases in its family, and goes back to `test-agent` as a RED row.
  Added because PR #25 needed three external review rounds after a green suite: the tests proved
  the design note, not the code's failure modes, and each fix was scoped to the reported symptom.
  `CLAUDE.md`'s workflow gains step 4, REVIEW. It never edits and never commits.
- **`file_read` pages a large file, `file_write` appends, `file_manage` gained the flags that
  make its defaults safe** (parity C-1, roadmap phase 2). `file_read(path, max_bytes, encoding,
  offset_lines = 0, limit_lines = 0)` returns the same plain text as before when neither window
  parameter is given, and a JSON `{path, totalLines, offset, returned, truncated, content}`
  window when either is — `offset_lines` is 1-based (`0` and `1` both mean the first line),
  `limit_lines: 0` runs to the end, and `truncated` says lines remain past the window, so a 5 MB
  log is readable a page at a time instead of by raising `max_bytes`, which still bounds the
  *file*. `file_write(…, append = false, create_parents = true)` adds to the end instead of
  replacing (the reply says `appended`) and creates a missing parent directory unless
  `create_parents: false` refuses it by name. `file_manage(…, overwrite = false,
  recursive = false, pattern = null, include_hidden = false, max_entries = 1000)`: `copy`/`move`
  take `overwrite`, a
  directory `copy` copies the tree and a cross-volume `move` falls back to copy-then-delete;
  `delete` takes `recursive`; and `list` returns
  `{Entries: [{Path, Name, IsDirectory, Size, Modified, Hidden, IsLink}], Truncated, MaxEntries}`
  with a case-insensitive name glob
  (`pattern`), optional recursion, hidden/system entries skipped unless `include_hidden`, and the
  walk stopped at `max_entries` (1–100000)
  (a skipped directory is not descended into, and an inaccessible entry is skipped rather than
  failing the listing). New pure `Services/LineWindow.cs`, new `FileEntry`, `FileListing` and
  `TextWindow` DTOs,
  and `IFileSystemService.ReadLinesAsync` beside the flags on `WriteTextAsync` / `CopyAsync` /
  `MoveAsync` / `DeleteAsync` / `ListAsync`. No new service — the count stays 39. Design note:
  `docs/design/C-1-file-flags.md`.
- **`process(list)` has a CPU column, an order and a cap; `process(kill)` can ask first**
  (parity C-3, roadmap phase 2). Every plain `process(list)` row now carries `CpuPercent`, taken
  from two `TotalProcessorTime` readings ~250 ms apart and normalised across **all** cores — a
  process saturating one of eight reads `12.5`, which is what Task Manager shows — with each
  reading timestamped per process so the box sums to about 100 %. `sort_by`
  (`memory` default | `cpu` | `name` | `pid`) and `limit` (`0` = all) order and cap that list;
  both are refused rather than silently ignored with `includeLineage` / `groupByRoot` /
  `orphans`, which have their own shapes and no CPU column. `process(kill, …, graceful = false,
  grace_ms = 3000)` posts `WM_CLOSE` to every *visible* top-level window of the pid, waits up to
  `grace_ms` (0–60000) and only then forces the process, so an editor can show its "save
  changes?" prompt; a process with no window is forced at once and the result says so, and
  `graceful` with `tree: true` is refused. New pure `Services/CpuSample.cs` (the normalisation,
  the sort and the cap), the internal `IProcessWindowNative` / `Win32ProcessWindowNative` seam
  (`EnumWindows` + `GetWindowThreadProcessId` + `PostMessage`, so the kill path is testable
  headless), `ProcessSort` / `ProcessListOptions` / `KillOptions` / `KillResult` DTOs,
  `ProcessDto +CpuPercent`, and `IProcessService.ListAsync(ProcessListOptions)` /
  `KillAsync(pid, KillOptions)` beside the old overloads — the `ListAsync(nameFilter)` one still
  does **not** sample, so a name kill and the startup report do not pay the 250 ms window. No new
  service — the count stays 39. Design note:
  `docs/design/C-3-process-cpu-graceful-kill.md`.
- **`registry_delete` — remove a registry value or a whole key, behind `confirm`** (parity C-2;
  68 → 69 tools, roadmap R6). `registry_delete(hive, path, value_name?, recursive = false,
  confirm = false)` removes that one value when `value_name` is given, otherwise the key itself:
  a key that has sub-keys also needs `recursive: true` (refused naming the flag and the count
  otherwise), and the hive root and a short denylist of roots the profile or Windows depends on
  (`Software`, `Software\Classes`, `Software\Microsoft`, the two `Windows` /
  `Windows NT` roots and their `CurrentVersion` keys, `Software\Policies`,
  `Software\WOW6432Node`, `System`, `SYSTEM\CurrentControlSet`, `SAM`, `SECURITY`,
  `Environment`, `Control Panel`, `Volatile Environment`) are refused outright, before the
  service is touched. Deleting what is
  not there is not an error: `existed: false`. Returns `{hive, path, valueName?, deleted, existed,
  subKeysRemoved?}`, where `subKeysRemoved` is the descendant count taken **before** the delete.
  Under `HKLM` the unelevated server usually lacks the rights and the OS error passes through
  unchanged. New pure `Services/RegistryGuard.cs` (the denylist, with normalisation so every
  spelling of a root compares equal) and `IRegistryService.DeleteValueAsync` /
  `DeleteKeyAsync` → `RegistryKeyDeleteResult`. No new service — the count stays 39. Design note:
  `docs/design/C-2-registry-delete.md`.
- **Every tool now advertises a title and all four MCP hints** (parity C-7, roadmap R10). Each
  `[McpServerTool]` in `src/WindowsMcp/Tools/*.cs` names `Title`, `ReadOnly`, `Destructive`,
  `Idempotent` and `OpenWorld` explicitly — even where the value equals the SDK default, which the
  SDK could not otherwise distinguish from "never set" — so clients see `readOnlyHint`,
  `destructiveHint`, `idempotentHint`, `openWorldHint` and a human title on all 69 tools instead
  of the MCP defaults (not read-only, destructive, not idempotent, open-world) on every one of
  them. A client can now auto-approve `screenshot` and `get_text` and confirm `file_manage` and
  `registry_delete` without a hand-maintained allowlist. The classification is one reviewed table
  (`docs/design/C-7-tool-annotations.md`), pinned column by column in `ToolInventoryTests`, which
  also holds README's "Safety rails" list and `destructiveHint` to the same answer. No behaviour
  change and no service touched.
- **`notification` takes an `app_id`** (parity C-4). `notification(title, message,
  app_id = "Windows-MCP")` shows the toast under that AppUserModelId — Windows treats the AUMID as
  the toast's identity. The server's own default id is registered once per process under
  `HKCU\Software\Classes\AppUserModelId\Windows-MCP` (a `DisplayName` value, the documented
  registration for an unpackaged exe); a packaged app's AUMID (the `Package_hash!App` form) shows
  the toast under that app, and any other id must already be registered or Windows drops the
  toast — reported as `shown: false` with the reason in `note`, never a throw. A caller's id is
  never written to the registry, and a blank `app_id` is an `ArgumentException`. Design note:
  `docs/design/C-4-notification-app-id.md`.
- **`multi_select` and `multi_edit` — a batch of clicks, or a whole form, in one call** (parity
  B-7; 66 → 68 tools, roadmap C3's last two slots). `multi_select(targets_json, ctrl = true)`
  takes a JSON array of `{x, y}` points or `{element_id}` objects — a JSON *string* holding that
  array is unwrapped once, for clients that stringify arguments — and clicks every one of them
  with Ctrl held from before the first click until after the last (`ctrl: false` clicks without
  it; the release sits in a `finally`, so a failure mid-batch never leaves the modifier down).
  `multi_edit(entries_json)` takes the same targets plus `text` (required) and the optional
  `clear` / `press_enter`, and per entry clicks the target then types through B-1's `TypeAsync`
  path, so `clear`, `press_enter` and the long-text paste mean exactly what they mean on `type`.
  Both resolve **every** target through B-4's `ResolveTargetAsync` before any input is sent, so a
  batch holding one off-screen element is refused with nothing done; both then run in order and
  **stop at the first failure**, returning `failedIndex` and `error` alongside the per-entry
  `results` produced so far — the batch is deliberately not atomic, and the response says how far
  it got. Returns `{count, ctrl, results:[{index, x, y, elementId?, name?, ok}], failedIndex?,
  error?}` and `{count, results:[{index, x, y, elementId?, name?, typed, method, ok}],
  failedIndex?, error?}`. New pure `Services/BatchTargets.cs` (one parser for both parameters,
  every refusal naming the parameter and the offending entry's index) and
  `IInputService.KeyDownAsync` / `KeyUpAsync` over new `KeyDown` / `KeyUp` members on the internal
  `IKeyboardSink` seam. No new service and no new CLI option — the service count stays 39. Design
  note: `docs/design/B-7-batch-tools.md`.
- **`wait_for` waits for a condition, not just an element** (parity B-6, roadmap C4).
  `wait_for(text, timeout_ms, interval_ms, kind, scope, window, include_offscreen,
  condition = "element_exists", use_dom = false)` adds four conditions beside today's behaviour:
  `element_enabled` (the same `find_element` path, but the match must be enabled),
  `focused_element` (the element holding keyboard focus is the one named), `text_exists` (the text
  anywhere in a `snapshot` of the scope — element names and values, scrollable regions, and with
  `use_dom: true` the browser page's own words, A-5) and `active_window` (the foreground window's
  title, matched exact → substring → fuzzy ≥ 70), which reads only the window inventory and never
  walks the UI tree. Upstream's short aliases `element`, `enabled`, `focused`, `text` and `window`
  are accepted; an unknown condition is an `ArgumentException` listing all five. `timeout_ms` is
  now range-checked at 0–120000 and `interval_ms` at 0–5000, and a blank `text` is refused naming
  the condition. Existing calls are unaffected: the default condition is exactly the old
  behaviour, and `text` keeps its place as the first parameter. New `WaitCondition`, `WaitRequest`
  and `WaitForResult` DTOs, the pure `Services/UiTree/WaitConditions.cs` evaluator (roadmap C10 —
  one poll's evidence in, verdict and detail out, no UIA and no clock) and
  `IUIAutomationService.WaitForAsync(WaitRequest)` beside the original overload, which is
  unchanged. Design note: `docs/design/B-6-wait-for-conditions.md`.
- **`launch` opens an app by the name a person would say it, and hands back its window** (parity
  B-8). `launch(app_name, wait_for_window = true, timeout_ms = 10000)`: a path, or an executable
  name that exists on `PATH`, is still started outright; anything else is resolved against an
  in-process catalog of Start Menu shortcuts (every `.lnk` under both `Programs` folders) and
  packaged Store/MSIX apps (the WinRT `PackageManager`'s app list entries — display name plus
  AUMID), matched exact, then by prefix, then fuzzy (score ≥ 70), so `launch("calc")` opens
  Calculator and `launch("vs code")` opens Visual Studio Code. A packaged app is activated by its
  AUMID through the shell's activation manager — the only API that hands back the pid — and a
  `.lnk` or a path goes through ShellExecute. The window inventory is then polled every 250 ms up
  to `timeout_ms` (1–60000) for a window of that pid, else for a window that was **not** open
  before the launch whose title matches the resolved name, so a packaged app or a browser that
  hands its window to a process the activation never named is still found. Returns `{MatchedName,
  Kind (shortcut|packaged|path), Score, Pid, Hwnd, Title, WindowDetected, Strategy
  (path|exact|prefix|fuzzy)}`; a wait that runs out is `WindowDetected:false` with the pid, not an
  error, and a name that matches nothing is an error listing the five nearest apps with their
  scores. Roadmap C7: no PowerShell anywhere in this path — `Get-StartApps` would pay a process
  cold start and take the serialization gate on every `launch`. New
  `IAppCatalogService`/`AppCatalogService`, registered as a singleton (38 → 39 services), with
  the catalog cached for five minutes and refreshed once on a miss; pure `Services/AppCatalog.cs`
  (merge and match) and `Services/LaunchWait.cs` (pick and poll); the `IAppActivator` seam with
  `Services/Win32AppActivator.cs` (`IApplicationActivationManager`, leading method only per the
  COM rule); DTOs `AppEntry`, `AppMatch` and `LaunchResult`; and `IWindowService.LaunchAsync(app,
  waitForWindow, timeoutMs)` alongside the original `LaunchAsync(app)` overload, which is
  unchanged. Design note: `docs/design/B-8-launch-catalog.md`.
- **`window` moves and resizes** (parity B-9). `window(action: "move" | "resize" | "set_bounds",
  title?, hwnd?, x?, y?, width?, height?, restore_first?)`: `move` needs `x` and `y` and keeps the
  size, `resize` needs `width` and `height` and keeps the position, `set_bounds` needs all four —
  the per-action rule is checked in the tool, before any window is touched. The target is matched
  exactly as the other `window` actions (`hwnd` wins, else `title` exact → substring → fuzzy),
  or is the foreground window when neither is given. A minimized or maximized window is refused
  naming its state unless `restore_first:true`, which sends `SW_RESTORE` first. One `SetWindowPos`
  always carries `SWP_NOZORDER|SWP_NOACTIVATE` — a move must not raise or focus the window — plus
  `SWP_NOMOVE` or `SWP_NOSIZE` for the half that was not asked for. Returns `{Window, Before,
  After, MatchStrategy, Score, Restored}`, where `After` is a second `GetWindowRect` rather than
  the requested rectangle (roadmap C11: the response is the outcome). New pure
  `Services/WindowGeometry.cs` behind an `IWindowGeometryNative` seam
  (`Services/Win32WindowGeometryNative.cs`), `IWindowService.SetBoundsAsync` and the
  `WindowBoundsResult` DTO; `NativeMethods.txt` is unchanged — `SetWindowPos`, `GetWindowRect`,
  `IsIconic`, `IsZoomed` and `ShowWindow` were already declared. Design note:
  `docs/design/B-9-window-bounds.md`.
- **`drag` moves the way a drop target expects: a nudge, intermediate motion, a duration, and an
  origin that defaults to the cursor** (parity B-2).
  `drag(from_x?, from_y?, to_x?, to_y?, element_id?, from_element_id?, button, duration_ms?,
  steps?)` presses at the origin, moves first past the system drag threshold (`SM_CXDRAG` + 1),
  then through `steps` interpolated points spaced `duration_ms / steps` apart, and releases
  exactly on the destination — file managers, canvases and browser drag-and-drop ignore a drag
  that teleports. The destination is `to_x`/`to_y` or `element_id` (its centre); the origin is
  `from_x`/`from_y`, `from_element_id`, or nothing at all, which starts at the live cursor and
  reports `fromTarget: "cursor"` (roadmap C2). `duration_ms` is 0–10000 (default 300) and `steps`
  2–200 (default 20), both refused with the range when out of it; the button is released even
  when the call is cancelled mid-motion, and middle-button drags are still unsupported. New pure
  `Services/DragPath.cs` (nudge first, monotone points, the last one exactly the destination);
  `IInputService.DragAsync(…, durationMs, steps)` joins the original press-jump-release overload,
  which is unchanged. Design note: `docs/design/B-2-drag.md`.
- **`scroll` scrolls under the cursor, at an element, or sideways with Shift** (parity B-3).
  `scroll(direction, amount?, x?, y?, element_id?, shift_wheel?)`: with no target the wheel turns
  wherever the pointer already is (`GetCursorPositionAsync`, roadmap C2), with `element_id` at
  that element's centre, with `x`/`y` at the point — and the response says which of
  `point | element | cursor` was used and where. `shift_wheel:true` holds Shift and sends the
  *vertical* wheel for `left`/`right`, which is how a window with no horizontal wheel scrolls
  sideways; it is refused for `up`/`down` in both the tool and the service.
  `IInputService.ScrollAsync(…, shiftWheel)` joins the original overload, and an invalid
  direction is now rejected before the cursor is moved rather than after. Design note:
  `docs/design/B-3-scroll.md`.
- **`type` takes a target, clears, places the caret, presses Enter — and pastes long text**
  (parity B-1). `type(text, x?, y?, element_id?, clear?, caret?, press_enter?, pace_ms?)` is now
  the whole gesture in one call: `x`/`y` or `element_id` clicks the target first, `clear:true`
  sends Ctrl+A then Backspace, `caret: start|end` sends Ctrl+Home / Ctrl+End (`idle`, the
  default, moves nothing), and `press_enter:true` presses Enter last — the
  `click` → `ctrl+a` → `backspace` → `type` → `enter` round-trip in one round-trip. The new pure
  `Services/TypePlanner.cs` decides how the text is entered (roadmap C8): 200 characters or more
  with no control character other than newline or tab goes through the clipboard as a single
  Ctrl+V, and the clipboard text that was there is put back afterwards; anything else is typed
  key by key, one character per SendInput call with `pace_ms` (default 5) between them — a whole chunk in one call is what a busy target garbles into its last character. The result says which path ran —
  `{typed, method: keys|paste, clipboardRestored?, x?, y?, elementId?, name?}` — and a paste that
  cannot borrow the clipboard falls back to keys and reports `keys`. New `TypeOptions` and
  `CaretPosition`; `TypeResult` gained trailing `Method`/`ClipboardRestored`;
  `IInputService.TypeAsync(text, options)` joins the single-argument overload;
  `InputService` takes an optional `IClipboardService` and executes plans against an internal
  `IKeyboardSink` seam (`SimulatorKeyboardSink` in production), which is what makes keystroke
  order testable without injecting input. Design note: `docs/design/B-1-type.md`.
- **`click` takes a snapshot element id, and `clicks:0` is a hover** (parity B-4).
  `click(x?, y?, element_id?, button, clicks)` accepts an `el_N` id from `snapshot` /
  `find_element` in place of coordinates and clicks that element's centre, so the ids the
  snapshot hands out no longer have to be copied back as numbers; an element that is off-screen,
  has no bounds, or has empty bounds is refused with a message naming the id and the reason,
  *before* any input is sent. `clicks:0` moves the pointer and presses nothing, reported as
  `{action: "hover"}`; `hover` stays as its own tool. The response is
  `{action, x, y, button, clicks, elementId?, name?}` with the point actually used. The rule is
  one resolver for every input verb (roadmap C1): exactly one of (`x` and `y`) or `element_id`,
  with both, half a pair, or neither where a target is required an `ArgumentException` naming the
  parameters in play — `click`, `type`, `scroll` and `drag` share it, and `InputTools` now injects
  `IUIAutomationService` for the id lookup. New pure `Services/UiTree/ElementTarget.cs`. Design
  note: `docs/design/B-4-click-by-element.md`.
- **`start_process` takes an argv list, a working directory and a shell-execute flag** (parity
  B-11). `start_process(command, args_json?, cwd?, use_shell_execute?)`: `args_json` is a JSON
  array of strings whose items go to `ProcessStartInfo.ArgumentList` **verbatim** — no quoting,
  no splitting, no escaping — so a path with spaces and quotes arrives intact, and with it
  `command` is the executable only. `cwd` sets the working directory and a directory that does
  not exist is a `DirectoryNotFoundException` naming it, raised before anything is spawned;
  `use_shell_execute` launches through the shell. `command` alone keeps its old whole-command-line
  meaning byte for byte (quoted exe, else the first space). New pure `Services/ArgvJson.cs`, the
  `ProcessStart` DTO and `IProcessService.StartDetachedAsync(ProcessStart)`; the string overload
  now forwards to it. Design note: `docs/design/B-11-start-process-argv.md`.
- **`multi_monitor` reports work area, orientation, DPI and scale** (parity B-12). Each monitor
  now also carries `WorkArea` (`GetMonitorInfo.rcWork` — the monitor minus the taskbar and any
  docked appbars, in virtual-desktop pixels), `Orientation` (0/90/180/270 from the current
  display mode), `EffectiveDpi` (`GetDpiForMonitor(MDT_EFFECTIVE_DPI)`) and `Scale`
  (`EffectiveDpi / 96.0`), so a 150 % display reads `EffectiveDpi: 144, Scale: 1.5`. The four
  fields are trailing and defaulted on `MonitorInfo`, and `WindowService` now reads
  `MONITORINFOEXW` for the device name `EnumDisplaySettings` needs; both extra reads are guarded
  and fall back to `96`/`0` rather than dropping a monitor, so A-8's region maths, the snapshot
  header and `screenshot`'s metadata are untouched. Design note:
  `docs/design/B-12-monitor-detail.md`.
- **Fuzzy window matching and a real bring-to-foreground ladder** (parity B-10).
  `switch_to_window`/`focus` take `title` *or* `hwnd` (from `window(action:"list")`, which wins
  and never fuzzes) and match a title exact → substring → fuzzy (score ≥ 70), so
  `switch_to_window("notepad")` finds `"Untitled - Notepad"`; ties inside one strategy go to the
  frontmost window and minimised windows are candidates. A minimised target is restored, then the
  tool climbs `SetForegroundWindow` → `AttachThreadInput` + `BringWindowToTop` → an ALT nudge,
  re-reading `GetForegroundWindow` after every rung — Windows refuses a plain
  `SetForegroundWindow` to a background process, so success is observed, not assumed. The four
  acting `window` actions go through the same matcher and accept `hwnd` too, so
  `window(action:"close", title:"notepad")` works. New pure `Services/FuzzyMatch.cs` (the three
  `thefuzz` scorers, in-repo, no package) and `Services/WindowMatcher.cs`, plus
  `Services/ForegroundLadder.cs` behind an `IForegroundNative` seam
  (`Services/Win32ForegroundNative.cs`) so the ladder is testable with no desktop;
  `ForegroundResult` DTO, `WindowAction` gained trailing `MatchStrategy`/`Score`/`Hwnd`, and
  `AttachThreadInput`/`BringWindowToTop`/`GetCurrentThreadId`/`keybd_event` joined
  `NativeMethods.txt` (roadmap C9). Design note: `docs/design/B-10-window-matching.md`.
- **`wait` — a plain pause, without a PowerShell cold start** (parity B-5; 65 → 66 tools).
  `wait(seconds)` delays in-process for more than 0 and at most 60 seconds (fractions allowed)
  and returns `{"waited": seconds}`; anything outside that range — `0`, negative, NaN, infinity,
  over 60 — is an `ArgumentException` naming the range and pointing at `wait_for` for a longer or
  conditional wait. It honours the request's cancellation token and spawns nothing, replacing
  `powershell("Start-Sleep")`, which paid a process cold start and took the PowerShell
  serialization gate. Annotated `ReadOnly = true, Idempotent = true`. Lives on `InputTools`, next
  to the verbs it is interleaved with. Design note: `docs/design/B-5-wait.md`.
- **Browser DOM mode on `snapshot`, Chromium only** (parity A-5 phase 1). `snapshot(use_dom:true)`
  walks every browser window in scope (chrome/msedge/brave/opera/vivaldi) from the web page — the
  `RootWebArea` document — instead of the window, so the address bar and tab strip never appear
  and the interactive list is the page's links, buttons, inputs, checkboxes, selects and list
  items with the same `el_N` ids and action hints as everywhere else. The result gains a `Pages`
  section, one entry per browser window: the page document's id, title, URL, vertical scroll
  percent and the visible page text in document order (the text form prints it after the
  scrollable list; JSON carries `Pages`, absent when `use_dom` is off so nothing else changes).
  The page document is listed as scrollable, never as a control; a text node that only repeats
  its control's label is not page text. Chromium builds its accessibility tree lazily, so the
  page finder retries a few times before concluding a browser window has no page; one with none
  (still loading, or Firefox, which is a documented follow-up) is walked whole and its entry says
  so. New pure `Services/UiTree/DomCorrection.cs`; `SnapshotRequest +UseDom`,
  `SnapshotResult +Pages`, `SnapshotPage`. Design note: `docs/design/A-5-browser-dom-mode.md`.
- **A second screen-capture backend: Windows.Graphics.Capture** (parity A-10). `screenshot`
  takes `backend: auto | gdi | wgc`. `wgc` reads the compositor's own frames — one
  `GraphicsCaptureItem` per monitor the rect touches, copied through a D3D11 staging texture and
  composed into the virtual-desktop rect — so GPU-accelerated, hardware-overlay and DRM-protected
  surfaces that GDI's screen copy returns black for come out as drawn. `auto` (the default)
  prefers `wgc` where the OS supports it and falls back to `gdi` silently; `backend:"wgc"` is an
  error when the compositor cannot serve the rect (session 0, no monitor under it). The
  metadata `backend` field, reserved since A-7, now always says which backend produced the
  picture. `--screenshot-backend auto|gdi|wgc` / `WINDOWSMCP_SCREENSHOT_BACKEND` sets the
  process default a call's `auto` defers to (both transports). Cursor overlay, downscale,
  annotations and the profiling stages run unchanged on either frame. New
  `Services/WgcCaptureBackend.cs` (the two COM interfaces it needs are declared in vtable order
  per the COM rule); `CaptureOptions`/`ScreenshotOptions` carry `Backend`; `ScreenshotService` is
  `IDisposable`. Design note: `docs/design/A-10-capture-backend.md`.
- **Virtual desktops, phase 1** (parity A-12). `window(action:"desktops")` lists every virtual
  desktop (`Id`, `Name` or `Desktop N`, `Index`, `IsCurrent`) and the current one; every window
  `list`/`active` returns now carries `DesktopId`. Only the documented `IVirtualDesktopManager`
  (declared in vtable order per the COM rule) and the registry are used; the per-build internal
  interface is not. On this Windows 11 build the registry holds no `VirtualDesktopIDs` blob, so the
  service falls back to the `Desktops` subkeys for the list and to the desktop the foreground
  window is on for the current one. Every failure is an empty list or a null, never an error. New
  `IVirtualDesktopService`/`VirtualDesktopService`, `VirtualDesktopInfo`; `WindowService` takes an
  optional `IVirtualDesktopService`. Design note: `docs/design/A-12-virtual-desktops.md`.
- **A post-capture flash and per-stage profiling** (parity A-14). After every `screenshot` an
  orange glow is drawn around the captured area for ~3.5 s — a layered, click-through, top-most,
  non-activating tool window on its own thread (`FlashOverlay`, painted by a pure `FlashGlow`),
  hidden again before the next capture so it is never in a picture, invisible to `window list`
  and `snapshot`, and a silent no-op wherever there is no desktop. On by default under both
  transports; `--flash off` / `WINDOWSMCP_FLASH=off` turns it off. Metadata carries `flash: true`
  when the glow was actually shown. `--profile-snapshot on` / `WINDOWSMCP_PROFILE_SNAPSHOT=on`
  adds per-stage timings: `snapshot` reports `header`/`walk` (a `Timing:` line in the text form,
  `Stages` in JSON) and `screenshot` reports `stages` (`resolve`, `cursor`, `snapshot`, `capture`,
  `resize`, `encode`) — both also logged to stderr at Information. Off, the responses are
  byte-identical to before. New `IFlashOverlay`, `StageTiming`; `ScreenshotOptions` and
  `UiTreeOptions` carry the switches. Design note: `docs/design/A-14-flash-and-profiling.md`.
- **`screenshot(annotate:true)` — boxes, label chips and a captioned grid on the capture** (parity
  A-6). The same call walks the desktop (one `snapshot`), keeps the interactive elements inside
  the captured rect, draws a 2 px coloured box with a label chip around each — the labels are the
  snapshot's `el_N` ids — and returns **three** content blocks: metadata, the snapshot's element
  rows filtered to what the picture contains, then the image, so label N in the picture is row N
  of the text from the same call and the ids go straight to `click`/`interact_element`.
  `grid_columns`/`grid_rows` (0–64, usable without annotate) overlay guide lines captioned with
  virtual-desktop coordinates. Annotations are drawn after the downscale, on a copy of the capture
  (the unscaled path's bitmap is a read-only GDI view), and the cursor is A-11's. Metadata gains
  `annotated`, `annotations` (boxes that landed) and `grid`. Because the walk is a snapshot walk
  it evicts the previous snapshot's ids. New pure `Services/Annotator.cs` (palette, `ToImage`,
  `ChipRect`, `UseDarkText`, `Draw`); `CaptureOptions` gained `Annotations`/`Grid`,
  `ScreenshotResult` gained `AnnotationsDrawn`; `ScreenTools` now takes `IUIAutomationService`.
  Design note: `docs/design/A-6-annotated-screenshot.md`.
- **`snapshot` — one call for the whole desktop** (parity A-2, with A-3 and A-4 inside; 64 → 65
  tools). Every open window (z-order, topmost first), the foreground one, the cursor, and every
  interactive element with its centre coordinates and an action hint
  (`click|fill|toggle|select|slide|scroll`) plus focus/password/value/toggle/expand/shortcut/range
  metadata, and the scrollable regions with their scroll percentages and `reached top/bottom`.
  Default output is a compact text block (several times cheaper than the JSON tree);
  `format:"json"` returns the same as DTOs, with the element tree when `include_tree` is set.
  `scope`: `desktop` (default, every non-minimised window) | `foreground` | `window` (with a
  title, exact then substring; an unknown title lists what is open). Element ids (`el_N`) work
  with `click` (centre), `interact_element` and `get_element` and are valid until the next
  snapshot, which evicts them — a `find_element` id issued in between survives. The walk runs
  under one UIA `CacheRequest` per window (one cross-process fetch per subtree instead of one per
  property) and is capped by an element budget: `max_elements` per call, else the new
  `--max-tree-elements <n>` / `WINDOWSMCP_MAX_TREE_ELEMENTS` (default 500, both transports); a
  truncated result says so and how to narrow the view. `use_dom` is accepted and refused until
  A-5. New `Services/UiTree/`: `UiNode`, `UiClassifier` (now the single home of D-6's
  interactive set, plus upstream's LegacyIAccessible role fallback), `ElementBudget`,
  `SnapshotRenderer`, `UiTraverser`; `IUIAutomationService.SnapshotAsync` and the
  `SnapshotRequest`/`SnapshotResult`/`SnapshotElement`/`SnapshotScrollable`/`ScrollInfo`/
  `UiTreeOptions` records; `UIAutomationService` now takes `IWindowService`. Design note:
  `docs/design/A-2-desktop-snapshot.md`.
- **`window(action:"list"|"active")` — the whole-desktop window inventory** (parity A-1). `list`
  returns every user-visible top-level window in z-order (`ZOrder` 0 = frontmost): `Title`
  (sanitised, A-13), `Hwnd`, `Pid`/`ProcessName`, `State` (`Normal|Minimized|Maximized`, by
  name), `Bounds` in virtual-desktop pixels, `IsActive`, `IsBrowser` (chrome/msedge/firefox/brave/
  opera/vivaldi), `MonitorIndex` into `multi_monitor`'s list (-1 when the window's centre is on no
  monitor, e.g. minimized), and `DesktopId` (reserved for A-12, null). `active` returns the
  foreground window as the list sees it, or `{"found":false}`. `include_minimized` (default
  true) and `include_hidden` (default false: untitled windows) narrow or widen the list. The
  filter is a pure `WindowFilter` over `WindowProbe` records: invisible, tool windows (unless
  `WS_EX_APPWINDOW`), DWM-cloaked (UWP ghosts, other virtual desktops), zero-area, shell chrome
  (taskbars, Program Manager, WorkerW, IME) and untitled windows are dropped. New
  `IWindowService.ListAsync`/`GetActiveAsync`, `WindowInfo`/`WindowProbe`/`WindowState`. The
  `window` tool now validates the action first and only the acting actions need a `title`; an
  unknown action is an error naming the six actions before any window is touched (it used to
  reach the service, which reported `Success:false` whenever no window carried that title).
  `UIAutomationService`'s last stray `DllImport` (`GetForegroundWindow`) is retired for CsWin32
  (roadmap C9). Design note: `docs/design/A-1-window-inventory.md`.

### Changed

- **`get_element` answers under the id it was given, and a dead element id says why** (parity D-11
  — a contract change). `get_element("el_12")` returned the element under a freshly minted id; it
  now returns `ElementId: "el_12"`, and so does every element-targeted
  `click`/`type`/`scroll`/`drag`, which resolve through it. The old `Element 'el_N' not in cache`
  is gone: an id the server issued and no longer holds reads `Element 'el_N' has expired: a newer
  snapshot replaced it, or it aged out of the element cache.` followed by the calls that take a
  snapshot too (`wait_for` with a text or focus condition, `screenshot` with `annotate`, `scrape`
  with source `dom`) and "Take a new snapshot"; one it never issued reads `Unknown element id
  'el_N': no such id was issued.` `assert_element(state:"exists")` on an expired id is `FAIL:
  exists — observed element id has expired (take a new snapshot)` (still never an error). The
  `snapshot`, `wait_for` and `scrape` descriptions now say which calls replace the previous
  snapshot's ids, and `get_element`'s says the id is echoed.
- **`scheduled_task create` takes one trigger form, and says so** (parity D-10). The `trigger`
  description offered `'daily'` and `'onlogon'`, which `DateTime.Parse` could never read, so they
  failed masked with nothing registered. It now asks for a one-time start as a date and time
  (`2026-05-06T09:00:00`); anything else is an `ArgumentException` naming `trigger` and the
  accepted form, thrown before the scheduler is touched (`TaskSchedulerService.ParseTrigger`,
  `TaskSchedulerTriggerTests`). Recurring triggers are still not built.
- **`FlashOverlay` paints through the shared `LayeredWindow`** (S-11). The overlay window's
  creation, the DIB + `UpdateLayeredWindow` paint and the top-most, non-activating show moved out
  of `FlashOverlay` into `Services/LayeredWindow.cs`, which the control indicator's two windows
  use too. The glow looks and behaves as before.
- **`scrape` returns a JSON result object, not a markdown string** (parity C-5 — a contract
  change). The tool used to answer with the converted markdown and nothing else; it now returns
  `{Source, Url, Title, Chars, Truncated, Content, Summarized, Model, Note}` with the text in
  `Content`, so a caller that pasted the whole answer into a prompt now has JSON around it — and
  a way to tell a cut page from a whole one. `IWebService.ScrapeAsync(url, maxChars = 100000,
  ct)` → `ScrapeResult` **replaces** `ScrapeAsync(url)` → `string`; there is no string overload
  left.
- **`scrape` and `http_request` fetch http and https only** (parity C-5 — a contract change for
  `http_request`). Any other scheme — `ftp:`, `file:`, `data:`, `ws:`, `javascript:` — is an
  `ArgumentException` naming the scheme, decided **before** the address check, where it used to
  reach the caller as `HttpClient`'s masked `NotSupportedException` (and a scheme with no host
  resolved `""` — this machine — for the private-address check). `http_request` also refuses a
  malformed `headers_json` by name, instead of masking the `JsonException` behind "An error
  occurred invoking 'http_request'.".
- **`PSResult` carries three more fields, and foreground output is capped like a job's** (parity
  C-6 — a contract change for a script that writes more than a megabyte to one stream). `PSResult`
  gained trailing `TimedOut`, `StdoutTrimmedChars` and `StderrTrimmedChars`, all defaulted, so
  every construction that predates C-6 still compiles and serialises with one extra `false` and
  two zeros. Each foreground stream is now a `BoundedTextBuffer` of 1 000 000 characters with the
  **tail** kept — exactly what a background job's output already did — so a script that floods
  stdout and then hangs no longer hands the client everything it wrote (100 MB in six seconds,
  measured); the two trimmed counts say how much went.
- **A foreground PowerShell backstop expiry reaches the caller as a `TimeoutException`** (parity
  C-6 — a contract change). `IPowerShellService.RunAsync(command, ct)` — the overload every
  internal caller uses (disk, storage, security, firewall, network, audio, file streams) — threw
  `OperationCanceledException`, which the SDK masked as "An error occurred invoking '<tool>'.";
  it now throws a caller-facing `TimeoutException` whose message is the reason. None of those
  callers read `Success`, so a *returned* failure would have been parsed as data — `audio`
  answering "volume 50" from a partial stdout. Only the two-argument overload, the `powershell`
  tool's, folds a timeout into `TimedOut: true` instead.
- **`file_manage` no longer overwrites or deletes a tree silently** (parity C-1 — two contract
  breaks, deliberate). `copy` and `move` used to pass `overwrite: true` to the framework, so an
  existing destination was replaced without a word; they now refuse it with an
  `InvalidOperationException` naming the flag. `delete` used to remove a whole directory tree
  behind nothing but `confirm: true`; it now refuses a **non-empty** directory naming
  `recursive` (a file and an empty directory still need only `confirm`). **Migration:** pass
  `overwrite: true` to keep the old copy/move behaviour and `recursive: true` to keep the old
  delete behaviour — both are opt-in on purpose, since the old defaults destroyed data the
  caller had not named. With `overwrite: true` a copy **replaces** the destination — an
  existing directory tree (stale files included) or a file where a directory is going is
  cleared first, never merged into. A copy or move whose destination is the source, is inside
  the source (a copy into its own subtree recursed into what it had just created until the path
  length ran out, and with `overwrite: true` first deleted part of the source), or contains the
  source is refused before anything is touched, whatever `overwrite` says. A cancelled copy of a
  tree stops before the next directory as well as before the next file (a tree of empty
  directories had nothing to stop it at).
- **`file_manage(list)` returns a bounded listing object, not a string array of paths** (parity
  C-1 — a contract break). It used to answer with `Directory.EnumerateFileSystemEntries`' bare
  paths, so telling a directory from a file, or reading a size, cost a `file_info` call per
  entry. It now returns
  `{Entries: [{Path, Name, IsDirectory, Size, Modified, Hidden, IsLink}], Truncated, MaxEntries}`
  (`Size` is `0` for a directory, `Modified` is UTC, `IsLink` marks a junction or symlink).
  Hidden and system entries, which used to be listed, are now skipped unless
  `include_hidden: true`. The walk is **always** bounded: it stops at `max_entries` (default
  1000, range 1–100000 — `0` is not "all" here) and says `Truncated: true`, because a recursive
  listing of `C:\Windows` was 160 000 entries and 42 MB in one response; and it never descends
  into a reparse point, so a self-referencing junction is one `IsLink: true` row instead of a
  walk into the path limit. A `pattern` holding a path separator is refused naming the parameter
  (a pattern is a name glob; `recursive: true` is how you descend), and `list` of a file says so
  rather than answering empty. **Migration:** read `Entries[].Path` instead of treating the
  element as a string, check `Truncated` (or raise `max_entries`) where a full listing matters,
  and pass `include_hidden: true` where the old full listing mattered (`startup_report`'s
  startup-folder scan does exactly that internally, and is not capped, so its reach is
  unchanged).
- **Every file tool refuses a relative path, and the `\\?\` / `\\.\` device forms** (parity C-1 —
  a contract break). `file_read`, `file_write`, `file_manage` (`src` **and** `dst`),
  `file_search`'s `root`, `file_hash`, `file_info`, `file_streams` and `archive` (`src` **and**
  `dst`) now require a fully qualified path (`Path.IsPathFullyQualified`; UNC passes) and raise
  an `ArgumentException` naming the parameter **before** the service is touched. A relative path
  used to resolve against the server's working directory — whatever the MCP host happened to set,
  and nothing the caller could see. The extended-length and device spellings are refused by the
  same check, in every mix of separators (`\\?\`, `\\.\`, `//?/`, `//./`, `\\?/`, `/\?\`),
  because they bypass the normalisation the containment guards rely on and a model has no reason
  to send them. **Migration:** pass plain absolute paths.
- **`file_manage`'s `copy` and `move` verify before they touch anything, and put back what they
  replaced** (parity C-1, review round 4 — the semantics of `overwrite`). A missing or locked
  source is reported before the destination is disturbed. An existing destination is no longer
  deleted up front: with `overwrite: true` it is moved aside to a sibling
  `<dst>.replaced.<guid>`, the copy or move runs, and only on success is the aside removed; any
  failure or cancellation removes the partial destination, puts the previous content back and
  removes the parent directories the call had created. The put-back is best effort — when it
  cannot happen the error says so and names where the previous content is sitting — and a
  cross-volume move commits the destination *before* it removes the source, so a file is never in
  zero places. A volume or share root is refused as source, destination or delete target; a
  directory copy or a cross-volume move neither descends into nor recreates a junction or symlink
  (they are dropped, and the description says so); `delete` of a junction removes the link only,
  never enumerates through it, needs no `recursive`, and answers `removed link '<path>' (its
  target is untouched)`; `delete` of a path that is not there answers `nothing at '<path>' to
  delete` rather than claiming a delete; `delete` clears a read-only bit rather than treating it
  as a second gate; and containment is judged on both the literal path and the volume's own
  spelling of it (`GetFinalPathNameByHandle`), so a `subst` letter, a mapped drive or a junction
  cannot smuggle a destination into its own source. A failed `file_write` no longer leaves its
  `.tmp.` file beside the target, and a failed `archive(zip)` no longer leaves a valid **empty**
  archive where the caller's previous one was — the zip is built beside the target and moved into
  place only when it is complete. New internal `Services/PathCanonical.cs` behind the
  `IFinalPathNative` / `Win32FinalPathNative` seam (`CreateFile` and `GetFinalPathNameByHandle`
  added to `NativeMethods.txt`) and one `DeleteTree` remover shared by `delete recursive:true`,
  the aside and the cross-volume source removal. No new service — the count stays 39. Design
  note: `docs/design/C-1-file-flags.md` (rounds 4 … 4f).
- **A tool's own answer reaches the client instead of "An error occurred invoking …"** (parity
  C-1, review round 4). `ToolErrors.IsCallerFacing` — the host's call-tool filter in
  `Hosting/WindowsMcpHost.cs` — gains `KeyNotFoundException`, `IOException` (so
  `FileNotFoundException`, `DirectoryNotFoundException`, `PathTooLongException` and "the file is
  in use"), `UnauthorizedAccessException` and `TimeoutException` beside `ArgumentException` and
  `InvalidOperationException`. These are deliberate answers, not faults: the window matcher's
  "Open windows: …", an element id no longer in the cache, `registry_get` on a missing key, an
  app the catalog does not know, `scheduled_task`, `watch`, `wait_for`'s old overload and every
  file tool's not-found / access-denied / path-too-long message with the path in it — all masked
  until now. `NullReferenceException`, `IndexOutOfRangeException`, `OutOfMemoryException`,
  `COMException` and `Win32Exception` keep the SDK's masking. Every surfaced message is capped at
  2 000 characters by `ToolErrors.MessageFor`, cut on a whole character (never a lone surrogate)
  and marked ` [cut: 2000-character limit] …`, so a path-too-long answer cannot be a 32 KB
  response. **Migration:** none — a failing call still comes back as an MCP error result; its
  text is now the message the server meant to send.
- **`process(kill, graceful: true)` reports a late exit as graceful, and a cancelled wait kills
  nothing** (parity C-3, review round 4). The grace timeout is re-checked against `HasExited`, so
  a process that answered `WM_CLOSE` just as the clock ran out is reported
  `exitedGracefully: true, forced: false` — it closed, late — instead of being counted as forced.
  Cancelling the request during the wait rethrows without killing: the process has been asked to
  close and is left to answer. `grace_ms` is per process on a name kill, and the tool description
  now says both. `process(list)` refuses `includeLineage` **and** `groupByRoot` together naming
  both rather than silently picking one, and `process(orphans)` refuses each of them by name — it
  already carries the lineage columns.
- **`file_read` returns JSON when it is windowed, and `file_write` is no longer `idempotentHint`**
  (parity C-1). A call with `offset_lines` and/or `limit_lines` answers with the
  `{path, totalLines, offset, returned, truncated, content}` object rather than raw text; a call
  with neither is byte-for-byte what it always was. `file_write`'s MCP annotation flips to
  `Idempotent = false` because `append: true` twice is not the same state twice — the C-7 table
  (`docs/design/C-7-tool-annotations.md`) and `ToolInventoryTests` carry the new answer.
  **Migration:** parse the JSON only on the calls you window; nothing else changes.
- **`process(kill)` by pid or name returns JSON, not a sentence** (parity C-3 — a contract
  break). The pid kill used to answer `killed pid N` (or `killed pid N (start-time verified)`)
  and the name kill `killed N process(es) named 'x'`, so a caller had to parse prose to learn
  which processes went and had no way to see *how* they went. Both now return
  `{killed:[{pid, name, graceful, exitedGracefully, forced, waitedMs}]}` — one row per process,
  the same shape whether one or five matched the name. The **tree** kill is unchanged and keeps
  its `killed N process(es) in tree of pid M` text. **Migration:** read `killed[].pid` (and
  `killed.length` for a name kill) instead of matching the sentence; `exitedGracefully` /
  `forced` are the new evidence of *how* it went.
- **`registry_get` without a `value_name` returns the key, not a comma-joined string of value
  names** (parity C-2, roadmap R6 — a contract break). The tool used to answer with the value
  names joined by commas and nothing about sub-keys, so walking a key meant falling back to
  `powershell`. It now returns `{Path, Values: [{Path, Name, Data, Kind}], SubKeys: [...]}` —
  every value with its data and kind, plus the immediate sub-key names — through the new
  `IRegistryService.ListAsync`, which reuses the two enumerators `RegistryService` already had.
  An empty `path` lists the hive root, and a missing key is an error naming the path rather than
  an empty result. **Migration:** parse the JSON — the names are `Values[].Name` (with
  `Values[].Data` and `Values[].Kind` alongside), not a `split(",")` of the response. A call
  *with* `value_name` is unchanged.
- **`notification` returns a result object instead of the string `"notification shown"`** (parity
  C-4). It now answers `{shown, appId, registered, note?}`: `registered` is whether the platform
  knows the AppUserModelId (a packaged AUMID, or one with an `AppUserModelId\<id>` registration
  under HKCU/HKLM) and `note` explains a dropped toast. **Migration:** read `shown` instead of
  matching the sentence — the old string was returned whether or not Windows had actually shown
  anything, which is the bug this fixes.
- **`notification` no longer shells out to PowerShell.** The toast is raised in-process through
  WinRT (`Services/WinRtToastSink.cs` behind the internal `IToastSink` seam), so a toast no longer
  pays a `powershell.exe` cold start or takes the PowerShell serialization gate behind whatever
  else is queued. `NotificationService` accordingly depends on `IRegistryService` (for the
  AppUserModelId registration) instead of `IPowerShellService`; the DI registration line is
  unchanged and the service count stays 39. One retry, 1 s apart, covers the documented lag
  between writing the registration and the platform honouring it (`COMException 0x80070490`).
- **`wait_for` always returns a result object; a timeout is no longer the string `"null"`**
  (parity B-6, roadmap C4 — the one contract break in section B). The tool used to answer with a
  serialized `ElementInfo`, the literal string `"null"` when nothing turned up, or an error when
  every poll had failed, so a caller had to string-compare to tell "not there" from "found". It
  now always returns `{Satisfied, Condition, ElapsedMs, Attempts, Detail, Element?}`: a timeout is
  `Satisfied: false` carrying the last poll's `Detail` (what was seen instead), and the
  every-poll-failed case is that same result with `Detail` of `every poll failed: <last message>`
  rather than a `TimeoutException`. **Migration:** read `Satisfied` instead of comparing the
  response to `"null"`, and take the element from `Element` (omitted when there is none). The old
  `IUIAutomationService.WaitForAsync(text, timeoutMs, …)` overload and its `PollAsync` loop are
  untouched for direct callers; no tool uses them any more.
- **`launch` returns a result object instead of `"launched (pid=N)"`.** The tool used to answer
  with that sentence and nothing else, so a caller could not tell which app the name had resolved
  to or whether a window ever appeared. It now returns `{MatchedName, Kind, Score, Pid, Hwnd,
  Title, WindowDetected, Strategy}` (parity B-8). **Migration:** parse the JSON — the pid is
  `Pid`, and "did it actually open" is `WindowDetected`, not the presence of the word "launched".
  The two new parameters (`wait_for_window`, `timeout_ms`) are optional and default to the waiting
  behaviour, so `launch("notepad")` still works; `IWindowService.LaunchAsync(app)` → `int` is
  untouched for callers that only want a pid.
- **`scroll` takes the direction first, and coordinates on `click`/`scroll`/`drag` are optional.**
  `scroll(x, y, direction, amount)` became `scroll(direction, amount?, x?, y?, element_id?,
  shift_wheel?)`: the only required argument is `direction`, and a call with no coordinates
  scrolls under the cursor instead of failing. **Migration:** this is a positional break — a
  caller that passed `scroll(100, 200, "down")` positionally must now pass
  `scroll("down", 3, 100, 200)` or name the arguments; callers that already used names only lose
  the requirement. `click` and `drag` keep their parameter order (`x`, `y` and `from_x`, `from_y`
  are still first), but their coordinates are optional too — a `click` may give `element_id`
  instead, and a `drag` may give `element_id`/`from_element_id` or let the origin default to the
  cursor. Half a coordinate pair, or coordinates *and* an element id, is an `ArgumentException`
  naming the parameters. The advertised schemas change accordingly and are pinned over the wire
  in `HttpTransportTests`.
- **`click`, `type`, `scroll` and `drag` return one result object each, not a sentence or a
  serialized DTO.** `scroll` used to answer `"scrolled down by 3 at (100,200)"`; `click`, `drag`
  and `type` serialized their `ClickResult` / `DragResult` / `TypeResult` DTOs, so the fields were
  `X`, `Y`, `Button` (the enum's *number*), `Clicks`, `CharsTyped`. They now return camelCase
  objects that carry the resolved target as well as the request: `click` →
  `{action: click|hover, x, y, button, clicks, elementId?, name?}`, `type` →
  `{typed, method, clipboardRestored?, x?, y?, elementId?, name?}`, `scroll` →
  `{direction, amount, x, y, target: point|element|cursor, shiftWheel, elementId?, name?}`,
  `drag` → `{fromX, fromY, toX, toY, button, durationMs, steps, fromTarget: point|element|cursor,
  elementId?, name?}`. **Migration:** read the lower-case field names; `button` is now the name
  (`"left"`), not the enum's ordinal, and the fields that describe an element target are `null`
  when the call used coordinates or the cursor.
- **`type(text)` and `interact_element(type)` press Enter for a newline instead of typing it
  raw.** Every text entry now goes through `TypePlanner`, so a `\n`, `\r` or `\r\n` in the text
  becomes an Enter key press and a `\t` becomes Tab, in place of one `TextEntry` call with the
  raw string — which some controls swallowed or turned into a literal glyph. This applies to
  everything that types: `type`, D-2's `interact_element(type)` keyboard fallback, and
  `file_dialog`. The same path also means text of 200 characters or more with no other control
  characters is pasted through the clipboard rather than typed. **Migration:** a caller that
  relied on a newline landing as a character in a single-line control must strip it first; a
  caller that wanted a new line gets one.

- **Test infrastructure: the Notepad fixture leaves nothing behind.** Modern Notepad is one
  process hosting every window and persists every open tab as session state, so a fixture that
  killed the process it launched left its window behind and the tab came back on the next
  launch. `NotepadFixture` now tracks the window (or tab) it opened, closes exactly that on
  dispose, answers the "Save changes?" flyout with *Don't save*, restores the tab-state folder
  to what it was, and terminates Notepad only when it was the sole owner; 53 unit tests pin its
  decisions and a desktop self-test proves the clean-up. The pointer-moving and foreground-
  changing desktop classes share one xUnit collection.

- **`switch_to_window` and `focus` return a result object, not a sentence.** They used to answer
  `"switched to 'X'"` / `"window 'X' not found"`; they now return
  `{Window, MatchStrategy (exact|substring|fuzzy|hwnd), Score, Restored, Strategy, Success}`,
  where `Strategy` names the rung of the foreground ladder that worked (`null` when none did) and
  `Success` is re-read from `GetForegroundWindow`. **Migration:** parse the JSON and read
  `Success`/`Strategy` instead of matching on the string; a title that matches nothing is now an
  error listing the open windows rather than a "not found" sentence. `title` is optional (give it
  or `hwnd`), so callers that passed it positionally are unaffected.
  `IWindowService.SwitchToAsync(title)` → `bool` was replaced by
  `BringToFrontAsync(title?, hwnd?)` → `ForegroundResult`.
- **`window(action: minimize|maximize|restore|close)` throws when nothing matches.** The title is
  now matched exact → substring → fuzzy (and `hwnd` is accepted, winning over a title) instead of
  `FindWindow`'s exact-only lookup, and a title that names no open window is a
  `KeyNotFoundException` listing them — where it used to return `{"Success": false}` after doing
  nothing. **Migration:** a caller that tested `Success` for "no such window" must catch the error
  instead; `Success` is now always `true` on the acting path. The result also carries the matched
  window's `Title`, `Hwnd`, `MatchStrategy` and `Score`, and the four actions accept `hwnd` in
  place of `title`. `IWindowService.ExecuteAsync` gained an `hwnd` parameter.
- **`start_process` returns JSON.** It used to answer `"started (pid=1234)"`; it now returns
  `{pid, executable, args, cwd}` so a caller can confirm which executable, argv and working
  directory were actually used. **Migration:** parse the JSON and read `pid` instead of scraping
  the sentence.
- **`get_state` is bounded by the element budget** (parity A-4). Same foreground root and
  three-level shape; when `--max-tree-elements` stops the walk the root carries `Truncated: true`
  and `ElementLimit` (absent otherwise, so the JSON is unchanged until a walk is cut short).
  `ElementInfo` gained a trailing `Scroll` (populated by `snapshot`; `find_element` not yet). The
  tool's `[Description]` now states the depth and the budget and points at `snapshot` for a
  whole-desktop read with centre coordinates and action hints (roadmap C4).
- **`screenshot` returns the image as MCP image content** (parity A-7). The tool result is now a
  text block with one JSON metadata object (`{width, height, format, coordinateSpace:
  "virtual-desktop", region, path?}` — A-8/A-9/A-11 below add `displays`, `originalWidth`/
  `originalHeight`, `cursor` and the scale fields) followed by an `ImageContentBlock`, so Claude
  Code and Claude Desktop render the capture inline and the model can look at it in the same call.
  **Migration:** `output` defaults to `inline` (was `file`; ask for `file` explicitly to get a
  path, `base64` is an alias of `inline` for one release), `format` defaults to `auto` (jpeg for
  inline, png for file; was always png), and the old `data_base64` JSON key is gone. Arguments
  are validated before any capture; unknown `output`/`format` name the accepted values. The
  reported format and mime type describe what was encoded, never the request. Design note:
  `docs/design/A-7-screenshot-image-content.md`.

- **`screenshot` downscales to 1920×1080 by default and reports the coordinate scale** (parity
  A-9). A 4K capture was a ~10 MB PNG; it is now fitted inside `max_width` × `max_height`
  (default 1920×1080, 0 = no limit) with a Mitchell cubic resample, then shrunk further by the
  call's `scale` (0–1] and the server's new `--screenshot-scale` / `WINDOWSMCP_SCREENSHOT_SCALE`
  (0.1–1.0, honoured by both transports). Metadata always carries
  `originalWidth`/`originalHeight`; when anything was scaled it adds `coordinateScale` and a
  `note` telling the model to multiply image pixel coordinates by that factor before `click`/
  `drag`/`scroll`, and omits both otherwise. New `quality` argument (1–100, default 90) for JPEG.
  `ocr` is unaffected — it always captures at full resolution. `IScreenshotService.CaptureAsync`
  now takes a `CaptureOptions` record and `ScreenshotResult` gained the original size and scale.
  Design note: `docs/design/A-9-screenshot-downscale.md`.

- **`screenshot` and `ocr` capture any monitor, and regions are validated, not clipped** (parity
  A-8). New `display` argument: `all`, or comma-separated zero-based indices in `multi_monitor`
  order (`1`, `0,2`); several are captured as their union, a monitor left of or above the
  primary keeps its negative origin. The default stays the primary display (cheaper than
  upstream's all-displays default; one flag flips it later). `region` (`x,y,w,h`, virtual-desktop
  pixels) wins over `display` and is now checked against the virtual screen — outside it is an
  error naming the bounds, where it used to be silently clipped or fail deep in GDI; an invalid
  `display` errors even when `region` wins. Metadata now **always** carries `region` (the rect
  actually captured — image (0,0) is its origin) and `displays` (every monitor's bounds), adds
  `selectedDisplays` when `display` picked the rect, and the `note` gains the offset form
  (`virtual-desktop x = 1920 + imageX × 2, …`) for any capture that does not start at (0,0).
  One shared parser (`RegionMath`) for both tools; a region with a non-integer part or a
  non-positive size is a named `ArgumentException` rather than a `FormatException`.
  `multi_monitor` indices are now the position in the returned list, so a monitor whose info
  query fails cannot leave a gap between what `display` selects and what the metadata reports.
  Design note: `docs/design/A-8-multi-display-capture.md`.

- **`screenshot` reports the mouse pointer and paints it onto the capture** (parity A-11). Metadata
  always carries `cursor {x, y, monitorIndex}` (virtual-desktop pixels; `-1` = on no monitor),
  drawn or not. New `include_cursor` argument (default true): the real cursor image is composited
  through `DrawIconEx` at its hotspot, and when the cursor is hidden or the composite fails a
  two-tone ring (white outside, black inside) is drawn instead; `cursorDrawn` says `icon` or `ring`
  and is absent when the pointer was outside the captured rect. The overlay goes onto the
  full-resolution bitmap before the A-9 downscale, so it shrinks with the picture. The tool reads
  the position once and hands it to the capture, so the numbers and the mark cannot disagree.
  `ocr` never draws it. New `IInputService.GetCursorPositionAsync` / `CursorPosition`; `CaptureOptions`
  gained `IncludeCursor` and `Cursor`, `ScreenshotResult` gained `CursorDrawn`. Design note:
  `docs/design/A-11-cursor.md`.
- **UI text is sanitised before it reaches the model** (parity A-13). Element names and values,
  `get_text`, `get_table` headers and cells, and the `assert_element state=value` observation now
  go through one `UiText.Sanitize`: Private Use Area glyphs (VS Code's codicons, icon fonts — BMP
  and both supplementary planes) are stripped, lone UTF-16 surrogates become U+FFFD explicitly
  (measured on .NET 10: `System.Text.Json` was already rewriting them to U+FFFD *silently*, so the
  model got a value that differed from the UI with nothing saying so), C0/C1 controls other than
  tab/LF/CR are dropped, and the result is trimmed. Valid emoji, ZWJ sequences, combining marks
  and RTL text are untouched. `assert_element state=value` compares the sanitised value, so a
  value read back from `find_element`/`get_text` matches. `get_table` columns without a header
  element are now `""` instead of null. Design note: `docs/design/A-13-unicode-hygiene.md`.

### Fixed

- **Refusals the caller could not read now arrive as text** (parity D-10, and the family the review
  found around it). `ToolErrors.IsCallerFacing` admits six exception types; a refusal raised as any
  other reached the client as `An error occurred invoking '<tool>'.`. Fixed at each call site:
  `interact_element` on a pattern the control lacks is an `InvalidOperationException` naming the
  pattern, the control type and its name (`TogglePattern not supported on Edit 'Query'.`), and
  `get_table`'s missing GridPattern says the same; a middle-button `drag` is an `ArgumentException`
  naming `button`. What the target itself throws on `interact_element`, `get_table` and `get_text`
  (and `IUIAutomationService.FocusAsync`, which no tool calls yet) — FlaUI's exception types and
  the UIA HRESULTs — is translated by the new `Services/UiTree/UiRefusals.cs` inside
  `UIAutomationService.Refusing`: a disabled control, an element that is no longer available
  (naming the id and "take a new snapshot"; was DEF-048), a child item or part gone while its
  container is alive, an unsupported pattern or property; anything unrecognised stays masked. An
  acting verb (all six `interact_element` actions, and `FocusAsync`) on a control that reports
  disabled is refused **before** anything is touched, so the foreground no longer moves on the way
  to the refusal, and `interact_element` validates the action name and `type`'s value before it
  looks at the element. `get_state`, a foreground `find_element` and a foreground `snapshot` fall
  back to the desktop when nothing is in the foreground (`Services/UiTree/UiRoots.cs`) —
  `FocusedElement()` throws there rather than returning null, so the documented fallback was
  unreachable. `start_process` and `launch` (`Services/ProcessStartErrors.cs`): a command that does
  not exist is a `FileNotFoundException` naming the file actually tried, with a quote-it /
  `args_json` hint when an unquoted command line was split at a space; access denied is an
  `UnauthorizedAccessException`, any other OS error an `InvalidOperationException`, and a failed
  packaged activation names the AUMID and the HRESULT in hex. Registry
  (`Services/RegistryErrors.cs`): access denied is one `UnauthorizedAccessException` naming the
  hive and key on every operation (`OpenSubKey` raised a masked `SecurityException`), a denied
  descendant is named by its own path, and a tree delete denied part-way says sub-keys may already
  be gone; `registry_delete`'s description no longer says the OS error is passed through.
  `power_action`: an OS refusal is an `InvalidOperationException` with the action and Windows'
  reason (`PowerService` over the new internal `IPowerNative` / `Win32PowerNative` seam, so
  `PowerServiceTests` reach the refusal without a power action). A new IL scan in `ToolErrorsTests`
  fails on any exception the server constructs that is neither caller-facing nor on a site-keyed,
  reasoned allow-list, and on HRESULT throws outside a reasoned list. Neighbour gaps the review
  found are logged as parity items D-12 … D-18, not fixed here.
- **The element-id cache is bounded** (parity D-11). Every `find_element` match, `get_state` node,
  `get_element` and element-targeted input call added an id that lived until the process exited —
  on a long-running HTTP host, an unbounded set of live COM references. The new
  `Services/UiTree/ElementCache.cs` keeps non-snapshot ids in a least-recently-used set of
  `max(UiTreeOptions.MaxCachedElements, 2 × MaxElements)` entries (`MaxCachedElements` defaults to
  5 000; no option sets it), while snapshot ids stay exempt and are dropped by the next snapshot as
  before. `get_element` no longer mints a second id (see *Changed*), and `wait_for`'s element
  conditions hand back each poll's unused ids — the final poll of a timed-out wait is kept, so an
  id named in `Detail` resolves. The removed `UIAutomationService.cs` header TODO tracked this
  since v0.2.0. Tests: `ElementCacheTests`, `WaitLoopReleaseTests`, `UIAutomationServiceTests`.
- **The overlay window class's procedure lives as long as the class** (found while extracting
  `LayeredWindow` for S-11). `FlashOverlay` registered its process-lifetime window class with a
  per-instance window-procedure delegate; a second instance reused the class, so once the first
  instance was collected, Windows would have dispatched the second one's messages through a freed
  delegate. `LayeredWindow` now holds one static procedure for every class it registers. Latent in
  production, which creates one `FlashOverlay` per process.
- **`launch("vs code")` opens Visual Studio Code, not "QR Code Scanner"; abbreviations match.**
  The B-8 fuzzy stage scored a name only by `max(PartialRatio, TokenSetRatio)`, which gives
  `vs code` 73 against both Visual Studio Code and "QR Code Scanner" (they share only "code"), and
  the tie went to the shorter name. `AppCatalog.Match` now also asks the new
  `FuzzyMatch.AbbreviationUnread` whether the request **abbreviates** the name: its words are read
  in order from the name's first word with no name word skipped, each request word of 2+
  characters as a word prefix or as the initials of the next two or more name words; a request
  read entirely as initials must reach the last word, and the best reading is the one with the
  fewest name words left unread. Such a name scores 95 less one per unread word when that beats
  the scorers, so `vs code` gives Visual Studio Code 95 and its Insiders build 94, `wt` reads
  Windows Terminal and `ssms` SQL Server Management Studio, while `mps` does not read "VLC media
  player skinned", `uy` does not read "Uninstall YubiKey Manager" and `7z` does not read "7-Zip
  File Manager". The search is memoized, so a crafted name cannot make it exponential. Exact and
  prefix matching are unchanged, and 95 stays below a scorer's 100.
- **An ambiguous abbreviation is refused instead of guessed.** When two or more apps tie at the
  top fuzzy score and at least one of them got it only through an abbreviation, `launch` starts
  nothing and returns the `ArgumentException` naming them: `'wt' could mean more than one app:
  'Windows Terminal' (95), 'Windows Tools' (95). Use more of the name.` It is deliberately not a
  `KeyNotFoundException`, so `AppCatalogService.ResolveAsync` does not refresh the catalog for it
  (a fresh catalog would be just as ambiguous). Ties the scorers alone decide still go to the
  shortest name. `IAppCatalogService.ResolveAsync` and `AppMatch` document the refusal.
- **One unreadable Start Menu folder no longer empties the shortcut half of the catalog.**
  `Directory.EnumerateFiles` is lazy, so a subfolder the user could not read threw from inside the
  loop, outside its `try`, and the source's catch then dropped every shortcut from **both** roots.
  `AppCatalogService.ScanStartMenu(roots)` now walks with `EnumerationOptions`
  (`IgnoreInaccessible`, `AttributesToSkip = 0` so Hidden and System folders such as
  Administrative Tools are still listed, as before, `*.lnk` ignoring case) and stops
  `MaxStartMenuDepth` = 8 folders below a root, so a junction back to a root cannot loop; a
  per-root `try` keeps whatever a broken walk had already read.
- **Tests / CI: the app-catalog Integration tests no longer assume this machine's apps.** CI on
  `main` was red in three `AppCatalogServiceIntegrationTests`: the GitHub Windows runner has
  Calculator and Notepad as Start Menu shortcuts, not packaged apps, and has no VS Code. The
  Calculator/Notepad test now accepts either form (an existing `.lnk`, or an AUMID), and the
  VS Code test is a new `[StartMenuShortcutFact("Visual Studio Code")]` that skips, naming the
  shortcut and both roots, when no such shortcut exists — it decides through the same
  `ScanStartMenu`. New `[JunctionFact]` fixture and `AppCatalogServiceStartMenuScanTests`
  (recursion, case, Hidden/System, the depth cap, and deny-ACL and junction Integration tests).
  No new tool or service: still 69 tools and 40 services. Design note:
  `docs/design/B-8-launch-catalog.md`.
- **HTTP-mode log lines carry a local timestamp, and the plain-HTTP warning is one of them.**
  Over `--transport http` nothing prefixes the server's stderr — a console window, a redirected
  file, a scheduled task's log — so no entry could be placed in time. Each one now starts with
  the local time to the millisecond and its UTC offset,
  `2026-10-20 13:06:34.223 -04:00 info: Microsoft.Hosting.Lifetime[14]` (the time separator and
  calendar follow the machine's culture, as in any ASP.NET Core app). These are defaults, not
  overrides: `Logging:Console` configuration wins wherever it is set, so
  `Logging__Console__FormatterName=json`, `Logging__Console__FormatterOptions__TimestampFormat`
  and `Logging__Console__FormatterOptions__UseUtcTimestamp=true` are honoured (every level still
  goes to stderr whatever the configuration says). The default stamp belongs to the simple
  formatter only: `json` carries no timestamp unless `FormatterOptions:TimestampFormat` is set as
  well. Behaviour change: in HTTP mode the deprecated flat `Logging:Console:Format`,
  `TimestampFormat`, `IncludeScopes`, `DisableColors` and `UseUtcTimestamp` settings are no longer
  honoured; use `Logging:Console:FormatterName` and `FormatterOptions:*` instead. stdio is
  unchanged and unstamped: the MCP host already timestamps every stderr line it captures. The
  off-loopback plain-HTTP warning, which `Program.RunHttpAsync` wrote as a raw
  `Windows-mcp: WARNING: listening on …` line, is now a `warn:` entry logged by
  `WindowsMcpHost.BuildHttpApp`, stamped like the rest and reading
  `Listening on <bind>:<port> over plain HTTP — …`. It cannot be silenced: when `Logging:LogLevel`
  filters would hide that entry, the old raw `Windows-mcp: WARNING: …` line is written instead.
  The other lines `Program` writes before any host exists — the environment-repair notice, option
  errors and the startup refusals (no API key off-loopback, an unusable `--cert-thumbprint`) —
  stay raw and unstamped. Code: `WindowsMcpHost.ConfigureStderrLogging` and `BuildHttpApp`; tests:
  `WindowsMcpHostTests` ("HTTP log timestamps"), `HttpHostLoggingIntegrationTests` (the real host,
  built but never started) and `ServerProcessLoggingIntegrationTests` (the built exe in both
  transports, read from its own stderr). README: "Run over HTTP/HTTPS (remote)" → **Logs.**
- **`find_element` and `wait_for` survive a stale element, and can be pinned to one window**
  (parity D-5). The find path walked the whole desktop in one `FindAllDescendants` and read
  `Name` / `ControlType` / bounds **unguarded**, so a single element that died between the walk and
  the read — a fading tooltip, a closing menu, a virtualised row — failed the entire call
  (`find_element(kind:"any")` errored on a busy desktop while `kind:"text"` worked). Every read is
  now guarded and each element is evaluated inside a catch: a dead element is skipped, never fatal.
  New `scope` parameter — **`foreground` is now the default** (behaviour change: the old implicit
  scope was the whole desktop, which is what made the tool slow and fragile), `window` with a
  `window` title pins a multi-step workflow to one window regardless of what steals focus (matched
  exact-then-substring on the window's UIA name; an unmatched title lists the open windows), and
  `desktop` restores the old whole-desktop walk. The kind filter is pushed into a UIA `OrCondition`
  so the provider marshals fewer elements. `wait_for` gained `kind`/`scope`/`window`, re-resolves
  the window on every poll (so it doubles as "wait for that app to open"), **retries a poll that
  throws** instead of ending the wait on the first transient failure, polls at least once
  (`timeout_ms: 0` now means "check now"), and throws rather than reporting a misleading `null`
  when *every* poll failed. The retry loop is a pure `PollAsync` with unit tests.
- **`find_element(kind:"interactive")` sees inputs, dropdowns, list rows, tabs and sliders**
  (parity D-6). The filter was four control types (Button, CheckBox, Hyperlink, MenuItem), so a text
  box was not "interactive" — the Claude Code prompt box is an `Edit` and could not be found that
  way. Replaced with upstream's `INTERACTIVE_CONTROL_TYPE_NAMES` plus `Document` (17 types, pinned
  by a test and spelled out in the tool description). Upstream's `TextBox` is omitted — no such UIA
  control type; it is `Edit`, already in the set.
- **`find_element` and `wait_for` no longer return off-screen elements by default** (parity D-7).
  Measured on a normal desktop, 18 of 21 `kind:"text"` hits were `IsOffscreen` — and because the
  20-result cap ran *before* any filtering the caller could do, on-screen matches were crowded out
  of the results entirely. Off-screen elements and empty bounds are now dropped **before** the cap;
  `wait_for` can no longer succeed on an element that has not been shown yet. `include_offscreen:true`
  restores the old behaviour. Upstream's `Edit` exception is kept: an `Edit` with real bounds stays
  in the results, because Chromium/WebView2 over-report it as off-screen while it is still the
  right target for `type`.
- **Background jobs decode their CLIXML stderr too** (D-9, the leftover D-8 deliberately left
  open). `JobService` pumps stderr into a bounded buffer as chars arrive, so it never had a whole
  document to decode and `job output` still returned raw `<Objs>` XML for a job that wrote a warning
  or re-enabled progress. Now decoded **once** when the job finishes — in the monitor, before the
  state flips to a terminal value, so no reader can see a finished job together with raw XML — via a
  new `BoundedTextBuffer.ReplaceAll`, which keeps `job status`'s `StderrChars` agreeing with what
  `job output` returns at no per-read cost. A *running* job's stderr is decoded on read, which works
  because `ClixmlStderr` now retries on everything up to the last `</Objs>` and drops a trailing
  partial document (that also helps the foreground tool when a child is killed mid-flush). A stream
  with no complete document still passes through raw, so this can never swallow non-CLIXML output.
- **`powershell` no longer ships the CLIXML progress stream to the model** (D-8). Windows PowerShell
  5.1 wraps every non-stdout stream in CLIXML when stderr is redirected, so each call carried
  ~0.6–3 KB of XML the model read and ignored (measured: a one-liner with one `Write-Progress`
  produced 596 characters of it). Two layers: `$ProgressPreference='SilentlyContinue'` in the
  invocation preamble kills it at the source — shared with background jobs, and a welcome speed-up
  for `Invoke-WebRequest`/`Invoke-RestMethod` — and the new `ClixmlStderr` decoder turns whatever
  remains into readable text (`WARNING: careful`), dropping progress records even when a script
  re-enables them. Non-CLIXML and unparseable CLIXML pass through raw. `Errors[]`, `Success` and
  the `PSResult` shape are unchanged; `ExtractErrors` now shares the same parser so the two cannot
  drift.
- **Stripped host environment no longer breaks child processes.** Claude Desktop (1.46) launches
  stdio servers with ~18 variables and `PATHEXT=.CPL`, so the `powershell` tool could not resolve
  `git`, `winget`, `dotnet`, `wsl` (no `.EXE` in the search list) and `docker mcp` panicked on a
  missing `ProgramData`. `Hosting/EnvironmentRepair` now runs first thing in `Main`: it fills
  missing variables from the registry (machine, then user; the two `Path` values joined) and
  well-known folder defaults, and corrects `PATHEXT` when it cannot resolve an `.exe`. Values the
  host did set are never overwritten (only `PATHEXT` is corrected). Repaired names are logged to
  stderr once at startup. Unit-tested in `EnvironmentRepairTests`.
- **`shortcut` / `key` accept letters, digits, punctuation, and bare keys** (parity D-1). The
  19-name key map is replaced by `Services/ShortcutParser`: named keys and aliases (`win`/`windows`/
  `super`, `return`, `del`, `printscreen`, `capslock`, numpad and media keys, `f1`–`f24`), single
  characters (`a`–`z` and `0`–`9` directly; anything else through `VkKeyScan`, which also supplies
  the layout's Shift state), `plus` for the `+` key, and single-part chords (`win` opens Start). So
  `ctrl+c`, `ctrl+shift+s`, `win+r`, `ctrl+1` and `key("a")` all work, and an unknown token is named
  in the error. The parser is pure and unit-tested (`ShortcutParserTests`).
- **`interact_element` implements every action it advertises** (parity D-2). `click` (Invoke →
  SelectionItem → Toggle → physical click at the element's centre), `invoke`, `toggle`, `select`
  (with a value: expand the container and pick the child item by name), `focus`, and `type` (a
  writable ValuePattern replaces the value, otherwise keyboard entry at the caret). Every branch
  either acts or throws `NotSupportedException` naming the pattern and control — the old code
  silently no-op'd when a pattern was missing and still answered `"interacted"` — and the tool now
  returns `{ElementId, Action, Method, Detail}` saying which pattern or fallback fired.
  `IInputService` is injected into `UIAutomationService`; input is sent off the UIA STA thread.
  Verified live against Notepad (`UIAutomationServiceTests`).
- **Cursor placement on secondary monitors** (parity D-3). `InputService` scaled coordinates by the
  primary monitor's size but sent them as virtual-desktop absolutes, so every `click`/`drag`/`hover`/
  `scroll` on a second monitor landed elsewhere and negative coordinates were unreachable. It now
  places the cursor with `SetCursorPos` (physical virtual-desktop pixels, origin = the primary's
  top-left — one coordinate space with UIA bounds and `multi_monitor` under Per-Monitor-V2) and
  reads it back: a point Windows clamped (off any monitor) throws instead of clicking somewhere
  else. The four mouse tools' descriptions now state the coordinate space. An integration test
  hovers to every monitor's centre and asserts the exact position.
- **`assert_element` implements every state it advertises** (parity D-4). `focused` (keyboard
  focus, or identity with UIA's focused element) and `value` (new `expected` parameter; exact match
  against the ValuePattern value, else the Name — the same read as `get_text`) now work instead of
  throwing `Unknown assertion state`; `exists` is a real liveness probe; `visible` also requires
  non-empty bounds. A FAIL names what was observed (`focus is on Button 'Save'`, `value is 'x'
  (from ValuePattern)`, `toggle state Off`, `element no longer available` once the window closed —
  a dead Win32 window's element answers reads with defaults rather than throwing, so the probe is
  the ProcessId). Optional properties a provider omits (modern Notepad's document has no
  `IsOffscreen`) no longer throw. `IUIAutomationService.AssertElementAsync` returns `AssertResult`.
  Verified live against Notepad and a killed Character Map window (`UIAutomationServiceTests`).
- **Doc drift after D-1/D-2/D-3 and the environment repair.** `docs/architecture/OVERVIEW.md` still
  described `interact_element` as "toggle, select, or invoke" and `key` as name-only, and
  `COMPONENTS.md`'s tool tables said the same; `COMPONENTS.md`'s `Program.cs` startup sequence
  predated `EnvironmentRepair`, its DTO table was missing `InteractResult`, and its NuGet table
  said "latest" for pinned versions while omitting `System.ServiceProcess.ServiceController`;
  `ARCHITECTURE.md` quoted the pre-D-3 `click` description and left `EnvironmentRepair` out of the
  `Hosting/` file list; `DATAFLOW.md`'s `AssertElement` flow still claimed `value` and `focused`
  work (they threw at the time — parity item D-4, fixed above). The item counts in `todo.md` and
  the parity checklist now say 51 / eight defects after D-4…D-8 were logged. Tool counts (64
  tools, 19 classes, 36 services) were already correct everywhere and are unchanged.

### Added

- **`--screenshot-scale <0.1-1.0>` / `WINDOWSMCP_SCREENSHOT_SCALE`** (parity A-9): a process-wide
  multiplier on every screenshot's own `scale`, for both transports — the cheap way to shrink
  what the model sees on a large desktop. Registered as a `ScreenshotOptions` singleton in
  `AddWindowsMcp`, which now takes the parsed `ServerOptions`. `BuildHttpApp` gained an optional
  `configureServices` seam (applied after `AddWindowsMcp`) so the transport tests can swap the
  capture service for a fake and prove the screenshot surface headless.
- **`test-agent` subagent (dev infrastructure).** `.claude/agents/test-agent.md` — an Opus-model
  Claude Code subagent that owns `tests/WindowsMcp.Tests` and enforces test-first work. Given the
  requirements for a change (the ask, a `docs/design` note, a parity-checklist item's "Tests." /
  "Done when." lines, a tool `[Description]`), it writes a requirement → test matrix and the
  failing xUnit tests **before** any production code exists, adding only the minimal
  `Abstractions` stubs (interface members, DTO records, `NotImplementedException` bodies) that keep
  the tree compiling. Run again after the implementation it measures coverlet coverage of the
  changed files, closes every open matrix row, proves the tests bite with a reverted one-line
  break, and flags mocked-only PowerShell/WMI paths that need an `Integration` sibling (the
  `disk_inspect` lesson in `todo.md`). It never writes feature logic and never commits.
  `CLAUDE.md` gains a "Test-first workflow" section and "Adding a tool" now starts with it.
- **`docs-agent` subagent (dev infrastructure).** `.claude/agents/docs-agent.md` — an Opus-model
  Claude Code subagent to run after any change. It derives the facts from the code (tool count and
  names, tool classes, services, interfaces, DTOs, CLI flags, version lockstep, publish flags) and
  brings README, `docs/architecture/*`, this changelog's `[Unreleased]`, the windows skill, the
  parity checklist, design notes, and tool `[Description]` strings back in line; `CLAUDE.md` drift
  is reported, not edited. It never changes behaviour and never commits.
- **Background PowerShell jobs** (63 → 64 tools). `powershell` gains `background: true`: instead
  of waiting, it starts the command as a job and returns `{Id, Pid, State}` immediately — the
  right pattern for silent installers, DISM, and anything longer than the foreground backstop.
  A new `job` tool manages them (`status` | `output` | `cancel` | `list`). Jobs run concurrently
  **outside** the foreground PowerShell serialization gate (`IJobService`/`JobService`): max 8
  running (new starts rejected when full), a 60-min per-job backstop that tree-kills runaways as
  `timedOut`, stdout/stderr captured into bounded ~1 MB/stream buffers (`BoundedTextBuffer`,
  oldest chars trimmed with counters surfaced), and the ~32 most recent finished jobs retained
  before eviction. Unknown job ids answer `found:false`/`cancelled:false` rather than erroring.
- **MCP progress heartbeats on foreground `powershell` calls.** While a command runs (or waits
  behind the serialization gate), the tool reports an `IProgress<ProgressNotificationValue>`
  heartbeat every 10s — spec-compliant clients that sent a `progressToken` reset their request
  timeout on progress, so long commands no longer die to client-side timeouts. No schema change
  (the SDK binds the parameter outside the tool's JSON schema) and a no-op sink when the client
  sends no token.
- `Services/PowerShellInvocation.cs` — the powershell.exe invocation builder (exe path, common
  flags, UTF-8 encoding preamble, `-EncodedCommand` with temp-`.ps1` fallback, stdin-redirect
  start-info) extracted from `PowerShellService` and shared with `JobService`, so background jobs
  spawn children exactly like foreground calls.
- **Streamable HTTP / HTTPS transport alongside stdio** (`--transport http`). The same exe now
  listens on a TCP port — `--port` (default 8765), `--bind` (default all interfaces) — so a
  client on another machine, e.g. Claude Code driving an RDP session host, can use it. MCP
  endpoint at `/mcp`, stateless Streamable HTTP, HTTP/1.1. `--cert-thumbprint <sha1>` resolves a
  certificate from `LocalMachine\My` then `CurrentUser\My` (private-key access is probed up
  front; the error names the key-ACL fix) and makes the port **HTTPS only**. `--api-key` /
  `WINDOWSMCP_API_KEY` gates every path with a constant-time bearer check; the server **refuses
  to start off-loopback without a key** (every tool would otherwise be open to the network) and
  warns when serving plain HTTP off-loopback. Every option has a `WINDOWSMCP_*` env fallback;
  `--help` prints them. No arguments still means plain stdio, so the plugin's `.mcp.json` is
  unchanged. README: "Run over HTTP/HTTPS (remote)".
- `src/WindowsMcp/Hosting/` — `ServerOptions` (pure, exhaustively unit-tested parser),
  `WindowsMcpHost` (the service/MCP wiring both transports share — registrations, identity,
  the `ToolErrors` filter, tool discovery — plus the HTTP host factory) and `CertificateLocator`.
  `HttpTransportTests` starts the real HTTP host in-process on an ephemeral port and drives it
  with the SDK client: handshake + tool list, 401 on every path without / with a wrong key, HTTPS
  with an ephemeral certificate (plaintext refused on that port), and a `confirm:true` refusal
  surfacing verbatim — the first test to exercise the DI wiring end to end.
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

### Changed

- **Single-file publish → `bundle/`; `dist/` retired (2026-09-04).** `scripts/build-release.ps1`
  now runs from any directory and publishes ONE file, `bundle/WindowsMcp.exe`. It adds
  `IncludeNativeLibrariesForSelfExtract=true` — the previously documented command left
  `libSkiaSharp.dll` and `aspnetcorev2_inprocess.dll` loose beside the exe, so the exe alone was
  not portable — and `DebugType=none`, then deletes the `libSkiaSharp.pdb` the SkiaSharp package
  emits regardless of publish flags. `bundle/` is gitignored (no binaries in the repo, by
  decision), and every `dist/` reference in README, `CLAUDE.md`, the version-bump skill,
  `todo.md`, and the parity checklist now points at `bundle/`.

- **Docs consolidated (2026-09-04).** Removed the executed `docs/superpowers/` specs and plans,
  the Python-era `docs/plans/` testing design, the generated dependency-graph / unused-analysis
  artifacts, and the Python-era `security-reviewer` agent. The feature backlog against the
  upstream Python server now lives in `docs/upstream-parity-checklist.md`. README, `CLAUDE.md`
  and `docs/architecture/*` re-aligned to the current surface (64 tools incl. `process_inspect`,
  `integrity`, `fs_changes`, `watch`; 36 service interfaces; `Hosting/` layout; SDK 2.2.0), the
  `CLAUDE.md` redeploy recipe rewritten around an explicit MCP registration instead of the
  upstream maintainer's marketplace clone, `version-bump` skill rewritten for the C# release
  flow, and the `windows` skill's obsolete temp-`.ps1` gotcha replaced (multi-line PowerShell has
  been passed whole via `-EncodedCommand` since 0.7.1).
- **Foreground PowerShell execution backstop 10 → 15 minutes** (`PowerShellService`). The
  backstop still arms only after the serialization gate is acquired, so it bounds execution,
  not queue-wait.
- `ModelContextProtocol` 1.0.x → 1.4.x, plus `ModelContextProtocol.AspNetCore` 1.4.x and a
  `Microsoft.AspNetCore.App` framework reference. The project SDK stays `Microsoft.NET.Sdk`
  (no `web.config` / `wwwroot` artefacts); the publish command is unchanged. The self-contained
  single-file exe grows from ~56 MB to ~66 MB (ASP.NET Core shared framework, compressed).
- Logging is stderr-only in both modes; HTTP mode additionally mutes the SDK's per-request
  server chatter to `Warning` (stateless mode builds a fresh `McpServer` per request).
- The `## [Unreleased]` section was buried between 0.6.1 and 0.6.0; moved to the top.
- **Target framework .NET 9 → .NET 10** (`net10.0-windows10.0.19041.0` in all three projects
  and in `Directory.Build.props`). `global.json` now pins a .NET 10 SDK (`10.0.100`,
  `rollForward: latestFeature`) — building requires the .NET 10 SDK; end users are unaffected
  (self-contained exe). README / CLAUDE.md / `docs/architecture` updated to say .NET 10.
- `Windows-mcp.sln` → `Windows-mcp.slnx` (the XML solution format). The old `.sln` is removed
  rather than kept alongside: two solution files in the root make a bare `dotnet build` fail with
  `MSB1011`. CI (`ci.yml`) and `ServerInfoTests.RepoRoot()` (which located the repo by the `.sln`)
  now look for the `.slnx`.

### Fixed

- **`powershell` no longer reports `Success:false` on healthy commands because of benign stderr
  noise.** Windows PowerShell 5.1 with redirected stderr wraps its error/warning/progress/verbose
  streams in CLIXML, so progress records ("Preparing modules for first use." on first-touch module
  import, or any `Write-Progress`) and warnings landed in `PSResult.Errors` and flipped `Success`.
  `PowerShellService` now parses CLIXML stderr and counts only genuine `<S S="Error">` records
  (decoded to plain text) against Success; `PSResult.Stderr` keeps the raw stream, and non-CLIXML
  or unparseable stderr falls back to the previous line-split behavior. Background jobs were never
  affected (their state derives from the exit code alone).
- Stale tool counts in docs: README / CLAUDE.md / `skills/windows/README.md` still said 60 tools
  (the surface has been 63 since 0.7.0; now 64), and `ARCHITECTURE.md` said 15 tool classes.
  `DATAFLOW.md`'s Powershell diagram described a long-removed implementation
  (`ValidateCommand` blocklist, `pwsh`, script piped via stdin) — rewritten to match the real
  gate → backstop → `-EncodedCommand`/temp-file flow. `COMPONENTS.md`'s `PowerShellService`
  section had the same rot and its service table was missing the integrity/USN/watch
  registrations.

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
