#nullable enable
using DinoRand.Randomizer;
using DinoRand.Randomizer.Spoiler;

namespace DinoRand.App;

/// <summary>
/// A shareable run identity: the <see cref="Seed"/> plus the full
/// <see cref="RandomizerConfig"/> packed into a short <c>DINO-{base64url}</c> string
/// (~12 chars), BioRand-style. Anyone pasting the string back reproduces the same run.
/// </summary>
/// <remarks>
/// The wire format (byte layout, back-compat rules, the frozen DC2 canonical-species order) is
/// owned by <see cref="SeedString"/> in DinoRand.Randomizer (docs/decisions/cross/SPOILER-LOG-PLAN.md §3.2 D) so
/// the CLI and the runners' spoiler debug block emit the identical string; this class is the
/// UI-level wrapper and delegates both directions. Encoding is unchanged — locked by
/// <c>AppSeedTests</c> + <c>SeedStringTests</c>.
/// </remarks>
public sealed class AppSeed
{
    /// <summary>Read-only view of the frozen DC2 canonical species order (a wire-format constant
    /// owned by <see cref="SeedString"/>) for the drift-lock test.</summary>
    public static IReadOnlyList<int> Dc2CanonicalSpeciesOrder => SeedString.Dc2CanonicalSpecies;

    public Seed Seed { get; }
    public RandomizerConfig Config { get; }

    private AppSeed(Seed seed, RandomizerConfig config) { Seed = seed; Config = config; }

    public static AppSeed Random()
        => new(Seed.Random(), new RandomizerConfig());

    public static bool TryParse(string s, out AppSeed result)
    {
        result = null!;
        if (!SeedString.TryParse(s, out var seed, out var config)) return false;
        result = new AppSeed(seed, config);
        return true;
    }

    public AppSeed WithConfig(RandomizerConfig config) => new(Seed, config);
    public AppSeed WithNewSeed() => new(Seed.Random(), Config);

    public override string ToString() => SeedString.Encode(Seed, Config);
}
