using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;
using DinoRand.Randomizer.Maps;
using DinoRand.Randomizer.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Phase 3 door-destination shuffle (docs/dc1/DOOR-RANDOMIZER-PLAN.md). Covers the auto-classified
/// map, the segmented reciprocal connector, the graph rebuild, the beatability re-check, and the
/// HARD-GATE guard that keeps an undecoded entry pose from ever being written.
/// </summary>
public class DoorRandomizerTests
{
    private static readonly DinoCrisis1 Game = new();

    // --- helpers ------------------------------------------------------------------------------

    private static RoomFile Room(int code, params (int dest, int type)[] doors)
    {
        var room = new RoomFile((code >> 8) & 0xff, code & 0xff);
        foreach (var (dest, type) in doors)
            room.Doors.Add(new DoorRecord
            {
                TargetStage = (dest >> 8) & 0xff,
                TargetRoom = dest & 0xff,
                OriginalTargetStage = (dest >> 8) & 0xff,
                OriginalTargetRoom = dest & 0xff,
                DoorType = type,
                FileOffset = 0,
            });
        return room;
    }

    /// <summary>A two-way link: a door A→B and the reciprocal door B→A (type 0 = free).</summary>
    private static void LinkBoth(List<RoomFile> rooms, int a, int b, int type = 0)
    {
        Get(rooms, a).Doors.Add(VanillaDoor(b, type));
        Get(rooms, b).Doors.Add(VanillaDoor(a, type));
    }

    private static DoorRecord VanillaDoor(int dest, int type) => new()
    {
        TargetStage = (dest >> 8) & 0xff,
        TargetRoom = dest & 0xff,
        OriginalTargetStage = (dest >> 8) & 0xff,
        OriginalTargetRoom = dest & 0xff,
        DoorType = type,
        FileOffset = 0,
    };

    private static RoomFile Get(List<RoomFile> rooms, int code)
    {
        var r = rooms.FirstOrDefault(x => ((x.Stage << 8) | x.Room) == code);
        if (r is null) { r = new RoomFile((code >> 8) & 0xff, code & 0xff); rooms.Add(r); }
        return r;
    }

    /// <summary>A map JSON over <paramref name="codes"/>, all Segment unless overridden, with the
    /// given begin/end — for connector tests on a small synthetic world of real-id rooms.</summary>
    private static DoorMap MapOf(string start, string end, IEnumerable<int> codes,
                                 IDictionary<int, string>? categories = null)
    {
        var entries = codes.Select(c =>
        {
            string cat = categories != null && categories.TryGetValue(c, out var v) ? v : "Segment";
            return $"    \"{c:X4}\": {{ \"category\": \"{cat}\" }}";
        });
        string json = "{ \"beginEnd\": { \"start\": \"" + start + "\", \"end\": \"" + end + "\" }, " +
                      "\"rooms\": {\n" + string.Join(",\n", entries) + "\n} }";
        return DoorMap.Parse(json);
    }

    // --- map.json load / classification --------------------------------------------------------

    [Fact]
    public void DefaultMap_LoadsBeginEndAnd96StoryRooms()
    {
        var map = DoorMap.LoadDefault();
        Assert.Equal(0x010d, map.StartCode);
        Assert.Equal(0x060d, map.EndCode);
        // 96 real story rooms. The stage-6 count excludes the unused ST60E shell (see
        // DefaultMap_ExcludesUnusedRoomSt60E); stage variants 7/8/9 and demo A/B/C are absent.
        Assert.Equal(96, map.RoomCodes.Count);
        Assert.True(map.Contains(0x010d));
        Assert.True(map.Contains(0x060d));
    }

