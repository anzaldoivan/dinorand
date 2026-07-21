"""Load the generated Dino Crisis 2 logic contract for Archipelago."""
from __future__ import annotations

import json
import pkgutil

# pkgutil keeps loading working when the package is distributed as a zipped .apworld.
_DATA = json.loads(pkgutil.get_data(__name__, "data/dc2_logic.json").decode("utf-8"))

VERSION: int = _DATA["version"]
START_ROOM: str = _DATA["startRoom"]
GOAL_ROOM: str = _DATA["goalRoom"]
VICTORY: dict = _DATA["victory"]
REGIONS: dict[str, str] = _DATA["regions"]
EDGES: list[dict] = _DATA["edges"]
LOCATIONS: list[dict] = _DATA["locations"]
EXCLUSIONS: list[dict] = _DATA["exclusions"]

ITEM_NAMES: dict[int, str] = {int(key): value for key, value in _DATA["items"]["names"].items()}
ITEM_CATEGORIES: dict[int, str] = {
    row["id"]: row["category"] for row in _DATA["items"]["catalog"]
}
KEY_ITEM_IDS: set[int] = set(_DATA["items"]["keyItemIds"])
PROGRESSION_ITEM_IDS: set[int] = set(_DATA["items"]["progressionItemIds"])
OPTIONAL_FIXED_ITEM_IDS: set[int] = set(_DATA["items"]["optionalFixedItemIds"])
ITEM_REWRITE_CLASSES: dict[int, str] = {
    int(item_id): rewrite_class
    for item_id, rewrite_class in _DATA["items"]["rewriteClasses"].items()
}
ITEM_PLACEMENT_CLASSES: dict[int, str] = {
    int(item_id): placement_class
    for item_id, placement_class in _DATA["items"]["placementClasses"].items()
}
POOL_ITEM_IDS: list[int] = _DATA["items"]["poolItemIds"]
PROGRESSION_ITEM_NAMES: set[str] = {ITEM_NAMES[item_id] for item_id in PROGRESSION_ITEM_IDS}

# Catalog-backed item ids remain stable when names or unrelated catalog rows change. Location ids
# are generated from each byte-provenanced source identity and therefore remain stable when other
# sources are added or removed.
BASE_ID = 0x0DC2_0000
ITEM_NAME_TO_ID: dict[str, int] = {
    name: BASE_ID + item_id for item_id, name in ITEM_NAMES.items()
}
LOCATION_NAME_TO_ID: dict[str, int] = {
    location["name"]: location["apId"] for location in LOCATIONS
}

OTHER_WORLD_MARKER = -1
GAME_ITEM_ID: dict[str, int] = {name: item_id for item_id, name in ITEM_NAMES.items()}
LOCATION_BY_AP_ID: dict[int, dict] = {location["apId"]: location for location in LOCATIONS}


def region_name(code: str) -> str:
    """Return the unique display name for a physical ST room."""
    return f"{REGIONS.get(code, f'ST{code}')} ({code})"
