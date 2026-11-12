# Upstream Parity Checklist — CursorTouch/Windows-MCP → Windows-mcp (.NET)

**Baseline:** 2026-09-04
**Upstream:** [CursorTouch/Windows-MCP](https://github.com/CursorTouch/Windows-MCP) `main` = **v0.8.5**
(released 2026-08-01; Python ≥ 3.14, FastMCP 3, 20 tools).
**Ours:** `main` @ `8cb40b6` + the phase-2/3/4/5 branches, 69 tools, plugin `0.7.3`, `CHANGELOG.md
[Unreleased]` carries the section-A phase-1 work (A-7, A-8, A-9, A-11, A-13), phase 2's A-1,
phase 3's A-2/A-3/A-4, phase 4's A-6 and phase 5's A-14, A-12 (phase 1), A-10 and A-5 (phase 1),
then all of section B and all of section C (C-7, C-4, C-2, C-3, C-1, C-6, C-5), D-10/D-11, S-11,
E-1, E-2 and E-3.
SDK `ModelContextProtocol` 2.2.0.
**Status:** Living document — check items off as they ship.

This is the working list of everything upstream can do that this server cannot (plus thirty-two
defects: D-1…D-4 from the original comparison, D-5…D-9 added later under rule 4 and D-10/D-11
logged 2026-11-10 — **all eleven are now fixed** — then D-12…D-18, logged 2026-11-11 by the
D-10/D-11 review, D-19…D-22, logged 2026-11-12 during E-1, D-23…D-27, logged 2026-11-12
during E-2, and D-28…D-32, logged 2026-11-12 by the E-3 review, all still open; section A's phase 1 — A-7, A-9, A-8, A-11 and A-13 — is done, phase 2 shipped A-1,
phase 3 shipped A-2 with A-3 and A-4 inside it, phase 4 shipped A-6, and phase 5 shipped A-14,
A-12 phase 1, A-10 and A-5 phase 1 — so every section-A row is ticked, with only A-5's Firefox
phase left open and A-12's phase 2 deliberately not planned; section B is ticked too, and
C-5/C-6 close section C — section S, D-12…D-32 and section E are the work that is left; section E
is not upstream parity but an agent-efficiency backlog from a review of this server's own tools).
Each item carries enough context to write a design note and an implementation plan without re-reading
upstream from scratch: what upstream does and where, what we do today and where, an
implementation sketch, files to touch, tests, and a "done when" bar.

---

## How to use this file

1. Pick an item. Change its `- [ ] Not started` line to `- [ ] In progress — <link>` and link
   the design note or PR. Design notes go under `docs/design/`, named `<ID>-<slug>.md`
   (e.g. `docs/design/A-2-desktop-snapshot.md`).
2. When it ships: tick the box, add `— shipped in <version> (<PR>)`.
3. Every shipped item must also update: `CHANGELOG.md [Unreleased]`, the tool counts in
   `README.md` ("Tool reference") and `docs/architecture/*`, and `skills/windows/SKILL.md` if
   the playbook should steer Claude toward the new capability.
4. Do not silently expand an item's scope. If a plan discovers a neighbour gap, add a new item.

**Repo conventions that apply to every item** (see `CLAUDE.md`):
`TreatWarningsAsErrors=true`; DTOs are `record`s in `WindowsMcp.Abstractions/Models`; services are
`sealed` behind an `IXxxService`; tools stay thin (`async Task<string>` with JSON out, or
`Task<CallToolResult>` when the result carries an image — `screenshot`); new services
are registered as singletons in `Hosting/WindowsMcpHost.AddWindowsMcp` (tools auto-discover);
destructive actions require `confirm: true`; tests that need the interactive desktop carry
`[Trait("Category","UIAutomation")]`; the redeploy path is `scripts/build-release.ps1` → `bundle/WindowsMcp.exe` (gitignored; binaries are never committed).

**Reading the upstream reference code.** Paths below are relative to `src/windows_mcp/` in the
upstream repo. Fetch a copy into the scratchpad (never into this repo):

```bash
mkdir -p "$SCRATCH/upstream" && curl -sL https://github.com/CursorTouch/Windows-MCP/archive/refs/heads/main.tar.gz | tar xz -C "$SCRATCH/upstream"
```

**Legend.** Priority: **P1** core computer-use parity (agents fail or fall back to raw PowerShell
without it) · **P2** ergonomics / fewer round-trips · **P3** nice-to-have. Effort: **S** < 1 day ·
**M** 1–3 days · **L** multi-day / new subsystem. Line numbers are as of the baseline and will drift;
function names are the stable anchor.

---

## Board

| ID | Item | Pri | Effort | Depends on | Status |
|---|---|---|---|---|---|
| D-1 | `shortcut`/`key` reject letters, digits, bare keys | P1 | S | — | ☑ |
| D-2 | `interact_element` missing click / focus / type | P1 | S | D-3 | ☑ |
| D-3 | Cursor placement wrong on secondary monitors | P1 | S | — | ☑ |
| D-4 | `assert_element` advertises `value` / `focused` but implements neither | P2 | S | — | ☑ |
| D-5 | `find_element(kind=any)` / `wait_for` fail on the first stale element; whole-desktop walk | P1 | S | — | ☑ |
| D-6 | `find_element(kind=interactive)` excludes Edit, ComboBox, ListItem, TabItem, … | P2 | S | — | ☑ |
| D-7 | `find_element` / `wait_for` return off-screen elements by default | P2 | S | D-5 | ☑ |
| D-8 | `powershell` ships the CLIXML progress stream to the model on every call | P2 | S | — | ☑ |
| D-9 | `job output` still returns raw CLIXML on stderr | P3 | S | D-8 | ☑ |
| A-1 | Whole-desktop window inventory | P1 | M | — | ☑ |
| A-2 | Desktop-wide labeled interactive-element snapshot | P1 | L | A-1 | ☑ |
| A-3 | Scrollable regions with scroll percentages | P2 | S | A-2 | ☑ |
| A-4 | Element budget, truncation note, UIA caching | P1 | M | A-2 | ☑ |
| A-5 | Browser DOM mode (Chromium; Firefox IA2) | P2 | L | A-2 · [A-5](design/A-5-browser-dom-mode.md) | ☑ phase 1 (Chromium); Firefox open |
| A-6 | Annotated screenshot (boxes, labels, grid, cursor) | P2 | M | A-2, A-7 | ☑ |
| A-7 | Return screenshot as MCP image content | P1 | S | — | ☑ |
| A-8 | Multi-display / virtual-desktop-coordinate capture | P1 | M | — | ☑ |
| A-9 | Auto-downscale + scale env + coordinate-scale report | P1 | S | A-7 | ☑ |
| A-10 | Alternative capture backend (WGC / DXGI) | P3 | M–L | [A-10](design/A-10-capture-backend.md) | ☑ |
| A-11 | Cursor position in responses + drawn on capture | P2 | S | — | ☑ |
| A-12 | Virtual desktops (report; optional manage) | P3 | L | A-1 | ☑ (phase 1) |
| A-13 | Unicode hygiene (PUA strip, surrogate repair) | P2 | S | — | ☑ |
| A-14 | Post-capture flash overlay + snapshot profiling | P3 | M | — | ☑ |
| B-1 | `type`: target, clear, caret, press_enter, paste path | P1 | M | D-2 · [B-1](design/B-1-type.md) | ☑ |
| B-2 | `drag`: duration / intermediate motion / from-cursor | P2 | S | D-3 · [B-2](design/B-2-drag.md) | ☑ |
| B-3 | `scroll` at current cursor or element | P2 | S | [B-3](design/B-3-scroll.md) | ☑ |
| B-4 | `click` by element id; `clicks=0` hover | P2 | S | D-2 · [B-4](design/B-4-click-by-element.md) | ☑ |
| B-5 | Plain `wait` tool | P1 | S | [B-5](design/B-5-wait.md) | ☑ |
| B-6 | `wait_for` conditions + window filter | P2 | M | A-1, A-2 · [B-6](design/B-6-wait-for-conditions.md) | ☑ |
| B-7 | `multi_select` / `multi_edit` batch tools | P2 | S–M | B-1 · [B-7](design/B-7-batch-tools.md) | ☑ |
| B-8 | Launch by Start Menu name (fuzzy) + wait for window | P1 | M | A-1 · [B-8](design/B-8-launch-catalog.md) | ☑ |
| B-9 | Window resize / move | P2 | S | [B-9](design/B-9-window-bounds.md) | ☑ |
| B-10 | Fuzzy window match + robust bring-to-foreground | P1 | M | A-1 · [B-10](design/B-10-window-matching.md) | ☑ |
| B-11 | `start_process` with argv list + cwd | P2 | S | [B-11](design/B-11-start-process-argv.md) | ☑ |
| B-12 | `multi_monitor`: work area, orientation, DPI, scale | P2 | S | [B-12](design/B-12-monitor-detail.md) | ☑ |
| C-1 | File tools: offset/limit, append, overwrite, recursive, pattern | P2 | M | [C-1](design/C-1-file-flags.md) | ☑ |
| C-2 | Registry delete + subkey listing on the tool surface | P2 | S | [C-2](design/C-2-registry-delete.md) | ☑ |
| C-3 | Process list CPU %, sort, limit; graceful kill | P2 | M | [C-3](design/C-3-process-cpu-graceful-kill.md) | ☑ |
| C-4 | Notification `app_id` (AUMID) | P3 | S | [C-4](design/C-4-notification-app-id.md) | ☑ |
| C-5 | `scrape`: DOM source, query, MCP sampling summary | P2 | M | A-5 · [C-5](design/C-5-scrape-dom-summary.md) | ☑ |
| C-6 | `powershell`: per-call timeout; env rebuild from registry | P2 | S–M | [C-6](design/C-6-powershell-timeout-path.md) | ☑ |
| C-7 | Tool annotations on all 69 tools | P2 | S | [C-7](design/C-7-tool-annotations.md) | ☑ |
| S-1 | Tool allow/deny lists (`--tools`, `--exclude-tools`) | P2 | S | — | ☐ |
| S-2 | IP allowlist (CIDR v4/v6) | P2 | S | S-8 | ☐ |
| S-3 | CORS origins | P3 | S | — | ☐ |
| S-4 | OAuth 2.0 + PKCE (or external IdP bearer) | P3 | L | — | ☐ |
| S-5 | PEM cert/key files + self-signed generator | P2 | S–M | — | ☐ |
| S-6 | Config file + `auth` helper | P2 | M | — | ☐ |
| S-7 | `install` / `uninstall` at-logon task | P2 | M | S-6 | ☐ |
| S-8 | Unauthenticated `/health` | P2 | S | — | ☐ |
| S-9 | Claude Desktop Extension (`.mcpb`) + registry `server.json` | P3 | M | — | ☐ |
| S-10 | Per-tool black-box tester skill | P3 | S | — | ☐ |
| S-11 | AI-control indicator + takeover hotkey (indicator only — no gating) | P2 | M | A-14 · [S-11](design/S-11-control-indicator.md) | ☑ |
| D-10 | `interact_element`'s "pattern not supported" refusal is masked from the caller | P1 | S | — | ☑ |
| D-11 | Element cache grows without bound outside `snapshot` | P1 | S | — | ☑ |
| D-12 | Recursive `registry_delete` is not atomic: a sub-key that denies delete leaves the tree half gone | P2 | S–M | D-10 | ☐ |
| D-13 | `registry_set` with `Binary`/`MultiString` always fails and leaves an empty key behind | P2 | S | — | ☐ |
| D-14 | `scheduled_task create` accepts a past time and silently replaces an existing task | P2 | S | — | ☐ |
| D-15 | `interact_element select` with a value leaves the dropdown open when no item matches | P3 | S | — | ☐ |
| D-16 | A click centre that lands off every monitor is refused naming `x`, a parameter the caller never passed | P3 | S | — | ☐ |
| D-17 | `launch` / `start_process` report a ShellExecute hand-off to a running app as a failure | P2 | S | — | ☐ |
| D-18 | `wait_for` leftovers: a detail id released when the final poll throws; ids of a poll in flight at cancel | P3 | S | D-11 | ☐ |
| D-19 | `snapshot`'s truncation note says `window=<title>`, which `snapshot` refuses without `scope=window` | P2 | S | — | ☐ |
| D-20 | `find_element` stops at 20 matches and does not say it stopped | P2 | S | — | ☐ |
| D-21 | `wait_for` spins with under 1 ms of budget left: `Task.Delay` of a sub-millisecond span returns at once | P3 | S | — | ☐ |
| D-22 | `archive` zip/unzip silently overwrite an existing destination; no `overwrite`, no `confirm` | P2 | S | — | ☐ |
| D-23 | `interact_element type` reports "replaced the whole value" after a ValuePattern write it never read back | P2 | S | E-2 | ☐ |
| D-24 | `find_element kind:scrollable` counts any ScrollPattern element, even one with no axis that scrolls | P3 | S | — | ☐ |
| D-25 | A Win32 child clipped out of its parent, or never shown, reads as on screen (`scroll_into_view`, `click` by element) | P2 | S–M | E-2 | ☐ |
| D-26 | `MissingPattern` / `Refusing` re-probe liveness after a pattern lookup timed out on a hung provider | P3 | S | D-10 | ☐ |
| D-27 | The pattern-not-supported, D-10-translated and `select` no-item refusals quote a name or value unclipped | P3 | S | E-2 | ☐ |
| D-28 | Nothing serialises overlapping input-driving calls: one call's key-up can release a modifier another still holds | P2 | M | E-3 | ☐ |
| D-29 | The takeover hotkey does not interrupt a `key` hold/repeat, a long `type` or a `drag` in progress | P2 | M | S-11, E-3 | ☐ |
| D-30 | A key held by `key(hold_ms)` is not released when the host stops mid-hold | P3 | S | E-3 | ☐ |
| D-31 | Cancellation between two modifier key-downs sends a key-up for a modifier that never went down | P3 | S | E-3 | ☐ |
| D-32 | `click`'s `modifiers` vocabulary (ctrl\|shift\|alt) is narrower than `shortcut`'s | P3 | S | E-3 | ☐ |
| E-1 | Server instructions in the MCP handshake | P1 | S | — | ☑ |
| E-2 | `interact_element`: expand, collapse, set_value, scroll_into_view | P1 | S | D-10 | ☑ |
| E-3 | `click` modifiers; `key` repeat and hold | P2 | S | — | ☑ |
| E-4 | `find_element` filters: automation id, control type, class, within, paging | P2 | M | — | ☐ |
| E-5 | `observe`: action tools return the state they produced | P1 | M | D-11 | ☐ |
| E-6 | `sequence`: an ordered batch of UI steps in one call | P2 | M | E-5, S-11 | ☐ |
| E-7 | Capture by window / element; OCR word positions | P2 | M | — | ☐ |
| E-8 | Session state: elevation, lock, secure desktop | P2 | S–M | — | ☐ |
| E-9 | `file_edit` and content search for remote hosts | P2 | M | — | ☐ |
| E-10 | Named toolsets on top of S-1 | P3 | S | S-1 | ☐ |
| E-11 | Stable element ids and a snapshot diff | P3 | L | E-5, D-11 | ☐ |

**Suggested order.** **Defects D-1 … D-11 are done** (D-12 … D-18, logged 2026-11-11 by the
D-10/D-11 review, D-19 … D-22, logged 2026-11-12 during E-1, D-23 … D-27, logged 2026-11-12
during E-2, and D-28 … D-32, logged 2026-11-12 by the E-3 review, are open), and **so are sections
A, B and C** (A-5's Firefox phase and
D-2's `interact_element(type) clear` are the open sub-items; A-12's phase 2 is not planned). What
is left of the parity work is section S: S-11 (the control indicator) is done (PR #32); the quick wins S-8 and S-1 can be interleaved anywhere; S-4 last.
The section-A sequencing, cross-item decisions (coordinate space, defaults, tool count, element
ids, env vars) and per-item test seeds are in [`docs/design/A-roadmap.md`](design/A-roadmap.md).
The section-B plan (four phases, the element-target resolver, window matcher, typing planner and
app-catalog decisions, per-item test seeds) is in [`docs/design/B-roadmap.md`](design/B-roadmap.md).
The section-C plan (three phases, the safer file defaults, the registry denylist, in-process toasts,
opt-in sampling and the annotation table, per-item test seeds) is in
[`docs/design/C-roadmap.md`](design/C-roadmap.md).

**Section E (added 2026-11-10) is not upstream parity.** It comes from a review of this server's
own tool surface for how many round trips and tokens an AI client spends driving it, and the same
review logged D-10 and D-11 (both done 2026-11-11, so E-2 and E-5 are unblocked). E-1, E-2 and
E-3 are done (2026-11-12). Order for the rest:
E-4, E-5, E-6, E-7, E-8, E-9, E-10. E-11 is last and conditional: measure the snapshot
cost after E-5 before committing to it. `todo.md` still records every UI-automation tool as never
verified live — that sweep outranks any E item that builds on those tools.

---

## D — Defects exposed by the comparison

### D-1 — `shortcut` and `key` reject letters, digits, and bare keys  `P1 · S`
- [x] Done 2026-09-04 — [design note](design/D-1-shortcut-parser.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream behaviour.** `Shortcut` splits on `+`; single-character parts are sent literally and
multi-character parts go through `_KEY_ALIASES` (`backspace`, `capslock`, `scrolllock`,
`windows`/`command`→Win, `option`→Alt) then `uia.SendKeys` (`desktop/service.py` `shortcut()`).
`"win"` alone opens Start; `"ctrl+c"`, `"win+r"`, `"ctrl+shift+esc"` all work.

**Ours today.** `Services/InputService.cs:17` `KeyMap` holds 19 named keys plus `F1`–`F12`
(static ctor). `PressShortcutAsync` (`:126`) throws `Unknown key in shortcut` for any part not in
the map and rejects fewer than two parts. So `ctrl+c`, `ctrl+v`, `win+r`, `ctrl+1` and bare `win`
all fail. `PressKeyAsync` (`:117`) has the same map.

**Implementation sketch.**
- Extract a pure `ShortcutParser` (static, `internal`) returning `VirtualKeyCode[]` so it is unit-
  testable without injecting input.
- Resolution order per part: named map → single char: `A`–`Z` → `VirtualKeyCode.VK_A..`,
  `0`–`9` → `VK_0..`; other printable chars via `PInvoke.VkKeyScan(char)` (low byte = VK, high
  byte = shift state; add SHIFT to modifiers when set).
- Add aliases: `windows`, `super`, `cmd`, `meta` → LWIN; `return`; `del`; `ins`/`insert`;
  `prtsc`/`printscreen`; `capslock`; `numlock`; `scrolllock`; `apps`/`menu`; `pause`; numpad keys.
- Allow a single-part shortcut (`win`, `esc`) — send `KeyPress`.

**Touches.** `Services/InputService.cs`, new `Services/ShortcutParser.cs`,
`tests/.../Services/InputServiceTests.cs`, `Tools/InputTools.cs` descriptions.

**Tests.** Parser tests (no input injection): each example above resolves; unknown token still
throws with the offending token named; case-insensitive.

**Done when.** `shortcut("ctrl+c")`, `("ctrl+shift+s")`, `("win+r")`, `("alt+f4")`, `("win")`,
`("ctrl+1")` all succeed; `key("a")` works; error text for a bad token names the token.

### D-2 — `interact_element` advertises click / focus / type but only implements toggle / select / invoke  `P1 · S`
- [x] Done 2026-09-04 — [design note](design/D-2-interact-element-actions.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream behaviour.** Not a single tool upstream — but label-targeted `Click`/`Type` are the
primary way agents act on elements there, so this is the closest equivalent and must work.

**Ours today.** `Tools/UIAutomationTools.cs` `InteractElement` description lists
`click, toggle, select, focus, type`. `Services/UIAutomationService.cs:237` `InteractAsync`
handles only `toggle`, `select`, `invoke`; anything else throws `Unknown interact action`.
`FocusAsync` exists (`:312`) but is unwired. `select` requires a `value` it never uses.

**Implementation sketch.**
- `click`: Invoke pattern → else SelectionItem.Select → else Toggle → else physical left click at
  `Bounds` centre via `IInputService.ClickAsync` (inject `IInputService`, or move the fallback to
  the tool). Keep `invoke` as an alias.
- `focus`: `el.Focus()` (existing `FocusAsync`).
- `type`: focus, then `ValuePattern.SetValue(value)` when supported and not read-only; else
  keyboard `TextEntry`. Optional `clear:true` (select-all + delete) — align semantics with B-1.
- `select`: drop the spurious `value` requirement, or use `value` to pick a child item by name in
  a combo/list (ExpandCollapse → find child by name → SelectionItem.Select).
- Return what happened (`{action, pattern:"Invoke"|"physical-click"|...}`) instead of `"interacted"`.

**Touches.** `Services/UIAutomationService.cs`, `Tools/UIAutomationTools.cs`,
`tests/.../Tools/UIAutomationToolsTests.cs` (mock), `tests/.../Services/UIAutomationServiceTests.cs`
(`UIAutomation` category, Notepad fixture).

**Done when.** Every action named in the tool description works or returns a specific
"pattern X not supported on <controlType>" message; the description and implementation agree.

### D-3 — Cursor placement is wrong on secondary monitors  `P1 · S`
- [x] Done 2026-09-04 — [design note](design/D-3-cursor-virtual-desktop.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream behaviour.** All coordinates are virtual-desktop pixels; `uia.Click(x,y)`/`MoveTo`
call `SetCursorPos` directly, so negative and beyond-primary coordinates land correctly.

**Ours today.** `Services/InputService.cs:49` `MoveCursorToVirtualDesktop` scales `x,y` by
`65535 / SM_CXSCREEN` (primary monitor size) but passes the result to
`MoveMouseToPositionOnVirtualDesktop`, which expects normalisation against the **virtual screen**
(`SM_XVIRTUALSCREEN`, `SM_CXVIRTUALSCREEN`, …). Any coordinate on a second monitor lands elsewhere.

**Implementation sketch.**
- Simplest: `PInvoke.SetCursorPos(x, y)` then send button events without `MOUSEEVENTF_ABSOLUTE`
  (H.InputSimulator `LeftButtonClick()` after positioning does this).
- Or keep absolute moves and normalise correctly:
  `nx = (x - vLeft) * 65535 / (vWidth - 1)`, same for y, using the four virtual-screen metrics.
- The process already opts into **Per-Monitor-V2 DPI awareness** at startup (`Program.cs`
  calls `SetProcessDpiAwarenessContext`, falling back to `SetProcessDpiAwareness`); keep it so
  UIA bounds and screenshot pixels share one coordinate space, and document that space
  ("physical pixels, virtual desktop, origin = primary monitor top-left") in every
  coordinate-taking tool description.

**Touches.** `Services/InputService.cs` (extract the maths into a pure function), app manifest,
`tests/.../Services/InputServiceTests.cs`.

**Done when.** A unit test proves the normalisation for a monitor left of / above primary; a
manual check clicks a target on a secondary monitor via `multi_monitor` bounds.

### D-4 — `assert_element` advertises `value` and `focused` but implements neither  `P2 · S`
- [x] Done 2026-09-04 — [design note](design/D-4-assert-element-states.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Found while planning D-2** (`docs/design/D-2-interact-element-actions.md`). `Tools/UIAutomationTools.cs`
`AssertElement` lists `exists, enabled, checked, value, visible, focused`;
`Services/UIAutomationService.cs:220` `AssertElementAsync` handles `exists`, `enabled`, `checked`,
`visible` and throws `Unknown assertion state` for `value` and `focused`.
`docs/architecture/COMPONENTS.md` also claims both work.

**Sketch.** `focused` → `el.Properties.HasKeyboardFocus`. `value` needs something to compare
against: add an optional `expected` parameter (`value` passes when `ValuePattern.Value == expected`),
or drop `value` from the description. Either way put the observed state in the `FAIL:` text.

**Touches.** `Services/UIAutomationService.cs`, `Tools/UIAutomationTools.cs`,
`tests/.../Tools/UIAutomationToolsTests.cs`, `docs/architecture/COMPONENTS.md`.

**Done when.** Every state named in the description is implemented; `FAIL:` names the observed state.

---

### D-5 — `find_element(kind=any)` and `wait_for` fail on the first stale element  `P1 · S`
- [x] Done 2026-09-04 — [design note](design/D-5-find-path-resilience.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Found 2026-09-04 in use**, not by the upstream comparison — logged under rule 4. `kind=any`
errored twice on a busy desktop while `text` and `interactive` worked; reproduced the same day on
a second machine: `find_element("zzqxv", kind="any")` → "An error occurred invoking
'find_element'", `kind="text"` → `{"Matches":[]}`.

**Ours today.** `Services/UIAutomationService.cs` `FindElementAsync` calls
`_automation.GetDesktop().FindAllDescendants()` — every element of every process on the desktop,
materialised by one cross-process `FindAll`, with no `CacheRequest` and no element cap
(`Take(20)` runs after the walk) — then filters in LINQ with **unguarded** reads: `el.Name` in the
text predicate, `el.ControlType` in `MatchesKind` (`text` / `interactive`),
`el.Patterns.Scroll.IsSupported` (`scrollable`); `ToInfo` guards everything except
`el.BoundingRectangle`. With `kind=any`, `MatchesKind` short-circuits to `true`, so `el.Name` is
read on every element until 20 match, and one element that vanished between the walk and the read
(a tooltip, a closing menu, a virtualised list row) raises `UIA_E_ELEMENTNOTAVAILABLE` and the
whole call fails. `text` / `interactive` narrow on `ControlType` first and usually survive, but
that read is just as unguarded. `WaitForAsync` hard-codes `FindKind.Any` and does not catch
between polls, so the first transient failure ends the wait instead of being retried — the one
thing a wait is for. The whole-desktop walk also holds the single STA worker for its duration
(every other UIA call queues behind it), and `wait_for` repeats it every `interval_ms`.

**Upstream behaviour.** `tree/service.py` walks per window (active window first) on its own
thread with retry, reads properties through a `CacheRequest` (`tree/cache_utils.py`) and stops at
the `TreeElementBudget` (A-4). A dead element costs one node, never the call.

**Implementation sketch.**
- Guard every read on the find path with the existing `TryGetName` / `TryGetControlType` plus new
  `TryGetBounds` / `TryIsScrollable`; a failed read means "skip this element", never "fail the
  call". Reuse D-4's `IsElementGone` predicate (`ElementNotAvailableException`, or `COMException`
  with `UIA_E_ELEMENTNOTAVAILABLE` / the RPC failures) and its ProcessId liveness probe — a dead
  Win32 window's element answers reads with defaults (ControlType Pane, ProcessId 0) rather than
  throwing, so the walker must not rely on an exception alone.
- Scope the walk: `scope` = `foreground` (default: `GetForegroundRoot()`, the root `get_state`
  already uses and the window the agent is acting on) | `window` (a `window` title, matched
  exact-then-substring against the top-level windows' UIA names) | `desktop` on `find_element` and
  `wait_for`. `desktop` walks the desktop's top-level children one window at a time, each in its
  own try/catch, so a window closing mid-walk drops out instead of killing the search.
  **Scope added 2026-09-04 at the user's request** (rule 4: recorded, not silent): `window` makes a
  multi-step workflow deterministic when focus moves, and takes over B-6's `window_name` filter
  — see the design note.
- Push the kind filter into the UIA condition where it can be (`ConditionFactory.ByControlType`
  OR-ed for `text` / `interactive`, `TrueCondition` for `any` / `scrollable`) so the provider
  marshals fewer elements; `Name` still has to be read client-side (UIA has no "contains").
- `WaitForAsync`: forward `kind` and `scope` instead of hard-coding `Any`; catch per poll, keep
  the last exception, and if the deadline passes with nothing found report it in the timeout
  message. Give the loop an internal overload that takes the poll delegate so the retry is
  unit-testable without UIA.
- Leave the `CacheRequest` and the element budget to A-4, but shape the walker as one method per
  window root so A-4 wraps it without another rewrite.

**Touches.** `Services/UIAutomationService.cs`, `Abstractions/IUIAutomationService.cs`,
`Tools/UIAutomationTools.cs` (`scope` on `find_element` / `wait_for`; descriptions),
`docs/architecture/COMPONENTS.md`, `DATAFLOW.md` (the `wait_for` diagram),
`skills/windows/SKILL.md` §4.

**Tests.** Unit: the tool forwards `kind` and `scope`; the `WaitForAsync` loop keeps polling when
a poll throws and returns the first hit. `UIAutomation` category: `FindElementAsync("", Any)`
against the Notepad fixture returns without throwing, in both scopes, ten times in a row;
`WaitForAsync` for text that appears after 500 ms returns it.

**Done when.** `find_element(kind=any)` and `wait_for` return on a busy desktop; a stale element
is skipped, not fatal; the default scope is the foreground window and `scope=desktop` is opt-in;
the STA worker is not held for a whole-desktop walk unless asked.

---

### D-6 — `find_element(kind=interactive)` excludes Edit, ComboBox, ListItem, TabItem, RadioButton, Slider, TreeItem  `P2 · S`
- [x] Done 2026-09-04 — [design note](design/D-6-interactive-control-types.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Ours today.** `MatchesKind` (`Services/UIAutomationService.cs`):
`Interactive => Button | CheckBox | Hyperlink | MenuItem`. Edit, ComboBox, ListItem, TabItem,
RadioButton, SplitButton, TreeItem, DataItem, HeaderItem, Spinner, Slider, ScrollBar and Document
are all excluded — so the Claude Code prompt box (an `Edit`) is not "interactive", nor is any
dropdown, list row, tab, or radio button. `kind=text` (`Text | Edit | Document`) is currently the
only way to find an input.

**Upstream behaviour.** `tree/config.py` `INTERACTIVE_CONTROL_TYPE_NAMES` = Button, ListItem,
MenuItem, Edit, CheckBox, RadioButton, ComboBox, Hyperlink, SplitButton, TabItem, TreeItem,
DataItem, HeaderItem, TextBox, Spinner, Slider, ScrollBar, plus `INTERACTIVE_ROLES` via the
LegacyIAccessible role for controls that misreport their type. `DocumentControl` is its own class
(`DOCUMENT_CONTROL_TYPE_NAMES`, action `scroll`), listed alongside the interactive elements in the
snapshot. A-2 ports the whole classifier.

**Implementation sketch.** Replace the four-type test with a `static readonly HashSet<ControlType>`
of the upstream set (`Button, ListItem, MenuItem, Edit, CheckBox, RadioButton, ComboBox, Hyperlink,
SplitButton, TabItem, TreeItem, DataItem, HeaderItem, Spinner, Slider, ScrollBar`) **plus
`Document`** — for a flat `kind` filter a text area you type into is interactive; A-2 can split
it back out when there is a separate document list. State the set in the `kind` parameter
description. When A-2 lands, `find_element` calls the classifier instead of keeping its own list
(A-2's acceptance test).

**Touches.** `Services/UIAutomationService.cs`, `Tools/UIAutomationTools.cs` (description),
`docs/architecture/COMPONENTS.md`.

**Tests.** `UIAutomation` category: `FindElementAsync("", Interactive)` on the Notepad fixture
returns the editor (`Edit` / `Document`) and at least one `MenuItem`; a unit test pins the set
against the list above so a later edit is a visible diff.

**Done when.** `find_element("", kind=interactive)` on Notepad lists the editor and the menu
items; the set matches upstream's and is stated in the description.

---

### D-7 — `find_element` and `wait_for` return off-screen elements by default  `P2 · S`
- [x] Done 2026-09-04 — [design note](design/D-7-offscreen-filter.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Ours today.** `FindElementAsync` has no `IsOffscreen` filter; `ToInfo` reports the flag and
leaves it to the caller. Observed 2026-09-04: 18 of 21 `kind=text` hits were `IsOffscreen: true`
(collapsed panes, virtualised list rows, minimised windows). Worse, `Take(20)` runs before any
filtering the caller could do, so off-screen hits **crowd out** on-screen ones — an on-screen match
can be absent from the 20 returned. `wait_for` inherits both and can "find" a not-yet-visible
element and return early. (Off-screen is not the same as negative coordinates: a monitor left of
or above the primary has negative bounds and is on-screen — D-3.)

**Upstream behaviour.** `tree/service.py` `tree_traversal`: `is_visible = area > 0 and not
is_offscreen` (narrow exceptions for `EditControl` and browser `ListItemControl`) — off-screen
nodes never reach the output. The A-2 sketch already says "off-screen dropped"; this item is the
same rule applied to the tools that exist today.

**Implementation sketch.** `include_offscreen` (`bool`, default `false`) on `find_element` and
`wait_for`; the filter is `!TryGetIsOffscreen(el)` and non-empty bounds, applied **before**
`Take(20)`. Decide in implementation whether to keep upstream's `Edit` exception (an edit control
scrolled out of view reports off-screen yet is still the right target for `type`); if adopted,
say so in the description. `IsOffscreen` stays in `ElementInfo` for `get_element`.

**Touches.** `Services/UIAutomationService.cs`, `Abstractions/IUIAutomationService.cs`,
`Tools/UIAutomationTools.cs`, `docs/architecture/COMPONENTS.md`, `skills/windows/SKILL.md` §4.

**Tests.** `UIAutomation` category: `find_element("", kind=text)` returns only
`IsOffscreen == false` results by default; with `include_offscreen=true` the count is ≥ the
default count. Unit: the tool forwards the flag.

**Done when.** Default results contain no `IsOffscreen: true` element; the 20-cap applies after
the filter; `include_offscreen=true` restores today's behaviour.

---

### D-8 — `powershell` ships the CLIXML progress stream to the model on every call  `P2 · S`
- [x] Done 2026-09-04 — [design note](design/D-8-powershell-clixml-stderr.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Not an upstream-parity item** — logged here (rule 4) because it is the highest token-ROI fix on
the most-used tool. Related: C-6 touches the same service; do D-8 first or together.

**Ours today.** Windows PowerShell 5.1 with redirected stderr wraps its non-stdout streams in
CLIXML. Every cold start emits a `progress` record ("Preparing modules for first use.") and any
`Write-Progress` — including the ones inside `Invoke-WebRequest` and module autoload — adds
another. `PowerShellService.RunAsync` keeps the raw stream in `PSResult.Stderr`, and
`ShellTools.Powershell` serialises the whole `PSResult`, so each call carries ~0.6–3 KB of XML the
model reads and ignores. Measured 2026-09-04 through the service's exact process setup: a
one-liner with one `Write-Progress` → 596 characters of CLIXML on stderr; the same script with
`$ProgressPreference='SilentlyContinue'` in the preamble → 0. `Errors[]` is already clean: since
`6c96350` (2026-08-24) `ExtractErrors` decodes only `<S S="Error">` records (test
`RunAsync_progress_records_on_stderr_do_not_fail_the_command`), so the blob is carried once, in
`Stderr`, not twice — except when `XElement.Parse` fails, in which case the `RawLines()` fallback
puts the raw XML lines into `Errors[]` as well.

**Implementation sketch.**
- `PowerShellInvocation.BuildArgumentsAsync`: add `$ProgressPreference='SilentlyContinue'` to the
  preamble (script scope; a caller's script can set it back). Welcome side effect:
  `Invoke-WebRequest` / `Invoke-RestMethod` are markedly faster without the progress bar. Update
  the preamble comment and the tool description ("progress output is suppressed — there is no
  console to draw it on").
- `PowerShellService`: decode `Stderr` the way `Errors` already is — when the stream is CLIXML,
  replace it with the decoded text of the error / warning / verbose / debug / information records
  (one line each, stream-prefixed, e.g. `WARNING: careful`) and drop `progress` records;
  non-CLIXML stderr and unparseable CLIXML stay raw. `Errors[]` unchanged. This also covers a
  script that re-enables progress and the parse-failure fallback.
- Keep `PSResult`'s shape (`Success, Stdout, Stderr, ExitCode, Errors`) so `job` output and the
  existing tests are untouched.

**Touches.** `Services/PowerShellInvocation.cs`, `Services/PowerShellService.cs`,
`Tools/ShellTools.cs` (description), `docs/architecture/COMPONENTS.md` (PowerShellService
bullets), `tests/.../Services/PowerShellServiceTests.cs`.

**Tests.** Pure tests on a captured CLIXML sample for the decoder (progress dropped, warning kept
as text, error kept, raw stderr passthrough, concatenated `<Objs>` documents); the existing
`RunAsync_progress_records_on_stderr_do_not_fail_the_command` flips its
`Stderr.Should().Contain("progress")` precondition to assert the stream is now empty; one
real-process test that `Write-Warning 'careful'` yields `Stderr` containing `careful` and not
`<Objs`.

**Done when.** A `powershell` call that triggers module first-use returns `Stderr: ""`; a
`Write-Warning` arrives as text; `Errors[]` and `Success` semantics are unchanged; the response for
`'hi'` is under 200 bytes.

---

### D-9 — `job output` still returns raw CLIXML on stderr  `P3 · S`
- [x] Done 2026-09-04 — [design note](design/D-9-job-clixml-stderr.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Found while implementing [D-8](design/D-8-powershell-clixml-stderr.md)** — logged under rule 4
rather than widening D-8.

**Ours today.** D-8 gave `PowerShellService` a whole-stream `ClixmlStderr.Decode`, and put
`$ProgressPreference='SilentlyContinue'` in `PowerShellInvocation`'s preamble — which background
jobs share, so the bulk of the noise is gone for them too. But `JobService` pumps stderr
incrementally into a `BoundedTextBuffer` and serves a `Tail(n)` of it, so it has no whole document
to parse and a tail can cut a CLIXML record in half. A job whose script re-enables progress, or
that writes warnings/verbose, still returns raw `<Objs>` XML from `job output`.

**Sketch.** Either decode at job **completion** (the full stream is known then, and `job output` on
a finished job is the common case), or a streaming line filter that drops `<Obj S="progress">`
spans as they arrive. The first is simpler and covers the case that matters; the second also helps
`job output` on a still-running job.

**As landed:** both, via a third trick. Decode once in `MonitorAsync` (before the state flips to a
terminal value, so no reader sees a finished job with raw XML), rewriting the buffer through a new
`BoundedTextBuffer.ReplaceAll` so `Tail`/`Length`/`TrimmedChars` stay consistent at no per-read
cost; and decode a copy on read while a job is **running**, which works because `ClixmlStderr` now
retries on everything up to the last `</Objs>` and drops a trailing partial document. A stream with
no complete document still passes through raw.

**Touches.** `Services/JobService.cs`, `tests/.../Services/JobServiceTests.cs`,
`docs/architecture/COMPONENTS.md` (JobService bullets).

**Done when.** `job output` on a finished job that emitted warnings returns prefixed text, not
`<Objs`; a running job's tail is no worse than today.

---

### D-10 — `interact_element`'s "pattern not supported" refusal is masked from the caller  `P1 · S`
- [x] Done 2026-11-11 — in `CHANGELOG.md [Unreleased]`, ships with the next release

**Found by the 2026-11-10 tool-surface review** (read from the source, not reproduced live).

**Ours today.** `UIAutomationService.NotSupported()` builds a `NotSupportedException` naming the
pattern and the control (`"TogglePattern not supported on Button 'Save'."`), and `invoke`, `toggle`
and `select` throw it. `ToolErrors.IsCallerFacing` does not list `NotSupportedException`, so the
call-tool filter in `WindowsMcpHost.AddWindowsMcp` lets the SDK flatten it to `"An error occurred
invoking 'interact_element'."`. The tool's `[Description]` promises the opposite ("an unsupported
pattern errors naming the control type"), and the caller cannot tell a wrong verb from a crash.

**Sketch.** Throw `InvalidOperationException` from `NotSupported()` — a deliberate refusal belongs
to the caller-facing set (`CLAUDE.md` "Caller-facing errors"). Fix the family, not the case: grep
`src/` for every other `NotSupportedException` and bare `Exception` thrown as a refusal.

**Touches.** `Services/UIAutomationService.cs`, any sibling found by the grep,
`tests/.../Services/UIAutomationServiceTests.cs`, a `ToolErrorsTests` case.

**Tests.** A fake element with no TogglePattern: `toggle` surfaces the message with the control
type and name through the call-tool filter, not the masked text.

**Done when.** `interact_element(action:"toggle")` on a plain button returns `IsError` with the
pattern and the control named.

**As landed:** the whole masked-refusal family, not just the reported case. `NotSupported()` became
`UiRefusals.PatternNotSupported` (an `InvalidOperationException`), and a middle-button `drag` an
`ArgumentException` naming `button`. The review then found the same masking wherever a library or
the OS raised the refusal: FlaUI's exceptions and UIA HRESULTs on `interact_element` / `get_table`
/ `get_text` (translated by `UiRefusals.Translate` inside `UIAutomationService.Refusing`: disabled,
element gone, child part gone, not supported), a disabled control refused only after the
foreground had moved (now refused first), the unreachable foreground-to-desktop fallback
(`UiRoots.FirstAvailable`), `Process.Start` and packaged activation (`ProcessStartErrors`), registry
access-denied (`RegistryErrors`), `power_action`'s OS refusals (`IPowerNative` seam), and
`scheduled_task create`'s unparseable `'daily'` trigger. Each is fixed at its call site; a new IL
scan in `ToolErrorsTests` fails on any non-caller-facing construction not on a site-keyed,
reasoned list. Gaps found next to it but outside it are D-12 … D-17.

### D-11 — Element cache grows without bound outside `snapshot`  `P1 · S`
- [x] Done 2026-11-11 — in `CHANGELOG.md [Unreleased]`, ships with the next release

**Found by the 2026-11-10 tool-surface review.** The header comment of `UIAutomationService.cs`
already records it as a v0.3.0 TODO that never landed.

**Ours today.** `ToInfo()` mints an `el_N` id and stores the `AutomationElement` in `_elementCache`
on every call. Only ids issued by `SnapshotAsync` are tracked in `_snapshotIds` and evicted by the
next snapshot. Every `find_element` match, every `get_state` node, every `get_element`, and every
element-targeted `click`/`type`/`scroll`/`drag` (each resolves through `GetElementAsync`, which
re-mints) adds an entry that lives until the process exits. On a long-running HTTP host that is an
unbounded set of live COM references.

**Sketch.** A bounded LRU over the non-snapshot ids (a cap in `UiTreeOptions`, default a few
thousand), and stop `GetElementAsync` minting a second id for an element that already has one.
An evicted id answers `KeyNotFoundException` saying it expired and to re-`snapshot` — not "not in
cache". Snapshot ids keep their evict-on-next-snapshot rule.

**Touches.** `Services/UIAutomationService.cs`, `Abstractions/Models/UIAutomationDtos.cs`
(`UiTreeOptions`), `tests/.../Services/UIAutomationServiceTests.cs`.

**Tests.** N+1 lookups past the cap evict the oldest; a snapshot id survives non-snapshot churn;
`get_element` on a snapshot id does not grow the cache; the expired-id message names the remedy.

**Done when.** A loop of 10 000 `find_element` calls leaves the cache at its cap.

**As landed:** `Services/UiTree/ElementCache.cs` — non-snapshot ids in an LRU of
`max(UiTreeOptions.MaxCachedElements = 5000, 2 × MaxElements)`, snapshot ids exempt and dropped by
the next snapshot, `get_element` answering under the id passed in. A dead id says *expired* (with
the hidden snapshot takers — `wait_for` text/focus conditions, `screenshot` annotate, `scrape`
source dom — and "take a new snapshot") or *unknown*, never "not in cache";
`assert_element(exists)` on an expired id FAILs saying so. Found on the way and fixed with it:
`wait_for`'s element conditions minted up to 20 ids per poll and kept them all, so each poll now
hands back its unused ids (the final poll of a timed-out wait is kept so an id in `Detail`
resolves). What that release still misses is D-18.

### D-12 — Recursive `registry_delete` is not atomic  `P2 · S–M`
- [ ] Not started

**Found by the D-10/D-11 review (2026-11-11), reproduced** on a scratch key under `HKCU`.

**Ours today.** `RegistryService.DeleteKeyAsync` counts the descendants first, which proves only
that the tree can be READ, then calls `DeleteSubKeyTree`. A sub-key that denies DELETE stops the
call after its siblings are already gone: with children `A\A1` (deletable) and `B` (delete
denied), `A` and `A\A1` are removed and `B` stays. Since D-10 the refusal at least says the delete
was denied part-way and that sub-keys may already have been removed; it used to read as a clean
access-denied naming only the parent. Cancellation is not observed inside the count or the delete.

**Sketch.** Check DELETE access on every descendant before the first mutation (open each with
`RegistryRights.Delete`, or delete leaf-first under one transaction where the hive supports it) and
refuse naming the first key that would deny. Observe `ct` per key.

**Done when.** A tree with one sub-key that denies delete is refused naming that sub-key and
every key is still there.

### D-13 — `registry_set` with `Binary` / `MultiString` always fails and leaves an empty key  `P2 · S`
- [ ] Not started

**Found by the D-10/D-11 review (2026-11-11), reproduced.**

**Ours today.** The tool passes `data` to `RegistryService.SetAsync` as a string whatever `kind`
says, so `Binary` and `MultiString` always fail inside `SetValue`, and `DWord`/`QWord` fail on
non-numeric data. `CreateSubKey` has already run by then, so a key that did not exist is left
behind empty.

**Sketch.** Convert `data` per kind in the tool (hex or base64 for `Binary`, a JSON array or
newline-separated text for `MultiString`, an integer parse for `DWord`/`QWord`) and refuse a
mismatch as an `ArgumentException` naming `data` and `kind` BEFORE the key is created. An unknown
`kind` should be refused too — today it silently becomes `String`.

**Done when.** A `Binary` value round-trips through `registry_set` / `registry_get`, and a bad
`DWord` is refused with the key not created.

### D-14 — `scheduled_task create` accepts a past time and silently replaces an existing task  `P2 · S`
- [ ] Not started

**Found by the D-10/D-11 review (2026-11-11), read from the source.**

**Ours today.** `TaskSchedulerService.ParseTrigger` accepts any date-time, so a time in the past
registers a task that never fires. `RegisterTaskDefinition(name, td)` replaces a task of the same
name without saying so. (D-10 made a non-date trigger a caller-facing refusal and stopped the
description advertising `daily` / `onlogon`; recurring triggers remain a feature nobody has built.)

**Sketch.** Refuse a start time in the past naming `trigger` and the current time; refuse an
existing name unless an explicit `overwrite: true`, the way `file_manage` does.

**Done when.** A past time and a duplicate name are each refused by name with nothing registered
or replaced.

### D-15 — `interact_element select` with a value leaves the dropdown open on no match  `P3 · S`
- [ ] Not started

**Found by the D-10/D-11 review (2026-11-11), read from the source.**

**Ours today.** `select` with a value calls `ExpandCollapse.Expand()` and then searches for the
item. When no item has that name the call throws `KeyNotFoundException` and the list stays
expanded — a refusal that left a side effect on screen.

**Sketch.** Collapse again in a `finally` when the item was not selected; or search before
expanding when the provider exposes its items collapsed.

**Done when.** A `select` for an item that is not there leaves the control as it found it.

### D-16 — An off-monitor click centre is refused naming `x`  `P3 · S`
- [ ] Not started

**Found by the D-10/D-11 review (2026-11-11), read from the source.**

**Ours today.** `interact_element click` on a control with no usable pattern computes the
element's centre and clicks it off the STA thread. When that point is on no monitor,
`InputService` throws `ArgumentOutOfRangeException` naming `x` — a parameter the caller never
passed, on a tool that takes an element id.

**Sketch.** Refuse in `ClickPoint` when the centre is outside every monitor, naming the element
and its bounds; `ElementTarget` already makes that check for the input verbs.

**Done when.** The refusal names the element, not `x`.

### D-17 — A ShellExecute hand-off to a running app is reported as a failure  `P2 · S`
- [ ] Not started

**Found by the D-10/D-11 close-out (2026-11-11), read from the source; pre-existing.**

**Ours today.** `Process.Start` under ShellExecute returns null when the target is handed to a
process that is already running (a document opened in an open editor, a single-instance app). All
three start paths (`start_process` with shell execute, `launch` by path, the legacy launch) treat
null as failure and throw `InvalidOperationException` "no new process was created" — the launch
worked and the caller is told it did not.

**Sketch.** Treat null under ShellExecute as success with no pid; for `launch`, fall through to the
window wait by title, which already handles "the window belongs to a process the activation never
named" (`WindowDetected`).

**Done when.** Opening a second document in a running single-instance app reports success.

### D-18 — `wait_for` id-release leftovers  `P3 · S`
- [ ] Not started

**Left open on purpose by D-11 (2026-11-11).** Both are bounded by the element cache's cap.

**Ours today.** (1) An unsatisfied element poll's ids are released just before the next poll is
gathered. If that next poll THROWS and the wait then times out, `Detail` still describes the
earlier clean poll ("found 'Save' (el_57) but it is disabled") whose ids are already gone.
(2) A poll still running on the STA thread when the wait is cancelled never returns its result to
the loop, so the ids it mints are not released; `OnStaAsync` checks the token only before the
work starts.

**Sketch.** For (1), release pending ids only after the next gather returns cleanly. For (2),
have the find path report the ids it mints through a caller-supplied sink so the wait can release
them when the abandoned work finishes.

**Done when.** No `el_N` named in a `wait_for` reply is ever expired on arrival.

### D-19 — `snapshot`'s truncation note recommends a call `snapshot` refuses  `P2 · S`
- [ ] Not started

**Found while pinning the E-1 instructions (2026-11-12), read from the source.**

**Ours today.** A text-format `snapshot` whose walk ran out of budget ends with
`ElementBudget.NoteFor` (`Services/UiTree/ElementBudget.cs`), printed by `SnapshotRenderer`:
`Truncated at N elements. Narrow the view (scope=foreground, or window=<title>) or raise
max_elements.` But `UIAutomationTools.Snapshot` refuses `window` unless `scope` is `window`
(`"window is only used with scope=window."`), and the default scope is `desktop` — so a client
that follows the note literally and adds `window=<title>` gets an `ArgumentException` instead of a
narrower walk.

**Sketch.** Make the note name the call that works: `scope=window with window=<title>`. Keep the
leading `Truncated` word (the E-1 instructions and `ServerInstructionsTests` key on it).

**Touches.** `Services/UiTree/ElementBudget.cs`, `tests/.../Services/UiTree/ElementBudgetTests.cs`,
`tests/.../Services/UiTree/SnapshotFixtures.cs` (which repeats the note verbatim),
`docs/testing/e2e-test-plan.md` (SNAPSHOT-02 quotes the trailer).

**Tests.** Every parameter assignment the note names is accepted by `snapshot` as written — parse
the note's `key=value` pairs and call the tool with them (scope=foreground alone; scope=window
with a window title) and expect no refusal; the note still starts with `Truncated`.

**Done when.** Doing exactly what the truncation note says never produces a refusal.

### D-20 — `find_element` stops at 20 matches and does not say so  `P2 · S`
- [ ] Not started

**Found while pinning the E-1 instructions (2026-11-12), read from the source.**

**Ours today.** `UIAutomationService.FindElementAsync` stops collecting at `MaxMatches = 20`
(after filtering, D-7), and `FindElementResult` is `(ElementInfo[] Matches)` — no `Truncated`
flag, no total. Twenty matches and "the first twenty of many" look identical, so a client cannot
apply the rule every other capped result follows (and the E-1 instructions state): *Truncated
means there is more, narrow before concluding something is absent*. The tool's `[Description]`
says "Returns at most 20 matches", which is the only signal. `wait_for`'s element conditions share
the find path and the cap.

**Sketch.** Collect up to 21 and report `Truncated: true` when the 21st exists, returning 20 —
the cheapest honest signal; the walk already stops early, so the extra cost is one element. E-4's
`max_results` / `offset` / total-matched is the full fix and builds on this; this item is only the
flag, not paging.

**Touches.** `Abstractions/Models/UIAutomationDtos.cs` (`FindElementResult` gains `Truncated`),
`Services/UIAutomationService.cs` (`FindElementAsync`, `CollectFrom`), `Tools/UIAutomationTools.cs`
(the description), `tests/.../Services/UIAutomationServiceTests.cs`,
`tests/.../Tools/UIAutomationToolsTests.cs`.

**Tests.** Exactly 20 matching elements → 20 rows, `Truncated:false`; 21 → 20 rows,
`Truncated:true`; the extra element is never cached (no id minted for the 21st, so D-11's cache
does not grow).

**Done when.** A `find_element` that dropped matches says so in its result.

### D-21 — `wait_for` spins when less than 1 ms of budget is left  `P3 · S`
- [ ] Not started

**Found while running the E-1 suite (2026-11-12): `Wait_loop_floors_a_zero_interval_at_ten_milliseconds`
failed once with 3 444 attempts in 150 ms.**

**Ours today.** `UIAutomationService.WaitLoopAsync` (the `wait_for` loop) and `PollAsync` (the
older `WaitForAsync(string, …)` member of `IUIAutomationService`, which no tool calls today) floor
the interval at 10 ms and then clamp it to the time left: `if (delay > remaining) delay =
remaining; await Task.Delay(delay, ct)`. `Task.Delay(TimeSpan)` truncates to whole milliseconds,
so with under 1 ms remaining it completes at once and the loop polls again with no sleep — over
and over until the deadline passes. With a cheap poll that is a busy spin (thousands of polls,
each minting ids on a real desktop); `Attempts` balloons and the 10 ms floor the remarks promise is
not honoured at the tail. `LaunchWait.ForWindowAsync` is not affected: it works in integer
milliseconds and stops at `remaining <= 0`.

**Sketch.** Treat a remaining budget under 1 ms as expired (break), or round the clamped delay up
to 1 ms; either keeps the loop at no more than one poll per floor interval plus one final poll.

**Touches.** `Services/UIAutomationService.cs` (`WaitLoopAsync`, `PollAsync`),
`tests/.../Services/WaitForServiceTests.cs`.

**Tests.** Drive the loop with a fake clock or a poll that burns the budget down to a fraction of a
millisecond and assert it does not poll again more than once; the existing
`Wait_loop_floors_a_zero_interval_at_ten_milliseconds` passes deterministically under load.

**Done when.** `Attempts` for a `timeout_ms` T at `interval_ms:0` is never more than T / 10 + 2.

### D-22 — `archive` zip/unzip overwrite an existing destination without asking  `P2 · S`
- [ ] Not started

**Found while pinning the E-1 instructions (2026-11-12), read from the source.**

**Ours today.** `archive` takes `action`, `src` and `dst` only — no `confirm`, no `overwrite` —
and is annotated `Destructive`. `FileSystemService.ZipAsync` builds beside the target and then
`File.Move(tmp, dstZip, overwrite: true)`, replacing any existing archive at `dst`;
`UnzipAsync` calls `ZipFile.ExtractToDirectory(srcZip, dstDir, overwriteFiles: true)`, replacing
every file in `dst` that the archive also contains. `file_manage` copy/move refuse an existing
destination unless `overwrite:true` (C-1's safer default); `archive` never got that rule. The E-1
instructions list `archive` among the changes that never ask.

**Sketch.** Add `overwrite` (default false) to `archive`: `zip` refuses an existing `dst` file and
`unzip` refuses when any entry would land on an existing file, each naming the path and
`overwrite`, before anything is written — the same contract as `file_manage`. Then drop `archive`
from the instructions' never-ask list (`ServerInstructionsTests` will demand it) and from
`skills/windows/SKILL.md` and README "Safety rails".

**Touches.** `Tools/FileTools.cs` (`Archive`), `Abstractions/IFileSystemService.cs`,
`Services/FileSystemService.cs` (`ZipAsync`, `UnzipAsync`), `Hosting/ServerInstructions.cs`,
`tests/.../Tools/FileToolsTests.cs`, `tests/.../Services/FileSystemServiceTests.cs`,
`tests/.../Hosting/ServerInstructionsTests.cs`.

**Tests.** Zip onto an existing archive and unzip over an existing file are each refused with the
target unchanged; both succeed with `overwrite:true`; unzip into an empty directory needs no flag;
a zip-slip entry is still refused.

**Done when.** No `archive` call replaces an existing file the caller did not opt into replacing.

### D-23 — `interact_element type` claims a ValuePattern write it never read back  `P2 · S`
- [ ] Not started

**Found while landing E-2 (2026-11-12), read from the source.**

**Ours today.** `UIAutomationService.InteractOnSta`'s `type` branch focuses the element and, when
it has a writable `ValuePattern`, calls `SetValue(value)` and returns `Method:"ValuePattern"`,
`Detail:"replaced the whole value"` without reading the value back. E-2 found that providers
accept a `SetValue` they then ignore — a WinForms slider, a drop-down list given a text that is not
one of its items — and made `set_value` verify (`SetTextValue`: read before and after, refuse a
write that changed nothing, `now '<shown>'`). `type` still reports the same false success on those
controls.

**Sketch.** Route `type`'s ValuePattern branch through the same verification as `set_value`
(`SetTextValue`, never reading a password field), keeping `type`'s focus call and keyboard
fallback for a control with no writable ValuePattern. Decide whether an ignored write should fall
back to the keyboard or be refused; the second matches `set_value`.

**Touches.** `Services/UIAutomationService.cs` (`InteractOnSta`, `type`),
`Tools/UIAutomationTools.cs` (the description's `type` clause),
`tests/.../Services/UIAutomationServiceInteractVerbsTests.cs`.

**Tests.** `type` on a fake whose ValuePattern ignores the write is not reported as
`replaced the whole value`; a write that took reports what the control shows; a password field is
never read or echoed.

**Done when.** `type` never reports a ValuePattern write the control did not take.

### D-24 — `find_element kind:scrollable` lists controls with nothing to scroll  `P3 · S`
- [ ] Not started

**Found while landing E-2 (2026-11-12), read from the source.**

**Ours today.** `UIAutomationService.TryIsScrollable` — the `kind:scrollable` filter in the find
path — is `el.Patterns.Scroll.IsSupported`. WPF lists offer `ScrollPattern` with neither axis
scrollable, so they match. The snapshot's rule (`UiClassifier.IsScrollable`) and E-2's
`scroll_into_view` ancestor walk (`CanScroll`) both require the pattern **and**
`VerticallyScrollable || HorizontallyScrollable`. E-2's refusal sends the caller to
`find_element kind:scrollable` when no scrollable ancestor is found, so the two disagree on what
"scrollable" means.

**Sketch.** Make `TryIsScrollable` read the two axis properties too (one rule, as D-6 did for the
interactive set — ideally one shared predicate).

**Touches.** `Services/UIAutomationService.cs` (`TryIsScrollable`),
`tests/.../Services/UIAutomationServiceTests.cs`.

**Tests.** A fake with `ScrollPattern` and no scrollable axis is not a `kind:scrollable` match; one
with either axis scrollable is.

**Done when.** `find_element kind:scrollable` and the snapshot's scrollable list agree.

### D-25 — A Win32 child clipped out of its parent, or never shown, reads as on screen  `P2 · S–M`
- [ ] Not started

**Found while writing E-2's real-control tests (2026-11-12), observed:** a button clipped far below
its parent's client area, and one created without `WS_VISIBLE`, each report
`IsOffscreen=false` with real bounds through UIA's Win32 proxy (recorded in
`tests/.../Fixtures/OwnedPatternControlsWindow.cs`).

**Ours today.** `scroll_into_view` on an element with no `ScrollItemPattern` treats "not offscreen
and bounds with area" (`UIAutomationService.OnScreenCentre`) as on screen and answers
`already on screen, centre (x,y)` with nothing done. `ElementTarget.CentreOf` uses the same two
facts, so `click` / `type` / `scroll` / `drag` / `multi_select` / `multi_edit` by element accept such
a control and act at a point that is not on it. D-7's `IsVisibleEnough` (find / wait_for) has the
same blind spot.

**Sketch.** For an element with a native window handle, also require `IsWindowVisible` and that its
bounds intersect its parent's client rectangle (or at least a monitor); otherwise treat it as
offscreen. Measure the per-element cost before applying it in the find path.

**Touches.** `Services/UIAutomationService.cs` (`OnScreenCentre`, `IsVisibleEnough`),
`Services/UiTree/ElementTarget.cs`, `tests/.../Fixtures/OwnedPatternControlsWindow.cs`,
`tests/.../Services/UIAutomationServiceInteractVerbsIntegrationTests.cs`.

**Tests.** On real Win32 controls: a child clipped out of its parent and a child without
`WS_VISIBLE` are each refused by `scroll_into_view` (no "already on screen") and by `click` by
element; a visible child inside the client area still succeeds.

**Done when.** No element-targeted call reports or acts on a point the control does not occupy.

### D-26 — Refusals re-probe a hung provider after a pattern lookup  `P3 · S`
- [ ] Not started

**Found by the E-2 review (2026-11-12), read from the source; pre-existing since D-10 (R23).**

**Ours today.** R23 says a hung target must not be read again: `Refusing` rethrows a
`TimeoutException` untouched. But a refusal raised *by the verb* is an `InvalidOperationException`,
which passes `UiRefusals.NeedsProbe`, so `Refusing`'s filter calls `IsGone(el)` again before
`Translate` hands it back unchanged — after `MissingPattern` already probed liveness with its own
`IsGone`. A pattern lookup that came back empty from a slow or hung provider is therefore followed
by two liveness reads, each of which can cost the UIA timeout on the one STA thread every element
tool shares. `scroll_into_view` without the pattern reads `IsGone` twice more (before
`OnScreenCentre` and in `NoScrollItem`).

**Sketch.** Skip the filter's probe for an exception that is already caller-facing (check
`ToolErrors.IsCallerFacing` before `IsGone`), and probe at most once per call.

**Touches.** `Services/UIAutomationService.cs` (`Refusing`, `MissingPattern`, `NoScrollItem`),
`tests/.../Services/UIAutomationServiceRefusingTests.cs`.

**Tests.** A fake whose pattern lookup returns null and whose `ProcessId` read is counted: a
missing-pattern refusal reads it once; a caller-facing refusal from the verb is not re-probed by
`Refusing`.

**Done when.** One refusal costs at most one liveness read.

### D-27 — Some UI refusals still quote a name or value in full  `P3 · S`
- [ ] Not started

**Found while documenting E-2 (2026-11-12), read from the source.**

**Ours today.** E-2 added `UiRefusals.Clip` (80 characters, then `...`) and applied it in
`UIAutomationService.Describe`, `ElementTarget.CentreOf` and the values `set_value` echoes, so a
row whose name is its whole text cannot push the remedy past the 2 000-character cap on error
messages. The other refusals still take the raw name: `UiRefusals.PatternNotSupported`
(`"<Pattern> not supported on <type> '<name>'."`, the most common `interact_element` refusal),
`UiRefusals.Translate` (disabled / does-not-support), `UiRefusals.ChildGone`, and `select`'s
`No item named '<value>' under …`, which echoes the caller's value unclipped.

**Sketch.** Clip the name inside those factories (one place) and the `select` value at its call
site.

**Touches.** `Services/UiTree/UiRefusals.cs`, `Services/UIAutomationService.cs` (`select`),
`tests/.../Services/UiTree/UiRefusalsTests.cs`.

**Tests.** Each factory with a 200-character name quotes 80 plus `...` and keeps its trailing
remedy; `select` with a 200-character value likewise.

**Done when.** No UI refusal quotes more than 80 characters of a name or a caller's value.

### D-28 — Overlapping input-driving calls are not serialised  `P2 · M`
- [ ] Not started

**Found by the E-3 review (2026-11-12), read from the source; pre-existing since B-3/B-7, widened
by E-3.**

**Ours today.** Nothing orders two input-driving calls that run at once (HTTP clients can overlap,
and S-11's indicator refcounts them for exactly that reason). Every key is one desktop-wide state,
so one call can release a modifier another still holds, or have its keystrokes modified by another
call's hold: `click(modifiers:…)` (E-3), `multi_select`'s Ctrl across a batch (B-7),
`scroll(shift_wheel:true)`'s Shift (B-3), and `key(hold_ms:…)` against a concurrent `type` or
`shortcut`. For example, a `click(modifiers:"shift")` that overlaps a `multi_select` makes the
batch's clicks Ctrl+Shift-clicks while it holds Shift; a `click(modifiers:"ctrl")` that ends during
the batch releases the Ctrl the batch still relies on; and a `type` that runs during a
`key(hold_ms:…)` types with that key logically down.

**Sketch.** One process-wide input gate (a `SemaphoreSlim(1,1)`) taken by every tool in
`Hosting/DrivingTools.Names` that injects input — or at least by every call that holds a key —
for the length of the call, with the request's token on the wait. Decide whether a queued call
waits or is refused while another holds keys.

**Touches.** `Tools/InputTools.cs` (`Click`, `Key`, `Scroll`, `MultiSelect`, `Type`, `Shortcut`,
`Drag`), possibly `Hosting/WindowsMcpHost.cs` (the call-tool filter that already brackets driving
tools), `tests/.../Tools/`.

**Tests.** Two overlapping calls on a recording `IInputService` whose `KeyDownAsync` blocks on a
gate: the second call's first event comes after the first call's last key-up; a cancelled waiter
sends nothing.

**Done when.** No input event of one call lands between another call's key-down and key-up.

### D-29 — The takeover hotkey does not stop input already in progress  `P2 · M`
- [ ] Not started

**Found by the E-3 review (2026-11-12), read from the source; S-11 is indicator-only by design.**

**Ours today.** The S-11 takeover hotkey only records the takeover; the note rides on the next
result. A call already running carries on: a `key(hold_ms:10000)` keeps the key down, a
`key(repeat:100)` keeps pressing, and a long `type` or a slow `drag` keeps moving, while the person
at the machine is trying to take over. The hotkey is registered with `MOD_NOREPEAT`
(`ControlIndicator`, `ModNoRepeat`), so a person who presses it while the server holds the
hotkey's own trigger key may not get a `WM_HOTKEY` at all — **unverified**.

**Sketch.** Give the indicator a takeover `CancellationToken` that the in-flight driving calls
link into their own token, so a takeover cancels them (E-3's release guarantee then lets go of
any held key). Refuse to hold or repeat the configured hotkey's trigger key.

**Touches.** `Services/ControlIndicator.cs`, `Hosting/WindowsMcpHost.cs` (the call-tool filter),
`Tools/InputTools.cs`, `docs/design/S-11-control-indicator.md`.

**Tests.** A simulated takeover during a `key(hold_ms)` on a recording `IInputService` releases
the key early and the result says why; a takeover during `repeat` stops the presses. One live
check (UIAutomation) that the hotkey fires while a held key is down.

**Done when.** Pressing the takeover hotkey ends any hold, repeat, type or drag within one pace
step.

### D-30 — A held key is not released when the host stops mid-hold  `P3 · S`
- [ ] Not started

**Found by the E-3 review (2026-11-12), read from the source.**

**Ours today.** `key(hold_ms:…)` releases its key in `InputTools.HoldingAsync`, which runs only if
the process does. A host shut down, or a process killed, mid-hold leaves the (non-modifier) key
logically down for the whole desktop until something sends its key-up; nothing tracks which keys
the server holds, so the host's stop path cannot release them. (E-3 refuses modifiers for exactly
this reason.) A hard kill cannot be covered, but a graceful stop (the host's
`ApplicationStopping`) can.

**Sketch.** A small held-keys registry (`HoldingAsync` adds and removes) whose
`IHostApplicationLifetime.ApplicationStopping` callback sends a key-up for each key still listed.

**Touches.** `Tools/InputTools.cs` (`HoldingAsync`), `Hosting/WindowsMcpHost.cs`, a new tracker
type, `tests/.../Tools/InputToolsKeyRepeatHoldTests.cs`.

**Tests.** A hold in progress when the lifetime signals stopping: the key-up is sent once, and not
a second time when the hold then ends.

**Done when.** A graceful stop never leaves a key the server pressed down.

### D-31 — Cancellation between two key-downs releases a modifier that never went down  `P3 · S`
- [ ] Not started

**Found by the E-3 review (2026-11-12), read from the source.**

**Ours today.** `HoldingAsync` adds a key to its `sent` list *before* calling
`KeyDownAsync(key, ct)` so that a key-down that throws half-way is still released. When the
request is cancelled between two key-downs, the next `KeyDownAsync` throws on the token before it
sends anything, yet that key is in `sent` and gets a key-up. If the person at the machine is
physically holding that modifier, the synthetic key-up releases it under them.

**Sketch.** Check the token before adding the key (or pass `CancellationToken.None` to the
key-downs and check `ct` between them), so only a key whose key-down was attempted is released;
keep the release for a key-down that threw for any other reason.

**Touches.** `Tools/InputTools.cs` (`HoldingAsync`),
`tests/.../Tools/InputToolsClickModifiersTests.cs`.

**Tests.** `modifiers:"ctrl+shift"` with the token cancelled after the Ctrl key-down: Ctrl is
released, Shift is never sent a key-up; a Shift key-down that throws a non-cancellation exception
is still released.

**Done when.** No key-up is sent for a key whose key-down was never attempted.

### D-32 — `click`'s modifier names are narrower than `shortcut`'s  `P3 · S`
- [ ] Not started

**Found by the E-3 review (2026-11-12), read from the source.**

**Ours today.** `click(modifiers:…)` accepts `ctrl`, `control`, `shift` and `alt` only
(`InputTools.ParseModifiers`). `shortcut` and `key` go through `ShortcutParser`, which also takes
`lctrl`/`rctrl`, `lshift`/`rshift`, `lalt`/`ralt` and `option`. A caller who learned the names from
`shortcut` gets `Unknown modifier 'option'` from `click`. (`win` is refused on purpose — E-3's
note — and stays refused.)

**Sketch.** Resolve each part through `ShortcutParser.ResolveKey` and map the result onto
ctrl/shift/alt (keeping the left/right key if the caller named one), refusing anything that is
not one of those three; or state the narrower vocabulary in the refusal and the description and
leave it.

**Touches.** `Tools/InputTools.cs` (`ParseModifiers`, the `click` description),
`tests/.../Tools/InputToolsClickModifiersTests.cs`.

**Tests.** `option`, `lctrl` and `rshift` are accepted (or refused naming the accepted set); `win`
and `lwin` stay refused.

**Done when.** Every modifier name `shortcut` accepts for ctrl, shift or alt is either accepted by
`click` or refused with a message that lists what is.

---

## A — Desktop state and screenshots

### A-1 — Whole-desktop window inventory  `P1 · M`
- [x] Done 2026-09-05 — [design note](design/A-1-window-inventory.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream behaviour.** Every `Snapshot`/`Screenshot` response lists **Focused Window** and
**Opened Windows** as a table: name, depth (z-order), status (Maximized/Minimized/Normal/Hidden),
width, height, handle; each `Window` also carries `process_id` and `is_browser`.
Source: `desktop/service.py` `get_controls_handles()` (EnumWindows callback, ~924),
`get_windows()` (~1036), `get_active_window()` (~959), `is_overlay_window()` (~910),
`get_window_status()` (~314); `desktop/views.py` `Window`, `Status`, `Browser`.

**Ours (before A-1).** No enumeration anywhere. `IWindowService` had `ExecuteAsync`, `SwitchToAsync`,
`LaunchAsync`, `EnumerateMonitorsAsync` only. `window` acted on an exact title through
`PInvoke.FindWindow`. Now `window(action:"list"|"active")` (see the design note).

**Implementation sketch.**
- New DTO `WindowInfo(string Title, long Hwnd, int Pid, string ProcessName, WindowState State,
  Bounds Bounds, int ZOrder, bool IsActive, bool IsBrowser, int MonitorIndex)` in
  `Models/WindowDtos.cs`.
- `IWindowService.ListAsync(bool includeMinimized = true, bool includeHidden = false)`:
  `EnumWindows` in z-order → keep `IsWindowVisible`, non-empty title, skip
  `WS_EX_TOOLWINDOW`, skip DWM-cloaked (`DwmGetWindowAttribute(DWMWA_CLOAKED)` — filters
  UWP ghosts and other-virtual-desktop windows), skip zero-area, mark
  `IsIconic`/`IsZoomed`; `GetWindowThreadProcessId` → PID/name; `MonitorFromWindow` → index.
  Upstream's overlay filter (`is_overlay_window`) shows which chrome to drop (taskbar, Program
  Manager, input-method windows).
- Tool surface: extend `window` with `action: list | active` (no new tool count), and reuse the
  same list inside A-2's snapshot header.

**Touches.** `Abstractions/IWindowService.cs`, `Models/WindowDtos.cs`,
`Services/WindowService.cs`, `Tools/WindowTools.cs`, new `tests/.../Services/WindowServiceTests.cs`
(integration: returns without throwing; the test host's own console window is present when run
interactively), `tests/.../Tools/WindowToolsTests.cs`.

**Done when.** `window(action="list")` returns every user-visible top-level window with the fields
above in z-order, and `action="active"` returns the foreground one.

### A-2 — Desktop-wide labeled interactive-element snapshot  `P1 · L`
- [x] Done 2026-09-05 — [design note](design/A-2-desktop-snapshot.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream behaviour.** `Snapshot` (`tools/snapshot.py`, helpers in `tools/_snapshot_helpers.py`)
walks **every** window (active first, then the others) and returns three text blocks rendered by
`tree/views.py`: the **UI Tree** (semantic hierarchy), plus interactive and scrollable lists in
the form

```
window "Untitled - Notepad"
├── (612,388) button "Save"  [action: click]  [shortcut:Ctrl+S]
├── (300,200) edit "Text Editor"  [action: fill]  [focused]  [value:"hello"]
└── (900,40) checkbox "Word wrap"  [action: toggle]  [toggle:On]
```

Centre coordinates are what `Click`/`Type`/`Scroll`/`Move` accept as `loc`, and the list index is
the `label` those tools also accept. Classification lives in `tree/config.py`
(`INTERACTIVE_CONTROL_TYPE_NAMES`, `INTERACTIVE_ROLES` via LegacyIAccessible role,
`INFORMATIVE_*`, `STRUCTURAL_*`, `DEFAULT_ACTIONS`); traversal rules in `tree/service.py`
`tree_traversal()` (~379) and `get_nodes()` (~835): visible, non-zero area, clipped to the window
rectangle via `iou_bounding_box()` (~240), off-screen dropped, per-window thread with retry.
Metadata collected per node: `has_focused`, `is_password`, `value`, `min`/`max` (RangeValue),
`toggle_state`, `expand_collapse_state`, `shortcut` (AccessKey/AcceleratorKey). Action hints
come from `_ACTION_MAP` (edit→fill, checkbox→toggle, combobox→select, slider→slide,
document→scroll, else click). Rendering: `_render_tree()`, `_node_meta_str()`,
`_render_semantic_node()`.

**Ours (before A-2).** `get_state` built a JSON `ElementTree` of the foreground window only,
three levels deep, with no classification, no action hints, no centres and no window list, and
`el_N` ids accumulated forever. Now `snapshot` (see the design note); `get_state` is kept,
budgeted.

**Implementation sketch.**
- New `IUIAutomationService.SnapshotAsync(SnapshotOptions)` → `SnapshotResult`:
  `Windows` (A-1), `ActiveWindow`, `Cursor` (A-11), `Interactive[]`, `Scrollable[]` (A-3),
  optional `SemanticTree`, `Truncated`/`ElementLimit` (A-4), `CaptureMs`.
- `InteractiveElement(string ElementId, string Window, string ControlType, string Name,
  int CenterX, int CenterY, Bounds Bounds, string Action, ElementMeta Meta)`.
- Options: `scope: foreground|desktop` (default desktop), `window_title` filter, `include_tree`,
  `max_elements`, `format: text|json` — the compact text format is roughly 5–10× cheaper in
  tokens than the JSON tree; make `text` the default and keep `json` for programmatic use.
- Port the control-type/role sets and the per-node metadata reads to FlaUI
  (`el.Patterns.RangeValue`, `.Toggle`, `.ExpandCollapse`, `.Value`, `HasKeyboardFocus`,
  `IsPassword`, `AccessKey`, `AcceleratorKey`, `LegacyIAccessible.Role`).
- Use a FlaUI `CacheRequest` with the property/pattern set for `TreeScope.Subtree` (A-4).
- Element cache: reset per snapshot (IDs = list index, like upstream labels) or keep `el_N` but
  evict on the next snapshot — decide in the spec; document that IDs are only valid until the next
  snapshot.
- Keep `get_state` as-is for compatibility (or make it `snapshot(scope=foreground, format=json)`).

**Touches.** `Abstractions/IUIAutomationService.cs`, `Models/UIAutomationDtos.cs`,
`Services/UIAutomationService.cs` (consider splitting a `Services/UiTree/` folder: classifier,
traverser, renderer), `Tools/UIAutomationTools.cs`, `skills/windows/SKILL.md`, README, docs.

**Tests.** Pure unit tests for the classifier, renderer and action map on fake node records;
`UIAutomation`-category integration against the Notepad fixture asserting the edit control is
listed with `action: fill` and a centre inside its bounds; a timing assertion under the element cap.
**Acceptance test carried over from D-6:** the classifier lists an `Edit` (Notepad's editor, the
Claude Code prompt box) and every type in upstream's `INTERACTIVE_CONTROL_TYPE_NAMES` as
interactive, and `find_element(kind=interactive)` calls this classifier instead of keeping its own list.

**Done when.** One call returns all visible windows' interactive elements with centre coordinates,
action hints and metadata, in the compact text form, bounded by the element cap, and the
coordinates work unchanged with `click`/`type`/`scroll`.

### A-3 — Scrollable regions with scroll percentages  `P2 · S`
- [x] Done 2026-09-05 — [design note](design/A-2-desktop-snapshot.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `scrollable_elements_to_string()` lists each ScrollPattern element with
`[v:37%]`/`[h:0%]` from `vertical_scroll_percent`/`horizontal_scroll_percent` and the
`vertical_scrollable`/`horizontal_scrollable` flags (`tree/views.py` `_scroll_meta_str`).

**Ours (before A-3).** `find_element(kind=scrollable)` returned `ElementInfo` with no scroll data.
Now the snapshot's scrollable list carries `ScrollInfo`; `find_element` still does not populate
the new `ElementInfo.Scroll` (follow-up in the design note).

**Sketch.** `ScrollableElement` record with `VerticalPercent`, `HorizontalPercent`,
`VerticallyScrollable`, `HorizontallyScrollable` from `Patterns.Scroll`; include in A-2 output and
in `find_element(kind=scrollable)`; `scroll(element_id)` (B-3) uses its centre.

**Done when.** Scrollable list shows percentages and "Reached top/bottom" can be inferred.

### A-4 — Element budget, truncation note, UIA caching  `P1 · M`
- [x] Done 2026-09-05 — [design note](design/A-2-desktop-snapshot.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `tree/budget.py` `TreeElementBudget` — default 500 elements, env
`WINDOWS_MCP_MAX_TREE_ELEMENTS`; traversal stops early, `TreeState.truncated=true`, and every
rendered block appends `_truncation_note()` telling the agent to narrow the view or raise the
limit. `tree/cache_utils.py` builds a `CacheRequest` so property reads are one cross-process call
per subtree instead of one per property.

**Ours (before A-4).** `BuildTree` recursion was depth-limited (3) but not count-limited — a large
grid at depth ≤ 3 still exploded; no cache request (each `TryGetX` was a COM round-trip).
Now `ElementBudget` (`Services/UiTree/`) bounds both `snapshot` and `get_state`: the default comes
from `--max-tree-elements` / `WINDOWSMCP_MAX_TREE_ELEMENTS` (500) through the injected
`UiTreeOptions`, a per-call `max_elements` overrides it, and a stopped walk reports `Truncated`
with `ElementLimit` plus the one-sentence note in the text render. `UiTraverser` walks each window
under a single FlaUI `CacheRequest` (`TreeScope.Subtree`, pattern properties cached too).
`FindElementAsync` is unchanged — it keeps its own UIA-side conditions and 20-match cap, and its
cache request is a follow-up (see the design note).

**Sketch.** `max_elements` param (default 500) + `WINDOWSMCP_MAX_TREE_ELEMENTS` env; counter
threaded through traversal; `Truncated`, `ElementLimit` in the result + a note in text output;
apply to both `get_state` and A-2; FlaUI `CacheRequest` (`Add(Name, ControlType,
BoundingRectangle, IsEnabled, IsOffscreen, HasKeyboardFocus, …)`, `TreeScope = Subtree`,
`AutomationElementMode = Full`) activated around the walk.

**Done when.** A synthetic 5 000-row grid returns in bounded time with `truncated:true` and the
note; a profile shows fewer COM calls per element than before.

### A-5 — Browser DOM mode (`use_dom`)  `P2 · L`
- [x] Phase 1 (Chromium) done 2026-09-05 — [design note](design/A-5-browser-dom-mode.md); in `CHANGELOG.md [Unreleased]`, ships with the next release (0.8.0 per roadmap C11)
- [ ] Phase 2 (Firefox via MSAA/IA2) not started — a browser window with no `RootWebArea` is walked whole and its `Pages` entry says so

**Upstream.** For Chrome/Edge the traversal looks for the element whose `AutomationId ==
"RootWebArea"` and walks only that subtree, classifying by LegacyIAccessible role; browser chrome
is dropped; text nodes are collected as `dom_informative_nodes`; the scrollable document becomes
`dom_node` with scroll percent; `_dom_correction()` (~294) dedupes and fixes Chromium quirks.
Firefox exposes web content only via MSAA/IAccessible2, so `tree/ia2.py` fetches the root
`IAccessible` with `AccessibleObjectFromWindow` (oleacc) and walks it into the same shapes.
`Scrape(use_dom)` and `WaitFor(use_dom)` consume it; `Browser` enum = chrome.exe, msedge.exe,
firefox.exe.

**Ours (before A-5).** Nothing browser-specific.

**Sketch.** Phase 1 (Chromium): `use_dom:true` on the A-2 snapshot — locate `RootWebArea`
under the active browser window (`FindFirstDescendant(cf.ByAutomationId("RootWebArea"))`),
traverse it with the same classifier, emit `DomText` block and the document's scroll percent.
Phase 2 (Firefox): declare `IAccessible` (available via the `Accessibility` interop assembly) and
walk `accChildCount`/`accChild`; IA2 (`IAccessible2`) needs hand-declared COM interfaces —
observe the **vtable-gap rule** in `CLAUDE.md`. Expose through `snapshot(use_dom)`,
`scrape(source=dom)` (C-5), `wait_for(use_dom)` (B-6).

**Done when.** With a page open in Edge, `snapshot(use_dom=true)` lists page links/inputs and
page text without address-bar/tab-strip elements; Firefox is a documented follow-up.

### A-6 — Annotated screenshot (bounding boxes, labels, grid, cursor)  `P2 · M`
- [x] Done 2026-09-05 — [design note](design/A-6-annotated-screenshot.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `Snapshot(use_vision=true, use_annotation=true)` draws a coloured rectangle and
numbered label per interactive node, highlights the cursor, and optionally overlays a reference
grid (`width_reference_line`/`height_reference_line`) — `desktop/service.py`
`get_annotated_screenshot()` (~1217, `draw_annotation`, `draw_label`, `get_random_color`).

**Ours (before A-6).** Plain capture only. Now `screenshot(annotate:true, grid_columns, grid_rows)` — see the design note.

**Sketch.** SkiaSharp (already referenced) `SKCanvas` over the captured bitmap: box per element
(2 px, colour from a fixed palette by index), label chip with contrast text at the box's top-left
(clamped inside the image), cursor crosshair/ring (A-11), grid lines every `w/n`, `h/n` with
coordinate captions. Params on the snapshot/screenshot tool: `annotate`, `grid_columns`,
`grid_rows`. Must run after the element walk so labels match IDs.

**Done when.** The annotated image's label N sits on element N from the same call.

### A-7 — Return the screenshot as MCP image content  `P1 · S`
- [x] Done 2026-09-04 — [design note](design/A-7-screenshot-image-content.md); in `CHANGELOG.md [Unreleased]`, ships with the next release (0.8.0 per roadmap C11)

**Upstream.** `build_snapshot_response()` returns `[text, Image(data=png, format="png")]` — an
MCP `ImageContent` block, so the model sees the picture directly.

**Ours (before A-7).** `Tools/ScreenTools.cs` `Screenshot` returned a JSON string: `output="file"` → path;
`output="base64"` → `{data_base64: …}` inside a **string**. Neither is an image content block, so
no client renders it and the model cannot look at it without a second tool.

**Sketch.** Return `CallToolResult` (or `IEnumerable<AIContent>` /
`Microsoft.Extensions.AI.DataContent`) — verify the accepted return types in SDK 2.2.0 — with a
`TextContentBlock` (metadata JSON: size, scale, region, cursor) plus an `ImageContentBlock`
(`MimeType = image/png|jpeg`). Keep `output=file` as an option; make inline the default.
Respect the ~1 MB tool-result limit some clients enforce (A-9).

**Touches.** `Tools/ScreenTools.cs`, `tests/.../Tools/ScreenToolsTests.cs` (assert content-block
types), `HttpTransportTests` smoke (image survives the HTTP transport).

**Done when.** Claude Code / Claude Desktop display the screenshot inline from a single call.

### A-8 — Multi-display capture and virtual-desktop coordinates  `P1 · M`
- [x] Done 2026-09-04 — [design note](design/A-8-multi-display-capture.md); in `CHANGELOG.md [Unreleased]`, ships with the next release (0.8.0 per roadmap C11)

**Upstream.** `display=[0]`/`[0,1]` (zero-based indices from `DisplayInventory`) captures the
union rect of the chosen monitors (`get_display_union_rect`, ~1177);
`region=[left,top,right,bottom]` in virtual-desktop pixels (`parse_region_selection`, ~1135)
takes precedence; out-of-bounds regions raise instead of clipping. Default = full desktop.
The response echoes `Visible Displays`, `Selected Displays`, `Screenshot Region`,
`Coordinate Space: Virtual desktop coordinates`.

**Ours (before A-8).** `Services/ScreenshotService.cs` defaulted to `SM_CXSCREEN × SM_CYSCREEN`
(primary only); `region` was `x,y,w,h` with no validation and an undocumented coordinate space;
`ocr` shared `ScreenTools.ParseRegion`.

**Sketch.** `display` param (`int[]` or `"all"`), reuse `IWindowService.EnumerateMonitorsAsync`
(order = index) to compute the union rect; `region` validated against the virtual screen
(`SM_XVIRTUALSCREEN…`) → `ArgumentException` when outside; response metadata lists displays,
selected displays, region, and states the coordinate space; same for `ocr`. Decide the default
(primary = cheaper tokens; upstream = all) and document it.

**Done when.** `screenshot(display=[1])` captures the second monitor; a region straddling two
monitors captures correctly; an out-of-bounds region errors.

### A-9 — Auto-downscale, scale env, coordinate-scale report  `P1 · S`
- [x] Done 2026-09-04 — [design note](design/A-9-screenshot-downscale.md); in `CHANGELOG.md [Unreleased]`, ships with the next release (0.8.0 per roadmap C11)

**Upstream.** Images larger than 1920×1080 are downscaled (LANCZOS) and
`WINDOWS_MCP_SCREENSHOT_SCALE` (0.1–1.0) applies on top; the text block reports
`Screenshot Original Size`, `Screenshot Coordinate Scale: 2.0 — multiply every image pixel
coordinate by 2.0 before passing to Click…` (`_snapshot_helpers.py`, `desktop/service.py`
`max_image_size` branch).

**Ours (before A-9).** Full resolution always; a 4K capture was a ~10 MB PNG.

**Sketch.** `max_width`/`max_height` (default 1920/1080), `scale` param and
`WINDOWSMCP_SCREENSHOT_SCALE` env, `SKBitmap.Resize(..., SKFilterQuality.High)`; JPEG `quality`
param; response carries `originalWidth/Height`, `scale`, and the explicit multiply-by note.
Ties into A-7's metadata block.

**Done when.** A 3840×2160 capture returns ≤ 1920 wide with the correct scale factor reported.

### A-10 — Alternative capture backend (WGC / DXGI)  `P3 · M–L`
- [x] Done 2026-09-05 — [design note](design/A-10-capture-backend.md); in `CHANGELOG.md [Unreleased]`, ships with the next release (0.8.0 per roadmap C11)

**Upstream.** `desktop/screenshot.py` backend registry: `dxcam` (DXGI desktop duplication) →
`mss` → Pillow, selected by `WINDOWS_MCP_SCREENSHOT_BACKEND=auto|dxcam|mss|pillow`; the used
backend is echoed in the response.

**Ours (before A-10).** GDI `Graphics.CopyFromScreen` only — returns black for DRM/exclusive-fullscreen surfaces
and is slower on high-refresh multi-monitor setups.

**Sketch.** `IScreenCaptureBackend` with `Gdi` and `WindowsGraphicsCapture`
(`Windows.Graphics.Capture` via CsWinRT, works with HDR/accelerated content) or DXGI Desktop
Duplication; `WINDOWSMCP_SCREENSHOT_BACKEND=auto|gdi|wgc`; fall back on failure and report which
backend produced the frame.

**Done when.** A GPU-accelerated window that captures black under GDI captures correctly under
the alternative backend.

### A-11 — Cursor position in responses and drawn on captures  `P2 · S`
- [x] Done 2026-09-04 — [design note](design/A-11-cursor.md); in `CHANGELOG.md [Unreleased]`, ships with the next release (0.8.0 per roadmap C11)

**Upstream.** `Cursor Position: (x, y)` heads every snapshot/screenshot (`get_cursor_location`);
the cursor is highlighted in annotated images.

**Ours (before A-11).** Nothing reported the pointer: `InputService` called `GetCursorPos` only
to read back its own `SetCursorPos` (D-3), and no response or capture carried the cursor.

**Sketch.** `PInvoke.GetCursorPos` → `Cursor {X,Y, MonitorIndex}` in the A-2/A-7 metadata;
`GetCursorInfo` + `DrawIconEx` onto the captured bitmap when `include_cursor:true` (default
true), or a drawn ring when the real cursor cannot be composited.

**Done when.** Screenshot metadata reports the cursor and the image shows it.

### A-12 — Virtual desktops  `P3 · L`
- [x] Phase 1 done 2026-09-05 — [design note](design/A-12-virtual-desktops.md); in `CHANGELOG.md [Unreleased]`, ships with the next release. Phase 2 (the undocumented interface) is not planned.

**Upstream.** Every snapshot shows **Active Desktop** and **All Desktops** (names). `vdm/core.py`
wraps the documented `IVirtualDesktopManager` (is-window-on-current, window's desktop GUID) and
the **undocumented, per-build** `IVirtualDesktopManagerInternal` (create/remove/rename/switch,
move window) with names read from
`HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops\{guid}\Name`.
Only current/all are wired into tools; the rest are library functions.

**Ours (before A-12).** Nothing. Now `window(action:"desktops")` and `DesktopId` on every listed window — see the design note (on this Windows 11 build the registry has no `VirtualDesktopIDs`; the service falls back to the `Desktops` subkeys and to the foreground window's desktop).

**Sketch.** Phase 1 (safe): documented `IVirtualDesktopManager` (`CLSID_VirtualDesktopManager`)
to tag A-1 windows with `DesktopId`, plus registry names → `virtual_desktop(action=list|current)`.
Phase 2 (optional): switch/create/remove/rename/move via the internal interface behind
build-number detection with graceful "unsupported on this build" failure; `remove` needs
`confirm:true`. Observe the vtable-gap rule.

**Done when.** Phase 1: window list shows which desktop each window is on and the current desktop
name.

### A-13 — Unicode hygiene  `P2 · S`
- [x] Done 2026-09-04 — [design note](design/A-13-unicode-hygiene.md); in `CHANGELOG.md [Unreleased]`, ships with the next release (0.8.0 per roadmap C11)

**Upstream.** `desktop/utils.py` `remove_private_use_chars()` strips U+E000–U+F8FF (VS Code's
codicons in element names) and `repair_surrogates()` fixes lone UTF-16 surrogates (emoji in window
titles) before any string reaches the JSON encoder — one bad title used to take the whole snapshot
down.

**Ours (before A-13).** Names flowed straight from UIA into `JsonSerializer`. Measured on
.NET 10: a lone surrogate does **not** throw, it is silently written as U+FFFD — a lossy rewrite
the model cannot see — and PUA glyphs passed through as token noise. Now every name, value,
`get_text` result, table header/cell and `assert_element` observation goes through
`UiText.Sanitize` (see the design note).

**Sketch.** A `UiText.Sanitize(string)` helper applied in `ToInfo`/`TryGetName` and to window
titles: strip PUA, replace lone surrogates with U+FFFD, trim control chars.

**Done when.** A window title containing an emoji and a VS Code sidebar both serialise cleanly.

### A-14 — Post-capture flash overlay and snapshot profiling  `P3 · M`
- [x] Done 2026-09-05 — [design note](design/A-14-flash-and-profiling.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `desktop/flash_overlay.py`: a layered, click-through, always-on-top window draws an
orange glow around the captured area for ~3.5 s **after** capture (torn down before the next
capture so it never appears in an image); `WINDOWS_MCP_DISABLE_FLASH` turns it off.
`WINDOWS_MCP_PROFILE_SNAPSHOT` logs per-stage timings (context, tree, region filter, capture,
resize, build).

**Ours (before A-14).** Neither. Now `--flash on|off` / `WINDOWSMCP_FLASH` (the parser has no
valueless flags, so the sketch's `WINDOWSMCP_DISABLE_FLASH` shipped as `--flash off`) and
`--profile-snapshot on|off` / `WINDOWSMCP_PROFILE_SNAPSHOT` — see the design note.

**Sketch.** Flash: `WS_EX_LAYERED|WS_EX_TRANSPARENT|WS_EX_TOPMOST` window with
`UpdateLayeredWindow` on a dedicated thread; env `WINDOWSMCP_DISABLE_FLASH`. Profiling:
`Stopwatch` per stage → stderr at Debug level when `WINDOWSMCP_PROFILE_SNAPSHOT` is set.

**Done when.** A capture shows the glow and the next capture does not contain it.

---

## B — Input, apps, and window ergonomics

### B-1 — `type`: target, clear, caret, press_enter, long-text paste  `P1 · M`
- [x] Done 2026-09-06 — [design note](design/B-1-type.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `Type(text, loc|label, clear, caret_position=start|idle|end, press_enter)`:
clicks the target, moves the caret (Home/End), clears with Ctrl+A + Backspace, then either
per-key `SendKeys` with escaping and 40 ms pacing (short text or text containing `\n`, `\t`,
`{`, `}`) or a **clipboard paste** for long plain text that restores the previous clipboard
(`desktop/service.py` `type()` ~716, `_paste_text()` ~747, `_escape_text_for_sendkeys()`).

**Ours (before B-1).** `type(text)` = `Keyboard.TextEntry` into whatever has focus; no targeting, no clear, no
enter, no long-text strategy (long `TextEntry` bursts drop keys in some apps).

**Sketch.** `TypeOptions(int? X, int? Y, string? ElementId, bool Clear, Caret Caret,
bool PressEnter, int? PaceMs)`; click/focus target first (element via D-2 focus, or physical
click); clear via `ctrl+a`,`backspace`; paste path when `text.Length ≥ threshold` and no control
chars — set clipboard through `IClipboardService`, `ctrl+v`, restore prior text; `\n` → Enter,
`\t` → Tab when typing per key. Return `{typed, method: keys|paste}`.

**Touches.** `Abstractions/IInputService.cs`, `Models/InputDtos.cs`, `Services/InputService.cs`,
`Tools/InputTools.cs`, tests (mock clipboard; `UIAutomation` test types into Notepad and reads
back via `get_text`).

**Done when.** `type("hello", element_id, clear=true, press_enter=true)` replaces a field's
content and submits; 5 000 characters arrive intact.

### B-2 — `drag`: duration, intermediate motion, from current cursor  `P2 · S`
- [x] Done 2026-09-06 — [design note](design/B-2-drag.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `Move(loc, drag=true, from_loc?, duration≤10s)` → `uia.DragDrop(cx,cy,x,y,
moveSpeed=1, duration)` with interpolated intermediate points; `from_loc` omitted = start at the
current cursor (`drag()` ~829).

**Ours (before B-2).** `DragAsync` = button down, one absolute jump, button up (`InputService.cs:74`). Many
targets (file managers, canvases, DnD in browsers) need intermediate `WM_MOUSEMOVE`s to
recognise a drag.

**Sketch.** `from_x/from_y` optional (default `GetCursorPos`); `duration_ms` (cap 10 000) and
`steps` (default ~20): press, then `steps` interpolated moves with `duration/steps` delays, small
initial nudge to exceed `SM_CXDRAG`, release. Keep the middle-button rejection.

**Done when.** Dragging a file between two Explorer windows works; a Notepad text drag-select
works.

### B-3 — `scroll` at current cursor or at an element  `P2 · S`
- [x] Done 2026-09-06 — [design note](design/B-3-scroll.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `Scroll(loc=None)` scrolls at the current mouse position; `label` targets an
element; `type=horizontal` uses Shift+wheel for apps without horizontal wheel support.

**Ours (before B-3).** `scroll(x, y, direction, amount)` — coordinates mandatory.

**Sketch.** Make `x,y` optional; add `element_id` (centre from Bounds / A-3); if horizontal scroll
has no effect, optional `use_shift_wheel:true`.

**Done when.** `scroll(direction="down")` with no coordinates scrolls under the cursor.

### B-4 — `click` by element id; `clicks=0` hover  `P2 · S`
- [x] Done 2026-09-06 — [design note](design/B-4-click-by-element.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `Click(loc|label, button, clicks 0|1|2)`; `0` = hover only.

**Ours (before B-4).** `click(x,y,button,clicks)`; separate `hover`. `interact_element` cannot physically
click (D-2).

**Sketch.** `element_id` alternative on `click` (resolve centre; refuse if `IsOffscreen`); accept
`clicks=0` as hover for parity. Mostly falls out of D-2.

### B-5 — Plain `wait` tool  `P1 · S`
- [x] Done 2026-09-05 — [design note](design/B-5-wait.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `Wait(duration)` sleeps N seconds (`tools/input.py` ~422). Agents call it
constantly between launch/click and the next snapshot.

**Ours (before B-5).** None. Agents fell back to `powershell("Start-Sleep 2")`, which pays a PowerShell
cold-start (seconds to tens of seconds under Defender — see `CLAUDE.md`) and takes the
serialization gate.

**Sketch.** `wait(seconds: double)` → `Task.Delay`, capped (e.g. 60 s), honour `CancellationToken`,
returns `"waited Ns"`. Annotate read-only/idempotent (C-7).

### B-6 — `wait_for` conditions and window filter  `P2 · M`
- [x] Done 2026-09-06 — [design note](design/B-6-wait-for-conditions.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `WaitFor(condition, text?, window_name?, timeout≤120s, interval≤5s, use_dom)`
with conditions `text_exists`, `active_window`, `element_exists`, `element_enabled`,
`focused_element` (aliases `text|window|element|enabled|focused`); polls the tree in-process;
returns elapsed + attempts + a detail string; raises `TimeoutError` with the last detail
(`tools/input.py` `_matches_wait_condition`, `_validate_wait_for_args`).

**Ours (before B-6).** `wait_for(text, timeout_ms, interval_ms)` — element-name text only, foreground only.
**Scope note:** the `window_name` filter is delivered by [D-5](design/D-5-find-path-resilience.md)
(`scope=foreground|window|desktop` on both `find_element` and `wait_for`); B-6 keeps the
`condition` enum and `use_dom`. Do not build the filter twice.

**Sketch.** `condition` enum + `window_title` filter + `use_dom` (A-5); evaluate against the A-2
snapshot (cheaper: A-1 window list for `active_window`); return
`{satisfied, elapsedMs, attempts, detail}`; timeout returns `satisfied:false` with detail rather
than throwing (keeps the existing contract of returning `null` on timeout — decide in spec).

**Done when.** `wait_for(condition="active_window", text="Notepad")` resolves after `launch`.

### B-7 — `multi_select` / `multi_edit` batch tools  `P2 · S–M`
- [x] Done 2026-09-06 — [design note](design/B-7-batch-tools.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `MultiSelect(locs|labels, press_ctrl=true)` holds Ctrl while clicking each point
(`multi_select()` ~868); `MultiEdit(locs=[[x,y,text],…]|labels=[[label,text],…])` clicks + types
each field (`multi_edit()` ~880). Both tolerate JSON-stringified lists (Claude Desktop quirk).

**Ours (before B-7).** None; agents issued N round-trips.

**Sketch.** `multi_select(points_json | element_ids, ctrl=true)` and
`multi_edit(entries_json)` where entries are `{x,y}|{element_id}` + `text`, reusing B-1's
typing path; return per-entry results; stop on first failure and report the index.

### B-8 — Launch by Start Menu name with fuzzy match and window wait  `P1 · M`
- [x] Done 2026-09-06 — [design note](design/B-8-launch-catalog.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `App(mode=launch, name)`: builds a name→AppID map from `Get-StartApps`
(CSV), falls back to scanning Start Menu `.lnk` folders, adds locale display names from
`shell:AppsFolder`; fuzzy-matches the request (`thefuzz` `extractOne`, score ≥ 70); launches a
path via `Start-Process -PassThru` (captures PID) or an AUMID via
`Start-Process shell:AppsFolder\<AUMID>` after `_check_app_exists`; then waits up to 10 s for a
window by PID, else by regex title; reports "launched" vs "sent, window not detected"
(`launch_app()` ~528, `get_apps_from_start_menu()` ~327, `app()` ~475).

**Ours (before B-8).** `launch(app_name)` = ShellExecute, returns a PID, no matching, no wait
(`WindowService.cs:63`). `ShortcutResolver` (IShellLink) already exists for the startup report.

**Sketch.** `IAppCatalogService` building the map from `Get-StartApps` (or the
`shell:AppsFolder` `IShellFolder` enumeration, no PowerShell) + Start Menu `.lnk` via
`ShortcutResolver`, cached with a TTL; a small Levenshtein/partial-ratio matcher (`internal`,
unit-tested); packaged apps via `IApplicationActivationManager.ActivateApplication(AUMID)`
(returns PID) or `explorer.exe shell:AppsFolder\<AUMID>`; wait loop over A-1's window list by PID
then fuzzy title; return `{matchedName, score, pid, hwnd, title, windowDetected}`. `launch` keeps
accepting a path.

**Done when.** `launch("calc")`, `launch("edge")`, `launch("visual studio code")` all open and
return the window handle.

### B-9 — Window resize / move  `P2 · S`
- [x] Done 2026-09-06 — [design note](design/B-9-window-bounds.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `App(mode=resize, name?, window_loc=[x,y], window_size=[w,h])` on a named or the
active window via `MoveWindow`; refuses minimized/maximized windows (`resize_app()` ~441).

**Ours (before B-9).** `window` has minimize/maximize/restore/close only.

**Sketch.** `window(action="move"|"resize"|"set_bounds", title?/hwnd?, x,y,w,h, restore_first)`
→ `SetWindowPos`; default target = foreground; error on minimized/maximized unless
`restore_first:true`; return new bounds.

### B-10 — Fuzzy window matching and robust bring-to-foreground  `P1 · M`
- [x] Done 2026-09-05 — [design note](design/B-10-window-matching.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `_find_window_by_name()` (~412) fuzzy-matches (score ≥ 70) over the snapshot's
window list; `switch_app()` restores from minimized; `bring_window_to_top()` (~574) tries
`SetForegroundWindow`+`BringWindowToTop`, then `AllowSetForegroundWindow(-1)`,
`AttachThreadInput` to the target thread (skipped when elevated/Access Denied), retries, and
reports "Restored … and switched" vs "Switched".

**Ours (before B-10).** `FindWindow(null, exactTitle)` + a bare `SetForegroundWindow` (`WindowService.cs:51`),
which Windows refuses when our process is not the foreground process — the common case for an
MCP server — so `switch_to_window`/`focus` often return `false` for a title that exists.

**Sketch.** Match: exact → case-insensitive substring → fuzzy over A-1's list; accept `hwnd` for
precision; foreground: `IsIconic`→`SW_RESTORE`; `SetForegroundWindow`; on failure
`AttachThreadInput(GetCurrentThreadId, GetWindowThreadProcessId(hwnd))` + `BringWindowToTop`
+ `SetForegroundWindow` + detach; last resort the ALT-key nudge (`keybd_event(VK_MENU)`);
return `{matchedTitle, score, strategy, restored}`.

**Done when.** `switch_to_window("notepad")` brings a window titled "Untitled - Notepad" to the
front from behind another app.

### B-11 — `start_process` with argv list and cwd  `P2 · S`
- [x] Done 2026-09-05 — [design note](design/B-11-start-process-argv.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `App(mode=launch_executable, executable, args=[…], cwd)` validates the exe and cwd
exist, uses `Popen([...], shell=False)` (no quoting bugs), returns `{pid, executable, args, cwd}`
(`tools/app.py` `_launch_executable`).

**Ours (before B-11).** `start_process(command)` — one command-line string, no cwd (`ProcessTools.cs:125`).

**Sketch.** Add `args_json` (`string[]`) → `ProcessStartInfo.ArgumentList`, `cwd`,
`use_shell_execute`; validate paths; keep `command` for backward compatibility.

### B-12 — `multi_monitor` detail: work area, orientation, DPI, scale  `P2 · S`
- [x] Done 2026-09-05 — [design note](design/B-12-monitor-detail.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `DisplayInventory` → index, device, primary, bounds, work_area, resolution,
orientation, effective_dpi, scale (`tools/display.py`, `uia.DisplayInfo`).

**Ours (before B-12).** `MonitorInfo(Index, DeviceName, X, Y, Width, Height, IsPrimary)`.

**Sketch.** Extend the record: `WorkArea` (`GetMonitorInfo.rcWork`), `Orientation`
(`EnumDisplaySettings.dmDisplayOrientation`), `EffectiveDpi` (`GetDpiForMonitor`,
`MDT_EFFECTIVE_DPI`), `Scale = dpi/96`. Requires Per-Monitor-V2 awareness (D-3) for physical
pixels.

---

## C — Files, registry, processes, notifications, scrape, shell

### C-1 — File tools: offset/limit, append, overwrite, recursive, pattern  `P2 · M`
- [x] Done 2026-09-06 — [design note](design/C-1-file-flags.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `FileSystem` modes (`tools/filesystem.py`, `filesystem/service.py`): `read` with
line `offset`/`limit`; `write` with `append` and parent creation; `copy`/`move` with `overwrite`
(refuse when the destination exists otherwise); `delete` with `recursive` (refuse non-empty dirs
otherwise); `list` with `pattern`, `recursive`, `show_hidden`; `search` glob; `info`. Relative
paths resolve from the user's Desktop.

**Ours (before C-1).** `file_read(max_bytes, encoding)`, `file_write(confirm)` (no append),
`file_manage(copy|move|delete|list)` with no overwrite/recursive/pattern flags. (We are ahead on
hashing, ADS, duplicates, archives, and confirm gates.)

**Sketch.** `file_read`: `offset_lines`, `limit_lines` (line window, 1-based like upstream);
`file_write`: `append`, `create_parents` (default true); `file_manage`: `overwrite` (default
false → error if exists), `recursive` (default false → error on non-empty dir), `list` gains
`pattern`, `recursive`, `include_hidden`. Decision: keep absolute-path-only (reject relative with
a clear message) — Desktop-relative resolution is a foot-gun; record the decision in the spec.

**Done when.** Each new flag has a unit test in `FileSystemServiceTests`/`FileToolsTests`.

### C-2 — Registry delete and subkey listing on the tool surface  `P2 · S`
- [x] Done 2026-09-06 — [design note](design/C-2-registry-delete.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `Registry(mode=delete, path, name?)` removes a value or, without `name`, the whole
key recursively; `mode=list` returns values **and** sub-keys in one call (`registry/service.py`).

**Ours (before C-2).** `registry_get` without `value_name` returned value names only; `RegistryService`
already implements `EnumerateValuesAsync` (`:22`) and `EnumerateSubKeysAsync` (`:49`) but no tool
exposes them; no delete at all.

**Sketch.** `registry_get(hive, path)` → `{values:[{name,kind,data}], subKeys:[…]}`; new
`registry_delete(hive, path, value_name?, recursive=false, confirm)` — whole-key delete requires
`confirm:true` **and** `recursive:true` when it has subkeys. Update the Safety-rails list.

### C-3 — Process list CPU %, sort, limit; graceful kill  `P2 · M`
- [x] Done 2026-09-06 — [design note](design/C-3-process-cpu-graceful-kill.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `Process(list, name?, sort_by=memory|cpu|name, limit=20)` prints PID, name,
CPU %, memory (psutil, fuzzy name filter > 60); `kill(force)` = `terminate()` (graceful) vs
`kill()` (`process/service.py`).

**Ours (before C-3).** `process(list)` had memory, path, lineage, orphan detection (ahead), but
no CPU column, sort, or limit; kill was always hard.

**Sketch.** CPU: two `TotalProcessorTime` samples ~250 ms apart normalised by core count, or
`Win32_PerfFormattedData_PerfProc_Process` via `IWmiService`; `sort_by`, `limit`; kill:
`graceful:true` → `CloseMainWindow()` / `WM_CLOSE`, wait N s, then `Kill()`; keep `confirm`.

### C-4 — Notification `app_id` (AUMID)  `P3 · S`
- [x] Done 2026-09-06 — [design note](design/C-4-notification-app-id.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `Notification(title, message, app_id)` — the AUMID is mandatory because Windows
uses it as toast identity (`notifications/service.py`).

**Ours (before C-4).** `NotificationService.cs:25` hard-coded `'Windows-MCP'` in a PowerShell script. Unregistered AUMIDs are dropped
on some builds.

**Sketch.** Optional `app_id` param (default keeps the current value); document the registration
requirement; optionally register a Start Menu shortcut carrying our AUMID on first use.

### C-5 — `scrape`: DOM source, query focus, MCP-sampling summary  `P2 · M`
- [x] Done 2026-09-08 — [design note](design/C-5-scrape-dom-summary.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `Scrape(url, query?, use_dom=false, use_sampling=true)`: HTTP fetch → markdownify,
**or** the active browser tab's DOM text with "Reached top / Scroll down to see more" hints
(`use_dom`); then, if the client supports sampling, `ctx.sample()` summarises the raw content with
a boilerplate-stripping system prompt focused on `query`; falls back to raw when sampling is
unsupported (`tools/scrape.py`).

**Ours (before C-5).** `scrape(url)` → HTML→Markdown via ReverseMarkdown, private IPs rejected.

**Sketch.** `source: http|dom` (dom via A-5), `query`, `summarize:true` → server-initiated
sampling through the SDK (`IMcpServer` sampling request — verify the 2.2.0 API and check the
client's `sampling` capability first), `max_chars` truncation for raw output.

### C-6 — `powershell`: per-call timeout; environment rebuild from registry  `P2 · S–M`
- [x] Done 2026-09-08 — [design note](design/C-6-powershell-timeout-path.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `PowerShell(command, timeout=30)` with `run_with_graceful_timeout`;
`powershell/service.py` `_read_reg_env()`/`_dedup_path()` rebuild the child environment
(HKLM `Session Manager\Environment` + HKCU `Environment`, `REG_EXPAND_SZ` expanded, PATH merged
and de-duplicated) because MCP hosts frequently spawn the server with a stripped environment,
which makes `git`, `node`, etc. "not found" inside tool calls.

**Ours (before C-6).** 15-min backstop + `background:true` jobs (ahead), but no per-call
`timeout_seconds`; `PowerShellInvocation.cs` does not touch the environment.

**Sketch.** `timeout_seconds` (≤ backstop) → cancel + tree-kill; environment: when the inherited
`PATH` lacks `%SystemRoot%\System32` or is empty, rebuild from the registry as above and inject
into `ProcessStartInfo.Environment` (foreground and jobs share `PowerShellInvocation`).

### C-7 — Tool annotations on all 69 tools  `P2 · S`
- [x] Done 2026-09-06 — [design note](design/C-7-tool-annotations.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** Every tool declares `ToolAnnotations(title, readOnlyHint, destructiveHint,
idempotentHint, openWorldHint)` — clients use them for auto-approve and confirmation UX.

**Ours (before C-7).** `[McpServerTool]` everywhere with no properties set.

**Sketch.** `[McpServerTool(Name=…, Title=…, ReadOnly=…, Destructive=…, Idempotent=…,
OpenWorld=…)]` — verify property names on `McpServerToolAttribute` in SDK 2.2.0. Classification
table in the plan (read-only: screenshot, ocr, get_*, find_element, system_info, …; destructive:
file_write, file_manage, registry_set, process kill, service, power_action, firewall, env set;
open-world: scrape, http_request, powershell, network). Add a `ServerInfoTests` assertion that
every listed tool has explicit annotations.

---

## S — Server, transport, packaging

### S-1 — Tool allow/deny lists  `P2 · S`
- [ ] Not started

**Upstream.** `--tools A,B` (explicit set, overrides) / `--exclude-tools X,Y` (+ env
`WINDOWS_MCP_TOOLS`/`_EXCLUDE_TOOLS`, `[tools] exclude=[…]` in config) filtered in
`__main__.py` `_apply_tool_filter()`; unknown names error at startup. Lets an operator run a
screenshot-only or no-PowerShell server.

**Ours.** All 69 tools always.

**Sketch.** `ServerOptions` gains `Tools`/`ExcludeTools` (flags + `WINDOWSMCP_TOOLS`/
`WINDOWSMCP_EXCLUDE_TOOLS`, valid for both transports); in `WindowsMcpHost.AddWindowsMcp` filter
`McpServerOptions.ToolCollection` after `WithToolsFromAssembly()`; validate names against the
discovered set; `--help` documents it; `ServerOptions` unit tests + an `HttpTransportTests`
case listing tools with an exclusion.

### S-2 — IP allowlist  `P2 · S`
- [ ] Not started

**Upstream.** `--ip-allowlist 10.0.0.0/8,192.168.1.5` (IPv4/IPv6, CIDR) →
`IPAllowlistMiddleware` (403 for others, `/health` exempt) — `infrastructure/security.py`.

**Ours.** README tells the operator to scope a firewall rule.

**Sketch.** `--ip-allowlist` / `WINDOWSMCP_IP_ALLOWLIST` parsed to `IPNetwork` list (HTTP mode
only); `app.Use` **before** the bearer gate; use `HttpContext.Connection.RemoteIpAddress` (do not
trust `X-Forwarded-For` unless a `--trust-proxy` flag is added); exempt `/health` (S-8);
`HttpTransportTests`: allowed loopback passes, `203.0.113.0/24`-only config yields 403.

### S-3 — CORS origins  `P3 · S`
- [ ] Not started

**Upstream.** `--cors-origins https://a.example` → CORS middleware; none emitted by default.

**Sketch.** `--cors-origins` / env → `AddCors` + `UseCors` only when set; document that
browser-based MCP clients need it and native clients do not.

### S-4 — OAuth 2.0 + PKCE (or external-IdP bearer)  `P3 · L`
- [ ] Not started

**Upstream.** `infrastructure/oauth.py`: in-process authorization server — RFC 8414 metadata at
`/.well-known/oauth-authorization-server`, `/oauth/authorize` (code + PKCE S256 required),
`/oauth/token`, pre-provisioned confidential client (`--oauth-client-id/secret`), 1 h tokens,
5 min codes, in-memory store; `AuthKeyMiddleware` accepts the static key **or** a valid OAuth token.

**Ours.** Static bearer only (constant-time compare in `WindowsMcpHost.IsAuthorized`).

**Sketch.** Two options for the spec to weigh: (a) port the minimal AS as above (self-contained,
single-process); (b) `Microsoft.AspNetCore.Authentication.JwtBearer` validating tokens from an
external IdP (Entra ID, Auth0) + the SDK's protected-resource metadata support — less code, more
standard. Either way keep the static key path working and unchanged for stdio.

### S-5 — PEM cert/key files and a self-signed generator  `P2 · S–M`
- [ ] Not started

**Upstream.** `--ssl-certfile/--ssl-keyfile` (PEM) and `auth --with-tls` which generates a
self-signed pair into `~/.windows-mcp/` (`_gen_tls`).

**Ours.** `--cert-thumbprint` from the certificate store only (better integrated with Windows,
but PEM files are what most people already have from mkcert/Let's Encrypt).

**Sketch.** `--cert-file` + `--key-file` → `X509Certificate2.CreateFromPemFile` (mutually
exclusive with `--cert-thumbprint`); `WindowsMcp.exe gen-cert --dns <host> [--out dir]`
creates a self-signed cert in `CurrentUser\My`, prints the thumbprint, exports `.cer` and `.pem`
for the client. `ServerOptions` tests; `HttpTransportTests` HTTPS case with a PEM pair.

### S-6 — Config file and `auth` helper  `P2 · M`
- [ ] Not started

**Upstream.** `~/.windows-mcp/config.toml` (`[server]`, `[security]`, `[tools]`) loaded by
`infrastructure/config.py`, precedence CLI > config > default, strict type validation;
`windows-mcp auth` generates a 32-byte key, saves it, and prints ready-to-paste client JSON for
stdio / SSE / Streamable HTTP.

**Ours.** CLI + `WINDOWSMCP_*` env only (`Hosting/ServerOptions.cs`).

**Sketch.** `%USERPROFILE%\.windows-mcp\config.json` (or `%LOCALAPPDATA%\WindowsMcp\config.json`;
JSON — .NET has no built-in TOML), `--config <path>`; precedence CLI > env > file > default,
still inside the pure `ServerOptions.Parse` (pass the file contents in, no I/O in the parser);
`WindowsMcp.exe auth [--transport http] [--port] [--bind] [--with-tls]` writes the key and prints
the `.mcp.json` / `claude mcp add` snippets. Keep "no args = stdio" untouched.

### S-7 — `install` / `uninstall` at-logon task  `P2 · M`
- [ ] Not started

**Upstream.** `windows-mcp install [--transport] [--host] [--port]` registers an **at-logon**
scheduled task for the current user via `Register-ScheduledTask … -RunLevel Limited` (no
elevation), runs it immediately, logs to `~/.windows-mcp/server.log`; `uninstall` ends and deletes
it (`__main__.py` `install()`/`uninstall()`).

**Ours.** Manual. Note `CLAUDE.md`/README: input, screenshot, window and UIA tools need the
**interactive session** — an at-logon task in the user's session is fine; a service or Session-0
task is not.

**Sketch.** `WindowsMcp.exe install --transport http --port … [--bind 127.0.0.1]` using the
already-referenced `TaskScheduler` NuGet (`LogonTrigger`, `TaskRunLevel.LUA`,
`ExecutionTimeLimit = 0`, stdout/stderr → `%LOCALAPPDATA%\WindowsMcp\logs`); requires an API key
unless loopback (reuse the S-6 config); `uninstall` stops + deletes; `status` prints the task
state and the running image path (the `Get-CimInstance Win32_Process` check from `CLAUDE.md`).

### S-8 — Unauthenticated `/health`  `P2 · S`
- [ ] Not started

**Upstream.** `/health` is exempt from auth and the IP allowlist (`_PUBLIC_PATHS`), used by
monitors and the `install` flow.

**Ours.** The bearer gate covers every path by design.

**Sketch.** `GET /health` → `{status:"ok", version, transport, uptimeSeconds}` mapped **before**
the gate; no tool list, no hostname; `HttpTransportTests` asserts 200 without a key and that
`/mcp` still 401s.

### S-9 — Claude Desktop Extension (`.mcpb`) and MCP registry `server.json`  `P3 · M`
- [ ] Not started

**Upstream.** `manifest.json` (manifest 0.4, `server.type: uv`, `user_config` toggles for
telemetry/profiling/backend/debug/watchdog mapped to env) packaged as an `.mcpb`, and
`server.json` for the official MCP registry (`io.github.CursorTouch/Windows-MCP`).

**Ours.** Claude Code plugin only (`.claude-plugin/plugin.json`, `bundle/WindowsMcp.exe`).

**Sketch.** `manifest.json` with `server.type: "binary"`, `entry_point: bundle/WindowsMcp.exe`,
`user_config` for `WINDOWSMCP_SCREENSHOT_SCALE`, `WINDOWSMCP_MAX_TREE_ELEMENTS`, backend, debug;
a `pack-mcpb` script; `server.json` naming `io.github.<owner>/Windows-mcp`; document both in
README.

### S-10 — Per-tool black-box tester skill  `P3 · S`
- [ ] Not started

**Upstream.** `.claude/skills/windows-mcp-tool-tester/SKILL.md`: one tool per run, cases derived
only from the MCP schema, correctness + latency, mandatory side-effect verification, PID-scoped
cleanup, VM/Sandbox recommendation for destructive tools.

**Ours.** `skills/windows/SKILL.md` is a usage playbook, not a tester.

**Sketch.** `skills/windows-tool-tester/SKILL.md` adapted to our 69 tools and `confirm:true`
gates; wire into the plugin manifest.

### S-11 — AI-control indicator and takeover hotkey  `P2 · M`
- [x] Done 2026-10-21, merged in PR #32 — [design note](design/S-11-control-indicator.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream (0.8.8, PR #445; policies and opt-out added after it, PRs #458/#460/#466).** A
desktop-control layer:
- an owner gate on tool calls, with relaxed/balanced/strict policies;
- physical input blocked through low-level hooks while the AI acts;
- a takeover on a large mouse movement or Ctrl+Alt+Shift+Backspace, until 10 s of idle;
- a blue edge and cursor glow plus a top notice, and a `ControlStatus` tool.

Two bugs followed within a week: #456 (the gate latched forever on a missed key-up) and #457 (the
agent was refused whenever the user touched the mouse).

**Ours (before S-11).** Nothing; the pointer moved with no explanation. Now
`--control-indicator on|off` / `WINDOWSMCP_CONTROL_INDICATOR` (default on, both transports) and
`--control-hotkey <combo>` / `WINDOWSMCP_CONTROL_HOTKEY` (default `ctrl+alt+shift+backspace`;
ctrl, alt or win must be written) — see the design note.

**Decision (2026-10-21, deliberate deviation).** The indicator only:
- a cursor ring and a hotkey banner while a driving tool runs, fading 5 s after the last one;
- takeover by the key combination alone, which hides the indicator and adds a one-time note to
  the AI's next tool result;
- **no** gating of tool calls, **no** input blocking, **no** `ControlStatus` tool.

See the design note for the shape and the requirement matrix.

**Done when.** A `click` shows the ring and banner and they fade 5 s later. No capture contains
them. The hotkey hides them, and the next result carries the note exactly once. `--control-indicator
off` shows nothing.

---

## E — Agent efficiency (not upstream parity)

Added 2026-11-10 from a review of this server's own tool surface: where an AI client spends extra
round trips or tokens, or cannot do something a person at the machine can. No item here has an
upstream counterpart, so there is no **Upstream.** paragraph. Every "Ours today" statement was
checked against the source on that date and none was exercised live. The preference throughout is
a parameter on an existing tool over a new tool — E-6 and E-9 are the only two that raise the tool
count (69 → 71), which moves the counts in `README.md`, `docs/architecture/*`,
`skills/windows/SKILL.md`, `ToolInventoryTests` and C-7's annotation table.

### E-1 — Server instructions in the MCP handshake  `P1 · S`
- [x] Done 2026-11-12 — in `CHANGELOG.md [Unreleased]`, ships with the next release

**Ours today.** `AddMcpServer` in `WindowsMcpHost.AddWindowsMcp` sets `ServerInfo` only. The
guidance on how to drive the server (start with `snapshot`, prefer `el_N` ids, re-snapshot after
acting, `wait_for` over `wait`, read `Truncated`, stop on a takeover note) lives in
`skills/windows/SKILL.md`, which reaches a Claude Code plugin install and nobody else: an HTTP
client, Claude Desktop or any other MCP client gets 69 tool descriptions and no loop.

**Sketch.** A constant of about 30 lines condensed from the skill's UI loop and safety rails, set
as the server's instructions in `AddMcpServer`, so both transports send it in the `initialize`
result. Confirm the option's name against the pinned `ModelContextProtocol` 2.2.0 before RED.
Keep it short: it is paid for in every session.

**Touches.** `Hosting/WindowsMcpHost.cs`, a new `Hosting/ServerInstructions.cs`,
`tests/.../ServerInfoTests.cs`, `tests/.../HttpTransportTests.cs`.

**Tests.** `initialize` over the in-process HTTP host carries the instructions; they name only
tools that exist (checked against the discovered set, so a rename cannot leave them stale); a
length ceiling.

**Done when.** A client that has never seen the skill is told the snapshot → act → confirm loop at
connect time.

**As landed:** the option is `McpServerOptions.ServerInstructions`, set to
`Hosting/ServerInstructions.Text` in `WindowsMcpHost.AddWindowsMcp`, so stdio and HTTP send the
same `instructions` in the `initialize` result (`HttpTransportTests` reads it through the SDK
client and from the raw JSON-RPC body; `StreamTransportTests` over the stdio wiring). The text is
16 lines, not 30: 1 980 characters of printable ASCII with `\n` breaks against a 2 000-character
ceiling — **twenty characters of headroom**, so any addition is a trade. It is a set of claims pinned
by `ServerInstructionsTests` rather than prose: every back-ticked word is a discovered tool; every
parameter named is in that tool's schema; the never-ask list (`powershell`, `archive`,
`file_manage` with overwrite, `scheduled_task` create or run, `service` start, `job` cancel,
`window` close, `http_request`) is exactly every `Destructive` tool with no `confirm` parameter
plus the ungated actions of gated tools, and each item is shown to run without `confirm`; the
takeover sentence matches the real note fragment by fragment; the calls named as retiring
snapshot ids are the ones that do (`WaitForServiceTests`, `WebToolsTests`); the quoted
`"An error occurred invoking ..."` is what an unexpected fault reads as over the production
wiring. Changing a tool's parameters, its confirm gate or what evicts snapshot ids therefore means
changing the sentence — the suite turns red until it is. `ServerInfoTests` was not touched.
Neighbour gaps found while pinning the text are D-19 … D-22; the same review corrected
`skills/windows/SKILL.md`'s "Confirm before destructive tools" rail, which claimed every listed
tool was gated.

### E-2 — `interact_element`: expand, collapse, set_value, scroll_into_view  `P1 · S`
- [x] Done 2026-11-12 — in `CHANGELOG.md [Unreleased]`, ships with the next release

**Ours today.** Six verbs: click, invoke, toggle, select, focus, type. The snapshot already reads
`Expand`, `RangeValue`, `RangeMin` and `RangeMax` for every element and prints a `slide` action
hint, but nothing can write them: a tree node or a collapsed pane can only be opened by a physical
click, a slider only by dragging, and an off-screen element is refused by `click` with wheel-scroll
and re-snapshot as the only remedy. (`select` with a value calls `Expand()` internally — the
pattern is in reach.)

**Sketch.** `expand` / `collapse` through `ExpandCollapsePattern`, reporting the state afterwards.
`set_value` through `RangeValuePattern` for a numeric `value` — out of range is refused naming the
minimum and maximum — falling back to a writable `ValuePattern`. `scroll_into_view` through
`ScrollItemPattern`; many apps do not implement it, so the refusal must say to scroll the nearest
scrollable ancestor (name its id when the walk has one). All refusals use D-10's caller-facing type.

**Touches.** `Services/UIAutomationService.cs` (`InteractOnSta`), `Tools/UIAutomationTools.cs`
(description and the unknown-action message), `skills/windows/SKILL.md`.

**Tests.** Each verb on a fake with and without the pattern; `set_value` at, below and above the
range and with a non-numeric value on a range control; the `Method`/`Detail` strings.

**Done when.** A tree node expands, a slider moves to a number, and an off-screen list row comes
into view, each in one call with no pointer movement.

**As landed:** ten verbs; the four new ones live in `UIAutomationService.InteractOnSta` with
helpers `SetRangeValue`, `SetTextValue`, `SetOrRejected`, `NoScrollItem` and
`NearestScrollableAncestor`. `expand` / `collapse` read `ExpandCollapseState` first — already in
the asked state is a success (`already Expanded`) and is not flipped, a leaf is refused — and
report the state re-read (`now Expanded`). `set_value` prefers `RangeValuePattern`: read-only is
refused before the value is parsed; the value must be a finite invariant-format number inside the
provider's bounds (a NaN or inverted bound is treated as unknown); `Detail` is the value re-read,
`now 30 (range 0 to 100)`. The bounds are the provider's: the Win32 trackbar proxy reports 0-100
**percent of the trackbar's own range** (a 5..50 trackbar reads 0..100), which the description now
states. Without a range it writes a writable `ValuePattern` and **verifies** it — read before and
after, `now '<shown>'`, and a write that was accepted but changed nothing is refused (a WinForms
slider, a drop-down list given a non-item); a password field is never read or echoed and an
unreadable control yields `replaced the whole value`. A provider rejection (`ArgumentException`,
`Win32Exception`) becomes a refusal naming the control. No focus call and no typing fallback — but
the window can still move to the front: UIA's Win32 proxies activate the control's
window on `ValuePattern.SetValue` (EDIT), `RangeValuePattern.SetValue` (trackbar) and `Expand`
(TreeView), so the description says a provider may bring the window forward. `scroll_into_view`
uses `ScrollItemPattern` (`now on screen, centre (x,y)` or `the control still reports offscreen`)
and is the one verb allowed on a disabled control; without the pattern an element already on
screen succeeds with `Method` `None`, otherwise the refusal names the nearest ancestor with
`ScrollPattern` **and** a scrollable axis (up to 25 levels up the control view) by a freshly issued
`el_N` for `scroll`, and a timeout in that walk propagates (R23). `""` is "no value" for
`expand`/`collapse`/`scroll_into_view`. Beyond the sketch: `ElementTarget.CentreOf`'s off-screen
refusal (`click`/`type`/`scroll`/`drag`/`multi_select`/`multi_edit` by element) now leads with
`interact_element scroll_into_view`, and names in those refusals and in `Describe` are clipped to 80
characters (`UiRefusals.Clip`). Tests: `UIAutomationServiceInteractVerbsTests` (Unit, fakes),
`UIAutomationServiceInteractVerbsIntegrationTests` and
`UIAutomationServiceInteractVerbsRealPatternTests` (Integration, on real Win32 controls in
`Fixtures/OwnedPatternControlsWindow.cs` with a strict input mock — the "no pointer movement" bar),
plus rows in `UIAutomationServiceRefusingTests`, `UIAutomationToolsTests`, `ElementTargetTests` and
`InputToolsClickTests`. Neighbour gaps found and not fixed are D-23 … D-27.

### E-3 — `click` modifiers; `key` repeat and hold  `P2 · S`
- [x] Done 2026-11-12 — in `CHANGELOG.md [Unreleased]`, ships with the next release (not verified live)

**Ours today.** `click` takes a button and a click count and no modifiers; Ctrl-click exists only
inside `multi_select`, and Shift-click (range select) and Alt-click nowhere. `key` presses one key
once, so ten Down-arrows are ten calls. `IInputService.KeyDownAsync`/`KeyUpAsync` exist (B-7) but
are not on the tool surface.

**Sketch.** `click(modifiers:"ctrl+shift")` holds the named keys around the click and releases
them in a `finally`, as `multi_select` does. `key(repeat:N, hold_ms:M)`: `repeat` 1–100 with the
existing inter-key pacing; `hold_ms` keeps one key down for that long. No free-standing
key-down/key-up tools: a modifier left down by a client that never sends the release is the #456
class of bug upstream hit.

**Touches.** `Tools/InputTools.cs`, `Services/InputService.cs`, `skills/windows/SKILL.md`.

**Tests.** The key-down/up ordering around the click on a recording `IInputService`; release on a
failed click and on cancellation; `repeat` and `hold_ms` bounds; an unknown modifier is refused
before any input is sent.

**Done when.** Shift-click selects a range and `key("down", repeat:10)` is one call.

**As landed:** all in `Tools/InputTools.cs`; `Services/InputService.cs` and `IInputService` are
unchanged (B-7's `KeyDownAsync`/`KeyUpAsync` carry the holds). `click(modifiers:…)` is parsed by
`ParseModifiers` — `ctrl` (alias `control`), `shift`, `alt`, joined with `+`, any case or order,
duplicates collapse — **before** the target is resolved, so anything else (`win` deliberately: a
Win key released alone opens Start), an empty part, or a modifier with `clicks:0` is refused with
nothing sent. Beyond the sketch: with modifiers the pointer is moved to the target first
(`HoverAsync`), so a point off every monitor is refused before a bare Alt or Shift tap; the keys go
down in the fixed order ctrl, shift, alt whatever order the caller wrote. `HoldingAsync` replaces
the sketch's plain `finally`: every key sent a key-down — including one whose key-down threw — is
released in reverse on `CancellationToken.None`, a failed release is retried once, a failure of the
click wins unchanged, and a release that fails twice after a sent click becomes an
`InvalidOperationException` saying the click was sent and naming the keys that may still be down.
The result gains `modifiers` (canonical `ctrl+shift`, or `null`). `key(repeat:N)` (1-100) presses
5 ms apart (`RepeatPace`, the pace `type` defaults to) and returns `pressed <key> N times`; a press
that fails after at least one landed reports `pressed i of N times`. `key(hold_ms:M)` (0-10000) is
key-down, `Delay`, key-up through `HoldingAsync` and returns `held <key> for M ms`. The two cannot
be combined; for either, the key is resolved through `ShortcutParser.ResolveKey` before any press,
every name the parser maps to ctrl, shift, alt or win (left/right variants included, `IsModifier`)
is refused — a held modifier is the free-standing key-down this item excluded, and repeating one
can trigger Sticky Keys — and a held key must need no implied modifier on the active layout
(letters are case-insensitive). Both tools now take the request's cancellation token. Tests:
`InputToolsClickModifiersTests` and `InputToolsKeyRepeatHoldTests` (Unit, a recording
`IInputService`; the `Delay` seam keeps them from sleeping). **The "done when" bar is met by the
unit tests only:** a real Shift-click range select and `key("down", repeat:10)` on a live desktop
were not run (they are e2e rows CLICK-08 and KEY-08). Neighbour gaps found by the review and not
fixed are D-28 … D-32.

### E-4 — `find_element` filters: automation id, control type, class, within, paging  `P2 · M`
- [ ] Not started

**Ours today.** `find_element` matches a case-insensitive substring of the element's Name, filtered
by four coarse kinds, and returns at most 20 with no way to ask for the rest. `ElementInfo` carries
no AutomationId and no ClassName, so two identically named controls cannot be told apart and a
search cannot be confined to a dialog or pane the client already holds an id for. Discovery itself
works: an empty `text` matches every element the kind and scope allow, so an unnamed control (most
icon buttons) does come back — but only as one of up to 20 anonymous rows the client must tell
apart by bounds, with no way to ask for *that* control by a stable property. What is missing is
precise targeting and paging, not the ability to see the control. `snapshot` has the same limit:
its scopes are desktop, foreground and window.

**Sketch.** `ElementInfo` gains `AutomationId` and `ClassName` (omitted from JSON when empty, so
existing output is unchanged). `find_element` gains `automation_id` (exact), `control_type`
(exact UIA type name), `class_name`, `within` (an element id: search that subtree only),
`max_results` (default 20, capped) and `offset`, and reports the total matched so truncation is
visible. `wait_for`'s element conditions take the same filters. Optionally `snapshot(root:<id>)`
walks one subtree — its own item if it grows.

**Touches.** `Abstractions/Models/UIAutomationDtos.cs`, `Abstractions/IUIAutomationService.cs`
(a request record instead of a seventh positional parameter), `Services/UIAutomationService.cs`,
`Tools/UIAutomationTools.cs`.

**Tests.** Each filter alone and combined; `within` on a stale id; paging is stable across two
calls on an unchanged tree; an empty `text` with `automation_id` returns exactly the one unnamed
control, not the first 20 of everything.

**Done when.** An unnamed toolbar button is addressed directly by automation id inside a named
dialog, as a single match.

### E-5 — `observe`: action tools return the state they produced  `P1 · M`
- [ ] Not started

**Ours today.** `click`, `type`, `key`, `shortcut`, `scroll`, `drag` and `interact_element` return
an echo of what they did. Learning what happened costs a `wait_for` or `wait` and then a
`snapshot`, so one UI step is three calls and three model turns.

**Sketch.** A shared `observe` parameter on those tools: `none` (default — today's output,
byte-identical), `foreground`, or `window` (the window the target element belongs to), with
`settle_ms` (default about 250, capped). After the action the tool waits `settle_ms`, takes a
snapshot of that scope and appends its compact text as a second block after the action's JSON.
It is a real snapshot: it evicts the previous snapshot's ids and issues new ones, and the
description must say so. One helper shared by every tool, not seven copies. A failed observation
never fails the action: the action result is returned with a note.

**Touches.** `Tools/InputTools.cs`, `Tools/UIAutomationTools.cs`, a new `Tools/Observation.cs`,
`skills/windows/SKILL.md` (the loop becomes act-with-observe), a design note.

**Tests.** `observe:none` output unchanged for every tool; the second block is the renderer's
output for the right scope; the snapshot runs after the input, not before; a throwing snapshot
yields the action result plus a note; `settle_ms` bounds.

**Done when.** Clicking a menu returns the opened menu's items in the same call.

### E-6 — `sequence`: an ordered batch of UI steps in one call  `P2 · M`
- [ ] Not started

**Ours today.** `multi_select` (Ctrl-clicks) and `multi_edit` (click-then-type) are the only
batches. A menu path, a wizard page or a keyboard-driven dialog is one call per step.

**Sketch.** A new tool `sequence(steps_json, observe?)`: an ordered array of `{tool, args}` drawn
from a fixed allowlist of input, UIA and wait tools (click, type, key, shortcut, scroll, drag,
interact_element, wait, wait_for, assert_element) — never `powershell`, file, registry or process
tools. Steps run in order and stop at the first failure, or at the first `wait_for` that is not
satisfied or `assert_element` that fails; the result carries per-step results, `failedIndex` and
`error` in the shape B-7 uses, plus a final E-5 observation. Targets are resolved per step, not
up front, because each step changes the screen; ids must therefore come from elements that survive
the sequence, or steps address by coordinates. **A takeover (S-11) during the sequence aborts the
remaining steps** — decide the mechanism in the design note before RED. Register the tool in
`DrivingTools.Names`; annotations per C-7.

**Touches.** A new `Tools/SequenceTools.cs` and a step dispatcher, `Hosting/DrivingTools.cs`,
`docs/design/C-7-tool-annotations.md`, every tool-count surface.

**Tests.** A tool outside the allowlist is refused before any step runs; stop-at-first-failure
with the partial results; an unsatisfied `wait_for` stops the batch; a takeover between steps 2
and 3 leaves step 3 unrun; a step cap.

**Done when.** File → Save As → type a name → Enter is one call that reports the resulting window.

### E-7 — Capture by window / element; OCR word positions  `P2 · M`
- [ ] Not started

**Ours today.** `screenshot` and `ocr` take `region` or `display`. Capturing one window means
`window(action:"list")`, reading its bounds and passing them back as a region. `IOcrService`
returns one string, so text read from an application with no UIA tree — a remote-desktop session,
a game, a canvas — cannot be clicked: the model knows the words and not where they are.

**Sketch.** `screenshot` and `ocr` gain `window` / `hwnd` (the B-10 matcher) and `element_id`,
mutually exclusive with `region` and `display`, resolved to that rect; a minimised or off-screen
target is refused naming its state. `ocr(format:"json")` returns lines and words with bounds in
**virtual-desktop** pixels (the engine's word rectangles, mapped back through the capture origin),
so a word's centre goes straight to `click`. `format:"text"` stays the default and unchanged.

**Touches.** `Abstractions/IOcrService.cs` and a result record, `Services/OcrService.cs`,
`Tools/ScreenTools.cs` (`ResolveRegionAsync`), `skills/windows/SKILL.md`.

**Tests.** Target-resolution unit tests for each exclusive combination; the coordinate mapping for
a region with a non-zero origin and for a secondary monitor with negative coordinates; a
`UIAutomation`-category live test beside `OcrServiceLiveTests`.

**Done when.** `ocr(window:"Notepad", format:"json")` returns a word whose centre, clicked, lands
on that word.

### E-8 — Session state: elevation, lock, secure desktop  `P2 · S–M`
- [ ] Not started

**Ours today.** Elevation is already caller-visible, but only as a by-product: `startup_report`
puts `IsElevated()` in its header, and the default summary and the JSON both print `Elevated`. That
is a full autorun inventory run to read one boolean, and the probe is private to
`StartupReportService`. (The server normally runs unelevated but can be launched elevated, so the
answer is not a constant.) What nothing reports is whether the workstation is locked, the session
disconnected, or a UAC prompt is up on the secure desktop. In each of those cases `snapshot`
returns an empty or stale tree and input goes nowhere, with nothing telling the model why.

**Sketch.** A lightweight session probe plus the missing lock and desktop diagnostics; share the
elevation check with `StartupReportService` instead of duplicating it.
`system_info(category:"session")` → `{Elevated, IntegrityLevel, UserName, SessionId,
Interactive, Locked, InputDesktop}`. `snapshot` adds a one-line note when the input desktop is not
the user's (`OpenInputDesktop` fails or names `Winlogon`) — the UAC and lock-screen cases — so the
model stops and tells the user. No elevation is ever attempted: this item reports, it does not
escalate.

**Touches.** A new `ISessionService` and implementation (register it in `AddWindowsMcp`),
`Tools/SystemTools.cs`, `Services/UIAutomationService.cs` or the snapshot renderer for the note,
`NativeMethods.txt`.

**Tests.** The DTO mapping on a mocked probe; the note appears only when the probe says so;
an `Integration` test that the real probe answers on the test machine without throwing.

**Done when.** With a UAC prompt showing, `snapshot` says the secure desktop has the input and
that the user must answer it.

### E-9 — `file_edit` and content search for remote hosts  `P2 · M`
- [ ] Not started

**Ours today.** `file_write` replaces or appends a whole file; `file_search` matches name, size and
date. On a local machine the client's own edit and search tools cover this. On a remote host over
HTTP they cannot reach the disk, so changing one line of a config file means reading the file and
writing all of it back, and finding which file mentions a setting means `powershell`.

**Sketch.** A new tool `file_edit(path, old_string, new_string, replace_all?, encoding?)`:
`old_string` must match exactly once unless `replace_all`; zero or several matches is a refusal
that says how many; the write is the same temp-file-and-rename `file_write` uses; encoding, BOM
and line endings of the file are preserved; the same path rules as C-1. `file_search` gains
`content` (a regex), `max_matches` and context lines, returning path, line number and line, and
skipping binary files.

**Touches.** `Abstractions/IFileSystemService.cs`, `Services/FileSystemService.cs`,
`Tools/FileTools.cs`, every tool-count surface, C-7's table.

**Tests.** Unique, missing and ambiguous matches; CRLF and BOM preserved; a cancelled or failed
write leaves the original; content search honours cancellation per file, the match cap, and an
invalid regex is refused by name.

**Done when.** One line of a remote config file is changed without the file crossing the wire.

### E-10 — Named toolsets on top of S-1  `P3 · S`
- [ ] Not started

**Ours today.** All 69 tool descriptions are sent to every client (`screenshot`'s alone is about
2 500 characters). S-1 will let an operator list tools to keep or drop by name.

**Sketch.** After S-1: `--toolset ui|admin|forensics|files` (repeatable; `WINDOWSMCP_TOOLSET`)
expands to a maintained list and composes with `--tools` / `--exclude-tools`. The lists live in
one table beside C-7's classification, and a test asserts every tool belongs to at least one set
so a new tool cannot be left out.

**Done when.** `--toolset ui` serves the UI-automation tools and nothing forensic.

### E-11 — Stable element ids and a snapshot diff  `P3 · L`
- [ ] Not started

**Ours today.** Ids are a counter (`el_{_nextId++}`); every `snapshot` evicts the previous
snapshot's ids and re-describes the whole scope, so an unchanged control gets a new id each time
and the client re-reads everything to learn that one thing changed.

**Sketch.** Key ids to the element's UIA RuntimeId so the same control keeps its id while it
lives, and add `snapshot(changes_only:true)` returning what appeared, disappeared and changed
since the last snapshot of the same scope. This rewrites the id contract every element-taking tool
relies on (roadmap C5) and RuntimeIds are not stable in every framework. **Conditional:** measure
the snapshot text an agent actually pays for after E-5; if `observe` scoped to one window makes it
small, close this item unbuilt.

**Done when.** Decided from the E-5 measurement, with a design note either way.

---

## X — Deliberately not porting

| Upstream feature | Reason |
|---|---|
| PostHog anonymous telemetry (`infrastructure/analytics.py`, `ANONYMIZED_TELEMETRY`, `POSTHOG_*`) | Privacy; no benefit to a self-hosted server. |
| SSE transport | Deprecated in the MCP spec; Streamable HTTP covers remote use. |
| UIA focus watchdog (`watchdog/`, `WINDOWS_MCP_WATCHDOG`) | Upstream itself documents it as debug-logging only and a crash risk. |
| `--stateless-http` toggle | Our HTTP transport is already stateless. |
| Desktop-relative path resolution in file tools | Ambiguity risk; absolute paths only (see C-1). |
| JSON-stringified-list coercion (`_as_loc`) | Our parameters are scalars; the Claude Desktop quirk does not apply. |
| `.mcpbignore`, `uv`/PyPI packaging | Python-specific. |

---

## Appendix A — Tool name map (upstream → ours)

| Upstream (20) | Ours (69) | Gap items |
|---|---|---|
| `Snapshot` | `snapshot`, `get_state`, `find_element`, `get_element`, `get_text`, `window` (list/active) | A-1..A-6, A-11..A-13 |
| `Screenshot` | `screenshot`, `ocr` | A-7..A-11 |
| `DisplayInventory` | `multi_monitor` | B-12 |
| `Click` | `click`, `interact_element` | D-2, B-4 |
| `Type` | `type`, `file_dialog` | B-1 |
| `Scroll` | `scroll` | B-3 |
| `Move` | `hover`, `drag` | B-2 |
| `Shortcut` | `shortcut`, `key` | D-1 |
| `Wait` | `wait` | B-5 |
| `WaitFor` | `wait_for`, `assert_element` | B-6 |
| `MultiSelect`, `MultiEdit` | `multi_select`, `multi_edit` | B-7 |
| `App` | `launch`, `switch_to_window`, `focus`, `window`, `start_process` | B-8..B-11 |
| `PowerShell` | `powershell`, `job` | C-6 |
| `FileSystem` | `file_read/write/manage/search/info/hash/streams`, `archive` | C-1 |
| `Scrape` | `scrape`, `http_request` | C-5 |
| `Clipboard` | `clipboard` | — |
| `Process` | `process`, `process_inspect` | C-3 |
| `Notification` | `notification` | C-4 |
| `Registry` | `registry_get`, `registry_set`, `registry_delete` | C-2 |
| _(none)_ | services, tasks, event log, disk, storage, network, firewall, security, startup report, integrity, USN, watch, env, power, audio, WMI, drivers, reliability, cert store, Defender, signatures | ours only |

## Appendix B — Upstream source map (`src/windows_mcp/`)

| Area | Files |
|---|---|
| CLI, transports, auth wiring, install/auth commands | `__main__.py` (`serve` ~447–745, `_apply_tool_filter` ~349, `_http_middleware` ~80, `install` ~856, `uninstall` ~905, `auth` ~925, `_gen_tls` ~747) |
| Config file | `infrastructure/config.py` |
| Bearer / OAuth / IP allowlist / SSRF | `infrastructure/auth.py`, `infrastructure/oauth.py`, `infrastructure/security.py` |
| Desktop facade (state, windows, app launch, input) | `desktop/service.py`, `desktop/views.py`, `desktop/utils.py` |
| Screenshot backends, flash | `desktop/screenshot.py`, `desktop/flash_overlay.py` |
| UI tree (classification, traversal, rendering, budget, cache, IA2) | `tree/config.py`, `tree/service.py`, `tree/views.py`, `tree/budget.py`, `tree/cache_utils.py`, `tree/ia2.py` |
| UIA wrapper (comtypes) | `uia/*.py` |
| Virtual desktops | `vdm/core.py` |
| Tools | `tools/*.py`, `tools/_snapshot_helpers.py` |
| Services | `filesystem/service.py`, `process/service.py`, `registry/service.py`, `notifications/service.py`, `powershell/service.py` |
| Packaging | `manifest.json`, `server.json`, `pyproject.toml` |
