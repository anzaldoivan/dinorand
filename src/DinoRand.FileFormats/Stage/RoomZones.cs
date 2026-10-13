namespace DinoRand.FileFormats.Stage;

/// <summary>An XZ quad: four (x, z) corner pairs as stored in the room data.</summary>
public readonly record struct ZoneQuad(short X0, short Z0, short X1, short Z1, short X2, short Z2, short X3, short Z3);

/// <summary>One camera zone record. <see cref="CameraId"/> (byte 2) is read as the camera the zone
/// selects — unverified: ids repeat and are not bounded by the zone count, so only the record size
/// and the quad are established.</summary>
public readonly record struct CameraZone(byte CameraId, byte FloorGroup, ZoneQuad Area);

/// <summary>One floor zone: a quad on floor <see cref="Level"/> with footstep sound <see cref="Step"/>.</summary>
public readonly record struct FloorZone(ZoneQuad Area, byte Level, byte Step);

/// <summary>A camera set by the room script (<c>0x2E</c> or <c>0x4C</c>): the two xyz triples the
/// handler passes to the engine's camera-position and camera-target setters
/// (<c>0x421F97</c> / <c>0x422051</c>).</summary>
public readonly record struct CameraShot(byte Opcode, int FileOffset,
    short EyeX, short EyeY, short EyeZ, short TargetX, short TargetY, short TargetZ)
{
    /// <summary>Offset of the position triple in both opcodes.</summary>
    public const int EyeOffset = 2;
    /// <summary>Offset of the target triple in a <c>0x2E</c> record.</summary>
    public const int ShortTargetOffset = 8;
    /// <summary>Offset of the target triple in a <c>0x4C</c> record.</summary>
    public const int LongTargetOffset = 0x12;
}

/// <summary>
/// Read-only parsers for two tables of a decompressed DC1 room buffer
/// (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.99). The buffer opens with seven PSX
/// pointers; pointer 3 is the camera-zone table and pointer 6 the floor-zone table.
///
/// <list type="bullet">
/// <item><b>Camera zones</b> — <c>u32 count</c>, then 68-byte records: camera id at <c>+2</c>,
/// floor group at <c>+3</c>, quad at <c>+4..+19</c>. The table ends exactly at pointer 4 in all
/// 148 rooms.</item>
/// <item><b>Floor zones</b> — a count <b>byte</b> (three padding bytes follow, not always zero),
/// then 20-byte records: quad at <c>+0..+15</c>, level at <c>+16</c>, step at <c>+17</c>. The table
/// ends exactly at pointer 5 in 147 of 148 rooms (<c>st60e</c> stores its tables in another
/// order).</item>
/// </list>
/// Both return an empty list when the pointers or sizes do not fit the buffer.
/// </summary>
public static class RoomZones
{
    private const int CameraRecord = 68;
    private const int FloorRecord = 20;

    public static IReadOnlyList<CameraZone> ReadCameraZones(ReadOnlySpan<byte> rdt)
    {
        if (!TryPointer(rdt, 3, out int at) || at + 4 > rdt.Length) return Array.Empty<CameraZone>();
        uint count = U32(rdt, at);
        if (count > 256 || at + 4 + (long)count * CameraRecord > rdt.Length) return Array.Empty<CameraZone>();

        var zones = new CameraZone[count];
        for (int i = 0; i < count; i++)
        {
            int rec = at + 4 + i * CameraRecord;
            zones[i] = new CameraZone(rdt[rec + 2], rdt[rec + 3], Quad(rdt, rec + 4));
        }
        return zones;
    }

    public static IReadOnlyList<FloorZone> ReadFloorZones(ReadOnlySpan<byte> rdt)
    {
        if (!TryPointer(rdt, 6, out int at) || at + 4 > rdt.Length) return Array.Empty<FloorZone>();
        int count = rdt[at];
        if (at + 4 + count * FloorRecord > rdt.Length) return Array.Empty<FloorZone>();

        var zones = new FloorZone[count];
        for (int i = 0; i < count; i++)
        {
            int rec = at + 4 + i * FloorRecord;
            zones[i] = new FloorZone(Quad(rdt, rec), rdt[rec + 16], rdt[rec + 17]);
        }
        return zones;
    }

    /// <summary>Buffer offset of header pointer <paramref name="index"/> (0-6), if it lands inside.</summary>
    public static bool TryPointer(ReadOnlySpan<byte> rdt, int index, out int offset)
    {
        offset = -1;
        if (rdt.Length < 7 * 4) return false;
        long at = (long)U32(rdt, index * 4) - RoomScript.PsxRdtBase;
        if (at < 0 || at >= rdt.Length) return false;
        offset = (int)at;
        return true;
    }

    private static ZoneQuad Quad(ReadOnlySpan<byte> b, int at) => new(
        S16(b, at), S16(b, at + 2), S16(b, at + 4), S16(b, at + 6),
        S16(b, at + 8), S16(b, at + 10), S16(b, at + 12), S16(b, at + 14));

    private static short S16(ReadOnlySpan<byte> b, int at) => (short)(b[at] | (b[at + 1] << 8));

    private static uint U32(ReadOnlySpan<byte> b, int at)
        => (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
}
