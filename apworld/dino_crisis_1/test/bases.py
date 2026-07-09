from test.bases import WorldTestBase

from ..world import DinoCrisis1World


class DinoCrisis1TestBase(WorldTestBase):
    """Base for Dino Crisis 1 world tests. Runs only inside an Archipelago checkout
    (imports AP's `test.bases`). Subclassing WorldTestBase also opts this world into AP's
    generic suite (fill, full-state reachability, empty-state seeding, id uniqueness).
    """

    game = "Dino Crisis 1"
    world: DinoCrisis1World
