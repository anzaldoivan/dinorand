using System.Globalization;
using System.Text.Json;

namespace DinoRand.Randomizer.Dc2;

/// <summary>One row of the donor-distribution registry: a weighable species' curated default
/// weight (0–15, biases the per-room weighted pick) and optional per-seed room cap
/// (docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md D2/D4).</summary>
public sealed record Dc2SpeciesDistribution(int Type, string Creature, byte DefaultWeight, int? RoomCap);

/// <summary>
/// Loads <c>data/dc2/enemy-distribution.json</c> — the curated default weights + room caps behind
/// the configurable enemy distribution (docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md D5). Rows are exactly
/// the <see cref="Dc2SpeciesTable"/> <c>Known</c>+LAND species (locked by
/// <c>Dc2EnemyDistributionTests</c>); tuning rarity is a registry edit, not a code change.
/// </summary>
public sealed class Dc2EnemyDistribution
{
    /// <summary>Embedded-resource logical name (see DinoRand.Randomizer.csproj).</summary>
    public const string DefaultResourceName = "DinoRand.Randomizer.Data.dc2.enemy-distribution.json";

    /// <summary>Registry rows in canonical (ascending-TYPE) order — the same order the seed
    /// string's weight nibbles use (<c>AppSeed</c>; docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md D6).</summary>
    public IReadOnlyList<Dc2SpeciesDistribution> Rows { get; }

    /// <summary>Curated default weight per species TYPE.</summary>
    public IReadOnlyDictionary<int, byte> DefaultWeights { get; }

    /// <summary>Room caps per species TYPE — only species that HAVE a cap appear.</summary>
    public IReadOnlyDictionary<int, int> RoomCaps { get; }

    private Dc2EnemyDistribution(IReadOnlyList<Dc2SpeciesDistribution> rows)
    {
        Rows = rows;
        DefaultWeights = rows.ToDictionary(r => r.Type, r => r.DefaultWeight);
        RoomCaps = rows.Where(r => r.RoomCap is not null).ToDictionary(r => r.Type, r => r.RoomCap!.Value);
    }

    /// <summary>The weight table a run actually uses: the curated defaults overlaid with the
    /// config's per-species overrides (<c>RandomizerConfig.Dc2SpeciesWeights</c>; unknown TYPEs in
    /// the override are ignored — the CLI/UI validate, this stays total). A species absent from the
    /// registry has effective weight 0 (excluded), so the weighted pick can never invent a donor
    /// the registry doesn't know.</summary>
    public IReadOnlyDictionary<int, byte> EffectiveWeights(IReadOnlyDictionary<int, byte>? overrides)
    {
        if (overrides is null || overrides.Count == 0) return DefaultWeights;
        var merged = Rows.ToDictionary(
            r => r.Type,
            r => overrides.TryGetValue(r.Type, out var w) ? Math.Min(w, Dc2DonorPicker.MaxWeight) : r.DefaultWeight);
        return merged;
    }

    public static Dc2EnemyDistribution LoadEmbedded()
    {
        var asm = typeof(Dc2EnemyDistribution).Assembly;
        using var s = asm.GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException($"embedded resource '{DefaultResourceName}' not found");
        return Parse(s);
    }

    public static Dc2EnemyDistribution Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = new List<Dc2SpeciesDistribution>();
        foreach (var r in doc.RootElement.GetProperty("species").EnumerateArray())
        {
            rows.Add(new Dc2SpeciesDistribution(
                Hex(r.GetProperty("type")),
                r.GetProperty("creature").GetString()!,
                (byte)r.GetProperty("default_weight").GetInt32(),
                r.GetProperty("room_cap").ValueKind == JsonValueKind.Null
                    ? null : r.GetProperty("room_cap").GetInt32()));
        }
        return new Dc2EnemyDistribution(rows.OrderBy(r => r.Type).ToArray());
    }

    private static int Hex(JsonElement e) =>
        int.Parse(e.GetString()!.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
