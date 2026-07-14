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
from .. import data as dc1
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

    def test_beatable_with_everything(self) -> None:
        self.collect_all_but([])
        self.assertBeatable(True)

    def test_key_items_are_progression(self) -> None:
        for name in ("BG Area Key", "C. O. Area Key", "Key Card Lv. A"):
            items = self.get_items_by_name(name)
            self.assertTrue(items, f"{name} missing from item pool")
            self.assertTrue(all(i.advancement for i in items), f"{name} not progression")

    def test_all_gate_items_are_progression(self) -> None:
        # EVERY item that gates a door in the logic contract (area keys, F.C. Device, all 14 DDK discs)
        # must be an AP progression item — otherwise fill can leave a required key behind its own gate.
        gate_names = {dc1.ITEM_NAMES[i] for e in dc1.EDGES for i in e["requiresItems"]}
        self.assertTrue(gate_names, "no gate items found in the contract — distiller regression?")
        for name in sorted(gate_names):
            items = self.get_items_by_name(name)
            self.assertTrue(items, f"{name} gates an edge but is absent from the item pool")
            self.assertTrue(all(i.advancement for i in items),
                            f"{name} gates an edge but is not a progression item")

    def test_ddk_disc_pairs_are_and_gated(self) -> None:
        # A DDK Input+Code disc door needs BOTH discs (native op58-3 AND-gate, [[ddk-disc-relocation]]).
        # 0304->0300 (discs E) and 0604->0609 (discs W) both bite in the star model with a location present.
        for room, discs in (("0300", ["DDK Input Disc E", "DDK Code Disc E"]),
                            ("0609", ["DDK Input Disc W", "DDK Code Disc W"])):
            self.assertAccessDependency([_first_location_in(room)], [discs], only_check_listed=True)
