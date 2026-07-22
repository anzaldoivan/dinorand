#!/usr/bin/env python3
"""Distil the authored per-game logic data into compact Archipelago-world contracts.

Each supported game distils into apworld/<pkg>/data/<game>_logic.json — the stable input the
.apworld consumes to build regions/locations/items/rules. Shared schema + structural checks live
here (repo-side); the shipped apworlds stay self-contained (AP zips can't share runtime code).
See the per-game Archipelago and item/key-item decision records under docs/decisions/.

    python3 scripts/gen_ap_logic.py            --check    # every game (used by pre-commit + CI)
    python3 scripts/gen_ap_logic.py            --apply    # regenerate every game's contract
    python3 scripts/gen_ap_logic.py dc2        --apply    # one game only

No third-party deps.

DC1: the logic graph is authored in map.json (door edges + `requires` item-id gates +
`requiresRoom` visit gates); per-location pickup data comes from room-data.json item_control;
item names from items.json (clean source of truth).

DC2: the v2 physical-room contract consumes byte-provenanced acquisition sources, the item
catalog, decoded door commits, door guards, and story-flag setters. It exposes only sources with
proven AP availability, records every excluded source and conditional-commit disposition, and
models ST101 start, the Gas Mask gate, and guarded ST503→ST504 victory.
"""
from __future__ import annotations

import json
import re
import sys
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DC1 = REPO / "data" / "dc1"

EMPTY_SLOT_ID = 0xFF  # DC1 empty item slot sentinel

# Constructs the AP star-topology model CANNOT faithfully represent (GRAPH-LOGIC-PARITY "parity
# contract"). Today: rooms with an entry-direction node-split ("nodeSplit": true) — the star model has
# no door topology, so it cannot see the sub-region partition (the 0309 shuttle fence). Each such room is
# a DECLARED, bounded debt, deferred to the apworld region-graph follow-up (REGION-SCHEMA-PLAN §2). The
# distiller FAILS CLOSED if map.json grows a node-split not listed here, so a new inexpressible construct
# can never be silently dropped the way 0309 was for weeks. Must equal the live map.json node-split set.
AP_UNREPRESENTABLE: set[str] = {"0309"}

ORACLE = DC1 / "reachability-oracle.json"  # engine-truth golden snapshot (Dc1ReachabilityOracleTests)


def _dc1_node_split_rooms() -> set[str]:
    """The canonical codes of every map.json room declaring \"nodeSplit\": true."""
    raw = json.loads((DC1 / "map.json").read_text(encoding="utf-8"))
    return {_code(c) for c, room in raw["rooms"].items() if room.get("nodeSplit") is True}


