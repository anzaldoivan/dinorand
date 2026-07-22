using System.Buffers.Binary;
using static DinoRand.FileFormats.Exe.ExePatcher;

namespace DinoRand.FileFormats.Exe;

internal static class Dc1InventoryExePatch
{
    internal static int StartingInventoryMaxCustomItems => StartingInventoryBlocks.Min(b => b.Slots.Length);

    internal static EmergencyBoxShuffleEntry[] ShuffleEmergencyBoxContents(Span<byte> exe, int seed)
    {
        uint rng = (uint)seed;
        var result = new List<EmergencyBoxShuffleEntry>(EmergencyBoxBlockVas.Length * EmergencyBoxesPerBlock);
        var scratch = new byte[EmergencyBoxesPerBlock * EmergencyBoxRecordStride];

        foreach (uint blockVa in EmergencyBoxBlockVas)
        {
            int blockOff = VaToFileOffset(blockVa);
            CheckBounds(exe.Length, blockOff, EmergencyBoxesPerBlock * EmergencyBoxRecordStride);

            // Validate the table shape: every record must lead with the 10-slot marker. A mismatch means
            // the offsets do not point at the box table (wrong build/locale) — refuse rather than corrupt.
            for (int i = 0; i < EmergencyBoxesPerBlock; i++)
                if (exe[blockOff + i * EmergencyBoxRecordStride] != EmergencyBoxSlotMarker)
                    throw new InvalidOperationException(
                        $"Emergency-box block 0x{blockVa:X} record {i} does not start with the 0x{EmergencyBoxSlotMarker:X2} " +
                        "slot marker — unexpected build/locale; refusing to shuffle.");

            // Snapshot the 17 records, then Fisher–Yates a permutation and write each destination from its
            // source snapshot (records are equal-size, so this is a pure reorder — multiset preserved).
            exe.Slice(blockOff, scratch.Length).CopyTo(scratch);
            var perm = new int[EmergencyBoxesPerBlock];
            for (int i = 0; i < EmergencyBoxesPerBlock; i++) perm[i] = i;
            for (int i = EmergencyBoxesPerBlock - 1; i > 0; i--)
            {
                int j = (int)(NextRand(ref rng) % (uint)(i + 1));
                (perm[i], perm[j]) = (perm[j], perm[i]);
            }
            for (int dst = 0; dst < EmergencyBoxesPerBlock; dst++)
            {
                int src = perm[dst];
                scratch.AsSpan(src * EmergencyBoxRecordStride, EmergencyBoxRecordStride)
                       .CopyTo(exe.Slice(blockOff + dst * EmergencyBoxRecordStride, EmergencyBoxRecordStride));
                result.Add(new EmergencyBoxShuffleEntry(blockVa, dst, src));
            }
        }
        return result.ToArray();
    }

