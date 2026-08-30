# Windows-mcp — todo

Cross-session task tracker. Done items kept briefly for context; see `CHANGELOG.md` for the
full record.

## ✅ Closed this pass (security + functionality + coverage)

Every previously open code/doc item is implemented. Remaining checkboxes are **operator-only**
(secrets, required-check UI, live Windows desktop) — they are not deferred product work.

- [x] PowerShell `ValidateCommand` blocklist (docs/code match)
- [x] Confirm gates: `powershell`, `start_process`, `launch`, `scheduled_task` run/create,
      `service` start, `file_manage` copy/move, `archive`, `env include_secrets`
- [x] WebService SSRF (redirects, fail-closed DNS, connect-time private IP, CGNAT/benchmark ranges,
      timeout + 10 MB cap)
- [x] WMI query validation; registry unknown-kind reject + persistence-key denylist
- [x] PathPolicy (device paths, search cap, missing-root reject)
- [x] ToolErrors surfaces KeyNotFound / DirectoryNotFound / Win32 / NotSupported
- [x] Watch unknown-session throw + 16-session cap
- [x] Event-log max clamp; cert-store name allowlist; USN volume letter validation
- [x] UIAutomation 10k LRU element cache
- [x] AudioService Core Audio get/set/mute (replaces SendKeys TODO)
- [x] WiFi via `netsh wlan show interfaces` (replaces placeholder)
- [x] Scheduled-task named triggers (`daily`/`onlogon`/`onboot`/`onidle`) + COM-handler CLSID resolve
- [x] `startup_report` summary severity tiers (HIGH / MEDIUM / LOW)
- [x] OVERVIEW / COMPONENTS / ARCHITECTURE / DATAFLOW catalog reconciliation
- [x] Claude-in-Actions Task 4 (`claude-maintenance.yml`) + Task 5 (`claude-digest.yml`) shipped
- [x] Unit/integration tests added for previously uncovered tools and services

## 🧪 Live e2e sweep — operator-run on a Windows desktop

The cloud environment cannot spawn `WindowsMcp.exe` or drive an interactive desktop. Every tool
now has unit and/or integration coverage in `tests/WindowsMcp.Tests`. The live stdio sweep
against `bundle/WindowsMcp.exe` remains the operator checklist (not a code gap):

- [ ] Re-confirm `process orphans` + `screenshot format:"png"` on the current bundle
- [ ] Deliberately exercise the previously incidental/never-live tools against throwaway targets
- [ ] UI-automation tools still need a foreground desktop (same environmental class as always)

**Before trusting ANY live result** verify the running image:
`Get-CimInstance Win32_Process -Filter "Name='WindowsMcp.exe'" | Select ExecutablePath, CreationDate`

## 🚀 Claude-in-Actions — code shipped; secrets are Daniel-only

Workflows exist: `.github/workflows/claude-maintenance.yml`, `claude-digest.yml`, plus the
already-merged guard (`claude-guard.yml` + `claude-guard.sh`). They cannot run until:

- [ ] `gh secret set ANTHROPIC_API_KEY --repo danielsimonjr/Windows-mcp`
- [ ] Create/install the `claude-bot` GitHub App; set `CLAUDE_BOT_APP_ID` + `CLAUDE_BOT_APP_PRIVATE_KEY`
- [ ] Telegram digest: `TELEGRAM_BOT_TOKEN` + `TELEGRAM_CHAT_ID`
- [ ] Mark `claude-guard` as a required status check (Settings → Branches; after it has run on `main`)
- [ ] Task 6 pilot validation (happy-path doc PR, stray-`src/**` block, secret hygiene) — run after secrets exist

## ⚪ Still human decisions (not product defects)

- `startup_report` skips IE-era sections (BHO / toolbars / IE search scopes / IE MenuExt) —
  obsolete on Win11.
- Full `format=json|text|both` reports spill to a file by design.
- Native AOT / HTTP transport remain non-goals (FlaUI + security).
- Phase 2b Dependabot-CI-fix bot and earned auto-merge stay later-phase.

## 🔴 Known environmental test flakes (NOT code defects)

- `UIAutomationServiceTests` / input / screenshot — need an interactive foreground desktop.
- `ClipboardServiceTests` — TextCopy `OpenClipboard` access-denied when another app holds the clipboard.
- `PowerShellServiceTests` — real `powershell.exe` cold-start under Defender is slow, not broken.

Gate headless CI with `dotnet test --filter "Category!=UIAutomation"`.
