using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Tests for <see cref="RandomizerConfig.NormalizeRatios"/> — the input-boundary clamp that keeps a
/// freshly-authored 0/0 ratio from reaching a user as their config (ITEM-RATIO-ZERO-PLAN.md). The
/// engine's own 0/0 intrinsic fallback is independent and stays (legacy 6-byte seeds decode to 0/0).
/// </summary>
public class RandomizerConfigTests
{
    [Fact]
    public void NormalizeRatios_BothZero_BecomesDefaultAndReportsChange()
    {
        var cfg = new RandomizerConfig { RatioAmmo = 0, RatioHealth = 0 };
        Assert.True(cfg.NormalizeRatios());
        Assert.Equal(16, cfg.RatioAmmo);
        Assert.Equal(16, cfg.RatioHealth);
    }

    [Fact]
    public void NormalizeRatios_SingleZero_IsLeftAlone()
    {
        var cfg = new RandomizerConfig { RatioAmmo = 16, RatioHealth = 0 };
        Assert.False(cfg.NormalizeRatios());
        Assert.Equal(16, cfg.RatioAmmo);
        Assert.Equal(0, cfg.RatioHealth);
    }

    [Fact]
    public void NormalizeRatios_NonZero_IsLeftAlone()
    {
        var cfg = new RandomizerConfig { RatioAmmo = 5, RatioHealth = 9 };
        Assert.False(cfg.NormalizeRatios());
        Assert.Equal(5, cfg.RatioAmmo);
        Assert.Equal(9, cfg.RatioHealth);
    }

    [Fact]
    public void EnabledWeaponFamilies_DefaultsToAll()
    {
        // Default = every family enabled, so the per-family filter (§7.4) is a no-op out of the box and
        // the item pass stays byte-identical until a family is explicitly cleared.
        Assert.Equal(WeaponFamily.All, new RandomizerConfig().EnabledWeaponFamilies);
    }
}
