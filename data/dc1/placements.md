# Dino Crisis 1 — Item / Enemy / Room Placement Map (vanilla)

Per-room placement oracle distilled from the GameFAQs walkthrough (Stinger_316, v1.0)
saved at [`dinocrisis-gamefaq-items-enemies.md`](../../docs/dc1/reference/dinocrisis-gamefaq-items-enemies.md),
cross-referenced to the code tables in this folder:

- **Room codes** ← [`rooms.json`](rooms.json) (`SSRR`: stage `SS`, room `RR`, hex).
- **Item ids** ← [`items.json`](items.json) (real game ids; supply pool `0x10–0x23`, keys `0x2B–0x6F`).
- **Enemy classes** ← [`enemies.json`](enemies.json) (ids still **provisional**; mapped by class here).

> **Status / confidence.** This is **human-playthrough ground truth**, not a file decode. Its purpose
> is to be the **validation oracle** for the SCD `0x4C` placement-record decode (see
> [`docs/reference/dc1/_registries/STATIC-SCD-RE.md`](../../docs/reference/dc1/_registries/STATIC-SCD-RE.md), 2026-06-19): a decoded `0x4C` record's
> `+0x1A` item id should match the item this map lists for that room. Caveats: (a) a walkthrough lists
> items in *pickup* order and may merge adjacent rooms or omit optional/branch pickups; (b) some items
> are **conditional** (branch choices, puzzle outcomes) — flagged inline; (c) enemy ("Dino") counts are
> only exact in the Operation: Wipe Out section. Treat mismatches as "investigate", not "decoder wrong".

Enemy shorthand used below (per `enemies.json` classes):
- **Raptor** = Velociraptor, `indoor`/`outdoor` class (`0x20–0x22` provisional). "tough/strong Dino" =
  hardened raptor variant (brown/grey).
- **Pteranodon** = `outdoor`-only flyer (`0x24` provisional); FAQ calls it "Pterodactyl".
- **T-Rex** = `scripted` set-piece boss — **never randomized** (unkillable / chase scenes).

---

## Stage 1 — Facility 1F (`01xx`)

| Room | Code | Items (id) | Enemies |
|---|---|---|---|
| Backyard of the Facility | `010D` | — (Gail intro scene) | — |
| Material Storage | `010C` | BG Area Key `0x30`; Resuscitation `0x1F` | — |
| Passageway to the Backup Generator | `010E` / `0114` | corpse → Med Pak M `0x1D` | Raptor ("big lizard", optional, post-generator) |
| Backup Generator Room 1F | `010F` | — (generator puzzle) | — |
| Office Hallway | `010A` | — (laser fence, ventilation) | — |
| Control Room Hall | `0105` | 9mm Parabellum `0x16` | — (save room adjacent) |
| Control Room 1F | `0106` | — (scene hub) | — |
| Management Office *(save)* | `0103` | Shotgun `0x01`; DDK Input Disc H `0x62`; Plug `0x2B`; Panel Key 2 `0x3E`; safe(0426) → Entrance Key `0x2E` + Resuscitation `0x1F` | — |
| Management Office Hall | `0102` | — (laser fence) | Raptor |
| Locker Room | `0100` | Recovery Aid `0x21`; An. Darts M `0x13`; DDK Code Disc H `0x69` | — |
| Main Entrance | `0107` | An. Aid `0x20`; (2F crate) An. Aid `0x20` | — |
| Lecture Room Hallway | `0108` | dead man → Plug `0x2B` | Raptor ×2 (one in adjacent Office) |
| Office | `010B` | — | Raptor |
| Lecture Room | `0109` | BG Room B1 Key `0x2F` | Raptor (Gail-scripted kill) |
| Elevator Hall | `0113` | Plug `0x2B`; Recovery Aid `0x21`; Facility 1F Map | — |
| Front Area of Entrance | `0400`* | DDK Code Disc N `0x6A`; SG Bullets `0x10`; An. Aid `0x20` | — |

\* "Front Area of Entrance" is stage-4 `0400` in `rooms.json` but is reached from 1F early game.

## Stage 2 — Facility 2F (`02xx`)

| Room | Code | Items (id) | Enemies |
|---|---|---|---|
| Hall 2F | `0203` | SG Bullets `0x10`; An. Aid `0x20` | Raptor |
| Lounge | `0204` | safe(8159) → Handgun Slides `0x0E`; Resuscitation `0x1F` | Raptor |
| Chief's Room | `0202` | Panel Key 1 `0x3D`; DDK Input Disc N `0x63`; (red box, keys 1+2) → Key Card L `0x47` | T-Rex (window smash, scripted) |
| Communication Room *(save)* | `0205` | Slag Bullets `0x11`; Antenna Key `0x45` | — |
| Passageway to the Communication Area | `0201` | — | — |
| Communication Antenna Room | `0200` | — (Antenna Key use; scene) | T-Rex (chase, scripted) |

