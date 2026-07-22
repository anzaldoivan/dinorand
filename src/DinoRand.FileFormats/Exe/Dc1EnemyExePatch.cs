using System.Buffers.Binary;
using static DinoRand.FileFormats.Exe.ExePatcher;

namespace DinoRand.FileFormats.Exe;

internal static class Dc1EnemyExePatch
{
    internal static uint RecordVa(int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index), index, "record index must be ≥ 0.");
        return RecordTableBaseVa + (uint)index * RecordStride;
    }

    internal static uint SetupFnFieldVa(int index) => RecordVa(index) + SetupFnFieldOffset;

    internal static uint ReadSetupFn(ReadOnlySpan<byte> exe, int index)
        => ReadUInt32AtVa(exe, SetupFnFieldVa(index));

    internal static uint RepointSetupFn(Span<byte> exe, int index, uint donorFnVa)
    {
        if (!IsFileBacked(donorFnVa))
            throw new ArgumentOutOfRangeException(nameof(donorFnVa),
                $"donor setup-fn VA 0x{donorFnVa:X} is not a file-backed code address.");
        int off = VaToFileOffset(SetupFnFieldVa(index));
        uint previous = ReadUInt32(exe, off);
        WriteUInt32(exe, off, donorFnVa);
        return previous;
    }

    internal static uint SetRecordCategoryHandler(Span<byte> exe, uint recordVa, int category, uint handlerVa)
    {
        if (category is < 0 or > 31)
            throw new ArgumentOutOfRangeException(nameof(category), category, "category must be 0..31.");
        if (!IsFileBacked(handlerVa))
            throw new ArgumentOutOfRangeException(nameof(handlerVa),
                $"handler VA 0x{handlerVa:X} is not a file-backed code address.");
        int off = VaToFileOffset(recordVa + (uint)category * 4);
        uint previous = ReadUInt32(exe, off);
        WriteUInt32(exe, off, handlerVa);
        return previous;
    }

    internal static uint[] RedirectCat8HitReaction(
        Span<byte> exe, ReadOnlySpan<byte> stream, uint caveVa = HitDescriptorCaveVa)
    {
        if (stream.Length == 0)
            throw new ArgumentException("reaction stream is empty.", nameof(stream));
        uint caveEnd = caveVa + (uint)stream.Length;
        if (caveVa < ImageBase || !IsFileBacked(caveVa) || caveEnd > TextRawEndVa)
            throw new ArgumentOutOfRangeException(nameof(caveVa),
                $"cave [0x{caveVa:X}, 0x{caveEnd:X}) must lie in the .text raw-slack window [{ImageBase:X}.., 0x{TextRawEndVa:X}).");
        if (caveVa >= 0x80000000)
            throw new ArgumentOutOfRangeException(nameof(caveVa), "cave VA must be < 0x80000000 (non-file-form).");

        int caveOff = VaToFileOffset(caveVa);
        // Clean-cave / idempotent guard: each target byte must be zero-slack or already the stream byte we write.
        for (int i = 0; i < stream.Length; i++)
            if (exe[caveOff + i] != 0 && exe[caveOff + i] != stream[i])
                throw new InvalidOperationException(
                    $"cave at VA 0x{caveVa:X} byte 0x{i:X} = 0x{exe[caveOff + i]:X2} is neither zero-slack nor the " +
                    $"intended stream byte 0x{stream[i]:X2}; refusing to overwrite (not a clean cave).");
        stream.CopyTo(exe.Slice(caveOff, stream.Length));

        // Repoint every file-form descriptor entry across all 9 tables; leave code-handler entries untouched.
        var previous = new List<uint>();
        for (uint va = Cat8ReactionTableLoVa; va < Cat8ReactionTableHiVa; va += 4)
        {
            int eoff = VaToFileOffset(va);
            uint cur = ReadUInt32(exe, eoff);
            if (!IsRdtFileFormPtr(cur) && cur != caveVa) continue; // skip code handlers / already-caved
            previous.Add(cur);
            WriteUInt32(exe, eoff, caveVa);
        }
        return previous.ToArray();
    }

    internal static byte[] InstallWalkerNullGuard(Span<byte> exe)
    {
        uint caveEnd = WalkerCaveVa + (uint)NullGuardedWalker.Length;
        if (!IsFileBacked(WalkerCaveVa) || caveEnd > TextRawEndVa)
            throw new ArgumentOutOfRangeException(nameof(WalkerCaveVa),
                $"walker cave [0x{WalkerCaveVa:X}, 0x{caveEnd:X}) must lie in the .text raw-slack window (.., 0x{TextRawEndVa:X}).");

        int caveOff = VaToFileOffset(WalkerCaveVa);
        for (int i = 0; i < NullGuardedWalker.Length; i++)
            if (exe[caveOff + i] != 0 && exe[caveOff + i] != NullGuardedWalker[i])
                throw new InvalidOperationException(
                    $"walker cave at 0x{WalkerCaveVa:X} byte 0x{i:X} = 0x{exe[caveOff + i]:X2} is neither zero-slack " +
                    $"nor the intended byte 0x{NullGuardedWalker[i]:X2}; refusing to overwrite.");
        NullGuardedWalker.CopyTo(exe.Slice(caveOff, NullGuardedWalker.Length));

        // jmp rel32 from WalkerVa to the cave.
        int jmpOff = VaToFileOffset(WalkerVa);
        var original = exe.Slice(jmpOff, 5).ToArray();
        int rel = (int)WalkerCaveVa - ((int)WalkerVa + 5);
        exe[jmpOff] = 0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(exe.Slice(jmpOff + 1, 4), rel);
        return original;
    }

    internal static byte[] InstallRenderModelGuard(Span<byte> exe)
    {
        byte[] cave = BuildRenderModelGuardCave(RenderGuardCaveVa);
        uint caveEnd = RenderGuardCaveVa + (uint)cave.Length;
        // The render-guard cave must sit in the .text raw-slack window and not run into the descriptor cave.
        if (!IsFileBacked(RenderGuardCaveVa) || caveEnd > HitDescriptorCaveVa)
            throw new ArgumentOutOfRangeException(nameof(RenderGuardCaveVa),
                $"render-guard cave [0x{RenderGuardCaveVa:X}, 0x{caveEnd:X}) must lie in .text slack before the " +
                $"descriptor cave (0x{HitDescriptorCaveVa:X}).");

        int caveOff = VaToFileOffset(RenderGuardCaveVa);
        for (int i = 0; i < cave.Length; i++)
            if (exe[caveOff + i] != 0 && exe[caveOff + i] != cave[i])
                throw new InvalidOperationException(
                    $"render-guard cave at 0x{RenderGuardCaveVa:X} byte 0x{i:X} = 0x{exe[caveOff + i]:X2} is neither " +
                    $"zero-slack nor the intended byte 0x{cave[i]:X2}; refusing to overwrite (revert a prior/narrow " +
                    "guard or restore the pristine exe first).");
        cave.CopyTo(exe.Slice(caveOff, cave.Length));

        // Hook: jmp rel32 to the cave + 7 nops (fills the 12-byte original instruction block).
        int hookOff = VaToFileOffset(RenderTransformHookVa);
        var original = exe.Slice(hookOff, RenderGuardOriginalHook.Length).ToArray();
        int rel = (int)RenderGuardCaveVa - ((int)RenderTransformHookVa + 5);
        exe[hookOff] = 0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(exe.Slice(hookOff + 1, 4), rel);
        for (int i = 5; i < RenderGuardOriginalHook.Length; i++) exe[hookOff + i] = 0x90;
        return original;
    }

    internal static uint[] RedirectCat8HitDescriptors(
        Span<byte> exe, ReadOnlySpan<byte> records, uint caveVa = HitDescriptorCaveVa)
    {
        int expected = HitDescriptorTotalRecords * HitDescriptorRecordSize;
        if (records.Length != expected)
            throw new ArgumentException(
                $"expected {expected} bytes ({HitDescriptorTotalRecords} × 0x{HitDescriptorRecordSize:X} records), got {records.Length}.",
                nameof(records));
        uint caveEnd = caveVa + (uint)expected;
        if (caveVa < ImageBase || !IsFileBacked(caveVa) || caveEnd > TextRawEndVa)
            throw new ArgumentOutOfRangeException(nameof(caveVa),
                $"cave [0x{caveVa:X}, 0x{caveEnd:X}) must lie in the .text raw-slack window [{ImageBase:X}.., 0x{TextRawEndVa:X}).");
        // A non-file-form sentinel is required so the engine's file-form relocations leave it verbatim.
        if (caveVa >= 0x80000000)
            throw new ArgumentOutOfRangeException(nameof(caveVa), "cave VA must be < 0x80000000 (non-file-form).");
        // Safety: never write over real code — each cave byte must be zero-slack, or already equal the
        // record byte we are about to write (so re-applying the patch on a live exe is idempotent; real
        // code can't coincidentally equal all 200 record bytes).
        int caveOff = VaToFileOffset(caveVa);
        for (int i = 0; i < expected; i++)
            if (exe[caveOff + i] != 0 && exe[caveOff + i] != records[i])
                throw new InvalidOperationException(
                    $"cave at VA 0x{caveVa:X} byte 0x{i:X} = 0x{exe[caveOff + i]:X2} is neither zero-slack nor the " +
                    $"intended record byte 0x{records[i]:X2}; refusing to overwrite (not a clean cave).");

        // Lay the records into the cave.
        records.CopyTo(exe.Slice(caveOff, expected));

        // Repoint the 10 table dwords: table17[0..4] then table15[0..4], matching the records order.
        var previous = new uint[HitDescriptorTotalRecords];
        int r = 0;
        foreach (uint tableVa in new[] { Cat8HitTable17Va, Cat8HitTable15Va })
            for (int i = 0; i < HitDescriptorIndexCount; i++, r++)
            {
                uint entryVa = tableVa + (uint)i * 4;
                uint recordVa = caveVa + (uint)r * (uint)HitDescriptorRecordSize;
                int eoff = VaToFileOffset(entryVa);
                previous[r] = ReadUInt32(exe, eoff);
                WriteUInt32(exe, eoff, recordVa);
            }
        return previous;
    }
}
