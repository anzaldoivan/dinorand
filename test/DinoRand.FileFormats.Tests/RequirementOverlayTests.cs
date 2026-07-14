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
                // A freshly-parsed (unshuffled) door: vanilla == current, so OriginalTargetCode == TargetCode
                // (what the real RoomScript parser sets). Region gates bind to OriginalTargetCode.
                OriginalTargetStage = (dest >> 8) & 0xff,
                OriginalTargetRoom = dest & 0xff,
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

    [Fact]
    public void DoorItemGate_BindsToDoorway_SurvivesRepoint()
    {
        // A door `requires` (item) gate must bind to the physical doorway (OriginalTargetCode), not the
        // current destination — same rule as the region gate. Real DC1 gate: 0112->0114 requires [0x30].
        var overlay = MapRequirements.LoadDefault();
        var room = new RoomFile(0x01, 0x12);
        // (a) the gated 0112->0114 doorway REPOINTED to 0300 (vanilla dest 0114 preserved on Original).
        room.Doors.Add(new DoorRecord
        {
            TargetStage = 0x03, TargetRoom = 0x00,
            OriginalTargetStage = 0x01, OriginalTargetRoom = 0x14, // vanilla doorway identity (0114)
        });
        // (b) an unrelated 0112 doorway REPOINTED to 0114 — must NOT inherit the item gate.
        room.Doors.Add(new DoorRecord
        {
            TargetStage = 0x01, TargetRoom = 0x14,          // now points at the vanilla dest 0114
            OriginalTargetStage = 0x01, OriginalTargetRoom = 0x11, // but its own doorway is 0111
        });
        var node = NodeOf(RoomGraph.Build(new List<RoomFile> { room }, overlay), 0x0112);

        var gated = node.Edges.Single(e => e.Door.OriginalTargetCode == 0x0114);
        Assert.Equal(new[] { 0x30 }, gated.Requires.Items);           // gate travelled with the doorway
        var intruder = node.Edges.Single(e => e.Door.OriginalTargetCode == 0x0111);
        Assert.True(intruder.Requires.IsEmpty);                       // no float onto the intruder
    }

    [Fact]
    public void DoorRequiresRoomGate_BindsToDoorway_SurvivesRepoint()
    {
        // A door `requiresRoom` gate must likewise bind to OriginalTargetCode. Real DC1 gate:
        // 0400->0401 requiresRoom [0205,0109] (0109 added 2026-07-15, user-directed — closes the
        // 050B/0604/0609/060B deep-facility chain back to 0101/0202/the H-pair via the heliport).
        var overlay = MapRequirements.LoadDefault();
        var room = new RoomFile(0x04, 0x00);
        // (a) the gated 0400->0401 doorway REPOINTED to 0300 (vanilla dest 0401 preserved).
        room.Doors.Add(new DoorRecord
        {
            TargetStage = 0x03, TargetRoom = 0x00,
            OriginalTargetStage = 0x04, OriginalTargetRoom = 0x01, // vanilla doorway identity (0401)
        });
        // (b) an unrelated 0400 doorway REPOINTED to 0401 — must NOT inherit the room gate.
        room.Doors.Add(new DoorRecord
        {
            TargetStage = 0x04, TargetRoom = 0x01,          // now points at the vanilla dest 0401
            OriginalTargetStage = 0x04, OriginalTargetRoom = 0x02, // but its own doorway is 0402
        });
        var node = NodeOf(RoomGraph.Build(new List<RoomFile> { room }, overlay), 0x0400);

        var gated = node.Edges.Single(e => e.Door.OriginalTargetCode == 0x0401);
        Assert.Equal(new[] { 0x0205, 0x0109 }, gated.Requires.RoomsVisited);  // gate travelled with the doorway
        var intruder = node.Edges.Single(e => e.Door.OriginalTargetCode == 0x0402);
        Assert.True(intruder.Requires.IsEmpty);                       // no float onto the intruder
    }

    // --- Laser-fence sub-room regions (REGION-SCHEMA-PLAN.md) ------------------------------------

    [Fact]
    public void ShippedDc1Map_LaserFenceRegion_GatesBehindDoors()
    {
        // The 0102/010A fence gates were migrated from door-level requiresRoom into regions.accessFrom;
        // MapRequirements now stamps them from the region schema, keyed by the doorway (vanilla dest).
        var overlay = MapRequirements.LoadDefault();
        var rooms = new List<RoomFile>
        {
            Room(0x0102, new[] { (0x0107, 0), (0x0101, 0), (0x0104, 0), (0x0110, 0) }, Array.Empty<int>()),
            Room(0x010A, new[] { (0x0107, 0), (0x010B, 0) }, Array.Empty<int>()),
        };
        var g = RoomGraph.Build(rooms, overlay);

        RoomEdge Edge(int src, int dest) =>
            NodeOf(g, src).Edges.Single(e => e.Target.Code == dest);

        // 0102 fenceB (behind {0107}): requiresRoom [0106]
        Assert.Equal(new[] { 0x0106 }, Edge(0x0102, 0x0107).Requires.RoomsVisited);
        // 0102 fenceA (behind {0101,0104}): requiresRoom [0106,0202,0107]
        Assert.Equal(new[] { 0x0106, 0x0202, 0x0107 }, Edge(0x0102, 0x0101).Requires.RoomsVisited);
        Assert.Equal(new[] { 0x0106, 0x0202, 0x0107 }, Edge(0x0102, 0x0104).Requires.RoomsVisited);
        // 010A west (behind {0107}): requiresRoom [0106,0202,0107]
        Assert.Equal(new[] { 0x0106, 0x0202, 0x0107 }, Edge(0x010A, 0x0107).Requires.RoomsVisited);
        // main-region doors carry no fence gate
        Assert.True(Edge(0x0102, 0x0110).Requires.IsEmpty);
        Assert.True(Edge(0x010A, 0x010B).Requires.IsEmpty);
    }

    [Fact]
    public void LaserFenceRegionGate_BindsToDoorway_SurvivesRepoint()
    {
        // The whole point of the region model: the gate binds to the physical doorway (OriginalTargetCode),
        // not the current destination — so a door-rando repoint carries it, and it never floats onto an
        // unrelated door that now happens to point at the vanilla destination.
        var overlay = MapRequirements.LoadDefault();
        var room = new RoomFile(0x01, 0x02);
        // (a) the fence's own 0102->0107 doorway, REPOINTED to 0300 by a shuffle (vanilla dest 0107 preserved).
        room.Doors.Add(new DoorRecord
        {
            TargetStage = 0x03, TargetRoom = 0x00,          // repointed target
            OriginalTargetStage = 0x01, OriginalTargetRoom = 0x07, // vanilla doorway identity (0107)
        });
        // (b) an unrelated 0102->0110 doorway REPOINTED to 0107 — must NOT inherit the fence gate.
        room.Doors.Add(new DoorRecord
        {
            TargetStage = 0x01, TargetRoom = 0x07,          // now points at the vanilla dest 0107
            OriginalTargetStage = 0x01, OriginalTargetRoom = 0x10, // but its own doorway is 0110 (main)
        });
        var g = RoomGraph.Build(new List<RoomFile> { room }, overlay);
        var node = g.Nodes.Single(n => n.Code == 0x0102);

        // gate travelled with the fence doorway to its new target 0300
        var fenceEdge = node.Edges.Single(e => e.Door.OriginalTargetCode == 0x0107);
        Assert.Equal(new[] { 0x0106 }, fenceEdge.Requires.RoomsVisited);
        // the unrelated door now pointing at 0107 did NOT inherit the gate (no float)
        var intruder = node.Edges.Single(e => e.Door.OriginalTargetCode == 0x0110);
        Assert.True(intruder.Requires.IsEmpty);
    }

    [Fact]
    public void ShippedDc1Map_Fence0606_GatesEastItemSegment()
    {
        // 0606's east segment is item-only behind fence 0:147: its 3 pickups need Key Card C (0x38) held
        // plus room 0505 reached (the enable-flag producer). Modelled as region items, stamped as guards.
        var overlay = MapRequirements.LoadDefault();
        var room = new RoomFile(0x06, 0x06);
        room.Items.Add(QuadItem(0x2b, 6144, -2560));   // east (behind) — Plug
        room.Items.Add(QuadItem(0x18, 3328, -2560));   // east (behind) — Grenade Bullets
        room.Items.Add(QuadItem(0x31, -7648, 2880));   // west (main) — ungated
        var items = NodeOf(RoomGraph.Build(new List<RoomFile> { room }, overlay), 0x0606).Items;

        var behind = items.Single(ni => PosOf(ni) == (6144, -2560));
        Assert.Equal(new[] { 0x38 }, behind.Requires.Items);
        Assert.Equal(new[] { 0x0505 }, behind.Requires.RoomsVisited);
        Assert.Equal(new[] { 0x38 }, items.Single(ni => PosOf(ni) == (3328, -2560)).Requires.Items);
        Assert.True(items.Single(ni => PosOf(ni) == (-7648, 2880)).Requires.IsEmpty); // main-side pickup free
    }

    [Fact]
    public void RegionSchema_DoesNotSplitNodes_TopologyStaysAtomic()
    {
        // The doorway-keyed realization never duplicates a node: every room is still exactly one node,
        // so the room-code-keyed reachability engine (KeyItemPlacer) is unaffected. Regression guard for
        // the "no node split" contract — a fence room yields one node just like an atomic room.
        var overlay = MapRequirements.LoadDefault();
        var rooms = new List<RoomFile>
        {
            Room(0x0102, new[] { (0x0107, 0), (0x0101, 0), (0x0110, 0) }, Array.Empty<int>()), // fenced
            Room(0x0106, new[] { (0x0102, 0) }, Array.Empty<int>()),                            // atomic
        };
        var g = RoomGraph.Build(rooms, overlay);
        Assert.Equal(g.Nodes.Count, g.Nodes.Select(n => n.Code).Distinct().Count()); // no duplicate Code
        Assert.Single(g.Nodes, n => n.Code == 0x0102);
    }

    private static (short, short) PosOf(NodeItem ni) =>
        ((short)(ni.Record.Raw[0x04] | ni.Record.Raw[0x05] << 8),
         (short)(ni.Record.Raw[0x06] | ni.Record.Raw[0x07] << 8));
}
