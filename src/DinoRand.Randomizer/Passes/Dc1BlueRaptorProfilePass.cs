using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Install;

namespace DinoRand.Randomizer.Passes;

/// <summary>Applies the fixed, evidence-backed ST102 Blue Raptor profile.</summary>
public sealed class Dc1BlueRaptorProfilePass : IRandomizationPass
{
    public string Name => "dc1-blue-raptor-profile";

    public bool IsEnabled(RandomizerConfig config) => config.Dc1BlueRaptorProfile;

    public void Apply(RandomizationContext context)
    {
        if (!string.Equals(context.Game.Id, "dc1", StringComparison.OrdinalIgnoreCase)) return;

        var target = context.AllRooms().FirstOrDefault(r =>
            r.Stage == Dc1RaptorVisualVariantProfiles.BlueTargetStage
            && r.Room == Dc1RaptorVisualVariantProfiles.BlueTargetRoom);
        var donor = context.AllRooms().FirstOrDefault(r =>
            r.Stage == Dc1RaptorVisualVariantProfiles.BlueDonorStage
            && r.Room == Dc1RaptorVisualVariantProfiles.BlueDonorRoom);
        var enemy = target?.Enemies.FirstOrDefault(e => e.IsRandomizableDino
                                                         && e.Species == DinoSpecies.Velociraptor
                                                         && e.Category == 2
                                                         && e.SpeciesBoneCount == 21);
        if (target is null || donor is null || enemy is null)
        {
            context.Log("[dc1-blue-raptor-profile] ST102 profile skipped — pinned target or donor unavailable");
            return;
        }

        Dc1RaptorVisualVariantPaletteResult result;
        try
        {
            result = Dc1RaptorVisualVariantEditor.TryApplyProfile(
                target, donor, enemy, Dc1RaptorVisualVariantProfiles.Blue);
        }
        catch (InvalidOperationException ex)
        {
            context.Log($"[dc1-blue-raptor-profile] ST102 profile skipped — {ex.Message}");
            return;
        }
        if (result.Outcome != Dc1RaptorVisualVariantOutcome.Applied)
        {
            context.Log($"[dc1-blue-raptor-profile] ST102 profile skipped — {result.Note}");
            return;
        }

        context.SetRoomOutput(target, result.PatchedBytes!);
        context.ExePatchRequests.Add(ExePatchRequest.BlueRaptorDescriptor());
        context.Log("[dc1-blue-raptor-profile] ST102 Blue Raptor profile applied; descriptor patch queued");
    }
}
