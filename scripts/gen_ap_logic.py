#!/usr/bin/env python3
"""Distil the authored DC1 logic data into a compact Archipelago-world contract.

Reads data/dc1/{map.json,items.json,room-data.json} (authored, no game bytes) and writes
apworld/dino_crisis_1/data/dc1_logic.json — the stable input the .apworld consumes to build
regions/locations/items/rules. See docs/decisions/cross/ARCHIPELAGO-INCREMENT-1-PLAN.md.

    python3 scripts/gen_ap_logic.py --apply    # regenerate the contract (writes + self-checks)
    python3 scripts/gen_ap_logic.py --check    # verify the committed contract is in sync (non-mutating)

No third-party deps. The logic graph is authored in map.json (door edges + `requires`
item-id gates + `requiresRoom` visit gates); per-location pickup data comes from
room-data.json item_control. Item names come from items.json (clean source of truth).
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DC1 = REPO / "data" / "dc1"
OUT = REPO / "apworld" / "dino_crisis_1" / "data" / "dc1_logic.json"

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


def build() -> dict:
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


def check(data: dict) -> None:
    names = {int(k): v for k, v in data["items"]["names"].items()}
    assert data["startRoom"] == "010d", data["startRoom"]
    assert data["goalRoom"] == "060d", data["goalRoom"]
    # A known key gate: some door from room 0112 to 0114 requires the BG Area key (0x30).
    gate = [e for e in data["edges"] if e["from"] == "0112" and e["to"] == "0114"]
    assert gate and 0x30 in gate[0]["requiresItems"], gate
    assert names.get(0x2E) == "Entrance Key", names.get(0x2E)
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


def main(argv: list[str]) -> int:
    data = build()
    expected = _serialize(data)

    if "--apply" in argv:
        OUT.parent.mkdir(parents=True, exist_ok=True)
        OUT.write_text(expected, encoding="utf-8")
        check(data)
        print(f"wrote {OUT.relative_to(REPO)}")
        return 0

    if "--check" in argv:
        # Non-mutating derived-data contract: the committed file must equal a fresh regeneration.
        if not OUT.exists() or OUT.read_text(encoding="utf-8") != expected:
            print(
                f"gen_ap_logic: {OUT.relative_to(REPO)} is stale or missing — "
                "run 'python3 scripts/gen_ap_logic.py --apply' and stage it",
                file=sys.stderr,
            )
            return 1
        check(data)
        return 0

    print("usage: gen_ap_logic.py --apply | --check", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
