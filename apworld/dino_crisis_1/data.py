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
REGIONS: dict[str, str] = _DATA["regions"]        # room code -> wiki name
EDGES: list[dict] = _DATA["edges"]                # {from,to,requiresItems,requiresRooms}
LOCATIONS: list[dict] = _DATA["locations"]        # {key,name,room,itemId,itemName,pos,collectedFlag}
ITEM_NAMES: dict[int, str] = {int(k): v for k, v in _DATA["items"]["names"].items()}
PROGRESSION_ITEM_IDS: list[int] = _DATA["items"]["progressionItemIds"]
PROGRESSION_ITEM_NAMES: set[str] = {ITEM_NAMES[i] for i in PROGRESSION_ITEM_IDS}
FILLER_NAMES: list[str] = [p["name"] for p in _DATA["items"]["pool"]]

# DinoRand DC1 id space. Ids are assigned deterministically from sorted names so they are
# stable as long as the name set is stable (see the location-id-stability risk in the decision record).
_BASE_ID = 0x0DC1_0000

# item names are not unique (14 costume variants share one name) -> key by the unique set.
ITEM_NAME_TO_ID: dict[str, int] = {
    name: _BASE_ID + i for i, name in enumerate(sorted(set(ITEM_NAMES.values())))
}
LOCATION_NAME_TO_ID: dict[str, int] = {
    loc["name"]: _BASE_ID + 0x1_0000 + i
    for i, loc in enumerate(sorted(LOCATIONS, key=lambda l: l["name"]))
}


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


def region_name(code: str) -> str:
    """Region display name: '<wiki name> (<code>)' — unique because the code is unique."""
    return f"{REGIONS.get(code, code)} ({code})"
