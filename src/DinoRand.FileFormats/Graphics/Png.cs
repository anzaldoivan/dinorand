using System.IO.Compression;

namespace DinoRand.FileFormats.Graphics;

/// <summary>A decoded PNG: <see cref="Channels"/> is 3 (RGB) or 4 (RGBA), rows top-down, no padding.</summary>
public sealed record PngImage(int Width, int Height, int Channels, byte[] Pixels);

/// <summary>
/// The PNG subset the Classic REbirth HD texture pack uses for DC1 room pages (<c>HD/&lt;ROOM&gt;/pNN.png</c>): 8-bit
/// RGB or RGBA, not interlaced. <see cref="Decode"/> returns null for anything else, so a caller can skip the page.
/// <see cref="Encode"/> writes the same subset with the Sub filter on every row.
/// </summary>
public static class Png
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static PngImage? Decode(byte[] png)
    {
        try { return DecodeOrThrow(png); }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException) { return null; }
    }

    private static PngImage? DecodeOrThrow(byte[] b)
    {
        if (b.Length < 8 || !b.AsSpan(0, 8).SequenceEqual(Signature)) return null;
        int w = 0, h = 0, c = 0;
        var idat = new MemoryStream();
        for (int p = 8; p + 8 <= b.Length;)
        {
            int len = ReadBe(b, p);
            var type = System.Text.Encoding.ASCII.GetString(b, p + 4, 4);
            if (type == "IHDR")
            {
                w = ReadBe(b, p + 8);
                h = ReadBe(b, p + 12);
                // bit depth 8, compression 0, filter method 0, no interlace; colour type 2 (RGB) or 6 (RGBA)
                if (b[p + 16] != 8 || b[p + 18] != 0 || b[p + 19] != 0 || b[p + 20] != 0) return null;
                c = b[p + 17] switch { 2 => 3, 6 => 4, _ => 0 };
                if (c == 0 || w <= 0 || h <= 0) return null;
            }
            else if (type == "IDAT") idat.Write(b, p + 8, len);
            else if (type == "IEND") break;
            p += 12 + len;
        }
        if (c == 0) return null;

        idat.Position = 0;
        var raw = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(raw);
        var r = raw.GetBuffer();
        int stride = w * c;
        if (raw.Length < (long)(stride + 1) * h) return null;
        var px = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            int filter = r[y * (stride + 1)], src = y * (stride + 1) + 1, dst = y * stride;
            if (filter > 4) return null;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= c ? px[dst + x - c] : 0;
                int up = y > 0 ? px[dst - stride + x] : 0;
                int ul = y > 0 && x >= c ? px[dst - stride + x - c] : 0;
                int pred = filter switch { 0 => 0, 1 => a, 2 => up, 3 => (a + up) >> 1, _ => Paeth(a, up, ul) };
                px[dst + x] = (byte)(r[src + x] + pred);
            }
        }
        return new PngImage(w, h, c, px);
    }

    public static byte[] Encode(PngImage img)
    {
        int stride = img.Width * img.Channels, c = img.Channels;
        var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[stride + 1];
            row[0] = 1; // Sub
            for (int y = 0; y < img.Height; y++)
            {
                int o = y * stride;
                for (int x = 0; x < stride; x++)
                    row[x + 1] = (byte)(img.Pixels[o + x] - (x >= c ? img.Pixels[o + x - c] : 0));
                z.Write(row);
            }
        }
        var header = new byte[13];
        WriteBe(header, 0, img.Width);
        WriteBe(header, 4, img.Height);
        header[8] = 8;
        header[9] = (byte)(c == 4 ? 6 : 2);
        var o2 = new MemoryStream();
        o2.Write(Signature);
        WriteChunk(o2, "IHDR", header);
        WriteChunk(o2, "IDAT", deflated.ToArray());
        WriteChunk(o2, "IEND", Array.Empty<byte>());
        return o2.ToArray();
    }

    /// <summary>
    /// Copy the <paramref name="sSize"/>² cell at (<paramref name="sx"/>, <paramref name="sy"/>) of <paramref name="src"/>
    /// over the <paramref name="dSize"/>² cell at (<paramref name="dx"/>, <paramref name="dy"/>) of <paramref name="dst"/>,
    /// leaving a <paramref name="border"/>-pixel frame of the destination. Shrinking averages each destination pixel's
    /// source box; enlarging is bilinear. RGB is copied; a 4-channel destination keeps the source alpha, or 255.
    /// </summary>
    public static void CopyCell(PngImage src, int sx, int sy, int sSize, PngImage dst, int dx, int dy, int dSize, int border)
    {
        double scale = (double)sSize / dSize;
        Span<double> acc = stackalloc double[4];
        for (int y = border; y < dSize - border; y++)
            for (int x = border; x < dSize - border; x++)
            {
                acc.Clear();
                if (scale >= 1)
                {
                    int x0 = (int)(x * scale), x1 = Math.Max(x0 + 1, (int)Math.Ceiling((x + 1) * scale));
                    int y0 = (int)(y * scale), y1 = Math.Max(y0 + 1, (int)Math.Ceiling((y + 1) * scale));
                    for (int v = y0; v < y1; v++)
                        for (int u = x0; u < x1; u++)
                            Add(acc, src, sx + u, sy + v, 1);
                    int n = (x1 - x0) * (y1 - y0);
                    for (int k = 0; k < 4; k++) acc[k] /= n;
                }
                else
                {
                    double fx = Math.Clamp((x + 0.5) * scale - 0.5, 0, sSize - 1);
                    double fy = Math.Clamp((y + 0.5) * scale - 0.5, 0, sSize - 1);
                    int u0 = (int)fx, v0 = (int)fy, u1 = Math.Min(u0 + 1, sSize - 1), v1 = Math.Min(v0 + 1, sSize - 1);
                    double tx = fx - u0, ty = fy - v0;
                    Add(acc, src, sx + u0, sy + v0, (1 - tx) * (1 - ty));
                    Add(acc, src, sx + u1, sy + v0, tx * (1 - ty));
                    Add(acc, src, sx + u0, sy + v1, (1 - tx) * ty);
                    Add(acc, src, sx + u1, sy + v1, tx * ty);
                }
                int o = ((dy + y) * dst.Width + dx + x) * dst.Channels;
                for (int k = 0; k < 3; k++) dst.Pixels[o + k] = (byte)Math.Round(acc[k]);
                if (dst.Channels == 4) dst.Pixels[o + 3] = (byte)Math.Round(acc[3]);
            }
    }

    private static void Add(Span<double> acc, PngImage img, int x, int y, double weight)
    {
        int i = (y * img.Width + x) * img.Channels;
        for (int k = 0; k < 3; k++) acc[k] += img.Pixels[i + k] * weight;
        acc[3] += (img.Channels == 4 ? img.Pixels[i + 3] : 255) * weight;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static int ReadBe(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    private static void WriteBe(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var buf = new byte[data.Length + 12];
        WriteBe(buf, 0, data.Length);
        System.Text.Encoding.ASCII.GetBytes(type, 0, 4, buf, 4);
        data.CopyTo(buf, 8);
        WriteBe(buf, data.Length + 8, (int)Crc32(buf.AsSpan(4, data.Length + 4)));
        s.Write(buf);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var x in data) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return ~c;
    }
}
