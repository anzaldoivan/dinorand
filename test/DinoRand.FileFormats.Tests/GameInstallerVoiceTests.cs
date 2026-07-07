using DinoRand.Randomizer.Install;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The loose-file install path for voice banks (docs/dc1/VOICE-RANDO-PLAN.md §12.3), integrated into
/// <see cref="GameInstaller"/>: voice banks live in <c>&lt;root&gt;\Sound\VOICE\</c> (a sibling of
/// <c>Data\</c>, in subdirectories), so they need root-relative backup + overlay + restore, with the
/// pristine hash merged into the same manifest the room overlay uses. Synthetic temp dirs — no game data.
/// </summary>
public sealed class GameInstallerVoiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dinorand_voice_" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDir;
    private readonly string _modDir;

    public GameInstallerVoiceTests()
    {
        _dataDir = Path.Combine(_root, "Data");
        _modDir = Path.Combine(_root, "mod_dinorand");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_modDir);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const string BankRel = "Sound/VOICE/xa10501.dat";
    private string GameBank => Path.Combine(_root, "Sound", "VOICE", "xa10501.dat");
    private string ModBank => Path.Combine(_modDir, "Sound", "VOICE", "xa10501.dat");
    private string LooseBackup => Path.Combine(_dataDir, GameInstaller.BackupDirName,
        GameInstaller.LooseBackupSubdir, "Sound", "VOICE", "xa10501.dat");

    private void WriteGameBank(byte[] b) { Directory.CreateDirectory(Path.GetDirectoryName(GameBank)!); File.WriteAllBytes(GameBank, b); }
    private void WriteModBank(byte[] b) { Directory.CreateDirectory(Path.GetDirectoryName(ModBank)!); File.WriteAllBytes(ModBank, b); }

    [Fact]
    public void Install_overlays_voice_bank_and_backs_up_pristine_in_loose_subtree()
    {
        var original = new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3 };
        WriteGameBank(original);
        WriteModBank(new byte[] { 9, 9, 9 });

        var result = GameInstaller.Install(_dataDir, _modDir, "seed-v");

        Assert.Equal(1, result.Overlaid);
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(GameBank));        // overlaid into game root
        Assert.Equal(original, File.ReadAllBytes(LooseBackup));                   // pristine kept in loose subtree

        // The pristine hash is recorded under the forward-slash relative key in the same manifest.
        var manifest = GameInstaller.ReadManifest(_dataDir)!;
        Assert.Contains(BankRel, manifest.Files);
        Assert.True(manifest.OriginalHashes!.ContainsKey(BankRel));
    }

    [Fact]
    public void Restore_reverts_voice_bank_to_pristine()
    {
        var original = new byte[] { 5, 5, 5, 5 };
        WriteGameBank(original);
        WriteModBank(new byte[] { 7, 7, 7 });

        GameInstaller.Install(_dataDir, _modDir, "seed-v");
        var restore = GameInstaller.Restore(_dataDir);

        Assert.True(restore.Restored >= 1);
        Assert.Equal(original, File.ReadAllBytes(GameBank));                      // back to pristine
        Assert.True(File.Exists(LooseBackup));                                    // backup kept for reuse
    }

    [Fact]
    public void Reroll_backs_up_voice_bank_only_once()
    {
        var original = new byte[] { 1, 1, 1 };
        WriteGameBank(original);

        WriteModBank(new byte[] { 2, 2, 2 });
        GameInstaller.Install(_dataDir, _modDir, "seed-1");
        WriteModBank(new byte[] { 3, 3, 3 });
        GameInstaller.Install(_dataDir, _modDir, "seed-2");

        Assert.Equal(original, File.ReadAllBytes(LooseBackup));                   // still the true original
        Assert.Equal(new byte[] { 3, 3, 3 }, File.ReadAllBytes(GameBank));
    }

    [Fact]
    public void Restore_refuses_a_tampered_voice_backup()
    {
        WriteGameBank(new byte[] { 4, 4, 4 });
        WriteModBank(new byte[] { 8, 8, 8 });
        GameInstaller.Install(_dataDir, _modDir, "seed-v");

        File.WriteAllBytes(LooseBackup, new byte[] { 0, 0, 0 }); // corrupt the pristine backup

        Assert.Throws<BackupIntegrityException>(() => GameInstaller.Restore(_dataDir));
        Assert.Equal(new byte[] { 8, 8, 8 }, File.ReadAllBytes(GameBank)); // refused ⇒ game left as-is
    }

    [Fact]
    public void Install_skips_a_loose_file_with_no_original_to_back_up()
    {
        // Mod ships a bank the install doesn't have — never create a stray file out of nowhere.
        WriteModBank(new byte[] { 9, 9, 9 });

        var result = GameInstaller.Install(_dataDir, _modDir, "seed-v");

        Assert.False(File.Exists(GameBank));
        Assert.False(File.Exists(LooseBackup));
        Assert.DoesNotContain(BankRel, GameInstaller.ReadManifest(_dataDir)!.Files);
    }
}
