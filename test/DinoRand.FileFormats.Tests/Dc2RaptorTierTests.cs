using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Spoiler;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Tests for the raptor tier feature (docs/dc2/RAPTOR-TIER-RE.md §4): the pure weighted planner,
/// the registry defaults (blue/super = weight 1), the Dino2.exe patch levers (build-gated), the
/// wave-table variant_off plumbing, and the seed-string raptor block round-trip.
/// </summary>
public class Dc2RaptorTierTests
{
    // --- registry -----------------------------------------------------------

    [Fact]
    public void TierTable_Defaults_BlueRareCommonFour()
    {
        var t = Dc2RaptorTierTable.LoadEmbedded();
        Assert.Equal(8, t.Rows.Count); // variants 0..7 (7 = the common stock static variant)
        Assert.Equal(1, t.DefaultWeights[Dc2RaptorTierTable.SuperRaptorVariant]);
        Assert.All(t.Rows.Where(r => r.Variant != Dc2RaptorTierTable.SuperRaptorVariant),
                   r => Assert.Equal(4, r.DefaultWeight));
        Assert.Equal(10000, t.Rows.Single(r => r.Variant == 5).HpBase);
    }

    [Fact]
    public void TierTable_EffectiveWeights_OverridesAndClamps()
    {
        var t = Dc2RaptorTierTable.LoadEmbedded();
        var w = t.EffectiveWeights(new Dictionary<int, byte> { [5] = 15, [0] = 0, [9] = 7 });
        Assert.Equal(15, w[5]);
        Assert.Equal(0, w[0]);
        Assert.False(w.ContainsKey(9)); // unknown variant ignored
    }

    // --- planner ------------------------------------------------------------

    private static readonly IReadOnlyDictionary<int, byte> DefaultWeights =
        Dc2RaptorTierTable.LoadEmbedded().DefaultWeights;

    [Fact]
    public void PickVariant_AllZeroWeights_ReturnsNull()
    {
        var weights = Enumerable.Range(0, 8).ToDictionary(v => v, _ => (byte)0);
        Assert.Null(Dc2RaptorTierPlanner.PickVariant(weights, new Random(1)));
    }

