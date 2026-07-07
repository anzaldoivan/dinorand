using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;
using DinoRand.Randomizer.Maps;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The graph-logic-parity overlay (GRAPH-LOGIC-PARITY-PLAN.md §4.1/§4.2): the <see cref="Requirement"/>
/// predicate, parsing the <c>map.json</c> requires schema (<see cref="MapRequirements"/>), and
/// stamping it onto the room graph. Also pins the auto-derived story gate the flag-graph extractor
/// writes into the shipped map.json (DATA-BACKFILL-PLAN.md).
/// </summary>
public class RequirementOverlayTests
{
    private static RoomFile Room(int code, (int dest, int type)[] doors, int[] items)
    {
        var room = new RoomFile((code >> 8) & 0xff, code & 0xff);
        foreach (var (dest, type) in doors)
            room.Doors.Add(new DoorRecord
            {
                TargetStage = (dest >> 8) & 0xff,
                TargetRoom = dest & 0xff,
                DoorType = type,
            });
        foreach (var id in items)
            room.Items.Add(new ItemRecord { ItemId = id, OriginalItemId = id, Amount = 1, FileOffset = 0 });
        return room;
    }

    private static RoomNode NodeOf(RoomGraph g, int code) =>
        g.Nodes.Single(n => n.Code == code);

    // --- Requirement predicate ------------------------------------------------------------------

    [Fact]
    public void Requirement_Default_IsAlwaysSatisfied()
    {
        Requirement none = default; // null arrays must behave like Requirement.None
        Assert.True(none.IsEmpty);
        Assert.True(none.SatisfiedBy(new HashSet<int>(), new HashSet<int>()));
        Assert.True(Requirement.None.IsEmpty);
    }

    [Fact]
    public void Requirement_AndSemantics_OverItemsAndRooms()
    {
        var req = new Requirement(new[] { 0x2e, 0x30 }, new[] { 0x0102 });
        Assert.False(req.SatisfiedBy(new HashSet<int> { 0x2e }, new HashSet<int> { 0x0102 }));      // missing 0x30
        Assert.False(req.SatisfiedBy(new HashSet<int> { 0x2e, 0x30 }, new HashSet<int>()));          // missing room
        Assert.True(req.SatisfiedBy(new HashSet<int> { 0x2e, 0x30 }, new HashSet<int> { 0x0102 })); // all held + visited
    }

    // --- Parsing + stamping ---------------------------------------------------------------------

    private const string SampleJson = """
    {
      "beginEnd": { "start": "010d", "end": "0200" },
      "rooms": {
        "010d": { "category": "Segment", "doors": { "0100": { "requires": [46] } } },
        "0100": { "category": "Segment", "items": { "30": { "requires": [46] } } },
        "0200": { "category": "Segment", "requiresRoom": ["0100"] }
      }
    }
    """;

    [Fact]
    public void MapRequirements_Parse_StampsEdgeNodeAndItemRequirements()
    {
        var rooms = new List<RoomFile>
        {
            Room(0x010d, new[] { (0x0100, 0) }, Array.Empty<int>()),
            Room(0x0100, new[] { (0x0200, 0) }, new[] { 0x30 }),
            Room(0x0200, Array.Empty<(int, int)>(), Array.Empty<int>()),
        };
        var overlay = MapRequirements.Parse(SampleJson);
        Assert.False(overlay.IsEmpty);

        var g = RoomGraph.Build(rooms, overlay);

        // edge 010d->0100 carries the door item requirement (0x2e == 46).
        var edge = NodeOf(g, 0x010d).Edges.Single(e => e.Target.Code == 0x0100);
        Assert.Equal(new[] { 0x2e }, edge.Requires.Items);

        // item 0x30 in room 0100 carries its guard.
        var guarded = NodeOf(g, 0x0100).Items.Single(ni => ni.Record.OriginalItemId == 0x30);
        Assert.Equal(new[] { 0x2e }, guarded.Requires.Items);

        // room 0200 carries the room-state requirement.
        Assert.Equal(new[] { 0x0100 }, NodeOf(g, 0x0200).Requires.RoomsVisited);
    }

