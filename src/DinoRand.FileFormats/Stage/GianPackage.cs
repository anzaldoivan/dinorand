namespace DinoRand.FileFormats.Stage;

/// <summary>
/// Entry type ids for the PC package format. Clean-room from the documented format (community RE
/// of <c>stNXX.dat</c>); these are interoperability facts, not copied code.
///
/// <para>The member <b>names follow the DC2 numbering</b>. DC1 uses the same ids 0-2 but different
/// meanings from 3 up (verified on all 465 DC1 <c>.dat</c> files, STATIC-SCD-RE cont.96); the DC1
/// meaning is given per member. Types 5 and 6 never occur in DC1 room files.</para>
/// </summary>
public enum GianEntryType : uint
{
    Data = 0,     // raw data (DC1: raw room data in 2 rooms, sound tables, code overlays)
    Texture = 1,  // raw texture
    Palette = 2,  // raw palette
    Sound = 3,    // DC2 GPC_SOUND   | DC1: PS1 sound header ("Gian" tone table)
    Mp3 = 4,      // DC2 GPC_MP3     | DC1: PS1 ADPCM sample bank
    Lzss0 = 5,    // DC2 LZSS data   | DC1: sequence-like data (not LZSS)
    Lzss1 = 6,    // DC2 LZSS texture| DC1: unused
    Unknown = 7,  // DC2 GPC_UNK     | DC1: LZSS data — the room data in all but 2 rooms
    Lzss2 = 8,    // LZSS texture (both games)
}

/// <summary>One entry in a Gian package: its type plus the byte span of its payload.</summary>
public readonly struct GianEntry
{
    public GianEntry(GianEntryType type, uint declaredSize, int payloadOffset, int alignedSize,
                     uint? loadAddress = null)
    {
        Type = type;
        DeclaredSize = declaredSize;
        PayloadOffset = payloadOffset;
        AlignedSize = alignedSize;
        LoadAddress = loadAddress;
    }

    /// <summary>Entry type (<see cref="GianEntryType"/>).</summary>
    public GianEntryType Type { get; }

    /// <summary>Logical payload size in bytes, as declared in the entry header.</summary>
    public uint DeclaredSize { get; }

    /// <summary>Byte offset of this entry's payload within the package file.</summary>
    public int PayloadOffset { get; }

    /// <summary>Sector-padded size (declared size rounded up to 2048) the next entry starts after.</summary>
    public int AlignedSize { get; }

    /// <summary>
    /// DC1 only: the PSX RAM address this entry is loaded at, when its header carries one
    /// (<see cref="GianPackage.LoadAddressOf"/>). Null for VRAM-rect entries (textures, palettes)
    /// and for every DC2 entry.
    /// </summary>
    public uint? LoadAddress { get; }

    public override string ToString() => $"{Type} @0x{PayloadOffset:X} ({DeclaredSize} B)";
}

/// <summary>
/// Parser for the Dino Crisis "Gian package" container that backs the per-room
/// <c>stNXX.dat</c> files. Layout (clean-room from documented format facts —
/// see <c>docs/decisions/dc1/scd/OPCODE-RESEARCH.md</c>):
///
/// <list type="bullet">
/// <item>A fixed <b>2048-byte header</b> of fixed-size entries — <b>16 bytes for DC1</b>,
/// 32 bytes for DC2 (detected by a zero-field probe at offset 16).</item>
/// <item>Each entry: <c>u32 type; u32 size;</c> (+ reserve / gfx coords). Payloads begin
/// at offset 2048 and each advances the cursor by <c>align(size, 2048)</c>.</item>
/// <item>Iteration stops at the first entry whose type is outside the known set, or when
/// the 2048-byte entry table is exhausted.</item>
/// </list>
///
/// This parser is <b>read-only and non-mutating</b>: it records each entry's raw payload
/// span (it does not decompress LZSS or unswizzle textures), so the original file bytes
/// are preserved verbatim for a byte-exact round-trip. The room object/script data is the
/// entry loaded at <see cref="RoomDataLoadAddress"/> (<see cref="RoomDataIndex"/>) — the last
/// entry in 107 of the 148 DC1 rooms, followed by sound entries in the other 41.
/// </summary>
public sealed class GianPackage
{
    /// <summary>Fixed header size in bytes; payloads start here.</summary>
    public const int HeaderSize = 2048;

    /// <summary>Sector size payload sizes are rounded up to.</summary>
    public const int SectorSize = 2048;

