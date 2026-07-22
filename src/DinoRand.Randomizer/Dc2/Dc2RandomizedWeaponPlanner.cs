using DinoRand.FileFormats.Exe;

namespace DinoRand.Randomizer.Dc2;

public sealed record Dc2RandomizedWeaponPlan(
    IReadOnlyList<byte> ReginaOnly,
    IReadOnlyList<byte> DylanOnly);

/// <summary>Seeded Fisher-Yates planner for the six DC2 randomized MAIN weapons.</summary>
public static class Dc2RandomizedWeaponPlanner
{
    public const string StreamName = "DC2 Randomized Weapons";

    public static Dc2RandomizedWeaponPlan Plan(Seed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var ids = Dc2RandomizedWeaponPatch.Domain.ToArray();
        var rng = seed.RngFor(StreamName);
        for (int i = ids.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (ids[i], ids[j]) = (ids[j], ids[i]);
        }
        return new Dc2RandomizedWeaponPlan(ids[..3], ids[3..]);
    }
}
