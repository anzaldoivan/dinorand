using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Unit tests for the pure donor-selection policy (docs/dc2/ENEMY-DISTRIBUTION-PLAN.md D1/D3):
/// the equal-weight ⇒ legacy-uniform stream identity, zero-weight exclusion (never a uniform
/// fallback), the weighted distribution shape, and fixed mode's no-RNG pin-or-decline contract.
/// </summary>
public class Dc2DonorPickerTests
{
    private static readonly IReadOnlyList<Dc2Species> DefaultPool = Dc2SpeciesTable.DefaultDonors;
    private static readonly IReadOnlyList<Dc2Species> FullPool =
        Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: true);

    private static Dictionary<int, byte> Weights(params (int Type, byte W)[] entries) =>
        entries.ToDictionary(e => e.Type, e => e.W);

    // --- D1 rule 3: equal weights == the legacy uniform pick, stream-identically -----------------

    [Fact]
    public void Weighted_EqualWeights_MatchesLegacyUniformStream()
    {
        // The regression criterion (plan §D1): with every valid donor at the same non-zero weight,
        // the picker must make the SAME single rng.Next(count) call the shipped uniform code made,
        // so default-config seeds reproduce today's donor sets exactly.
        var picker = Dc2DonorPicker.Weighted(Dc2EnemyDistribution.LoadEmbedded().DefaultWeights);
        for (int seed = 0; seed < 500; seed++)
        {
            var legacy = DefaultPool[new Random(seed).Next(DefaultPool.Count)];
            var picked = picker.Pick(DefaultPool, new Random(seed));
            Assert.Equal(legacy.Type, picked!.Type);
        }
    }

    [Fact]
    public void Weighted_SameSeed_IsDeterministic()
    {
        var picker = Dc2DonorPicker.Weighted(Weights((0x02, 8), (0x07, 3), (0x08, 1), (0x0e, 12)));
        for (int seed = 0; seed < 100; seed++)
            Assert.Equal(picker.Pick(DefaultPool, new Random(seed))!.Type,
                         picker.Pick(DefaultPool, new Random(seed))!.Type);
    }

    // --- D1 rule 2: zero weight = excluded; all-zero = decline (never uniform fallback) ----------

    [Fact]
    public void Weighted_ZeroWeightSpecies_IsNeverPicked()
    {
        var picker = Dc2DonorPicker.Weighted(Weights((0x02, 0), (0x07, 8), (0x08, 8), (0x0e, 8)));
        for (int seed = 0; seed < 300; seed++)
            Assert.NotEqual(0x02, picker.Pick(DefaultPool, new Random(seed))!.Type);
    }

    [Fact]
    public void Weighted_AllValidDonorsZero_Declines()
    {
        // Even when zero-weight species are the ONLY valid donors, the pick declines (room stays
        // vanilla) — the "never silently fall back to uniform" rule.
        var picker = Dc2DonorPicker.Weighted(Weights((0x02, 0), (0x07, 0), (0x08, 0), (0x0e, 0)));
        Assert.Null(picker.Pick(DefaultPool, new Random(1)));
    }

    [Fact]
    public void Weighted_SpeciesMissingFromTable_IsExcluded()
    {
        // A donor with no weight entry draws weight 0 — the table is authoritative.
        var picker = Dc2DonorPicker.Weighted(Weights((0x07, 8)));
        for (int seed = 0; seed < 100; seed++)
            Assert.Equal(0x07, picker.Pick(DefaultPool, new Random(seed))!.Type);
    }

    [Fact]
    public void Weighted_EmptyPool_Declines()
    {
        var picker = Dc2DonorPicker.Weighted(Weights((0x02, 8)));
        Assert.Null(picker.Pick(Array.Empty<Dc2Species>(), new Random(1)));
    }

    // --- Distribution shape (chi-square-ish tolerance over one long deterministic stream) --------

    [Fact]
    public void Weighted_DefaultWeights_BossDonorsLandNearNominalShare()
    {
        // Full pool (both toggles on) under the curated defaults: normals 8, T-Rex 2, Giga 1
        // (total 43 with the setpiece Triceratops in). Nominal shares: T-Rex 2/43 ≈ 4.65%,
        // Giga 1/43 ≈ 2.33%, each normal 8/43 ≈ 18.6%. One seeded stream, 40k draws ⇒ ±1% is
        // generous (σ ≈ 0.1%) yet still pins the shape.
        var picker = Dc2DonorPicker.Weighted(Dc2EnemyDistribution.LoadEmbedded().DefaultWeights);
        var rng = new Random(12345);
        const int draws = 40_000;
        var counts = new Dictionary<int, int>();
        for (int i = 0; i < draws; i++)
        {
            int t = picker.Pick(FullPool, rng)!.Type;
            counts[t] = counts.GetValueOrDefault(t) + 1;
        }

        Assert.Equal(2.0 / 43, counts[0x03] / (double)draws, 2.0 / 43 * 0.25); // T-Rex ≈ 4.65%
        Assert.Equal(1.0 / 43, counts[0x06] / (double)draws, 1.0 / 43 * 0.25); // Giga ≈ 2.33%
        foreach (var normal in new[] { 0x02, 0x07, 0x08, 0x09, 0x0e })
            Assert.Equal(8.0 / 43, counts[normal] / (double)draws, 0.01);
    }

    [Fact]
    public void Weighted_SkewedWeights_FollowTheRatio()
    {
        // 12:4 over two donors ⇒ 75%/25%.
        var pool = new[] { Dc2SpeciesTable.ForType(0x02)!, Dc2SpeciesTable.ForType(0x07)! };
        var picker = Dc2DonorPicker.Weighted(Weights((0x02, 12), (0x07, 4)));
        var rng = new Random(999);
        int raptor = 0;
        const int draws = 20_000;
        for (int i = 0; i < draws; i++)
            if (picker.Pick(pool, rng)!.Type == 0x02) raptor++;
        Assert.Equal(0.75, raptor / (double)draws, 0.02);
    }

    // --- Fixed mode -------------------------------------------------------------------------------

    [Fact]
    public void Fixed_PicksThePin_WhenValid()
    {
        var picker = Dc2DonorPicker.Fixed(0x03);
        Assert.Equal(0x03, picker.Pick(FullPool, new Random(1))!.Type);
    }

    [Fact]
    public void Fixed_Declines_WhenPinNotInValidPool()
    {
        // e.g. the pinned species is native to the room (filtered out of validDonors upstream).
        var picker = Dc2DonorPicker.Fixed(0x03);
        Assert.Null(picker.Pick(DefaultPool, new Random(1))); // default pool has no T-Rex
    }

    [Fact]
    public void Fixed_ConsumesNoRng()
    {
        // The pick must not advance the room's namespaced stream: after a fixed pick, the next
        // rng.Next matches a fresh RNG's first draw.
        var picker = Dc2DonorPicker.Fixed(0x02);
        var rng = new Random(42);
        picker.Pick(DefaultPool, rng);
        Assert.Equal(new Random(42).Next(1000), rng.Next(1000));
    }
}
