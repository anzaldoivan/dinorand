# Dino Crisis 2 game data

JSON/Markdown tables mirroring `data/dc1/`. Now predominantly byte-confirmed (items,
doors, locks, placements, flag gating — see the table below); the guide-sourced and
placeholder layers that remain are called out per-file and in the placeholder section.
Every record is provenance-tagged.

> **Provenance legend** (used in every file):
> - `bytes` — decoded from the game files in `4249140_DinoCrisis2/` this session (game files
>   were read **only**, never modified).
> - `confirmed` — two or more independent external sources agree.
> - `single-source` — one external source (almost always the cached FAQ).
> - `inference` — a reasoned guess from byte facts, **not** proven.
> - `placeholder` — unknown; deliberately left null/empty rather than guessed.

## Files

| File | What it holds | Dominant provenance |
|---|---|---|
| `enemies.json` | 13 `E*.DAT` model slots w/ obj/tri/quad counts **+** 12-creature roster | model counts = **bytes**; creature mapping = placeholder/inference |
| `rooms.json` | 89 `ST*.DAT` room ids (stage/room) + per-room `zone` (byte-derived) | ids = **bytes**; **zone = bytes** (EXE 0x491c70, ZONE-MAPPING-RE.md); names = unresolved |
| `items.json` | the unified item catalog: ids 0x00–0x39 with English names (weapons, subs, health, key items 0x21–0x34, EPS/perks) | **bytes** (K75: PC catalog `0x704260` + US-SLUS glyph decode; supersedes the old guide placeholders) |
| `map.json` | 10 canonical zones (story order) + **84 named rooms (JP+EN)** + each zone's byte-derived `st_ids` | zone names = **bytes**+confirmed; `st_ids` = **bytes** (0x491c70); room names = guide (XGameMania) |
| `door-graph.json` | per-room door dest-word blob offsets (180 literal doors / 84 rooms, K81) + lock gates (K78) + flags/built_state/door_type + ST003 `conditional_dests` | **bytes** (static slot-5 decode; some live-validated) |
| `spawn-graph.json` | per-room enemy spawn (op 0x1a) editable literal operands (TYPE/posX/posY/posZ/SLOT) + blob offsets | **bytes** (static slot-5 decode, T10 `edit_spawn.py`; `verify` re-reads 1663 offsets, 0 mismatch) |
| `item-placements.json` | per-room item commits (op 0x31): id (editable lever) + blob offset, 168 items / 54 rooms (K81) | **bytes** (static slot-5 decode; 3 live-validated) |
| `door-connectivity.json` | room-level directed warp graph (outbound/inbound/degree, bidirectionality, dead-ends, hubs) from `door-graph.json` | **bytes** (derived; `gen_connectivity.py`) |
| `door-gating.json` | per-room/per-routine flag dataflow (op-0x1d tests / op-0x1c+0x33 sets), items/examine/key_use, resolved `locked_doors`, + the K30 `entry_gate` flag list with native+script setters (K81) | **bytes** (static slot-5 decode + .text census; `gen_gating.py`) |
| `door-guards.json` | conditional-commit + provenance census (K119–K123): every conditional door/item AOT commit with a SYMBOLICALLY-RESOLVED predicate (27 doors / 64 items, 0 unresolved — incl. the one held-key-item gate `10D->10F` requires Gas Mask `0x2e`, character & prev-room gates), subweapon key_use table (Stungun=Regina electric / Machete=Dylan vine), family-5 scene-property map, per-door provenance (134 on-entry / 17 guarded / 16 interaction / 12 event / 2 no-start-site) + the closed routine-invocation model | **bytes** (offline capstone decode + symbolic census; method in `_source`/`_semantics`) |
| `enemies-static.json` | static per-room enemy set (65 rooms / 441 spawns) from the spawn-graph + TYPE→species table | **bytes** (derived; `gen_enemies_static.py`) |
| `scd-vm-opcodes.json` | the slot-5 SCD-VM opcode + operand model (ALU table, stack-delta model, AOT builders) | **bytes** (offline disx, `AOT-SYSTEM-RE.md`) |
| `room-data.json` | **consolidated per-room DB** — joins spawns+doors+connectivity+items+runtime-enemies per room (89), + **byte-derived `zone` per room** + 10-zone catalog. Mirrors `data/dc1/room-data.json` | **bytes** (pure join; `gen_room_data.py`). **ST→zone RESOLVED** (EXE 0x491c70, ZONE-MAPPING-RE.md) |
| `placements.md` | per-area Dino File + item pickups, qualitative enemy roster | guide (single-source, **partial**) |
| `sources.md` | external URLs consulted + usability notes | — |

