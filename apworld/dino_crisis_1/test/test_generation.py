"""Integration tests: Archipelago generation over DinoRand's authored DC1 logic.

These construct a real MultiWorld and exercise AP's fill/reachability against the world built by
DinoCrisis1World. WorldTestBase also contributes AP's generic tests automatically:
  - test_fill                          a real fill of the seed completes
  - test_all_state_can_reach_everything  every location reachable with the full inventory
  - test_empty_state_can_reach_something the fill has reachable spots to seed from

The custom tests below lock in the DinoRand-specific logic (the Key Card Lv. A gate on the goal).
"""
from .bases import DinoCrisis1TestBase

# One of the goal-room (Underground Heliport, 060d) checks; all of them sit behind the Key Card gate.
GOAL_ROOM_LOCATION = "Underground Heliport (060d) - Plug @-2816,10368"


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
