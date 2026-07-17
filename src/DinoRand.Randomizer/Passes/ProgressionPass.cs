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

        RecordSpheres(context, result);

        LogPlugEconomy(context, graph, game);
    }

    /// <summary>
    /// The sphere playthrough (DOCS-AUDIENCE-PLAN.md §5a): one row per <see cref="KeyItemPlacer.SphereStep"/>
    /// of the final layout's Verify fixpoint — the Archipelago/OoTR spoiler convention (sphere 0 =
    /// empty-handed reach; sphere N opened by keys collected in spheres &lt; N). Pure projection of
    /// what Verify already computed; recorded whenever this pass ran, shuffle or not.
    /// </summary>
    private static void RecordSpheres(RandomizationContext context, KeyItemPlacer.PlacementResult result)
    {
        if (result.Spheres is not { Count: > 0 } spheres) return;
        var section = context.Spoiler.Section("Playthrough (DC1 spheres)",
                                              "Sphere", "Rooms reachable", "Keys collected in this sphere");
        foreach (var step in spheres)
            section.AddRow(step.Index.ToString(), step.RoomsReachable.ToString(),
                step.Collected.Count == 0
                    ? "—"
                    : string.Join("; ", step.Collected.Select(c =>
                          $"{Spoiler.Dc1ItemNames.NameOf(c.KeyItem)} @ {Spoiler.Dc1RoomNames.Describe(c.RoomCode)}")));
        section.AddNote(result.Success
            ? $"goal reachable after {spheres.Count} sphere(s)"
            : "WARNING: fixpoint ended without proving the goal reachable");
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
            {
                foreach (var k in game.KeyItemsForDoor(edge.Door.DoorType))
                    doorKeys.Add(k);
                // Opt-in: also relocate the in-scope progression keys that gate via the map.json overlay
                // `requires` (edge item AND-gates) rather than the door TYPE byte — the DDK Input/Code disc
                // PAIRS (game.OverlayRelocationKeyIds; PROGRESSION-KEY-RELOCATION-RESEARCH.md). Restricted to
                // that game-supplied band so it excludes B1 Room Key 0x2F (also a requires gate, out of scope)
                // and the panel keys (which gate no edge). The pair-aware forward-fill (FrontierKeys surfaces
                // edge.Requires.Items) seats both discs before the gate; Fixed/twin/reachable exclusions below
                // are shared with door keys. In the product this rides on ShuffleKeyItems (GUI/CLI couple it);
                // the flag stays independent so tests can exercise it in isolation.
                if (context.Config.RelocateDdkDiscs)
                    foreach (var k in edge.Requires.Items ?? Array.Empty<int>())
                        if (game.OverlayRelocationKeyIds.Contains(k)) doorKeys.Add(k);
            }

        // The relocatable spots: records holding one of those door keys, in a room reachable in the
        // playable world (full-key reachability — holding every key item that physically exists —
        // excludes the demo / Wipe-Out copies). A relocation twin (records sharing a room + non-null
        // Link) is ONE logical pickup the game duplicates across script states, so it contributes a
        // single spot/key; its sibling records are mirrored onto the canonical's assignment afterwards,
        // never seated independently (which would desync or duplicate the key). See
        // docs/reference/dc1/spawn/TRIGGER-DECODE.md + NodeItem.Link. The non-relocated progression items
        // (DDK discs, B1 Room Key, …) are NOT assumed held — Place now collects them in logic at their
        // fixed spots, symmetric with Verify (KEY-ITEM-RANDO-RESEARCH.md §3(b)).
        var playableKeys = new HashSet<int>();
        foreach (var node in graph.Nodes)
            foreach (var ni in node.Items)
                if (game.KeyItemIds.Contains(ni.Record.ItemId)) playableKeys.Add(ni.Record.ItemId);
        var reachable = KeyItemPlacer.Reachable(graph, game, game.StartRoomCode, playableKeys);
        var spots = new List<KeyItemPlacer.Spot>();
        var keys = new List<int>();
        var canonical = new Dictionary<(int, string), ItemRecord>();
        var siblings = new Dictionary<ItemRecord, List<ItemRecord>>();
        // Hidden-spot rule (AvoidHiddenPickupSpots, on by default): an interaction-only spot (no ground
        // visual — decoded map.json itemVisuals) may only receive a key whose own vanilla home was also
        // interaction-only ("no worse than vanilla"): a visible key never becomes invisible, a hidden key
        // may become visible. Collected in the same sweep, stamped onto the spots afterwards (the set is
        // complete only after the loop). docs/decisions/dc1/items/PICKUP-VISUAL-PLACEMENT-PLAN.md.
        bool avoidHidden = context.Config.AvoidHiddenPickupSpots;
        var hiddenVanillaKeys = new HashSet<int>();
        var hiddenSpotIdx = new List<int>();
        var spotVisuals = new Dictionary<ItemRecord, PickupVisual>(); // spoiler annotation only
        foreach (var node in graph.Nodes)
        {
            // Reachable, and not a one-way ending sink: a key seated in an escape-ride dead end
            // (game.EndingZoneRoomCodes) verifies "beatable" but can never be collected in time — the
            // reachability engine can't see the no-return property. Exclude from the spot pool.
            if (!reachable.Contains(node.Code) || game.EndingZoneRoomCodes.Contains(node.Code)) continue;
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
                spotVisuals[ni.Record] = ni.Visual;
                if (avoidHidden && ni.Visual == PickupVisual.InteractionOnly)
                {
                    hiddenVanillaKeys.Add(ni.Record.ItemId);
                    hiddenSpotIdx.Add(spots.Count);
                }
                spots.Add(new KeyItemPlacer.Spot(node.Code, ni.Record, ni.Requires));
                keys.Add(ni.Record.ItemId);
            }
        }

        // Scatter (opt-in): also admit static ammo/health pickups as spots, so a door key may spread into
        // one. They carry no key to place — just extra homes — so a chosen scatter slot's vanilla
        // consumable is DISPLACED and redistributed after placement to conserve the item multiset.
        // Predicate-gated to static/unconditional/non-twin slots (KEY-ITEM-SCATTER-DATA-AUDIT.md), never
        // runtime-armed/flag-gated/missable. Off ⇒ byte-identical to the door-key-only shuffle.
        var scatterRecords = new List<ItemRecord>();
        if (context.Config.ShuffleKeyItemsIntoPickups)
            foreach (var node in graph.Nodes)
            {
                if (!reachable.Contains(node.Code) || game.EndingZoneRoomCodes.Contains(node.Code)) continue;
                foreach (var ni in node.Items)
                {
                    // Skip a key-item record even if the position-keyed overlay stamped it (a consumable
                    // co-located at the same quad corner) — a scatter target is never a key item; the data
                    // also drops co-located positions, this is the belt to that suspenders.
                    if (!ni.IsScatterTarget || ni.Record.IsEmptySlot
                        || game.KeyItemIds.Contains(ni.Record.ItemId)) continue;
                    spotVisuals[ni.Record] = ni.Visual;
                    if (avoidHidden && ni.Visual == PickupVisual.InteractionOnly)
                        hiddenSpotIdx.Add(spots.Count); // admits only hidden-vanilla keys (below)
                    spots.Add(new KeyItemPlacer.Spot(node.Code, ni.Record, ni.Requires));
                    scatterRecords.Add(ni.Record);
                }
            }

        // Stamp the completed hidden-vanilla key set onto every interaction-only spot. One shared set
        // instance — the placer's tightness guard groups constrained spots by it. Flag off ⇒ no spot is
        // constrained and the placer runs exactly as before.
        foreach (var i in hiddenSpotIdx)
            spots[i] = spots[i] with { EligibleKeys = hiddenVanillaKeys };

        if (spots.Count == 0)
        {
            context.Log("[keyshuffle] no door keys present to shuffle");
            context.Spoiler.Section(KeySpoilerTitle, KeySpoilerColumns)
                .AddNote("no door keys present to shuffle");
            return;
        }

        // Records this shuffle will overwrite (chosen canonical spots + their mirrored twin siblings):
        // Place must NOT collect these as fixed progression (their id is moving), while every OTHER key
        // item stays put and is collected in logic.
        var relocating = new HashSet<ItemRecord>();
        foreach (var s in spots) relocating.Add(s.Record);
        foreach (var sibs in siblings.Values)
            foreach (var sib in sibs) relocating.Add(sib);

        var placement = new KeyItemPlacer().Place(graph, game, game.StartRoomCode, game.GoalRoomCode,
                                                  spots, keys, context.Seed, relocating);
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

        // Conservation for scatter: a door key placed into a scatter slot DISPLACES that slot's consumable,
        // and a door key's own vanilla slot that did NOT receive a key would otherwise keep the key it lost
        // (duplication). Move each displaced consumable (from a CHOSEN scatter slot) into a VACATED door-key
        // slot (an un-chosen door-key spot) — counts match by construction (a chosen scatter slot exists iff
        // a door-key slot went un-chosen). Un-chosen scatter slots keep their own consumable, untouched.
        // Captured BEFORE mutation: OriginalItemId is the vanilla consumable, Amount its vanilla count.
        var placedRecords = placement.Placements.Select(p => p.Spot.Record).ToHashSet();
        var scatterSet = new HashSet<ItemRecord>(scatterRecords);
        var displacedConsumables = scatterRecords.Where(placedRecords.Contains)
            .Select(r => (Id: r.OriginalItemId, Amount: Math.Max(1, r.Amount))).ToList();
        var vacatedDoorKeySlots = spots.Select(s => s.Record)
            .Where(r => !scatterSet.Contains(r) && !placedRecords.Contains(r)).Distinct().ToList();

        // Apply the placement, capturing each record's original (id, amount) so it can be reverted.
        // OriginalItemId is never mutated, so the spoiler's vanilla-id column stays correct post-mutation.
        var original = new Dictionary<ItemRecord, (int Id, int Amount)>();
        foreach (var (spot, key) in placement.Placements)
        {
            original.TryAdd(spot.Record, (spot.Record.ItemId, spot.Record.Amount));
            spot.Record.ItemId = key;
            spot.Record.Amount = Math.Max(1, spot.Record.Amount);
        }
        // Mirror each linked canonical onto its sibling records so every copy of a relocation twin
        // shows the one shared assignment (no in-room desync).
        foreach (var (canon, sibs) in siblings)
            foreach (var sib in sibs)
            {
                original.TryAdd(sib, (sib.ItemId, sib.Amount));
                sib.ItemId = canon.ItemId;
                sib.Amount = Math.Max(1, sib.Amount);
            }
        for (int i = 0; i < vacatedDoorKeySlots.Count && i < displacedConsumables.Count; i++)
        {
            original.TryAdd(vacatedDoorKeySlots[i], (vacatedDoorKeySlots[i].ItemId, vacatedDoorKeySlots[i].Amount));
            vacatedDoorKeySlots[i].ItemId = displacedConsumables[i].Id;
            vacatedDoorKeySlots[i].Amount = displacedConsumables[i].Amount;
        }

        // Enforced gate (mirror the Place-fails branch): Place is symmetric with Verify, so a produced
        // layout should always verify — but never SHIP an unbeatable one. Verify the committed layout; on
        // failure revert every mutated record to vanilla and keep the vanilla placement. GRAPH-LOGIC-PARITY §8l.
        var check = KeyItemPlacer.Verify(graph, game, game.StartRoomCode, game.GoalRoomCode,
                                         KeysByRoom(graph, game));
        if (!check.Success)
        {
            foreach (var (rec, (id, amt)) in original) { rec.ItemId = id; rec.Amount = amt; }
            foreach (var line in check.Log) context.Log(line);
            context.Log("[keyshuffle] produced layout failed verification; reverted to vanilla placement");
            context.Spoiler.Section(KeySpoilerTitle, KeySpoilerColumns)
                .AddNote("produced layout failed verification; kept vanilla placement");
            return;
        }

        var spoiler = context.Spoiler.Section(KeySpoilerTitle, KeySpoilerColumns);
        var donorIds = DonorIdsForHint(context);
        foreach (var (spot, key) in placement.Placements)
        {
            // Ground-visual hint (cont.72): the spot's visual never follows the relocated id, so tell
            // the player what they will (or won't) actually see at the spot. When Lever-A normalization
            // is on (NormalizePickupVisuals), a mismatched spot is rewritten to the generic panel, so the
            // vanilla hidden/wrong-mesh hint no longer applies. The current key-shuffle exposure rooms all
            // have a free slot (census, PICKUP-GROUND-MODEL-FEASIBILITY.md), so the note holds.
            var spotVisual = spotVisuals.GetValueOrDefault(spot.Record);
            var visualNote = VisualNote(spotVisual, context.Config.NormalizePickupVisuals,
                                        context.Config.ImportPickupModels, donorIds.Contains(key),
                                        Spoiler.Dc1ItemNames.NameOf(spot.Record.OriginalItemId));
            spoiler.AddRow(Spoiler.Dc1RoomNames.Describe(spot.RoomCode),
                           Spoiler.Dc1ItemNames.NameOf(spot.Record.OriginalItemId),
                           Spoiler.Dc1ItemNames.NameOf(key) + visualNote);
        }
        context.Log($"[keyshuffle] relocated {placement.Placements.Count} door keys across " +
                    $"{placement.Placements.Select(p => p.Spot.RoomCode).Distinct().Count()} rooms");
        spoiler.AddNote($"relocated {placement.Placements.Count} door key(s) across " +
                        $"{placement.Placements.Select(p => p.Spot.RoomCode).Distinct().Count()} room(s)");
    }

    /// <summary>The key-table's per-row ground-visual hint (a PREDICTION — the authoritative outcome
    /// is the "Pickup models imported" spoiler section, since a Lever-B import can fail closed).
    /// Design correction 2026-07-17: donor-aware — a key with its own donor mesh shows that model on
    /// ANY spot class when Lever B is on; the generic-panel note applies only where marking actually
    /// rewrites the visual (mismatched spot, no donor upgrade).</summary>
    public static string VisualNote(PickupVisual spotVisual, bool normalizeOn, bool importOn,
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
