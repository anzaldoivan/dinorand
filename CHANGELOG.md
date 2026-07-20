# Changelog

All notable changes to DinoRand are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project aims to
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html) (pre-1.0: minor
versions may include breaking changes).

The version number is set by `<VersionPrefix>` in [`Directory.Build.props`](Directory.Build.props).

## [Unreleased]

### Added

- **DC2 “Enable Randomized Weapons” (EXPERIMENTAL, default OFF).** Seed-scrambles the six
  non-starter MAIN weapons into exact three-weapon Regina/Dylan sets while pinning each character's
  starter and required door tool. Uses the existing cross-character grafts, validates every owned
  MAIN against a non-NULL character slot, installs after starting-loadout edits, and restores to the
  verified native layout. Seed byte 16 bit 7; GUI Advanced option and CLI
  `--dc2-randomize-weapons`; incompatible with Shared Weapons. Shop prices/recovery still shuffle,
  but catalog-mask shuffling is suppressed for this option. Known cosmetic limitation: randomized
  weapons retain the original owner's inventory icon. Not verified in-game.

- **DC2 "Enable Character Shared Weapons" (EXPERIMENTAL, default OFF).** Makes weapons **shared**
  between Regina and Dylan rather than one set each — usable *and* shop-purchasable by both — the way
  the seven natively dual-owned catalog ids already work in the retail game. Sub-weapons (Machete,
  Large Stungun) are a two-bit `Dino2.exe` item-catalog edit (`0x704260[id]+0xA`). Main weapons
  additionally need their geometry graft — an owner bit on a MAIN whose `0x71B230` slot is NULL runs
  the loader on a stale blob and crashes — so the main half **delegates to the in-game-proven graft
  installer** rather than writing the same bits twice; enabling this implies "Enable Cross Character
  Weapons". GUI: Advanced options; CLI: `--dc2-shared-weapons`, or
  `--dc2-shared-weapons-subs-only` for the graft-free subset (`--restore` to revert). Not
  seed-encoded. Known limitations: a shared weapon keeps its original owner's inventory icon on both
  characters (the icon UV is global per weapon id, with no per-character indirection), and the four
  large weapons (Missile Pod, Rocket Launcher, Solid Cannon, Antitank Rifle) are built but not yet
  fully witnessed in-game.

- **Archipelago runtime client, DC1 (multiworld play).** New long-running CLI command
  `--ap-connect <host[:port]> --ap-slot <name> [--ap-password] --install <dir>`: connects to an
  Archipelago server, patches AP's item placement into the local GOG install (same
  backup-and-swap as a normal seed; other worlds' items appear as a marker pickup), then polls
  the running `DINO.exe` from outside the process — pickups become multiworld checks, items other
  players find are granted in-game, and reaching the Underground Heliport reports the goal.
  Windows host only for the play half; reconnect-safe (the server stays authoritative). The DC1
  apworld contract moves to v2 (slot_data `placements`/`item_ids`; 12 shared-flag locations are
  excluded from progression). Decision record `AP-CLIENT-PLAN.md`; taken-flag decode
  `EXE-SYMBOLS` cont.81.

- **Door Skip (Experimental) — DC1.** New default-off option (GUI checkbox + `--dc1-door-skip`) that
  patches `DINO.exe` so room-to-room door transitions are near-instant: the door-opening animation is
  removed while the destination room's background still loads correctly. Two small reversible `.text`
  windows, applied at install from a pristine backup and undone by `--restore`; not part of the seed
  string, and leaves the shared animation engine untouched (no effect on enemy/cutscene timing).
  Decode + live confirmation: `STATIC-SCD-RE` / `EXE-SYMBOLS` cont.78.

- **Fast Forward Cutscenes (Experimental/Crash Risk) — DC1.** New default-off option (GUI checkbox +
  `--dc1-fast-forward-cutscenes`) that patches `DINO.exe` so cutscenes play faster: dead-air pauses
  are compressed by a guarded script-VM tick multiplier while every story flag, item grant and
  dialogue line is preserved and message/voice pacing is kept intact. One reversible `.text` hook +
  code cave, applied at install from a pristine backup and undone by `--restore`; not part of the seed
  string. Labelled "Crash Risk" pending broad in-game witness. Decode: `STATIC-SCD-RE` / `EXE-SYMBOLS`
  cont.79; the aggressive auto-skip variant is a documented crash dead-end (cont.80 RCA,
  `CUTSCENE-DRAIN-NULL-SKELETON-RCA`).

