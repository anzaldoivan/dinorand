---
name: cut-release
description: Cut a DinoRand version release — prepare and validate the release inputs, land them through a version-branch PR, tag the exact merged commit, and verify the idempotent release workflow. Use when the user asks to cut/tag/ship a release, bump the version, or generate a release build.
---

## When to use

The user wants to cut a release: "release vX.Y.Z", "bump the version", "generate the exe/release", or "cut a build". Pre-1.0 (`0.x`): a minor bump may carry breaking changes.

## The contract

- A release change lands through a strict `version/vMAJOR.MINOR.PATCH` or
  `version/vMAJOR.MINOR.PATCH-PRERELEASE` pull request. Never use `feature/release-*`,
  `release/*`, or a direct push to `main`, even when the owner can bypass the ruleset.
- Create the annotated tag only after the PR is merged. Point it at the exact verified PR merge
  commit, not at the pre-merge version-branch commit and not implicitly at the current checkout.
- `.github/workflows/release.yml` normally runs when a tag matching `v*` is pushed. Its
  `workflow_dispatch` input can safely replay the same existing tag without deleting or moving it.
- The validator requires strict `vMAJOR.MINOR.PATCH` or
  `vMAJOR.MINOR.PATCH-PRERELEASE` syntax.
- The tag must already exist, be annotated, resolve to the validator's exact commit, and point to a
  commit contained in freshly fetched `origin/main`. A branch push is not the release trigger.
- The tag's core version must equal the single `<VersionPrefix>` in `Directory.Build.props`. For `vX.Y.Z-rc1`, use `X.Y.Z` as `VersionPrefix`.
- `CHANGELOG.md` must contain exactly one non-empty curated heading for the full tag version: `## [X.Y.Z]` (or the full prerelease version), optionally followed by ` — YYYY-MM-DD`. Generated notes are not a fallback.
- The publisher is idempotent. It creates or resumes the exact draft, uploads missing assets in a
  fixed order with bounded retries/timeouts, verifies remote size and SHA-256, and publishes only
  the exact six-asset set. Rerun it; never repair a release with ad-hoc uploads.

## HARD GATE: approval before real-install validation

Do not run any test command that can discover or use a configured real game install until the user has explicitly authorized the exact real-install scope. The approval must name each game, the exact `DINORAND_DC1_DIR` and/or `DINORAND_DC2_DIR` path, the test command, and whether the test is allowed to read the install or must use a temporary copy. If that approval is absent, do not run the command.

Before resolving or using any install path, read the gitignored `.env`. Use only `DINORAND_DC1_DIR` and `DINORAND_DC2_DIR` from it; do not use any other `.env` setting or search for an install elsewhere. State in the approval record whether the test touches the install or uses temporary copies. `RealInstallGate` reads the configured install and its pristine backups but does not write to them; its install/restore exercise writes only to temporary test trees.

Any required-mode receipt and test-results directory must be unique to this run and outside the repository. After approval, and only after exporting the approved paths from `.env`, the safe required-mode command is below. The xUnit/VSTest trait-key/value OR filter selects both `[Trait("RealInstall", "DC1")]` and `[Trait("RealInstall", "DC2")]` cases:

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

1. **Pick the exact version and start clean.** Confirm an ambiguous or surprising pre-1.0 jump.
   Preserve unrelated user changes; do not stash or discard them. Fetch remote state, verify that the
   release tag and release do not already exist, then create the PR transport branch:

   ```bash
   git status --short --branch
   git fetch origin main --tags
   git ls-remote --tags origin refs/tags/vX.Y.Z
   gh release view vX.Y.Z --repo anzaldoivan/dinorand
   git switch -c version/vX.Y.Z
   ```

   The two lookup commands must report no existing tag/release for a new cut.
   `version/vX.Y.Z` exists only to carry the release PR; pushing it does not trigger publication.

2. **Update the three release inputs.**

   - Set the single `<VersionPrefix>` in `Directory.Build.props` to the tag's core version.
   - Update the hand-maintained label in
     `src/DinoRand.App.Avalonia/MainWindow.axaml`.
   - Turn `## [Unreleased]` into `## [VERSION] — YYYY-MM-DD`, leave a fresh empty
     `## [Unreleased]` above it, and add the version link reference. Keep the bullets
     player-facing.

   Verify both version surfaces and the curated section:

   ```bash
   git grep -nIE 'v[0-9]+\.[0-9]+\.[0-9]+' -- 'src/**' ':!*.csproj'
   awk -v v="X.Y.Z" '$0 ~ "^## \\[" v "\\]" {g=1;next} g&&/^## \[/{exit} g' CHANGELOG.md
   ```

3. **Build and validate before committing.** `dist/` must not be reused from another cut.

   ```bash
   dotnet restore DinoRand.sln --locked-mode
   DINORAND_RELEASE_VERSION=X.Y.Z bash scripts/publish-release.sh win-x64
   test -f dist/win-x64/dinorand.exe
   test -f dist/win-x64/DinoRand.Avalonia.exe
   DINORAND_DISABLE_REAL_INSTALL=1 \
     dotnet test DinoRand.sln -c Release --no-restore
   git diff --check
   ```

   `DINORAND_DISABLE_REAL_INSTALL=1` makes the test initializer clear inherited install variables
   and skip `.env`, so this ordinary suite cannot cross the real-install hard gate. The Avalonia
   release remains PREVIEW until an explicitly authorized real-desktop check.

4. **Commit the release inputs, but do not tag.**

   ```bash
   git add CHANGELOG.md Directory.Build.props src/DinoRand.App.Avalonia/MainWindow.axaml
   git diff --cached --check
   git diff --cached --stat
   git commit -m "Prepare vX.Y.Z release"
   git status --short --branch
   ```

   Record the full version-branch commit SHA as `RELEASE_HEAD`. Do not create a tag yet.