## Stage 3 — Facility B1 (`03xx`)

| Room | Code | Items (id) | Enemies |
|---|---|---|---|
| Medical Room Hallway | `0307` | — | — |
| Medical Room *(save)* | `0308` | Med Pak M `0x1D`; ID Card "Colonel" `0x34`; **Small Size Key boxes (choose one):** L = Resuscitation `0x1F` + Med Pak M `0x1D`, R = Multiplier `0x23` + Med Pak M `0x1D` | — |
| Hall B1 | `0309` / `030E` | An. Aid `0x20` (table) | Raptor (elevator pop-out) |
| Hallway for Carrying in Materials | `030A` / `030D` | — (laser fence) | Raptor |
| Backup Generator Room B1 | `030B` | Startup Battery R `0x3F`→charged `0x40`; Plug `0x2B` | — |
| Carrying Out Room B1 | `030C` | Recovery Aid `0x21`; An. Darts M `0x13` | — (crane puzzle) |
| Main Hallway | `0306` | — (laser fence) | Raptor ×2 |
| Library Room | `0305` | Med Pak M `0x1D`; (B1 Key Chip computer) → Key Card R `0x48`; Handgun Sights `0x0D` | Raptor |
| Research Area Hall | `0301` | An. Aid `0x20` | Raptor ×2 |
| Research Meeting Room | `0302` | DDK Input Disc E `0x65`; Plug `0x2B`; (path) Med Pak M `0x1D` | — |
| Computer Room *(save)* | `0304` | Plug `0x2B`; Screwdriver `0x44` | — |
| Gas Experiment Room | `0303` | Med Pak M `0x1D`; (gas chamber) → B1 Key Chip `0x46`; dead man → Small Size Key `0x2D` | Raptor (post-puzzle) |
| Experiment Simulation Room | `0300` | Shotgun Stock `0x0C`; An. Darts L `0x14` | — (circuit puzzle) |

## Stage 4 — Facility Outdoors (`04xx`)

| Room | Code | Items (id) | Enemies |
|---|---|---|---|
| Toilet | `0101`* | Hemostat `0x1B`; Recovery Aid `0x21` | — |
| Strategy Room | `0104`* | DDK Code Disc E `0x6C`; Plug `0x2B`; F. C. Device `0x41` | — |
| Large Size Elevator Passageway | `0404` | Slag Bullets `0x11`; Resuscitation `0x1F` | Raptor ×2 |
| Large Size Elevator | `0405` | — (scene) | — |
| Large Size Elevator Control Room | `0406` | DDK Input Disc L `0x64`; Tom's body → DDK Code Disc L `0x6B`; Facility Outdoors Map | — |
| Passageway to the Power Room | `0407` | An. Aid `0x20` | **Pteranodon** |
| Elevator Power Room | `0408` | Med Pak M `0x1D`; B1 Crane Card `0x43` | — (panel puzzle) |
| Passageway to the Heliport | `0401` | Hemostat `0x1B` | Raptor ×2 (pop-outs) |
| Hangar | `0402` | Grenade Gun `0x09`; Grenade Bullets `0x18` ×2 | — (crate puzzle) |
| Liaison Elevator No.2 | `0409` | — (scene) | — |
| Underground Passageway to the Facility | `040A` | — | — |
| Materials Room | `040B` | C. O. Pass Card `0x3C`; An. Aid `0x20`; (memo) | — |
| Liaison Elevator No.1 | `040C` | — (scene) | — |

\* Toilet/Strategy Room are stage-1 codes (`0101`/`0104`) reached during the Outdoors leg.
Office ID-card computer: ID Card "Colonel" `0x34` → "Researcher" `0x36` (transform, not a pickup).

## Stage 5 — Facility B2 (`05xx`)

