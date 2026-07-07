using System.Buffers.Binary;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The byte-insertion + relocation core of the "ADD an enemy" feature
/// (docs/dc1/ADD-ENEMY-PLAN.md). Synthetic scripts pin every relocation kind (file-form pointer,
/// function-table entry, pc-relative branch); a gated real-install test injects a 24-byte record
/// into every clean room and asserts the script still walks clean and stays branch-equivalent.
/// </summary>
public class ScriptInjectorTests
{
    private const uint B = ScriptInjector.PsxBase; // 0x80100000

    private static void PutU32(byte[] buf, int off, uint v)
        => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off, 4), v);
    private static uint GetU32(byte[] buf, int off)
        => BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off, 4));
    private static short GetI16(byte[] buf, int off)
        => BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(off, 2));

    /// <summary>
    /// A minimal but valid RDT: header with a <c>+0x14</c> table pointer, a 2-entry function table at
    /// 0x20, and two subroutines. sub0 = [0x28, 0x38): a forward goto (0x0c) targeting its own return
    /// at 0x34, padded with nops. sub1 = [0x38, end): a lone return. A file-form pointer sits at 0x18
    /// targeting 0x34 (an in-sub0 byte, used to verify pointer relocation).
    /// </summary>
    private static byte[] BuildScript()
    {
        var rdt = new byte[0x40];
        PutU32(rdt, 0x14, B + 0x20);     // header -> func table at 0x20
        PutU32(rdt, 0x18, B + 0x34);     // file-form ptr targeting 0x34 (in sub0)

        // func table @0x20: entry0 = table size (8) -> sub0 at 0x28; entry1 = 0x18 -> sub1 at 0x38.
        PutU32(rdt, 0x20, 0x08);
        PutU32(rdt, 0x24, 0x18);

        // sub0 @0x28
        rdt[0x28] = 0x0c;                                  // goto, forward
        BinaryPrimitives.WriteInt16LittleEndian(rdt.AsSpan(0x2a, 2), 0x0c); // target = 0x28 + 0x0c = 0x34
        rdt[0x2c] = 0x01;                                  // nop (len 4)
        rdt[0x30] = 0x01;                                  // nop (len 4)
        rdt[0x34] = 0x10;                                  // return (len 2)
        // 0x36, 0x37 padding
        // sub1 @0x38
        rdt[0x38] = 0x10;                                  // return
        return rdt;
    }

    [Fact]
    public void Insert_GrowsBuffer_AndPlacesRecordAtOffset()
    {
        var rdt = BuildScript();
        var record = new byte[] { 0x01, 0xAA, 0xBB, 0xCC };
        var outBuf = ScriptInjector.Insert(rdt, 0x30, record);

        Assert.Equal(rdt.Length + 4, outBuf.Length);
        Assert.Equal(record, outBuf[0x30..0x34]);
        // bytes before O are verbatim; the byte formerly at 0x30 moved to 0x34.
        Assert.Equal(rdt[0x2c], outBuf[0x2c]);
        Assert.Equal(rdt[0x30], outBuf[0x34]);
    }

    [Fact]
    public void Insert_ShiftsFuncTableEntriesPastOffset_Only()
    {
        var rdt = BuildScript();
        var outBuf = ScriptInjector.Insert(rdt, 0x30, new byte[4]);

        // entry0 -> sub0 at 0x28 (< O): unchanged. entry1 -> sub1 at 0x38 (>= O): +4.
        Assert.Equal(0x08u, GetU32(outBuf, 0x20));
        Assert.Equal(0x18u + 4, GetU32(outBuf, 0x24));
        // the table is re-read at the same place and both subroutine starts now resolve.
        Assert.True(ScriptInjector.TryReadFuncTable(outBuf, out _, out var starts));
        Assert.Equal(new[] { 0x28, 0x3c }, starts);
    }

    [Fact]
    public void Insert_StraddlingForwardGoto_GrowsOperandByK()
    {
        var rdt = BuildScript();
        // O = 0x30 lies between the goto (0x28) and its target (0x34) -> straddle.
        var outBuf = ScriptInjector.Insert(rdt, 0x30, new byte[4]);

        // operand grew 0x0c -> 0x10; resolved target is the moved return at 0x38.
        Assert.Equal((short)0x10, GetI16(outBuf, 0x2a));
        var sites = ScriptInjector.BranchSites(outBuf);
        var goto0 = Assert.Single(sites, s => s.Offset == 0x28);
        Assert.Equal(0x38, goto0.Target);
        Assert.Equal(0x10, outBuf[goto0.Target]); // still points at a return opcode
    }

    [Fact]
    public void Insert_NonStraddlingGoto_LeavesOperandUnchanged()
    {
        var rdt = BuildScript();
        // O = 0x38 (start of sub1) is past both the goto and its target -> no straddle.
        var outBuf = ScriptInjector.Insert(rdt, 0x38, new byte[4]);
        Assert.Equal((short)0x0c, GetI16(outBuf, 0x2a));     // operand unchanged
        var goto0 = Assert.Single(ScriptInjector.BranchSites(outBuf), s => s.Offset == 0x28);
        Assert.Equal(0x34, goto0.Target);                    // still resolves to the (unmoved) return
    }

    [Fact]
    public void Insert_BackwardLoop_StraddleAndNonStraddle()
    {
        // sub0: nop@0x28, loop@0x2c jumping back to 0x28.
        var rdt = new byte[0x40];
        PutU32(rdt, 0x14, B + 0x20);
        PutU32(rdt, 0x20, 0x04);          // 1 subroutine -> sub0 at 0x24
        rdt[0x24] = 0x01;                 // nop @0x24
        rdt[0x28] = 0x01;                 // nop @0x28
        rdt[0x2c] = 0x0a;                 // loop-next @0x2c
        BinaryPrimitives.WriteInt16LittleEndian(rdt.AsSpan(0x2e, 2), 0x08); // target = 0x2c - 8 = 0x24
        rdt[0x30] = 0x10;                 // return

        // straddle: O=0x28 between target(0x24) and loop(0x2c). Distance grows by K.
        var s1 = ScriptInjector.Insert(rdt, 0x28, new byte[4]);
        var loopS = Assert.Single(ScriptInjector.BranchSites(s1), x => rdt0Op(s1, x.Offset) == 0x0a);
        Assert.Equal(0x24, loopS.Target);            // target unmoved (< O)
        Assert.Equal((short)0x0c, GetI16(s1, loopS.Offset + 2)); // 0x08 -> 0x0c

        // non-straddle: O=0x34 past both -> unchanged.
        var s2 = ScriptInjector.Insert(rdt, 0x34, new byte[4]);
        Assert.Equal((short)0x08, GetI16(s2, 0x2e));
    }

    private static byte rdt0Op(byte[] b, int off) => b[off];

    [Fact]
    public void Insert_RelocatesFileFormPointersPastOffset_Only()
    {
        var rdt = BuildScript();
        PutU32(rdt, 0x1c, B + 0x10);     // ptr target 0x10 (< O): must stay
        var outBuf = ScriptInjector.Insert(rdt, 0x30, new byte[4]);

        Assert.Equal(B + 0x34 + 4, GetU32(outBuf, 0x18)); // target 0x34 >= O -> +4
        Assert.Equal(B + 0x10, GetU32(outBuf, 0x1c));     // target 0x10 < O -> unchanged
    }

    [Fact]
    public void Insert_LeavesFixedRamPointersAlone()
    {
        var rdt = BuildScript();
        PutU32(rdt, 0x1c, 0x80150000);   // fixed PSX RAM, outside the (small) RDT -> never relocated
        var outBuf = ScriptInjector.Insert(rdt, 0x30, new byte[4]);
        Assert.Equal(0x80150000u, GetU32(outBuf, 0x1c));
    }

    [Theory]
    [InlineData(0x32, 4)]   // offset not 4-aligned
    [InlineData(0x30, 6)]   // record length not a multiple of 4
    [InlineData(0x30, 0)]   // empty record
    [InlineData(0x10, 4)]   // offset before the table base (0x20)
    public void Insert_InvalidInputs_Throw(int offset, int recordLen)
    {
        var rdt = BuildScript();
        Assert.Throws<ArgumentException>(() => ScriptInjector.Insert(rdt, offset, new byte[recordLen]));
    }

    [Theory]
    [InlineData(0x28, 0)]   // sub0 first opcode (the goto)
    [InlineData(0x2c, 0)]   // sub0 nop boundary
    [InlineData(0x30, 0)]   // sub0 nop boundary
    [InlineData(0x38, 1)]   // sub1 start
    public void SubroutineAtBoundary_ResolvesCleanBoundaries(int offset, int expectedSub)
        => Assert.Equal(expectedSub, ScriptInjector.SubroutineAtBoundary(BuildScript(), offset));

    [Theory]
    [InlineData(0x2a)]    // inside the 4-byte goto @0x28
    [InlineData(0x32)]    // inside the nop @0x30
    [InlineData(0x100)]   // past the end of the buffer
    public void SubroutineAtBoundary_RejectsMidInstructionAndOutOfRange(int offset)
        => Assert.Equal(-1, ScriptInjector.SubroutineAtBoundary(BuildScript(), offset));

    // --- gated real install ---------------------------------------------------------------------

    private static string? DataDir()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        foreach (var c in new[] { root, Path.Combine(root, "Data") })
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "st*.dat").Any())
                return c;
        return Directory.EnumerateDirectories(root, "Data", SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>
    /// Inject a 24-byte record before sub0's terminator in every cleanly-parsing room and assert the
    /// grown script still walks clean and every pre-existing branch resolves to the same target opcode
    /// (branch-equivalence) — the static proof that the relocation is complete across the real corpus.
    /// </summary>
    [Fact]
    public void RealInstall_InjectIntoEveryCleanRoom_StaysCleanAndBranchEquivalent()
    {
        var dir = DataDir();
        if (dir is null) return; // no game files: no-op (CI)

        int injected = 0;
        foreach (var path in Directory.EnumerateFiles(dir, "st*.dat"))
        {
            var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (name.Length < 5) continue;
            var rf = RoomFile.Read(0, 0, File.ReadAllBytes(path));
            if (!rf.ParsedCleanly) continue;
            var rdt = rf.RdtBuffer;
            if (!ScriptInjector.TryReadFuncTable(rdt, out _, out var starts) || starts.Count == 0) continue;

            // O = last 4-aligned opcode boundary in sub0 (just before its terminator).
            int s0 = starts[0], e0 = starts.Count > 1 ? starts[1] : rdt.Length;
            int o = -1, pos = s0;
            while (pos < e0)
            {
                int len = DcOpcodes.Length(rdt, pos);
                if (len <= 0 || pos + len > e0) break;
                if (pos > s0 && (pos & 3) == 0) o = pos;
                pos += len;
            }
            if (o < 0) continue;

            // Branch target opcodes before injection (multiset), to compare after.
            var before = ScriptInjector.BranchSites(rdt).Select(b => rdt[b.Target]).OrderBy(x => x).ToArray();

            var record = new byte[24];
            record[0] = DcOpcodes.Enemy;
            PutU32(record, 0x10, B + (uint)s0); // dummy in-range model/motion ptrs
            PutU32(record, 0x14, B + (uint)s0);
            var grown = ScriptInjector.Insert(rdt, o, record);

            var reparsed = RoomScript.Parse(grown);
            Assert.True(reparsed.ParsedCleanly, $"{name}: grown script did not walk clean");

            var after = ScriptInjector.BranchSites(grown).Select(b => grown[b.Target]).OrderBy(x => x).ToArray();
            Assert.Equal(before, after); // every branch still lands on the same opcode kind
            injected++;
        }
        Assert.True(injected >= 90, $"expected to inject into the full corpus, only did {injected}");
    }
}
