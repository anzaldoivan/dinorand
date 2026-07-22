"""Generate map.json physical-pickup policy from the decoded item_control layer.

This projects stable record-offset targets for fixed priorities, explicit logical/synchronization
groups, scatter eligibility, and ground-visual classes. Equal item ids or coordinates never create a
group; the explicit group registries below are the only multi-record identity authority. Legacy
first-corner coordinates remain as human-readable aliases and synthetic-fixture fallback only.

The generator reads ONLY data/dc1/room-data.json and authored map.json metadata. No game .dat is
needed, so regeneration runs in CI. room-data.json is produced by the repository's decoded item
catalog pipeline; map.json is consumed by standalone planning and AP distillation.

Hazard policy (a physical record is pinned Fixed iff ANY apply):
  - item_id == 0xff            (runtime-armed slot; shuffle is a no-op / native fill)
  - gate is a flag test_branch (conditionally placed → reachability/deadlock risk)
  - trigger.kind == unresolved  (may never appear; placing a needed item here softlocks)
  - member of an explicit synchronization group (alternate script-state record)

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

# Explicit physical membership. These lists replace the old same-id / first-corner inference.
# logical groups are the 12 published multi-record AP pickups; sync groups additionally capture
# alternate script-state records that standalone key mutation must mirror. Record offsets are stable
# identities verified by room-data.json and ap-client-checks.json.
_EXPLICIT_LOGICAL_GROUPS = {
    "0303": [("0303:2d:3088,-1008", [0x4FC70, 0x50B70]),
             ("0303:46:3088,-1008", [0x4FD5C, 0x50700])],
    "0308": [("0308:1d:1536,-2464", [0x42250, 0x4227C]),
             ("0308:1d:-1185,1451", [0x42B4C, 0x42BA8])],
    "0504": [("0504:4e:2256,-3840", [0x4FEE0, 0x501A8])],
    "0507": [("0507:52:4352,7824", [0x416FC, 0x42AD8]),
             ("0507:53:4352,9616", [0x41734, 0x42B10])],
    "060D": [("060d:2b:-2816,10368", [0x2D1F8, 0x2D2A4]),
             ("060d:18:-2816,10368", [0x2D230, 0x2D268, 0x2D2DC, 0x2D314])],
    "0610": [("0610:2b:-1024,-2048", [0x23820, 0x238A8]),
             ("0610:18:-1024,-2048", [0x2384C, 0x23878, 0x238D4, 0x23900])],
    "0612": [("0612:18:-896,0", [0x23330, 0x2335C])],
}

_EXPLICIT_SYNC_GROUPS = {
    "0108": [("0108:plug-states", [0x3D82C, 0x3DB08])],
    "0303": [("0303:small-key-states", [0x4FC70, 0x50B70]),
             ("0303:b1-chip-states", [0x4FD5C, 0x50700, 0x50B20])],
    "0308": [("0308:med-pak-a-copies", [0x42250, 0x4227C]),
             ("0308:med-pak-b-copies", [0x42B4C, 0x42BA8])],
    "0406": [("0406:ddk-l-states", [0x37674, 0x37FA0])],
    "0504": [("0504:protect-1a-states", [0x4FEE0, 0x501A8]),
             ("0504:protect-2a-states", [0x4FBF0, 0x500F8]),
             ("0504:protect-1b-states", [0x4FEB4, 0x5028C])],
    "0507": [("0507:core-1-copies", [0x416FC, 0x42AD8]),
             ("0507:core-2-copies", [0x41734, 0x42B10])],
    "0606": [("0606:plug-region-copies", [0x3E5A0, 0x3E5CC])],
    "060D": [("060d:plug-grant-copies", [0x2D1F8, 0x2D2A4]),
             ("060d:grenade-grant-copies", [0x2D230, 0x2D268, 0x2D2DC, 0x2D314])],
    "0610": [("0610:plug-grant-copies", [0x23820, 0x238A8]),
             ("0610:grenade-grant-copies", [0x2384C, 0x23878, 0x238D4, 0x23900])],
    "0612": [("0612:grenade-grant-copies", [0x23330, 0x2335C])],
}

# Deliberate fail-closed exception: 030C has two records at one coordinate and the historical safety
# policy pins both when either side is hazardous. No other coordinate collision inherits policy.
_CONSERVATIVE_PIN_GROUPS = {
    "030C": [{0x49BA0, 0x4C044}],
}


def _room_groups(groups, room):
    return groups.get(room.upper(), [])


def _member_offsets(groups, room):
    return {off for _gid, members in _room_groups(groups, room) for off in members}


def is_key_item(iid):
    return iid != "0xff" and KEY_LO <= int(iid, 16) <= KEY_HI


def hazard_reasons(rec, sync_offsets):
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
    if int(rec.get("rec_offset", "-1"), 16) in sync_offsets:
        reasons.append("relocation-twin")
    return reasons


def room_pins(records, sync_offsets=frozenset(), conservative_groups=()):
    """Compute the itemPriorities entries for one room's item_control records.
    Stable record targets are merged by position for readability without spreading policy to
    non-target records. conservative_groups is the one explicit 030C over-pin exception."""
    by_pos = {}
    for r in records:
        reasons = hazard_reasons(r, sync_offsets)
        if not reasons:
            continue
        pos = r.get("pos")
        if not pos or len(pos) != 2:
            continue
        key = (int(pos[0]), int(pos[1]))
        entry = by_pos.setdefault(key, {"items": set(), "reasons": set(), "records": set()})
        entry["items"].add(r.get("item_name") or r.get("item_id"))
        entry["reasons"].update(reasons)
        offset = int(r["rec_offset"], 16)
        entry["records"].add(offset)
        for group in conservative_groups:
            if offset in group:
                entry["records"].update(group)

    pins = []
    for (x, z), e in sorted(by_pos.items()):
        pins.append({
            "at": f"{x},{z}",
            "records": [hex(off) for off in sorted(e["records"])],
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


def is_scatter_target(rec, sync_offsets):
    """True iff this record is a legal key-item scatter target: an ammo/health pickup that is static
    (placed at load / by an always-present enter-zone), appears UNCONDITIONALLY (no test_branch or
    branch_only gate), and is not a relocation twin."""
    iid = rec.get("item_id")
    if not _is_consumable(iid) or int(rec.get("rec_offset", "-1"), 16) in sync_offsets:
        return False
    if (rec.get("trigger") or {}).get("kind") not in _STATIC_TRIGGER_KINDS:
        return False
    return (rec.get("gate") or {}).get("type") == "unconditional"


def room_scatter(records, sync_offsets=frozenset()):
    """Compute record-targeted `scatterTargets` entries, one per
    legal scatter target, sorted by position for a stable diff. A position occupied by MORE THAN ONE
    record remains excluded by the established conservative scatter policy."""
    pos_counts = {}
    for r in records:
        pos = r.get("pos")
        if pos and len(pos) == 2:
            pos_counts[(int(pos[0]), int(pos[1]))] = pos_counts.get((int(pos[0]), int(pos[1])), 0) + 1
    out = {}
    for r in records:
        if not is_scatter_target(r, sync_offsets):
            continue
        pos = r.get("pos")
        if not pos or len(pos) != 2:
            continue
        key = (int(pos[0]), int(pos[1]))
        if pos_counts.get(key, 0) > 1:
            continue  # co-located with another record — ambiguous, not a scatter target
        out[key] = (r.get("item_name") or r.get("item_id"), int(r["rec_offset"], 16))
    return [{"at": f"{x},{z}", "records": [hex(offset)],
             "_why": f"static ammo/health scatter target — {name}"}
            for (x, z), (name, offset) in sorted(out.items())]


def compute(doc):
    """room_id -> itemPriorities list, for every item_control room with >=1 pin."""
    out = {}
    for rid, room in doc["rooms"].items():
        ic = room.get("item_control")
        if not ic:
            continue
        pins = room_pins(ic.get("records", []), _member_offsets(_EXPLICIT_SYNC_GROUPS, rid),
                         _CONSERVATIVE_PIN_GROUPS.get(rid.upper(), ()))
        if pins:
            out[rid] = pins
    return out


def compute_links(doc):
    """room_id -> explicit sync groups, never inferred from repeated item ids."""
    out = {}
    for rid, room in doc["rooms"].items():
        ic = room.get("item_control")
        if not ic:
            continue
        available = {int(r["rec_offset"], 16) for r in ic.get("records", [])}
        links = []
        for group_id, members in _room_groups(_EXPLICIT_SYNC_GROUPS, rid):
            missing = set(members) - available
            if missing:
                raise ValueError(f"{rid} sync group {group_id} missing records {sorted(missing)}")
            links.append({"id": group_id, "records": [hex(x) for x in members],
                          "_why": "explicit alternate/copy membership"})
        if links:
            out[rid] = links
    return out


def compute_groups(doc):
    """room_id -> explicit logical pickup groups used by standalone and AP."""
    out = {}
    for rid, room in doc["rooms"].items():
        ic = room.get("item_control")
        if not ic:
            continue
        available = {int(r["rec_offset"], 16) for r in ic.get("records", [])}
        groups = []
        for group_id, members in _room_groups(_EXPLICIT_LOGICAL_GROUPS, rid):
            missing = set(members) - available
            if missing:
                raise ValueError(f"{rid} logical group {group_id} missing records {sorted(missing)}")
            groups.append({"id": group_id, "records": [hex(x) for x in members],
                           "_why": "explicit published multi-record pickup"})
        if groups:
            out[rid] = groups
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
        st = room_scatter(ic.get("records", []), _member_offsets(_EXPLICIT_SYNC_GROUPS, rid))
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


def room_record_visuals(records):
    """Emit visual policy per stable physical record; equal coordinates never merge policy."""
    out = []
    for rec in records:
        if rec.get("item_id") == "0xff" or rec.get("visual") == _VISUAL_DEFAULT:
            continue
        visual = rec.get("visual")
        pos = rec.get("pos")
        if visual not in _VISUAL_RANK or not pos or len(pos) != 2:
            continue
        out.append({
            "at": f"{int(pos[0])},{int(pos[1])}",
            "records": [hex(int(rec["rec_offset"], 16))],
            "visual": visual,
            "_why": f"ground visual (rec+0x22/0x24) — {rec.get('item_name') or rec.get('item_id')}",
        })
    return sorted(out, key=lambda x: (x["at"], x["records"][0]))


def compute_visuals(doc):
    """room_id -> itemVisuals list, for every item_control room with >=1 non-default visual."""
    out = {}
    for rid, room in doc["rooms"].items():
        ic = room.get("item_control")
        if not ic:
            continue
        vv = room_record_visuals(ic.get("records", []))
        if vv:
            out[rid] = vv
    return out


# A gen-computed itemPriorities entry leads its `_why` with a hazard-reason token (see hazard_reasons);
# a hand-authored pin uses free-form prose the generator never produces. This lets regenerate_map keep
# hand-authored pins instead of clobbering them, and integrity_violations skip them in the SPURIOUS check.
_GEN_PIN_REASONS = ("flag-gated", "relocation-twin", "runtime-armed", "unresolved-trigger")

_ITEM_POLICY_DERIVATION = (
    "Physical-pickup placement policy generated by scripts/gen_item_map.py from room-data.json. "
    "Entries carry stable `records` offsets; `at` is a readable geometry alias and is used only as "
    "a legacy/synthetic fallback when no explicit record target matches. Fixed records never enter "
    "ordinary or progression placement. `itemGroups` defines player-visible logical pickups and "
    "`itemLinks` defines script-state synchronization groups; neither is inferred from equal ids or "
    "coordinates. The conservative 030C coordinate collision intentionally pins both records."
)

_REGION_DERIVATION = (
    "Sub-room REGION schema for authored entry-direction partitions. `nodeSplit: true` rooms become "
    "distinct RoomGraph nodes; external doors land in the region owning the reciprocal doorway, and "
    "items belong to the primary node. `accessFrom` defines intra-room requirements. The engine "
    "reachability oracle exports this node topology and scripts/gen_ap_logic.py consumes the same "
    "physical nodes/edges for AP v3. Rooms without a split remain atomic."
)


def _is_computed_pin(entry):
    return str(entry.get("_why", "")).startswith(_GEN_PIN_REASONS)


def regenerate_map(roomdoc, mapdoc):
    """Return a NEW map doc with itemPriorities + itemLinks + scatterTargets set on every in-scope
    item_control room. Pure function. Rooms with item_control but not in map.json are reported, not
    invented."""
    pins = compute(roomdoc)
    links = compute_links(roomdoc)
    groups = compute_groups(roomdoc)
    scatter = compute_scatter(roomdoc)
    visuals = compute_visuals(roomdoc)
    out = json.loads(json.dumps(mapdoc))  # deep copy
    out.setdefault("_derivation", {})["itemPriorities"] = _ITEM_POLICY_DERIVATION
    out["_derivation"]["regions"] = _REGION_DERIVATION
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
        if rid in groups:
            rooms[mkey]["itemGroups"] = groups[rid]
        else:
            rooms[mkey].pop("itemGroups", None)
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
    groups = compute_groups(roomdoc)
    visuals = compute_visuals(roomdoc)
    npins = sum(len(v) for v in pins.values())
    nlinks = sum(len(v) for v in links.values())
    ngroups = sum(len(v) for v in groups.values())
    nscatter = sum(len(v) for v in scatter.values())
    nvis = sum(len(v) for v in visuals.values())
    print(f"applied: {npins} Fixed item pins across {len(pins)} rooms; "
          f"{nlinks} itemLinks across {len(links)} rooms; "
          f"{ngroups} itemGroups across {len(groups)} rooms; "
          f"{nscatter} scatterTargets across {len(scatter)} rooms; "
          f"{nvis} itemVisuals across {len(visuals)} rooms; map.json valid")
    if missing:
        print(f"  NOTE: {len(missing)} item_control room(s) not in map.json (out of door-rando scope): {missing}")


def integrity_violations(roomdoc, mapdoc):
    v = []
    def targets(entries):
        return [(int(value, 16), entry) for entry in entries
                for value in entry.get("records", [])]

    pins = compute(roomdoc)
    links = compute_links(roomdoc)
    groups = compute_groups(roomdoc)
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
        have = {(offset, entry.get("priority"))
                for offset, entry in targets(rooms[mkey].get("itemPriorities", []))}
        for offset, pin in targets(plist):
            if (offset, "Fixed") not in have:
                v.append(f"COVERAGE: {rid} missing Fixed pin for record {hex(offset)} ({pin['_why']})")
    # every computed relocation-twin link present in map.json
    for rid, llist in links.items():
        mkey = ci.get(rid.lower())
        if mkey is None:
            continue
        have = {(offset, entry.get("id"))
                for offset, entry in targets(rooms[mkey].get("itemLinks", []))}
        for offset, link in targets(llist):
            if (offset, link["id"]) not in have:
                v.append(f"COVERAGE: {rid} missing itemLink {link['id']} for record {hex(offset)}")
    # every computed scatter target present in map.json
    for rid, slist in scatter.items():
        mkey = ci.get(rid.lower())
        if mkey is None:
            continue
        have = {offset for offset, _ in targets(rooms[mkey].get("scatterTargets", []))}
        for offset, scatter_entry in targets(slist):
            if offset not in have:
                v.append(f"COVERAGE: {rid} missing scatterTarget record {hex(offset)} ({scatter_entry['_why']})")
    # every computed non-default ground visual present in map.json with the same class
    for rid, vlist in visuals.items():
        mkey = ci.get(rid.lower())
        if mkey is None:
            continue
        have = {(offset, entry.get("visual"))
                for offset, entry in targets(rooms[mkey].get("itemVisuals", []))}
        for offset, visual in targets(vlist):
            if (offset, visual["visual"]) not in have:
                v.append(f"COVERAGE: {rid} missing itemVisual {visual['visual']} for record {hex(offset)}")
    # no spurious itemPriorities / itemLinks / scatterTargets in an item_control room (every entry computed)
    for rid, r in roomdoc["rooms"].items():
        if not r.get("item_control"):
            continue
        mkey = ci.get(rid.lower())
        if mkey is None:
            continue
        want_pins = {offset for offset, _ in targets(pins.get(rid, []))}
        for e in rooms[mkey].get("itemPriorities", []):
            # hand-authored pins (a human `_why`) are legitimately outside the computed set — skip them.
            if _is_computed_pin(e):
                for offset, _ in targets([e]):
                    if offset not in want_pins:
                        v.append(f"SPURIOUS: {rid} Fixed record {hex(offset)} has no item_control hazard")
        want_links = {(offset, entry["id"]) for offset, entry in targets(links.get(rid, []))}
        for offset, entry in targets(rooms[mkey].get("itemLinks", [])):
            if (offset, entry.get("id")) not in want_links:
                v.append(f"SPURIOUS: {rid} itemLink {entry.get('id')} record {hex(offset)}")
        want_scatter = {offset for offset, _ in targets(scatter.get(rid, []))}
        for offset, _ in targets(rooms[mkey].get("scatterTargets", [])):
            if offset not in want_scatter:
                v.append(f"SPURIOUS: {rid} scatterTarget record {hex(offset)}")
        want_visuals = {(offset, entry["visual"])
                        for offset, entry in targets(visuals.get(rid, []))}
        for offset, entry in targets(rooms[mkey].get("itemVisuals", [])):
            if (offset, entry.get("visual")) not in want_visuals:
                v.append(f"SPURIOUS: {rid} itemVisual {entry.get('visual')} record {hex(offset)}")
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
    print(f"\n{ntwin} explicit synchronization itemLinks across {len(links)} rooms:")
    for rid in sorted(links):
        ids = ", ".join(f"{l['id']} (×{len(l['records'])})" for l in links[rid])
        print(f"  {rid}: {ids}")


if __name__ == "__main__":
    arg = sys.argv[1] if len(sys.argv) > 1 else "--report"
    if arg == "--apply":
        cmd_apply()
    elif arg == "--check":
        cmd_check()
    else:
        cmd_report()
