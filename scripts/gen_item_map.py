"""Generate map.json `itemPriorities` from the decoded item_control layer.

A naive item randomizer that shuffles item ids in place is unsafe over the flag-gated item
placements (docs/dc1/TRIGGER-DECODE.md "Item gate layer"): logical deadlock, relocation-twin
desync/duplication, block-grant breakage, no-op `0xff` runtime-armed slots. BioRand (ref/classic)
mitigates this with per-item `Priority=Fixed` + `Requires`/`Link` in its logic Map.

DinoRand's ItemRandomizer already honours `ItemPriority.Fixed` (src/.../Passes/ItemRandomizer.cs:
"Fixed-priority pickups stay exactly vanilla"), matched by the placement-quad first corner via the
`itemPriorities: [{ "at": "X,Z", "priority": "Fixed" }]` overlay (src/.../Maps/MapRequirements.cs).
It has no item `link`/`condition`/flag-`requires` consumer yet, so the safe, consumable first cut is
to PIN every hazardous item `Fixed` (strictly safer than today's blind shuffle).

This reads ONLY data/dc1/room-data.json (item_control + per-record `pos`) and writes the
`itemPriorities` array of each item_control room in data/dc1/map.json. No game .dat needed → runs in
CI. Downstream of tools/scd_re/item_catalog.py (which decodes item_control from the .dat).

Hazard policy (a record is pinned Fixed iff ANY apply):
  - item_id == 0xff            (runtime-armed slot; shuffle is a no-op / native fill)
  - gate is a flag test_branch (conditionally placed → reachability/deadlock risk)
  - trigger.kind == unresolved  (may never appear; placing a needed item here softlocks)
  - relocation twin            (same non-0xff id in >1 record of the room → desync/dup risk)

Usage:
  python scripts/gen_item_map.py            # report the pins (dry run)
  python scripts/gen_item_map.py --apply    # write itemPriorities into map.json
  python scripts/gen_item_map.py --check     # CI/pre-commit gate: assert sync + integrity
"""
import sys, os, json

_ROOT = os.path.join(os.path.dirname(__file__), "..", "data", "dc1")
ROOMDATA_FP = os.path.join(_ROOT, "room-data.json")
MAP_FP = os.path.join(_ROOT, "map.json")

# Key items 0x2b..0x6f gate doors by possession and are owned by KeyItemPlacer (never id-rerolled by
# the item pass), so their gate is progression-relevant. Ids outside the range (consumables/ammo and
# weapon-part upgrades) are safe to reroll in place. Mirrors GameDefinition.KeyItemIds.
KEY_LO, KEY_HI = 0x2B, 0x6F


def is_key_item(iid):
    return iid != "0xff" and KEY_LO <= int(iid, 16) <= KEY_HI


def hazard_reasons(rec, twin_ids):
    """Return the list of hazard reasons that pin this item record Fixed (empty = shuffleable).

    The only relaxation vs. the original blanket policy is the flag gate: a NON-key item gated by a
    story/event flag (group != 11) is no longer pinned — its gate controls only WHEN it appears, not
    progression, so it is safe to reroll in place. Runtime-armed `0xff`, unresolved triggers, key-item
    or group-11 (inventory-possession) gates, and relocation twins still pin Fixed. Twins are also
    bound by `itemLinks` (room_links) so every copy shares one assignment — redundant with the Fixed
    pin for the item pass (which skips key items), but the binding the key shuffle needs to stay in
    sync."""
    reasons = []
    iid = rec.get("item_id")
    if iid == "0xff":
        reasons.append("runtime-armed")
    gate = rec.get("gate") or {}
    if gate.get("type") == "test_branch" and gate.get("flag"):
        fl = gate["flag"]
        # Keep Fixed only for key items (progression) or inventory-possession flags (group 11, e.g. the
        # 11:10 escape-loadout block grant). A non-key event-flag gate is timing-only → shuffleable:
        # the slot's gate is untouched by an id reroll and the collected flag (group 8, bit=id) self-keys.
        if is_key_item(iid) or fl["group"] == 11:
            reasons.append(f"flag-gated {fl['group']}:{fl['index']}")
    if (rec.get("trigger") or {}).get("kind") == "unresolved":
        reasons.append("unresolved-trigger")
    if iid != "0xff" and iid in twin_ids:
        reasons.append("relocation-twin")
    return reasons


