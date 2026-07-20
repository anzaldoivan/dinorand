namespace DinoRand.ApClient;

/// <summary>
/// Pure predicate evaluator: given the group-7 taken-flag bank bytes, which AP locations read as
/// checked? Flags latch on pickup (0x44A6B9) and predicates of non-excluded locations are
/// pairwise unique (gen_ap_logic --check invariant), so evaluation is stateless — the poll
/// engine diffs against the already-sent set. Shared-flag (excluded) locations fire as a group
/// by construction: every member carries the same anyOf indices.
/// </summary>
public sealed class Dc1CheckTracker
{
    private readonly IReadOnlyList<(string Name, int[] AnyOf)> _predicates;

    public Dc1CheckTracker(Dc1ClientChecks checks) =>
        _predicates = checks.Locations
            .Select(l => (l.Name, l.Predicate.AnyOf.ToArray()))
            .ToList();

    /// <summary>Location names whose predicate holds in <paramref name="group7Bank"/>
    /// (little-endian dwords; bit n = dword n>>5, bit n&amp;31 — SetFlag addressing, cont.5).</summary>
    public IReadOnlyList<string> Checked(ReadOnlySpan<byte> group7Bank)
    {
        var result = new List<string>();
        foreach (var (name, anyOf) in _predicates)
        {
            foreach (int f in anyOf)
            {
                if (IsBitSet(group7Bank, f))
                {
                    result.Add(name);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>Flag-bank bit test matching the engine's addressing (byte f>>3 works for
    /// little-endian dword banks: bit f&amp;31 of dword f>>5 == bit f&amp;7 of byte f>>3).</summary>
    public static bool IsBitSet(ReadOnlySpan<byte> bank, int flag) =>
        flag >= 0 && (flag >> 3) < bank.Length && (bank[flag >> 3] & (1 << (flag & 7))) != 0;
}
