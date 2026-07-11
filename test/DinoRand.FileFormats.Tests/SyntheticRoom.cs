using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Builds <b>DMCA-safe synthetic</b> Dino Crisis room files (<c>stNXX.dat</c> / <c>ST*.DAT</c>) entirely
/// from decoded format knowledge — <b>no copyrighted game bytes</b>. These stand in for a real install so
/// the room round-trip integration gates (<see cref="RoomFileRoundTripTests"/>, <see cref="GianPackageTests"/>,
/// <see cref="Dc2RoomRoundTripTests"/>) still run — with data — on CI, where game files can never be present.
///
/// <para><b>DC1 layout.</b> A 2-entry <see cref="GianPackage"/> <c>[filler Texture, Data RDT]</c> (two entries
/// so the second slot's nonzero size defeats the single-entry DC2-misdetection at file offset 16). The Data
/// RDT is stored raw (<see cref="RoomFile"/> reads the last entry verbatim). The RDT buffer is:
/// <c>header(0x24) → embedded model blocks → 4-byte function table → one subroutine of records</c>. The model
/// blocks sit in the pre-table gap so the SCD walker never decodes them; the single subroutine holds only
/// valid item/door/enemy opcodes and ends exactly at EOF, so <see cref="RoomScript.ParsedCleanly"/> holds and
/// the records surface. Each enemy's model pointer targets its embedded skeleton block (bone count at
/// <see cref="EnemySkeleton.BoneCountOffset"/>), so species decodes from the room bytes alone.</para>
///
/// <para><b>DC2 layout.</b> A 32-byte-entry package whose entries tile the file exactly (byte-identical repack)
/// with at least one <see cref="GianEntryType.Lzss0"/> entry whose payload is real <see cref="Lzss"/> output
/// (lossless codec round-trip + sector-fit).</para>
/// </summary>
internal static class SyntheticRoom
{
    private const uint Psx = RoomScript.PsxRdtBase; // 0x80100000
    private const int Header = 0x24;

    /// <summary>The category ↔ bone-count ↔ species bijection proven in
    /// <c>RoomScriptTests.Walk_DecodesSpecies_FromModelSkeleton</c>; every synthetic enemy uses one of
    /// these pairs so <see cref="EnemyRecord.SpeciesMatchesCategory"/> holds and the corpus stays 1:1.</summary>
    public static readonly (byte Category, int Bones)[] Species =
    {
        (1, 15), // Velociraptor
        (2, 21), // RaptorHeavy
        (3, 20), // Tyrannosaurus (boss)
        (4, 10), // Tyrannosaurus (Chief's Room)
        (5, 7),  // Swarm
        (7, 18), // Pteranodon
    };

    /// <summary>An enemy to place: its opcode (0x20 or 0x59), AI category and skeleton bone count.</summary>
    public readonly record struct Enemy(byte Opcode, byte Category, int Bones);

    /// <summary>An item pickup to place (id + count). Id 0xFF = empty slot.</summary>
    public readonly record struct Item(byte Id, ushort Count);

    /// <summary>A door to place (destination stage/room, lock id, door type).</summary>
    public readonly record struct Door(byte Stage, byte Room, byte Lock, byte Type);

    // ---- DC1 ------------------------------------------------------------------------------------------

    /// <summary>Build a full DC1 room <c>.dat</c> from the given records.</summary>
    public static byte[] Dc1Room(IReadOnlyList<Item> items, IReadOnlyList<Door> doors,
                                 IReadOnlyList<Enemy> enemies)
        => Dc1Package(BuildRdt(items, doors, enemies));

    private static byte[] BuildRdt(IReadOnlyList<Item> items, IReadOnlyList<Door> doors,
                                   IReadOnlyList<Enemy> enemies)
    {
        // 1. Lay out one embedded model block per enemy in the pre-table gap [Header, tableBase).
        var modelOffset = new int[enemies.Count];
        int pos = Header;
        for (int i = 0; i < enemies.Count; i++)
        {
            modelOffset[i] = pos;
            pos += EnemySkeleton.BoneArrayOffset + enemies[i].Bones * EnemySkeleton.BoneStride;
        }
        int tableBase = pos;               // function table right after the model blocks
        int subStart = tableBase + 4;      // one subroutine, 4-byte self-describing table

        // 2. Author the records (all valid, fixed-length opcodes) for the single subroutine.
        var records = new List<byte[]>();
        foreach (var it in items) records.Add(ItemRec(it.Id, it.Count));
        foreach (var d in doors) records.Add(DoorRec(d.Stage, d.Room, d.Lock, d.Type));
        for (int i = 0; i < enemies.Count; i++)
            records.Add(EnemyRec(enemies[i], (byte)i, model: Psx + (uint)modelOffset[i],
                                 motion: Psx + 0x40000u + (uint)(i * 8)));
        int recBytes = records.Sum(r => r.Length);

        // 3. Assemble the buffer: header, model blocks, table, subroutine — subroutine ends exactly at EOF.
        var buf = new byte[subStart + recBytes];
        WriteU32(buf, 0x14, Psx + (uint)tableBase);          // header dword 0x14 → function table
        for (int i = 0; i < enemies.Count; i++)              // each model block's bone count
            WriteU32(buf, modelOffset[i] + EnemySkeleton.BoneCountOffset, (uint)enemies[i].Bones);
        WriteU32(buf, tableBase, 4);                          // table size = 4 → n=1, entry0 = 4 → subStart
        int at = subStart;
        foreach (var r in records) { r.CopyTo(buf, at); at += r.Length; }
        return buf;
    }

