using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;
using DinoRand.Randomizer.Maps;

namespace DinoRand.Randomizer.Passes;

/// <summary>
/// Phase 3. Door-destination shuffle (plan §5). Re-pairs the world's reciprocal doors into a fresh,
/// provably beatable layout each seed via <see cref="SegmentedDoorConnector"/>, carrying each door's
/// arrival pose with it (RE-style, plan §4.1), then rebuilds the graph so the progression / key
/// passes run on the new world.
///
/// <para><b>Entry pose (plan §3.2, Increment A — DECODED).</b> The entry-pose offsets are now
/// CE-confirmed (<see cref="DoorPoseLayout.IsDecoded"/> = true; X/Y/Z/D at +0x1e/+0x20/+0x22/+0x24),
/// so a re-pointed door carries the reciprocal donor's arrival pose into the destination. The pass
/// is gated behind the default-off <see cref="RandomizerConfig.RandomizeDoors"/> (frontend enable is
/// Increment C). It still never ships an unbeatable seed (see below).</para>
///
/// <para><b>Beatability (plan §8).</b> Two layers: the connector only reports success when the goal
/// is reachable, and each candidate layout is re-checked here under the exact key logic the
/// downstream <see cref="ProgressionPass"/> will apply. A layout that fails is retried with a fresh
/// sub-seed; after <see cref="MaxAttempts"/> failures the pass falls back to vanilla doors.</para>
/// </summary>
public sealed class DoorRandomizer : IRandomizationPass
{
    /// <summary>How many fresh-sub-seed attempts before falling back to vanilla doors.</summary>
    public const int MaxAttempts = 8;

    public string Name => "doors";

    public bool IsEnabled(RandomizerConfig config) => config.RandomizeDoors;

    public void Apply(RandomizationContext ctx)
    {
        var map = DoorMap.LoadDefault();
        var connector = new SegmentedDoorConnector();

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var rng = ctx.Seed.RngFor(attempt == 1 ? "doors" : $"doors-{attempt}");
            var result = connector.Connect(ctx.Graph, map, rng);
            foreach (var line in result.Log) ctx.Log(line);

            if (!result.Success)
            {
                ctx.Log($"[doors] attempt {attempt}/{MaxAttempts}: connector could not reach the goal");
                continue;
            }

            // Belt-and-suspenders: re-check beatability under the same key logic ProgressionPass uses.
            var candidate = BuildCandidateGraph(ctx.Graph, result);
            ctx.Game.Requirements?.ApplyTo(candidate); // restamp gates for the re-pointed topology
            if (!IsBeatable(ctx, candidate))
            {
                ctx.Log($"[doors] attempt {attempt}/{MaxAttempts}: layout reaches goal but is not " +
                        "key-progression solvable; rerolling");
                continue;
            }

            Commit(result);
            ctx.RebuildGraph();
            ctx.Log($"[doors] shuffled {result.Pairings.Count} reciprocal door pairs " +
                    $"({result.SelfLoops.Count} self-loops) on attempt {attempt}");
            return;
        }

