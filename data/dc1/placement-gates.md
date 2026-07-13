# DC1 placement-axis door-gate provenance ledger

**Status:** validated (conservative filter, reconciled with the §8c/§8d live capture). Companion to
`map-requirements.md` (which owns the *lock* axis — type-1/3 doors gated by `GetFlag(9,·)`, auto-generated
by `tools/scd_re/extract_logic.py`). This ledger owns the *placement* axis — type-0 doors whose activation
is a `test → 0x0e` script flag-gate (`tools/scd_re/door_catalog.py`, STATIC-SCD-RE cont.42/45,
GRAPH-LOGIC-PARITY §8a–§8e).

The raw placement census is 120 flag-gated doors, dominated by a broad scenario-phase flag (`2:4` set in
`030B`) the dominance heuristic mis-attributes to ~20 early-map doors (both directions of `0102↔0110`,
`0110↔010A`, `0101↔0111`, …). Promoting those as gates would inject false constraints, so a **conservative
filter** keeps only trustworthy per-door **monotonic** gates:

> a `(src → dest)` edge is a trusted placement gate iff **every** placement of that dest is a
> `skip-if-false` `test_branch` (door placed only when the flag is SET) with an **external** producer
> room, **none** unconditional, **none** `skip-if-true` (open-by-default variant), a **single** gate
> flag, that flag gates **≤ 3** destinations (fan-out cap — `2:4` fans out to ~13), **and** that flag is
> **not a §8c toggled register** (`2:4/2:5/2:6` elevator, `3:33/34/57` runtime — these are set by native
> elevator code the SCD census can't see, so a monotonic `requiresRoom` would wrongly gate them; §8c/§8d
> deferred them, "author nothing yet").

Run: `PYTHONPATH=tools/scd_re python3 tools/scd_re/door_catalog.py --audit` (needs the local install).
It cross-checks the trusted set against the authored `map.json` door overlay and **asserts every trusted
placement gate is authored** — so the binary can never imply a monotonic progression gate the overlay is
missing.

## Trusted monotonic placement gates (all authored in `map.json` `doors.<dest>.requiresRoom`)

| Door room | Dest | Gate flag | Producer room | Authored | Notes |
|---|---|---|---|---|---|
| 0400 | 0401 | 0:74 | 0205 | ✅ | Front Area → Heliport passage (§8d) |
| 050C | 050D | 0:197 | 050B | ✅ | Power Freq. Room → Personal Lab passage (§8d) |
| 010D | 010A | 0:45 (armed by 0:57) | 0114 | ✅ | Backyard → Office Hallway. NOT found by the conservative filter: the deciding `0x0e` branch (st10d `@0x3ea28`) **targets** the door record `@0x3eb00` instead of skipping it, so the mechanical `skip-if-true` sense label reads inverted (door placed only when `0:45` is SET). CFG single-predecessor proof + in-cutscene re-placement witness (`sub24 @0x407e8`, called from the `0:45` setter subs 22/23) + oracle walkthrough order. `0:45` set by 010D sub22 `@0x4009c` / 0112 sub14 `@0x4fc24` (Backyard ambush scene), armed by `0:57` set only in 0114 sub12 `@0x40170` (walk-through zone `@0x40084`, placed while `0:57` clear). STATIC-SCD-RE cont.61. |

The audit confirms the overlay is **complete** for the gates its conservative filter can see — but the
filter's sense heuristic assumes the branch skips *over* the record; when the branch jumps *into* the
record (the 010D→010A shape above) it mislabels the gate "open-by-default" and drops it, so filter
silence is no longer proof of absence for branch-into-record placements (cont.61). The
lock-axis external gate `0108→0113` (type-1, flag `9:6`) lives in `map-requirements.md`, not here.

## Resolved — Large-Size Elevator edges are FREE (CE-witnessed 2026-07-10, NOT authored)

