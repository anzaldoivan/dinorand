# Public Release Trust and Operations

## Decision

Branch creation is not a release authority: a branch can be pushed before review, may not be in
`main`, and the old workflow could synthesize a tag through `gh release create --target`. DinoRand
therefore publishes only after an existing tag is pushed. The accepted grammar is:

- stable: `vMAJOR.MINOR.PATCH`;
- prerelease: `vMAJOR.MINOR.PATCH-IDENTIFIER[.IDENTIFIER...]`;
- numeric identifiers and core numbers have no leading zeroes; identifiers use ASCII letters,
  digits, and hyphens; build metadata (`+...`) is rejected.

The tag resolves to a commit, that commit must be an ancestor of freshly fetched `origin/main`, its
core version must equal the single `VersionPrefix` in `Directory.Build.props`, and
`CHANGELOG.md` must have one exact, non-empty curated section for the full version (including a
prerelease suffix). Generated notes are never a fallback.

## Job boundaries

1. `validate` has `contents: read`, checks full history/tag/main/version/notes, and exports only the
   validated notes and scalar outputs.
2. `build` has `contents: read`, restores locked graphs, builds/tests, and packages assets.
3. `attest` has only `contents: read`, `id-token: write`, and `attestations: write`.
4. `publish` has only `contents: write`. It checks `SHA256SUMS`, uses `gh release create
   --verify-tag --draft` without `--target`, uploads the exact six-asset set, reads the draft back,
   and changes `draft` to false only after exact completeness verification.

A failure after draft creation intentionally leaves an incomplete draft. Inspect it with
`gh api repos/anzaldoivan/dinorand/releases/tags/vX.Y.Z`; upload only verified missing assets with
`gh release upload vX.Y.Z <files>`, repeat the exact asset-set and checksum checks from the workflow,
then publish with `gh release edit vX.Y.Z --draft=false`. Never create or move a replacement tag.

## Pinned tools and dependencies

`global.json` and `actions/setup-dotnet` both pin SDK `8.0.423`; `rollForward: disable` prevents a
feature-band fallback. Solution and RID restores use `--locked-mode`; build/test/publish use
`--no-restore` where supported. Executable and test roots own tracked lockfiles, and the existing AP
client lock remains authoritative.

Every Action tag below was resolved against its official repository with `git ls-remote <official
repository> refs/tags/<tag>` on 2026-07-23:

| Action | Tag | Full commit |
|---|---|---|
| `actions/checkout` | `v4.2.2` | `11bd71901bbe5b1630ceea73d27597364c9af683` |
| `actions/setup-dotnet` | `v4.3.1` | `67a3573c9a986a3f9c594539f4ab511d57bb3ce9` |
| `actions/setup-python` | `v5.6.0` | `a26af69be951a213d495a4c3e4e4022e16d87065` |
| `actions/upload-artifact` | `v4.6.2` | `ea165f8d65b6e75b540449e92b4886f43607fa02` |
| `actions/download-artifact` | `v4.3.0` | `d3f86a106a0bac45b974a628896c90dbdf5c8093` |
| `schneegans/dynamic-badges-action` | `v1.7.0` | `e9a478b16159b4d31420099ba146cdc50f134483` |
| `actions/dependency-review-action` | `v4.7.1` | `da24556b548a50705dd671f47852072ea4c105d9` |
| `actions/attest-build-provenance` | `v2.4.0` | `e8998f949152b193b063cb0ec769d69d929409be` |

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

## Remote safety gate and current deviation

The legacy remote `release.yml` must be disabled before this change merges and remain disabled until
all post-merge checks below pass:

```bash
gh workflow disable release.yml --repo anzaldoivan/dinorand
gh workflow view release.yml --repo anzaldoivan/dinorand
```

Authentication succeeded on retry on 2026-07-24. Workflow `#309012384` was read back as
`disabled_manually` and must remain in that state until the hardened workflow is on `main` and the
post-merge checks below pass.

## Repository settings and rollback

Mode-0700 before/after receipts and exact rollback payloads were captured under
`/tmp/dinorand-public-hardening-remote-20260724`. Before-state was: description
`Randomizer for classic Dino Crisis`; topics `mod`, `randomizer`, `dino-crisis`; release workflow
active; private vulnerability reporting, immutable releases, vulnerability alerts, automated
security fixes, secret scanning, push protection, and CodeQL default setup disabled. Actions already
used read-only default permissions, could not approve pull requests, allowed all Actions, and did not
require full-SHA pinning.

The approved metadata and security controls were enabled and read back. The complete `main-protection`
ruleset was round-tripped: bypass actors and existing rules/checks were preserved, and only
`branch-name`, `apworld`, and `apworld-ap-integration` were added. The release-branch and release-tag
rulesets, releases, and tags remained unchanged.

The protected receipt directory contains `rollback-commands.sh`, which was generated but not run.
The durable equivalent is:

```bash
printf '%s\n' '{"description":"Randomizer for classic Dino Crisis","security_and_analysis":{"secret_scanning":{"status":"disabled"},"secret_scanning_push_protection":{"status":"disabled"}}}' | \
  gh api --method PATCH repos/anzaldoivan/dinorand --input -
printf '%s\n' '{"names":["mod","randomizer","dino-crisis"]}' | \
  gh api --method PUT repos/anzaldoivan/dinorand/topics --input -
gh api --method DELETE repos/anzaldoivan/dinorand/private-vulnerability-reporting
gh api --method DELETE repos/anzaldoivan/dinorand/immutable-releases
gh api --method DELETE repos/anzaldoivan/dinorand/vulnerability-alerts
gh api --method DELETE repos/anzaldoivan/dinorand/automated-security-fixes
printf '%s\n' '{"state":"not-configured"}' | \
  gh api --method PATCH repos/anzaldoivan/dinorand/code-scanning/default-setup --input -

gh api repos/anzaldoivan/dinorand/rulesets/18643611 > /tmp/dinorand-main-ruleset-current.json
jq '(.rules[] | select(.type == "required_status_checks") |
      .parameters.required_status_checks) |=
      map(select(.context != "branch-name" and .context != "apworld" and
                 .context != "apworld-ap-integration")) |
    {name,target,enforcement,bypass_actors,conditions,rules}' \
  /tmp/dinorand-main-ruleset-current.json > /tmp/dinorand-main-ruleset-rollback.json
gh api --method PUT repos/anzaldoivan/dinorand/rulesets/18643611 \
  --input /tmp/dinorand-main-ruleset-rollback.json
gh workflow enable release.yml --repo anzaldoivan/dinorand
```

Before any future settings change, save its GET response mode-0700 and generate the inverse command
from that response. Never guess an unavailable prior value.

The applied public metadata and its read-back commands are:

```bash
gh api --method PATCH repos/anzaldoivan/dinorand \
  -f description='Cross-platform Dino Crisis 1 & 2 randomizer for the GOG DRM-free releases, with deterministic seeds, reversible installs, and Archipelago support.'
printf '%s\n' '{"names":["archipelago","avalonia","csharp","dino-crisis","dotnet","game-randomizer","gog","modding","randomizer","reverse-engineering"]}' | \
  gh api --method PUT repos/anzaldoivan/dinorand/topics --input -
gh api repos/anzaldoivan/dinorand --jq '{description,visibility,default_branch}'
gh api repos/anzaldoivan/dinorand/topics --jq '.names'
```

## Required post-merge order

Do not run these steps until the workflow pins and scripts above are in `main`, the icon P0 is
resolved, and the legacy release workflow is confirmed disabled.

1. Confirm every `uses:` on `main` is a full SHA, then preserve the current Actions policy while
   enabling mandatory SHA pinning:

   ```bash
   git fetch origin main
   git show origin/main:.github/workflows/release.yml | rg '@[0-9a-f]{40} # v'
   gh api repos/anzaldoivan/dinorand/actions/permissions > /tmp/dinorand-actions-before.json
   jq '.sha_pinning_required=true' /tmp/dinorand-actions-before.json | \
     gh api --method PUT repos/anzaldoivan/dinorand/actions/permissions --input -
   gh api repos/anzaldoivan/dinorand/actions/permissions --jq '{allowed_actions,sha_pinning_required}'
   ```

2. Confirm the three verified contexts already present on `main-protection`, then use the idempotent
   post-merge phase to protect release-tag creation and explicitly remove the obsolete release-branch
   ruleset:

   ```bash
   bash scripts/setup-rulesets.sh --phase post-merge
   gh api repos/anzaldoivan/dinorand/rulesets/18643611 --jq '{bypass_actors,rules}'
   gh api repos/anzaldoivan/dinorand/rulesets/18643614 --jq '{bypass_actors,conditions,rules}'
   bash scripts/setup-rulesets.sh --phase post-merge --remove-obsolete-release-branch-ruleset
   gh api repos/anzaldoivan/dinorand/rulesets --jq '.[] | [.id,.name,.target,.enforcement]'
   ```

3. Read back the approved metadata, private vulnerability reporting, immutable releases, alerts,
   automated security fixes, secret scanning, push protection, and CodeQL default setup. These were
   applied on 2026-07-24; do not mutate them again if the read-back already matches.

4. Re-enable only the merged tag workflow and verify its trigger from `origin/main`:

   ```bash
   git show origin/main:.github/workflows/release.yml | sed -n '1,35p'
   gh workflow enable release.yml --repo anzaldoivan/dinorand
   gh workflow view release.yml --repo anzaldoivan/dinorand
   ```

## P0 gates and known risks

- **P0: `src/DinoRand.App.Avalonia/resources/dinorand.ico — owner verification required`.** Its
  provenance/license has not been established. It must remain byte-identical and no tag or release
  may be produced until the owner resolves it.
- No screenshot or social preview is approved.
- No tag or release may be created until every P0 gate is closed and the repository settings have
  been read back successfully.
- Practical risk-reduction only, not legal advice; a lawyer confirms before publishing.
