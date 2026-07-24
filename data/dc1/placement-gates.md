# DC1 placement-axis door-gate provenance ledger

**Status:** validated (conservative filter, reconciled with the §8c/§8d live capture). Companion to
`map-requirements.md` (which owns the *lock* axis — type-1/3 doors gated by `GetFlag(9,·)`, auto-generated
by `tools/scd_re/extract_logic.py`). This ledger owns the *placement* axis — type-0 doors whose activation
is a `test → 0x0e` script flag-gate (`tools/scd_re/door_catalog.py`, STATIC-SCD-RE cont.42/45,
GRAPH-LOGIC-PARITY §8a–§8e).

The raw placement census is 120 flag-gated doors, dominated by a broad scenario-phase flag historically
attributed to `030B` that the dominance heuristic mis-attributes to ~20 early-map doors (both directions of `0102↔0110`,
`0110↔010A`, `0101↔0111`, …). Promoting those as gates would inject false constraints, so a **conservative
filter** keeps only trustworthy per-door **monotonic** gates:

> a `(src → dest)` edge is a trusted placement gate iff **every** placement of that dest is a
> `skip-if-false` `test_branch` (door placed only when the flag is SET) with an **external** producer
> room, **none** unconditional, **none** `skip-if-true` (open-by-default variant), a **single** gate
> flag, that flag gates **≤ 3** destinations (fan-out cap — `2:4` fans out to ~13), **and** that flag is
> **not a transient/native register** (`2:4/2:5/2:6` selection registers, `3:33/34/57` runtime — these
> are not monotonic progression flags, and their native consumers are case-specific; §8c/§8d deferred
> them, "author nothing yet").

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

## Elevator forge-chain + power gates (re-audit 2026-07-14 — NATIVE mechanism, requiresRoom producer model)

A native-mechanism analog to the `0400→0401` communication-room gate above (§8d): where the real unlock is
a **native** check with **no readable SCD flag**, model it on the ROOM whose event produces the unlock —
not on a refuted item/register. The current ledger combines cont.71's facility-elevator proxy with
cont.46's large-elevator free witness; the exact facility native predicate remains open:

- **Facility elevator (`0113` → floors) — native predicate unresolved.** The decoded script side shows
  `st113` sub15 testing card items `0x36/0x3B/0x34` and writing `0:55/0:56`, but no corpus reader for
  those outputs is known. The generic menu handlers that write `2:4/2:5/2:6` are selection-register
  handlers, not by themselves proof of an elevator gate. The current `requiresRoom` entries therefore
  remain conservative forge-room proxies; the byte-true card-to-floor predicate is **[unverified]**.
- **Big/cargo elevator (`0405↔060F↔030C/0600`) — power, native.** Gated on the generator-power producer
  room `030B` (power restored via BG Room B1 Key `0x2F`, `010D→030B`). **Erratum (2026-07-15, user-directed):**
  the B3 stop `060F→0600` needs an ADDITIONAL gate. `030B` is reachable from the start (`010D→030B` via the
  B1 Room Key `0x2F`), so `requiresRoom:[030B]` alone did NOT require the Entrance Key — leaving a **phantom
  descent**: the engine reached Carrying Out Room B3 `0600` (and the whole B3 endgame + goal `060D`) with only
  `0x2F`, no Entrance Key, no DDK discs (measured: goal reachable holding all keys minus `{2E,63,6A}`). In the
  real game the Large Elevator's B3 stop only works after the **heliport route** reaches Liaison Elevator
  No.1 (`0401→0402→0403→0409→040A→040C`). The gate therefore preserves the existing generator-power
  and heliport prerequisites and adds `040C` as the hard activation room.

| Door room | Dest | Real gate | Authored | Provenance |
|---|---|---|---|---|
| 0113 | 0309 (B1) | Researcher-card forge | ✅ `requires:[0x41]` + `requiresRoom:[010B]` | F.C. Device `0x41` @`0104` → Paul Baker print @`0113` → rewrite @`010B` (sub10) |
| 0113 | 050B (B2) | Kirk-card forge | ✅ `requiresRoom:[0506]` | Kirk print @`050C` → rewrite @`0506` (sub13) |
| 0113 | 0604 (B3) | Kirk-card forge | ✅ `requiresRoom:[0506]` | same endgame forge |
| 0405 | 060F | generator power | ✅ `requiresRoom:[030B]` | power @`030B` |
| 060F | 030C | generator power | ✅ `requiresRoom:[030B]` | power @`030B` |
| 060F | 0600 | generator power **+ Liaison Elevator No.1 activation** | ✅ `requiresRoom:[030B, 0401, 040C]` | power @`030B`; heliport route `0401→0402→0403→0409→040A→040C` enables the Large Size Elevator B3 stop |