| Room | Code | Items (id) | Enemies |
|---|---|---|---|
| Passageway to the Experiment Area | `0500` | An. Aid `0x20` | Raptor |
| Security Pass Room *(save)* | `0506` | B2 Key Chip 1 `0x56`; DDK Input Disc S `0x67` | — |
| (ventilation off `0500`) | `0500` | An. Darts L `0x14`; Key Card Lv. C `0x38` | Raptor (on Key Card grab) |
| Stabilizer Design Room | `0504` | DDK Code Disc W `0x6D`; *(Rick path)* Protect P. 1-B `0x50` | — |
| Researcher Rest Room | `0505` | Slag Bullets `0x11` | — (Gail scene) |
| Disembarkation Immigration Office *(save)* | `0609`† | B2 Key Chip 2 `0x57`; dead man → Plug `0x2B`; DDK Code Disc S `0x6E` | — |
| Parts Storage | `0507` | Resuscitation `0x1F`; *(Rick path)* Core Parts 1 `0x52` + Core Parts 2 `0x53` + Grenade Gun Parts `0x0F` | Raptors (Rick path, many) |
| Stabilizer Experiment Room | `0503` | DDK Code Disc D `0x6F`; safe(1281) → Shotgun Parts `0x0B`; *(Rick path)* Protect P. 2-B `0x51` | Raptor |
| Passageway | `0508` | An. Aid `0x20` | — |
| Third Energy Area B2 | `0509` | — (bridge switch) | — |
| Third Energy Control Room | `050B` / `0510` | Key Card Lv. B `0x39`; Plug `0x2B` | — |
| Power Freq. Room | `050C` | Facility B2 Map; corpse → Researcher Memo `0x5D` | — (panel puzzle) |
| Passageway to Personal Lab | `050D` | — | — |
| Dr. Kirk's Personal Lab | `050E` | Kirk scene → Key Card Lv. A `0x3A` | — |
| Third Energy Area B3 | `050A` | — (Initializer use) | — |
| Dr. Kirk's Library Room | `050F` | Gail → Pulse Receiver `0x58` | — |
| *(Rick path, Stab. Design)* Protect P. 1-A `0x4E` + Protect P. 2-A `0x4F` | `0504` | (Planning Disc computers) | — |

† "Disembarkation Immigration Office" is a B3 room (`0609`) visited from the B2 leg.
**Planning Disc `0x61`** is granted on the Rick branch (not a fixed room pickup).

## Stage 6 — Facility B3 (`06xx`)

| Room | Code | Items (id) | Enemies |
|---|---|---|---|
| Carrying Out Room B3 | `0600` | — (T-Rex scene → Startup Battery W `0x5A`) | T-Rex (scripted) |
| Backup Generator Room B3 | `0601` | Startup Battery W `0x5A`; Multiplier `0x23`; An. Aid `0x20`; Grenade Bullets `0x18` | — |
| Control Room B3 *(save)* | `0602` | Multiplier `0x23`; B3 Crane Card 1 `0x49`; B3 Crane Card 2 `0x4A`; Plug `0x2B` | — |
| General Weapons Storage | `0605` | (crane puzzle) | Raptor (tough); Raptor ×2 (3rd-ending route) |
| Transport Passageway | `0606` | C. O. Area Key `0x31`; B3 Crane Card 3 `0x4B` | Raptor |
| (post-crane dead man) | `0605`/`0606` | DDK Input Disc W `0x66`; Resuscitation `0x1F` | — |
| Passageway to the Carrying Out Room | `0603` | — | Raptor ×2 |
| Rest Station | `0604` | Facility B3 Map | — |
| Central Stairway | `0608` | — | Raptor ×2 |
| Large Size Elevator | `060F` | dead men → Port Card Key `0x32` + DDK Input Disc D `0x68` + Plug `0x2B` | — |
| Special Weapons Storage | `0607` | *(Gail path)* Stabilizer `0x4C` + Initializer `0x4D` | Raptors (many) |
| Disembarkation Immigration Office *(save)* | `0609` | (see B2 leg) | — |
| Passageway to the Port | `060B` | — | Raptors |
| Port | `060A` | — *(Rick ending: Energy Tank `0x54` use)* | — |
| Hovercraft Storage | `0610` | bullets (final-battle supply) | T-Rex (final, scripted) |
| Underground Heliport | `060D` | — (3rd ending: helicopter / Kirk) | — |
| Port Transport Passageway | `0614` | — | — |
| Heliport Transport Passageway | `0611` | — | — |

---

## Operation: Wipe Out — exact enemy counts (alt room set `07xx`/`08xx`/`09xx`)

Wipe Out reuses B1/B2/B3 geometry under stage codes 7/8/9 and the walkthrough gives **exact raptor
counts** — the cleanest enemy-count oracle in the FAQ.

