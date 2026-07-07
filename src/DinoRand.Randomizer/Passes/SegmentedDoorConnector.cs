using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Maps;

namespace DinoRand.Randomizer.Passes;

/// <summary>
/// Re-pairs a world's reciprocal doors into a fresh, connected layout — an adaptation of BioRand's
/// segmented connector (<c>DoorRandomiser.CreateRandomGraph</c> /
/// ref/classic/IntelOrca.Biohazard.BioRand/DoorRandomiser.cs:114-180) to Dino Crisis 1's data model.
///
/// <para><b>Model.</b> A <see cref="FreeEnd"/> is one re-pointable door record (one side of a
/// reciprocal pair). Connecting end <c>a</c> (in room A) to end <c>b</c> (in room B) is BioRand's
/// reciprocal <c>ConnectDoor</c>: A's door now leads to B and B's door now leads to A
/// (plan §4.1). The connector keeps the multiset of free ends and only re-matches them, so every
/// shuffled door stays two-way.</para>
///
/// <para><b>Algorithm.</b> Grow a random spanning tree from <see cref="DoorMap.StartCode"/>, each
/// step joining a not-yet-reached room by matching one of its free ends to a free end of an
/// already-reached room — biased to branch through <see cref="RoomCategory.Bridge"/> hubs and to
/// reach a <see cref="RoomCategory.Box"/> supply room early (BioRand's bridge/box notions). Any
/// remaining free ends are then paired at random (only ever adding edges, so reachability cannot
/// drop) and a lone odd end loops back to its own room (BioRand's <c>FinalChecks</c> self-lock).
/// Success requires <see cref="DoorMap.EndCode"/> to be reachable from start over the new pairing;
/// otherwise the caller retries with a fresh sub-seed or falls back to vanilla (plan §5, §8).</para>
///
/// <para>This is topology only — it never reads or writes the (undecoded) entry pose. The door
/// pass applies the pose carry separately, behind the HARD GATE.</para>
/// </summary>
public sealed class SegmentedDoorConnector
{
    /// <summary>One re-pointable door: the room it sits in and the record to rewrite.</summary>
    public sealed record FreeEnd(int RoomCode, DoorRecord Door);

    /// <summary>A new reciprocal connection: A's door → B's room and B's door → A's room.</summary>
    public sealed record Pairing(FreeEnd A, FreeEnd B);

    public sealed record Result(
        bool Success,
        IReadOnlyList<Pairing> Pairings,
        IReadOnlyList<FreeEnd> SelfLoops,
        IReadOnlyList<string> Log);

    /// <summary>
    /// Collect the in-scope, reciprocal, non-static free ends of <paramref name="graph"/> under
    /// <paramref name="map"/>. A directed door qualifies only when both its endpoints are mapped
    /// (non-<see cref="RoomCategory.Exclude"/>), the edge is not tagged static (one-way/scripted),
    /// <b>and</b> a qualifying reverse door exists — so the result is reciprocally closed.
    /// </summary>
    public static IReadOnlyList<FreeEnd> CollectFreeEnds(RoomGraph graph, DoorMap map)
    {
        bool InScope(int code) => map.Contains(code) && map.CategoryOf(code) != RoomCategory.Exclude;

        // First pass: every directed door that is in-scope on both ends and not static.
        bool Qualifies(int from, int to)
            => InScope(from) && InScope(to) && !map.IsStaticEdge(from, to);

        var ends = new List<FreeEnd>();
        foreach (var node in graph.Nodes)
        {
            int from = node.Code;
            if (!InScope(from)) continue;
            foreach (var edge in node.Edges)
            {
                int to = edge.Target.Code;
                if (!Qualifies(from, to)) continue;
                // Reciprocal: the destination must have a qualifying door back to us.
                bool reciprocal = edge.Target.Edges.Any(e => e.Target.Code == from && Qualifies(to, from));
                if (reciprocal)
                    ends.Add(new FreeEnd(from, edge.Door));
            }
        }
        return ends;
    }

