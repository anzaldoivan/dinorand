using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Exe;
using DinoRand.FileFormats.Stage;
using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Spoiler;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// "Enable Cross Character Weapons" (DC2): each of Regina/Dylan can wield the other's weapons while
/// still rendering with their OWN body model.
///
/// <para>Two halves, tested separately: (1) <see cref="Dc2WeaponGraft"/> builds a WEP_P package =
/// the weapon-OWNER's file (keeps the id's fire tail/sound/descriptors) with the blob geometry head
/// <c>[0x00,0x3550)</c> + TEXTURE/PALETTE entries replaced by the TARGET character's own model;
/// (2) <see cref="Dc2CrossCharWeaponPatch"/> repoints the NULL <c>0x71B230</c> catalog slots at the
/// new filenames, ORs the target's owner bit into <c>0x704260[id]+0xA</c>, and writes the new
/// filename strings into verified <c>.data</c> slack.</para>
///
/// <para>Measured 2026-07-19 from the pristine build: <c>[0x3550,0x3800)</c> is <c>0xCD</c> filler
/// slack, NOT a shared struct — four weapon files (WEP_P008/103/104/109, the four large weapons)
/// spill real geometry past <c>0x3550</c>, so their pairs are flagged
/// <see cref="Dc2CrossCharPair.HeadGraftSafe"/> = false and await an in-game witness.</para>
/// </summary>
public class Dc2CrossCharWeaponTests
{
    // ---- synthetic DC2 package fixtures -------------------------------------------------

    private const int HeadEnd = Dc2WeaponGraft.HeadEnd;   // 0x3550
    private const int BlobLen = 0x4000;

    /// <summary>A 7-entry DC2 WEP_P-shaped package: SOUND, DATA, TEXTURE, PALETTE, TEXTURE,
    /// PALETTE, LZSS0(blob). Entry table at offset 0 (32-byte entries), payloads 2048-aligned.</summary>
    private static byte[] MakePackage(byte[] blob, byte skinTag)
    {
        var payloads = new List<(GianEntryType Type, byte[] Bytes)>
        {
            (GianEntryType.Sound,   Fill(0x40, 0x53)),
            (GianEntryType.Data,    Fill(0x40, 0xD1)),
            (GianEntryType.Texture, Fill(0x800, skinTag)),
            (GianEntryType.Palette, Fill(0x40, skinTag)),
            (GianEntryType.Texture, Fill(0x800, skinTag)),
            (GianEntryType.Palette, Fill(0x40, skinTag)),
            (GianEntryType.Lzss0,   Lzss.Compress(blob)),
        };

        int total = 2048 + payloads.Sum(p => Align(p.Bytes.Length));
        var pkg = new byte[total];
        int pos = 2048;
        for (int i = 0; i < payloads.Count; i++)
        {
            BitConverter.GetBytes((uint)payloads[i].Type).CopyTo(pkg, i * 32);
            BitConverter.GetBytes((uint)payloads[i].Bytes.Length).CopyTo(pkg, i * 32 + 4);
            payloads[i].Bytes.CopyTo(pkg, pos);
            pos += Align(payloads[i].Bytes.Length);
        }
        return pkg;
    }

    private static int Align(int v) => (v + 2047) & ~2047;
    private static byte[] Fill(int n, byte b) => Enumerable.Repeat(b, n).ToArray();

    /// <summary>A blob whose geometry ends at <paramref name="geomEnd"/> (rest is 0xCD filler),
    /// with a distinguishable head byte and tail byte.</summary>
    private static byte[] MakeBlob(byte headTag, byte tailTag, int geomEnd)
    {
        var blob = new byte[BlobLen];
        for (int i = 0; i < geomEnd; i++) blob[i] = headTag;
        for (int i = geomEnd; i < 0x3800; i++) blob[i] = 0xCD;          // filler slack
        for (int i = 0x3800; i < BlobLen; i++) blob[i] = tailTag;       // fire tail / descriptors
        // the 3 absolute header pointers belong to the geometry head
        BitConverter.GetBytes(0x00662648u).CopyTo(blob, 0);
        BitConverter.GetBytes(0x00663110u | (uint)headTag).CopyTo(blob, 4);
        BitConverter.GetBytes(0x00663BD8u | (uint)headTag).CopyTo(blob, 8);
        return blob;
    }

