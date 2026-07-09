# claude-guard — post-merge activation runbook

The `claude-guard` workflow is a `workflow_run` check that only produces its check-run after the
CI workflow completes on a PR. So it must run once before it can be added as a required check.

## Step 1 — let claude-guard run once
After this branch merges to `main`, open any throwaway PR (or wait for the next PR). Confirm a
`claude-guard` check-run appears on it (it will be `success`/"not applicable" for non-claude PRs).

## Step 2 — make claude-guard a required check (Dependabot-safe)
Non-claude PRs (including Dependabot) receive an automatic `claude-guard = success`, so requiring it
does NOT break Dependabot auto-merge.

```bash
gh api -X PUT repos/danielsimonjr/Windows-mcp/branches/main/protection --input - <<'JSON'
{"required_status_checks":{"strict":false,"contexts":["test","claude-guard"]},"enforce_admins":false,"required_pull_request_reviews":null,"restrictions":null}
JSON
```

`required_pull_request_reviews` stays `null` in the pilot — CODEOWNERS enforcement is deferred (below).

## Step 3 — DEFERRED to the auto-merge-enablement phase
When narrow auto-merge for doc-only Claude PRs is eventually enabled, turn on code-owner review so
auto-merge cannot land a Claude change into a high-risk path:
- Set `required_pull_request_reviews` to `{"require_code_owner_reviews": true, "required_approving_review_count": 0}`.
- FIRST reconcile Dependabot: its `.github/` and csproj bumps touch CODEOWNERS paths and would then
  need review — decide whether to keep auto-merging those (e.g. narrow the CODEOWNERS globs) or accept
  human review on them.

## Known residual (from the guard-workflow review)
If the `claude-guard` workflow itself errors before posting a check-run, a required `claude-guard`
would leave the PR hanging. `openssl` (used for the random delimiter) ships on the `ubuntu-latest`
base image so this is low-risk; a hardening follow-up is an error trap that posts a `failure`
check-run on any workflow error.
