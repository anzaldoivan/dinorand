"""Integration tests: Archipelago generation over DinoRand's authored DC1 logic.

These construct a real MultiWorld and exercise AP's fill/reachability against the world built by
DinoCrisis1World. WorldTestBase also contributes AP's generic tests automatically:
  - test_fill                          a real fill of the seed completes
  - test_all_state_can_reach_everything  every location reachable with the full inventory
  - test_empty_state_can_reach_something the fill has reachable spots to seed from

The custom tests below lock in the DinoRand-specific logic (the Key Card Lv. A gate on the goal, that
every door-gating item is progression, and the DDK disc AND-gates). Run these in CI against a pinned
Archipelago checkout (.github/workflows/apworld-checks.yml) so the shipped world — not just a python
re-simulation — is validated the AP way (GRAPH-LOGIC-PARITY parity contract).
"""
from BaseClasses import Item, ItemClassification

from .. import data as dc1
from ..world import DinoCrisis1Item
from .bases import DinoCrisis1TestBase

# One of the goal-room (Underground Heliport, 060d) checks; all of them sit behind the Key Card gate.
GOAL_ROOM_LOCATION = "Underground Heliport (060d) - Plug @-2816,10368"


def _first_location_in(room: str) -> str:
    """A deterministic AP location name in a room (sorted) — avoids brittle hardcoded pickup strings."""
    locs = sorted(loc["name"] for loc in dc1.LOCATIONS if loc["room"] == room)
    assert locs, f"no distilled location in room {room}"
    return locs[0]


