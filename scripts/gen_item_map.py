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


# --- Scatter-target classification (docs/decisions/dc1/items/KEY-ITEM-SCATTER-DATA-AUDIT.md) --------
# A LEGAL key-item scatter target: a genuinely-static ammo/health slot a shuffled door key can safely
# land in. STRICTER than the Fixed pin (which relaxes timing-only flags) and than gen_ap_logic's
# `source` tag (which reads only trigger.kind, calling the 060F aot_zone+branch_only Poison Dart
# "static"). Fails closed on any non-unconditional gate, so a key is never seated behind a missable
# predicate.
_AMMO_LO, _AMMO_HI = 0x10, 0x1A
_HEALTH_LO, _HEALTH_HI = 0x1B, 0x23
_STATIC_TRIGGER_KINDS = {"init", "init_gosub", "aot_zone"}


def _is_consumable(iid):
    if iid == "0xff":
        return False
    v = int(iid, 16)
    return _AMMO_LO <= v <= _AMMO_HI or _HEALTH_LO <= v <= _HEALTH_HI


def is_scatter_target(rec, twin_ids):
    """True iff this record is a legal key-item scatter target: an ammo/health pickup that is static
    (placed at load / by an always-present enter-zone), appears UNCONDITIONALLY (no test_branch or
    branch_only gate), and is not a relocation twin."""
    iid = rec.get("item_id")
    if not _is_consumable(iid) or iid in twin_ids:
        return False
    if (rec.get("trigger") or {}).get("kind") not in _STATIC_TRIGGER_KINDS:
        return False
    return (rec.get("gate") or {}).get("type") == "unconditional"


def room_scatter(records):
    """Compute the `scatterTargets` entries for one room: `[ { "at": "X,Z", "_why": "..." } ]`, one per
    legal scatter target, sorted by position for a stable diff. A position occupied by MORE THAN ONE
    record (e.g. 0103's An. Aid co-located with the Entrance Key at -2576,3008) is dropped: the overlay
    is position-keyed, so it cannot address one of a co-located pair unambiguously, and scattering a key
    there would collide two field pickups at one coordinate."""
    counts = {}
    pos_counts = {}
    for r in records:
        iid = r.get("item_id")
        if iid != "0xff":
            counts[iid] = counts.get(iid, 0) + 1
        pos = r.get("pos")
        if pos and len(pos) == 2:
            pos_counts[(int(pos[0]), int(pos[1]))] = pos_counts.get((int(pos[0]), int(pos[1])), 0) + 1
    twin_ids = {iid for iid, n in counts.items() if n > 1}

    out = {}
    for r in records:
        if not is_scatter_target(r, twin_ids):
            continue
        pos = r.get("pos")
        if not pos or len(pos) != 2:
            continue
        key = (int(pos[0]), int(pos[1]))
        if pos_counts.get(key, 0) > 1:
            continue  # co-located with another record — ambiguous, not a scatter target
        out[key] = r.get("item_name") or r.get("item_id")
    return [{"at": f"{x},{z}", "_why": f"static ammo/health scatter target — {name}"}
            for (x, z), name in sorted(out.items())]


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


# Design-authored scatter exclusions — NOT a hazard finding (the position still passes the §4 legal-
# target predicate), a deliberate opt-out. `010E`/`0114` are both named "Passageway to the Backup
# Generator" (two distinct rooms, same wiki_title); a shuffled key landing in either reads identically
# in the spoiler log/UI, so a player can't tell which corridor to search. Keyed by room id -> set of
# `"X,Z"` positions (matches `room_scatter`'s `at`). KEY-ITEM-SCATTER-DATA-AUDIT.md §7.
_SCATTER_EXCLUDED = {
    "010E": {"-9024,-7760"},  # An. Aid — duplicate room name with 0114
    "0114": {"5632,3184"},    # An. Aid — duplicate room name with 010E
}


def compute_scatter(doc):
    """room_id -> scatterTargets list, for every item_control room with >=1 legal scatter target."""
    out = {}
    for rid, room in doc["rooms"].items():
        ic = room.get("item_control")
        if not ic:
            continue
        st = room_scatter(ic.get("records", []))
        excluded = _SCATTER_EXCLUDED.get(rid, set())
        st = [e for e in st if e["at"] not in excluded]
        if st:
            out[rid] = st
    return out


