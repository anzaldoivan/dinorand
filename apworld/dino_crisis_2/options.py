from dataclasses import dataclass

from Options import PerGameCommonOptions


@dataclass
class DinoCrisis2Options(PerGameCommonOptions):
    """No DC2-specific options yet — kept because World.options_dataclass is required.

    DC2's shipped levers (enemy rando, --dc2-shuffle-bgm/-shop, voices, starting loadout, skins)
    map onto RandomizerConfig flags and become options here once the DC2 AP loop is real.
    """
