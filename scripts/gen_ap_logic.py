#!/usr/bin/env python3
"""Distil the authored per-game logic data into compact Archipelago-world contracts.

Each supported game distils into apworld/<pkg>/data/<game>_logic.json — the stable input the
.apworld consumes to build regions/locations/items/rules. Shared schema + structural checks live
here (repo-side); the shipped apworlds stay self-contained (AP zips can't share runtime code).
See docs/decisions/cross/ARCHIPELAGO-INCREMENT-1-PLAN.md and ARCHIPELAGO-DC2-STUB-PLAN.md.

    python3 scripts/gen_ap_logic.py            --check    # every game (used by pre-commit + CI)
    python3 scripts/gen_ap_logic.py            --apply    # regenerate every game's contract
    python3 scripts/gen_ap_logic.py dc2        --apply    # one game only

No third-party deps.

DC1: the logic graph is authored in map.json (door edges + `requires` item-id gates +
`requiresRoom` visit gates); per-location pickup data comes from room-data.json item_control;
item names from items.json (clean source of truth).

DC2: intentionally an EMPTY stub — data/dc2 has a decoded door-graph + item spots but no key-item
ids or door→key gating yet, so there is no logically-gated graph to distil. The contract carries
the full schema with empty locations/items/edges so enabling DC2 later is populating data (a DC2
builder), not rewriting the world. See the DC2 stub decision record.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DC1 = REPO / "data" / "dc1"

EMPTY_SLOT_ID = 0xFF  # DC1 empty item slot sentinel


def _code(s: str) -> str:
    """Canonicalise a room code to lowercase 4-hex (map.json mixes '030C' and '010d')."""
    return f"{int(str(s), 16):04x}"


def _item_id(s) -> int:
    """Normalise an item id: '0x1'/'0x01' hex-string or a bare int -> int."""
    if isinstance(s, int):
        return s
    return int(str(s), 16)


def _clean(name: str) -> str:
    """items.json doubles inner quotes (Shotgun \"\"Model PA3\"\"); collapse them."""
    return name.replace('""', '"').strip()


def load_items() -> dict:
    raw = json.loads((DC1 / "items.json").read_text(encoding="utf-8"))
    names: dict[int, str] = {}
    for entry in raw["allItems"] + raw["keyItems"]:
        names[_item_id(entry["id"])] = _clean(entry["name"])
    key_ids = sorted(_item_id(e["id"]) for e in raw["keyItems"])
    pool = [
        {"id": _item_id(e["id"]), "name": _clean(e["name"]), "weight": float(e["weight"])}
        for e in raw["pool"]
    ]
    return {"names": names, "keyItemIds": key_ids, "pool": pool}


def load_map() -> dict:
    raw = json.loads((DC1 / "map.json").read_text(encoding="utf-8"))
    regions: dict[str, str] = {}
    edges: list[dict] = []
    for code, room in raw["rooms"].items():
        c = _code(code)
        regions[c] = room.get("name", c)
        for target, door in (room.get("doors") or {}).items():
            tc = _code(target)
            # Drop self-referential visit gates (a door to X gated on visiting X is vacuous here).
            req_rooms = [_code(r) for r in (door.get("requiresRoom") or []) if _code(r) != tc]
            edges.append(
                {
                    "from": c,
                    "to": tc,
                    "requiresItems": [_item_id(i) for i in (door.get("requires") or [])],
                    "requiresRooms": req_rooms,
                }
            )
    # Ensure every referenced room exists as a region (targets/gates may point outside the keyset).
    for e in edges:
        for c in [e["to"], *e["requiresRooms"]]:
            regions.setdefault(c, c)
    start = _code(raw["beginEnd"]["start"])
    end = _code(raw["beginEnd"]["end"])
    regions.setdefault(start, start)
    regions.setdefault(end, end)
    return {"regions": regions, "edges": edges, "startRoom": start, "goalRoom": end}


def load_locations(item_names: dict[int, str]) -> list[dict]:
    raw = json.loads((DC1 / "room-data.json").read_text(encoding="utf-8"))
    rooms = raw["rooms"]
    room_name = {_code(c): r.get("name", c) for c, r in rooms.items()}
    locs: list[dict] = []
    seen: set[str] = set()  # collapse relocation-twins (same item+pos = one logical pickup)
    for code, room in rooms.items():
        ic = room.get("item_control")
        if not ic:
            continue
        c = _code(code)
        for rec in ic["records"]:
            iid = _item_id(rec["item_id"])
            if iid == EMPTY_SLOT_ID:
                continue
            pos = rec.get("pos") or [0, 0]
            x, y = int(pos[0]), int(pos[1])
            key = f"{c}:{iid:02x}:{x},{y}"
            if key in seen:
                continue
            seen.add(key)
            iname = item_names.get(iid, _clean(rec.get("item_name", f"0x{iid:02x}")))
            locs.append(
                {
                    "key": key,
                    "name": f"{room_name.get(c, c)} ({c}) - {iname} @{x},{y}",
                    "room": c,
                    "itemId": iid,
                    "itemName": iname,
                    "pos": [x, y],
                    "collectedFlag": rec.get("collected_flag"),
                }
            )
    return locs


def build_dc1() -> dict:
    items = load_items()
    m = load_map()
    locs = load_locations(items["names"])
    progression = sorted({i for e in m["edges"] for i in e["requiresItems"]})
    return {
        "_generated_by": "scripts/gen_ap_logic.py",
        "_source": "data/dc1/{map,items,room-data}.json (authored)",
        "version": 1,
        "startRoom": m["startRoom"],
        "goalRoom": m["goalRoom"],
        "regions": m["regions"],
        "edges": m["edges"],
        "locations": locs,
        "items": {
            "names": {str(i): n for i, n in sorted(items["names"].items())},
            "keyItemIds": items["keyItemIds"],
            "progressionItemIds": progression,
            "pool": items["pool"],
        },
    }


DC2 = REPO / "data" / "dc2"

# op-0x31 field-pickup type names (data/dc2/items.json _field_pickup_types; K75/K77 —
# this is the SEPARATE field-type space, NOT the catalog id space).
DC2_FIELD_TYPES = {
    1: "Med Pak S", 2: "Resusc. Pak", 3: "Field item 3", 4: "Field item 4",
    5: "Dino File", 6: "Field item 6", 7: "Ammo pickup", 8: "Field item 8",
    9: "Field item 9",
}


def _dc2_unlock_rooms(gate: dict, gating: dict, dest: str) -> list[list[str]]:
    """OR-alternatives of room sets that satisfy one locked door's unlock flag(8,K).

    Each unlock source (K78: an op-0x3a key_use record, or a script SetFlag(8,K,1) routine)
    yields one alternative = {source room} ∪ the transitive rooms of its guard-flag setters.
    Sources in the door's DEST room are dropped (can't have been used before first entry)."""
    rooms = gating["rooms"]

    def flag_setter_rooms(grp: int, fid: int, seen: set) -> set[str]:
        """Rooms whose visit is REQUIRED for flag(grp,fid): only meaningful when the flag
        has exactly ONE setting room — several setters mean OR, which an AND-semantics
        requiresRooms list cannot express, so those are treated as satisfiable-open
        (the per-source OR-alternative edges carry the real disjunction)."""
        key = (grp, fid)
        if key in seen:
            return set()
        seen.add(key)
        setters = [
            (st, f) for st, rfacts in rooms.items() for f in rfacts
            if any((g, i, v) == (grp, fid, 1) for g, i, v in f["sets"])
        ]
        if len({st for st, _ in setters}) != 1:
            return set()
        st, f = setters[0]
        out = {st}
        for tg, ti in f["tests"]:
            out |= flag_setter_rooms(tg, ti, seen)
        return out

    alts: list[list[str]] = []
    for src in gate["unlocked_by"]:
        if src["room"] == dest:
            continue
        req = {src["room"]}
        for tg, ti in src.get("routine_guards", []):
            req |= flag_setter_rooms(tg, ti, set())
        alts.append(sorted(req))
    return alts


def build_dc2() -> dict:
    """Populated DC2 contract (ITEMS-KEYITEMS-RE-PLAN Phase 4) from the byte-grounded
    data/dc2 artifacts: door-graph.json (op-0x30 doors + K78 gate fields), door-gating.json
    (flag-dataflow facts + resolved unlock sources) and item-placements.json (165 op-0x31
    field pickups). Star topology + gated-edge overlay, same model as DC1: only gated
    targets' edges are emitted; every other room hangs off Menu.

    v1 scope (until the Phase-2 give-path decode): progression is ROOM-VISIT gates only
    (requiresRooms); requiresItems stays empty because key items are not yet relocatable —
    DC2 progression is flag-driven (K78) and the field-pickup lever is unverified (K77)."""
    doors = json.loads((DC2 / "door-graph.json").read_text(encoding="utf-8"))["rooms"]
    gating = json.loads((DC2 / "door-gating.json").read_text(encoding="utf-8"))
    placements = json.loads((DC2 / "item-placements.json").read_text(encoding="utf-8"))["rooms"]
    room_data = json.loads((DC2 / "room-data.json").read_text(encoding="utf-8"))

    regions = {st: room_data.get(st, {}).get("zone", st) for st in doors}

    # Gated edges: one edge per unlock ALTERNATIVE (multiple edges same from->to = OR).
    edges: list[dict] = []
    gated_targets: set[str] = set()
    for st, entry in doors.items():
        for dr in entry["doors"]:
            if "lock_index" not in dr or dr["dest_id"] not in regions:
                continue
            dest = dr["dest_id"]
            gate = gating["locked_doors"].get(f"{st}->{dest}")
            alts = _dc2_unlock_rooms(gate, gating, dest) if gate else []
            gated_targets.add(dest)
            for req in (alts or [[]]):
                edges.append({
                    "from": st, "to": dest, "requiresItems": [],
                    # a requirement equal to the edge's own source room is vacuous
                    "requiresRooms": [r for r in req if r not in (st, dest)],
                })
    # Overlay: UNLOCKED doors into gated-target rooms (so an alternate free path in the
    # real graph is not modelled as locked).
    for st, entry in doors.items():
        for dr in entry["doors"]:
            if "lock_index" in dr or dr["dest_id"] not in gated_targets:
                continue
            edges.append({"from": st, "to": dr["dest_id"],
                          "requiresItems": [], "requiresRooms": []})

    locs = []
    for st_room, items in placements.items():
        code = st_room[2:]  # "ST304" -> "304"
        zone = regions.get(code, code)
        for it in items:
            tname = DC2_FIELD_TYPES.get(it["id"], f"Field item {it['id']}")
            locs.append({
                "key": f"{code}:{it['slot']}:{it['id_blob_off']}",
                "name": f"{zone} (ST{code}) - {tname} @slot{it['slot']}/0x{it['id_blob_off']:x}",
                "room": code,
                "itemId": it["id"],
                "itemName": tname,
                "pos": [it["id_blob_off"], 0],
                "collectedFlag": None,
            })

    from collections import Counter
    hist = Counter(loc["itemId"] for loc in locs)
    pool = [{"id": i, "name": DC2_FIELD_TYPES[i], "weight": float(n)}
            for i, n in sorted(hist.items())]
    return {
        "_generated_by": "scripts/gen_ap_logic.py",
        "_source": "data/dc2/{door-graph,door-gating,item-placements,room-data}.json (byte-grounded, "
        "K74-K78). v1: visit-gated edges only; key items are flag-driven and not yet relocatable "
        "(requiresItems empty until the Phase-2 give-path decode) — see "
        "docs/decisions/dc2/ITEMS-KEYITEMS-RE-PLAN.md.",
        "version": 1,
        "startRoom": "000",
        "goalRoom": "904",
        "regions": regions,
        "edges": edges,
        "locations": locs,
        "items": {
            "names": {str(i): DC2_FIELD_TYPES[i] for i in sorted(hist)},
            "keyItemIds": [],
            "progressionItemIds": [],
            "pool": pool,
        },
    }


def _reachable_rooms(data: dict, held: set[int]) -> set[str]:
    """Simulate the apworld's region model (star topology + gated-edge overlay) to a fixpoint.

    Every room that is NOT the target of a gated edge is freely reachable from the origin.
    Gated-target rooms open only through their edge once its items/visit-gates are satisfied.
    Mirrors DinoCrisis1World.create_regions/set_rules so `--check` proves the graph is solvable
    without an Archipelago checkout.
    """
    gated_targets = {e["to"] for e in data["edges"]}
    reach = {c for c in data["regions"] if c not in gated_targets}
    changed = True
    while changed:
        changed = False
        for e in data["edges"]:
            if e["to"] in reach or e["from"] not in reach:
                continue
            if all(i in held for i in e["requiresItems"]) and all(
                r in reach for r in e["requiresRooms"]
            ):
                reach.add(e["to"])
                changed = True
    return reach


def _forward_fill_beatable(data: dict) -> bool:
    """Simulate AP's assumed-forward-fill: place each progression key into a currently-reachable,
    still-free location, re-flooding after each placement. Succeeds iff no key is stranded behind
    its own gate and the goal is reachable once all keys are placed.
    """
    remaining = list(data["items"]["progressionItemIds"])
    held: set[int] = set()
    used: set[str] = set()
    while remaining:
        reach = _reachable_rooms(data, held)
        slot = next(
            (loc for loc in data["locations"] if loc["room"] in reach and loc["key"] not in used),
            None,
        )
        if slot is None:
            return False
        used.add(slot["key"])
        held.add(remaining.pop())
    return data["goalRoom"] in _reachable_rooms(data, held)


def _check_common(data: dict) -> dict:
    """Schema/structural invariants that hold for every game (vacuous on an empty stub).
    Returns the int-keyed item-name map for the caller."""
    for field in ("startRoom", "goalRoom", "regions", "edges", "locations"):
        assert field in data, f"missing contract field: {field}"
    names = {int(k): v for k, v in data["items"]["names"].items()}
    # Every referenced item id resolves to a name.
    for e in data["edges"]:
        for i in e["requiresItems"]:
            assert i in names, f"edge requires unknown item 0x{i:02x}"
    for loc in data["locations"]:
        assert loc["itemId"] in names, loc
    # Location keys and display names are unique (AP needs stable, unique location names).
    keys = [loc["key"] for loc in data["locations"]]
    assert len(keys) == len(set(keys)), "duplicate location keys"
    disp = [loc["name"] for loc in data["locations"]]
    assert len(disp) == len(set(disp)), "duplicate location display names"
    # Every edge endpoint is a known region.
    for e in data["edges"]:
        assert e["from"] in data["regions"] and e["to"] in data["regions"], e
    return names


def check_dc2(data: dict) -> None:
    names = _check_common(data)
    assert data["startRoom"] == "000" and data["goalRoom"] == "904", (
        data["startRoom"], data["goalRoom"])
    assert len(data["regions"]) == 89, len(data["regions"])
    # 168 since the 2026-07-10 slot-5 routine-directory fix (decode_script.subprograms):
    # +3 op-0x31 items recovered from previously capped tail routines (ST003, ST402 x2).
    assert len(data["locations"]) == 168, len(data["locations"])
    assert len(names) == 9, names  # the op-0x31 field-type space (K77)
    # Known K78 gates present: the ST101<->ST102 crank pair and the ST302->ST303 keycard door.
    assert any(e["from"] == "101" and e["to"] == "102" for e in data["edges"])
    assert any(e["from"] == "302" and e["to"] == "303" for e in data["edges"])
    # v1 progression = visit gates only; solvability: everything (incl. the goal and every
    # location's room) must be reachable with NO items.
    reach = _reachable_rooms(data, set())
    assert data["goalRoom"] in reach, "goal unreachable"
    unreachable = {loc["room"] for loc in data["locations"]} - reach
    assert not unreachable, f"locations in unreachable rooms: {sorted(unreachable)}"
    print(
        f"OK: {len(data['regions'])} regions, {len(data['edges'])} edges "
        f"({sum(1 for e in data['edges'] if e['requiresRooms'])} with visit-gates), "
        f"{len(data['locations'])} locations, {len(names)} field-type names, "
        f"goal {data['goalRoom']} reachable (visit-gated model, no key-item shuffle yet)"
    )


def check_dc1(data: dict) -> None:
    names = _check_common(data)
    assert data["startRoom"] == "010d", data["startRoom"]
    assert data["goalRoom"] == "060d", data["goalRoom"]
    # A known key gate: some door from room 0112 to 0114 requires the BG Area key (0x30).
    gate = [e for e in data["edges"] if e["from"] == "0112" and e["to"] == "0114"]
    assert gate and 0x30 in gate[0]["requiresItems"], gate
    assert names.get(0x2E) == "Entrance Key", names.get(0x2E)
    # Solvability: holding all progression items, every location's room + the goal are reachable;
    # and with no items there are already reachable locations to seed the fill.
    prog = set(data["items"]["progressionItemIds"])
    reach_all = _reachable_rooms(data, prog)
    assert data["goalRoom"] in reach_all, "goal unreachable even with all keys"
    loc_rooms = {loc["room"] for loc in data["locations"]}
    unreachable = loc_rooms - reach_all
    assert not unreachable, f"locations in unreachable rooms: {sorted(unreachable)}"
    reach_none = _reachable_rooms(data, set())
    assert any(loc["room"] in reach_none for loc in data["locations"]), "no free locations to seed fill"
    # Gate bites: the goal must NOT be reachable without Key Card Lv. A (0x3a), and holding it
    # (alone) must open the goal. A test that only ever passes with all keys proves nothing.
    assert data["goalRoom"] not in _reachable_rooms(data, prog - {0x3A}), "goal reachable without Key Card"
    assert data["goalRoom"] in _reachable_rooms(data, {0x3A}), "Key Card alone does not open the goal"
    # Beatability: a forward fill can place every progression key into an already-reachable spot
    # (i.e. no key is locked behind its own gate) and the goal is then reachable.
    assert _forward_fill_beatable(data), "no beatable placement of progression keys exists"
    print(
        f"  solvable: {len(reach_none)} rooms free, {len(reach_all)} with all keys, "
        f"goal {data['goalRoom']} gated by Key Card, forward-fill beatable"
    )
    print(
        f"OK: {len(data['regions'])} regions, {len(data['edges'])} edges, "
        f"{len(data['locations'])} locations, {len(names)} item names, "
        f"progression items {[hex(i) for i in data['items']['progressionItemIds']]}"
    )


def _serialize(data: dict) -> str:
    return json.dumps(data, indent=2) + "\n"


# game -> (apworld package dir, contract filename, builder, checker)
GAMES: dict[str, tuple[str, str, "callable", "callable"]] = {
    "dc1": ("dino_crisis_1", "dc1_logic.json", build_dc1, check_dc1),
    "dc2": ("dino_crisis_2", "dc2_logic.json", build_dc2, check_dc2),
}


def _out(pkg: str, fname: str) -> Path:
    return REPO / "apworld" / pkg / "data" / fname


def _run_game(game: str, apply: bool) -> int:
    pkg, fname, build, check = GAMES[game]
    out = _out(pkg, fname)
    data = build()
    expected = _serialize(data)
    print(f"[{game}]")
    if apply:
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(expected, encoding="utf-8")
        check(data)
        print(f"wrote {out.relative_to(REPO)}")
        return 0
    # Non-mutating derived-data contract: the committed file must equal a fresh regeneration.
    if not out.exists() or out.read_text(encoding="utf-8") != expected:
        print(
            f"gen_ap_logic: {out.relative_to(REPO)} is stale or missing — "
            f"run 'python3 scripts/gen_ap_logic.py {game} --apply' and stage it",
            file=sys.stderr,
        )
        return 1
    check(data)
    return 0


def main(argv: list[str]) -> int:
    apply = "--apply" in argv
    if not apply and "--check" not in argv:
        print("usage: gen_ap_logic.py [dc1|dc2] --apply | --check", file=sys.stderr)
        return 2
    games = [a for a in argv if not a.startswith("-")]
    for g in games:
        if g not in GAMES:
            print(f"unknown game '{g}' (known: {', '.join(GAMES)})", file=sys.stderr)
            return 2
    return max(_run_game(g, apply) for g in (games or list(GAMES)))


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
