"""Integration tests: Archipelago generation over the DC2 stub world.

DC2 has no gated logic yet, so this is a minimal "it generates and is completable" proof.
WorldTestBase also contributes AP's generic tests automatically (test_fill,
test_all_state_can_reach_everything, test_empty_state_can_reach_something, id uniqueness).
When the DC2 contract is populated, add gate/beatability tests mirroring DC1's.
"""
from .bases import DinoCrisis2TestBase


class TestStubGeneration(DinoCrisis2TestBase):
    options: dict = {}

    def test_victory_present_and_beatable(self) -> None:
        # The stub has a single free Victory event, so the seed is always completable.
        self.assertTrue(self.multiworld.get_location("Victory", 1))
        self.collect_all_but([])
        self.assertBeatable(True)

    def test_no_fillable_locations_yet(self) -> None:
        # Stub carries no real (id-bearing) locations; only the Victory event.
        real = [loc for loc in self.multiworld.get_locations(1) if loc.address is not None]
        self.assertEqual(real, [])