    /// <summary>Produce a connected re-pairing, or a failed result if the end room cannot be reached.</summary>
    public Result Connect(RoomGraph graph, DoorMap map, Random rng)
    {
        var log = new List<string>();
        var byRoom = new Dictionary<int, List<FreeEnd>>();
        foreach (var end in CollectFreeEnds(graph, map))
            (byRoom.TryGetValue(end.RoomCode, out var l) ? l : byRoom[end.RoomCode] = new()).Add(end);

        int totalEnds = byRoom.Values.Sum(l => l.Count);
        log.Add($"[doors] {totalEnds} reciprocal free ends across {byRoom.Count} in-scope rooms");

        if (!byRoom.ContainsKey(map.StartCode))
            return Fail(log, $"start room {map.StartCode:X4} has no reciprocal door to connect");
        if (!byRoom.ContainsKey(map.EndCode))
            return Fail(log, $"end room {map.EndCode:X4} has no reciprocal door to connect");

        var pairings = new List<Pairing>();
        var inTree = new HashSet<int> { map.StartCode };
        bool boxReached = map.CategoryOf(map.StartCode) == RoomCategory.Box;

        // --- Spanning growth: join one new room per step until none can be reached. ---
        while (true)
        {
            var srcRooms = inTree.Where(r => Count(byRoom, r) > 0).ToList();
            var dstRooms = byRoom.Keys.Where(r => !inTree.Contains(r) && byRoom[r].Count > 0).ToList();
            if (srcRooms.Count == 0 || dstRooms.Count == 0) break;

            int dst = ChooseDestination(dstRooms, map, boxReached, rng);
            int src = ChooseSource(srcRooms, map, rng);

            pairings.Add(new Pairing(Pop(byRoom, src, rng), Pop(byRoom, dst, rng)));
            inTree.Add(dst);
            boxReached |= map.CategoryOf(dst) == RoomCategory.Box;
        }

        // --- Pair every leftover end at random (adds edges only; reachability can't drop). ---
        var leftover = byRoom.Values.SelectMany(l => l).OrderBy(_ => rng.Next()).ToList();
        var selfLoops = new List<FreeEnd>();
        for (int i = 0; i + 1 < leftover.Count; i += 2)
            pairings.Add(new Pairing(leftover[i], leftover[i + 1]));
        if (leftover.Count % 2 == 1)
            selfLoops.Add(leftover[^1]); // odd end loops back to its own room (BioRand FinalChecks)

        // --- Verify the end room is reachable from start over the new (undirected) pairing. ---
        bool reachable = EndReachable(pairings, map.StartCode, map.EndCode);
        log.Add($"[doors] connected {pairings.Count} reciprocal pairs, {selfLoops.Count} self-loops; " +
                $"end {map.EndCode:X4} {(reachable ? "reachable" : "UNREACHABLE")}");

        return new Result(reachable, pairings, selfLoops, log);
    }

    private static int ChooseDestination(List<int> dstRooms, DoorMap map, bool boxReached, Random rng)
    {
        // Reach a Box (supply) room early — BioRand prioritises box rooms per segment.
        if (!boxReached)
        {
            var boxes = dstRooms.Where(r => map.CategoryOf(r) == RoomCategory.Box).ToList();
            if (boxes.Count > 0) return boxes[rng.Next(boxes.Count)];
        }
        return dstRooms[rng.Next(dstRooms.Count)];
    }

    private static int ChooseSource(List<int> srcRooms, DoorMap map, Random rng)
    {
        // Branch through bridge hubs when one is available (keeps the frontier rich).
        var bridges = srcRooms.Where(r => map.CategoryOf(r) == RoomCategory.Bridge).ToList();
        var pool = bridges.Count > 0 && rng.Next(2) == 0 ? bridges : srcRooms;
        return pool[rng.Next(pool.Count)];
    }

    private static int Count(Dictionary<int, List<FreeEnd>> byRoom, int room)
        => byRoom.TryGetValue(room, out var l) ? l.Count : 0;

    private static FreeEnd Pop(Dictionary<int, List<FreeEnd>> byRoom, int room, Random rng)
    {
        var l = byRoom[room];
        int i = rng.Next(l.Count);
        var end = l[i];
        l.RemoveAt(i);
        return end;
    }

    private static bool EndReachable(IReadOnlyList<Pairing> pairings, int start, int end)
    {
        var adj = new Dictionary<int, List<int>>();
        void Add(int a, int b) => (adj.TryGetValue(a, out var l) ? l : adj[a] = new()).Add(b);
        foreach (var p in pairings)
        {
            Add(p.A.RoomCode, p.B.RoomCode); // door in A now leads to B
            Add(p.B.RoomCode, p.A.RoomCode); // and reciprocally
        }
        var seen = new HashSet<int> { start };
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (cur == end) return true;
            if (!adj.TryGetValue(cur, out var neighbours)) continue;
            foreach (var n in neighbours)
                if (seen.Add(n)) queue.Enqueue(n);
        }
        return seen.Contains(end);
    }

    private static Result Fail(List<string> log, string reason)
    {
        log.Add($"[doors] cannot shuffle: {reason}");
        return new Result(false, Array.Empty<Pairing>(), Array.Empty<FreeEnd>(), log);
    }
}
