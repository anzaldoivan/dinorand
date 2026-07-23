#!/usr/bin/env bash
set -euo pipefail

root="$(git rev-parse --show-toplevel)"
tmp="$(mktemp -d "$root/.hook-test.XXXXXX")"
case "$tmp" in "$root"/.hook-test.*) trap 'rm -rf -- "$tmp"' EXIT ;; *) exit 1 ;; esac

new_repo() {
  repo="$tmp/$1"
  mkdir -p "$repo/.githooks" "$repo/scripts"
  cp "$root/.githooks/pre-commit" "$repo/.githooks/pre-commit"
  printf '#!/usr/bin/env bash\nexit 0\n' > "$repo/scripts/check-no-copyrighted-files.sh"
  git -C "$repo" init -q
  git -C "$repo" config user.email hook-test@example.invalid
  git -C "$repo" config user.name hook-test
}

stage_file() {
  mkdir -p "$(dirname "$repo/$1")"
  printf 'test\n' > "$repo/$1"
  git -C "$repo" add "$1"
}

new_repo missing-oracle
stage_file src/DinoRand.Randomizer/Graph/RoomGraph.cs
if (cd "$repo" && PATH="/usr/bin:/bin" bash .githooks/pre-commit >output 2>&1); then
  echo "expected graph-source change without a staged oracle to fail" >&2
  exit 1
fi
grep -q "reachability-oracle.json" "$repo/output"

new_repo install-proves-unchanged
mkdir -p "$repo/fake-bin" "$repo/install"
cat > "$repo/fake-bin/dotnet" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$HOOK_DOTNET_LOG"
exit 0
EOF
chmod +x "$repo/fake-bin/dotnet"
stage_file src/DinoRand.Randomizer/Graph/RoomGraph.cs
(cd "$repo" && DINORAND_DC1_DIR="$repo/install" HOOK_DOTNET_LOG="$repo/dotnet.log" \
  PATH="$repo/fake-bin:/usr/bin:/bin" bash .githooks/pre-commit)
grep -q "RealInstall_Oracle_MatchesEngine_ByteIdentical" "$repo/dotnet.log"

new_repo py-launcher
mkdir -p "$repo/fake-bin"
cat > "$repo/fake-bin/python3" <<'EOF'
#!/usr/bin/env bash
exit 1
EOF
cat > "$repo/fake-bin/py" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$HOOK_TEST_LOG"
exit 0
EOF
cat > "$repo/fake-bin/dotnet" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$HOOK_DOTNET_LOG"
exit 0
EOF
chmod +x "$repo/fake-bin/python3" "$repo/fake-bin/py" "$repo/fake-bin/dotnet"
mkdir "$repo/install"
stage_file src/DinoRand.FileFormats/Stage/RoomScript.cs
stage_file data/dc1/reachability-oracle.json
(cd "$repo" && DINORAND_DC1_DIR="$repo/install" HOOK_TEST_LOG="$repo/python.log" \
  HOOK_DOTNET_LOG="$repo/dotnet.log" PATH="$repo/fake-bin:/usr/bin:/bin" bash .githooks/pre-commit)
grep -q -- "-3 .*gen_ap_logic.py --check" "$repo/python.log"
grep -q "RealInstall_Oracle_MatchesEngine_ByteIdentical" "$repo/dotnet.log"
cat > "$repo/fake-bin/powershell.exe" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$HOOK_POWERSHELL_LOG"
exit 0
EOF
chmod +x "$repo/fake-bin/powershell.exe"
rm "$repo/fake-bin/dotnet"
(cd "$repo" && DINORAND_DC1_DIR="$repo/install" HOOK_TEST_LOG="$repo/python.log" \
  HOOK_POWERSHELL_LOG="$repo/powershell.log" PATH="$repo/fake-bin:/usr/bin:/bin" bash .githooks/pre-commit)
grep -q "RealInstall_Oracle_MatchesEngine_ByteIdentical" "$repo/powershell.log"

new_repo missing-python
stage_file CHANGELOG.md
if (cd "$repo" && PATH="/usr/bin:/bin" bash .githooks/pre-commit >output 2>&1); then
  echo "expected a relevant change without Python to fail closed" >&2
  exit 1
fi
grep -q "Python 3" "$repo/output"

echo "pre-commit hook regression checks passed"
