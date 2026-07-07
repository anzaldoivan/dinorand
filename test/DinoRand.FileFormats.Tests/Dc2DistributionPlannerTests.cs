using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Planner-level tests for the donor-distribution seam (docs/dc2/ENEMY-DISTRIBUTION-PLAN.md):
/// the curated-default weighted picker reproduces the legacy uniform planner byte-for-byte (the
/// regression criterion), fixed mode converts every eligible room to the pin (or leaves it
/// vanilla), and a declining picker degrades to "room unchanged".
/// </summary>
public class Dc2DistributionPlannerTests
{
    private static readonly Dc2DonorPicker DefaultWeighted =
        Dc2DonorPicker.Weighted(Dc2EnemyDistribution.LoadEmbedded().DefaultWeights);

    private static Dc2SpawnRecord Spawn(int type, int slot, int off, int mode = 0) =>
        new(type, mode, off, slot);

    private static Dc2WaveRoom RaptorWaveRoom() => new(
        new[] { new Dc2WaveDescriptor(0x5ed4, 0x5ed5, NativeType: 0x02, Armed: true),
                new Dc2WaveDescriptor(0x6194, 0x6195, NativeType: 0x02, Armed: false) },
        new[] { new Dc2GenericCreatureSpawn(0x200, 0x210, 0x220, HpPushMode: 6, MbBase: 0x633000) });

    // --- The regression criterion: untouched defaults == the shipped uniform behavior ------------

    [Fact]
    public void PlanRoom_DefaultWeights_IdenticalToLegacyUniform_AcrossManySeeds()
    {
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x02, 6, 600), Spawn(0x07, 7, 700) };
        for (int seed = 0; seed < 250; seed++)
        {
            var legacy = Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(seed));
            var weighted = Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(seed),
                                                           picker: DefaultWeighted);
            Assert.Equal(legacy.Select(e => (e.ValueOff, e.OldType, e.NewType)),
                         weighted.Select(e => (e.ValueOff, e.OldType, e.NewType)));
        }
    }

    [Fact]
    public void PlanRoomWithWaves_DefaultWeights_IdenticalToLegacyUniform_AcrossManySeeds()
    {
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x10, 6, 600) };
        var wave = RaptorWaveRoom();
        for (int seed = 0; seed < 250; seed++)
        {
            var legacy = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(spawns, wave, new Random(seed));
            var weighted = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(spawns, wave, new Random(seed),
                                                                    picker: DefaultWeighted);
            Assert.Equal(legacy.WordEdits.Select(e => (e.ValueOff, e.NewType)),
                         weighted.WordEdits.Select(e => (e.ValueOff, e.NewType)));
            Assert.Equal(legacy.ByteEdits.Select(e => (e.Offset, e.NewValue)),
                         weighted.ByteEdits.Select(e => (e.Offset, e.NewValue)));
            Assert.Equal(legacy.DonorType, weighted.DonorType);
        }
    }

    // --- Fixed mode --------------------------------------------------------------------------------

    [Fact]
    public void FixedTRex_ConvertsEveryEligibleEdit_ToType0x03()
    {
        // The all-T-Rex success criterion at planner level: every changed room's donor is 0x03 —
        // spawn words, wave descriptor bytes, and normalized ambush spawns alike.
        var picker = Dc2DonorPicker.Fixed(0x03);
        var pool = Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: true);
        var plan = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(
            new[] { Spawn(0x02, 5, 500) }, RaptorWaveRoom(), new Random(7), pool, picker);

        Assert.False(plan.IsEmpty);
        Assert.Equal(0x03, plan.DonorType);
        // Word edits are TYPE conversions (→0x03) plus normalization zero-writes (→0).
        Assert.All(plan.WordEdits, e => Assert.True(e.NewType is 0x03 or 0));
        Assert.Contains(plan.WordEdits, e => e.NewType == 0x03);
        // Descriptor species bytes carry the pin; normalization mode bytes are 0.
        Assert.Contains(plan.ByteEdits, b => b.Offset == 0x5ed5 && b.NewValue == 0x03);
        Assert.Contains(plan.ByteEdits, b => b.Offset == 0x6195 && b.NewValue == 0x03);
    }

    [Fact]
    public void FixedDonor_NativeToTheRoom_LeavesRoomVanilla()
    {
        // The collision guard outranks the pin: a room natively hosting the pinned species stays
        // unchanged rather than duplicating a TYPE across its slots (ST202 lesson).
        var picker = Dc2DonorPicker.Fixed(0x02);
        var pool = Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: true);
        Assert.Empty(Dc2CrossSpeciesPlanner.PlanRoom(
            new[] { Spawn(0x02, 5, 500) }, new Random(1), pool, picker));
    }

    [Fact]
    public void FixedSharedBaseDonor_ExcludedByLegacyBudgetGuard_LeavesRoomVanilla()
    {
        // Legacy (non-wave) path + generic-0x10 spawn ⇒ the shared-0x640000 budget guard removes
        // T-Rex from validDonors before the picker runs ⇒ the pin declines ⇒ vanilla room.
        var picker = Dc2DonorPicker.Fixed(0x03);
        var pool = Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: true);
        Assert.Empty(Dc2CrossSpeciesPlanner.PlanRoom(
            new[] { Spawn(0x02, 5, 500), Spawn(0x10, 6, 600) }, new Random(1), pool, picker));
    }

    [Fact]
    public void FixedMode_IsDeterministic_AndRngFree()
    {
        var picker = Dc2DonorPicker.Fixed(0x03);
        var pool = Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: true);
        var spawns = new[] { Spawn(0x02, 5, 500) };
        // Different seeds, same outcome — the pin doesn't ride the RNG.
        var a = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(spawns, RaptorWaveRoom(), new Random(1), pool, picker);
        var b = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(spawns, RaptorWaveRoom(), new Random(999), pool, picker);
        Assert.Equal(a.WordEdits.Select(e => (e.ValueOff, e.NewType)),
                     b.WordEdits.Select(e => (e.ValueOff, e.NewType)));
        Assert.Equal(a.ByteEdits.Select(e => (e.Offset, e.NewValue)),
                     b.ByteEdits.Select(e => (e.Offset, e.NewValue)));
    }

    // --- Declining picker ⇒ vanilla room (never uniform fallback) ---------------------------------

    [Fact]
    public void ZeroWeightedValidDonors_LeaveRoomVanilla_BothPaths()
    {
        // Every donor the room would accept is weight-0 ⇒ both planner paths leave it unchanged.
        var zeroAll = Dc2DonorPicker.Weighted(new Dictionary<int, byte>());
        Assert.Empty(Dc2CrossSpeciesPlanner.PlanRoom(
            new[] { Spawn(0x02, 5, 500) }, new Random(1), picker: zeroAll));
        Assert.True(Dc2CrossSpeciesPlanner.PlanRoomWithWaves(
            new[] { Spawn(0x02, 5, 500) }, RaptorWaveRoom(), new Random(1), picker: zeroAll).IsEmpty);
    }

    [Fact]
    public void PlanRoomWithWaves_CarriesTheDonorType()
    {
        var plan = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(
            new[] { Spawn(0x02, 5, 500) }, null, new Random(3));
        Assert.NotNull(plan.DonorType);
        Assert.Equal(plan.WordEdits[0].NewType, plan.DonorType);
        Assert.True(Dc2RoomPlan.Empty.DonorType is null);
    }
}
