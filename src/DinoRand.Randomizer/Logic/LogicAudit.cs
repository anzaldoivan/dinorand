using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;

namespace DinoRand.Randomizer.Logic;

/// <summary>
/// Diagnostics that flag an <i>over-permissive</i> progression graph — places where our static door
/// decode (type-byte key gates + the group-9 reader/setter lock protocol) misses the real,
/// event-activated gates the game enforces in script / native code, so the intended multi-stage
/// progression collapses into a near-free-roam (investigation 2026-06-26;
/// docs/decisions/cross/GRAPH-LOGIC-PARITY-PLAN.md §8, map-requirements.md). The group-9 lock is not a
/// single flag but ~40 indices with a reader/setter protocol (door types 1/3 read, type 2 sets — see
/// STATIC-SCD-RE.md cont.40); <see cref="Definitions.GameDefinition.KeyItemsForDoor"/> currently models
/// types 1/3 as free, which is safe for vanilla reachability but must be latch-modelled before door-rando.
///
/// <para>These are detectors, not fixes: they surface the gap (and guard against it widening) until the
/// missing gates are authored into <c>map.json</c>. Pure functions over the graph — no game files
/// needed for the synthetic unit tests; the real-install tests run them on the shipped rooms.</para>
/// </summary>
public static class LogicAudit
{
    /// <summary>
    /// Door keys whose removal makes the <see cref="GameDefinition.GoalRoomCode"/> unreachable — the
    /// genuinely progression-critical set. An under-gated graph makes far fewer keys critical than the
    /// game actually locks doors with, so a low count is the headline "logic too permissive" signal.
    /// </summary>
    public static IReadOnlyList<int> CriticalDoorKeys(RoomGraph graph, GameDefinition game,
        IReadOnlyDictionary<int, IReadOnlyList<int>> keysByRoom)
    {
        // Only consider keys the game actually hands out as pickups (present in keysByRoom).
        var present = new HashSet<int>(keysByRoom.Values.SelectMany(v => v));
        var doorKeys = DoorKeys(graph, game).Where(present.Contains).OrderBy(x => x).ToList();

        var critical = new List<int>();
        foreach (var key in doorKeys)
        {
            // Hold every door key except this one; if the goal is now unreachable, the key gates the
            // only path to it → genuinely progression-critical.
            var held = new HashSet<int>(doorKeys);
            held.Remove(key);
            if (!KeyItemPlacer.Reachable(graph, game, game.StartRoomCode, held).Contains(game.GoalRoomCode))
                critical.Add(key);
        }
        return critical;
    }

    /// <summary>
    /// Free (no key required) door edges whose destination stage is at least
    /// <paramref name="minStageJump"/> stages beyond the source stage: phantom cross-region shortcuts
    /// (e.g. a 1F room opening straight into the stage-4 back area, or the elevator reaching unlocked
    /// floors) that flatten the progression. Each is a candidate missing gate.
    /// </summary>
    public static IReadOnlyList<(int From, int To, int Type)> CrossRegionFreeBridges(
        RoomGraph graph, GameDefinition game, int minStageJump = 2)
    {
        var bridges = new List<(int, int, int)>();
        foreach (var node in graph.Nodes)
        {
            int sourceStage = (node.Code >> 8) & 0xff;
            foreach (var edge in node.Edges)
            {
                if (game.KeyItemsForDoor(edge.Door.DoorType).Count != 0) continue; // properly gated
                int targetStage = (edge.Target.Code >> 8) & 0xff;
                if (targetStage - sourceStage >= minStageJump)
                    bridges.Add((node.Code, edge.Target.Code, edge.Door.DoorType));
            }
        }
        return bridges;
    }

    /// <summary>
    /// Rooms the graph lets you reach <i>without</i> holding the key that the (walkthrough-derived)
    /// <paramref name="requiredKeyByRoom"/> ground truth says they require — i.e. a gate the decode
    /// missed. Checks reachability while holding every door key <i>except</i> the required one; if the
    /// room is still reachable, that key is not actually gating it.
    /// </summary>
    public static IReadOnlyList<int> RoomsReachableWithoutRequiredKey(RoomGraph graph, GameDefinition game,
        IReadOnlyDictionary<int, int> requiredKeyByRoom)
    {
        var doorKeys = DoorKeys(graph, game);
        var violations = new List<int>();
        foreach (var (room, key) in requiredKeyByRoom)
        {
            // Hold every door key except the one this room is supposed to need; if the room is still
            // reachable, the graph is missing that gate (an event-activated lock the decode didn't see).
            var held = new HashSet<int>(doorKeys);
            held.Remove(key);
            if (KeyItemPlacer.Reachable(graph, game, game.StartRoomCode, held).Contains(room))
                violations.Add(room);
        }
        return violations.OrderBy(x => x).ToList();
    }

    /// <summary>The door keys that gate at least one edge in the graph (the relocatable / lockable set).</summary>
    internal static HashSet<int> DoorKeys(RoomGraph graph, GameDefinition game)
    {
        var keys = new HashSet<int>();
        foreach (var node in graph.Nodes)
            foreach (var edge in node.Edges)
                foreach (var k in game.KeyItemsForDoor(edge.Door.DoorType))
                    keys.Add(k);
        return keys;
    }
}
