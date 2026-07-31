using System.Security.Cryptography;

namespace DinoRand.FileFormats.Exe;

/// <summary>Fail-closed ST102 Blue-Raptor descriptor edit for the supported PC build.</summary>
public static class Dc1BlueRaptorDescriptorPatch
{
    public const uint DescriptorTableVa = 0x00653930;
    public const uint MaxHpVa = DescriptorTableVa + 0x0A;
    public const ushort ExpectedMaxHp = 850;
    public const ushort BlueMaxHp = 1500;

    /// <summary>Apply the one-word descriptor edit after validating the exact source image.</summary>
    public static void Apply(Span<byte> exe, string buildSha256, int buildLength)
    {
        if (exe.Length != buildLength)
            throw new InvalidOperationException(
                $"DC1 Blue descriptor patch refused: build length {exe.Length} != expected {buildLength}.");

        string actual = Convert.ToHexString(SHA256.HashData(exe));
        if (!string.Equals(actual, buildSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "DC1 Blue descriptor patch refused: DINO.exe build hash does not match the plan.");

        ushort current = ExePatcher.ReadUInt16AtVa(exe, MaxHpVa);
        if (current != ExpectedMaxHp)
            throw new InvalidOperationException(
                $"DC1 Blue descriptor patch refused: descriptor 0 +0x0A is {current}, " +
                $"expected {ExpectedMaxHp}.");

        ExePatcher.WriteUInt16(exe, ExePatcher.VaToFileOffset(MaxHpVa), BlueMaxHp);
    }
}
