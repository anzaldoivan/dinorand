namespace DinoRand.ApClient;

/// <summary>One received AP item, resolved to its DC1 game item id.</summary>
/// <param name="Index">Position in the server's ReceivedItems list (0-based, stable per seed).</param>
/// <param name="GameItemId">DC1 item id (slot_data item_ids mapping).</param>
/// <param name="FromOwnSlot">True when this item was found in OUR world — the engine already
/// granted it at pickup (AddItem/SetFlag ran with the patched id), so consumables must not be
/// applied again.</param>
public readonly record struct ReceivedGameItem(int Index, int GameItemId, bool FromOwnSlot);

/// <summary>A planned process-memory write.</summary>
public readonly record struct MemWrite(uint Va, byte[] Bytes);

/// <summary>Memory state the planner reconciles against (read under the gameplay gate).</summary>
public sealed record Dc1InventorySnapshot(byte[] Group11Bank, byte[] SupplyArray, byte SupplyCapacity);

/// <summary>
/// Idempotent grant planner (AP-CLIENT-PLAN.md §3): reconciles the full server item list against
/// current memory state and emits the minimal writes.
///
/// <list type="bullet">
///   <item>Key items / weapons / anything outside the consumable band [0x10,0x24): a state
///   ASSERT — set the group-11 ownership bit if clear (never replayed increments, so re-running
///   the whole list every poll is safe, and a save-reload keeps server-granted keys).</item>
///   <item>Consumables: applied exactly once per ReceivedItems index (positions ≤
///   <paramref name="appliedThrough"/> are done), and only for items from OTHER slots — own-world
///   pickups were granted by the engine itself. Supply write mirrors AddItem 0x445048 exactly
///   (re-disassembled, cont.81): a slot is FREE when its qty byte is 0 (not its id); stacking
///   needs id AND class to match and caps at the per-id max from the property table
///   0x64EFC0+id*12+1; full → the item stays queued (appliedThrough does not advance past it)
///   and is retried next poll.</item>
/// </list>
/// </summary>
public static class Dc1GrantPlanner
{
    /// <summary>Fallback per-slot stack cap when the property table is unreadable.</summary>
    private const byte DefaultMaxStack = 99;

    public static (IReadOnlyList<MemWrite> Writes, int AppliedThrough) Plan(
        IReadOnlyList<ReceivedGameItem> items, int appliedThrough, Dc1InventorySnapshot mem,
        Func<int, byte?>? maxStackOf = null)
    {
        var writes = new List<MemWrite>();

        // --- ownership asserts (all items, every reconcile) ---
        byte[] bank = (byte[])mem.Group11Bank.Clone();
        foreach (var item in items)
        {
            int id = item.GameItemId;
            if (id is >= Dc1Symbols.ConsumableIdMin and < Dc1Symbols.ConsumableIdEnd) continue;
            if (id is <= 0 or > 255) continue;
            if (!Dc1CheckTracker.IsBitSet(bank, id))
            {
                bank[id >> 3] |= (byte)(1 << (id & 7));
                // one-byte write per newly-set bit — minimal collision surface with the engine
                writes.Add(new MemWrite(Dc1Symbols.Group11BankVa + (uint)(id >> 3),
                                        new[] { bank[id >> 3] }));
            }
        }

        // --- consumables, once per index, in order ---
        byte[] supply = (byte[])mem.SupplyArray.Clone();
        int slots = Math.Min((int)mem.SupplyCapacity, Dc1Symbols.SupplySlotCount);
        var touched = new HashSet<int>();
        int applied = appliedThrough;
        foreach (var item in items.OrderBy(i => i.Index))
        {
            if (item.Index <= appliedThrough) continue;
            int id = item.GameItemId;
            bool isConsumable = id is >= Dc1Symbols.ConsumableIdMin and < Dc1Symbols.ConsumableIdEnd;
            if (!isConsumable || item.FromOwnSlot)
            {
                applied = item.Index; // nothing (more) to apply for this index
                continue;
            }
            byte max = maxStackOf?.Invoke(id) ?? DefaultMaxStack;
            if (max == 0) max = DefaultMaxStack;
            int slot = FindSlot(supply, slots, id, Dc1Symbols.SupplyClassOf(id), max);
            if (slot < 0) break; // array full — stay queued, retry next poll (do not advance)
            if (supply[slot * 4 + 1] != 0)
            {
                supply[slot * 4 + 1] = (byte)Math.Min(supply[slot * 4 + 1] + 1, max);
            }
            else
            {
                supply[slot * 4] = (byte)id;
                supply[slot * 4 + 1] = 1;
                supply[slot * 4 + 2] = Dc1Symbols.SupplyClassOf(id);
                supply[slot * 4 + 3] = 0;
            }
            touched.Add(slot);
            applied = item.Index;
        }
        foreach (int slot in touched.OrderBy(s => s))
            writes.Add(new MemWrite(Dc1Symbols.SupplyArrayVa + (uint)(slot * 4),
                                    supply.AsSpan(slot * 4, 4).ToArray()));

        return (writes, applied);
    }

    /// <summary>AddItem's slot choice (0x445048, cont.81): a stackable slot = qty≠0 with matching
    /// id AND class and headroom below the per-id max; a free slot = qty==0. Stackable wins in
    /// scan order (the engine walks slots once, stacking before falling through to a free one);
    /// -1 when neither exists below capacity.</summary>
    private static int FindSlot(ReadOnlySpan<byte> supply, int slots, int id, byte cls, byte max)
    {
        int free = -1;
        for (int s = 0; s < slots && (s + 1) * 4 <= supply.Length; s++)
        {
            byte qty = supply[s * 4 + 1];
            if (qty != 0)
            {
                if (supply[s * 4] == id && supply[s * 4 + 2] == cls && qty < max) return s;
            }
            else if (free < 0)
            {
                free = s;
            }
        }
        return free;
    }
}
