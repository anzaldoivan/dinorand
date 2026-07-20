namespace DinoRand.FileFormats.Stage;

/// <summary>A pickup texture cut from a donor room's VRAM uploads: the mesh texref's UV-bbox
/// sub-rect pixels plus its CLUT row, with the donor-side coords they were cut from.</summary>
public sealed record PickupTextureCut(ushort Tpage, ushort Clut,
                                      VramRect TexRect, byte[] TexPixels,
                                      VramRect ClutRect, byte[] ClutPixels);

/// <summary>Where a <see cref="PickupTextureCut"/> lands in the target room's VRAM, and the rewritten
/// codes the imported mesh must carry.</summary>
public sealed record PickupTexturePlacement(ushort Tpage, ushort Clut, VramRect TexRect, VramRect ClutRect);

/// <summary>
/// Lever B's texture travel (PICKUP-GROUND-MODEL-FEASIBILITY.md "Lever B plan"): pickup textures are
/// room-resident (cont.73 — 47/47 texrefs sample the donor room's own uploads), so the donor mesh's
/// texture footprint must be re-uploaded into the destination room. Unlike the enemy path
/// (<see cref="TextureImporter"/>, which moves a whole dedicated VRAM column entry), a pickup's texels
/// live inside the room's shared atlas pages — so only the UV bounding-box <b>sub-rect</b> is cut and
/// re-uploaded, placed at a free 64-aligned column <b>keeping its intra-page X/Y</b> so the mesh UVs
/// stay valid unrewritten.
/// </summary>
public static class PickupModelImporter
{
    /// <summary>The engine's type-8 blit consumes payload rows in 64-byte units = 32 halfwords
    /// (CE-witnessed 2026-07-17, PICKUP-GROUND-MODEL-FEASIBILITY.md "CE witness session": a W=16
    /// entry was laid into VRAM 32 halfwords per row — image squashed 2×, lower sampled rows left
    /// unwritten ⇒ opaque black). Every vanilla type-8 has W ∈ {64,192,256}, all multiples of 32,
    /// so only imported sub-rects can trip it: extracted widths are padded up to this unit.</summary>
    public const int BlitRowHalfwords = 32;

    /// <summary>
    /// Cut the texture footprint of one mesh texref out of the donor room package: the UV bbox
    /// inside the texref's tpage (4bpp texels → /4 halfwords, 8bpp → /2) sliced from the covering
    /// upload block, plus the CLUT row (16 halfwords at 4bpp, 256 at 8bpp) from the covering
    /// palette. Fails when either is not covered (never observed in the corpus) or 16bpp.
    /// </summary>
    public static bool TryExtractTexture(ReadOnlySpan<byte> donorFile, PickupTexref texref,
                                         out PickupTextureCut? cut)
    {
        cut = null;
        int bpp = (texref.Tpage >> 7) & 3;
        if (bpp > 1) return false;                        // 4/8bpp only in the corpus
        int div = bpp == 0 ? 4 : 2;                       // texels per halfword
        int clutLen = bpp == 0 ? 16 : 256;                // halfwords in one CLUT row

        var (px, py) = TextureImporter.TpageOrigin(texref.Tpage);
        int x0 = px + texref.UMin / div;
        int x1 = px + texref.UMax / div + 1;              // exclusive
        int y0 = py + texref.VMin;
        // Width padded right to the engine's blit row unit; only the sampled span needs donor
        // coverage — pad columns beyond the donor block come back zero-filled (never sampled).
        int paddedW = (x1 - x0 + BlitRowHalfwords - 1) / BlitRowHalfwords * BlitRowHalfwords;
        var texRect = new VramRect(x0, y0, paddedW, texref.VMax - texref.VMin + 1);
        var sampledRect = texRect with { W = x1 - x0 };
        var (cx, cy) = TextureImporter.ClutOrigin(texref.Clut);
        var clutRect = new VramRect(cx, cy, clutLen, 1);

        var blocks = TextureImporter.ParseVramBlocks(donorFile);
        var texBlock = blocks.FirstOrDefault(b => b.Type != GianEntryType.Palette && Covers(b.Dst, sampledRect));
        var palBlock = blocks.FirstOrDefault(b => b.Type == GianEntryType.Palette && Covers(b.Dst, clutRect));
        if (texBlock is null || palBlock is null) return false;

        cut = new PickupTextureCut(texref.Tpage, texref.Clut,
                                   texRect, CutRect(texBlock, texRect),
                                   clutRect, CutRect(palBlock, clutRect));
        return true;
    }

