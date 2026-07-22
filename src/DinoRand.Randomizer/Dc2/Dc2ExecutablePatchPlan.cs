namespace DinoRand.Randomizer.Dc2;

/// <summary>Seed-selected DC2 values consumed by validating FileFormats application methods.</summary>
internal sealed record Dc2ExecutablePatchPlan
{
    public string[]? MusicNames { get; init; }
    public uint[]? ShopPrices { get; init; }
    public ushort[]? ShopMasks { get; init; }
    public ushort[]? RecoveryPrices { get; init; }
    public string[]? ElevatorCodes { get; init; }
    public int[][]? CircuitSequences { get; init; }
    public int? RequiredPlate { get; init; }
}