    internal static EmergencyBoxShuffleEntry[] ApplyEmergencyBoxShufflePlan(
        Span<byte> exe, IReadOnlyList<int[]> sourceSlotsByBlock)
    {
        ArgumentNullException.ThrowIfNull(sourceSlotsByBlock);
        if (sourceSlotsByBlock.Count != EmergencyBoxBlockVas.Length)
            throw new ArgumentException($"box shuffle plan has {sourceSlotsByBlock.Count} blocks; expected {EmergencyBoxBlockVas.Length}.", nameof(sourceSlotsByBlock));
        var result = new List<EmergencyBoxShuffleEntry>(EmergencyBoxBlockVas.Length * EmergencyBoxesPerBlock);
        var scratch = new byte[EmergencyBoxesPerBlock * EmergencyBoxRecordStride];
        for (int b = 0; b < EmergencyBoxBlockVas.Length; b++)
        {
            uint blockVa = EmergencyBoxBlockVas[b];
            int blockOff = VaToFileOffset(blockVa);
            CheckBounds(exe.Length, blockOff, scratch.Length);
            for (int i = 0; i < EmergencyBoxesPerBlock; i++)
                if (exe[blockOff + i * EmergencyBoxRecordStride] != EmergencyBoxSlotMarker)
                    throw new InvalidOperationException(
                        $"Emergency-box block 0x{blockVa:X} record {i} does not start with the 0x{EmergencyBoxSlotMarker:X2} slot marker — unexpected build/locale; refusing to shuffle.");
            int[] permutation = sourceSlotsByBlock[b];
            if (permutation.Length != EmergencyBoxesPerBlock
                || !permutation.Order().SequenceEqual(Enumerable.Range(0, EmergencyBoxesPerBlock)))
                throw new ArgumentException($"box shuffle plan block {b} is not a {EmergencyBoxesPerBlock}-slot permutation.", nameof(sourceSlotsByBlock));
            exe.Slice(blockOff, scratch.Length).CopyTo(scratch);
            for (int dst = 0; dst < EmergencyBoxesPerBlock; dst++)
            {
                int src = permutation[dst];
                scratch.AsSpan(src * EmergencyBoxRecordStride, EmergencyBoxRecordStride)
                    .CopyTo(exe.Slice(blockOff + dst * EmergencyBoxRecordStride, EmergencyBoxRecordStride));
                result.Add(new EmergencyBoxShuffleEntry(blockVa, dst, src));
            }
        }
        return result.ToArray();
    }

    internal static EmergencyBoxShuffleEntry[] RerollEmergencyBoxContents(Span<byte> exe, int seed)
    {
        uint rng = (uint)seed;
        var result = new List<EmergencyBoxShuffleEntry>(EmergencyBoxBlockVas.Length * EmergencyBoxesPerBlock);

        foreach (uint blockVa in EmergencyBoxBlockVas)
        {
            int blockOff = VaToFileOffset(blockVa);
            CheckBounds(exe.Length, blockOff, EmergencyBoxesPerBlock * EmergencyBoxRecordStride);

            for (int i = 0; i < EmergencyBoxesPerBlock; i++)
                if (exe[blockOff + i * EmergencyBoxRecordStride] != EmergencyBoxSlotMarker)
                    throw new InvalidOperationException(
                        $"Emergency-box block 0x{blockVa:X} record {i} does not start with the 0x{EmergencyBoxSlotMarker:X2} " +
                        "slot marker — unexpected build/locale; refusing to reroll.");

            // Build this block's pool from its own records: weightedItems (one entry per occurrence) and the
            // set of amounts each item is seen with. Slot count of a record = its run of valid (id,count) pairs.
            var weightedItems = new List<byte>();
            var amountsOf = new Dictionary<byte, List<byte>>();
            var slotCount = new int[EmergencyBoxesPerBlock];
            for (int i = 0; i < EmergencyBoxesPerBlock; i++)
            {
                int rec = blockOff + i * EmergencyBoxRecordStride;
                int slots = 0;
                for (int k = 0; k < 10; k++)
                {
                    byte id = exe[rec + 1 + 2 * k];
                    if (id < EmergencyBoxFirstItemId || id > EmergencyBoxLastItemId) break;
                    byte amt = exe[rec + 2 + 2 * k];
                    weightedItems.Add(id);
                    if (!amountsOf.TryGetValue(id, out var list)) amountsOf[id] = list = new List<byte>();
                    if (!list.Contains(amt)) list.Add(amt);
                    slots++;
                }
                slotCount[i] = slots;
            }
            if (weightedItems.Count == 0) continue; // empty block (no loot to draw from)

            // Reroll each record's slots in place from the block pool (item, then amount for that item).
            for (int i = 0; i < EmergencyBoxesPerBlock; i++)
            {
                int rec = blockOff + i * EmergencyBoxRecordStride;
                for (int k = 0; k < slotCount[i]; k++)
                {
                    byte item = weightedItems[(int)(NextRand(ref rng) % (uint)weightedItems.Count)];
                    var amts = amountsOf[item];
                    byte amount = amts[(int)(NextRand(ref rng) % (uint)amts.Count)];
                    exe[rec + 1 + 2 * k] = item;
                    exe[rec + 2 + 2 * k] = amount;
                }
                result.Add(new EmergencyBoxShuffleEntry(blockVa, i, i));
            }
        }
        return result.ToArray();
    }

