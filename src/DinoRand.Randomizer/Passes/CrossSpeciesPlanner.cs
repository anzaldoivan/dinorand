using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Passes;

/// <summary>A room the cross-species pass may target, reduced to the facts the placement decision needs
/// (so the decision is a pure function, unit-testable without real RDTs).</summary>
/// <param name="Stage">Stage number (<c>roomId &gt;&gt; 8</c> — the cat-slot patch's scope).</param>
/// <param name="Room">Room number within the stage.</param>
/// <param name="HasVictim">True when the room has a randomizable raptor that can be replaced.</param>
/// <param name="Present">Species already present in the room (never placed on top of itself).</param>
public readonly record struct RoomCandidate(int Stage, int Room, bool HasVictim, IReadOnlyCollection<DinoSpecies> Present);

/// <summary>One decided placement: put <paramref name="Species"/> into room (<paramref name="Stage"/>,<paramref name="Room"/>).</summary>
public readonly record struct CrossSpeciesPlacement(int Stage, int Room, DinoSpecies Species);

/// <summary>
/// The pure placement decision for the cross-species pass — separated from the heavy import so the
/// stage-scoping logic is unit-testable. Enforces the conventions in docs/decisions/dc1/enemies/CROSS-SPECIES-PASS-PLAN.md §3/§4:
/// <list type="bullet">
///   <item>only <see cref="ExoticSpeciesCatalog.Enabled"/> species with an available donor are placed;</item>
///   <item>a species needing a cat-slot patch may only target a stage in its <c>FreeStages</c>;</item>
///   <item><b>at most one cat-slot species per (stage, category)</b> — the patch is stage-global, so every
///   placement of that category in the stage must be the same species (it may recur across rooms);</item>
///   <item>never place a species a room already hosts.</item>
/// </list>
/// </summary>
public static class CrossSpeciesPlanner
{
    public static List<CrossSpeciesPlacement> Plan(
        IReadOnlyList<RoomCandidate> candidates,
        IReadOnlyCollection<DinoSpecies> availableDonors,
        double chance,
        Random rng)
    {
        var enabled = ExoticSpeciesCatalog.Enabled
            .Where(d => availableDonors.Contains(d.Species))
            .ToList();
        var placements = new List<CrossSpeciesPlacement>();
        if (enabled.Count == 0) return placements;

        // The single cat-slot species locked in for each (stage, category) — once chosen it is reused.
        var stageCatSlot = new Dictionary<(int Stage, int Category), DinoSpecies>();

        foreach (var c in candidates)
        {
            if (!c.HasVictim) continue;

            var choices = enabled.Where(d =>
                d.AllowsStage(c.Stage) &&
                !c.Present.Contains(d.Species) &&
                (!d.NeedsCatSlotPatch
                 || !stageCatSlot.TryGetValue((c.Stage, d.Category), out var locked)
                 || locked == d.Species)).ToList();
            if (choices.Count == 0) continue;

            if (rng.NextDouble() > chance) continue;

            var def = choices[rng.Next(choices.Count)];
            if (def.NeedsCatSlotPatch) stageCatSlot[(c.Stage, def.Category)] = def.Species;
            placements.Add(new CrossSpeciesPlacement(c.Stage, c.Room, def.Species));
        }
        return placements;
    }
}
