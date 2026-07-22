---
title: "DinoRand"
slug: readme
game: cross
subsystem: meta
doc_type: index
status: validated
corpus: false
confidence: open
chunk: default
related: []
---

<!-- Audience: new users and builders. Keep this page a brief orientation; detailed player steps belong in USER-GUIDE.md. -->

# DinoRand

[![CI](https://github.com/anzaldoivan/dinorand/actions/workflows/ci.yml/badge.svg)](https://github.com/anzaldoivan/dinorand/actions/workflows/ci.yml)
[![coverage](https://img.shields.io/endpoint?url=https://gist.githubusercontent.com/anzaldoivan/b1a330aadd5e6deb20cf6ec18c738b6a/raw/dinorand-coverage.json)](https://github.com/anzaldoivan/dinorand/actions/workflows/ci.yml)
[![no-copyrighted-files](https://github.com/anzaldoivan/dinorand/actions/workflows/no-copyrighted-files.yml/badge.svg)](https://github.com/anzaldoivan/dinorand/actions/workflows/no-copyrighted-files.yml)

<!-- Coverage badge auto-updates: CI's "Update coverage badge gist" step writes PCT to a public gist on each
     main push; shields.io renders it via the endpoint URL above. -->

A randomizer for **Dino Crisis 1 & 2**, modelled on [BioRand](https://github.com/biorand/classic),
the classic Resident Evil randomizer.

Give it an unmodified Dino Crisis 1 install (the **DRM-free GOG release**) and a seed;
get back a randomized, still-completable playthrough — items, enemies, loadout, and
(eventually) the whole door graph.

> **Playing, not building?** Start with the **[User Guide](USER-GUIDE.md)** — install,
> flags, seeds, spoiler log, and how to undo everything.
> **Building on top?** The **[Modding & Format Guide](MODDING.md)** maps what's decoded,
> the data/library surfaces you can use, and the ground rules.

> **Supported edition: GOG (DRM-free) only.** The Steam re-release is wrapped in
> "The Enigma Protector" DRM; DinoRand does not support patching it. Use the GOG build.

> **Status: active.** DC1 (items, enemies, key-item logic, loadout, audio) and DC2 (enemy swaps,
> shop, BGM, voice, starting loadout) levers are implemented and tested; door-graph randomization is
> gated pending further RE. See **[DESIGN.md](docs/reference/cross/architecture/DESIGN.md)** for the full
> design/decision record and **[ROADMAP.md](docs/decisions/cross/ROADMAP.md)** for the build plan and current phase.

## Layout

```
src/DinoRand.FileFormats   "dino-utils": parse/write STxxx.DAT stage files (no logic)
src/DinoRand.Randomizer    seed-driven engine: graph, key-item logic, item/enemy passes
src/DinoRand.Cli           console front-end (cross-platform)
src/DinoRand.App.Avalonia  cross-platform GUI front-end → DinoRand.Avalonia (Win/Linux/macOS)
test/                      unit tests (codec + parser round-trips)
data/dc1/                  JSON game data (items, enemies, room metadata)
```

## Build (requires the .NET 8 SDK)

If you don't have the SDK, install it user-local in WSL/Linux (no sudo):

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet" && export PATH="$HOME/.dotnet:$PATH"   # add to ~/.bashrc to persist
```

Then:

```bash
dotnet build                                   # 0 warnings / 0 errors
dotnet test                                    # full unit suite (codec + parser + randomizer round-trips)
dotnet run --project src/DinoRand.Cli -- --help
# Run against your install (absolute path); output → mod_dinorand/
# (includes SPOILER.md — bug-report debug block on top, room-by-room spoilers below a
#  marker; --no-spoiler skips it. docs/decisions/cross/SPOILER-LOG-PLAN.md)
dotnet run --project src/DinoRand.Cli -- --install /path/to/DinoCrisis --out mod_dinorand --seed 12345
```

### Code coverage

Requires `dotnet tool restore` (one-time, installs ReportGenerator from `.config/dotnet-tools.json`).

```bash
dotnet tool restore   # one-time

# Full run: tests → Cobertura XML → HTML report → text summary printed to terminal
bash scripts/coverage.sh

# Quick: just run tests with raw Cobertura output (no HTML)
dotnet test \
  --settings test/DinoRand.FileFormats.Tests/coverage.runsettings \
  --collect:"XPlat Code Coverage" \
  --results-directory TestResults
```

Outputs:

- `coverage-report/index.html` — line/branch heatmap, open in a browser
- `coverage-report/Cobertura.xml` — machine-readable, CI-consumable
- `coverage-report/Summary.txt` — plain-text table printed at the end of the script

### Cross-platform GUI (Avalonia)

`src/DinoRand.App.Avalonia` is the GUI (Avalonia 12, `net8.0`); it builds to
**`DinoRand.Avalonia`** and runs on Windows, Linux and macOS. It is the sole GUI front-end (the
former WPF `.App` was retired — see [AVALONIA-PORT.md](docs/decisions/cross/AVALONIA-PORT.md)). From the repo root
with the .NET 8 SDK installed:

```bash
# Build and launch the cross-platform GUI in one step
dotnet run --project src/DinoRand.App.Avalonia

# …or produce a release build and run the executable
dotnet build src/DinoRand.App.Avalonia -c Release
./src/DinoRand.App.Avalonia/bin/Release/net8.0/DinoRand.Avalonia      # DinoRand.Avalonia.exe on Windows
```

> Needs a desktop session (it opens a window) — it can't run headless over plain SSH/WSL without an
> X server. For headless/WSL runs use `DinoRand.Cli`.

### Standalone release build

`dotnet build`/`dotnet run` produce **framework-dependent** binaries — they need the .NET 8
runtime installed to launch. For a release, use `scripts/publish-release.sh` to emit
**self-contained, single-file** executables that run with **no .NET runtime installed**:

```bash
scripts/publish-release.sh                 # default: win-x64 (Avalonia GUI + CLI)
scripts/publish-release.sh linux-x64       # Avalonia GUI + CLI
scripts/publish-release.sh win-x64 osx-arm64 linux-x64   # several RIDs at once
```

Artifacts land in `dist/<rid>/`:

- `DinoRand.Avalonia` / `DinoRand.Avalonia.exe` — cross-platform GUI front-end (every RID)
- `dinorand` / `dinorand.exe` — CLI front-end (every RID)

No data files ship alongside the binary: the game data is already baked in (`map.json` is an
embedded resource; the rest of `data/dc1` is compiled into `Definitions/DinoCrisis1.cs`), so
each published executable is the whole tool.

## Archipelago (experimental)

DinoRand ships a Python [Archipelago](https://archipelago.gg) world in **`apworld/dino_crisis_1/`**
(zip it as `dino_crisis_1.apworld` for AP's `custom_worlds/`, or drop the folder into a source
checkout's `worlds/` — verified against AP 0.6.7) **plus a DC1 runtime client** in the DinoRand CLI:
`dinorand --ap-connect <host[:port]> --ap-slot <name> --install <dir>` patches AP's item placement
into your GOG install and syncs the running game — pickups become checks, received items appear in
your inventory, reaching the goal completes your slot. The full loop (connect, checks, grants,
reconnect, goal) is live-verified end-to-end. The client polls `DINO.exe` from outside the
process (nothing injected) and therefore runs on the **Windows host only**. DC2 is generation-only
for now. It ships **no game assets** — the logic is distilled from authored data via
`scripts/gen_ap_logic.py`.

Setup + tests: **[apworld/dino_crisis_1/README.md](apworld/dino_crisis_1/README.md)** and the
USER-GUIDE "Archipelago" section.

## Contributing & Releases

Branch off `main` and open a PR — `main` is protected, and CI (`build-test-coverage`)
runs the tests behind a coverage floor. New features need tests that hold or raise it.
Releases are cut by pushing a `release/vX.Y.Z` branch, which auto-builds and publishes
the binaries. Full details: **[CONTRIBUTING.md](CONTRIBUTING.md)**.

## Legal

DinoRand is an **unofficial, fan-made** project. It is **not affiliated with, authorized
by, endorsed, or sponsored by Capcom**. "Dino Crisis" and all related names, marks, and
game assets are trademarks and/or copyrights of **Capcom Co., Ltd.** and their respective
owners.

DinoRand ships **no** Dino Crisis assets. You must own a legal copy of the game. The tool
reads your install and writes randomized copies to `mod_dinorand/`; it never modifies the
originals in place.

DinoRand deliberately targets the **DRM-free GOG build** and patches only gameplay data — it never
circumvents any technological protection measure (cf. DMCA §1201). The Steam re-release (wrapped in
"The Enigma Protector" DRM) is unsupported for that reason.

DinoRand's own source code is released under the [MIT License](LICENSE), which covers
**only** the project's authored code, documentation, and data — it grants no rights in
Capcom's intellectual property. Third-party components are credited in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Provided for interoperability and personal use with a game you own. This is **not legal
advice**; consult your game's EULA regarding reverse engineering and modification.
