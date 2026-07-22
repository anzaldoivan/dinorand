"""AP-independent self-check for the generated Dino Crisis 2 world.

Drives the world against a small Archipelago API stub and verifies the physical room graph, exact
item multiset, guarded Victory event, and installer-facing slot-data contract.

Named without a `test_` prefix so pytest inside a real Archipelago checkout won't collect it.

Run:  python3 apworld/dino_crisis_2/selfcheck.py
"""
import random
import sys
import types
from pathlib import Path


def _install_ap_stubs() -> None:
    base = types.ModuleType("BaseClasses")

    class Region:
        def __init__(self, name, player, multiworld):
            self.name, self.player, self.multiworld = name, player, multiworld
            self.locations, self.exits = [], []

        def connect(self, other, name=None, rule=None):
            assert other is not self, f"self-loop on region {self.name}"
            self.exits.append((other, rule))

    class Location:
        def __init__(self, player, name, code, parent):
            self.player, self.name, self.code, self.address = player, name, code, code
            self.parent_region = parent
            self.locked_item = None
            self.item = None
            self.access_rule = lambda state: True

        def place_locked_item(self, item):
            self.locked_item = item
            self.item = item

    class Item:
        def __init__(self, name, classification, code, player):
            self.name, self.classification, self.code, self.player = name, classification, code, player

    class ItemClassification:
        progression = "progression"
        useful = "useful"
        filler = "filler"

    class CollectionState:  # only referenced in type hints / rule bodies, never built here
        pass

    base.Region, base.Location, base.Item = Region, Location, Item
    base.ItemClassification, base.CollectionState = ItemClassification, CollectionState
    sys.modules["BaseClasses"] = base

    worlds = types.ModuleType("worlds")
    worlds.__path__ = []
    autoworld = types.ModuleType("worlds.AutoWorld")

    class World:
        def __init__(self, multiworld, player):
            self.multiworld, self.player = multiworld, player
            self.random = random.Random(1)

        def get_location(self, name):
            for r in self.multiworld.regions:
                for loc in r.locations:
                    if loc.name == name:
                        return loc
            raise KeyError(name)

    autoworld.World = World
    sys.modules["worlds"] = worlds
    sys.modules["worlds.AutoWorld"] = autoworld

    options = types.ModuleType("Options")

    class PerGameCommonOptions:
        pass

    options.PerGameCommonOptions = PerGameCommonOptions
    sys.modules["Options"] = options


class _MultiWorld:
    def __init__(self):
        self.regions, self.itempool, self.completion_condition = [], [], {}

    def get_locations(self, player):
        return [loc for region in self.regions for loc in region.locations if loc.player == player]

    def register_indirect_condition(self, region, entrance):
        pass


def main() -> int:
    _install_ap_stubs()
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))  # so `dino_crisis_2` is a package
    from dino_crisis_2 import data as dc2
    from dino_crisis_2.world import DinoCrisis2World, VICTORY

    mw = _MultiWorld()
    world = DinoCrisis2World(mw, player=1)
    world.create_regions()
    world.set_rules()
    world.create_items()

    assert len(mw.regions) == len(dc2.REGIONS) + 1, "expected one region per room plus Menu"

    menu = next(region for region in mw.regions if region.name == "Menu")
    assert len(menu.exits) == 1, "Menu must connect only to the proven ST101 start"
    assert menu.exits[0][0].name == dc2.region_name(dc2.START_ROOM)

    # The Victory event is locked in the selected ST504 goal region.
    all_locs = [loc for r in mw.regions for loc in r.locations]
    events = [loc for loc in all_locs if loc.code is None]
    assert len(events) == 1 and events[0].name == VICTORY, "expected exactly one Victory event"
    assert events[0].locked_item is not None and events[0].locked_item.name == VICTORY
    assert events[0].parent_region.name == dc2.region_name(dc2.GOAL_ROOM)

    # Fillable locations + exact source-item pool stay balanced.
    fillable = [loc for loc in all_locs if loc.code is not None]
    assert len(fillable) == len(dc2.LOCATIONS), (len(fillable), len(dc2.LOCATIONS))
    assert len(mw.itempool) == len(dc2.LOCATIONS), (len(mw.itempool), len(dc2.LOCATIONS))
    assert sorted(item.name for item in mw.itempool) == sorted(
        dc2.ITEM_NAMES[item_id] for item_id in dc2.POOL_ITEM_IDS
    )
    # D3 item-ID writes are class-preserving. AP's generic fill must never seat one of this
    # world's health items in a key operand (or vice versa), because the static installer would
    # correctly reject that placement.
    fillable_by_name = {location.name: location for location in fillable}
    for row in dc2.LOCATIONS:
        location = fillable_by_name[row["name"]]
        vanilla = world.create_item(dc2.ITEM_NAMES[row["itemId"]])
        assert location.item_rule(vanilla), row["sourceId"]
        incompatible_id = next(
            item_id for item_id in dc2.POOL_ITEM_IDS
            if dc2.ITEM_PLACEMENT_CLASSES[item_id] != row["placementClass"]
        )
        incompatible = world.create_item(dc2.ITEM_NAMES[incompatible_id])
        assert not location.item_rule(incompatible), row["sourceId"]
        remote = world.create_item(dc2.ITEM_NAMES[row["itemId"]])
        remote.player = 2
        assert not location.item_rule(remote), row["sourceId"]
    assert 1 in mw.completion_condition, "completion condition not set"

    # Simulate a completed AP fill so slot_data can close the install/runtime loop.
    for location, item in zip(sorted(fillable, key=lambda loc: loc.address), mw.itempool):
        location.item = item
    slot_data = world.fill_slot_data()
    assert slot_data["logic_version"] == 2
    assert slot_data["start_room"] == "101"
    assert slot_data["goal_room"] == "504"
    assert slot_data["victory"] == dc2.VICTORY
    assert len(slot_data["placements"]) == len(fillable)
    assert len(slot_data["source_ids"]) == len(fillable)
    assert slot_data["exclusions"] == dc2.EXCLUSIONS
    assert set(slot_data["item_ids"].values()) == set(dc2.ITEM_NAMES)

    print(
        f"selfcheck OK: {len(mw.regions)} regions, {len(fillable)} fillable locations + "
        f"1 Victory event, {len(mw.itempool)} items"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
