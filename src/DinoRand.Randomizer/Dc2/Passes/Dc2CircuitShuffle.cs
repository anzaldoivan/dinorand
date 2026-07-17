using DinoRand.FileFormats.Stage.Dc2;

namespace DinoRand.Randomizer.Dc2.Passes;

/// <summary>
/// DC2 stungun-circuit shuffle pass (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §4 item 2, K110):
/// rewrites the blink box-id literals of ST607 routines 7/8 and ST402 routines 23/24 with
/// seed-derived sequences (same length, full box coverage, no adjacent repeats) via
/// <see cref="Dc2CircuitPatch"/>. Runs on the context's working bytes so it composes with the
/// enemy/raptor passes when they touch the same rooms. The RNG key matches
/// <see cref="Dc2CircuitShuffleInstaller"/>, so the standalone CLI flag and a GUI run produce
/// identical blink orders for the same seed.
/// </summary>
public sealed class Dc2CircuitShuffle : IDc2RandomizationPass
{
    public string Name => "DC2 Circuits";

    public bool IsEnabled(RandomizerConfig config) => config.Dc2ShuffleCircuits;

    public void Apply(Dc2RandomizationContext context)
    {
        var spoiler = context.Spoiler.Section("Stungun circuits (DC2)", "Room", "Routine", "Blink order");
        int routinesChanged = 0;
        foreach (var spec in Dc2CircuitPatch.Rooms)
        {
            var room = context.Rooms.FirstOrDefault(r => r.Stage == spec.Stage && r.Room == spec.Room);
            if (room is null)
            {
                context.Log($"[dc2-circuits] {spec.FileName} not among the loaded rooms; skipped");
                continue;
            }
            var rng = context.Seed.RngFor($"{Name}:{spec.FileName}");
            var bytes = Dc2CircuitPatch.ShuffleRoom(context.CurrentBytes(room), spec, rng, out var results);
            context.EmitRoom(room, bytes);
            foreach (var r in results)
            {
                spoiler.AddRow($"ST{spec.Stage:X}{spec.Room:X2}", $"[{r.RoutineIndex}]", string.Join(",", r.NewIds));
                routinesChanged++;
            }
        }
        context.Log($"[dc2-circuits] {routinesChanged} blink routine(s) reshuffled across the two circuit rooms.");
    }
}