Only the `0113→floor` **descent** edges are gated; the inter-floor elevator edges (`0309↔050B↔0604`) are
left to their door-record logic — gating them **breaks the vanilla key ordering** (measured: `Verify`
fails). All six gates are reachability-**inert** (the lab keeps non-elevator entrances) but remove the
free-bridge EDGE (matters for door-rando / model soundness). This **deliberately supersedes** the
§8c/§8d "author nothing yet" deferral **for these specific edges** (user-directed, 2026-07-14): the
deferral was about the `2:x/3:x` toggled FLAGS; these gate the producer ROOM instead (monotonic
room-visit, the same shape as `0400→0401`). Guards:
`KeyItemPlacerTests.RealInstall_ElevatorDescent_GatedByForgeAndPowerRooms` (edge requirements) +
`KeyItemPlacerTests.RealInstall_DeepFacility_RequiresARealSurfaceDescent` (the goal needs a real surface
descent — heliport or DDK-N elevator hall — so the Large Elevator B3 stop can't be a free third route).

## Endgame goal-lock — Key Card A only (re-audit 2026-07-14)

Static native tracing **REFUTED** the prior §8o/§8p door gates: the Third Energy overload sets **no SCD
flag** the `060D` escape doors read (they read only the transient cutscene toggle `3:33`, denylisted), so
Kirk `0x3B` / Stabilizer `0x4C` / Initializer `0x4D` are native/CE-only — **not** modeled door gates. The
only offline-verifiable goal lock is the pre-existing **Key Card A (`0x3a`)** on the escape doors +
`0611→060D` (type `0x08`). The overload chain is `[uncertain — native / CE-only]`, left to a future
`ce-live-capture`. Guard: `KeyItemPlacerTests.RealInstall_EndgameEscape_OnlyKeyCardAIsGoalCritical`. Also
note: the **Entrance Key `0x2e`** is NOT a placement-axis gate — it is a door **TYPE-byte** gate
(`0107→0400 type=0x2E`, handled by `KeyItemsForDoor`), so it is already modeled in the engine and belongs
to neither ledger; the earlier "Entrance Key is free / reachable another way" note was **wrong** (removed).

## Resolved — Large-Size Elevator edges are FREE (CE-witnessed 2026-07-10, NOT authored)

| Door room | Dest | Flag | Verdict |
|---|---|---|---|
| 0405 | 060F | 2:6 | **FREE** — live CE capture (STATIC-SCD-RE cont.46): user rode `0405→060F→030C` and back with `2:4/2:5/2:6 == 0` the whole time, without entering 030B, and the ride set none of them. The door is **present at `2:6==0`** ⇒ skip-if-**true** (open at start), refuting the `door_catalog.py` "skip-if-false `2:6`" decode as a passability constraint and confirming §8d. `2:4/2:5/2:6` are runtime UI/cursor registers (`0x446664` menu object writes grp2 b4–7 from input); `[0x677F5D]` is generic interaction-session state, **not** the g2 bank or an established elevator-only state — so no monotonic progression gate is proven. Verdict (a) impossible; `requiresRoom=[030B]` NOT authored (would gate a free passage). |
| 060F | 030C | 2:5/2:4 | **FREE** — same capture; both `060F→030C` and reciprocal `030C→060F` traversed at `2:x==0`, both directions, no 030B, no flag written. |

These are no longer `CrossRegionFreeBridges` phantoms *to resolve* — they are confirmed genuinely-free topology (§8e), matching §8d. Left unauthored by design.

## Excluded as broad-flag noise (NOT gates)

21 doors gated on flag **`2:4`** (historically attributed to `030B`, fan-out ~13) or another native-writer register: both
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

> **ERRATA (2026-07-14) — the `0309` `nodeSplit` was described "Applied" (cont.64) but was NEVER in
> `map.json`** (`git log -S nodeSplit -- data/dc1/map.json` empty; the C# split, `RegionNodeSplitTests`,
> and this ledger all referenced a partition the *data file* lacked). Effect: `0309` parsed **atomic**, so
> the graph **flattened** — descending to B1 by the stairs (`010D→030B`, BG Room B1 Key `0x2F`) freely
> reached the elevator hall `0113` and the whole deep B2/B3 hub, bypassing the ~6-stage heliport /
> large-elevator progression (a `CrossRegionFreeBridges` phantom, GRAPH-LOGIC-PARITY §8k). This was the
> root cause of the "DDK Input N in `0508` is reachable" report: `0508` was reached through the phantom
> shuttle crossing (`030A/030D→0309→050B→0509→0508`), not the intended route. **RESTORED**: authored the
> `0309.nodeSplit + regions` (`west {0307,030A,030D}` / `shuttle {0306,0113,050B,0604}`, cont.64 §E) into
> `map.json`. Post-fix the generator stairs + descent keys (`0x2F,0x30`) reach B1 (36 rooms) but NOT the
> shuttle side (`0113/050B/0604/0506/0509/0508`) — the deep B2/B3 hub is reachable only through the
> heliport / large-elevator route (or the `0113` elevator once `0506` is reached). Vanilla still beatable;
> shuffle-OFF byte-identical (overlay-only). Guard: `KeyItemPlacerTests.RealInstall_HallB1ShuttleSplit_
> GeneratorStairsCannotReachB2B3Hub` (RED on atomic `0309`, GREEN with the split).

> **REFINEMENT (2026-07-15, user-directed) — the `0309` shuttle deep stops are heliport-only.** The split
> above still let the **facility elevator** be a phantom descent: `0107→0113` (DDK-N disc pair `0x63+0x6a`)
> lands in the `0309` shuttle region, and the shuttle stops `0309→050B` (B2) and `0309→0604` (B3) were
> **free** — so with the DDK-N discs (whose `0x6a` the shuffle can relocate out of the Entrance-Key zone
> `0400`) the engine reached the whole B2/B3 hub and the goal **without the Entrance Key** (measured: goal
> reachable holding all keys minus `0x2e`). Real game: the shuttle only reaches the deep floors after the
> heliport route — the giant elevator's B3 stop needs `0401` power, and the Third Energy Control Room
> `050B` (the shuttle's B2 stop) is itself reachable only after the heliport and Library progression.
> Same shape as the big-elevator fix. **AUTHORED**: `0309→050B` requiresRoom `[0401,050F]` (shuttle B2
> stop needs the heliport and prior Dr. Kirk's Library access; `050F` added 2026-07-24, user-directed) and
> `0309→0604` requiresRoom `[050B]` (shuttle B3 stop needs Third Energy Control). Post-fix the whole B2/B3
> deep facility (incl. `050B/0604/0600/0608/0609/060D`) leaves every single-key oracle probe — it appears
> only when the Entrance Key is held. So the Entrance Key is now a hard B3 requirement. The `0113→050B` /
> `0113→0604` forge edges (reqRoom `0506`) need no change: `0506` is itself inside the now-gated hub.
> The shuffled forward-fill model remains beatable. The
> `0401→0402→0403→0409→040A→040C→0405→060F→0600→0601→0602` route now records the real first
> descent and generator activation without a C.O.-key self-lock. The vanilla sphere model continues
> through the free General Weapons Storage variants to `0606`; do not restore `0401↔0606`.
> Guards: `KeyItemPlacerTests.RealInstall_DeepFacility_RequiresTheEntranceKey` and
> `RealInstall_CoAreaKey_IsRequiredBeforePassagewayCanReachRestStation`.

> **REFINEMENT (2026-07-24, user-directed) — Library-gated shuttle + Rest Station boundary.**
> `0309→050B` now requires both `[0401,050F]`, and Rest Station `0604` has a room-level
> `requiresRoom:[0603]` backstop so none of its alternate graph inbounds bypass the C.O.-locked
> `0600→0603` passage. No `0401↔0606` edge is authored: Passageway to the Heliport has no physical
> door to Transport Passageway. Exact-seed characterization for `DINO-AetwArn_H_8vBw` now places
> `0x31` in `0502`, reachable without itself; the reported `0606→050F` row no longer reproduces.

> **REFINEMENT (2026-07-24, user-directed) — Carrying Out Room bootstrap.**
> `0600→0602` does **not** require the C.O. Area Key. It now has
> `requiresRoom:[0601]`: entering Backup Generator Room B3 enables the Control Room B3 route.
> This removes the direct C.O.-key self-lock.
>
> **REFINEMENT (2026-07-24, user-directed) — C.O. Area Key boundary.**
> The C.O. Area Key gates only `0600→0603`. The authored `0x31` requirement on
> `0600→060F` was erroneous and has been removed; the protected reverse Large Size Elevator gate
> `060F→0600 requiresRoom:[030B,0401,040C]` remains intact. This correctly models returning from
> Carrying Out Room B3 to the Large Size Elevator without using the C.O. Area Key.
>
> **REFINEMENT (2026-07-24, user-directed + same-build static room proof) — Key Card Lv. A boundary.**
> Key Card A gates the Transport Passageway connections `0606↔0607` (Special Weapons Storage) and
> `0606↔0611` (Heliport Transport Passageway), not either General Weapons Storage state. Static room
> records prove `0602→0605/0615`, `0605/0615→0606`, and the reverse storage directions are TYPE `00`,
> lock `00`; the four authored `requires:[0x3A]` overlays on `0602/0606→0605/0615` were erroneous and
> are removed. The raw forward `0606→0607` and both `0606↔0611` records already encode TYPE `08`;
> `0607→0606` receives the missing authored reverse gate. Vanilla spheres now collect the C.O. Area Key
> in `0606` at sphere 5, Key Card A in `050E` at sphere 6, and complete all 96 rooms. Guards:
> `Dc1ReachabilityOracleTests.CommittedOracle_KeyCardADoesNotGateGeneralWeaponsStorageRoute`,
> `KeyItemPlacerTests.RealInstall_KeyCardADoesNotGateGeneralWeaponsStorageRoute`, and
> `RealInstall_VanillaOverlay_CollectsCoAreaKeyBeforeKeyCardA`.
>
> **CLARIFICATION (2026-07-24, user-directed) — Grenade Gun Parts pickup.**
> Parts Storage room `0507` remains reachable independently of this rule, but its vanilla GG Parts
> pickup (decoded item `0x0F`, record `0x41410`) requires Key Card Lv. A `0x3A`. This is authored as
> `0507.items["0f"].requires:[0x3A]`, so it gates collection of that location without adding a door or
> room-entry requirement. Guards: `Dc1MapContractTests.Map_0507_GrenadeGunParts_RequiresKeyCardA` and
> `KeyItemPlacerTests.RealInstall_PartsStorageGrenadeGunParts_RequiresKeyCardA`.

## `010D` An. Aid scatter-spot gate (2026-07-15, user-directed, NO code trace)

**Deviation from this ledger's own standing policy** (`[[dc1-gate-authoring-lessons]]`: "never author a
gate from guides alone — needs a code trace"). Flagged and logged as such for the record.

`010D` ("Backyard of the Facility") is DC1's **start room** (`GameDefinition.StartRoomCode`,
`REACHABILITY.md: Start 010d`) and its An. Aid pickup at `-6144,768` (item `0x20`) is a legal key-item
**scatter target** (`KEY-ITEM-SCATTER-DATA-AUDIT.md` §4/§7). The static evidence available in this repo
— the item's own census record (`gate: unconditional`, `trigger: init`, `data/dc1/room-data.json`), the
`010D→030B` door gate (`requires:[0x2F]`, i.e. the B1 Room Key gates only the *door down to the
basement*, not anything inside `010D`), and `REACHABILITY.md`'s sphere table (both `BG Room B1 Key
@0109` and `BG Area Key @010c` are sphere-0, held-from-t=0) — is **consistent with this item being
freely reachable from game start**, not gated by the B1 Room Key.

The user asserted from an unspecified source that the position is in fact unreachable without B1 Room
Key `0x2F` and directed the gate be authored anyway, conservatively, without a code trace. **AUTHORED**:
`010D.items["20"].requires = [47]` — the pickup's `NodeItem.Requires` now demands holding `0x2F` before
`KeyItemPlacer` treats that spot as reachable (consumed identically whether the spot is used for a
door-key or an opt-in scatter placement, `ProgressionPass.cs`). Effect is one-directional-safe: if the
claim is right, this closes a real softlock; if wrong, it only makes that one spot slightly less
available to the placer (measured: still used by 15/40 swept seeds for other keys; 0/40 for the tracked
BG Area Key specifically, vs. landing there in about half of an earlier un-gated 10-seed sample — a
distribution shift, not a beatability regression, all 40 confirmed beatable). No CE witness taken.
**Follow-up:** if a future session gets a live capture of `010D`, confirm or retract this gate against
the CE ground truth. Guard: `Dc1MapContractTests.Map_010D_AnAid_RequiresB1RoomKey` (data-contract only;
asserts the map.json entry, not the underlying physical claim).

## `0101` Toilet / `0104` Strategy Room — room-level backstops for the `0102` fenceA gate
## (2026-07-15, user-directed, CODE-TRACED)

Both rooms have a direct byte-cited match already in this ledger's row-A `0102 A` fence (`0x39eac`,
flag `0:12`, wall `z=-4000`): the fence's keypad-enable flag `0:240` is armed by `0107 sub8 @0x13a60`,
which requires `0:22` (0106 route) **and** `7:85` — the take-flag for DDK Input Disc N in `0202`
(Chief's Room). That fence gate already authors `0102→{0101,0104} requiresRoom [0106,0202,0107]` on the
door edge (via the `fenceA` region overlay, `ParseRegions` → `RegionDoorGates`). The user's claim
("`0101` requires having visited `0202`", later extended to `0104`) is therefore **already correct and
evidenced** — the only gap was that the existing gate lives entirely on the `0102→0101`/`0102→0104` door
edges, so a door-rando repoint of some *other* door straight into either room would silently bypass it.

**AUTHORED**: `0101.requiresRoom = ["0202"]` and `0104.requiresRoom = ["0202"]` — room-level gates
(`RoomNode.Requires`, applied to every incoming edge regardless of route), layered on top of the
existing edge-level fence gate as a door-rando-safe backstop, not a replacement.

**Reachability-oracle effect, measured (corrects an earlier mis-citation in this entry — the affected
pair is the DDK **H** discs, not N):** `0202`'s own reachability is gated by the *pre-existing*
`0203→0202 requires [0x62,0x69]` door (DDK Input/Code Disc H), unrelated to this session's edits.
`0101`/`0104`'s new `requiresRoom:[0202]` only re-states "0202 must be reachable" — which the *existing*
`0102A` fence edge-gate already encoded as one of its three AND-conditions. Net effect on the oracle:
**zero diff** (confirmed on a from-clean `obj`/`bin` rebuild, so it isn't a stale-embedded-resource
artifact) — `0101`/`0104`/`0202` are all `False` only in `all-minus-ddk-62`/`all-minus-ddk-69` (count
96→93, the H pair — this is the *pre-existing* door gate doing its job, not something these two edits
changed) and `True` in every other DDK probe including the N pair (`all-minus-ddk-63`/`-6a` stay at 96).
So both room-level gates are, today, pure door-rando-only insurance with no effect on the base graph;
that is the expected, correct outcome for a defense-in-depth backstop, not a bug. `gen_ap_logic --check`
/ apworld selfchecks / beatability sweeps all clean both times. Guards:
`Dc1MapContractTests.Map_0101_Toilet_RequiresVisitingChiefsRoom`,
`Dc1MapContractTests.Map_0104_StrategyRoom_RequiresVisitingChiefsRoom`.

## DDK Input/Code Disc H circular-relocation safety (2026-07-15, user-raised, VERIFIED, no data change)

Follow-up concern raised after the `0101` fix above: since the `0203→0202` door requires holding BOTH
DDK Input Disc H (`0x62`) and DDK Code Disc H (`0x69`) (the DDK pair table, `Dc1MapContractTests`), and
`0202` now gates `0101`, could `RelocateDdkDiscs` ever place either H disc somewhere unreachable without
already holding it? The rooms first named (`0511` Stabilizer Experiment Room, `0205` Communication Room,
`050B` Third Energy Control Room) turned out **unrelated to this fence** on inspection — `0511` sits
behind a *different* fence (`0502`, already safely collocated with a type-6 Key Card door per the fence
table above); `0205` is not fenced at all; `050B` is gated by the unrelated Entrance-Key/heliport chain.

The room genuinely at risk is **`0104` (Strategy Room)** — it sits behind the same `0102` fenceA
partition as `0101` (`0102→{0101,0104} requiresRoom [0106,0202,0107]`) and hosts DDK Code Disc E
(`0x6c`) as its vanilla item, making it a legal `RelocateDdkDiscs` pool spot. Verified empirically
(`KeyItemPlacer.Verify` on the real graph): forcing **either** DDK Input Disc H (`0x62`, vanilla `0103`)
**or** DDK Code Disc H (`0x69`, vanilla `0100`) into `0104` — displacing `0x6c` out to the moved disc's
vanilla room — is correctly rejected as unsolvable (`Success=False`) by the existing sphere-based
reachability check, for both discs independently. **No map.json change** — the fenceA gate was already
correctly authored before this session, and `KeyItemPlacer`'s existing safety net (the same mechanism
that makes every swept seed beatable) already refuses this placement; there was nothing to fix, only to
prove and lock in. Guard: `KeyItemPlacerTests.RealInstall_DdkDiscRelocation_RejectsEitherHDiscBehindItsOwnFenceGate`
(`[Theory]`, both discs covered per user direction — an earlier single-disc version only tested `0x69`).

## The 0109/0401 deep chain — closing a real phantom bridge (2026-07-15, user-directed, NO code trace)

Follow-up to the DDK-H entry above: the user pointed out a REAL gap the `0104` check above didn't cover
— `0107→0113` (needs only the DDK-N pair `0x63`/`0x6a`, unrelated to `0101`/`0202`/H-pair) reaches a
free, unconditional cluster `{0108,0111,010B,0109}`, and `0109` (Lecture Room) hosts the vanilla `BG
Room B1 Key 0x2F`. **Empirically confirmed via the reachability oracle** (not just asserted): in
`all-minus-ddk-62`/`all-minus-ddk-69` (missing an H-pair disc), `0101`/`0202` were correctly `False`,
but `0109`/`0113`/`0108`/`0111`/`010B` were **all `True`** — the model believed this whole cluster
(including the B1 Room Key spot) was reachable without the H-pair. A genuine phantom bridge, the same
class of bug as the `010F`/`0114` heliport gate and the `0309` shuttle flattening (both `[[gitignore]]`-
adjacent memory entries) — caught this time by deliberately probing the oracle instead of trusting that
"the code says it's reachable."

**Four gates authored** (all `requiresRoom`, no code trace — same conservative, user-directed pattern as
the `010D`/`0101`/`0104` entries above):

| Edge/room | Change | Why (user's description) |
|---|---|---|
| `010B→010A` door | `requiresRoom:[0101]` | the Office↔Office-Hallway shortcut. **Known incomplete**: `010A` has an independent free entry via `0107→010A`, and the `{0108,0111,010B,0109}` cluster has its own free entry via `0107→0113→0108`, so this edge gate alone does not block either side's overall reachability — user accepted leaving it as-is ("it's okay") rather than pursuing a room-level `010A`/`0113` gate. |
| `0109` (Lecture Room) | `requiresRoom:[0101]` | "Gail cutscene" trigger site — closes the B1 Room Key phantom-reach directly. |
| `0105→0307` door | `requiresRoom:[030B,0109]` (was `[030B]`) | Control Room Hall shutter to Medical Room Hallway — the 030B+Gail-cutscene combination. |
| `0112→0404` door | `requiresRoom:[0106,0109]` (was `[0106]`) | The Backyard → Large Size Elevator Passageway — same combination, the other alternate path. |
| `0400→0401` door | `requiresRoom:[0205,0109]` (was `[0205]`) | **The decisive one.** `0401` (Passageway to the Heliport) already gates the *entire* B2/B3 deep facility transitively (`0309.doors["050B"].requiresRoom=[0401,050F]`, `.doors["0604"].requiresRoom=[050B]`) per the Entrance-Key/heliport + Library work — so adding `0109` here alone closes `050B`/`0604`/`0609`/`060B`/the whole deep facility back to `0101`/`0202`/the H-pair, without touching those downstream edges directly. |

**Measured effect (large):** `all-minus-ddk-62`/`-69` reach count dropped from 96 → **46** (half the map)
— confirms the DDK H-pair is now a transitive hard requirement for the **goal room `060D` itself**
(`060d` present in `reach` only when both H discs are held), not just a side room. This is consistent
with, not a new category from, the pre-existing `gen_ap_logic.py` progression-items list, which already
carried `0x62`/`0x69` as progression-critical before this session.

**Verified safe despite the scale of the change:** full `KeyItemPlacer`/`Dc1MapContract`/
`Dc1ReachabilityOracle`/`RequirementOverlay`/`RegionNodeSplit` suite green (one stale hardcoded
assertion in `RequirementOverlayTests` — pinned the old `0400→0401` gate value — updated to match);
`gen_ap_logic`/`gen_reachability`/`gen_data_reference` `--apply` + `--check` clean, both apworld
selfchecks pass; a 50-seed `RelocateDdkDiscs`+scatter sweep tracking DDK Code Disc H specifically shows
it landing only in shallow/early rooms across every seed (never `060B`/`0406`/`0510`/`050B`), 0/50
unsolvable. Guards: `Dc1MapContractTests.Map_010B_to_010A_RequiresVisitingToilet`,
`.Map_0109_LectureRoom_RequiresVisitingToilet`, `.Map_0105_to_0307_RequiresGailCutsceneAtLectureRoom`,
`.Map_0112_to_0404_RequiresGailCutsceneAtLectureRoom`, `.Map_0400_to_0401_RequiresGailCutsceneAtLectureRoom`.

**Known residual gap:** the `010B↔010A` edge-only gate (row 1 above) doesn't actually close anything on
its own, per the "known incomplete" note — if a future session wants the `{0108,0111,010B,0109}` cluster
and `010A` fully sealed against the DDK-N-only `0107→0113` shortcut, that needs a room-level gate on
`010A` and/or `0113` itself, deliberately left open this session per user direction.

## ENGINE BUG FIXED — the group-9 story-latch pre-pass ignored the setter edge's own gate (2026-07-15,
## user-caught, CODE fix, not data)

Follow-up to the `010B→010A` gate above: the user pointed out `010A→010B` (the reciprocal type-1
"shortcut back" reader) was *also* supposed to be locked behind `0101`, since it's the same physical
lock as the now-gated `010B→010A` type-2 setter. Investigating found this wasn't a data gap — it was a
genuine bug in `KeyItemPlacer.Reachable`'s latch pre-pass (`src/DinoRand.Randomizer/Logic/
KeyItemPlacer.cs`): the loop that populates `latches` from every reachable node's type-2
(`SetsStoryLatch`) edges checked only `e.Door.SetsStoryLatch`, never the setter edge's own
`Requires`/destination `Requires` — i.e. "a type-2 door is free to cross" (true for every *other* type-2
door in the game, until this session's `010B→010A`) was hard-coded as "its source room is reachable",
completely bypassing any authored gate on the setter itself. Since `010B` is independently reachable
(via `0108`, unrelated to `0101`), the pre-pass unconditionally added `010B→010A`'s lock to `latches`,
silently re-opening `010A→010B` for anyone, gate or no gate.

**No measurable effect on the current oracle** (confirmed: zero diff on regeneration) — `010A` and
`010B` are *both* already independently reachable via other free edges (`0107→010A`, `0108→010B`), so
this bug happened to be a no-op for their own reachability today. It is NOT a no-op in general: any
future type-2 setter with an authored gate, or a door-rando shuffle that removes one side's independent
free route, would have silently bypassed the gate via this exact mechanism — precisely the "you think
it's reachable due to the code saying so" class of bug flagged earlier in this session (same family as
the `010F`/`0114` and `0309` phantom bridges, but in the *algorithm* this time, not the data).

**FIXED**: the latch pre-pass now additionally requires `CanCross` (door-type key-set) and
`e.Requires.SatisfiedBy(...)`/`e.Target.Requires.SatisfiedBy(...)` before adding a setter's lock to
`latches` — i.e. a type-2 door only opens its reciprocal reader once it is *actually* traversable, not
merely "its source room is in the reachable set." Verified: new synthetic regression test RED before the
fix, GREEN after; the 3 pre-existing latch tests (stranded producer / reachable-producer shortcut /
self-latching type-2 / type-3 free) all stay green — the "free to cross" fast path is unchanged for
every ungated type-2 door. Oracle regeneration shows zero diff (expected, see above). Full 109-test
suite green, `gen_ap_logic`/`gen_reachability`/`gen_data_reference` `--check` + apworld selfchecks clean,
30-seed beatability sweep 0/30 unsolvable. Guard:
`KeyItemPlacerTests.Reachable_Type2Setter_GatedByRequirement_LatchStaysClosedUntilSatisfied`.

## Gate A — `0309→0306` DDK-L: VERIFIED already sealed, NO change (2026-07-16, user-directed audit)

The user directed a check that the Main Hallway door `0309→0306` requires DDK Input Disc L (`0x64`).
Finding: **already correctly enforced, no bypass, nothing authored.** The exact gate (`requires
[0x64,0x6b]`, the native op58-3 pair AND) has been in `map.json` since the DDK-pair tier
(`Dc1MapContractTests.Map_DdkPairDoors_RequireBothDiscs` row L pins the exact array), and a full
edge dump on the real graph (`RoomGraph.Build(rooms, Game.Requirements)`, every edge into/out of the
`{0300,0301,0302,0303,0304,0305,0306}` cluster reverse-searched) proved the cluster's **only external
entry is this one door** — every other incoming edge to any cluster room originates inside the cluster
(`0300←0304` only; `0304←{0300,0301,0306}`; `0306←{0304,0305,0309.shuttle}`; `0301/0302/0303/0305`
all interior). Oracle confirmation: `all-minus-ddk-64`/`-6b` drop the whole 7-room cluster (96→89
pre-session), no other probe reaches it. A `0101`/`0104`-style **room-level backstop was considered and
is NOT expressible**: room-level `requiresRoom` carries visited-rooms only, not held items, so "holding
the DDK-L pair" cannot be encoded on the room node — the lock-in is instead the exact-array contract
test plus a new reach guard. Also noted for the record: the interior path `0306→0305→0301→0304`
bypasses the `0306→0304 requiresRoom [030C]` door gate entirely, so `030C` is NOT actually required to
traverse the cluster (that gate only shortcuts the direct door). Guard:
`KeyItemPlacerTests.RealInstall_ExperimentWing_SoleEntryIsTheDdkLDoor` (theory, both L discs: dropping
either must remove all 7 cluster rooms from reach).

## Gate B — `0304→0300` also requires Key Card L + Key Card R (2026-07-16, user-directed, NO code trace)

The Experiment Simulation Room door `0304→0300` (the DDK-E pair door, native op58-3 `[0x65,0x6c]`)
additionally requires **Key Card L (`0x47`) and Key Card R (`0x48`)**, per user direction — no code
trace; the census records for both cards (`0202` KC-L, event sub25, flag-gated 2:11; `0305` KC-R,
event sub14) do not by themselves witness a door gate. Both ids appended to the door's `requires`
(edge-level item AND), preserving the DDK-E pair:

```
0304.doors["0300"].requires: [101,108] → [101,108,71,72]   (0x65,0x6c,0x47,0x48)
```

Circularity pre-checked before authoring: KC-L vanilla-spawns in `0202` (behind the DDK-H door,
shallow) and KC-R in `0305` (inside the experiment-wing cluster but BEFORE this door), so vanilla
stays beatable. The contract theory row E was moved out of `Map_DdkPairDoors_RequireBothDiscs`
(exact-array assert) into its own fact. Oracle: `47`/`48` join `gatingKeys` (+`key-47`/`key-48`
probes, 3 rooms each); the minus-E effect is subsumed by Gate C below. Guards:
`Dc1MapContractTests.Map_0304_to_0300_RequiresDdkEPairAndKeyCardsLR`,
`KeyItemPlacerTests.RealInstall_CommunicationRoomChain_ItemIsGoalCritical` (rows 0x47/0x48/0x65/0x6c).

## Gate C — `0205` Communication Room requires reaching `0300` (2026-07-16, user-directed, NO code
## trace, LARGE cascade)

`0205` gets a **room-level** `requiresRoom ["0300"]` — room-level, not edge-level, because the edge
dump found TWO free incoming routes (`0106→0205` free type-0, and `0201→0205` which is three physical
records: type-1 story reader + type-0xff + a plain **type-0 free record**, so the group-9 latch is not
the only way in); a room gate closes all of them uniformly (same pattern as `0101`/`0104`/`0109`).

```
0205.requiresRoom: (none) → ["0300"]
```

**Measured cascade (oracle regenerated, per-probe diff inspected):** `0400→0401` requires
`[0205,0109]` and `0401` transitively gates the entire B2/B3 deep facility + goal `060D` — so `0300`'s
whole access chain becomes goal-critical: the facility elevator (DDK-N pair `0x63/0x6a` — the heliport
alternative is now circular through `0205` — plus F.C. Device `0x41` + `010B`), the cluster entry
(DDK-L pair `0x64/0x6b`), and the `0304→0300` door itself (DDK-E pair + KC-L/R). Probe movements,
each verified room-by-room:

| Probe | Before → after | What moved |
|---|---|---|
| `all` | 96 → 96 | unchanged — vanilla reaches everything |
| `key-2f`, `key-30` | 31→30, 20→19 | lost exactly `0205` (its free reach is gone) |
| `all-minus-ddk-63`/`-6a` (N) | 96 → 41 | −55: `0113` + the whole cluster + `0205,0401`, all of B2/B3 incl. goal `060d` — DDK-N now goal-critical (was tolerated via the heliport) |
| `all-minus-ddk-64`/`-6b` (L) | 89 → 42 | −47: `0205,0401` + B2/B3 incl. `060d` (cluster was already lost pre-change) |
| `all-minus-ddk-65`/`-6c` (E) | 95 → 48 | −47: same deep set (only `0300` was lost pre-change) |

Goal checks: `060D`, `0205`, `0401`, `0300` are absent from every one of those six probes, and from
all-keys-minus-`0x47`/-`0x48` (test-verified; the DDK-band-only oracle has no minus-47/48 probes).
The Entrance-Key invariant is unchanged (minus-`0x2e`: `0205`/`0300` reachable, goal not). The
pre-existing guard `RealInstall_DeepFacility_RequiresTheEntranceKey` carried a now-stale assertion
("goal stays reachable without the DDK-N pair" — the heliport route) — updated to assert the
opposite, with the reason inline. `gen_ap_logic` promoted `0x47/0x48` into the AP progression-items
list. Guards: `Dc1MapContractTests.Map_0205_CommunicationRoom_RequiresExperimentSimulationRoom`,
`KeyItemPlacerTests.RealInstall_CommunicationRoomChain_ItemIsGoalCritical` (9-row theory: E/L/N
pairs, KC-L/R, F.C. Device each individually kill `0300`→`0205`→`0401`→goal).

## ENGINE BUG FIXED — FrontierKeys was blind to a split room's non-primary regions (2026-07-16,
## found by Gate C, CODE fix, not data)

Gate C initially made `ProgressionPass`'s key shuffle fail EVERY seed (`RelocateDdkDiscs` never moved
a disc across 30 seeds; every `Place` attempt "stalled at 42 rooms with 13 keys unplaced"). Bisecting
against HEAD proved the gates triggered it; a step-by-step replication of the fill isolated the bug in
`KeyItemPlacer.FrontierKeys` (`src/DinoRand.Randomizer/Logic/KeyItemPlacer.cs`): it resolved the
masked ROOM codes in `reach` through a **NodeCode-keyed** dictionary, so for a node-split room only
the PRIMARY region's edges were enumerated — a key-gated door owned by a non-primary sub-region (the
real case: the DDK-L door on `0309`'s `shuttle` region, exactly Gate A's door) was invisible to the
frontier, its key was never surfaced as "helpful", and the fill stalled at the point where crossing it
is the only way forward. Pre-Gate-C this blindspot was benign: the cluster wasn't goal-critical, so
the L discs were seated as post-goal filler. Same family as the latch pre-pass bug above — the
algorithm, not the data, silently disagreeing with the split-room model.

**FIXED**: `Reachable` now has an internal `ReachableCore` variant that also returns the reached
**NodeCodes**; `PlaceOnce` passes that set to `FrontierKeys`, which enumerates edges per reached
sub-region node (room-code semantics unchanged for the already-reachable skip and `SatisfiedBy`).
Verified: synthetic regression RED before / GREEN after
(`KeyItemPlacerTests.Place_KeyGatedDoorOnNonPrimaryRegion_IsSurfacedToTheFrontier` — start→`0113`→
`0309.shuttle`→key-gated `050B`); all 8 `RealInstall_RelocateDdkDiscs_*` tests green again including
`ActuallyRelocatesADisc`. No gate was loosened.

## `0305` B1 Key Chip → Key Card R + numbered memo (2026-07-17, user-directed, NO code trace)

Same conservative, user-directed pattern as the `010D` An. Aid entry above (an **item-guard**, not a
door gate). The user's authored note: *"B1 Key Chip — Required to find the Key Card R and a memo at
0305."* `0305` (Library Room) hosts two pickups gated behind the **B1 Key Chip (`0x46`, vanilla `0303`)**:
**Key Card R (`0x48`)** at `2304,2560` (event sub14) and the **numbered memo "B1 Key Chip (3695)"
(`0x5e`)** at `-6400,-4864` (event sub12, census `GetFlag(0,96)`). Neither pickup's census record
by itself witnesses the `0x46` dependency (both are engine/native-armed), so this is authored from the
user's game knowledge, flagged as trace-free for the record.

**AUTHORED**: `0305.items["48"].requires = [70]` and `0305.items["5e"].requires = [70]` (`70` = `0x46`)
— the pickups' `NodeItem.Requires` now demand holding the B1 Key Chip before `KeyItemPlacer` treats
those spots as reachable (consumed identically for a door-key or an opt-in scatter placement).

**Circularity pre-checked** (`KeyItemPlacer.Verify` on the real graph, both with and without the guard):
`Success=True` in both cases — `0x46` @`0303` is collectable before either `0x48` or `0x5e` (both rooms
sit in the experiment wing entered at `0306`; neither `0303` nor `0305` is behind the KC-R-gated
`0304→0300` door), so no cycle and vanilla stays beatable. **Oracle/AP effect: none** — item-guards gate
pickup *collection*, not room reachability, so `0x46` does not enter `gatingKeys` / the AP progression
list (`gen_ap_logic`/`gen_reachability`/`gen_data_reference` `--check` all clean, zero diff), exactly
like the `010D` precedent. Guards (data-contract, like `010D`):
`Dc1MapContractTests.Map_0305_KeyCardR_RequiresB1KeyChip`,
`.Map_0305_NumberedMemo_RequiresB1KeyChip`. The existing `RealInstall_VanillaPlacement_IsBeatable`
now exercises the guard on the live graph.

**Validation (2026-07-17):** full `Dc1MapContract` + `KeyItemPlacer` suite green (106/106, incl. the two
new `Map_0305_*` facts and the install-gated `RealInstall_VanillaPlacement_IsBeatable`, which now
exercises the guard on the live graph); `Dc1ReachabilityOracle` byte-identical (zero diff, confirming
item-guards don't touch the oracle); `gen_ap_logic`/`gen_reachability`/`gen_data_reference` `--check`
clean. (A brief window of concurrent unrelated WIP had the C# build non-compiling mid-session; it cleared
and the suite was run to completion.)