- **Ready-made `.apworld` downloads on every release.** Releases now attach
  `dino_crisis_1.apworld` and `dino_crisis_2.apworld` alongside the per-RID binaries — drop the
  file straight into your Archipelago install's `custom_worlds/` instead of hand-zipping the
  source folder. Dino Crisis 1 is the full world (generation **and** multiworld play via the
  runtime client); Dino Crisis 2 is **generation-only** — it produces a completable seed but has
  no key-item logic or in-game client yet.

### Fixed

- **DC2 owner-bit revert is now vanilla-relative (latent-corruption fix).**
  `Dc2CrossCharWeaponPatch.Restore` cleared a weapon's owner bit with a blanket `flags & ~bit`. That
  was correct only because no *natively dual-owned* id was in its pair list; RE work (K125) then
  established that **seven** catalog ids ship dual-owned (`0x00 0x05 0x10 0x13 0x14 0x16 0x19`), not
  the three previously assumed — and `0x16` Chain Mine is a shop subweapon, a plausible future
  addition. Adding any of them would have made Apply→Restore silently non-byte-identical *and*
  stripped a bit the retail build set. Revert now restores each bit to its PSX-verified vanilla
  state via the new `Dc2OwnerBits` helper, and fails closed on ids with no recorded baseline. No
  behaviour change for the currently shipped weapon set.

- **Archipelago DC1 apworld: distributable-package fixes** found by the first live end-to-end
  run (local AP 0.6.7 server, 2-slot multiworld — full connectivity matrix recorded in
  `AP-CLIENT-PLAN.md` Deviations, all rows passing): the logic contract now loads from inside a
  zipped `.apworld` (pkgutil, not filesystem paths), `archipelago.json` carries the
  `compatible_version` the AP 0.7 loader will require, and `requiresRooms` edge rules register
  their indirect region conditions so AP's region cache can't serve stale reachability during
  fill. CI's AP integration job re-pinned from a nonexistent `0.6.8` tag to the real `0.6.7`
  release; the AP protocol dependency is now lockfile-pinned and restored in locked mode.

## [0.5.2] - 2026-07-18

### Added

- **Title-screen seed watermark (both games, always on).**
- **Added Puzzle Randomizer (DC2).**

### Fixed

- **Adjusted Key Randomizer Logic in order to avoid Softlocks (DC1).**
- **Keep important items visible during swaps (DC1, on by default).**
- **Pickup ground-visual data layer (DC1).**
- **Normalize relocated pickup visuals (DC1).**
- **Imported textures can no longer overwrite the game's icon/font graphics (DC1).** Pickup and
  enemy texture imports now only use the video-memory space the game's own rooms use, and skip the
  import (showing the generic pickup instead) when a room has no free space — previously a full room
  could garble the inventory icons and on-screen text for the rest of the session.
- **Crashes related to the Enemy Randomizer have been fixed (DC2).**

## [0.5.1] — 2026-07-15

### Fixed

- **DC1 key-item placement fix for the Backyard of the Facility Area.**
- **DC1 Chief Room Softlock has been fixed**

## [0.5.0] — 2026-07-15

### Added

- **Scatter key items into ammo/health pickups (DC1)**

- **Shuffle Key Items now also relocates the DDK Input/Code disc pairs (DC1).**

### Changed

- **DC1 key-item placement is now beatable when shuffled.**

- **DC1 key-item logic is tighter and more faithful.**

### Fixed

- **DC1 item generator now protects the right pickups in "letter" rooms.**

## [0.4.2] — 2026-07-12

### Added

- **Cutscene-safe enemy randomization for Dino Crisis 1** (`--dc1-cutscene-safe`, off by
  default). Some rooms stage their enemies as part of a scripted moment — an ambush, an intro.
  Turn this on and those 37 rooms keep their original enemies (so the choreography still plays)
  but get a randomized color tint so they still look fresh. Leave it off and your existing seeds
  are unchanged.