class TestDefaultGeneration(DinoCrisis1TestBase):
    options: dict = {}

    def test_goal_room_requires_key_card(self) -> None:
        # Reaching the goal room (and thus completing) must depend on Key Card Lv. A, and nothing
        # else in the pool should substitute for it.
        self.assertAccessDependency(
            [GOAL_ROOM_LOCATION],
            [["Key Card Lv. A"]],
            only_check_listed=True,
        )

    def test_not_beatable_without_key_card(self) -> None:
        self.collect_all_but(["Key Card Lv. A"])
        self.assertBeatable(False)

    def test_not_beatable_without_entrance_key(self) -> None:
        self.collect_all_but(["Entrance Key"])
        self.assertBeatable(False)

    def test_beatable_with_everything(self) -> None:
        self.collect_all_but([])
        self.assertBeatable(True)

    def test_key_items_are_progression(self) -> None:
        for name in ("BG Area Key", "C. O. Area Key", "Key Card Lv. A"):
            items = self.get_items_by_name(name)
            self.assertTrue(items, f"{name} missing from item pool")
            self.assertTrue(all(i.advancement for i in items), f"{name} not progression")

    def test_all_gate_items_are_progression(self) -> None:
        # EVERY item that gates a physical edge or pickup (door-type OR gates, puzzle AND gates,
        # F.C. Device, and all 14 DDK discs)
        # must be an AP progression item — otherwise fill can leave a required key behind its own gate.
        gate_names = {
            dc1.ITEM_NAMES[i]
            for edge in dc1.EDGES
            for i in edge["requiresItems"] + edge["requiresAnyItems"]
        }
        self.assertTrue(gate_names, "no gate items found in the contract — distiller regression?")
        for name in sorted(gate_names):
            items = self.get_items_by_name(name)
            self.assertTrue(items, f"{name} gates an edge but is absent from the item pool")
            self.assertTrue(all(i.advancement for i in items),
                            f"{name} gates an edge but is not a progression item")
        location_gate_names = {
            dc1.ITEM_NAMES[item_id]
            for location in dc1.LOCATIONS
            for item_id in location["requiresItems"]
        }
        for name in sorted(location_gate_names):
            items = self.get_items_by_name(name)
            self.assertTrue(items, f"{name} guards a location but is absent from the item pool")
            self.assertTrue(all(item.advancement for item in items),
                            f"{name} guards a location but is not progression")

    def test_excluded_locations_hold_no_progression(self) -> None:
        # Shared-taken-flag locations (ap-client-checks.json `excluded`, dc1_logic v3) cannot be
        # attributed individually by the runtime client — fill must keep progression out.
        from Fill import distribute_items_restrictive
        excluded = [loc["name"] for loc in dc1.LOCATIONS if loc.get("excluded")]
        self.assertEqual(12, len(excluded), "excluded tail drifted — regenerate the contract")
        distribute_items_restrictive(self.multiworld)
        for name in excluded:
            item = self.multiworld.get_location(name, self.player).item
            self.assertIsNotNone(item, f"{name} unfilled")
            self.assertFalse(item.advancement, f"progression item '{item.name}' on excluded '{name}'")

    def test_slot_data_placements_cover_every_location(self) -> None:
        # The loop-closing channel (AP-CLIENT-PLAN.md D5): every location must appear in
        # placements with either a valid DC1 game item id or the other-world marker.
        from Fill import distribute_items_restrictive
        distribute_items_restrictive(self.multiworld)
        slot_data = self.world.fill_slot_data()
        self.assertEqual(3, slot_data["logic_version"])
        placements = slot_data["placements"]
        self.assertEqual(len(dc1.LOCATIONS), len(placements))
        valid_ids = set(dc1.GAME_ITEM_ID.values())
        for loc in dc1.LOCATIONS:
            ap_id = str(dc1.LOCATION_NAME_TO_ID[loc["name"]])
            self.assertIn(ap_id, placements, f"no placement for {loc['name']}")
            value = placements[ap_id]
            self.assertTrue(value == dc1.OTHER_WORLD_MARKER or value in valid_ids,
                            f"bad placement value {value} at {loc['name']}")

    def test_not_beatable_without_fc_device(self) -> None:
        # Every edge into the goal room (0503->060d, 0607->060d) requires the F.C. Device —
        # the second hard goal gate besides Key Card Lv. A.
        fc_device = dc1.ITEM_NAMES[58]
        self.collect_all_but([fc_device])
        self.assertBeatable(False)

    def test_slot_data_marks_foreign_items_with_marker(self) -> None:
        # The runtime client renders another slot's item as the marker pickup. Exercise the
        # item.player/item.game discrimination in fill_slot_data directly (a solo fill never
        # takes that branch): a foreign-game item AND a same-game-other-slot item must both
        # become OTHER_WORLD_MARKER.
        from Fill import distribute_items_restrictive
        distribute_items_restrictive(self.multiworld)
        loc_a = self.multiworld.get_location(_first_location_in("0100"), self.player)
        loc_b = self.multiworld.get_location(_first_location_in("0103"), self.player)
        loc_a.item = Item("Foreign Widget", ItemClassification.filler, None, self.player + 1)
        loc_b.item = DinoCrisis1Item("An. Aid", ItemClassification.filler, None, self.player + 1)
        slot_data = self.world.fill_slot_data()
        self.assertEqual(dc1.OTHER_WORLD_MARKER, slot_data["placements"][str(loc_a.address)])
        self.assertEqual(dc1.OTHER_WORLD_MARKER, slot_data["placements"][str(loc_b.address)])

    def test_slot_data_item_ids_shape(self) -> None:
        # item_ids is the client's AP-id -> game-id grant map: complete, valid, and never
        # colliding with the other-world marker value.
        slot_data = self.world.fill_slot_data()
        item_ids = slot_data["item_ids"]
        self.assertEqual(len(dc1.ITEM_NAME_TO_ID), len(item_ids))
        valid_ids = set(dc1.GAME_ITEM_ID.values())
        for ap_id, game_id in item_ids.items():
            self.assertIn(int(ap_id), dc1.ITEM_NAME_TO_ID.values(), f"unknown AP id {ap_id}")
            self.assertIn(game_id, valid_ids, f"AP id {ap_id} maps to invalid game id {game_id}")
            self.assertNotEqual(dc1.OTHER_WORLD_MARKER, game_id)

    def test_ddk_disc_pairs_are_and_gated(self) -> None:
        # A DDK Input+Code disc door needs BOTH discs (native op58-3 AND-gate, [[ddk-disc-relocation]]).
        # 0304->0300 (discs E) and 0604->0609 (discs W) both bite in the physical graph.
        for room, discs in (("0300", ["DDK Input Disc E", "DDK Code Disc E"]),
                            ("0609", ["DDK Input Disc W", "DDK Code Disc W"])):
            self.assertAccessDependency([_first_location_in(room)], [discs], only_check_listed=True)

    def test_physical_node_split_and_guarded_pickup_are_wired(self) -> None:
        hall_nodes = [node for node in dc1.NODES if node["room"] == "0309"]
        self.assertEqual({"west", "shuttle"}, {node["region"] for node in hall_nodes})
        guarded = next(location for location in dc1.LOCATIONS
                       if location["room"] == "0305" and location["itemId"] == 0x48)
        self.assertAccessDependency(
            [guarded["name"]],
            [[dc1.ITEM_NAMES[0x46]]],
            only_check_listed=True,
        )

    def test_parallel_physical_edges_keep_unique_entrance_names(self) -> None:
        # Distinct physical doors can share endpoints while carrying different latch rules.
        # AP requires every entrance name to be unique, so parallel edges must not be dropped or
        # registered under the same default name.
        entrances = [
            entrance.name
            for region in self.multiworld.regions
            if region.player == self.player
            for entrance in region.exits
        ]
        expected = 1 + sum(edge["from"] != edge["to"] for edge in dc1.EDGES)  # Menu -> start
        self.assertEqual(expected, len(entrances))
        self.assertEqual(len(entrances), len(set(entrances)))

    def test_shared_placement_policy_keeps_progression_out_of_ineligible_locations(self) -> None:
        from Fill import distribute_items_restrictive
        distribute_items_restrictive(self.multiworld)
        for location in dc1.LOCATIONS:
            item = self.multiworld.get_location(location["name"], self.player).item
            self.assertIsNotNone(item)
            if item.advancement:
                self.assertIn(dc1.GAME_ITEM_ID[item.name], location["allowedProgressionItemIds"],
                              f"{item.name} placed at ineligible {location['name']}")


class TestMinimalAccessibility(DinoCrisis1TestBase):
    """Generic AP suite (fill / reachability / seeding) under accessibility: minimal —
    the universal option most likely to expose a rule/region wiring hole."""
    options = {"accessibility": "minimal"}


class TestStartInventory(DinoCrisis1TestBase):
    """Generic AP suite with a precollected goal gate — catches pool/count math breaking
    when a progression item starts in inventory instead of the pool."""
    options = {"start_inventory": {"Key Card Lv. A": 1}}


class TestDeterminism(DinoCrisis1TestBase):
    def test_same_seed_same_placements(self) -> None:
        # slot_data is the install contract — the same multiworld seed must produce the same
        # placements byte-for-byte (unseeded randomness in get_filler_item_name or set-iteration
        # order would silently break reroll reproducibility).
        from Fill import distribute_items_restrictive

        def generate(seed: int) -> dict:
            self.world_setup(seed)
            distribute_items_restrictive(self.multiworld)
            return dict(self.world.fill_slot_data()["placements"])

        self.assertEqual(generate(20260719), generate(20260719))
