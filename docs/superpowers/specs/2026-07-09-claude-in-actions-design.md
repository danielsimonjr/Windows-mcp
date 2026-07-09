# Claude-in-Actions (Phase 2) — Design

**Goal:** Add an autonomous `claude -p` (Claude Code headless) layer to GitHub Actions on top of
the completed deterministic CI/Dependabot/auto-merge layer, piloted on this repo (Windows-mcp),
starting with a safe, human-gated documentation-drift bot.

**Status:** Design approved (Daniel, 2026-07-09) after two adversarial review rounds — an internal
Claude-opus pass and an independent cross-model pass (Gemini 2.5 Pro + OpenAI o3). Implementation
is blocked on credential provisioning that only Daniel can perform (see Prerequisites).

## Context

- Solo maintainer running Claude Code over SSH; ~150 repos. The deterministic layer (SHA-pinned
  CI, grouped Dependabot, patch/minor auto-merge behind the required check, branch protection) is
  complete across active repos.
- Phase 2 uses the **official `anthropics/claude-code-action`** (SHA-pinned), not a hand-rolled
  `claude -p` install.
- Windows-mcp specifics that shape the design: it is a C#/.NET 9 MCP server exposing 60 tools
  (incl. powershell, registry_set, process, firewall, service, scheduled_task, input injection,
  screen capture); it ships a single-file `bundle/WindowsMcp.exe` redeployed to every plugin user
  via a **mechanical** copy of `dist/` over the committed bundle; CI runs `on: pull_request` (all
  PRs incl. forks) with `contents: read`; the **required gate is `dotnet test --filter
  "Category!=UIAutomation"`** (196 windows-only tests — UIAutomation, clipboard, and PowerShell
  integration tests are EXCLUDED); existing auto-merge is gated on
  `pull_request.user.login == 'dependabot[bot]'`; windows-latest runners queue ~15 min and are
  occasionally cancelled by runner-reclaim.

## Governing decision: human-gated, autonomy earned

The initial intent was CI-gated auto-merge of AI output ("trust the gate"). Both adversarial
passes demonstrated this is unsafe here, because:

1. **The gate lives inside the agent's write scope** — `contents: write` includes `.github/`,
   `tests/`, `*.csproj`, and build config, so the agent can rewrite the very check that
   green-lights it. A "test-gap filling" task literally authors its own gate.
2. **The required gate is a blind oracle** — it excludes exactly the dangerous categories
   (PowerShell, registry, input injection, screen capture), so a malicious or wrong change to the
   highest-risk tools passes 196 tests and ships in the next bundle redeploy.
3. **Building untrusted dependency code is remote code execution** — diagnosing a broken bump
   requires `dotnet build/test`, which runs attacker-controlled MSBuild targets / npm postinstall
   and can inject prompt-injection payloads into the build log.

Therefore: **Claude opens PRs; Daniel merges.** The prior "never auto-merge AI output" invariant
STANDS, reinforced by review. Auto-merge is a later, narrow, earned capability (doc-only,
allowlist-guarded, time-delayed) — never for arbitrary diffs, and never for dependency upgrades.

## Prerequisites (Daniel only — implementation is blocked until these exist)

