using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;

namespace DinoRand.Randomizer.Passes;

/// <summary>
/// Phase 3. Progression logic on the real door graph (<see cref="KeyItemPlacer"/>). When
/// <see cref="RandomizerConfig.ShuffleKeyItems"/> is on it relocates the door-gating key items into
/// new, progression-safe spots; either way it then proves the goal room is still reachable and logs
/// the result. Runs first so a later item/enemy shuffle builds on a beatable, key-settled baseline.
/// </summary>
public sealed class ProgressionPass : IRandomizationPass
{
    public string Name => "progression";

    private const string KeySpoilerTitle = "Key items (DC1)";
    private static readonly string[] KeySpoilerColumns = { "Room", "Vanilla key there", "New key there" };

    public bool IsEnabled(RandomizerConfig config) => config.EnsureBeatable || config.ShuffleKeyItems;

    public void Apply(RandomizationContext context)
    {
        var game = context.Game;
        var graph = context.Graph;

        if (context.Config.ShuffleKeyItems)
            ShuffleDoorKeys(context, graph, game);

        // Validate the (possibly shuffled) layout: collect where every key now sits and verify the
        // goal stays reachable under the door-graph key logic.
        var result = KeyItemPlacer.Verify(graph, game, game.StartRoomCode, game.GoalRoomCode,
                                          KeysByRoom(graph, game));
        foreach (var line in result.Log) context.Log(line);
        if (!result.Success)
            context.Log("[progression] WARNING: seed is not provably beatable under door-graph logic");

        // The key-item section exists only when the shuffle ran (dynamic tables, SPOILER-LOG-PLAN.md
        // §4); the beatability verdict rides along as a note.
        if (context.Config.ShuffleKeyItems)
            context.Spoiler.Section(KeySpoilerTitle, KeySpoilerColumns)
                .AddNote(result.Success
                    ? "seed verified beatable under door-graph key logic"
                    : "WARNING: seed is not provably beatable under door-graph logic");

        LogPlugEconomy(context, graph, game);
    }

    /// <summary>
    /// Report the Plug economy for emergency boxes (§7.4): the reachable plug supply vs the plugs needed
    /// to open every reachable box. Boxes are optional storage (never progression), so this never fails a
    /// seed — it only surfaces when a layout would leave reachable boxes unopenable. In vanilla the player
    /// cannot open every box anyway (the FAQ notes one of the final three is story-locked), so supply &lt;
    /// demand is expected; the value is in spotting a <i>regression</i> (e.g. a future door shuffle
    /// stranding plugs) against this baseline. Silent when the game has no plug mechanic.
    /// </summary>
    private static void LogPlugEconomy(RandomizationContext context, RoomGraph graph, GameDefinition game)
    {
        if (game.PlugItemId is null || game.EmergencyBoxes.Count == 0) return;

        // Full-key reachability = the playable world (excludes demo / Operation-Wipe-Out copies), the same
        // frame the door-key reachability uses, so the plug supply/demand are scoped to the real run.
        var doorKeys = new HashSet<int>();
        foreach (var node in graph.Nodes)
            foreach (var edge in node.Edges)
                foreach (var k in game.KeyItemsForDoor(edge.Door.DoorType))
                    doorKeys.Add(k);
        var world = KeyItemPlacer.Reachable(graph, game, game.StartRoomCode, doorKeys);

        var balance = PlugEconomy.Evaluate(graph, game, world);
        context.Log($"[plugs] reachable plug supply {balance.Supply}, reachable box demand {balance.Demand}"
                    + (balance.MeetsDemand
                        ? " (every reachable box can be opened)"
                        : " (supply < demand — some reachable boxes stay locked; expected in vanilla)"));
    }

