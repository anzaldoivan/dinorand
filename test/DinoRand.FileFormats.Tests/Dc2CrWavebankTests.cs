using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Dc2.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Classic REbirth serves each package's SFX from its own HD wavebank at
/// <c>CR\data\&lt;package&gt;\snd.wbk</c>, bypassing the engine-side CORE SOUND bank entirely
/// (live-witnessed 2026-07-04: rebirth sound-bank tables 0x878BA0 all-zero while SFX played —
/// docs/dc2/DC2-CHARACTER-VOICE-SFX-PLAN.md §6). So on a CR install the voice swap must ALSO swap
/// the per-character CR wavebanks. Synthetic-tree tests — no game files needed, always run.
/// </summary>
public class Dc2CrWavebankTests
{
    private static string MakeInstall(params (string RelPath, byte[] Bytes)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "dinorand-test-" + Guid.NewGuid().ToString("N"));
        // Minimal recognizable DC2 layout: Data\ST100.DAT beside CR\.
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        File.WriteAllBytes(Path.Combine(root, "Data", "ST100.DAT"), new byte[4]);
        foreach (var (rel, bytes) in files)
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, bytes);
        }
        return root;
    }

    private static byte[] B(string tag) => System.Text.Encoding.ASCII.GetBytes(tag);

    // Build the wavebank paths with Path.Combine so the segments are OS-correct separators — a
    // literal @"CR\data\..." is a single filename on Linux, not a nested tree (would break the suite).
    private static string Wbk(string core) =>
        Path.Combine("CR", "data", core, Dc2CharacterSkinInstaller.WavebankName);
    private static string Bak(string core) =>
        Wbk(core) + Dc2CharacterSkinInstaller.WavebankBackupSuffix;

    [Fact]
    public void Plan_maps_skins_to_core_wavebank_dirs()
    {
        Assert.Empty(Dc2CharacterSkinInstaller.PlanCrWavebankSwaps(Dc2CharacterSkin.Stock, Dc2CharacterSkin.Stock));
        Assert.Equal(new[] { ("CORE01", "CORE06") },
            Dc2CharacterSkinInstaller.PlanCrWavebankSwaps(Dc2CharacterSkin.Gail, Dc2CharacterSkin.Stock));
        Assert.Equal(new[] { ("CORE00", "CORE05") },
            Dc2CharacterSkinInstaller.PlanCrWavebankSwaps(Dc2CharacterSkin.Stock, Dc2CharacterSkin.Rick));
        Assert.Equal(new[] { ("CORE01", "CORE05"), ("CORE00", "CORE06") },
            Dc2CharacterSkinInstaller.PlanCrWavebankSwaps(Dc2CharacterSkin.Rick, Dc2CharacterSkin.Gail));
        // Random must be resolved (ResolveSkin) before planning — an unresolved value is a caller bug.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Dc2CharacterSkinInstaller.PlanCrWavebankSwaps(Dc2CharacterSkin.Random, Dc2CharacterSkin.Stock));
    }

    [Fact]
    public void Apply_swaps_wavebanks_with_one_time_backup_and_stays_idempotent()
    {
        var root = MakeInstall(
            (Wbk("CORE01"), B("dylan")),
            (Wbk("CORE00"), B("regina")),
            (Wbk("CORE06"), B("gail")));
        try
        {
            Dc2CharacterSkinInstaller.ApplyCrWavebanks(root, Dc2CharacterSkin.Gail, Dc2CharacterSkin.Gail);

            foreach (var target in new[] { "CORE01", "CORE00" })
                Assert.Equal(B("gail"), File.ReadAllBytes(Path.Combine(root, "CR", "data", target, "snd.wbk")));
            Assert.Equal(B("dylan"), File.ReadAllBytes(Path.Combine(root, Bak("CORE01"))));
            Assert.Equal(B("regina"), File.ReadAllBytes(Path.Combine(root, Bak("CORE00"))));
            // Donor dir is never touched.
            Assert.False(File.Exists(Path.Combine(root, Bak("CORE06"))));

            // Re-apply: the backup is never re-captured (no compounding on re-roll).
            Dc2CharacterSkinInstaller.ApplyCrWavebanks(root, Dc2CharacterSkin.Gail, Dc2CharacterSkin.Gail);
            Assert.Equal(B("dylan"), File.ReadAllBytes(Path.Combine(root, Bak("CORE01"))));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Apply_is_a_noop_without_a_cr_dir_and_skips_missing_wavebanks()
    {
        // Vanilla install (no CR dir): the engine-side CORE SOUND swap already covers it — no throw.
        var vanilla = MakeInstall();
        try
        {
            Dc2CharacterSkinInstaller.ApplyCrWavebanks(vanilla, Dc2CharacterSkin.Gail, Dc2CharacterSkin.Stock);
        }
        finally { Directory.Delete(vanilla, recursive: true); }

        // CR present but the donor bank missing: skip that pair, never throw (cosmetic-only step).
        var partial = MakeInstall((Wbk("CORE01"), B("dylan")));
        try
        {
            Dc2CharacterSkinInstaller.ApplyCrWavebanks(partial, Dc2CharacterSkin.Gail, Dc2CharacterSkin.Stock);
            Assert.Equal(B("dylan"), File.ReadAllBytes(Path.Combine(partial, Wbk("CORE01"))));
            Assert.False(File.Exists(Path.Combine(partial, Bak("CORE01"))));
        }
        finally { Directory.Delete(partial, recursive: true); }
    }

    [Fact]
    public void Restore_reverses_all_swaps_and_removes_backups()
    {
        var root = MakeInstall(
            (Wbk("CORE01"), B("dylan")),
            (Wbk("CORE00"), B("regina")),
            (Wbk("CORE05"), B("rick")));
        try
        {
            Dc2CharacterSkinInstaller.ApplyCrWavebanks(root, Dc2CharacterSkin.Rick, Dc2CharacterSkin.Rick);
            int restored = Dc2CharacterSkinInstaller.RestoreCrWavebanks(root);

            Assert.Equal(2, restored);
            Assert.Equal(B("dylan"), File.ReadAllBytes(Path.Combine(root, Wbk("CORE01"))));
            Assert.Equal(B("regina"), File.ReadAllBytes(Path.Combine(root, Wbk("CORE00"))));
            Assert.False(File.Exists(Path.Combine(root, Bak("CORE01"))));
            Assert.False(File.Exists(Path.Combine(root, Bak("CORE00"))));
            // Second restore: nothing left to do.
            Assert.Equal(0, Dc2CharacterSkinInstaller.RestoreCrWavebanks(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
