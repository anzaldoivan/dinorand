#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: scripts/build-release-assets.sh VERSION" >&2
  exit 2
fi
VERSION="$1"
case "$VERSION" in
  *[!0-9A-Za-z.-]*|.*|*..*|*.) echo "invalid release version: $VERSION" >&2; exit 2 ;;
esac

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIST="$ROOT/dist"
ASSETS="$DIST/release-assets"
if [ -e "$DIST" ]; then
  echo "refusing to reuse existing dist/: start from a clean checkout" >&2
  exit 1
fi
mkdir -p "$ASSETS"
export DINORAND_RELEASE_VERSION="$VERSION"

# These executable roots declare all supported RuntimeIdentifiers. A locked restore here materializes
# every RID target in one graph; each publish below selects one of those targets with --no-restore.
dotnet restore "$ROOT/src/DinoRand.Cli/DinoRand.Cli.csproj" --locked-mode -p:SelfContained=true
dotnet restore "$ROOT/src/DinoRand.App.Avalonia/DinoRand.App.Avalonia.csproj" --locked-mode -p:SelfContained=true

for rid in win-x64 linux-x64 osx-arm64; do
  bash "$ROOT/scripts/publish-release.sh" "$rid"
  PYTHONDONTWRITEBYTECODE=1 python3 "$ROOT/scripts/release_notices.py" \
    --repository "$ROOT" --rid "$rid" --output "$DIST/$rid" \
    --assets "$ROOT/src/DinoRand.Cli/obj/project.assets.json" \
    --assets "$ROOT/src/DinoRand.App.Avalonia/obj/project.assets.json"
  PYTHONDONTWRITEBYTECODE=1 python3 "$ROOT/scripts/package_release.py" archive \
    --rid "$rid" --source "$DIST/$rid" --output "$ASSETS/dinorand-v$VERSION-$rid.zip"
done

PYTHONDONTWRITEBYTECODE=1 python3 "$ROOT/scripts/package_apworlds.py"
cp "$DIST/dino_crisis_1.apworld" "$ASSETS/dino_crisis_1.apworld"
cp "$DIST/dino_crisis_2.apworld" "$ASSETS/dino_crisis_2.apworld"
PYTHONDONTWRITEBYTECODE=1 python3 "$ROOT/scripts/package_release.py" checksums --asset-dir "$ASSETS"
(cd "$ASSETS" && sha256sum -c SHA256SUMS)
