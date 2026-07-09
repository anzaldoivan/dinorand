"""AP-INDEPENDENT self-check for DinoCrisis2World generation (no Archipelago install needed).

Mirror of dino_crisis_1/selfcheck.py. Drives create_regions()/set_rules()/create_items() against a
minimal AP stub and asserts the stub world wires up: only the Menu region, a single locked "Victory"
event location, an empty item pool, and a completion condition set. When the DC2 contract is
populated later, extend these assertions the way DC1's did.

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
            self.player, self.name, self.code, self.parent_region = player, name, code, parent
            self.locked_item = None
            self.access_rule = lambda state: True

        def place_locked_item(self, item):
            self.locked_item = item

    class Item:
        def __init__(self, name, classification, code, player):
            self.name, self.classification, self.code, self.player = name, classification, code, player

    class ItemClassification:
        progression = "progression"
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

    # The Victory event: a single id-less locked location that makes the world beatable.
    all_locs = [loc for r in mw.regions for loc in r.locations]
    events = [loc for loc in all_locs if loc.code is None]
    assert len(events) == 1 and events[0].name == VICTORY, "expected exactly one Victory event"
    assert events[0].locked_item is not None and events[0].locked_item.name == VICTORY

    # Fillable locations + itempool stay balanced (both empty in the stub).
    fillable = [loc for loc in all_locs if loc.code is not None]
    assert len(fillable) == len(dc2.LOCATIONS), (len(fillable), len(dc2.LOCATIONS))
    assert len(mw.itempool) == len(dc2.LOCATIONS), (len(mw.itempool), len(dc2.LOCATIONS))
    assert 1 in mw.completion_condition, "completion condition not set"

    print(
        f"selfcheck OK: {len(mw.regions)} regions, {len(fillable)} fillable locations + "
        f"1 Victory event, {len(mw.itempool)} items"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
