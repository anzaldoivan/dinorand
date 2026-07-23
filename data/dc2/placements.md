# Dino Crisis 2 — Item / Enemy / Dino-File Placement Map (guide-sourced, PARTIAL)

Per-area placement oracle derived from contributor research; no third-party guide text is
reproduced here.

> **Status / confidence — read first.** This is **human-playthrough, single-source** data
> (one FAQ). It is **NOT** a byte decode. Unlike DC1's `placements.md` (which validates a
> decoded SCD `0x4C` record), DC2 has **no** decoded per-room item/enemy record yet — the
> in-room spawn/item/door tables live in the undecoded room blob. So this file cannot yet be
> cross-checked against bytes; treat every row as `single-source`.
>
> **Granularity gap.** Placements are keyed by the FAQ's **area names**, not by `ST*.DAT`
> ids — the two are not yet linkable (the FAQ uses its own map-grid coordinates). See
> the area progression notes and [`README.md`](README.md) for the gap.
>
> **Coverage is partial.** The FAQ brackets only *some* pickups; many items/enemies are
> described in prose without markup and are not captured here. Enemy spawns are described
> only qualitatively ("more dinos", "Oviraptors and Compys") — no per-area counts exist.

## Dino File collectibles (10 of the FAQ-stated 11 located) — `single-source`

| Dino File | Found in area |
|---|---|
| Velociraptor | Water Tower |
| Allosaurus | Dock - Hanging Bridge |
| Compsognathus | Research Facility - Lounge |
| Pteranodon | 3rd Energy Facility - Goods Storage |
| Mosasaurus | 3rd Energy Facility - Control Room |
| Plesiosaurus | 3rd Energy Reactor - Machine Coolant System Passageway |
| Inostrancevia | City Outskirts - Goods Passage |
| Triceratops | Command Center - Exterior |
| Oviraptor | City - Living Area 1 |
| Giganotosaurus | Missile Silo - Data Control Room |

> The FAQ states there are **11** Dino Files; only the 10 above are bracketed in the text.
> The 11th (likely T-Rex or Super Raptor) is **placeholder** — not located in this source.

## Key-item / weapon pickups (bracketed in FAQ) — `single-source`, partial

| Area | Pickups |
|---|---|
| Military Facility - Infirmary | Keyplate |
| Military Facility - Machine Storage | Research Facility Keycard, Keyplate |
| Military Facility - Control Room | Blue Keyplate |
| Research Facility - Security Control Room | Flame Launcher, Fire Wall |
| Research Facility - Lounge | Inner Suit |
| Research Facility - Laboratory | Battery |
| 3rd Energy Facility - Pathway 3 | Complete Recovery (in Box-Key locked box) |
| 3rd Energy Facility - Control Room | Box Key |
| 3rd Energy Facility - Goods Storage | ID Card |
| 3rd Energy Reactor (various) | Shutter Control Plug, Diver Suit |
| City Outskirts / City | City Keycard, Living Area Key |
| Missile Silo | Signal Shooter, Anti-Tank Rifle, Aqua Grenade |

> Rows beyond the strictly-bracketed set are summarised from walkthrough prose and are the
> least reliable; refine against a second FAQ before relying on them.

## Enemy population (qualitative only) — `single-source`

The FAQ gives no per-area enemy counts (except the Extra Crisis Colosseum, which is not a
story area). Roster-by-zone, as described in prose:

- **Jungle / Military Facility:** Velociraptor, Oviraptor, Compsognathus (Compys).
- **Research Facility:** Velociraptor, Oviraptor, Pteranodon.
- **3rd Energy Facility / Reactor (underwater):** Plesiosaurus, Mosasaurus, Inostrancevia.
- **Edward City:** Allosaurus, Inostrancevia, Velociraptor; Triceratops set-piece.
- **Missile Silo / finale:** Giganotosaurus (final boss), T-Rex set-pieces.