    private static byte[] ItemRec(byte id, ushort count)
    {
        var r = new byte[DcOpcodes.ItemLength];              // 44
        r[0] = DcOpcodes.Item;                               // 0x28
        r[2] = DcOpcodes.ItemSubtype;                        // subtype 4
        r[ItemRecord.IdOffset] = id;
        r[ItemRecord.CountOffset] = (byte)count;
        r[ItemRecord.CountOffset + 1] = (byte)(count >> 8);
        return r;
    }

    private static byte[] DoorRec(byte stage, byte room, byte lockId, byte type)
    {
        var r = new byte[DcOpcodes.DoorLength];              // 48
        r[0] = DcOpcodes.Door;                               // 0x28
        r[2] = DcOpcodes.DoorSubtype;                        // subtype 0
        r[DoorRecord.DestOffset] = room;                     // word[+0x1c] low = room
        r[DoorRecord.DestOffset + 1] = stage;                // word[+0x1c] high = stage
        r[DoorRecord.LockOffset] = lockId;                   // +0x27
        r[DoorRecord.DoorTypeOffset] = type;                 // +0x28
        return r;                                            // entry-pose words left 0 (== Original)
    }

    private static byte[] EnemyRec(Enemy e, byte slot, uint model, uint motion)
    {
        if (e.Opcode == DcOpcodes.Enemy2)
        {
            var r = new byte[DcOpcodes.Enemy2Length];        // 20
            r[0] = DcOpcodes.Enemy2;                         // 0x59
            r[EnemyRecord.SlotOffset] = slot;
            r[EnemyRecord.CategoryOffset] = e.Category;
            WriteU32(r, EnemyRecord.Enemy2ModelOffset, model);
            WriteU32(r, EnemyRecord.Enemy2MotionOffset, motion);
            return r;
        }
        var b = new byte[DcOpcodes.EnemyLength];             // 24
        b[0] = DcOpcodes.Enemy;                              // 0x20
        b[EnemyRecord.SlotOffset] = slot;
        b[EnemyRecord.CategoryOffset] = e.Category;
        WriteU32(b, EnemyRecord.ModelOffset, model);
        WriteU32(b, EnemyRecord.MotionOffset, motion);
        return b;
    }

    private static byte[] Dc1Package(byte[] rdt)
    {
        // [filler Texture, Data RDT] — RDT is the last entry (RoomDataEntry). Two entries so the second
        // slot's nonzero size at file offset 20 defeats the all-zero DC2 probe (offsets 16..31).
        var filler = new byte[] { 0xDC, 0x1D, 0x00, 0x00 };
        return Package(GianPackage.Dc1EntrySize,
            (GianEntryType.Texture, filler),
            (GianEntryType.Data, rdt));
    }

    // ---- DC2 ------------------------------------------------------------------------------------------

    /// <summary>Build a full DC2 room <c>.dat</c>: an LZSS0 SCD blob (varying with <paramref name="variant"/>)
    /// plus a raw Data tail, as a 32-byte-entry package whose entries tile the file exactly.</summary>
    public static byte[] Dc2Room(int variant)
    {
        // A compressible SCD stand-in (repeating pattern → LZSS actually compresses); size varies per room.
        int n = 300 + variant * 37;
        var scd = new byte[n];
        for (int i = 0; i < n; i++) scd[i] = (byte)((i * 7 + variant) & 0x0f);
        byte[] compressed = Lzss.Compress(scd);
        var tail = new byte[16 + (variant & 7)];             // small raw Data tail

        return Package(GianPackage.Dc2EntrySize,
            (GianEntryType.Lzss0, compressed),
            (GianEntryType.Data, tail));
    }

    // ---- shared container builder --------------------------------------------------------------------

    /// <summary>Emit a Gian package: 2048-byte entry table (stride <paramref name="entrySize"/>) then each
    /// payload sector-aligned to 2048, tiling the file exactly. Entry fields beyond type+size stay zero — for
    /// DC2 that keeps the offset-16..31 probe all-zero so the 32-byte layout is detected.</summary>
    internal static byte[] Package(int entrySize, params (GianEntryType type, byte[] payload)[] entries)
    {
        static int Align(int v) => (v + GianPackage.SectorSize - 1) & ~(GianPackage.SectorSize - 1);

        int total = GianPackage.HeaderSize + entries.Sum(e => Align(e.payload.Length));
        var buf = new byte[total];
        int pos = GianPackage.HeaderSize;
        for (int i = 0; i < entries.Length; i++)
        {
            int hdr = i * entrySize;
            WriteU32(buf, hdr, (uint)entries[i].type);
            WriteU32(buf, hdr + 4, (uint)entries[i].payload.Length);
            entries[i].payload.CopyTo(buf, pos);
            pos += Align(entries[i].payload.Length);
        }
        return buf;
    }

    private static void WriteU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
        b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
    }
}
