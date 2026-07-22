using System.Buffers.Binary;
using static DinoRand.FileFormats.Exe.ExePatcher;

namespace DinoRand.FileFormats.Exe;

internal static class Dc1TransitionExePatch
{
    internal static bool IsDoorSkipApplied(ReadOnlySpan<byte> exe)
        => Slice(exe, VaToFileOffset(DoorSkipSwingVa), DoorSkipSwingCode.Length).SequenceEqual(DoorSkipSwingCode);

    internal static void ApplyDoorSkip(Span<byte> exe)
    {
        var swing = Slice(exe, VaToFileOffset(DoorSkipSwingVa), DoorSkipSwingCode.Length);
        bool pristine = swing.SequenceEqual(DoorSkipSwingPristine);
        bool patched = swing.SequenceEqual(DoorSkipSwingCode);
        if (!pristine && !patched)
            throw new InvalidOperationException(
                $"door-skip window @0x{DoorSkipSwingVa:X} is neither pristine nor already patched; refusing to overwrite an unexpected build.");

        int gate = VaToFileOffset(DoorHoldGateVa);
        if (!Slice(exe, gate, DoorHoldGateSig.Length).SequenceEqual(DoorHoldGateSig))
            throw new InvalidOperationException(
                $"door-skip hold gate @0x{DoorHoldGateVa:X} guard mismatch (expected `cmp edx,imm8`); refusing.");

        DoorSkipSwingCode.CopyTo(swing);   // A: skip the leaf-sweep
        exe[gate + 2] = DoorHoldPatched;   // B: shorten the 60-frame hold
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
