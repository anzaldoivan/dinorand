namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Byte-level patcher for Classic REbirth's <c>english/ddraw.dll</c> — the companion to
/// <see cref="ExePatcher.ExpandDc1CharacterVertexTables"/> that completes the DC1 400-vertex
/// ceiling lift (docs/decisions/dc1/renderer/REBIRTH-DLL-VERTEX-CEILING-RCA.md).
///
/// <para><b>Why a DLL patch exists.</b> REbirth detours DINO.exe's character render pipeline at
/// runtime (<c>0xE9</c> hooks over <c>0x5F2270</c>/<c>0x5F18F0</c>/…) into its own tri/quad builders
/// and transform writer. Those copies address the game's ORIGINAL 400-entry color/otz tables via
/// constants baked into the DLL file, and add TWO private 400-entry tables in DLL <c>.data</c> BSS
/// (z 400×4 at RVA <c>0x3FC950</c>, vertex 400×8 at RVA <c>0x3FD390</c>). The EXE-side relocation is
/// therefore bypassed whenever REbirth is present; a &gt;400-vert model overruns the DLL tables.</para>
///
/// <para><b>The lever.</b> Append a zero-filled RW section <c>.dinovtx</c> hosting 1024-entry
/// replacements for the two private tables, and rewrite all 33 operands: 16 game-table operands →
/// the lifted EXE's <c>.dinovtx</c> tables (cross-module absolutes; verified reloc-EXEMPT in the
/// DLL, correct because DINO.exe never rebases), 17 private-table operands → the new DLL section
/// (preferred-base form; verified reloc-COVERED, so the loader adjusts them on rebase exactly like
/// the originals — the DLL is ASLR-enabled and does rebase in practice).</para>
///
/// <para><b>Hard dependency:</b> a patched <c>ddraw.dll</c> reads the EXE's <c>.dinovtx</c> tables
/// and REQUIRES the lifted <c>DINO.exe</c> (<see cref="ExePatcher.IsDc1CharacterVertexTablesExpanded"/>);
/// with a stock EXE it would fault on the unmapped <c>0x6FC000</c>. Apply both or neither.</para>
///
/// <para>Version lock: all offsets are for the installed REbirth build, 3,536,384 B, SHA256
/// <c>249F1B8F…</c>; every operand's stock value is verified before any byte is written.</para>
/// </summary>
public static class DdrawPatcher
{
    /// <summary>ddraw.dll preferred image base (the DLL is ASLR'd; all DLL-internal operands below
    /// are stored in preferred-base form and reloc-adjusted by the loader).</summary>
    public const uint DllPreferredBase = 0x10000000;

    /// <summary>Stock REbirth private tables (DLL <c>.data</c> BSS, preferred-base VAs): z-depth
    /// 400 × 4 B, per-vertex 400 × 8 B (<c>[idx*8 + base]</c>).</summary>
    public const uint StockPrivZTableVa = 0x103FC950;
    public const uint StockPrivVertTableVa = 0x103FD390;

    public const int RebirthVertexTableStockCapacity = 400;
    public const int RebirthVertexTableExpandedCapacity = 1024;

    /// <summary>New section layout: RVA at the stock <c>SizeOfImage</c>, raw appended at stock EOF.
    /// z-table 1024×4 then vertex table 1024×8.</summary>
    public const uint NewSectionRva = 0x521000;
    public const int NewSectionSize = 0x3000;
    public const int StockDllLength = 0x35F600;
    public const uint NewPrivZTableVa = DllPreferredBase + NewSectionRva;                 // 0x10521000
    public const uint NewPrivVertTableVa = NewPrivZTableVa + 4 * (uint)RebirthVertexTableExpandedCapacity; // 0x10522000

    // PE header field positions, verified against the stock DLL (e_lfanew 0x138, 6 sections,
    // section table 0x230..0x320 with a zeroed 7th slot, SizeOfImage 0x521000, checksum 0).
    private const int PeSigOffset = 0x138;
    private const int NumberOfSectionsOffset = 0x13E;
    private const int SizeOfImageOffset = 0x188;
    private const int NewSectionHeaderOffset = 0x320;

