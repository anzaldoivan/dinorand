using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Graph;

namespace DinoRand.Randomizer.Logic;

/// <summary>Stable identity of one physical DC1 item record.</summary>
public readonly record struct PhysicalPickupId(int RoomCode, int RecordOffset)
{
    public override string ToString() => $"{RoomCode:x4}:0x{RecordOffset:x}";
}

/// <summary>Explicit identity of one player-visible pickup. Multi-record pickups share this value.</summary>
public readonly record struct LogicalPickupId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>The complete placement quad carried by a subtype-4 item record.</summary>
public readonly record struct PickupGeometry(
    short X1, short Z1, short X2, short Z2, short X3, short Z3, short X4, short Z4)
{
    public static PickupGeometry From(ItemRecord record)
    {
        static short Word(byte[] raw, int at) => raw.Length >= at + 2
            ? (short)(raw[at] | raw[at + 1] << 8)
            : (short)0;
        return new PickupGeometry(
            Word(record.Raw, 0x04), Word(record.Raw, 0x06),
            Word(record.Raw, 0x08), Word(record.Raw, 0x0a),
            Word(record.Raw, 0x0c), Word(record.Raw, 0x0e),
            Word(record.Raw, 0x10), Word(record.Raw, 0x12));
    }
}

public enum PickupSourceClass { Unknown, Static, EventGranted, RuntimeArmed }
public enum PickupPlacementClass { Ordinary, Progression, Fixed, Ending, Excluded }

/// <summary>
/// Immutable physical/logical pickup snapshot shared by planning, writers, AP projection, and spoilers.
/// Mutable <see cref="ItemRecord"/> objects are locators only; a plan changes placement by creating a
/// new snapshot with <see cref="WithPlacement"/>.
/// </summary>
public sealed record PickupLocationContract(
    PhysicalPickupId PhysicalId,
    LogicalPickupId LogicalId,
    int OriginalCatalogId,
    int PlacedCatalogId,
    int RoomCode,
    int RecordOffset,
    Dc1ItemRecordClass ExpectedClass,
    int ExpectedOriginalAmount,
    ushort ExpectedOriginalTake,
    PickupGeometry Geometry,
    ItemPriority Priority,
    PickupVisual Visual,
    PickupSourceClass Source,
    bool Excluded,
    Requirement Requirements,
    PickupPlacementClass AllowedPlacementClass)
{
    public PickupLocationContract WithPlacement(int itemId) => this with { PlacedCatalogId = itemId };

    internal static PickupLocationContract Snapshot(NodeItem item) => new(
        item.PhysicalId,
        item.LogicalId ?? new LogicalPickupId(item.PhysicalId.ToString()),
        item.Record.OriginalItemId,
        item.Record.ItemId,
        item.RoomCode,
        item.Record.FileOffset,
        Dc1ItemRecordClass.Pickup,
        item.Record.OriginalAmount,
        item.Record.OriginalTakeIndex,
        PickupGeometry.From(item.Record),
        item.Priority,
        item.Visual,
        item.Record.IsEmptySlot ? PickupSourceClass.RuntimeArmed : item.Source,
        item.Excluded,
        item.Requires,
        item.Priority == ItemPriority.Fixed ? PickupPlacementClass.Fixed : item.AllowedPlacementClass);
}

/// <summary>
/// Immutable description of one planned physical-record mutation. The live record is retained only
/// as the mutation target; diagnostics and spoilers consume the captured before/after values and
/// location snapshot, never mutable post-commit state.
/// </summary>
internal sealed record PickupPlacementEdit(
    PickupLocationContract Location,
    ItemRecord Record,
    int BeforeItemId,
    int BeforeAmount,
    int ItemId,
    int Amount,
    string? Note = null)
{
    internal bool Changed => BeforeItemId != ItemId || BeforeAmount != Amount;
}