## What is BYTE-CONFIRMED (quote-backed)

### Room id space — `bytes`
The literal `ST*.DAT` filename table in `Dino2.exe` matches the on-disk file set **exactly**:

```
EXE ST*.DAT strings : 89
On-disk ST*.DAT      : 89
in EXE not on disk   : (none)
on disk not in EXE   : (none)
```

Stage groups: `ST0(6) ST1(17) ST2(6) ST3(7) ST4(12) ST5(5) ST6(9) ST7(8) ST8(12) ST9(7)`.

### Zone names — `bytes`
`Dino2.exe` (rebirth build) has an area-name string table @ VA `0x00733e90` listing the **10
canonical zones** (exact spelling): `JUNGLE`, `MILITARY FACILITY`, `RESEARCH FACILITY`,
`PATROL SHIP`, `3RD ENERGY FACILITY`, `3RD ENERGY REACTOR`, `AREAWAY TO CITY`, `EDWARD CITY`,
`HABITAT SUPPORT FACILITY`, `MISSILE SILO`. These are the zone names in `map.json` (so they're
`confirmed`, not guessed). **10 zones == 10 ST stage groups** (count match). But the table is
built as a stack array in code, not a stage-indexed data table, so it does **not** by itself
say which stage is which zone — see the placeholder section.
So the 89 room ids in `rooms.json` are real and complete. (Room *names* are not — see gap.)

### Enemy model catalog — `bytes`
Decoded with `tools/dc2_re/dcm_and_misc.py` (DCM header: `tri/quad` u16 @ `+0x10/+0x12`,
`obj_cnt` u32 @ `+0x14` — the DC2 analogue of DC1's bone-count-at-`+0x14` species
discriminator). All 13 slots:

```
slot  obj  tri  quad   blob     ram_base
E00   21   300   86   164012   0x00633000
E10   20   410   72   113344   0x00640000
E20   18   358   58   121192   0x00640000
E30   20   290  132   101280   0x00640000
E31   17   214  135   115260   0x00640000
E32   17   214  135   120700   0x00640000   <- identical counts to E31 (shared rig)
E40   20   570   69   114276   0x0063f500
E50   19   370   19   126748   0x00640000
E60   20   408   80   154008   0x00638000
E70   20   434   62    60584   0x00650000
E80   19   346  154   127696   0x00640000
E90   17   234   93   138716   0x0063d000
EA0    7    32   37    23584   0x00640000   <- clear outlier: tiny rig (the small swarmer)
```

13 model files vs **11 distinct creatures** reconciles cleanly once the Dino Crisis Wiki
confirms the raptor is one creature in colour variants: the **Velociraptor occupies the
triple-file group E30/E31/E32** (with E31 ≡ E32, identical `17/214/135` = palette variants on
a shared rig), **Compsognathus = EA0** (the only uniquely-tiny 7-obj/23 KB rig), and the
remaining **9 files map 1:1 to the 9 other creatures**. See "model slot → creature" below for
what is locked vs unresolved.

## What is GUIDE-SOURCED

