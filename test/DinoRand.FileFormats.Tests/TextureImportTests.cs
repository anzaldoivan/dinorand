using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Texture-aware cross-species import (docs/dc1/TEXTURE-IMPORT-VRAM.md, increment 2). Pins the
/// offline-resolved DC1 texture format: a room package's type-8 entries are raw 16bpp VRAM
/// upload blocks (LZSS-compressed; decompressed payload = W*H*2 bytes), their destination
/// rect lives in the 16-byte Gian entry header (u16 X,Y,W,H = a PSX LoadImage RECT), and an
/// enemy's model primitives reference a texture page (tpage) + palette (CLUT) in that VRAM.
///
/// <para>Synthetic tests prove the parse / tpage-CLUT math / relocatable round-trip without
/// game files; a gated real-install test extracts the raptor's texture block from st10e and
/// checks it against the model's tpage 0x8b/0x9b (X=704) + CLUT 0x7fb0 (palette VRAM 768,510),
/// the codes dumped statically by tools/scd_re/prim_walk.py.</para>
/// </summary>
public class TextureImportTests
{
    // ---- PSX GPU tpage / CLUT bit math (the codes baked into a model's primitives) ----

    [Fact]
    public void TpageOrigin_DecodesPageColumnAndRow()
    {
        // 0x8b = X base 11*64=704, Y base 0, 8bpp; 0x9b = same column, Y base 256.
        Assert.Equal((704, 0), TextureImporter.TpageOrigin(0x8b));
        Assert.Equal((704, 256), TextureImporter.TpageOrigin(0x9b));
        Assert.Equal((192, 0), TextureImporter.TpageOrigin(0x83));
    }

    [Fact]
    public void ClutOrigin_DecodesPaletteVramCoords()
    {
        // CLUT: bits0-5 = X/16, bits6-14 = Y. 0x7ff0 -> (768,511); 0x7fb0 -> (768,510).
        Assert.Equal((768, 511), TextureImporter.ClutOrigin(0x7ff0));
        Assert.Equal((768, 510), TextureImporter.ClutOrigin(0x7fb0));
    }

    [Fact]
    public void MakeTpage_MakeClut_InvertOrigins()
    {
        foreach (ushort tp in new ushort[] { 0x8b, 0x9b, 0x82, 0x83 })
        {
            var (x, y) = TextureImporter.TpageOrigin(tp);
            int bpp = (tp >> 7) & 3;
            Assert.Equal(tp, TextureImporter.MakeTpage(x, y, bpp));
        }
        foreach (ushort cl in new ushort[] { 0x7ff0, 0x7fb0, 0x0000, 0x00f5 })
        {
            var (x, y) = TextureImporter.ClutOrigin(cl);
            Assert.Equal(cl, TextureImporter.MakeClut(x, y));
        }
    }

    // ---- Synthetic package: parse VRAM blocks + extract a species texture block ----

    /// <summary>Builds a DC1 package with full 16-byte entry headers (incl. the VRAM X,Y,W,H
    /// coords that GianPackage does not surface) and LZSS-compressed type-8 payloads.</summary>
    private static byte[] BuildTexturePackage(params (GianEntryType type, int x, int y, int w, int h, byte[] payload)[] e)
    {
        static int Align(int v) => (v + 2047) & ~2047;
        int total = 2048 + e.Sum(x => Align(x.payload.Length));
        var buf = new byte[total];
        int pos = 2048;
        for (int i = 0; i < e.Length; i++)
        {
            int hdr = i * 16;
            WriteU32(buf, hdr, (uint)e[i].type);
            WriteU32(buf, hdr + 4, (uint)e[i].payload.Length);
            WriteU16(buf, hdr + 8, (ushort)e[i].x);
            WriteU16(buf, hdr + 10, (ushort)e[i].y);
            WriteU16(buf, hdr + 12, (ushort)e[i].w);
            WriteU16(buf, hdr + 14, (ushort)e[i].h);
            e[i].payload.CopyTo(buf, pos);
            pos += Align(e[i].payload.Length);
        }
        return buf;
    }

    private static void WriteU32(byte[] b, int o, uint v)
    { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    private static void WriteU16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }

