using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;
using DinoRand.Randomizer.Maps;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// True sub-region node-split for intra-room, entry-direction partitions (REGION-SCHEMA-PLAN.md §2 /
/// GRAPH-LOGIC-PARITY-PLAN.md §8k). Models the 0309 shuttle: which exits are reachable depends on which
/// door you entered by — a predicate the flat door/room-state overlay cannot express. The split floods
/// over <see cref="RoomNode.NodeCode"/> while returning masked room codes, so un-split rooms stay
/// byte-identical.
/// </summary>
public class RegionNodeSplitTests
{
    private static readonly DinoCrisis1 Game = new();

    private static RoomFile Room(int code, params int[] doorDests)
    {
        var room = new RoomFile((code >> 8) & 0xff, code & 0xff);
        foreach (var dest in doorDests)
            room.Doors.Add(new DoorRecord
            {
                TargetStage = (dest >> 8) & 0xff,
                TargetRoom = dest & 0xff,
                DoorType = 0,
                OriginalTargetStage = (dest >> 8) & 0xff,
                OriginalTargetRoom = dest & 0xff,
            });
        return room;
    }

    // 0309-like hub split into west {0307, 030A} and shuttle {0113, 050B}, connected only through their
    // external doors (no on-foot crossing) — the shuttle car partition.
    private const string SplitOverlay = """
    {
      "rooms": {
        "0309": {
          "nodeSplit": true,
          "regions": {
            "west":    { "doors": ["0307", "030A"] },
            "shuttle": { "doors": ["0113", "050B"] }
          }
        }
      }
    }
    """;

    private static List<RoomFile> HubRooms(bool withElevatorEntry) => new()
    {
        // start reaches the west entry (0307) always, and the elevator entry (0113) only when asked.
        Room(0x010d, withElevatorEntry ? new[] { 0x0307, 0x0113 } : new[] { 0x0307 }),
        Room(0x0307, 0x010d, 0x0309),   // west entry into the hub
        Room(0x0113, 0x010d, 0x0309),   // elevator entry into the hub
        Room(0x0309, 0x0307, 0x030a, 0x0113, 0x050b), // hub: doors on both sides
        Room(0x030a, 0x0309),
        Room(0x050b, 0x0309),           // goal-side room, physically on the shuttle side only
    };

    [Fact]
    public void Split_EntryFromWestSide_CannotReachShuttleSide()
    {
        // Enter the hub only from 0307 (west). The shuttle-side room 050B must be UNREACHABLE — the car
        // blocks the crossing. (Atomic model would wrongly reach it: 0309 free → all exits free.)
        var graph = RoomGraph.Build(HubRooms(withElevatorEntry: false), MapRequirements.Parse(SplitOverlay));
        var reach = KeyItemPlacer.Reachable(graph, Game, 0x010d, new HashSet<int>());

        Assert.Contains(0x0309, reach);   // the hub itself is reached (west side)
        Assert.Contains(0x030a, reach);   // a west-side exit is reachable
        Assert.DoesNotContain(0x050b, reach); // the shuttle-side exit is NOT
    }

    [Fact]
    public void Split_EntryFromElevatorSide_ReachesShuttleSide()
    {
        // Add the elevator entry (0113 → lands on the shuttle side). Now 050B is reachable.
        var graph = RoomGraph.Build(HubRooms(withElevatorEntry: true), MapRequirements.Parse(SplitOverlay));
        var reach = KeyItemPlacer.Reachable(graph, Game, 0x010d, new HashSet<int>());

        Assert.Contains(0x050b, reach);
    }

    [Fact]
    public void Split_ProducesTwoNodesSharingOneRoomCode()
    {
        var graph = RoomGraph.Build(HubRooms(withElevatorEntry: true), MapRequirements.Parse(SplitOverlay));
        var hubNodes = graph.Nodes.Where(n => n.Code == 0x0309).ToList();
        Assert.Equal(2, hubNodes.Count);
        Assert.Contains(hubNodes, n => n.RegionIndex == 0 && n.NodeCode == 0x0309);       // primary == Code
        Assert.Contains(hubNodes, n => n.RegionIndex == 1 && n.NodeCode == (1 << 16 | 0x0309));
    }

    [Fact]
    public void NoSplit_IsByteIdenticalToAtomicGraph()
    {
        // Same rooms, no overlay: 0309 stays atomic (one node), and 050B is reachable from 0307 alone —
        // the pre-split behaviour, unchanged.
        var graph = RoomGraph.Build(HubRooms(withElevatorEntry: false));
        Assert.Single(graph.Nodes, n => n.Code == 0x0309);
        var reach = KeyItemPlacer.Reachable(graph, Game, 0x010d, new HashSet<int>());
        Assert.Contains(0x050b, reach);   // atomic: entering 0309 from 0307 reaches every exit
    }
}
