namespace DinoRand.FileFormats.Stage;

/// <summary>Suppresses the PC-only DC1 0303 Recovery Aid whose native take index collides with Panel Key 2.</summary>
public static class Dc1NativeRecoveryAidSuppression
{
    public const int RoomCode = 0x0303;
    public const int RecordOffset = 0x4FA68;

    private static readonly byte[] ExpectedRecord = BuildExpectedRecord();
    private static readonly byte[] NoOpRecord = new byte[ItemRecord.Length];

    /// <summary>Returns generated room bytes with only the validated Recovery Aid record replaced by 44 no-ops.</summary>
    public static byte[] Apply(int roomCode, ReadOnlySpan<byte> roomBytes)
    {
        if (roomCode != RoomCode) return roomBytes.ToArray();

        var room = RoomFile.Read(roomCode >> 8, roomCode & 0xff, roomBytes);
        if (!room.ParsedCleanly)
            throw Refuse("room 0303 did not parse cleanly");

        if (RecordOffset > room.RdtBuffer.Length - ItemRecord.Length)
            throw Refuse("room 0303 Recovery Aid record is truncated");

        var actual = room.RdtBuffer.AsSpan(RecordOffset, ItemRecord.Length);
        if (actual.SequenceEqual(NoOpRecord))
            return roomBytes.ToArray();
        if (!actual.SequenceEqual(ExpectedRecord))
            throw Refuse("room 0303 Recovery Aid record signature mismatch at 0x4FA68");

        var edited = room.RdtBuffer.ToArray();
        edited.AsSpan(RecordOffset, ItemRecord.Length).Clear();
        return room.WriteWithRdt(edited);
    }

    private static byte[] BuildExpectedRecord()
    {
        var record = new byte[ItemRecord.Length];
        record[0] = DcOpcodes.Item;
        record[2] = DcOpcodes.ItemSubtype;
        record[4] = 0x00; record[5] = 0x02; // X = 512
        record[8] = 0x00; record[9] = 0xF8; // Z = -2048
        record[ItemRecord.IdOffset] = 0x21;
        record[ItemRecord.CountOffset] = 1;
        record[ItemRecord.TakeIndexOffset] = 179;
        record[ItemRecord.DisplaySlotOffset] = 0x15;
        record[ItemRecord.ModelPtrOffset] = 0x00;
        record[ItemRecord.ModelPtrOffset + 1] = 0x00;
        record[ItemRecord.ModelPtrOffset + 2] = 0x18;
        record[ItemRecord.ModelPtrOffset + 3] = 0x80;
        return record;
    }

    private static InvalidDataException Refuse(string reason)
        => new($"DC1 native Recovery Aid suppression refused: {reason}.");
}
