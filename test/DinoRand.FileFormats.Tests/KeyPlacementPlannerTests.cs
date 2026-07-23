using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;
using DinoRand.Randomizer.Maps;
using DinoRand.Randomizer.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public sealed class KeyPlacementPlannerTests
{
    private sealed class Game : GameDefinition
    {
        private static readonly DinoCrisis1 Inner = new();
        public IRequirementOverlay? Overlay { get; init; }
        public override string Id => "test";
        public override string DisplayName => "Test";
        public override string ExecutableName => "test.exe";
        public override IReadOnlySet<int> KeyItemIds => Inner.KeyItemIds;
        public override IReadOnlyList<ItemPoolEntry> ItemPool => Inner.ItemPool;
        public override IReadOnlySet<int> ScriptedEnemyRoomCodes => Inner.ScriptedEnemyRoomCodes;
        public override int StartRoomCode => 0x0100;
        public override int GoalRoomCode => 0x0102;
        public override IRequirementOverlay? Requirements => Overlay;
        public override IReadOnlyCollection<int> KeyItemsForDoor(int doorType) => Inner.KeyItemsForDoor(doorType);
        public override IReadOnlyList<RoomFileRef> EnumerateRooms(string installDir) => Array.Empty<RoomFileRef>();
        public override string? GetDataDir(string installDir) => null;
    }

    private static RandomizationContext Context(out ItemRecord first, out ItemRecord second)
    {
        var a = new RoomFile(1, 0);
        var b = new RoomFile(1, 1);
        var goal = new RoomFile(1, 2);
        a.Doors.Add(new DoorRecord { TargetStage = 1, TargetRoom = 1, DoorType = 0 });
        b.Doors.Add(new DoorRecord { TargetStage = 1, TargetRoom = 2, DoorType = 0x2e });
        first = new ItemRecord { ItemId = 0x2e, OriginalItemId = 0x2e, Amount = 1, OriginalAmount = 1, FileOffset = 0x100 };
        second = new ItemRecord { ItemId = 0x30, OriginalItemId = 0x30, Amount = 1, OriginalAmount = 1, FileOffset = 0x200 };
        a.Items.Add(first);
        b.Items.Add(second);
        var rooms = new[] { a, b, goal };
        var game = new Game();
        return new RandomizationContext(game, rooms, RoomGraph.Build(rooms), new Seed(7),
            new RandomizerConfig { ShuffleKeyItems = true }, _ => { });
    }

    [Fact]
    public void Planning_IsDeterministicAndDoesNotMutateRecords()
    {
        var context = Context(out var first, out var second);
        var before = new[] { (first.ItemId, first.Amount), (second.ItemId, second.Amount) };

        var a = KeyPlacementPlanner.Plan(context, context.Graph, context.Game);
        var b = KeyPlacementPlanner.Plan(context, context.Graph, context.Game);

        Assert.True(a.Success);
        Assert.Equal(a.Edits.Select(x => (x.Record.FileOffset, x.ItemId, x.Amount)),
                     b.Edits.Select(x => (x.Record.FileOffset, x.ItemId, x.Amount)));
        Assert.Equal(before, new[] { (first.ItemId, first.Amount), (second.ItemId, second.Amount) });
    }

    [Fact]
    public void DoorCandidateAndCommitPolicy_EnumerateIdenticalKeysAndLocations()
    {
        var context = Context(out _, out _);
        var firstDoor = context.Rooms[0].Doors[0];
        var secondDoor = context.Rooms[1].Doors[0];
        var result = new SegmentedDoorConnector.Result(true,
            new[] { new SegmentedDoorConnector.Pairing(
                new SegmentedDoorConnector.FreeEnd(0x0100, firstDoor),
                new SegmentedDoorConnector.FreeEnd(0x0101, secondDoor)) },
            Array.Empty<SegmentedDoorConnector.FreeEnd>(), Array.Empty<string>());
        var candidateGraph = DoorRandomizer.BuildCandidateGraph(context, result);
        var candidate = KeyPlacementPlanner.BuildPolicy(context, candidateGraph, context.Game);
        var candidateReachability = KeyItemPlacer.Verify(candidateGraph, context.Game,
            context.Game.StartRoomCode, context.Game.GoalRoomCode,
            KeyShuffleTransaction.KeysByRoom(candidateGraph, context.Game));
        var candidateWorld = KeyItemPlacer.Reachable(candidateGraph, context.Game,
            context.Game.StartRoomCode, context.Game.KeyItemIds);

        DoorRandomizer.Commit(result);
        context.RebuildGraph();
        var commit = KeyPlacementPlanner.BuildPolicy(context, context.Graph, context.Game);
        var committedReachability = KeyItemPlacer.Verify(context.Graph, context.Game,
            context.Game.StartRoomCode, context.Game.GoalRoomCode,
            KeyShuffleTransaction.KeysByRoom(context.Graph, context.Game));
        var committedWorld = KeyItemPlacer.Reachable(context.Graph, context.Game,
            context.Game.StartRoomCode, context.Game.KeyItemIds);

        Assert.Equal(candidate.Keys, commit.Keys);
        Assert.Equal(candidate.Spots.Select(x => x.Record.FileOffset),
                     commit.Spots.Select(x => x.Record.FileOffset));
        Assert.Equal(candidate.Relocating.Select(x => x.FileOffset).Order(),
                     commit.Relocating.Select(x => x.FileOffset).Order());
        Assert.Equal(candidateReachability.Success, committedReachability.Success);
        Assert.Equal(candidateWorld.Order(), committedWorld.Order());
    }

    [Fact]
    public void DoorCandidateAndCommit_PreserveTheSameSplitNodeTopology()
    {
        const string split = """
        { "rooms": { "0101": { "nodeSplit": true, "regions": {
          "west": { "doors": ["0100"] }, "east": { "doors": ["0102"] }
        } } } }
        """;
        var rooms = new[]
        {
            new RoomFile(1, 0), new RoomFile(1, 1), new RoomFile(1, 2),
        };
        rooms[0].Doors.Add(new DoorRecord { TargetStage = 1, TargetRoom = 1 });
        rooms[1].Doors.Add(new DoorRecord { TargetStage = 1, TargetRoom = 0 });
        rooms[1].Doors.Add(new DoorRecord { TargetStage = 1, TargetRoom = 2 });
        rooms[2].Doors.Add(new DoorRecord { TargetStage = 1, TargetRoom = 1 });
        var game = new Game { Overlay = MapRequirements.Parse(split) };
        var context = new RandomizationContext(game, rooms,
            RoomGraph.Build(rooms, game.Requirements), new Seed(3),
            new RandomizerConfig { ShuffleKeyItems = true }, _ => { });
        var result = new SegmentedDoorConnector.Result(true,
            Array.Empty<SegmentedDoorConnector.Pairing>(),
            Array.Empty<SegmentedDoorConnector.FreeEnd>(), Array.Empty<string>());

        var candidate = DoorRandomizer.BuildCandidateGraph(context, result);
        DoorRandomizer.Commit(result);
        context.RebuildGraph();

        Assert.Equal(2, candidate.Nodes.Count(x => x.Code == 0x0101));
        Assert.Equal(candidate.Nodes.Select(x => x.NodeCode).Order(),
                     context.Graph.Nodes.Select(x => x.NodeCode).Order());
        Assert.Equal(
            KeyItemPlacer.Reachable(candidate, game, game.StartRoomCode, new HashSet<int>()),
            KeyItemPlacer.Reachable(context.Graph, game, game.StartRoomCode, new HashSet<int>()));
    }

    [Fact]
    public void ApplyingRejectedPlan_ChangesNoRecord()
    {
        Context(out var first, out var second);
        var rejected = new KeyPlacementPlanner.PlanResult(false,
            Array.Empty<KeyPlacementPlanner.Edit>(), null, Array.Empty<string>(), null);

        Assert.Throws<InvalidOperationException>(() => KeyPlacementPlanner.Apply(rejected));
        Assert.Equal((0x2e, 1), (first.ItemId, first.Amount));
        Assert.Equal((0x30, 1), (second.ItemId, second.Amount));
    }

    [Fact]
    public void ProgressionVerificationFailure_ThrowsInsteadOfWarning()
    {
        var room = new RoomFile(1, 0);
        var game = new Game();
        var context = new RandomizationContext(game, new[] { room }, RoomGraph.Build(new[] { room }),
            new Seed(1), new RandomizerConfig { EnsureBeatable = true }, _ => { });

        Assert.Throws<InvalidOperationException>(() => new ProgressionPass().Apply(context));
    }

    [Fact]
    public void InjectedCommittedVerificationFailure_RestoresItemState()
    {
        var context = Context(out var first, out var second);
        var before = new[] { (first.ItemId, first.Amount), (second.ItemId, second.Amount) };
        KeyItemPlacer.PlacementResult Fail(RoomGraph _, GameDefinition __) => new(
            false, Array.Empty<(KeyItemPlacer.Spot, int)>(), new[] { "injected" });

        Assert.Throws<InvalidOperationException>(() =>
            KeyShuffleTransaction.Execute(context, context.Graph, context.Game, Fail));
        Assert.Equal(before, new[] { (first.ItemId, first.Amount), (second.ItemId, second.Amount) });
    }

    [Fact]
    public void ScatterSpoiler_ListsRelocatedKeyAndDisplacedConsumablePhysicalEdits()
    {
        RandomizationContext? scattered = null;
        for (int seed = 0; seed < 64 && scattered is null; seed++)
        {
            var start = new RoomFile(1, 0);
            var goal = new RoomFile(1, 2);
            start.Doors.Add(new DoorRecord
            {
                TargetStage = 1, TargetRoom = 2, DoorType = 0x2e,
            });
            var key = new ItemRecord
            {
                ItemId = 0x2e, OriginalItemId = 0x2e,
                Amount = 1, OriginalAmount = 1, FileOffset = 0x100,
            };
            var consumable = new ItemRecord
            {
                ItemId = 0x16, OriginalItemId = 0x16,
                Amount = 15, OriginalAmount = 15, FileOffset = 0x200,
            };
            start.Items.AddRange(new[] { key, consumable });
            var rooms = new[] { start, goal };
            var context = new RandomizationContext(new Game(), rooms, RoomGraph.Build(rooms),
                new Seed(seed), new RandomizerConfig
                {
                    ShuffleKeyItems = true,
                    ShuffleKeyItemsIntoPickups = true,
                }, _ => { });
            context.Graph.Nodes.SelectMany(node => node.Items)
                .Single(item => item.Record == consumable).IsScatterTarget = true;

            KeyShuffleTransaction.Execute(context, context.Graph, context.Game);
            if (consumable.ItemId == 0x2e)
                scattered = context;
        }

        Assert.NotNull(scattered);
        var section = Assert.Single(scattered.Spoiler.Sections,
            section => section.Title == KeyShuffleTransaction.SpoilerTitle);
        Assert.Equal(new[] { "Room", "Vanilla key there", "New key there" }, section.Columns);
        Assert.Equal(2, section.Rows.Count);
        Assert.Contains(section.Rows, row =>
            row[1].Contains(DinoRand.Randomizer.Spoiler.Dc1ItemNames.NameOf(0x2e))
            && row[2].Contains(DinoRand.Randomizer.Spoiler.Dc1ItemNames.NameOf(0x16)));
        Assert.Contains(section.Rows, row =>
            row[1].Contains(DinoRand.Randomizer.Spoiler.Dc1ItemNames.NameOf(0x16))
            && row[2].Contains(DinoRand.Randomizer.Spoiler.Dc1ItemNames.NameOf(0x2e)));
        Assert.Equal(2, section.Rows.Select(row => row[0]).Distinct().Count());
    }

    [Fact]
    public void CompositeSnapshot_RestoresDoorAndItemState()
    {
        var context = Context(out var first, out _);
        var door = context.Rooms[0].Doors[0];
        var snapshot = DoorKeyStateSnapshot.Capture(context.Rooms);
        door.TargetStage = 6;
        door.TargetRoom = 13;
        first.ItemId = 0x30;
        first.Amount = 99;

        snapshot.Restore();

        Assert.Equal(0x0101, door.TargetCode);
        Assert.Equal((0x2e, 1), (first.ItemId, first.Amount));
    }
}
