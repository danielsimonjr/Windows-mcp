#!/usr/bin/env bash
# Pure policy: is a claude-bot PR diff within the stage allowlist, clear of
# high-risk src, and under the one-concern caps?
# Usage: claude-guard.sh <changed-files-file> <added-lines>
# Exit 0 = ALLOW; exit 1 = BLOCK (reasons on stdout).
set -euo pipefail

CHANGED_FILES_FILE="${1:?usage: claude-guard.sh <changed-files-file> <added-lines>}"
ADDED_LINES="${2:-0}"

# Stage-1 doc allowlist (override per-stage via ALLOWLIST_REGEX).
ALLOWLIST_REGEX="${ALLOWLIST_REGEX:-^(docs/|README\.md$|CHANGELOG\.md$|CLAUDE\.md$)}"
# Capability guard: high-risk sources are HUMAN-ONLY regardless of stage.
CAPABILITY_REGEX="${CAPABILITY_REGEX:-^src/}"
MAX_FILES="${MAX_FILES:-20}"
MAX_LINES="${MAX_LINES:-400}"

mapfile -t files < <(grep -vE '^[[:space:]]*$' "$CHANGED_FILES_FILE" || true)

verdict=0; reasons=()
if [ "${#files[@]}" -eq 0 ]; then
  echo "BLOCK:"; echo "  - no changed files detected (nothing to verify)"; exit 1
fi
for f in "${files[@]}"; do
  if [[ "$f" == *..* ]]; then
    reasons+=("traversal: '$f' contains '..' (default-deny)"); verdict=1
  fi
done
for f in "${files[@]}"; do
  if [[ "$f" =~ $CAPABILITY_REGEX ]]; then
    reasons+=("capability: '$f' is a high-risk source (human-only)"); verdict=1
  fi
done
for f in "${files[@]}"; do
  if [[ ! "$f" =~ $ALLOWLIST_REGEX ]]; then
    reasons+=("allowlist: '$f' is outside the stage allowlist"); verdict=1
  fi
done
if [ "${#files[@]}" -gt "$MAX_FILES" ]; then
  reasons+=("cap: ${#files[@]} files > MAX_FILES=$MAX_FILES"); verdict=1
fi
if [ "$ADDED_LINES" -gt "$MAX_LINES" ]; then
  reasons+=("cap: $ADDED_LINES added lines > MAX_LINES=$MAX_LINES"); verdict=1
fi

if [ "$verdict" -eq 0 ]; then
  echo "ALLOW: ${#files[@]} file(s), $ADDED_LINES added line(s) within policy"
else
  echo "BLOCK:"; printf '  - %s\n' "${reasons[@]}"
fi
exit "$verdict"