    private static byte[] FakePixels(int w, int h, byte seed)
    {
        var px = new byte[w * h * 2];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)(seed + i * 7);
        return px;
    }

    [Fact]
    public void ParseVramBlocks_DecodesRectAndDecompressesPayload()
    {
        var texPx = FakePixels(64, 512, 0x11);   // 64x512x2 = 0x10000 bytes
        var palPx = FakePixels(256, 1, 0x22);    // 256-colour 8bpp palette
        var pkg = BuildTexturePackage(
            (GianEntryType.Lzss2, 704, 0, 64, 512, Lzss.Compress(texPx)),
            (GianEntryType.Palette, 768, 510, 256, 1, palPx),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62, 0x02 }));

        var blocks = TextureImporter.ParseVramBlocks(pkg);

        var tex = blocks.Single(b => b.Type == GianEntryType.Lzss2);
        Assert.Equal(new VramRect(704, 0, 64, 512), tex.Dst);
        Assert.Equal(64 * 512 * 2, tex.Pixels.Length);     // structure: W*H*2
        Assert.Equal(texPx, tex.Pixels);                    // byte-exact decompress

        var pal = blocks.Single(b => b.Type == GianEntryType.Palette);
        Assert.Equal(new VramRect(768, 510, 256, 1), pal.Dst);
        Assert.Equal(palPx, pal.Pixels);
    }

    [Fact]
    public void ExtractSpeciesTexture_MapsModelCodesToCoveringBlocks()
    {
        var texPx = FakePixels(64, 512, 0x33);
        var palPx = FakePixels(256, 1, 0x44);
        var pkg = BuildTexturePackage(
            (GianEntryType.Lzss2, 704, 0, 64, 512, Lzss.Compress(texPx)),
            (GianEntryType.Palette, 768, 510, 256, 1, palPx),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));

        // Raptor codes: tpages 0x8b (704,0) and 0x9b (704,256); CLUT 0x7fb0 (768,510).
        var block = TextureImporter.ExtractSpeciesTexture(pkg, new ushort[] { 0x8b, 0x9b }, 0x7fb0);

        Assert.Equal(new VramRect(704, 0, 64, 512), block.Texture.Dst);
        Assert.Equal(new VramRect(768, 510, 256, 1), block.Palette.Dst);
        Assert.Equal((ushort)0x7fb0, block.ClutCode);
        Assert.Contains((ushort)0x8b, block.TpageCodes);
        Assert.Contains((ushort)0x9b, block.TpageCodes);
    }

    [Fact]
    public void Relocate_ShiftsRectsAndRewritesCodes_RoundTripsAtZero()
    {
        var texPx = FakePixels(64, 512, 0x55);
        var palPx = FakePixels(256, 1, 0x66);
        var pkg = BuildTexturePackage(
            (GianEntryType.Lzss2, 704, 0, 64, 512, Lzss.Compress(texPx)),
            (GianEntryType.Palette, 768, 510, 256, 1, palPx),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));
        var block = TextureImporter.ExtractSpeciesTexture(pkg, new ushort[] { 0x8b, 0x9b }, 0x7fb0);

        // No-op relocation reproduces the same rects, codes and pixels (identity).
        var same = block.Relocate(0, 0);
        Assert.Equal(block.Texture.Dst, same.Texture.Dst);
        Assert.Equal(block.TpageCodes, same.TpageCodes);
        Assert.Equal(block.ClutCode, same.ClutCode);
        Assert.Equal(block.Texture.Pixels, same.Texture.Pixels);    // pixels never change

        // Move the texture one page-column right (X 704 -> 768, dx=64) and the palette down 1.
        var moved = block.Relocate(64, 0);
        Assert.Equal(new VramRect(768, 0, 64, 512), moved.Texture.Dst);
        // 0x8b (704,0) -> (768,0) = column 12 = 0x8c; 0x9b (704,256) -> (768,256) = 0x9c.
        Assert.Contains((ushort)0x8c, moved.TpageCodes);
        Assert.Contains((ushort)0x9c, moved.TpageCodes);
        Assert.Equal(block.Texture.Pixels, moved.Texture.Pixels);   // relocation is coords-only
    }

    // ---- In-place overwrite (the Theri->0203 texture import primitive) ----

    [Fact]
    public void OverwriteInPlace_ReplacesTexelsAndPalette_HeaderAndLengthUnchanged()
    {
        var tgtTex = FakePixels(64, 512, 0x10);                  // target's own texels
        var tgtPal = FakePixels(256, 1, 0x20);
        var donTex = new byte[64 * 512 * 2]; Array.Fill(donTex, (byte)0x77); // donor: tiny compressed, fits
        var donPal = FakePixels(256, 1, 0x90);

        var target = BuildTexturePackage(
            (GianEntryType.Lzss2, 640, 0, 64, 512, Lzss.Compress(tgtTex)),
            (GianEntryType.Palette, 768, 511, 256, 1, tgtPal),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));
        var donor = BuildTexturePackage(
            (GianEntryType.Lzss2, 640, 0, 64, 512, Lzss.Compress(donTex)),
            (GianEntryType.Palette, 768, 511, 256, 1, donPal),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));

        var patched = TextureImporter.OverwriteSpeciesTextureInPlace(target, donor, new ushort[] { 0x8a, 0x9a }, 0x7ff0);

        // In-place: same length, byte-identical header table (no entry resized, nothing downstream moved).
        Assert.Equal(target.Length, patched.Length);
        Assert.Equal(target.AsSpan(0, 2048).ToArray(), patched.AsSpan(0, 2048).ToArray());
        // The model now samples the donor's texels + palette at the same coords.
        var got = TextureImporter.ExtractSpeciesTexture(patched, new ushort[] { 0x8a, 0x9a }, 0x7ff0);
        Assert.Equal(donTex, got.Texture.Pixels);
        Assert.Equal(donPal, got.Palette.Pixels);
    }

    [Fact]
    public void OverwriteInPlace_DonorStreamTooBig_Throws()
    {
        var smallTgt = new byte[64 * 512 * 2];                   // all-zero -> tiny compressed target slot
        var bigDon = FakePixels(64, 512, 0x33);                  // incompressible donor -> won't fit
        var target = BuildTexturePackage(
            (GianEntryType.Lzss2, 640, 0, 64, 512, Lzss.Compress(smallTgt)),
            (GianEntryType.Palette, 768, 511, 256, 1, FakePixels(256, 1, 1)),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));
        var donor = BuildTexturePackage(
            (GianEntryType.Lzss2, 640, 0, 64, 512, Lzss.Compress(bigDon)),
            (GianEntryType.Palette, 768, 511, 256, 1, FakePixels(256, 1, 1)),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));
        Assert.Throws<InvalidOperationException>(
            () => TextureImporter.OverwriteSpeciesTextureInPlace(target, donor, new ushort[] { 0x8a, 0x9a }, 0x7ff0));
    }

    [Fact]
    public void OverwriteInPlace_TargetLacksColumn_Throws()
    {
        // Target uploads the X=704 column; asking for the X=640 (Theri) column finds no covering entry.
        var target = BuildTexturePackage(
            (GianEntryType.Lzss2, 704, 0, 64, 512, Lzss.Compress(FakePixels(64, 512, 1))),
            (GianEntryType.Palette, 768, 511, 256, 1, FakePixels(256, 1, 1)),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));
        var donor = BuildTexturePackage(
            (GianEntryType.Lzss2, 640, 0, 64, 512, Lzss.Compress(FakePixels(64, 512, 2))),
            (GianEntryType.Palette, 768, 511, 256, 1, FakePixels(256, 1, 1)),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));
        Assert.Throws<InvalidOperationException>(
            () => TextureImporter.OverwriteSpeciesTextureInPlace(target, donor, new ushort[] { 0x8a, 0x9a }, 0x7ff0));
    }

    /// <summary>The real Theri-&gt;0203 texture step: st603 uploads the Theri texture at (640,0)+(768,511),
    /// the exact coords 0203 already uploads, so the overwrite is in-place (docs/dc1/THERI-0203-SWAP-PLAN.md).</summary>
    [Fact]
    public void RealInstall_TheriTextureFromSt603_OverwritesInto0203InPlace()
    {
        var dir = DataDir();
        if (dir is null) return; // CI without game files: no-op
        var p203 = Path.Combine(dir, "st203.dat");
        var p603 = Path.Combine(dir, "st603.dat");
        if (!File.Exists(p203) || !File.Exists(p603)) return;

        var target = File.ReadAllBytes(p203);
        var donor = File.ReadAllBytes(p603);
        ushort[] codes = { 0x8a, 0x9a };
        const ushort clut = 0x7ff0;

        var patched = TextureImporter.OverwriteSpeciesTextureInPlace(target, donor, codes, clut);

        Assert.Equal(target.Length, patched.Length);
        Assert.Equal(target.AsSpan(0, 2048).ToArray(), patched.AsSpan(0, 2048).ToArray());

        var expected = TextureImporter.ExtractSpeciesTexture(donor, codes, clut);
        var got = TextureImporter.ExtractSpeciesTexture(patched, codes, clut);
        Assert.Equal(new VramRect(640, 0, 64, 512), got.Texture.Dst);
        Assert.Equal(expected.Texture.Pixels, got.Texture.Pixels);
        Assert.Equal(expected.Palette.Pixels, got.Palette.Pixels);

        // From a pristine (own-textured) 0203 the skin actually changes; idempotent once already applied.
        var before = TextureImporter.ExtractSpeciesTexture(target, codes, clut);
        if (!before.Texture.Pixels.SequenceEqual(expected.Texture.Pixels))
            Assert.NotEqual(before.Texture.Pixels, got.Texture.Pixels);
    }

    // ---- Append (add the column to a small room that lacks it) ----

    [Fact]
    public void AppendTexture_AddsColumnAsNewEntries_RdtStaysLast_ModelSamplesDonor()
    {
        var donTex = new byte[64 * 512 * 2]; Array.Fill(donTex, (byte)0x55);
        var donPal = FakePixels(256, 1, 0x66);
        var rdtPayload = FakePixels(100, 1, 0x11);              // stand-in RDT bytes
        // Target: bg X=320 + raptor X=704 + RDT (type Unknown, last). NO X=640 column.
        var target = BuildTexturePackage(
            (GianEntryType.Lzss2, 320, 128, 192, 384, Lzss.Compress(FakePixels(192, 384, 1))),
            (GianEntryType.Lzss2, 704, 0, 64, 512, Lzss.Compress(FakePixels(64, 512, 2))),
            (GianEntryType.Unknown, 0, 0, 0, 0, rdtPayload));
        var donor = BuildTexturePackage(
            (GianEntryType.Lzss2, 640, 0, 64, 512, Lzss.Compress(donTex)),
            (GianEntryType.Palette, 768, 511, 256, 1, donPal),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));

        Assert.False(TextureImporter.HasSpeciesTextureSlot(target, new ushort[] { 0x8a, 0x9a }, 0x7ff0));
        var patched = TextureImporter.AppendSpeciesTexture(target, donor, new ushort[] { 0x8a, 0x9a }, 0x7ff0);

        Assert.True(TextureImporter.HasSpeciesTextureSlot(patched, new ushort[] { 0x8a, 0x9a }, 0x7ff0));
        var got = TextureImporter.ExtractSpeciesTexture(patched, new ushort[] { 0x8a, 0x9a }, 0x7ff0);
        Assert.Equal(donTex, got.Texture.Pixels);
        Assert.Equal(donPal, got.Palette.Pixels);

        // The RDT (type Unknown) is still the LAST entry, payload byte-identical.
        var pkg = GianPackage.TryParse(patched);
        Assert.NotNull(pkg);
        Assert.Equal(GianEntryType.Unknown, pkg!.Entries[^1].Type);
        var rdt = pkg.Entries[^1];
        Assert.Equal(rdtPayload, patched.AsSpan(rdt.PayloadOffset, (int)rdt.DeclaredSize).ToArray());
        // Two new entries added (texture + palette).
        Assert.Equal(GianPackage.TryParse(target)!.Entries.Count + 2, pkg.Entries.Count);
    }

    [Fact]
    public void AppendTexture_TargetAlreadyHasColumn_Throws()
    {
        var target = BuildTexturePackage(
            (GianEntryType.Lzss2, 640, 0, 64, 512, Lzss.Compress(FakePixels(64, 512, 1))),
            (GianEntryType.Palette, 768, 511, 256, 1, FakePixels(256, 1, 1)),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));
        var donor = target;
        Assert.Throws<InvalidOperationException>(
            () => TextureImporter.AppendSpeciesTexture(target, donor, new ushort[] { 0x8a, 0x9a }, 0x7ff0));
    }

    [Fact]
    public void AppendTexture_VramOverlap_Throws()
    {
        // Target uploads a wide texture spanning X=640 (so the donor's 640 column would collide).
        var target = BuildTexturePackage(
            (GianEntryType.Lzss2, 640, 0, 128, 512, Lzss.Compress(FakePixels(128, 512, 1))), // covers 640..768
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));
        var donor = BuildTexturePackage(
            (GianEntryType.Lzss2, 640, 0, 64, 512, Lzss.Compress(FakePixels(64, 512, 2))),
            (GianEntryType.Palette, 768, 511, 256, 1, FakePixels(256, 1, 1)),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));
        // target's 640-wide texture covers the donor tpage column, so HasSlot is true and append refuses.
        Assert.Throws<InvalidOperationException>(
            () => TextureImporter.AppendSpeciesTexture(target, donor, new ushort[] { 0x8a, 0x9a }, 0x7ff0));
    }

    // ---- Mixed: palette present, texture column absent (room 0102) ----

    [Fact]
    public void ImportTexture_PalettePresentTextureAbsent_OverwritesPalette_AppendsTexture()
    {
        var donTex = new byte[64 * 512 * 2]; Array.Fill(donTex, (byte)0x55);
        var donPal = FakePixels(256, 1, 0x66);
        var rdtPayload = FakePixels(100, 1, 0x11);
        // Target (0102-shaped): bg X=320 + raptor X=704 + the SHARED palette at (768,511) + RDT. NO X=640.
        var target = BuildTexturePackage(
            (GianEntryType.Lzss2, 320, 128, 192, 384, Lzss.Compress(FakePixels(192, 384, 1))),
            (GianEntryType.Lzss2, 704, 0, 64, 512, Lzss.Compress(FakePixels(64, 512, 2))),
            (GianEntryType.Palette, 768, 511, 256, 1, FakePixels(256, 1, 0x33)),   // a DIFFERENT palette, same coords
            (GianEntryType.Unknown, 0, 0, 0, 0, rdtPayload));
        var donor = BuildTexturePackage(
            (GianEntryType.Lzss2, 640, 0, 64, 512, Lzss.Compress(donTex)),
            (GianEntryType.Palette, 768, 511, 256, 1, donPal),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));

        // Neither all-present (no X=640) nor all-absent (palette is there) — the mixed case.
        Assert.False(TextureImporter.HasSpeciesTextureSlot(target, new ushort[] { 0x8a, 0x9a }, 0x7ff0));
        var patched = TextureImporter.ImportSpeciesTexture(target, donor, new ushort[] { 0x8a, 0x9a }, 0x7ff0);

        // Now the column is fully present and samples the DONOR texture + DONOR palette (palette overwritten).
        Assert.True(TextureImporter.HasSpeciesTextureSlot(patched, new ushort[] { 0x8a, 0x9a }, 0x7ff0));
        var got = TextureImporter.ExtractSpeciesTexture(patched, new ushort[] { 0x8a, 0x9a }, 0x7ff0);
        Assert.Equal(donTex, got.Texture.Pixels);
        Assert.Equal(donPal, got.Palette.Pixels);

        // Exactly ONE new entry (the texture); the palette was overwritten in place, not duplicated.
        var pkg = GianPackage.TryParse(patched)!;
        Assert.Equal(GianPackage.TryParse(target)!.Entries.Count + 1, pkg.Entries.Count);
        Assert.Single(pkg.Entries, e => e.Type == GianEntryType.Palette);   // palette overwritten, not duplicated
        Assert.Equal(GianEntryType.Unknown, pkg.Entries[^1].Type);      // RDT still last
        var rdt = pkg.Entries[^1];
        Assert.Equal(rdtPayload, patched.AsSpan(rdt.PayloadOffset, (int)rdt.DeclaredSize).ToArray());
    }

    /// <summary>The real small-room path: st200 has a free X=640 column (its bg is only 320..576), so the
    /// Theri texture is ADDED as new entries; the RDT stays last and the package re-parses
    /// (docs/dc1/THERI-0203-SWAP-PLAN.md). (st205's bg spans 320..672, occupying X=640 — append throws there.)</summary>
    [Fact]
    public void RealInstall_TheriTextureAppendedIntoSt200_PackageStaysValid()
    {
        var dir = DataDir();
        if (dir is null) return;
        var p200 = Path.Combine(dir, "st200.dat");
        var p603 = Path.Combine(dir, "st603.dat");
        if (!File.Exists(p200) || !File.Exists(p603)) return;

        var target = File.ReadAllBytes(p200);
        var donor = File.ReadAllBytes(p603);
        ushort[] codes = { 0x8a, 0x9a };
        const ushort clut = 0x7ff0;

        Assert.False(TextureImporter.HasSpeciesTextureSlot(target, codes, clut)); // st205 has no X=640
        var patched = TextureImporter.AppendSpeciesTexture(target, donor, codes, clut);
        Assert.True(TextureImporter.HasSpeciesTextureSlot(patched, codes, clut));

        var expected = TextureImporter.ExtractSpeciesTexture(donor, codes, clut);
        var got = TextureImporter.ExtractSpeciesTexture(patched, codes, clut);
        Assert.Equal(expected.Texture.Pixels, got.Texture.Pixels);
        Assert.Equal(expected.Palette.Pixels, got.Palette.Pixels);

        // RDT preserved (last entry, same declared size); file grew by the appended entries.
        var op = GianPackage.TryParse(target);
        var np = GianPackage.TryParse(patched);
        Assert.NotNull(op); Assert.NotNull(np);
        Assert.Equal(op!.Entries[^1].DeclaredSize, np!.Entries[^1].DeclaredSize);
        Assert.Equal(op.Entries.Count + 2, np.Entries.Count);
        Assert.True(patched.Length > target.Length);
    }

    [Fact]
    public void EngineRoomRdtCeiling_IsTheCeConfirmedValue()
        => Assert.Equal(0x60000, SpeciesImporter.EngineRoomRdtCeiling);

    [Fact]
    public void ExtractSpeciesTexture_NoCoveringBlock_Throws()
    {
        // A room whose VRAM has no texture at the model's tpage column (the 0203/DDRAW case):
        // the species cannot sample a valid page here.
        var pkg = BuildTexturePackage(
            (GianEntryType.Lzss2, 320, 0, 224, 512, Lzss.Compress(FakePixels(224, 512, 1))),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));
        Assert.Throws<InvalidOperationException>(
            () => TextureImporter.ExtractSpeciesTexture(pkg, new ushort[] { 0x8b }, 0x7ff0));
    }

    // ---- Gated real-install test (st10e raptor) ----

    private static string? DataDir()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        foreach (var c in new[] { root, Path.Combine(root, "Data") })
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "st*.dat").Any())
                return c;
        return Directory.EnumerateDirectories(root, "Data", SearchOption.AllDirectories).FirstOrDefault();
    }

    [Fact]
    public void RealInstall_ExtractRaptorTextureFromSt10e_MatchesModelCodes()
    {
        var dir = DataDir();
        if (dir is null) return; // CI without game files: no-op
        var path = Path.Combine(dir, "st10e.dat");
        if (!File.Exists(path)) return;
        var file = File.ReadAllBytes(path);

        var blocks = TextureImporter.ParseVramBlocks(file);

        // st10e uploads the raptor texture to VRAM (704,0,64,512) and its palette to (768,510).
        var tex = blocks.Single(b => b.Type == GianEntryType.Lzss2 && b.Dst.X == 704);
        Assert.Equal(new VramRect(704, 0, 64, 512), tex.Dst);
        Assert.Equal(tex.Dst.PixelBytes, tex.Pixels.Length);          // W*H*2 structure holds

        // Byte-exact round-trip: re-compress the extracted pixels and they decompress back.
        Assert.Equal(tex.Pixels, Lzss.Decompress(Lzss.Compress(tex.Pixels)));

        // Extract by the raptor's static model codes (prim_walk.py: 0x8b/0x9b, CLUT 0x7fb0).
        var block = TextureImporter.ExtractSpeciesTexture(file, new ushort[] { 0x8b, 0x9b }, 0x7fb0);
        Assert.Equal(704, block.Texture.Dst.X);
        Assert.Equal(new VramRect(768, 510, 256, 1), block.Palette.Dst); // CLUT 0x7fb0 -> (768,510)
        Assert.Equal(0x10000, block.Texture.Pixels.Length);             // 64*512*2
    }
}
