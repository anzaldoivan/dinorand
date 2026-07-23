"""Dino Crisis 1 Archipelago world over DinoRand's physical v3 logic contract."""
from __future__ import annotations

from typing import Any, Callable, Mapping

from BaseClasses import CollectionState, Item, ItemClassification, Location, LocationProgressType, Region
from worlds.AutoWorld import World

from . import data as dc1
from .options import DinoCrisis1Options


class DinoCrisis1Item(Item):
    game = "Dino Crisis 1"


class DinoCrisis1Location(Location):
    game = "Dino Crisis 1"


class DinoCrisis1World(World):
    """Dino Crisis 1 randomizer with physical topology and runtime-client integration."""

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
        room_regions: dict[str, list[Region]] = {}
        for node in dc1.NODES:
            r = Region(dc1.node_name(node["id"]), player, mw)
            regions[node["id"]] = r
            room_regions.setdefault(node["room"], []).append(r)
            mw.regions.append(r)

        for loc in dc1.LOCATIONS:
            parent = regions[loc["node"]]
            ap_loc = DinoCrisis1Location(player, loc["name"], loc["apId"], parent)
            # Shared-taken-flag locations the runtime client cannot attribute individually
            # (ap-client-checks.json `excluded`): keep progression out; the client fires the
            # whole shared group when the flag flips, so anything here must be low-stakes.
            if loc.get("excluded"):
                ap_loc.progress_type = LocationProgressType.EXCLUDED
            required_items = [dc1.ITEM_NAMES[item_id] for item_id in loc["requiresItems"]]
            required_rooms = [room_regions[room] for room in loc["requiresRooms"]]
            ap_loc.access_rule = self._requirement_rule(required_items, [], required_rooms)
            allowed = {dc1.ITEM_NAMES[item_id] for item_id in loc["allowedProgressionItemIds"]}
            if allowed != dc1.PROGRESSION_ITEM_NAMES:
                ap_loc.item_rule = lambda item, allowed=allowed: (
                    not item.advancement
                    or (item.game == self.game and item.name in allowed)
                )
            parent.locations.append(ap_loc)

        menu.connect(regions[dc1.START_NODE])
        latch_setters: dict[int, list[dict]] = {}
        for edge in dc1.EDGES:
            if edge["setsLatch"] is not None:
                latch_setters.setdefault(edge["setsLatch"], []).append(edge)
        for edge_index, e in enumerate(dc1.EDGES):
            if e["from"] == e["to"]:
                continue  # physical same-node doors add no AP reachability and AP rejects self-connects
            entrance_name = (
                f"{regions[e['from']].name} -> {regions[e['to']].name} "
                f"[edge {edge_index}]"
            )
            entrance = regions[e["from"]].connect(
                regions[e["to"]],
                name=entrance_name,
                rule=self._edge_rule(e, room_regions, latch_setters),
            )
            # A rule that calls state.can_reach_region must register the dependency, or AP's
            # region cache can serve stale reachability during fill (several requiresRooms
            # targets are themselves gated regions).
            indirect = [region for room in e["requiresRooms"] for region in room_regions[room]]
            if e["requiresLatch"] is not None:
                for setter in latch_setters.get(e["requiresLatch"], []):
                    indirect.append(regions[setter["from"]])
                    indirect.extend(region for room in setter["requiresRooms"]
                                    for region in room_regions[room])
            for region in dict.fromkeys(indirect):
                mw.register_indirect_condition(region, entrance)

    def _requirement_rule(self, items: list[str], any_items: list[str],
                          rooms: list[list[Region]]) -> Callable[[CollectionState], bool]:
        player = self.player

        def rule(state: CollectionState) -> bool:
            return (state.has_all(items, player)
                    and (not any_items or any(state.has(item, player) for item in any_items))
                    and all(any(state.can_reach_region(region.name, player) for region in variants)
                            for variants in rooms))

        return rule

    def _edge_rule(self, edge: dict, room_regions: dict[str, list[Region]],
                   latch_setters: dict[int, list[dict]]) -> Callable[[CollectionState], bool]:
        player = self.player
        items = [dc1.ITEM_NAMES[item_id] for item_id in edge["requiresItems"]]
        any_items = [dc1.ITEM_NAMES[item_id] for item_id in edge["requiresAnyItems"]]
        rooms = [room_regions[room] for room in edge["requiresRooms"]]
        base = self._requirement_rule(items, any_items, rooms)
        latch = edge["requiresLatch"]
        if latch is None:
            return base

        setter_rules = [
            (dc1.node_name(setter["from"]), self._requirement_rule(
                [dc1.ITEM_NAMES[item_id] for item_id in setter["requiresItems"]],
                [dc1.ITEM_NAMES[item_id] for item_id in setter["requiresAnyItems"]],
                [room_regions[room] for room in setter["requiresRooms"]],
            ))
            for setter in latch_setters.get(latch, [])
        ]

        def rule(state: CollectionState) -> bool:
            return base(state) and any(
                state.can_reach_region(source, player) and setter_rule(state)
                for source, setter_rule in setter_rules
            )

        return rule

    def set_rules(self) -> None:
        goal = dc1.node_name(dc1.GOAL_NODE)
        completion_items = [dc1.ITEM_NAMES[i] for i in dc1.COMPLETION_ITEM_IDS]
        self.multiworld.completion_condition[self.player] = (
            lambda state: state.can_reach_region(goal, self.player)
            and state.has_all(completion_items, self.player)
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
        # Loop-closing channel (AP-CLIENT-PLAN.md D5): the client patches AP's fill into the
        # local install from `placements` — {AP location id: DC1 game item id}, with
        # OTHER_WORLD_MARKER for items that belong to another slot/game (rendered locally as
        # the marker item; picking it up is the check, it grants nothing). `item_ids` maps AP
        # item ids -> DC1 game ids so the client grants ReceivedItems without name parsing.
        # JSON object keys must be strings (slot_data round-trips through JSON).
        placements: dict[str, int] = {}
        for loc in self.multiworld.get_locations(self.player):
            item = loc.item
            if item is None or loc.address is None:
                continue  # events (none today) — never part of the patch surface
            placements[str(loc.address)] = (
                dc1.GAME_ITEM_ID[item.name] if item.player == self.player and item.game == self.game
                else dc1.OTHER_WORLD_MARKER
            )
        return {
            "logic_version": dc1.VERSION,
            "goal_room": dc1.GOAL_ROOM,
            "placements": placements,
            "item_ids": {str(ap_id): dc1.GAME_ITEM_ID[name] for name, ap_id in dc1.ITEM_NAME_TO_ID.items()},
        }
