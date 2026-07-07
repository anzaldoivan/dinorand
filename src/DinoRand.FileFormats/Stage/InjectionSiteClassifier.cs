using System.Buffers.Binary;

namespace DinoRand.FileFormats.Stage;

/// <summary>Does an injected enemy hunt? <see cref="Inert"/> = present but frozen (<c>entity+0x3E=0</c>);
/// <see cref="Active"/> = activates and hunts (<c>+0x3E=1</c>).</summary>
public enum SpawnActivation { Inert, Active }

/// <summary>Does an injected enemy come back? <see cref="OneShot"/> = a one-time encounter (rides a
/// self-latching event trigger that never re-fires); <see cref="EveryEntry"/> = re-instantiated on every
/// room entry (init runs at every load — actual placement still subject to the site's own gate).</summary>
public enum SpawnPersistence { OneShot, EveryEntry }

/// <summary>
/// One candidate <c>0x20</c> injection offset with its predicted spawn mode. The mode is the pair
/// (<see cref="Activation"/>, <see cref="Persistence"/>) — two orthogonal axes, see
/// docs/reference/dc1/enemies/ENEMY-INJECTION-MODES.md.
/// </summary>
public readonly record struct InjectionSite(
    int Offset,
    int Subroutine,
    bool IsInit,
    SpawnActivation Activation,
    SpawnPersistence Persistence,
    bool IsConditionalBranchTarget,
    bool PostDominatesEntry,
    string Rationale);

/// <summary>
/// The decoupled <b>site-selection</b> layer for enemy injection: it labels each clean opcode boundary in a
/// room's SCD with its predicted (activation × persistence) spawn mode, so the <i>intent</i> ("standing
/// hunter" / "one-shot encounter" / "inert prop") is chosen from analysis rather than from a raw offset the
/// caller derived by hand. Pure / DOM-free over the decompressed RDT, like <see cref="ScriptCfg"/> and
/// <see cref="ScriptInjector"/>; <see cref="RoomFile"/>'s intent functions are thin consumers.
///
/// <para><b>Activation tracks the splice SITE's reachability</b> (not "init vs event", the pre-2026-06-28
/// reading): a site the engine reaches on the settled main init flow — i.e. one that <b>post-dominates</b>
/// sub0's entry — spawns <i>inert</i> (STATIC-SCD-RE cont.16/18); a site reached <b>conditionally</b> (an
/// event sub, or a sub0 <b>branch-target</b> block) spawns <i>active</i>. The latter was proven by copy B
/// (a <c>0x20</c> in 010A's flag-gated init branch-target block) reading <c>+0x3E=1</c> in the 44912 dump
/// (ADD-ENEMY-EVENT-INJECTION-CRASH-RCA.md "44912 dump RCA").</para>
///
/// <para><b>Persistence (coarse, honest).</b> An init site is <see cref="SpawnPersistence.EveryEntry"/>
/// (init runs at every load; whether it actually places on a given entry is decided by its own gate). An
/// event site is conservatively <see cref="SpawnPersistence.OneShot"/> — most event subs are self-latching
/// AOTs, and proving re-arming needs the corpus-wide flag-setter scan (spawn_catalog.py) that is not ported
/// here. So the gate-latch refinement is deferred; the labels never over-promise.</para>
/// </summary>
public static class InjectionSiteClassifier
{
    private const byte CondGoto = 0x0e; // pc += s16[+2]; byte[+1]==0 => unconditional take (cont.17)

    /// <summary>Every clean, 4-aligned, interior opcode boundary in the room, each labelled with its
    /// predicted spawn mode. Empty if the function table is unreadable.</summary>
    public static IReadOnlyList<InjectionSite> Classify(ReadOnlySpan<byte> rdt)
    {
        var result = new List<InjectionSite>();
        if (!ScriptInjector.TryReadFuncTable(rdt, out _, out var starts) || starts.Count == 0)
            return result;

        var condTargets = ConditionalBranchTargets(rdt);
        int s0 = starts[0], e0 = starts.Count > 1 ? starts[1] : rdt.Length;
        var pdom = ScriptCfg.EntryPostDominators(rdt, s0, e0); // sub0 only distinguishes inert vs active

        for (int i = 0; i < starts.Count; i++)
        {
            int s = starts[i], e = i + 1 < starts.Count ? starts[i + 1] : rdt.Length;
            int pos = s;
            while (pos < e)
            {
                int len = DcOpcodes.Length(rdt, pos);
                if (len <= 0 || pos + len > e) break; // trailing data / derail
                if (pos > s && (pos & 3) == 0)
                {
                    bool isInit = i == 0;
                    bool isTarget = condTargets.Contains(pos);
                    bool postDom = isInit && pdom is not null && pdom.Contains(pos);
                    SpawnActivation act;
                    SpawnPersistence persist;
                    string why;
                    if (isInit)
                    {
                        act = postDom ? SpawnActivation.Inert : SpawnActivation.Active;
                        persist = SpawnPersistence.EveryEntry;
                        why = postDom
                            ? "sub0 post-dominates entry: settled main flow, every load -> inert (cont.16/18)"
                            : "sub0 but conditionally reached (branch-target / guarded): engine activates -> active (copy B)";
                    }
                    else
                    {
                        act = SpawnActivation.Active;
                        persist = SpawnPersistence.OneShot;
                        why = "event sub (sub>0): reached on its trigger -> active; assumed one-shot (self-latching AOT)";
                    }
                    result.Add(new InjectionSite(pos, i, isInit, act, persist, isTarget, postDom, why));
                }
                pos += len;
            }
        }
        return result;
    }

