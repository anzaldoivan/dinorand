# Contributing to DinoRand

> **Why this file lives at the repo root** (not under `docs/`): `docs/` is held back
> from publish, and GitHub automatically links a root `CONTRIBUTING.md` from the PR and
> issue UI. Contributors should always see it.

## Branching & pull requests

- **Branch off `main`** into a **`feature/<name>`** branch.
  No direct pushes to `main` — it is protected by repository rulesets
  (see `scripts/setup-rulesets.sh`). PR branch names are checked by the `branch-name` CI job.
- **All changes land via pull request.** Every PR needs the `build-test-coverage` and
  `no-copyrighted-files` (`scan`) checks green and **≥1 approving review**; stale approvals are
  dismissed on new pushes. (Repo admins can bypass in an emergency.)
- For reverse-engineering changes, verify repository registries before relying on an address or
  layout, cite evidence, record new symbols/findings in the owning public registry when that
  registry is published, preserve failed approaches as contributor notes, and never commit game
  bytes, copied disassembly, dumps, or third-party guide text.
- **Never commit game bytes or copyrighted content.** `scripts/check-no-copyrighted-files.sh`
  runs in the pre-commit hook and CI — don't bypass it with `--no-verify`.

## Tests & the coverage gate

New behaviour needs tests. CI enforces a **line-coverage floor**: the
`build-test-coverage` job fails if overall line coverage drops below the current
threshold. The floor is meant to **ratchet upward** — a feature that lowers coverage
either gets tests or raises the floor; it does not merge below it.

Run the exact same coverage step CI runs, locally:

```bash
dotnet tool restore        # one-time: installs ReportGenerator
bash scripts/coverage.sh   # tests → Cobertura XML + HTML + Summary.txt
```

Outputs land in `coverage-report/`:

- `Summary.txt` — the plain-text table (this is what CI posts to its job summary)
- `index.html` — line/branch heatmap
- `Cobertura.xml` — machine-readable; CI reads the overall `line-rate` from here

**Game files for the integration tests.** The room round-trip gates run against a real Dino
Crisis install when one is configured, and otherwise fall back to **DMCA-safe synthetic mock
rooms** (so they still run on CI, which never has game files). To run them against your own
install, copy `.env.example` → `.env` (gitignored) and set `DINORAND_DC1_DIR` / `DINORAND_DC2_DIR`.
Never commit game bytes.

## Documentation and changelog

Documentation is a product surface, not an implementation log. Before editing a document, identify
its audience and write at that reader's level:

| File or area | Audience | Purpose |
|---|---|---|
| `README.md` | New users and builders | Brief project overview, quick start, and links |
| `USER-GUIDE.md` | Players | Task-oriented installation, options, safety, and recovery |
| `CHANGELOG.md` | Players and release readers | Curated, user-visible release notes |
| `MODDING.md` | Modders | High-level format and tooling map |
| `CONTRIBUTING.md` | Contributors | Development, review, and documentation process |
| `docs/reference/`, `tools/`, and handoffs | Researchers and maintainers | Evidence, implementation detail, and technical history |

Public root documentation should explain what a person can do and why it matters. Prefer active,
concrete language over commit titles or implementation nouns. Do not put offsets, symbols, opcode
numbers, patch-class names, test internals, or decision-record identifiers in release notes. Put that
detail in the technical record and link to it only from contributor-facing material.

For `CHANGELOG.md`:

- Add only a player-visible change; internal refactors and research-only work do not need an entry.
- Use one logical bullet per change, normally one or two sentences.
- Say what changed, what the player gets, and the flag/toggle, default, safety, or limitation that
  affects their decision.
- For fixes, describe the symptom and the result, not the mechanism that caused the bug.
- Keep the existing `Added`, `Changed`, `Fixed`, `Removed`, and `Withdrawn` structure. Delete empty
  headings when cutting a release.

Feature and refactor workers should return a `user_visible_changes` section in their handoff or PR
description with plain-language candidates. They should not paste implementation notes directly into
the public root documents unless that document is explicitly assigned to them. The release owner
curates those candidates into `CHANGELOG.md` at release time and gives the final tone/readability
review. The `--help` output remains the canonical exhaustive CLI reference; user guides summarize it.

Generated documentation is never hand-edited. Run its generator and its `--check` mode as described
by the owning script. The local `docs/` audience plan is supplementary; this tracked section is the
repository's durable contributor-facing policy.

## Releases

Releases are automated by `.github/workflows/release.yml`. To cut one:

1. Pick `X.Y.Z` or a SemVer prerelease such as `X.Y.Z-rc.1`. Update
   `Directory.Build.props` and add an exact, non-empty `CHANGELOG.md` section for that version.
2. Merge the checked release commit to protected `main` and wait for all required checks.
3. Create the tag at that exact commit, verify it is contained in `origin/main`, and push only the
   existing tag:

   ```bash
   git fetch origin main
   git merge-base --is-ancestor HEAD origin/main
   git tag -a vX.Y.Z -m "DinoRand vX.Y.Z"
   git push origin refs/tags/vX.Y.Z
   ```

4. The tag-push workflow independently validates the existing tag and `main` ancestry, builds and
   attests the three RID archives and two `.apworld` files, then creates a draft with `SHA256SUMS`.
   It publishes only after the draft asset set is complete. There is no manual or branch trigger.

Operator settings, recovery, and post-merge steps are documented in [.github/RELEASE.md](.github/RELEASE.md).