    /// <summary>
    /// Every operand rewrite, as (file offset of the 32-bit operand, stock value, new value).
    /// Census closed by whole-file byte scan + capstone disassembly of every site + .reloc parse
    /// (game-table sites reloc-exempt, private-table sites reloc-covered; zero violations).
    /// Reads live in the DLL's tri builder (file ~0x540xx–0x542xx) and quad builder (~0x542xx–0x544xx);
    /// the four <c>0x54Dxx</c> sites are the transform writer's cursor-init immediates.
    /// </summary>
    public static readonly (int FileOffset, uint StockValue, uint NewValue)[] OperandRewrites =
    {
        // game LIT-COLOR table 0x6B51A0 -> lifted EXE color table (7 reads + writer cursor init)
        (0x541AA, 0x006B51A0, ExePatcher.Dc1NewColorTableVa), (0x541B7, 0x006B51A0, ExePatcher.Dc1NewColorTableVa),
        (0x541C5, 0x006B51A0, ExePatcher.Dc1NewColorTableVa), (0x5440E, 0x006B51A0, ExePatcher.Dc1NewColorTableVa),
        (0x5441B, 0x006B51A0, ExePatcher.Dc1NewColorTableVa), (0x54429, 0x006B51A0, ExePatcher.Dc1NewColorTableVa),
        (0x54437, 0x006B51A0, ExePatcher.Dc1NewColorTableVa), (0x54D8C, 0x006B51A0, ExePatcher.Dc1NewColorTableVa),
        // game OT-DEPTH table 0x6B57E0 -> lifted EXE otz table (7 reads + writer cursor init)
        (0x5416D, 0x006B57E0, ExePatcher.Dc1NewOtzTableVa), (0x54184, 0x006B57E0, ExePatcher.Dc1NewOtzTableVa),
        (0x5419C, 0x006B57E0, ExePatcher.Dc1NewOtzTableVa), (0x543B9, 0x006B57E0, ExePatcher.Dc1NewOtzTableVa),
        (0x543D0, 0x006B57E0, ExePatcher.Dc1NewOtzTableVa), (0x543E8, 0x006B57E0, ExePatcher.Dc1NewOtzTableVa),
        (0x54400, 0x006B57E0, ExePatcher.Dc1NewOtzTableVa), (0x54D75, 0x006B57E0, ExePatcher.Dc1NewOtzTableVa),
        // private z-table (7 reads + writer cursor init)
        (0x541EB, StockPrivZTableVa, NewPrivZTableVa), (0x541F5, StockPrivZTableVa, NewPrivZTableVa),
        (0x54208, StockPrivZTableVa, NewPrivZTableVa), (0x54465, StockPrivZTableVa, NewPrivZTableVa),
        (0x5446F, StockPrivZTableVa, NewPrivZTableVa), (0x5447F, StockPrivZTableVa, NewPrivZTableVa),
        (0x5448F, StockPrivZTableVa, NewPrivZTableVa), (0x54D57, StockPrivZTableVa, NewPrivZTableVa),
        // private vertex table, stride 8 (8 address materializations + writer cursor init; one site
        // addresses base+4 — the Y half of entry 0)
        (0x54087, StockPrivVertTableVa, NewPrivVertTableVa), (0x54092, StockPrivVertTableVa, NewPrivVertTableVa),
        (0x5409E, StockPrivVertTableVa, NewPrivVertTableVa), (0x542A7, StockPrivVertTableVa, NewPrivVertTableVa),
        (0x542B2, StockPrivVertTableVa, NewPrivVertTableVa), (0x542BE, StockPrivVertTableVa, NewPrivVertTableVa),
        (0x54309, StockPrivVertTableVa, NewPrivVertTableVa), (0x54D4F, StockPrivVertTableVa, NewPrivVertTableVa),
        (0x54312, StockPrivVertTableVa + 4, NewPrivVertTableVa + 4),
    };

