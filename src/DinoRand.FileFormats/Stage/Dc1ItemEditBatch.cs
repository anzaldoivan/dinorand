using System.Buffers.Binary;

namespace DinoRand.FileFormats.Stage;

/// <summary>The verified SCD record class an immutable DC1 item edit expects at its target.</summary>
public readonly record struct Dc1ItemRecordClass(byte Opcode, byte Subtype, int Length)
{
    public static Dc1ItemRecordClass Pickup { get; } =
        new(ItemRecord.Opcode, DcOpcodes.ItemSubtype, ItemRecord.Length);
}

/// <summary>Requested ground-visual fields for a DC1 pickup. Null on <see cref="Dc1ItemEdit.Visual"/>
/// preserves both fields byte-for-byte.</summary>
public readonly record struct Dc1ItemVisualEdit(byte DisplaySlot, uint ModelPointer);

/// <summary>
/// Immutable, preflightable edit of one physical DC1 pickup record. Expected values describe the
/// pristine source; requested values describe the complete installed pickup state.
/// </summary>
public sealed record Dc1ItemEdit(
    int RoomCode,
    int RecordOffset,
    Dc1ItemRecordClass ExpectedClass,
    int ExpectedItemId,
    int ExpectedAmount,
    ushort ExpectedTakeIndex,
    int ItemId,
    int Amount,
    ushort TakeIndex,
    Dc1ItemVisualEdit? Visual);

/// <summary>One room's publishable bytes, exposed only after the complete edit batch preflights.</summary>
public sealed record Dc1PreparedItemRoom(int RoomCode, byte[] Bytes);

public sealed record Dc1ItemEditBatchResult(IReadOnlyList<Dc1PreparedItemRoom> Rooms);

/// <summary>
/// Preflights and applies immutable DC1 item edits without mutating the parsed <see cref="RoomFile"/>
/// objects. The complete cross-room batch is validated before any RDT clone is changed or result is
/// exposed, so a later stale target cannot leave an earlier room publishable.
/// </summary>
public static class Dc1ItemEditBatch
{
    private sealed record Validated(RoomFile Room, Dc1ItemEdit Edit);

    public static Dc1ItemEditBatchResult Prepare(
        IReadOnlyList<RoomFile> rooms,
        IReadOnlyList<Dc1ItemEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(edits);

        var roomByCode = new Dictionary<int, RoomFile>();
        foreach (var room in rooms)
        {
            int code = room.Stage << 8 | room.Room;
            if (!roomByCode.TryAdd(code, room))
                throw Refuse($"duplicate source room 0x{code:X4}");
        }

        ValidateEditRanges(edits);

        // Phase 1: validate every target and every expected pristine value. Do not clone or write yet.
        var validated = new List<Validated>(edits.Count);
        foreach (var edit in edits)
        {
            ValidateRequestedValues(edit);
            if (!roomByCode.TryGetValue(edit.RoomCode, out var room))
                throw Refuse($"room 0x{edit.RoomCode:X4} is not present in the source batch");
            if (!room.ParsedCleanly)
                throw Refuse($"room 0x{edit.RoomCode:X4} did not parse cleanly");
            if (edit.ExpectedClass != Dc1ItemRecordClass.Pickup)
                throw Refuse($"room 0x{edit.RoomCode:X4} offset 0x{edit.RecordOffset:X} expects an unsupported record class");

            var item = room.Items.FirstOrDefault(x => x.FileOffset == edit.RecordOffset)
                ?? throw Refuse($"room 0x{edit.RoomCode:X4} has no item record at 0x{edit.RecordOffset:X}");
            ValidateTargetBytes(room, item, edit);
            validated.Add(new Validated(room, edit));
        }

        // Phase 2: all targets are known-valid. Apply to private room clones, then expose the complete set.
        var output = new List<Dc1PreparedItemRoom>();
        foreach (var group in validated.GroupBy(x => x.Edit.RoomCode))
        {
            var room = group.First().Room;
            var rdt = (byte[])room.RdtBuffer.Clone();
            foreach (var entry in group)
                Apply(rdt, entry.Edit);
            output.Add(new Dc1PreparedItemRoom(group.Key, room.WriteWithRdt(rdt)));
        }
        return new Dc1ItemEditBatchResult(output);
    }

