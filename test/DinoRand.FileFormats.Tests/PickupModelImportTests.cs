using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// DC1 Lever B (PICKUP-GROUND-MODEL-FEASIBILITY.md "Lever B plan", STATIC-SCD-RE cont.73):
/// a relocated key/weapon gets its own donor ground mesh imported into the destination room —
/// mesh blob appended to the RDT, texture UV sub-rect + CLUT row re-uploaded at a free VRAM
/// column, texrefs rewritten — with fail-closed fallback to the Lever-A generic panel.
/// </summary>
public class PickupModelImportTests
{
    private const uint PsxBase = RoomScript.PsxRdtBase;

    private static void WriteU16(byte[] b, int off, ushort v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
    }

    private static void WriteU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
        b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
    }

    private static uint ReadU32(byte[] b, int off)
        => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

    // --- synthetic mesh builder (cont.73 layout) ---------------------------------------------------

    /// <summary>One 40-byte textured gouraud tri prim: 3×{s16 x,y,z; u8 u,v} + {tpage, clut} + 3 colors
    /// (color-0 code 0x34).</summary>
    private static byte[] TriPrim(ushort tpage, ushort clut, (byte u, byte v)[] uv)
    {
        var p = new byte[PickupMeshFormat.TriPrimSize];
        for (int j = 0; j < 3; j++) { p[j * 8 + 6] = uv[j].u; p[j * 8 + 7] = uv[j].v; }
        WriteU16(p, 0x18, tpage); WriteU16(p, 0x1a, clut);
        p[0x1f] = 0x34;
        return p;
    }

    /// <summary>One 52-byte textured gouraud quad prim (color-0 code 0x3c, or 0x3e semi-transparent).</summary>
    private static byte[] QuadPrim(ushort tpage, ushort clut, (byte u, byte v)[] uv, byte code = 0x3c)
    {
        var p = new byte[PickupMeshFormat.QuadPrimSize];
        for (int j = 0; j < 4; j++) { p[j * 8 + 6] = uv[j].u; p[j * 8 + 7] = uv[j].v; }
        WriteU16(p, 0x20, tpage); WriteU16(p, 0x22, clut);
        p[0x27] = code;
        return p;
    }

    /// <summary>Assemble a pickup ground mesh at <paramref name="offset"/> inside a buffer:
    /// header {triPtr, quadPtr, u16 triCount, u16 quadCount} + contiguous tri then quad prims.</summary>
    private static byte[] BuildMesh(int offset, byte[][] tris, byte[][] quads)
    {
        int size = PickupMeshFormat.HeaderSize
                 + tris.Length * PickupMeshFormat.TriPrimSize
                 + quads.Length * PickupMeshFormat.QuadPrimSize;
        var buf = new byte[offset + size];
        uint triPtr = PsxBase + (uint)offset + PickupMeshFormat.HeaderSize;
        uint quadPtr = triPtr + (uint)(tris.Length * PickupMeshFormat.TriPrimSize);
        WriteU32(buf, offset, triPtr);
        WriteU32(buf, offset + 4, quadPtr);
        WriteU16(buf, offset + 8, (ushort)tris.Length);
        WriteU16(buf, offset + 0xa, (ushort)quads.Length);
        int pos = offset + PickupMeshFormat.HeaderSize;
        foreach (var t in tris) { t.CopyTo(buf, pos); pos += t.Length; }
        foreach (var q in quads) { q.CopyTo(buf, pos); pos += q.Length; }
        return buf;
    }

    private static readonly (byte, byte)[] UvTri = { ((byte)128, (byte)128), ((byte)159, (byte)128), ((byte)128, (byte)143) };
    private static readonly (byte, byte)[] UvQuad = { ((byte)128, (byte)128), ((byte)159, (byte)128), ((byte)128, (byte)143), ((byte)159, (byte)143) };

    // --- PickupMeshFormat: parse / extract / retarget ----------------------------------------------

    [Fact]
    public void TryParse_RoundTripsSyntheticTriQuadMesh()
    {
        const ushort tpage = 0x0007, clut = 0x7ef6;
        var rdt = BuildMesh(0x40,
            new[] { TriPrim(tpage, clut, new[] { ((byte)10, (byte)20), ((byte)30, (byte)20), ((byte)10, (byte)40) }) },
            new[] { QuadPrim(tpage, clut, UvQuad), QuadPrim(tpage, clut, UvQuad, code: 0x3e) });

        Assert.True(PickupMeshFormat.TryParse(rdt, 0x40, out var mesh));
        Assert.Equal(1, mesh!.TriCount);
        Assert.Equal(2, mesh.QuadCount);
        Assert.Equal(PickupMeshFormat.HeaderSize + 40 + 2 * 52, mesh.Size);

        var tr = Assert.Single(mesh.Texrefs);
        Assert.Equal(tpage, tr.Tpage);
        Assert.Equal(clut, tr.Clut);
        Assert.Equal(10, tr.UMin); Assert.Equal(159, tr.UMax);
        Assert.Equal(20, tr.VMin); Assert.Equal(143, tr.VMax);
    }

    [Fact]
    public void TryParse_RejectsBadCodeByteAndBadHeader()
    {
        var bad = BuildMesh(0, Array.Empty<byte[]>(), new[] { QuadPrim(7, 0x7ef6, UvQuad, code: 0x77) });
        Assert.False(PickupMeshFormat.TryParse(bad, 0, out _));

        var mesh = BuildMesh(0, Array.Empty<byte[]>(), new[] { QuadPrim(7, 0x7ef6, UvQuad) });
        WriteU32(mesh, 0, PsxBase + 0x999);      // triPtr no longer header+0xC
        Assert.False(PickupMeshFormat.TryParse(mesh, 0, out _));

        Assert.False(PickupMeshFormat.TryParse(new byte[8], 0, out _)); // undersized
    }

    [Fact]
    public void ExtractAndRetarget_RebasesHeaderAndRewritesTexrefsOnly()
    {
        const ushort tpage = 0x0007, clut = 0x7ef6, newTpage = 0x0009, newClut = 0x7ff6;
        var rdt = BuildMesh(0x40,
            new[] { TriPrim(tpage, clut, new[] { ((byte)1, (byte)2), ((byte)3, (byte)4), ((byte)5, (byte)6) }) },
            new[] { QuadPrim(tpage, clut, UvQuad) });

        var blob = PickupMeshFormat.ExtractBlob(rdt, 0x40);
        Assert.Equal(PickupMeshFormat.HeaderSize + 40 + 52, blob.Length);

        const uint newPtr = PsxBase + 0x2000;
        PickupMeshFormat.RebaseAndRetarget(blob, newPtr,
            new Dictionary<(ushort, ushort), (ushort, ushort)> { [(tpage, clut)] = (newTpage, newClut) });

        // Header ptrs rebased to the new location.
        Assert.Equal(newPtr + PickupMeshFormat.HeaderSize, ReadU32(blob, 0));
        Assert.Equal(newPtr + PickupMeshFormat.HeaderSize + 40, ReadU32(blob, 4));

        // Blob re-parses at its new home with the new texref; geometry bytes untouched.
        var home = new byte[0x2000 + blob.Length];
        blob.CopyTo(home, 0x2000);
        Assert.True(PickupMeshFormat.TryParse(home, 0x2000, out var mesh));
        var tr = Assert.Single(mesh!.Texrefs);
        Assert.Equal(newTpage, tr.Tpage);
        Assert.Equal(newClut, tr.Clut);
        Assert.Equal(2, tr.VMin);       // UVs untouched
        Assert.Equal(143, tr.VMax);
    }

    // --- PickupModelImporter: texture sub-rect extract + free-VRAM placement -----------------------

    /// <summary>Minimal package: 2048-byte entry table + [type-8 LZSS texture] + [type-2 palette] +
    /// [type-0 RDT last]. Rects in the entry header words (dstX,dstY,W,H).</summary>
    private static byte[] BuildPackage(params (GianEntryType Type, VramRect Dst, byte[] Payload)[] entries)
    {
        const int header = 2048, sector = 2048;
        int total = header + entries.Sum(e => (e.Payload.Length + sector - 1) & ~(sector - 1));
        var buf = new byte[total];
        int pos = header;
        for (int i = 0; i < entries.Length; i++)
        {
            var (type, dst, payload) = entries[i];
            WriteU32(buf, i * 16, (uint)type);
            WriteU32(buf, i * 16 + 4, (uint)payload.Length);
            WriteU16(buf, i * 16 + 8, (ushort)dst.X);
            WriteU16(buf, i * 16 + 10, (ushort)dst.Y);
            WriteU16(buf, i * 16 + 12, (ushort)dst.W);
            WriteU16(buf, i * 16 + 14, (ushort)dst.H);
            payload.CopyTo(buf, pos);
            pos += (payload.Length + sector - 1) & ~(sector - 1);
        }
        return buf;
    }

    private static byte[] PatternPixels(int w, int h)
    {
        var px = new byte[w * h * 2];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)(i * 31);
        return px;
    }

    [Fact]
    public void TryExtractTexture_CutsUvSubRectAndClutRow()
    {
        // Donor uploads a 96x256 atlas at (448,0) and a 16-halfword CLUT at (864,507).
        var atlas = PatternPixels(96, 256);
        var clutPx = PatternPixels(16, 1);
        var donor = BuildPackage(
            (GianEntryType.Lzss2, new VramRect(448, 0, 96, 256), Lzss.Compress(atlas)),
            (GianEntryType.Palette, new VramRect(864, 507, 16, 1), clutPx),
            (GianEntryType.Data, default, new byte[4]));

        // 4bpp tpage at (448,0); UV box u[128..159] v[128..143] → halfwords x=480 w=8, padded to the
        // engine's 32-halfword blit row unit (CE witness 2026-07-17), y=128 h=16.
        var texref = new PickupTexref(0x0007, TextureImporter.MakeClut(864, 507), 128, 159, 128, 143);
        Assert.True(PickupModelImporter.TryExtractTexture(donor, texref, out var cut));

        Assert.Equal(new VramRect(480, 128, PickupModelImporter.BlitRowHalfwords, 16), cut!.TexRect);
        Assert.Equal(new VramRect(864, 507, 16, 1), cut.ClutRect);
        Assert.Equal(clutPx, cut.ClutPixels);
        Assert.Equal(32 * 16 * 2, cut.TexPixels.Length);
        // Row slice: expected row r = atlas[( (128+r)*96 + 32 )*2 ..]; the atlas covers the whole
        // padded rect (480..511 < 448+96), so pad columns carry donor pixels too.
        for (int r = 0; r < 16; r++)
            for (int b = 0; b < 64; b++)
                Assert.Equal(atlas[((128 + r) * 96 + 32) * 2 + b], cut.TexPixels[r * 64 + b]);
    }

    [Fact]
    public void TryExtractTexture_WidthPadding_ZeroFillsBeyondDonorCoverage()
    {
        // Narrow atlas (448,0,40,256) covers halfwords 448..487: the sampled rect 480..487 fits
        // exactly, but the padded rect extends to 511 — those columns must come back zero-filled.
        var atlas = PatternPixels(40, 256);
        var donor = BuildPackage(
            (GianEntryType.Lzss2, new VramRect(448, 0, 40, 256), Lzss.Compress(atlas)),
            (GianEntryType.Palette, new VramRect(864, 507, 16, 1), PatternPixels(16, 1)),
            (GianEntryType.Data, default, new byte[4]));

        var texref = new PickupTexref(0x0007, TextureImporter.MakeClut(864, 507), 128, 159, 128, 143);
        Assert.True(PickupModelImporter.TryExtractTexture(donor, texref, out var cut));

        Assert.Equal(new VramRect(480, 128, 32, 16), cut!.TexRect);
        for (int r = 0; r < 16; r++)
        {
            for (int b = 0; b < 16; b++)   // sampled span: donor pixels
                Assert.Equal(atlas[((128 + r) * 40 + 32) * 2 + b], cut.TexPixels[r * 64 + b]);
            for (int b = 16; b < 64; b++)  // pad beyond the donor block: zero
                Assert.Equal(0, cut.TexPixels[r * 64 + b]);
        }
    }

    [Fact]
    public void TryExtractTexture_AlignedWidthIsUnchanged()
    {
        // UV box u[0..127] at 4bpp = exactly 32 halfwords — already blit-row aligned, no pad.
        var atlas = PatternPixels(96, 256);
        var donor = BuildPackage(
            (GianEntryType.Lzss2, new VramRect(448, 0, 96, 256), Lzss.Compress(atlas)),
            (GianEntryType.Palette, new VramRect(864, 507, 16, 1), PatternPixels(16, 1)),
            (GianEntryType.Data, default, new byte[4]));

        var texref = new PickupTexref(0x0007, TextureImporter.MakeClut(864, 507), 0, 127, 0, 15);
        Assert.True(PickupModelImporter.TryExtractTexture(donor, texref, out var cut));
        Assert.Equal(new VramRect(448, 0, 32, 16), cut!.TexRect);
    }

    [Fact]
    public void TryPlace_PicksFreeColumnKeepingIntraPageOffsets()
    {
        // Target occupies its atlas at (448,0..96x256), the 512 column (512,0,64,256), and
        // palette rows 505..507 at x=768/864; rows 508+ free; columns 576+ free.
        var target = BuildPackage(
            (GianEntryType.Lzss2, new VramRect(448, 0, 96, 256), Lzss.Compress(PatternPixels(96, 256))),
            (GianEntryType.Lzss2, new VramRect(512, 0, 64, 256), Lzss.Compress(PatternPixels(64, 256))),
            (GianEntryType.Palette, new VramRect(864, 507, 16, 1), PatternPixels(16, 1)),
            (GianEntryType.Data, default, new byte[4]));

        var cut = new PickupTextureCut(0x0007, TextureImporter.MakeClut(864, 507),
                                       new VramRect(480, 128, 8, 16), PatternPixels(8, 16),
                                       new VramRect(864, 507, 16, 1), PatternPixels(16, 1));

        Assert.True(PickupModelImporter.TryPlace(target, Array.Empty<VramRect>(), cut, out var placed));

        // 512 column occupied → 576; intra-page x offset (480-448=32) preserved; y untouched.
        Assert.Equal(new VramRect(576 + 32, 128, 8, 16), placed!.TexRect);
        Assert.Equal(TextureImporter.MakeTpage(576, 0, 0), placed.Tpage);
        // CLUT keeps its X, moves to a free row scanned from the bottom.
        Assert.Equal(864, placed.ClutRect.X);
        Assert.NotEqual(507, placed.ClutRect.Y);
        Assert.Equal(TextureImporter.MakeClut(placed.ClutRect.X, placed.ClutRect.Y), placed.Clut);

        // A rect staged by an earlier import in the same room blocks its column.
        var staged = new[] { new VramRect(576, 0, 64, 256) };
        Assert.True(PickupModelImporter.TryPlace(target, staged, cut, out var placed2));
        Assert.Equal(640 + 32, placed2!.TexRect.X);
    }

    [Fact]
    public void TryPlace_FailsClosed_WhenRoomArenaFull()
    {
        // Corpus rule (148-room survey, 2026-07-18): room packages only ever upload texture columns
        // inside [512,768). x>=768 holds engine-global VRAM — the item-icon atlas at (768,0) and the
        // font glyphs — which seed DINO-WxCfArn_H_8vBw garbled by parking a pickup texture there
        // (st10d: atlas 320..576 + species columns 576/640/704 fill the whole arena). A full arena
        // must fail closed (Lever-A generic panel), never spill past 768.
        var target = BuildPackage(
            (GianEntryType.Lzss2, new VramRect(320, 0, 256, 512), Lzss.Compress(PatternPixels(256, 512))),
            (GianEntryType.Lzss2, new VramRect(576, 0, 64, 512), Lzss.Compress(PatternPixels(64, 512))),
            (GianEntryType.Lzss2, new VramRect(640, 0, 64, 512), Lzss.Compress(PatternPixels(64, 512))),
            (GianEntryType.Lzss2, new VramRect(704, 0, 64, 512), Lzss.Compress(PatternPixels(64, 512))),
            (GianEntryType.Palette, new VramRect(768, 505, 256, 4), PatternPixels(256, 4)),
            (GianEntryType.Data, default, new byte[4]));

        var cut = new PickupTextureCut(TextureImporter.MakeTpage(512, 0, 0), TextureImporter.MakeClut(768, 511),
                                       new VramRect(512, 0, 32, 128), PatternPixels(32, 128),
                                       new VramRect(768, 511, 16, 1), PatternPixels(16, 1));

        Assert.False(PickupModelImporter.TryPlace(target, Array.Empty<VramRect>(), cut, out _));
    }

    [Fact]
    public void TryPlace_ClutRow_StaysOnVanillaWitnessedRows()
    {
        // Vanilla palette uploads only ever land on rows {497,498,505..511}; 499..504 hold engine
        // CLUTs (the bad seed parked one at (768,504) and trashed the UI). With 505..511 taken the
        // next candidate is 498 — not 504.
        var target = BuildPackage(
            (GianEntryType.Lzss2, new VramRect(320, 0, 192, 512), Lzss.Compress(PatternPixels(192, 512))),
            (GianEntryType.Palette, new VramRect(768, 505, 256, 7), PatternPixels(256, 7)),
            (GianEntryType.Data, default, new byte[4]));

        var cut = new PickupTextureCut(TextureImporter.MakeTpage(512, 0, 0), TextureImporter.MakeClut(864, 507),
                                       new VramRect(512, 0, 8, 16), PatternPixels(8, 16),
                                       new VramRect(864, 507, 16, 1), PatternPixels(16, 1));

        Assert.True(PickupModelImporter.TryPlace(target, Array.Empty<VramRect>(), cut, out var placed));
        Assert.Equal(498, placed!.ClutRect.Y);
    }

    // --- RoomScript.ApplyEdits: the VisualModelPtr write (Lever B repoint) -------------------------

    [Fact]
    public void ApplyEdits_WritesVisualModelPtr_WhenSet()
    {
        // Reuse the RoomScriptTests synthetic layout: header + one-entry func table + one item record.
        const int headerLen = 0x24, tableBytes = 4;
        var rec = new byte[DcOpcodes.ItemLength];
        rec[0] = DcOpcodes.Item; rec[2] = DcOpcodes.ItemSubtype;
        rec[ItemRecord.IdOffset] = 0x16;
        rec[ItemRecord.DisplaySlotOffset] = ItemRecord.NoDisplaySlot;
        var buf = new byte[headerLen + tableBytes + rec.Length];
        WriteU32(buf, 0x14, PsxBase + headerLen);
        WriteU32(buf, headerLen, tableBytes);
        rec.CopyTo(buf, headerLen + tableBytes);

        var script = RoomScript.Parse(buf);
        var item = Assert.Single(script.Items);

        const uint meshPtr = PsxBase + 0x1234;
        item.ItemId = 0x2e;
        item.NormalizeVisual = true;
        item.NormalizeDisplaySlot = 0x05;
        item.VisualModelPtr = meshPtr;

        var edited = (byte[])buf.Clone();
        script.ApplyEdits(edited, script.Items);
        Assert.Equal(0x05, edited[item.FileOffset + ItemRecord.DisplaySlotOffset]);
        Assert.Equal(meshPtr, ReadU32(edited, item.FileOffset + ItemRecord.ModelPtrOffset));

        // Default stays the Lever-A generic panel.
        var item2 = RoomScript.Parse(buf).Items.Single();
        item2.NormalizeVisual = true;
        item2.NormalizeDisplaySlot = 0x05;
        var edited2 = (byte[])buf.Clone();
        RoomScript.Parse(buf).ApplyEdits(edited2, new[] { item2 });
        Assert.Equal(ItemRecord.GenericPanelModelPtr, ReadU32(edited2, item2.FileOffset + ItemRecord.ModelPtrOffset));
    }

    // --- real install ------------------------------------------------------------------------------

    private static readonly GameDefinition Game = new DinoCrisis1();

    private static List<RoomFile>? LoadInstall()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        var refs = Game.EnumerateRooms(root);
        if (refs.Count == 0) return null;
        return refs.Select(r => RoomFile.ReadFromFile(r.Stage, r.Room, r.Path)).ToList();
    }

    /// <summary>The donor census (cont.73): 43 relocatable ids have a bespoke donor mesh.</summary>
    private static readonly int[] ExpectedDonorIds =
    {
        0x01, 0x09, 0x0c, 0x0d, 0x0e, 0x0f,
        0x2b, 0x2e, 0x2f, 0x30, 0x31, 0x32, 0x34, 0x38, 0x39, 0x3c, 0x3e, 0x41, 0x43, 0x45,
        0x49, 0x4a, 0x4b, 0x4e, 0x4f, 0x50, 0x52, 0x53, 0x56, 0x57,
        0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x6c, 0x6d, 0x6e, 0x6f,
    };

    [Fact]
    public void RealInstall_DonorCatalog_MatchesTheCensus()
    {
        var rooms = LoadInstall();
        if (rooms is null) return; // no game files (CI) — skip

        var relocatable = new HashSet<int>(Game.KeyItemIds);
        relocatable.UnionWith(Game.WeaponIds);
        relocatable.UnionWith(Game.WeaponPartIds);

        var catalog = PickupDonorCatalog.Build(rooms, relocatable);
        Assert.Equal(ExpectedDonorIds.OrderBy(i => i), catalog.Keys.OrderBy(i => i));

        // Preference rule: an id with a solo mesh must not be served by a shared pile —
        // the Grenade Gun's donor is its solo st402 mesh, not the st60d/st610 pile.
        var gg = catalog[0x09];
        Assert.Equal(0x0402, gg.RoomCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(2026)]
    public void RealInstall_ImportPickupModels_DonorMeshTravelsOrFallsBack(int seed)
    {
        var rooms = LoadInstall();
        if (rooms is null) return; // no game files (CI) — skip

        var graph = RoomGraph.Build(rooms, Game.Requirements);
        var config = new RandomizerConfig
        {
            RandomizeItems = false,
            ShuffleKeyItems = true,
            ShuffleKeyItemsIntoPickups = true,
            RelocateDdkDiscs = true,
            NormalizePickupVisuals = true,
            ImportPickupModels = true,
        };
        var ctx = new RandomizationContext(Game, rooms, graph, new Seed(seed), config, _ => { });
        new ProgressionPass().Apply(ctx);
        new NormalizePickupVisualsPass().Apply(ctx);
        new PickupModelImportPass().Apply(ctx);

        var donorIds = new HashSet<int>(ExpectedDonorIds);
        int imported = 0, fellBack = 0;
        foreach (var room in rooms)
        {
            var pending = room.Items.Where(i => i.NormalizeVisual).ToList();
            if (pending.Count == 0) continue;

            var written = room.Write();
            var reread = RoomFile.Read(room.Stage, room.Room, written);
            var blocks = TextureImporter.ParseVramBlocks(written);

            foreach (var it in pending)
            {
                var rec = reread.Items.Single(i => i.FileOffset == it.FileOffset);
                uint ptr = ReadU32(rec.Raw, ItemRecord.ModelPtrOffset);
                Assert.Equal(it.VisualModelPtr, ptr);

                if (!donorIds.Contains(it.ItemId))
                {
                    // No donor anywhere → permanent Lever-A fallback.
                    Assert.Equal(ItemRecord.GenericPanelModelPtr, ptr);
                    fellBack++;
                    continue;
                }
                if (ptr == ItemRecord.GenericPanelModelPtr) { fellBack++; continue; } // fail-closed path

                imported++;
                // The repointed mesh parses at its appended home in the WRITTEN room...
                int off = (int)(ptr - PsxBase);
                Assert.True(PickupMeshFormat.TryParse(reread.RdtBuffer, off, out var mesh),
                    $"seed {seed}: room {room.Stage:X2}{room.Room:X2} id 0x{it.ItemId:x2}: appended mesh unparsable");
                // ...and every texref samples VRAM the written package now uploads.
                foreach (var tr in mesh!.Texrefs)
                {
                    var (px, py) = TextureImporter.TpageOrigin(tr.Tpage);
                    int div = ((tr.Tpage >> 7) & 3) == 0 ? 4 : 2;
                    var (cx, cy) = TextureImporter.ClutOrigin(tr.Clut);
                    var texBlock = blocks.FirstOrDefault(b => b.Type != GianEntryType.Palette
                                                && b.Dst.Contains(px + tr.UMin / div, py + tr.VMin));
                    Assert.True(texBlock is not null,
                        $"seed {seed}: texref tpage {tr.Tpage:x} not covered by an upload");
                    // The whole sampled bbox is inside the upload, and the entry is blit-row aligned
                    // (engine type-8 blit row unit = 64 bytes, CE witness 2026-07-17).
                    Assert.True(texBlock!.Dst.Contains(px + tr.UMax / div, py + tr.VMax),
                        $"seed {seed}: texref tpage {tr.Tpage:x} bbox exceeds its upload rect");
                    Assert.True(texBlock.Dst.W % PickupModelImporter.BlitRowHalfwords == 0,
                        $"seed {seed}: upload W={texBlock.Dst.W} not a multiple of the blit row unit");
                    // Imports never leave the room-class VRAM arena (x>=768 is the engine's
                    // icon/font atlas; rows 499..504 its CLUTs — the DINO-WxCfArn_H_8vBw garble).
                    Assert.True(texBlock.Dst.X >= TextureImporter.RoomArenaX
                             && texBlock.Dst.X + texBlock.Dst.W <= TextureImporter.RoomArenaEnd,
                        $"seed {seed}: import upload {texBlock.Dst} outside the room VRAM arena");
                    Assert.Contains(cy, TextureImporter.RoomClutRows);
                    Assert.True(blocks.Any(b => b.Type == GianEntryType.Palette && b.Dst.Contains(cx, cy)),
                        $"seed {seed}: texref clut {tr.Clut:x} not covered by a palette upload");
                }
            }
        }
        Assert.True(imported > 0, $"seed {seed}: no donor import happened at all ({fellBack} fallbacks)");
    }

    [Fact]
    public void RealInstall_TryPlace_FailsClosed_St10d_FullArena()
    {
        // Direct repro of the DINO-WxCfArn_H_8vBw garble: pristine st10d's atlas (320,0,256,512) plus
        // species columns 576/640/704 occupy the entire room VRAM arena — a pickup import there must
        // fail closed, not land at (768,0) on top of the engine's item-icon atlas.
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return; // no game files (CI) — skip
        var refs = Game.EnumerateRooms(root);
        if (refs.Count == 0) return;
        var st10d = refs.Single(r => r.Stage == 1 && r.Room == 0x0d);
        var target = File.ReadAllBytes(st10d.Path);

        var cut = new PickupTextureCut(TextureImporter.MakeTpage(512, 0, 0), TextureImporter.MakeClut(768, 511),
                                       new VramRect(512, 0, 32, 128), PatternPixels(32, 128),
                                       new VramRect(768, 511, 16, 1), PatternPixels(16, 1));

        Assert.False(PickupModelImporter.TryPlace(target, Array.Empty<VramRect>(), cut, out var placed),
            $"pickup import must fail closed in st10d, but landed at {placed?.TexRect}");
    }

    // --- design correction (2026-07-17): visual always matches the landed item ----------------------

    [Theory]
    // Own model whenever the landed id has a donor and Lever B is on, regardless of spot class.
    [InlineData(PickupVisual.GenericPanel, false, true, true, " (shows its own model)")]
    [InlineData(PickupVisual.BespokeMesh, true, true, true, " (shows its own model)")]
    [InlineData(PickupVisual.InteractionOnly, true, true, true, " (shows its own model)")]
    // No donor (or Lever B off) on a mismatched spot → generic panel note.
    [InlineData(PickupVisual.BespokeMesh, true, true, false, " (shown as generic pickup)")]
    [InlineData(PickupVisual.BespokeMesh, false, true, false, " (shown as generic pickup)")]
    [InlineData(PickupVisual.InteractionOnly, true, false, true, " (shown as generic pickup)")]
    // Generic spot without an upgrade → no note (it already shows a generic panel).
    [InlineData(PickupVisual.GenericPanel, true, false, true, "")]
    [InlineData(PickupVisual.GenericPanel, true, true, false, "")]
    // Both levers off → the vanilla warnings.
    [InlineData(PickupVisual.InteractionOnly, false, false, false, " (hidden — examine the spot)")]
    [InlineData(PickupVisual.BespokeMesh, false, false, false, " (appears as Med. Pak M)")]
    public void VisualNote_IsDonorAware(PickupVisual spot, bool normalizeOn, bool importOn,
                                        bool hasDonor, string expected)
        => Assert.Equal(expected, ProgressionPass.VisualNote(spot, normalizeOn, importOn, hasDonor, "Med. Pak M"));

    [Fact]
    public void RealInstall_VisualAlwaysMatchesLandedItem()
    {
        if (LoadInstall() is null) return; // no game files (CI) — skip

        var relocatable = new HashSet<int>(Game.KeyItemIds);
        relocatable.UnionWith(Game.WeaponIds);
        relocatable.UnionWith(Game.WeaponPartIds);

        bool sawDonorOnGenericImported = false, sawConsumableOnBespoke = false;
        int consumableOnHidden = 0;

        for (int seed = 1; seed <= 12; seed++)
        {
            var rooms = LoadInstall()!;
            var graph = RoomGraph.Build(rooms, Game.Requirements);
            var catalog = PickupDonorCatalog.Build(rooms, relocatable);
            var config = new RandomizerConfig
            {
                RandomizeItems = true,
                ReplaceItemPool = true,
                ShuffleKeyItems = true,
                ShuffleKeyItemsIntoPickups = true,
                RelocateDdkDiscs = true,
                NormalizePickupVisuals = true,
                ImportPickupModels = true,
            };
            var ctx = new RandomizationContext(Game, rooms, graph, new Seed(seed), config, _ => { });
            new ProgressionPass().Apply(ctx);
            new ItemRandomizer().Apply(ctx);
            new NormalizePickupVisualsPass().Apply(ctx);
            new PickupModelImportPass().Apply(ctx);

            var visualOf = new Dictionary<ItemRecord, PickupVisual>();
            foreach (var node in graph.Nodes)
                foreach (var ni in node.Items)
                    visualOf[ni.Record] = ni.Visual;

            foreach (var room in rooms)
            {
                if (room.Script is not { ParsedCleanly: true }) continue;
                foreach (var it in room.Items)
                {
                    if (it.IsEmptySlot || it.FileOffset < 0 || it.ItemId == it.OriginalItemId) continue;
                    if (it.Raw.Length > 0x2B && (it.Raw[0x28] | it.Raw[0x29] | it.Raw[0x2a] | it.Raw[0x2b]) != 0)
                        continue; // deferred-visual records are never normalized
                    var visual = visualOf.GetValueOrDefault(it, PickupVisual.GenericPanel);

                    if (relocatable.Contains(it.ItemId) && catalog.ContainsKey(it.ItemId)
                        && visual is PickupVisual.GenericPanel or PickupVisual.BespokeMesh)
                    {
                        // Invariant: a donor-id landing on any slot-bearing spot is always marked.
                        Assert.True(it.NormalizeVisual,
                            $"seed {seed}: donor id 0x{it.ItemId:x2} on {visual} spot not marked");
                        if (visual == PickupVisual.GenericPanel
                            && it.VisualModelPtr != ItemRecord.GenericPanelModelPtr)
                            sawDonorOnGenericImported = true;
                    }

                    if (!relocatable.Contains(it.ItemId) && visual == PickupVisual.BespokeMesh)
                    {
                        // A consumable must never keep the old key/weapon mesh.
                        Assert.True(it.NormalizeVisual,
                            $"seed {seed}: consumable 0x{it.ItemId:x2} on bespoke spot not normalized");
                        Assert.Equal(ItemRecord.GenericPanelModelPtr, it.VisualModelPtr);
                        sawConsumableOnBespoke = true;
                    }

                    if (!relocatable.Contains(it.ItemId) && visual == PickupVisual.InteractionOnly)
                    {
                        // User decision: hidden-spot consumables stay vanilla-invisible.
                        Assert.False(it.NormalizeVisual,
                            $"seed {seed}: consumable 0x{it.ItemId:x2} on hidden spot was marked");
                        consumableOnHidden++;
                    }
                }
            }
            if (sawDonorOnGenericImported && sawConsumableOnBespoke && consumableOnHidden > 0) break;
        }

        Assert.True(sawDonorOnGenericImported, "no donor key/weapon on a generic-panel spot got its own model in 12 seeds");
        Assert.True(sawConsumableOnBespoke, "no consumable landed on a bespoke spot in 12 seeds");
    }

    [Fact]
    public void ImportPass_Gating()
    {
        // Default ON for the in-game witness session (user decision 2026-07-17); the flag remains
        // the kill-switch (CLI --no-pickup-ground-models).
        Assert.True(new PickupModelImportPass().IsEnabled(new RandomizerConfig()));
        Assert.False(new PickupModelImportPass().IsEnabled(new RandomizerConfig { ImportPickupModels = false }));
        // Lever B implies the Lever-A fallback marking even when NormalizePickupVisuals is off.
        Assert.True(new NormalizePickupVisualsPass().IsEnabled(
            new RandomizerConfig { NormalizePickupVisuals = false, ImportPickupModels = true }));
    }
}