    /// <summary>
    /// Lift REbirth's private 400-entry ceiling to <see cref="RebirthVertexTableExpandedCapacity"/>
    /// and rebind its game-table accesses to the lifted EXE's tables. Returns a NEW buffer
    /// (<see cref="NewSectionSize"/> bytes longer); the input is not modified. Throws
    /// <see cref="InvalidOperationException"/> on any precondition mismatch (foreign REbirth build,
    /// or already patched) — it never writes a byte it cannot verify. Reversal is the installer's
    /// backup file. The caller must ensure the installed DINO.exe is lifted first.
    /// </summary>
    public static byte[] ExpandRebirthVertexTables(ReadOnlySpan<byte> dll)
    {
        if (dll.Length != StockDllLength)
            throw new InvalidOperationException(
                $"ddraw.dll is 0x{dll.Length:X} bytes, expected stock REbirth 0x{StockDllLength:X}.");
        if (dll[0] != 'M' || dll[1] != 'Z' || ExePatcher.ReadUInt32(dll, 0x3C) != PeSigOffset
            || ExePatcher.ReadUInt32(dll, PeSigOffset) != 0x00004550)
            throw new InvalidOperationException("Not the expected PE layout (MZ/PE anchors mismatch).");
        if (ExePatcher.ReadUInt16(dll, NumberOfSectionsOffset) != 6)
            throw new InvalidOperationException("Expected 6 PE sections (stock REbirth ddraw.dll).");
        if (ExePatcher.ReadUInt32(dll, SizeOfImageOffset) != NewSectionRva)
            throw new InvalidOperationException("SizeOfImage is not the stock 0x521000.");
        for (int i = 0; i < 0x28; i++)
            if (dll[NewSectionHeaderOffset + i] != 0)
                throw new InvalidOperationException("The 7th section-header slot is not empty.");
        foreach (var (off, stock, _) in OperandRewrites)
            if (ExePatcher.ReadUInt32(dll, off) != stock)
                throw new InvalidOperationException(
                    $"Operand at file 0x{off:X} does not hold stock value 0x{stock:X} " +
                    "(already patched, or an unknown REbirth build).");

        var outBuf = new byte[StockDllLength + NewSectionSize];
        dll.CopyTo(outBuf);
        var span = outBuf.AsSpan();

        ExePatcher.WriteUInt16(span, NumberOfSectionsOffset, 7);
        ExePatcher.WriteUInt32(span, SizeOfImageOffset, NewSectionRva + NewSectionSize);
        System.Text.Encoding.ASCII.GetBytes(".dinovtx").CopyTo(span.Slice(NewSectionHeaderOffset, 8));
        ExePatcher.WriteUInt32(span, NewSectionHeaderOffset + 0x08, NewSectionSize);   // VirtualSize
        ExePatcher.WriteUInt32(span, NewSectionHeaderOffset + 0x0C, NewSectionRva);    // VirtualAddress
        ExePatcher.WriteUInt32(span, NewSectionHeaderOffset + 0x10, NewSectionSize);   // SizeOfRawData
        ExePatcher.WriteUInt32(span, NewSectionHeaderOffset + 0x14, StockDllLength);   // PointerToRawData
        ExePatcher.WriteUInt32(span, NewSectionHeaderOffset + 0x24, 0xC0000040);       // RW initialized data

        foreach (var (off, _, val) in OperandRewrites)
            ExePatcher.WriteUInt32(span, off, val);

        return outBuf;
    }

    /// <summary>True when <paramref name="dll"/> already carries the expanded REbirth tables.</summary>
    public static bool IsRebirthVertexTablesExpanded(ReadOnlySpan<byte> dll)
        => dll.Length == StockDllLength + NewSectionSize
           && ExePatcher.ReadUInt16(dll, NumberOfSectionsOffset) == 7
           && ExePatcher.ReadUInt32(dll, OperandRewrites[0].FileOffset) == OperandRewrites[0].NewValue;
}
