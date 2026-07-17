using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The DC2 aquatic-enemy randomization feature (docs/decisions/dc2/enemies/DC2-AQUATIC-LAND-UNLOCK-FEASIBILITY.md,
/// K72). ALL default-OFF: with <see cref="RandomizerConfig.Dc2AllowWaterLevelEnemySwaps"/> off the pool,
/// room eligibility, and emitted edits are identical to before. Covers D1 (hard-block aquatic-native
/// rooms), D2 (species reclassification per K68/K66/K70), D3 (aquatic donor placement gated to the
/// wave/preload path, never op-0x1a), and D4 (the experimental water flag).
/// </summary>
public class Dc2AquaticEnemyTests
{
    private static Dc2SpawnRecord Spawn(int type, int slot, int off, int mode = 0) => new(type, mode, off, slot);

    // A wave room with ONLY armed descriptors — no op-0x1a spawns, no generic creature spawns, so
    // every edit lands on the crash-safe wave (op-0x4f) path.
    private static Dc2WaveRoom WaveOnly(int nativeType) => new(
        new[] { new Dc2WaveDescriptor(0x100, 0x101, NativeType: nativeType, Armed: true) },
        Array.Empty<Dc2GenericCreatureSpawn>());

    private static Dc2WaveRoom RaptorWaveRoom() => new(
        new[] { new Dc2WaveDescriptor(0x5ed4, 0x5ed5, NativeType: 0x02, Armed: true) },
        new[] { new Dc2GenericCreatureSpawn(0x200, 0x210, 0x220, HpPushMode: 6, MbBase: 0x633000) });

    // === D1 — aquatic-native rooms hard-blocked by default =========================================

    [Fact]
    public void D1_AquaticRooms_Explicit_Contains_700_702_703_704()
    {
        foreach (var r in new[] { "700", "702", "703", "704" })
            Assert.True(Dc2AquaticRooms.Contains(r), $"ST{r} must be a hard-blocked aquatic-native room");
    }

    // === D2 — species mapping + reclassification (K68/K66/K70) ======================================

    [Fact]
    public void D2_AquaticSpecies_MappingMatchesK68()
    {
        var boss = Dc2SpeciesTable.ForType(0x05)!;
        Assert.Equal("Plesiosaurus (boss form)", boss.Creature);
        Assert.Equal("E30", boss.EFile);
        Assert.Equal(Dc2Habitat.Aquatic, boss.Habitat);
        Assert.Equal(Confidence.Known, boss.Confidence);

        var mosa = Dc2SpeciesTable.ForType(0x0a)!;
        Assert.Equal("Mosasaurus", mosa.Creature);
        Assert.Equal("E80", mosa.EFile);
        Assert.Equal(Dc2Habitat.Aquatic, mosa.Habitat);
        Assert.Equal(Confidence.Known, mosa.Confidence);

        Assert.Equal("E31", Dc2SpeciesTable.ForType(0x0b)!.EFile);
        Assert.Equal("E32", Dc2SpeciesTable.ForType(0x0c)!.EFile);
        Assert.All(new[] { 0x0b, 0x0c }, t =>
        {
            var s = Dc2SpeciesTable.ForType(t)!;
            Assert.Equal("Plesiosaurus (regular/grunt form)", s.Creature);
            Assert.Equal(Dc2Habitat.Aquatic, s.Habitat);
            Assert.Equal(Confidence.Known, s.Confidence);
        });
    }

    [Fact]
    public void D2_PlesiosaurusAndGiganotosaurus_AreSetpiece_MosasaurusIsRegular()
    {
        foreach (var t in new[] { 0x05, 0x0b, 0x0c, 0x06 })
            Assert.True(Dc2SpeciesTable.ForType(t)!.IsSetpiece, $"0x{t:X2} must be IsSetpiece");
        Assert.False(Dc2SpeciesTable.ForType(0x06)!.IsBoss); // Giganotosaurus moved boss -> setpiece
        Assert.False(Dc2SpeciesTable.ForType(0x0a)!.IsSetpiece); // Mosasaurus is a regular (low) donor
        Assert.False(Dc2SpeciesTable.ForType(0x0a)!.IsBoss);
    }

    [Fact]
    public void D2_Distribution_HasLowWeightAquaticRows()
    {
        var dist = Dc2EnemyDistribution.LoadEmbedded();
        // 0x0c (E32) intentionally has NO row — hard-excluded as a donor (crash RCA 2026-07-17,
        // Dc2SpeciesTable.IsCrashProneDonorType). The other aquatics keep their low-weight rows.
        foreach (var t in new[] { 0x05, 0x0a, 0x0b })
            Assert.True(dist.DefaultWeights.ContainsKey(t), $"0x{t:X2} needs a distribution row");
        Assert.False(dist.DefaultWeights.ContainsKey(0x0c), "E32 (0x0c) must NOT have a distribution row");
        Assert.True(dist.DefaultWeights[0x0a] <= 2, "Mosasaurus must be a LOW default weight");
        Assert.True(dist.DefaultWeights[0x05] <= 2, "Plesiosaurus boss must be a very low default weight");
    }

    // === D2/D4 — donor pool gating ==================================================================

    [Fact]
    public void D4_Mosasaurus_EntersWeightedPool_OnlyWhenWaterOn()
    {
        var off = Dc2SpeciesTable.DonorPool(includeSetpiece: false, includeBoss: false, allowWater: false)
            .Select(s => s.Type);
        Assert.DoesNotContain(0x0a, off);

        var on = Dc2SpeciesTable.DonorPool(includeSetpiece: false, includeBoss: false, allowWater: true)
            .Select(s => s.Type).ToList();
        Assert.Contains(0x0a, on);              // Mosasaurus in the default weighted pool when water on
        Assert.DoesNotContain(0x05, on);        // the setpiece Plesios still need the setpiece opt-in
        Assert.DoesNotContain(0x0b, on);
        Assert.DoesNotContain(0x0c, on);

        var onSet = Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: false, allowWater: true)
            .Select(s => s.Type).ToList();
        Assert.Contains(0x05, onSet);
        Assert.Contains(0x0b, onSet);
        Assert.DoesNotContain(0x0c, onSet); // E32 hard-excluded even with setpiece+water (crash RCA 2026-07-17)
    }

    [Fact]
    public void D4_WaterOff_MaximalPool_IsLandOnly()
    {
        // Byte-identical regression: with water off, even both opt-ins on yields the same LAND-only
        // maximal pool as before the feature (no aquatic species can leak in).
        var max = Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: true, allowWater: false)
            .Select(s => s.Type).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 0x02, 0x03, 0x06, 0x07, 0x08, 0x09, 0x0e }, max);
        Assert.DoesNotContain(0x0a, max);
    }

    // === D3 — aquatic donor placement gated to the wave/preload path ================================

    [Fact]
    public void D3_AquaticDonor_AllowedOnWaveOnlyRoom()
    {
        var pool = new[] { Dc2SpeciesTable.ForType(0x0a)! }; // Mosasaurus (aquatic)
        var plan = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(
            Array.Empty<Dc2SpawnRecord>(), WaveOnly(0x02), new Random(1), pool, Dc2DonorPicker.Fixed(0x0a));
        Assert.False(plan.IsEmpty);
        Assert.Empty(plan.WordEdits); // never onto an op-0x1a record
        Assert.Contains(plan.ByteEdits, b => b.Offset == 0x101 && b.NewValue == 0x0a);
    }

    [Fact]
    public void D3_AquaticDonor_NeverEmittedOntoOp1aSpawn()
    {
        var pool = new[] { Dc2SpeciesTable.ForType(0x0a)! };
        var plan = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(
            new[] { Spawn(0x02, 5, 500) }, RaptorWaveRoom(), new Random(1), pool, Dc2DonorPicker.Fixed(0x0a));
        Assert.True(plan.IsEmpty, "a room with an op-0x1a/generic spawn must skip aquatic donors");
    }

    [Fact]
    public void D3_WaveLessRoom_SkipsAquaticDonors()
    {
        var pool = new[] { Dc2SpeciesTable.ForType(0x0a)! };
        Assert.Empty(Dc2CrossSpeciesPlanner.PlanRoom(
            new[] { Spawn(0x02, 5, 500) }, new Random(1), pool, Dc2DonorPicker.Fixed(0x0a)));
    }

    // === D4 — room eligibility gated by the water flag ==============================================

    [Fact]
    public void D4_WaterOff_AquaticNativeWaveRoom_LeftVanilla()
    {
        var plan = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(
            Array.Empty<Dc2SpawnRecord>(), WaveOnly(0x0a), new Random(1),
            Dc2SpeciesTable.DefaultDonors, null, allowWaterLevels: false);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void D4_WaterOn_AquaticNativeWaveRoom_GetsLandDonor()
    {
        var plan = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(
            Array.Empty<Dc2SpawnRecord>(), WaveOnly(0x0a), new Random(1),
            Dc2SpeciesTable.DefaultDonors, null, allowWaterLevels: true);
        Assert.False(plan.IsEmpty);
        Assert.Contains(plan.ByteEdits, b => b.Offset == 0x101); // land donor placed on the wave descriptor
    }

    [Fact]
    public void D4_FlyerNativeWaveRoom_StaysBlocked_EvenWithWaterOn()
    {
        var plan = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(
            Array.Empty<Dc2SpawnRecord>(), WaveOnly(0x04), new Random(1),
            Dc2SpeciesTable.DefaultDonors, null, allowWaterLevels: true);
        Assert.True(plan.IsEmpty, "the water flag must not lift flyer-native protection");
    }

    [Theory]
    [InlineData(0x0b)] // Plesiosaurus grunt E31 — native to ST600/601/604
    [InlineData(0x0c)] // Plesiosaurus grunt E32 — native to ST001
    public void D4_PlesiosaurusGruntNativeWaveRoom_StaysBlocked_EvenWithWaterOn(int nativeType)
    {
        // Plesiosaurus-grunt water rooms (ST001/600/601/604) have invisible colliders a swapped donor
        // attacks the player through (playtest 2026-07-12). Like the flyer skip, the water flag must NOT
        // lift them — a full LAND donor pool + water on must still leave the room vanilla. This replaces
        // the per-room ST600/601 hard-block: coverage is by native type, so ST604 is caught for free.
        var plan = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(
            Array.Empty<Dc2SpawnRecord>(), WaveOnly(nativeType), new Random(1),
            Dc2SpeciesTable.DefaultDonors, null, allowWaterLevels: true);
        Assert.True(plan.IsEmpty, "the water flag must not lift Plesiosaurus-grunt-native protection");
        Assert.True(Dc2SpeciesTable.IsPlesiosaurusGruntNativeType(nativeType));
        Assert.True(Dc2SpeciesTable.IsUnconditionalSkipNativeType(nativeType));
    }

    [Fact]
    public void D4_Config_WaterFlag_DefaultsOff() =>
        Assert.False(new RandomizerConfig().Dc2AllowWaterLevelEnemySwaps);
}
