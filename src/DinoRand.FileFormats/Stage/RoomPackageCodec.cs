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
        byte[] originalBytes, GianPackage package, GianEntry entry, bool compressed,
        ReadOnlySpan<byte> editedDecompressedRdt)
        => RebuildLastEntry(originalBytes, package, entry,
            EncodeRdt(editedDecompressedRdt, compressed));

    internal static byte[] RebuildLastEntry(
        byte[] originalBytes, GianPackage package, GianEntry entry, byte[] payload)
    {
        int sector = GianPackage.SectorSize;
        int padded = (payload.Length + sector - 1) & ~(sector - 1);

        var result = new byte[entry.PayloadOffset + padded];
        // Header + earlier entries (everything before this entry's payload) verbatim.
        Array.Copy(originalBytes, 0, result, 0, entry.PayloadOffset);
        Array.Copy(payload, 0, result, entry.PayloadOffset, payload.Length);

        // Patch the last entry's declared size in the header table (size field at entry+4).
        int entryIndex = package.Entries.Count - 1;
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
