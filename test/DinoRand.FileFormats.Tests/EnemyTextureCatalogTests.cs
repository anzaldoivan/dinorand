using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Locks the per-species enemy-texture catalog <c>data/dc1/enemy-textures.json</c> to the real
/// game files. The catalog records, for each of the 7 DC1 enemy model classes, the tpage/CLUT
/// codes its model primitives carry plus the VRAM texture/palette rects those codes name — all
/// sourced from the room-file model data, NOT DINO.exe (which holds no per-species texture table).
///
/// <para>Two layers. A files-free guard pins the catalog's shape and cross-reference invariants
/// (one row per category/bones, codes decode to the recorded rects via the PSX bit math). A gated
/// real-install test (<c>DINORAND_DC1_DIR</c>) loads each row's donor room and runs
/// <see cref="TextureImporter.ExtractSpeciesTexture"/> with the recorded codes, asserting the
/// returned texture/palette rects match the catalog and the texture LZSS round-trips byte-exactly —
/// the same dogfooding the catalog was built from (tools/scd_re/species_tpage.py).</para>
/// </summary>
public class EnemyTextureCatalogTests
{
    private sealed record TexRow(int category, int bones, string name, string donor_room,
                                 string place_op, string model_ptr,
                                 string[] tpage_codes, string clut_code,
                                 int[] texture_rect, int[] palette_rect, string note);
    private sealed record CatalogFile(TexRow[] textures);

