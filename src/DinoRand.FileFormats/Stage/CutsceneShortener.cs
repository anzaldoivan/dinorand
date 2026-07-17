using System.Buffers.Binary;

namespace DinoRand.FileFormats.Stage;

/// <summary>
/// DC1 cutscene-shorten rewrite (CUTSCENE-SKIP-FEASIBILITY.md §9.3, STATIC-SCD-RE cont.74).
///
/// <para>A DC1 cutscene is a script-authored bracket: an event sub runs
/// <c>SetFlag(2,2,1)</c> … choreography + side-effect ops … <c>SetFlag(2,2,0)</c> (op-0x25/0x48);
/// a native wrapper task (0x409773) letterboxes and saves/restores the player around the bracket.
/// This rewrite keeps every side-effect op — flag writes (op-0x25/0x48, any group), AOT records
/// (op-0x28, item grants included) and op-0x4C placements — and jumps each maximal run of
/// choreography ops with an op-<c>0x0c</c> goto, tiling the dead bytes with op-<c>0x00</c> so a
/// linear walker still lands on opcode boundaries. Everything is in place: no relocation, no
/// length change, no branch retargeting.</para>
///
/// <para>Conservative whitelist — a bracket is left untouched when any of these hold:
/// interior contains a control-transfer op (task-spawn 0x01, goto-sub 0x05, gosub 0x0f — side
/// effects could live in the callee; end 0x00 / return 0x10 — control anomaly), a choreography run
/// is too short to hold the 4-byte goto, or a live branch elsewhere in the sub targets a run's
/// interior. A rewritten bracket's interior contains 0x00 tiles, so a second pass rejects it —
/// the rewrite is byte-idempotent.</para>
/// </summary>
public static class CutsceneShortener
{
    private const byte SetFlagOp = 0x25;   // 25 <group> <idx> <val>
    private const byte SetVarFlagOp = 0x48; // 48 <group> <idx> <val> (same operand layout)
    private const byte GotoOp = 0x0c;      // pc += s16[+2]
    private const byte CondGotoOp = 0x0e;
    private const byte LoopNextOp = 0x0a;  // pc -= s16[+2]
    private const int GotoLength = 4;

    /// <summary>One flag(2,2) bracket found in a script, with its whitelist verdict.</summary>
    public readonly record struct Bracket(int SubIndex, int OpenOffset, int CloseOffset, bool Eligible, string? Reason);

    private static bool IsFlagWrite(ReadOnlySpan<byte> rdt, int pos, byte group, byte idx, byte val)
        => (rdt[pos] == SetFlagOp || rdt[pos] == SetVarFlagOp)
           && rdt[pos + 1] == group && rdt[pos + 2] == idx && rdt[pos + 3] == val;

    // Side-effect ops preserved in place; everything else inside a bracket is choreography.
    private static bool IsKeep(byte op) => op is 0x25 or 0x48 or 0x28 or 0x4c;

    // Ops whose presence rejects the bracket outright (control leaves the bracket, or a parallel
    // task is spawned whose script this rewrite cannot see).
    private static bool IsRejected(byte op) => op is 0x00 or 0x01 or 0x05 or 0x0f or 0x10;

    /// <summary>All flag(2,2) brackets in the script, each with its eligibility verdict.</summary>
    public static IReadOnlyList<Bracket> FindBrackets(ReadOnlySpan<byte> rdt)
    {
        var brackets = new List<Bracket>();
        if (!ScriptInjector.TryReadFuncTable(rdt, out _, out var starts))
            return brackets;

        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i];
            int end = i + 1 < starts.Count ? starts[i + 1] : rdt.Length;

            // one linear walk: op stream + live branch targets of the whole sub
            var ops = new List<(int Pos, byte Op, int Len)>();
            int pos = start;
            while (pos < end)
            {
                int len = DcOpcodes.Length(rdt, pos);
                if (len <= 0 || pos + len > end) break; // derail / trailing data
                ops.Add((pos, rdt[pos], len));
                pos += len;
            }

