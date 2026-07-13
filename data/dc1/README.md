# Dino Crisis 1 game data

JSON tables that drive randomization (item pool, enemy classes, room/door metadata).
These are the **data** half of the data-over-code design (DESIGN.md §6): adding or
correcting content here should not require touching engine code.

> Item/key/room ids are the **real** game values (Phase 0, from the residentevil123
> forum tables cross-checked against the files). Enemy ids are still provisional. Today
> `DinoCrisis1.cs` mirrors the engine-relevant subset in code; loading directly from
> these JSON files is a planned cleanup.

## Source-of-truth ownership (room data)

Owner-per-concern, **not** one merged file. Exactly one file is loaded by the engine; the rest
are upstream research or content tables. `RoomDataConsistencyTests` locks them together.

| File | Role | Loaded by engine? |
|---|---|---|
| `map.json` | **Code-loaded overlay.** Owns door-rando metadata (`beginEnd`/`category`/`floor`/`staticTargets`) and — going forward — the hand-authored progression logic (`requires` / `requiresRoom`, see `GRAPH-LOGIC-PARITY-PLAN.md` §4.1). The one room file the engine reads. | **Yes** (`Maps/DoorMap.cs`, embedded resource) |
| `room-data.json` | **Upstream research DB.** Owns wiki content + decoded `spawn_control` per room. `map.json`'s `name`/`floor` are derived from it; a test enforces they stay in sync. | No |
| ~~`rooms.json`~~ | **Retired.** Room enumeration is filesystem-based (`DinoCrisis1.EnumerateRooms`); story-room names live in `map.json`/`room-data.json`; its only unique data was the out-of-scope demo stages (7/8/9). | — |
| ~~`wiki-aliases.json`~~ | **Retired.** Its 6 entries were identical to `room-data.json.wiki_title`. | — |

## Files

- `map.json` — see the ownership table above and its own `_derivation` block.
- `room-data.json` — see the ownership table above.
- `map-requirements.md` — provenance ledger for the progression gates in `map.json`
  (`requires` / `requiresRoom`), **GENERATED** by `tools/scd_re/extract_logic.py --apply` (lock
  axis: type-1/3 doors + the op58-subtype-3 DDK disc-pair gates, STATIC-SCD-RE cont.61 §C); do
  not hand-edit. Each gate records the decode it came from, so the logic is auditable.
- `placement-gates.md` — provenance ledger for the **placement-axis** gates (type-0 doors whose
  record is script-flag-gated, e.g. `010D→010A`, STATIC-SCD-RE cont.61 §A), audited by
  `tools/scd_re/door_catalog.py --audit`.
- `items.json` — full item table (116 entries) with categories, the key-item set
  (ids `0x2B–0x6F`), and the non-key shuffle pool (ammo + health) with weights.
- `enemies.json` — enemy ids grouped by placement class (indoor/outdoor/boss/scripted).
  **Still placeholder** — the forum tables don't enumerate enemy ids.
- `placements.md` — per-room item/enemy **placement oracle** distilled from the GameFAQs
  walkthrough and mapped to the codes above (room `SSRR`, item ids, enemy classes), plus the
  key-item progression chain for `Logic/KeyItemPlacer`. Used to **validate** decoded SCD `0x4C`
  records (their `+0x1A` item id should match this map). Human-playthrough ground truth — see the
  status/caveats note at the top of that file.
- `cutscene-rooms.json` — **GENERATED** by `tools/scd_re/cutscene_catalog.py --apply` (needs the
  game install; do NOT hand-edit `flagged`). Choreography-involvement census: rooms whose enemy
  records are op-0x22-bound + op-0x3a/0x5a-scripted (STATIC-SCD-RE cont.49/59, cont.58 policy),
  plus the hand-curated `scripted_enemy`/`cutscene` exclusion tiers the enemy pass ships
  (moved out of C#). Embedded resource consumed by `DinoCrisis1` / the DC1 enemy pass.
