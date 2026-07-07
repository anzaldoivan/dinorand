using System.Buffers.Binary;
using DinoRand.FileFormats.Compression;

namespace DinoRand.FileFormats.Stage;

/// <summary>
/// A VRAM destination rectangle in PSX framebuffer coordinates, exactly as a Dino Crisis room
/// package declares it in the 16-byte Gian entry header (<c>u16 X, u16 Y, u16 W, u16 H</c> at
/// header offset <c>+8</c>) — i.e. a PSX <c>LoadImage</c> RECT. X/Y/W are in 16-bit VRAM units
/// (one unit = one 16-bit halfword of framebuffer); 8-bit textures pack two texels per unit.
/// </summary>
public readonly record struct VramRect(int X, int Y, int W, int H)
{
    /// <summary>Size of the rect's pixel payload: <c>W*H*2</c> bytes of 16bpp VRAM data.</summary>
    public int PixelBytes => W * H * 2;

    /// <summary>True if VRAM point <paramref name="px"/>,<paramref name="py"/> lies in this rect.</summary>
    public bool Contains(int px, int py) => px >= X && px < X + W && py >= Y && py < Y + H;
}

/// <summary>
/// One uploaded VRAM block: the decompressed 16bpp pixels of a single package texture/palette
/// entry plus the <see cref="VramRect"/> the engine DMAs it to at room load. Self-contained —
/// its pixels reference no other block — so it is relocatable by shifting <see cref="Dst"/>
/// alone (the texture-import analogue of <see cref="SpeciesBlock"/>'s pointer-closed contract).
/// </summary>
public sealed record VramBlock(int EntryIndex, GianEntryType Type, VramRect Dst, byte[] Pixels);

/// <summary>
/// A species' texture as a movable unit: the enemy texture-page <see cref="Texture"/> block its
/// model samples plus the <see cref="Palette"/> block its CLUT references, and the exact PSX GPU
/// codes the model's primitives carry (<see cref="TpageCodes"/> at primitive <c>+0x0e</c>,
/// <see cref="ClutCode"/> at <c>+0x0a</c>; see docs/reference/dc1/textures/TEXTURE-IMPORT-VRAM.md (a)). Either re-place
/// it at the same VRAM coords in the target room, or <see cref="Relocate"/> it to a free region
/// and rewrite the model's codes to the values this returns.
/// </summary>
public sealed record TextureBlock(VramBlock Texture, VramBlock Palette,
                                  IReadOnlyList<ushort> TpageCodes, ushort ClutCode)
{
    /// <summary>
    /// Shift this block's VRAM placement by <paramref name="dx"/>,<paramref name="dy"/> (16-bit
    /// VRAM units) and return a copy with the moved rects plus the rewritten tpage/CLUT codes the
    /// model must use to keep sampling its own texels. Pixels are never touched — relocation is a
    /// coordinate change only (so <c>Relocate(0,0)</c> is the identity), mirroring
    /// <see cref="SpeciesImporter.Append"/>'s uniform-delta relocation but in VRAM space.
    /// </summary>
    public TextureBlock Relocate(int dx, int dy) => RelocateSplit(dx, dy, dx, dy);

    /// <summary>
    /// Like <see cref="Relocate"/> but shifts the texture and the palette by <i>independent</i>
    /// deltas — the general case, since a 64-wide enemy texture page and its CLUT row rarely share a
    /// single rigid delta that lands both in free VRAM (the palette is pinned near the right edge).
    /// Shifts <see cref="Texture"/> by <paramref name="texDx"/>,<paramref name="texDy"/> and
    /// <see cref="Palette"/> by <paramref name="palDx"/>,<paramref name="palDy"/>, and returns the
    /// rewritten tpage codes (by the texture delta) and CLUT code (by the palette delta) the model
    /// must carry to keep sampling its own texels. Pixels are never touched.
    /// </summary>
    public TextureBlock RelocateSplit(int texDx, int texDy, int palDx, int palDy)
    {
        var tex = Texture with { Dst = Texture.Dst with { X = Texture.Dst.X + texDx, Y = Texture.Dst.Y + texDy } };
        var pal = Palette with { Dst = Palette.Dst with { X = Palette.Dst.X + palDx, Y = Palette.Dst.Y + palDy } };

        var newTpages = new List<ushort>(TpageCodes.Count);
        foreach (var tp in TpageCodes)
        {
            var (x, y) = TextureImporter.TpageOrigin(tp);
            newTpages.Add(TextureImporter.MakeTpage(x + texDx, y + texDy, (tp >> 7) & 3));
        }
        var (cx, cy) = TextureImporter.ClutOrigin(ClutCode);
        ushort newClut = TextureImporter.MakeClut(cx + palDx, cy + palDy);

        return new TextureBlock(tex, pal, newTpages, newClut);
    }
}

