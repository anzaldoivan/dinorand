from test.bases import WorldTestBase

from ..world import DinoCrisis2World


class DinoCrisis2TestBase(WorldTestBase):
    """Base for Dino Crisis 2 world tests. Runs only inside an Archipelago checkout
    (imports AP's `test.bases`). Subclassing WorldTestBase also opts this world into AP's
    generic suite (fill, full-state reachability, empty-state seeding, id uniqueness).
    """

    game = "Dino Crisis 2"
    world: DinoCrisis2World