def room_pins(records):
    """Compute the itemPriorities entries for one room's item_control records.
    Keyed by position (the C# match key); reasons merged on collision."""
    counts = {}
    for r in records:
        iid = r.get("item_id")
        if iid != "0xff":
            counts[iid] = counts.get(iid, 0) + 1
    twin_ids = {iid for iid, n in counts.items() if n > 1}

    by_pos = {}
    for r in records:
        reasons = hazard_reasons(r, twin_ids)
        if not reasons:
            continue
        pos = r.get("pos")
        if not pos or len(pos) != 2:
            continue
        key = (int(pos[0]), int(pos[1]))
        entry = by_pos.setdefault(key, {"items": set(), "reasons": set()})
        entry["items"].add(r.get("item_name") or r.get("item_id"))
        entry["reasons"].update(reasons)

    pins = []
    for (x, z), e in sorted(by_pos.items()):
        pins.append({
            "at": f"{x},{z}",
            "priority": "Fixed",
            "_why": "; ".join(sorted(e["reasons"])) + " — " + ", ".join(sorted(e["items"])),
        })
    return pins


def room_links(records):
    """Compute the `itemLinks` entries for one room: the hex ids (no `0x`) that occur in more than one
    record (relocation twins). Every record of a linked id shares one assignment downstream, so a
    key-item twin can't desync under the key shuffle and a non-key twin can't duplicate. Returns a
    sorted list of `{ "id": "46", "_why": "..." }` (sorted by id for a stable diff)."""
    counts = {}
    names = {}
    for r in records:
        iid = r.get("item_id")
        if iid == "0xff":
            continue
        counts[iid] = counts.get(iid, 0) + 1
        names.setdefault(iid, r.get("item_name") or iid)
    links = []
    for iid in sorted((i for i, n in counts.items() if n > 1), key=lambda s: int(s, 16)):
        links.append({
            "id": f"{int(iid, 16):02x}",
            "_why": f"relocation-twin ×{counts[iid]} — {names[iid]}",
        })
    return links


def compute(doc):
    """room_id -> itemPriorities list, for every item_control room with >=1 pin."""
    out = {}
    for rid, room in doc["rooms"].items():
        ic = room.get("item_control")
        if not ic:
            continue
        pins = room_pins(ic.get("records", []))
        if pins:
            out[rid] = pins
    return out


def compute_links(doc):
    """room_id -> itemLinks list, for every item_control room with >=1 relocation twin."""
    out = {}
    for rid, room in doc["rooms"].items():
        ic = room.get("item_control")
        if not ic:
            continue
        links = room_links(ic.get("records", []))
        if links:
            out[rid] = links
    return out


def regenerate_map(roomdoc, mapdoc):
    """Return a NEW map doc with itemPriorities + itemLinks set on every in-scope item_control room.
    Pure function. Rooms with item_control but not in map.json are reported, not invented."""
    pins = compute(roomdoc)
    links = compute_links(roomdoc)
    out = json.loads(json.dumps(mapdoc))  # deep copy
    rooms = out["rooms"]
    missing = []
    # set/refresh pins+links for in-scope rooms; clear our keys on item_control rooms with none
    item_rooms = {rid.lower() for rid, r in roomdoc["rooms"].items() if r.get("item_control")}
    for rid in sorted(item_rooms):
        mkey = rid.lower()
        if mkey not in rooms:
            if rid in pins or rid in links:
                missing.append(rid)
            continue
        if rid in pins:
            rooms[mkey]["itemPriorities"] = pins[rid]
        else:
            rooms[mkey].pop("itemPriorities", None)
        if rid in links:
            rooms[mkey]["itemLinks"] = links[rid]
        else:
            rooms[mkey].pop("itemLinks", None)
    return out, missing


def serialize_map(doc):
    # match the existing map.json writer (tools/scd_re/extract_logic.py): ensure_ascii=True + \n
    return json.dumps(doc, indent=2, ensure_ascii=True) + "\n"


def cmd_apply():
    roomdoc = json.load(open(ROOMDATA_FP, encoding="utf-8"))
    mapdoc = json.load(open(MAP_FP, encoding="utf-8"))
    new, missing = regenerate_map(roomdoc, mapdoc)
    text = serialize_map(new)
    json.loads(text)
    open(MAP_FP, "w", encoding="utf-8", newline="\n").write(text)
    pins, links = compute(roomdoc), compute_links(roomdoc)
    npins = sum(len(v) for v in pins.values())
    nlinks = sum(len(v) for v in links.values())
    print(f"applied: {npins} Fixed item pins across {len(pins)} rooms; "
          f"{nlinks} itemLinks across {len(links)} rooms; map.json valid")
    if missing:
        print(f"  NOTE: {len(missing)} item_control room(s) not in map.json (out of door-rando scope): {missing}")


