using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Logic;

/// <summary>Rollback boundary for the coupled door destinations/poses and progression item state.</summary>
internal sealed class DoorKeyStateSnapshot
{
    private sealed record DoorState(DoorRecord Record, int Stage, int Room,
        short X, short Y, short Z, short D);
    private sealed record ItemState(ItemRecord Record, int Id, int Amount);

    private readonly IReadOnlyList<DoorState> _doors;
    private readonly IReadOnlyList<ItemState> _items;

    private DoorKeyStateSnapshot(IReadOnlyList<DoorState> doors, IReadOnlyList<ItemState> items)
    {
        _doors = doors;
        _items = items;
    }

    internal static DoorKeyStateSnapshot Capture(IEnumerable<RoomFile> rooms) => new(
        rooms.SelectMany(room => room.Doors)
            .Select(door => new DoorState(door, door.TargetStage, door.TargetRoom,
                door.EntryX, door.EntryY, door.EntryZ, door.EntryD)).ToArray(),
        rooms.SelectMany(room => room.Items)
            .Select(item => new ItemState(item, item.ItemId, item.Amount)).ToArray());

    internal void Restore()
    {
        foreach (var state in _doors)
        {
            state.Record.TargetStage = state.Stage;
            state.Record.TargetRoom = state.Room;
            state.Record.EntryX = state.X;
            state.Record.EntryY = state.Y;
            state.Record.EntryZ = state.Z;
            state.Record.EntryD = state.D;
        }
        foreach (var state in _items)
        {
            state.Record.ItemId = state.Id;
            state.Record.Amount = state.Amount;
        }
    }
}