    private static void ValidateEditRanges(IReadOnlyList<Dc1ItemEdit> edits)
    {
        foreach (var group in edits.GroupBy(x => x.RoomCode))
        {
            var ordered = group.OrderBy(x => x.RecordOffset).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var current = ordered[i];
                if (current.RecordOffset < 0)
                    throw Refuse($"room 0x{current.RoomCode:X4} has a negative record offset");
                if (i == 0) continue;
                var previous = ordered[i - 1];
                if (current.RecordOffset == previous.RecordOffset)
                    throw Refuse($"duplicate item target in room 0x{current.RoomCode:X4} at 0x{current.RecordOffset:X}");
                if (current.RecordOffset < previous.RecordOffset + previous.ExpectedClass.Length)
                    throw Refuse($"overlapping item targets in room 0x{current.RoomCode:X4} at "
                        + $"0x{previous.RecordOffset:X} and 0x{current.RecordOffset:X}");
            }
        }
    }

    private static void ValidateRequestedValues(Dc1ItemEdit edit)
    {
        if (edit.ItemId is < byte.MinValue or > byte.MaxValue)
            throw Refuse($"requested item id {edit.ItemId} is outside the byte range");
        if (edit.Amount is < ushort.MinValue or > ushort.MaxValue)
            throw Refuse($"requested item amount {edit.Amount} is outside the unsigned 16-bit range");
        if (edit.ExpectedItemId is < byte.MinValue or > byte.MaxValue)
            throw Refuse($"expected item id {edit.ExpectedItemId} is outside the byte range");
        if (edit.ExpectedAmount is < ushort.MinValue or > ushort.MaxValue)
            throw Refuse($"expected item amount {edit.ExpectedAmount} is outside the unsigned 16-bit range");
    }

    private static void ValidateTargetBytes(RoomFile room, ItemRecord item, Dc1ItemEdit edit)
    {
        int end = edit.RecordOffset + edit.ExpectedClass.Length;
        if (end < edit.RecordOffset || end > room.RdtBuffer.Length)
            throw Refuse($"room 0x{edit.RoomCode:X4} item record at 0x{edit.RecordOffset:X} is truncated");
        var raw = room.RdtBuffer.AsSpan(edit.RecordOffset, edit.ExpectedClass.Length);
        if (raw[0] != edit.ExpectedClass.Opcode || raw[2] != edit.ExpectedClass.Subtype
            || item.Raw.Length != edit.ExpectedClass.Length)
            throw Refuse($"room 0x{edit.RoomCode:X4} record class mismatch at 0x{edit.RecordOffset:X}");

        int actualId = raw[ItemRecord.IdOffset];
        int actualAmount = BinaryPrimitives.ReadUInt16LittleEndian(raw[ItemRecord.CountOffset..]);
        ushort actualTake = BinaryPrimitives.ReadUInt16LittleEndian(raw[ItemRecord.TakeIndexOffset..]);
        if (actualId != edit.ExpectedItemId || item.OriginalItemId != edit.ExpectedItemId)
            throw Refuse($"room 0x{edit.RoomCode:X4} expected item 0x{edit.ExpectedItemId:X2} at "
                + $"0x{edit.RecordOffset:X}, found 0x{actualId:X2}");
        if (actualAmount != edit.ExpectedAmount || item.OriginalAmount != edit.ExpectedAmount)
            throw Refuse($"room 0x{edit.RoomCode:X4} expected amount {edit.ExpectedAmount} at "
                + $"0x{edit.RecordOffset:X}, found {actualAmount}");
        if (actualTake != edit.ExpectedTakeIndex || item.OriginalTakeIndex != edit.ExpectedTakeIndex)
            throw Refuse($"room 0x{edit.RoomCode:X4} expected take index {edit.ExpectedTakeIndex} at "
                + $"0x{edit.RecordOffset:X}, found {actualTake}");
    }

    private static void Apply(Span<byte> rdt, Dc1ItemEdit edit)
    {
        var record = rdt.Slice(edit.RecordOffset, edit.ExpectedClass.Length);
        record[ItemRecord.IdOffset] = (byte)edit.ItemId;
        BinaryPrimitives.WriteUInt16LittleEndian(record[ItemRecord.CountOffset..], (ushort)edit.Amount);
        BinaryPrimitives.WriteUInt16LittleEndian(record[ItemRecord.TakeIndexOffset..], edit.TakeIndex);
        if (edit.Visual is { } visual)
        {
            record[ItemRecord.DisplaySlotOffset] = visual.DisplaySlot;
            BinaryPrimitives.WriteUInt32LittleEndian(record[ItemRecord.ModelPtrOffset..], visual.ModelPointer);
        }
    }

    private static InvalidDataException Refuse(string reason)
        => new($"DC1 item edit preflight refused: {reason}.");
}