    private static byte[] BlobOf(byte[] package)
    {
        var pkg = GianPackage.TryParse(package);
        Assert.NotNull(pkg);
        var e = pkg!.Entries.Last();
        return Lzss.Decompress(package.AsSpan(e.PayloadOffset, (int)e.DeclaredSize));
    }

    // ---- 1. the (char, weapon) grid -----------------------------------------------------

    [Fact]
    public void Pairs_cover_every_single_owner_regina_and_dylan_main_weapon()
    {
        // Verified from the pristine exe: WEP_P grid 0x71B230[charBase+id], Regina 0x1E9 / Dylan 0x1F9.
        // Files present => owner; 0x00 and 0x05 exist for BOTH chars (no work), 0x0A/0x0B are David's.
        var toDylan = Dc2CrossCharWeaponPatch.Pairs
            .Where(p => p.Target == Dc2CrossCharTarget.Dylan).Select(p => p.WeaponId).OrderBy(x => x);
        var toRegina = Dc2CrossCharWeaponPatch.Pairs
            .Where(p => p.Target == Dc2CrossCharTarget.Regina).Select(p => p.WeaponId).OrderBy(x => x);

        Assert.Equal(new byte[] { 0x02, 0x06, 0x07, 0x08 }, toDylan);   // Regina-owned mains
        Assert.Equal(new byte[] { 0x01, 0x03, 0x04, 0x09 }, toRegina);  // Dylan-owned mains
        Assert.Equal(8, Dc2CrossCharWeaponPatch.Pairs.Count);
    }

    [Fact]
    public void Pairs_source_the_tail_from_the_weapon_owner_and_the_body_from_the_target()
    {
        var handgun = Dc2CrossCharWeaponPatch.Pairs.Single(
            p => p.Target == Dc2CrossCharTarget.Dylan && p.WeaponId == 0x02);

        Assert.Equal("WEP_P002.DAT", handgun.OwnerFile);      // Regina's handgun: fire tail + sound
        Assert.Equal("WEP_P101.DAT", handgun.GeometryFile);   // Dylan's own body (proven donor)
        Assert.Equal("WEP_P102.DAT", handgun.GraftFile);      // natural slot-matching name

        // every graft targets its own character's namespace and is a fresh filename
        Assert.All(Dc2CrossCharWeaponPatch.Pairs, p =>
            Assert.StartsWith(p.Target == Dc2CrossCharTarget.Dylan ? "WEP_P1" : "WEP_P0", p.GraftFile));
        Assert.Equal(8, Dc2CrossCharWeaponPatch.Pairs.Select(p => p.GraftFile).Distinct().Count());
    }

    [Fact]
    public void Pairs_flag_the_four_spilling_large_weapons_as_unwitnessed()
    {
        // Measured geometry end: P008 0x3580, P103 0x37a0, P104 0x35a0, P109 0x35e0 — all past 0x3550.
        var unsafePairs = Dc2CrossCharWeaponPatch.Pairs.Where(p => !p.HeadGraftSafe)
            .Select(p => p.OwnerFile).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(
            new[] { "WEP_P008.DAT", "WEP_P103.DAT", "WEP_P104.DAT", "WEP_P109.DAT" }, unsafePairs);
    }

    [Fact]
    public void Sub_weapons_are_flag_only_because_they_resolve_char_independently()
    {
        // DC2-WEAPON-SYSTEM-DECODE §1c: WEP_SUB fileId = 0x213 + subIndex, no charBase term —
        // so Machete (0x11, Dylan) and Large Stungun (0x12, Regina) need no graft and no catalog write.
        Assert.Equal(new byte[] { 0x11, 0x12 }, Dc2CrossCharWeaponPatch.SubWeaponIds);
        Assert.DoesNotContain(Dc2CrossCharWeaponPatch.Pairs, p => p.WeaponId >= 0x10);
    }

    // ---- 2. the graft builder -----------------------------------------------------------

    [Fact]
    public void Graft_splices_the_target_head_over_the_owner_blob_preserving_size()
    {
        var owner = MakePackage(MakeBlob(headTag: 0xAA, tailTag: 0x11, geomEnd: 0x3000), skinTag: 0xA0);
        var geom = MakePackage(MakeBlob(headTag: 0xBB, tailTag: 0x22, geomEnd: 0x3200), skinTag: 0xB0);

        var graft = Dc2WeaponGraft.Build(owner, geom);
        var got = BlobOf(graft);

        Assert.Equal(BlobLen, got.Length);                                  // size preserved
        Assert.Equal(0xBB, got[0x100]);                                     // head = target geometry
        Assert.Equal(0x00662648u, BitConverter.ToUInt32(got, 0));           // fixed load-base pointer
    }

