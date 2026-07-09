from dataclasses import dataclass

from Options import PerGameCommonOptions


@dataclass
class DinoCrisis1Options(PerGameCommonOptions):
    """No DC1-specific options yet — kept because World.options_dataclass is required.

    Future levers (enemy rando, weapon families, difficulty) map onto RandomizerConfig flags
    and become options here; see the decision record's Future section.
    """