        ctx.Log($"[doors] no beatable layout after {MaxAttempts} attempts: falling back to vanilla doors");
    }

    /// <summary>
    /// Apply the pose-carrying rewrites (plan §4.1): each end takes its new destination and carries
    /// the donor door's original arrival pose. Only ever runs once <see cref="DoorPoseLayout.IsDecoded"/>
    /// is true; the lower-level <see cref="RoomScript.ApplyDoorEdits"/> throws otherwise.
    /// </summary>
    private static void Commit(SegmentedDoorConnector.Result result)
    {
        foreach (var p in result.Pairings)
        {
            Repoint(p.A, p.B.RoomCode, donor: p.B);
            Repoint(p.B, p.A.RoomCode, donor: p.A);
        }
        foreach (var loop in result.SelfLoops)
            Repoint(loop, loop.RoomCode, donor: loop); // odd end loops back to its own room
    }

    private static void Repoint(SegmentedDoorConnector.FreeEnd end, int destCode,
                                SegmentedDoorConnector.FreeEnd donor)
    {
        var d = end.Door;
        d.TargetStage = (destCode >> 8) & 0xff;
        d.TargetRoom = destCode & 0xff;
        // Carry the arrival pose of the door we are now connected to (the reciprocal donor): its
        // CE-confirmed NextX/Y/Z/D (plan §4.1) — where the player should stand at this doorway.
        d.EntryX = donor.Door.OriginalEntryX;
        d.EntryY = donor.Door.OriginalEntryY;
        d.EntryZ = donor.Door.OriginalEntryZ;
        d.EntryD = donor.Door.OriginalEntryD;
    }

    /// <summary>
    /// Build the <see cref="RoomGraph"/> the new pairing would produce, without mutating any record:
    /// unchanged doors keep their vanilla edge; each moved free end points at its new room. Used only
    /// to validate beatability before committing.
    /// </summary>
    private static RoomGraph BuildCandidateGraph(RoomGraph current, SegmentedDoorConnector.Result result)
    {
        var moved = new HashSet<DoorRecord>(ReferenceEqualityComparer.Instance);
        foreach (var p in result.Pairings) { moved.Add(p.A.Door); moved.Add(p.B.Door); }
        foreach (var loop in result.SelfLoops) moved.Add(loop.Door);

        var graph = new RoomGraph();
        foreach (var node in current.Nodes)
        {
            var copy = graph.GetOrAdd(node.Stage, node.Room);
            // Carry the live item records onto the candidate so its spot/key scans read node.Items
            // like the real graph. Guards are re-stamped by the overlay after this build.
            foreach (var ni in node.Items)
                copy.Items.Add(new NodeItem(ni.Record));
        }

        void AddEdge(int fromCode, int toCode, DoorRecord door)
        {
            var from = graph.GetOrAdd((fromCode >> 8) & 0xff, fromCode & 0xff);
            var to = graph.GetOrAdd((toCode >> 8) & 0xff, toCode & 0xff);
            from.Edges.Add(new RoomEdge(to, door));
        }

        // Keep every door the shuffle did not move (out-of-scope / static / unmatched-vanilla).
        foreach (var node in current.Nodes)
            foreach (var edge in node.Edges)
                if (!moved.Contains(edge.Door))
                    AddEdge(node.Code, edge.Target.Code, edge.Door);

        // Add the moved doors at their new destinations (reciprocal), door TYPE byte unchanged.
        foreach (var p in result.Pairings)
        {
            AddEdge(p.A.RoomCode, p.B.RoomCode, p.A.Door);
            AddEdge(p.B.RoomCode, p.A.RoomCode, p.B.Door);
        }
        foreach (var loop in result.SelfLoops)
            AddEdge(loop.RoomCode, loop.RoomCode, loop.Door);

        return graph;
    }

    /// <summary>
    /// Is the candidate layout beatable under the logic <see cref="ProgressionPass"/> will run?
    /// With key shuffle on, a solvable key seating must exist (<see cref="KeyItemPlacer.Place"/>);
    /// with it off, the vanilla key positions must already solve it (<see cref="KeyItemPlacer.Verify"/>).
    /// </summary>
    private static bool IsBeatable(RandomizationContext ctx, RoomGraph candidate)
    {
        var game = ctx.Game;
        if (ctx.Config.ShuffleKeyItems)
        {
            var (spots, keys) = DoorKeySpots(candidate, game);
            if (keys.Count == 0) return true; // nothing key-gated to seat
            return new KeyItemPlacer()
                .Place(candidate, game, game.StartRoomCode, game.GoalRoomCode, spots, keys, ctx.Seed)
                .Success;
        }
        return KeyItemPlacer.Verify(candidate, game, game.StartRoomCode, game.GoalRoomCode,
                                    KeysByRoom(candidate, game)).Success;
    }

    private static (IReadOnlyList<KeyItemPlacer.Spot> spots, IReadOnlyList<int> keys) DoorKeySpots(
        RoomGraph graph, GameDefinition game)
    {
        var doorKeys = new HashSet<int>();
        foreach (var node in graph.Nodes)
            foreach (var edge in node.Edges)
                foreach (var k in game.KeyItemsForDoor(edge.Door.DoorType))
                    doorKeys.Add(k);

        var reachable = KeyItemPlacer.Reachable(graph, game, game.StartRoomCode, doorKeys);
        var spots = new List<KeyItemPlacer.Spot>();
        var keys = new List<int>();
        foreach (var node in graph.Nodes)
        {
            if (!reachable.Contains(node.Code)) continue;
            foreach (var ni in node.Items)
                if (!ni.Record.IsEmptySlot && doorKeys.Contains(ni.Record.ItemId))
                {
                    spots.Add(new KeyItemPlacer.Spot(node.Code, ni.Record, ni.Requires));
                    keys.Add(ni.Record.ItemId);
                }
        }
        return (spots, keys);
    }

    private static Dictionary<int, IReadOnlyList<int>> KeysByRoom(RoomGraph graph, GameDefinition game)
    {
        var map = new Dictionary<int, List<int>>();
        foreach (var node in graph.Nodes)
            foreach (var ni in node.Items)
            {
                if (ni.Record.IsEmptySlot || !game.KeyItemIds.Contains(ni.Record.ItemId)) continue;
                (map.TryGetValue(node.Code, out var l) ? l : map[node.Code] = new()).Add(ni.Record.ItemId);
            }
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value);
    }
}
