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
  chmod +x "$repo/scripts/check-no-copyrighted-files.sh"
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
printf 'receipt=%s args=%s\n' "${DINORAND_REAL_INSTALL_RECEIPT_DIR:-}" "$*" >> "$HOOK_DOTNET_LOG"
exit 0
EOF
chmod +x "$repo/fake-bin/dotnet"
stage_file src/DinoRand.Randomizer/Graph/RoomGraph.cs
stage_file src/DinoRand.Randomizer/Install/GameInstaller.cs
(cd "$repo" && DINORAND_DC1_DIR="$repo/install" HOOK_DOTNET_LOG="$repo/dotnet.log" \
  PATH="$repo/fake-bin:/usr/bin:/bin" bash .githooks/pre-commit)
grep -q "RealInstall_Oracle_MatchesEngine_ByteIdentical" "$repo/dotnet.log"
grep -q -- "RealInstall=DC1" "$repo/dotnet.log"

new_repo real-install-dc2-selection
mkdir -p "$repo/fake-bin" "$repo/install"
printf 'DINORAND_DC2_DIR=%s\n' "$repo/install" > "$repo/.env"
cat > "$repo/fake-bin/dotnet" <<'EOF'
#!/usr/bin/env bash
printf 'receipt=%s args=%s\n' "${DINORAND_REAL_INSTALL_RECEIPT_DIR:-}" "$*" >> "$HOOK_DOTNET_LOG"
exit 0
EOF
chmod +x "$repo/fake-bin/dotnet"
stage_file src/DinoRand.FileFormats/Stage/PackageRepacker.cs
(cd "$repo" && HOOK_DOTNET_LOG="$repo/dotnet.log" \
  PATH="$repo/fake-bin:/usr/bin:/bin" bash .githooks/pre-commit)
grep -q -- "RealInstall=DC2" "$repo/dotnet.log"
if grep -q -- "RealInstall=DC1" "$repo/dotnet.log"; then
  echo "unexpected DC1 real-install case" >&2
  exit 1
fi
receipt="$(cut -d= -f2- "$repo/dotnet.log" | cut -d' ' -f1)"
if [ -n "$receipt" ] && [ -e "$receipt" ]; then
  echo "real-install receipt directory was not cleaned up" >&2
  exit 1
fi

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

new_repo real-install-direct-dependencies
mkdir -p "$repo/fake-bin"
cat > "$repo/fake-bin/dotnet" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$HOOK_DOTNET_LOG"
exit 0
EOF
chmod +x "$repo/fake-bin/dotnet"
for source in \
  src/DinoRand.Randomizer/Install/OverlayInstaller.cs \
  src/DinoRand.FileFormats/Exe/ExeProtection.cs \
  src/DinoRand.FileFormats/Stage/GianPackage.cs \
  src/DinoRand.FileFormats/Stage/PackageRepacker.cs \
  src/DinoRand.FileFormats/Stage/RoomFile.cs \
  src/DinoRand.Randomizer/Definitions/GameDefinition.cs \
  src/DinoRand.Randomizer/Definitions/DinoCrisis1.cs \
  src/DinoRand.FileFormats/Compression/Lzss.cs \
  src/DinoRand.FileFormats/Stage/RoomPackageCodec.cs \
  src/DinoRand.FileFormats/Stage/RoomScript.cs \
  src/DinoRand.FileFormats/Stage/DcOpcodes.cs \
  src/DinoRand.FileFormats/Stage/StageObjects.cs \
  src/DinoRand.FileFormats/Stage/EnemySpecies.cs \
  src/DinoRand.FileFormats/Stage/DoorEntryPoses.cs \
  src/DinoRand.FileFormats/Stage/RoomTextureStager.cs \
  src/DinoRand.FileFormats/Stage/TextureImporter.cs \
  src/DinoRand.FileFormats/Stage/Dc1PuzzleCodeSync.cs \
  src/DinoRand.FileFormats/Stage/Dc1NativeRecoveryAidSuppression.cs \
  src/DinoRand.FileFormats/Exe/ExePatcher.cs \
  src/DinoRand.FileFormats/Exe/Dc1EditionDetector.cs \
  src/DinoRand.Randomizer/RandomizerRunner.cs \
  src/DinoRand.Randomizer/Output/Dc1RunArtifactWriter.cs \
  src/DinoRand.Randomizer/Definitions/DinoCrisis2.cs \
  src/DinoRand.FileFormats/Stage/Dc2/Dc2ScdBlob.cs \
  src/DinoRand.FileFormats/Exe/Dc2WpGatePatch.cs \
  src/DinoRand.FileFormats/Exe/Dc2DdrawTrailPatch.cs \
  src/DinoRand.Randomizer/Dc2/Dc2RandomizerRunner.cs \
  src/DinoRand.Randomizer/Dc2/Dc2OutputSink.cs \
  src/DinoRand.Randomizer/Dc2/Dc2RoomEmitGuard.cs \
  src/DinoRand.Randomizer/Output/Dc2RunArtifactWriter.cs \
  test/DinoRand.FileFormats.Tests/GianPackageTests.cs \
  test/DinoRand.FileFormats.Tests/PackageRepackerTests.cs \
  test/DinoRand.FileFormats.Tests/Refactor/RealInstallGate.cs \
  test/DinoRand.FileFormats.Tests/LzssTests.cs \
  test/DinoRand.FileFormats.Tests/RoomScriptTests.cs \
  test/DinoRand.FileFormats.Tests/Dc2OutputDirSinkTests.cs \
  test/DinoRand.FileFormats.Tests/Refactor/RunnerArtifactCharacterizationTests.cs; do
  stage_file "$source"