    [Fact]
    public void DefaultMap_ExcludesUnusedRoomSt60E()
    {
        var map = DoorMap.LoadDefault();
        // ST60E is the game's only UNUSED room — an unfinished shell (uncompressed segment, no
        // lighting/enemy triggers/final textures). It must never be a door-shuffle target, so the
        // map must not govern it: out of scope (not Contains) and Exclude category, matching how the
        // bonus/variant rooms are treated. Otherwise the connector could rewire a real door into it.
        Assert.False(map.Contains(0x060E));
        Assert.Equal(RoomCategory.Exclude, map.CategoryOf(0x060E));

        // Boundary: the real stage-6 neighbours stay in scope — exclusion must be surgical to 060E.
        Assert.True(map.Contains(0x060D));                              // Underground Heliport (goal/EndCode)
        Assert.NotEqual(RoomCategory.Exclude, map.CategoryOf(0x060D));
        Assert.True(map.Contains(0x060F));                              // Large Size Elevator (real room)
        Assert.NotEqual(RoomCategory.Exclude, map.CategoryOf(0x060F));
        Assert.True(map.Contains(0x0610));                             // Hovercraft Storage
    }

    [Fact]
    public void DefaultMap_ClassifiesCategories()
    {
        var map = DoorMap.LoadDefault();
        Assert.Equal(RoomCategory.Box, map.CategoryOf(0x010c));     // Material Storage
        Assert.Equal(RoomCategory.Bridge, map.CategoryOf(0x0102));  // degree-6 hub
        Assert.Equal(RoomCategory.Segment, map.CategoryOf(0x0100)); // ordinary room
    }

    [Fact]
    public void DefaultMap_OutOfScopeRoom_IsExclude()
    {
        var map = DoorMap.LoadDefault();
        // Stage-variant (7) and demo (A/B/C) rooms are intentionally absent → Exclude.
        Assert.False(map.Contains(0x0701));
        Assert.Equal(RoomCategory.Exclude, map.CategoryOf(0x0701));
        Assert.Equal(RoomCategory.Exclude, map.CategoryOf(0x0a03));
    }

    [Fact]
    public void DefaultMap_TagsOneWayDoorsAsStatic()
    {
        var map = DoorMap.LoadDefault();
        Assert.True(map.IsStaticEdge(0x0300, 0x030c));  // one-way in the decoded graph
        Assert.True(map.IsStaticEdge(0x030c, 0x0300));  // symmetric tagging
        Assert.False(map.IsStaticEdge(0x0103, 0x0102)); // an ordinary reciprocal door (neither room scripted)
    }

    [Fact]
    public void DefaultMap_TagsCutsceneRoomEdgesAsStatic()
    {
        // Every door edge incident to a scripted/cutscene room (GameDefinition.CutsceneRoomCodes:
        // 010D/0112/0109/030A/030E) must be static so the shuffle never re-points a FORCED traversal.
        // The prologue forces Regina through 0112<->010d — a real 0x28 door record (CE-traced,
        // STATIC-SCD-RE.md cont.24/25); re-pointing it would break the intro.
        var map = DoorMap.LoadDefault();
        Assert.True(map.IsStaticEdge(0x0112, 0x010d)); // the prologue forced door
        Assert.True(map.IsStaticEdge(0x010d, 0x0112)); // symmetric
        Assert.True(map.IsStaticEdge(0x0109, 0x0108)); // Lecture Room
        Assert.True(map.IsStaticEdge(0x030a, 0x030c)); // Hallway for Carrying in Materials
        Assert.True(map.IsStaticEdge(0x030e, 0x0307)); // Hall B1 (added alongside its one-way 030D)
        // Start-room exception: 010d is the connector's growth anchor, so only its FORCED edge
        // (0112) is locked; its ordinary doors stay shuffleable or the connector can't grow.
        Assert.False(map.IsStaticEdge(0x010d, 0x010a));
    }

    // --- segmented reciprocal connector --------------------------------------------------------

    private static List<RoomFile> ReciprocalWorld()
    {
        // A connected, fully reciprocal mini-world of real story-room ids.
        var rooms = new List<RoomFile>();
        LinkBoth(rooms, 0x010d, 0x010a);
        LinkBoth(rooms, 0x010a, 0x010c);
        LinkBoth(rooms, 0x010c, 0x0611);
        LinkBoth(rooms, 0x0611, 0x060d);
        LinkBoth(rooms, 0x010d, 0x0611);
        return rooms;
    }

