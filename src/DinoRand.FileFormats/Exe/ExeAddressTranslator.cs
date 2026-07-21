using System.Buffers.Binary;
using static DinoRand.FileFormats.Exe.ExePatcher;

namespace DinoRand.FileFormats.Exe;

internal static class ExeAddressTranslator
{
    internal static int VaToFileOffset(uint va)
    {
        if (va < ImageBase)
            throw new ArgumentOutOfRangeException(nameof(va), $"VA 0x{va:X} is below the image base 0x{ImageBase:X}.");
        uint rva = va - ImageBase;
        if (rva < FileBackedRvaLo || rva >= FileBackedRvaHi)
            throw new ArgumentOutOfRangeException(nameof(va),
                $"VA 0x{va:X} (RVA 0x{rva:X}) is outside the file-backed window " +
                $"[0x{ImageBase + FileBackedRvaLo:X}, 0x{ImageBase + FileBackedRvaHi:X}); " +
                "it is BSS/runtime-only or non-raw-aligned and cannot be patched on disk.");
        return (int)rva; // raw == virtual ⇒ file offset == RVA, delta 0
    }

    internal static bool IsFileBacked(uint va)
        => va >= ImageBase && (va - ImageBase) >= FileBackedRvaLo && (va - ImageBase) < FileBackedRvaHi;

    internal static ushort ReadUInt16(ReadOnlySpan<byte> exe, int fileOffset)
        => BinaryPrimitives.ReadUInt16LittleEndian(Slice(exe, fileOffset, 2));

    internal static uint ReadUInt32(ReadOnlySpan<byte> exe, int fileOffset)
        => BinaryPrimitives.ReadUInt32LittleEndian(Slice(exe, fileOffset, 4));

    internal static void WriteUInt16(Span<byte> exe, int fileOffset, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(Slice(exe, fileOffset, 2), value);

    internal static void WriteUInt32(Span<byte> exe, int fileOffset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(Slice(exe, fileOffset, 4), value);

    internal static uint ReadUInt32AtVa(ReadOnlySpan<byte> exe, uint va)
        => ReadUInt32(exe, VaToFileOffset(va));

    internal static ushort ReadUInt16AtVa(ReadOnlySpan<byte> exe, uint va)
        => ReadUInt16(exe, VaToFileOffset(va));

    internal static void WriteUInt32AtVa(Span<byte> exe, uint va, uint value)
        => WriteUInt32(exe, VaToFileOffset(va), value);

    internal static void WriteUInt16AtVa(Span<byte> exe, uint va, ushort value)
        => WriteUInt16(exe, VaToFileOffset(va), value);
}
