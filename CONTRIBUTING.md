# Contributing to DinoRand

> **Why this file lives at the repo root** (not under `docs/`): `docs/` is held back
> from publish, and GitHub automatically links a root `CONTRIBUTING.md` from the PR and
> issue UI. Contributors should always see it.

## Branching & pull requests

- **Branch off `main`** into a **`feature/<name>`** branch (releases use `release/vX.Y.Z`).
  No direct pushes to `main` — it is protected by repository rulesets
  (see `scripts/setup-rulesets.sh`). PR branch names are checked by the `branch-name` CI job.
- **All changes land via pull request.** Every PR needs the `build-test-coverage` and
  `no-copyrighted-files` (`scan`) checks green and **≥1 approving review**; stale approvals are
  dismissed on new pushes. (Repo admins can bypass in an emergency.)
- Read `CLAUDE.md` and `docs/CONTRIBUTING-RE.md` for the reverse-engineering session
  contract (append decoded symbols/findings in the same change).
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

## Releases

Releases are automated by `.github/workflows/release.yml`. To cut one:

1. Pick the version `X.Y.Z` (append `-rc1` / `-beta` etc. for a prerelease).
2. Create and push a version branch:

   ```bash
   git switch -c release/vX.Y.Z
   git push -u origin release/vX.Y.Z
   ```

3. The workflow builds self-contained single-file executables for
   `win-x64`, `linux-x64`, and `osx-arm64` (via `scripts/publish-release.sh`), then
   creates the GitHub Release for tag `vX.Y.Z` with a per-RID zip attached to each.
   A `-suffix` version is marked **prerelease** automatically.

`workflow_dispatch` (run from the release branch) is a manual fallback if the branch
already exists.
