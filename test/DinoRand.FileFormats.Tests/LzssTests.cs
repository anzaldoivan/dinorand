using DinoRand.FileFormats.Compression;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public class LzssTests
{
    [Fact]
    public void RoundTrips_EmptyInput()
    {
        Assert.Empty(Lzss.Decompress(Lzss.Compress(Array.Empty<byte>())));
    }

    [Fact]
    public void RoundTrips_Literals()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 200, 255, 0 };
        Assert.Equal(data, Lzss.Decompress(Lzss.Compress(data)));
    }

    [Fact]
    public void RoundTrips_HighlyRepetitive()
    {
        var data = new byte[2048];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 4); // long runs → matches
        var roundTripped = Lzss.Decompress(Lzss.Compress(data));
        Assert.Equal(data, roundTripped);
    }

    [Fact]
    public void Compresses_RepetitiveData_SmallerThanInput()
    {
        var data = new byte[1024];
        Array.Fill(data, (byte)0xAA); // maximally compressible
        var compressed = Lzss.Compress(data);
        Assert.True(compressed.Length < data.Length,
            $"expected compression, got {compressed.Length} >= {data.Length}");
    }

    [Fact]
    public void RoundTrips_PseudoRandom()
    {
        var rng = new Random(1234);
        var data = new byte[4096];
        rng.NextBytes(data);
        Assert.Equal(data, Lzss.Decompress(Lzss.Compress(data)));
    }
}