done
(cd "$repo" && DINORAND_DC1_DIR=/configured/dc1 DINORAND_DC2_DIR=/configured/dc2 \
  HOOK_DOTNET_LOG="$repo/dotnet.log" PATH="$repo/fake-bin:/usr/bin:/bin" \
  bash .githooks/pre-commit)
grep -q 'Dc2RoomFileTests' "$repo/dotnet.log"
grep -q 'RunnerArtifactCharacterizationTests' "$repo/dotnet.log"

new_repo real-install-title-data-boundary
mkdir -p "$repo/fake-bin"
cat > "$repo/fake-bin/dotnet" <<'EOF'
#!/usr/bin/env bash
touch "$HOOK_DOTNET_LOG"
exit 0
EOF
chmod +x "$repo/fake-bin/dotnet"
stage_file src/DinoRand.FileFormats/Graphics/Dc2TitleWatermark.cs
(cd "$repo" && DINORAND_DC2_DIR=/configured/dc2 HOOK_DOTNET_LOG="$repo/dotnet.log" \
  PATH="$repo/fake-bin:/usr/bin:/bin" bash .githooks/pre-commit)
[ ! -e "$repo/dotnet.log" ]

new_repo real-install-dc2-selection
mkdir -p "$repo/fake-bin"
cat > "$repo/fake-bin/dotnet" <<'EOF'
#!/usr/bin/env bash
printf 'args=%s\nreceipt=%s\nresults=%s\n' "$*" "$DINORAND_REAL_INSTALL_RECEIPT_DIR" "${@: -1}" >> "$HOOK_REAL_LOG"
exit 0
EOF
chmod +x "$repo/fake-bin/dotnet"
stage_file src/DinoRand.Randomizer/Install/ExePatchInstaller.cs
stage_file src/DinoRand.FileFormats/Exe/Dc1TransitionExePatch.cs
(cd "$repo" && DINORAND_DC2_DIR=/configured/dc2 HOOK_REAL_LOG="$repo/real.log" \
  PATH="$repo/fake-bin:/usr/bin:/bin" bash .githooks/pre-commit)
grep -q 'RealInstall=DC2' "$repo/real.log"
if grep -q 'RealInstall=DC1' "$repo/real.log"; then
  echo "DC2-only configuration selected the wrong real-install case" >&2
  exit 1
fi
grep -q 'GameInstaller' "$repo/real.log"
grep -q 'PackageRepackerTests' "$repo/real.log"
grep -q 'ExePatcherTests' "$repo/real.log"
receipt_run=$(sed -n 's/^receipt=//p' "$repo/real.log")
[ -n "$receipt_run" ] && [ ! -e "$receipt_run" ]

new_repo real-install-dc2-output-sink
mkdir -p "$repo/fake-bin"
cat > "$repo/fake-bin/dotnet" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$HOOK_REAL_LOG"
exit 0
EOF
chmod +x "$repo/fake-bin/dotnet"
stage_file src/DinoRand.Randomizer/Dc2/Dc2OutputSink.cs
(cd "$repo" && DINORAND_DC2_DIR=/configured/dc2 HOOK_REAL_LOG="$repo/real.log" \
  PATH="$repo/fake-bin:/usr/bin:/bin" bash .githooks/pre-commit)
