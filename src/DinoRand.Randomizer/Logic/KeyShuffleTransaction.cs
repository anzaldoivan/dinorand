using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Passes;

namespace DinoRand.Randomizer.Logic;

/// <summary>
/// Discovers and applies one progression-safe door-key relocation as an atomic mutation: either the
/// produced layout verifies and is kept, or every touched record is restored to its original state.
/// </summary>
internal static class KeyShuffleTransaction
{
    internal const string SpoilerTitle = "Key items (DC1)";
    internal static readonly string[] SpoilerColumns = { "Room", "Vanilla key there", "New key there" };

    /// <summary>
    /// Build and atomically apply the shared progression-key plan. The policy includes vanilla key
    /// homes, opted-in DDK pairs, explicit synchronization groups, and opted-in scatter targets;
    /// scatter conserves displaced consumable tuples by moving them into vacated key records. Demo /
    /// Operation-Wipe-Out records outside the playable graph remain untouched.
    /// </summary>
    internal static void Execute(
        RandomizationContext context, RoomGraph graph, GameDefinition game,
        Func<RoomGraph, GameDefinition, KeyItemPlacer.PlacementResult>? finalVerifier = null)
    {
        var plan = KeyPlacementPlanner.Plan(context, graph, game);
        foreach (var line in plan.Log) context.Log(line);
        if (!plan.Success)
            throw new InvalidOperationException(
                "progression-key planning failed; no key or door changes were committed");

        if (plan.Policy is null || plan.Policy.Keys.Count == 0)
        {
            context.Log("[keyshuffle] no door keys present to shuffle");
            context.Spoiler.Section(SpoilerTitle, SpoilerColumns)
                .AddNote("no door keys present to shuffle");
            return;
        }

        var original = plan.Edits.ToDictionary(
            edit => edit.Record,
            edit => (ItemId: edit.Record.ItemId, Amount: edit.Record.Amount));
        KeyPlacementPlanner.Apply(plan);

        var check = finalVerifier is null
            ? KeyItemPlacer.Verify(graph, game, game.StartRoomCode, game.GoalRoomCode,
                                   KeysByRoom(graph, game))
            : finalVerifier(graph, game);
        if (!check.Success)
        {
            foreach (var (record, value) in original)
            {
                record.ItemId = value.ItemId;
                record.Amount = value.Amount;
            }
            foreach (var line in check.Log) context.Log(line);
            throw new InvalidOperationException(
                "committed progression-key plan failed final verification and was rolled back");
        }

        var spoiler = context.Spoiler.Section(SpoilerTitle, SpoilerColumns);
        var donorIds = DonorIdsForHint(context);
        foreach (var edit in plan.Edits.Where(edit => edit.Changed))
        {
            var visualNote = VisualNote(edit.Location.Visual, context.Config.NormalizePickupVisuals,
                                        context.Config.ImportPickupModels, donorIds.Contains(edit.ItemId),
                                        Spoiler.Dc1ItemNames.NameOf(edit.BeforeItemId));
            spoiler.AddRow(DescribePhysical(edit.Location),
                           Describe(edit.BeforeItemId, edit.BeforeAmount),
                           Describe(edit.ItemId, edit.Amount) + visualNote);
        }
        var placement = plan.Placement!;
        context.Log($"[keyshuffle] relocated {placement.Placements.Count} door keys across " +
                    $"{placement.Placements.Select(p => p.Spot.RoomCode).Distinct().Count()} rooms");
        context.Log($"[keyshuffle] committed {plan.Edits.Count(edit => edit.Changed)} changed physical records");
        spoiler.AddNote($"relocated {placement.Placements.Count} door key(s) across " +
                        $"{placement.Placements.Select(p => p.Spot.RoomCode).Distinct().Count()} room(s)");
    }

    private static string DescribePhysical(PickupLocationContract location) =>
        $"{Spoiler.Dc1RoomNames.Describe(location.RoomCode)} [record 0x{location.RecordOffset:X}]";

    private static string Describe(int itemId, int amount) =>
        Spoiler.Dc1ItemNames.NameOf(itemId) + (amount > 1 ? $" ×{amount}" : "");

    /// <summary>The key-table's per-row ground-visual hint (a PREDICTION — the authoritative outcome
    /// is the "Pickup models imported" spoiler section, since a Lever-B import can fail closed).
    /// Design correction 2026-07-17: donor-aware — a key with its own donor mesh shows that model on
    /// ANY spot class when Lever B is on; the generic-panel note applies only where marking actually
    /// rewrites the visual (mismatched spot, no donor upgrade).</summary>
    internal static string VisualNote(PickupVisual spotVisual, bool normalizeOn, bool importOn,
                                      bool hasDonor, string originalItemName)
    {
        if (importOn && hasDonor) return " (shows its own model)";
        bool markingOn = normalizeOn || importOn;   // mirrors NormalizePickupVisualsPass.IsEnabled
        return (spotVisual, markingOn) switch
        {
            (PickupVisual.InteractionOnly or PickupVisual.BespokeMesh, true) => " (shown as generic pickup)",
            (PickupVisual.InteractionOnly, false) => " (hidden — examine the spot)",
            (PickupVisual.BespokeMesh, false) => $" (appears as {originalItemName})",
            _ => "",
        };
    }

    /// <summary>Ids Lever B can give their own ground model — empty when Lever B is off, so the
    /// donor-catalog scan (a full-corpus mesh parse) only runs when the hint can differ.</summary>
    private static IReadOnlySet<int> DonorIdsForHint(RandomizationContext context)
    {
        if (!context.Config.ImportPickupModels) return new HashSet<int>();
        var relocatable = new HashSet<int>(context.Game.KeyItemIds);
        relocatable.UnionWith(context.Game.WeaponIds);
        relocatable.UnionWith(context.Game.WeaponPartIds);
        return PickupDonorCatalog.Build(context.Rooms, relocatable).Keys.ToHashSet();
    }

    internal static Dictionary<int, IReadOnlyList<int>> KeysByRoom(RoomGraph graph, GameDefinition game)
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