    [Fact]
    public void Connector_ReciprocalWorld_ReachesGoalAndPairsAreReciprocal()
    {
        var rooms = ReciprocalWorld();
        var graph = RoomGraph.Build(rooms);
        var map = MapOf("010d", "060d", rooms.Select(r => (r.Stage << 8) | r.Room));

        var result = new SegmentedDoorConnector().Connect(graph, map, new Random(7));

        Assert.True(result.Success, string.Join("\n", result.Log));
        // Every collected free end is reassigned exactly once (pairings use two ends each).
        int ends = SegmentedDoorConnector.CollectFreeEnds(graph, map).Count;
        Assert.Equal(ends, result.Pairings.Count * 2 + result.SelfLoops.Count);
    }

    [Fact]
    public void Connector_AllFreeEndsAreInScopeReciprocalNonStatic()
    {
        var rooms = ReciprocalWorld();
        // Add a one-way door (no reciprocal) and an out-of-scope destination — both excluded.
        Get(rooms, 0x010d).Doors.Add(VanillaDoor(0x0100, 0)); // 0100 in map but no door back
        Get(rooms, 0x010a).Doors.Add(VanillaDoor(0x0701, 0)); // 0701 out of scope (variant stage)
        var graph = RoomGraph.Build(rooms);
        var map = MapOf("010d", "060d", new[] { 0x010d, 0x010a, 0x010c, 0x0611, 0x060d, 0x0100 });

        var ends = SegmentedDoorConnector.CollectFreeEnds(graph, map);
        Assert.DoesNotContain(ends, e => e.Door.TargetRoom == 0x00 && e.Door.TargetStage == 0x01); // 0100 one-way
        Assert.DoesNotContain(ends, e => e.Door.TargetStage == 0x07);                              // 0701 out-of-scope
        Assert.All(ends, e => Assert.True(map.Contains(e.RoomCode)));
    }

    [Fact]
    public void Connector_GoalHasNoReciprocalDoor_Fails()
    {
        // 060d is only reachable via a one-way door, so it has no free end to re-pair → fallback.
        var rooms = new List<RoomFile>();
        LinkBoth(rooms, 0x010d, 0x0611);
        Get(rooms, 0x0611).Doors.Add(VanillaDoor(0x060d, 0)); // one-way 0611 → 060d (no door back)
        Get(rooms, 0x060d); // exists but doorless
        var graph = RoomGraph.Build(rooms);
        var map = MapOf("010d", "060d", new[] { 0x010d, 0x0611, 0x060d });

        var result = new SegmentedDoorConnector().Connect(graph, map, new Random(1));
        Assert.False(result.Success);
    }

    // --- shuffled graph stays beatable (KeyItemPlacer.Verify) ----------------------------------

    [Fact]
    public void ShuffledGraph_GoalRemainsReachable_VerifyPasses()
    {
        var rooms = ReciprocalWorld();
        var graph = RoomGraph.Build(rooms);
        var map = MapOf("010d", "060d", rooms.Select(r => (r.Stage << 8) | r.Room));

        var result = new SegmentedDoorConnector().Connect(graph, map, new Random(11));
        Assert.True(result.Success);

        // Apply the new destinations to the records (as the pass would), then rebuild + verify.
        foreach (var p in result.Pairings)
        {
            Repoint(p.A.Door, p.B.RoomCode);
            Repoint(p.B.Door, p.A.RoomCode);
        }
        foreach (var loop in result.SelfLoops) Repoint(loop.Door, loop.RoomCode);

        var shuffled = RoomGraph.Build(rooms);
        var verify = KeyItemPlacer.Verify(shuffled, Game, 0x010d, 0x060d,
                                          new Dictionary<int, IReadOnlyList<int>>());
        Assert.True(verify.Success, string.Join("\n", verify.Log));
    }

    private static void Repoint(DoorRecord d, int destCode)
    {
        d.TargetStage = (destCode >> 8) & 0xff;
        d.TargetRoom = destCode & 0xff;
    }

    // --- RebuildGraph -------------------------------------------------------------------------