1. A **dedicated automation account** (not Daniel's personal identity) with a Claude subscription;
   generate `CLAUDE_CODE_OAUTH_TOKEN` via `claude setup-token`. A dedicated account prevents a bot
   loop from starving Daniel's interactive quota.
2. A **dedicated `claude-bot` GitHub App**, installed ONLY on Windows-mcp, with permissions
   `contents: write` + `pull_requests: write` and NOTHING else (no `workflows`, `packages`,
   `administration`); not on any branch-protection bypass list.
3. Store the OAuth token in a GitHub **Environment with required reviewers**, exposed only to the
   Claude step. Resolve `anthropics/claude-code-action@v1` to a commit SHA and pin it + its
   transitive actions.

## Pilot component: Stage-1 documentation-drift bot

**File:** `.github/workflows/claude-maintenance.yml`. **Trigger:** `workflow_dispatch` first;
add a weekly cron only after it proves out.

**Behavior:** one run = one focused PR that syncs docs to code (`docs/architecture/*` tool-counts,
README, CHANGELOG, CLAUDE.md). It opens a PR on its own branch `claude/maint-docs-<run_id>`, labels
it `claude-maintenance`, posts a summary comment, and STOPS. Daniel reviews and merges.

**Why this pilot is safe:** it executes no untrusted dependency code; it opens its own branch (no
`dependabot/**` push-permission problem); it is human-gated. It therefore sidesteps every critical
finding while proving the machinery (App, token, guards, digest).

## Universal guards (enforced by the workflow / a `main`-defined check, not by prompt)

- **Allowlist, not denylist.** The bot may modify only an explicit path set (pilot: `docs/**`,
  `README.md`, `CHANGELOG.md`, `CLAUDE.md`). A required status check **defined on `main`** (a
  reusable workflow / `workflow_run`, never the PR's own copy) FAILS the PR if the diff touches
  anything outside the allowlist. Fail-safe by construction; denylists are fail-open.
- **Capability guard.** Any change touching the high-risk tool/service sources the gate does not
  cover (PowerShell, registry, input/screen-capture, firewall, service, scheduled-task) is
  HUMAN-ONLY — never bot-mergeable, regardless of green tests. Enforced in the same `main`-defined
  check + CODEOWNERS on those paths.
- **One concern = a file/line cap** in that check.
- **Untrusted-text discipline.** Any external text fed to a workflow step (logs, PR bodies,
  changelogs) is command-parser-disabled (`echo "::stop-commands::<token>"`), stripped of control
  chars, length-capped, and fenced as DATA in the prompt.
- **SHA-check before any push;** abort if the PR head moved since the trigger. Never blind
  force-push.
- **Idempotency.** Skip if an open `claude-maintenance` PR already exists; deterministic branch
  names; clean up merged/closed bot branches; "no drift = zero PRs".
- **Observability.** Label + summary comment per action; a weekly **Telegram digest** (via
  telegram-mcp) of everything the bots did; a `git log --author=claude-bot` saved search; a
  post-merge CI run on `main` that pings on red.
- **Cost/loop guards.** Per-run `max-turns` + `timeout-minutes`; a global monthly run budget with
  alert; `cancelled`/`timed_out` CI never wakes Claude (cheap `gh run rerun` instead).

## Deferred component: Dependabot-CI-fix bot (Phase 2b)

Not built in the pilot. It is the hard, dangerous path and its earlier design did not hold up:
a non-Dependabot GitHub App is 403'd pushing to `dependabot/**`; `workflow_run` supplies the
failing SHA but no branch (detached-HEAD push failure); building the branch to diagnose is RCE +
a log-injection vector; blind C# patches burn ~15-min windows runs. Its hardened design (captured
for 2b) must include: no-build diagnosis that consumes the EXISTING (zero-secret) CI logs via API
rather than rebuilding; patch validation in a SEPARATE zero-secret job (`dotnet build /warnaserror`
+ tests, on ubuntu where possible) before pushing; a real push-permission solution (whitelist
`claude-bot` on `dependabot/**`, or push to a new branch + follow-up PR); all universal guards
above; and a workflow-enforced attempt limiter (give up after 2 bot commits on a branch, label
`claude-fix-exhausted`, ping Daniel), per-branch concurrency, and the infra-noise filter.

## Success criteria (before adding cron, earned auto-merge, or widening)

1. The doc-drift bot opens a correct, allowlist-clean PR; Daniel merges it; no duplicate next run.
2. The `main`-defined allowlist check demonstrably BLOCKS a PR that touches a non-doc path, and the
   capability guard blocks a PR touching a high-risk tool source.
3. The OAuth token stays confined to the reviewer-gated Environment step; no secret exposure in
   logs/comments; cost stays under the monthly cap; Daniel's interactive quota is never starved.

Only after all three hold: add the weekly cron, then consider narrow earned auto-merge for
doc-only PRs, then begin the Phase-2b fix-bot design, then widen to a second repo.

## Security posture summary

Never runs on fork PRs with secrets; token in a reviewer-gated Environment, SHA-pinned action;
least-privilege dedicated App; the deterministic `test` check stays a gate but is NOT trusted as a
sufficient safety oracle (hence human merge + capability guard); every high-risk or
infrastructure path routed to human review by a check the agent cannot edit.
