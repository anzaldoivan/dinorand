using System.Buffers.Binary;
using static DinoRand.FileFormats.Exe.ExePatcher;

namespace DinoRand.FileFormats.Exe;

internal static class Dc1TransitionExePatch
{
    private const int DoorSkipHookLength = 14;

    private static readonly byte[] DoorSkipHookPristine =
        { 0xC7, 0x45, 0xF0, 0x00, 0x00, 0x80, 0x1F, 0x81, 0x7D, 0xF0, 0x00, 0x00, 0x80, 0x1F };

    private static byte[] DoorSkipHookPatched()
    {
        var hook = new byte[DoorSkipHookLength];
        hook[0] = 0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(hook.AsSpan(1, 4),
            unchecked((int)(DoorSkipCaveVa - (DoorSkipHookVa + 5))));
        hook.AsSpan(5).Fill(0x90);
        return hook;
    }

    private static byte[] DoorSkipCave()
    {
        var cave = new byte[]
        {
            0x66, 0x83, 0x3D, 0x40, 0x3C, 0x6D, 0x00, 0x00, // cmp word [0x6D3C40],0
            0x75, 0x13,                                     // jne new-edge path
            0xC7, 0x45, 0xF0, 0x00, 0x00, 0x80, 0x1F,       // displaced mov
            0x81, 0x7D, 0xF0, 0x00, 0x00, 0x80, 0x1F,       // displaced cmp
            0xE9, 0x00, 0x00, 0x00, 0x00,                   // resume at 0x471130
            0x8B, 0x45, 0x08,                               // mov eax,[ebp+8]
            0x66, 0xC7, 0x40, 0x02, 0x01, 0x00,             // mov word [eax+2],1
            0xC6, 0x40, 0x0F, 0x3D,                         // mov byte [eax+0xf],0x3d
            0xE9, 0x00, 0x00, 0x00, 0x00,                   // jump to 0x47149a
        };
        BinaryPrimitives.WriteUInt32LittleEndian(cave.AsSpan(3, 4), DoorSkipMappedEdgeVa);
        BinaryPrimitives.WriteInt32LittleEndian(cave.AsSpan(25, 4),
            unchecked((int)(0x00471130u - (DoorSkipCaveVa + 29))));
        BinaryPrimitives.WriteInt32LittleEndian(cave.AsSpan(43, 4),
            unchecked((int)(0x0047149Au - (DoorSkipCaveVa + 47))));
        return cave;
    }

    internal static bool IsDoorSkipApplied(ReadOnlySpan<byte> exe)
        => Slice(exe, VaToFileOffset(DoorSkipHookVa), DoorSkipHookLength).SequenceEqual(DoorSkipHookPatched())
        && Slice(exe, VaToFileOffset(DoorSkipCaveVa), DoorSkipCave().Length).SequenceEqual(DoorSkipCave());

    internal static void ApplyDoorSkip(Span<byte> exe)
    {
        var hook = Slice(exe, VaToFileOffset(DoorSkipHookVa), DoorSkipHookLength);
        var patchedHook = DoorSkipHookPatched();
        bool pristine = hook.SequenceEqual(DoorSkipHookPristine);
        bool patched = hook.SequenceEqual(patchedHook);
        if (!pristine && !patched)
            throw new InvalidOperationException(
                $"door-skip hook @0x{DoorSkipHookVa:X} is neither pristine nor already patched; refusing to overwrite an unexpected build.");

        var cave = DoorSkipCave();
        uint caveEnd = DoorSkipCaveVa + (uint)cave.Length;
        if (!IsFileBacked(DoorSkipCaveVa) || caveEnd > TextRawEndVa || caveEnd > CutsceneFfCaveVa)
            throw new ArgumentOutOfRangeException(nameof(DoorSkipCaveVa),
                $"door-skip cave [0x{DoorSkipCaveVa:X}, 0x{caveEnd:X}) must fit the verified .text slack before the cutscene cave.");

        int caveOff = VaToFileOffset(DoorSkipCaveVa);
        var caveTarget = Slice(exe, caveOff, cave.Length);
        for (int i = 0; i < cave.Length; i++)
            if (caveTarget[i] != 0 && caveTarget[i] != cave[i])
                throw new InvalidOperationException(
                    $"door-skip cave at 0x{DoorSkipCaveVa:X} byte 0x{i:X} = 0x{caveTarget[i]:X2} is neither zero-slack nor the intended cave byte; refusing.");

        cave.CopyTo(caveTarget);
        patchedHook.CopyTo(hook);
    }

    internal static bool IsCutsceneFastForwardApplied(ReadOnlySpan<byte> exe)
        => Slice(exe, VaToFileOffset(CutsceneFfHookVa), CutsceneFfHookPatched.Length).SequenceEqual(CutsceneFfHookPatched);

    internal static void ApplyCutsceneFastForward(Span<byte> exe)
    {
        var hook = Slice(exe, VaToFileOffset(CutsceneFfHookVa), CutsceneFfHookPristine.Length);
        bool pristine = hook.SequenceEqual(CutsceneFfHookPristine);
        bool patched = hook.SequenceEqual(CutsceneFfHookPatched);
        if (!pristine && !patched)
            throw new InvalidOperationException(
                $"cutscene fast-forward hook @0x{CutsceneFfHookVa:X} is neither pristine `call 0x46AA41` nor already patched; refusing to overwrite an unexpected build.");

        uint caveEnd = CutsceneFfCaveVa + (uint)CutsceneFfCave.Length;
        if (!IsFileBacked(CutsceneFfCaveVa) || caveEnd > TextRawEndVa)
            throw new ArgumentOutOfRangeException(nameof(CutsceneFfCaveVa),
                $"fast-forward cave [0x{CutsceneFfCaveVa:X}, 0x{caveEnd:X}) must lie in the .text raw-slack window (.., 0x{TextRawEndVa:X}).");

        int caveOff = VaToFileOffset(CutsceneFfCaveVa);
        for (int i = 0; i < CutsceneFfCave.Length; i++)
            if (exe[caveOff + i] != 0 && exe[caveOff + i] != CutsceneFfCave[i])
                throw new InvalidOperationException(
                    $"fast-forward cave at 0x{CutsceneFfCaveVa:X} byte 0x{i:X} = 0x{exe[caveOff + i]:X2} is neither zero-slack nor the intended cave byte; refusing (not a clean cave).");

        CutsceneFfCave.CopyTo(exe.Slice(caveOff, CutsceneFfCave.Length));
        CutsceneFfHookPatched.CopyTo(hook);
    }
}