    private static ushort Hex(string s) =>
        ushort.Parse(s.StartsWith("0x") ? s[2..] : s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static CatalogFile Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "dc1", "enemy-textures.json");
        Assert.True(File.Exists(path), $"enemy-textures.json not found at {path}");
        var file = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(file);
        Assert.NotNull(file!.textures);
        return file;
    }

    // ---- Files-free guard: shape + the PSX code<->rect math the catalog claims ----

    [Fact]
    public void Catalog_HasAllSevenClasses_WithDistinctCategoryAndBones()
    {
        var rows = Load().textures;
        Assert.Equal(7, rows.Length);
        Assert.Equal(7, rows.Select(r => r.category).ToHashSet().Count);
        Assert.Equal(7, rows.Select(r => r.bones).ToHashSet().Count);
        // The 7 catalog categories are exactly the 7 enemies.json model classes.
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 7, 8 }, rows.Select(r => r.category).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Catalog_CodesDecodeToTheRecordedRects()
    {
        foreach (var row in Load().textures)
        {
            Assert.NotEmpty(row.tpage_codes);
            Assert.Equal(4, row.texture_rect.Length);
            Assert.Equal(4, row.palette_rect.Length);

            // Every tpage's page origin lies inside the recorded texture rect (they share a column).
            var tex = new VramRect(row.texture_rect[0], row.texture_rect[1], row.texture_rect[2], row.texture_rect[3]);
            foreach (var t in row.tpage_codes)
            {
                var (px, py) = TextureImporter.TpageOrigin(Hex(t));
                Assert.True(tex.Contains(px, py),
                    $"{row.name}: tpage {t} origin ({px},{py}) not in texture rect {tex}");
            }

            // The CLUT code decodes to the recorded palette rect's (X,Y).
            var (cx, cy) = TextureImporter.ClutOrigin(Hex(row.clut_code));
            Assert.Equal(row.palette_rect[0], cx);
            Assert.Equal(row.palette_rect[1], cy);
            Assert.Equal(new[] { 256, 1 }, new[] { row.palette_rect[2], row.palette_rect[3] });
        }
    }

    // ---- Gated real-install: dogfood TextureImporter against each donor room ----

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
    public void RealInstall_EveryCatalogRow_ExtractsToTheRecordedRects()
    {
        var dir = DataDir();
        if (dir is null) return; // CI without game files: no-op

        foreach (var row in Load().textures)
        {
            var path = Path.Combine(dir, $"st{row.donor_room}.dat");
            Assert.True(File.Exists(path), $"{row.name}: donor room {path} missing");
            var file = File.ReadAllBytes(path);

            var codes = row.tpage_codes.Select(Hex).ToArray();
            var block = TextureImporter.ExtractSpeciesTexture(file, codes, Hex(row.clut_code));

            var expectTex = new VramRect(row.texture_rect[0], row.texture_rect[1], row.texture_rect[2], row.texture_rect[3]);
            var expectPal = new VramRect(row.palette_rect[0], row.palette_rect[1], row.palette_rect[2], row.palette_rect[3]);
            Assert.Equal(expectTex, block.Texture.Dst);
            Assert.Equal(expectPal, block.Palette.Dst);

            // Texture payload is W*H*2 16bpp bytes and LZSS round-trips byte-exactly.
            Assert.Equal(expectTex.PixelBytes, block.Texture.Pixels.Length);
            Assert.Equal(block.Texture.Pixels, Lzss.Decompress(Lzss.Compress(block.Texture.Pixels)));

            // Palette is a raw 256-colour 16bpp CLUT.
            Assert.Equal(256 * 2, block.Palette.Pixels.Length);
        }
    }

    // ---- Case A: swap the Pteranodon's texture into raptor-room 0102 (in-place) ----

    private readonly record struct PkgEntry(
        int Index, GianEntryType Type, int Size, int X, int Y, int W, int H, int PayloadOffset, int AlignedSize);

    /// <summary>Walk a room package's 16-byte entry table exactly as the loader /
    /// <see cref="TextureImporter.ParseVramBlocks"/> do (sector-aligned advance, stop at the
    /// first out-of-range type / zero size), returning each entry's rect + on-disk position.</summary>
    private static List<PkgEntry> Entries(byte[] file)
    {
        var list = new List<PkgEntry>();
        int pos = 2048;
        for (int i = 0; i < 2048 / 16; i++)
        {
            int hdr = i * 16;
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(hdr, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(hdr + 4, 4));
            if (type > (uint)GianEntryType.Lzss2 || size == 0) break;
            if (pos + (long)size > file.Length) break;
            int x = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(hdr + 8, 2));
            int y = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(hdr + 10, 2));
            int w = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(hdr + 12, 2));
            int h = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(hdr + 14, 2));
            int aligned = (int)((size + 2047) & ~2047u);
            list.Add(new PkgEntry(i, (GianEntryType)type, (int)size, x, y, w, h, pos, aligned));
            pos += aligned;
        }
        return list;
    }

    /// <summary>
    /// Case A (docs/dc1/TEXTURE-IMPORT-VRAM.md §(d)(i)): a Pteranodon that <i>replaces</i> the
    /// raptor in room 0102 wears the raptor skin because both models name the identical VRAM —
    /// tpage 0x8b/0x9b (X=704) + CLUT 0x7ff0 (palette 768,511) — and 0102 uploads the raptor's
    /// texels there. The fix is a pure room-file edit (no exe patch): overwrite the donor
    /// Pteranodon's texture + palette into 0102's matching entries. The donor's compressed
    /// texture (st400, 0xa426) fits 0102's existing aligned slot (0xb000), so this is an
    /// in-place overwrite — entry sizes (hence <c>align(size)</c>) are unchanged, so entry [6]
    /// and the RDT never move and no multi-entry repack is needed. No model-code rewrite either
    /// (the codes already name those coords).
    /// </summary>
    [Fact]
    public void RealInstall_CaseA_PteranodonTextureInto0102_IsInPlaceRoomFileEdit()
    {
        var dir = DataDir();
        if (dir is null) return; // CI without game files: no-op
        var p102 = Path.Combine(dir, "st102.dat");
        var p400 = Path.Combine(dir, "st400.dat");
        if (!File.Exists(p102) || !File.Exists(p400)) return;

        var orig = File.ReadAllBytes(p102);
        var donor = File.ReadAllBytes(p400);
        var e102 = Entries(orig);
        var e400 = Entries(donor);

        ushort[] codes = { 0x8b, 0x9b };
        const ushort clut = 0x7ff0;

        // The shared enemy slot: texture column X=704 and palette (768,511), in both rooms.
        var tgtTex = e102.Single(e => e.Type != GianEntryType.Palette && e.X == 704 && e.Y == 0);
        var tgtPal = e102.Single(e => e.Type == GianEntryType.Palette && e.X == 768 && e.Y == 511);
        var donTex = e400.Single(e => e.Type != GianEntryType.Palette && e.X == 704 && e.Y == 0);
        var donPal = e400.Single(e => e.Type == GianEntryType.Palette && e.X == 768 && e.Y == 511);

        // Precondition for an in-place swap: the donor stream fits the target's aligned slot, and
        // the palettes are the same raw size — so no entry resizes and nothing downstream moves.
        Assert.True(donTex.Size <= tgtTex.AlignedSize,
            $"donor tex {donTex.Size:x} exceeds 0102 slot {tgtTex.AlignedSize:x} — would need a repack");
        Assert.Equal(tgtPal.Size, donPal.Size);

        var patched = (byte[])orig.Clone();
        // Texture: donor compressed stream, then fill the rest of the *recorded* payload (kept at
        // 0102's original size) with an all-literal LZSS pad (0xFF flag = 8 literals) so a full
        // decode of the slice never back-references before the stream start.
        Array.Copy(donor, donTex.PayloadOffset, patched, tgtTex.PayloadOffset, donTex.Size);
        for (int k = tgtTex.PayloadOffset + donTex.Size; k < tgtTex.PayloadOffset + tgtTex.Size; k++)
            patched[k] = 0xFF;
        // Palette: raw copy (identical size).
        Array.Copy(donor, donPal.PayloadOffset, patched, tgtPal.PayloadOffset, donPal.Size);

        // (1) Same length and a byte-identical header table — proof this is in-place: no entry
        // grew/shrank, so align(size) is unchanged and every later payload stays put.
        Assert.Equal(orig.Length, patched.Length);
        Assert.Equal(orig.AsSpan(0, 2048).ToArray(), patched.AsSpan(0, 2048).ToArray());

        // (2) The RDT (room script, last entry) is byte-identical — we only touched texture VRAM.
        var rdt = e102[^1];
        Assert.Equal(orig.AsSpan(rdt.PayloadOffset, rdt.Size).ToArray(),
                     patched.AsSpan(rdt.PayloadOffset, rdt.Size).ToArray());

        // (3) Patched 0102 now serves the Pteranodon's own texels + palette at the model's coords,
        // byte-exact against the donor room — so the model samples Pteranodon, not raptor, pixels.
        var expected = TextureImporter.ExtractSpeciesTexture(donor, codes, clut);
        var got = TextureImporter.ExtractSpeciesTexture(patched, codes, clut);
        Assert.Equal(new VramRect(704, 0, 64, 512), got.Texture.Dst);
        Assert.Equal(new VramRect(768, 511, 256, 1), got.Palette.Dst);
        Assert.Equal(expected.Texture.Pixels, got.Texture.Pixels);
        Assert.Equal(expected.Palette.Pixels, got.Palette.Pixels);

        // (4) From a vanilla (raptor-textured) 0102 the overlay changes the skin; once the install
        // has already been Pteranodon-textured the overlay is idempotent (so only assert the change
        // when the starting texels differ — this test reads the live, possibly-already-patched file).
        var before = TextureImporter.ExtractSpeciesTexture(orig, codes, clut);
        if (!before.Texture.Pixels.SequenceEqual(expected.Texture.Pixels))
            Assert.NotEqual(before.Texture.Pixels, got.Texture.Pixels);
    }
}
