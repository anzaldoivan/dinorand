# Changelog

All notable changes to DinoRand are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project aims to
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html) (pre-1.0: minor
versions may include breaking changes).

The version number is set by `<VersionPrefix>` in [`Directory.Build.props`](Directory.Build.props).

## [Unreleased]

### Added — DC2 starting main-weapon randomizer (I3 in-game witness pending)

- **Starting main weapon** (`--dc2-randomize-start-weapon [--seed N]` random, or
  `--dc2-start-weapon dylan=<id>[,regina=<id>]` explicit; off by default): patches the two
  weapon-id bytes inside the Dino2.exe new-game bootstrap equip immediates (Dylan `0x450D69`,
  Regina `0x450E9D` — docs/decisions/dc2/loadout/DC2-STARTING-LOADOUT-PLAN.md). Ids are restricted to each
  character's own WEP_P band (cross-character is a decoded NO-GO); subweapon bytes are never
  written, so the Machete and Large Stun Gun always stay in the loadout. Rebirth build only,
  shared `.bak` contract, `--restore` reverts just the two bytes. Static probe:
  `tools/dc2_re/start_loadout_probe.py`. Takes effect on NEW GAME only.
- **UI**: "Randomize Starting Weapon (new game only)" checkbox in the DC2 options panel with
  per-character pick dropdowns (Random / explicit id; names beyond the stock shotgun/handgun
  labelled unverified until the I3 witness). Applied at install next to the other Dino2.exe
  patches; the Restore button's full-exe restore reverts it. Seed-encoded in a new byte 22
  (bit7 = on, bits 0–2/3–5 = Dylan/Regina pick) — old seed strings parse unchanged.
- **Fixed: spawn room no longer changes with the pick** (I3 RCA): the bootstrap's
  `mov edx,0x0101` immediate is dual-use — it is the new-game START ROOM (`scene+0x1090`)
  before it feeds the equip words; the original lever patched it and booted into
  ST103/ST104. The lever now leaves that immediate canonical (repairing older installs on
  re-apply) and patches Dylan's id via two same-length instruction rewrites of the
  downstream equip stores (`0x450E84`/`0x450E9F`), preserving the starting-EP constant.
- **Fixed: crash-class weapons excluded (Solid Cannon menu crash)** — the third-witness dump
  RCA'd `0x496D70` as the **weapon-select menu** ring builder (not the fire path): it divides
  `0x1000 / mainCount` with no zero guard. The Solid Cannon fires fine but its fire path
  empties its own inventory record, so once it is the only starting main, the ring drops to
  zero mains and the next weapon-select divides by zero (`0x496EAC`). The clean discriminator:
  among all band mains only `0x04` (Solid Cannon) and `0x07` (Grenade Gun) carry catalog byte
  `b8 = 0x40`. Those two are now **crash-class** (`Dc2StartingLoadoutPatch.CrashClassStartWeaponIds`)
  and refused as starting weapons: random never picks them, the UI dropdowns don't list them,
  and a shared seed carrying one is rejected on parse. Explicit CLI installs refuse them too
  unless `--allow-unsafe` (kept for the open decode of whether a per-id ammo count could
  re-admit them). Witnessed: 0x03 = Rocket Launcher (fires + menu clean), 0x04 = Solid Cannon
  (excluded).
