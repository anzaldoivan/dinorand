using System.Buffers.Binary;
using static DinoRand.FileFormats.Exe.ExePatcher;

namespace DinoRand.FileFormats.Exe;

internal static class Dc1VertexExePatch
{
    internal static byte[] ExpandDc1CharacterVertexTables(ReadOnlySpan<byte> exe)
    {
        if (exe.Length != Dc1StockExeLength)
            throw new InvalidOperationException(
                $"DINO.exe is 0x{exe.Length:X} bytes, expected stock 0x{Dc1StockExeLength:X}.");
        if (exe[0] != 'M' || exe[1] != 'Z' || ReadUInt32(exe, 0x3C) != PeSigOffset
            || ReadUInt32(exe, PeSigOffset) != 0x00004550) // "PE\0\0"
            throw new InvalidOperationException("Not the expected PE layout (MZ/PE anchors mismatch).");
        if (ReadUInt16(exe, NumberOfSectionsOffset) != 7)
            throw new InvalidOperationException("Expected 7 PE sections (stock image).");
        if (ReadUInt32(exe, SizeOfImageOffset) != Dc1NewTableSectionRva)
            throw new InvalidOperationException("SizeOfImage is not the stock 0x2FC000.");
        for (int i = 0; i < 0x28; i++)
            if (exe[NewSectionHeaderOffset + i] != 0)
                throw new InvalidOperationException("The 8th section-header slot is not empty.");
        foreach (var (va, stock) in Dc1VertexTableOperands)
            if (ReadUInt32AtVa(exe, va) != stock)
                throw new InvalidOperationException(
                    $"Operand at VA 0x{va:X} does not hold stock table address 0x{stock:X} " +
                    "(already expanded, or an unknown build).");

        var outBuf = new byte[Dc1StockExeLength + Dc1NewTableSectionSize];
        exe.CopyTo(outBuf);
        var span = outBuf.AsSpan();

        // header: 8th section ".dinovtx", RW initialized data, raw == appended zero tail
        WriteUInt16(span, NumberOfSectionsOffset, 8);
        WriteUInt32(span, SizeOfImageOffset, Dc1NewTableSectionRva + Dc1NewTableSectionSize);
        System.Text.Encoding.ASCII.GetBytes(".dinovtx").CopyTo(span.Slice(NewSectionHeaderOffset, 8));
        WriteUInt32(span, NewSectionHeaderOffset + 0x08, Dc1NewTableSectionSize); // VirtualSize
        WriteUInt32(span, NewSectionHeaderOffset + 0x0C, Dc1NewTableSectionRva);  // VirtualAddress
        WriteUInt32(span, NewSectionHeaderOffset + 0x10, Dc1NewTableSectionSize); // SizeOfRawData
        WriteUInt32(span, NewSectionHeaderOffset + 0x14, Dc1StockExeLength);      // PointerToRawData
        WriteUInt32(span, NewSectionHeaderOffset + 0x24, 0xC0000040);             // RW initialized data

        foreach (var (va, stock) in Dc1VertexTableOperands)
            WriteUInt32AtVa(span, va, Dc1NewTableVaFor(stock));

        return outBuf;
    }

    internal static bool IsDc1CharacterVertexTablesExpanded(ReadOnlySpan<byte> exe)
        => exe.Length == Dc1StockExeLength + Dc1NewTableSectionSize
           && ReadUInt16(exe, NumberOfSectionsOffset) == 8
           && ReadUInt32AtVa(exe, Dc1VertexTableOperands[0].OperandVa) == Dc1NewXyTableVa;
}