    [Fact]
    public void PickVariant_ZeroWeightVariantNeverDrawn_AndAlwaysInRange()
    {
        var weights = new Dictionary<int, byte>(DefaultWeights) { [3] = 0 };
        var rng = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            int v = Dc2RaptorTierPlanner.PickVariant(weights, rng)!.Value;
            Assert.NotEqual(3, v);
            Assert.InRange(v, 0, Dc2RaptorTierTable.MaxVariant);
        }
    }

    [Fact]
    public void PlanRoom_SameSeed_IsDeterministic_AndPreservesFlagBits()
    {
        var statics = new (int, short)[] { (100, 0x10), (200, 0x00), (300, 0x07) };
        var wave = new[] { 400 };
        var a = Dc2RaptorTierPlanner.PlanRoom(statics, wave, new Random(99), DefaultWeights);
        var b = Dc2RaptorTierPlanner.PlanRoom(statics, wave, new Random(99), DefaultWeights);
        Assert.Equal(a.WordEdits, b.WordEdits);
        Assert.Equal(a.ByteEdits, b.ByteEdits);
        Assert.Equal(3, a.WordEdits.Count);
        Assert.Single(a.ByteEdits);
        Assert.Equal(0x10, a.WordEdits[0].Variant & 0xF0); // ST102-style store flag preserved
        Assert.All(a.WordEdits, w => Assert.InRange((short)(w.Variant & 0xF), (short)0, (short)7));
        Assert.All(a.ByteEdits, e => Assert.InRange(e.Value, (byte)0, (byte)7));
    }

    // --- colour modes (RAPTOR-TIER-RE.md §4b: desc+4 raw = room colour; pair table = stats) ------

    [Fact]
    public void PlanRoom_WaveByte_IsWeightedVariantDraw()
    {
        // Weight only variant 6 ⇒ the room's desc+4 (colour) byte must be 6, not 0-3 jitter.
        var weights = Enumerable.Range(0, 8).ToDictionary(v => v, v => (byte)(v == 6 ? 4 : 0));
        for (int seed = 0; seed < 50; seed++)
        {
            var plan = Dc2RaptorTierPlanner.PlanRoom(
                Array.Empty<(int, short)>(), new[] { 400, 500 }, new Random(seed), weights);
            Assert.All(plan.ByteEdits, e => Assert.Equal(6, e.Value));
        }
    }

    [Fact]
    public void PlanRoom_StaticsAndWave_ShareOneRoomVariant()
    {
        // The room draws ONE variant: every static tier nibble and every wave byte carry it
        // (colour is per-room; per-spawn draws would desync colour from stats).
        var statics = new (int, short)[] { (100, 0x10), (200, 0x00), (300, 0x07) };
        for (int seed = 0; seed < 50; seed++)
        {
            var plan = Dc2RaptorTierPlanner.PlanRoom(statics, new[] { 400 }, new Random(seed), DefaultWeights);
            var nibbles = plan.WordEdits.Select(w => w.Variant & 0xF)
                .Concat(plan.ByteEdits.Select(b => (int)b.Value)).Distinct().ToList();
            Assert.Single(nibbles);
        }
    }

    [Fact]
    public void PlanPairTable_RoomTierMode_IsIdentity()
    {
        var tiers = Dc2RaptorTierTable.LoadEmbedded();
        var pairs = Dc2RaptorTierPlanner.PlanPairTable(
            new Random(5), DefaultWeights, Dc2RaptorColourMode.RoomTier, tiers)!;
        Assert.Equal(20, pairs.Length);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(Math.Min(i, 7), pairs[i * 2]);     // stats == the desc+4 that selected it
            Assert.Equal(Math.Min(i, 7), pairs[i * 2 + 1]);
        }
    }

    [Fact]
    public void PlanPairTable_MixedMode_FirstByteIsIndex_SecondNeverStronger()
    {
        var tiers = Dc2RaptorTierTable.LoadEmbedded();
        int HpOf(byte v) => tiers.Rows.Single(r => r.Variant == v).HpBase;
        for (int seed = 0; seed < 20; seed++)
        {
            var pairs = Dc2RaptorTierPlanner.PlanPairTable(
                new Random(seed), DefaultWeights, Dc2RaptorColourMode.MixedTiers, tiers)!;
            for (int i = 0; i < 10; i++)
            {
                byte colour = pairs[i * 2], other = pairs[i * 2 + 1];
                Assert.Equal(Math.Min(i, 7), colour);                 // colour tier = strongest
                Assert.True(HpOf(other) <= HpOf(colour),
                    $"pair[{i}]: second byte V{other} (HP {HpOf(other)}) stronger than colour V{colour} (HP {HpOf(colour)})");
            }
        }
    }

    [Fact]
    public void PlanPairTable_MixedMode_ZeroWeightWeakerVariants_FallBackToColour()
    {
        var tiers = Dc2RaptorTierTable.LoadEmbedded();
        // Only variant 4 weighted ⇒ no weaker-weighted pool for most entries; second byte must
        // fall back to the colour tier, never draw a weight-0 variant stronger than the colour.
        var weights = Enumerable.Range(0, 8).ToDictionary(v => v, v => (byte)(v == 4 ? 4 : 0));
        var pairs = Dc2RaptorTierPlanner.PlanPairTable(
            new Random(3), weights, Dc2RaptorColourMode.MixedTiers, tiers)!;
        int HpOf(byte v) => tiers.Rows.Single(r => r.Variant == v).HpBase;
        for (int i = 0; i < 10; i++)
        {
            byte colour = pairs[i * 2], other = pairs[i * 2 + 1];
            Assert.True(other == colour || HpOf(other) <= HpOf(colour));
        }
    }

    // --- exe patch ----------------------------------------------------------

    /// <summary>A synthetic pristine-build buffer carrying both patch sites.</summary>
    internal static byte[] FakeExe()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        new byte[] { 0x66, 0x83, 0xB8, 0x24, 0x0C, 0x00, 0x00, 0x13, 0x76 }
            .CopyTo(exe, 0x1E59F); // cmp word [eax+0xC24],0x13; jbe
        Dc2RaptorPatch.PairTableVanilla.CopyTo(exe.AsSpan(Dc2RaptorPatch.PairTableOffset));
        return exe;
    }

    [Theory]
    [InlineData(1, 0x00)]
    [InlineData(3, 0x02)]
    [InlineData(20, 0x13)]
    public void ComboThreshold_MapsToImm8(int threshold, byte imm)
    {
        var exe = FakeExe();
        Dc2RaptorPatch.ApplyComboThreshold(exe, threshold);
        Assert.Equal(imm, exe[Dc2RaptorPatch.ComboImmOffset]);
        // idempotent re-patch (installer restores from .bak, but the site stays recognizable anyway)
        Dc2RaptorPatch.ApplyComboThreshold(exe, 20);
        Assert.Equal(0x13, exe[Dc2RaptorPatch.ComboImmOffset]);
    }

    [Fact]
    public void ComboThreshold_RejectsOutOfRangeAndWrongBuild()
    {
        var exe = FakeExe();
        Assert.Throws<ArgumentOutOfRangeException>(() => Dc2RaptorPatch.ApplyComboThreshold(exe, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Dc2RaptorPatch.ApplyComboThreshold(exe, 21));
        Assert.Throws<InvalidOperationException>(
            () => Dc2RaptorPatch.ApplyComboThreshold(new byte[123], 10));         // wrong length
        var scrambled = FakeExe();
        scrambled[0x1E59F] = 0x90;                                                // wrong site bytes
        Assert.Throws<InvalidOperationException>(
            () => Dc2RaptorPatch.ApplyComboThreshold(scrambled, 10));
    }

    [Fact]
    public void PairTable_ApplyRewritesPairsOnly_AndStaysRecognized()
    {
        var exe = FakeExe();
        Assert.True(Dc2RaptorPatch.IsPairTablePristine(exe));
        var pairs = Enumerable.Range(0, 20).Select(i => (byte)(i % 8)).ToArray();
        Dc2RaptorPatch.ApplyPairTable(exe, pairs);
        for (int i = 0; i < Dc2RaptorPatch.PairTableEntries; i++)
        {
            Assert.Equal(pairs[i * 2], exe[Dc2RaptorPatch.PairTableOffset + i * 4]);
            Assert.Equal(pairs[i * 2 + 1], exe[Dc2RaptorPatch.PairTableOffset + i * 4 + 1]);
            // bytes 2-3 of each entry untouched
            Assert.Equal(Dc2RaptorPatch.PairTableVanilla[i * 4 + 2], exe[Dc2RaptorPatch.PairTableOffset + i * 4 + 2]);
            Assert.Equal(Dc2RaptorPatch.PairTableVanilla[i * 4 + 3], exe[Dc2RaptorPatch.PairTableOffset + i * 4 + 3]);
        }
        Assert.False(Dc2RaptorPatch.IsPairTablePristine(exe));
        Assert.True(Dc2RaptorPatch.IsPairTableRecognized(exe)); // re-patchable on another seed
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Dc2RaptorPatch.ApplyPairTable(exe, Enumerable.Repeat((byte)8, 20).ToArray()));
    }

    // --- wave table plumbing --------------------------------------------------

    [Fact]
    public void WaveTable_ParsesVariantOff_St40A()
    {
        var wave = Dc2WaveTable.LoadEmbedded().ForRoom("40A");
        Assert.NotNull(wave);
        var d = wave!.Descriptors.Single();
        Assert.Equal(0x757C, d.VariantOff);
        Assert.Equal(2, d.Variant);
        Assert.Equal(2, d.NativeType);
    }

    [Fact]
    public void SpawnGraph_ParsesVariantField_St102()
    {
        var spawns = Dc2SpawnGraph.LoadEmbedded().ForRoom("102");
        Assert.NotNull(spawns);
        var raptors = spawns!.Where(s => s.Type == 0x02 && s.TypeMode == 0).ToList();
        Assert.NotEmpty(raptors);
        Assert.All(raptors, s => Assert.True(s.VariantValueOff > 0));
    }

    // --- seed string ----------------------------------------------------------

    [Fact]
    public void SeedString_DefaultConfig_HasNoRaptorBlock()
    {
        var s = SeedString.Encode(new Seed(1234), new RandomizerConfig());
        Assert.True(SeedString.TryParse(s, out _, out var cfg));
        Assert.False(cfg.Dc2RandomizeRaptorTiers);
        Assert.Equal(20, cfg.Dc2BlueRaptorComboThreshold);
        Assert.Null(cfg.Dc2RaptorTierWeights);
    }

    [Fact]
    public void SeedString_RaptorBlock_RoundTrips()
    {
        var config = new RandomizerConfig
        {
            Dc2RandomizeRaptorTiers = true,
            Dc2BlueRaptorComboThreshold = 3,
            Dc2RaptorTierWeights = new Dictionary<int, byte> { [5] = 15, [0] = 0 },
        };
        var s = SeedString.Encode(new Seed(-77), config);
        Assert.True(SeedString.TryParse(s, out var seed, out var cfg));
        Assert.Equal(-77, seed.Value);
        Assert.True(cfg.Dc2RandomizeRaptorTiers);
        Assert.Equal(3, cfg.Dc2BlueRaptorComboThreshold);
        Assert.NotNull(cfg.Dc2RaptorTierWeights);
        Assert.Equal(15, cfg.Dc2RaptorTierWeights![5]);
        Assert.Equal(0, cfg.Dc2RaptorTierWeights![0]);
        Assert.Equal(4, cfg.Dc2RaptorTierWeights![1]); // untouched default carried in the nibbles
    }

    [Fact]
    public void SeedString_RaptorColourMode_RoundTrips()
    {
        var config = new RandomizerConfig
        {
            Dc2RandomizeRaptorTiers = true,
            Dc2RaptorColourMode = Dc2RaptorColourMode.MixedTiers,
        };
        Assert.True(SeedString.TryParse(SeedString.Encode(new Seed(9), config), out _, out var cfg));
        Assert.Equal(Dc2RaptorColourMode.MixedTiers, cfg.Dc2RaptorColourMode);
        // default mode round-trips too (and stays the wire default for old seeds)
        Assert.True(SeedString.TryParse(SeedString.Encode(new Seed(9),
            new RandomizerConfig { Dc2RandomizeRaptorTiers = true }), out _, out var cfg2));
        Assert.Equal(Dc2RaptorColourMode.RoomTier, cfg2.Dc2RaptorColourMode);
    }

    [Fact]
    public void SeedString_ThresholdOnly_EmitsBlockAndRoundTrips()
    {
        var config = new RandomizerConfig { Dc2BlueRaptorComboThreshold = 10 };
        Assert.True(SeedString.TryParse(SeedString.Encode(new Seed(5), config), out _, out var cfg));
        Assert.Equal(10, cfg.Dc2BlueRaptorComboThreshold);
        Assert.False(cfg.Dc2RandomizeRaptorTiers);
        Assert.Null(cfg.Dc2RaptorTierWeights); // default weights decode back to null
    }
}
