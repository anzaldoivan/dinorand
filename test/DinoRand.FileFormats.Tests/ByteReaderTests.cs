using DinoRand.FileFormats.Primitives;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>The little-endian cursor every table/record decoder reads through. A decode or
/// position-advance bug here mis-reads every offset downstream, so each read width is pinned
/// including sign boundaries.</summary>
public class ByteReaderTests
{
    [Fact]
    public void ReadU8_AdvancesOneByte()
    {
        var r = new ByteReader(new byte[] { 0xAB, 0xCD });
        Assert.Equal(0xAB, r.ReadU8());
        Assert.Equal(1, r.Position);
        Assert.Equal(0xCD, r.ReadU8());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void ReadU16_IsLittleEndian_AndAdvancesTwo()
    {
        var r = new ByteReader(new byte[] { 0x34, 0x12 });
        Assert.Equal(0x1234, r.ReadU16());
        Assert.Equal(2, r.Position);
    }

    [Fact]
    public void ReadU32_IsLittleEndian_AndAdvancesFour()
    {
        var r = new ByteReader(new byte[] { 0x78, 0x56, 0x34, 0x12 });
        Assert.Equal(0x12345678u, r.ReadU32());
        Assert.Equal(4, r.Position);
    }

    [Fact]
    public void ReadU32_TopBitSet_DoesNotSignExtend()
    {
        var r = new ByteReader(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        Assert.Equal(0xFFFFFFFFu, r.ReadU32());
    }

    [Fact]
    public void ReadI16_SignBoundaries()
    {
        // 0x7FFF, 0x8000, 0xFFFF — max positive, min negative, -1.
        var r = new ByteReader(new byte[] { 0xFF, 0x7F, 0x00, 0x80, 0xFF, 0xFF });
        Assert.Equal(short.MaxValue, r.ReadI16());
        Assert.Equal(short.MinValue, r.ReadI16());
        Assert.Equal((short)-1, r.ReadI16());
    }

    [Fact]
    public void ReadI32_SignBoundary()
    {
        var r = new ByteReader(new byte[] { 0x00, 0x00, 0x00, 0x80, 0xFF, 0xFF, 0xFF, 0xFF });
        Assert.Equal(int.MinValue, r.ReadI32());
        Assert.Equal(-1, r.ReadI32());
    }

    [Fact]
    public void ReadBytes_ReturnsSlice_AndAdvances()
    {
        var r = new ByteReader(new byte[] { 1, 2, 3, 4, 5 });
        r.Seek(1);
        Assert.Equal(new byte[] { 2, 3, 4 }, r.ReadBytes(3).ToArray());
        Assert.Equal(4, r.Position);
        Assert.False(r.EndOfData);
    }

    [Fact]
    public void Seek_MovesCursor_AndEndOfDataAtExactEnd()
    {
        var r = new ByteReader(new byte[] { 1, 2, 3 });
        Assert.Equal(3, r.Length);
        r.Seek(3);
        Assert.True(r.EndOfData);
        r.Seek(2);
        Assert.False(r.EndOfData);
        Assert.Equal(3, r.ReadU8());
    }

    [Fact]
    public void Reads_PastEnd_Throw()
    {
        Assert.Throws<IndexOutOfRangeException>(() =>
        {
            var r = new ByteReader(new byte[] { 1 });
            r.ReadU16();
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var r = new ByteReader(new byte[] { 1, 2 });
            r.ReadBytes(3);
        });
    }
}
