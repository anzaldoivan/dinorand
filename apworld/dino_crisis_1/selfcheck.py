"""AP-INDEPENDENT self-check for DinoCrisis1World generation (no Archipelago install needed).

Named without a `test_` prefix on purpose: it lives inside the shipped world package, and must
NOT be picked up by pytest when the package is dropped into a real Archipelago checkout (it stubs
`sys.modules`, which would clobber a real AP run). The real, AP-side integration tests live in the
`test/` subpackage and run under Archipelago's own test runner.

This stubs just enough of the AP API to drive create_regions()/create_items() and assert the world's
structural wiring (no self-loops, region/itempool counts, gate overlay). Logic/solvability (gate
bites, forward-fill beatability) is covered AP-independently by `scripts/gen_ap_logic.py --check`.

Run:  python3 apworld/dino_crisis_1/selfcheck.py
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
            return (self, other)  # stands in for the Entrance world.py registers indirect conditions on

    class Location:
        def __init__(self, player, name, code, parent):
            self.player, self.name, self.code, self.parent_region = player, name, code, parent

    class Item:
        def __init__(self, name, classification, code, player):
            self.name, self.classification, self.code, self.player = name, classification, code, player

    class ItemClassification:
        progression = "progression"
        filler = "filler"

    class CollectionState:  # only referenced in type hints / rule bodies, never built here
        pass

    class LocationProgressType:
        DEFAULT, PRIORITY, EXCLUDED = 1, 2, 3  # mirrors AP's enum values

    base.Region, base.Location, base.Item = Region, Location, Item
    base.ItemClassification, base.CollectionState = ItemClassification, CollectionState
    base.LocationProgressType = LocationProgressType
    sys.modules["BaseClasses"] = base

    worlds = types.ModuleType("worlds")
    worlds.__path__ = []
    autoworld = types.ModuleType("worlds.AutoWorld")

    class World:
        def __init__(self, multiworld, player):
            self.multiworld, self.player = multiworld, player
            self.random = random.Random(1)

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
        self.indirect_connections = []

    def register_indirect_condition(self, region, entrance):
        self.indirect_connections.append((region, entrance))


def main() -> int:
    _install_ap_stubs()
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))  # so `dino_crisis_1` is a package
    from dino_crisis_1 import data as dc1
    from dino_crisis_1.world import DinoCrisis1World

    mw = _MultiWorld()
    world = DinoCrisis1World(mw, player=1)
    world.create_regions()
    world.set_rules()
    world.create_items()

    assert len(mw.regions) == len(dc1.REGIONS) + 1, "expected one region per room plus Menu"
    total_locs = sum(len(r.locations) for r in mw.regions)
    assert total_locs == len(dc1.LOCATIONS), (total_locs, len(dc1.LOCATIONS))

    menu = next(r for r in mw.regions if r.name == "Menu")
    gated_targets = {dc1.region_name(e["to"]) for e in dc1.EDGES}
    menu_dsts = {other.name for other, _ in menu.exits}
    assert menu_dsts.isdisjoint(gated_targets), "a gated room is reachable free from Menu"

    assert len(mw.itempool) == len(dc1.LOCATIONS), (len(mw.itempool), len(dc1.LOCATIONS))
    prog = [i for i in mw.itempool if i.classification == "progression"]
    assert len(prog) == len(dc1.PROGRESSION_ITEM_IDS), len(prog)
    assert {i.name for i in prog} == dc1.PROGRESSION_ITEM_NAMES
    assert 1 in mw.completion_condition, "completion condition not set"

    print(
        f"selfcheck OK: {len(mw.regions)} regions, {total_locs} locations, "
        f"{len(mw.itempool)} items ({len(prog)} progression), Menu -> {len(menu_dsts)} free rooms"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
