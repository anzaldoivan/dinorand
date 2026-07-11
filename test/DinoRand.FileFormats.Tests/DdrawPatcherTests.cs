using System.IO;
using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Unit tests for <see cref="DdrawPatcher"/> — the REbirth ddraw.dll companion of the DC1
/// vertex-ceiling lift (docs/dc1/REBIRTH-DLL-VERTEX-CEILING-RCA.md). Includes a REAL-file
/// validation gate: the EXE-side lever's only defect (3 transcribed operand offsets) was invisible
/// to a synthetic image built from the same table and was caught only by the real binary, so when
/// the installed stock ddraw.dll is present, its bytes are the oracle here.
/// </summary>
public class DdrawPatcherTests
{
    internal static byte[] NewStockImage()
    {
        var dll = new byte[DdrawPatcher.StockDllLength];
        dll[0] = (byte)'M'; dll[1] = (byte)'Z';
        ExePatcher.WriteUInt32(dll, 0x3C, 0x138);             // e_lfanew
        ExePatcher.WriteUInt32(dll, 0x138, 0x00004550);       // "PE\0\0"
        ExePatcher.WriteUInt16(dll, 0x13E, 6);                // NumberOfSections
        ExePatcher.WriteUInt32(dll, 0x188, 0x00521000);       // SizeOfImage
        foreach (var (off, stock, _) in DdrawPatcher.OperandRewrites)
            ExePatcher.WriteUInt32(dll, off, stock);
        return dll;
    }

    [Fact]
    public void ExpandRebirthVertexTables_AppendsSectionAndRewritesAll33Operands()
    {
        var patched = DdrawPatcher.ExpandRebirthVertexTables(NewStockImage());

        Assert.Equal(DdrawPatcher.StockDllLength + DdrawPatcher.NewSectionSize, patched.Length);
        Assert.Equal(7, ExePatcher.ReadUInt16(patched, 0x13E));
        Assert.Equal(0x00521000u + (uint)DdrawPatcher.NewSectionSize, ExePatcher.ReadUInt32(patched, 0x188));
        Assert.Equal(".dinovtx", System.Text.Encoding.ASCII.GetString(patched, 0x320, 8));
        Assert.Equal(0x00521000u, ExePatcher.ReadUInt32(patched, 0x320 + 0x0C));                        // VirtualAddress
        Assert.Equal((uint)DdrawPatcher.StockDllLength, ExePatcher.ReadUInt32(patched, 0x320 + 0x14));  // PointerToRawData
        Assert.Equal(0xC0000040u, ExePatcher.ReadUInt32(patched, 0x320 + 0x24));

        foreach (var (off, _, val) in DdrawPatcher.OperandRewrites)
            Assert.Equal(val, ExePatcher.ReadUInt32((System.ReadOnlySpan<byte>)patched, off));
        Assert.Equal(33, DdrawPatcher.OperandRewrites.Length);

        Assert.All(patched[DdrawPatcher.StockDllLength..], b => Assert.Equal(0, b));
        Assert.True(DdrawPatcher.IsRebirthVertexTablesExpanded(patched));
        Assert.False(DdrawPatcher.IsRebirthVertexTablesExpanded(NewStockImage()));
    }

    [Fact]
    public void ExpandRebirthVertexTables_LayoutIsConsistent()
    {
        // z 1024x4 then verts 1024x8, filling the section exactly; new game-table targets must be
        // the lifted EXE's tables (the cross-module dependency this patch encodes).
        Assert.Equal(DdrawPatcher.NewPrivZTableVa + 4u * DdrawPatcher.RebirthVertexTableExpandedCapacity,
            DdrawPatcher.NewPrivVertTableVa);
        Assert.Equal((4 + 8) * DdrawPatcher.RebirthVertexTableExpandedCapacity, DdrawPatcher.NewSectionSize);
        Assert.Contains(DdrawPatcher.OperandRewrites, r => r.NewValue == ExePatcher.Dc1NewColorTableVa);
        Assert.Contains(DdrawPatcher.OperandRewrites, r => r.NewValue == ExePatcher.Dc1NewOtzTableVa);
    }

    [Fact]
    public void ExpandRebirthVertexTables_RefusesForeignOrAlreadyPatchedImages()
    {
        Assert.Throws<InvalidOperationException>(() => DdrawPatcher.ExpandRebirthVertexTables(new byte[100]));

        var tampered = NewStockImage();
        ExePatcher.WriteUInt32(tampered, DdrawPatcher.OperandRewrites[20].FileOffset, 0xDEADBEEF);
        Assert.Throws<InvalidOperationException>(() => DdrawPatcher.ExpandRebirthVertexTables(tampered));

        var once = DdrawPatcher.ExpandRebirthVertexTables(NewStockImage());
        Assert.Throws<InvalidOperationException>(() => DdrawPatcher.ExpandRebirthVertexTables(once));
    }

    /// <summary>The real-binary oracle: every operand offset must hold its stock value in the
    /// installed stock ddraw.dll (or the already-patched one must satisfy the detector). Skips
    /// when no game install is present (CI).</summary>
    [Fact]
    public void OperandTable_MatchesInstalledRebirthDll()
    {
        const string path = @"C:\Games\dinorand\english\ddraw.dll";
        if (!File.Exists(path)) return; // gated: no install on this machine
        var dll = File.ReadAllBytes(path);
        if (DdrawPatcher.IsRebirthVertexTablesExpanded(dll))
        {
            foreach (var (off, _, val) in DdrawPatcher.OperandRewrites)
                Assert.Equal(val, ExePatcher.ReadUInt32((System.ReadOnlySpan<byte>)dll, off));
        }
        else
        {
            Assert.Equal(DdrawPatcher.StockDllLength, dll.Length);
            foreach (var (off, stock, _) in DdrawPatcher.OperandRewrites)
                Assert.Equal(stock, ExePatcher.ReadUInt32((System.ReadOnlySpan<byte>)dll, off));
        }
    }
}
