---
name: cut-release
description: Cut a DinoRand version release — prepare and validate the release inputs, land them through a version-branch PR, verify automatic tagging of the exact merge commit, and watch the idempotent release workflow. Use when the user asks to cut/tag/ship a release, bump the version, or generate a release build.
---

## When to use

The user wants to cut a release: "release vX.Y.Z", "bump the version", "generate the exe/release", or "cut a build". Pre-1.0 (`0.x`): a minor bump may carry breaking changes.

## The contract

- A release change lands through a strict `version/vMAJOR.MINOR.PATCH` or
  `version/vMAJOR.MINOR.PATCH-PRERELEASE` pull request. `feature/*` remains unrestricted feature
  naming; do not use it for an actual release cut. Never use `release/*` or a direct push to `main`,
  even when the owner can bypass the ruleset.
- The post-merge release-tag workflow creates the annotated tag only after an eligible PR merges.
  It validates the exact merge commit with trusted pre-merge control code, then uses the
  repository-scoped `RELEASE_TAG_TOKEN`; do not create or push a release tag manually.
- `.github/workflows/release.yml` normally runs when a tag matching `v*` is pushed. Its
  `workflow_dispatch` input is only for recovery when the exact tag exists but its push event
  produced no run.
- The validator requires strict `vMAJOR.MINOR.PATCH` or
  `vMAJOR.MINOR.PATCH-PRERELEASE` syntax.
- The tag must already exist, be annotated, resolve to the validator's exact commit, and point to a
  commit contained in freshly fetched `origin/main`. A branch push is not the release trigger.
- The tag's core version must equal the single `<VersionPrefix>` in `Directory.Build.props`. For `vX.Y.Z-rc1`, use `X.Y.Z` as `VersionPrefix`.
- `CHANGELOG.md` must contain exactly one non-empty curated heading for the full tag version: `## [X.Y.Z]` (or the full prerelease version), optionally followed by ` — YYYY-MM-DD`. Generated notes are not a fallback.
- The publisher is idempotent. It creates or resumes the exact draft, uploads missing assets in a
  fixed order with bounded retries/timeouts, verifies remote size and SHA-256, and publishes only
  the exact six-asset set. Rerun it; never repair a release with ad-hoc uploads.
- Before merging the first version PR, confirm the `RELEASE_TAG_TOKEN` repository secret contains
  an owner fine-grained PAT limited to this repository with `Contents: read/write`. The token owner
  must retain administrator status so the existing protected-tag creation bypass applies.

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

6. **Prove the PR merged, then watch automatic tagging.** Read the PR back and require `MERGED`,
   base `main`, the exact `version/vX.Y.Z` head, the recorded `RELEASE_HEAD`, and a non-null merge
   commit. Fetch `main` and require the merge commit to be its ancestor.

   ```bash
   gh pr view PR_NUMBER \
     --json state,baseRefName,headRefName,headRefOid,mergeCommit,mergedAt
   git fetch origin main --tags
   git merge-base --is-ancestor MERGE_COMMIT origin/main
   gh run list --workflow post-merge-release-tag.yml --event pull_request_target \
     --commit MERGE_COMMIT --limit 20 \
     --json databaseId,headBranch,headSha,status,conclusion,url
   gh run watch TAG_RUN_ID --exit-status
   ```

   Require the run for `PR_NUMBER` to succeed. Then read the protected tag reference and its
   annotated object through the API. Require reference type `tag`, tag name `vX.Y.Z`, message
   `DinoRand vX.Y.Z`, target type `commit`, and target `MERGE_COMMIT`. The tagger date must equal
   the PR's `mergedAt` timestamp and the tagger must be the repository-owned release automation.

   ```bash
   TAG_OBJECT_SHA="$(gh api repos/anzaldoivan/dinorand/git/ref/tags/vX.Y.Z --jq '.object.sha')"
   gh api "repos/anzaldoivan/dinorand/git/tags/$TAG_OBJECT_SHA" \
     --jq '{tag,message,object,tagger}'
   ```

7. **Require and watch the exact tag-triggered release run.** The PAT-created tag push starts
   `release.yml`; do not infer release success merely from the tagging run.

   ```bash
   gh run list --workflow release.yml --limit 20 \
     --json databaseId,headBranch,headSha,status,conclusion,url
   gh run watch RUN_ID --exit-status
   gh run view RUN_ID --json status,conclusion,headBranch,headSha,jobs,url
   ```

   Require `headBranch=vX.Y.Z`, the validated merge commit as `headSha`, and all four jobs
   (`validate`, `build`, `attest`, `publish`) successful.

8. **Use only idempotent recovery.**

   - If a run exists but failed or was cancelled because of a transient service/runner failure,
     inspect it with `gh run view RUN_ID --log-failed`, then rerun the unchanged tag workflow:

     ```bash
     gh run rerun RUN_ID
     gh run watch RUN_ID --exit-status
     ```

     The publisher resumes the same draft, reconciles ambiguous requests, uploads only missing
     assets, and verifies every remote digest before publication.

   - Only if the exact remote tag exists but its tag push produced no `release.yml` run, validate
     the remote annotated object again. After explicit approval, enable the workflow if necessary
     and dispatch that same immutable tag from protected `main`:

     ```bash
     gh workflow enable release.yml
     gh workflow run release.yml --ref main -f tag=vX.Y.Z
     ```

   Never use dispatch to replace an existing failed tag-triggered run. If the release control or
   tagged product is defective, fix the repository through a protected `main` PR and cut a new
   version. Never delete, recreate, move, or force-update a release tag, use ad-hoc
   `gh release upload`, or publish a draft manually.

9. **Read back the published release.** Require `isDraft=false`, the expected prerelease state,
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
- A workflow rerun is safe for the unchanged tag. Manual same-tag dispatch is reserved for a
  missed tag event and remains safe only after the remote annotated object is revalidated.
- `scripts/build-release-assets.sh X.Y.Z` is the CI packaging path; it produces all release RIDs and the complete asset set. The local check above intentionally builds the Windows RID first.
