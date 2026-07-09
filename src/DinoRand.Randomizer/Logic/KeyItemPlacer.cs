using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;

namespace DinoRand.Randomizer.Logic;

/// <summary>
/// Phase 3. Progression-safe key-item logic on the real room graph, ported from BioRand's model
/// (docs/reference/cross/architecture/DESIGN.md §4): treat each door's required key as a gate and only ever place a key in a room
/// reachable with the keys collected so far, so every seed stays beatable.
///
/// <para><b>Gate model (proven — <c>docs/reference/dc1/_registries/STATIC-SCD-RE.md</c> cont.13).</b> A door's required key
/// is selected by its <see cref="DoorRecord.DoorType"/> byte via
/// <see cref="GameDefinition.KeyItemsForDoor"/> (the <c>+0x27</c> lock flag is only the group-9
/// "already unlocked" state bit, not the key selector). An edge is passable when the door is not
/// key-gated or the player holds a satisfying key.</para>
///
/// <para>Two entry points share one BFS: <see cref="Verify"/> checks a <i>given</i> placement is
/// solvable (assumed-fill: collect keys found in reached rooms, expand, repeat to a fixpoint),
/// and <see cref="Place"/> <i>produces</i> a solvable placement (forward flood-fill: when the
/// frontier stalls behind a gate, drop a still-needed key into a currently reachable spot and
/// resume). Both surface an unsolvable/over-constrained case as a failure.</para>
/// </summary>
public sealed class KeyItemPlacer
{
    /// <summary>A candidate location for a key: the room it sits in, the item record to write, and the
    /// optional guard that must be satisfied before the pickup is reachable (the map.json item
    /// <c>requires</c> case). <c>default</c> guard ⇒ no guard (see <see cref="Requirement"/>).</summary>
    public sealed record Spot(int RoomCode, ItemRecord Record, Requirement Requires = default);

    public sealed record PlacementResult(
        bool Success,
        IReadOnlyList<(Spot Spot, int KeyItem)> Placements,
        IReadOnlyList<string> Log);

    /// <summary>
    /// Rooms reachable from <paramref name="start"/> crossing every ungated door plus every
    /// key-gated door the player can currently open with <paramref name="held"/>, additionally
    /// honouring each edge's composite <see cref="RoomEdge.Requires"/> and each room's
    /// <see cref="RoomNode.Requires"/> (the map.json overlay). Because a room-state requirement
    /// (<c>requiresRoom</c>) can be unlocked by reaching a room through another path, the flood runs
    /// to a fixpoint — re-flooding until no new room is added (BioRand's
    /// <c>ItemRandomiser.cs:486-497</c> pattern). With no overlay this returns the same set as a
    /// single door-type BFS.
    /// </summary>
    public static HashSet<int> Reachable(RoomGraph graph, GameDefinition game, int start,
                                         IReadOnlySet<int> held)
    {
        var byCode = graph.Nodes.ToDictionary(n => n.Code);
        var seen = new HashSet<int> { start };
        // Group-9 story latches (STATIC-SCD-RE.md cont.40): a type-2 door is free to cross, so its lock
        // is set once its source room is reachable; a type-1 reader door is passable only once its lock
        // is in this set. Recomputed from `seen` each fixpoint pass (monotonic, set-once), which is what
        // stops the door-rando "stranded shortcut" softlock the free-edge model could not see.
        var latches = new HashSet<int>();
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var code in seen)
                if (byCode.TryGetValue(code, out var n))
                    foreach (var e in n.Edges)
                        if (e.Door.SetsStoryLatch) latches.Add(e.Door.LockId);