    internal static byte[][] ReadEmergencyBoxRecords(ReadOnlySpan<byte> exe)
    {
        var records = new List<byte[]>(EmergencyBoxBlockVas.Length * EmergencyBoxesPerBlock);
        foreach (uint blockVa in EmergencyBoxBlockVas)
        {
            int blockOff = VaToFileOffset(blockVa);
            CheckBounds(exe.Length, blockOff, EmergencyBoxesPerBlock * EmergencyBoxRecordStride);
            for (int i = 0; i < EmergencyBoxesPerBlock; i++)
            {
                int rec = blockOff + i * EmergencyBoxRecordStride;
                if (exe[rec] != EmergencyBoxSlotMarker)
                    throw new InvalidOperationException(
                        $"Emergency-box block 0x{blockVa:X} record {i} does not start with the 0x{EmergencyBoxSlotMarker:X2} slot marker — unexpected build/locale; refusing to reroll.");
                records.Add(exe.Slice(rec, EmergencyBoxRecordStride).ToArray());
            }
        }
        return records.ToArray();
    }

    internal static EmergencyBoxShuffleEntry[] ApplyEmergencyBoxRerollPlan(
        Span<byte> exe, IReadOnlyList<byte[]> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        int expected = EmergencyBoxBlockVas.Length * EmergencyBoxesPerBlock;
        if (records.Count != expected)
            throw new ArgumentException($"box reroll plan has {records.Count} records; expected {expected}.", nameof(records));
        var result = new List<EmergencyBoxShuffleEntry>(expected);
        int p = 0;
        foreach (uint blockVa in EmergencyBoxBlockVas)
        {
            int blockOff = VaToFileOffset(blockVa);
            CheckBounds(exe.Length, blockOff, EmergencyBoxesPerBlock * EmergencyBoxRecordStride);
            for (int i = 0; i < EmergencyBoxesPerBlock; i++, p++)
            {
                int rec = blockOff + i * EmergencyBoxRecordStride;
                if (exe[rec] != EmergencyBoxSlotMarker)
                    throw new InvalidOperationException(
                        $"Emergency-box block 0x{blockVa:X} record {i} does not start with the 0x{EmergencyBoxSlotMarker:X2} slot marker — unexpected build/locale; refusing to reroll.");
                byte[] planned = records[p];
                if (planned.Length != EmergencyBoxRecordStride || planned[0] != EmergencyBoxSlotMarker)
                    throw new ArgumentException($"box reroll plan record {p} has an invalid shape.", nameof(records));
                planned.CopyTo(exe.Slice(rec, EmergencyBoxRecordStride));
                result.Add(new EmergencyBoxShuffleEntry(blockVa, i, i));
            }
        }
        return result.ToArray();
    }

    internal static byte ReadUInt8AtVa(ReadOnlySpan<byte> exe, uint va) => Slice(exe, VaToFileOffset(va), 1)[0];

    internal static void WriteUInt8AtVa(Span<byte> exe, uint va, byte value) => Slice(exe, VaToFileOffset(va), 1)[0] = value;

    internal static void ValidateStartingInventory(ReadOnlySpan<byte> exe)
    {
        foreach (var block in StartingInventoryBlocks)
            foreach (var slot in block.Slots)
            {
                CheckSlotStore(exe, slot.IdImmVa, InventorySlotIdBaseDisp + (uint)slot.Slot * 4, block.Name, slot.Slot, "id");
                CheckSlotStore(exe, slot.QtyImmVa, InventorySlotIdBaseDisp + (uint)slot.Slot * 4 + 1, block.Name, slot.Slot, "qty");
                byte id = ReadUInt8AtVa(exe, slot.IdImmVa);
                if (id != 0 && (id < StartingInvFirstItemId || id > StartingInvLastItemId))
                    throw new InvalidOperationException(
                        $"Starting-inventory {block.Name} slot {slot.Slot} id immediate is 0x{id:X2} " +
                        "(not 0 or a valid item id) — unexpected build/locale; refusing to patch.");
            }
    }

