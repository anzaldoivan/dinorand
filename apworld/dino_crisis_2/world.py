"""Dino Crisis 2 Archipelago world — STUB (generation only, empty logic).

DC2 has a decoded door-graph + item spots (data/dc2) but no key-item ids or door→key gating yet
(KaQ OPEN #2/#5), so there is no logically-gated graph to shuffle. This world exists to prove the
DC2 pipeline registers and GENERATES under AP: it builds whatever the contract carries (empty
today) plus a free "Victory" event so the seed is completable.

When the DC2 builder populates data/dc2_logic.json (regions/locations/edges/items), this world
lights up unchanged — the create_regions/set_rules/create_items bodies are already contract-driven.
The only stub-specific bit is the free Victory event, which becomes gated on the real goal room the
moment one exists (see the `# ponytail` note in set_rules).

Self-contained on purpose (mirror of dino_crisis_1/world.py): AP apworlds are separate zips and
cannot import each other's runtime code. The shared machinery is the repo-side distiller.
"""
from __future__ import annotations

from typing import Any, Callable, Mapping

from BaseClasses import CollectionState, Item, ItemClassification, Location, Region
from worlds.AutoWorld import World

from . import data as dc2
from .options import DinoCrisis2Options

VICTORY = "Victory"


class DinoCrisis2Item(Item):
    game = "Dino Crisis 2"


class DinoCrisis2Location(Location):
    game = "Dino Crisis 2"


class DinoCrisis2World(World):
    """Dino Crisis 2 randomizer (DinoRand), Archipelago integration stub."""

    game = "Dino Crisis 2"
    options_dataclass = DinoCrisis2Options
    options: DinoCrisis2Options
    item_name_to_id = dc2.ITEM_NAME_TO_ID
    location_name_to_id = dc2.LOCATION_NAME_TO_ID
    origin_region_name = "Menu"

    def create_regions(self) -> None:
        player, mw = self.player, self.multiworld
        menu = Region("Menu", player, mw)
        mw.regions.append(menu)

        regions: dict[str, Region] = {}
        for code in dc2.REGIONS:
            r = Region(dc2.region_name(code), player, mw)
            regions[code] = r
            mw.regions.append(r)

        for loc in dc2.LOCATIONS:
            parent = regions[loc["room"]]
            parent.locations.append(
                DinoCrisis2Location(player, loc["name"], dc2.LOCATION_NAME_TO_ID[loc["name"]], parent)
            )

        gated_targets = {e["to"] for e in dc2.EDGES}
        for code, r in regions.items():
            if code not in gated_targets:
                menu.connect(r)
        for e in dc2.EDGES:
            items = [dc2.ITEM_NAMES[i] for i in e["requiresItems"]]
            rooms = [dc2.region_name(r) for r in e["requiresRooms"]]
            regions[e["from"]].connect(regions[e["to"]], rule=self._edge_rule(items, rooms))

        # Victory event: an id-less locked item on a free location so an empty (or populated) DC2
        # world always has a completion target. ponytail: gated on nothing while there are no rooms;
        # when the goal room becomes a real region, gate this on reaching it (see set_rules).
        victory = DinoCrisis2Location(player, VICTORY, None, menu)
        victory.place_locked_item(DinoCrisis2Item(VICTORY, ItemClassification.progression, None, player))
        menu.locations.append(victory)

    def _edge_rule(self, items: list[str], rooms: list[str]) -> Callable[[CollectionState], bool]:
        player = self.player

        def rule(state: CollectionState) -> bool:
            return state.has_all(items, player) and all(
                state.can_reach_region(rn, player) for rn in rooms
            )

        return rule

    def set_rules(self) -> None:
        player = self.player
        goal = dc2.GOAL_ROOM
        if goal in dc2.REGIONS:
            # Populated DC2: Victory requires reaching the real goal room.
            goal_region = dc2.region_name(goal)
            self.get_location(VICTORY).access_rule = (
                lambda state: state.can_reach_region(goal_region, player)
            )
        self.multiworld.completion_condition[player] = lambda state: state.has(VICTORY, player)

    def create_item(self, name: str) -> DinoCrisis2Item:
        classification = (
            ItemClassification.progression
            if name in dc2.PROGRESSION_ITEM_NAMES
            else ItemClassification.filler
        )
        return DinoCrisis2Item(name, classification, dc2.ITEM_NAME_TO_ID[name], self.player)

    def create_items(self) -> None:
        # One item per fillable (non-event) location. Empty in the stub.
        pool = [self.create_item(dc2.ITEM_NAMES[i]) for i in dc2.PROGRESSION_ITEM_IDS]
        while len(pool) < len(dc2.LOCATIONS):
            pool.append(self.create_item(self.get_filler_item_name()))
        self.multiworld.itempool += pool

    def get_filler_item_name(self) -> str:
        return self.random.choice(dc2.FILLER_NAMES) if dc2.FILLER_NAMES else VICTORY

    def fill_slot_data(self) -> Mapping[str, Any]:
        return {"logic_version": dc2.VERSION, "goal_room": dc2.GOAL_ROOM}
