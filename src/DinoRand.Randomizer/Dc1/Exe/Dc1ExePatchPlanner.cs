using DinoRand.FileFormats.Exe;

namespace DinoRand.Randomizer.Dc1.Exe;

/// <summary>Owns DC1 seed selection; FileFormats remains responsible for validation and writes.</summary>
internal static class Dc1ExePatchPlanner
{
    private static readonly (byte Id, byte Max)[] StartingInventoryPool =
    {
        (0x16, 34), (0x11, 10), (0x10, 10), (0x12, 3), (0x13, 3), (0x14, 3), (0x18, 6), (0x19, 6),
        (0x1B, 3), (0x1C, 2), (0x1D, 2), (0x1E, 1), (0x1F, 1),
    };

    public static Dc1ExePatchPlan PlanBgmCatalog(ReadOnlySpan<byte> exe, int seed)
    {
        int n = ExePatcher.BgmRecordCount - (ExePatcher.BgmFirstShuffledId - 1);
        var flags = new uint[n];
        var sourceIds = Enumerable.Range(ExePatcher.BgmFirstShuffledId, n).ToArray();
        for (int k = 0; k < n; k++)
            flags[k] = ExePatcher.ReadUInt32AtVa(exe,
                ExePatcher.BgmRecordVa(ExePatcher.BgmFirstShuffledId + k) + 0x0C);

        uint rng = (uint)seed;
        foreach (uint cls in flags.Distinct().OrderBy(f => f))
        {
            int[] members = Enumerable.Range(0, n).Where(k => flags[k] == cls).ToArray();
            int[] permutation = Permutation(members.Length, ref rng);
            for (int i = 0; i < members.Length; i++)
                sourceIds[members[i]] = ExePatcher.BgmFirstShuffledId + members[permutation[i]];
        }
        return new Dc1ExePatchPlan { BgmSourceIds = sourceIds };
    }

    public static Dc1ExePatchPlan PlanEmergencyBoxShuffle(int seed)
    {
        uint rng = (uint)seed;
        var blocks = new int[ExePatcher.EmergencyBoxBlockVas.Length][];
        for (int b = 0; b < blocks.Length; b++)
            blocks[b] = Permutation(ExePatcher.EmergencyBoxesPerBlock, ref rng);
        return new Dc1ExePatchPlan { EmergencyBoxSourceSlots = blocks };
    }

    public static Dc1ExePatchPlan PlanEmergencyBoxReroll(ReadOnlySpan<byte> exe, int seed)
    {
        byte[][] records = ExePatcher.ReadEmergencyBoxRecords(exe);
        byte[][] planned = records.Select(record => (byte[])record.Clone()).ToArray();
        uint rng = (uint)seed;
        for (int b = 0; b < ExePatcher.EmergencyBoxBlockVas.Length; b++)
        {
            var weightedItems = new List<byte>();
            var amountsOf = new Dictionary<byte, List<byte>>();
            var slotCount = new int[ExePatcher.EmergencyBoxesPerBlock];
            for (int i = 0; i < ExePatcher.EmergencyBoxesPerBlock; i++)
            {
                byte[] record = records[b * ExePatcher.EmergencyBoxesPerBlock + i];
                for (int k = 0; k < 10; k++)
                {
                    byte id = record[1 + 2 * k];
                    if (id < ExePatcher.EmergencyBoxFirstItemId || id > ExePatcher.EmergencyBoxLastItemId) break;
                    byte amount = record[2 + 2 * k];
                    weightedItems.Add(id);
                    if (!amountsOf.TryGetValue(id, out var amounts)) amountsOf[id] = amounts = new List<byte>();
                    if (!amounts.Contains(amount)) amounts.Add(amount);
                    slotCount[i]++;
                }
            }
            if (weightedItems.Count == 0) continue;
            for (int i = 0; i < ExePatcher.EmergencyBoxesPerBlock; i++)
            {
                byte[] record = planned[b * ExePatcher.EmergencyBoxesPerBlock + i];
                for (int k = 0; k < slotCount[i]; k++)
                {
                    byte item = weightedItems[(int)(NextRand(ref rng) % (uint)weightedItems.Count)];
                    List<byte> amounts = amountsOf[item];
                    record[1 + 2 * k] = item;
                    record[2 + 2 * k] = amounts[(int)(NextRand(ref rng) % (uint)amounts.Count)];
                }
            }
        }
        return new Dc1ExePatchPlan { EmergencyBoxRecords = planned };
    }

    public static Dc1ExePatchPlan PlanStartingInventory(int seed)
    {
        uint rng = (uint)seed;
        var blocks = new (int Id, int Count)[ExePatcher.StartingInventoryBlocks.Length][];
        for (int b = 0; b < blocks.Length; b++)
        {
            blocks[b] = new (int, int)[ExePatcher.StartingInventoryBlocks[b].Slots.Length];
            for (int i = 0; i < blocks[b].Length; i++)
            {
                if (i == 0) blocks[b][i] = (ExePatcher.StartingInvHandgunAmmoId, 0x22);
                else
                {
                    var (id, max) = StartingInventoryPool[(int)(NextRand(ref rng) % (uint)StartingInventoryPool.Length)];
                    blocks[b][i] = (id, 1 + (int)(NextRand(ref rng) % max));
                }
            }
        }
        return new Dc1ExePatchPlan { StartingInventoryBlocks = blocks };
    }

    public static Dc1ExePatchPlan PlanManagementOfficeSafeCode(int seed)
    {
        unchecked
        {
            uint x = (uint)seed * 2654435761u + 0x9E3779B9u;
            var digits = new int[MgmtOfficeSafeCode.DigitCount];
            for (int i = 0; i < digits.Length; i++)
            {
                x ^= x >> 15;
                x *= 0x2C1B3C6Du;
                x ^= x >> 12;
                digits[i] = (int)(x % 10u);
            }
            return new Dc1ExePatchPlan { ManagementOfficeSafeCode = digits };
        }
    }

    private static int[] Permutation(int count, ref uint rng)
    {
        var permutation = Enumerable.Range(0, count).ToArray();
        for (int i = count - 1; i > 0; i--)
        {
            int j = (int)(NextRand(ref rng) % (uint)(i + 1));
            (permutation[i], permutation[j]) = (permutation[j], permutation[i]);
        }
        return permutation;
    }

    private static uint NextRand(ref uint state)
    {
        state += 0x9E3779B9u;
        uint z = state;
        z = (z ^ (z >> 16)) * 0x21F0AAADu;
        z = (z ^ (z >> 15)) * 0x735A2D97u;
        return z ^ (z >> 15);
    }
}
