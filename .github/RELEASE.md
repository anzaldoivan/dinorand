# Public Release Trust and Operations

## Decision

Branch creation is not a release authority: a branch can be pushed before review and may not be in
`main`. `feature/*` names remain unrestricted and never authorize tagging. A release cut uses a
same-repository PR whose head exactly matches `version/vMAJOR.MINOR.PATCH[-PRERELEASE]`. When and
only when that PR merges into `main`, `post-merge-release-tag.yml` creates one annotated tag at the
exact merge commit. DinoRand publishes only after that PAT-created tag push. The accepted tag
grammar is:

- stable: `vMAJOR.MINOR.PATCH`;
- prerelease: `vMAJOR.MINOR.PATCH-IDENTIFIER[.IDENTIFIER...]`;
- numeric identifiers and core numbers have no leading zeroes; identifiers use ASCII letters,
  digits, and hyphens; build metadata (`+...`) is rejected.

The post-merge workflow checks out the PR's pre-merge base SHA as trusted control code and checks out
the exact merge commit separately as data. It never executes candidate scripts. The trusted
validator requires that commit to be the candidate `HEAD` and an ancestor of freshly fetched
`origin/main`, requires its core version to equal the single `VersionPrefix` in
`Directory.Build.props`, and requires `CHANGELOG.md` to have one exact, non-empty curated section
for the full version. Generated notes are never a fallback.

The repository-owned tagger uses GitHub's annotated-tag-object then tag-reference REST sequence. It
sets the target to the exact merge SHA, the message to `DinoRand vVERSION`, fixed repository-owned
tagger identity, and the PR merge time. It reconciles ambiguous responses before bounded retries.
An existing exact annotated tag is success; a lightweight tag or any target/metadata mismatch fails
closed. `RELEASE_TAG_TOKEN` must be an owner fine-grained PAT limited to this repository with
`Contents: read/write`; its owner retains administrator status for the protected `v*` creation
bypass. The PAT is required so its tag push starts `release.yml`; the normal `GITHUB_TOKEN` does
not provide that event chaining.

## Job boundaries

1. `post-merge-release-tag` has only `contents: read` through the workflow token. It runs only for a
   merged, same-repository `version/*` PR targeting `main`; trusted code validates the strict branch
   grammar and candidate inputs before the separate PAT creates or reconciles the tag.
2. Release `validate` has `contents: read`, checks full history/tag/main/version/notes, and exports
   only the validated notes, tag, version, prerelease state, and exact commit.
3. `build` has `contents: read`, restores locked graphs, builds/tests, and packages assets.
4. `attest` has only `contents: read`, `id-token: write`, and `attestations: write`.
5. `publish` has only `contents: write` and a 30-minute job timeout. The repository-owned
   `scripts/release_publish.py` is checked out at the run's immutable `github.workflow_sha`. A tag
   push therefore uses the tagged control plane. The publisher re-resolves the remote annotated tag
   to the validator's exact commit, creates or resumes its draft, and uploads missing assets one at
   a time in a fixed order. Every request has a 120-second timeout and at most three attempts. After
   an ambiguous response it reads the draft back before retrying; an exact remote digest counts as
   success, an incomplete `starter` asset is removed, and an uploaded size/digest mismatch fails
   closed. Publication occurs only after the exact six names, sizes, uploaded states, and SHA-256
   digests match.

Both a tag push and `workflow_dispatch` run the same validator and publisher. A failed or cancelled
run intentionally leaves a resumable draft and is rerun unchanged. Dispatch the exact unchanged tag
only if its original tag event produced no run. Never delete/re-push the tag, upload assets ad hoc,
or publish the draft manually.

## Pinned tools and dependencies

