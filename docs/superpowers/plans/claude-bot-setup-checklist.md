# claude-bot provisioning checklist (Daniel only)

These are the credential/config prerequisites for the Claude-in-Actions pilot. Tasks 4–6 of the
implementation plan (the maintenance workflow + Telegram digest) stay BLOCKED until every box is
ticked. Tasks 1–3 (the guard) need none of this.

- [ ] Create a dedicated automation GitHub account (e.g. `danielsimonjr-bot`) with its own Claude subscription.
- [ ] On that account run `claude setup-token`; copy the `CLAUDE_CODE_OAUTH_TOKEN`.
- [ ] Register a GitHub App named `claude-bot`:
      - Repository permissions: Contents = Read and write; Pull requests = Read and write. Nothing else.
      - Subscribe to no events. Where can it be installed: Only on this account.
- [ ] Install the `claude-bot` App on `danielsimonjr/Windows-mcp` ONLY.
- [ ] Record the App ID and generate a private key (.pem).
- [ ] In repo Settings → Environments, create environment `claude-bot` with "Required reviewers" = danielsimonjr.
- [ ] Add secrets: `CLAUDE_CODE_OAUTH_TOKEN` (Environment `claude-bot`), `CLAUDE_BOT_APP_ID` (repo), `CLAUDE_BOT_APP_PRIVATE_KEY` (repo).
- [ ] Add repo secrets `TELEGRAM_BOT_TOKEN` + `TELEGRAM_CHAT_ID` for the weekly digest.
- [ ] Confirm the App's bot login slug (usually `claude-bot[bot]`): after its first PR, `gh pr view <n> --json author --jq .author.login` shows it. Put the exact value in `.github/claude-guard.env`.
- [ ] Confirm `claude-bot` is NOT listed under Settings → Branches → branch protection "Allow specified actors to bypass".
