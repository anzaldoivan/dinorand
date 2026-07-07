namespace DinoRand.Randomizer.Definitions;

/// <summary>
/// Player-facing weapon families, the unit of the per-weapon enable/disable toggles
/// (docs/decisions/cross/ITEM-RANDO-PLAN.md §7.4). A family groups a base weapon with its custom variants and upgrade
/// parts (see <c>data/dc1/items.json</c> <c>weaponFamilies</c>). The bit values double as the seed
/// encoding (<c>AppSeed</c> byte 9, low 3 bits) and the <see cref="RandomizerConfig.EnabledWeaponFamilies"/>
/// flag set, so they are part of the on-disk/shared-seed contract — do not renumber.
/// </summary>
[Flags]
public enum WeaponFamily
{
    None = 0,
    Handgun = 1,
    Shotgun = 2,
    GrenadeGun = 4,
    All = Handgun | Shotgun | GrenadeGun,
}
