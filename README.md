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

# DinoRand

[![CI](https://github.com/anzaldoivan/dinorand/actions/workflows/ci.yml/badge.svg)](https://github.com/anzaldoivan/dinorand/actions/workflows/ci.yml)
[![coverage](https://img.shields.io/badge/coverage-60%25-yellow)](https://github.com/anzaldoivan/dinorand/actions/workflows/ci.yml)
[![no-copyrighted-files](https://github.com/anzaldoivan/dinorand/actions/workflows/no-copyrighted-files.yml/badge.svg)](https://github.com/anzaldoivan/dinorand/actions/workflows/no-copyrighted-files.yml)

<!-- Coverage badge is static (60%, the current line-coverage floor). To make it auto-update, see CONTRIBUTING.md /
     the CI runbook: Codecov (works on private repos) or a shields.io endpoint (needs a public repo). -->

A randomizer for **Dino Crisis 1 & 2**, modelled on [BioRand](https://github.com/biorand/classic),
the classic Resident Evil randomizer.

Give it an unmodified Dino Crisis 1 install (the **DRM-free GOG release**) and a seed;
get back a randomized, still-completable playthrough — items, enemies, loadout, and
(eventually) the whole door graph.

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
src/DinoRand.App           WPF GUI front-end → DinoRand.exe (Windows only)
src/DinoRand.App.Avalonia  cross-platform GUI port (in progress — see AVALONIA-PORT.md)
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

### Windows GUI executable (PowerShell)

`src/DinoRand.App` is the WPF front-end; it builds to **`DinoRand.exe`** and runs **only on
Windows**. From a PowerShell prompt at the repo root (with the .NET 8 SDK installed):

```powershell
# Build and launch the GUI in one step
dotnet run --project src\DinoRand.App

# …or produce a standalone DinoRand.exe and run it
dotnet build src\DinoRand.App -c Release
.\src\DinoRand.App\bin\Release\net8.0-windows\DinoRand.exe
```

> The `.App` project targets `net8.0-windows` (WPF). It _compiles_ on Linux/WSL
> (`EnableWindowsTargeting`) but can only be **launched** on Windows — use the
> cross-platform `DinoRand.Cli` for headless/WSL runs.

### Cross-platform GUI (Avalonia)

`src/DinoRand.App.Avalonia` is the cross-platform GUI (Avalonia 12, `net8.0`); it builds to
**`DinoRand.Avalonia`** and runs on Windows, Linux and macOS. It is the front-end going forward
(the WPF `.App` above is being retired — see [AVALONIA-PORT.md](docs/decisions/cross/AVALONIA-PORT.md)). From the repo root
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
scripts/publish-release.sh                 # default: win-x64 (GUI + CLI)
scripts/publish-release.sh linux-x64       # CLI only (GUI is Windows-only, auto-skipped)
scripts/publish-release.sh win-x64 osx-arm64 linux-x64   # several RIDs at once
```

Artifacts land in `dist/<rid>/`:

- `DinoRand.exe` — WPF GUI front-end (`win-*` RIDs only)
- `dinorand` / `dinorand.exe` — CLI front-end (every RID)

No data files ship alongside the binary: the game data is already baked in (`map.json` is an
embedded resource; the rest of `data/dc1` is compiled into `Definitions/DinoCrisis1.cs`), so
each published executable is the whole tool.

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
