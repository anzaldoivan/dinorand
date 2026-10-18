using System.IO.Compression;
using DinoRand.FileFormats.Graphics;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>The PNG subset of the Classic REbirth HD texture pack (<see cref="Png"/>): decoding each row filter,
/// round-tripping, refusing other formats, and the cell blit used to swap emergency-box art.</summary>
public class PngTests
{
    private static PngImage Pattern(int w, int h, int c)
    {
        var px = new byte[w * h * c];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)(i * 37 + (i / (w * c)) * 11);
        return new PngImage(w, h, c, px);
    }

    /// <summary>A PNG whose every row uses <paramref name="filter"/>, built independently of <see cref="Png.Encode"/>
    /// (CRCs left zero: the decoder does not check them).</summary>
    private static byte[] Build(PngImage img, byte filter, byte colourType = 0, byte bitDepth = 8, byte interlace = 0)
    {
        int c = img.Channels, stride = img.Width * c;
        var raw = new MemoryStream();
        for (int y = 0; y < img.Height; y++)
        {
            raw.WriteByte(filter);
            for (int x = 0; x < stride; x++)
            {
                int a = x >= c ? img.Pixels[y * stride + x - c] : 0;
                int up = y > 0 ? img.Pixels[(y - 1) * stride + x] : 0;
                int ul = y > 0 && x >= c ? img.Pixels[(y - 1) * stride + x - c] : 0;
                int p = a + up - ul, pa = Math.Abs(p - a), pb = Math.Abs(p - up), pc = Math.Abs(p - ul);
                int pred = filter switch
                {
                    0 => 0, 1 => a, 2 => up, 3 => (a + up) / 2,
                    _ => pa <= pb && pa <= pc ? a : pb <= pc ? up : ul,
                };
                raw.WriteByte((byte)(img.Pixels[y * stride + x] - pred));
            }
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(raw.ToArray());
        var o = new MemoryStream();
        o.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string t, byte[] d)
        {
            o.Write(new[] { (byte)(d.Length >> 24), (byte)(d.Length >> 16), (byte)(d.Length >> 8), (byte)d.Length });
            o.Write(System.Text.Encoding.ASCII.GetBytes(t));
            o.Write(d);
            o.Write(new byte[4]);
        }
        var hdr = new byte[13];
        hdr[3] = (byte)img.Width; hdr[7] = (byte)img.Height; hdr[8] = bitDepth;
        hdr[9] = colourType != 0 ? colourType : (byte)(c == 4 ? 6 : 2);
        hdr[12] = interlace;
        Chunk("IHDR", hdr);
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return o.ToArray();
    }

    [Theory]
    [InlineData(0, 3)] [InlineData(1, 3)] [InlineData(2, 3)] [InlineData(3, 3)] [InlineData(4, 3)]
    [InlineData(0, 4)] [InlineData(4, 4)]
    public void Decode_ReadsEachFilter(byte filter, int channels)
    {
        var img = Pattern(13, 7, channels);
        var got = Png.Decode(Build(img, filter));
        Assert.NotNull(got);
        Assert.Equal((13, 7, channels), (got!.Width, got.Height, got.Channels));
        Assert.Equal(img.Pixels, got.Pixels);
    }

    [Theory]
    [InlineData(3)] [InlineData(4)]
    public void Encode_RoundTrips(int channels)
    {
        var img = Pattern(40, 9, channels);
        var png = Png.Encode(img);
        var got = Png.Decode(png)!;
        Assert.Equal(img.Pixels, got.Pixels);
        // IHDR's CRC is the standard one over type + data.
        Assert.Equal(new byte[] { 0x49, 0x48, 0x44, 0x52 }, png[12..16]);
    }

    [Fact]
    public void Decode_RefusesOtherFormats()
    {
        var img = Pattern(4, 4, 3);
        Assert.Null(Png.Decode(Build(img, 0, colourType: 3)));   // palette
        Assert.Null(Png.Decode(Build(img, 0, bitDepth: 16)));
        Assert.Null(Png.Decode(Build(img, 0, interlace: 1)));
        Assert.Null(Png.Decode(Build(img, 5)));                  // no such filter
        Assert.Null(Png.Decode(new byte[] { 1, 2, 3 }));
        var truncated = Build(img, 0);
        Assert.Null(Png.Decode(truncated[..40]));
    }

    [Fact]
    public void CopyCell_KeepsTheBorder_AndCopiesTheInside()
    {
        var src = new PngImage(8, 8, 3, Enumerable.Repeat((byte)200, 8 * 8 * 3).ToArray());
        var dst = new PngImage(10, 10, 4, new byte[10 * 10 * 4]);
        Png.CopyCell(src, 0, 0, 8, dst, 1, 1, 8, border: 2);
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 10; x++)
            {
                bool inside = x >= 3 && x < 7 && y >= 3 && y < 7;
                int i = (y * 10 + x) * 4;
                Assert.Equal(inside ? 200 : 0, dst.Pixels[i]);
                Assert.Equal(inside ? 255 : 0, dst.Pixels[i + 3]);
            }
    }

    [Fact]
    public void CopyCell_BoxFiltersWhenShrinking_AndInterpolatesWhenEnlarging()
    {
        // A 1-pixel checker of 0 and 100 averages to 50 at half size.
        var checker = new PngImage(8, 8, 3, new byte[8 * 8 * 3]);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            checker.Pixels[(y * 8 + x) * 3] = (byte)((x + y) % 2 * 100);
        var small = new PngImage(4, 4, 3, new byte[4 * 4 * 3]);
        Png.CopyCell(checker, 0, 0, 8, small, 0, 0, 4, border: 0);
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal(50, small.Pixels[i * 3]));

        // A left-to-right ramp stays monotonic and within its ends at double size.
        var ramp = new PngImage(4, 4, 3, new byte[4 * 4 * 3]);
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) ramp.Pixels[(y * 4 + x) * 3 + 1] = (byte)(x * 60);
        var big = new PngImage(8, 8, 3, new byte[8 * 8 * 3]);
        Png.CopyCell(ramp, 0, 0, 4, big, 0, 0, 8, border: 0);
        var row = Enumerable.Range(0, 8).Select(x => (int)big.Pixels[(3 * 8 + x) * 3 + 1]).ToArray();
        Assert.Equal(new[] { 0, 15, 45, 75, 105, 135, 165, 180 }, row);
    }
}
