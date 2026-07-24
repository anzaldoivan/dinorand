---
name: cut-release
description: Cut a DinoRand version release — bump VersionPrefix, update the Avalonia label and curated changelog, make the local Windows build, and prepare a local commit/tag for the tag-triggered release workflow. Use when the user asks to cut/tag/ship a release, bump the version, or generate a release build.
---

## When to use

The user wants to cut a release: "release vX.Y.Z", "bump the version", "generate the exe/release", or "cut a build". Pre-1.0 (`0.x`): a minor bump may carry breaking changes.

## The contract (get this wrong and the release silently never fires)

- `.github/workflows/release.yml` runs when a tag matching `v*` is pushed. The validator then requires strict `vMAJOR.MINOR.PATCH` or `vMAJOR.MINOR.PATCH-PRERELEASE` syntax.
- The tag must already exist, resolve to a commit, and point to a commit contained in freshly fetched `origin/main`. A branch creation or push is not the release trigger.
- The tag's core version must equal the single `<VersionPrefix>` in `Directory.Build.props`. For `vX.Y.Z-rc1`, use `X.Y.Z` as `VersionPrefix`.
- `CHANGELOG.md` must contain exactly one non-empty curated heading for the full tag version: `## [X.Y.Z]` (or the full prerelease version), optionally followed by ` — YYYY-MM-DD`. Generated notes are not a fallback.

## HARD GATE: approval before real-install validation

Do not run any test command that can discover or use a configured real game install until the user has explicitly authorized the exact real-install scope. The approval must name each game, the exact `DINORAND_DC1_DIR` and/or `DINORAND_DC2_DIR` path, the test command, and whether the test is allowed to read the install or must use a temporary copy. If that approval is absent, do not run the command.

Before resolving or using any install path, read the gitignored `.env`. Use only `DINORAND_DC1_DIR` and `DINORAND_DC2_DIR` from it; do not use any other `.env` setting or search for an install elsewhere. State in the approval record whether the test touches the install or uses temporary copies. `RealInstallGate` reads the configured install and its pristine backups but does not write to them; its install/restore exercise writes only to temporary test trees.

Any required-mode receipt and test-results directory must be unique to this run and outside the repository. After approval, and only after exporting the approved paths from `.env`, the safe required-mode command is. The xUnit/VSTest trait-key/value OR filter selects both `[Trait("RealInstall", "DC1")]` and `[Trait("RealInstall", "DC2")]` cases:

```bash
run_root="$(mktemp -d /tmp/dinorand-release-real-install-XXXXXX)"
receipt_dir="$run_root/receipts"
results_dir="$run_root/results"
mkdir "$receipt_dir" "$results_dir"

DINORAND_DC1_DIR="${DINORAND_DC1_DIR:?approved path read from .env}" \
DINORAND_DC2_DIR="${DINORAND_DC2_DIR:?approved path read from .env}" \
DINORAND_REQUIRE_REAL_INSTALL=1 \
DINORAND_REAL_INSTALL_RECEIPT_DIR="$receipt_dir" \
dotnet test test/DinoRand.FileFormats.Tests/DinoRand.FileFormats.Tests.csproj \
  --filter 'RealInstall=DC1|RealInstall=DC2' \
  --results-directory "$results_dir"
```

This command requires both approved game paths because it runs both `RealInstallGate` cases. If the user authorizes only one game, do not run this both-game command; use a separately reviewed command that selects only the approved case and its approved variable. Never broaden the scope without new approval. Do not place receipts, test results, or copied game data in the repository.

## Steps

1. **Pick the version.** Confirm with the user if ambiguous; confirm pre-1.0 jumps (0.2.0 → 4.0.0 is almost always a typo for 0.4.0).

2. **Bump the project version.** Set `<VersionPrefix>` in `Directory.Build.props` to the tag's core version.

3. **Update the Avalonia label.** `src/DinoRand.App.Avalonia/MainWindow.axaml` has a hand-maintained header label such as `vX.Y.Z`; update it to the new version. Check that no stale source label remains:
   ```bash
   git grep -nIE 'v[0-9]+\.[0-9]+\.[0-9]+' -- 'src/**' ':!*.csproj'
   ```

4. **Cut the changelog.** Turn `## [Unreleased]` into `## [VERSION] — YYYY-MM-DD` (`VERSION` is `X.Y.Z` or the full prerelease version), leave a fresh empty `## [Unreleased]` above it, and add `[VERSION]: https://github.com/anzaldoivan/dinorand/releases/tag/vVERSION` with the other link references. Release bullets are player-facing; keep technical evidence out of them.

   Verify the section locally:
   ```bash
   awk -v v="X.Y.Z" '$0 ~ "^## \\[" v "\\]" {g=1;next} g&&/^## \[/{exit} g' CHANGELOG.md
   ```

