using System.Buffers.Binary;
using static DinoRand.FileFormats.Exe.ExePatcher;

namespace DinoRand.FileFormats.Exe;

internal static class Dc1LaserFenceExePatch
{
    private const uint GetFlagVa = 0x0040764B;
    private const int LookupOffset = 66;

    private static readonly byte[] FenceEnableByState =
    {
        12, 240, 13, 241, 32, 243, 36, 244, 78, 245, 79, 246, 98, 247,
        99, 248, 112, 249, 177, 250, 173, 251, 147, 252, 149, 253,
    };

    internal static byte[] BuildCave(uint caveVa)
    {
        var cave = new byte[LookupOffset + FenceEnableByState.Length];
        byte[] code =
        {
            0x8B, 0x44, 0x24, 0x04,             // mov eax,[esp+4]       ; screen object
            0x0F, 0xB6, 0x40, 0x05,             // movzx eax,byte [eax+5]; fence state bit
            0xB9, 0x0D, 0x00, 0x00, 0x00,       // mov ecx,13
            0xBA, 0x00, 0x00, 0x00, 0x00,       // mov edx,lookup
            0x3A, 0x02,                         // scan: cmp al,[edx]
            0x74, 0x0A,                         // je found
            0x83, 0xC2, 0x02,                   // add edx,2
            0xE2, 0xF7,                         // loop scan
            0xE9, 0x18, 0x00, 0x00, 0x00,       // unknown state bit: vanilla active
            0x0F, 0xB6, 0x42, 0x01,             // found: movzx eax,byte [edx+1]
            0x50,                               // push enable bit
            0x6A, 0x00,                         // push group 0
            0xE8, 0x00, 0x00, 0x00, 0x00,       // call GetFlag
            0x83, 0xC4, 0x08,                   // discard arguments
            0x85, 0xC0,                         // test eax,eax
            0x75, 0x05,                         // enabled: vanilla active
            0xE9, 0x05, 0x00, 0x00, 0x00,       // disabled: native down
            0xE9, 0x00, 0x00, 0x00, 0x00,       // active tail-jump
            0xE9, 0x00, 0x00, 0x00, 0x00,       // down tail-jump
        };
        code.CopyTo(cave, 0);
        FenceEnableByState.CopyTo(cave, LookupOffset);

        BinaryPrimitives.WriteUInt32LittleEndian(
            cave.AsSpan(14, 4), caveVa + LookupOffset);
        BinaryPrimitives.WriteInt32LittleEndian(
            cave.AsSpan(40, 4), unchecked((int)(GetFlagVa - (caveVa + 44))));
        BinaryPrimitives.WriteInt32LittleEndian(
            cave.AsSpan(57, 4), unchecked((int)(LaserFenceControllerActiveVa - (caveVa + 61))));
        BinaryPrimitives.WriteInt32LittleEndian(
            cave.AsSpan(62, 4), unchecked((int)(LaserFenceControllerDownVa - (caveVa + LookupOffset))));
        return cave;
    }

    internal static bool IsApplied(ReadOnlySpan<byte> exe)
    {
        var cave = BuildCave(LaserFenceConditionalCaveVa);
        return ReadUInt32AtVa(exe, LaserFenceControllerPointerVa) == LaserFenceConditionalCaveVa
            && ReadUInt32AtVa(exe, LaserFenceControllerPointerVa + 4) == LaserFenceControllerDownVa
            && Slice(exe, VaToFileOffset(LaserFenceConditionalCaveVa), cave.Length).SequenceEqual(cave);
    }

    internal static void Apply(Span<byte> exe)
    {
        uint current = ReadUInt32AtVa(exe, LaserFenceControllerPointerVa);
        if (current != LaserFenceControllerActiveVa
            && current != LaserFenceControllerDownVa
            && current != LaserFenceConditionalCaveVa)
            throw new InvalidOperationException(
                $"laser-fence controller pointer @0x{LaserFenceControllerPointerVa:X} is 0x{current:X8}, " +
                "not the verified active, legacy-down, or conditional controller; refusing to overwrite an unexpected build.");

        uint down = ReadUInt32AtVa(exe, LaserFenceControllerPointerVa + 4);
        if (down != LaserFenceControllerDownVa)
            throw new InvalidOperationException(
                $"laser-fence down-state pointer @0x{LaserFenceControllerPointerVa + 4:X} is unexpected; " +
                "refusing to overwrite an unsupported build.");

        var cave = BuildCave(LaserFenceConditionalCaveVa);
        uint caveEnd = LaserFenceConditionalCaveVa + (uint)cave.Length;
        if (!IsFileBacked(LaserFenceConditionalCaveVa) || caveEnd > CutsceneFfCaveVa)
            throw new ArgumentOutOfRangeException(nameof(LaserFenceConditionalCaveVa),
                $"laser-fence cave [0x{LaserFenceConditionalCaveVa:X}, 0x{caveEnd:X}) must lie in the verified .text slack.");

        var target = Slice(exe, VaToFileOffset(LaserFenceConditionalCaveVa), cave.Length);
        for (int i = 0; i < cave.Length; i++)
            if (target[i] != 0 && target[i] != cave[i])
                throw new InvalidOperationException(
                    $"laser-fence cave at 0x{LaserFenceConditionalCaveVa:X} is neither zero-slack nor the intended cave; " +
                    "refusing to overwrite.");

        cave.CopyTo(target);
        WriteUInt32AtVa(exe, LaserFenceControllerPointerVa, LaserFenceConditionalCaveVa);
    }
}
