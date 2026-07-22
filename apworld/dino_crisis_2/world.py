"""Dino Crisis 2 Archipelago world backed by the generated physical-room contract."""
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
    """Dino Crisis 2 item randomizer and physical-graph Archipelago integration."""

    game = "Dino Crisis 2"
    options_dataclass = DinoCrisis2Options
    options: DinoCrisis2Options
    item_name_to_id = dc2.ITEM_NAME_TO_ID
    location_name_to_id = dc2.LOCATION_NAME_TO_ID
    origin_region_name = "Menu"

    def create_regions(self) -> None:
        player, multiworld = self.player, self.multiworld
        menu = Region("Menu", player, multiworld)
        multiworld.regions.append(menu)

        regions: dict[str, Region] = {}
        for code in dc2.REGIONS:
            region = Region(dc2.region_name(code), player, multiworld)
            regions[code] = region
            multiworld.regions.append(region)

        menu.connect(regions[dc2.START_ROOM], name=f"Start -> ST{dc2.START_ROOM}")

        for edge in dc2.EDGES:
            entrance = regions[edge["from"]].connect(
                regions[edge["to"]],
                name=edge["id"],
                rule=self._requirement_rule(edge["requiresItems"], edge["requiresRooms"]),
            )
            # Room requirements depend on a second region's reachability. Register that dependency
            # so Archipelago invalidates the entrance cache when the required region becomes live.
            for required_room in edge["requiresRooms"]:
                multiworld.register_indirect_condition(regions[required_room], entrance)

        for location in dc2.LOCATIONS:
            parent = regions[location["room"]]
            ap_location = DinoCrisis2Location(
                player, location["name"], location["apId"], parent
            )
            ap_location.access_rule = self._requirement_rule(
                location["requiresItems"], location["requiresRooms"]
            )
            ap_location.item_rule = self._item_rule(location["placementClass"])
            parent.locations.append(ap_location)

        goal = regions[dc2.GOAL_ROOM]
        victory = DinoCrisis2Location(player, VICTORY, None, goal)
        victory.place_locked_item(
            DinoCrisis2Item(VICTORY, ItemClassification.progression, None, player)
        )
        goal.locations.append(victory)

    def _requirement_rule(
        self, item_ids: list[int], room_codes: list[str]
    ) -> Callable[[CollectionState], bool]:
        player = self.player
        item_names = [dc2.ITEM_NAMES[item_id] for item_id in item_ids]
        room_names = [dc2.region_name(room) for room in room_codes]

        def rule(state: CollectionState) -> bool:
            return state.has_all(item_names, player) and all(
                state.can_reach_region(room, player) for room in room_names
            )

        return rule

    def _item_rule(self, placement_class: str) -> Callable[[Item], bool]:
        """Keep every static-install placement local and within its compatibility class."""
        player, game = self.player, self.game

        def rule(item: Item) -> bool:
            if item.player != player or item.game != game:
                return False
            item_id = dc2.GAME_ITEM_ID.get(item.name)
            return dc2.ITEM_PLACEMENT_CLASSES.get(item_id) == placement_class

        return rule

    def set_rules(self) -> None:
        self.multiworld.completion_condition[self.player] = (
            lambda state: state.has(VICTORY, self.player)
        )

    def create_item(self, name: str) -> DinoCrisis2Item:
        item_id = dc2.GAME_ITEM_ID[name]
        if item_id in dc2.PROGRESSION_ITEM_IDS:
            classification = ItemClassification.progression
        elif item_id in dc2.KEY_ITEM_IDS:
            classification = ItemClassification.useful
        else:
            classification = ItemClassification.filler
        return DinoCrisis2Item(name, classification, dc2.ITEM_NAME_TO_ID[name], self.player)

    def create_items(self) -> None:
        # The generated pool is an exact multiset of the original items at supported sources.
        self.multiworld.itempool += [
            self.create_item(dc2.ITEM_NAMES[item_id]) for item_id in dc2.POOL_ITEM_IDS
        ]

    def get_filler_item_name(self) -> str:
        filler = [
            dc2.ITEM_NAMES[item_id]
            for item_id in dc2.POOL_ITEM_IDS
            if item_id not in dc2.KEY_ITEM_IDS
        ]
        return self.random.choice(filler)

    def fill_slot_data(self) -> Mapping[str, Any]:
        placements: dict[str, int] = {}
        for location in self.multiworld.get_locations(self.player):
            if location.address is None or location.item is None:
                continue
            item = location.item
            placements[str(location.address)] = (
                dc2.GAME_ITEM_ID[item.name]
                if item.player == self.player and item.game == self.game
                else dc2.OTHER_WORLD_MARKER
            )

        return {
            "logic_version": dc2.VERSION,
            "start_room": dc2.START_ROOM,
            "goal_room": dc2.GOAL_ROOM,
            "victory": dc2.VICTORY,
            "placements": placements,
            "item_ids": {
                str(ap_id): dc2.GAME_ITEM_ID[name]
                for name, ap_id in dc2.ITEM_NAME_TO_ID.items()
            },
            "source_ids": {
                str(location["apId"]): location["sourceId"] for location in dc2.LOCATIONS
            },
            "exclusions": dc2.EXCLUSIONS,
        }
