namespace DinoRand.FileFormats.Stage;

/// <summary>Suppresses the PC-only DC1 0303 Recovery Aid whose native take index collides with Panel Key 2.</summary>
public static class Dc1NativeRecoveryAidSuppression
{
    public const int RoomCode = 0x0303;
    public const int RecordOffset = 0x4FA68;

    // Complete 44-byte record from the supported GOG DC1 source room, including reserved bytes.
    private static readonly byte[] ExpectedRecord = Convert.FromHexString(
        "28020400000200F8000800F8000800F2000200F2040200010000000021000100B30015000000188000000000");
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

    /// <summary>Applies the native transform only when the pristine source is the recognized GOG room.
    /// Unrecognized fixtures pass through; a changed output for a recognized source is refused.</summary>
    public static byte[] ApplyGenerated(int roomCode, ReadOnlySpan<byte> sourceBytes,
                                        ReadOnlySpan<byte> outputBytes)
    {
        if (roomCode != RoomCode) return outputBytes.ToArray();

        var source = RoomFile.Read(roomCode >> 8, roomCode & 0xff, sourceBytes);
        if (!HasRecord(source) || !source.RdtBuffer.AsSpan(RecordOffset, ItemRecord.Length)
                .SequenceEqual(ExpectedRecord))
            return outputBytes.ToArray();

        var output = RoomFile.Read(roomCode >> 8, roomCode & 0xff, outputBytes);
        if (!HasRecord(output))
            throw Refuse("generated room 0303 Recovery Aid record is truncated");

        var actual = output.RdtBuffer.AsSpan(RecordOffset, ItemRecord.Length);
        if (actual.SequenceEqual(NoOpRecord))
            return outputBytes.ToArray();
        if (!actual.SequenceEqual(ExpectedRecord))
            throw Refuse("generated room 0303 Recovery Aid record signature mismatch at 0x4FA68");

        var edited = output.RdtBuffer.ToArray();
        edited.AsSpan(RecordOffset, ItemRecord.Length).Clear();
        return output.WriteWithRdt(edited);
    }

    private static bool HasRecord(RoomFile room)
        => room.ParsedCleanly && RecordOffset <= room.RdtBuffer.Length - ItemRecord.Length;

    private static InvalidDataException Refuse(string reason)
        => new($"DC1 native Recovery Aid suppression refused: {reason}.");
}
