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
/// sub-seed; after <see cref="MaxAttempts"/> failures the pass aborts without committing a candidate.</para>
/// </summary>
public sealed class DoorRandomizer : IRandomizationPass
{
    /// <summary>How many fresh-sub-seed attempts are allowed before generation aborts.</summary>
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
            var candidate = BuildCandidateGraph(ctx, result);
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

        throw new InvalidOperationException(
            $"no beatable door/key layout after {MaxAttempts} attempts; no door changes were committed");
    }

    /// <summary>
    /// Apply the pose-carrying rewrites (plan §4.1): each end takes its new destination and carries
    /// the donor door's original arrival pose. Only ever runs once <see cref="DoorPoseLayout.IsDecoded"/>
    /// is true; the lower-level <see cref="RoomScript.ApplyDoorEdits"/> throws otherwise.
    /// </summary>
    internal static void Commit(SegmentedDoorConnector.Result result)
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

    /// <summary>Build a dry-run graph through the normal room builder so node splits, item identity,
    /// requirements, exclusions, and candidate/commit ordering are identical.</summary>
    internal static RoomGraph BuildCandidateGraph(RandomizationContext context,
                                                  SegmentedDoorConnector.Result result)
    {
        var targets = new Dictionary<DoorRecord, int>();
        foreach (var pair in result.Pairings)
        {
            targets[pair.A.Door] = pair.B.RoomCode;
            targets[pair.B.Door] = pair.A.RoomCode;
        }
        foreach (var loop in result.SelfLoops) targets[loop.Door] = loop.RoomCode;

        var rooms = new List<RoomFile>();
        foreach (var source in context.Rooms)
        {
            var room = new RoomFile(source.Stage, source.Room);
            room.Items.AddRange(source.Items);
            foreach (var door in source.Doors)
            {
                int target = targets.GetValueOrDefault(door, door.TargetCode);
                room.Doors.Add(new DoorRecord
                {
                    TargetStage = (target >> 8) & 0xff,
                    TargetRoom = target & 0xff,
                    OriginalTargetStage = door.OriginalTargetStage,
                    OriginalTargetRoom = door.OriginalTargetRoom,
                    DoorType = door.DoorType,
                    LockId = door.LockId,
                    SubroutineIndex = door.SubroutineIndex,
                    ActivationKind = door.ActivationKind,
                    FileOffset = door.FileOffset,
                    Raw = door.Raw,
                });
            }
            rooms.Add(room);
        }
        return RoomGraph.Build(rooms, context.Game.Requirements);
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
            return KeyPlacementPlanner.Plan(ctx, candidate, game).Success;
        return KeyItemPlacer.Verify(candidate, game, game.StartRoomCode, game.GoalRoomCode,
                                    KeysByRoom(candidate, game)).Success;
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
