namespace DinoRand.Randomizer;

/// <summary>
/// Reproducible randomness for a run. Every pass draws from the same seed, so a
/// seed + config fully determines the output (shareable, like BioRand). docs/reference/cross/architecture/DESIGN.md §6.
/// </summary>
public sealed class Seed
{
    public int Value { get; }

    public Seed(int value) => Value = value;

    public static Seed Random() => new(Environment.TickCount);

    public static Seed Parse(string text) =>
        int.TryParse(text, out var v) ? new Seed(v) : new Seed(StableHash(text));

    /// <summary>A fresh RNG for one pass, namespaced so passes don't share a sequence.</summary>
    public Random RngFor(string passName) => new(Value ^ StableHash(passName));

    private static int StableHash(string s)
    {
        unchecked
        {
            int hash = 17;
            foreach (char c in s) hash = hash * 31 + c;
            return hash;
        }
    }

    public override string ToString() => Value.ToString();
}