    [Fact]
    public void Graft_keeps_the_weapon_owners_tail_so_fire_descriptors_stay_at_their_baked_offsets()
    {
        var owner = MakePackage(MakeBlob(0xAA, tailTag: 0x11, geomEnd: 0x3000), skinTag: 0xA0);
        var geom = MakePackage(MakeBlob(0xBB, tailTag: 0x22, geomEnd: 0x3200), skinTag: 0xB0);

        var got = BlobOf(Dc2WeaponGraft.Build(owner, geom));

        Assert.Equal(0x11, got[0x3800]);   // tail belongs to the weapon owner
        Assert.Equal(0x11, got[BlobLen - 1]);
        Assert.Equal(0xBB, got[0x3100]);   // head belongs to the target character (its geometry ends 0x3200)
        Assert.Equal(0xCD, got[HeadEnd - 1]);  // ...and its trailing slack comes across as filler
    }

    [Fact]
    public void Graft_fills_spilled_owner_geometry_with_filler_so_no_orphan_fragment_survives()
    {
        // owner geometry runs to 0x37a0 (a "large weapon"); the target's body ends well before 0x3550,
        // so everything in [0x3550,0x37a0) would otherwise be a stranded fragment of the owner's model.
        var owner = MakePackage(MakeBlob(0xAA, tailTag: 0x11, geomEnd: 0x37A0), skinTag: 0xA0);
        var geom = MakePackage(MakeBlob(0xBB, tailTag: 0x22, geomEnd: 0x3200), skinTag: 0xB0);

        var got = BlobOf(Dc2WeaponGraft.Build(owner, geom));

        for (int i = HeadEnd; i < 0x37A0; i++)
            Assert.Equal(0xCD, got[i]);
        Assert.Equal(0x11, got[0x3800]);   // the real tail is still untouched
    }

    [Fact]
    public void Graft_output_reparses_as_a_seven_entry_dc2_package()
    {
        var graft = Dc2WeaponGraft.Build(
            MakePackage(MakeBlob(0xAA, 0x11, 0x3000), 0xA0),
            MakePackage(MakeBlob(0xBB, 0x22, 0x3200), 0xB0));

        var pkg = GianPackage.TryParse(graft);
        Assert.NotNull(pkg);
        Assert.True(pkg!.IsDc2);
        Assert.Equal(7, pkg.Entries.Count);
        Assert.Equal(GianEntryType.Lzss0, pkg.Entries.Last().Type);
    }

    [Fact]
    public void Graft_takes_the_targets_body_skin_but_leaves_the_weapons_effect_texture_alone()
    {
        var graft = Dc2WeaponGraft.Build(
            MakePackage(MakeBlob(0xAA, 0x11, 0x3000), skinTag: 0xA0),
            MakePackage(MakeBlob(0xBB, 0x22, 0x3200), skinTag: 0xB0));

        var pkg = GianPackage.TryParse(graft)!;
        foreach (var i in new[] { 2, 3 })         // body TEXTURE + PALETTE → the target character
            Assert.Equal(0xB0, graft[pkg.Entries[i].PayloadOffset]);
        // entries 4/5 are the WEAPON's effect texture/palette — grafting them corrupts explosions
        // (live-witnessed on the Missile Pod, 2026-07-20), so they must stay with the owner.
        foreach (var i in new[] { 4, 5 })
            Assert.Equal(0xA0, graft[pkg.Entries[i].PayloadOffset]);
        Assert.Equal(0x53, graft[pkg.Entries[0].PayloadOffset]);   // SOUND stays the weapon owner's
    }

    [Fact]
    public void Graft_lzss_payload_roundtrips()
    {
        var graft = Dc2WeaponGraft.Build(
            MakePackage(MakeBlob(0xAA, 0x11, 0x3000), 0xA0),
            MakePackage(MakeBlob(0xBB, 0x22, 0x3200), 0xB0));

        var pkg = GianPackage.TryParse(graft)!;
        var e = pkg.Entries.Last();
        var blob = Lzss.Decompress(graft.AsSpan(e.PayloadOffset, (int)e.DeclaredSize));
        Assert.Equal(BlobLen, blob.Length);
    }

