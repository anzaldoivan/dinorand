using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Install-time WP-gate patch for the character-skin swap: back up Dino2.exe once, apply
/// <see cref="Dc2WpGatePatch"/>, stay idempotent, refuse unknown builds, restore by copying the
/// backup back (mirrors <c>Dc2MotionTrailInstallerTests</c>).
/// </summary>
public class Dc2CharacterSkinInstallerTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dinorand-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] SyntheticPristineExe()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        exe[Dc2WpGatePatch.GateOffset - 2] = 0x85;
        exe[Dc2WpGatePatch.GateOffset - 1] = 0xC0;
        exe[Dc2WpGatePatch.GateOffset]     = 0x74;
        exe[Dc2WpGatePatch.GateOffset + 1] = 0x62;
        return exe;
    }

    [Fact]
    public void Apply_backs_up_once_patches_and_is_idempotent_then_restore_reverses()
    {
        var dir = TempDir();
        try
        {
            var exePath = Path.Combine(dir, "Dino2.exe");
            var pristine = SyntheticPristineExe();
            File.WriteAllBytes(exePath, pristine);

            Assert.Equal(Dc2SkinGateOutcome.Applied, Dc2CharacterSkinInstaller.ApplyToFile(exePath));
            var backupPath = exePath + Dc2CharacterSkinInstaller.BackupSuffix;
            Assert.True(File.Exists(backupPath));
            Assert.Equal(pristine, File.ReadAllBytes(backupPath));
            Assert.True(Dc2WpGatePatch.IsApplied(File.ReadAllBytes(exePath)));

            // Idempotent: second apply is a no-op and never re-captures the backup.
            var backupBytes = File.ReadAllBytes(backupPath);
            Assert.Equal(Dc2SkinGateOutcome.AlreadyApplied, Dc2CharacterSkinInstaller.ApplyToFile(exePath));
            Assert.Equal(backupBytes, File.ReadAllBytes(backupPath));

            // Restore reverses to byte-identical pristine.
            Assert.True(Dc2CharacterSkinInstaller.Restore(exePath));
            Assert.Equal(pristine, File.ReadAllBytes(exePath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Apply_refuses_unrecognized_build_and_missing_file()
    {
        var dir = TempDir();
        try
        {
            var exePath = Path.Combine(dir, "Dino2.exe");
            Assert.Equal(Dc2SkinGateOutcome.NotFound, Dc2CharacterSkinInstaller.ApplyToFile(exePath));
            Assert.False(Dc2CharacterSkinInstaller.Restore(exePath)); // nothing to restore

            var foreign = new byte[123456];
            File.WriteAllBytes(exePath, foreign);
            Assert.Equal(Dc2SkinGateOutcome.UnrecognizedVersion, Dc2CharacterSkinInstaller.ApplyToFile(exePath));
            Assert.Equal(foreign, File.ReadAllBytes(exePath));                    // untouched
            Assert.False(File.Exists(exePath + Dc2CharacterSkinInstaller.BackupSuffix)); // no backup captured
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
