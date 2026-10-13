using System.Security.Cryptography;

namespace DinoRand.FileFormats.Exe;

/// <summary>Fail-closed ST102 Blue-Raptor descriptor edit for the supported PC build.</summary>
public static class Dc1BlueRaptorDescriptorPatch
{
    public const uint DescriptorTableVa = 0x00657180;
    public const uint MaxHpVa = DescriptorTableVa + 0x0A;
    public const int MaxHpFileOffset = 0x25718A;
    public const ushort ExpectedMaxHp = 850;
    public const ushort BlueMaxHp = 1500;
    public const int SupportedBuildLength = 0x28C000;
    public const string SupportedBuildSha256 =
        "7ACC3DC1A3F3398E08E5BDAD746BDBF98CA5507E9B9BA5FBA709031B498D3FCA";

    public static ReadOnlySpan<byte> ExpectedSourceBytes => new byte[] { 0x52, 0x03 };

    /// <summary>Apply the one-word descriptor edit after validating the exact source image.</summary>
    public static void Apply(Span<byte> exe)
    {
        if (exe.Length != SupportedBuildLength)
            throw new InvalidOperationException(
                $"DC1 Blue descriptor patch refused: build length {exe.Length} != expected {SupportedBuildLength}.");

        string actual = Convert.ToHexString(SHA256.HashData(exe));
        if (!string.Equals(actual, SupportedBuildSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "DC1 Blue descriptor patch refused: DINO.exe is not the pinned GOG build.");

        if (!exe.Slice(MaxHpFileOffset, ExpectedSourceBytes.Length).SequenceEqual(ExpectedSourceBytes))
            throw new InvalidOperationException(
                "DC1 Blue descriptor patch refused: descriptor 0 +0x0A source bytes do not match.");

        ushort current = ExePatcher.ReadUInt16(exe, MaxHpFileOffset);
        if (current != ExpectedMaxHp)
            throw new InvalidOperationException(
                $"DC1 Blue descriptor patch refused: descriptor 0 +0x0A is {current}, " +
                $"expected {ExpectedMaxHp}.");

        ExePatcher.WriteUInt16(exe, MaxHpFileOffset, BlueMaxHp);
    }
}
