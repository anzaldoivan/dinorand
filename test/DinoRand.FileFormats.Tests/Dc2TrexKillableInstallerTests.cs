using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The killable-T-Rex install step, file-level (<c>ApplyToFile</c>) on a synthetic pristine exe
/// (<see cref="Dc2TrexKillablePatchTests.NewPristine"/>). What is pinned: the full outcome lattice
/// (NotFound / UnrecognizedVersion / Applied / Skipped / Restored) and the backup-once contract —
/// a re-run must never overwrite the pristine <c>.bak</c> with patched bytes.
/// </summary>
public class Dc2TrexKillableInstallerTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("trexinst").FullName;

    private static string WriteExe(string dir, byte[] bytes)
    {
        var path = Path.Combine(dir, "Dino2.exe");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ---- WantedFor: the pure config gate (no filesystem) -----------------------------------------

    [Fact]
    public void WantedFor_ManualOverride_AlwaysTrue()
    {
        var config = new RandomizerConfig { Dc2MakeTrexKillable = true, RandomizeEnemies = false };
        Assert.True(Dc2TrexKillableInstaller.WantedFor(config));
    }

    [Fact]
    public void WantedFor_NoEnemyRandomization_False()
    {
        var config = new RandomizerConfig { RandomizeEnemies = false, IncludeDc2BossEnemies = true };
        Assert.False(Dc2TrexKillableInstaller.WantedFor(config));
    }

    [Fact]
    public void WantedFor_FixedMode_TrueOnlyForTrexPin()
    {
        var trexPin = new RandomizerConfig
        {
            RandomizeEnemies = true,
            Dc2EnemyMode = Dc2EnemyDistributionMode.Fixed,
            Dc2FixedSpeciesType = 0x03, // T-Rex
        };
        Assert.True(Dc2TrexKillableInstaller.WantedFor(trexPin));

        var raptorPin = new RandomizerConfig
        {
            RandomizeEnemies = true,
            Dc2EnemyMode = Dc2EnemyDistributionMode.Fixed,
            Dc2FixedSpeciesType = 0x02,
        };
        Assert.False(Dc2TrexKillableInstaller.WantedFor(raptorPin));
    }

    [Fact]
    public void WantedFor_WeightedMode_FollowsBossPool()
    {
        var withBosses = new RandomizerConfig { RandomizeEnemies = true, IncludeDc2BossEnemies = true };
        Assert.True(Dc2TrexKillableInstaller.WantedFor(withBosses));
        var noBosses = new RandomizerConfig { RandomizeEnemies = true, IncludeDc2BossEnemies = false };
        Assert.False(Dc2TrexKillableInstaller.WantedFor(noBosses));
    }

    [Fact]
    public void WantedFor_NullConfig_Throws()
        => Assert.Throws<ArgumentNullException>(() => Dc2TrexKillableInstaller.WantedFor(null!));

    // ---- ApplyToFile lifecycle --------------------------------------------------------------------

    [Fact]
    public void MissingExe_ReturnsNotFound()
    {
        var dir = TempDir();
        try
        {
            Assert.Equal(Dc2TrexKillableOutcome.NotFound,
                Dc2TrexKillableInstaller.ApplyToFile(Path.Combine(dir, "Dino2.exe")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void UnrecognizedBuild_LeavesFileUntouched()
    {
        var dir = TempDir();
        try
        {
            var garbage = new byte[123];
            var path = WriteExe(dir, garbage);
            Assert.Equal(Dc2TrexKillableOutcome.UnrecognizedVersion,
                Dc2TrexKillableInstaller.ApplyToFile(path));
            Assert.Equal(garbage, File.ReadAllBytes(path));      // never written
            Assert.False(File.Exists(path + ".bak"));            // no backup of a refused file
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Apply_Skip_Restore_FullLifecycle_KeepsPristineBackup()
    {
        var dir = TempDir();
        try
        {
            var pristine = Dc2TrexKillablePatchTests.NewPristine();
            var path = WriteExe(dir, pristine);

            // 1. apply: patched + pristine .bak captured
            Assert.Equal(Dc2TrexKillableOutcome.Applied, Dc2TrexKillableInstaller.ApplyToFile(path));
            Assert.True(Dc2TrexKillablePatch.IsApplied(File.ReadAllBytes(path)));
            Assert.Equal(pristine, File.ReadAllBytes(path + ".bak"));

            // 2. re-apply: idempotent skip, .bak still pristine (the corruption-with-no-way-back bug)
            Assert.Equal(Dc2TrexKillableOutcome.Skipped, Dc2TrexKillableInstaller.ApplyToFile(path));
            Assert.Equal(pristine, File.ReadAllBytes(path + ".bak"));

            // 3. restore: byte-identical to pristine
            Assert.Equal(Dc2TrexKillableOutcome.Restored,
                Dc2TrexKillableInstaller.ApplyToFile(path, restore: true));
            Assert.Equal(pristine, File.ReadAllBytes(path));

            // 4. restore again: nothing applied → skip
            Assert.Equal(Dc2TrexKillableOutcome.Skipped,
                Dc2TrexKillableInstaller.ApplyToFile(path, restore: true));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
