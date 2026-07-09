using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Guards the donor-distribution registry (<c>data/dc2/enemy-distribution.json</c>,
/// docs/dc2/ENEMY-DISTRIBUTION-PLAN.md D2/D4/D5): its rows are exactly the weighable
/// (<c>Known</c>+LAND) species, the curated numbers hold the plan's rarity targets, and the
/// override overlay stays total.
/// </summary>
public class Dc2EnemyDistributionTests
{
    private static readonly Dc2EnemyDistribution Dist = Dc2EnemyDistribution.LoadEmbedded();

    [Fact]
    public void Registry_RowsAreExactlyTheMaximalDonorPool()
    {
        // The weighable set == every POSSIBLE donor (both opt-ins + the water flag on, which is the only
        // way the aquatic wave-only donors ever become weighable). A species added to Dc2SpeciesTable as
        // a Known donor must get a registry row (and vice versa) or this locks.
        var weighable = Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: true, allowWater: true)
            .Select(s => s.Type).OrderBy(t => t);
        Assert.Equal(weighable, Dist.Rows.Select(r => r.Type));
    }

    [Fact]
    public void Registry_CreatureNamesMatchTheSpeciesTable()
    {
        Assert.All(Dist.Rows, r =>
            Assert.Equal(Dc2SpeciesTable.ForType(r.Type)!.Creature, r.Creature));
    }

    [Fact]
    public void Registry_RowsAreInCanonicalAscendingTypeOrder()
    {
        Assert.Equal(Dist.Rows.Select(r => r.Type).OrderBy(t => t), Dist.Rows.Select(r => r.Type));
    }

    [Fact]
    public void CuratedDefaults_NormalsEqual_BossesRareAndCapped()
    {
        // Plan D2/D4: every non-boss species weight 8 (equal ⇒ legacy-uniform regression holds),
        // T-Rex 2 (≈5% nominal) capped at 2 rooms, Giga 1 (≈3% nominal) capped at 1 room.
        foreach (var t in new[] { 0x02, 0x07, 0x08, 0x09, 0x0e })
        {
            Assert.Equal(8, Dist.DefaultWeights[t]);
            Assert.False(Dist.RoomCaps.ContainsKey(t));
        }
        Assert.Equal(2, Dist.DefaultWeights[0x03]);
        Assert.Equal(1, Dist.DefaultWeights[0x06]);
        Assert.Equal(2, Dist.RoomCaps[0x03]);
        Assert.Equal(1, Dist.RoomCaps[0x06]);
    }

    [Fact]
    public void DefaultWeights_FitTheSeedNibble()
    {
        Assert.All(Dist.Rows, r => Assert.InRange(r.DefaultWeight, (byte)0, Dc2DonorPicker.MaxWeight));
    }

    [Fact]
    public void EffectiveWeights_NullOrEmptyOverrides_AreTheDefaults()
    {
        Assert.Same(Dist.DefaultWeights, Dist.EffectiveWeights(null));
        Assert.Same(Dist.DefaultWeights, Dist.EffectiveWeights(new Dictionary<int, byte>()));
    }

    [Fact]
    public void EffectiveWeights_OverlayOverridesAndIgnoreUnknownTypes()
    {
        var eff = Dist.EffectiveWeights(new Dictionary<int, byte> { [0x02] = 0, [0x03] = 9, [0x55] = 7 });
        Assert.Equal(0, eff[0x02]);                     // overridden
        Assert.Equal(9, eff[0x03]);                     // overridden
        Assert.Equal(8, eff[0x07]);                     // default kept
        Assert.False(eff.ContainsKey(0x55));            // unknown TYPE can't enter the table
        Assert.Equal(Dist.Rows.Count, eff.Count);       // stays total over the registry
    }

    [Fact]
    public void EffectiveWeights_ClampToTheNibbleRange()
    {
        var eff = Dist.EffectiveWeights(new Dictionary<int, byte> { [0x02] = 200 });
        Assert.Equal(Dc2DonorPicker.MaxWeight, eff[0x02]);
    }
}
