"""Load the distilled DC1 logic contract and derive Archipelago name<->id maps.

The contract (data/dc1_logic.json) is produced by scripts/gen_ap_logic.py from the authored
data/dc1 files. This module is pure data + no Archipelago imports, so it can be unit-tested
(and the id maps inspected) without an AP checkout.
"""
from __future__ import annotations

import json
import pkgutil

# pkgutil (not pathlib) so the contract loads from a zipped .apworld too, not just a folder.
_DATA = json.loads(pkgutil.get_data(__name__, "data/dc1_logic.json").decode("utf-8"))

VERSION: int = _DATA["version"]
START_ROOM: str = _DATA["startRoom"]
GOAL_ROOM: str = _DATA["goalRoom"]
START_NODE: str = _DATA["startNode"]
GOAL_NODE: str = _DATA["goalNode"]
REGIONS: dict[str, str] = _DATA["regions"]        # room code -> wiki name
NODES: list[dict] = _DATA["nodes"]                # {id,room,primary,region}
EDGES: list[dict] = _DATA["edges"]                # physical node edges + requirements/latches
LOCATIONS: list[dict] = _DATA["locations"]        # {key,name,room,itemId,itemName,pos,collectedFlag}
ITEM_NAMES: dict[int, str] = {int(k): v for k, v in _DATA["items"]["names"].items()}
PROGRESSION_ITEM_IDS: list[int] = _DATA["items"]["progressionItemIds"]
COMPLETION_ITEM_IDS: list[int] = _DATA["items"]["completionItemIds"]
PROGRESSION_ITEM_NAMES: set[str] = {ITEM_NAMES[i] for i in PROGRESSION_ITEM_IDS}
FILLER_NAMES: list[str] = [p["name"] for p in _DATA["items"]["pool"]]

# Explicit append-only ids emitted from data/dc1/ap-id-registry.json. Aliases retain published
# display names after an internal/catalog rename without creating a new numeric identity.
ITEM_NAME_TO_ID: dict[str, int] = dict(_DATA["items"]["apIds"])
for _alias, _primary in _DATA["items"].get("aliases", {}).items():
    ITEM_NAME_TO_ID[_alias] = ITEM_NAME_TO_ID[_primary]
LOCATION_NAME_TO_ID: dict[str, int] = {}
for _loc in LOCATIONS:
    LOCATION_NAME_TO_ID[_loc["name"]] = _loc["apId"]
    for _alias in _loc.get("aliases", []):
        LOCATION_NAME_TO_ID[_alias] = _loc["apId"]


# Runtime client (AP-CLIENT-PLAN.md D5): slot_data `placements` value meaning "this location
# holds another world's item" — the install renders it as the marker item (client-side constant;
# the apworld never carries exe knowledge).
OTHER_WORLD_MARKER = -1

# AP item name -> the DC1 game item id the installer writes / the client grants. Names are not
# unique in the raw table (14 costume variants share one name) — take the lowest id per name,
# deterministically, matching ITEM_NAME_TO_ID's unique-name key set.
GAME_ITEM_ID: dict[str, int] = {}
for _gid, _name in sorted(ITEM_NAMES.items()):
    GAME_ITEM_ID.setdefault(_name, _gid)
for _alias, _primary in _DATA["items"].get("aliases", {}).items():
    GAME_ITEM_ID[_alias] = GAME_ITEM_ID[_primary]


def region_name(code: str, region: str | None = None) -> str:
    """Stable room display name, with an explicit suffix for split physical nodes."""
    name = f"{REGIONS.get(code, code)} ({code})"
    return f"{name} [{region}]" if region is not None else name


_NODE_BY_ID: dict[str, dict] = {node["id"]: node for node in NODES}


def node_name(node_id: str) -> str:
    node = _NODE_BY_ID[node_id]
    return region_name(node["room"], node["region"])
