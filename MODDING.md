---
title: "DinoRand — Modding & Format Guide"
slug: modding
game: cross
subsystem: meta
doc_type: guide
status: validated
corpus: false
confidence: open
chunk: default
related: []
---

<!-- Audience: modders. Summarize and link; detailed offsets and byte facts belong in the registries. -->

# DinoRand — Modding & Format Guide

For people who want to **build on top of** DinoRand's reverse-engineering: modders, tool
authors, and randomizer tinkerers. Players: see the [User Guide](USER-GUIDE.md).
Contributors to DinoRand itself: [CONTRIBUTING.md](CONTRIBUTING.md).

> **Publish status.** DinoRand's deep RE documentation (`docs/`) and the standalone model
> toolset (`tools/`) are **not yet published** — they exist and are maintained, but the
> repository currently ships only the code, the authored data, and this summary. Where this
> page says *"detailed notes: local docs, pending publish"*, that's the reason. If you need
> something specific from them, open an issue.

## What you can build on today (in this repository)

- **`src/DinoRand.FileFormats`** — the C# library that parses and writes both games' room
  files and executable tables. MIT-licensed, no game assets, usable as a library for your
  own tools. Format knowledge lives here; randomization policy deliberately does not.
- **`data/dc1/` and `data/dc2/`** — authored JSON: item and enemy catalogs, room metadata,
  the DC1 logic map, the DC2 door graph and wave-descriptor data, and more. Every file was
  derived by our own reverse engineering (no copied game tables) and each folder's README
  describes provenance.
- **The CLI's targeted one-shot commands** — `--set-door`, `--set-item`, `--add-enemy`,
  `--swap-species`, `--copy-enemy-palette`, `--dc2-swap-enemies`, `--dc2-edit-door`… are a
  scriptable modding surface in their own right: single-room edits with automatic backup and
  `--restore`. See `dinorand --help` and the [User Guide](USER-GUIDE.md#targeted-one-shot-tools-advanced).
- **`apworld/` + `scripts/gen_ap_logic.py`** — the distilled progression-logic model (rooms,
  gated edges, locations) as plain JSON, regenerable from the authored data. Useful if you
  want machine-readable game logic without touching C#.

## What's decoded (status summary — no byte facts here, the registries stay canonical)

| System | Status | Deep notes |
|---|---|---|
| DC1 room files (script VM: items, enemies, doors, key gates, triggers) | Decoded, validated, shipped in the randomizer | local docs, pending publish |
| DC1 executable tables (music catalog, enemy sounds, starting inventory, emergency boxes, puzzle codes, damage/stats) | Decoded; several shipped as levers | local docs, pending publish |
| DC2 (Rebirth) room containers, door graph, spawn/wave system, shop, loadout | Decoded and shipped for the systems the randomizer touches; item lever still unverified | local docs, pending publish |
| 3D model format (both games) + DC2 textures | Decoded and byte-validated; standalone read/write toolset with tests | `tools/DinoCrisis.Model` (pending publish) |
| Cross-game model interchange (Dino Crisis ↔ classic Resident Evil) | Working pipeline, motion retarget still open | `tools/DinoCrisis.Interchange` (pending publish) |
| Audio (DC1 music catalog + external import; DC2 music containers + import/export) | Decoded and shipped — see the BGM flags in the [User Guide](USER-GUIDE.md) | local docs, pending publish |
| Vanilla progression/reachability model | Generated reference pages exist (sphere order, door graphs) | generated docs, pending publish |

## Tooling that exists but isn't published yet

- **`DinoCrisis.Model`** (+ `.Cli`, + tests) — standalone C# DCM model library: read, write,
  round-trip, OBJ export, texture decode, model rebuilding. Own solution, MIT, no DinoRand
  dependency — designed to be shared.
- **`DinoCrisis.Interchange`** — DCM ↔ RE EMD/MD1 conversion (builds against IntelOrca's
  MIT `biohazard-utils`).
- **Python investigators** (`tools/scd_re`, `tools/dc2_re`, `tools/ce_re`) — the static-RE
  scripts that produced the decoded knowledge; read-only by convention.

## Ground rules for building on this

- **Never redistribute game bytes** — no extracted models, textures, audio, executables, or
  text dumped from the games. This repo's CI rejects them, and anything you build on top
  should hold the same line. Authored knowledge (your own notes, offsets you derived,
  original JSON) is fine.
- **GOG (DRM-free) builds only.** Nothing here may be used to bypass the Steam release's
  copy protection.
- The full legal posture is in the [README](README.md#legal).