- **Creature roster (15).** `confirmed` (FAQ + Wikipedia + Dino Crisis Wiki): Velociraptor,
  T-Rex, Allosaurus, Oviraptor, Compsognathus, Mosasaurus, Plesiosaurus, Inostrancevia,
  Triceratops, Giganotosaurus, Pteranodon. `single-source` (Dino Crisis Wiki only): the four
  raptor colour variants — Brown / Blue / Green / Red Raptor (Red ≈ the FAQ's "Super Raptor").
  The wiki list was pulled via its MediaWiki `api.php` (the rendered `/wiki/` page 403s
  WebFetch; the API endpoint does not — see `sources.md`).
- **Weapons / key items / Dino Files / currency** — `single-source` (cached FAQ).
- **Area progression** — 72 named areas across 10 zones, ordered by walkthrough appearance;
  zone chain cross-confirmed by Wikipedia.

## What is PLACEHOLDER (NOT guessed)

1. **`ST`-id → room/area name (still unresolved).** No source maps an `ST*.DAT` id to a name.
   Per-zone room NAMES now exist in `map.json` (84 rooms, JP+EN, from XGameMania), but they are
   **not** tied to ST ids. Why it's hard: the EXE has only **zone-level** names (10 strings,
   English) and references ST strings individually; and the on-screen Japanese room name (e.g.
   基地・エントランス) is **baked into the `.DBS` background graphics**, not stored as text in any
   EXE/DAT (verified: zero Shift-JIS hits for area kanji across all exes + 2071 DAT files). The
   earlier story-order stage→zone hypothesis was **retracted** — room counts contradict it
   (ST1=17 matches Jungle 16, not its story-order slot), and counts don't yield a clean
   bijection (`rooms.json` → `_stage_zone_structure`). Resolve by reading the on-screen AREA
   name per loaded ST file (emulator), or decoding a zone/BGM id in the room blob — one
   confirmed ST→room anchor per zone would let the XGameMania lists align to ST ids.
2. **Model slot → creature (partially firmed).** Two groupings are now **locked** by the
   count balance + wiki facts: **EA0 = Compsognathus** (only uniquely-tiny rig) and
   **E30/E31/E32 = Velociraptor** (the only multi-variant enemy maps to the only triple-file
   group). The remaining **9 files** (E00/E10/E20/E40/E50/E60/E70/E80/E90) map 1:1 to the 9
   other creatures, but the **within-group assignment is unresolved**: `obj_cnt` is
   near-constant (17–21) across the non-Compy roster, so it does **not** discriminate a flyer
   from a quadruped from a theropod (this weakens the DC2-FEASIBILITY "obj_cnt = species
   discriminator" claim for DC2). **The EXE enemy-id table was checked and does NOT resolve
   it:** `Dino2.exe` has a filename pointer table (id↔E-file, order E00…EA0, at VA `0x71b3xx`)
   but **zero creature-name strings**, so it maps id→*filename*, never id→*creature* (see
   [`docs/reference/dc2/enemies/EXE-ENEMY-TABLE.md`](../../docs/reference/dc2/enemies/EXE-ENEMY-TABLE.md) and `enemies.json` →
   `mapping.exe_id_table`). Resolving the 9 still needs the in-room spawn decode or a runtime
   RAM dump. See `enemies.json` → `mapping`.
3. ~~**All numeric item ids.**~~ **RESOLVED (K75):** the full 58-entry catalog (`0x704260`)
   with English names is in `items.json` — ids 0x00–0x39 incl. key items 0x21–0x34.
4. ~~**Per-room door connections & exact spawn/item placements.**~~ **RESOLVED (K78/K81/K99):**
   decoded statically from the slot-5 SCD blob — see `door-graph.json`, `spawn-graph.json`,
   `item-placements.json`, `door-gating.json`, `door-guards.json`. Still open on the item
   axis: the per-pickup granted **consumable** catalog id is runtime-seeded (K99/K101/K102),
   so `item-placements.json` deliberately carries no `catalogId`; key-item identities are in
   `items.json.keyItemGrantSites`.

## How this differs from `data/dc1/`

DC1's dataset is byte-validated end-to-end (decoded SCD records, real item ids, a reachable
door graph). DC2 has since caught up on the logic layer (items K75, doors/locks K78/K81,
placements K99, flag gating K107, conditional commits K119): the remaining honest gaps are
the ST-id→room-name mapping, the 9-way enemy model assignment, and the runtime-seeded
consumable pickup ids above. Sections 1–2 of the placeholder list are still live; treat
those as scaffolding, the rest as byte-backed ground truth.