`global.json` and `actions/setup-dotnet` both pin SDK `8.0.423`; `rollForward: disable` prevents a
feature-band fallback. All Python-using release jobs pin Python `3.12.13` through a full-SHA-pinned
`actions/setup-python`. The publisher uses only that Python standard library and GitHub's versioned
REST API, not the runner's mutable `gh` binary. Solution and RID restores use `--locked-mode`;
build/test/publish use `--no-restore` where supported. Executable and test roots own tracked
lockfiles, and the existing AP client lock remains authoritative.

The current full-SHA Action pins are:

| Action | Tag | Full commit |
|---|---|---|
| `actions/checkout` | `v7.0.1` | `3d3c42e5aac5ba805825da76410c181273ba90b1` |
| `actions/setup-dotnet` | `v6.0.0` | `a98b56852c35b8e3190ac28c8c2271da59106c68` |
| `actions/setup-python` | `v7.0.0` | `5fda3b95a4ea91299a34e894583c3862153e4b97` |
| `actions/upload-artifact` | `v7.0.1` | `043fb46d1a93c77aae656e7c1c64a875d1fc6a0a` |
| `actions/download-artifact` | `v8.0.1` | `3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c` |
| `schneegans/dynamic-badges-action` | `v1.9.0` | `28b0fa8bdeb46170ac397105ece0c1fe58f68910` |
| `actions/dependency-review-action` | `v5.0.0` | `a1d282b36b6f3519aa1f3fc636f609c47dddb294` |
| `actions/attest-build-provenance` | `v4.1.1` | `0f67c3f4856b2e3261c31976d6725780e5e4c373` |

## Notices, archives, checksums, and attestations

For each RID, the notice generator reads the RID target in both executable `project.assets.json`
files and includes only packages with runtime/native assets. NuGet `<license type="expression">` or
a present package-declared license file is required; a license URL alone fails closed. Declared
license files and package notices are copied, and the .NET runtime pack must supply both its license
and third-party notices. Generated text rejects machine/cache paths and credential-like content.
The two legacy corefx packages (`System.Memory 4.5.3` and `System.ValueTuple 4.5.0`) are accepted
only when NuGet's restored content hash, their package-supplied license/notice hashes, and the exact
official corefx source commit all match `scripts/release-license-overrides.json`.

Each deterministic archive contains both executables, project `LICENSE`, `LEGAL.md`, existing
`THIRD-PARTY-NOTICES.md`, the generated inventory/package evidence, and clearly named .NET runtime
license/notices. `SHA256SUMS` covers the three RID archives and two `.apworld` files in filename
order and is checked before upload. Build provenance attests that same asset directory.

## Current remote release controls

The one-time public-hardening rollout is complete. The current release baseline is:

- `release.yml` is active and supports both `v*` tag pushes and explicit same-tag recovery dispatch;
- `post-merge-release-tag.yml` automatically tags eligible merged version PRs;
- before the first version PR merge, `RELEASE_TAG_TOKEN` must be configured with repository-only
  `Contents: read/write` access and an administrator owner;
- repository Actions policy requires full-SHA pins;
- `main-protection` requires PR review and the configured status checks;
- `release-tag-protection` protects `v*` creation, deletion, and non-fast-forward updates with the
  configured owner bypass;
- the obsolete release-branch ruleset has been removed; and
- immutable releases and the approved repository security controls are enabled.

A normal release cut must not disable the workflow or mutate repository settings/rulesets. Read them
back only when diagnosing an actual mismatch:

```bash
gh workflow view release.yml --repo anzaldoivan/dinorand
gh api repos/anzaldoivan/dinorand/actions/permissions \
  --jq '{allowed_actions,sha_pinning_required}'
gh api repos/anzaldoivan/dinorand/rulesets \
  --jq '.[] | [.id,.name,.target,.enforcement]'
gh api repos/anzaldoivan/dinorand/immutable-releases
```

Any future settings mutation is a separate operation: capture its exact before-state in a
mode-0700 directory, generate the inverse from that response, and obtain explicit approval. Never
run historical rollout/rollback commands as part of a version cut.

## Legal scope

Practical risk-reduction only, not legal advice; a lawyer confirms before publishing.