# --- Ground-visual overlay (STATIC-SCD-RE cont.72; PICKUP-GROUND-MODEL-FEASIBILITY.md) --------------
# Projects item_control's per-record `visual` (decoded from rec+0x22 display slot + rec+0x24 model
# pointer) onto map.json as `itemVisuals: [{at, visual, _why}]`. Only NON-DEFAULT classes are
# emitted (`generic-panel` — the shared blinking panel — is the implicit default), so the overlay
# stays small. A position shared by records of different classes takes the MOST RESTRICTIVE
# (interaction-only > bespoke-mesh > generic-panel): the consumer treats restrictive classes as
# placement constraints, so worst-wins fails closed. Runtime-armed 0xff slots are skipped (never a
# placement target; always Fixed-pinned).
_VISUAL_RANK = {"generic-panel": 0, "bespoke-mesh": 1, "interaction-only": 2}
_VISUAL_DEFAULT = "generic-panel"


def room_visuals(records):
    by_pos = {}
    for r in records:
        if r.get("item_id") == "0xff":
            continue
        vis = r.get("visual")
        if vis not in _VISUAL_RANK:
            continue
        pos = r.get("pos")
        if not pos or len(pos) != 2:
            continue
        key = (int(pos[0]), int(pos[1]))
        cur = by_pos.get(key)
        name = r.get("item_name") or r.get("item_id")
        if cur is None or _VISUAL_RANK[vis] > _VISUAL_RANK[cur[0]]:
            by_pos[key] = (vis, {name})
        elif _VISUAL_RANK[vis] == _VISUAL_RANK[cur[0]]:
            cur[1].add(name)
    return [{"at": f"{x},{z}", "visual": vis,
             "_why": f"ground visual (rec+0x22/0x24) — {', '.join(sorted(names))}"}
            for (x, z), (vis, names) in sorted(by_pos.items()) if vis != _VISUAL_DEFAULT]


def compute_visuals(doc):
    """room_id -> itemVisuals list, for every item_control room with >=1 non-default visual."""
    out = {}
    for rid, room in doc["rooms"].items():
        ic = room.get("item_control")
        if not ic:
            continue
        vv = room_visuals(ic.get("records", []))
        if vv:
            out[rid] = vv
    return out


# A gen-computed itemPriorities entry leads its `_why` with a hazard-reason token (see hazard_reasons);
# a hand-authored pin uses free-form prose the generator never produces. This lets regenerate_map keep
# hand-authored pins instead of clobbering them, and integrity_violations skip them in the SPURIOUS check.
_GEN_PIN_REASONS = ("flag-gated", "relocation-twin", "runtime-armed", "unresolved-trigger")


def _is_computed_pin(entry):
    return str(entry.get("_why", "")).startswith(_GEN_PIN_REASONS)


def regenerate_map(roomdoc, mapdoc):
    """Return a NEW map doc with itemPriorities + itemLinks + scatterTargets set on every in-scope
    item_control room. Pure function. Rooms with item_control but not in map.json are reported, not
    invented."""
    pins = compute(roomdoc)
    links = compute_links(roomdoc)
    scatter = compute_scatter(roomdoc)
    visuals = compute_visuals(roomdoc)
    out = json.loads(json.dumps(mapdoc))  # deep copy
    rooms = out["rooms"]
    # map.json keys rooms in UPPERCASE hex (010C, 030C, …). Resolve the room-data id case-INSENSITIVELY
    # so hex-letter rooms get their computed pins/links too — matching a room-data id straight against a
    # lowercased key silently skipped every A–F room (cont.68; was a latent quirk, now fixed).
    ci = {k.lower(): k for k in rooms}
    missing = []
    for rid, room in roomdoc["rooms"].items():
        if not room.get("item_control"):
            continue
        mkey = ci.get(rid.lower())
        if mkey is None:
            if rid in pins or rid in links or rid in scatter or rid in visuals:
                missing.append(rid)
            continue
        # Merge computed pins with any HAND-AUTHORED pin already in the room (a human `_why` the
        # generator never emits — e.g. 010C's BG Area Key softlock-safety pin). Computed pins own
        # their positions; a hand-authored pin at an un-computed position is preserved. All-digit
        # rooms carry no hand-authored pins, so this stays byte-identical there.
        computed = pins.get(rid, [])
        computed_at = {e["at"] for e in computed}
        handauth = [e for e in rooms[mkey].get("itemPriorities", [])
                    if not _is_computed_pin(e) and e.get("at") not in computed_at]
        merged = handauth + computed
        if merged:
            rooms[mkey]["itemPriorities"] = merged
        else:
            rooms[mkey].pop("itemPriorities", None)
        if rid in links:
            rooms[mkey]["itemLinks"] = links[rid]
        else:
            rooms[mkey].pop("itemLinks", None)
        if rid in scatter:
            rooms[mkey]["scatterTargets"] = scatter[rid]
        else:
            rooms[mkey].pop("scatterTargets", None)
        if rid in visuals:
            rooms[mkey]["itemVisuals"] = visuals[rid]
        else:
            rooms[mkey].pop("itemVisuals", None)
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
    pins, links, scatter = compute(roomdoc), compute_links(roomdoc), compute_scatter(roomdoc)
    visuals = compute_visuals(roomdoc)
    npins = sum(len(v) for v in pins.values())
    nlinks = sum(len(v) for v in links.values())
    nscatter = sum(len(v) for v in scatter.values())
    nvis = sum(len(v) for v in visuals.values())
    print(f"applied: {npins} Fixed item pins across {len(pins)} rooms; "
          f"{nlinks} itemLinks across {len(links)} rooms; "
          f"{nscatter} scatterTargets across {len(scatter)} rooms; "
          f"{nvis} itemVisuals across {len(visuals)} rooms; map.json valid")
    if missing:
        print(f"  NOTE: {len(missing)} item_control room(s) not in map.json (out of door-rando scope): {missing}")


