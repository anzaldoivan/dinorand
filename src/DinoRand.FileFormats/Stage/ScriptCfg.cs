using System.Buffers.Binary;

namespace DinoRand.FileFormats.Stage;

/// <summary>
/// Control-flow analysis over one SCD subroutine: which opcode offsets are <i>guaranteed</i> to
/// execute on every entry. This is the static half of the spawn-reliability fix
/// (docs/decisions/dc1/spawn/ENEMY-SPAWN-SYSTEM.md, docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.17). A placed <c>0x20</c> only spawns
/// when control flow actually reaches it; the shipped tail-insert assumed "the last opcode of sub0
/// runs at load", which the corpus disproves in 13 of 25 enemy rooms (a branch or an
/// <c>0x04</c>/<c>0x10</c> exit precedes the tail on every path).
///
/// <para>"Will offset O run on every entry?" is exactly "<b>O post-dominates the entry</b>". This
/// class builds the subroutine CFG with the verified edge semantics and returns the latest
/// 4-aligned post-dominated offset — a <b>dominance-safe injection point</b> (setup has run, and the
/// spawn is unconditional by construction).</para>
///
/// <para><b>Edge semantics (verified from the DINO.exe handlers).</b>
/// <c>0x04</c>/<c>0x10</c>/<c>0x11</c> are subroutine terminators (no successor);
/// <c>0x0c</c> goto has the single target <c>pos + s16[+2]</c>;
/// <c>0x0e</c> cond-goto has {fall-through, target} — except operand <c>byte[+1]==0</c>, which the
/// handler makes an unconditional take ({target} only);
/// <c>0x0a</c> loop-next has {fall-through, back=<c>pos - s16[+2]</c>};
/// <c>0x0f</c> gosub and every other opcode fall through. A branch target that is not itself a
/// decoded opcode boundary is dropped (mis-aligned / cross-subroutine), and a walk that runs off the
/// end without a terminator ends at an implicit exit.</para>
///
/// Clean-room: derived from our own disassembly; pure CFG algorithm over those facts.
/// </summary>
public static class ScriptCfg
{
    private const byte Goto = 0x0c, CondGoto = 0x0e, LoopNext = 0x0a;
    private const byte Exit04 = 0x04, Return10 = 0x10, End11 = 0x11;

    /// <summary>
    /// A control-flow opcode that a record insertion must NOT displace: the terminators <c>0x10</c>/
    /// <c>0x11</c>, the branches <c>0x0c</c>/<c>0x0e</c>, the loop-next <c>0x0a</c>, and <c>0x04</c> —
    /// which is <b>not</b> a plain terminator but a <b>counter-gated loop/return</b> (handler
    /// <c>0x4a3296</c>: ends the subroutine when <c>task+7==0</c>, else decrements and loops back via
    /// <c>task+0xac</c>). Splicing a record at such a slot derails the SCD VM — 0102 crashed at stage load
    /// when the auto-init offset landed on sub0's <c>0x04</c> and the VM ran off the subroutine into data,
    /// dispatching an invalid opcode (docs/reference/dc1/enemies/ENEMY-INJECTION-MODES.md "0102 load-crash RCA").
    /// </summary>
    public static bool IsControlOpcode(byte op) => op is 0x04 or 0x0a or 0x0c or 0x0e or 0x10 or 0x11;

    /// <summary>
    /// The latest 4-aligned opcode offset strictly inside <c>[start,end)</c> that post-dominates
    /// <paramref name="start"/> (i.e. runs on every entry) <b>and is a plain (non-control) opcode</b>.
    /// Control-flow opcodes (<see cref="IsControlOpcode"/>) are excluded: a record spliced before a branch
    /// / loop / the <c>0x04</c> loop-return derails the VM (the 0102 load-crash). Returns -1 if the
    /// subroutine does not parse into a usable CFG or has no interior post-dominated plain boundary.
    /// </summary>
    public static int SafeInsertOffset(ReadOnlySpan<byte> rdt, int start, int end)
    {
        var pdom = EntryPostDominators(rdt, start, end);
        if (pdom is null) return -1;
        int best = -1;
        foreach (int o in pdom)
            if (o > start && (o & 3) == 0 && o > best && !IsControlOpcode(rdt[o])) best = o;
        return best;
    }

