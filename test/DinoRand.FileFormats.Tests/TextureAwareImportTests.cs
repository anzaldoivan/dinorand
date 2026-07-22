using System.Buffers.Binary;
using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The general texture-aware cross-room import (docs/dc1/TEXTURE-IMPORT-VRAM.md §(d)(ii)): relocate a
/// donor's texture into a free VRAM region of the target room, rewrite the imported model's primitive
/// tpage/CLUT codes to the relocated coords, and inject the relocated texture + palette as new package
/// entries. Synthetic tests pin the pieces (multi-entry repack, split relocate, free-region pick)
/// without game files; gated tests dogfood the model-code read/rewrite and a full
/// <see cref="RoomFile.ImportSpeciesTextured(SpeciesDonor,int)"/> against the real install.
/// </summary>
public class TextureAwareImportTests
{
    // ---- synthetic package helpers ----

    private static byte[] Pkg(params (GianEntryType type, int x, int y, int w, int h, byte[] payload)[] e)
    {
        static int Align(int v) => (v + 2047) & ~2047;
        int total = 2048 + e.Sum(x => Align(x.payload.Length));
        var buf = new byte[total];
        int pos = 2048;
        for (int i = 0; i < e.Length; i++)
        {
            int h = i * 16;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(h, 4), (uint)e[i].type);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(h + 4, 4), (uint)e[i].payload.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(h + 8, 2), (ushort)e[i].x);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(h + 10, 2), (ushort)e[i].y);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(h + 12, 2), (ushort)e[i].w);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(h + 14, 2), (ushort)e[i].h);
            e[i].payload.CopyTo(buf, pos);
            pos += Align(e[i].payload.Length);
        }
        return buf;
    }

    private static byte[] Px(int w, int h, byte seed)
    {
        var p = new byte[w * h * 2];
        for (int i = 0; i < p.Length; i++) p[i] = (byte)(seed + i * 7);
        return p;
    }

    [Fact]
    public void PackageRepacker_InsertsBeforeRdt_PreservesExistingEntriesAndRdt()
    {
        var bgPx = Px(224, 64, 0x10);
        var rdtPayload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var pkg = Pkg(
            (GianEntryType.Lzss2, 320, 0, 224, 64, Lzss.Compress(bgPx)),
            (GianEntryType.Unknown, 0, 0, 0, 0, rdtPayload));   // RDT is the last entry

        var texPx = Px(64, 512, 0x33);
        var palPx = Px(256, 1, 0x44);
        var outp = PackageRepacker.InsertEntriesBeforeRdt(pkg, new[]
        {
            new PackageRepacker.NewEntry(GianEntryType.Lzss2, new VramRect(640, 0, 64, 512), Lzss.Compress(texPx)),
            new PackageRepacker.NewEntry(GianEntryType.Palette, new VramRect(768, 500, 256, 1), palPx),
        });

        var blocks = TextureImporter.ParseVramBlocks(outp);
        Assert.Equal(texPx, blocks.Single(b => b.Dst.X == 640).Pixels);                  // new texture decodes
        Assert.Equal(palPx, blocks.Single(b => b.Type == GianEntryType.Palette).Pixels);  // new palette
        Assert.Contains(blocks, b => b.Dst.X == 320);                                     // existing bg kept

        // The RDT is still the last entry and its payload is byte-identical.
        var pkgParsed = GianPackage.TryParse(outp)!;
        var rdt = pkgParsed.RoomDataEntry!.Value;
        Assert.Equal(GianEntryType.Unknown, rdt.Type);
        Assert.Equal(rdtPayload, outp[rdt.PayloadOffset..(rdt.PayloadOffset + (int)rdt.DeclaredSize)]);
    }

    [Fact]
    public void RelocateSplit_MovesTextureAndPaletteByIndependentDeltas()
    {
        var pkg = Pkg(
            (GianEntryType.Lzss2, 704, 0, 64, 512, Lzss.Compress(Px(64, 512, 0x55))),
            (GianEntryType.Palette, 768, 511, 256, 1, Px(256, 1, 0x66)),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 1 }));
        var block = TextureImporter.ExtractSpeciesTexture(pkg, new ushort[] { 0x8b, 0x9b }, 0x7ff0);

        // Texture left to X=512 (dx=-192); palette up to Y=500 (dy=-11) — independent moves.
        var moved = block.RelocateSplit(-192, 0, 0, -11);
        Assert.Equal(new VramRect(512, 0, 64, 512), moved.Texture.Dst);
        Assert.Equal(new VramRect(768, 500, 256, 1), moved.Palette.Dst);
        // 0x8b (704,0)->(512,0)=col8=0x88 ; 0x9b (704,256)->(512,256)=0x98
        Assert.Contains((ushort)0x88, moved.TpageCodes);
        Assert.Contains((ushort)0x98, moved.TpageCodes);
        Assert.Equal(TextureImporter.MakeClut(768, 500), moved.ClutCode);
        Assert.Equal(block.Texture.Pixels, moved.Texture.Pixels);   // pixels never touched
    }

    [Fact]
    public void PickFreeRegion_SkipsOccupiedColumnsAndPaletteRows()
    {
        // Target occupies the X=320 background, the X=704 enemy column, and palette row 511.
        var target = Pkg(
            (GianEntryType.Lzss2, 320, 0, 224, 512, Lzss.Compress(Px(224, 512, 1))),
            (GianEntryType.Lzss2, 704, 0, 64, 512, Lzss.Compress(Px(64, 512, 2))),
            (GianEntryType.Palette, 768, 511, 256, 1, Px(256, 1, 3)),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 1 }));
        var donorPkg = Pkg(
            (GianEntryType.Lzss2, 704, 0, 64, 512, Lzss.Compress(Px(64, 512, 0x55))),
            (GianEntryType.Palette, 768, 511, 256, 1, Px(256, 1, 0x66)),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 1 }));
        var block = TextureImporter.ExtractSpeciesTexture(donorPkg, new ushort[] { 0x8b, 0x9b }, 0x7ff0);

        var placed = TextureImporter.PickFreeRegion(target, block);

        // X=512 overlaps the 320 bg (320..544); X=704 is occupied — so the first free column is 576.
        Assert.Equal(576, placed.Texture.Dst.X);
        Assert.Equal(510, placed.Palette.Dst.Y);            // 511 occupied → first free row below
        // codes agree with the chosen rects
        Assert.Equal(576, TextureImporter.TpageOrigin(placed.TpageCodes[0]).X);
        Assert.Equal((768, 510), TextureImporter.ClutOrigin(placed.ClutCode));  // palette keeps X, row moves
    }

    [Fact]
    public void PickFreeRegion_Refuses_GlobalVramBeyondArena()
    {
        // Corpus rule (148-room survey, 2026-07-18): vanilla rooms only ever upload texture columns in
        // [512,768); x>=768 is engine-global (item-icon atlas, font glyphs). Arena full → throw
        // (geometry-only fallback), never relocate to 768+.
        var target = Pkg(
            (GianEntryType.Lzss2, 320, 0, 256, 512, Lzss.Compress(Px(256, 512, 1))),
            (GianEntryType.Lzss2, 576, 0, 64, 512, Lzss.Compress(Px(64, 512, 2))),
            (GianEntryType.Lzss2, 640, 0, 64, 512, Lzss.Compress(Px(64, 512, 3))),
            (GianEntryType.Lzss2, 704, 0, 64, 512, Lzss.Compress(Px(64, 512, 4))),
            (GianEntryType.Palette, 768, 511, 256, 1, Px(256, 1, 5)),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 1 }));
        var donorPkg = Pkg(
            (GianEntryType.Lzss2, 704, 0, 64, 512, Lzss.Compress(Px(64, 512, 0x55))),
            (GianEntryType.Palette, 768, 511, 256, 1, Px(256, 1, 0x66)),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 1 }));
        var block = TextureImporter.ExtractSpeciesTexture(donorPkg, new ushort[] { 0x8b, 0x9b }, 0x7ff0);

        Assert.Throws<InvalidOperationException>(() => TextureImporter.PickFreeRegion(target, block));
    }

    [Fact]
    public void PickFreeRegion_ReclaimableVictimRects_PlaceIntoTheVictimColumn()
    {
        // Same VRAM-crowded arena as the refusal test — but the X=704 column + palette row 511 belong
        // to the victim being replaced, so passing them as reclaimable frees exactly that slot.
        var target = Pkg(
            (GianEntryType.Lzss2, 320, 0, 256, 512, Lzss.Compress(Px(256, 512, 1))),
            (GianEntryType.Lzss2, 576, 0, 64, 512, Lzss.Compress(Px(64, 512, 2))),
            (GianEntryType.Lzss2, 640, 0, 64, 512, Lzss.Compress(Px(64, 512, 3))),
            (GianEntryType.Lzss2, 704, 0, 64, 512, Lzss.Compress(Px(64, 512, 4))),
            (GianEntryType.Palette, 768, 511, 256, 1, Px(256, 1, 5)),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 1 }));
        var donorPkg = Pkg(
            (GianEntryType.Lzss2, 704, 0, 64, 512, Lzss.Compress(Px(64, 512, 0x55))),
            (GianEntryType.Palette, 768, 511, 256, 1, Px(256, 1, 0x66)),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 1 }));
        var block = TextureImporter.ExtractSpeciesTexture(donorPkg, new ushort[] { 0x8b, 0x9b }, 0x7ff0);

        var reclaim = new[] { new VramRect(704, 0, 64, 512), new VramRect(768, 511, 256, 1) };
        var placed = TextureImporter.PickFreeRegion(target, block, reclaim);

        Assert.Equal(new VramRect(704, 0, 64, 512), placed.Texture.Dst);
        Assert.Equal(new VramRect(768, 511, 256, 1), placed.Palette.Dst);
        Assert.Equal(704, TextureImporter.TpageOrigin(placed.TpageCodes[0]).X);
        Assert.Equal((768, 511), TextureImporter.ClutOrigin(placed.ClutCode));
    }

    [Fact]
    public void PickFreeRegion_PaletteRow_StaysOnVanillaWitnessedRows()
    {
        // Vanilla palettes only ever land on rows {497,498,505..511}; 499..504 hold engine CLUTs.
        // With 505..511 occupied the next free row is 498 — not 504.
        var target = Pkg(
            (GianEntryType.Lzss2, 320, 0, 224, 512, Lzss.Compress(Px(224, 512, 1))),
            (GianEntryType.Palette, 768, 505, 256, 7, Px(256, 7, 3)),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 1 }));
        var donorPkg = Pkg(
            (GianEntryType.Lzss2, 704, 0, 64, 512, Lzss.Compress(Px(64, 512, 0x55))),
            (GianEntryType.Palette, 768, 511, 256, 1, Px(256, 1, 0x66)),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 1 }));
        var block = TextureImporter.ExtractSpeciesTexture(donorPkg, new ushort[] { 0x8b, 0x9b }, 0x7ff0);

        var placed = TextureImporter.PickFreeRegion(target, block);

        Assert.Equal(498, placed.Palette.Dst.Y);
    }

    // ---- gated real-install ----

    private static string? DataDir()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        foreach (var c in new[] { root, Path.Combine(root, "Data") })
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "st*.dat").Any())
                return c;
        return Directory.EnumerateDirectories(root, "Data", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static IEnumerable<int> Heads(RoomFile rf)
    {
        foreach (var e in rf.Enemies)
        {
            if (e.OriginalModelPtr >= SpeciesImporter.PsxBase) yield return (int)(e.OriginalModelPtr - SpeciesImporter.PsxBase);
            if (e.OriginalMotionPtr >= SpeciesImporter.PsxBase) yield return (int)(e.OriginalMotionPtr - SpeciesImporter.PsxBase);
        }
    }

    [Fact]
    public void RealInstall_ReadModelTextureCodes_AndRewrite_RoundTrip()
    {
        var dir = DataDir();
        if (dir is null) return;
        var path = Path.Combine(dir, "st400.dat");
        if (!File.Exists(path)) return;

        var rf = RoomFile.ReadFromFile(4, 0, path);
        const uint pteranodonModel = 0x80109bd8;     // donor model (tools/scd_re/species_tpage.py)

        var (tpages, clut) = TextureImporter.ReadModelTextureCodes(rf.RdtBuffer, pteranodonModel);
        Assert.Equal(new ushort[] { 0x8b, 0x9b }, tpages.OrderBy(x => x).ToArray());
        Assert.Equal((ushort)0x7ff0, clut);

        // Rewrite to a relocated code set and read it straight back.
        var buf = (byte[])rf.RdtBuffer.Clone();
        var map = new Dictionary<ushort, ushort> { [0x8b] = 0x88, [0x9b] = 0x98 };
        TextureImporter.RewriteModelCodes(buf, pteranodonModel, map, 0x7fb0);

        var (tpages2, clut2) = TextureImporter.ReadModelTextureCodes(buf, pteranodonModel);
        Assert.Equal(new ushort[] { 0x88, 0x98 }, tpages2.OrderBy(x => x).ToArray());
        Assert.Equal((ushort)0x7fb0, clut2);
    }

    [Fact]
    public void RealInstall_TexturedSwap_RelocatesPteranodonTextureIntoRaptorRoom()
    {
        var dir = DataDir();
        if (dir is null) return;
        var donorPath = Path.Combine(dir, "st400.dat");
        var targetPath = Path.Combine(dir, "st10e.dat");
        if (!File.Exists(donorPath) || !File.Exists(targetPath)) return;

        // Donor: a Pteranodon, carrying its texture.
        var drf = RoomFile.ReadFromFile(4, 0, donorPath);
        var drec = drf.Enemies.First(e => e.IsRandomizableDino && e.Species == DinoSpecies.Pteranodon);
        var donor = SpeciesImporter.ExtractDonor(drf.RdtBuffer, drec, Heads(drf))
            with { Texture = SpeciesImporter.TryExtractTexture(drf.OriginalBytes, drf.RdtBuffer, drec.OriginalModelPtr) };
        Assert.NotNull(donor.Texture);

        // Target: st10e's Velociraptor (occupies X=704), so the donor must relocate to a free column.
        var trf = RoomFile.ReadFromFile(1, 0x0e, targetPath);
        var victim = trf.Enemies.First(e => e.IsRandomizableDino);
        int idx = trf.Enemies.IndexOf(victim);
        uint importedModel;
        var res = trf.ImportSpeciesTextured(donor, idx);
        importedModel = trf.Enemies[idx].ModelPtr;

        Assert.Equal(RoomFile.TextureImportOutcome.Relocated, res.Outcome);
        Assert.NotEqual(704, res.TextureRect!.Value.X);            // not the donor's original column

        byte[] final = trf.Write();

        // 1. The written package serves the Pteranodon's own texels + palette at the relocated coords.
        var blocks = TextureImporter.ParseVramBlocks(final);
        Assert.Equal(donor.Texture!.Texture.Pixels, blocks.Single(b => b.Dst == res.TextureRect).Pixels);
        Assert.Equal(donor.Texture!.Palette.Pixels,
            blocks.Single(b => b.Type == GianEntryType.Palette && b.Dst == res.PaletteRect).Pixels);

        // 2. The imported model's primitives all carry the relocated codes (sample the new column/row).
        var (tps, clut) = TextureImporter.ReadModelTextureCodes(trf.RdtBuffer, importedModel);
        Assert.All(tps, t => Assert.Equal(res.TextureRect!.Value.X, TextureImporter.TpageOrigin(t).X));
        Assert.Equal(TextureImporter.MakeClut(res.PaletteRect!.Value.X, res.PaletteRect!.Value.Y), clut);
    }

    [Fact]
    public void RealInstall_TexturedSwap_ReclaimsVictimColumn_WhenVramIsFull()
    {
        var dir = DataDir();
        if (dir is null) return;
        var donorPath = Path.Combine(dir, "st100.dat");
        var targetPath = Path.Combine(dir, "st112.dat");
        if (!File.Exists(donorPath) || !File.Exists(targetPath)) return;

        var drf = RoomFile.ReadFromFile(1, 0, donorPath);
        var drec = drf.Enemies.First(e => e.IsRandomizableDino && e.Species == DinoSpecies.RaptorHeavy);
        var donor = SpeciesImporter.ExtractDonor(drf.RdtBuffer, drec, Heads(drf))
            with { Texture = SpeciesImporter.TryExtractTexture(drf.OriginalBytes, drf.RdtBuffer, drec.OriginalModelPtr) };
        Assert.NotNull(donor.Texture);

        // st112's VRAM has no free column (2026-07-19 census), but the victim's own texture is
        // exclusive to it — the fallback must place the donor over the victim's column instead of
        // degrading to geometry-only.
        var trf = RoomFile.ReadFromFile(1, 0x12, targetPath);
        var victim = trf.Enemies.First(e => e.IsRandomizableDino);
        var victimTex = SpeciesImporter.TryExtractTexture(trf.OriginalBytes, trf.RdtBuffer, victim.OriginalModelPtr);
        Assert.NotNull(victimTex);
        int idx = trf.Enemies.IndexOf(victim);

        var res = trf.ImportSpeciesTextured(donor, idx);
        Assert.Equal(RoomFile.TextureImportOutcome.ReclaimedVictim, res.Outcome);
        Assert.Equal(victimTex!.Texture.Dst, res.TextureRect);        // landed on the victim's column

        // The written package uploads the donor texels at that rect AFTER the victim's original entry
        // (last upload wins the DMA), with the imported model's codes pointing there.
        byte[] final = trf.Write();
        var atRect = TextureImporter.ParseVramBlocks(final).Where(b => b.Dst == res.TextureRect).ToList();
        Assert.True(atRect.Count >= 2);                               // original + staged overwrite
        Assert.Equal(donor.Texture!.Texture.Pixels, atRect[^1].Pixels);
        var (tps, clut) = TextureImporter.ReadModelTextureCodes(trf.RdtBuffer, trf.Enemies[idx].ModelPtr);
        Assert.All(tps, t => Assert.Equal(res.TextureRect!.Value.X, TextureImporter.TpageOrigin(t).X));
        Assert.Equal(TextureImporter.MakeClut(res.PaletteRect!.Value.X, res.PaletteRect!.Value.Y), clut);
    }
}
