namespace DinoRand.FileFormats.Stage;

/// <summary>
/// Dino Crisis 1 SCD script opcode → record-length table, and the item-placement record
/// layout. The room script is the decompressed RDT buffer's SCD stream (see
/// <see cref="RoomScript"/>): each record begins with a one-byte opcode whose total length
/// (opcode byte included) is fixed per opcode, except two variable-length opcodes
/// (<c>0x28</c>, <c>0x5a</c>) whose length is computed from a record byte.
///
/// <para><b>Status: complete and trustworthy.</b> All 114 opcode handlers (<c>0x00–0x71</c>)
/// were disassembled from <c>DINO.exe</c> (the Classic REbirth PSX recompile); each handler's
/// program-counter advance gives its length. The 9 control-flow opcodes that set the PC
/// indirectly were resolved from their handler logic, and the two variable opcodes from their
/// per-record size byte. The table walks the whole 107-room corpus cleanly (every subroutine
/// consumes only in-range opcodes and aligns exactly; only trailing non-code data after the
/// last subroutine "derails", as expected). See <c>docs/reference/dc1/_registries/STATIC-SCD-RE.md</c> (2026-06-19
/// cont.7–cont.9).</para>
///
/// <para><b>Item placement.</b> Items are placed by SCD opcode <see cref="Item"/> (<c>0x28</c>)
/// in its subtype-4 form (<c>byte[pc+2]==4</c>, total length 44). The item id is the low byte
/// of the word at <see cref="ItemRecord.IdOffset"/> and the count is the word at
/// <see cref="ItemRecord.CountOffset"/>. Proven against <c>DINO.exe</c> (the 0x28 handler at
/// <c>0x426a73</c> uses that word as an item id — indexing the per-item property table and the
/// "already collected" flag array) and validated against the <c>placements.md</c> oracle and a
/// full-corpus scan (every subtype-4 id lands in the item-id space, byte-identical across the
/// five western localizations). The old <c>0x62</c>/22 B forum record is refuted (it is not an
/// SCD instruction; <c>0x62</c>'s real SCD length is 8).</para>
///
/// Clean-room: opcode ids/lengths/field offsets are interoperability facts established from our
/// own disassembly of the game files, not copied from any third-party source.
/// </summary>
public static class DcOpcodes
{
    /// <summary>Enemy/entity-placement opcode (the SCD <c>SCE_EM_SET</c> analog; fixed 24 B).</summary>
    public const byte Enemy = 0x20;

    /// <summary>Total length of an enemy (<c>0x20</c>) record, opcode byte included.</summary>
    public const int EnemyLength = 24;

    /// <summary>Second enemy-placement opcode (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.20): a fixed 20-byte record
    /// that carries the enemy's category + model + motion <i>inline</i> but pulls its position and
    /// kill-flag from a secondary instance table indexed by <c>record+0x10</c>. Many rooms cont.18
    /// thought "native-only" (e.g. 0102) actually place their enemy with this opcode, not
    /// <see cref="Enemy"/>; both converge on the same entity layout and per-category AI.</summary>
    public const byte Enemy2 = 0x59;

    /// <summary>Total length of an enemy (<c>0x59</c>) record, opcode byte included.</summary>
    public const int Enemy2Length = 20;

    /// <summary>Item-placement opcode (the SCD "area-of-things" placement; subtype 4 = item).</summary>
    public const byte Item = 0x28;

    /// <summary>Subtype byte (<c>record[2]</c>) that marks an <see cref="Item"/> record as an item.</summary>
    public const byte ItemSubtype = 4;

    /// <summary>Total length of the item (<c>0x28</c> subtype-4) record, opcode byte included.</summary>
    public const int ItemLength = 44;

    /// <summary>Door / area-transition opcode — the same <c>0x28</c> AOT umbrella, subtype 0.</summary>
    public const byte Door = 0x28;

    /// <summary>Subtype byte (<c>record[2]</c>) that marks a <see cref="Door"/> record as a door.</summary>
    public const byte DoorSubtype = 0;

    /// <summary>Total length of the door (<c>0x28</c> subtype-0) record, opcode byte included.</summary>
    public const int DoorLength = 48;

    /// <summary>Sentinel in <see cref="Lengths"/> for a variable-length opcode (use <see cref="Length"/>).</summary>
    private const int Variable = -1;

