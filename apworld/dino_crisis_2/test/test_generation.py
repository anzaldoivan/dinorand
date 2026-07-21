"""Integration tests: Archipelago generation over the DC2 world.

WorldTestBase also contributes AP's generic tests automatically (test_fill,
test_all_state_can_reach_everything, test_empty_state_can_reach_something, id uniqueness).
"""
from .. import data as dc2
from .bases import DinoCrisis2TestBase


class TestStubGeneration(DinoCrisis2TestBase):
    options: dict = {}

    def test_victory_present_and_beatable(self) -> None:
        # The stub has a single free Victory event, so the seed is always completable.
        self.assertTrue(self.multiworld.get_location("Victory", 1))
        self.collect_all_but([])
        self.assertBeatable(True)

    def test_fillable_locations_match_contract(self) -> None:
        real = [loc for loc in self.multiworld.get_locations(1) if loc.address is not None]
        self.assertEqual(
            sorted(loc.name for loc in real),
            sorted(loc["name"] for loc in dc2.LOCATIONS),
        )