    /// <summary>
    /// Find a home for <paramref name="cut"/> in the target room's VRAM: a 64-aligned texture
    /// column inside the room arena <c>[512,768)</c> (<see cref="TextureImporter.RoomArenaX"/> —
    /// everything past it is engine-global icon/font VRAM) where the sub-rect — kept at its donor
    /// intra-page X offset and its Y — collides with neither the target's own uploads nor
    /// <paramref name="staged"/> (rects already claimed by earlier imports into this room), and a
    /// free vanilla-witnessed CLUT row (<see cref="TextureImporter.RoomClutRows"/>, X kept).
    /// Fail-closed: no free column/row → <c>false</c> (caller falls back to the Lever-A generic panel).
    /// </summary>
    public static bool TryPlace(ReadOnlySpan<byte> targetFile, IReadOnlyCollection<VramRect> staged,
                                PickupTextureCut cut, out PickupTexturePlacement? placement)
    {
        placement = null;
        var occupied = TextureImporter.ParseVramBlocks(targetFile).Select(b => b.Dst).Concat(staged).ToList();

        int bpp = (cut.Tpage >> 7) & 3;
        var (pageX, pageY) = TextureImporter.TpageOrigin(cut.Tpage);
        int intraX = cut.TexRect.X - pageX;

        VramRect texAt = default;
        int col = -1;
        for (int x = TextureImporter.RoomArenaX; x < TextureImporter.RoomArenaEnd; x += 64)
        {
            var cand = cut.TexRect with { X = x + intraX };
            if (cand.X + cand.W > TextureImporter.RoomArenaEnd) continue;
            if (occupied.Any(r => Intersects(r, cand))) continue;
            col = x; texAt = cand; break;
        }
        if (col < 0) return false;

        VramRect clutAt = default;
        int row = -1;
        foreach (int y in TextureImporter.RoomClutRows)
        {
            var cand = cut.ClutRect with { Y = y };
            if (occupied.Any(r => Intersects(r, cand))) continue;
            row = y; clutAt = cand; break;
        }
        if (row < 0) return false;

        placement = new PickupTexturePlacement(
            TextureImporter.MakeTpage(col, pageY, bpp),
            TextureImporter.MakeClut(clutAt.X, row),
            texAt, clutAt);
        return true;
    }

    private static bool Covers(VramRect outer, VramRect inner)
        => inner.X >= outer.X && inner.X + inner.W <= outer.X + outer.W
        && inner.Y >= outer.Y && inner.Y + inner.H <= outer.Y + outer.H;

    private static bool Intersects(VramRect a, VramRect b)
        => a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    /// <summary>Row-slice <paramref name="rect"/> (16bpp halfword coords) out of a parsed block.
    /// Columns of a width-padded rect beyond the block's right edge stay zero-filled.</summary>
    private static byte[] CutRect(VramBlock block, VramRect rect)
    {
        var pixels = new byte[rect.PixelBytes];
        int rowBytes = rect.W * 2;
        int coveredBytes = Math.Min(rect.W, block.Dst.X + block.Dst.W - rect.X) * 2;
        for (int r = 0; r < rect.H; r++)
        {
            int src = ((rect.Y - block.Dst.Y + r) * block.Dst.W + (rect.X - block.Dst.X)) * 2;
            Array.Copy(block.Pixels, src, pixels, r * rowBytes, coveredBytes);
        }
        return pixels;
    }
}