| Door room | Dest | Flag | Verdict |
|---|---|---|---|
| 0405 | 060F | 2:6 | **FREE** — live CE capture (STATIC-SCD-RE cont.46): user rode `0405→060F→030C` and back with `2:4/2:5/2:6 == 0` the whole time, without entering 030B, and the ride set none of them. The door is **present at `2:6==0`** ⇒ skip-if-**true** (open at start), refuting the `door_catalog.py` "skip-if-false `2:6`" decode as a passability constraint and confirming §8d. `2:4/2:5/2:6` are runtime UI/cursor registers (`0x446664` menu object writes grp2 b4–7 from input) + the elevator's own state is byte `[0x677F5D]`, **not** the g2 bank — so no monotonic progression to gate on. Verdict (a) impossible; `requiresRoom=[030B]` NOT authored (would gate a free passage). |
| 060F | 030C | 2:5/2:4 | **FREE** — same capture; both `060F→030C` and reciprocal `030C→060F` traversed at `2:x==0`, both directions, no 030B, no flag written. |

These are no longer `CrossRegionFreeBridges` phantoms *to resolve* — they are confirmed genuinely-free topology (§8e), matching §8d. Left unauthored by design.

## Excluded as broad-flag noise (NOT gates)

21 doors gated on flag **`2:4`** (set in `030B`, fan-out ~13) or another native-writer register: both
directions of `0102↔0110`, `0105↔0110`, `0110↔010A`, `0101↔0111`, `0108↔0111`, `0300→030C`, `0106↔0205`,
`0501↔0502`, `0509↔050A`, `0405↔060F`, plus `0306→0304` (`0:119`, fan-out 4). A single room-init guard /
elevator register, not a per-door lock — these doors are walkable from game start, long before `030B`.
Since cont.47 the toggled/runtime exclusion is **derived, not hand-coded**: `door_catalog.py --audit`
excludes any candidate flag with a NATIVE `SetFlag 0x40752B` writer (static census,
`tools/scd_re/flag_writers.py`) — this reproduces the old `{2:4,2:5,2:6,3:33,3:34,3:57}` denylist
exactly (all six are exact native-writer flags) and found **no new door-gate flag** to queue for CE.
Kept as data only; promoting any requires CE validation of what the flag actually gates in-game
(STATIC-SCD-RE cont.42/47, GRAPH-LOGIC-PARITY §8c/§8d).
## Laser-fence door gates (state driver DECODED offline — STATIC-SCD-RE cont.62, 2026-07-13)