def integrity_violations(roomdoc, mapdoc):
    v = []
    pins = compute(roomdoc)
    links = compute_links(roomdoc)
    rooms = mapdoc["rooms"]
    # every computed pin present in map.json with priority Fixed
    for rid, plist in pins.items():
        mkey = rid.lower()
        if mkey not in rooms:
            continue  # out-of-scope, reported by --apply
        have = {(e.get("at"), e.get("priority")) for e in rooms[mkey].get("itemPriorities", [])}
        for p in plist:
            if (p["at"], "Fixed") not in have:
                v.append(f"COVERAGE: {rid} missing Fixed pin at {p['at']} ({p['_why']})")
    # every computed relocation-twin link present in map.json
    for rid, llist in links.items():
        mkey = rid.lower()
        if mkey not in rooms:
            continue
        have = {e.get("id") for e in rooms[mkey].get("itemLinks", [])}
        for l in llist:
            if l["id"] not in have:
                v.append(f"COVERAGE: {rid} missing itemLink for id {l['id']} ({l['_why']})")
    # no spurious itemPriorities / itemLinks in an item_control room (every entry must be computed)
    item_rooms = {rid.lower() for rid, r in roomdoc["rooms"].items() if r.get("item_control")}
    for rid in item_rooms:
        if rid not in rooms:
            continue
        # pins/links are keyed by the original room-id casing; normalise to the lowercase map key
        want_at = {p["at"] for p in pins.get(_orig_key(pins, rid), [])}
        for e in rooms[rid].get("itemPriorities", []):
            if e.get("at") not in want_at:
                v.append(f"SPURIOUS: {rid} has itemPriorities at {e.get('at')} with no item_control hazard")
        want_id = {l["id"] for l in links.get(_orig_key(links, rid), [])}
        for e in rooms[rid].get("itemLinks", []):
            if e.get("id") not in want_id:
                v.append(f"SPURIOUS: {rid} has itemLink for id {e.get('id')} that is not a relocation twin")
    return v


def _orig_key(pins, lower_rid):
    for k in pins:
        if k.lower() == lower_rid:
            return k
    return lower_rid


def cmd_check():
    on_disk = open(MAP_FP, encoding="utf-8").read()
    roomdoc = json.load(open(ROOMDATA_FP, encoding="utf-8"))
    mapdoc = json.loads(on_disk)
    fails = []
    new, _ = regenerate_map(roomdoc, mapdoc)
    if serialize_map(new) != on_disk:
        fails.append("IDEMPOTENCE: map.json itemPriorities out of sync with item_control "
                     "(run `scripts/gen_item_map.py --apply`).")
    fails.extend(integrity_violations(roomdoc, json.loads(serialize_map(new))))
    if fails:
        print("ITEM-MAP CONTRACT VIOLATIONS:")
        for f in fails:
            print("  -", f)
        sys.exit(1)
    print("item-map contract OK")


def cmd_report():
    roomdoc = json.load(open(ROOMDATA_FP, encoding="utf-8"))
    pins = compute(roomdoc)
    links = compute_links(roomdoc)
    total = sum(len(v) for v in pins.values())
    print(f"{total} Fixed item pins across {len(pins)} rooms:")
    for rid in sorted(pins):
        print(f"  {rid}: {len(pins[rid])} pinned")
        for p in pins[rid]:
            print(f"     @{p['at']:>14}  {p['_why']}")
    ntwin = sum(len(v) for v in links.values())
    print(f"\n{ntwin} relocation-twin itemLinks across {len(links)} rooms:")
    for rid in sorted(links):
        ids = ", ".join(f"{l['id']} (×{l['_why'].split('×')[1].split(' ')[0]})" for l in links[rid])
        print(f"  {rid}: {ids}")


if __name__ == "__main__":
    arg = sys.argv[1] if len(sys.argv) > 1 else "--report"
    if arg == "--apply":
        cmd_apply()
    elif arg == "--check":
        cmd_check()
    else:
        cmd_report()
