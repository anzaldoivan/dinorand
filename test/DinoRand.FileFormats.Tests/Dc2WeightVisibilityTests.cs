using DinoRand.App;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// TDD guard for the weight-row visibility rule (docs/dc2/ENEMY-DISTRIBUTION-PLAN.md D7 follow-up):
/// a species' weight slider shows only while the boss/setpiece toggles admit it into the donor pool
/// — the rule IS pool membership (<see cref="Dc2SpeciesTable.IsDonorPoolMember"/>), not a parallel
/// hardcoded list, so a future registry species inherits it for free. Hiding is display-only: the
/// row keeps its weight value (a non-member's weight never affects the pick anyway).
/// </summary>
public class Dc2WeightVisibilityTests
{
    private const int Velo = 0x02, TRex = 0x03, Giga = 0x06, Ovi = 0x07,
                      Allo = 0x08, Trice = 0x09, Inos = 0x0e;

    [Theory]
    [InlineData(Velo)]
    [InlineData(Ovi)]
    [InlineData(Allo)]
    [InlineData(Inos)]
    public void NormalSpecies_AlwaysVisible_RegardlessOfToggles(int type)
    {
        Assert.True(Dc2SpeciesTable.IsDonorPoolMember(type, includeSetpiece: false, includeBoss: false));
        Assert.True(Dc2SpeciesTable.IsDonorPoolMember(type, includeSetpiece: true, includeBoss: false));
        Assert.True(Dc2SpeciesTable.IsDonorPoolMember(type, includeSetpiece: false, includeBoss: true));
        Assert.True(Dc2SpeciesTable.IsDonorPoolMember(type, includeSetpiece: true, includeBoss: true));
    }

    [Fact]
    public void SetpieceTriceratops_VisibleOnlyWithSetpieceToggle()
    {
        Assert.False(Dc2SpeciesTable.IsDonorPoolMember(Trice, includeSetpiece: false, includeBoss: false));
        Assert.False(Dc2SpeciesTable.IsDonorPoolMember(Trice, includeSetpiece: false, includeBoss: true));
        Assert.True(Dc2SpeciesTable.IsDonorPoolMember(Trice, includeSetpiece: true, includeBoss: false));
        Assert.True(Dc2SpeciesTable.IsDonorPoolMember(Trice, includeSetpiece: true, includeBoss: true));
    }

    [Theory]
    [InlineData(TRex)]
    [InlineData(Giga)]
    public void BossSpecies_VisibleOnlyWithBossToggle(int type)
    {
        Assert.False(Dc2SpeciesTable.IsDonorPoolMember(type, includeSetpiece: false, includeBoss: false));
        Assert.False(Dc2SpeciesTable.IsDonorPoolMember(type, includeSetpiece: true, includeBoss: false));
        Assert.True(Dc2SpeciesTable.IsDonorPoolMember(type, includeSetpiece: false, includeBoss: true));
        Assert.True(Dc2SpeciesTable.IsDonorPoolMember(type, includeSetpiece: true, includeBoss: true));
    }

    [Fact]
    public void Membership_MatchesDonorPool_ForEveryRegistrySpeciesAndToggleCombination()
    {
        // The predicate must be the SAME rule as the pool the pass actually draws from — if they
        // ever diverge, a visible slider could weight a species the pick can't choose (or vice versa).
        foreach (var setpiece in new[] { false, true })
            foreach (var boss in new[] { false, true })
            {
                var pool = Dc2SpeciesTable.DonorPool(setpiece, boss).Select(d => d.Type).ToHashSet();
                foreach (var row in Dc2EnemyDistribution.LoadEmbedded().Rows)
                    Assert.Equal(pool.Contains(row.Type),
                                 Dc2SpeciesTable.IsDonorPoolMember(row.Type, setpiece, boss));
            }
    }

    [Fact]
    public void NonDonorSpecies_AreNeverMembers()
    {
        // Aquatic Mosasaurus, flyer Pteranodon, unresolved shared types: never weighable, any toggles.
        foreach (var type in new[] { 0x05, 0x04, 0x0a, 0x0b, 0x0c })
            Assert.False(Dc2SpeciesTable.IsDonorPoolMember(type, includeSetpiece: true, includeBoss: true));
        Assert.False(Dc2SpeciesTable.IsDonorPoolMember(0x55, true, true)); // unknown TYPE
    }

    // --- The thin UI consumer: Dc2WeightOption.IsVisible ------------------------------------------

    [Fact]
    public void WeightOption_IsVisible_DefaultsTrue_AndNotifiesOnChange()
    {
        var row = new Dc2WeightOption("Giganotosaurus", Giga, 1);
        Assert.True(row.IsVisible);

        var notified = new List<string>();
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName!);
        row.IsVisible = false;
        Assert.False(row.IsVisible);
        Assert.Contains(nameof(Dc2WeightOption.IsVisible), notified);
    }

    [Fact]
    public void WeightOption_HidingDoesNotTouchTheWeight()
    {
        // Display-only: toggling visibility must never reset the value (it round-trips in the seed).
        var row = new Dc2WeightOption("Tyrannosaurus", TRex, 2) { Weight = 9 };
        row.IsVisible = false;
        row.IsVisible = true;
        Assert.Equal(9, row.Weight);
    }
}
