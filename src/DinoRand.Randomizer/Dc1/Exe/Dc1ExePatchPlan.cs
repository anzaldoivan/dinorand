namespace DinoRand.Randomizer.Dc1.Exe;

/// <summary>Seed-selected values consumed by FileFormats' validating EXE application APIs.</summary>
internal sealed record Dc1ExePatchPlan
{
    public int[]? BgmSourceIds { get; init; }
    public int[][]? EmergencyBoxSourceSlots { get; init; }
    public byte[][]? EmergencyBoxRecords { get; init; }
    public (int Id, int Count)[][]? StartingInventoryBlocks { get; init; }
    public int[]? ManagementOfficeSafeCode { get; init; }
}
