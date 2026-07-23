using System.Buffers.Binary;
using static DinoRand.FileFormats.Exe.ExePatcher;

namespace DinoRand.FileFormats.Exe;

internal static class Dc1ItemPickupExePatch
{
    private const int HookLength = 5;

    private static readonly byte[] HookOriginal =
        { 0x55, 0x8B, 0xEC, 0x6A, 0x00 }; // push ebp; mov ebp,esp; push 0

    internal static byte[] BuildCave(uint caveVa)
    {
        // Preserve the displaced close-routine prologue and its first `push 0`, clear the
        // item-action latch, then resume at the original close body after the hook.
        var cave = new byte[]
        {
            0x55,                         // push ebp
            0x8B, 0xEC,                   // mov ebp, esp
            0x6A, 0x00,                   // original close: SetFlag(2, 0x0E, 0)'s value
            0x6A, 0x00,                   // SetFlag(1, 0x0B, 0): value
            0x6A, 0x0B,                   // index
            0x6A, 0x01,                   // group
            0xE8, 0x00, 0x00, 0x00, 0x00,// call SetFlag
            0x83, 0xC4, 0x0C,             // discard the clear call's three arguments
            0xE9, 0x00, 0x00, 0x00, 0x00,// return to 0x44ADEF
        };

        BinaryPrimitives.WriteInt32LittleEndian(
            cave.AsSpan(12, 4),
            unchecked((int)(ItemPickupPendingFlagSetterVa - (caveVa + 16))));
        BinaryPrimitives.WriteInt32LittleEndian(
            cave.AsSpan(20, 4),
            unchecked((int)((ItemPickupSessionCloseVa + HookLength) - (caveVa + (uint)cave.Length))));
        return cave;
    }

    internal static byte[] BuildHook(uint caveVa)
    {
        var hook = new byte[HookLength];
        hook[0] = 0xE9; // jmp rel32
        BinaryPrimitives.WriteInt32LittleEndian(
            hook.AsSpan(1, 4), unchecked((int)(caveVa - (ItemPickupSessionCloseVa + HookLength))));
        return hook;
    }

    internal static bool IsApplied(ReadOnlySpan<byte> exe)
    {
        var cave = BuildCave(ItemPickupCancelCaveVa);
        return Slice(exe, VaToFileOffset(ItemPickupSessionCloseVa), HookLength)
                   .SequenceEqual(BuildHook(ItemPickupCancelCaveVa))
            && Slice(exe, VaToFileOffset(ItemPickupCancelCaveVa), cave.Length)
                   .SequenceEqual(cave);
    }

    internal static byte[] Install(Span<byte> exe)
    {
        var cave = BuildCave(ItemPickupCancelCaveVa);
        uint caveEnd = ItemPickupCancelCaveVa + (uint)cave.Length;
        if (!IsFileBacked(ItemPickupSessionCloseVa)
            || !IsFileBacked(ItemPickupCancelCaveVa)
            || caveEnd > CutsceneFfCaveVa)
            throw new ArgumentOutOfRangeException(nameof(ItemPickupCancelCaveVa),
                $"item-pickup close cave [0x{ItemPickupCancelCaveVa:X}, 0x{caveEnd:X}) must lie in the .text raw-slack window.");

        int hookOff = VaToFileOffset(ItemPickupSessionCloseVa);
        var hook = Slice(exe, hookOff, HookLength);
        var patchedHook = BuildHook(ItemPickupCancelCaveVa);
        bool pristine = hook.SequenceEqual(HookOriginal);
        bool patched = hook.SequenceEqual(patchedHook);
        if (!pristine && !patched)
            throw new InvalidOperationException(
                $"item-pickup close hook @0x{ItemPickupSessionCloseVa:X} is neither pristine nor already patched; " +
                "refusing to overwrite an unexpected build.");

        int caveOff = VaToFileOffset(ItemPickupCancelCaveVa);
        var caveTarget = Slice(exe, caveOff, cave.Length);
        for (int i = 0; i < cave.Length; i++)
            if (caveTarget[i] != 0 && caveTarget[i] != cave[i])
                throw new InvalidOperationException(
                    $"item-pickup close cave at 0x{ItemPickupCancelCaveVa:X} byte 0x{i:X} = " +
                    $"0x{caveTarget[i]:X2} is neither zero-slack nor the intended byte; refusing to overwrite.");

        byte[] original = hook.ToArray();
        cave.CopyTo(caveTarget);
        patchedHook.CopyTo(hook);
        return original;
    }
}
