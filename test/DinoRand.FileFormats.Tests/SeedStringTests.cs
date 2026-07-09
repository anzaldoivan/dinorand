using DinoRand.App;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Spoiler;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Locks the shared <see cref="SeedString"/> encoder (docs/SPOILER-LOG-PLAN.md §3.2 D) to the
/// UI-level <see cref="AppSeed"/> wire format: identical bytes for identical (seed, config), and
/// a round-trip through <see cref="AppSeed.TryParse"/> — the acceptance criterion for the
/// spoiler debug block's seed string.
/// </summary>
public class SeedStringTests
{
    private static readonly RandomizerConfig[] Cases =
    {
        new(),                                                     // defaults (10-byte payload)
        new() { RandomizeItems = false, ShuffleKeyItems = true },  // flag bits
        new() { RandomizeEnemyHp = true },                         // byte-4 bit6 (DC1 HP)
        new() { AmmoReduction = 3 },                               // 11-byte payload
        new()                                                      // 16-byte DC2 block
        {
            Dc2EnemyMode = Dc2EnemyDistributionMode.Fixed,
            Dc2FixedSpeciesType = 0x03,
            IncludeDc2BossEnemies = true,
        },
        new() { Dc2SpeciesWeights = new Dictionary<int, byte> { [0x02] = 0, [0x08] = 15 } },
        new() { Dc2RandomizeRaptorTiers = true },                  // 22-byte raptor block
        new() { Dc2RandomizeStartWeapon = true },                  // 23-byte, both random
        new()                                                      // 23-byte, explicit picks
        {
            Dc2RandomizeStartWeapon = true,
            Dc2DylanStartWeaponId = 0x03,
            Dc2ReginaStartWeaponId = 0x05,
        },
    };

    [Fact]
    public void Encode_MatchesAppSeedToString_ForAssortedConfigs()
    {
        foreach (var cfg in Cases)
        {
            var app = AppSeed.Random().WithConfig(cfg);
            Assert.Equal(app.ToString(), SeedString.Encode(app.Seed, cfg));
        }
    }

    [Fact]
    public void Encode_RoundTripsThroughAppSeedTryParse()
    {
        foreach (var cfg in Cases)
        {
            var seed = new Seed(987654);
            var s = SeedString.Encode(seed, cfg);
            Assert.True(AppSeed.TryParse(s, out var parsed), $"AppSeed.TryParse rejected '{s}'");
            Assert.Equal(seed.Value, parsed.Seed.Value);
            Assert.Equal(cfg.RandomizeItems, parsed.Config.RandomizeItems);
            Assert.Equal(cfg.ShuffleKeyItems, parsed.Config.ShuffleKeyItems);
            Assert.Equal(cfg.RandomizeEnemyHp, parsed.Config.RandomizeEnemyHp);
            Assert.Equal(cfg.AmmoReduction, parsed.Config.AmmoReduction);
            Assert.Equal(cfg.Dc2EnemyMode, parsed.Config.Dc2EnemyMode);
            Assert.Equal(cfg.Dc2FixedSpeciesType, parsed.Config.Dc2FixedSpeciesType);
            Assert.Equal(cfg.IncludeDc2BossEnemies, parsed.Config.IncludeDc2BossEnemies);
        }
    }

    [Fact]
    public void TryParse_RoundTripsItsOwnEncoding()
    {
        foreach (var cfg in Cases)
        {
            var seed = new Seed(-42);
            Assert.True(SeedString.TryParse(SeedString.Encode(seed, cfg), out var s2, out var c2));
            Assert.Equal(seed.Value, s2.Value);
            Assert.Equal(SeedString.Encode(seed, cfg), SeedString.Encode(s2, c2));
        }
    }

