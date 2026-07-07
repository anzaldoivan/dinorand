using System.Buffers.Binary;
using DinoRand.FileFormats.Compression;

namespace DinoRand.FileFormats.Stage.Dc2;

/// <summary>
/// Shared low-level access to a Dino Crisis 2 room's <b>SCD blob</b> — the single
/// <see cref="GianEntryType.Lzss0"/> entry of an <c>ST*.DAT</c> Gian package, loaded at base
/// <c>0x005e0000</c>. Both the door editor (<see cref="Dc2DoorEditor"/>) and the spawn editor
/// (<see cref="Dc2SpawnEditor"/>) edit literal words inside this blob, so the decompress + repack
/// plumbing lives here once.
///
/// <para>Reuses the shipped <see cref="GianPackage"/> (locate the blob) and <see cref="Lzss"/>
/// (de/recompress). Repack rewrites the edited entry's size field and re-pads every payload to the
/// 2048-byte sector boundary; DC2 derives payload offsets by walking those aligned sizes, so no
/// stored offset needs patching (validated by <c>Dc2RoomRoundTripTests</c> — the T8 gate). The
/// recompressed blob never needs more sectors than the original reserved (T8 P3), so an edit is
/// sector-stable.</para>
/// </summary>
internal static class Dc2ScdBlob
{
    /// <summary>VA base the LZSS0 room blob is loaded at (docs/reference/dc2/format/FORMAT-DC2.md). A graph offset
    /// equals <c>VA - BlobBaseVa</c>, i.e. it is already a decompressed-blob byte offset.</summary>
    public const uint BlobBaseVa = 0x005e0000;

    /// <summary>Index of the single LZSS0 SCD blob entry in the package.</summary>
    public static int EntryIndex(GianPackage pkg)
    {
        for (int i = 0; i < pkg.Entries.Count; i++)
            if (pkg.Entries[i].Type == GianEntryType.Lzss0)
                return i;
        throw new InvalidDataException("package has no LZSS0 SCD blob entry");
    }

    /// <summary>Decompress the room's SCD blob (the package's single LZSS0 entry).</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> packageBytes)
    {
        var pkg = GianPackage.TryParse(packageBytes)
                  ?? throw new InvalidDataException("not a recognized Gian package");
        var entry = pkg.Entries[EntryIndex(pkg)];
        return Lzss.Decompress(packageBytes.Slice(entry.PayloadOffset, (int)entry.DeclaredSize));
    }

    /// <summary>Return a fresh package buffer with the SCD blob replaced by <paramref name="newBlob"/>:
    /// the blob is LZSS0-recompressed and the package repacked so it re-parses cleanly. Never mutates
    /// the input.</summary>
    public static byte[] RepackWithBlob(ReadOnlySpan<byte> packageBytes, byte[] newBlob)
    {
        var pkg = GianPackage.TryParse(packageBytes)
                  ?? throw new InvalidDataException("not a recognized Gian package");
        int idx = EntryIndex(pkg);
        byte[] newPayload = Lzss.Compress(newBlob);

        int header = GianPackage.HeaderSize;
        int sector = GianPackage.SectorSize;

        int total = header;
        for (int i = 0; i < pkg.Entries.Count; i++)
            total += Align(i == idx ? newPayload.Length : (int)pkg.Entries[i].DeclaredSize, sector);

        var result = new byte[total];
        packageBytes.Slice(0, header).CopyTo(result);

        int pos = header;
        for (int i = 0; i < pkg.Entries.Count; i++)
        {
            var e = pkg.Entries[i];
            ReadOnlySpan<byte> src = i == idx
                ? newPayload
                : packageBytes.Slice(e.PayloadOffset, (int)e.DeclaredSize);
            src.CopyTo(result.AsSpan(pos));
            if (i == idx)
                BinaryPrimitives.WriteUInt32LittleEndian(
                    result.AsSpan(i * pkg.EntrySize + 4, 4), (uint)src.Length);
            pos += Align(src.Length, sector);
        }
        return result;
    }

    private static int Align(int value, int sector) => (value + sector - 1) & ~(sector - 1);
}
