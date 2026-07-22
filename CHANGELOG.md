# Changelog

All notable changes to DinoRand are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project aims to
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html) (pre-1.0: minor
versions may include breaking changes).

The version number is set by `<VersionPrefix>` in [`Directory.Build.props`](Directory.Build.props).

<!-- Audience: players and release readers. Keep this section user-facing; technical evidence belongs in the
     contributor/research records, not in the release notes. -->

## [Unreleased]

## [0.6.0] — 2026-07-22

### Added

- **Randomized DC2 weapons (experimental, off by default).** The six non-starter main weapons can
  be shuffled between Regina and Dylan while each character keeps a usable starter and required
  door tool. Enable it in Advanced options or with `--dc2-randomize-weapons`; it is incompatible
  with Shared Weapons, and randomized weapons keep their original inventory icon.

- **Shared DC2 weapons (experimental, off by default).** Regina and Dylan can use and buy the same
  weapons. Enable it in Advanced options or with `--dc2-shared-weapons` (or the sub-weapons-only
  `--dc2-shared-weapons-subs-only` option); use `--restore` to undo the change. The four largest
  weapons still need broader in-game verification, and shared weapons keep their original icons.

- **Archipelago runtime client for DC1.** Connect DinoRand to an existing Archipelago game with
  `--ap-connect <host[:port]> --ap-slot <name> --install <dir>`: pickups become checks, items from
  other players arrive in-game, and reconnecting keeps your progress. The client runs on Windows;
  DC2 remains generation-only.

- **Door Skip for DC1 (experimental, off by default).** The door-opening animation can be skipped
  with the GUI option or `--dc1-door-skip` while the destination room still loads normally. It is
  reversible with `--restore` and does not change the seed.

- **Fast-forward cutscenes for DC1 (experimental, crash risk, off by default).**
  `--dc1-fast-forward-cutscenes` shortens waiting between story moments while keeping dialogue,
  voices, item grants, and story progress intact. It is reversible with `--restore`; broad in-game
  verification is still pending.

- **Ready-made Archipelago world downloads.** Releases now include `.apworld` files that can be
  dropped directly into Archipelago's `custom_worlds/` folder. DC1 supports generation and the
  runtime client; DC2 supports generation only.

### Fixed

- **Restoring DC2 shared weapons now returns the game to its original state.** Repeated apply and
  restore cycles no longer risk changing weapons that were shared in the retail game.

- **Archipelago release packages are more reliable.** The downloadable worlds now work from their
  packaged files and remain compatible with the supported Archipelago release, while generation
  keeps the same reachability rules.

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

[0.6.0]: https://github.com/anzaldoivan/dinorand/releases/tag/v0.6.0
[0.5.0]: https://github.com/anzaldoivan/dinorand/releases/tag/v0.5.0
[0.4.2]: https://github.com/anzaldoivan/dinorand/releases/tag/v0.4.2
[0.4.1]: https://github.com/anzaldoivan/dinorand/releases/tag/v0.4.1
[0.4.0]: https://github.com/anzaldoivan/dinorand/releases/tag/v0.4.0
[0.2.0]: https://github.com/anzaldoivan/dinorand/releases/tag/v0.2.0
[0.1.0]: https://github.com/anzaldoivan/dinorand/releases/tag/v0.1.0
