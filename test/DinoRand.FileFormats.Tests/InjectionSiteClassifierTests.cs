using System.Buffers.Binary;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Pure, CI-independent tests for <see cref="InjectionSiteClassifier"/> — the decoupled site-selection
/// layer that labels each clean opcode boundary with its predicted (activation, persistence) spawn mode
/// (docs/dc1/ENEMY-INJECTION-MODES.md). Built on hand-laid synthetic RDTs so the control-flow shapes are
/// exact and need no game files: a sub0 with a copy-B "branch-target spawn" block (a conditional <c>0x0e</c>
/// jumps INTO it; an unconditional <c>0x0e</c> guards the fall-through OVER it; it holds an existing enemy)
/// plus a post-dominated tail and an event sub1.
/// </summary>
public class InjectionSiteClassifierTests
{
    // Synthetic RDT (88 B). Offsets are decompressed-RDT offsets:
    //   0x14: u32 = func-table pointer (PSX form 0x80100000 + 0x18)
    //   0x18: func table {entry0=0x08 -> sub0@0x20, entry1=0x34 -> sub1@0x4c}; first dword = 4*N = 8
    //   sub0 [0x20,0x4c):
    //     0x20 26 GetFlag           (entry)
    //     0x24 0e 01 08 00  cond  -> 0x2c   (jump INTO the block when the flag is set)
    //     0x28 0e 00 1c 00  uncond-> 0x44   (fall-through guard: skip the block)
    //     0x2c 59 ...(20 B)                  <- block head = conditional branch target + EXISTING enemy
    //     0x40 22                            <- right after the enemy (Active) = STANDING site
    //     0x44 22                            <- convergence: post-dominates entry (Inert)
    //     0x48 04 terminator
    //   sub1 [0x4c,0x58):  (an event sub)  0x4c 22 (entry)  0x50 22  0x54 04
    private static byte[] BuildRdt()
    {
        var b = new byte[0x58];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x14, 4), RoomScript.PsxRdtBase + 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x18, 4), 0x08);   // first entry = 4*N (N=2)
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x1c, 4), 0x34);   // entry1 -> sub1 @ 0x4c
        b[0x20] = 0x26;
        b[0x24] = 0x0e; b[0x25] = 0x01; b[0x26] = 0x08; b[0x27] = 0x00; // cond goto -> 0x2c
        b[0x28] = 0x0e; b[0x29] = 0x00; b[0x2a] = 0x1c; b[0x2b] = 0x00; // uncond goto -> 0x44
        b[0x2c] = 0x59;                                                  // Enemy2 (20 B) -> next boundary 0x40
        b[0x40] = 0x22; b[0x44] = 0x22; b[0x48] = 0x04;
        b[0x4c] = 0x22; b[0x50] = 0x22; b[0x54] = 0x04;                  // sub1 (event)
        return b;
    }

    [Fact]
    public void Classify_LabelsBranchTargetBlockActive_AndPostDominatedTailInert()
    {
        var rdt = BuildRdt();
        var sites = InjectionSiteClassifier.Classify(rdt);
        InjectionSite At(int off) => sites.Single(s => s.Offset == off);

        // Block head (0x2c): reached only via the conditional jump -> ACTIVE + conditional branch target.
        var head = At(0x2c);
        Assert.True(head.IsInit);
        Assert.Equal(SpawnActivation.Active, head.Activation);
        Assert.Equal(SpawnPersistence.EveryEntry, head.Persistence);
        Assert.True(head.IsConditionalBranchTarget);
        Assert.False(head.PostDominatesEntry);

        // Just after the enemy (0x40): still Active (conditional), not itself a branch target.
        var inside = At(0x40);
        Assert.Equal(SpawnActivation.Active, inside.Activation);
        Assert.False(inside.PostDominatesEntry);
        Assert.False(inside.IsConditionalBranchTarget);

        // Convergence (0x44): on every path -> INERT (what AddEnemy targets today).
        var tail = At(0x44);
        Assert.Equal(SpawnActivation.Inert, tail.Activation);
        Assert.True(tail.PostDominatesEntry);
        Assert.Equal(SpawnPersistence.EveryEntry, tail.Persistence);

        // Event sub boundary (0x50): Active + OneShot, not init.
        var ev = At(0x50);
        Assert.False(ev.IsInit);
        Assert.Equal(SpawnActivation.Active, ev.Activation);
        Assert.Equal(SpawnPersistence.OneShot, ev.Persistence);
    }

    [Fact]
    public void StandingSite_PicksRightAfterTheExistingEnemyInTheBlock()
    {
        // Copy B's shape: the standing site is the boundary just after the block's existing enemy (the
        // native 0x59), inside the guarded branch-target block (010A: 0x48860 + 20 -> 0x48874; here 0x2c+20).
        Assert.Equal(0x40, InjectionSiteClassifier.StandingSite(BuildRdt()));
    }

    [Fact]
    public void EncounterSite_PicksFirstEventSubBoundary()
    {
        Assert.Equal(0x50, InjectionSiteClassifier.EncounterSite(BuildRdt()));
    }

    [Fact]
    public void StandingSite_ReturnsMinusOne_WhenNoBranchTargetSpawnBlock()
    {
        // A straight-line sub0 (no conditional jump INTO a guarded block) has no standing site — the
        // honest failure, so AddEnemyStanding never silently produces a wrong mode.
        var b = new byte[0x30];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x14, 4), RoomScript.PsxRdtBase + 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x18, 4), 0x04); // first entry = 4*N (N=1)
        b[0x1c] = 0x22; b[0x20] = 0x22; b[0x24] = 0x04;                    // sub0: straight line + terminator
        Assert.Equal(-1, InjectionSiteClassifier.StandingSite(b));
    }

    [Fact]
    public void StandingSite_ReturnsMinusOne_WhenBranchTargetBlockHasNoEnemy()
    {
        // A guarded branch-target block with NO enemy placement (e.g. a camera/scene block, gated on its
        // own flag) is NOT a standing-enemy site — the selector must skip it rather than splice a raptor
        // into a scene block. Same geometry as BuildRdt but the head is a plain 0x22, not a 0x59.
        var b = BuildRdt();
        b[0x2c] = 0x22;                 // block head: ordinary opcode, no enemy in [0x2c,0x44)
        // Re-shape so the block walks cleanly as 4-byte opcodes (0x2c..0x40 were one 0x59 record before).
        b[0x30] = 0x22; b[0x34] = 0x22; b[0x38] = 0x22; b[0x3c] = 0x22;
        Assert.Equal(-1, InjectionSiteClassifier.StandingSite(b));
    }
}