    [Fact]
    public void Graft_geometry_end_detects_the_spill_boundary()
    {
        Assert.Equal(0x3200, Dc2WeaponGraft.GeometryEnd(MakePackage(MakeBlob(0xBB, 0x22, 0x3200), 0xB0)));
        Assert.Equal(0x37A0, Dc2WeaponGraft.GeometryEnd(MakePackage(MakeBlob(0xAA, 0x11, 0x37A0), 0xA0)));
    }

    // ---- 3. the exe edits ---------------------------------------------------------------

    /// <summary>Synthetic pristine image: real build length, all eight cross slots NULL, the item
    /// catalog carrying the PSX-verified owner flags, string slack zeroed.</summary>
    private static byte[] MakeExe()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        // owner flags (u16 at 0x704260 + id*12 + 0xA), PSX-verified values for the ids we touch
        var flags = new Dictionary<byte, ushort>
        {
            [0x01] = 0x004A, [0x02] = 0x0049, [0x03] = 0x002A, [0x04] = 0x004A,
            [0x06] = 0x0029, [0x07] = 0x0049, [0x08] = 0x0029, [0x09] = 0x004A,
            [0x11] = 0x00D2, [0x12] = 0x00D1,
        };
        foreach (var (id, f) in flags)
            BitConverter.GetBytes(f).CopyTo(exe, Dc2CrossCharWeaponPatch.ItemFlagsOffset + id * 12 + 0xA);
        // the stock (non-cross) catalog slots must be non-NULL for the build check
        foreach (var slot in new[] { 0x1E9, 0x1F9 })
            BitConverter.GetBytes(0x71BEC8u).CopyTo(exe, Dc2CrossCharWeaponPatch.CatalogBaseOffset + slot * 4);
        return exe;
    }

    [Fact]
    public void Apply_repoints_all_eight_null_catalog_slots_at_the_graft_filenames()
    {
        var exe = MakeExe();
        Dc2CrossCharWeaponPatch.Apply(exe);

        foreach (var p in Dc2CrossCharWeaponPatch.Pairs)
        {
            int slot = Dc2CrossCharWeaponPatch.CatalogSlot(p.Target, p.WeaponId);
            Assert.Equal(p.GraftFile, Dc2CrossCharWeaponPatch.ReadCatalogName(exe, slot));
        }
    }

    [Fact]
    public void Apply_adds_the_target_owner_bit_to_every_cross_weapon_including_subs()
    {
        var exe = MakeExe();
        Dc2CrossCharWeaponPatch.Apply(exe);

        Assert.Equal(0x004B, Dc2CrossCharWeaponPatch.ReadOwnerFlags(exe, 0x02)); // 0x49 | Dylan 0x02
        Assert.Equal(0x004B, Dc2CrossCharWeaponPatch.ReadOwnerFlags(exe, 0x01)); // 0x4A | Regina 0x01
        Assert.Equal(0x00D3, Dc2CrossCharWeaponPatch.ReadOwnerFlags(exe, 0x11)); // Machete + Regina
        Assert.Equal(0x00D3, Dc2CrossCharWeaponPatch.ReadOwnerFlags(exe, 0x12)); // Stungun + Dylan
    }

    [Fact]
    public void Apply_writes_each_graft_filename_into_the_data_slack()
    {
        var exe = MakeExe();
        Dc2CrossCharWeaponPatch.Apply(exe);

        var slack = exe.AsSpan(Dc2CrossCharWeaponPatch.StringSlackOffset,
                               Dc2CrossCharWeaponPatch.StringSlackLength).ToArray();
        foreach (var p in Dc2CrossCharWeaponPatch.Pairs)
            Assert.Contains(p.GraftFile, System.Text.Encoding.ASCII.GetString(slack));
    }

    [Fact]
    public void Apply_leaves_dual_owned_and_david_weapons_untouched()
    {
        var exe = MakeExe();
        var before = (byte[])exe.Clone();
        Dc2CrossCharWeaponPatch.Apply(exe);

        foreach (byte id in new byte[] { 0x00, 0x05, 0x0A, 0x0B, 0x13, 0x14, 0x15 })
        {
            int off = Dc2CrossCharWeaponPatch.ItemFlagsOffset + id * 12 + 0xA;
            Assert.Equal(BitConverter.ToUInt16(before, off), BitConverter.ToUInt16(exe, off));
        }
    }

    [Fact]
    public void Restore_roundtrips_to_pristine()
    {
        var exe = MakeExe();
        var pristine = (byte[])exe.Clone();

        Dc2CrossCharWeaponPatch.Apply(exe);
        Assert.NotEqual(pristine, exe);
        Dc2CrossCharWeaponPatch.Restore(exe);

        Assert.Equal(pristine, exe);
    }

    [Fact]
    public void Apply_is_idempotent_and_recognizes_its_own_work()
    {
        var exe = MakeExe();
        Dc2CrossCharWeaponPatch.Apply(exe);
        var once = (byte[])exe.Clone();

        Assert.True(Dc2CrossCharWeaponPatch.IsApplied(exe));
        Assert.False(Dc2CrossCharWeaponPatch.IsRecognizedPristine(exe));
        Dc2CrossCharWeaponPatch.Apply(exe);

        Assert.Equal(once, exe);
    }

    [Fact]
    public void Apply_refuses_a_foreign_build()
    {
        var wrongLength = new byte[Dc2WpGatePatch.ExpectedLength - 1];
        Assert.Throws<InvalidOperationException>(() => Dc2CrossCharWeaponPatch.Apply(wrongLength));

        var occupiedSlack = MakeExe();
        occupiedSlack[Dc2CrossCharWeaponPatch.StringSlackOffset + 4] = 0x41;  // someone else's data
        Assert.Throws<InvalidOperationException>(() => Dc2CrossCharWeaponPatch.Apply(occupiedSlack));
    }

    // ---- 4. config + seed encoding ------------------------------------------------------

    [Fact]
    public void Flag_defaults_off_and_leaves_existing_seed_strings_byte_identical()
    {
        Assert.False(new RandomizerConfig().Dc2CrossCharWeapons);

        // default-OFF must not perturb any historical seed string
        Assert.Equal(
            SeedString.Encode(new Seed(42), new RandomizerConfig()),
            SeedString.Encode(new Seed(42), new RandomizerConfig { Dc2CrossCharWeapons = false }));
    }

    [Fact]
    public void Seed_string_roundtrips_the_cross_char_flag_on_byte_16_bit_6()
    {
        var off = SeedString.Encode(new Seed(42), new RandomizerConfig());
        var on = SeedString.Encode(new Seed(42), new RandomizerConfig { Dc2CrossCharWeapons = true });

        Assert.NotEqual(off, on);
        Assert.True(SeedString.TryParse(on, out var seed, out var config));
        Assert.Equal(42, seed.Value);
        Assert.True(config.Dc2CrossCharWeapons);

        // and it composes with the other byte-16 flags rather than clobbering them
        var both = SeedString.Encode(new Seed(42),
            new RandomizerConfig { Dc2CrossCharWeapons = true, Dc2RekeyPlateDoor = true });
        Assert.True(SeedString.TryParse(both, out _, out var bothConfig));
        Assert.True(bothConfig.Dc2CrossCharWeapons);
        Assert.True(bothConfig.Dc2RekeyPlateDoor);
    }

    // ---- 5. real-file invariants (skipped when the game isn't present) -------------------

    private static string? FindDataDir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "4249140_DinoCrisis2", "rebirth", "Data");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    [Fact]
    public void Real_weapon_files_match_the_measured_spill_census()
    {
        var dataDir = FindDataDir();
        if (dataDir is null) return;   // no game files → skip

        foreach (var p in Dc2CrossCharWeaponPatch.Pairs)
        {
            var owner = File.ReadAllBytes(Path.Combine(dataDir, p.OwnerFile));
            bool fits = Dc2WeaponGraft.GeometryEnd(owner) <= Dc2WeaponGraft.HeadEnd;
            Assert.Equal(p.HeadGraftSafe, fits);
        }
    }

    [Fact]
    public void Real_graft_builds_and_preserves_the_blob_size_for_every_pair()
    {
        var dataDir = FindDataDir();
        if (dataDir is null) return;   // no game files → skip

        foreach (var p in Dc2CrossCharWeaponPatch.Pairs)
        {
            var owner = File.ReadAllBytes(Path.Combine(dataDir, p.OwnerFile));
            var geom = File.ReadAllBytes(Path.Combine(dataDir, p.GeometryFile));

            var graft = Dc2WeaponGraft.Build(owner, geom);

            Assert.Equal(BlobOf(owner).Length, BlobOf(graft).Length);
            Assert.Equal(7, GianPackage.TryParse(graft)!.Entries.Count);
        }
    }
}
