using DinoRand.Randomizer.Install;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The installer is the only thing that touches the real game folder, so these tests pin
/// its non-destructive contract: originals are backed up before being overlaid, re-rolling
/// never destroys the pristine backup, and restore reproduces the originals byte-for-byte.
/// Uses synthetic st*.dat files in temp dirs — no game data required.
/// </summary>
public sealed class GameInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dinorand_test_" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDir;
    private readonly string _modDir;

    public GameInstallerTests()
    {
        _dataDir = Path.Combine(_root, "Data");
        _modDir = Path.Combine(_root, "mod_dinorand");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_modDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteData(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(_dataDir, name), bytes);
    private void WriteMod(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(_modDir, name), bytes);
    private byte[] ReadData(string name) => File.ReadAllBytes(Path.Combine(_dataDir, name));
    private byte[] ReadBackup(string name) => File.ReadAllBytes(Path.Combine(_dataDir, GameInstaller.BackupDirName, name));
    private static string Sha(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    [Fact]
    public void Install_overlays_room_files_and_backs_up_originals()
    {
        WriteData("st100.dat", new byte[] { 1, 1, 1 });
        WriteData("st101.dat", new byte[] { 2, 2, 2 });
        WriteMod("st100.dat", new byte[] { 9, 9, 9 });
        WriteMod("st101.dat", new byte[] { 8, 8, 8 });

        var result = GameInstaller.Install(_dataDir, _modDir, "seed-abc");

        Assert.Equal(2, result.Overlaid);
        Assert.Equal(2, result.BackedUp);
        Assert.Equal(new byte[] { 9, 9, 9 }, ReadData("st100.dat"));
        Assert.True(GameInstaller.IsInstalled(_dataDir));

        var backupDir = Path.Combine(_dataDir, GameInstaller.BackupDirName);
        Assert.Equal(new byte[] { 1, 1, 1 }, File.ReadAllBytes(Path.Combine(backupDir, "st100.dat")));
        Assert.Equal("seed-abc", GameInstaller.ReadManifest(_dataDir)!.Seed);
    }

    [Fact]
    public void Install_only_touches_files_with_a_matching_original()
    {
        WriteData("st100.dat", new byte[] { 1 });
        WriteMod("st100.dat", new byte[] { 9 });
        WriteMod("st999.dat", new byte[] { 7 });           // no original — must be ignored
        File.WriteAllText(Path.Combine(_modDir, "log_dinorand.txt"), "log"); // not a .dat

        var result = GameInstaller.Install(_dataDir, _modDir);

        Assert.Equal(1, result.Overlaid);
        Assert.False(File.Exists(Path.Combine(_dataDir, "st999.dat")));
        Assert.False(File.Exists(Path.Combine(_dataDir, "log_dinorand.txt")));
    }

    [Fact]
    public void Reinstalling_a_new_seed_keeps_the_pristine_backup()
    {
        WriteData("st100.dat", new byte[] { 1, 1, 1 });    // pristine

        WriteMod("st100.dat", new byte[] { 5, 5, 5 });
        GameInstaller.Install(_dataDir, _modDir, "seed-1");

        WriteMod("st100.dat", new byte[] { 6, 6, 6 });     // re-roll a different seed
        GameInstaller.Install(_dataDir, _modDir, "seed-2");

        Assert.Equal(new byte[] { 6, 6, 6 }, ReadData("st100.dat"));
        // The backup must still hold the ORIGINAL (1,1,1), not the first randomized roll.
        var backup = Path.Combine(_dataDir, GameInstaller.BackupDirName, "st100.dat");
        Assert.Equal(new byte[] { 1, 1, 1 }, File.ReadAllBytes(backup));
        Assert.Equal("seed-2", GameInstaller.ReadManifest(_dataDir)!.Seed);
    }

    [Fact]
    public void Restore_makes_data_byte_identical_to_originals_and_keeps_the_backup()
    {
        var orig100 = new byte[] { 1, 2, 3, 4 };
        var orig101 = new byte[] { 5, 6, 7, 8 };
        WriteData("st100.dat", orig100);
        WriteData("st101.dat", orig101);
        WriteMod("st100.dat", new byte[] { 9, 9, 9, 9 });
        WriteMod("st101.dat", new byte[] { 8, 8, 8, 8 });

        GameInstaller.Install(_dataDir, _modDir, "seed-x");
        var restore = GameInstaller.Restore(_dataDir);

        Assert.Equal(2, restore.Restored);
        Assert.Equal(orig100, ReadData("st100.dat"));
        Assert.Equal(orig101, ReadData("st101.dat"));
        // The mod is no longer applied, but the pristine backup is RETAINED for reuse (never deleted).
        Assert.False(GameInstaller.IsInstalled(_dataDir));
        Assert.True(GameInstaller.HasBackup(_dataDir));
        Assert.True(Directory.Exists(Path.Combine(_dataDir, GameInstaller.BackupDirName)));
        Assert.Equal(orig100, ReadBackup("st100.dat"));
    }

    [Fact]
    public void Install_records_pristine_original_hashes_in_the_manifest()
    {
        var orig = new byte[] { 1, 2, 3 };
        WriteData("st100.dat", orig);
        WriteMod("st100.dat", new byte[] { 9, 9, 9 });

        GameInstaller.Install(_dataDir, _modDir, "seed-abc");

        var manifest = GameInstaller.ReadManifest(_dataDir)!;
        Assert.NotNull(manifest.OriginalHashes);
        Assert.Equal(Sha(orig), manifest.OriginalHashes!["st100.dat"]);
    }

    [Fact]
    public void Applied_state_is_true_after_install_and_false_after_restore_while_backup_persists()
    {
        WriteData("st100.dat", new byte[] { 1 });
        WriteMod("st100.dat", new byte[] { 9 });

        GameInstaller.Install(_dataDir, _modDir, "seed-x");
        Assert.True(GameInstaller.IsInstalled(_dataDir));   // applied
        Assert.True(GameInstaller.HasBackup(_dataDir));

        GameInstaller.Restore(_dataDir);
        Assert.False(GameInstaller.IsInstalled(_dataDir));  // no longer applied
        Assert.True(GameInstaller.HasBackup(_dataDir));     // backup retained for reuse
    }

    [Fact]
    public void Reinstall_after_restore_reuses_the_pristine_backup()
    {
        var orig = new byte[] { 1, 1, 1 };
        WriteData("st100.dat", orig);

        WriteMod("st100.dat", new byte[] { 5, 5, 5 });
        GameInstaller.Install(_dataDir, _modDir, "seed-1");
        GameInstaller.Restore(_dataDir);                    // live back to orig; backup kept

        WriteMod("st100.dat", new byte[] { 7, 7, 7 });
        GameInstaller.Install(_dataDir, _modDir, "seed-2"); // must reuse the kept backup
        Assert.Equal(new byte[] { 7, 7, 7 }, ReadData("st100.dat"));
        Assert.Equal(orig, ReadBackup("st100.dat"));        // still pristine, not re-captured
    }

    [Fact]
    public void Install_refuses_to_overlay_when_the_existing_backup_is_not_pristine()
    {
        var orig = new byte[] { 1, 2, 3 };
        WriteData("st100.dat", orig);
        WriteMod("st100.dat", new byte[] { 9, 9, 9 });
        GameInstaller.Install(_dataDir, _modDir, "seed-1");  // captures pristine + records hash

        // Corrupt the backup (simulate tampering or a backup that isn't actually pristine).
        File.WriteAllBytes(Path.Combine(_dataDir, GameInstaller.BackupDirName, "st100.dat"), new byte[] { 6, 6, 6 });

        WriteMod("st100.dat", new byte[] { 8, 8, 8 });
        Assert.Throws<BackupIntegrityException>(() => GameInstaller.Install(_dataDir, _modDir, "seed-2"));
    }

    [Fact]
    public void Restore_refuses_a_corrupted_backup_rather_than_writing_it_over_the_game()
    {
        var orig = new byte[] { 1, 2, 3 };
        WriteData("st100.dat", orig);
        WriteMod("st100.dat", new byte[] { 9, 9, 9 });
        GameInstaller.Install(_dataDir, _modDir, "seed-1");

        File.WriteAllBytes(Path.Combine(_dataDir, GameInstaller.BackupDirName, "st100.dat"), new byte[] { 6, 6, 6 });

        Assert.Throws<BackupIntegrityException>(() => GameInstaller.Restore(_dataDir));
        // Live data must NOT have been overwritten with the corrupt backup.
        Assert.Equal(new byte[] { 9, 9, 9 }, ReadData("st100.dat"));
    }

    [Fact]
    public void FindGameExe_resolves_from_a_direct_exe_path_or_a_containing_folder()
    {
        var exePath = Path.Combine(_root, GameInstaller.ExeName);
        File.WriteAllBytes(exePath, new byte[] { 0x4d, 0x5a });

        Assert.Equal(exePath, GameInstaller.FindGameExe(exePath));   // direct exe path
        Assert.Equal(exePath, GameInstaller.FindGameExe(_root));     // containing folder
        Assert.Null(GameInstaller.FindGameExe(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void FindGameExe_resolves_the_requested_exe_name()
    {
        // DC2's Dino2.exe is found when passed explicitly, and never mistaken for the DC1 default.
        var dc2Exe = Path.Combine(_root, "Dino2.exe");
        File.WriteAllBytes(dc2Exe, new byte[] { 0x4d, 0x5a });

        Assert.Equal(dc2Exe, GameInstaller.FindGameExe(dc2Exe, "Dino2.exe"));
        Assert.Equal(dc2Exe, GameInstaller.FindGameExe(_root, "Dino2.exe"));
        Assert.Null(GameInstaller.FindGameExe(_root)); // default DINO.exe absent
    }

    [Fact]
    public void BackupOnce_captures_pristine_once_and_Restore_reverses_an_in_place_edit()
    {
        // The shared single-file backup primitive (used by the DC1 swaps + the DC2 door edit): a
        // pristine backup is captured once, an in-place edit is reversed by Restore.
        var orig = new byte[] { 1, 2, 3, 4 };
        WriteData("st101.dat", orig);
        var targetPath = Path.Combine(_dataDir, "st101.dat");

        var backupPath = GameInstaller.BackupOnce(_dataDir, targetPath);
        Assert.Equal(orig, File.ReadAllBytes(backupPath));

        // Edit in place, then re-back-up: the pristine copy must NOT be overwritten by the edited file.
        File.WriteAllBytes(targetPath, new byte[] { 9, 9, 9, 9 });
        var backupPath2 = GameInstaller.BackupOnce(_dataDir, targetPath);
        Assert.Equal(backupPath, backupPath2);
        Assert.Equal(orig, File.ReadAllBytes(backupPath)); // still pristine (non-compounding)

        var restore = GameInstaller.Restore(_dataDir);
        Assert.Equal(1, restore.Restored);
        Assert.Equal(orig, ReadData("st101.dat"));
        Assert.False(GameInstaller.IsInstalled(_dataDir));
    }

    [Fact]
    public void Restore_without_an_install_is_a_no_op()
    {
        WriteData("st100.dat", new byte[] { 1 });

        var restore = GameInstaller.Restore(_dataDir);

        Assert.Equal(0, restore.Restored);
        Assert.Equal(new byte[] { 1 }, ReadData("st100.dat"));
    }

    [Fact]
    public void Mixed_case_mod_name_overlays_the_lowercase_original()
    {
        WriteData("st502.dat", new byte[] { 1, 1 });       // original is lowercase
        WriteMod("St502.dat", new byte[] { 9, 9 });        // randomizer emits mixed-case

        var result = GameInstaller.Install(_dataDir, _modDir);

        Assert.Equal(1, result.Overlaid);
        Assert.Equal(new byte[] { 9, 9 }, ReadData("st502.dat"));
        // No stray second-cased file should appear on a case-sensitive filesystem.
        Assert.Single(Directory.EnumerateFiles(_dataDir, "*.dat"));
    }
}