- **Music randomization from your own track packs.** Both games can now pull background music
  from an external pack folder, matched by mood:
  - **DC1** (`--bgm-import --bgm-packs <dir>`): swaps in music from a BioRand-style pack. A GUI
    toggle and folder picker are included. Reversible with `--restore`.
  - **DC2**: export every in-game track to `.mp3` (`--dc2-export-bgm`) or import your own
    (`--dc2-import-bgm`). `.mp3` files are used as-is; `.ogg`/`.wav` are converted (needs ffmpeg).
    The install is never modified in place — output goes to `--out`.
  - DC2's built-in shuffle (`--dc2-shuffle-bgm`) now keeps tracks in the same mood family.

  (Mood tags ship uncurated in v1 — every track is in the shared pool until a by-ear pass tunes them.)

### Fixed

- **Fixed a crash when the randomizer placed an Inostrancevia.** Dropping this enemy into a
  room that wasn't set up for its emergence animation would crash the game (seen in Edward City,
  ST902). The fix guards the shared spawn system, so it also protects against the same class of
  crash from any other injected enemy — not just this one — and is fully reversible with `--restore`.
- **Room edits no longer silently stack.** Applying a second edit to the same room now rebuilds
  from a clean backup and warns you it replaces the previous edit, instead of quietly dropping it.
- **Clearer `--add-enemy-at` guidance.** The tool now reports whether an added enemy will act on
  its own or needs `--activate`, per enemy type, replacing an outdated blanket warning.
- **Safer enemy-injection placement.** Fixed a case where injected script could land in unreachable
  code; new enemies are now placed at a verified-good spot.

## [0.4.1] — 2026-07-11

### Added

- **DC1 puzzle codes now stay in sync on Classic REbirth English installs.** When the keypad/safe
  codes are randomized, the on-screen documents that tell you the code are updated to match on
  REbirth English installs too (previously only GOG European). Japanese REbirth installs are skipped
  with a warning (codes stay stock); anything unrecognized is refused rather than left mismatched.
  Reversible with `--restore`.

### Fixed

- **DC2 turret and escort set-pieces are no longer remixed by the enemy randomizer.** The opening
  turret room, the two escort rooms, and the Allosaurus room are scripted around their specific
  enemies — shuffling them broke the sequence (including a turret-room crash). They're now always left
  as-is.
- **DC2 character skin swap no longer breaks the weapon-select menu.** The swap now takes only the
  donor's portrait/name/team plate and keeps the target's own weapon icons, instead of drawing the
  wrong icons for your weapons.
- **DC2 weapon randomizer no longer mis-draws or mis-sorts weapons.** Fixed the "two weapons crammed
  in one slot" overdraw and the ownership/class mix-ups a shuffled catalog could cause. Starting
  weapons now only draw from safe choices and fall back to a default if a shuffle leaves none.
- **DC1 puzzle-code scramble refuses European GOG executables it can't safely edit** instead of
  writing to the wrong place, and re-running with a new seed now always re-derives from a clean backup
  (edits never compound).

## [0.4.0] — 2026-07-09

### Added

- **DC2 starting main-weapon randomizer** (`--dc2-randomize-start-weapon` / `--dc2-start-weapon`;
  UI checkbox + per-character pick). Restricted to each character's owned mains; appends the pick so
  the sub-weapon default is never lost. Rebirth build only, reversible. New-game only.
- **DC2 shop shuffle** (`--dc2-shuffle-shop`): permutes retail prices and stock-unlock shop levels;
  every item stays purchasable. Reversible.
- **DC2 BGM shuffle** (`--dc2-shuffle-bgm`): permutes the music-file table within like-classes,
  seed-deterministic and reversible. Live-witnessed in-game.
- **DC2 raptor tier randomization** (`--dc2-raptor-tiers`) with per-variant weights, two colour
  modes (`--dc2-raptor-colour room|mixed`), and a blue-raptor combo trigger (`--dc2-blue-combo`).
