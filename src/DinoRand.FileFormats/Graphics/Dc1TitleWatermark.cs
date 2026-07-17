using System.Buffers.Binary;

namespace DinoRand.FileFormats.Graphics;

/// <summary>
/// Draws the seed watermark into DC1's <c>Data\t_image.imd</c> — the main title screen, a bare
/// 16bpp TIM (magic <c>0x10</c>, type <c>0x02</c>, image rect <c>{0,0,320,240}</c>, 20-byte
/// header + 153,600 pixel bytes; docs/decisions/cross/SEED-WATERMARK-RESEARCH.md §3). The edit is
/// an in-place pixel blit: the output is always byte-length-identical to the input, and the
/// header is never touched. Fail-closed: anything not shaped exactly like the title TIM throws
/// (never risk corrupting an unexpected file).
/// </summary>
public static class Dc1TitleWatermark
{
    public const int Width = 320;
    public const int Height = 240;

    /// <summary>8-byte TIM header + 12-byte image-block header.</summary>
    public const int PixelOffset = 20;

    /// <summary>Exact on-disk size of the title TIM: header + 320×240 16-bit pixels.</summary>
    public const int FileSize = PixelOffset + Width * Height * 2;

    /// <summary>Anchor locked in SEED-WATERMARK-PLAN.md: (4,4), second line one LineHeight below.</summary>
    public const int AnchorX = 4;
    public const int AnchorY = 4;

    /// <summary>Return a watermarked copy of <paramref name="tim"/>; the input is never mutated.</summary>
    public static byte[] Apply(ReadOnlySpan<byte> tim, string line1, string line2)
    {
        if (tim.Length != FileSize)
            throw new InvalidDataException($"not the title TIM: {tim.Length} bytes (expected {FileSize})");
        if (BinaryPrimitives.ReadUInt32LittleEndian(tim) != 0x10)
            throw new InvalidDataException("not a TIM (bad magic)");
        if (BinaryPrimitives.ReadUInt32LittleEndian(tim.Slice(4)) != 0x02)
            throw new InvalidDataException("not a 16bpp direct-color TIM");
        if (BinaryPrimitives.ReadUInt16LittleEndian(tim.Slice(16)) != Width
            || BinaryPrimitives.ReadUInt16LittleEndian(tim.Slice(18)) != Height)
            throw new InvalidDataException("unexpected TIM dimensions");

        var output = tim.ToArray();
        var pixels = output.AsSpan(PixelOffset);
        BitmapFont.Blit(pixels, Width, Height, AnchorX, AnchorY, line1);
        BitmapFont.Blit(pixels, Width, Height, AnchorX, AnchorY + BitmapFont.LineHeight, line2);
        return output;
    }
}