    internal static StartingInvWrite[] RandomizeStartingInventory(Span<byte> exe, int seed)
    {
        ValidateStartingInventory(exe);
        uint rng = (uint)seed;
        var result = new List<StartingInvWrite>();
        foreach (var block in StartingInventoryBlocks)
            for (int i = 0; i < block.Slots.Length; i++)
            {
                var slot = block.Slots[i];
                byte id, count;
                if (i == 0)
                {
                    id = StartingInvHandgunAmmoId;
                    count = StartingInvHandgunAmmoFull;
                }
                else
                {
                    var (pid, pmax) = StartingInvRandomPool[(int)(NextRand(ref rng) % (uint)StartingInvRandomPool.Length)];
                    id = pid;
                    count = (byte)(1 + NextRand(ref rng) % pmax);
                }
                WriteUInt8AtVa(exe, slot.IdImmVa, id);
                WriteUInt8AtVa(exe, slot.QtyImmVa, count);
                result.Add(new StartingInvWrite(block.Name, slot.Slot, id, count));
            }
        return result.ToArray();
    }

    internal static StartingInvWrite[] ApplyStartingInventoryPlan(
        Span<byte> exe, IReadOnlyList<IReadOnlyList<(int Id, int Count)>> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ValidateStartingInventory(exe);
        if (blocks.Count != StartingInventoryBlocks.Length)
            throw new ArgumentException($"starting-inventory plan has {blocks.Count} blocks; expected {StartingInventoryBlocks.Length}.", nameof(blocks));
        var result = new List<StartingInvWrite>();
        for (int b = 0; b < StartingInventoryBlocks.Length; b++)
        {
            var block = StartingInventoryBlocks[b];
            var planned = blocks[b];
            if (planned.Count != block.Slots.Length)
                throw new ArgumentException($"starting-inventory plan block {b} has {planned.Count} slots; expected {block.Slots.Length}.", nameof(blocks));
            for (int i = 0; i < block.Slots.Length; i++)
            {
                var (id, count) = planned[i];
                if (id < StartingInvFirstItemId || id > StartingInvLastItemId || count is < 1 or > 0xFF)
                    throw new ArgumentException($"starting-inventory plan block {b} slot {i} is outside the item/count range.", nameof(blocks));
                var slot = block.Slots[i];
                WriteUInt8AtVa(exe, slot.IdImmVa, (byte)id);
                WriteUInt8AtVa(exe, slot.QtyImmVa, (byte)count);
                result.Add(new StartingInvWrite(block.Name, slot.Slot, (byte)id, (byte)count));
            }
        }
        return result.ToArray();
    }

    internal static StartingInvWrite[] SetStartingInventory(Span<byte> exe, IReadOnlyList<(int Id, int Count)> items)
    {
        if (items is null || items.Count == 0)
            throw new ArgumentException("custom starting inventory is empty.", nameof(items));
        if (items.Count > StartingInventoryMaxCustomItems)
            throw new ArgumentException(
                $"custom starting inventory has {items.Count} items but at most {StartingInventoryMaxCustomItems} " +
                "fit every difficulty block.", nameof(items));
        for (int i = 0; i < items.Count; i++)
        {
            var (id, count) = items[i];
            if (id < StartingInvFirstItemId || id > StartingInvLastItemId)
                throw new ArgumentException(
                    $"item {i} id 0x{id:X} is out of range 0x{StartingInvFirstItemId:X2}..0x{StartingInvLastItemId:X2}.", nameof(items));
            if (count is < 1 or > 0xFF)
                throw new ArgumentException($"item {i} count {count} is out of range 1..255.", nameof(items));
        }
        ValidateStartingInventory(exe);

        var result = new List<StartingInvWrite>();
        foreach (var block in StartingInventoryBlocks)
            for (int i = 0; i < block.Slots.Length; i++)
            {
                var slot = block.Slots[i];
                byte id = i < items.Count ? (byte)items[i].Id : (byte)0;
                byte count = i < items.Count ? (byte)items[i].Count : (byte)0;
                WriteUInt8AtVa(exe, slot.IdImmVa, id);
                WriteUInt8AtVa(exe, slot.QtyImmVa, count);
                result.Add(new StartingInvWrite(block.Name, slot.Slot, id, count));
            }
        return result.ToArray();
    }

