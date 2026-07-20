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
    public void Dc2RandomizeWeapons_DefaultsOff()
    {
        Assert.False(new RandomizerConfig().Dc2RandomizeWeapons);
    }

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

    // --- Dc2RandomizePuzzles: the GUI's single "Randomize Puzzles" flag over the two puzzle
    // subflags (DC2-PUZZLE-RANDO-PLAN.md). Setter drives BOTH; getter ORs them so a seed/CLI
    // state with only one subflag on still reads as puzzles-randomized. ---

    [Fact]
    public void Dc2RandomizePuzzles_DefaultsOff_WithBothSubflagsOff()
    {
        var cfg = new RandomizerConfig();
        Assert.False(cfg.Dc2RandomizePuzzles);
        Assert.False(cfg.Dc2ScramblePuzzleCodes);
        Assert.False(cfg.Dc2ShuffleCircuits);
    }

    [Fact]
    public void Dc2RandomizePuzzles_On_EnablesBothSubflags()
    {
        var cfg = new RandomizerConfig { Dc2RandomizePuzzles = true };
        Assert.True(cfg.Dc2ScramblePuzzleCodes);
        Assert.True(cfg.Dc2ShuffleCircuits);
        Assert.True(cfg.Dc2RandomizePuzzles);
    }

    [Fact]
    public void Dc2RandomizePuzzles_Off_ClearsBothSubflags()
    {
        var cfg = new RandomizerConfig { Dc2ScramblePuzzleCodes = true, Dc2ShuffleCircuits = true };
        cfg.Dc2RandomizePuzzles = false;
        Assert.False(cfg.Dc2ScramblePuzzleCodes);
        Assert.False(cfg.Dc2ShuffleCircuits);
    }

    [Fact]
    public void Dc2RandomizePuzzles_ReadsTrue_WhenEitherSubflagIsOn()
    {
        // Boundary: mixed states stay representable (a CLI-applied single lever / a legacy seed
        // with one bit) and must present as "puzzles randomized" without mutating the other subflag.
        var codesOnly = new RandomizerConfig { Dc2ScramblePuzzleCodes = true };
        Assert.True(codesOnly.Dc2RandomizePuzzles);
        Assert.False(codesOnly.Dc2ShuffleCircuits);

        var circuitsOnly = new RandomizerConfig { Dc2ShuffleCircuits = true };
        Assert.True(circuitsOnly.Dc2RandomizePuzzles);
        Assert.False(circuitsOnly.Dc2ScramblePuzzleCodes);

        var plateOnly = new RandomizerConfig { Dc2RekeyPlateDoor = true };
        Assert.True(plateOnly.Dc2RandomizePuzzles);
        Assert.False(plateOnly.Dc2ScramblePuzzleCodes);
        Assert.False(plateOnly.Dc2ShuffleCircuits);
    }

    [Fact]
    public void Dc2RandomizePuzzles_DrivesTheKeyPlateSubflag()
    {
        var on = new RandomizerConfig { Dc2RandomizePuzzles = true };
        Assert.True(on.Dc2RekeyPlateDoor);   // K118: setter drives the plate-key re-key too

        var off = new RandomizerConfig { Dc2RekeyPlateDoor = true };
        off.Dc2RandomizePuzzles = false;
        Assert.False(off.Dc2RekeyPlateDoor);
    }
}
