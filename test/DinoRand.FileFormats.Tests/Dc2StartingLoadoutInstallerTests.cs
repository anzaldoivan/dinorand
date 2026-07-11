using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The starting-loadout install step, file-level (<c>ApplyToFile</c>) on a synthetic exe composed
/// from the loadout-patch fixture (<see cref="Dc2StartingLoadoutPatchTests.MakeExe"/>) plus the
/// append-patch hook site. Pins: outcome branches, explicit-vs-random id selection, seed
/// determinism, the shared-main warning, and backup-once + restore round-trip.
/// </summary>
public class Dc2StartingLoadoutInstallerTests
{
    /// <summary>Loadout fixture + the inventory-init hook the append patch needs (<c>mov esi,3</c>),
    /// pre-run through the catalog-flag repair — the installer repairs flags before both apply AND
    /// restore, so a fixture already at PSX source-of-truth round-trips byte-exactly.</summary>
    private static byte[] NewPristine()
    {
        var exe = Dc2StartingLoadoutPatchTests.MakeExe();
        new byte[] { 0xBE, 0x03, 0x00, 0x00, 0x00 }.CopyTo(exe, Dc2StartWeaponAppendPatch.HookOffset);
        Dc2WeaponCatalogFlagsPatch.Apply(exe);
        return exe;
    }

    private static string WriteExe(string dir, byte[] bytes)
    {
        var path = Path.Combine(dir, "Dino2.exe");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void MissingExe_ReturnsNotFound()
    {
        var dir = Directory.CreateTempSubdirectory("loadoutinst").FullName;
        try
        {
            Assert.Equal(Dc2BgmShuffleOutcome.NotFound,
                Dc2StartingLoadoutInstaller.ApplyToFile(Path.Combine(dir, "Dino2.exe"), 1, null, null));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void UnrecognizedBuild_LeavesFileUntouched()
    {
        var dir = Directory.CreateTempSubdirectory("loadoutinst").FullName;
        try
        {
            var wrongLen = WriteExe(dir, new byte[100]);
            Assert.Equal(Dc2BgmShuffleOutcome.UnrecognizedVersion,
                Dc2StartingLoadoutInstaller.ApplyToFile(wrongLen, 1, null, null));

            var zeroed = new byte[Dc2WpGatePatch.ExpectedLength]; // right length, no fingerprint
            var path = WriteExe(dir, zeroed);
            Assert.Equal(Dc2BgmShuffleOutcome.UnrecognizedVersion,
                Dc2StartingLoadoutInstaller.ApplyToFile(path, 1, null, null));
            Assert.Equal(zeroed, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".bak"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ExplicitIds_Apply_Restore_RoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory("loadoutinst").FullName;
        try
        {
            var pristine = NewPristine();
            var path = WriteExe(dir, pristine);

            // Dylan → grenade gun 0x03 (fire-witnessed), Regina stays canonical 0x02.
            Assert.Equal(Dc2BgmShuffleOutcome.Applied,
                Dc2StartingLoadoutInstaller.ApplyToFile(path, seed: 1, dylanId: 0x03, reginaId: 0x02));
            var patched = File.ReadAllBytes(path);
            Assert.NotEqual(pristine, patched);
            Assert.Equal(pristine, File.ReadAllBytes(path + ".bak"));

            // the patched exe still validates (lever-state accepted, not a foreign edit)
            Dc2StartingLoadoutPatch.Validate(patched);

            // re-apply with different ids: .bak still pristine (backup-once)
            Dc2StartingLoadoutInstaller.ApplyToFile(path, seed: 2, dylanId: 0x01, reginaId: 0x02);
            Assert.Equal(pristine, File.ReadAllBytes(path + ".bak"));

            Assert.Equal(Dc2BgmShuffleOutcome.Restored,
                Dc2StartingLoadoutInstaller.ApplyToFile(path, seed: 0, null, null, restore: true));
            Assert.Equal(pristine, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RandomIds_AreSeedDeterministic()
    {
        var dir = Directory.CreateTempSubdirectory("loadoutinst").FullName;
        try
        {
            var pristine = NewPristine();
            var a = WriteExe(Directory.CreateDirectory(Path.Combine(dir, "a")).FullName, pristine);
            var b = WriteExe(Directory.CreateDirectory(Path.Combine(dir, "b")).FullName, pristine);

            Assert.Equal(Dc2BgmShuffleOutcome.Applied,
                Dc2StartingLoadoutInstaller.ApplyToFile(a, seed: 777, null, null));
            Assert.Equal(Dc2BgmShuffleOutcome.Applied,
                Dc2StartingLoadoutInstaller.ApplyToFile(b, seed: 777, null, null));
            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ExplicitSharedMain_LogsTheSharedWarning()
    {
        var dir = Directory.CreateTempSubdirectory("loadoutinst").FullName;
        try
        {
            var path = WriteExe(dir, NewPristine());
            var log = new List<string>();
            // id 0x05 is owned by BOTH characters — explicit pick allowed, but must warn.
            Dc2StartingLoadoutInstaller.ApplyToFile(path, seed: 1, dylanId: 0x05, reginaId: 0x02,
                log: log.Add);
            Assert.Contains(log, l => l.Contains("warn") && l.Contains("0x05"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
