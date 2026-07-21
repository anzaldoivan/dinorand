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

    private const string KeySpoilerTitle = KeyShuffleTransaction.SpoilerTitle;
    private static readonly string[] KeySpoilerColumns = KeyShuffleTransaction.SpoilerColumns;

    public bool IsEnabled(RandomizerConfig config) => config.EnsureBeatable || config.ShuffleKeyItems;

    public void Apply(RandomizationContext context)
    {
        var game = context.Game;
        var graph = context.Graph;

        if (context.Config.ShuffleKeyItems)
            KeyShuffleTransaction.Execute(context, graph, game);

        // Validate the (possibly shuffled) layout: collect where every key now sits and verify the
        // goal stays reachable under the door-graph key logic.
        var result = KeyItemPlacer.Verify(graph, game, game.StartRoomCode, game.GoalRoomCode,
                                          KeyShuffleTransaction.KeysByRoom(graph, game));
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

    /// <summary>The key-table's per-row ground-visual hint (a PREDICTION — the authoritative outcome
    /// is the "Pickup models imported" spoiler section, since a Lever-B import can fail closed).
    /// Design correction 2026-07-17: donor-aware — a key with its own donor mesh shows that model on
    /// ANY spot class when Lever B is on; the generic-panel note applies only where marking actually
    /// rewrites the visual (mismatched spot, no donor upgrade).</summary>
    public static string VisualNote(PickupVisual spotVisual, bool normalizeOn, bool importOn,
                                    bool hasDonor, string originalItemName)
        => KeyShuffleTransaction.VisualNote(spotVisual, normalizeOn, importOn, hasDonor, originalItemName);
}