    /// <summary>
    /// The offset of an <b>(active, every-entry)</b> "standing native-like" site, or <c>-1</c> if the room
    /// has none. It is the boundary just after the room's <b>existing standing enemy</b> inside a
    /// <b>branch-target spawn block</b>: a sub0 block whose head is a conditional <c>0x0e</c> target, whose
    /// fall-through is guarded by an <b>unconditional</b> <c>0x0e</c> jumping forward past it, and which
    /// already contains an enemy placement (<c>0x20</c>/<c>0x59</c>). Copy B rode exactly this — it sat right
    /// after 010A's native <c>0x59 @0x48860</c>, at <c>0x48874</c>. Requiring an existing enemy targets the
    /// room's standing-enemy block specifically, not an arbitrary gated (e.g. camera/scene) block; splicing
    /// strictly after the enemy (not at the branch-target head) keeps the record reachable, since a branch
    /// whose target equals the insertion offset is relocated past the inserted bytes.
    /// </summary>
    public static int StandingSite(ReadOnlySpan<byte> rdt)
    {
        if (!ScriptInjector.TryReadFuncTable(rdt, out _, out var starts) || starts.Count == 0) return -1;
        int s0 = starts[0], e0 = starts.Count > 1 ? starts[1] : rdt.Length;
        var pdom = ScriptCfg.EntryPostDominators(rdt, s0, e0);
        var condTargets = ConditionalBranchTargets(rdt);

        // Ordered sub0 opcode boundaries.
        var bounds = new List<int>();
        for (int pos = s0; pos < e0;)
        {
            int len = DcOpcodes.Length(rdt, pos);
            if (len <= 0 || pos + len > e0) break;
            bounds.Add(pos);
            pos += len;
        }

        for (int idx = 1; idx < bounds.Count; idx++)
        {
            int t = bounds[idx];
            if (!condTargets.Contains(t)) continue;             // head must be a conditional jump destination
            int g = bounds[idx - 1];                            // opcode immediately before the head
            if (rdt[g] != CondGoto || rdt[g + 1] != 0) continue; // ...must be an UNCONDITIONAL 0x0e (the guard)
            int guardTarget = g + BinaryPrimitives.ReadInt16LittleEndian(rdt.Slice(g + 2, 2));
            if (guardTarget <= t) continue;                     // guard must skip forward past the head

            // The block must hold the room's existing standing enemy; splice right after it.
            for (int j = idx; j < bounds.Count && bounds[j] < guardTarget; j++)
            {
                byte op = rdt[bounds[j]];
                if (op != DcOpcodes.Enemy && op != DcOpcodes.Enemy2) continue;
                int o = bounds[j] + DcOpcodes.Length(rdt, bounds[j]);
                if (o >= guardTarget || (o & 3) != 0) break;     // no room after the enemy before convergence
                if (pdom is not null && pdom.Contains(o)) break; // must stay conditionally-reached (active)
                return o;
            }
        }
        return -1;
    }

    /// <summary>
    /// The offset of an <b>(active, one-shot)</b> event site — the first clean interior boundary in a
    /// non-init subroutine (one reached by an AOT trigger / gosub mid-game) — or <c>-1</c> if the room has
    /// no event subroutine. This formalizes the event-injection capability (copy A family / the
    /// <c>--add-enemy-at</c> path) as a named selection.
    /// </summary>
    public static int EncounterSite(ReadOnlySpan<byte> rdt)
    {
        if (!ScriptInjector.TryReadFuncTable(rdt, out _, out var starts) || starts.Count < 2) return -1;
        for (int i = 1; i < starts.Count; i++)
        {
            int s = starts[i], e = i + 1 < starts.Count ? starts[i + 1] : rdt.Length;
            for (int pos = s; pos < e;)
            {
                int len = DcOpcodes.Length(rdt, pos);
                if (len <= 0 || pos + len > e) break;
                if (pos > s && (pos & 3) == 0) return pos;
                pos += len;
            }
        }
        return -1;
    }

    /// <summary>Targets of <b>conditional</b> <c>0x0e</c> branches (byte[+1]!=0) across all subroutines —
    /// the offsets reached by a flag/test jump, not by straight-line fall-through.</summary>
    private static HashSet<int> ConditionalBranchTargets(ReadOnlySpan<byte> rdt)
    {
        var set = new HashSet<int>();
        if (!ScriptInjector.TryReadFuncTable(rdt, out _, out var starts)) return set;
        for (int i = 0; i < starts.Count; i++)
        {
            int s = starts[i], e = i + 1 < starts.Count ? starts[i + 1] : rdt.Length;
            for (int pos = s; pos < e;)
            {
                int len = DcOpcodes.Length(rdt, pos);
                if (len <= 0 || pos + len > e) break;
                if (rdt[pos] == CondGoto && rdt[pos + 1] != 0)
                    set.Add(pos + BinaryPrimitives.ReadInt16LittleEndian(rdt.Slice(pos + 2, 2)));
                pos += len;
            }
        }
        return set;
    }
}
