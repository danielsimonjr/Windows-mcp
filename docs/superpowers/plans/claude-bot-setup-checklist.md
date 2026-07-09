# claude-bot provisioning checklist (Daniel only)

These are the credential/config prerequisites for the Claude-in-Actions pilot. Tasks 4–6 of the
implementation plan (the maintenance workflow + Telegram digest) stay BLOCKED until every box is
ticked. Tasks 1–3 (the guard) need none of this.

- [ ] Obtain an `ANTHROPIC_API_KEY` (`sk-ant-…`). Prefer a **dedicated CI key** minted in the Anthropic Console with a **spend limit** (that limit is the cost cap); reusing the existing `~/.claude/api_key.txt` works to bootstrap but couples CI + local-RLM blast radius. (This replaces the subscription OAuth token: it's a service credential, metered, and won't starve your interactive Claude quota.)
- [ ] Add it as a GitHub secret: `gh secret set ANTHROPIC_API_KEY --repo danielsimonjr/Windows-mcp` (paste the key at the prompt, or `tr -d '\r\n' < key.txt | gh secret set ANTHROPIC_API_KEY --repo danielsimonjr/Windows-mcp`).
- [ ] Register a GitHub App named `claude-bot`:
      - Repository permissions: Contents = Read and write; Pull requests = Read and write. Nothing else.
      - Subscribe to no events. Where can it be installed: Only on this account.
- [ ] Install the `claude-bot` App on `danielsimonjr/Windows-mcp` ONLY.
- [ ] Record the App ID and generate a private key (.pem).
- [ ] In repo Settings → Environments, create environment `claude-bot` with "Required reviewers" = danielsimonjr.
- [ ] Add secrets: `CLAUDE_BOT_APP_ID` (repo), `CLAUDE_BOT_APP_PRIVATE_KEY` (repo). (`ANTHROPIC_API_KEY` was added above.)
- [ ] Add repo secrets `TELEGRAM_BOT_TOKEN` + `TELEGRAM_CHAT_ID` for the weekly digest.
- [ ] Confirm the App's bot login slug (usually `claude-bot[bot]`): after its first PR, `gh pr view <n> --json author --jq .author.login` shows it. Put the exact value in `.github/claude-guard.env`.
- [ ] Confirm `claude-bot` is NOT listed under Settings → Branches → branch protection "Allow specified actors to bypass".