5. **Make the local Windows build.** Restore the locked solution, then publish the self-contained Windows RID:
   ```bash
   dotnet restore DinoRand.sln --locked-mode
   DINORAND_RELEASE_VERSION=X.Y.Z bash scripts/publish-release.sh win-x64
   ```
   Check `dist/win-x64/` for `dinorand.exe` and `DinoRand.Avalonia.exe`. The Avalonia release remains marked PREVIEW until real-desktop runtime verification.

6. **Prepare the commit and tag locally.** Review the diff, stage only the release inputs, and commit them:
   ```bash
   git diff --check
   git add CHANGELOG.md Directory.Build.props src/DinoRand.App.Avalonia/MainWindow.axaml
   git diff --cached --check
   git commit -m "Prepare vX.Y.Z release"
   git tag -a vX.Y.Z -m "DinoRand vX.Y.Z"
   ```
   For a prerelease, substitute the full tag everywhere above. The tagged commit must be the exact release commit that will be present in `main`; verify with `git fetch origin main` and `git merge-base --is-ancestor vX.Y.Z origin/main` before the tag is pushed.

7. **STOP before any push.** Show the commit, tag, changelog section, and build result, then get explicit user confirmation. After confirmation only, land the release commit on `main` through the approved repository path, verify the tag commit is an ancestor of freshly fetched `origin/main`, and push `vX.Y.Z`. Pushing that tag starts validation; never push a tag before this confirmation gate.

8. **Verify the tag event after the approved push.** Do not assume that a successful tag push created a run. Check the workflow state and recent runs:
   ```bash
   gh workflow list --all --limit 100
   gh workflow view release.yml
   gh run list --workflow release.yml --limit 20
   ```
   `--all` is required because disabled workflows are hidden by default. Confirm that `release.yml` is active and that `gh run list` contains a run for `vX.Y.Z`; then watch that run to completion with `gh run watch RUN_ID --exit-status`. Use `gh run view RUN_ID --log-failed` for a failed run. This workflow has only a `push` trigger for `v*` tags, not `workflow_dispatch`, so enabling it does not replay a tag event that was missed while it was disabled.

   If `release.yml` is disabled, stop. Before enabling it or taking any tag recovery action, and again immediately before a destructive recovery if time has elapsed, validate the unchanged local annotated tag and its `main` ancestry:
   ```bash
   set -euo pipefail
   git fetch origin main
   test "$(git cat-file -t refs/tags/vX.Y.Z)" = tag
   local_tag_object="$(git rev-parse refs/tags/vX.Y.Z)"
   remote_tag_object="$(git ls-remote --tags origin refs/tags/vX.Y.Z | cut -f1)"
   test -n "$remote_tag_object"
   if [ "$local_tag_object" != "$remote_tag_object" ]; then
     printf 'remote tag object differs from local tag object; aborting\n' >&2
     exit 1
   fi
   tag_commit="$(git rev-parse --verify refs/tags/vX.Y.Z^{commit})"
   git merge-base --is-ancestor "$tag_commit" origin/main
   notes_file="$(mktemp /tmp/dinorand-release-recovery-notes-XXXXXX)"
   PYTHONDONTWRITEBYTECODE=1 python3 scripts/release_validate.py \
     --repository . --tag vX.Y.Z --notes-output "$notes_file"
   rm -f "$notes_file"
   ```
   After those checks pass, obtain explicit user approval before enabling the workflow:
   ```bash
   gh workflow enable release.yml
   gh workflow view release.yml
   ```
   Enabling is one approval; it is not approval to alter the remote tag. Because enabling does not replay the missed tag event, get a separate, explicit user approval naming the exact tag and unchanged annotated tag object before deleting and re-pushing it. Only then may the recovery use `git push origin :refs/tags/vX.Y.Z` followed by `git push origin refs/tags/vX.Y.Z`; never recreate, move, or force-update the local tag. The delete/re-push is destructive and is not covered by the earlier push approval. Recheck `gh workflow view release.yml` and `gh run list --workflow release.yml --limit 20` afterward, then watch the newly created run.

## Gotchas

- If the version changes after the build or tag is prepared, update `VersionPrefix`, the Avalonia label, the changelog heading/reference, rebuild, and recreate the local tag.
- `scripts/build-release-assets.sh X.Y.Z` is the CI packaging path; it produces all release RIDs and the complete asset set. The local check above intentionally builds the Windows RID first.
