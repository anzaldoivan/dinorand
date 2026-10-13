using DinoRand.FileFormats.Compression;

namespace DinoRand.FileFormats.Stage;

/// <summary>Internal package/RDT byte handling for <see cref="RoomFile"/>.</summary>
internal static class RoomPackageCodec
{
    internal static (byte[] Buffer, bool Compressed) DecodeRdt(ReadOnlySpan<byte> bytes, GianEntry entry)
    {
        var payload = bytes.Slice(entry.PayloadOffset, (int)entry.DeclaredSize);
        bool compressed = IsCompressed(entry.Type);
        return (compressed ? Lzss.Decompress(payload) : payload.ToArray(), compressed);
    }

    internal static byte[] EncodeRdt(ReadOnlySpan<byte> rdt, bool compressed)
        => compressed ? Lzss.Compress(rdt) : rdt.ToArray();

    internal static byte[] RewriteRdt(
        byte[] originalBytes, GianPackage package, bool compressed,
        ReadOnlySpan<byte> editedDecompressedRdt)
        => RebuildRoomData(originalBytes, package, EncodeRdt(editedDecompressedRdt, compressed));

    /// <summary>Replace the room-data entry's payload. Entries after it (the sound entries of the
    /// extra-stage rooms) are carried over verbatim and shift by whole sectors; the header stores no
    /// offsets, so only the room-data size field changes.</summary>
    internal static byte[] RebuildRoomData(byte[] originalBytes, GianPackage package, byte[] payload)
    {
        int entryIndex = package.RoomDataIndex;
        var entry = package.Entries[entryIndex];
        int sector = GianPackage.SectorSize;
        int padded = (payload.Length + sector - 1) & ~(sector - 1);

        // Nothing follows the last entry; a middle entry is followed by the rest of the file.
        int tailStart = entry.PayloadOffset + entry.AlignedSize;
        int tailLength = entryIndex == package.Entries.Count - 1
            ? 0 : Math.Max(0, originalBytes.Length - tailStart);

        var result = new byte[entry.PayloadOffset + padded + tailLength];
        // Header + earlier entries (everything before this entry's payload) verbatim.
        Array.Copy(originalBytes, 0, result, 0, entry.PayloadOffset);
        Array.Copy(payload, 0, result, entry.PayloadOffset, payload.Length);
        Array.Copy(originalBytes, tailStart, result, entry.PayloadOffset + padded, tailLength);

        // Patch the entry's declared size in the header table (size field at entry+4).
        int sizeFieldOffset = entryIndex * package.EntrySize + 4;
        uint size = (uint)payload.Length;
        result[sizeFieldOffset + 0] = (byte)size;
        result[sizeFieldOffset + 1] = (byte)(size >> 8);
        result[sizeFieldOffset + 2] = (byte)(size >> 16);
        result[sizeFieldOffset + 3] = (byte)(size >> 24);
        return result;
    }

    private static bool IsCompressed(GianEntryType type) => type switch
    {
        GianEntryType.Lzss0 or GianEntryType.Lzss1 or GianEntryType.Lzss2
            or GianEntryType.Unknown => true,
        _ => false,
    };
}
