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

The audit confirms the overlay is **complete** for monotonic placement gates — no missing gate. The
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
