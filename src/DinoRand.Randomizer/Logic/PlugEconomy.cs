using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;

namespace DinoRand.Randomizer.Logic;

/// <summary>
/// The Plug economy for emergency boxes (docs/decisions/cross/ITEM-RANDO-PLAN.md §7.4). Boxes are optional storage gated by
/// Plugs (<see cref="GameDefinition.PlugItemId"/>), not progression gates — so they never affect
/// beatability. This computes the <i>supply</i> (Plug pickups the player can reach) against the
/// <i>demand</i> (plugs needed to open the boxes the player can reach), so the randomizer can surface
/// when a seed would leave reachable boxes unopenable. Plugs are key items, so the item pass already
/// conserves them; this is the visibility/guard layer over that invariant.
/// </summary>
public static class PlugEconomy
{
    public readonly record struct Balance(int Supply, int Demand)
    {
        /// <summary>True when every reachable box can be opened with the reachable plug supply.</summary>
        public bool MeetsDemand => Supply >= Demand;
    }

    /// <summary>Number of plugs the player can pick up in <paramref name="reachable"/> rooms (see
    /// <see cref="PlugSupply"/>). 0 when the game has no plug mechanic.</summary>
    public static int ReachablePlugSupply(RoomGraph graph, GameDefinition game, IReadOnlySet<int> reachable)
    {
        if (game.PlugItemId is not { } plug) return 0;
        return PlugSupply(graph.Nodes.Where(n => reachable.Contains(n.Code))
                                     .SelectMany(n => n.Items).Select(ni => ni.Record), plug);
    }

    /// <summary>Plugs that can be picked up from <paramref name="records"/>: one per take flag
    /// (<see cref="ItemRecord.TakeIndex"/>), since taking one record of a flag suppresses the others. Some
    /// rooms place one pickup per camera angle (060D, 0610) and stage A copies repeat stage 1's plugs; a
    /// record with take index 0 respawns and counts once.</summary>
    public static int PlugSupply(IEnumerable<ItemRecord> records, int plugItemId)
    {
        var flags = new HashSet<int>();
        int untracked = 0;
        foreach (var r in records)
        {
            if (r.IsEmptySlot || r.ItemId != plugItemId) continue;
            if (r.TakeIndex == 0) untracked++;
            else flags.Add(r.TakeIndex);
        }
        return flags.Count + untracked;
    }

    /// <summary>Total plug cost of the emergency boxes whose room is in <paramref name="reachable"/>, one per box
    /// location. <paramref name="costs"/> (from <see cref="BoxCosts"/>) gives each location's current cost by its
    /// vanilla box id; a location it does not list keeps its catalogue cost.</summary>
    public static int ReachableBoxDemand(GameDefinition game, IReadOnlySet<int> reachable,
                                         IReadOnlyDictionary<int, int>? costs = null)
    {
        int demand = 0;
        foreach (var box in game.EmergencyBoxes)
            if (reachable.Contains(box.RoomCode))
                demand += costs is not null && costs.TryGetValue(box.BoxId, out int c) ? c : box.PlugCost;
        return demand;
    }

    /// <summary>Each box location's current plug cost, keyed by its vanilla box id, read from the room scripts'
    /// box records. A stage A/B/C copy of a box has the same id and cost, so it is counted once.</summary>
    public static Dictionary<int, int> BoxCosts(IEnumerable<RoomFile> rooms)
    {
        var costs = new Dictionary<int, int>();
        foreach (var room in rooms.Where(r => r.ParsedCleanly))
            foreach (var box in room.Script!.EmergencyBoxes)
                costs.TryAdd(box.OriginalBoxId, box.PlugCost);
        return costs;
    }

    /// <summary>The plug supply-vs-demand balance over the <paramref name="reachable"/> world.</summary>
    public static Balance Evaluate(RoomGraph graph, GameDefinition game, IReadOnlySet<int> reachable,
                                   IReadOnlyDictionary<int, int>? costs = null)
        => new(ReachablePlugSupply(graph, game, reachable), ReachableBoxDemand(game, reachable, costs));
}