grep -q 'Dc2OutputDirSinkTests' "$repo/real.log"

new_repo real-install-powershell-arguments
mkdir -p "$repo/fake-bin"
cat > "$repo/fake-bin/powershell.exe" <<'EOF'
#!/usr/bin/env bash
printf 'command=%s\nfilter=%s\nresults=%s\n' "$*" "$DINORAND_TEST_FILTER" "$DINORAND_TEST_RESULTS" >> "$HOOK_POWERSHELL_LOG"
exit 0
EOF
chmod +x "$repo/fake-bin/powershell.exe"
stage_file src/DinoRand.Randomizer/Install/ExePatchInstaller.cs
(cd "$repo" && DINORAND_DC1_DIR=/configured/dc1 HOOK_POWERSHELL_LOG="$repo/powershell.log" \
  PATH="$repo/fake-bin:/usr/bin:/bin" bash .githooks/pre-commit)
grep -q 'RealInstall=DC1' "$repo/powershell.log"
grep -q 'results=' "$repo/powershell.log"
grep -Fq 'filter=FullyQualifiedName~GameInstaller|FullyQualifiedName~InstallerTests|FullyQualifiedName~ExePatcherTests|FullyQualifiedName~PatcherTests|FullyQualifiedName~PatchTests|FullyQualifiedName~ExeProtectionTests|FullyQualifiedName~GianPackageTests|FullyQualifiedName~PackageRepackerTests|FullyQualifiedName~LzssTests|FullyQualifiedName~RoomFileRoundTripTests|FullyQualifiedName~RoomScriptTests|FullyQualifiedName~EnemySpeciesTableTests|FullyQualifiedName~DoorEntryPosesTests|FullyQualifiedName~TextureImportTests|FullyQualifiedName~Dc1EditionDetectorTests|FullyQualifiedName~Dc1NativeRecoveryAidSuppressionTests|FullyQualifiedName~Dc2RoomFileTests|FullyQualifiedName~Dc2RoomRoundTripTests|FullyQualifiedName~Dc2D2pWriterTests|FullyQualifiedName~Dc2OutputDirSinkTests|FullyQualifiedName~Dc2BackupSwapSinkTests|FullyQualifiedName~InstallCharacterizationTests|FullyQualifiedName~FileFormatCharacterizationTests|FullyQualifiedName~RunnerArtifactCharacterizationTests|RealInstall=DC1' "$repo/powershell.log"

new_repo real-install-failure-cleans-up
mkdir -p "$repo/fake-bin"
cat > "$repo/fake-bin/dotnet" <<'EOF'
#!/usr/bin/env bash
printf 'receipt=%s\nresults=%s\n' "$DINORAND_REAL_INSTALL_RECEIPT_DIR" "${@: -1}" >> "$HOOK_REAL_LOG"
mkdir -p "$DINORAND_REAL_INSTALL_RECEIPT_DIR" "${@: -1}"
touch "$DINORAND_REAL_INSTALL_RECEIPT_DIR/receipt.json" "${@: -1}/results.trx"
exit 7
EOF
chmod +x "$repo/fake-bin/dotnet"
stage_file src/DinoRand.Randomizer/Install/ExePatchInstaller.cs
if (cd "$repo" && DINORAND_DC1_DIR=/configured/dc1 HOOK_REAL_LOG="$repo/real.log" \
  PATH="$repo/fake-bin:/usr/bin:/bin" bash .githooks/pre-commit >output 2>&1); then
  echo "required real-install failure unexpectedly passed" >&2
  exit 1
fi
receipt_run=$(sed -n 's/^receipt=//p' "$repo/real.log")
results_run=$(sed -n 's/^results=//p' "$repo/real.log")
[ -n "$receipt_run" ] && [ ! -e "$receipt_run" ]
[ -n "$results_run" ] && [ ! -e "$results_run" ]

new_repo missing-python
mkdir -p "$repo/fake-bin"
printf '#!/usr/bin/env bash\nexit 1\n' > "$repo/fake-bin/python3"
chmod +x "$repo/fake-bin/python3"
stage_file CHANGELOG.md
if (cd "$repo" && PATH="$repo/fake-bin:/usr/bin:/bin" bash .githooks/pre-commit >output 2>&1); then
  echo "expected a relevant change without Python to fail closed" >&2
  exit 1
fi
grep -q "Python 3" "$repo/output"

echo "pre-commit hook regression checks passed"