A fence's wall entity polls `GetFlag(0, fenceFlag)` every frame (shared type-4 screen handler
`0x4B0E10`; `0` = beams active/**blocking**, `1` = down/passable). Its keypads (op28 **subtype-11**
AOT pairs, one each side of the wall) are always placed; operability is the NATIVE toggle FSM
`0x44B90D`: refuse unless `GetFlag(0, enableBit)` (record `+0x1F`), plus a security-level check
(record `+0x1E`: level 6 needs **Key Card C `0x38`** — `GetFlag(11,0x38/0x39/0x3A)` → level 6/7/8).
Because an OPERABLE keypad toggles the fence freely from either side, the progression gate is the
**enable flag's producer chain**, never the momentary on/off state (locked design decision).
Entry-side approximation: rooms are atomic in both graph layers, so each partition is encoded as
`requiresRoom`/`requires` on the door edge(s) crossing it **from the vanilla entry side only**; the
reverse edges are NOT gated (door-rando caveat: a shuffle that lands the player on the far side of a
partition bypasses/strands these gates — same caveat as every intra-room partition until a
sub-room-node schema exists).

All 14 fences (fence flag / enable bit are group 0; walls are ±3000 spans; RDT offsets = op58 sites):

| Room | op58 @ | Fence flag | Wall | Enable | Level | Enable producer chain (byte-cited) | Blocked (entry side) | Gate authored |
|---|---|---|---|---|---|---|---|---|
| 0102 A | `0x39eac` | 0:12 | z=-4000 | 0:240 | 0 | 0107 sub8 zone `@0x13a60` (needs 0:22 `@0x13a50` + 7:85 `@0x13a58` = 0202 DDK Input Disc N taken, take-idx 85 `st202@0x4ff0c`) | 0102→0101, 0102→0104 | ✅ `requiresRoom [0106,0202,0107]` |
| 0102 B | `0x39ef4` | 0:13 | x=6500 | 0:241 | 0 | 0102 sub6 auto-event `@0x3a1e4` (needs 0:22 ← 0106 sub8 `@0x2e6c0`) | 0102→0107 | `requiresRoom [0106]` |
| 0108 | `0x3d6a0` | 0:32 | z=4500 | 0:243 | 0 | 0107 sub8 (as 0102 A) | 0108→0113 | **none — collocates with the existing 9:6 story lock** (`0108→0113 requiresRoom [0113]`, cont.61 §B): the fence partitions the same boundary the 9:6 door already gates, so it adds nothing monotonic. Not authored (would double-gate a modeled edge; the shipped `[0113]` value is pinned by RequirementOverlayTests) |
| 010A | `0x4855c` | 0:36 | x=-7500 | 0:244 | 0 | 0107 sub8 (as 0102 A) | 010A→0107 | ✅ `requiresRoom [0106,0202,0107]` |
| 0301 A | `0x3c4e8` | 0:78 | z=500 (west arm) | 0:245 | 0 | own init `@0x3c6c8` (unless lockdown cache 0:94) | 0304/0302 ↔ 0305 partition | none (self-enables on arrival) |
| 0301 B | `0x3c530` | 0:79 | x=0 | 0:246 | 0 | own init (same arm) | 0304 ↔ 0302/0305 partition | none |
| 0306 A | `0x45ea8` | 0:98 | z=-4050 | 0:247 | 0 | own init `@0x460ec` (unless 0:103) | 0305 ↔ 0309 | none |
| 0306 B | `0x45ef0` | 0:99 | z=9200 | 0:248 | 0 | own init (same arm) | 0305 ↔ 0304 | none |
| 030A | `0x54d34` | 0:112 | z=-1000 | 0:249 | 0 | 0406 sub12 first-visit `@0x37a9c` (campaign; the 0106 sub16 arm `@0x2f7d4` needs bonus-mode 0:1) | wall sits BETWEEN the two 030C doors — no whole edge blocked | none (collocated; both 030C doors reachable) |
| 030D | `0x48408` | 0:112 (shared) | z=-1000 | 0:249 (shared) | 0 | same | same | none |
| 0500 | `0x3dcb4` | 0:177 | x=0 | 0:250 | 6 | 0608 sub6 `@0x3fe00` (needs 0:137 ← 0505 sub17 `@0x48760`); **0500 init forces 0:177=1 (down) on first visit `@0x3de40`** | nothing in practice | none (init lowers it before the player can move) |
| 0502 | `0x47f70` | 0:173 | x=-10007 | 0:251 | 6 | 0608 sub6 (via 0505) + Key Card C | 0502→0503, 0502→0511 | **none — collocates with the type-6 Key Card doors** (0503/0511 are type-6 card doors; the card requirement is already enforced by KeyItemPlacer via the door TYPE byte, cont.61 §B). Not authored |
| 0606 | `0x3e870` | 0:147 | x=-1500 | 0:252 | 6 | 0608 sub6 (via 0505) + Key Card C | **no door** — east segment holds pickups only: `0x2B @0x3e5a0`, `0x2B @0x3e5cc`, `0x18 @0x3e5f8` | none on doors; item row below |
| 0608 | `0x3fae4` | 0:149 | z=4000 | 0:253 | 6 | own sub6 auto-event (needs 0:137 ← 0505) + Key Card C | 0608→0614 | **none — collocates with the type-8 Key Card A door** (`0608→0614 requires [0x3A]`, cont.61 §B). Not authored |

**Lockdown (non-monotonic, ledger-only — NOT authored):** 0300 sub16 sets 0:200; re-entering 0304
then runs sub14 → **0:91 = facility lockdown ON** (030A/030D inits replace keypads with refusal
AOTs and force 0:112=0; 0305/0309 inits cache it as 0:94/0:103, disabling the 0301/0306 keypads and
forcing their fence flags 0). The 030C Computer Room events (sub18/sub19) set **0:119 = lockdown
LIFTED** (and reset 0:78/79/98/99 to 0). A monotonic `requiresRoom` cannot express a re-lock;
vanilla beatability is unaffected (the lift event is on the mainline), matching cont.61 §B's
fixpoint. The two native `SetFlag(0,0xF0..0xFD,1)` loops (`0x436D60`/`0x43A86C`) and the
0:254/0:255+0:0–0:7 pair-switch (`0x447DCA..0x447F87`) are **bonus-mode** mass-unlock plumbing
(mode word `0x6D3C40 & 0xA0/0x8A0`), not campaign logic.

**0606 east item segment (placement gate, not a door gate):** pickups `0x2B ×2 + 0x18` behind fence
0:147 → reachable only with enable 0:252 (0608 sub6 via 0505) + Key Card C `0x38`. Recorded here for
the item-logic layer; the door graph is unaffected.

## Region model (2026-07-13) — supersedes the entry-side approximation

All 14 fences are now modelled as sub-room **regions** in `map.json` (`rooms[X].regions`, schema in
`_derivation.regions`; decision record `docs/decisions/dc1/doors/REGION-SCHEMA-PLAN.md`). Each
partition's crossing rule lives in its region's `accessFrom`, and the C# binds the gate to the physical
**doorway** (`DoorRecord.OriginalTargetCode`) instead of the destination room — so under door-rando the
fence gate travels with its own doorway rather than floating (the entry-side caveat above is closed for
these gates). The four door-level fence gates (`0102→{0107,0101,0104}`, `010A→0107`) were **migrated**
out of `doors.<dest>.requiresRoom` into `regions.accessFrom` and removed from the door-level form (no
double-gating). The other fences carry an empty (always-open) rule with a `_why` note: self-enabling
(0301/0306), init-forced-down (0500), collocated with a typed/story door whose own gate already
enforces the partition (0108 = 9:6, 0502/0608 = Key Card doors), or lockdown-non-monotonic (030A/030D).
0606's east segment stays an item-layer gate, now expressed as `regions.east.items` + `accessFrom`
(Key Card C `0x38` + enable room 0505). For fixed doors this is byte-identical to the old door-level
gates; only door-rando behaviour changes (soundly).

## Native/branch-into-record locks + the 0309 shuttle node-split (2026-07-13, cont.64)

Three door gates the `door_catalog.py --audit` filter structurally missed (branch-into-record senses
are labelled `skip-if-true` and dropped), plus one intra-room partition. Byte cites: STATIC-SCD-RE
cont.64; engine: REGION-SCHEMA-PLAN §2 / GRAPH-LOGIC-PARITY-PLAN §8k.

| Edge | Class | Authored | Citation |
|---|---|---|---|
| `010D→030B` | native KEY lock | `requires:[0x2F]` (BG Room B1 Key) | type=0/unconditional yet live key-locked (cont.63); key in 0109; item 0x2F |
| `0112→0404` | branch-into-record STORY | `requiresRoom:[0106]` | present-iff-`0:24`-TRUE; `0:24` set unconditionally by `0106 sub16 @0x2f724` (cont.61 sense-erratum; live "it's locked" cont.63) |
| `0105→0307` | generator SHUTTLE | `requiresRoom:[030B]` | door unconditional but shuttle powered by `0:114` ← `030B sub15 @0x19cac`; conservative reach-030B proxy for the puzzle-completion predicate |

**`0309` "Hall B1" = an entry-direction shuttle partition (node-split, not a flag gate).** The shuttle
car partitions `west {0307,030A,030D}` (0307 B1-key-route entry) from `shuttle {0306,0113,050B,0604}`
(boarding/`0113`-elevator side; shuttle-car doors placed in event `sub32`). Which exits you reach
depends on the entry door — un-expressible by `requires`/`requiresRoom`. Modeled as `map.json`
`rooms.0309.nodeSplit + regions`; `RoomGraph.Build` routes each inbound door to the sub-region owning
its reciprocal. The `west↔shuttle` on-foot crossing is `[uncertain — needs CE]` (the `2:5`
persistence-vs-runtime-panel question, cont.46) → **no `accessFrom` authored** (tightest safe model;
vanilla stays beatable via the `0113` elevator). The pre-existing `0309→0306` DDK gate (`requires
[0x64,0x6b]`) is preserved and binds to the shuttle sub-region.
