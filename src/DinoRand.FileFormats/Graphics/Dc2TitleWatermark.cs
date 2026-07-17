using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;

namespace DinoRand.FileFormats.Graphics;

/// <summary>
/// Draws the seed watermark into DC2's <c>TITLE.DAT</c> / <c>TITLE2.DAT</c> static title
/// background: the LZSS0 Gian entry that decompresses to exactly one raw 320×240 15-bit page
/// (K109 — TITLE2.DAT carries a second, small LZSS0 table, so selection is <b>by decompressed
/// size</b>, never positional). Decompress → blit → recompress → repack; the recompressed
/// payload may change size, which the sector-cursor container layout tolerates
/// (docs/decisions/cross/SEED-WATERMARK-PLAN.md).
/// </summary>
public static class Dc2TitleWatermark
{
    public const int Width = 320;
    public const int Height = 240;

    /// <summary>A full-screen raw 15-bit page: 320×240×2.</summary>
    public const int PageBytes = Width * Height * 2;

    /// <summary>Anchor locked in SEED-WATERMARK-PLAN.md: (4,4), second line one LineHeight below.</summary>
    public const int AnchorX = 4;
    public const int AnchorY = 4;

    /// <summary>Return a watermarked copy of the package; the input is never mutated. Throws when
    /// the bytes are not a Gian package holding a full-screen LZSS0 background (fail-closed).</summary>
    public static byte[] Apply(ReadOnlySpan<byte> package, string line1, string line2)
    {
        var pkg = GianPackage.TryParse(package)
                  ?? throw new InvalidDataException("not a Gian package");

        for (int i = 0; i < pkg.Entries.Count; i++)
        {
            var entry = pkg.Entries[i];
            if (entry.Type != GianEntryType.Lzss0) continue;

            var page = Lzss.Decompress(package.Slice(entry.PayloadOffset, (int)entry.DeclaredSize));
            if (page.Length != PageBytes) continue;

            BitmapFont.Blit(page, Width, Height, AnchorX, AnchorY, line1);
            BitmapFont.Blit(page, Width, Height, AnchorX, AnchorY + BitmapFont.LineHeight, line2);
            return PackageRepacker.ReplaceEntryDc2(package, i, Lzss.Compress(page));
        }

        throw new InvalidDataException("no full-screen LZSS0 background entry in this package");
    }
}
