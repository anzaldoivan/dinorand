using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Dc2.Passes;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Install;
using DinoRand.Randomizer.Spoiler;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// DC2 character-skin swap: main-game Dylan renders as Gail or Rick by serving their Extra Crisis
/// graft files (WP75A/79A/83A/84A: 64KB texture + ~14KB geometry blob applied over the WEP_P model
/// head at 0x662500) under Dylan's six WP&lt;n&gt;A slots, plus the WP-gate exe patch
/// (docs/dc2/DC2-EXTRA-CRISIS-ROSTER-DECODE.md §7–9). Visual-only: weapon ids, fire tails and
/// behavior stay Dylan's. Real-data tests skip when the game files are absent.
/// </summary>
public class Dc2PlayerModelSwapTests
{
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "4249140_DinoCrisis2", "rebirth", "Data")))
                return dir.FullName;
        return null;
    }

    private static string? FindDataDir()
    {
        var root = FindRepoRoot();
        return root is null ? null : Path.Combine(root, "4249140_DinoCrisis2", "rebirth", "Data");
    }

    /// <summary>Pristine bytes mirroring the pass's resolution order
    /// (installer backup → <c>.dinorand-bak</c> sibling → live).</summary>
    private static byte[] Pristine(string dataDir, string name)
    {
        string installerBackup = Path.Combine(dataDir, ".dinorand_backup", name);
        string sibling = Path.Combine(dataDir, name + ".dinorand-bak");
        string live = Path.Combine(dataDir, name);
        return File.ReadAllBytes(File.Exists(installerBackup) ? installerBackup
                               : File.Exists(sibling) ? sibling : live);
    }

    [Fact]
    public void Registry_covers_all_six_dylan_wp_slots_per_skin()
    {
        // Dylan's WP row = fileId 0x227+wid for his weapon ids {0,1,3,4,5,9} (roster decode §4).
        var dylanSlots = new[] { "WP10A.DAT", "WP11A.DAT", "WP13A.DAT", "WP14A.DAT", "WP15A.DAT", "WP19A.DAT" };
        foreach (var skin in new[] { Dc2CharacterSkin.Gail, Dc2CharacterSkin.Rick })
        {
            var plan = Dc2PlayerModelSwap.SkinDonors[skin];
            Assert.Equal(dylanSlots, plan.Select(p => p.Target).OrderBy(t => t).ToArray());
        }
        // Donor pairing mirrors the engine's own char-5/6 selects (0x4826A5):
        // Gail: weapon 5 → WP75A, everything else WP79A. Rick: weapon 3 → WP83A, else WP84A.
        var gail = Dc2PlayerModelSwap.SkinDonors[Dc2CharacterSkin.Gail].ToDictionary(p => p.Target, p => p.Donor);
        Assert.Equal("WP75A.DAT", gail["WP15A.DAT"]);
        Assert.All(gail.Where(kv => kv.Key != "WP15A.DAT"), kv => Assert.Equal("WP79A.DAT", kv.Value));
        var rick = Dc2PlayerModelSwap.SkinDonors[Dc2CharacterSkin.Rick].ToDictionary(p => p.Target, p => p.Donor);
        Assert.Equal("WP83A.DAT", rick["WP13A.DAT"]);
        Assert.All(rick.Where(kv => kv.Key != "WP13A.DAT"), kv => Assert.Equal("WP84A.DAT", kv.Value));
    }

    [Fact]
    public void Dylan_skin_and_default_config_disable_the_pass()
    {
        var pass = new Dc2PlayerModelSwap();
        Assert.False(pass.IsEnabled(new RandomizerConfig()));
        Assert.False(pass.IsEnabled(new RandomizerConfig { Dc2CharacterSkin = Dc2CharacterSkin.Stock }));
        Assert.True(pass.IsEnabled(new RandomizerConfig { Dc2CharacterSkin = Dc2CharacterSkin.Gail }));
        Assert.True(pass.IsEnabled(new RandomizerConfig { Dc2CharacterSkin = Dc2CharacterSkin.Random }));
    }

    [Fact]
    public void Random_skin_resolves_deterministically_from_seed_and_never_to_random()
    {
        var seed = new Seed(1234567);
        var first = Dc2PlayerModelSwap.ResolveSkin(Dc2CharacterSkin.Random, seed);
        Assert.Equal(first, Dc2PlayerModelSwap.ResolveSkin(Dc2CharacterSkin.Random, seed));
        Assert.NotEqual(Dc2CharacterSkin.Random, first);
        // Fixed choices resolve to themselves.
        Assert.Equal(Dc2CharacterSkin.Rick, Dc2PlayerModelSwap.ResolveSkin(Dc2CharacterSkin.Rick, seed));
        // All three outcomes are reachable across seeds.
        var seen = Enumerable.Range(0, 64)
            .Select(i => Dc2PlayerModelSwap.ResolveSkin(Dc2CharacterSkin.Random, new Seed(i)))
            .ToHashSet();
        Assert.Contains(Dc2CharacterSkin.Stock, seen);
        Assert.Contains(Dc2CharacterSkin.Gail, seen);
        Assert.Contains(Dc2CharacterSkin.Rick, seen);
    }

    [Fact]
    public void Plan_refuses_files_that_are_not_dc2_packages()
    {
        // All-or-nothing: a corrupt donor must throw before anything is planned/emitted.
        var dir = Path.Combine(Path.GetTempPath(), "dinorand-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (_, donor) in Dc2PlayerModelSwap.SkinDonors[Dc2CharacterSkin.Gail])
                File.WriteAllBytes(Path.Combine(dir, donor), new byte[64]); // not a Gian package
            Assert.Throws<InvalidDataException>(() => Dc2PlayerModelSwap.Plan(dir, Dc2CharacterSkin.Gail));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Plan_refuses_when_the_two_pristine_sources_disagree()
    {
        // Poisoned-capture guard (K82): if .dinorand_backup\<file> and <file>.dinorand-bak disagree,
        // one of them was captured from an already-modded file — refuse instead of grafting poison.
        var dir = Path.Combine(Path.GetTempPath(), "dinorand-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, GameInstaller.BackupDirName));
        try
        {
            foreach (var (_, donor) in Dc2PlayerModelSwap.SkinDonors[Dc2CharacterSkin.Gail])
            {
                File.WriteAllBytes(Path.Combine(dir, donor), new byte[64]);
                File.WriteAllBytes(Path.Combine(dir, GameInstaller.BackupDirName, donor), new byte[] { 1, 1 });
                File.WriteAllBytes(Path.Combine(dir, donor + GameInstaller.SiblingBackupSuffix), new byte[] { 2, 2 });
            }
            var ex = Assert.Throws<InvalidDataException>(() => Dc2PlayerModelSwap.Plan(dir, Dc2CharacterSkin.Gail));
            Assert.Contains("disagree", ex.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Plan_throws_when_a_donor_file_is_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dinorand-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir); // empty: every donor missing
        try
        {
            Assert.Throws<FileNotFoundException>(() => Dc2PlayerModelSwap.Plan(dir, Dc2CharacterSkin.Rick));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- gated real-data integration ----

    [Fact]
    public void Plan_emits_six_wp_files_bytewise_equal_to_pristine_donors_plus_core_atlas()
    {
        var dataDir = FindDataDir();
        if (dataDir is null) return; // no game files → skip

        foreach (var skin in new[] { Dc2CharacterSkin.Gail, Dc2CharacterSkin.Rick })
        {
            var plan = Dc2PlayerModelSwap.Plan(dataDir, skin);
            Assert.Equal(7, plan.Count); // 6 WP grafts + CORE01 menu atlas
            foreach (var (fileName, bytes) in plan)
            {
                if (fileName == Dc2PlayerModelSwap.DylanCoreFile) continue; // covered below
                var donor = Dc2PlayerModelSwap.SkinDonors[skin].Single(p => p.Target == fileName).Donor;
                Assert.Equal(Pristine(dataDir, donor), bytes);
                var pkg = GianPackage.TryParse(bytes);
                Assert.NotNull(pkg);
                Assert.True(pkg!.IsDc2);
            }
            Assert.Contains(plan, p => p.FileName == Dc2PlayerModelSwap.DylanCoreFile);
        }
    }

    [Fact]
    public void Menu_atlas_carries_donor_portrait_strip_keeps_target_palette_with_sort_plate_over_trat()
    {
        var dataDir = FindDataDir();
        if (dataDir is null) return; // no game files → skip

        var built = Dc2PlayerModelSwap.BuildCoreFile(dataDir, "CORE01.DAT", "CORE06.DAT");
        var target = Pristine(dataDir, "CORE01.DAT");
        var donor = Pristine(dataDir, "CORE06.DAT");

        var builtPkg = GianPackage.TryParse(built)!;
        var targetPkg = GianPackage.TryParse(target)!;
        var donorPkg = GianPackage.TryParse(donor)!;
        GianEntry E(GianPackage p, GianEntryType t) => p.Entries.Single(e => e.Type == t);

        // The repacked package keeps DC2 shape: same entry types in order, sector-aligned payloads.
        Assert.True(builtPkg.IsDc2);
        Assert.Equal(targetPkg.Entries.Select(e => e.Type), builtPkg.Entries.Select(e => e.Type));
        Assert.All(builtPkg.Entries, e => Assert.Equal(0, e.PayloadOffset % 2048));

        // SOUND (voice bank) is now the DONOR's, byte-for-byte; DATA (params) stays the target's.
        var builtSound = E(builtPkg, GianEntryType.Sound);
        var donorSound = E(donorPkg, GianEntryType.Sound);
        Assert.Equal(donorSound.DeclaredSize, builtSound.DeclaredSize);
        Assert.Equal(donor.AsSpan(donorSound.PayloadOffset, (int)donorSound.DeclaredSize).ToArray(),
                     built.AsSpan(builtSound.PayloadOffset, (int)builtSound.DeclaredSize).ToArray());
        var dataE = E(builtPkg, GianEntryType.Data);
        var targetData = E(targetPkg, GianEntryType.Data);
        Assert.Equal(target.AsSpan(targetData.PayloadOffset, (int)targetData.DeclaredSize).ToArray(),
                     built.AsSpan(dataE.PayloadOffset, (int)dataE.DeclaredSize).ToArray());

        // The swapped bank is internally sound: every nonzero record in the 32-slot sample
        // directory at payload+0x100 points in-bounds at RIFF magic.
        int sndOff = builtSound.PayloadOffset;
        int riffs = 0;
        for (int i = 0; i < 32; i++)
        {
            int ofs = BitConverter.ToInt32(built, sndOff + 0x100 + i * 8);
            if (ofs == 0) continue;
            Assert.InRange(ofs, 0x200, (int)builtSound.DeclaredSize - 4);
            Assert.Equal("RIFF"u8.ToArray(), built.AsSpan(sndOff + ofs, 4).ToArray());
            riffs++;
        }
        Assert.True(riffs > 0);

        // PALETTE stays the TARGET's (donor palette + target weapon icons = colour artifacts, witnessed);
        // strip-0 TEXTURE is the donor's except the 128-byte TRAT plate region, which now repeats the
        // SORT row (strip 0, byte cols 48..63, rows 0..7 → 8..15). Strips 1-3 (weapon icons) stay the
        // target's — asserted in Menu_atlas_preserves_target_weapon_icons_and_palette_takes_only_donor_portrait.
        var pal = E(builtPkg, GianEntryType.Palette);
        Assert.Equal(target.AsSpan(E(targetPkg, GianEntryType.Palette).PayloadOffset, (int)pal.DeclaredSize).ToArray(),
                     built.AsSpan(pal.PayloadOffset, (int)pal.DeclaredSize).ToArray());

        var tex = E(builtPkg, GianEntryType.Texture).PayloadOffset;
        var donorTex = E(donorPkg, GianEntryType.Texture).PayloadOffset;
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 64; x++)
            {
                bool plate = y >= 8 && x >= 48;
                byte expected = plate ? built[tex + (y - 8) * 64 + x] : donor[donorTex + y * 64 + x];
                Assert.Equal(expected, built[tex + y * 64 + x]);
            }
        // Face region (the only per-char texture diff) is the donor's.
        Assert.Equal(donor.AsSpan(donorTex + 0x2000, 0x2000).ToArray(),
                     built.AsSpan(tex + 0x2000, 0x2000).ToArray());
    }

    [Fact]
    public void Menu_atlas_preserves_target_weapon_icons_and_palette_takes_only_donor_portrait()
    {
        // Root-caused live 2026-07-10 (CE + CORE-texture bisection): the menu weapon-select icons are a
        // PER-CHARACTER atlas in the CORE texture's strips 1-3 (bytes 0x4000..0xFFFF), indexed by the
        // item-catalog (U,V). The portrait/name/plates live in strip 0 (0x0000..0x3FFF). Copying the
        // WHOLE donor texture (+ donor palette) replaced the target's weapon icons with the donor's, so
        // e.g. Regina-as-Gail rendered Gail's icons for Regina's weapons. The fix: take ONLY the donor's
        // strip 0 (portrait), keep the target's strips 1-3 (weapon icons) AND the target's PALETTE
        // (donor palette + target icons = colour artifacts, also witnessed).
        var dataDir = FindDataDir();
        if (dataDir is null) return; // no game files → skip

        var built = Dc2PlayerModelSwap.BuildCoreFile(dataDir, "CORE01.DAT", "CORE06.DAT");
        var target = Pristine(dataDir, "CORE01.DAT");
        var donor = Pristine(dataDir, "CORE06.DAT");
        var bp = GianPackage.TryParse(built)!; var tp = GianPackage.TryParse(target)!; var dp = GianPackage.TryParse(donor)!;
        GianEntry E(GianPackage p, GianEntryType t) => p.Entries.Single(e => e.Type == t);
        int bt = E(bp, GianEntryType.Texture).PayloadOffset;
        int tt = E(tp, GianEntryType.Texture).PayloadOffset;
        int dt = E(dp, GianEntryType.Texture).PayloadOffset;

        // Weapon-icon strips 1-3 stay the TARGET's, byte-for-byte.
        Assert.Equal(target.AsSpan(tt + 0x4000, 0xC000).ToArray(), built.AsSpan(bt + 0x4000, 0xC000).ToArray());
        // Portrait (strip-0 face region 0x2000..0x3FFF, no plate/name fixups) is the DONOR's.
        Assert.Equal(donor.AsSpan(dt + 0x2000, 0x2000).ToArray(), built.AsSpan(bt + 0x2000, 0x2000).ToArray());
        // PALETTE stays the TARGET's.
        var bpal = E(bp, GianEntryType.Palette); var tpal = E(tp, GianEntryType.Palette);
        Assert.Equal(target.AsSpan(tpal.PayloadOffset, (int)tpal.DeclaredSize).ToArray(),
                     built.AsSpan(bpal.PayloadOffset, (int)bpal.DeclaredSize).ToArray());
    }

    [Fact]
    public void Menu_name_glyphs_follow_the_skin()
    {
        var dataDir = FindDataDir();
        if (dataDir is null) return; // no game files → skip

        // (target, donor, name span px cols) — Dylan span 57..85, Regina span 24..56, rows 40..45.
        foreach (var (targetName, donorName, word, col, width) in new[]
                 {
                     ("CORE01.DAT", "CORE06.DAT", "GAIL", 57, 29),
                     ("CORE00.DAT", "CORE05.DAT", "RICK", 24, 33),
                 })
        {
            var built = Dc2PlayerModelSwap.BuildCoreFile(dataDir, targetName, donorName);
            var donor = Pristine(dataDir, donorName);
            var builtPkg = GianPackage.TryParse(built)!;
            var donorPkg = GianPackage.TryParse(donor)!;
            int tex = builtPkg.Entries.Single(e => e.Type == GianEntryType.Texture).PayloadOffset;
            int donorTex = donorPkg.Entries.Single(e => e.Type == GianEntryType.Texture).PayloadOffset;

            // Expected = donor strips 0-1 with the same composition applied to a scratch buffer.
            var expected = donor.AsSpan(donorTex, 0x8000).ToArray();
            Dc2PlayerModelSwap.ComposeMenuName(expected, 0, col, width, word);

            // Name rows 40..45 match the composition and actually changed (name follows the skin).
            bool changed = false;
            for (int y = 40; y < 46; y++)
                for (int x = 0; x < 64; x++)
                {
                    Assert.Equal(expected[y * 64 + x], built[tex + y * 64 + x]);
                    changed |= built[tex + y * 64 + x] != donor[donorTex + y * 64 + x];
                }
            Assert.True(changed);

            // Rows outside the name band and the glyph-source label block are untouched donor bytes
            // (plate rows 0..15 are covered by the SORT-over-TRAT test).
            for (int y = 16; y < 128; y++)
            {
                if (y is >= 40 and < 46) continue;
                Assert.Equal(donor.AsSpan(donorTex + y * 64, 64).ToArray(),
                             built.AsSpan(tex + y * 64, 64).ToArray());
            }
        }
    }

    [Fact]
    public void Pass_emits_the_six_wp_files_through_the_sink()
    {
        var root = FindRepoRoot();
        if (root is null) return; // no game files → skip

        var outDir = Path.Combine(Path.GetTempPath(), "dinorand-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var game = new DinoCrisis2();
            var installDir = Path.Combine(root, "4249140_DinoCrisis2");
            var context = new Dc2RandomizationContext(
                game, Array.Empty<DinoRand.FileFormats.Stage.Dc2.Dc2RoomFile>(),
                Seed.Parse("1"), new RandomizerConfig { Dc2CharacterSkin = Dc2CharacterSkin.Gail },
                _ => { }, new Dc2OutputDirSink(outDir), game.GetDataDir(installDir));

            new Dc2PlayerModelSwap().Apply(context);

            var written = Directory.GetFiles(outDir, "WP*.DAT").Select(Path.GetFileName)
                                   .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(6, written.Count);
            foreach (var (target, _) in Dc2PlayerModelSwap.SkinDonors[Dc2CharacterSkin.Gail])
                Assert.Contains(target, written);
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public void Regina_registry_covers_her_six_wp_slots_with_native_pairings()
    {
        // Regina's WP row = fileId 0x21D+wid for her weapon ids {0,2,5,6,7,8}; cross-rig graft
        // in-game verified 2026-07-03 (correct proportions, no crashes).
        var reginaSlots = new[] { "WP00A.DAT", "WP02A.DAT", "WP05A.DAT", "WP06A.DAT", "WP07A.DAT", "WP08A.DAT" };
        foreach (var skin in new[] { Dc2CharacterSkin.Gail, Dc2CharacterSkin.Rick })
            Assert.Equal(reginaSlots,
                Dc2PlayerModelSwap.ReginaSkinDonors[skin].Select(pr => pr.Target).OrderBy(t => t).ToArray());
        // Gail: weapon 5 keeps his native WP75A pairing; everything else WP79A.
        var gail = Dc2PlayerModelSwap.ReginaSkinDonors[Dc2CharacterSkin.Gail].ToDictionary(pr => pr.Target, pr => pr.Donor);
        Assert.Equal("WP75A.DAT", gail["WP05A.DAT"]);
        Assert.All(gail.Where(kv => kv.Key != "WP05A.DAT"), kv => Assert.Equal("WP79A.DAT", kv.Value));
        // Rick: Regina has no weapon 3, so WP83A never applies — WP84A everywhere.
        Assert.All(Dc2PlayerModelSwap.ReginaSkinDonors[Dc2CharacterSkin.Rick],
                   pr => Assert.Equal("WP84A.DAT", pr.Donor));
    }

    [Fact]
    public void Regina_skin_enables_the_pass_and_plans_independently()
    {
        var pass = new Dc2PlayerModelSwap();
        Assert.True(pass.IsEnabled(new RandomizerConfig { Dc2ReginaSkin = Dc2CharacterSkin.Rick }));

        var dataDir = FindDataDir();
        if (dataDir is null) return; // no game files -> skip
        // Regina-only plan = her six slots + CORE00; combined plan = fourteen distinct files.
        var reginaOnly = Dc2PlayerModelSwap.Plan(dataDir, Dc2CharacterSkin.Stock, Dc2CharacterSkin.Rick);
        Assert.Equal(7, reginaOnly.Count);
        Assert.All(reginaOnly.Where(pr => pr.FileName != Dc2PlayerModelSwap.ReginaCoreFile),
                   pr => Assert.Equal(Pristine(dataDir, "WP84A.DAT"), pr.Bytes));
        Assert.Contains(reginaOnly, pr => pr.FileName == Dc2PlayerModelSwap.ReginaCoreFile);
        var both = Dc2PlayerModelSwap.Plan(dataDir, Dc2CharacterSkin.Gail, Dc2CharacterSkin.Rick);
        Assert.Equal(14, both.Select(pr => pr.FileName).Distinct().Count());
    }

    [Fact]
    public void Seed_string_roundtrips_the_regina_skin_with_byte16()
    {
        var defaultStr = SeedString.Encode(new Seed(42), new RandomizerConfig());
        Assert.Equal(defaultStr, SeedString.Encode(new Seed(42),
            new RandomizerConfig { Dc2ReginaSkin = Dc2CharacterSkin.Stock }));

        foreach (var skin in new[] { Dc2CharacterSkin.Gail, Dc2CharacterSkin.Rick, Dc2CharacterSkin.Random })
        {
            var s = SeedString.Encode(new Seed(42), new RandomizerConfig { Dc2ReginaSkin = skin });
            Assert.True(SeedString.TryParse(s, out var seed, out var config));
            Assert.Equal(skin, config.Dc2ReginaSkin);
            Assert.Equal(Dc2CharacterSkin.Stock, config.Dc2CharacterSkin);
            Assert.Equal(42, seed.Value);
        }
        // Both skins round-trip together.
        var s2 = SeedString.Encode(new Seed(7), new RandomizerConfig
            { Dc2CharacterSkin = Dc2CharacterSkin.Gail, Dc2ReginaSkin = Dc2CharacterSkin.Random });
        Assert.True(SeedString.TryParse(s2, out _, out var cfg2));
        Assert.Equal(Dc2CharacterSkin.Gail, cfg2.Dc2CharacterSkin);
        Assert.Equal(Dc2CharacterSkin.Random, cfg2.Dc2ReginaSkin);
    }

    [Fact]
    public void Seed_string_roundtrips_the_skin_and_defaults_stay_byte_identical()
    {
        // Non-default skin rides byte 11 bits 6–7; default configs keep their historical strings.
        var defaultStr = SeedString.Encode(new Seed(42), new RandomizerConfig());
        var dylanStr = SeedString.Encode(new Seed(42),
            new RandomizerConfig { Dc2CharacterSkin = Dc2CharacterSkin.Stock });
        Assert.Equal(defaultStr, dylanStr);

        foreach (var skin in new[] { Dc2CharacterSkin.Gail, Dc2CharacterSkin.Rick, Dc2CharacterSkin.Random })
        {
            var s = SeedString.Encode(new Seed(42), new RandomizerConfig { Dc2CharacterSkin = skin });
            Assert.NotEqual(defaultStr, s);
            Assert.True(SeedString.TryParse(s, out var seed, out var config));
            Assert.Equal(42, seed.Value);
            Assert.Equal(skin, config.Dc2CharacterSkin);
        }
    }
}