/// <summary>
/// Offline texture-import support (docs/reference/dc1/textures/TEXTURE-IMPORT-VRAM.md, increment 2). Where
/// <see cref="SpeciesImporter"/> copies an enemy's <i>geometry</i> (model + motion) but leaves it
/// sampling whatever the target room loaded into VRAM at the model's hard-coded texture page —
/// which renders the wrong skin (e.g. a Pteranodon wearing the raptor texture in room 0102) or
/// faults the DDRAW draw call where that page is unpopulated (room 0203) — this type parses the
/// room's <b>texture VRAM</b> so the donor's texture can travel with its model.
///
/// <para><b>Format (all offline-verified, tools/scd_re/{vram_block,entry_hdr,prim_walk}.py).</b>
/// A DC1 room <c>stNXX.dat</c> is a Gian package; its texture entries are <i>raw VRAM upload
/// blocks</i>, not TIM files. Each 16-byte entry header is <c>u32 type; u32 size; u16 dstX; u16
/// dstY; u16 W; u16 H</c> — a PSX LoadImage RECT (DINO.exe walks these via the type jump table at
/// <c>0x42ce81</c>; LZSS types decompress through <c>0x42d974</c>). Type-8 (<see
/// cref="GianEntryType.Lzss2"/>) payloads are LZSS-compressed and expand to exactly <c>W*H*2</c>
/// bytes of 16bpp pixels (plus &lt;=8 bytes of LZSS run-out we trim); type-2
/// (<see cref="GianEntryType.Palette"/>) holds a raw 16bpp CLUT. A model's primitives carry a
/// texture-page code (tpage) at primitive <c>+0x0e</c> and a palette code (CLUT) at <c>+0x0a</c>;
/// enemies live in a fixed VRAM column (e.g. X=704 for raptor/Pteranodon, palette near the VRAM
/// bottom).</para>
///
/// <para>Standalone by design (the prompt's enemy-import code is owned elsewhere): it depends only
/// on <see cref="Lzss"/> and <see cref="GianEntryType"/>, and never mutates a room.</para>
/// </summary>
public static class TextureImporter
{
    /// <summary>PSX VRAM framebuffer width in 16-bit units.</summary>
    public const int VramWidth = 1024;

    /// <summary>PSX VRAM framebuffer height in lines.</summary>
    public const int VramHeight = 512;

    private const int HeaderSize = 2048;
    private const int SectorSize = 2048;

    /// <summary>
    /// VRAM origin (X in 16-bit units, Y in lines) of a PSX texture-page code: bits 0-3 select the
    /// page column (×64), bit 4 the page row (×256). bits 7-8 are the colour mode (0=4bpp,1=8bpp,
    /// 2=16bpp) which does not move the origin.
    /// </summary>
    public static (int X, int Y) TpageOrigin(ushort tpage) => ((tpage & 0xF) * 64, ((tpage >> 4) & 1) * 256);

    /// <summary>Inverse of <see cref="TpageOrigin"/>: build the tpage code for a page at
    /// <paramref name="x"/>,<paramref name="y"/> with colour-mode bits <paramref name="bppMode"/>
    /// (0=4bpp,1=8bpp,2=16bpp). <paramref name="x"/> must be a 64-unit, <paramref name="y"/> a
    /// 256-line page boundary.</summary>
    public static ushort MakeTpage(int x, int y, int bppMode)
    {
        if (x % 64 != 0 || (x / 64) > 0xF) throw new ArgumentOutOfRangeException(nameof(x), $"x={x} not a page column");
        if (y % 256 != 0 || (y / 256) > 1) throw new ArgumentOutOfRangeException(nameof(y), $"y={y} not a page row");
        return (ushort)((x / 64) | ((y / 256) << 4) | ((bppMode & 3) << 7));
    }

    /// <summary>VRAM coords of a CLUT (palette) code: bits 0-5 = X/16, bits 6-14 = Y.</summary>
    public static (int X, int Y) ClutOrigin(ushort clut) => ((clut & 0x3F) * 16, (clut >> 6) & 0x1FF);

    /// <summary>Inverse of <see cref="ClutOrigin"/>. <paramref name="x"/> must be a 16-unit
    /// boundary.</summary>
    public static ushort MakeClut(int x, int y)
    {
        if (x % 16 != 0 || (x / 16) > 0x3F) throw new ArgumentOutOfRangeException(nameof(x), $"x={x} not a CLUT column");
        if ((uint)y > 0x1FF) throw new ArgumentOutOfRangeException(nameof(y), $"y={y} out of VRAM");
        return (ushort)((x / 16) | (y << 6));
    }

