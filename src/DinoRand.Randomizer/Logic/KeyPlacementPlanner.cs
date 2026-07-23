using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;

namespace DinoRand.Randomizer.Logic;

/// <summary>Pure authoritative discovery and planning for DC1 progression-key relocation.</summary>
internal static class KeyPlacementPlanner
{
    internal sealed record Policy(
        IReadOnlyList<KeyItemPlacer.Spot> Spots,
        IReadOnlyList<int> Keys,
        IReadOnlyDictionary<ItemRecord, IReadOnlyList<ItemRecord>> Siblings,
        IReadOnlyList<ItemRecord> ScatterRecords,
        IReadOnlyDictionary<ItemRecord, PickupVisual> SpotVisuals,
        IReadOnlyDictionary<ItemRecord, PickupLocationContract> Locations,
        IReadOnlySet<ItemRecord> Relocating);

    internal sealed record Edit(
        PickupLocationContract Location,
        ItemRecord Record,
        int BeforeItemId,
        int BeforeAmount,
        int ItemId,
        int Amount)
    {
        internal bool Changed => BeforeItemId != ItemId || BeforeAmount != Amount;
    }

    internal sealed record PlanResult(
        bool Success,
        IReadOnlyList<Edit> Edits,
        KeyItemPlacer.PlacementResult? Placement,
        IReadOnlyList<string> Log,
        Policy? Policy);

    internal static Policy BuildPolicy(RandomizationContext context, RoomGraph graph, GameDefinition game)
    {
        var relocatingKeys = new HashSet<int>();
        foreach (var node in graph.Nodes)
            foreach (var edge in node.Edges)
            {
                foreach (var key in game.KeyItemsForDoor(edge.Door.DoorType))
                    relocatingKeys.Add(key);
                if (context.Config.RelocateDdkDiscs)
                    foreach (var key in edge.Requires.Items ?? Array.Empty<int>())
                        if (game.OverlayRelocationKeyIds.Contains(key)) relocatingKeys.Add(key);
            }

        var playableKeys = PlayableKeyEntitlements(graph, game);
        var reachable = KeyItemPlacer.Reachable(graph, game, game.StartRoomCode, playableKeys);

        var spots = new List<KeyItemPlacer.Spot>();
        var keys = new List<int>();
        var canonical = new Dictionary<(int Room, string Link), ItemRecord>();
        var siblings = new Dictionary<ItemRecord, List<ItemRecord>>();
        var visuals = new Dictionary<ItemRecord, PickupVisual>();
        var hiddenKeys = new HashSet<int>();
        var hiddenSpotIndexes = new List<int>();
        var locations = graph.Nodes.SelectMany(node => node.Items)
            .ToDictionary(item => item.Record, item => item.Location);

        foreach (var node in graph.Nodes)
        {
            if (!reachable.Contains(node.Code) || game.EndingZoneRoomCodes.Contains(node.Code)) continue;
            foreach (var item in node.Items)
            {
                var record = item.Record;
                if (record.IsEmptySlot || !relocatingKeys.Contains(record.ItemId)
                    || item.Priority == ItemPriority.Fixed) continue;
                if (item.Link is { } link)
                {
                    var group = (node.Code, link);
                    if (canonical.TryGetValue(group, out var first))
                    {
                        siblings[first].Add(record);
                        continue;
                    }
                    canonical[group] = record;
                    siblings[record] = new List<ItemRecord>();
                }
                visuals[record] = item.Visual;
                if (context.Config.AvoidHiddenPickupSpots && item.Visual == PickupVisual.InteractionOnly)
                {
                    hiddenKeys.Add(record.ItemId);
                    hiddenSpotIndexes.Add(spots.Count);
                }
                spots.Add(new KeyItemPlacer.Spot(node.Code, record, item.Requires));
                keys.Add(record.ItemId);
            }
        }

        var scatter = new List<ItemRecord>();
        if (context.Config.ShuffleKeyItemsIntoPickups)
            foreach (var node in graph.Nodes)
            {
                if (!reachable.Contains(node.Code) || game.EndingZoneRoomCodes.Contains(node.Code)) continue;
                foreach (var item in node.Items)
                {
                    var record = item.Record;
                    if (!item.IsScatterTarget || record.IsEmptySlot || game.KeyItemIds.Contains(record.ItemId))
                        continue;
                    visuals[record] = item.Visual;
                    if (context.Config.AvoidHiddenPickupSpots && item.Visual == PickupVisual.InteractionOnly)
                        hiddenSpotIndexes.Add(spots.Count);
                    spots.Add(new KeyItemPlacer.Spot(node.Code, record, item.Requires));
                    scatter.Add(record);
                }
            }

        foreach (int index in hiddenSpotIndexes)
            spots[index] = spots[index] with { EligibleKeys = hiddenKeys };

        var relocating = spots.Select(spot => spot.Record).ToHashSet();
        foreach (var group in siblings.Values)
            foreach (var sibling in group)
                relocating.Add(sibling);

        return new Policy(
            spots,
            keys,
            siblings.ToDictionary(x => x.Key, x => (IReadOnlyList<ItemRecord>)x.Value),
            scatter,
            visuals,
            locations,
            relocating);
    }