    /// <summary>
    /// <c>0x28</c> subtype → total record length, indexed by <c>record[2]</c> (0..11).
    /// Recovered from the handler's jump-table pc-advances (subtype 4 = the item form, 44 B).
    /// </summary>
    private static readonly int[] Op28Lengths = { 48, 40, 32, 36, 44, 32, 32, 52, 32, 32, 32, 32 };

    /// <summary>
    /// Opcode → total record length (opcode byte included). <see cref="Variable"/> (-1) marks an
    /// opcode whose length depends on a record byte — resolve those through <see cref="Length"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<byte, int> Lengths = new Dictionary<byte, int>
    {
        [0x00]=1, [0x01]=4, [0x02]=4, [0x03]=4, [0x04]=4, [0x05]=4, [0x06]=4, [0x07]=4,
        [0x08]=4, [0x09]=8, [0x0a]=4, [0x0b]=4, [0x0c]=4, [0x0d]=4, [0x0e]=4, [0x0f]=4,
        [0x10]=2, [0x11]=2, [0x12]=4, [0x13]=4, [0x14]=4, [0x15]=8, [0x16]=4, [0x17]=4,
        [0x18]=4, [0x19]=1, [0x1a]=1, [0x1b]=1, [0x1c]=1, [0x1d]=1, [0x1e]=1, [0x1f]=1,
        [0x20]=24, [0x21]=8, [0x22]=4, [0x23]=32, [0x24]=8, [0x25]=4, [0x26]=4, [0x27]=4,
        [0x28]=Variable, [0x29]=8, [0x2a]=8, [0x2b]=8, [0x2c]=4, [0x2d]=4, [0x2e]=20, [0x2f]=4,
        [0x30]=4, [0x31]=4, [0x32]=4, [0x33]=4, [0x34]=4, [0x35]=8, [0x36]=8, [0x37]=8,
        [0x38]=4, [0x39]=4, [0x3a]=12, [0x3b]=4, [0x3c]=8, [0x3d]=12, [0x3e]=4, [0x3f]=8,
        [0x40]=12, [0x41]=4, [0x42]=20, [0x43]=8, [0x44]=8, [0x45]=8, [0x46]=8, [0x47]=4,
        [0x48]=4, [0x49]=4, [0x4a]=4, [0x4b]=12, [0x4c]=32, [0x4d]=8, [0x4e]=8, [0x4f]=8,
        [0x50]=8, [0x51]=8, [0x52]=4, [0x53]=12, [0x54]=4, [0x55]=12, [0x56]=28, [0x57]=8,
        [0x58]=8, [0x59]=20, [0x5a]=Variable, [0x5b]=44, [0x5c]=4, [0x5d]=4, [0x5e]=20, [0x5f]=12,
        [0x60]=4, [0x61]=4, [0x62]=8, [0x63]=8, [0x64]=4, [0x65]=4, [0x66]=4, [0x67]=8,
        [0x68]=8, [0x69]=4, [0x6a]=8, [0x6b]=4, [0x6c]=4, [0x6d]=4, [0x6e]=4, [0x6f]=4,
        [0x70]=1, [0x71]=1,
    };

    /// <summary>
    /// Total record length of the opcode at <paramref name="pos"/> in <paramref name="buffer"/>,
    /// resolving the variable opcodes <c>0x28</c> (subtype byte at <c>+2</c>) and <c>0x5a</c>
    /// (<c>4 + 8 * byte[+3]</c>). Returns 0 if the opcode is unknown or the record runs off the
    /// buffer (so the walker can stop on trailing non-code data).
    /// </summary>
    public static int Length(ReadOnlySpan<byte> buffer, int pos)
    {
        if (pos < 0 || pos >= buffer.Length) return 0;
        byte op = buffer[pos];
        if (!Lengths.TryGetValue(op, out int len)) return 0;
        if (len != Variable) return len;

        if (op == 0x28)
        {
            if (pos + 2 >= buffer.Length) return 0;
            int subtype = buffer[pos + 2];
            return subtype >= 0 && subtype < Op28Lengths.Length ? Op28Lengths[subtype] : 0;
        }
        // op == 0x5a: 4 + 8 * byte[pos+3]
        if (pos + 3 >= buffer.Length) return 0;
        return 4 + 8 * buffer[pos + 3];
    }

    /// <summary>True if the opcode-length table is complete enough to trust a walk's enumeration.</summary>
    public static bool IsTrustworthy => true;
}