            int open = -1;
            for (int k = 0; k < ops.Count; k++)
            {
                var (p, op, _) = ops[k];
                if (op is not (SetFlagOp or SetVarFlagOp)) continue;
                if (IsFlagWrite(rdt, p, 2, 2, 1)) open = k;
                else if (IsFlagWrite(rdt, p, 2, 2, 0) && open >= 0)
                {
                    brackets.Add(Classify(rdt, ops, i, open, k));
                    open = -1;
                }
            }
        }
        return brackets;
    }

    private static Bracket Classify(ReadOnlySpan<byte> rdt, List<(int Pos, byte Op, int Len)> ops,
                                    int subIndex, int openIdx, int closeIdx)
    {
        int openOff = ops[openIdx].Pos;
        int closeOff = ops[closeIdx].Pos;

        var runs = new List<(int Start, int End)>();
        int runStart = -1;
        for (int k = openIdx + 1; k < closeIdx; k++)
        {
            var (p, op, len) = ops[k];
            if (IsRejected(op))
                return new Bracket(subIndex, openOff, closeOff, false, $"op 0x{op:x2} at 0x{p:x} transfers control");
            if (IsKeep(op))
            {
                if (runStart >= 0) { runs.Add((runStart, p)); runStart = -1; }
            }
            else if (runStart < 0) runStart = p;
        }
        if (runStart >= 0) runs.Add((runStart, closeOff));

        foreach (var (a, b) in runs)
            if (b - a < GotoLength)
                return new Bracket(subIndex, openOff, closeOff, false, $"choreography run at 0x{a:x} shorter than a goto");

        // A live branch (one whose opcode is NOT inside any run of this bracket) must not target a
        // run's interior — the goto overwrite would land it mid-tile. Targets at a run start are
        // fine: the start becomes the goto.
        foreach (var (p, op, _) in ops)
        {
            if (op is not (GotoOp or CondGotoOp or LoopNextOp)) continue;
            if (runs.Any(r => p >= r.Start && p < r.End)) continue; // dead after rewrite
            short off = BinaryPrimitives.ReadInt16LittleEndian(rdt.Slice(p + 2, 2));
            int target = op == LoopNextOp ? p - off : p + off;
            foreach (var (a, b) in runs)
                if (target > a && target < b)
                    return new Bracket(subIndex, openOff, closeOff, false, $"branch at 0x{p:x} targets run interior 0x{target:x}");
        }

        return new Bracket(subIndex, openOff, closeOff, runs.Count > 0, runs.Count > 0 ? null : "no choreography to remove");
    }

    /// <summary>
    /// Rewrites every eligible bracket in place; returns the number of brackets rewritten.
    /// The buffer length never changes and ineligible brackets are left byte-identical.
    /// </summary>
    public static int Shorten(byte[] rdt)
    {
        int rewritten = 0;
        if (!ScriptInjector.TryReadFuncTable(rdt, out _, out var starts))
            return 0;

        foreach (var bracket in FindBrackets(rdt))
        {
            if (!bracket.Eligible) continue;

            // re-derive this bracket's runs (Classify proved them safe)
            int end = NextSubEnd(starts, bracket.SubIndex, rdt.Length);
            int pos = bracket.OpenOffset + GotoLength; // open op is len 4
            int runStart = -1;
            var runs = new List<(int Start, int End)>();
            while (pos < bracket.CloseOffset)
            {
                int len = DcOpcodes.Length(rdt, pos);
                if (len <= 0 || pos + len > end) { runs.Clear(); break; }
                if (IsKeep(rdt[pos]))
                {
                    if (runStart >= 0) { runs.Add((runStart, pos)); runStart = -1; }
                }
                else if (runStart < 0) runStart = pos;
                pos += len;
            }
            if (runStart >= 0) runs.Add((runStart, bracket.CloseOffset));
            if (runs.Count == 0) continue;

            foreach (var (a, b) in runs)
            {
                rdt[a] = GotoOp;
                rdt[a + 1] = 0;
                BinaryPrimitives.WriteInt16LittleEndian(rdt.AsSpan(a + 2, 2), (short)(b - a));
                for (int j = a + GotoLength; j < b; j++) rdt[j] = 0x00; // walkable dead tile
            }
            rewritten++;
        }
        return rewritten;
    }

    private static int NextSubEnd(IReadOnlyList<int> starts, int subIndex, int length)
        => subIndex + 1 < starts.Count ? starts[subIndex + 1] : length;
}