    /// <summary>Byte 22 (DC2 starting weapon, DC2-STARTING-LOADOUT-PLAN.md): explicit ids and
    /// random-from-band round-trip; the byte is absent when the option is off, so pre-feature
    /// seeds (≤22 bytes) parse with the feature defaulted off.</summary>
    [Fact]
    public void StartWeaponByte_RoundTrips_AndIsAbsentWhenOff()
    {
        var seed = new Seed(123);

        var explicitCfg = new RandomizerConfig
        {
            Dc2RandomizeStartWeapon = true,
            Dc2DylanStartWeaponId = 0x03,
            Dc2ReginaStartWeaponId = 0x05,
        };
        Assert.True(SeedString.TryParse(SeedString.Encode(seed, explicitCfg), out _, out var c));
        Assert.True(c.Dc2RandomizeStartWeapon);
        Assert.Equal((byte)0x03, c.Dc2DylanStartWeaponId);
        Assert.Equal((byte)0x05, c.Dc2ReginaStartWeaponId);

        var randomCfg = new RandomizerConfig { Dc2RandomizeStartWeapon = true };
        Assert.True(SeedString.TryParse(SeedString.Encode(seed, randomCfg), out _, out c));
        Assert.True(c.Dc2RandomizeStartWeapon);
        Assert.Null(c.Dc2DylanStartWeaponId);
        Assert.Null(c.Dc2ReginaStartWeaponId);

        // Off ⇒ no byte 22: a default config keeps its historical 10-byte payload, and a
        // raptor-only config keeps 22 bytes — both parse with the feature off.
        Assert.Equal(10, PayloadLength(SeedString.Encode(seed, new RandomizerConfig())));
        var raptorOnly = SeedString.Encode(seed, new RandomizerConfig { Dc2RandomizeRaptorTiers = true });
        Assert.Equal(22, PayloadLength(raptorOnly));
        Assert.True(SeedString.TryParse(raptorOnly, out _, out c));
        Assert.False(c.Dc2RandomizeStartWeapon);
    }

    /// <summary>Pristine re-audit: replace-mode selectable = the full band minus the fire-empty 0x07
    /// (Regina). A shared seed naming 0x07 in replace mode is refused on parse (so it can't brick the
    /// weapon menu); the pristine-audit additions 04/05/09 (Dylan) and 06 (Regina) now round-trip.</summary>
    [Fact]
    public void StartWeaponByte_RejectsFireEmpty_InReplaceMode_AcceptsPristineOwnedMains()
    {
        var seed = new Seed(123);

        // 0x07 (Regina) in replace mode encodes to a real selection but parse refuses it (fire-empty).
        var bad = SeedString.Encode(seed, new RandomizerConfig
        {
            Dc2RandomizeStartWeapon = true, Dc2ReginaStartWeaponId = 0x07,
        });
        Assert.False(SeedString.TryParse(bad, out _, out _));

        // The pristine owned-main additions now round-trip in replace mode.
        foreach (var (d, r) in new (byte?, byte?)[] { ((byte?)0x04, null), ((byte?)0x05, null), ((byte?)0x09, null), (null, (byte?)0x06) })
        {
            var s = SeedString.Encode(seed, new RandomizerConfig
            {
                Dc2RandomizeStartWeapon = true, Dc2DylanStartWeaponId = d, Dc2ReginaStartWeaponId = r,
            });
            Assert.True(SeedString.TryParse(s, out _, out var cfg), $"replace-mode {d}/{r} should parse");
            Assert.Equal(d, cfg.Dc2DylanStartWeaponId);
            Assert.Equal(r, cfg.Dc2ReginaStartWeaponId);
        }
    }

    /// <summary>Add-and-equip (byte-22 bit 6) accepts the full band including the fire-empty 0x07 —
    /// the ring guard neutralizes it — and the mode flag survives the round-trip.</summary>
    [Fact]
    public void StartWeaponByte_AddAndEquip_AcceptsFullBand()
    {
        var seed = new Seed(123);
        var s = SeedString.Encode(seed, new RandomizerConfig
        {
            Dc2RandomizeStartWeapon = true, Dc2AddAndEquipStartWeapon = true, Dc2ReginaStartWeaponId = 0x07,
        });
        Assert.True(SeedString.TryParse(s, out _, out var cfg));
        Assert.True(cfg.Dc2AddAndEquipStartWeapon);
        Assert.Equal((byte?)0x07, cfg.Dc2ReginaStartWeaponId);
    }

    private static int PayloadLength(string s)
    {
        var b64 = s["DINO-".Length..].Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64.PadRight((b64.Length + 3) / 4 * 4, '=')).Length;
    }

    [Fact]
    public void TryParse_RejectsGarbage()
    {
        Assert.False(SeedString.TryParse("", out _, out _));
        Assert.False(SeedString.TryParse("BIO-abc", out _, out _));
        Assert.False(SeedString.TryParse("DINO-!!!", out _, out _));
    }
}