    [Fact]
    public void RebuildGraph_ReflectsEditedDoorDestinations()
    {
        var rooms = new List<RoomFile> { Room(0x010d, (0x0100, 0)), Room(0x0100), Room(0x0200) };
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(1),
                                           new RandomizerConfig(), _ => { });

        Assert.Contains(ctx.Graph.Nodes.Single(n => n.Code == 0x010d).Edges, e => e.Target.Code == 0x0100);

        // Re-point the door and rebuild: the new edge must replace the old one.
        var door = rooms[0].Doors[0];
        door.TargetStage = 0x02; door.TargetRoom = 0x00;
        ctx.RebuildGraph();

        var start = ctx.Graph.Nodes.Single(n => n.Code == 0x010d);
        Assert.Contains(start.Edges, e => e.Target.Code == 0x0200);
        Assert.DoesNotContain(start.Edges, e => e.Target.Code == 0x0100);
    }

    // --- laser-fence region gate survives a shuffle (REGION-SCHEMA-PLAN.md I3) ------------------

    [Fact]
    public void FenceRegionGate_SurvivesDoorShuffle_ViaRebuildGraph()
    {
        // 0102's fence gate (regions.accessFrom, migrated from door-level) binds to the physical
        // doorway (OriginalTargetCode 0107), NOT the destination. Simulate the connector repointing that
        // doorway elsewhere AND an unrelated 0102 doorway landing on the vanilla dest 0107, then rebuild
        // through the real overlay path (Game.Requirements). The gate must travel with the fence doorway
        // and must NOT float onto the intruder — the door-rando gate-floating bug the region model fixes.
        var rooms = new List<RoomFile>
        {
            Room(0x0102, (0x0107, 0), (0x0110, 0)),   // fence doorway (vanilla→0107) + a main doorway
            Room(0x0107), Room(0x0110), Room(0x0500),
        };
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms, Game.Requirements),
                                           new Seed(1), new RandomizerConfig(), _ => { });

        // vanilla: the fence doorway carries requiresRoom [0106]
        var before = ctx.Graph.Nodes.Single(n => n.Code == 0x0102)
                        .Edges.Single(e => e.Door.OriginalTargetCode == 0x0107);
        Assert.Equal(new[] { 0x0106 }, before.Requires.RoomsVisited);

        // connector repoints: fence doorway 0107→0500; the main doorway 0110→0107 (now targets the
        // vanilla fence destination). OriginalTargetCode of each door is unchanged.
        var fenceDoor = rooms[0].Doors.Single(d => d.OriginalTargetCode == 0x0107);
        var mainDoor = rooms[0].Doors.Single(d => d.OriginalTargetCode == 0x0110);
        Repoint(fenceDoor, 0x0500);
        Repoint(mainDoor, 0x0107);
        ctx.RebuildGraph();

        var node = ctx.Graph.Nodes.Single(n => n.Code == 0x0102);
        // gate travelled with the fence doorway to its new target 0500
        Assert.Equal(new[] { 0x0106 },
            node.Edges.Single(e => e.Door.OriginalTargetCode == 0x0107).Requires.RoomsVisited);
        // the intruder now pointing at 0107 did NOT inherit the fence gate
        Assert.True(node.Edges.Single(e => e.Door.OriginalTargetCode == 0x0110).Requires.IsEmpty);
    }

    // --- pose carry: decoded + live (Increment A, CE-validated 2026-06-20) ----------------------

    [Fact]
    public void PoseLayout_IsDecoded_WithCeConfirmedOffsets()
    {
        // Increment A landed: pose = four consecutive signed words after the destination word.
        Assert.True(DoorPoseLayout.IsDecoded);
        Assert.Equal(0x1e, DoorPoseLayout.EntryXOffset);
        Assert.Equal(0x20, DoorPoseLayout.EntryYOffset);
        Assert.Equal(0x22, DoorPoseLayout.EntryZOffset);
        Assert.Equal(0x24, DoorPoseLayout.EntryDOffset);
    }

    [Fact]
    public void ApplyDoorEdits_PoseEdit_NowWritesPoseAtDecodedOffsets()
    {
        // The undecoded HARD-GATE throw is gone now that the offsets are CE-confirmed: a carried
        // pose is written in place at +0x1e/+0x20/+0x22/+0x24.
        var script = ParseSomeScript(out _);
        var buffer = new byte[0x40];
        var door = new DoorRecord
        {
            FileOffset = 0,
            EntryX = 5, EntryY = 6, EntryZ = 7, EntryD = 8, // all differ from Original (0) → PoseEdited
        };
        Assert.True(door.PoseEdited);

        script.ApplyDoorEdits(buffer, new[] { door }); // must not throw
        Assert.Equal(5, buffer[DoorPoseLayout.EntryXOffset]);
        Assert.Equal(6, buffer[DoorPoseLayout.EntryYOffset]);
        Assert.Equal(7, buffer[DoorPoseLayout.EntryZOffset]);
        Assert.Equal(8, buffer[DoorPoseLayout.EntryDOffset]);
    }

    [Fact]
    public void ApplyDoorEdits_DestinationOnlyEdit_WritesAndDoesNotThrow()
    {
        var script = ParseSomeScript(out _);
        var buffer = new byte[0x40];
        var door = new DoorRecord
        {
            FileOffset = 0,
            TargetStage = 0x06, TargetRoom = 0x0d,
            OriginalTargetStage = 0x01, OriginalTargetRoom = 0x00,
        };
        Assert.True(door.IsEdited);
        Assert.False(door.PoseEdited);

        script.ApplyDoorEdits(buffer, new[] { door });
        Assert.Equal(0x0d, buffer[DoorRecord.DestOffset]);     // room low byte
        Assert.Equal(0x06, buffer[DoorRecord.DestOffset + 1]); // stage high byte
    }

    [Fact]
    public void DoorRandomizer_PoseDecoded_ShufflesAndCommits()
    {
        // A connected world of real-map rooms reaching the goal, so the connector + beatability
        // both succeed; with the pose decoded the pass now commits the re-pointed doors.
        var rooms = ReciprocalWorld();
        var log = new List<string>();
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(3),
                                           new RandomizerConfig { RandomizeDoors = true }, log.Add);

        new DoorRandomizer().Apply(ctx);

        Assert.Contains(log, l => l.Contains("shuffled"));
        Assert.DoesNotContain(log, l => l.Contains("HARD GATE"));
        Assert.Contains(rooms.SelectMany(r => r.Doors), d => d.IsEdited); // at least one door re-pointed
    }

    [Fact]
    public void DoorRandomizer_DefaultConfig_IsDisabled()
        => Assert.False(new DoorRandomizer().IsEnabled(new RandomizerConfig()));

    [Fact]
    public void DoorRandomizer_ConnectorCannotReachGoal_FallsBackToVanillaAfterMaxAttempts()
    {
        // A world that never contains the goal room (060d): every connector attempt fails, and the
        // pass must exhaust MaxAttempts then fall back to vanilla — an unbeatable seed is never
        // shipped, and no door record is left half-edited.
        var rooms = new List<RoomFile>();
        LinkBoth(rooms, 0x010d, 0x010a);
        LinkBoth(rooms, 0x010a, 0x010c);
        var log = new List<string>();
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(11),
                                           new RandomizerConfig { RandomizeDoors = true }, log.Add);

        new DoorRandomizer().Apply(ctx);

        Assert.Contains(log, l => l.Contains("falling back to vanilla doors"));
        Assert.Equal(DoorRandomizer.MaxAttempts,
            log.Count(l => l.Contains("connector could not reach the goal")));
        Assert.DoesNotContain(rooms.SelectMany(r => r.Doors), d => d.IsEdited);
    }

    [Fact]
    public void DoorRandomizer_WithKeyShuffle_StillCommitsWhenNothingIsKeyGated()
    {
        // ShuffleKeyItems routes beatability through KeyItemPlacer.Place; with no key-gated doors in
        // the world the "nothing to seat" arm must accept the layout rather than reroll it.
        var rooms = ReciprocalWorld();
        var log = new List<string>();
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(3),
            new RandomizerConfig { RandomizeDoors = true, ShuffleKeyItems = true }, log.Add);

        new DoorRandomizer().Apply(ctx);

        Assert.Contains(log, l => l.Contains("shuffled"));
        Assert.DoesNotContain(log, l => l.Contains("falling back"));
    }

    // --- real install: the connector shuffles the real graph and stays beatable ----------------

    private static List<RoomFile>? LoadInstall()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        var refs = Game.EnumerateRooms(root);
        if (refs.Count == 0) return null;
        return refs.Select(r => RoomFile.ReadFromFile(r.Stage, r.Room, r.Path)).ToList();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(2024)]
    public void RealInstall_Connector_ShufflesRealGraphAndKeepsGoalReachable(int seed)
    {
        var rooms = LoadInstall();
        if (rooms is null) return; // no game files (CI) — skip

        var map = DoorMap.LoadDefault();
        var graph = RoomGraph.Build(rooms);
        var result = new SegmentedDoorConnector().Connect(graph, map, new Seed(seed).RngFor("doors"));
        Assert.True(result.Success, string.Join("\n", result.Log));

        // Apply the new destinations to the real records, rebuild, and confirm the goal is reachable.
        foreach (var p in result.Pairings)
        {
            Repoint(p.A.Door, p.B.RoomCode);
            Repoint(p.B.Door, p.A.RoomCode);
        }
        foreach (var loop in result.SelfLoops) Repoint(loop.Door, loop.RoomCode);

        var shuffled = RoomGraph.Build(rooms);
        var reach = KeyItemPlacer.Reachable(shuffled, Game, Game.StartRoomCode,
                                            Game.KeyItemIds.ToHashSet());
        Assert.Contains(Game.GoalRoomCode, reach);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(99)]
    public void RealInstall_DoorPass_StaysBeatableUnderKeyShuffle(int seed)
    {
        var rooms = LoadInstall();
        if (rooms is null) return;

        var map = DoorMap.LoadDefault();
        var graph = RoomGraph.Build(rooms);
        var result = new SegmentedDoorConnector().Connect(graph, map, new Seed(seed).RngFor("doors"));
        Assert.True(result.Success, string.Join("\n", result.Log));

        foreach (var p in result.Pairings)
        {
            Repoint(p.A.Door, p.B.RoomCode);
            Repoint(p.B.Door, p.A.RoomCode);
        }
        foreach (var loop in result.SelfLoops) Repoint(loop.Door, loop.RoomCode);

        // The rewired world must admit a beatable key seating (what ProgressionPass would do).
        var shuffled = RoomGraph.Build(rooms);
        var spots = new List<KeyItemPlacer.Spot>();
        foreach (var room in rooms)
        {
            int code = (room.Stage << 8) | room.Room;
            foreach (var item in room.Items)
                if (!item.IsEmptySlot) spots.Add(new KeyItemPlacer.Spot(code, item));
        }
        var doorKeys = new[] { 0x2e, 0x30, 0x31, 0x3a };
        var placed = new KeyItemPlacer().Place(shuffled, Game, Game.StartRoomCode, Game.GoalRoomCode,
                                               spots, doorKeys, new Seed(seed));
        Assert.True(placed.Success, string.Join("\n", placed.Log));
    }

    private static RoomScript ParseSomeScript(out byte[] buffer)
    {
        // Build a trivial valid RDT buffer with an empty function table so Parse succeeds; we only
        // exercise ApplyDoorEdits, which patches a caller-supplied buffer by FileOffset.
        buffer = new byte[0x40];
        // header dword @0x14 → points at table base 0x20; table size dword = 4 (one subroutine).
        const int tableBase = 0x20;
        uint ptr = RoomScript.PsxRdtBase + tableBase;
        buffer[0x14] = (byte)ptr; buffer[0x15] = (byte)(ptr >> 8);
        buffer[0x16] = (byte)(ptr >> 16); buffer[0x17] = (byte)(ptr >> 24);
        buffer[tableBase] = 4; // table size = 4 bytes → 1 entry; entry value 4 → subroutine at base+4 (empty)
        var script = RoomScript.Parse(buffer);
        return script;
    }
}