    /// <summary>
    /// The set of opcode offsets that post-dominate the subroutine entry (the <i>mandatory spine</i>):
    /// every path from entry to a subroutine exit passes through each of them, so each is guaranteed
    /// to execute. Null if the subroutine has no decodable opcodes. Exposed for analysis/tests.
    /// </summary>
    public static IReadOnlySet<int>? EntryPostDominators(ReadOnlySpan<byte> rdt, int start, int end)
    {
        // 1. Linear-decode into nodes + successor edges.
        var offsets = new List<int>();
        var succ = new Dictionary<int, List<int>>();
        int pos = start;
        while (pos < end)
        {
            int len = DcOpcodes.Length(rdt, pos);
            if (len <= 0 || pos + len > end) break; // trailing non-code data / derail
            byte op = rdt[pos];
            int next = pos + len;
            int? fall = next < end ? next : null;
            var edges = new List<int>();
            switch (op)
            {
                case Exit04:
                case Return10:
                case End11:
                    // Treated as exits for post-dominance (each eventually leaves the subroutine). NOTE:
                    // 0x04 is actually a counter-gated loop/return (handler 0x4a3296) whose non-exit branch
                    // loops back to a RUNTIME pc (task+0xac), so no correct static back-edge exists — hence
                    // it stays modelled as an exit here, but is excluded as a splice site by IsControlOpcode
                    // (a record displacing it derails the VM — the 0102 load-crash, ENEMY-INJECTION-MODES.md).
                    break; // terminator / loop-return: no static successor
                case Goto:
                {
                    int tgt = pos + BinaryPrimitives.ReadInt16LittleEndian(rdt.Slice(pos + 2, 2));
                    if (tgt >= start && tgt < end) edges.Add(tgt);
                    break;
                }
                case CondGoto:
                {
                    int tgt = pos + BinaryPrimitives.ReadInt16LittleEndian(rdt.Slice(pos + 2, 2));
                    bool unconditional = rdt[pos + 1] == 0; // bit-index 0 => always-take (cont.17)
                    if (!unconditional && fall is int f1) edges.Add(f1);
                    if (tgt >= start && tgt < end) edges.Add(tgt);
                    break;
                }
                case LoopNext:
                {
                    int back = pos - BinaryPrimitives.ReadInt16LittleEndian(rdt.Slice(pos + 2, 2));
                    if (fall is int f2) edges.Add(f2);
                    if (back >= start && back < end) edges.Add(back);
                    break;
                }
                default:
                    if (fall is int f3) edges.Add(f3); // gosub + ordinary opcodes
                    break;
            }
            offsets.Add(pos);
            succ[pos] = edges;
            pos = next;
        }
        if (offsets.Count == 0) return null;

        var nodeSet = new HashSet<int>(offsets);
        foreach (int o in offsets)
            succ[o].RemoveAll(t => !nodeSet.Contains(t)); // drop mis-aligned targets

        int entry = offsets[0];

        // 2. Reachable nodes from entry (unreachable dead code must not affect post-dominance).
        var reach = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(entry);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            if (!reach.Add(n)) continue;
            foreach (int t in succ[n]) stack.Push(t);
        }

        // 3. Post-dominators over the reachable graph with a virtual EXIT sink (-1). A node whose
        //    in-range successors are all gone (terminator / dead-end) flows to EXIT.
        const int Exit = -1;
        var nodes = offsets.Where(reach.Contains).ToList();
        var outs = new Dictionary<int, List<int>>();
        foreach (int n in nodes)
        {
            var os = succ[n].Where(reach.Contains).ToList();
            outs[n] = os.Count > 0 ? os : new List<int> { Exit };
        }

        var universe = new HashSet<int>(nodes) { Exit };
        var pdom = new Dictionary<int, HashSet<int>>();
        foreach (int n in nodes) pdom[n] = new HashSet<int>(universe);
        pdom[Exit] = new HashSet<int> { Exit };

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (int n in nodes)
            {
                HashSet<int>? inter = null;
                foreach (int t in outs[n])
                {
                    if (inter is null) inter = new HashSet<int>(pdom[t]);
                    else inter.IntersectWith(pdom[t]);
                }
                inter ??= new HashSet<int>();
                inter.Add(n);
                if (!inter.SetEquals(pdom[n])) { pdom[n] = inter; changed = true; }
            }
        }

        var result = new HashSet<int>(pdom[entry]);
        result.Remove(Exit);
        return result;
    }
}
