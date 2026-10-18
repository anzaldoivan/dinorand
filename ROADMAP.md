# DinoRand delivery roadmap

This is the short orchestration index, not the project history or a technical
reverse-engineering record.

## Detailed authorities

- `docs/reference/RE-PROGRESS.md`
- `docs/reference/ROADMAP-HISTORY.md`
- `docs/reference/dc2/ROADMAP.md`
- `docs/ARCHITECTURE.md`
- `docs/CONTRIBUTING-RE.md`

These local-only authorities are available only in the primary/local checkout
or through a materialized task packet. They outrank this file. This file only
sequences work, names its gate, and points an owner to the authoritative
record.

## Delivery principles

- Prefer the smallest reversible change that answers the current question.
- Keep read-only audits separate from install mutations and runtime work.
- Use the supported GOG builds and the player's own files only.
- Require explicit user scope for every install, targeted, or live operation.
- Preserve a backup and a restore path before an approved mutation.
- Treat static evidence as a lead until the owning record validates it.
- Keep FileFormats, Randomizer, and frontend responsibilities separate.
- Record maturity as shipped, pilot, blocked, or deferred.
- Do not turn a pilot into a default by implication.
- Close a gate with a reproducible check and an authority path.

## Work sequence

| Work item | Status | Prerequisite / exit authority |
| --- | --- | --- |
| active orchestration rollout | Active | Prerequisite: `docs/reference/RE-PROGRESS.md`. Exit: `docs/_prompts/orchestration/README.md`. |
| active DC2 T1 read-only ST*.DAT filename-table audit pilot | Active | Prerequisite: `docs/reference/dc2/ROADMAP.md`. Exit: `docs/reference/dc2/ROADMAP.md` accepts the read-only result. |
| next DC2 items (scope/policy owner decision) | Next | Prerequisite: owner decision in `docs/reference/dc2/ROADMAP.md`. Exit: selected scope and policy are recorded there. |
| blocked DC2 doors runtime/human gate | Blocked | Prerequisite: runtime evidence and human approval. Exit: `docs/reference/dc2/ROADMAP.md` records both gates closed. |
| blocked DC1 NPC/native-loader and exotic-species live gates | Blocked | Prerequisite: live gate and safety review. Exit: approved witness is recorded in `docs/reference/RE-PROGRESS.md`. |
| pilot DC1 emergency-box plug costs and box ids (opt-in) | Pilot | Prerequisite: in-game witness of a moved, repriced and recoloured box and of ST105's shuttle. Exit: `docs/reference/dc1/items/EMERGENCY-BOX-DATA.md` records it. |
| deferred DC1 door/progression work | Deferred | Prerequisite: `docs/reference/ROADMAP-HISTORY.md` and a bounded design. Exit: tested production surface is accepted there. |
| DC2 generic TYPE 0x10/audio/voice | Deferred | Prerequisite: `docs/reference/dc2/ROADMAP.md` resolves scope and validation. Exit: supported command contract is accepted there. |

## Operating rule

The active rollout may coordinate only work whose prerequisite is satisfied.
A blocked or deferred item stays out of defaults, release claims, and player
instructions until its detailed authority changes its status.
An owner should update the detailed record first, then refresh this index.
No root roadmap entry duplicates byte-level evidence, historical rationale, or
the campaign's task ledger.

## Status transitions

Active work has a named owner and a bounded exit check.
Pilot work remains read-only or opt-in until its exit authority says otherwise.
Blocked work needs its stated runtime, human, or safety gate.
Deferred work is not a promise of the next release.
Scope changes belong in the detailed authority before this index changes.
Acceptance evidence must cover the supported GOG build.
Install-facing work must retain explicit scope and restore coverage.
The index is refreshed after, not instead of, that authority update.

The [root specification](SPEC.md) defines the product boundary and acceptance
rule; the [initiative index](TASKS.md) is the compact handoff view.
