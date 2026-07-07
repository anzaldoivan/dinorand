using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;
using DinoRand.Randomizer.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Progression-logic tests (Phase 3 cont.13). The door-graph gate map and the flood-fill placer
/// are exercised on small synthetic graphs (no game files), and the whole model is validated on
/// the real install when <c>DINORAND_DC1_DIR</c> is set.
/// </summary>
public class KeyItemPlacerTests
{
    private static readonly DinoCrisis1 Game = new();

    private static DoorRecord Door(int destCode, int type) => new()
    {
        TargetStage = (destCode >> 8) & 0xff,
        TargetRoom = destCode & 0xff,
        DoorType = type,
    };

    private static void Link(RoomGraph g, int from, int to, int type)
    {
        var a = g.GetOrAdd((from >> 8) & 0xff, from & 0xff);
        var b = g.GetOrAdd((to >> 8) & 0xff, to & 0xff);
        a.Edges.Add(new RoomEdge(b, Door(to, type)));
    }

    private static KeyItemPlacer.Spot Spot(int roomCode, int originalId = 0x16) =>
        new(roomCode, new ItemRecord { ItemId = originalId, OriginalItemId = originalId, FileOffset = 0 });

    // --- Door-type -> required-key map (the cont.13 result) -------------------------------------

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x03)]
    [InlineData(0x05)]
    [InlineData(0xfd)]
    [InlineData(0xff)]
    public void DoorsThatAreNotKeyGated_RequireNoKey(int type)
        => Assert.Empty(Game.KeyItemsForDoor(type));

    [Fact]
    public void DirectKeyDoor_TypeByteIsTheItemId()
    {
        Assert.Equal(new[] { 0x2e }, Game.KeyItemsForDoor(0x2e)); // Entrance Key
        Assert.Equal(new[] { 0x30 }, Game.KeyItemsForDoor(0x30)); // BG Area Key
        Assert.Equal(new[] { 0x31 }, Game.KeyItemsForDoor(0x31)); // C.O. Area Key
    }

    [Fact]
    public void KeyCardLadder_HigherCardOpensLowerDoors()
    {
        Assert.Equal(new[] { 0x38, 0x39, 0x3a }, Game.KeyItemsForDoor(6)); // Lv C door: any card
        Assert.Equal(new[] { 0x39, 0x3a }, Game.KeyItemsForDoor(7));       // Lv B door: B or A
        Assert.Equal(new[] { 0x3a }, Game.KeyItemsForDoor(8));             // Lv A door: A only
    }

    // --- Reachable: a key gate blocks until the key is held -------------------------------------

    [Fact]
    public void Reachable_GatedDoorBlocksUntilKeyHeld()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);   // free
        Link(g, 0x0100, 0x0200, 0x2e);   // needs Entrance Key 0x2e

        var without = KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int>());
        Assert.Contains(0x0100, without);
        Assert.DoesNotContain(0x0200, without);

        var with = KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int> { 0x2e });
        Assert.Contains(0x0200, with);
    }

    [Fact]
    public void Reachable_KeyCardLevelIsHierarchical()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x08);   // needs a level-8 card (Lv A 0x3a)

        Assert.DoesNotContain(0x0100, KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int> { 0x39 }));
        Assert.Contains(0x0100, KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int> { 0x3a }));
    }

    // --- Verify: an existing placement is/ isn't solvable ---------------------------------------

    [Fact]
    public void Verify_SolvableChain_Succeeds()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        Link(g, 0x0100, 0x0200, 0x2e);   // goal behind Entrance Key

        // Key sits in a room reachable before the gate.
        var keys = new Dictionary<int, IReadOnlyList<int>> { [0x0100] = new[] { 0x2e } };
        var res = KeyItemPlacer.Verify(g, Game, 0x010d, 0x0200, keys);
        Assert.True(res.Success);
    }

    [Fact]
    public void Verify_KeyLockedBehindItsOwnGate_Fails()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        Link(g, 0x0100, 0x0200, 0x2e);

        // The only copy of the key is behind the door it opens — unreachable.
        var keys = new Dictionary<int, IReadOnlyList<int>> { [0x0200] = new[] { 0x2e } };
        var res = KeyItemPlacer.Verify(g, Game, 0x010d, 0x0200, keys);
        Assert.False(res.Success);
    }

    // --- Strengthened guarantee: every in-world door key must stay collectable, not just the goal ----

    [Fact]
    public void Verify_NonGoalDoorKeyStrandedBehindItsOwnGate_IsRejected()
    {
        // start --free--> goal(0100); 0100 --(Entrance Key 0x2e)--> 0200 side branch. The ONLY Entrance
        // Key sits in 0200, behind the very door it opens. The goal does not need it, so goal-only
        // reachability calls this beatable — but the key is uncollectable. The randomizer guarantee is
        // BioRand's "every item reachable", so this must be rejected (the Entrance-Key-in-0606 class of
        // defect surfaced by the key-shuffle preview).
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);   // free path to the goal
        Link(g, 0x0100, 0x0200, 0x2e);   // optional branch gated by the Entrance Key

        var keys = new Dictionary<int, IReadOnlyList<int>> { [0x0200] = new[] { 0x2e } };
        var res = KeyItemPlacer.Verify(g, Game, 0x010d, 0x0100, keys);
        Assert.False(res.Success);
    }

    [Fact]
    public void Verify_NonGoalDoorKeyCollectableElsewhere_StaysBeatable()
    {
        // Same shape, but the Entrance Key sits in the freely-reachable goal room — collectable, so the
        // gated branch is openable. A door key that gates only a non-goal branch must NOT be rejected
        // merely for existing; only an *uncollectable* one is a defect.
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        Link(g, 0x0100, 0x0200, 0x2e);

        var keys = new Dictionary<int, IReadOnlyList<int>> { [0x0100] = new[] { 0x2e } };
        Assert.True(KeyItemPlacer.Verify(g, Game, 0x010d, 0x0100, keys).Success);
    }

    [Fact]
    public void Verify_DoorKeyInUnreachableArea_IsNotRequired()
    {
        // A duplicate key in a disconnected room (the Operation Wipe-Out copies, e.g. 0A03) is outside
        // the playable world, so it must not be required — beatability is unaffected.
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        Link(g, 0x0100, 0x0200, 0x2e);
        g.GetOrAdd(0x0a, 0x03);          // disconnected room — no edge leads in

        var keys = new Dictionary<int, IReadOnlyList<int>>
        {
            [0x0100] = new[] { 0x2e },   // collectable copy in the playable world
            [0x0a03] = new[] { 0x2e },   // unreachable duplicate — ignored
        };
        Assert.True(KeyItemPlacer.Verify(g, Game, 0x010d, 0x0100, keys).Success);
    }

    // --- Place: produce a solvable placement, or fail if over-constrained ------------------------

    [Fact]
    public void Place_GatedChain_PlacesKeyInReachableSpotAndReachesGoal()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        Link(g, 0x0100, 0x0200, 0x2e);

        var spots = new[] { Spot(0x010d), Spot(0x0100) };
        var res = new KeyItemPlacer().Place(g, Game, 0x010d, 0x0200, spots,
                                            new[] { 0x2e }, new Seed(7));

        Assert.True(res.Success);
        var (spot, key) = Assert.Single(res.Placements);
        Assert.Equal(0x2e, key);
        // Placed in a room reachable empty-handed (so the gate can actually be opened).
        Assert.Contains(spot.RoomCode, new[] { 0x010d, 0x0100 });

        // The produced placement verifies as solvable.
        var keys = new Dictionary<int, IReadOnlyList<int>> { [spot.RoomCode] = new[] { key } };
        Assert.True(KeyItemPlacer.Verify(g, Game, 0x010d, 0x0200, keys).Success);
    }

    [Fact]
    public void Place_OverConstrained_OnlySpotBehindTheGate_Fails()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0200, 0x2e);   // goal directly behind the gate

        // The single spot is in the goal room, behind the very door its key must open.
        var spots = new[] { Spot(0x0200) };
        var res = new KeyItemPlacer().Place(g, Game, 0x010d, 0x0200, spots,
                                            new[] { 0x2e }, new Seed(1));
        Assert.False(res.Success);
    }

    // --- Composite requirements: edge item gate + room-state gate (graph-logic parity) -----------

    private static void LinkReq(RoomGraph g, int from, int to, int type, Requirement req)
    {
        var a = g.GetOrAdd((from >> 8) & 0xff, from & 0xff);
        var b = g.GetOrAdd((to >> 8) & 0xff, to & 0xff);
        a.Edges.Add(new RoomEdge(b, Door(to, type)) { Requires = req });
    }

    [Fact]
    public void Reachable_EdgeItemRequirement_BlocksUntilAllHeld()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);                                  // free
        LinkReq(g, 0x0100, 0x0200, 0x00, Requirement.OfItems(0x40, 0x41)); // AND of two non-door items

        Assert.DoesNotContain(0x0200, KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int> { 0x40 }));
        Assert.Contains(0x0200, KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int> { 0x40, 0x41 }));
    }

    [Fact]
    public void Reachable_RoomStateRequirement_ResolvesAtFixpoint()
    {
        // goal's room requires that 0x0100 has been reached; 0x0100 is reachable on a side branch,
        // so the fixpoint flood opens the goal even though its edge is visited before 0x0100.
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);     // side branch makes 0x0100 reachable
        Link(g, 0x010d, 0x0200, 0x00);     // edge to goal is free...
        g.GetOrAdd(0x02, 0x00).Requires = Requirement.OfRooms(0x0100); // ...but the room needs 0x0100 visited

        Assert.Contains(0x0200, KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int>()));
    }

    [Fact]
    public void Reachable_RoomStateRequirement_BlocksWhenRequiredRoomUnreachable()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0200, 0x00);
        g.GetOrAdd(0x02, 0x00).Requires = Requirement.OfRooms(0x0999); // a room with no path in

        Assert.DoesNotContain(0x0200, KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int>()));
    }

    // --- Item-guarded items: a pickup gated behind a held key ------------------------------------

    private static KeyItemPlacer.Spot GuardedSpot(int roomCode, int id, Requirement guard) =>
        new(roomCode, new ItemRecord { ItemId = id, OriginalItemId = id, FileOffset = 0 }, guard);

    [Fact]
    public void Place_GuardedSpot_IsAvoidedWhenItsGuardCannotBeHeld()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0200, 0x2e); // goal behind Entrance Key

        var guarded = GuardedSpot(0x010d, 0x16, Requirement.OfItems(0x40)); // 0x40 is never placed/held
        var open = Spot(0x010d);
        var res = new KeyItemPlacer().Place(g, Game, 0x010d, 0x0200,
                                            new[] { guarded, open }, new[] { 0x2e }, new Seed(3));

        Assert.True(res.Success);
        var (spot, _) = Assert.Single(res.Placements);
        Assert.Same(open.Record, spot.Record); // never seated into the guarded spot
    }

    [Fact]
    public void Verify_ItemGuardedKey_IsNotCollectableUntilGuardHeld()
    {
        // Room 0x0100 holds the goal key 0x2e, but that pickup is guarded by 0x40, which is nowhere —
        // so the key can never be collected and the goal is unreachable.
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        Link(g, 0x0100, 0x0200, 0x2e);
        var node = g.GetOrAdd(0x01, 0x00);
        node.Items.Add(new NodeItem(new ItemRecord { ItemId = 0x2e, OriginalItemId = 0x2e })
        {
            Requires = Requirement.OfItems(0x40),
        });

        var keys = new Dictionary<int, IReadOnlyList<int>> { [0x0100] = new[] { 0x2e } };
        Assert.False(KeyItemPlacer.Verify(g, Game, 0x010d, 0x0200, keys).Success);

        // Remove the guard → the key is collectable and the goal is reachable.
        node.Items[0].Requires = Requirement.None;
        Assert.True(KeyItemPlacer.Verify(g, Game, 0x010d, 0x0200, keys).Success);
    }

    // --- Real install: the door-graph model is solvable on the shipped game ----------------------

    private static List<RoomFile>? LoadInstall()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        var refs = Game.EnumerateRooms(root);
        if (refs.Count == 0) return null;
        return refs.Select(r => RoomFile.ReadFromFile(r.Stage, r.Room, r.Path)).ToList();
    }

    private static Dictionary<int, IReadOnlyList<int>> KeysByRoom(IEnumerable<RoomFile> rooms)
    {
        var map = new Dictionary<int, List<int>>();
        foreach (var room in rooms)
        {
            int code = ((room.Stage & 0xff) << 8) | (room.Room & 0xff);
            foreach (var item in room.Items)
                if (!item.IsEmptySlot && Game.KeyItemIds.Contains(item.ItemId))
                    (map.TryGetValue(code, out var l) ? l : map[code] = new List<int>()).Add(item.ItemId);
        }
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value);
    }

    [Fact]
    public void RealInstall_VanillaPlacement_IsBeatable()
    {
        var rooms = LoadInstall();
        if (rooms is null) return; // no game files (CI) — skip

        var graph = RoomGraph.Build(rooms);
        var res = KeyItemPlacer.Verify(graph, Game, Game.StartRoomCode, Game.GoalRoomCode,
                                       KeysByRoom(rooms));
        Assert.True(res.Success, string.Join("\n", res.Log));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1337)]
    public void RealInstall_ProgressionPass_LogsBeatable(int seed)
    {
        var rooms = LoadInstall();
        if (rooms is null) return;

        var log = new List<string>();
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(seed),
                                           new RandomizerConfig(), log.Add);
        new ProgressionPass().Apply(ctx);

        Assert.Contains(log, l => l.Contains("goal") && l.Contains("reachable"));
        Assert.DoesNotContain(log, l => l.Contains("UNSOLVABLE") || l.Contains("WARNING"));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(99)]
    [InlineData(2024)]
    public void RealInstall_FloodFillPlacesDoorKeys_ProducesBeatableLayout(int seed)
    {
        var rooms = LoadInstall();
        if (rooms is null) return;

        var graph = RoomGraph.Build(rooms);

        // Candidate spots: every real (non-empty) item record, tagged with its room.
        var spots = new List<KeyItemPlacer.Spot>();
        foreach (var room in rooms)
        {
            int code = ((room.Stage & 0xff) << 8) | (room.Room & 0xff);
            foreach (var item in room.Items)
                if (!item.IsEmptySlot)
                    spots.Add(new KeyItemPlacer.Spot(code, item));
        }

        // The keys the door graph actually gates on.
        var doorKeys = new[] { 0x2e, 0x30, 0x31, 0x3a };
        var res = new KeyItemPlacer().Place(graph, Game, Game.StartRoomCode, Game.GoalRoomCode,
                                            spots, doorKeys, new Seed(seed));
        Assert.True(res.Success, string.Join("\n", res.Log));

        // The placement the flood-fill produced is itself beatable.
        var keysByRoom = res.Placements
            .GroupBy(p => p.Spot.RoomCode)
            .ToDictionary(grp => grp.Key, grp => (IReadOnlyList<int>)grp.Select(p => p.KeyItem).ToList());
        Assert.True(KeyItemPlacer.Verify(graph, Game, Game.StartRoomCode, Game.GoalRoomCode, keysByRoom)
                        .Success);
    }

    // --- Key-item shuffle (ProgressionPass behind RandomizerConfig.ShuffleKeyItems) --------------

    private static readonly int[] DoorKeys = { 0x2e, 0x30, 0x31, 0x3a };

    private static RoomFile Room(int code, (int dest, int type)[] doors, int[] keys)
    {
        var room = new RoomFile((code >> 8) & 0xff, code & 0xff);
        foreach (var (dest, type) in doors)
            room.Doors.Add(new DoorRecord
            {
                TargetStage = (dest >> 8) & 0xff,
                TargetRoom = dest & 0xff,
                DoorType = type,
            });
        foreach (var k in keys)
            room.Items.Add(new ItemRecord { ItemId = k, OriginalItemId = k, Amount = 1, FileOffset = 0 });
        return room;
    }

    /// <summary>A definition that reuses DC1's door-gate map but lets a test pick start/goal codes.</summary>
    private sealed class TestGame : GameDefinition
    {
        private static readonly DinoCrisis1 Inner = new();
        public int Start { get; init; }
        public int Goal { get; init; }
        public override string Id => "test";
        public override string DisplayName => "Test";
        public override string ExecutableName => "test.exe";
        public override IReadOnlySet<int> KeyItemIds => Inner.KeyItemIds;
        public override IReadOnlyList<ItemPoolEntry> ItemPool => Inner.ItemPool;
        public override IReadOnlySet<int> ScriptedEnemyRoomCodes => Inner.ScriptedEnemyRoomCodes;
        public override int StartRoomCode => Start;
        public override int GoalRoomCode => Goal;
        public override IReadOnlyCollection<int> KeyItemsForDoor(int doorType) => Inner.KeyItemsForDoor(doorType);
        public override IReadOnlyList<RoomFileRef> EnumerateRooms(string installDir) => Array.Empty<RoomFileRef>();
        public override string? GetDataDir(string installDir) => null;
    }

    [Fact]
    public void ShuffleKeyItems_SyntheticWorld_StaysBeatableAndConservesKeys()
    {
        // start --free--> M ; start --(BG Area 0x30)--> GA ; M --(Entrance 0x2e)--> goal.
        var game = new TestGame { Start = 0x010d, Goal = 0x0121 };
        var rooms = new List<RoomFile>
        {
            Room(0x010d, new[] { (0x0100, 0), (0x0120, 0x30) }, new[] { 0x2e }),
            Room(0x0100, new[] { (0x0121, 0x2e) }, new[] { 0x30 }),
            Room(0x0120, Array.Empty<(int, int)>(), Array.Empty<int>()),
            Room(0x0121, Array.Empty<(int, int)>(), Array.Empty<int>()),
        };

        var log = new List<string>();
        var ctx = new RandomizationContext(game, rooms, RoomGraph.Build(rooms), new Seed(5),
                                           new RandomizerConfig { ShuffleKeyItems = true }, log.Add);
        new ProgressionPass().Apply(ctx);

        Assert.DoesNotContain(log, l => l.Contains("UNSOLVABLE") || l.Contains("WARNING"));
        var keys = rooms.SelectMany(r => r.Items).Select(i => i.ItemId)
                        .Where(id => id is 0x2e or 0x30).OrderBy(x => x);
        Assert.Equal(new[] { 0x2e, 0x30 }, keys); // both keys still present, exactly once each
    }

    [Fact]
    public void ShuffleOff_LeavesKeysInTheirVanillaSpots()
    {
        var game = new TestGame { Start = 0x010d, Goal = 0x0121 };
        var rooms = new List<RoomFile>
        {
            Room(0x010d, new[] { (0x0100, 0) }, new[] { 0x2e }),
            Room(0x0100, new[] { (0x0121, 0x2e) }, new[] { 0x30 }),
            Room(0x0121, Array.Empty<(int, int)>(), Array.Empty<int>()),
        };

        var ctx = new RandomizationContext(game, rooms, RoomGraph.Build(rooms), new Seed(5),
                                           new RandomizerConfig(), _ => { }); // ShuffleKeyItems off
        new ProgressionPass().Apply(ctx);

        Assert.Equal(0x2e, rooms[0].Items[0].ItemId);
        Assert.Equal(0x30, rooms[1].Items[0].ItemId);
    }

    private static List<int> DoorKeyMultiset(IEnumerable<RoomFile> rooms) =>
        rooms.SelectMany(r => r.Items)
             .Where(i => !i.IsEmptySlot && DoorKeys.Contains(i.ItemId))
             .Select(i => i.ItemId).OrderBy(x => x).ToList();

    private static Dictionary<int, List<int>> DoorKeyPositions(IEnumerable<RoomFile> rooms)
    {
        var map = new Dictionary<int, List<int>>();
        foreach (var room in rooms)
        {
            int code = ((room.Stage & 0xff) << 8) | (room.Room & 0xff);
            foreach (var item in room.Items)
                if (!item.IsEmptySlot && DoorKeys.Contains(item.ItemId))
                    (map.TryGetValue(item.ItemId, out var l) ? l : map[item.ItemId] = new List<int>()).Add(code);
        }
        foreach (var l in map.Values) l.Sort();
        return map;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(123)]
    public void RealInstall_KeyShuffle_StaysBeatableAndConservesKeys(int seed)
    {
        var rooms = LoadInstall();
        if (rooms is null) return;

        var before = DoorKeyMultiset(rooms);
        var log = new List<string>();
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(seed),
                                           new RandomizerConfig { ShuffleKeyItems = true }, log.Add);
        new ProgressionPass().Apply(ctx);

        Assert.Contains(log, l => l.Contains("[keyshuffle] relocated"));
        Assert.Contains(log, l => l.Contains("goal") && l.Contains("reachable"));
        Assert.DoesNotContain(log, l => l.Contains("UNSOLVABLE") || l.Contains("WARNING"));
        Assert.Equal(before, DoorKeyMultiset(rooms)); // every key conserved, none created/lost
    }

    [Fact]
    public void RealInstall_KeyShuffle_ActuallyRelocatesAtLeastOneKey()
    {
        var vanilla = LoadInstall();
        if (vanilla is null) return;
        var vanillaPos = DoorKeyPositions(vanilla);

        // Across a spread of seeds at least one must move a key off its vanilla spot.
        bool moved = false;
        for (int seed = 0; seed < 30 && !moved; seed++)
        {
            var rooms = LoadInstall()!;
            var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(seed),
                                               new RandomizerConfig { ShuffleKeyItems = true }, _ => { });
            new ProgressionPass().Apply(ctx);
            moved = !PositionsEqual(vanillaPos, DoorKeyPositions(rooms));
        }
        Assert.True(moved, "key shuffle never changed any door-key position across 30 seeds");
    }

    private static bool PositionsEqual(Dictionary<int, List<int>> a, Dictionary<int, List<int>> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var l) && l.SequenceEqual(kv.Value));
}
