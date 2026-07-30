namespace DinoRand.Randomizer;

/// <summary>Named RNG namespace reserved for future, census-backed visual selection.</summary>
public static class Dc1RaptorVisualVariantRandom
{
    public const string StreamName = "dc1-raptor-visual-variants";

    public static Random RngFor(Seed seed) => seed.RngFor(StreamName);
}
