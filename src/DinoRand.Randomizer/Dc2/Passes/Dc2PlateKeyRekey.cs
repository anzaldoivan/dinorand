using DinoRand.FileFormats.Stage.Dc2;

namespace DinoRand.Randomizer.Dc2.Passes;

/// <summary>
/// DC2 Key-Plate terminal re-key pass (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §4 item 4, K118):
/// permutes the SAT-9 routing of ST205's blue terminal so a seed-chosen plate colour is the correct
/// one, and recolours the blue slot panel to match, via <see cref="Dc2PlateKeyPatch"/>. Runs on the
/// context's working bytes so it composes with the enemy/raptor passes. The RNG key matches
/// <see cref="Dc2PlateKeyInstaller"/>, so the standalone CLI flag and a GUI run produce identical
/// bytes for the same seed.
/// </summary>
public sealed class Dc2PlateKeyRekey : IDc2RandomizationPass
{
    public string Name => "DC2 Plate Key";

    public bool IsEnabled(RandomizerConfig config) => config.Dc2RekeyPlateDoor;

    public void Apply(Dc2RandomizationContext context)
    {
        var room = context.Rooms.FirstOrDefault(r => r.Stage == 2 && r.Room == 5); // ST205
        if (room is null)
        {
            context.Log("[dc2-plate-key] ST205 not among the loaded rooms; skipped");
            return;
        }
        var patchPlan = Dc2ExecutablePatchPlanner.PlanRequiredPlate(context.Seed.RngFor("DC2 Plate Key"));
        int plate = patchPlan.RequiredPlate!.Value;
        byte[] bytes;
        Dc2PlateKeyPatch.Result result;
        try
        {
            bytes = Dc2PlateKeyPatch.ApplyRoom(context.CurrentBytes(room), plate, out result);
        }
        catch (InvalidOperationException ex)
        {
            context.Log($"[dc2-plate-key] {ex.Message}");
            return;
        }
        context.EmitRoom(room, bytes);
        context.Spoiler.Section("Key-Plate terminal (DC2)", "Room", "Accepts plate", "Panel")
            .AddRow("ST205", PlateName(result.TargetPlate), result.Changed ? "recoloured" : "blue (vanilla)");
        context.Log($"[dc2-plate-key] ST205 terminal now accepts {PlateName(result.TargetPlate)}.");
    }

    private static string PlateName(int plate) => plate switch
    {
        Dc2PlateKeyPatch.Green => "Green", Dc2PlateKeyPatch.Blue => "Blue", Dc2PlateKeyPatch.Red => "Red",
        Dc2PlateKeyPatch.Yellow => "Yellow", Dc2PlateKeyPatch.White => "White", Dc2PlateKeyPatch.Purple => "Purple",
        _ => $"0x{plate:X2}",
    };
}