    /// <summary>
    /// Parse every texture/palette VRAM block from a room package <paramref name="file"/>: walk the
    /// 16-byte entry table (stopping at the first out-of-range type, like the engine and
    /// <see cref="GianPackage"/>), and for each texture entry read its <see cref="VramRect"/> from
    /// the header and its pixels from the payload — LZSS-decompressed and trimmed to <c>W*H*2</c>
    /// for compressed types (5/6/8), raw for type-1/2. The RDT (last entry) and audio entries are
    /// skipped.
    /// </summary>
    public static List<VramBlock> ParseVramBlocks(ReadOnlySpan<byte> file)
    {
        var blocks = new List<VramBlock>();
        if (file.Length < HeaderSize) return blocks;

        int slots = HeaderSize / 16;
        int pos = HeaderSize;
        for (int i = 0; i < slots; i++)
        {
            int hdr = i * 16;
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(hdr, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(hdr + 4, 4));
            if (type > (uint)GianEntryType.Lzss2 || size == 0) break;     // end of table
            if (pos + (long)size > file.Length) break;

            var et = (GianEntryType)type;
            int x = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(hdr + 8, 2));
            int y = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(hdr + 10, 2));
            int w = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(hdr + 12, 2));
            int h = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(hdr + 14, 2));

            if (IsTextureEntry(et))
            {
                var rect = new VramRect(x, y, w, h);
                var payload = file.Slice(pos, (int)size);
                byte[] pixels = IsCompressed(et) ? Lzss.Decompress(payload) : payload.ToArray();
                int want = rect.PixelBytes;
                if (want > 0 && pixels.Length >= want)
                    pixels = pixels[..want];                              // trim LZSS run-out / over-decode
                blocks.Add(new VramBlock(i, et, rect, pixels));
            }

            pos += Align(size);
            if (pos > file.Length) break;
        }
        return blocks;
    }

    /// <summary>
    /// Extract a species' texture as a relocatable <see cref="TextureBlock"/>: the texture VRAM
    /// block that covers the model's <paramref name="tpageCodes"/> page origins, plus the palette
    /// block at the model's <paramref name="clutCode"/> VRAM coords. Throws when the room's VRAM
    /// has no block covering a tpage (the 0203/DDRAW case — the species cannot sample a valid page
    /// there) or no palette at the CLUT coords.
    /// </summary>
    public static TextureBlock ExtractSpeciesTexture(ReadOnlySpan<byte> file,
                                                     IReadOnlyCollection<ushort> tpageCodes, ushort clutCode)
    {
        if (tpageCodes.Count == 0) throw new ArgumentException("no tpage codes", nameof(tpageCodes));
        var blocks = ParseVramBlocks(file);

        VramBlock? texture = null;
        foreach (var tp in tpageCodes)
        {
            var (px, py) = TpageOrigin(tp);
            var cover = blocks.FirstOrDefault(b => b.Type != GianEntryType.Palette && b.Dst.Contains(px, py));
            if (cover is null)
                throw new InvalidOperationException(
                    $"no texture block covers tpage {tp:#x} (VRAM {px},{py}) — species can't sample a valid page here");
            // All tpages of one enemy share a column; the covering block is the same for each.
            texture ??= cover;
            if (cover.Dst != texture.Dst)
                throw new InvalidOperationException(
                    $"tpages span more than one VRAM block ({texture.Dst} vs {cover.Dst}); not a single movable unit");
        }

        var (clX, clY) = ClutOrigin(clutCode);
        var palette = blocks.FirstOrDefault(b => b.Type == GianEntryType.Palette
                                                 && b.Dst.X == clX && b.Dst.Y == clY)
            ?? throw new InvalidOperationException(
                $"no palette block at CLUT {clutCode:#x} (VRAM {clX},{clY})");

        return new TextureBlock(texture!, palette, tpageCodes.ToArray(), clutCode);
    }

    /// <summary>A raw package entry: its type, declared payload size, VRAM rect and on-disk payload
    /// offset — the offsets <see cref="ParseVramBlocks"/> drops (it returns decompressed pixels).</summary>
    private readonly record struct RawEntry(int Index, GianEntryType Type, int Size, VramRect Dst, int PayloadOffset);

    /// <summary>Walk the 16-byte entry table keeping each entry's raw on-disk position (same stop
    /// rule as <see cref="ParseVramBlocks"/> / the loader).</summary>
    private static List<RawEntry> RawEntries(ReadOnlySpan<byte> file)
    {
        var list = new List<RawEntry>();
        if (file.Length < HeaderSize) return list;
        int pos = HeaderSize;
        for (int i = 0; i < HeaderSize / 16; i++)
        {
            int hdr = i * 16;
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(hdr, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(hdr + 4, 4));
            if (type > (uint)GianEntryType.Lzss2 || size == 0) break;
            if (pos + (long)size > file.Length) break;
            int x = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(hdr + 8, 2));
            int y = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(hdr + 10, 2));
            int w = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(hdr + 12, 2));
            int h = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(hdr + 14, 2));
            list.Add(new RawEntry(i, (GianEntryType)type, (int)size, new VramRect(x, y, w, h), pos));
            pos += Align(size);
            if (pos > file.Length) break;
        }
        return list;
    }

    /// <summary>
    /// Overwrite <paramref name="target"/>'s enemy texture + palette entries — the ones the model's
    /// <paramref name="tpageCodes"/>/<paramref name="clutCode"/> name — IN PLACE with
    /// <paramref name="donor"/>'s, returning a patched copy (the original spans are not mutated).
    ///
    /// <para>The simplest texture import: it requires donor and target to upload to the <b>same VRAM
    /// coords</b> (so the model needs no tpage/CLUT rewrite — the codes already name those coords), and the
    /// donor's compressed texture stream to fit the target entry's declared size. The donor's stream is
    /// copied verbatim (no re-compress) and the rest of the target slot is padded with <c>0xFF</c> — an
    /// all-literal LZSS flag, so a full decode of the slice yields the donor's texels first and then
    /// harmless literals, never back-referencing before the stream start. The palette (raw 16bpp) is copied
    /// verbatim. Because no entry's declared size changes, the 2048-byte header and every downstream payload
    /// offset stay put — no package repack. docs/reference/dc1/textures/TEXTURE-IMPORT-VRAM.md §(d)(i); technique pinned by
    /// <c>EnemyTextureCatalogTests</c> Case A.</para>
    ///
    /// <para>Throws <see cref="InvalidOperationException"/> when either room lacks the covering
    /// texture/palette entry, the donor and target rects differ (would need relocation + a code rewrite),
    /// or the donor stream does not fit the target slot (would need a repack).</para>
    /// </summary>
    public static byte[] OverwriteSpeciesTextureInPlace(ReadOnlySpan<byte> target, ReadOnlySpan<byte> donor,
                                                        IReadOnlyCollection<ushort> tpageCodes, ushort clutCode)
    {
        if (tpageCodes.Count == 0) throw new ArgumentException("no tpage codes", nameof(tpageCodes));
        var (px, py) = TpageOrigin(tpageCodes.First());
        var (clX, clY) = ClutOrigin(clutCode);

        var tEntries = RawEntries(target);
        var dEntries = RawEntries(donor);

        var tgtTex = FindTexEntry(tEntries, px, py, "target"); var donTex = FindTexEntry(dEntries, px, py, "donor");
        var tgtPal = FindPalEntry(tEntries, clX, clY, "target"); var donPal = FindPalEntry(dEntries, clX, clY, "donor");

        if (tgtTex.Dst != donTex.Dst)
            throw new InvalidOperationException(
                $"texture rects differ (target {tgtTex.Dst} vs donor {donTex.Dst}); in-place overwrite needs matching coords");
        if (tgtPal.Dst != donPal.Dst)
            throw new InvalidOperationException(
                $"palette rects differ (target {tgtPal.Dst} vs donor {donPal.Dst}); in-place overwrite needs matching coords");
        if (donTex.Size > tgtTex.Size)
            throw new InvalidOperationException(
                $"donor texture stream 0x{donTex.Size:X} exceeds target slot 0x{tgtTex.Size:X}; would need a package repack");
        if (donPal.Size != tgtPal.Size)
            throw new InvalidOperationException(
                $"palette sizes differ (target 0x{tgtPal.Size:X} vs donor 0x{donPal.Size:X})");

        var patched = target.ToArray();
        // Texture: donor compressed stream verbatim, then 0xFF all-literal pad to the target's declared size.
        donor.Slice(donTex.PayloadOffset, donTex.Size).CopyTo(patched.AsSpan(tgtTex.PayloadOffset, donTex.Size));
        patched.AsSpan(tgtTex.PayloadOffset + donTex.Size, tgtTex.Size - donTex.Size).Fill(0xFF);
        // Palette: raw 16bpp CLUT, identical size.
        donor.Slice(donPal.PayloadOffset, donPal.Size).CopyTo(patched.AsSpan(tgtPal.PayloadOffset, donPal.Size));
        return patched;
    }

    /// <summary>True if <paramref name="file"/> already uploads a texture covering the
    /// <paramref name="tpageCodes"/> page origin AND a palette at the <paramref name="clutCode"/> coords —
    /// i.e. the donor's texture can be dropped in by <see cref="OverwriteSpeciesTextureInPlace"/>. When
    /// false (the column is absent), use <see cref="AppendSpeciesTexture"/> to add it instead.</summary>
    public static bool HasSpeciesTextureSlot(ReadOnlySpan<byte> file,
                                             IReadOnlyCollection<ushort> tpageCodes, ushort clutCode)
    {
        if (tpageCodes.Count == 0) return false;
        var (px, py) = TpageOrigin(tpageCodes.First());
        var (clX, clY) = ClutOrigin(clutCode);
        var es = RawEntries(file);
        bool tex = es.Any(e => IsTextureEntry(e.Type) && e.Type != GianEntryType.Palette && e.Dst.Contains(px, py));
        bool pal = es.Any(e => e.Type == GianEntryType.Palette && e.Dst.X == clX && e.Dst.Y == clY);
        return tex && pal;
    }

    /// <summary>
    /// ADD the donor's enemy texture + palette to <paramref name="target"/> as <b>new</b> package entries
    /// (returning a repacked copy) — for a target room that does <i>not</i> already upload the donor's VRAM
    /// column (the small-room case, where <see cref="OverwriteSpeciesTextureInPlace"/> has nothing to
    /// overwrite). The donor's compressed texture stream + raw palette are copied verbatim into two fresh
    /// entries at the donor's own VRAM coords, inserted <b>before the RDT</b> (which stays the last entry);
    /// the package is re-laid sector-aligned and its 16-byte header table rebuilt. The model keeps its native
    /// tpage/CLUT codes (they already name those coords), so no model-primitive rewrite is needed.
    ///
    /// <para>Texture entries load to <b>VRAM</b>, not the RDT's PSX-RAM buffer, so this does <i>not</i> count
    /// against the engine room-RDT ceiling (<see cref="SpeciesImporter.EngineRoomRdtCeiling"/>). Throws when
    /// the target already uploads the donor's column (use the overwrite path) or when the donor's texture /
    /// palette rect would overlap VRAM the target already uses (a real conflict).</para>
    /// </summary>
    public static byte[] AppendSpeciesTexture(ReadOnlySpan<byte> target, ReadOnlySpan<byte> donor,
                                              IReadOnlyCollection<ushort> tpageCodes, ushort clutCode)
    {
        if (tpageCodes.Count == 0) throw new ArgumentException("no tpage codes", nameof(tpageCodes));
        var (px, py) = TpageOrigin(tpageCodes.First());
        var (clX, clY) = ClutOrigin(clutCode);

        var dEntries = RawEntries(donor);
        var donTex = FindTexEntry(dEntries, px, py, "donor");
        var donPal = FindPalEntry(dEntries, clX, clY, "donor");

        var tEntries = RawEntries(target);
        if (tEntries.Count == 0) throw new InvalidOperationException("target is not a parseable package");
        if (tEntries.Any(e => IsTextureEntry(e.Type) && e.Type != GianEntryType.Palette && e.Dst.Contains(px, py))
            || tEntries.Any(e => e.Type == GianEntryType.Palette && e.Dst.X == clX && e.Dst.Y == clY))
            throw new InvalidOperationException(
                "target already uploads this species' VRAM column — use OverwriteSpeciesTextureInPlace");
        // The new texture/palette rects must not overlap VRAM the target already uses.
        foreach (var e in tEntries)
        {
            if (!IsTextureEntry(e.Type)) continue;
            if (RectsOverlap(e.Dst, donTex.Dst))
                throw new InvalidOperationException($"donor texture {donTex.Dst} overlaps target VRAM {e.Dst}");
            if (RectsOverlap(e.Dst, donPal.Dst))
                throw new InvalidOperationException($"donor palette {donPal.Dst} overlaps target VRAM {e.Dst}");
        }

        // New entry order: [target's entries except the RDT] + [donor texture, donor palette] + [RDT last].
        int rdtIdx = tEntries.Count - 1;
        // Carry each planned entry as (type, rect, size, source, payload offset) — a ReadOnlySpan can't
        // live in a List, so we record where to copy from and slice at emit time.
        var plan = new List<(GianEntryType Type, VramRect Dst, int Size, bool FromDonor, int Off)>();
        for (int i = 0; i < tEntries.Count; i++)
        {
            if (i == rdtIdx) continue; // RDT appended last
            var e = tEntries[i];
            plan.Add((e.Type, e.Dst, e.Size, false, e.PayloadOffset));
        }
        plan.Add((donTex.Type, donTex.Dst, donTex.Size, true, donTex.PayloadOffset));
        plan.Add((donPal.Type, donPal.Dst, donPal.Size, true, donPal.PayloadOffset));
        var rdt = tEntries[rdtIdx];
        plan.Add((rdt.Type, rdt.Dst, rdt.Size, false, rdt.PayloadOffset));

        if (plan.Count > HeaderSize / 16)
            throw new InvalidOperationException($"too many entries ({plan.Count}) for the {HeaderSize}-byte header");

        int total = HeaderSize + plan.Sum(p => Align((uint)p.Size));
        var outBuf = new byte[total];
        int pos = HeaderSize;
        for (int i = 0; i < plan.Count; i++)
        {
            var p = plan[i];
            int hdr = i * 16;
            BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(hdr, 4), (uint)p.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(hdr + 4, 4), (uint)p.Size);
            BinaryPrimitives.WriteUInt16LittleEndian(outBuf.AsSpan(hdr + 8, 2), (ushort)p.Dst.X);
            BinaryPrimitives.WriteUInt16LittleEndian(outBuf.AsSpan(hdr + 10, 2), (ushort)p.Dst.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(outBuf.AsSpan(hdr + 12, 2), (ushort)p.Dst.W);
            BinaryPrimitives.WriteUInt16LittleEndian(outBuf.AsSpan(hdr + 14, 2), (ushort)p.Dst.H);
            var src = p.FromDonor ? donor : target;
            src.Slice(p.Off, p.Size).CopyTo(outBuf.AsSpan(pos, p.Size));
            pos += Align((uint)p.Size);
        }
        return outBuf;
    }

    /// <summary>
    /// Import the donor's enemy texture page + palette into <paramref name="target"/>, deciding
    /// <b>per resource</b>: a resource the target already uploads (same VRAM coords) is <b>overwritten</b> in
    /// its existing slot with the donor's payload; a resource the target lacks is <b>appended</b> as a new
    /// package entry. This handles the <i>mixed</i> case the all-or-nothing
    /// <see cref="OverwriteSpeciesTextureInPlace"/> (needs both present) and <see cref="AppendSpeciesTexture"/>
    /// (needs both absent) miss — e.g. room 0102, which uploads the shared CLUT at (768,511) but not the
    /// X=640 texture column, so the palette is overwritten and the texture page appended
    /// (docs/decisions/dc1/theri/THERI-CLIP-STRIP-PLAN.md). Texture entries live in VRAM, off the RDT ceiling.
    /// </summary>
    public static byte[] ImportSpeciesTexture(ReadOnlySpan<byte> target, ReadOnlySpan<byte> donor,
                                              IReadOnlyCollection<ushort> tpageCodes, ushort clutCode)
    {
        if (tpageCodes.Count == 0) throw new ArgumentException("no tpage codes", nameof(tpageCodes));
        var (px, py) = TpageOrigin(tpageCodes.First());
        var (clX, clY) = ClutOrigin(clutCode);

        var tEntries = RawEntries(target);
        if (tEntries.Count == 0) throw new InvalidOperationException("target is not a parseable package");
        var dEntries = RawEntries(donor);
        var donTex = FindTexEntry(dEntries, px, py, "donor");
        var donPal = FindPalEntry(dEntries, clX, clY, "donor");

        bool texPresent = tEntries.Any(e => IsTextureEntry(e.Type) && e.Type != GianEntryType.Palette && e.Dst.Contains(px, py));
        bool palPresent = tEntries.Any(e => e.Type == GianEntryType.Palette && e.Dst.X == clX && e.Dst.Y == clY);

        if (texPresent && palPresent)           // both present: the simple in-place overwrite, no repack
            return OverwriteSpeciesTextureInPlace(target, donor, tpageCodes, clutCode);

        // Validate the present-resource overwrites (must match coords + fit the existing slot).
        int texOverwriteOff = -1, palOverwriteOff = -1;
        if (texPresent)
        {
            var t = FindTexEntry(tEntries, px, py, "target");
            if (t.Dst != donTex.Dst) throw new InvalidOperationException(
                $"texture rects differ (target {t.Dst} vs donor {donTex.Dst}); overwrite needs matching coords");
            if (donTex.Size > t.Size) throw new InvalidOperationException(
                $"donor texture stream 0x{donTex.Size:X} exceeds target slot 0x{t.Size:X}; would need a repack");
            texOverwriteOff = t.PayloadOffset;
        }
        if (palPresent)
        {
            var p = FindPalEntry(tEntries, clX, clY, "target");
            if (p.Dst != donPal.Dst) throw new InvalidOperationException(
                $"palette rects differ (target {p.Dst} vs donor {donPal.Dst}); overwrite needs matching coords");
            if (donPal.Size != p.Size) throw new InvalidOperationException(
                $"palette sizes differ (target 0x{p.Size:X} vs donor 0x{donPal.Size:X})");
            palOverwriteOff = p.PayloadOffset;
        }
        // An appended (absent) resource must not collide with VRAM the target already uses.
        foreach (var e in tEntries)
        {
            if (!IsTextureEntry(e.Type)) continue;
            if (!texPresent && RectsOverlap(e.Dst, donTex.Dst))
                throw new InvalidOperationException($"donor texture {donTex.Dst} overlaps target VRAM {e.Dst}");
            if (!palPresent && RectsOverlap(e.Dst, donPal.Dst))
                throw new InvalidOperationException($"donor palette {donPal.Dst} overlaps target VRAM {e.Dst}");
        }

        // Materialise each entry's payload; overwrite present resources from the donor, keep others verbatim.
        int rdtIdx = tEntries.Count - 1;
        var plan = new List<(GianEntryType Type, VramRect Dst, byte[] Payload)>();
        for (int i = 0; i < tEntries.Count; i++)
        {
            if (i == rdtIdx) continue;   // RDT stays last
            var e = tEntries[i];
            byte[] payload;
            if (e.PayloadOffset == texOverwriteOff)         // overwrite texture: donor stream + 0xFF literal pad
            {
                payload = new byte[e.Size];
                donor.Slice(donTex.PayloadOffset, donTex.Size).CopyTo(payload);
                payload.AsSpan(donTex.Size).Fill(0xFF);
            }
            else if (e.PayloadOffset == palOverwriteOff)    // overwrite palette: donor raw CLUT (same size)
                payload = donor.Slice(donPal.PayloadOffset, donPal.Size).ToArray();
            else
                payload = target.Slice(e.PayloadOffset, e.Size).ToArray();
            plan.Add((e.Type, e.Dst, payload));
        }
        if (!texPresent) plan.Add((donTex.Type, donTex.Dst, donor.Slice(donTex.PayloadOffset, donTex.Size).ToArray()));
        if (!palPresent) plan.Add((donPal.Type, donPal.Dst, donor.Slice(donPal.PayloadOffset, donPal.Size).ToArray()));
        var rdtE = tEntries[rdtIdx];
        plan.Add((rdtE.Type, rdtE.Dst, target.Slice(rdtE.PayloadOffset, rdtE.Size).ToArray()));

        if (plan.Count > HeaderSize / 16)
            throw new InvalidOperationException($"too many entries ({plan.Count}) for the {HeaderSize}-byte header");

        int total = HeaderSize + plan.Sum(p => (int)Align((uint)p.Payload.Length));
        var outBuf = new byte[total];
        int pos = HeaderSize;
        for (int i = 0; i < plan.Count; i++)
        {
            var p = plan[i];
            int hdr = i * 16;
            BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(hdr, 4), (uint)p.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(hdr + 4, 4), (uint)p.Payload.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(outBuf.AsSpan(hdr + 8, 2), (ushort)p.Dst.X);
            BinaryPrimitives.WriteUInt16LittleEndian(outBuf.AsSpan(hdr + 10, 2), (ushort)p.Dst.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(outBuf.AsSpan(hdr + 12, 2), (ushort)p.Dst.W);
            BinaryPrimitives.WriteUInt16LittleEndian(outBuf.AsSpan(hdr + 14, 2), (ushort)p.Dst.H);
            p.Payload.CopyTo(outBuf.AsSpan(pos, p.Payload.Length));
            pos += (int)Align((uint)p.Payload.Length);
        }
        return outBuf;
    }

    private static RawEntry FindTexEntry(List<RawEntry> es, int px, int py, string which)
    {
        foreach (var e in es)
            if (IsTextureEntry(e.Type) && e.Type != GianEntryType.Palette && e.Dst.Contains(px, py))
                return e;
        throw new InvalidOperationException($"{which} has no texture entry covering tpage VRAM ({px},{py})");
    }

    private static RawEntry FindPalEntry(List<RawEntry> es, int clX, int clY, string which)
    {
        foreach (var e in es)
            if (e.Type == GianEntryType.Palette && e.Dst.X == clX && e.Dst.Y == clY)
                return e;
        throw new InvalidOperationException($"{which} has no palette entry at CLUT VRAM ({clX},{clY})");
    }

    private static bool RectsOverlap(VramRect a, VramRect b)
        => a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    /// <summary>File-form PSX-RAM base the RDT's internal pointers (and a model's mesh pointers) are
    /// relative to (same as <see cref="SpeciesImporter.PsxBase"/>; duplicated to keep this type's
    /// dependency surface small).</summary>
    public const uint PsxRdtBase = 0x80100000;

    /// <summary>
    /// The CLUT "marker" a model's primitives all carry: the CLUT code (<c>u16 @ +0x0a</c>) of the
    /// first triangle packet of bone 0 — the per-model constant a primitive walk keys on.
    /// </summary>
    private static ushort ClutMarker(byte[] rdt, uint modelPtr)
    {
        int ba = (int)(modelPtr - PsxRdtBase) + 0x18;
        uint firstTri = BinaryPrimitives.ReadUInt32LittleEndian(rdt.AsSpan(ba + 8, 4));
        int t = (int)(firstTri - PsxRdtBase);
        return BinaryPrimitives.ReadUInt16LittleEndian(rdt.AsSpan(t + 0x0a, 2));
    }

    /// <summary>
    /// Visit every texture-bearing primitive packet of the model at <paramref name="modelPtr"/>:
    /// walk each bone (array @ model+0x18, stride 0x14) and its two mesh pools (triangle @ +8 /
    /// 16-byte packets, quad @ +0xc / 20-byte packets), stepping by stride while the packet's CLUT
    /// (<c>+0x0a</c>) stays equal to the model's <see cref="ClutMarker"/>. The C# counterpart of
    /// tools/scd_re/species_tpage.py — the shared walk behind <see cref="ReadModelTextureCodes"/> and
    /// <see cref="RewriteModelCodes"/>. <paramref name="visit"/> receives each packet's offset; CLUT is
    /// at <c>+0x0a</c> and tpage at <c>+0x0e</c> in both packet kinds.
    /// </summary>
    private static void ForEachPacket(byte[] rdt, uint modelPtr, Action<int> visit)
    {
        int off = (int)(modelPtr - PsxRdtBase);
        if (off < 0 || off + 0x18 > rdt.Length)
            throw new ArgumentOutOfRangeException(nameof(modelPtr), $"model ptr {modelPtr:X8} out of RDT");
        uint bones = BinaryPrimitives.ReadUInt32LittleEndian(rdt.AsSpan(off + 0x14, 4));
        if (bones == 0 || bones > 64)
            throw new InvalidOperationException($"implausible bone count {bones} for model {modelPtr:X8}");
        int ba = off + 0x18;
        ushort marker = ClutMarker(rdt, modelPtr);
        for (int i = 0; i < bones; i++)
        {
            int boneRec = ba + i * 0x14;
            foreach (var (slot, stride) in new[] { (boneRec + 8, 16), (boneRec + 0x0c, 20) })
            {
                uint mesh = BinaryPrimitives.ReadUInt32LittleEndian(rdt.AsSpan(slot, 4));
                if (mesh < PsxRdtBase || mesh >= PsxRdtBase + (uint)rdt.Length) continue;
                int o = (int)(mesh - PsxRdtBase);
                while (o + stride <= rdt.Length
                       && BinaryPrimitives.ReadUInt16LittleEndian(rdt.AsSpan(o + 0x0a, 2)) == marker)
                {
                    visit(o);
                    o += stride;
                }
            }
        }
    }

    /// <summary>
    /// Read the texture codes a model's primitives carry: the set of distinct tpage codes
    /// (<c>u16 @ +0x0e</c>) and the dominant CLUT code (<c>u16 @ +0x0a</c>), by walking every bone
    /// mesh (<see cref="ForEachPacket"/>). These are the codes <see cref="ExtractSpeciesTexture"/>
    /// consumes — derived from the model itself, so no per-species table is needed at runtime.
    /// </summary>
    public static (IReadOnlyList<ushort> Tpages, ushort Clut) ReadModelTextureCodes(byte[] rdt, uint modelPtr)
    {
        var tpages = new SortedSet<ushort>();
        ForEachPacket(rdt, modelPtr, o => tpages.Add(BinaryPrimitives.ReadUInt16LittleEndian(rdt.AsSpan(o + 0x0e, 2))));
        return (tpages.ToArray(), ClutMarker(rdt, modelPtr));
    }

    /// <summary>
    /// Rewrite the texture codes baked into the model at <paramref name="modelPtr"/> in place: every
    /// primitive's tpage (<c>+0x0e</c>) is replaced via <paramref name="tpageMap"/> (old→new) and its
    /// CLUT (<c>+0x0a</c>) set to <paramref name="newClut"/>. Used after a texture
    /// <see cref="TextureBlock.RelocateSplit"/> so the imported model samples the relocated VRAM. The
    /// CLUT stop-marker is captured before any write, so rewriting is walk-safe.
    /// </summary>
    public static void RewriteModelCodes(byte[] rdt, uint modelPtr,
                                         IReadOnlyDictionary<ushort, ushort> tpageMap, ushort newClut)
    {
        ForEachPacket(rdt, modelPtr, o =>
        {
            ushort tp = BinaryPrimitives.ReadUInt16LittleEndian(rdt.AsSpan(o + 0x0e, 2));
            if (tpageMap.TryGetValue(tp, out var ntp))
                BinaryPrimitives.WriteUInt16LittleEndian(rdt.AsSpan(o + 0x0e, 2), ntp);
            BinaryPrimitives.WriteUInt16LittleEndian(rdt.AsSpan(o + 0x0a, 2), newClut);
        });
    }

    /// <summary>
    /// Relocate <paramref name="block"/> to a free VRAM region of <paramref name="targetBytes"/>: find
    /// a clear 64-wide texture page column (X in [512,960], 64-aligned, the full texture height clear)
    /// and a clear palette row, and return the block <see cref="TextureBlock.RelocateSplit"/>'d there
    /// (with the rewritten tpage/CLUT codes the imported model must then carry). The texture keeps its
    /// Y (page-row bit) and the palette keeps its X; only the free column / row move. Throws when the
    /// target has no free column or palette row (caller falls back to a geometry-only import).
    /// </summary>
    public static TextureBlock PickFreeRegion(ReadOnlySpan<byte> targetBytes, TextureBlock block)
    {
        var occupied = ParseVramBlocks(targetBytes).Select(b => b.Dst).ToList();
        var tex = block.Texture.Dst;
        var pal = block.Palette.Dst;

        int texX = -1;
        for (int x = 512; x <= 960; x += 64)
        {
            var cand = tex with { X = x };
            if (x + cand.W <= VramWidth && !occupied.Any(r => Intersects(r, cand))) { texX = x; break; }
        }
        if (texX < 0) throw new InvalidOperationException("no free texture column in target VRAM for relocation");

        int palY = -1;
        for (int y = VramHeight - 1; y >= 480; y--)
        {
            var cand = pal with { Y = y };
            if (!occupied.Any(r => Intersects(r, cand))) { palY = y; break; }
        }
        if (palY < 0) throw new InvalidOperationException("no free palette row in target VRAM for relocation");

        return block.RelocateSplit(texX - tex.X, 0, 0, palY - pal.Y);
    }

    private static bool Intersects(VramRect a, VramRect b)
        => a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    private static bool IsTextureEntry(GianEntryType t) => t switch
    {
        GianEntryType.Texture or GianEntryType.Palette
            or GianEntryType.Lzss0 or GianEntryType.Lzss1 or GianEntryType.Lzss2 => true,
        _ => false,
    };

    private static bool IsCompressed(GianEntryType t) => t switch
    {
        GianEntryType.Lzss0 or GianEntryType.Lzss1 or GianEntryType.Lzss2 => true,
        _ => false,
    };

    private static int Align(uint v) => (int)((v + (uint)(SectorSize - 1)) & ~(uint)(SectorSize - 1));
}
