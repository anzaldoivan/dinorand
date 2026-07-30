# DinoRand orchestration scope

## Purpose

DinoRand turns a player-owned game installation and a seed into a reproducible
randomized playthrough or a narrowly scoped, reversible operation.
The project supports Dino Crisis 1 and Dino Crisis 2 from the DRM-free GOG
releases supplied by the player.
Steam and Enigma-protected builds are outside the project boundary.

## Product shape and maturity

Maturity labels are **shipped**, **pilot**, **blocked**, and **deferred**.

| Game or command surface | Maturity | Scope |
| --- | --- | --- |
| DC1 main CLI generation | shipped | Items, enemies, key logic, loadout, and audio passes. |
| DC1 targeted commands | shipped/pilot | Lab and modding operations; use the command's safety contract. |
| DC1 door graph and progression | deferred | Not a production randomizer surface. |
| DC2 main CLI generation | shipped | Supported enemy, shop, loadout, music, and voice surfaces. |
| DC2 standalone commands | shipped/pilot | Command-specific install, restore, and verification limits apply. |
| DC2 T1 filename-table audit | pilot | Read-only `ST*.DAT` audit; no game-file writes. |

The CLI and Avalonia GUI are frontends over the same format and randomizer
boundaries; a frontend's maturity does not expand a backend's scope.

## Output and installation contract

By default, generation writes a new `mod_dinorand/` output tree and leaves the
source install alone.
The seed plus applicable settings is intended to produce the same output and
spoiler identity across repeated runs.
Explicit install and targeted operations are user-scoped and protected by a
reversible backup/restore path before changing a source or target.
Backup/restore verification is a separate, available validation step; this is
not a claim that every operation verifies its backup.
Restore is the normal exit path for an approved mutation.
This is a scope and safety rule, not a categorical claim that originals can
never be modified.
No operation may silently broaden from an output write into an install write.

## Architecture boundary

`DinoRand.FileFormats` parses and writes game files; it does not choose seeds
or progression.
`DinoRand.Randomizer` owns seeded selection, progression, and passes; it does
not own frontend policy or file-format mechanics.
The CLI and Avalonia projects coordinate user intent, output, backup, and
reporting; they do not become format or selection authorities.

## Canonical sources

- [AGENTS.md](AGENTS.md) — repository safety and operating policy.
- [LEGAL.md](LEGAL.md) — supported editions, ownership, and legal posture.
- [USER-GUIDE.md](USER-GUIDE.md) — player-facing behavior and safety contract.
- `dinorand --help` — intended exhaustive CLI reference, pending parity
  reconciliation with this spec and the User Guide.

Local-only technical authorities:

- `docs/ARCHITECTURE.md`
- `docs/CONTRIBUTING-RE.md`
- `docs/reference/`
- `docs/_prompts/orchestration/`

These local-only authorities are available only in the primary/local checkout
or through a materialized task packet; they are not links into the tracked
public checkout.

## Non-goals

- Supporting Steam, Enigma-protected, or unowned game installations.
- Shipping game assets, executables, or copied third-party research material.
- Making an install or targeted operation without explicit user scope and a
  reversible backup/restore path.
- Treating frontend flags as proof that an experimental or blocked feature is
  production-ready.
- Replacing detailed registries, evidence records, or campaign ledgers with
  this short orientation document.

## Completion and acceptance

This scope is complete when a requested capability has an owning detailed
authority record, a documented maturity label, a deterministic check, and a
safe output or backup/restore path appropriate to its operation class.
The acceptance rule is evidence-backed behavior in the supported GOG build,
with no unsupported-edition or silent-mutation claim.
