#!/usr/bin/env bash
# Plain-bash test harness (no bats dependency) for claude-guard.sh
set -uo pipefail
GUARD="$(cd "$(dirname "$0")" && pwd)/claude-guard.sh"
pass=0; fail=0
run() { # name expected_exit files_newline_string [added_lines]
  local name="$1" expected="$2" files="$3" added="${4:-0}" tmp rc
  tmp="$(mktemp)"; printf '%s\n' "$files" > "$tmp"
  bash "$GUARD" "$tmp" "$added" >/dev/null 2>&1; rc=$?
  rm -f "$tmp"
  if [ "$rc" -eq "$expected" ]; then echo "ok   - $name"; pass=$((pass+1))
  else echo "FAIL - $name (exit $rc, want $expected)"; fail=$((fail+1)); fi
}
run "docs allowed"            0 "docs/architecture/OVERVIEW.md"
run "readme allowed"          0 "README.md"
run "changelog allowed"       0 "CHANGELOG.md"
run "claude.md allowed"       0 "CLAUDE.md"
run "multiple docs allowed"   0 $'docs/a.md\nCHANGELOG.md'
run "src blocked capability"  1 "src/WindowsMcp/Services/PowerShellService.cs"
run "workflow blocked"        1 ".github/workflows/ci.yml"
run "csproj blocked"          1 "src/WindowsMcp/WindowsMcp.csproj"
run "legacy blocked"          1 "legacy/foo.py"
run "empty diff blocked"      1 ""
run "too many files blocked"  1 "$(for i in $(seq 1 25); do echo "docs/f$i.md"; done)" 0
run "too many lines blocked"  1 "docs/a.md" 999
run "traversal blocked"       1 "docs/../src/Evil.cs"
echo "---"; echo "pass=$pass fail=$fail"
[ "$fail" -eq 0 ]