- **DC2 configurable enemy distribution** — `weighted` (default, per-species weights, bosses rare +
  room-capped) or `fixed` (one pinned donor). Boss/set-piece toggles are now seed-encoded.
- **DC2 character skin swap now also carries the portrait, team name, menu name, and voice/SFX** along
  with the model. Voice confirmed in-game.
- **Per-seed spoiler log** (`SPOILER.md`) written beside each seed: shareable seed string + config at
  the top (spoiler-free), room-by-room changes below. Doesn't affect the generated seed; disable with
  `--no-spoiler`.

### Changed

- **DC2 enemy randomizer now also skips flyer-native rooms** (in addition to aquatic and non-land
  rooms), so swaps can't crash them.

### Fixed

- **DC2 could occasionally produce an unbootable seed** — the installer now writes only the files a run
  actually touched, clears stale leftovers, and refuses mismatched files (fixes a new-game crash).
- **DC2 starting-weapon pick is restricted to a character's own main weapons**, closing a menu crash.

### Removed

- **Old Windows-only (WPF) GUI retired** — the cross-platform GUI is now the only one; release builds
  no longer produce the old `DinoRand.exe`.

### Withdrawn / Research

- **DC2 player character swap (Regina ↔ Dylan)** shipped then pulled — it crashed on firing because a
  whole-character swap brings the wrong per-weapon data. It'll return as a model-only graft; the config
  and UI are kept.
- **DC1 keypad research** — the Management Office 4-digit code was found to be a fixed value in the
  game data (so it's randomizable as a data edit), not generated randomly at runtime. No player-facing
  change yet.

## [0.2.0] — 2026-06-27

### Added — Voice randomization (early slice)

- **DC1 cutscene voice randomizer** (BioRand-style): shuffles the supporting cast, lets you pick the
  protagonist's voice, and optionally pulls voices from the other game. CLI and GUI toggles included.
- **Full cutscene voice cast list** — all 630 voice clips were labelled by speaker (Regina, Rick, Gail,
  Kirk, Tom, Colonel, plus the computer voices), so the shuffle knows who's who.

### Notes

- This release plans and logs the voice shuffle but **doesn't yet write it to your game files** —
  the final step was still being worked out. (Fully shipped in a later release.)

## [0.1.0]

Initial feature set — everything prior to the voice randomizer.

### Item randomizer

- Shuffle existing pickups, or replace them by category with tunable ammo/health ratios and ammo
  amounts.
- Weapons and parts placed so the run stays completable (you're never left without a usable weapon),
  with ammo matched to the weapons you're given.
- Weapon-upgrade chance and per-weapon enable toggles, all captured in the shareable seed string.
- Emergency-box contents can be shuffled/rerolled (experimental, reversible).

### Enemy & loadout randomizer (partial)

- Swap enemy appearances within the same behavior class, with cutscene/scripted rooms protected.
- Place enemies from other rooms as new species (proven fully playable — e.g. Therizinosaurus into a
  stage-1 room, spawns/animates/killable), add extra enemies (`--add-enemy`), and an off-by-default
  cross-species swap (`--exotic-enemies`).
- Randomized / custom starting inventory (experimental, reversible).

### Door & progression foundations (partial)

- Room map built from the game's own door data, with key-item placement kept logically solvable and an
  optional key-item shuffle. Door-destination shuffle was decoded but held for a later release.

### Front-ends & packaging

- Cross-platform command-line tool and a Windows GUI.
- Non-destructive install: originals are backed up and `--restore` returns your game to exactly how it
  was.
- Self-contained single-file release builds.

[0.5.0]: https://github.com/anzaldoivan/dinorand/releases/tag/v0.5.0
[0.4.2]: https://github.com/anzaldoivan/dinorand/releases/tag/v0.4.2
[0.4.1]: https://github.com/anzaldoivan/dinorand/releases/tag/v0.4.1
[0.4.0]: https://github.com/anzaldoivan/dinorand/releases/tag/v0.4.0
[0.2.0]: https://github.com/anzaldoivan/dinorand/releases/tag/v0.2.0
[0.1.0]: https://github.com/anzaldoivan/dinorand/releases/tag/v0.1.0
