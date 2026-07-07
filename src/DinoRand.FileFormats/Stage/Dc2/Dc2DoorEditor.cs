namespace DinoRand.FileFormats.Stage.Dc2;

/// <summary>
/// Minimal, focused editor for a single Dino Crisis 2 door <b>destination</b> inside an
/// <c>ST*.DAT</c> room file. This is the smallest useful slice of DC2 room editing: it reads /
/// rewrites the 2-byte <c>(stage,room)</c> destination word that a literal-push door commits, then
/// repacks the Gian package so the game re-parses it (docs/decisions/dc1/doors/DOOR-RANDOMIZER-PLAN.md;
/// data/dc2/door-graph.json).
///
/// <para>The editable word lives in the room's <b>SCD blob</b> — the single
/// <see cref="GianEntryType.Lzss0"/> entry of the package (loaded at base <c>0x005e0000</c>, so the
/// graph's <c>dest_va = 0x5e0000 + dest_push_off</c>). The word is little-endian:
/// <c>low byte = stage, high byte = room</c> (e.g. <c>02 01</c> = stage&#160;2, room&#160;1 = ST201).</para>
///
/// <para>Reuses the shipped building blocks — <see cref="GianPackage"/> to locate the blob,
/// <see cref="Lzss"/> to de/recompress it — and does <b>not</b> re-implement the container or codec.
/// It never mutates its input: <see cref="WriteDestination"/> returns a fresh package buffer.</para>
/// </summary>
public static class Dc2DoorEditor
{
    /// <summary>VA base the LZSS0 room blob is loaded at (docs/reference/dc2/format/FORMAT-DC2.md; reserve[0] of the
    /// LZSS0 entry). A door graph <c>dest_va</c> equals <c>BlobBaseVa + dest_push_off</c>.</summary>
    public const uint BlobBaseVa = Dc2ScdBlob.BlobBaseVa;

    /// <summary>Per-stage room counts for DC2 stages 0..9 — the validation domain for a new
    /// destination. Mirrors <c>data/dc2/stage-room-map.json</c> <c>_validation.per_stage_room_counts</c>
    /// (89 rooms total; byte-confirmed from the EXE descriptor table 0x7310EC + cumbase 0x70346C).</summary>
    public static readonly IReadOnlyList<int> RoomsPerStage = new[] { 6, 17, 6, 7, 12, 5, 9, 8, 12, 7 };

    /// <summary>A door destination = the target room as <c>(stage,room)</c>, with its ST id
    /// (<c>'%X%02X'</c>, e.g. stage&#160;2 room&#160;1 → <c>"201"</c> = ST201 — the identity encoding
    /// proven in stage-room-map.json).</summary>
    public readonly record struct DoorDest(int Stage, int Room)
    {
        /// <summary>The ST file id: stage nibble + 2 room hex digits (uppercase).</summary>
        public string StId => $"{Stage:X}{Room:X2}";

        /// <summary>The raw little-endian word as stored at the dest offset (low=stage, high=room).</summary>
        public ushort Word => (ushort)((Room << 8) | (Stage & 0xff));

        public override string ToString() => $"ST{StId} (stage {Stage}, room {Room})";
    }

    /// <summary>True if <paramref name="stage"/>/<paramref name="room"/> is a real DC2 room
    /// (stage 0..9 and room within that stage's <see cref="RoomsPerStage"/> count).</summary>
    public static bool IsValidDestination(int stage, int room)
        => stage >= 0 && stage < RoomsPerStage.Count && room >= 0 && room < RoomsPerStage[stage];

    /// <summary>Decompress the room's SCD blob (the package's single LZSS0 entry).</summary>
    public static byte[] DecompressScdBlob(ReadOnlySpan<byte> packageBytes)
        => Dc2ScdBlob.Decompress(packageBytes);

    /// <summary>
    /// Read the door destination at <paramref name="destOffset"/> in a <b>decompressed</b> SCD blob.
    /// The 2 little-endian bytes there encode <c>low=stage, high=room</c>.
    /// </summary>
    public static DoorDest ReadDestination(ReadOnlySpan<byte> blob, int destOffset)
    {
        if (destOffset < 0 || destOffset + 2 > blob.Length)
            throw new ArgumentOutOfRangeException(nameof(destOffset),
                $"dest offset 0x{destOffset:X} + 2 is outside the {blob.Length}-byte blob");
        return new DoorDest(blob[destOffset], blob[destOffset + 1]);
    }

    /// <summary>Convenience: decompress the package's SCD blob and read the destination at
    /// <paramref name="destOffset"/> in one step.</summary>
    public static DoorDest ReadDestinationFromPackage(ReadOnlySpan<byte> packageBytes, int destOffset)
        => ReadDestination(DecompressScdBlob(packageBytes), destOffset);

    /// <summary>
    /// Return a fresh package buffer with the door at <paramref name="destOffset"/> retargeted to
    /// <c>(<paramref name="newStage"/>,<paramref name="newRoom"/>)</c>: the SCD blob is decompressed,
    /// the 2 dest bytes are overwritten (low=stage, high=room), the blob is re-compressed (LZSS0), and
    /// the package is repacked so it re-parses cleanly — the edited entry's size field is rewritten and
    /// every payload re-padded to the 2048-byte sector boundary (DC2 derives payload offsets by walking
    /// those aligned sizes, so no stored offset needs patching).
    /// </summary>
    public static byte[] WriteDestination(ReadOnlySpan<byte> packageBytes, int destOffset, int newStage, int newRoom)
    {
        if (!IsValidDestination(newStage, newRoom))
            throw new ArgumentException(
                $"({newStage},{newRoom}) is not a valid DC2 room (stage 0..9; room 0..{RoomsPerStage.Count} per " +
                $"stage count {{6,17,6,7,12,5,9,8,12,7}})", nameof(newRoom));

        var blob = Dc2ScdBlob.Decompress(packageBytes);
        if (destOffset < 0 || destOffset + 2 > blob.Length)
            throw new ArgumentOutOfRangeException(nameof(destOffset),
                $"dest offset 0x{destOffset:X} + 2 is outside the {blob.Length}-byte blob");
        blob[destOffset] = (byte)newStage;
        blob[destOffset + 1] = (byte)newRoom;

        return Dc2ScdBlob.RepackWithBlob(packageBytes, blob);
    }
}
