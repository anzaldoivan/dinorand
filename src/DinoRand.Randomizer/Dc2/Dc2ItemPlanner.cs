using DinoRand.FileFormats.Stage.Dc2;

namespace DinoRand.Randomizer.Dc2;

/// <summary>Deterministic, class-conserving placement and reachability proof for the 42-site DC2 v2 graph.</summary>
public static class Dc2ItemPlanner
{
    public const string RngNamespace = "DC2 Items v2";

    public static Dc2ItemPlan Plan(
        Dc2ItemData data,
        Seed seed,
        bool randomizeHealth,
        bool shuffleKeys)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(seed);
        var rng = seed.RngFor(RngNamespace);
        var assigned = data.Locations.ToDictionary(x => x.SourceId, x => x.ItemId, StringComparer.Ordinal);

        if (shuffleKeys)
            PlaceGenericKeys(data, assigned, rng);
        if (randomizeHealth)
            ShuffleClass(data, assigned, Dc2ItemEditor.ItemRewriteClass.Health, rng);

        var placements = data.Locations.Select(location => new Dc2ItemPlacement(
            location.SourceId,
            location.ApId,
            location.RoomId,
            location.Name,
            location.ItemId,
            assigned[location.SourceId],
            data.Catalog[assigned[location.SourceId]],
            location.RewriteClass,
            location.Site)).ToArray();

