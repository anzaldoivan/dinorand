"""Integration tests: Archipelago generation over the physical DC2 world.

WorldTestBase also contributes AP's generic tests automatically (test_fill,
test_all_state_can_reach_everything, test_empty_state_can_reach_something, id uniqueness).
"""
from .. import data as dc2
from .bases import DinoCrisis2TestBase


class TestGeneration(DinoCrisis2TestBase):
    options: dict = {}

    def test_victory_present_and_beatable(self) -> None:
        victory = self.multiworld.get_location("Victory", 1)
        self.assertEqual(victory.parent_region.name, dc2.region_name(dc2.GOAL_ROOM))
        self.collect_all_but([])
        self.assertBeatable(True)

    def test_fillable_locations_match_contract(self) -> None:
        real = [loc for loc in self.multiworld.get_locations(1) if loc.address is not None]
        self.assertEqual(
            sorted(loc.name for loc in real),
            sorted(loc["name"] for loc in dc2.LOCATIONS),
        )

    def test_item_pool_matches_contract_multiset(self) -> None:
        self.assertEqual(
            sorted(item.name for item in self.multiworld.itempool),
            sorted(dc2.ITEM_NAMES[item_id] for item_id in dc2.POOL_ITEM_IDS),
        )

    def test_local_items_are_restricted_to_writer_compatibility_class(self) -> None:
        for row in dc2.LOCATIONS:
            location = self.multiworld.get_location(row["name"], 1)
            self.assertTrue(location.item_rule(self.world.create_item(row["itemName"])))
            incompatible_id = next(
                item_id for item_id in dc2.POOL_ITEM_IDS
                if dc2.ITEM_PLACEMENT_CLASSES[item_id] != row["placementClass"]
            )
            self.assertFalse(
                location.item_rule(self.world.create_item(dc2.ITEM_NAMES[incompatible_id]))
            )
            remote = self.world.create_item(row["itemName"])
            remote.player = 2
            self.assertFalse(location.item_rule(remote))

    def test_catalog_and_location_ids_are_contract_backed(self) -> None:
        self.assertEqual(
            dc2.ITEM_NAME_TO_ID,
            {name: dc2.BASE_ID + item_id for item_id, name in dc2.ITEM_NAMES.items()},
        )
        self.assertEqual(
            dc2.LOCATION_NAME_TO_ID,
            {loc["name"]: loc["apId"] for loc in dc2.LOCATIONS},
        )

    def test_slot_data_closes_install_contract(self) -> None:
        from Fill import distribute_items_restrictive

        distribute_items_restrictive(self.multiworld)
        slot_data = self.world.fill_slot_data()
        self.assertEqual(slot_data["logic_version"], 2)
        self.assertEqual(slot_data["start_room"], "101")
        self.assertEqual(slot_data["goal_room"], "504")
        self.assertEqual(slot_data["victory"], dc2.VICTORY)
        self.assertEqual(slot_data["exclusions"], dc2.EXCLUSIONS)
        self.assertEqual(
            slot_data["source_ids"],
            {str(loc["apId"]): loc["sourceId"] for loc in dc2.LOCATIONS},
        )
        placements = slot_data["placements"]
        self.assertEqual(len(placements), len(dc2.LOCATIONS))
        self.assertEqual(
            set(placements),
            {str(loc["apId"]) for loc in dc2.LOCATIONS},
        )
