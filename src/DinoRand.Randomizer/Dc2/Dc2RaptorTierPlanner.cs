namespace DinoRand.Randomizer.Dc2;

/// <summary>How raptor colour relates to raptor stats per room (RAPTOR-TIER-RE.md §4b: the room
/// renders ONE skin, selected by desc+4 raw; stats come from the pair table).</summary>
public enum Dc2RaptorColourMode
{
    /// <summary>One weighted variant per room — colour AND stats identical (identity pair table).</summary>
    RoomTier = 0,
    /// <summary>Room colour = the strongest tier present; other raptors may be weaker (pair =
    /// {colour tier, weaker weighted draw}). Colour never under-warns (at stage idx 0).</summary>
    MixedTiers = 1,
}

/// <summary>A room's planned raptor-tier edits: word writes (static op-0x1a VARIANT operands) and
/// byte writes (wave descriptor <c>desc+4</c>), applied via <c>Dc2SpawnEditor.ApplyEdits</c>.</summary>
public sealed record Dc2RaptorTierRoomPlan(
    IReadOnlyList<(int ValueOff, short Variant)> WordEdits,
    IReadOnlyList<(int Offset, byte Value)> ByteEdits)
{
    public bool IsEmpty => WordEdits.Count == 0 && ByteEdits.Count == 0;
}

/// <summary>
/// Pure, RNG-injected raptor-tier planner (docs/reference/dc2/enemies/RAPTOR-TIER-RE.md §4). The tier is the
/// variant nibble consumed by the E00 init (skin + HP together):
/// <list type="bullet">
/// <item>STATIC op-0x1a raptor spawns — the block+0x08 VARIANT literal is the tier directly; each
/// eligible spawn gets an independent weighted draw.</item>
/// <item>WAVE rooms — <c>desc+4</c> only offsets the EXE pair-table index
/// (<c>idx = desc4 + 3*[G+0x1388]</c>, clamp 9), so per-room bytes are jitter; the exact weighted
/// pool lives in the pair-table exe patch (<see cref="PlanPairTable"/> → <c>Dc2RaptorPatch</c>).</item>
/// </list>
/// The caller filters edit targets to spawns/descriptors whose CURRENT type is still the raptor
/// (0x02) — after a cross-species pass the raptor may be gone, and the variant operand then belongs
/// to another category's .TEX clamp.
/// </summary>
public static class Dc2RaptorTierPlanner
{
    public const int RaptorType = 0x02;

    /// <summary>Wave pair-table geometry (EXE table 0x703E9C): 10 entries × 4 bytes, bytes 0–1 used.</summary>
    public const int PairTableEntries = 10;

    /// <summary>One weighted draw over the tier pool; null when every weight is 0.
    /// Cumulative draw, single <c>rng.Next</c> — same stream discipline as <see cref="Dc2DonorPicker"/>.</summary>
    public static int? PickVariant(IReadOnlyDictionary<int, byte> weights, Random rng)
    {
        var pool = weights.Where(kv => kv.Value > 0 && kv.Key >= 0 && kv.Key <= Dc2RaptorTierTable.MaxVariant)
                          .OrderBy(kv => kv.Key).ToList(); // deterministic order regardless of dict impl
        if (pool.Count == 0) return null;
        int roll = rng.Next(pool.Sum(kv => kv.Value));
        foreach (var (variant, weight) in pool)
        {
            roll -= weight;
            if (roll < 0) return variant;
        }
        return pool[^1].Key; // unreachable
    }

    /// <summary>Plan one room: ONE weighted variant draw for the whole room (the room renders a
    /// single skin — RAPTOR-TIER-RE.md §4b — so per-spawn draws would desync colour from stats).
    /// The variant is written to every wave <c>desc+4</c> byte (the room-colour knob AND the
    /// pair-table index) and into every static VARIANT literal's tier nibble, PRESERVING the flag
    /// bits (0x10 = store-for-wave-re-arm on stock ST102 spawns).</summary>
    public static Dc2RaptorTierRoomPlan PlanRoom(
        IReadOnlyList<(int ValueOff, short Current)> staticVariants,
        IReadOnlyList<int> waveVariantOffs,
        Random rng,
        IReadOnlyDictionary<int, byte> weights)
    {
        if (PickVariant(weights, rng) is not int v)
            return new Dc2RaptorTierRoomPlan(Array.Empty<(int, short)>(), Array.Empty<(int, byte)>());
        var words = staticVariants
            .Select(s => (s.ValueOff, (short)((s.Current & ~0xF) | v))).ToList();
        var bytes = waveVariantOffs.Select(off => (off, (byte)v)).ToList();
        return new Dc2RaptorTierRoomPlan(words, bytes);
    }

    /// <summary>Pair-table plan for the exe patch. The table is indexed by
    /// <c>desc4 + 3*[G+0x1388]</c>, and <c>desc4</c> is ALSO the room's loaded colour, so:
    /// <see cref="Dc2RaptorColourMode.RoomTier"/> ⇒ identity pairs (stats == the colour that
    /// selected them; no RNG consumed, weight-independent);
    /// <see cref="Dc2RaptorColourMode.MixedTiers"/> ⇒ <c>{colour, weaker weighted draw}</c> —
    /// the spawner's coin flip mixes the colour tier with weaker ones, so the room colour is
    /// always the strongest tier present (at stage idx 0; later stages escalate vanilla-style).
    /// Never null — sparse/zero weights fall back to identity per entry.</summary>
    public static byte[] PlanPairTable(Random rng, IReadOnlyDictionary<int, byte> weights,
                                       Dc2RaptorColourMode mode, Dc2RaptorTierTable tiers)
    {
        var hpOf = tiers.Rows.ToDictionary(r => r.Variant, r => r.HpBase);
        var pairs = new byte[PairTableEntries * 2];
        for (int i = 0; i < PairTableEntries; i++)
        {
            byte colour = (byte)Math.Min(i, Dc2RaptorTierTable.MaxVariant);
            byte other = colour;
            if (mode == Dc2RaptorColourMode.MixedTiers)
            {
                // Weighted draw among variants no stronger than the colour tier; empty pool
                // (weight-0 everywhere weaker) ⇒ keep the colour tier.
                var weaker = weights
                    .Where(kv => kv.Value > 0 && hpOf.TryGetValue(kv.Key, out var hp)
                              && hp <= hpOf[colour])
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                if (PickVariant(weaker, rng) is int w) other = (byte)w;
            }
            pairs[i * 2] = colour;
            pairs[i * 2 + 1] = other;
        }
        return pairs;
    }
}
