using System.Text.Json;
using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Dc1;

public sealed record Dc1SpeciesDistribution(
    DinoSpecies Species, string Creature, byte DefaultWeight, int VanillaCount);

public enum Dc1EnemyDistributionMode
{
    Weighted = 0,
    Fixed = 1,
}

public sealed class Dc1EnemyDistribution
{
    public const string DefaultResourceName = "DinoRand.Randomizer.Data.dc1.enemy-distribution.json";
    public IReadOnlyList<Dc1SpeciesDistribution> Rows { get; }
    public IReadOnlyDictionary<DinoSpecies, byte> DefaultWeights { get; }
    public IReadOnlyDictionary<DinoSpecies, int> VanillaCounts { get; }

    private Dc1EnemyDistribution(IReadOnlyList<Dc1SpeciesDistribution> rows)
    {
        Rows = rows;
        DefaultWeights = rows.ToDictionary(row => row.Species, row => row.DefaultWeight);
        VanillaCounts = rows.ToDictionary(row => row.Species, row => row.VanillaCount);
    }

    public IReadOnlyDictionary<DinoSpecies, byte> EffectiveWeights(
        IReadOnlyDictionary<DinoSpecies, byte>? overrides)
    {
        if (overrides is null || overrides.Count == 0) return DefaultWeights;
        return Rows.ToDictionary(row => row.Species,
            row => overrides.TryGetValue(row.Species, out var weight) ? weight : row.DefaultWeight);
    }

    public static Dc1EnemyDistribution LoadEmbedded()
    {
        using var stream = typeof(Dc1EnemyDistribution).Assembly
            .GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException($"embedded resource '{DefaultResourceName}' not found");
        return Parse(stream);
    }

    public static Dc1EnemyDistribution Parse(Stream json)
    {
        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.GetProperty("species").EnumerateArray()
            .Select(row => new Dc1SpeciesDistribution(
                Enum.Parse<DinoSpecies>(row.GetProperty("species").GetString()!, ignoreCase: false),
                row.GetProperty("creature").GetString()!,
                (byte)row.GetProperty("default_weight").GetInt32(),
                row.GetProperty("vanilla_count").GetInt32()))
            .ToArray();
        return new Dc1EnemyDistribution(rows);
    }
}

public sealed class Dc1DonorPicker
{
    public const byte MaxWeight = byte.MaxValue;

    private readonly IReadOnlyDictionary<DinoSpecies, byte>? _weights;
    private readonly DinoSpecies _fixedSpecies;

    private Dc1DonorPicker(IReadOnlyDictionary<DinoSpecies, byte>? weights, DinoSpecies fixedSpecies)
    {
        _weights = weights;
        _fixedSpecies = fixedSpecies;
    }

    public static Dc1DonorPicker Weighted(IReadOnlyDictionary<DinoSpecies, byte> weights)
        => new(weights, DinoSpecies.Unknown);

    public static Dc1DonorPicker Fixed(DinoSpecies species)
        => new(null, species);

    public DinoSpecies? Pick(IReadOnlyList<DinoSpecies> validDonors, Random rng)
    {
        if (_weights is null)
        {
            var fixedSpecies = validDonors.FirstOrDefault(species => species == _fixedSpecies);
            return fixedSpecies == DinoSpecies.Unknown ? null : fixedSpecies;
        }

        var weighted = validDonors
            .Select(species => (Species: species, Weight: _weights.GetValueOrDefault(species)))
            .Where(entry => entry.Weight > 0)
            .ToList();
        if (weighted.Count == 0) return null;
        int total = weighted.Sum(entry => entry.Weight);
        int roll = rng.Next(total);
        foreach (var entry in weighted)
        {
            roll -= entry.Weight;
            if (roll < 0) return entry.Species;
        }
        return weighted[^1].Species;
    }
}
