"""Load the distilled DC2 logic contract and derive Archipelago name<->id maps.

The contract (data/dc2_logic.json) is produced by scripts/gen_ap_logic.py. Today it is an EMPTY
stub (DC2 has a decoded door-graph + item spots but no key-item ids / door→key gating yet), so the
maps below come out empty and the world ships only a free "Victory" event. The moment the DC2
builder populates the contract, this module and world.py light up with no structural change.

Mirror of dino_crisis_1/data.py — kept self-contained because AP apworlds are separate zips that
cannot share runtime code (imports are relative within a package). The shared machinery is the
repo-side distiller, not this file.
"""
from __future__ import annotations

import json
from pathlib import Path

_DATA = json.loads((Path(__file__).parent / "data" / "dc2_logic.json").read_text(encoding="utf-8"))

VERSION: int = _DATA["version"]
START_ROOM: str = _DATA["startRoom"]
GOAL_ROOM: str = _DATA["goalRoom"]
REGIONS: dict[str, str] = _DATA["regions"]        # room code -> name (empty in the stub)
EDGES: list[dict] = _DATA["edges"]                # {from,to,requiresItems,requiresRooms}
LOCATIONS: list[dict] = _DATA["locations"]        # {key,name,room,itemId,itemName,pos,collectedFlag}
ITEM_NAMES: dict[int, str] = {int(k): v for k, v in _DATA["items"]["names"].items()}
PROGRESSION_ITEM_IDS: list[int] = _DATA["items"]["progressionItemIds"]
PROGRESSION_ITEM_NAMES: set[str] = {ITEM_NAMES[i] for i in PROGRESSION_ITEM_IDS}
FILLER_NAMES: list[str] = [p["name"] for p in _DATA["items"]["pool"]]

# DinoRand DC2 id space (distinct from DC1's 0x0DC1_0000). Ids are assigned deterministically from
# sorted names so they are stable as long as the name set is stable.
_BASE_ID = 0x0DC2_0000

ITEM_NAME_TO_ID: dict[str, int] = {
    name: _BASE_ID + i for i, name in enumerate(sorted(set(ITEM_NAMES.values())))
}
LOCATION_NAME_TO_ID: dict[str, int] = {
    loc["name"]: _BASE_ID + 0x1_0000 + i
    for i, loc in enumerate(sorted(LOCATIONS, key=lambda l: l["name"]))
}


def region_name(code: str) -> str:
    """Region display name: '<name> (<code>)' — unique because the code is unique."""
    return f"{REGIONS.get(code, code)} ({code})"
