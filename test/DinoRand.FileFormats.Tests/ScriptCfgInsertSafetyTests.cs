using System.Buffers.Binary;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Pure tests for the splice-offset safety rule (docs/dc1/ENEMY-INJECTION-MODES.md "0102 load-crash RCA"):
/// <see cref="ScriptCfg.SafeInsertOffset"/> must never return a <b>control-flow opcode</b> slot
/// (terminator / branch / the counter-gated <c>0x04</c> loop-return). Inserting a record there derails the
/// SCD VM (0102 crashed at stage load: the VM ran past sub0's <c>0x04</c> and dispatched an invalid opcode).
/// </summary>
public class ScriptCfgInsertSafetyTests
{
    // Straight-line sub0: three plain 0x22 ops then a 0x04 terminator (all post-dominate the entry).
    //   0x18: func table {entry0=4 -> sub0 @0x1c}; first dword = 4*N = 4 (N=1)
    //   0x1c 22   0x20 22   0x24 22   0x28 04   (end 0x2c)
    private static byte[] BuildRdt()
    {
        var b = new byte[0x2c];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x14, 4), RoomScript.PsxRdtBase + 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x18, 4), 0x04);
        b[0x1c] = 0x22; b[0x20] = 0x22; b[0x24] = 0x22; b[0x28] = 0x04;
        return b;
    }

    [Fact]
    public void SafeInsertOffset_DoesNotReturnTheTerminalControlOpcode()
    {
        var rdt = BuildRdt();
        int o = ScriptCfg.SafeInsertOffset(rdt, 0x1c, 0x2c);
        // The 0x04 at 0x28 is the latest post-dominated boundary, but it is a control opcode — the fix
        // returns the plain 0x22 before it (0x24) instead.
        Assert.Equal(0x24, o);
        Assert.False(ScriptCfg.IsControlOpcode(rdt[o]));
    }

    [Fact]
    public void IsControlOpcode_CoversTerminatorsBranchesAndTheLoopReturn()
    {
        // 0x04 is a counter-gated loop/return (handler 0x4a3296), not a plain terminator — the 0102 bug.
        // 0x05 is the GOTO-SUB tail-call (handler 0x4A32F7, cont.50): displacing it rewires the sub,
        // so it joined the control set (cont.52 — the pre-cont.50 model treated it as plain).
        foreach (byte op in new byte[] { 0x04, 0x05, 0x0a, 0x0c, 0x0e, 0x10, 0x11 })
            Assert.True(ScriptCfg.IsControlOpcode(op), $"0x{op:x2} should be a control opcode");
        foreach (byte op in new byte[] { 0x20, 0x59, 0x22, 0x58, 0x26, 0x23, 0x01, 0x0f })
            Assert.False(ScriptCfg.IsControlOpcode(op), $"0x{op:x2} should NOT be a control opcode");
    }
}
