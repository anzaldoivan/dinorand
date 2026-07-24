using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Detectors for an over-permissive progression graph (<see cref="LogicAudit"/>). The investigation
/// (2026-06-26) found the static door decode misses the game's event-activated gates, so the intended
/// multi-stage progression flattens into a near-free-roam — the Entrance Key reads as optional and the
/// key shuffle relocates it into the last area. These tests prove the detectors on synthetic graphs and
/// document the live gap on the shipped rooms (the real-install cases auto-skip without the game files).
/// </summary>
public class LogicAuditTests
{
    private static readonly DinoCrisis1 Game = new();

    private static DoorRecord Door(int destCode, int type) => new()
    {
        TargetStage = (destCode >> 8) & 0xff,
        TargetRoom = destCode & 0xff,
        DoorType = type,
    };

    private static void Edge(RoomGraph g, int from, int to, int type)
    {
        var a = g.GetOrAdd((from >> 8) & 0xff, from & 0xff);
        var b = g.GetOrAdd((to >> 8) & 0xff, to & 0xff);
        a.Edges.Add(new RoomEdge(b, Door(to, type)));
    }

    /// <summary>A definition reusing DC1's door-gate map with test-chosen start/goal.</summary>
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
        public override IReadOnlyList<RoomFileRef> EnumerateRooms(string installDir) => System.Array.Empty<RoomFileRef>();
        public override string? GetDataDir(string installDir) => null;
    }

    // --- #2 key-criticality -----------------------------------------------------------------------

    [Fact]
    public void CriticalDoorKeys_FlagsAKeyWhoseDoorIsTheSolePathToTheGoal()
    {
        // start --free--> A --(Entrance Key 0x2e)--> goal. Dropping 0x2e makes the goal unreachable.
        var game = new TestGame { Start = 0x010d, Goal = 0x0200 };
        var g = new RoomGraph();
        Edge(g, 0x010d, 0x0100, 0x00);
        Edge(g, 0x0100, 0x0200, 0x2e);

        var keysByRoom = new Dictionary<int, IReadOnlyList<int>> { [0x0100] = new[] { 0x2e } };
        Assert.Equal(new[] { 0x2e }, LogicAudit.CriticalDoorKeys(g, game, keysByRoom));
    }

    [Fact]
    public void CriticalDoorKeys_OmitsAKeyThatGatesOnlyAnOptionalBranch()
    {
        // start --free--> goal, and a side branch goal --(0x2e)--> B. The goal never needs 0x2e, so it
        // is NOT critical — exactly the Entrance-Key situation our real graph produces.
        var game = new TestGame { Start = 0x010d, Goal = 0x0100 };
        var g = new RoomGraph();
        Edge(g, 0x010d, 0x0100, 0x00);
        Edge(g, 0x0100, 0x0200, 0x2e);

        var keysByRoom = new Dictionary<int, IReadOnlyList<int>> { [0x0100] = new[] { 0x2e } };
        Assert.Empty(LogicAudit.CriticalDoorKeys(g, game, keysByRoom));
    }

    // --- #4 phantom cross-region free bridges -----------------------------------------------------

    [Fact]
    public void CrossRegionFreeBridges_FlagsAFreeDoorJumpingTwoOrMoreStages()
    {
        // A free (type-0) door from a stage-1 room straight into a stage-4 room — the 0112->0404 class.
        var game = new TestGame { Start = 0x010d, Goal = 0x0404 };
        var g = new RoomGraph();
        Edge(g, 0x010d, 0x0112, 0x00);
        Edge(g, 0x0112, 0x0404, 0x00);   // stage 1 -> stage 4, free: a phantom bridge
        Edge(g, 0x0112, 0x0114, 0x00);   // stage 1 -> stage 1, free: NOT a bridge

        var bridges = LogicAudit.CrossRegionFreeBridges(g, game, minStageJump: 2);
        Assert.Contains((0x0112, 0x0404, 0x00), bridges);
        Assert.DoesNotContain((0x0112, 0x0114, 0x00), bridges);
    }

    [Fact]
    public void CrossRegionFreeBridges_IgnoresKeyGatedCrossRegionDoors()
    {
        // A cross-region door that is properly key-gated is fine — only *free* jumps are phantom.
        var game = new TestGame { Start = 0x010d, Goal = 0x0404 };
        var g = new RoomGraph();
        Edge(g, 0x0112, 0x0404, 0x2e);   // gated by Entrance Key → legitimate, not a bridge

        Assert.Empty(LogicAudit.CrossRegionFreeBridges(g, game, minStageJump: 2));
    }

    // --- #1 walkthrough-order differential (parameterized by curated ground truth) -----------------

    [Fact]
    public void RoomsReachableWithoutRequiredKey_FlagsAGateTheGraphLetsYouSkip()
    {
        // Ground truth (e.g. from placements.md): room 0606 should require the Entrance Key 0x2e. But a
        // free path reaches it, so the key is not actually gating it → a missing gate.
        var game = new TestGame { Start = 0x010d, Goal = 0x0606 };
        var g = new RoomGraph();
        Edge(g, 0x010d, 0x0606, 0x00);   // free path that should not exist
        Edge(g, 0x010d, 0x0400, 0x2e);   // the real (modeled) Entrance-Key door

        var required = new Dictionary<int, int> { [0x0606] = 0x2e };
        Assert.Equal(new[] { 0x0606 }, LogicAudit.RoomsReachableWithoutRequiredKey(g, game, required));
    }

    [Fact]
    public void RoomsReachableWithoutRequiredKey_PassesWhenTheGateIsHonoured()
    {
        // 0400 genuinely sits behind the Entrance-Key door, so holding every key except 0x2e cannot
        // reach it — no violation.
        var game = new TestGame { Start = 0x010d, Goal = 0x0400 };
        var g = new RoomGraph();
        Edge(g, 0x010d, 0x0107, 0x00);
        Edge(g, 0x0107, 0x0400, 0x2e);

        var required = new Dictionary<int, int> { [0x0400] = 0x2e };
        Assert.Empty(LogicAudit.RoomsReachableWithoutRequiredKey(g, game, required));
    }

    // --- Real install: document the live gap (auto-skips without DINORAND_DC1_DIR) -----------------

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
    public void RealInstall_GraphHasPhantomCrossRegionBridges()
    {
        var rooms = LoadInstall();
        if (rooms is null) return;   // CI / no game files

        var graph = RoomGraph.Build(rooms);
        var bridges = LogicAudit.CrossRegionFreeBridges(graph, Game, minStageJump: 2);

        // The decisive symptom: a free 1F->stage-4 door (and the elevator's free jumps). Documents the
        // live gap; should become EMPTY once the gates are authored into map.json (GRAPH-LOGIC-PARITY §8).
        Assert.Contains((0x0112, 0x0404, 0x00), bridges);
    }

    [Fact]
    public void RealInstall_KeyCardAIsTheOnlyCriticalDoorKey()
    {
        var rooms = LoadInstall();
        if (rooms is null) return;

        var graph = RoomGraph.Build(rooms);
        var critical = LogicAudit.CriticalDoorKeys(graph, Game, KeysByRoom(rooms));

        // Characterization of the RAW door graph (LogicAudit builds without the map.json requires
        // overlay — that is applied later in ProgressionPass). Key Card Lv A 0x3a is the sole critical
        // raw door key because the Transport Passageway enters Special Weapons Storage through the
        // TYPE-8 0606->0607 record. The free 0607->060d escape does not bypass that entrance gate.
        // Other known native/event gates remain absent from this static raw-door model.
        Assert.Equal(new[] { 0x3a }, critical);
    }

    [Fact(Skip = "KNOWN GAP: the four-critical-keys soundness target is NOT offline-achievable — the " +
                 "2026-07-14 re-audit (STATIC-SCD-RE cont.71 / GRAPH-LOGIC-PARITY §8q) proved DC1's " +
                 "endgame + elevator gates are native (no readable SCD/door flag). Reaching the four-key " +
                 "state needs a ce-live-capture of those native gates, not more static authoring. " +
                 "Asserts the RAW graph, which locks 0 keys today.")]
    public void RealInstall_LogicGraph_IsSound()
    {
        var rooms = LoadInstall();
        if (rooms is null) return;

        var graph = RoomGraph.Build(rooms);
        Assert.Empty(LogicAudit.CrossRegionFreeBridges(graph, Game, minStageJump: 2));
        Assert.Equal(4, LogicAudit.CriticalDoorKeys(graph, Game, KeysByRoom(rooms)).Count);
    }
}
