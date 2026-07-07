namespace DinoRand.FileFormats.Stage.Dc2;

/// <summary>
/// One Dino Crisis 2 (Rebirth) room — physically the <c>ST*.DAT</c> file (the paired <c>ST*.DBS</c>
/// is a pre-rendered background stream and is ignored for logic). This is the DC2 analogue of
/// <see cref="RoomFile"/> but a <b>separate type</b>: DC2's container, section directory, record
/// format, and enemy-model storage all differ from DC1, so the DC1 reader cannot be reused. Keeping
/// DC2 in its own type is what lets DC2 ship without touching shipped/DC1 code
/// (docs/parity/BIORAND-REUSE-VALIDATION.md Q3).
///
/// <para><b>⚠ STUB.</b> Container parse + section-table labeling are not yet implemented.
/// <see cref="Read"/> currently only captures the raw bytes; <see cref="Write"/> is a byte-exact
/// no-op. NB the room blob's <b>slot-5</b> is an SCD program that DOES carry enemy spawns (op 0x1a)
/// and doors (op 0x15) — editable in the room file (K47–K50, docs/reference/dc2/spawn/ENEMY-SPAWNER-RE.md). The
/// edit primitives are shipped (<see cref="Dc2SpawnEditor"/>, <see cref="Dc2DoorEditor"/>) and the
/// per-operand offsets live in <c>data/dc2/{spawn,door}-graph.json</c>; the in-C# slot-5 parse to
/// populate <see cref="Enemies"/> is the next step. The intended pipeline is inline.</para>
/// </summary>
public sealed class Dc2RoomFile
{
    private Dc2RoomFile(int stage, int room)
    {
        Stage = stage;
        Room = room;
    }

    /// <summary>Stage group 0–9 (DC2 has stages ST0..ST9 — docs/reference/dc2/rooms/ROOMS-DC2.md).</summary>
    public int Stage { get; }

    /// <summary>Room id within the stage (the XX in <c>STNXX.DAT</c>); id scheme TBD (KaQ K14).</summary>
    public int Room { get; }

    /// <summary>Raw file bytes as read (round-trip baseline until records decode).</summary>
    public byte[] OriginalBytes { get; private set; } = Array.Empty<byte>();

    /// <summary>Decoded slot-5 enemy spawns (op 0x1a). Currently empty — the in-C# slot-5 parse is
    /// pending; until then the spawn data is consumed from <c>data/dc2/spawn-graph.json</c>
    /// (tools/dc2_re/edit_spawn.py) and written via <see cref="Dc2SpawnEditor"/>
    /// (docs/reference/dc2/spawn/ENEMY-SPAWNER-RE.md, K47–K50).</summary>
    public List<Dc2EnemyPlacement> Enemies { get; } = new();

    // TODO(dc2): add Items / Doors lists once their in-room record sections are located (OPEN #2/#5).

    public static Dc2RoomFile Read(int stage, int room, ReadOnlySpan<byte> bytes)
    {
        var rf = new Dc2RoomFile(stage, room) { OriginalBytes = bytes.ToArray() };

        // TODO(dc2) — intended pipeline (none of these decoders exist yet):
        //   1. Parse the Gian package (32-byte entries; auto-detect via data[16:32]==0) and pull the
        //      single LZSS0 room blob (reserve[0] base 0x005e0000). docs/reference/dc2/format/FORMAT-DC2.md.
        //   2. LZSS-decompress it (codec byte-identical to DC1).
        //   3. Walk the 12-slot absolute-pointer SECTION TABLE at the blob head (rebase off = ptr - base):
        //      slots 0-9 are SPATIAL (geometry / collision / camera / camera-switch zones / triggers,
        //      K20-K26, docs/reference/dc2/rooms/ROOM-SECTION-TABLE.md); SLOT 5 is the SCD program.
        //   4. Walk slot-5 (port tools/dc2_re/decode_script.py): enemy spawns (op 0x1a) -> Enemies,
        //      doors (op 0x15) -> a future Doors list. Literal operands are room-file editable
        //      (K47-K50). Until this C# parse lands, the randomizer consumes data/dc2/spawn-graph.json.

        return rf;
    }

    /// <summary>The <c>ST*.DAT</c> path this room was read from (set by <see cref="ReadFromFile"/>),
    /// or <c>null</c> for an in-memory room. The backup-and-swap output sink writes back here.</summary>
    public string? SourcePath { get; private set; }

    public static Dc2RoomFile ReadFromFile(int stage, int room, string path)
    {
        var rf = Read(stage, room, File.ReadAllBytes(path));
        rf.SourcePath = path;
        return rf;
    }

    /// <summary>Serialize back. Byte-exact no-op until the record decoders + re-emit path exist
    /// (which will also require the re-LZSS / variable-segment-size tolerance gate, OPEN&#160;#9).</summary>
    public byte[] Write() => OriginalBytes;

    public override string ToString() => $"ST{Stage}{Room:D2}";
}