    internal static IReadOnlySet<int> PlayableKeyEntitlements(RoomGraph graph, GameDefinition game) =>
        graph.Nodes.SelectMany(node => node.Items)
            .Select(item => item.Record)
            .Where(record => !record.IsEmptySlot && game.KeyItemIds.Contains(record.ItemId))
            .Select(record => record.ItemId).ToHashSet();

    internal static PlanResult Plan(RandomizationContext context, RoomGraph graph, GameDefinition game)
    {
        var policy = BuildPolicy(context, graph, game);
        if (policy.Keys.Count == 0)
        {
            var currentCheck = KeyItemPlacer.Verify(graph, game, game.StartRoomCode, game.GoalRoomCode,
                KeysByRoom(graph, game, new Dictionary<ItemRecord, Edit>()));
            return new PlanResult(currentCheck.Success, Array.Empty<Edit>(), currentCheck,
                currentCheck.Log, policy);
        }

        var placement = new KeyItemPlacer().Place(
            graph, game, game.StartRoomCode, game.GoalRoomCode,
            policy.Spots, policy.Keys, context.Seed, policy.Relocating);
        if (!placement.Success)
            return new PlanResult(false, Array.Empty<Edit>(), placement, placement.Log, policy);

        var edits = new Dictionary<ItemRecord, Edit>();
        Edit MakeEdit(ItemRecord record, int itemId, int amount) => new(
            policy.Locations[record], record, record.ItemId, record.Amount, itemId, amount);
        foreach (var (spot, key) in placement.Placements)
            edits[spot.Record] = MakeEdit(spot.Record, key, Math.Max(1, spot.Record.Amount));

        var placedRecords = placement.Placements.Select(x => x.Spot.Record)
            .ToHashSet();
        var scatterSet = policy.ScatterRecords.ToHashSet();
        var displaced = policy.ScatterRecords.Where(placedRecords.Contains)
            .Select(record => (record.ItemId, Amount: Math.Max(1, record.Amount))).ToList();
        var vacated = policy.Spots.Select(spot => spot.Record)
            .Where(record => !scatterSet.Contains(record) && !placedRecords.Contains(record))
            .Distinct().ToList();
        for (int index = 0; index < Math.Min(vacated.Count, displaced.Count); index++)
            edits[vacated[index]] = MakeEdit(vacated[index], displaced[index].ItemId,
                                             displaced[index].Amount);

        foreach (var (canonical, group) in policy.Siblings)
        {
            var canonicalEdit = edits.GetValueOrDefault(canonical)
                ?? MakeEdit(canonical, canonical.ItemId, canonical.Amount);
            foreach (var sibling in group)
                edits[sibling] = MakeEdit(sibling, canonicalEdit.ItemId, Math.Max(1, canonicalEdit.Amount));
        }

        var verification = KeyItemPlacer.Verify(graph, game, game.StartRoomCode, game.GoalRoomCode,
            KeysByRoom(graph, game, edits));
        if (!verification.Success)
            return new PlanResult(false, Array.Empty<Edit>(), placement,
                placement.Log.Concat(verification.Log).ToArray(), policy);
        return new PlanResult(true, edits.Values.ToArray(), placement,
            placement.Log.Concat(verification.Log).ToArray(), policy);
    }

    internal static void Apply(PlanResult plan)
    {
        if (!plan.Success) throw new InvalidOperationException("cannot apply a rejected key-placement plan");
        foreach (var edit in plan.Edits)
        {
            edit.Record.ItemId = edit.ItemId;
            edit.Record.Amount = edit.Amount;
        }
    }

    internal static Dictionary<int, IReadOnlyList<int>> KeysByRoom(
        RoomGraph graph, GameDefinition game, IReadOnlyDictionary<ItemRecord, Edit> edits)
    {
        var map = new Dictionary<int, List<int>>();
        foreach (var node in graph.Nodes)
            foreach (var item in node.Items)
            {
                var record = item.Record;
                int id = edits.TryGetValue(record, out var edit) ? edit.ItemId : record.ItemId;
                if (record.IsEmptySlot || !game.KeyItemIds.Contains(id)) continue;
                (map.TryGetValue(node.Code, out var list) ? list : map[node.Code] = new()).Add(id);
            }
        return map.ToDictionary(x => x.Key, x => (IReadOnlyList<int>)x.Value);
    }
}