def integrity_violations(roomdoc, mapdoc):
    v = []
    pins = compute(roomdoc)
    links = compute_links(roomdoc)
    scatter = compute_scatter(roomdoc)
    visuals = compute_visuals(roomdoc)
    rooms = mapdoc["rooms"]
    # Resolve every room-data id onto map.json's UPPERCASE hex key (010C, 030C, …); the same
    # case-insensitive resolver regenerate_map uses, so hex-letter rooms are validated too (cont.68).
    ci = {k.lower(): k for k in rooms}
    # every computed pin present in map.json with priority Fixed
    for rid, plist in pins.items():
        mkey = ci.get(rid.lower())
        if mkey is None:
            continue  # out-of-scope, reported by --apply
        have = {(e.get("at"), e.get("priority")) for e in rooms[mkey].get("itemPriorities", [])}
        for p in plist:
            if (p["at"], "Fixed") not in have:
                v.append(f"COVERAGE: {rid} missing Fixed pin at {p['at']} ({p['_why']})")
    # every computed relocation-twin link present in map.json
    for rid, llist in links.items():
        mkey = ci.get(rid.lower())
        if mkey is None:
            continue
        have = {e.get("id") for e in rooms[mkey].get("itemLinks", [])}
        for l in llist:
            if l["id"] not in have:
                v.append(f"COVERAGE: {rid} missing itemLink for id {l['id']} ({l['_why']})")
    # every computed scatter target present in map.json
    for rid, slist in scatter.items():
        mkey = ci.get(rid.lower())
        if mkey is None:
            continue
        have = {e.get("at") for e in rooms[mkey].get("scatterTargets", [])}
        for s in slist:
            if s["at"] not in have:
                v.append(f"COVERAGE: {rid} missing scatterTarget at {s['at']} ({s['_why']})")
    # every computed non-default ground visual present in map.json with the same class
    for rid, vlist in visuals.items():
        mkey = ci.get(rid.lower())
        if mkey is None:
            continue
        have = {(e.get("at"), e.get("visual")) for e in rooms[mkey].get("itemVisuals", [])}
        for e in vlist:
            if (e["at"], e["visual"]) not in have:
                v.append(f"COVERAGE: {rid} missing itemVisual {e['visual']} at {e['at']} ({e['_why']})")
    # no spurious itemPriorities / itemLinks / scatterTargets in an item_control room (every entry computed)
    for rid, r in roomdoc["rooms"].items():
        if not r.get("item_control"):
            continue
        mkey = ci.get(rid.lower())
        if mkey is None:
            continue
        want_at = {p["at"] for p in pins.get(rid, [])}
        for e in rooms[mkey].get("itemPriorities", []):
            # hand-authored pins (a human `_why`) are legitimately outside the computed set — skip them.
            if _is_computed_pin(e) and e.get("at") not in want_at:
                v.append(f"SPURIOUS: {rid} has itemPriorities at {e.get('at')} with no item_control hazard")
        want_id = {l["id"] for l in links.get(rid, [])}
        for e in rooms[mkey].get("itemLinks", []):
            if e.get("id") not in want_id:
                v.append(f"SPURIOUS: {rid} has itemLink for id {e.get('id')} that is not a relocation twin")
        want_st = {s["at"] for s in scatter.get(rid, [])}
        for e in rooms[mkey].get("scatterTargets", []):
            if e.get("at") not in want_st:
                v.append(f"SPURIOUS: {rid} has scatterTarget at {e.get('at')} that is not a legal static target")
        want_vis = {(x["at"], x["visual"]) for x in visuals.get(rid, [])}
        for e in rooms[mkey].get("itemVisuals", []):
            if (e.get("at"), e.get("visual")) not in want_vis:
                v.append(f"SPURIOUS: {rid} has itemVisual {e.get('visual')} at {e.get('at')} "
                         "not backed by item_control")
    return v


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