5. **STOP before the branch push.** Show `RELEASE_HEAD`, the exact staged/committed paths, the
   changelog section, and validation results. Obtain explicit approval to push
   `version/vX.Y.Z` and open its PR. After approval:

   ```bash
   git push -u origin version/vX.Y.Z
   gh pr create --base main --head version/vX.Y.Z \
     --title "Prepare vX.Y.Z release" --body-file PR_BODY_FILE
   gh pr checks PR_NUMBER --watch
   ```

   Never substitute `git push origin main`. Required review and status checks remain authoritative.
   If the owner must use an admin merge because independent review is impossible, stop and obtain
   explicit approval for that bypass; do not infer it from release approval.

6. **Prove the PR merged, then create the local tag.** Read the PR back and require `MERGED`,
   base `main`, the recorded `RELEASE_HEAD`, and a non-null merge commit. Fetch `main`, require the
   merge commit to be its ancestor, and ensure the tag is still absent remotely.

   ```bash
   gh pr view PR_NUMBER \
     --json state,baseRefName,headRefName,headRefOid,mergeCommit,mergedAt
   git fetch origin main --tags
   git merge-base --is-ancestor MERGE_COMMIT origin/main
   git ls-remote --tags origin refs/tags/vX.Y.Z
   ```

   Create the annotated tag only after the PR is merged:

   ```bash
   git tag -a vX.Y.Z MERGE_COMMIT -m "DinoRand vX.Y.Z"
   test "$(git cat-file -t refs/tags/vX.Y.Z)" = tag
   test "$(git rev-parse refs/tags/vX.Y.Z^{commit})" = "MERGE_COMMIT"
   notes_file="$(mktemp /tmp/dinorand-release-notes-XXXXXX)"
   PYTHONDONTWRITEBYTECODE=1 python3 scripts/release_validate.py \
     --repository . --tag vX.Y.Z --notes-output "$notes_file"
   ```

7. **STOP before the tag push.** Show the exact annotated tag object, dereferenced commit, validated
   notes, remote-tag absence, and active workflow state:

   ```bash
   git rev-parse refs/tags/vX.Y.Z
   git rev-parse refs/tags/vX.Y.Z^{commit}
   cat "$notes_file"
   gh workflow list --all --limit 100
   gh workflow view release.yml
   ```

   Obtain explicit approval naming that tag object before pushing. Use the explicit refspec; a plain
   `git push` does not push the tag:

   ```bash
   git push origin refs/tags/vX.Y.Z:refs/tags/vX.Y.Z
   ```

8. **Require and watch the exact tag run.** Do not infer success from the tag push.

   ```bash
   gh run list --workflow release.yml --limit 20 \
     --json databaseId,headBranch,headSha,status,conclusion,url
   gh run watch RUN_ID --exit-status
   gh run view RUN_ID --json status,conclusion,headBranch,headSha,jobs,url
   ```

   Require `headBranch=vX.Y.Z`, the validated merge commit as `headSha`, and all four jobs
   (`validate`, `build`, `attest`, `publish`) successful.

9. **Use only idempotent recovery.**

   - If a run exists but failed or was cancelled because of a transient service/runner failure,
     inspect it with `gh run view RUN_ID --log-failed`, then rerun the unchanged tag workflow:

     ```bash
     gh run rerun RUN_ID
     gh run watch RUN_ID --exit-status
     ```

     The publisher resumes the same draft, reconciles ambiguous requests, uploads only missing
     assets, and verifies every remote digest before publication.

   - If the release-control workflow or publisher itself is defective, fix it through a new
     protected `main` PR. Do not rerun the old workflow commit. After the fix merges, dispatch from
     `main`; the publish job executes that dispatch's immutable `github.workflow_sha`, while
     validation and asset building still use the unchanged release tag:

     ```bash
     gh workflow run release.yml --ref main -f tag=vX.Y.Z
     ```

   - If the tag push produced no run because the workflow was disabled, validate that the local and
     remote annotated tag objects are identical and still point into `origin/main`. After explicit
     approval, enable the workflow and dispatch the same immutable tag:

     ```bash
     gh workflow enable release.yml
     gh workflow run release.yml --ref main -f tag=vX.Y.Z
     ```

   Never delete, recreate, move, or force-update a release tag. Never use ad-hoc
   `gh release upload` or publish a draft manually; rerun or dispatch the repository-owned
   deterministic publisher. If the tagged product inputs or built source are wrong, fail closed and
   cut a new version; a control-plane recovery must not change tagged release content.

10. **Read back the published release.** Require `isDraft=false`, the expected prerelease state,
    the exact six asset names, uploaded states, sizes, and `sha256:` digests:

    ```bash
    gh release view vX.Y.Z --repo anzaldoivan/dinorand \
      --json tagName,isDraft,isPrerelease,publishedAt,url,assets
    gh api repos/anzaldoivan/dinorand/releases/tags/vX.Y.Z \
      --jq '{tag_name,draft,prerelease,published_at,assets:[.assets[]|{name,size,state,digest}]}'
    ```

## Gotchas

- A version or release-input change after the local build requires another build and PR update.
  Because tagging happens after merge, no tag recreation is part of the normal flow.
- A workflow rerun or manual dispatch is safe only for the exact unchanged remote annotated tag.
  The validator and publisher both recheck its commit.
- `scripts/build-release-assets.sh X.Y.Z` is the CI packaging path; it produces all release RIDs and the complete asset set. The local check above intentionally builds the Windows RID first.