    [Fact]
    public void MapRequirements_DoorRequiresRoom_StampsRoomStateOnTheEdge()
    {
        // Story-unlock door: crossing 010d->0100 needs room 0x0112 visited (the SetFlag producer),
        // composed with an item gate on the same door — exercises both edge AND arrays at once.
        const string json = """
        { "beginEnd": { "start": "010d", "end": "0100" },
          "rooms": { "010d": { "category": "Segment",
              "doors": { "0100": { "requires": [46], "requiresRoom": ["0112"] } } },
            "0100": { "category": "Segment" } } }
        """;
        var rooms = new List<RoomFile> { Room(0x010d, new[] { (0x0100, 0) }, Array.Empty<int>()) };
        var g = RoomGraph.Build(rooms, MapRequirements.Parse(json));

        var edge = NodeOf(g, 0x010d).Edges.Single(e => e.Target.Code == 0x0100);
        Assert.Equal(new[] { 0x2e }, edge.Requires.Items);
        Assert.Equal(new[] { 0x0112 }, edge.Requires.RoomsVisited);
    }

    [Fact]
    public void MapRequirements_NoRequiresFields_IsEmpty_AndLeavesGraphUntouched()
    {
        const string json = """
        { "beginEnd": { "start": "010d", "end": "0100" },
          "rooms": { "010d": { "category": "Segment" }, "0100": { "category": "Box" } } }
        """;
        var overlay = MapRequirements.Parse(json);
        Assert.True(overlay.IsEmpty);

        var rooms = new List<RoomFile> { Room(0x010d, new[] { (0x0100, 0) }, Array.Empty<int>()) };
        var g = RoomGraph.Build(rooms, overlay);
        Assert.True(NodeOf(g, 0x010d).Edges.Single().Requires.IsEmpty);
    }

    // A record carrying a placement quad whose first corner is (x,z) — what the item-priority overlay
    // matches on (the real walker fills Raw; the harness must set it).
    private static ItemRecord QuadItem(int id, short x, short z)
    {
        var raw = new byte[44];
        raw[0x04] = (byte)x; raw[0x05] = (byte)(x >> 8);
        raw[0x06] = (byte)z; raw[0x07] = (byte)(z >> 8);
        return new ItemRecord { ItemId = id, OriginalItemId = id, Amount = 1, FileOffset = 0, Raw = raw };
    }

    [Fact]
    public void ItemPriorityOverlay_StampsFixed_ByPosition()
    {
        // itemPriorities marks the pickup at (1536,-2464) Fixed; the sibling at a different position
        // stays Normal. Keyed by position (quad first corner), not item id (ITEM-RANDO-PLAN.md §7.1).
        const string json = """
        {
          "beginEnd": { "start": "0308", "end": "0308" },
          "rooms": {
            "0308": { "itemPriorities": [ { "at": "1536,-2464", "priority": "Fixed" } ] }
          }
        }
        """;
        var overlay = MapRequirements.Parse(json);
        Assert.False(overlay.IsEmpty);

        var room = new RoomFile(0x03, 0x08);
        room.Items.Add(QuadItem(0x1d, 1536, -2464));   // [0] the Fixed spot
        room.Items.Add(QuadItem(0x1d, 999, 111));      // [1] same id, different position → Normal
        var items = NodeOf(RoomGraph.Build(new List<RoomFile> { room }, overlay), 0x0308).Items;

        Assert.Equal(ItemPriority.Fixed, items[0].Priority);
        Assert.Equal(ItemPriority.Normal, items[1].Priority);
    }

    [Fact]
    public void ShippedDc1Map_CarriesAutoDerivedStoryGate()
    {
        // The flag-graph extractor (tools/scd_re/extract_logic.py, DATA-BACKFILL-PLAN.md) populated
        // the overlay, so it is no longer empty and DinoCrisis1 surfaces it.
        var overlay = MapRequirements.LoadDefault();
        Assert.False(overlay.IsEmpty);
        Assert.IsType<MapRequirements>(new DinoCrisis1().Requirements);

        // v1 high-confidence gate: crossing 0108->0113 requires having reached 0113 (the room that
        // runs SetFlag(9,6) which unlocks the type-1 story door). Pins the data + the engine wiring.
        var rooms = new List<RoomFile> { Room(0x0108, new[] { (0x0113, 0) }, Array.Empty<int>()) };
        var g = RoomGraph.Build(rooms, overlay);
        var edge = NodeOf(g, 0x0108).Edges.Single(e => e.Target.Code == 0x0113);
        Assert.Equal(new[] { 0x0113 }, edge.Requires.RoomsVisited);
    }
}