def _assert_dc1_parity(require_oracle: bool) -> None:
    """Gap B (fail-closed) + Gap C (oracle↔source) tripwire — no game install needed, so it runs on CI.

    Errors (loudly, with the offending rooms) when the AP star model would silently misrepresent the
    authored graph: a node-split not on AP_UNREPRESENTABLE, a stale allow-list entry, or a
    reachability-oracle that no longer agrees with map.json's node-split set (i.e. the golden snapshot
    went stale — e.g. someone removed the 0309 split from map.json but did not regenerate the oracle)."""
    live = _dc1_node_split_rooms()
    if live != AP_UNREPRESENTABLE:
        added = sorted(live - AP_UNREPRESENTABLE)
        removed = sorted(AP_UNREPRESENTABLE - live)
        raise SystemExit(
            "gen_ap_logic: map.json node-split set != AP_UNREPRESENTABLE allow-list.\n"
            + (f"  NEW node-split(s) the AP star model cannot represent: {added} — either author the "
               "apworld region-graph for them or add them to AP_UNREPRESENTABLE with a rationale.\n" if added else "")
            + (f"  allow-list has stale ent(s) no longer in map.json: {removed} — shrink AP_UNREPRESENTABLE.\n" if removed else "")
            + "  (GRAPH-LOGIC-PARITY parity contract.)"
        )
    if require_oracle:
        if not ORACLE.exists():
            raise SystemExit(
                f"gen_ap_logic: {ORACLE.relative_to(REPO)} missing — regenerate it with "
                "DINORAND_UPDATE_ORACLE=1 dotnet test --filter Dc1ReachabilityOracleTests and stage it."
            )
        oracle = json.loads(ORACLE.read_text(encoding="utf-8"))
        oset = {_code(c) for c in oracle.get("nodeSplitRooms", [])}
        if oset != live:
            raise SystemExit(
                f"gen_ap_logic: {ORACLE.relative_to(REPO)} is STALE — its nodeSplitRooms {sorted(oset)} "
                f"!= map.json {sorted(live)}. Regenerate with DINORAND_UPDATE_ORACLE=1 "
                "dotnet test --filter Dc1ReachabilityOracleTests and stage it. (Gap C oracle↔source.)"
            )


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
        # Laser-fence sub-room regions (REGION-SCHEMA-PLAN.md): each non-primary region's accessFrom
        # rule gates the edges to that region's door destinations — the room-granular AP analog of the
        # doorway-keyed gate the C# stamps (the two fence gates 0102/010A migrated here from door-level).
        # Primary regions (no accessFrom) and always-open fences (empty rule) emit nothing. 0606's
        # item-only segment is gated at the item layer (Key Card C, not a progression item), not an edge.
        for region in (room.get("regions") or {}).values():
            af = region.get("accessFrom")
            if not af:
                continue
            req_items = [_item_id(i) for r in af.values() for i in (r.get("requires") or [])]
            gate_rooms = [_code(r) for rr in af.values() for r in (rr.get("requiresRoom") or [])]
            if not req_items and not gate_rooms:
                continue  # always-open fence — no gate
            for target in (region.get("doors") or []):
                tc = _code(target)
                edges.append(
                    {
                        "from": c,
                        "to": tc,
                        "requiresItems": req_items,
                        "requiresRooms": [r for r in gate_rooms if r != tc],
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


def _source(rec) -> str:
    """Classify how a pickup record materialises (STATIC-SCD-RE cont.60): a real id placed at
    room load = static; id 0xff = runtime-armed slot (a trigger/native code sets the id later,
    TRIGGER-DECODE item-gate layer — never guess the id); a real id placed by a non-load event
    sub = event-granted."""
    if _item_id(rec["item_id"]) == EMPTY_SLOT_ID:
        return "runtime-armed (trigger)"
    kind = (rec.get("trigger") or {}).get("kind")
    return "static" if kind in ("init", "init_gosub", "aot_zone") else "event-granted"


def load_locations(item_names: dict[int, str]) -> list[dict]:
    """ALL decoded pickup rows, each tagged `source` — nothing is silently dropped here;
    build_dc1 filters what the AP contract can actually place."""
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
            pos = rec.get("pos") or [0, 0]
            x, y = int(pos[0]), int(pos[1])
            key = f"{c}:{iid:02x}:{x},{y}"
            if key in seen:
                continue
            seen.add(key)
            iname = item_names.get(iid, _clean(rec.get("item_name", f"0x{iid:02x}")))
            loc = {
                "key": key,
                "name": f"{room_name.get(c, c)} ({c}) - {iname} @{x},{y}",
                "room": c,
                "itemId": iid,
                "itemName": iname,
                "pos": [x, y],
                "collectedFlag": rec.get("collected_flag"),
                "source": _source(rec),
                # Ground-visual class (item_control `visual`, STATIC-SCD-RE cont.72): generic-panel /
                # bespoke-mesh / interaction-only. Informational (DATA-REFERENCE / hint text) — the AP
                # contract places against ids and rooms, never the visual.
                "visual": rec.get("visual"),
            }
            fl = (rec.get("gate") or {}).get("flag")
            if fl:
                loc["gateFlag"] = f"{fl['group']}:{fl['index']}"
            locs.append(loc)
    return locs


CLIENT_CHECKS = DC1 / "ap-client-checks.json"


def _load_client_checks() -> dict[str, dict]:
    """AP location name -> its runtime-client check entry (data/dc1/ap-client-checks.json,
    generated install-side by scripts/gen_ap_client_checks.py). Fail-closed: the runtime client
    (AP-CLIENT-PLAN.md) needs a predicate for every location, so a missing/renamed entry must
    break the build, not surface as a silently-uncheckable location."""
    if not CLIENT_CHECKS.exists():
        raise SystemExit(f"gen_ap_logic: {CLIENT_CHECKS.relative_to(REPO)} missing — "
                         "run 'python3 scripts/gen_ap_client_checks.py --apply' (needs the DC1 install)")
    doc = json.loads(CLIENT_CHECKS.read_text(encoding="utf-8"))
    return {e["name"]: e for e in doc["locations"]}


def build_dc1() -> dict:
    _assert_dc1_parity(require_oracle=False)  # fail closed: never silently drop an unrepresentable construct
    items = load_items()
    m = load_map()
    all_locs = load_locations(items["names"])
    # AP contract: only real-id pickups in rooms on the door graph are placeable checks.
    # Runtime-armed 0xff slots (no id to place against) and alt-room copies outside the
    # graph stay visible in DATA-REFERENCE.md via load_locations' `source` tag.
    locs = [l for l in all_locs
            if l["itemId"] != EMPTY_SLOT_ID and l["room"] in m["regions"]]
    off_graph = sorted({l["room"] for l in all_locs
                        if l["itemId"] != EMPTY_SLOT_ID and l["room"] not in m["regions"]})
    if off_graph:
        print(f"  NOTE: dc1 pickups in {len(off_graph)} room(s) outside the door graph "
              f"excluded from the AP contract: {off_graph}")
    # Runtime-client check parity (AP-CLIENT-PLAN.md §2): every AP location must have a check
    # predicate, and shared-flag locations the client cannot attribute individually are marked
    # `excluded` so the apworld keeps progression out of them (LocationProgressType.EXCLUDED).
    checks = _load_client_checks()
    have, want = set(checks), {l["name"] for l in locs}
    if have != want:
        missing, stale = sorted(want - have), sorted(have - want)
        raise SystemExit("gen_ap_logic: ap-client-checks.json out of sync with the location set "
                         f"(missing {missing[:5]}{'…' if len(missing) > 5 else ''}, "
                         f"stale {stale[:5]}{'…' if len(stale) > 5 else ''}) — "
                         "regenerate with gen_ap_client_checks.py --apply")
    for l in locs:
        if checks[l["name"]].get("excluded"):
            l["excluded"] = True
    progression = sorted({i for e in m["edges"] for i in e["requiresItems"]})
    return {
        "_generated_by": "scripts/gen_ap_logic.py",
        "_source": "data/dc1/{map,items,room-data}.json (authored) + ap-client-checks.json",
        "version": 2,
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


DC2_EXCLUDED_ROOMS = {"608", "707", "905", "906"}
DC2_PROGRESSION_ITEMS = [0x2E]
DC2_OPTIONAL_FIXED_ITEMS = [0x33, 0x34]


def _dc2_rewrite_class(item_id: int) -> str | None:
    """Return the D3 item-ID-only writer compatibility class, if supported."""
    if 0x1A <= item_id <= 0x1F and item_id != 0x1E:
        return "health"
    if item_id == 0x2F:
        return "special_key_2f"
    if 0x21 <= item_id <= 0x2E or 0x30 <= item_id <= 0x34:
        return "generic_key"
    return None


def _stable_dc2_source_id(source_id: str) -> int:
    """Addition-stable AP id derived directly from the immutable source identity."""
    value = 0x811C9DC5
    for byte in source_id.encode("utf-8"):
        value = ((value ^ byte) * 0x01000193) & 0xFFFFFFFF
    return 0x40000000 | (value & 0x3FFFFFFF)


def _dc2_catalog() -> tuple[dict[int, str], list[int], list[dict]]:
    raw = json.loads((DC2 / "items.json").read_text(encoding="utf-8"))
    base_names = [row.get("name") for row in raw["catalog"] if row.get("name")]
    duplicates = Counter(base_names)
    names: dict[int, str] = {}
    rows: list[dict] = []
    for row in raw["catalog"]:
        iid = _item_id(row["id"])
        base = row.get("name") or f"Catalog entry 0x{iid:02X}"
        name = f"{base} [0x{iid:02X}]" if duplicates[base] > 1 else base
        names[iid] = name
        rows.append({"id": iid, "name": name, "category": row["category"]})
    key_ids = sorted(_item_id(value) for value in raw["keyItemIds"])
    return names, key_ids, rows


def _dc2_story_setter_rooms(gating: dict, group: int, flag_id: int) -> list[str]:
    return sorted({
        room
        for room, routines in gating["rooms"].items()
        for routine in routines
        if [group, flag_id, 1] in routine["sets"]
    })


def _dc2_source_availability(source: dict, gating: dict) -> tuple[bool, list[str], str]:
    """Translate fixture-owned AP availability; writer eligibility alone is insufficient."""
    if not source.get("eligible_item_rewrite"):
        return False, [], source.get("reason", f"not_fillable_{source['source_type']}")
    availability = source.get("ap_availability")
    if not availability:
        return False, [], "missing_ap_availability"
    disposition = availability["disposition"]
    if disposition == "excluded":
        return False, [], availability["reason"]
    if disposition == "unconditional":
        return True, [], availability["reason"]
    if disposition != "modelable":
        return False, [], f"unsupported_ap_availability:{disposition}"
    rooms: set[str] = set()
    for requirement in availability["requirements"]:
        if requirement.get("kind") != "story_flag_set":
            return False, [], f"unsupported_availability_requirement:{requirement.get('kind')}"
        setters = _dc2_story_setter_rooms(
            gating, int(requirement["group"]), int(requirement["id"])
        )
        if len(setters) != 1:
            return False, [], "unresolved_story_flag_producer"
        rooms.add(setters[0])
    return True, sorted(rooms), availability["reason"]


def build_dc2() -> dict:
    """Build the DC2 AP v2 physical campaign contract from maintained decoded fixtures."""
    doors = json.loads((DC2 / "door-graph.json").read_text(encoding="utf-8"))["rooms"]
    gating = json.loads((DC2 / "door-gating.json").read_text(encoding="utf-8"))
    guards = json.loads((DC2 / "door-guards.json").read_text(encoding="utf-8"))
    source_doc = json.loads((DC2 / "item-sources.json").read_text(encoding="utf-8"))
    if source_doc.get("schema_version") != 2:
        raise SystemExit("gen_ap_logic: DC2 item-sources schema v2 with ap_availability required")
    catalog_names, key_ids, catalog_rows = _dc2_catalog()
    fixed_lifecycle_ids = sorted({
        _item_id(source["catalog_id"])
        for source in source_doc["sources"]
        if source["source_type"] == "group9_clear" and source.get("catalog_id")
        and _item_id(source["catalog_id"]) not in DC2_PROGRESSION_ITEMS
    })
    stage_rooms = json.loads((DC2 / "stage-room-map.json").read_text(encoding="utf-8"))["rooms"]
    supported = {row["st_id"] for row in stage_rooms} - DC2_EXCLUDED_ROOMS
    regions = {code: f"ST{code}" for code in sorted(supported)}

    graph_door = {
        (room, row["commit_off"], row["dest_id"]): row
        for room, entry in doors.items()
        for row in entry.get("doors", [])
    }
    guarded_door = {
        (row["room"], row["commit_off"], row["dest_id"]): row
        for row in guards["door_guards"]
    }
    provenance = guards["_door_commit_provenance"]["doors"]
    pair_counts = Counter((row["room"], row["dest"]) for row in provenance)

    def door_disposition(row: dict, guard: dict | None) -> str:
        if row["class"] == "no-start-site-found":
            return "excluded_unsupported_duplicate"
        identity = (row["room"], row["commit_off"], row["dest"])
        if identity == ("10D", "0xe4c0", "10F"):
            return "modeled_item_gate"
        if identity == ("503", "0x15f56", "504"):
            return "modeled_victory_commit"
        exprs = [value["expr"] for value in (guard or {}).get("guards", [])]
        if any("flag(8," in value for value in exprs):
            return "fixed_vanilla_subweapon"
        if any("CHARACTER(" in value for value in exprs):
            return "fixed_vanilla_character_sequence"
        if any("vmEntryState" in value or "!(" in value for value in exprs):
            return "fixed_vanilla_nonmonotonic"
        if pair_counts[(row["room"], row["dest"])] > 1:
            return "fixed_vanilla_variant"
        return "modeled_monotonic_story" if guard else "unconditional_physical_door"

    edges: list[dict] = []
    for row in provenance:
        src, encoded_dest = row["room"], row["dest"]
        physical = graph_door.get((src, row["commit_off"], encoded_dest))
        destinations = (physical or {}).get("conditional_dests") or [encoded_dest]
        if src not in regions or row["class"] == "no-start-site-found":
            continue
        for dest in destinations:
            if dest not in regions:
                continue
            identity = (src, row["commit_off"], encoded_dest)
            guard = guarded_door.get(identity)
            native_conditions = [
                clause["expr"] for clause in (guard or {}).get("guards", [])
            ]
            disposition = (
                "modeled_resolved_computed_destination"
                if physical and physical.get("conditional_dests")
                else door_disposition(row, guard)
            )
            required_items = [0x2E] if disposition == "modeled_item_gate" else []
            required_rooms: set[str] = set()
            for clause in (guard or {}).get("guards", []):
                match = re.fullmatch(r"flag\((\d+),(\d+)\)", clause["expr"])
                if not match:
                    continue
                setters = _dc2_story_setter_rooms(
                    gating, int(match.group(1)), int(match.group(2))
                )
                if len(setters) == 1 and setters[0] not in (src, dest):
                    required_rooms.add(setters[0])
            lock_alternatives: list[list[str]] = []
            if physical and "lock_index" in physical:
                gate = gating["locked_doors"].get(f"{src}->{encoded_dest}")
                lock_alternatives = _dc2_unlock_rooms(gate, gating, dest) if gate else []
            for alt_index, alt in enumerate(lock_alternatives or [[]]):
                rooms = sorted(
                    required_rooms | {value for value in alt if value not in (src, dest)}
                )
                suffix = f":alt{alt_index}" if len(lock_alternatives) > 1 else ""
                edges.append({
                    "id": f"ST{src}:door@{row['commit_off']}->ST{dest}{suffix}",
                    "from": src,
                    "to": dest,
                    "commitOff": row["commit_off"],
                    "requiresItems": required_items,
                    "requiresRooms": rooms,
                    "nativeConditions": native_conditions,
                    "disposition": disposition,
                })

    dispositions: list[dict] = []
    for row in guards["door_guards"]:
        identity = (row["room"], row["commit_off"], row["dest_id"])
        prov = next(value for value in provenance
                    if (value["room"], value["commit_off"], value["dest"]) == identity)
        dispositions.append({
            "id": f"ST{row['room']}:door@{row['commit_off']}->ST{row['dest_id']}",
            "kind": "door",
            "nativeConditions": [clause["expr"] for clause in row["guards"]],
            "disposition": door_disposition(prov, row),
        })
    source_by_offset = {
        (source.get("provenance", {}).get("room", "")[2:],
         source.get("provenance", {}).get("op_blob_off")): source
        for source in source_doc["sources"]
        if "op_blob_off" in source.get("provenance", {})
    }
    for row in guards["item_guards"]:
        source = source_by_offset[(row["room"], int(row["commit_off"], 16))]
        if source["source_type"] != "sat1_trigger":
            raise AssertionError(f"conditional item commit is not SAT-1: {source['source_id']}")
        dispositions.append({
            "id": f"ST{row['room']}:item@{row['commit_off']}",
            "kind": "item",
            "sourceId": source["source_id"],
            "nativeConditions": [clause["expr"] for clause in row["guards"]],
            "disposition": "excluded_sat1_physical_trigger",
        })
    dispositions.sort(key=lambda row: row["id"])

    locations: list[dict] = []
    exclusion_reason: dict[str, str] = {}
    used_ap_ids: dict[int, str] = {}
    for source in source_doc["sources"]:
        source_id = source["source_id"]
        include, required_rooms, reason = _dc2_source_availability(source, gating)
        room = source.get("provenance", {}).get("room", "")[2:]
        if room and room not in supported:
            include, reason = False, "unsupported_campaign_room"
        if not include:
            exclusion_reason[source_id] = reason
            continue
        iid = _item_id(source["catalog_id"])
        rewrite_class = source["rewrite_class"]
        if _dc2_rewrite_class(iid) != rewrite_class:
            raise AssertionError(
                f"fixture rewrite class mismatch: {source_id} item 0x{iid:02X} "
                f"is {rewrite_class!r}"
            )
        ap_id = _stable_dc2_source_id(source_id)
        if ap_id in used_ap_ids:
            raise AssertionError(
                f"DC2 source AP id collision: {source_id} and {used_ap_ids[ap_id]} -> {ap_id}"
            )
        used_ap_ids[ap_id] = source_id
        short_source = source_id.split(":", 1)[1]
        locations.append({
            "key": source_id,
            "sourceId": source_id,
            "apId": ap_id,
            "name": f"ST{room} - {catalog_names[iid]} [{short_source}]",
            "room": room,
            "itemId": iid,
            "itemName": catalog_names[iid],
            "requiresItems": [],
            "requiresRooms": required_rooms,
            "sourceType": source["source_type"],
            "rewriteClass": rewrite_class,
            "placementClass": (
                f"fixed_lifecycle_{iid:02x}" if iid in fixed_lifecycle_ids else rewrite_class
            ),
            "collectedFlag": f"5:{source['flag5']}",
        })
    locations.sort(key=lambda row: row["sourceId"])
    exclusions = [
        {
            "sourceId": source["source_id"],
            "sourceType": source["source_type"],
            "reason": exclusion_reason[source["source_id"]],
        }
        for source in source_doc["sources"]
        if source["source_id"] in exclusion_reason
    ]
    pool_ids = [row["itemId"] for row in locations]
    pool_counts = Counter(pool_ids)
    return {
        "_generated_by": "scripts/gen_ap_logic.py",
        "_source": "data/dc2/{items,item-sources,door-graph,door-gating,door-guards,"
        "stage-room-map}.json (byte-grounded; K119/K122/K129).",
        "version": 2,
        "topology": "physical",
        "startRoom": "101",
        "goalRoom": "504",
        "victory": {
            "kind": "guarded_door_commit",
            "from": "503",
            "to": "504",
            "commitOff": "0x15f56",
        },
        "excludedRooms": sorted(DC2_EXCLUDED_ROOMS),
        "regions": regions,
        "edges": edges,
        "locations": locations,
        "exclusions": exclusions,
        "conditionalCommitDispositions": dispositions,
        "items": {
            "names": {str(i): name for i, name in sorted(catalog_names.items())},
            "catalog": catalog_rows,
            "catalogItemIds": sorted(catalog_names),
            "keyItemIds": key_ids,
            "progressionItemIds": DC2_PROGRESSION_ITEMS,
            "optionalFixedItemIds": DC2_OPTIONAL_FIXED_ITEMS,
            "fixedLifecycleItemIds": fixed_lifecycle_ids,
            "rewriteClasses": {
                str(iid): rewrite_class
                for iid in sorted(catalog_names)
                if (rewrite_class := _dc2_rewrite_class(iid)) is not None
            },
            "placementClasses": {
                str(iid): (f"fixed_lifecycle_{iid:02x}" if iid in fixed_lifecycle_ids else rewrite_class)
                for iid in sorted(catalog_names)
                if (rewrite_class := _dc2_rewrite_class(iid)) is not None
            },
            "poolItemIds": pool_ids,
            "pool": [
                {"id": iid, "name": catalog_names[iid], "count": count}
                for iid, count in sorted(pool_counts.items())
            ],
        },
    }


def _reachable_rooms(data: dict, held: set[int]) -> set[str]:
    """Simulate either generated topology to a reachability fixpoint.

    DC2 v2 starts at its byte-proven room and follows only physical edges. The historical DC1
    contract retains its Menu-star plus gated-edge overlay. This mirrors each apworld closely
    enough for ``--check`` to prove the authored graph without an Archipelago checkout.
    """
    if data.get("topology") == "physical":
        reach = {data["startRoom"]}
    else:
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
    """Schema/structural invariants shared by every generated game contract.
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
    assert data["version"] == 2 and data["topology"] == "physical", (
        data["version"], data.get("topology"))
    assert data["startRoom"] == "101" and data["goalRoom"] == "504", (
        data["startRoom"], data["goalRoom"])
    assert set(data["excludedRooms"]) == DC2_EXCLUDED_ROOMS
    assert len(data["regions"]) == 85, len(data["regions"])
    assert len(data["locations"]) == 42, len(data["locations"])
    assert len(names) == 58, len(names)
    assert [loc["itemId"] for loc in data["locations"]] == data["items"]["poolItemIds"]
    assert data["items"]["progressionItemIds"] == [0x2E]
    assert data["items"]["optionalFixedItemIds"] == [0x33, 0x34]
    assert all(room in data["regions"] for loc in data["locations"]
               for room in loc["requiresRooms"])
    assert len(data["conditionalCommitDispositions"]) == 91
    assert sum(row["kind"] == "door" for row in data["conditionalCommitDispositions"]) == 27
    assert sum(row["kind"] == "item" for row in data["conditionalCommitDispositions"]) == 64
    gas_edges = [edge for edge in data["edges"] if 0x2E in edge["requiresItems"]]
    assert len(gas_edges) == 1 and (gas_edges[0]["from"], gas_edges[0]["to"]) == ("10D", "10F")
    assert any(edge["from"] == "003" and edge["to"] == "004"
               and edge["disposition"] == "modeled_resolved_computed_destination"
               for edge in data["edges"])
    assert data["victory"] == {
        "kind": "guarded_door_commit", "from": "503", "to": "504",
        "commitOff": "0x15f56",
    }
    progression = set(data["items"]["progressionItemIds"])
    reach_none = _reachable_rooms(data, set())
    reach_all = _reachable_rooms(data, progression)
    assert data["goalRoom"] not in reach_none, "Gas Mask gate does not constrain victory path"
    assert data["goalRoom"] in reach_all, "goal unreachable with all progression items"
    unreachable = {loc["room"] for loc in data["locations"]} - reach_all
    assert not unreachable, f"locations in unreachable rooms: {sorted(unreachable)}"
    assert any(loc["room"] in reach_none for loc in data["locations"]), "no free fill seed"
    assert _forward_fill_beatable(data), "no beatable DC2 progression placement"
    print(
        f"OK: {len(data['regions'])} regions, {len(data['edges'])} edges "
        f"({sum(1 for e in data['edges'] if e['requiresRooms'])} with visit gates), "
        f"{len(data['locations'])} fillable direct sources, {len(data['exclusions'])} exclusions, "
        f"91 conditional-commit dispositions; goal {data['goalRoom']} Gas-Mask gated"
    )


def check_dc1(data: dict) -> None:
    _assert_dc1_parity(require_oracle=True)  # + oracle golden snapshot still agrees with the source
    names = _check_common(data)
    assert data["version"] == 2, data["version"]  # v2 = runtime-client fields (excluded)
    assert data["startRoom"] == "010d", data["startRoom"]
    assert data["goalRoom"] == "060d", data["goalRoom"]
    # Runtime-client check contract: name parity is enforced in build_dc1 (fail-closed); here,
    # non-excluded locations must have pairwise-unique check flags (the client's attribution
    # guarantee) and the excluded set must stay a small tail, never a silent majority.
    checks = _load_client_checks()
    # apId parity: the checks file carries the apworld's sorted-name location ids so the C#
    # client never re-derives the scheme — assert the two derivations agree (id-scheme guard).
    expected_ap = {name: 0x0DC1_0000 + 0x1_0000 + i
                   for i, name in enumerate(sorted(l["name"] for l in data["locations"]))}
    flag_users: dict[int, str] = {}
    excluded = 0
    for loc in data["locations"]:
        e = checks[loc["name"]]
        assert e["apId"] == expected_ap[loc["name"]], (loc["name"], e["apId"])
        assert e["predicate"]["kind"] == "flag" and e["predicate"]["anyOf"], e
        if loc.get("excluded"):
            excluded += 1
            continue
        for f in e["predicate"]["anyOf"]:
            assert f not in flag_users, f"flag 7:{f} shared by '{flag_users[f]}' and '{loc['name']}'"
            assert 0 < f < 256, f
            flag_users[f] = loc["name"]
    assert excluded <= len(data["locations"]) // 4, f"{excluded} excluded locations — checks data suspect"
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
        with out.open("w", encoding="utf-8", newline="\n") as stream:
            stream.write(expected)
        check(data)
        print(f"wrote {out.relative_to(REPO)}")
        return 0
    # Non-mutating derived-data contract: the committed file must equal a fresh regeneration.
    actual = out.open(encoding="utf-8", newline="").read() if out.exists() else None
    if actual != expected:
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
