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
        IReadOnlyList<string> Log,
        IReadOnlyList<SphereStep>? Spheres = null);

    /// <summary>One round of the <see cref="Verify"/> fixpoint, in the randomizer-community "sphere"
    /// convention (Archipelago / OoTR spoiler playthroughs): sphere 0 is what the start reaches
    /// empty-handed; sphere N is opened by the keys collected in spheres &lt; N. <see cref="Collected"/>
    /// lists the keys picked up <i>within</i> this sphere's reach (they open the next one).</summary>
    public sealed record SphereStep(
        int Index,
        IReadOnlyList<(int KeyItem, int RoomCode)> Collected,
        int RoomsReachable);

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
        // Flood over NodeCode identity (so a split room's sub-regions are distinct — entry-direction
        // partitions, REGION-SCHEMA-PLAN.md §2), but track and RETURN masked room codes so every caller —
        // requiresRoom, keysByRoom, goal test, Spot.RoomCode — sees rooms exactly as before. For an atomic
        // room NodeCode == Code, so `nodes`, `seen`, and `rooms` coincide → byte-identical.
        var byNode = graph.Nodes.ToDictionary(n => n.NodeCode);
        var seen = new HashSet<int> { start };               // NodeCodes (start room's primary node)
        var rooms = new HashSet<int> { start & 0xffff };     // masked room codes (requiresRoom + return)
        // Group-9 story latches (STATIC-SCD-RE.md cont.40): a type-2 door is free to cross, so its lock
        // is set once its source room is reachable; a type-1 reader door is passable only once its lock
        // is in this set. Recomputed from `seen` each fixpoint pass (monotonic, set-once), which is what
        // stops the door-rando "stranded shortcut" softlock the free-edge model could not see.
        var latches = new HashSet<int>();
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var nc in seen)
                if (byNode.TryGetValue(nc, out var n))
                    foreach (var e in n.Edges)
                        if (e.Door.SetsStoryLatch) latches.Add(e.Door.LockId);

            var queue = new Queue<int>(seen);
            while (queue.Count > 0)
            {
                if (!byNode.TryGetValue(queue.Dequeue(), out var node)) continue;
                foreach (var edge in node.Edges)
                {
                    if (seen.Contains(edge.Target.NodeCode)) continue;
                    if (!CanTraverse(game, edge, held, rooms, latches)) continue;
                    seen.Add(edge.Target.NodeCode);
                    rooms.Add(edge.Target.Code);
                    queue.Enqueue(edge.Target.NodeCode);
                    grew = true;
                }
            }
        }
        return rooms;
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
        // Keyed by NodeCode (unique across split-room sub-regions). Room-code lookups (`byCode[room]`)
        // resolve to the primary region (index 0, NodeCode == Code) — the room's item records live there.
        var byCode = graph.Nodes.ToDictionary(n => n.NodeCode);
        var held = new HashSet<int>();
        var log = new List<string>();

        // Assumed-fill to a fixpoint: collect every key reachable with the keys held so far, re-flood,
        // repeat. Unlike a goal-only check this keeps collecting *past* the goal, so the result can also
        // prove no door key was left stranded (BioRand's "every item reachable", not just the win).
        // Each round is one playthrough "sphere" (SphereStep) — recorded for the spoiler log.
        var spheres = new List<SphereStep>();
        HashSet<int> reach;
        while (true)
        {
            reach = Reachable(graph, game, start, held);
            var collected = new List<(int KeyItem, int RoomCode)>();
            foreach (var room in reach)
                if (keysByRoom.TryGetValue(room, out var keys))
                    foreach (var key in keys)
                        if (PickupReachable(byCode, room, key, held, reach) && held.Add(key))
                            collected.Add((key, room));
            // Record every productive round, the first round (baseline reach), and the terminal round
            // after progress (it shows what the last keys unlocked). A terminal round with no prior
            // progress would duplicate the baseline, so it is skipped.
            if (collected.Count > 0 || spheres.Count == 0 || spheres[^1].Collected.Count > 0)
                spheres.Add(new SphereStep(spheres.Count, collected, reach.Count));
            if (collected.Count == 0) break;
        }

        if (!reach.Contains(goal))
        {
            log.Add($"[progression] UNSOLVABLE: goal {goal:X4} unreachable; " +
                    $"reached {reach.Count} rooms; door keys held: {Fmt(held)}");
            return new PlacementResult(false, Array.Empty<(Spot, int)>(), log, spheres);
        }

        // Every KEY ITEM that exists in the playable world must be collectable; one stranded behind its
        // own (or a later) gate is a soft defect even when the goal does not depend on it — the
        // Entrance-Key-relocated-to-the-last-area class the key-shuffle preview exposed. Generalized from
        // door-TYPE keys to all key items (BioRand/AP's "every location reachable"; Phase 4 (i)), so a
        // non-door progression item stranded behind an authored edge gate is caught too.
        var stranded = StrandedKeyItems(graph, game, start, keysByRoom, held);
        if (stranded.Count > 0)
        {
            log.Add($"[progression] UNSOLVABLE: goal {goal:X4} reachable but key item(s) stranded " +
                    $"(in-world yet uncollectable): {Fmt(stranded)}");
            return new PlacementResult(false, Array.Empty<(Spot, int)>(), log, spheres);
        }

        log.Add($"[progression] goal {goal:X4} reachable; all key items collectable; held: {Fmt(held)}");
        return new PlacementResult(true, Array.Empty<(Spot, int)>(), log, spheres);
    }

    /// <summary>
    /// Key items that exist in the playable world (rooms reachable while holding <i>every</i> key item
    /// present) but were not <paramref name="collected"/> by the assumed-fill — i.e. every copy is
    /// stranded behind its own or a later gate. Generalized from door-TYPE keys to <b>all</b> key items
    /// (<see cref="GameDefinition.KeyItemIds"/>) so a non-door progression item stranded behind an
    /// authored edge/item gate is caught too, not only a <see cref="GameDefinition.KeyItemsForDoor"/> key
    /// (Phase 4 (i) — BioRand/AP's "every location reachable"). Items whose only copies sit in areas
    /// unreachable even with all keys (the Operation Wipe-Out duplicates, e.g. <c>0A03</c>) are excluded:
    /// they are out of the playable world and never required. Empty ⇒ every key item is obtainable.
    /// </summary>
    private static IReadOnlyList<int> StrandedKeyItems(RoomGraph graph, GameDefinition game, int start,
        IReadOnlyDictionary<int, IReadOnlyList<int>> keysByRoom, IReadOnlySet<int> collected)
    {
        // Every key item this placement actually seats somewhere (door + non-door), not just the ones a
        // door TYPE byte gates on — the full frame of "a key exists, so it must be obtainable".
        var present = new HashSet<int>();
        foreach (var keys in keysByRoom.Values)
            foreach (var k in keys)
                if (game.KeyItemIds.Contains(k)) present.Add(k);
        if (present.Count == 0) return Array.Empty<int>();

        var world = Reachable(graph, game, start, present);
        var inWorld = new HashSet<int>();
        foreach (var room in world)
            if (keysByRoom.TryGetValue(room, out var keys))
                foreach (var k in keys)
                    if (present.Contains(k)) inWorld.Add(k);

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
                                 Seed seed, IReadOnlySet<ItemRecord>? relocating = null)
    {
        // The greedy fill can dead-end (seat a key so the last spot lands behind its own gate), so
        // reroll with a fresh stream on failure — the DoorRandomizer retry pattern. Attempt 1 uses
        // the original "keyitems" stream, so every seed that already succeeded is byte-identical.
        const int maxAttempts = 20;
        PlacementResult result = null!;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var rng = seed.RngFor(attempt == 1 ? "keyitems" : $"keyitems-{attempt}");
            result = PlaceOnce(graph, game, start, goal, spots, keyItems, rng, relocating);
            if (result.Success) return result;
        }
        return result;
    }

    private static PlacementResult PlaceOnce(RoomGraph graph, GameDefinition game, int start, int goal,
                                             IReadOnlyList<Spot> spots, IReadOnlyCollection<int> keyItems,
                                             Random rng, IReadOnlySet<ItemRecord>? relocating)
    {
        // Symmetric with Verify (KEY-ITEM-RANDO-RESEARCH.md §3(b)): the placer does NOT assume the
        // non-relocated progression items held from t=0. It COLLECTS them the way Verify does — each fixed
        // key item enters `held` only once its room is reachable and its pickup guard is satisfied — so
        // `held` grows identically in Place and Verify, and a layout Place accepts, Verify accepts too.
        // The records this call is itself relocating are excluded (their id is moving, not staying put).
        // Keyed by NodeCode; a masked room code from `reach` resolves to the primary region (index 0,
        // NodeCode == Code), where the room's item records live — same convention as Verify/PickupReachable.
        var byNode = graph.Nodes.ToDictionary(n => n.NodeCode);
        var held = new HashSet<int>();
        var remaining = new List<int>(keyItems);
        var placements = new List<(Spot, int)>();
        var usedSpots = new HashSet<Spot>();
        var log = new List<string>();

        while (true)
        {
            // Assumed-fill sub-fixpoint: expand reach and collect every reachable, guard-clear fixed key
            // item, until neither grows (BioRand's Search/_haveItems discipline). Without this, gating a
            // non-relocated item held-from-t=0 lets the fill cross a gate it has not honestly earned and
            // seat a key Verify then can't reach (the §8l Place↔Verify divergence).
            HashSet<int> reach;
            while (true)
            {
                reach = Reachable(graph, game, start, held);
                bool collected = false;
                foreach (var room in reach)
                    if (byNode.TryGetValue(room, out var node))
                        foreach (var ni in node.Items)
                            if ((relocating is null || !relocating.Contains(ni.Record))
                                && game.KeyItemIds.Contains(ni.Record.ItemId)
                                && ni.Requires.SatisfiedBy(held, reach)
                                && held.Add(ni.Record.ItemId))
                                collected = true;
                if (!collected) break;
            }

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
        // Keyed by NodeCode (unique across split-room sub-regions). Room-code lookups (`byCode[room]`)
        // resolve to the primary region (index 0, NodeCode == Code) — the room's item records live there.
        var byCode = graph.Nodes.ToDictionary(n => n.NodeCode);
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
