namespace DinoRand.FileFormats.Stage;

/// <summary>
/// Entry type ids for the PC ("DC2 PC", shared by the DC1 PC build) package format.
/// Clean-room from the documented format (community RE of <c>stNXX.dat</c>); these are
/// interoperability facts, not copied code.
/// </summary>
public enum GianEntryType : uint
{
    Data = 0,     // GPC_DATA   — generic literal data (room RDT can live here)
    Texture = 1,  // GPC_TEXTURE
    Palette = 2,  // GPC_PALETTE
    Sound = 3,    // GPC_SOUND
    Mp3 = 4,      // GPC_MP3
    Lzss0 = 5,    // GPC_LZSS0  — compressed data
    Lzss1 = 6,    // GPC_LZSS1  — compressed texture
    Unknown = 7,  // GPC_UNK    — room RDT in most DC1 rooms; raw (uncompressed)
    Lzss2 = 8,    // GPC_LZSS2  — another compressed texture
}

/// <summary>One entry in a Gian package: its type plus the byte span of its payload.</summary>
public readonly struct GianEntry
{
    public GianEntry(GianEntryType type, uint declaredSize, int payloadOffset, int alignedSize)
    {
        Type = type;
        DeclaredSize = declaredSize;
        PayloadOffset = payloadOffset;
        AlignedSize = alignedSize;
    }

    /// <summary>Entry type (<see cref="GianEntryType"/>).</summary>
    public GianEntryType Type { get; }

    /// <summary>Logical payload size in bytes, as declared in the entry header.</summary>
    public uint DeclaredSize { get; }

    /// <summary>Byte offset of this entry's payload within the package file.</summary>
    public int PayloadOffset { get; }

    /// <summary>Sector-padded size (declared size rounded up to 2048) the next entry starts after.</summary>
    public int AlignedSize { get; }

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
/// <b>last entry</b> (raw <c>GPC_UNK</c> or <c>GPC_DATA</c> in the DC1 PC build).
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

    /// <summary>
    /// The room object/script segment = the last entry. Null if the package has no
    /// entries (which would indicate an unrecognized file).
    /// </summary>
    public GianEntry? RoomDataEntry => Entries.Count > 0 ? Entries[^1] : null;

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
            entries.Add(new GianEntry((GianEntryType)type, size, pos, aligned));
            pos += aligned;
            if (pos > bytes.Length) break;
        }

        return entries.Count > 0 ? new GianPackage(entrySize, isDc2, entries) : null;
    }

    private static uint ReadU32(ReadOnlySpan<byte> b, int off)
        => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
}
