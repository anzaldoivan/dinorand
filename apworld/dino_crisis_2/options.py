from dataclasses import dataclass

from Options import PerGameCommonOptions


@dataclass
class DinoCrisis2Options(PerGameCommonOptions):
    """No DC2-specific AP options yet; the v2 contract defines the supported surface.

    DC2's shipped levers (enemy rando, --dc2-shuffle-bgm/-shop, voices, starting loadout, skins)
    can become options here if their runtime contracts are added later.
    """