            var queue = new Queue<int>(seen);
            while (queue.Count > 0)
            {
                if (!byCode.TryGetValue(queue.Dequeue(), out var node)) continue;
                foreach (var edge in node.Edges)
                {
                    if (seen.Contains(edge.Target.Code)) continue;
                    if (!CanTraverse(game, edge, held, seen, latches)) continue;
                    seen.Add(edge.Target.Code);
                    queue.Enqueue(edge.Target.Code);
                    grew = true;
                }
            }
        }
        return seen;
    }

    /// <summary>True when the door-type key-set (OR) is open, the group-9 story latch (if this is a
    /// type-1 reader) has been set by a partner type-2 door, <b>and</b> the edge's authored item gate
    /// (AND) and the destination room's room-state gate (AND) are both satisfied.</summary>
    private static bool CanTraverse(GameDefinition game, RoomEdge edge, IReadOnlySet<int> held,
                                    IReadOnlySet<int> reach, IReadOnlySet<int> latches)
        => CanCross(game, edge.Door, held)
           && (!edge.Door.GatesOnStoryLatch || latches.Contains(edge.Door.LockId))
           && edge.Requires.SatisfiedBy(held, reach)
           && edge.Target.Requires.SatisfiedBy(held, reach);

    private static bool CanCross(GameDefinition game, DoorRecord door, IReadOnlySet<int> held)
    {
        var required = game.KeyItemsForDoor(door.DoorType);
        return required.Count == 0 || required.Any(held.Contains);
    }

    /// <summary>
    /// Verify a placement: with keys at the locations given by <paramref name="keysByRoom"/>,
    /// is <paramref name="goal"/> reachable? Models the player picking up every key in a room once
    /// they can stand in it, then re-flooding — the standard solvability check for an existing
    /// (e.g. vanilla, or shuffled-and-already-placed) layout.
    /// </summary>
    public static PlacementResult Verify(RoomGraph graph, GameDefinition game, int start, int goal,
                                         IReadOnlyDictionary<int, IReadOnlyList<int>> keysByRoom)
    {
        var byCode = graph.Nodes.ToDictionary(n => n.Code);
        var held = new HashSet<int>();
        var log = new List<string>();

        // Assumed-fill to a fixpoint: collect every key reachable with the keys held so far, re-flood,
        // repeat. Unlike a goal-only check this keeps collecting *past* the goal, so the result can also
        // prove no door key was left stranded (BioRand's "every item reachable", not just the win).
        HashSet<int> reach;
        while (true)
        {
            reach = Reachable(graph, game, start, held);
            bool grew = false;
            foreach (var room in reach)
                if (keysByRoom.TryGetValue(room, out var keys))
                    foreach (var key in keys)
                        if (PickupReachable(byCode, room, key, held, reach) && held.Add(key))
                            grew = true;
            if (!grew) break;
        }

        if (!reach.Contains(goal))
        {
            log.Add($"[progression] UNSOLVABLE: goal {goal:X4} unreachable; " +
                    $"reached {reach.Count} rooms; door keys held: {Fmt(held)}");
            return new PlacementResult(false, Array.Empty<(Spot, int)>(), log);
        }

        // Every door key that exists in the playable world must be collectable; one stranded behind its
        // own (or a later) gate is a soft defect even when the goal does not depend on it — the
        // Entrance-Key-relocated-to-the-last-area class the key-shuffle preview exposed.
        var stranded = StrandedDoorKeys(graph, game, start, keysByRoom, held);
        if (stranded.Count > 0)
        {
            log.Add($"[progression] UNSOLVABLE: goal {goal:X4} reachable but door key(s) stranded " +
                    $"(in-world yet uncollectable): {Fmt(stranded)}");
            return new PlacementResult(false, Array.Empty<(Spot, int)>(), log);
        }

        log.Add($"[progression] goal {goal:X4} reachable; all door keys collectable; held: {Fmt(held)}");
        return new PlacementResult(true, Array.Empty<(Spot, int)>(), log);
    }

    /// <summary>
    /// Door keys that exist in the playable world (rooms reachable while holding <i>every</i> door key)
    /// but were not <paramref name="collected"/> by the assumed-fill — i.e. every copy is stranded
    /// behind its own or a later gate. Keys whose only copies sit in areas unreachable even with all
    /// keys (the Operation Wipe-Out duplicates, e.g. <c>0A03</c>) are excluded: they are out of the
    /// playable world and never required. Empty ⇒ every door key is obtainable.
    /// </summary>
    private static IReadOnlyList<int> StrandedDoorKeys(RoomGraph graph, GameDefinition game, int start,
        IReadOnlyDictionary<int, IReadOnlyList<int>> keysByRoom, IReadOnlySet<int> collected)
    {
        var doorKeys = new HashSet<int>();
        foreach (var node in graph.Nodes)
            foreach (var edge in node.Edges)
                foreach (var k in game.KeyItemsForDoor(edge.Door.DoorType))
                    doorKeys.Add(k);
        if (doorKeys.Count == 0) return Array.Empty<int>();

        var world = Reachable(graph, game, start, doorKeys);
        var inWorld = new HashSet<int>();
        foreach (var room in world)
            if (keysByRoom.TryGetValue(room, out var keys))
                foreach (var k in keys)
                    if (doorKeys.Contains(k)) inWorld.Add(k);

        inWorld.ExceptWith(collected);
        return inWorld.OrderBy(x => x).ToList();
    }

    /// <summary>
    /// Produce a progression-safe placement of <paramref name="keyItems"/> into
    /// <paramref name="spots"/>: forward flood-fill from <paramref name="start"/>, dropping a
    /// still-needed key into a currently reachable spot whenever the frontier stalls behind a gate,
    /// until <paramref name="goal"/> is reachable; then scatter any leftover keys into reachable
    /// spots. Fails (no exception) when the layout is over-constrained — no reachable spot for a
    /// key the goal depends on.
    /// </summary>
    public PlacementResult Place(RoomGraph graph, GameDefinition game, int start, int goal,
                                 IReadOnlyList<Spot> spots, IReadOnlyCollection<int> keyItems,
                                 Seed seed)
    {
        // The greedy fill can dead-end (seat a key so the last spot lands behind its own gate), so
        // reroll with a fresh stream on failure — the DoorRandomizer retry pattern. Attempt 1 uses
        // the original "keyitems" stream, so every seed that already succeeded is byte-identical.
        const int maxAttempts = 20;
        PlacementResult result = null!;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var rng = seed.RngFor(attempt == 1 ? "keyitems" : $"keyitems-{attempt}");
            result = PlaceOnce(graph, game, start, goal, spots, keyItems, rng);
            if (result.Success) return result;
        }
        return result;
    }

    private static PlacementResult PlaceOnce(RoomGraph graph, GameDefinition game, int start, int goal,
                                             IReadOnlyList<Spot> spots, IReadOnlyCollection<int> keyItems,
                                             Random rng)
    {
        var held = new HashSet<int>();
        var remaining = new List<int>(keyItems);
        var placements = new List<(Spot, int)>();
        var usedSpots = new HashSet<Spot>();
        var log = new List<string>();

        while (true)
        {
            var reach = Reachable(graph, game, start, held);
            bool goalReached = reach.Contains(goal);

            if (goalReached && remaining.Count == 0)
            {
                log.Add($"[progression] placed {placements.Count} keys; goal {goal:X4} reachable.");
                return new PlacementResult(true, placements, log);
            }

            // Which still-unplaced keys would open a door off the current frontier (and so expand
            // reach)? Once the goal is reached, any remaining key is non-blocking filler.
            var helpful = goalReached
                ? new HashSet<int>(remaining)
                : FrontierKeys(graph, game, reach, held, remaining);

            // Item-guard frontier (plan §4.3): if a reachable spot is blocked only by its own guard,
            // surface the guard's items so a key needed to *reach a key* is placed first.
            if (!goalReached)
            {
                var remainingSet = new HashSet<int>(remaining);
                foreach (var s in spots)
                {
                    if (usedSpots.Contains(s) || !reach.Contains(s.RoomCode)) continue;
                    if (s.Requires.SatisfiedBy(held, reach)) continue;
                    foreach (var id in s.Requires.Items ?? Array.Empty<int>())
                        if (remainingSet.Contains(id)) helpful.Add(id);
                }
            }

            if (helpful.Count == 0)
            {
                // No key can grow reach and the goal is not reached → the layout cannot be solved.
                log.Add($"[progression] FAILED: stalled at {reach.Count} rooms with " +
                        $"{remaining.Count} keys unplaced; goal {goal:X4} unreachable.");
                return new PlacementResult(false, placements, log);
            }

            int key = Pick(helpful, rng);
            // Only seat a key in a spot the player can actually pick up: reachable room, not used,
            // and its own guard (if any) already satisfied.
            var free = spots.Where(s => reach.Contains(s.RoomCode) && !usedSpots.Contains(s)
                                        && s.Requires.SatisfiedBy(held, reach)).ToList();
            if (free.Count == 0)
            {
                log.Add($"[progression] FAILED: no reachable spot to place key {key:X2} " +
                        $"({reach.Count} rooms reached).");
                return new PlacementResult(false, placements, log);
            }

            var spot = free[rng.Next(free.Count)];
            usedSpots.Add(spot);
            placements.Add((spot, key));
            held.Add(key);
            remaining.Remove(key);
        }
    }

    /// <summary>Keys (from <paramref name="remaining"/>) that gate an edge leading from a reached
    /// room to a not-yet-reached one — i.e. placing one would extend the frontier. Covers both the
    /// door-type key-set and the edge's / destination's authored item requirement (the room-state
    /// part is unlocked by reaching rooms, not by placing a key, so it is not surfaced here).</summary>
    private static HashSet<int> FrontierKeys(RoomGraph graph, GameDefinition game,
                                             IReadOnlySet<int> reach, IReadOnlySet<int> held,
                                             IReadOnlyCollection<int> remaining)
    {
        var byCode = graph.Nodes.ToDictionary(n => n.Code);
        var remainingSet = new HashSet<int>(remaining);
        var result = new HashSet<int>();

        void Surface(IEnumerable<int>? ids)
        {
            foreach (var k in ids ?? Array.Empty<int>())
                if (remainingSet.Contains(k)) result.Add(k);
        }

        foreach (var code in reach)
        {
            if (!byCode.TryGetValue(code, out var node)) continue;
            foreach (var edge in node.Edges)
            {
                if (reach.Contains(edge.Target.Code)) continue;     // already reachable some other way
                if (!CanCross(game, edge.Door, held))                // door-type gate
                    Surface(game.KeyItemsForDoor(edge.Door.DoorType));
                if (!edge.Requires.SatisfiedBy(held, reach))         // edge item gate (puzzle/door key)
                    Surface(edge.Requires.Items);
                if (!edge.Target.Requires.SatisfiedBy(held, reach))  // destination's item gate (if any)
                    Surface(edge.Target.Requires.Items);
            }
        }
        return result;
    }

    /// <summary>True when the pickup of <paramref name="key"/> in <paramref name="room"/> is not held
    /// back by an item-guard (the map.json item <c>requires</c> case). When the room has no attached
    /// item carrying that id (e.g. a synthetic graph with no item records), it is treated as
    /// reachable — so this only ever constrains an actually-authored guard.</summary>
    private static bool PickupReachable(IReadOnlyDictionary<int, RoomNode> byCode, int room, int key,
                                        IReadOnlySet<int> held, IReadOnlySet<int> reach)
    {
        if (!byCode.TryGetValue(room, out var node)) return true;
        var matching = node.Items.Where(ni => ni.Record.ItemId == key).ToList();
        if (matching.Count == 0) return true;
        return matching.Any(ni => ni.Requires.SatisfiedBy(held, reach));
    }

    private static int Pick(HashSet<int> set, Random rng)
    {
        int i = rng.Next(set.Count);
        foreach (var v in set)
            if (i-- == 0) return v;
        return set.First();
    }

    private static string Fmt(IEnumerable<int> ids)
    {
        var list = ids.OrderBy(x => x).Select(x => $"{x:X2}").ToList();
        return list.Count == 0 ? "(none)" : string.Join(",", list);
    }
}
