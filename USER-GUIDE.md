---
title: "DinoRand — User Guide"
slug: user-guide
game: cross
subsystem: meta
doc_type: guide
status: validated
corpus: false
confidence: open
chunk: default
related: []
---

# DinoRand — User Guide

DinoRand randomizes **Dino Crisis 1 & 2** (PC): give it your unmodified install and a seed,
get back a shuffled but still-completable playthrough — items, enemies, music, loadout and
more. This guide is for players. Modders/contributors: see the repo
[README](README.md) and [CONTRIBUTING.md](CONTRIBUTING.md).

> Flag semantics on this page are one-line summaries. The **canonical, always-current
> reference is `dinorand --help`** — if this page and `--help` ever disagree, trust `--help`.

## What you need

- **Dino Crisis 1 and/or 2 — the DRM-free GOG release.** The Steam re-release is wrapped in
  "The Enigma Protector" DRM and is **not supported** (DinoRand never circumvents DRM —
  see [Legal](#legal)). DC2 support targets the Classic REbirth build.
- The DinoRand release binaries run standalone — **no .NET runtime needed**. Building from
  source needs the .NET 8 SDK (see [README](README.md)).

## Quick start (GUI)

1. Download the latest release for your OS and unpack it.
2. Run **`DinoRand.Avalonia`** (`DinoRand.Avalonia.exe` on Windows).
3. Point it at your game install folder and pick a seed (or let it roll one).
4. Leave the defaults on for a first run — every experimental lever is off by default,
   and default seeds are kept provably beatable.
5. Generate and install, then launch the game normally. Sharing the seed (and settings)
   with a friend reproduces the identical run.

## Quick start (CLI)

```bash
# Dino Crisis 1 — randomize with seed 12345 into mod_dinorand/, then overlay the install
dinorand --install /path/to/DinoCrisis --seed 12345 --install-to-data

# Dino Crisis 2 (Rebirth) — enemy randomization
dinorand --install /path/to/DinoCrisis2/rebirth --game dc2 --seed 12345 --install-to-data

# Undo everything
dinorand --install /path/to/DinoCrisis --restore
```

Without `--install-to-data`, the randomized files are only written to the output folder
(default `mod_dinorand/`) and your game is untouched.

## Undo / backups — read this once

- DinoRand **never edits your originals without a backup**. `--install-to-data` (and every
  targeted room/exe command) first copies the pristine files to `Data\.dinorand_backup`
  (some exe patches use a `.bak` beside the file).
- **`--restore` puts everything back** and removes the backup. Per-room commands restore
  per-room.
- `--verify-backup` audits the backup read-only (both games) — run it if you suspect a
  backup captured already-modified files.
- Commands that patch the executable (`--shuffle-bgm`, `--exe-patch`, box shuffles,
  starting-inventory options…) need the game **closed** first — Windows locks a running exe —
  and a relaunch to take effect.

## What gets randomized

**Dino Crisis 1** (main run): item pickups (pool ratios/difficulty-scaled), in-room enemy
permutes, optional key-item relocation (`--shuffle-keys`, kept provably beatable), optional
enemy HP, starting inventory/weapon, BGM shuffle + external music import, emergency-box
shuffles, cutscene-safe mode. Door randomization is built but **not yet enabled**.

**Dino Crisis 2** (main run + standalone commands): cross-species enemy randomization
(weighted or all-one-species), raptor colour/HP tiers, protagonist skins (Gail/Rick),
shop stock/prices, starting weapon, BGM shuffle + external music import/export.

## Flag reference (one line each — `--help` is canonical)

### Main run (both games)

| Flag | What it does |
|---|---|
| `--install <dir>` | Your game folder (DC1 root / DC2 `rebirth`). Originals are never modified in place. |
| `--game dc1\|dc2` | Target game (default `dc1`). |
| `--out <dir>` | Output folder (default `mod_dinorand`). |
| `--seed <n>` | Deterministic seed — same seed + settings = same run. |
| `--install-to-data` | Overlay the output onto the install, with backup. |
| `--no-items` / `--no-enemies` | Turn a pass off. |
| `--difficulty <0..1>` | Scales item pool generosity (and DC1 enemy HP band). |
| `--ratio-ammo` / `--ratio-health` / `--ammo-quantity` | Item-pool composition dials. |
| `--weapon-upgrade-chance` / `--pre-upgraded-weapon-chance` / `--disable-weapons` | Weapon pool dials. |
| `--no-spoiler` | Skip `SPOILER.md` (game files identical either way). |

### Dino Crisis 1

| Flag | What it does |
|---|---|
| `--shuffle-keys` | Relocate the door-gating keys; every seed proven beatable first. |
| `--dc1-enemy-hp` | Seeded per-placement HP for the enemy classes that honour it. |
| `--exotic-enemies` | EXPERIMENTAL: import foreign species (Theri etc.); applies exe patches; close the game first. |
| `--dc1-cutscene-safe` | Exclude choreographed cutscene rooms from enemy changes (palette tint instead). |
| `--random-inventory` / `--starting-items` / `--starting-weapon` | EXPERIMENTAL new-game loadout (exe patch; Handgun always granted / force-placed so seeds stay beatable). |
| `--shuffle-bgm` | Shuffle the music catalog within stream/loop classes (exe patch). |
| `--bgm-import --bgm-packs <dir>` | Import external mood-tagged music over `Sound/BGM` slots. |
| `--shuffle-boxes` / `--reroll-boxes` | EXPERIMENTAL emergency-box contents (exe patch; mutually exclusive). |

### Dino Crisis 2

| Flag | What it does |
|---|---|
| `--dc2-enemy-mode weighted\|fixed` + `--dc2-fixed-species` + `--dc2-weight <sp>=<w>` | How each room's donor species is picked (all-T-Rex runs = `fixed`). |
| `--include-setpiece-enemies` / `--include-boss-enemies` | Opt-in degenerate donors (Triceratops/Giga; T-Rex — auto-applies the killable-T-Rex exe patch). |
| `--dc2-allow-water-swaps` | EXPERIMENTAL: land donors in water levels + aquatic donors in the wave pool. |
| `--dc2-raptor-tiers` (+ `--dc2-raptor-weight/colour`, `--dc2-blue-combo`) | Raptor colour/HP variants; wave rooms via exe patch. |
| `--dc2-character-skin` / `--dc2-regina-skin` | Render the protagonist as Gail/Rick (visual only). |
| `--dc2-trex-killable` / `--dc2-inostra-spawn-guard` | Force the safety exe patches on. |
| `--dc2-shuffle-shop --install <dir> [--restore]` | Standalone: shuffle shop stock/prices. |
| `--dc2-randomize-start-weapon` / `--dc2-start-weapon dylan=<id>,regina=<id>` | Standalone: new-game main weapon (crash-prone ids excluded unless `--allow-unsafe`). |
| `--dc2-shuffle-bgm [--restore]` / `--dc2-export-bgm` / `--dc2-import-bgm --bgm-packs <dir>` | Standalone: music shuffle / export to MP3 / external import (ffmpeg for non-MP3). |

### Targeted one-shot tools (advanced)

Single-room/single-door probes for testing and modding — `--swap-species`, `--add-enemy`,
`--set-door`, `--set-item`, `--exe-patch`, `--copy-enemy-palette`, `--dc2-swap-enemies`,
`--dc2-edit-door`, `--voice-preview`. All follow the same backup/`--restore` contract; several
say **CE-verify** in `--help` — they are lab tools, expect rough edges.

## Seeds, spoilers and the playthrough log

Every run writes into the output folder:

- **`SPOILER.md`** — a bug-report debug block on top (safe to share), then room-by-room
  spoilers below a marker, including a **"Playthrough (DC1 spheres)"** section: sphere 0 is
  what you can reach empty-handed; each later sphere lists the keys that open the next one.
  `--no-spoiler` skips the file.
- **`log_dinorand.txt`** — the full generation log.
- **`map.dgml`** — the seed's room graph (opens in Visual Studio).

## Safety and known limits

- Defaults are conservative: anything marked **EXPERIMENTAL** in `--help` is off unless you
  turn it on, and known crash-prone combinations are excluded unless you pass
  `--allow-unsafe` (which exists for crash-testing, not for playing).
- Seeds with item/key randomization are verified beatable before they ship; if generation
  can't prove it, it rerolls or falls back rather than emitting a softlock.
- If the game crashes on a randomized seed: run `--restore` to get back to vanilla, and
  report the issue with the **debug block from the top of `SPOILER.md`** (it identifies the
  seed and settings without spoiling your run).

## Archipelago (experimental)

DinoRand ships Archipelago multiworld worlds under `apworld/` (DC1 + DC2, generation only —
no in-game client yet). Setup: [apworld/dino_crisis_1/README.md](apworld/dino_crisis_1/README.md).

## Legal

DinoRand is an unofficial fan project, not affiliated with Capcom. It ships **no game
assets** — you must own the game — and only targets the DRM-free build (it never bypasses
copy protection). Full notice: [README — Legal](README.md#legal).
