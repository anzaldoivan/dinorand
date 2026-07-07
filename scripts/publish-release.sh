#!/usr/bin/env bash
# publish-release.sh — produce standalone, self-contained executables for release.
#
# Both fronts are published as single-file, self-contained binaries: they run
# WITHOUT a .NET runtime installed on the target machine. The game data is
# already baked into the assemblies (map.json is an EmbeddedResource; the rest
# of data/dc1 is hardcoded in Definitions/DinoCrisis1.cs), so there is nothing
# to ship alongside the binary — the published .exe IS the whole tool.
#
# Artifacts land in dist/<rid>/:
#   DinoRand.exe            — WPF GUI front-end       (Windows RIDs only; WPF cannot run elsewhere)
#   DinoRand.Avalonia[.exe] — cross-platform GUI      (every RID) — feature-complete MVVM port of
#                             the WPF window; PREVIEW until runtime parity is verified on real
#                             Windows/Linux/macOS desktops (AVALONIA-PORT.md Phase 5). Ships labelled.
#   dinorand[.exe]          — CLI front-end           (every RID)
#
# Usage:
#   scripts/publish-release.sh                 # default RID: win-x64 (WPF GUI + Avalonia GUI + CLI)
#   scripts/publish-release.sh win-x64         # explicit RID
#   scripts/publish-release.sh linux-x64       # Avalonia GUI + CLI (WPF skipped, not an error)
#   scripts/publish-release.sh win-x64 osx-arm64 linux-x64   # several at once
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIST_DIR="$REPO_ROOT/dist"
CONFIG="Release"

cd "$REPO_ROOT"

# RIDs from args, or default to win-x64.
RIDS=("$@")
if [ "${#RIDS[@]}" -eq 0 ]; then
  RIDS=("win-x64")
fi

publish() {
  # $1 = project path, $2 = RID, $3 = friendly label
  local project="$1" rid="$2" label="$3"
  echo "=== Publishing $label ($rid) ==="
  dotnet publish "$project" \
    -c "$CONFIG" \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o "$DIST_DIR/$rid"
}

for rid in "${RIDS[@]}"; do
  # CLI publishes for every RID.
  publish "src/DinoRand.Cli" "$rid" "DinoRand.Cli"

  # Avalonia GUI publishes for every RID (the cross-platform front; on Linux/macOS it is the only
  # GUI). Feature-complete MVVM port of the WPF window — still labelled PREVIEW until its runtime
  # behaviour is verified on real Windows/Linux/macOS desktops, after which the WPF front is retired
  # and this label drops (AVALONIA-PORT.md Phase 5).
  echo "--- NOTE: DinoRand.Avalonia is a feature-complete MVVM port, PREVIEW pending real-desktop runtime verification (AVALONIA-PORT.md) ---"
  publish "src/DinoRand.App.Avalonia" "$rid" "DinoRand.App.Avalonia (GUI, PREVIEW)"

  # WPF GUI is Windows-only — publish it for win-* RIDs, skip (don't fail) elsewhere.
  case "$rid" in
    win-*) publish "src/DinoRand.App" "$rid" "DinoRand.App (WPF GUI)" ;;
    *)     echo "--- Skipping WPF GUI for $rid (WPF runs on Windows only) ---" ;;
  esac
done

echo ""
echo "=== Done. Artifacts in $DIST_DIR/ ==="
for rid in "${RIDS[@]}"; do
  echo "  $rid:"
  ls -1 "$DIST_DIR/$rid"/DinoRand.exe "$DIST_DIR/$rid"/DinoRand.Avalonia* "$DIST_DIR/$rid"/dinorand* 2>/dev/null \
    | sed 's/^/    /' || true
done
