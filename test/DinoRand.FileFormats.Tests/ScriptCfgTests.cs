using System.Buffers.Binary;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Post-dominance / dominance-safe-injection analysis (docs/dc1/STATIC-SCD-RE.md cont.17). Synthetic
/// subroutines pin the CFG edge semantics and the "guaranteed to run" answer; a gated real-install
/// test asserts the offset <see cref="RoomFile.AddEnemy"/> now picks is post-dominated in every clean
/// room (the static guarantee behind the spawn-reliability fix).
/// </summary>
public class ScriptCfgTests
{
    private const uint B = ScriptInjector.PsxBase;

    private static void PutU32(byte[] buf, int off, uint v)
        => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off, 4), v);
    private static void PutI16(byte[] buf, int off, short v)
        => BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(off, 2), v);

    /// <summary>RDT with a single-subroutine function table at 0x20 (sub0 starts at 0x24).</summary>
    private static byte[] OneSub(int size = 0x80)
    {
        var rdt = new byte[size];
        PutU32(rdt, 0x14, B + 0x20); // header -> table at 0x20
        PutU32(rdt, 0x20, 0x04);     // table size 4 -> sub0 at 0x24
        return rdt;
    }

    [Fact]
    public void StraightLine_AllBoundariesPostDominate_SafeIsLast()
    {
        var rdt = OneSub();
        rdt[0x24] = 0x01;            // nop
        rdt[0x28] = 0x01;            // nop
        rdt[0x2c] = 0x10;            // return
        int end = rdt.Length;

        var pdom = ScriptCfg.EntryPostDominators(rdt, 0x24, end)!;
        Assert.Equal(new[] { 0x24, 0x28, 0x2c }, pdom.OrderBy(x => x).ToArray());
        // latest 4-aligned post-dominated PLAIN-opcode boundary: the 0x10 return at 0x2c is a control
        // opcode and is excluded (displacing it is unsafe — the 0102 load-crash fix), so the offset is the
        // nop at 0x28.
        Assert.Equal(0x28, ScriptCfg.SafeInsertOffset(rdt, 0x24, end));
    }

    [Fact]
    public void ConditionalSkipsTail_SafeIsBeforeBranch_TailIsUndominated()
    {
        // 0x24 nop (setup) ; 0x28 nop (setup) ; 0x2c cond-goto bit1 -> 0x34 ;
        // 0x30 return (fall-through early-exit) ; 0x34 nop (taken-path "enemy tail") ; 0x38 return.
        var rdt = OneSub();
        rdt[0x24] = 0x01;
        rdt[0x28] = 0x01;
        rdt[0x2c] = 0x0e; rdt[0x2d] = 0x01; PutI16(rdt, 0x2e, 0x34 - 0x2c); // conditional, target 0x34
        rdt[0x30] = 0x10;                                                   // early return
        rdt[0x34] = 0x01;                                                   // only on cond-true path
        rdt[0x38] = 0x10;                                                   // return
        int end = rdt.Length;

        var pdom = ScriptCfg.EntryPostDominators(rdt, 0x24, end)!;
        // mandatory spine = the common prefix before the split; the post-split nodes are skippable.
        Assert.Equal(new[] { 0x24, 0x28, 0x2c }, pdom.OrderBy(x => x).ToArray());
        Assert.DoesNotContain(0x30, pdom);
        Assert.DoesNotContain(0x34, pdom);
        Assert.DoesNotContain(0x38, pdom);

        int safe = ScriptCfg.SafeInsertOffset(rdt, 0x24, end);
        // Guaranteed-run AND a plain opcode: the 0x0e cond-goto at 0x2c is post-dominated but is a control
        // opcode (excluded), so the offset is the setup nop at 0x28 — strictly before the branch.
        Assert.Equal(0x28, safe);
        Assert.Contains(safe, pdom);
        Assert.False(ScriptCfg.IsControlOpcode(rdt[safe]));

        // The pre-cont.17 tail (largest interior 4-aligned boundary) is the final return at 0x38,
        // which is NOT post-dominated — the bug this fix closes.
        Assert.DoesNotContain(0x38, pdom);
        Assert.True(safe < 0x38);
    }

    [Fact]
    public void UnconditionalCondGoto_BitIndexZero_TreatedAsGoto()
    {
        // cond-goto with operand byte[+1]==0 is an unconditional take (cont.17): the fall-through
        // opcode becomes unreachable, so it cannot be a post-dominator.
        var rdt = OneSub();
        rdt[0x24] = 0x0e; rdt[0x25] = 0x00; PutI16(rdt, 0x26, 0x30 - 0x24); // uncond -> 0x30
        rdt[0x28] = 0x01;  // dead (fall-through never taken)
        rdt[0x2c] = 0x01;  // dead
        rdt[0x30] = 0x01;  // reachable nop (the goto target)
        rdt[0x34] = 0x10;  // return
        int end = rdt.Length;

        var pdom = ScriptCfg.EntryPostDominators(rdt, 0x24, end)!;
        Assert.Equal(new[] { 0x24, 0x30, 0x34 }, pdom.OrderBy(x => x).ToArray());
        Assert.DoesNotContain(0x28, pdom);
        // SafeInsertOffset skips the control opcodes (the 0x0e goto at 0x24, the 0x10 return at 0x34) and
        // returns the reachable plain nop at 0x30.
        Assert.Equal(0x30, ScriptCfg.SafeInsertOffset(rdt, 0x24, end));
    }

    [Fact]
    public void LoopBody_PostDominatesThroughFallThrough()
    {
        // 0x24 nop ; 0x28 loop-next back to 0x24 ; 0x2c return.
        var rdt = OneSub();
        rdt[0x24] = 0x01;
        rdt[0x28] = 0x0a; PutI16(rdt, 0x2a, 0x28 - 0x24); // loop-next, back to 0x24
        rdt[0x2c] = 0x10;                                  // return (loop-exit fall-through)
        int end = rdt.Length;

        var pdom = ScriptCfg.EntryPostDominators(rdt, 0x24, end)!;
        // loop-next falls through to the return on exit, so all three run on every entry.
        Assert.Equal(new[] { 0x24, 0x28, 0x2c }, pdom.OrderBy(x => x).ToArray());
    }

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
    /// For every cleanly-parsing room, the dominance-safe offset is real, strictly interior, and
    /// actually post-dominates sub0's entry — the static guarantee that an enemy injected there is
    /// reached on every room entry (which the old tail offset was not, in 13 of 25 enemy rooms).
    /// </summary>
    [Fact]
    public void RealInstall_SafeOffsetIsPostDominated_InEveryCleanRoom()
    {
        var dir = DataDir();
        if (dir is null) return; // no game files: no-op (CI)

        int examined = 0;
        foreach (var path in Directory.EnumerateFiles(dir, "st*.dat"))
        {
            var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (name.Length < 5) continue; // st1..st9 are stage files, not rooms (not LZSS room data)
            var rf = RoomFile.Read(0, 0, File.ReadAllBytes(path));
            if (!rf.ParsedCleanly) continue;
            var rdt = rf.RdtBuffer;
            if (!ScriptInjector.TryReadFuncTable(rdt, out _, out var starts) || starts.Count == 0) continue;

            int s0 = starts[0], e0 = starts.Count > 1 ? starts[1] : rdt.Length;
            int safe = ScriptCfg.SafeInsertOffset(rdt, s0, e0);
            if (safe < 0) continue; // (no corpus room hits this)

            var pdom = ScriptCfg.EntryPostDominators(rdt, s0, e0)!;
            Assert.True(safe > s0, $"{name}: safe offset not interior");
            Assert.True((safe & 3) == 0, $"{name}: safe offset not 4-aligned");
            Assert.Contains(safe, pdom);
            examined++;
        }
        Assert.True(examined >= 90, $"expected the full corpus, only checked {examined}");
    }
}