    /// <summary>
    /// Permute the door-gating key items among their (reachable) spots with a beatable assignment.
    /// The spots are exactly the records currently holding such a key, so the relocation conserves
    /// every key and its count — no item is created, lost, or displaced. Demo / Operation-Wipe-Out
    /// duplicate copies (rooms not reachable in the main game) are left untouched.
    /// </summary>
    private static void ShuffleDoorKeys(RandomizationContext context, RoomGraph graph, GameDefinition game)
    {
        // Keys that actually gate a door somewhere in the graph.
        var doorKeys = new HashSet<int>();
        foreach (var node in graph.Nodes)
            foreach (var edge in node.Edges)
                foreach (var k in game.KeyItemsForDoor(edge.Door.DoorType))
                    doorKeys.Add(k);

        // The relocatable spots: records holding one of those keys, in a room reachable in the
        // playable world (full-key reachability excludes the demo / Wipe-Out copies). A relocation
        // twin (records sharing a room + non-null Link) is ONE logical pickup the game duplicates
        // across script states, so it contributes a single spot/key; its sibling records are mirrored
        // onto the canonical's assignment afterwards, never seated independently (which would desync or
        // duplicate the key). See docs/reference/dc1/spawn/TRIGGER-DECODE.md + NodeItem.Link.
        var reachable = KeyItemPlacer.Reachable(graph, game, game.StartRoomCode, doorKeys);
        var spots = new List<KeyItemPlacer.Spot>();
        var keys = new List<int>();
        var canonical = new Dictionary<(int, string), ItemRecord>();
        var siblings = new Dictionary<ItemRecord, List<ItemRecord>>();
        foreach (var node in graph.Nodes)
        {
            if (!reachable.Contains(node.Code)) continue;
            foreach (var ni in node.Items)
            {
                if (ni.Record.IsEmptySlot || !doorKeys.Contains(ni.Record.ItemId)) continue;
                // Pinned key (map.json itemPriorities "Fixed"): keep it in its vanilla spot and out of
                // the shuffle pool — for door keys whose real requirement (e.g. an early generator /
                // return trip) the door-graph can't model, so shuffling them risks an unprovable softlock.
                if (ni.Priority == ItemPriority.Fixed) continue;
                if (ni.Link is { } link)
                {
                    var groupKey = (node.Code, link);
                    if (canonical.TryGetValue(groupKey, out var canon))
                    {
                        siblings[canon].Add(ni.Record);   // mirror target, not its own spot/key
                        continue;
                    }
                    canonical[groupKey] = ni.Record;
                    siblings[ni.Record] = new List<ItemRecord>();
                }
                spots.Add(new KeyItemPlacer.Spot(node.Code, ni.Record, ni.Requires));
                keys.Add(ni.Record.ItemId);
            }
        }

        if (spots.Count == 0)
        {
            context.Log("[keyshuffle] no door keys present to shuffle");
            context.Spoiler.Section(KeySpoilerTitle, KeySpoilerColumns)
                .AddNote("no door keys present to shuffle");
            return;
        }

        var placement = new KeyItemPlacer().Place(graph, game, game.StartRoomCode, game.GoalRoomCode,
                                                  spots, keys, context.Seed);
        if (!placement.Success)
        {
            // Should not happen for vanilla geometry (every key spot is reachable empty-handed), but
            // never break a seed: keep the original placement and let Verify report below.
            foreach (var line in placement.Log) context.Log(line);
            context.Log("[keyshuffle] could not find a beatable key layout; kept vanilla placement");
            context.Spoiler.Section(KeySpoilerTitle, KeySpoilerColumns)
                .AddNote("could not find a beatable key layout; kept vanilla placement");
            return;
        }

        var spoiler = context.Spoiler.Section(KeySpoilerTitle, KeySpoilerColumns);
        foreach (var (spot, key) in placement.Placements)
        {
            // Recorded per placement (one row per relocated spot, matching the pass's own count),
            // BEFORE the mutation so the vanilla id is still readable (SPOILER-LOG-PLAN.md §4).
            spoiler.AddRow(Spoiler.Dc1RoomNames.Describe(spot.RoomCode),
                           Spoiler.Dc1ItemNames.NameOf(spot.Record.OriginalItemId),
                           Spoiler.Dc1ItemNames.NameOf(key));
            spot.Record.ItemId = key;
            spot.Record.Amount = Math.Max(1, spot.Record.Amount);
        }
        // Mirror each linked canonical onto its sibling records so every copy of a relocation twin
        // shows the one shared assignment (no in-room desync).
        foreach (var (canon, sibs) in siblings)
            foreach (var sib in sibs)
            {
                sib.ItemId = canon.ItemId;
                sib.Amount = Math.Max(1, sib.Amount);
            }
        context.Log($"[keyshuffle] relocated {placement.Placements.Count} door keys across " +
                    $"{placement.Placements.Select(p => p.Spot.RoomCode).Distinct().Count()} rooms");
        spoiler.AddNote($"relocated {placement.Placements.Count} door key(s) across " +
                        $"{placement.Placements.Select(p => p.Spot.RoomCode).Distinct().Count()} room(s)");
    }

    private static Dictionary<int, IReadOnlyList<int>> KeysByRoom(RoomGraph graph, GameDefinition game)
    {
        var map = new Dictionary<int, List<int>>();
        foreach (var node in graph.Nodes)
            foreach (var ni in node.Items)
            {
                if (ni.Record.IsEmptySlot || !game.KeyItemIds.Contains(ni.Record.ItemId)) continue;
                if (!map.TryGetValue(node.Code, out var list))
                    map[node.Code] = list = new List<int>();
                list.Add(ni.Record.ItemId);
            }
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value);
    }
}