**Mission 01 — Facility B1 (`07xx`), 10 targets:** Hall B1 `0709` ×2 · Hallway for Carrying in
Materials `070D` ×1 · Main Hallway `0706` ×2 · Library Room `0705` ×2 · Research Area Hall `0701` ×2 ·
Gas Experiment Room `0703` ×1. (Medical Room Hallway `0707`, Research Meeting Room `0702`: none.)

**Mission 02 — Facility B2 (`08xx`), 6 targets:** Passageway to the Experiment Area `0800` ×1 ·
Experiment Room Hall `0802` ×2 · Stabilizer Design Room `0804` ×1 · Researcher Rest Room `0805` ×1 ·
Passageway `0808` ×1. (Security Pass `0806`, Parts Storage `0807`: none.)

**Mission 03 — Facility B3 (`09xx`), 7 targets (all tough variants):** Central Stairway `0908` ×2 ·
Passageway to the Carrying Out Room `0903` ×2 · General Weapons Storage `0905` ×2 · Transport
Passageway `0906` ×1. (Rest Station `0904`, Carrying Out Room B3 `0900`: none.)

---

## Key-item progression (BioRand graph model)

Models the vanilla **gate → key** dependency chain for `Logic/KeyItemPlacer` (DESIGN.md §4: key items
as gates, flood-fill placement so every seed is solvable — same algorithm as BioRand's `ref/classic`).
Today our doctrine treats keys as **flag-driven (`SetFlag`) and placement-stable — not rerolled**; only
the supply pool (`0x10–0x23`) is shuffled. This chain is the logic graph for **if/when** key-item
shuffle is enabled.

**1F/2F gate chain**
- `0x2E` Entrance Key (Mgmt Office safe) → laser/door progression on 1F.
- `0x3D` Panel Key 1 + `0x3E` Panel Key 2 → Chief's Room red box → `0x47` Key Card L.
- `0x34` ID Card "Colonel" → Strategy Room; + `0x41`→`0x42` F. C. Device (fingerprint) → Office computer
  → `0x36` ID Card "Researcher" → unlocks the elevator (gates the Outdoors leg).
- `0x45` Antenna Key → Communication Antenna Room → `0x35` ID Card "Communicator".

**B1 gate chain**
- `0x3F`→`0x40` Startup Battery R (charged) → Backup Generator Room B1 (restores B1 power).
- `0x2F` BG Room B1 Key → B1 access. `0x43` B1 Crane Card → Elevator Power Room / Carrying Out crane.
- `0x46`→`0x5E` B1 Key Chip (numbered 3695) → Library computer → `0x48` Key Card R → Computer Room
  (Gail call) → unlocks deeper B1. `0x44` Screwdriver → circuit boxes.
- `0x2D` Small Size Key → Medical Room supply boxes (optional reward, not a gate).

**B2 gate chain**
- `0x56`/`0x5F` B2 Key Chip 1 + `0x57`/`0x60` B2 Key Chip 2 → Security Pass computer → door + switch gates.
- Key Card ladder `0x37` D → `0x38` C → `0x39` B → `0x3A` A (escalating security doors; `0x3A` Lv. A
  from Kirk gates the Special Weapons Storage / Parts Level-A rooms).
- `0x61` Planning Disc (Rick branch) → Core/Protect parts (`0x52,0x53,0x4E–0x51`, `0x0F`) → assemble
  `0x4C` Stabilizer + `0x4D` Initializer (Gail branch finds them assembled in Special Weapons Storage).

**B3 / endgame gate chain**
- `0x5A` Startup Battery W (Large) → Backup Generator Room B3 (restores B3 power).
- `0x49`/`0x4A`/`0x4B` B3 Crane Cards 1/2/3 → B3 crane puzzles.
- `0x31` C. O. Area Key + `0x3C` C. O. Pass Card + `0x32` Port Card Key → port/heliport access.
- `0x4C` Stabilizer + `0x4D` Initializer → Third Energy (B2↔B3) → required for all endings.
- `0x58` Pulse Receiver → locate Kirk (third ending); `0x54` Energy Tank → Rick-ending hovercraft.

**DDK disc pairs** (each `Input`+`Code` pair solves one door-decode puzzle; gate a specific door):
H `0x62`/`0x69` · N `0x63`/`0x6A` · L `0x64`/`0x6B` · E `0x65`/`0x6C` · W `0x66`/`0x6D` ·
S `0x67`/`0x6E` · D `0x68`/`0x6F`.

**Plug `0x2B`** is a repeated consumable-style key (many copies across rooms) used to power panels — not
a single-instance gate; safe to treat as a non-progression key.
