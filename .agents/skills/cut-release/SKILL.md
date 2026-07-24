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

## Gotchas

- If the version changes after the build or tag is prepared, update `VersionPrefix`, the Avalonia label, the changelog heading/reference, rebuild, and recreate the local tag.
- `scripts/build-release-assets.sh X.Y.Z` is the CI packaging path; it produces all release RIDs and the complete asset set. The local check above intentionally builds the Windows RID first.
