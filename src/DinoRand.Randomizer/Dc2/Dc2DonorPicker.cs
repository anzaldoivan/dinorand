namespace DinoRand.Randomizer.Dc2;

/// <summary>How the DC2 enemy pass chooses each room's donor species
/// (docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md D1/D3).</summary>
public enum Dc2EnemyDistributionMode
{
    /// <summary>Per-species weight table biases the per-room pick (default; curated defaults
    /// reproduce the legacy uniform pick exactly).</summary>
    Weighted = 0,
    /// <summary>One pinned donor for every eligible room — the bulk analogue of the single-room
    /// forced swap (<c>--dc2-swap-enemies</c>).</summary>
    Fixed = 1,
}

/// <summary>
/// Pure, RNG-injected donor selection policy — the seam that replaced the planner's inline
/// <c>validDonors[rng.Next(count)]</c> (docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md D1/D3). All safety
/// decisions (habitat, native collision, shared-base budget) happen BEFORE this runs: the picker
/// only chooses among the room's already-valid donors, or declines (<c>null</c> ⇒ room left
/// vanilla — never a silent uniform fallback).
/// </summary>
public sealed class Dc2DonorPicker
{
    /// <summary>Weights are nibbles (0–15) so they pack into the seed string (plan D6).</summary>
    public const byte MaxWeight = 15;

    private readonly IReadOnlyDictionary<int, byte>? _weights; // weighted mode
    private readonly int _fixedType;                            // fixed mode

    private Dc2DonorPicker(IReadOnlyDictionary<int, byte>? weights, int fixedType)
    {
        _weights = weights;
        _fixedType = fixedType;
    }

    /// <summary>Weighted mode over a COMPLETE weight table (species missing from the table draw
    /// weight 0 = excluded). Equal non-zero weights across a room's whole valid pool reproduce the
    /// legacy uniform pick <b>stream-identically</b> (same single <c>rng.Next(count)</c> call).</summary>
    public static Dc2DonorPicker Weighted(IReadOnlyDictionary<int, byte> weights) =>
        new(weights, 0);

    /// <summary>Fixed mode: pick <paramref name="speciesType"/> where it is valid, decline
    /// elsewhere. Consumes no RNG. Safety validation of the pin (Known+LAND) is the caller's
    /// (CLI/UI/pass), matching the forced single-room swap's contract.</summary>
    public static Dc2DonorPicker Fixed(int speciesType) =>
        new(null, speciesType);

    /// <summary>Pick a donor from the room's valid pool, or <c>null</c> to leave the room vanilla
    /// (empty pool / pinned species not valid here / every valid donor at weight 0).</summary>
    public Dc2Species? Pick(IReadOnlyList<Dc2Species> validDonors, Random rng)
    {
        if (validDonors.Count == 0) return null;

        if (_weights is null)
            return validDonors.FirstOrDefault(d => d.Type == _fixedType);

        // Weighted: restrict the table to the room's valid donors; zero-weight species are excluded
        // even when they are the only valid donor (plan D1 rule 2 — never fall back to uniform).
        var weighted = new List<(Dc2Species Donor, int Weight)>(validDonors.Count);
        foreach (var d in validDonors)
            if (_weights.TryGetValue(d.Type, out var w) && w > 0)
                weighted.Add((d, w));
        if (weighted.Count == 0) return null;

        // All non-zero weights equal ⇒ plain index pick. When nothing was excluded this is the
        // SAME rng.Next(validDonors.Count) call the legacy uniform code made, so curated defaults
        // reproduce shipped seeds byte-identically (plan D1 rule 3, pinned by tests).
        bool allEqual = weighted.All(x => x.Weight == weighted[0].Weight);
        if (allEqual)
            return weighted[rng.Next(weighted.Count)].Donor;

        // Cumulative draw — one rng.Next either way, so mixed-weight rooms and equal-weight rooms
        // consume the same amount of the room's namespaced stream.
        int total = weighted.Sum(x => x.Weight);
        int roll = rng.Next(total);
        foreach (var (donor, weight) in weighted)
        {
            roll -= weight;
            if (roll < 0) return donor;
        }
        return weighted[^1].Donor; // unreachable; total covers every slot
    }
}
