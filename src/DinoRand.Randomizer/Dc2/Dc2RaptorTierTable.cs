using System.Text.Json;

namespace DinoRand.Randomizer.Dc2;

/// <summary>One raptor (E00-family) tier variant from <c>data/dc2/raptor-tiers.json</c>
/// (docs/reference/dc2/enemies/RAPTOR-TIER-RE.md §1): the variant nibble written into the spawn's variant operand,
/// its base HP class value, the recolour <c>.TEX</c> it selects, and its default pool weight.</summary>
public sealed record Dc2RaptorTier(int Variant, int HpBase, string TexFile, byte DefaultWeight, string? Notes)
{
    /// <summary>UI/spoiler label, e.g. "V4 E00_02 (HP 1595) — red …".</summary>
    public string Label =>
        $"V{Variant} {Path.GetFileNameWithoutExtension(TexFile)} (HP {HpBase})"
        + (Notes is null ? "" : $" — {Notes.Split(';')[0]}");
}

/// <summary>
/// Loads <c>data/dc2/raptor-tiers.json</c> — the decoded raptor variant/tier registry
/// (docs/reference/dc2/enemies/RAPTOR-TIER-RE.md). Mirrors <see cref="Dc2EnemyDistribution"/>: rows + default
/// weights + <c>EffectiveWeights(overrides)</c>.
/// </summary>
public sealed class Dc2RaptorTierTable
{
    public const string DefaultResourceName = "DinoRand.Randomizer.Data.dc2.raptor-tiers.json";

    /// <summary>Highest valid variant nibble; v8+ index garbage stat records (RAPTOR-TIER-RE.md §1).</summary>
    public const int MaxVariant = 7;

    /// <summary>The blue/super raptor variant (10000 HP), the combo-latch tier.</summary>
    public const int SuperRaptorVariant = 5;

    public IReadOnlyList<Dc2RaptorTier> Rows { get; }

    private Dc2RaptorTierTable(IReadOnlyList<Dc2RaptorTier> rows) => Rows = rows;

    public IReadOnlyDictionary<int, byte> DefaultWeights =>
        Rows.ToDictionary(r => r.Variant, r => r.DefaultWeight);

    /// <summary>Default weights with per-variant overrides applied (unknown variants ignored;
    /// weights clamped to <see cref="Dc2DonorPicker.MaxWeight"/>).</summary>
    public IReadOnlyDictionary<int, byte> EffectiveWeights(IReadOnlyDictionary<int, byte>? overrides)
    {
        var w = Rows.ToDictionary(r => r.Variant, r => r.DefaultWeight);
        if (overrides is not null)
            foreach (var (variant, weight) in overrides)
                if (w.ContainsKey(variant))
                    w[variant] = Math.Min(weight, Dc2DonorPicker.MaxWeight);
        return w;
    }

    public static Dc2RaptorTierTable LoadEmbedded()
    {
        var asm = typeof(Dc2RaptorTierTable).Assembly;
        using var s = asm.GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException($"embedded resource '{DefaultResourceName}' not found");
        return Parse(s);
    }

    public static Dc2RaptorTierTable Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = new List<Dc2RaptorTier>();
        foreach (var v in doc.RootElement.GetProperty("variants").EnumerateArray())
        {
            rows.Add(new Dc2RaptorTier(
                v.GetProperty("variant").GetInt32(),
                v.GetProperty("hpBase").GetInt32(),
                v.GetProperty("texFile").GetString()!,
                (byte)v.GetProperty("default_weight").GetInt32(),
                v.TryGetProperty("notes", out var n) ? n.GetString() : null));
        }
        if (rows.Count == 0 || rows.Any(r => r.Variant < 0 || r.Variant > MaxVariant))
            throw new InvalidDataException("raptor-tiers.json: variants must be 0..7 and non-empty");
        return new Dc2RaptorTierTable(rows);
    }
}
