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

    /// <summary>Number of Plug pickups sitting in <paramref name="reachable"/> rooms (each non-empty Plug
    /// record is one plug). 0 when the game has no plug mechanic.</summary>
    public static int ReachablePlugSupply(RoomGraph graph, GameDefinition game, IReadOnlySet<int> reachable)
    {
        if (game.PlugItemId is not { } plug) return 0;
        int supply = 0;
        foreach (var node in graph.Nodes)
        {
            if (!reachable.Contains(node.Code)) continue;
            foreach (var ni in node.Items)
                if (!ni.Record.IsEmptySlot && ni.Record.ItemId == plug)
                    supply++;
        }
        return supply;
    }

    /// <summary>Total plug cost of the emergency boxes whose room is in <paramref name="reachable"/>.</summary>
    public static int ReachableBoxDemand(GameDefinition game, IReadOnlySet<int> reachable)
    {
        int demand = 0;
        foreach (var box in game.EmergencyBoxes)
            if (reachable.Contains(box.RoomCode))
                demand += box.PlugCost;
        return demand;
    }

    /// <summary>The plug supply-vs-demand balance over the <paramref name="reachable"/> world.</summary>
    public static Balance Evaluate(RoomGraph graph, GameDefinition game, IReadOnlySet<int> reachable)
        => new(ReachablePlugSupply(graph, game, reachable), ReachableBoxDemand(game, reachable));
}
