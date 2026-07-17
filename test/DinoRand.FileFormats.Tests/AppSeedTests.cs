using DinoRand.App;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Guards the <c>DINO-…</c> seed-string wire format around the DC2 enemy-distribution block
/// (AppSeed bytes 11–15, docs/dc2/ENEMY-DISTRIBUTION-PLAN.md D6): default configs keep their
/// historical 10-byte payload, non-default distribution round-trips (mode, pin, toggles, weights),
/// and legacy payload lengths still parse to their original runs.
/// </summary>
public class AppSeedTests
{
    private static AppSeed Make(RandomizerConfig config) => AppSeed.Random().WithConfig(config);

    private static byte[] Payload(AppSeed seed)
    {
        var b64 = seed.ToString()[5..].Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64.PadRight((b64.Length + 3) / 4 * 4, '='));
    }

    private static AppSeed RoundTrip(AppSeed seed)
    {
        Assert.True(AppSeed.TryParse(seed.ToString(), out var parsed));
        return parsed;
    }

    [Fact]
    public void DefaultConfig_KeepsTheHistorical10BytePayload()
    {
        // The DC2 block must be omitted for an all-default config so every pre-feature seed string
        // stays byte-identical (plan D6).
        var seed = Make(new RandomizerConfig());
        Assert.Equal(10, Payload(seed).Length);
        Assert.Equal(seed.ToString(), RoundTrip(seed).ToString());
    }

    [Fact]
    public void AmmoReductionOnly_KeepsTheHistorical11BytePayload()
    {
        var seed = Make(new RandomizerConfig { AmmoReduction = 3 });
        Assert.Equal(11, Payload(seed).Length);
        Assert.Equal(3, RoundTrip(seed).Config.AmmoReduction);
    }

    [Fact]
    public void BossAndSetpieceToggles_AreNowSeedEncoded()
    {
        // The pre-existing gap this block closes (audit G3): a pasted seed used to silently drop
        // these toggles.
        var seed = Make(new RandomizerConfig
        {
            IncludeDc2BossEnemies = true,
            IncludeDc2SetpieceEnemies = true,
        });
        Assert.Equal(16, Payload(seed).Length);
        var back = RoundTrip(seed).Config;
        Assert.True(back.IncludeDc2BossEnemies);
        Assert.True(back.IncludeDc2SetpieceEnemies);
        Assert.Equal(Dc2EnemyDistributionMode.Weighted, back.Dc2EnemyMode);
        Assert.Null(back.Dc2SpeciesWeights); // default nibbles decode back to "use defaults"
    }

    [Fact]
    public void FixedMode_RoundTripsThePinnedSpecies()
    {
        foreach (var pin in AppSeed.Dc2CanonicalSpeciesOrder)
        {
            var seed = Make(new RandomizerConfig
            {
                Dc2EnemyMode = Dc2EnemyDistributionMode.Fixed,
                Dc2FixedSpeciesType = pin,
            });
            var back = RoundTrip(seed).Config;
            Assert.Equal(Dc2EnemyDistributionMode.Fixed, back.Dc2EnemyMode);
            Assert.Equal(pin, back.Dc2FixedSpeciesType);
        }
    }

    [Fact]
    public void WeightOverrides_RoundTrip()
    {
        var weights = new Dictionary<int, byte> { [0x02] = 0, [0x03] = 15, [0x07] = 3 };
        var seed = Make(new RandomizerConfig { Dc2SpeciesWeights = weights });
        var back = RoundTrip(seed).Config;
        Assert.NotNull(back.Dc2SpeciesWeights);
        // The decoded table is the full effective table (defaults + overrides).
        Assert.Equal(0, back.Dc2SpeciesWeights![0x02]);
        Assert.Equal(15, back.Dc2SpeciesWeights[0x03]);
        Assert.Equal(3, back.Dc2SpeciesWeights[0x07]);
        Assert.Equal(8, back.Dc2SpeciesWeights[0x08]); // untouched default carried through
        // And a re-encode is stable (same string).
        Assert.Equal(seed.ToString(), RoundTrip(seed).ToString());
    }

    [Fact]
    public void FixedModeWithTogglesAndWeights_RoundTripsEverything()
    {
        var seed = Make(new RandomizerConfig
        {
            Dc2EnemyMode = Dc2EnemyDistributionMode.Fixed,
            Dc2FixedSpeciesType = 0x0e,
            IncludeDc2BossEnemies = true,
            Dc2SpeciesWeights = new Dictionary<int, byte> { [0x06] = 5 },
            AmmoReduction = 2, // exercises the now-mandatory byte 10
        });
        var back = RoundTrip(seed).Config;
        Assert.Equal(0x0e, back.Dc2FixedSpeciesType);
        Assert.True(back.IncludeDc2BossEnemies);
        Assert.False(back.IncludeDc2SetpieceEnemies);
        Assert.Equal(5, back.Dc2SpeciesWeights![0x06]);
        Assert.Equal(2, back.AmmoReduction);
    }

    [Fact]
    public void LegacyPayloadLengths_StillParse_WithDefaultDc2Block()
    {
        // A pre-distribution seed (10/11 bytes) must decode with the DC2 block all-default.
        var old = Make(new RandomizerConfig { AmmoReduction = 1 });
        var back = RoundTrip(old).Config;
        Assert.Equal(Dc2EnemyDistributionMode.Weighted, back.Dc2EnemyMode);
        Assert.Null(back.Dc2FixedSpeciesType);
        Assert.Null(back.Dc2SpeciesWeights);
        Assert.False(back.IncludeDc2BossEnemies);
    }

    [Fact]
    public void ShopShuffle_RoundTrips_InByte16Bit2()
    {
        var seed = Make(new RandomizerConfig { Dc2ShuffleShop = true });
        Assert.Equal(17, Payload(seed).Length); // forces the byte-16 block, nothing more
        Assert.True(RoundTrip(seed).Config.Dc2ShuffleShop);

        // pre-feature payloads (≤16 bytes) decode to off
        var legacy = Make(new RandomizerConfig { IncludeDc2BossEnemies = true });
        Assert.Equal(16, Payload(legacy).Length);
        Assert.False(RoundTrip(legacy).Config.Dc2ShuffleShop);
    }

    [Fact]
    public void PuzzleMaster_RoundTrips_AsByte16Bits3And4()
    {
        // The GUI's single "Randomize Puzzles" flag rides the two subflag bits — no new wire field.
        var seed = Make(new RandomizerConfig { Dc2RandomizePuzzles = true });
        Assert.Equal(17, Payload(seed).Length); // forces the byte-16 block, nothing more
        var back = RoundTrip(seed).Config;
        Assert.True(back.Dc2ScramblePuzzleCodes);
        Assert.True(back.Dc2ShuffleCircuits);
        Assert.True(back.Dc2RandomizePuzzles);

        // Boundary: a single-subflag seed (CLI/legacy) round-trips the exact mixed state and still
        // presents as puzzles-on; pre-feature payloads decode fully off.
        var circuitsOnly = RoundTrip(Make(new RandomizerConfig { Dc2ShuffleCircuits = true })).Config;
        Assert.True(circuitsOnly.Dc2ShuffleCircuits);
        Assert.False(circuitsOnly.Dc2ScramblePuzzleCodes);
        Assert.True(circuitsOnly.Dc2RandomizePuzzles);

        var legacy = Make(new RandomizerConfig { IncludeDc2BossEnemies = true });
        Assert.False(RoundTrip(legacy).Config.Dc2RandomizePuzzles);
    }

    [Fact]
    public void CanonicalSpeciesOrder_LocksToTheSeedEncodedLandSpecies()
    {
        // The wire order is FROZEN and only holds the LAND species — the aquatic donors (0x05/0x0a/0x0b/
        // 0x0c) are experimental, gated behind Dc2AllowWaterLevelEnemySwaps (itself NOT seed-encoded), so
        // their weights are intentionally NOT carried in the 8-nibble seed block. The canonical order must
        // therefore equal the registry rows MINUS the aquatic ones. If a new LAND weighable species lands,
        // APPEND it to AppSeed.Dc2CanonicalSpecies (never reorder) and extend the block — this is the tripwire.
        var seedEncodable = Dc2EnemyDistribution.LoadEmbedded().Rows
            .Select(r => r.Type)
            .Where(t => !Dc2SpeciesTable.IsWaterHabitat(Dc2SpeciesTable.ForType(t)!.Habitat));
        Assert.Equal(seedEncodable, AppSeed.Dc2CanonicalSpeciesOrder);
    }
}