    /// <summary>DC1 entry stride in the header table.</summary>
    public const int Dc1EntrySize = 16;

    /// <summary>DC2 entry stride in the header table.</summary>
    public const int Dc2EntrySize = 32;

    private GianPackage(int entrySize, bool isDc2, IReadOnlyList<GianEntry> entries)
    {
        EntrySize = entrySize;
        IsDc2 = isDc2;
        Entries = entries;
    }

    /// <summary>Entry stride: 16 (DC1) or 32 (DC2).</summary>
    public int EntrySize { get; }

    /// <summary>True if the 32-byte (DC2) entry layout was detected.</summary>
    public bool IsDc2 { get; }

    /// <summary>Parsed entries in file order.</summary>
    public IReadOnlyList<GianEntry> Entries { get; }

    /// <summary>PSX RAM address every DC1 room's object/script data is loaded at.</summary>
    public const uint RoomDataLoadAddress = 0x80100000;

    /// <summary>
    /// Decode the PSX load address a DC1 entry header carries in its x/y words: when
    /// <c>y &amp; 0x8000</c>, the address is <c>((y &amp; 0x7FFF) &lt;&lt; 16) | x | 0x80000000</c>;
    /// otherwise x/y is a VRAM rect origin and there is no load address.
    /// </summary>
    public static uint? LoadAddressOf(int x, int y)
        => (y & 0x8000) != 0 ? (uint)(((y & 0x7FFF) << 16) | (x & 0xFFFF)) | 0x80000000u : null;

    /// <summary>
    /// Index of the room object/script entry: the data entry loaded at
    /// <see cref="RoomDataLoadAddress"/>. Falls back to the last entry when no entry carries that
    /// address (DC2, and packages without load addresses). -1 for an empty package.
    /// </summary>
    public int RoomDataIndex
    {
        get
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
                if (Entries[i].LoadAddress == RoomDataLoadAddress
                    && Entries[i].Type is GianEntryType.Data or GianEntryType.Unknown)
                    return i;
            return Entries.Count - 1;
        }
    }

    /// <summary>
    /// The room object/script segment (<see cref="RoomDataIndex"/>). Null if the package has no
    /// entries (which would indicate an unrecognized file).
    /// </summary>
    public GianEntry? RoomDataEntry => Entries.Count > 0 ? Entries[RoomDataIndex] : null;

    private static int Align(uint value, int sector) => (int)((value + (uint)(sector - 1)) & ~(uint)(sector - 1));

    /// <summary>
    /// Try to parse <paramref name="bytes"/> as a Gian package. Returns null if the bytes
    /// are too short or no valid entry can be read (caller then treats the file as opaque).
    /// </summary>
    public static GianPackage? TryParse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize) return null;

        // Detect DC1 (16-byte) vs DC2 (32-byte) entries: DC2 zero-pads the second half of
        // each 32-byte entry, so the four u32s at offset 16 are all zero for the first entry.
        bool isDc2 = bytes.Length >= 32
                     && ReadU32(bytes, 16) == 0 && ReadU32(bytes, 20) == 0
                     && ReadU32(bytes, 24) == 0 && ReadU32(bytes, 28) == 0;
        int entrySize = isDc2 ? Dc2EntrySize : Dc1EntrySize;
        int slots = HeaderSize / entrySize;

        var entries = new List<GianEntry>();
        int pos = HeaderSize;
        for (int i = 0; i < slots; i++)
        {
            int entryOff = i * entrySize;
            uint type = ReadU32(bytes, entryOff);
            uint size = ReadU32(bytes, entryOff + 4);

            // Unknown type → end of the entry table (matches the game's switch default).
            if (type > (uint)GianEntryType.Lzss2) break;
            // A zero-size or out-of-range payload terminates the table on the real files.
            if (size == 0) break;
            if (pos + (long)size > bytes.Length) break;

            int aligned = Align(size, SectorSize);
            uint? load = isDc2 ? null : LoadAddressOf(
                bytes[entryOff + 8] | (bytes[entryOff + 9] << 8),
                bytes[entryOff + 10] | (bytes[entryOff + 11] << 8));
            entries.Add(new GianEntry((GianEntryType)type, size, pos, aligned, load));
            pos += aligned;
            if (pos > bytes.Length) break;
        }

        return entries.Count > 0 ? new GianPackage(entrySize, isDc2, entries) : null;
    }

    private static uint ReadU32(ReadOnlySpan<byte> b, int off)
        => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
}