- **Inventory follows the weapon** (I3 live-witness fix): the equip immediates alone left the
  new weapon with 0 ammo and the status screen showing the shotgun — the new-game inventory
  init `0x496750` stocks `scene+0x10BC` from its own hardcoded templates (decoded + CE-
  witnessed live). The lever now also rewrites the main-weapon record id in all three
  templates (Dylan's register-sourced record via a same-length imm16 instruction rewrite);
  ammo counts stay canonical, restore is byte-identical to pristine.

### Fixed — DC2 starting-weapon guard is now an owned-MAIN rule (closes a latent crash gap)

- The `b8 = 0x40` "crash class" label was coincidental — `b8`/`b9` are inventory-icon atlas U/V
  coordinates, not a weapon property (docs/reference/dc2/weapon/DC2-WEAPON-SYSTEM-INVESTIGATION.md §3). The real
  fault is `mainCount == 0` → unguarded `idiv 0x1000/mainCount` at `0x496EAC`. The guard is now the
  **owned-MAIN rule**: a starting main must have the catalog MAIN flag (`0x704260` bit 3) AND the
  character's ownership bit, minus the fire-empty residual `0x07`. This closes three ids the old
  guard wrongly admitted (all `b8 = 0x00`): Dylan `0x05` (Regina-owned main) / `0x09` (a SUB) and
  Regina `0x06` (a SUB). Safe sets: Dylan `{0x01, 0x03}`, Regina `{0x02, 0x05, 0x08}`. Wire bands
  and seed byte-22 unchanged; grounded by `tools/dc2_re/weapon_catalog_probe.py`.

### Added — DC2 shop shuffle

- **Shop shuffle** (`--dc2-shuffle-shop [--seed N]` / UI "Shuffle Shop" checkbox, off by default;
  seed-encoded in byte 16 bit 2 so shared seed strings carry it): permutes
  the Dino2.exe shop economy — the 11 for-sale items' retail prices (master table `0x71DCB8`) and
  their stock-unlock shop-level bitmasks (catalog `0x704260+id*12+0xA`), first static decode in
  docs/decisions/dc2/shop/DC2-SHOP-RANDO-PLAN.md (9 prices validated vs GameFAQs retail). Every item stays
  purchasable at some shop level by construction; difficulty half/double pricing still applies on
  top. Rebirth build only, `.bak`-protected, `--restore` rewrites just the shop tables
  (byte-identical to pristine, verified). In-game witness (I3) pending.

### Added — DC2 raptor colour modes (colour now follows the tier)

- Live CE capture proved room colour loads from `desc+4` RAW while stats come from the exe pair
  table — the two were desynced, so randomized rooms looked vanilla. The tier pass now draws ONE
  weighted variant per room (written to `desc+4` and every static variant nibble) with two modes
  (`--dc2-raptor-colour` / UI "Colour mode", seed-encoded): **room** (default; colour == strength,
  identity pair table) and **mixed** (room colour = strongest tier present; pair table
  `{colour, weaker draw}`). Engine ceilings documented: one raptor skin per room; double-size
  E00_02/E00_03 textures may render brown on the PC port. docs/reference/dc2/enemies/RAPTOR-TIER-RE.md §4b/4c.

### Added — DC2 raptor tier randomization + blue-raptor options

- **Randomize Raptor Tiers** (`--dc2-raptor-tiers` / UI checkbox, off by default): shuffles the
  decoded raptor colour/HP tier variants (docs/reference/dc2/enemies/RAPTOR-TIER-RE.md). Static op-0x1a raptor
  spawns get per-spawn weighted variant draws (room-file edit via the shipped `Dc2SpawnEditor`,
  flag bits preserved); wave rooms get per-room `desc+4` jitter plus a seeded weighted rewrite of
  the Dino2.exe wave pair table (`0x703E9C`, rebirth build only, `.bak`-protected, undone by
  Restore). Per-variant weights: `--dc2-raptor-weight <0..7>=<0..15>` / UI sliders
  (`data/dc2/raptor-tiers.json` defaults; the V5 blue/super raptor at weight 1). Runs after the
  cross-species pass on the same room bytes (new context working-bytes pipeline) and skips
  spawns/waves converted away from raptor.
- **Blue Raptor Spawn Condition** (`--dc2-blue-combo <1..20>` / UI slider, default 20 = vanilla):
  the max-combo hit count that arms the natural variant-5 blue/super raptor for the next room
  (decoded arm site `0x41E59F`; 1-byte imm patch at `0x41E5A6`). Works independently of tier
  randomization.
- Seed strings grew an optional raptor block (bytes 17–21) so these options share/round-trip like
  everything else; old seeds parse unchanged.
- Data plumbing: `spawn-graph.json` now records the op-0x1a `VARIANT` (block+0x08) operand
  (regenerated from pristine backups; `edit_spawn.py` field renamed from the stale "HP" label);
  `Dc2WaveTable` reads `variant_off`; variant 7 recognized as a valid stock tier (V0–V7).

### Added — DC2 skin swap now carries the menu NAME text

- **The inventory/status menu name follows the skin** (last gap after portrait/plate/voice): the
  name is a shared "REGINADYLAN" 4bpp glyph run present byte-identically in every CORE atlas
  (strip 0 rows 40–45; the EC donor COREs carry it too, which is why the atlas swap alone left the
  name reading "Dylan"), sub-rect-selected by char code at draw time. `BuildCoreFile` now composes
  "GAIL"/"RICK" over the target character's name span from same-style glyphs already in the atlas
  (name-run letters + C/K from strip 1's RECOVERY/KEY ITEM labels) via the new nibble-precise
  `ComposeMenuName`. No new toggles — rides the existing skin settings. Decode:
  docs/decisions/dc2/models/DC2-INVENTORY-UI-SWAP-PLAN.md §8. In-game eyeball pending.

### Added — DC2 character skin swap now carries the pause-menu portrait and team name

- **The Dylan/Regina → Gail/Rick skin swap now also swaps the inventory/status-menu character
  art**: `Dc2PlayerModelSwap` additionally emits the target's `CORE01.DAT`/`CORE00.DAT` with the
  donor's (`CORE06`/`CORE05`) menu-atlas TEXTURE+PALETTE entries (same-size in-place copy — the
  per-character face portrait is the only art difference), plus the S.O.R.T. team-plate row copied
  over the T.R.A.T. row so the swapped Dylan shows the correct team name (the menu picks the TRAT
  row whenever the char code is 1). Per-char params (DATA) entries are untouched.
  Decode + live CE witness: docs/decisions/dc2/models/DC2-INVENTORY-UI-SWAP-PLAN.md. In-game eyeball pending.

### Added — DC2 skin swap now carries the character voice/SFX bank

- **The swapped character speaks with their own voice**: the same CORE emit now also swaps the
  SOUND entry (the per-character grunt/hurt/death RIFF-WAV bank) from the donor via the new
  `PackageRepacker.ReplaceEntryDc2` (DC2 32-byte-stride entry replace: header verbatim, size dword
  updated, payloads re-laid at 2048 alignment). Safe by decode: the engine heap-stages the bank
  and resolves samples bank-relatively — no baked addresses, so sizes are free to differ, and
  missing donor samples degrade to silence, not a crash
  (docs/decisions/dc2/voice/DC2-CHARACTER-VOICE-SFX-PLAN.md). Rides the existing
  `--dc2-character-skin`/`--dc2-regina-skin` toggles (no new option, seed strings unchanged).
  **Ear-confirmed in-game 2026-07-04.**
- **Classic REbirth installs**: CR plays SFX from its own `CR\data\<pkg>\snd.wbk` HD wavebanks,
  bypassing the CORE SOUND bank (live CE witness — the rebirth build's sound-bank tables stay
  zero). The app's install now also swaps the per-character CR wavebanks
  (`Dc2CharacterSkinInstaller.ApplyCrWavebanks`, one-time `.dinorand-bak` backups, no-op on
  vanilla installs) and Restore reverses them (`RestoreCrWavebanks`). TDD:
  `Dc2CrWavebankTests` (synthetic tree, always run).

### Added — DC2 BGM shuffle: `--dc2-shuffle-bgm`

- **DC2 music randomizer** — permutes the `Dino2.exe` music-file pointer table (slots 150..217 →
  `ME_/MF_/MS_*.DAT` stream containers) within like-classes (identical track-index sets, derived
  from the install's own container headers), deterministic from `--seed`. Reversible: one-time
  pristine `.bak` plus a slice-only `--restore` that leaves other exe patches intact.
  **Live-witnessed in-game 2026-07-05** (docs/decisions/dc2/audio/DC2-BGM-RANDO-PLAN.md I3: memory poll caught the
  loader consuming the rerouted file for a vanilla music id on room entry, ear-confirmed distinct,
  no Classic REbirth regression). This overturns the
  prior docs/reference/dc2/audio/AUDIO-RE.md "no static BGM lever" DEFER verdict: the in-EXE filename table
  (`0x71B230`), its consumer (`0x404280`), and the `MS_*` container format were decoded and
  probe-validated (`tools/dc2_re/ms_bgm_probe.py`, 65/65).

### Changed — DC2 flyer-native rooms are no longer swap targets

- **Rooms natively spawning the Flyer (Pteranodon, TYPE `0x04`) are now skipped by the enemy
  randomizer**, same as aquatic/non-land rooms. Live-verified (2026-07-04): a ground species
  replacing a flyer spawns outside the level hitbox — unreachable, not a crash. Implemented as one
  shared predicate `Dc2SpeciesTable.IsNonLandNativeType` (Aquatic/NonLand/Flyer) now used by all
  skip sites (`Dc2CrossSpeciesPlanner` + `Dc2RoomEnemySwap.IsAquaticNativeRoom`). Flyer donors were
  already excluded; donor selection is unchanged.

### Changed — DC2 room ST905 excluded from the enemy randomizer

- **ST905 (the Extra Crisis bonus level, identified 2026-07-04) is now hard-excluded** via the
  hand-curated `Dc2RoomExclusions.Setpiece` set, so `Dc2EnemyRandomizer` leaves it vanilla.

### Fixed — DC2 install could ship an unbootable seed (container-stride corruption)

- **A DC2 seed could crash on new-game load (first room `ST101`).** Root cause
  ([docs/decisions/dc2/crash-rcas/DC2-ROOM-CONTAINER-STRIDE-CRASH-RCA.md](docs/decisions/dc2/crash-rcas/DC2-ROOM-CONTAINER-STRIDE-CRASH-RCA.md)):
  the installer overlaid **every `*.dat`** in the reused working dir with no format check, so a stale
  wrong-format room file (a DC1 16-byte-entry container where the DC2 engine reads 32-byte entries) left
  from an earlier experiment got installed — the engine misread its resource directory into an
  out-of-range Classic REbirth GPU index and hard-crashed. Not a species/enemy edit.
- **Three-layer fix** ([docs/decisions/dc2/install/DC2-INSTALL-INTEGRITY-PLAN.md](docs/decisions/dc2/install/DC2-INSTALL-INTEGRITY-PLAN.md)):
  (1) `GameInstaller.Install` overlays **only the files the run recorded** (`onlyFiles` allow-list,
  from `Dc2RunResult.WrittenFiles`); (2) the runner **clears stale `*.dat`** from the working dir per
  run (`RunOutputDir`); (3) container-format guards refuse to overlay — or emit — a room whose Gian
  entry stride differs from its pristine/original (`ContainerFormatMismatchException`,
  `Dc2RoomEmitGuard`). Non-Gian fixtures and the DC1/voice overlay-all paths are unaffected.

### Added — per-seed spoiler log (`SPOILER.md`)

- **Every generated seed now writes a `SPOILER.md` beside its randomized files** (both games;
  decision record: [docs/decisions/cross/SPOILER-LOG-PLAN.md](docs/decisions/cross/SPOILER-LOG-PLAN.md)). Debug block FIRST —
  the shareable `DINO-…` seed string (round-trips through the app's seed box), int seed, game,
  DinoRand version, timestamp, full config dump, pass log, and output file list — all
  spoiler-free and safe to paste in a bug report; the room-by-room diff tables sit below an
  unmissable `⚠ SPOILERS BELOW` marker.
- **Tables adapt to the options the seed used** (a disabled pass produces no section):
  DC2 cross-species enemies (per room: vanilla species → donor, edit counts, the distribution
  mode + donor tally, and skip summaries for set-piece/aquatic/no-donor rooms), DC1 items
  (per physical pickup: vanilla item → new item, with real names and room names), DC1 key-item
  relocations (+ beatability verdict), and the DC1 in-room enemy permute.
- **Zero behavioral impact, by construction and by regression test**: passes record typed diff
  entries into a `SpoilerCollector` on the run context at the moment they decide (never
  re-simulated, never parsed from logs), and the file is written strictly after every game file —
  the same seed emits byte-identical game files with the spoiler on or off. CLI `--no-spoiler`
  suppresses the file.
- The `DINO-…` wire format now lives in `DinoRand.Randomizer` (`SeedString`); the app's
  `AppSeed` delegates to it (encoding unchanged, byte-for-byte — existing seed strings and
  tests unaffected). The full DC1 item-name table (`data/dc1/items.json`) ships embedded for
  readable spoiler rows.

### Added — DC2 configurable enemy distribution (weighted + fixed donor modes)

- **The DC2 enemy randomizer's donor pick is now configurable** (was: uniform over the donor pool).
  Two modes (decision record: [docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md](docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md),
  grounded in a BioRand weighting audit,
  [docs/parity/ENEMY-DISTRIBUTION-PARITY-AUDIT.md](docs/parity/ENEMY-DISTRIBUTION-PARITY-AUDIT.md)):
  - **Weighted** (default): per-species weights (0–15) bias each room's donor pick over its valid
    donors. Curated defaults live in the new registry `data/dc2/enemy-distribution.json` — every
    non-boss species 8 (with untouched settings this reproduces the previous uniform behavior
    **stream-identically**, so existing default seeds are unchanged), bosses rare (T-Rex 2 ≈5%,
    Giganotosaurus 1 ≈3%) and additionally **room-capped** (≤2 / ≤1 rooms per seed). Weight 0
    excludes a species; a room whose every valid donor is weight-0 stays vanilla (never a silent
    uniform fallback).
  - **Fixed**: one pinned donor for every eligible room (e.g. an all-T-Rex run) — the bulk analogue
    of `--dc2-swap-enemies`, RNG-free, with every existing safety guard still applied (unsafe rooms
    stay vanilla).
- **Surface**: CLI `--dc2-enemy-mode weighted|fixed`, `--dc2-fixed-species <name|0xNN>`,
  `--dc2-weight <name|0xNN>=<0..15>` (repeatable); app UI mode selector + fixed-species dropdown +
  per-species weight sliders under the DC2 enemy options. The generate log now prints a per-seed
  **donor tally**.
- **Seed string**: a new optional 5-byte DC2 block (bytes 11–15) encodes mode/pin/weights — and
  finally the `Include setpiece/boss enemies` toggles, which previously **weren't seed-encoded at
  all** (a pasted seed silently dropped them). Default configs keep their historical byte-identical
  `DINO-…` strings; all legacy payload lengths still parse to their original runs.
- Note: with the boss toggle ON, boss donors are now rare-by-default instead of uniform-share —
  the intended behavior change this feature exists for.

### Withdrawn — DC2 player character model swap (whole-file mechanism)

- **The Regina ↔ Dylan whole-file swap below is disabled** (`--swap-player-characters` is a no-op, the
  UI checkbox is hidden): in-game testing showed rendering/animation work, but **firing a weapon
  crashes** — each `WEP_P` package carries per-weapon fire-effect data addressed by the slot's weapon
  id, so serving another weapon's package spawns a class-id-0 effect record whose next-frame dispatch
  calls a NULL handler (full decode:
  [docs/decisions/dc2/models/DC2-PLAYER-SWAP-FIRE-CRASH-RCA.md](docs/decisions/dc2/models/DC2-PLAYER-SWAP-FIRE-CRASH-RCA.md)). The
  feature returns as a **geometry graft** (keep the target package, graft the donor's character
  geometry + texture); the config/UI/CLI wiring is retained for that v2.

### Added — DC2 player character model swap (Regina ↔ Dylan)

- **New option "Swap player characters — Regina ↔ Dylan" (DC2, EXPERIMENTAL)** in the app (visible only
  for DC2) and CLI (`--game dc2 --swap-player-characters`). Whole-file swap of the six per-weapon player
  model packages per character (`WEP_P0xx.DAT` ↔ `WEP_P1xx.DAT`, paired by sorted weapon index): each
  model carries its own rig, animations, textures and sounds, so no repackaging is involved. Sources are
  always read pristine (installer backup → `.dinorand-bak` sibling → live), all 12 files are validated as
  DC2 Gian packages before any is emitted, and Install/Restore reverse it via the standard
  `.dinorand_backup` manifest. New `GameFeature.PlayerModel` (DC2 opts in; DC1's measured-feasible costume
  swap is a separate decision). Parity audit + design:
  [docs/decisions/dc2/models/DC2-PLAYER-SWAP-PARITY-PLAN.md](docs/decisions/dc2/models/DC2-PLAYER-SWAP-PARITY-PLAN.md); feasibility census:
  [docs/decisions/cross/HUMANOID-MODEL-SWAP-FEASIBILITY.md](docs/decisions/cross/HUMANOID-MODEL-SWAP-FEASIBILITY.md).

### Research — DC1 PSX-layer reverse engineering (no shipped code yet)

- **PSX-side RE route established (the original disc unblocks the recompiled layer).** The PC `english`
  build is **Dino Crisis USA v1.1** (`SLUS_009.22`; v1.0 + Japan ruled out by overlay size/byte divergence).
  The vanilla PSX main exe + overlays are extracted to `scratch/psx/dc1/`, giving clean MIPS for the engine
  code `DINO.exe` recompiled to x86. Decoded the **data-driven overlay loader**: each `ST*.DAT` opens with a
  2048-byte resource header of 16-byte records `{type, size, loadaddr@+8, flags}`, type-7 payloads
  LZSS-decompressed to their `loadaddr` — which is why no overlay base is a literal in the SLUS. See
  [docs/reference/dc1/psx/PSX-MIPS-ROUTE.md](docs/reference/dc1/psx/PSX-MIPS-ROUTE.md).
- **Management Office keypad — SOLVED + VERIFIED** (multi-agent workflow, 3/3 adversarial lenses). The
  4-digit code is **FIXED, a static data table** (`0x800ef828`) in the keypad overlay (`ST1.DAT` entry0 @
  `0x800e8000`) — **not randomized** (refutes the earlier `rand()` hypothesis), no save, zero writers.
  Compare = MIPS `0x800e8aa0`; success → `SetFlag(2,9)` → room SCD → durable `SetFlag(0,19)`. A puzzle-code
  randomizer is now feasible as a **DATA-file edit** (patch the table; no EXE patch). See
  [docs/reference/dc1/puzzle/MGMT-OFFICE-SAFE-PUZZLE-DECODE.md](docs/reference/dc1/puzzle/MGMT-OFFICE-SAFE-PUZZLE-DECODE.md) §12.
- Corrected a wrong intermediate verdict ("the compare is resident x86") and added the MIPS driver
  `tools/scd_re/mips.py`. Narrative: STATIC-SCD-RE.md cont.37 (wrong turn) → cont.38 (solved).

## [0.2.0] — 2026-06-27

### Added — Voice randomization (Phase 4 slice)

- **DC1 cutscene voice randomizer.** BioRand-style cast voice shuffle ported as a
  tested vertical slice: BioRand-layout datapack loader (`VoiceDataPack`), filename
  parser (`VoiceFileName`), seeded swap planner (`VoiceSwapPlanner` — supporting-cast
  shuffle + protagonist character-select + opt-in cross-game donors), the `WavAudio`
  codec, config flags, and WPF/CLI toggles. See
  [docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md](docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md).
- **Full cutscene-cast voice manifest** (`data/dc1/voice.json`). A human folder-curation
  pass labelled all 630 cast banks under `Sound\VOICE\` by speaker —
  regina (161), rick (206), gail (138), kirk (62), tom (15), colonel (5) — plus the
  non-cast machine voices computer (23) / computer-lab (20). The original 144
  fingerprint + by-ear labels are preserved. (Builds on the bank-centric fingerprint
  match and by-ear corrections in `data/dc1/voice-corrections.json`.)
- `VoiceActor` gains `Tom` and `Colonel`; both join the supporting-cast shuffle.

### Notes

- The byte-writing tail (the DC1 `Sound\VOICE\` slot map + ogg→WAV emission) stays
  **gated behind `VoiceManifestLayout.IsDecoded = false`**: the pass computes and logs
  its swap plan but commits nothing to game files until the cutscene-voice addressing
  is reverse-engineered. `computer` / `computer-lab` are recorded distinctly in the
  manifest but resolve to `VoiceActor.Unknown` in-engine (never swap targets).

## [0.1.0]

Initial feature set — everything prior to the voice randomizer.

### File-format library (Phase 0)

- Parse/write the per-room `stNXX.dat` stage files (145 rooms / 12 stages) with
  `Read→Write` byte-identical round-trips gated on the real install.
- LZSS de/compression (`Dc2LzssDec`) and the clean-room Gian-package parser
  (`GianPackage`); SCD object/script walker (`DcOpcodes`, `RoomScript`).
- Decoded SCD records, validated against the room corpus and recorded in
  [docs/reference/dc1/_registries/EXE-SYMBOLS.md](docs/reference/dc1/_registries/EXE-SYMBOLS.md): item placement (`0x28` subtype-4),
  enemy placement (`0x20`), door (`0x28` subtype-0), emergency boxes.
- Game data: `data/dc1/{items,enemies,rooms,room-data,emergency-boxes}.json`.

### Item randomizer (Phase 1)

- Shuffle-existing and replace-with-ratios modes; weighted pool with per-category
  ratio dials (ammo/health) and ammo-quantity scaling.
- Weapons + weapon parts pool-placed (reach-aware, minimum-weapon guarantee); ammo
  linked to granted weapon families; reachability-aware fill with a start-region floor.
- BioRand-parity zeroed-category semantics; set-piece room/item protection
  (`ItemPriority.Fixed`); multi-camera-cut pickup dedup.
- Weapon-upgrade chance, experimental pre-upgraded-weapon chance, per-family weapon
  enable toggles — all carried in the shareable `AppSeed` string.
- Emergency boxes: decoded contents (EXE table), reachable-plug-economy reporting, and
  experimental shuffle/reroll of box contents (reversible EXE patch).

### Enemy & loadout randomizer (Phase 2, partial)

- In-room (model, motion) permute within an AI category; species decode
  (skeleton ↔ category) with cutscene/scripted-room guards.
- Cross-room species placement (`SpeciesImporter`) — texture-aware import + pointer
  relocation; proven fully playable for the Therizinosaurus into stage-1 room 0102
  (spawns, animates, killable) via the resident-pool-floor clip-strip and the universal
  hit/death walker NULL-guard.
- Add-an-enemy injection (`ScriptInjector`, `--add-enemy`); cross-species swap pass
  integrated into the bulk pipeline (`--exotic-enemies`, off by default).
- Randomized / custom starting inventory (reversible EXE patches; experimental).

### Door-graph foundations (Phase 3, partial)

- `RoomGraph` built from decoded door records; `KeyItemPlacer` flood-fill +
  `ShuffleKeyItems`; composite AND requirement engine (`Requirement`).
- Logic-overlay auto-backfill from SCD flag dataflow (`tools/scd_re/extract_logic.py`).
- Door *destination* shuffle is decoded but deferred behind Phase 1.

### Front-ends & packaging

- Cross-platform `DinoRand.Cli` and Windows WPF `DinoRand.App` (→ `DinoRand.exe`).
- Non-destructive backup-and-swap installer (`GameInstaller`): pristine originals
  backed up with SHA256, `--restore` returns the install byte-identical.
- Self-contained single-file release builds (`scripts/publish-release.sh`).

[0.2.0]: https://github.com/anzaldoivan/dinorand/releases/tag/v0.2.0
[0.1.0]: https://github.com/anzaldoivan/dinorand/releases/tag/v0.1.0
