namespace DinoRand.FileFormats.Primitives;

/// <summary>
/// Minimal cursor over a byte span. Dino Crisis (PS1-derived PC port) stores
/// multi-byte integers little-endian, matching x86, so no byte-swapping is needed.
/// </summary>
public ref struct ByteReader
{
    private readonly ReadOnlySpan<byte> _data;

    public ByteReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        Position = 0;
    }

    public int Position { get; set; }
    public int Length => _data.Length;
    public bool EndOfData => Position >= _data.Length;

    public byte ReadU8() => _data[Position++];

    public ushort ReadU16()
    {
        ushort v = (ushort)(_data[Position] | (_data[Position + 1] << 8));
        Position += 2;
        return v;
    }

    public uint ReadU32()
    {
        uint v = (uint)(_data[Position]
                        | (_data[Position + 1] << 8)
                        | (_data[Position + 2] << 16)
                        | (_data[Position + 3] << 24));
        Position += 4;
        return v;
    }

    public short ReadI16() => (short)ReadU16();
    public int ReadI32() => (int)ReadU32();

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        var slice = _data.Slice(Position, count);
        Position += count;
        return slice;
    }

    public void Seek(int position) => Position = position;
}