        ValidateMultisets(data, placements);
        ReachabilityResult proof = Prove(data, placements);
        if (!proof.IsBeatable)
            throw new InvalidOperationException($"DC2 item placement is not provably beatable: {proof.Diagnostics}");
        return new Dc2ItemPlan(placements, true, proof.ReachableRooms, proof.Spheres, proof.Diagnostics);
    }

    private static void PlaceGenericKeys(
        Dc2ItemData data,
        Dictionary<string, int> assigned,
        Random rng)
    {
        var sites = data.Locations
            .Where(x => x.RewriteClass == Dc2ItemEditor.ItemRewriteClass.GenericKey
                        && !data.FixedLifecycleItemIds.Contains(x.ItemId)).ToList();
        var progression = sites.Where(x => data.ProgressionItemIds.Contains(x.ItemId)).ToList();
        if (progression.Count != data.ProgressionItemIds.Count)
            throw new InvalidOperationException("DC2 progression pool and generic-key locations disagree");

        var held = new HashSet<int>();
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (Dc2ItemLocation item in progression.OrderBy(x => x.ItemId))
        {
            HashSet<string> rooms = ReachableRooms(data, held);
            var candidates = sites.Where(location => !reserved.Contains(location.SourceId)
                                                    && LocationAvailable(location, rooms, held)).ToList();
            if (candidates.Count == 0)
                throw new InvalidOperationException($"no reachable generic-key site can seat progression item 0x{item.ItemId:x2}");
            Dc2ItemLocation selected = candidates[rng.Next(candidates.Count)];
            assigned[selected.SourceId] = item.ItemId;
            reserved.Add(selected.SourceId);
            held.Add(item.ItemId);
        }

        var remainingItems = sites.Select(x => x.ItemId)
            .Where(id => !data.ProgressionItemIds.Contains(id)).ToList();
        Shuffle(remainingItems, rng);
        var remainingSites = sites.Where(x => !reserved.Contains(x.SourceId)).ToList();
        Shuffle(remainingSites, rng);
        if (remainingItems.Count != remainingSites.Count)
            throw new InvalidOperationException("DC2 generic-key multiset does not balance after forward fill");
        for (int i = 0; i < remainingSites.Count; i++)
            assigned[remainingSites[i].SourceId] = remainingItems[i];
    }

    private static void ShuffleClass(
        Dc2ItemData data,
        Dictionary<string, int> assigned,
        Dc2ItemEditor.ItemRewriteClass rewriteClass,
        Random rng)
    {
        var sites = data.Locations.Where(x => x.RewriteClass == rewriteClass).ToList();
        var items = sites.Select(x => x.ItemId).ToList();
        Shuffle(items, rng);
        for (int i = 0; i < sites.Count; i++)
            assigned[sites[i].SourceId] = items[i];
    }

    private static ReachabilityResult Prove(Dc2ItemData data, IReadOnlyList<Dc2ItemPlacement> placements)
    {
        var placementBySource = placements.ToDictionary(x => x.SourceId, StringComparer.Ordinal);
        var held = new HashSet<int>();
        var collected = new HashSet<string>(StringComparer.Ordinal);
        var spheres = new List<Dc2ReachabilitySphere>();

        while (true)
        {
            HashSet<string> rooms = ReachableRooms(data, held);
            var newlyReachable = data.Locations.Where(location => !collected.Contains(location.SourceId)
                                                              && LocationAvailable(location, rooms, held)).ToArray();
            if (newlyReachable.Length == 0)
            {
                bool beatable = rooms.Contains(data.GoalRoomId) && collected.Count == data.Locations.Count;
                string diagnostic = beatable
                    ? $"proved {collected.Count}/{data.Locations.Count} locations and {data.GoalRoomId} reachable in {spheres.Count} sphere(s)"
                    : $"reached {collected.Count}/{data.Locations.Count} locations; goal {data.GoalRoomId} reachable={rooms.Contains(data.GoalRoomId)}";
                return new ReachabilityResult(beatable, rooms.Order(StringComparer.Ordinal).ToArray(), spheres, diagnostic);
            }

            var gained = new HashSet<int>();
            foreach (Dc2ItemLocation location in newlyReachable)
            {
                collected.Add(location.SourceId);
                int itemId = placementBySource[location.SourceId].ItemId;
                if (data.ProgressionItemIds.Contains(itemId) && held.Add(itemId))
                    gained.Add(itemId);
            }
            spheres.Add(new Dc2ReachabilitySphere(
                spheres.Count,
                newlyReachable.Select(x => x.SourceId).Order(StringComparer.Ordinal).ToArray(),
                gained.Order().ToArray()));

            if (gained.Count == 0)
            {
                HashSet<string> finalRooms = ReachableRooms(data, held);
                bool beatable = finalRooms.Contains(data.GoalRoomId) && collected.Count == data.Locations.Count;
                string diagnostic = beatable
                    ? $"proved {collected.Count}/{data.Locations.Count} locations and {data.GoalRoomId} reachable in {spheres.Count} sphere(s)"
                    : $"reached {collected.Count}/{data.Locations.Count} locations; goal {data.GoalRoomId} reachable={finalRooms.Contains(data.GoalRoomId)}";
                return new ReachabilityResult(beatable, finalRooms.Order(StringComparer.Ordinal).ToArray(), spheres, diagnostic);
            }
        }
    }

    private static HashSet<string> ReachableRooms(Dc2ItemData data, IReadOnlySet<int> held)
    {
        var rooms = new HashSet<string>(StringComparer.Ordinal) { data.StartRoomId };
        bool changed;
        do
        {
            changed = false;
            foreach (Dc2LogicEdge edge in data.Edges)
            {
                if (!rooms.Contains(edge.FromRoomId)
                    || !edge.RequiredItems.All(held.Contains)
                    || !edge.RequiredRooms.All(rooms.Contains))
                    continue;
                changed |= rooms.Add(edge.ToRoomId);
            }
        } while (changed);
        return rooms;
    }

    private static bool LocationAvailable(
        Dc2ItemLocation location,
        IReadOnlySet<string> rooms,
        IReadOnlySet<int> held)
        => rooms.Contains(location.RoomId)
           && location.RequiredRooms.All(rooms.Contains)
           && location.RequiredItems.All(held.Contains);

    private static void ValidateMultisets(Dc2ItemData data, IReadOnlyList<Dc2ItemPlacement> placements)
    {
        foreach (var group in data.Locations.GroupBy(x => x.RewriteClass))
        {
            int[] expected = group.Select(x => x.ItemId).Order().ToArray();
            int[] actual = placements.Where(x => x.RewriteClass == group.Key).Select(x => x.ItemId).Order().ToArray();
            if (!expected.SequenceEqual(actual))
                throw new InvalidOperationException($"DC2 {group.Key} placement changed its item multiset");
        }
    }

    private static void Shuffle<T>(IList<T> values, Random rng)
    {
        for (int i = values.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private sealed record ReachabilityResult(
        bool IsBeatable,
        IReadOnlyList<string> ReachableRooms,
        IReadOnlyList<Dc2ReachabilitySphere> Spheres,
        string Diagnostics);
}

public sealed record Dc2ItemPlacement(
    string SourceId,
    long ApId,
    string RoomId,
    string LocationName,
    int OriginalItemId,
    int ItemId,
    string ItemName,
    Dc2ItemEditor.ItemRewriteClass RewriteClass,
    Dc2ItemEditor.ItemSiteSpec Site);

public sealed record Dc2ReachabilitySphere(
    int Index,
    IReadOnlyList<string> SourceIds,
    IReadOnlyList<int> ProgressionItemsGained);

public sealed record Dc2ItemPlan(
    IReadOnlyList<Dc2ItemPlacement> Placements,
    bool IsBeatable,
    IReadOnlyList<string> ReachableRooms,
    IReadOnlyList<Dc2ReachabilitySphere> Spheres,
    string Diagnostics);
