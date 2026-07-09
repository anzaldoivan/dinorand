"""Dino Crisis 1 Archipelago world — increment 1 (generation only).

Proves the pipeline: AP can generate a logically-valid DC1 seed from DinoRand's authored logic.
It does NOT patch the game or sync a multiworld — that needs the runtime client (deferred; see
docs/decisions/cross/ARCHIPELAGO-INTEGRATION-FEASIBILITY.md).

Region model (increment 1): star topology + gated-edge overlay. Every room is a region; every
room that is NOT the target of a gated door hangs off "Menu" freely (full room connectivity lives
in the .dat files, not the authored data — deferred). The 16 authored gated edges are applied as
an overlay, so the real key gates (BG Area / C.O. Area / Key Card Lv. A) still constrain the goal.
"""
from __future__ import annotations

from typing import Any, Callable, Mapping

from BaseClasses import CollectionState, Item, ItemClassification, Location, Region
from worlds.AutoWorld import World

from . import data as dc1
from .options import DinoCrisis1Options


class DinoCrisis1Item(Item):
    game = "Dino Crisis 1"


class DinoCrisis1Location(Location):
    game = "Dino Crisis 1"


class DinoCrisis1World(World):
    """Dino Crisis 1 randomizer (DinoRand), Archipelago integration increment 1."""

    game = "Dino Crisis 1"
    options_dataclass = DinoCrisis1Options
    options: DinoCrisis1Options
    item_name_to_id = dc1.ITEM_NAME_TO_ID
    location_name_to_id = dc1.LOCATION_NAME_TO_ID
    origin_region_name = "Menu"

    def create_regions(self) -> None:
        player, mw = self.player, self.multiworld
        menu = Region("Menu", player, mw)
        mw.regions.append(menu)

        regions: dict[str, Region] = {}
        for code in dc1.REGIONS:
            r = Region(dc1.region_name(code), player, mw)
            regions[code] = r
            mw.regions.append(r)

        for loc in dc1.LOCATIONS:
            parent = regions[loc["room"]]
            parent.locations.append(
                DinoCrisis1Location(player, loc["name"], dc1.LOCATION_NAME_TO_ID[loc["name"]], parent)
            )

        gated_targets = {e["to"] for e in dc1.EDGES}
        for code, r in regions.items():
            if code not in gated_targets:
                menu.connect(r)
        for e in dc1.EDGES:
            items = [dc1.ITEM_NAMES[i] for i in e["requiresItems"]]
            rooms = [dc1.region_name(r) for r in e["requiresRooms"]]
            regions[e["from"]].connect(regions[e["to"]], rule=self._edge_rule(items, rooms))

    def _edge_rule(self, items: list[str], rooms: list[str]) -> Callable[[CollectionState], bool]:
        player = self.player

        def rule(state: CollectionState) -> bool:
            return state.has_all(items, player) and all(
                state.can_reach_region(rn, player) for rn in rooms
            )

        return rule

    def set_rules(self) -> None:
        goal = dc1.region_name(dc1.GOAL_ROOM)
        self.multiworld.completion_condition[self.player] = (
            lambda state: state.can_reach_region(goal, self.player)
        )

    def create_item(self, name: str) -> DinoCrisis1Item:
        classification = (
            ItemClassification.progression
            if name in dc1.PROGRESSION_ITEM_NAMES
            else ItemClassification.filler
        )
        return DinoCrisis1Item(name, classification, dc1.ITEM_NAME_TO_ID[name], self.player)

    def create_items(self) -> None:
        pool = [self.create_item(dc1.ITEM_NAMES[i]) for i in dc1.PROGRESSION_ITEM_IDS]
        while len(pool) < len(dc1.LOCATIONS):
            pool.append(self.create_item(self.get_filler_item_name()))
        self.multiworld.itempool += pool

    def get_filler_item_name(self) -> str:
        return self.random.choice(dc1.FILLER_NAMES)

    def fill_slot_data(self) -> Mapping[str, Any]:
        return {"logic_version": dc1.VERSION, "goal_room": dc1.GOAL_ROOM}
