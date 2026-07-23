using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Graph;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public sealed class RoomGraphTests
{
    [Fact]
    public void Build_ExportsInitTransitions_AndRetainsEventDoorsAsRawRecords()
    {
        static (RoomFile Room, DoorRecord Door) DoorRoom(int stage, int room, int subroutineIndex)
        {
            var source = new RoomFile(stage, room);
            var door = new DoorRecord
            {
                TargetStage = 0x06,
                TargetRoom = 0x09,
                SubroutineIndex = subroutineIndex,
            };
            source.Doors.Add(door);
            return (source, door);
        }

        var event0503 = DoorRoom(0x05, 0x03, 1);
        var event050f = DoorRoom(0x05, 0x0f, 2);
        var init0604 = DoorRoom(0x06, 0x04, 0);
        var event0607 = DoorRoom(0x06, 0x07, 3);
        var init060b = DoorRoom(0x06, 0x0b, 0);
        var rooms = new[] { event0503.Room, event050f.Room, init0604.Room, event0607.Room, init060b.Room };

        var graph = RoomGraph.Build(rooms);

        Assert.Same(event0503.Door, Assert.Single(event0503.Room.Doors));
        Assert.Same(event050f.Door, Assert.Single(event050f.Room.Doors));
        Assert.Same(event0607.Door, Assert.Single(event0607.Room.Doors));

        var incoming = graph.Nodes
            .SelectMany(source => source.Edges.Select(edge => (From: source.Code, Edge: edge)))
            .Where(pair => pair.Edge.Target.Code == 0x0609)
            .OrderBy(pair => pair.From)
            .ToList();
        Assert.Equal(new[] { 0x0604, 0x060b }, incoming.Select(pair => pair.From).ToArray());
        Assert.All(incoming, pair => Assert.Equal(0x0609, pair.Edge.Target.Code));
        Assert.Same(init0604.Door, incoming[0].Edge.Door);
        Assert.Same(init060b.Door, incoming[1].Edge.Door);
    }

    [Fact]
    public void Build_ExportsKnownEventTransitions_ButNotTaskOrUnresolvedDoors()
    {
        static (RoomFile Room, DoorRecord Door) DoorRoom(int stage, int room,
                                                         DoorActivationKind activation)
        {
            var source = new RoomFile(stage, room);
            var door = new DoorRecord
            {
                TargetStage = 0x06,
                TargetRoom = 0x04,
                ActivationKind = activation,
            };
            source.Doors.Add(door);
            return (source, door);
        }

        var aot = DoorRoom(0x03, 0x09, DoorActivationKind.AotZone);
        var gotoSub = DoorRoom(0x01, 0x13, DoorActivationKind.GotoSub);
        var task = DoorRoom(0x05, 0x03, DoorActivationKind.TaskSpawn);
        var unresolved = DoorRoom(0x05, 0x0f, DoorActivationKind.Unresolved);

        var graph = RoomGraph.Build(new[] { aot.Room, gotoSub.Room, task.Room, unresolved.Room });
        var incoming = graph.Nodes
            .SelectMany(source => source.Edges.Select(edge => (From: source.Code, Edge: edge)))
            .Where(pair => pair.Edge.Target.Code == 0x0604)
            .OrderBy(pair => pair.From)
            .ToList();

        Assert.Equal(new[] { 0x0113, 0x0309 }, incoming.Select(pair => pair.From).ToArray());
        Assert.Same(gotoSub.Door, incoming[0].Edge.Door);
        Assert.Same(aot.Door, incoming[1].Edge.Door);
        Assert.Same(task.Door, Assert.Single(task.Room.Doors));
        Assert.Same(unresolved.Door, Assert.Single(unresolved.Room.Doors));
    }
}
