using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The raptor-tier install step, file-level (<c>ApplyToFile</c>) on the synthetic two-site exe
/// (<see cref="Dc2RaptorTierTests.FakeExe"/>). Pins: the Skipped fast-path (checked before
/// recognition), NotFound, UnrecognizedVersion on both apply and restore, the two plan modes,
/// and backup-once.
/// </summary>
public class Dc2RaptorTierInstallerTests
{
    private static string WriteExe(string dir, byte[] bytes)
    {
        var path = Path.Combine(dir, "Dino2.exe");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static RandomizerConfig VanillaConfig() => new()
    {
        Dc2RandomizeRaptorTiers = false,
        Dc2BlueRaptorComboThreshold = Dc2RaptorPatch.VanillaComboThreshold,
    };

    [Fact]
    public void MissingExe_ReturnsNotFound()
    {
        var dir = Directory.CreateTempSubdirectory("raptorinst").FullName;
        try
        {
            Assert.Equal(Dc2RaptorTierPatchOutcome.NotFound,
                Dc2RaptorTierInstaller.ApplyToFile(Path.Combine(dir, "Dino2.exe"), new Seed(1), VanillaConfig()));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void NothingToPatch_SkipsBeforeRecognition()
    {
        // Tiers off + vanilla combo threshold ⇒ Skipped — even on garbage bytes, because the
        // decision precedes the build check (nothing would be written anyway).
        var dir = Directory.CreateTempSubdirectory("raptorinst").FullName;
        try
        {
            var path = WriteExe(dir, new byte[100]);
            Assert.Equal(Dc2RaptorTierPatchOutcome.Skipped,
                Dc2RaptorTierInstaller.ApplyToFile(path, new Seed(1), VanillaConfig()));
            Assert.False(File.Exists(path + ".bak"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void UnrecognizedBuild_RefusedOnApplyAndRestore()
    {
        var dir = Directory.CreateTempSubdirectory("raptorinst").FullName;
        try
        {
            var garbage = new byte[Dc2WpGatePatch.ExpectedLength]; // right length, wrong sites
            var path = WriteExe(dir, garbage);

            var config = VanillaConfig();
            config.Dc2BlueRaptorComboThreshold = 10; // something to patch ⇒ reaches recognition
            Assert.Equal(Dc2RaptorTierPatchOutcome.UnrecognizedVersion,
                Dc2RaptorTierInstaller.ApplyToFile(path, new Seed(1), config));
            Assert.Equal(garbage, File.ReadAllBytes(path));

            Assert.Equal(Dc2RaptorTierPatchOutcome.UnrecognizedVersion,
                Dc2RaptorTierInstaller.ApplyToFile(path, new Seed(1), config, restore: true));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ComboOnly_Apply_Restore_RoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory("raptorinst").FullName;
        try
        {
            var pristine = Dc2RaptorTierTests.FakeExe();
            var path = WriteExe(dir, pristine);
            var config = VanillaConfig();
            config.Dc2BlueRaptorComboThreshold = 5;

            Assert.Equal(Dc2RaptorTierPatchOutcome.Applied,
                Dc2RaptorTierInstaller.ApplyToFile(path, new Seed(7), config));
            var patched = File.ReadAllBytes(path);
            Assert.Equal(0x04, patched[Dc2RaptorPatch.ComboImmOffset]);      // arm when combo > 4
            Assert.Equal(pristine, File.ReadAllBytes(path + ".bak"));

            // pair table untouched in combo-only mode
            Assert.True(Dc2RaptorPatch.IsPairTablePristine(patched));

            Assert.Equal(Dc2RaptorTierPatchOutcome.Restored,
                Dc2RaptorTierInstaller.ApplyToFile(path, new Seed(7), config, restore: true));
            Assert.Equal(pristine, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void MixedTiers_Apply_IsSeedDeterministic_AndBackupOnce()
    {
        var dir = Directory.CreateTempSubdirectory("raptorinst").FullName;
        try
        {
            var pristine = Dc2RaptorTierTests.FakeExe();
            var path = WriteExe(dir, pristine);
            var config = new RandomizerConfig
            {
                Dc2RandomizeRaptorTiers = true,
                Dc2RaptorColourMode = Dc2RaptorColourMode.MixedTiers,
            };

            Assert.Equal(Dc2RaptorTierPatchOutcome.Applied,
                Dc2RaptorTierInstaller.ApplyToFile(path, new Seed(42), config));
            var run1 = File.ReadAllBytes(path);
            Assert.True(Dc2RaptorPatch.IsPairTableRecognized(run1)); // structure preserved

            // same seed, fresh pristine exe ⇒ identical bytes
            var path2 = WriteExe(Directory.CreateDirectory(Path.Combine(dir, "b")).FullName, pristine);
            Dc2RaptorTierInstaller.ApplyToFile(path2, new Seed(42), config);
            Assert.Equal(run1, File.ReadAllBytes(path2));

            // re-run with a different seed: .bak still the pristine original
            Dc2RaptorTierInstaller.ApplyToFile(path, new Seed(43), config);
            Assert.Equal(pristine, File.ReadAllBytes(path + ".bak"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