    internal static void ValidateStartingWeaponGrants(ReadOnlySpan<byte> exe)
    {
        foreach (var block in StartingWeaponGrantBlocks)
            foreach (var site in block.Sites)
            {
                if (ReadUInt8AtVa(exe, site.ValImmVa - 1) != PushImm8Opcode
                    || ReadUInt8AtVa(exe, site.IdxImmVa - 1) != PushImm8Opcode
                    || ReadUInt8AtVa(exe, site.ValImmVa) != 1)
                    throw new InvalidOperationException(
                        $"Starting-weapon {block.Name} grant at VA 0x{site.ValImmVa - 1:X} is not the expected " +
                        "`push 1; push idx` SetFlag(11,…) form — unexpected build/locale; refusing to patch.");
                byte idx = ReadUInt8AtVa(exe, site.IdxImmVa);
                if (idx < StartingWeaponFirstId || idx > StartingWeaponLastId)
                    throw new InvalidOperationException(
                        $"Starting-weapon {block.Name} grant idx 0x{idx:X2} is not a weapon id " +
                        $"(0x{StartingWeaponFirstId:X2}..0x{StartingWeaponLastId:X2}) — unexpected build/locale; refusing to patch.");
            }
    }

    internal static (string Block, byte WeaponId)[] SetStartingWeapon(Span<byte> exe, int? weaponId)
    {
        // A weaponless start ("None") is NOT supported yet: clearing the group-11 owned-flag is not enough —
        // the engine re-equips a default Handgun through an as-yet-undecoded equipped-weapon path (confirmed
        // in-game: None still starts Regina with the pistol). So null would silently leave her armed; reject
        // it rather than lie. Needs a runtime (CE) decode of the equipped-weapon source. docs/reference/dc1/items/STARTING-INVENTORY.md.
        if (weaponId is null)
            throw new ArgumentException(
                "a weaponless start ('None') is not supported yet — the engine re-equips a default Handgun via an " +
                "undecoded path, so it can't be reliably removed. Choose a weapon id (0x01..0x0A).", nameof(weaponId));
        if (weaponId is { } w && (w < StartingWeaponFirstId || w > StartingWeaponLastId))
            throw new ArgumentOutOfRangeException(nameof(weaponId), weaponId,
                $"starting weapon id must be 0x{StartingWeaponFirstId:X2}..0x{StartingWeaponLastId:X2}.");
        ValidateStartingWeaponGrants(exe);

        var result = new List<(string, byte)>();
        foreach (var block in StartingWeaponGrantBlocks)
        {
            byte granted = 0;
            for (int i = 0; i < block.Sites.Length; i++)
            {
                var site = block.Sites[i];
                if (i == 0 && weaponId is { } id)
                {
                    WriteUInt8AtVa(exe, site.IdxImmVa, (byte)id);
                    WriteUInt8AtVa(exe, site.ValImmVa, 1);
                    granted = (byte)id;
                }
                else
                {
                    WriteUInt8AtVa(exe, site.ValImmVa, 0); // disable this grant (SetFlag(11,idx,0) — clear)
                }
            }
            result.Add((block.Name, granted));
        }
        return result.ToArray();
    }
}
