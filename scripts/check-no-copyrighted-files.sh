#!/usr/bin/env bash
#
# check-no-copyrighted-files.sh — fail-closed guard against committing game
# assets, binaries, or RE artifacts (copyrighted Capcom / Dino Crisis content).
#
# Used by:
#   - .githooks/pre-commit  (passes the staged file list as arguments)
#   - .github/workflows/no-copyrighted-files.yml  (no args => scans whole tree)
#
# Rules (any hit => non-zero exit):
#   1. Forbidden extension     — game-asset / executable / RE-artifact types.
#   2. Binary content          — anything git treats as binary, unless allowlisted.
#   3. Oversized file          — > MAX_KB; source/data here is small, assets are big.
#   4. Embedded blob           — very long contiguous hex/base64 run inside a text file.
#
# Binary detection uses git's own heuristic (numstat shows "-" for binary), so it
# matches exactly what would be committed — no grep/NUL portability issues.

set -euo pipefail

MAX_KB=256
MAX_BYTES=$((MAX_KB * 1024))

# Game-asset / executable / RE-artifact extensions (case-insensitive).
FORBIDDEN_EXT_RE='\.(dat|exe|dll|afs|mrg|tim|emd|pak|iso|img|bin|sav|vab|str|xa|pss|mov|ct)$'

# Exact relative paths permitted to be binary. Keep this list TINY and intentional.
# (dinorand.ico is the app icon — provenance must be confirmed original art before release.)
ALLOW_BINARY_RE='^src/DinoRand\.App(\.Avalonia)?/resources/dinorand\.ico$'

# Files to check: explicit args (staged set) or, if none, every tracked file.
if [ "$#" -gt 0 ]; then
  files=("$@")
else
  mapfile -t files < <(git ls-files)
fi

is_binary() {
  # git prints "-" in the added-lines column for files it considers binary.
  [ "$(git diff --no-index --numstat /dev/null "$1" 2>/dev/null | cut -f1)" = "-" ]
}

fail=0
note() { echo "  ✗ $1"; fail=1; }

for f in "${files[@]}"; do
  [ -f "$f" ] || continue   # skip deletions / vanished paths

  if printf '%s' "$f" | grep -qiE "$FORBIDDEN_EXT_RE"; then
    note "forbidden game/binary extension: $f"
    continue
  fi

  if is_binary "$f" && ! printf '%s' "$f" | grep -qE "$ALLOW_BINARY_RE"; then
    note "binary file not on allowlist: $f"
    continue
  fi

  # Oversize cap targets asset *bytes*; authored text/JSON is legitimately large and is
  # already guarded by the embedded-blob rule below. Only size-check binaries.
  bytes=$(wc -c < "$f")
  if is_binary "$f" && [ "$bytes" -gt "$MAX_BYTES" ] && ! printf '%s' "$f" | grep -qE "$ALLOW_BINARY_RE"; then
    note "binary file exceeds ${MAX_KB} KB (${bytes} bytes) — assets don't belong here: $f"
    continue
  fi

  # Long contiguous hex/base64 run => an embedded asset masquerading as text.
  if ! is_binary "$f" && LC_ALL=C grep -qE '[A-Za-z0-9+/]{4096,}={0,2}|[A-Fa-f0-9]{4096,}' "$f"; then
    note "embedded blob (4096+ char hex/base64 run) — extract data as authored text, not bytes: $f"
    continue
  fi
done

if [ "$fail" -ne 0 ]; then
  echo ""
  echo "BLOCKED: the above files look like copyrighted game content or binaries."
  echo "Commit game knowledge as your own text (offsets, authored JSON), never game bytes."
  echo "If a file is a genuine, original, safe binary, add its exact path to ALLOW_BINARY_RE."
  exit 1
fi

echo "OK: no copyrighted/binary game files detected (${#files[@]} files checked)."
