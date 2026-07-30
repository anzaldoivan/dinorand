using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Definitions;

/// <summary>How the cross-species pass places a given exotic — which import path it uses and which EXE
/// patches it must request. Adding a species/variant is a registry entry, not a code path
/// (docs/decisions/dc1/enemies/CROSS-SPECIES-PASS-PLAN.md §4b/§5).</summary>
public enum PlacementKind
{
    /// <summary>A grounded species the target stage already has an AI handler for (e.g. the heavy raptor):
    /// geometry+texture import only, <b>no</b> EXE patch.</summary>
    SameCategoryNoPatch,

    /// <summary>The cat-8 Therizinosaurus: closure import + clip-strip under the resident-pool floor + fixed-
    /// column texture, plus cat8 AI-slot + hit-reaction (walker NULL-guard) + enemy-SE EXE patches.</summary>
    Cat8Therizinosaurus,

    /// <summary>The cat-3 Tyrannosaurus boss rig: closure import + texture + cat3 AI-slot patch.</summary>
    Cat3Tyrannosaurus,

    /// <summary>A cat-7 flyer / cat-5 swarm: needs the target stage to expose that category's handler
    /// (un-located) — reserved.</summary>
    CatFlyerOrSwarm,
}

/// <summary>One placeable exotic species and the constraints under which the pass may use it.</summary>
/// <param name="Species">The donor species to import.</param>
/// <param name="Kind">The placement/import strategy + which EXE patches it needs.</param>
/// <param name="Enabled">Proven and on for this increment. Disabled entries are kept (with a
/// <paramref name="Blocker"/>) so enabling them later is a data change, not a rewrite.</param>
/// <param name="Blocker">Why a disabled species is gated off (the open work).</param>
/// <param name="FreeStages">Stages whose AI-record category slot is verified free, so a stage-scoped cat-slot
/// patch is safe there. Empty = no slot patch needed (<see cref="PlacementKind.SameCategoryNoPatch"/>) ⇒ no
/// stage constraint.</param>
public sealed record ExoticSpeciesDef(
    DinoSpecies Species,
    PlacementKind Kind,
    int Category,
    bool Enabled,
    string? Blocker,
    IReadOnlyList<int> FreeStages)
{
    /// <summary>True when placing this species requires a stage-scoped EXE cat-slot patch (so it may only go
    /// into a stage in <see cref="FreeStages"/>, and at most one such species per stage+category).</summary>
    public bool NeedsCatSlotPatch => Kind is not PlacementKind.SameCategoryNoPatch;

    /// <summary>True when <paramref name="stage"/> is an eligible target for this species.</summary>
    public bool AllowsStage(int stage) => !NeedsCatSlotPatch || FreeStages.Contains(stage);
}

/// <summary>
/// The data-driven registry of exotic species the cross-species pass can place. Spans all five species; only
/// the CE-proven paths are <see cref="ExoticSpeciesDef.Enabled"/> this increment (cat-8 Therizinosaurus +
/// grounded RaptorHeavy). T-Rex/flyer/swarm are present but gated off with their blockers recorded.
/// See docs/decisions/dc1/enemies/CROSS-SPECIES-PASS-PLAN.md.
/// </summary>
public static class ExoticSpeciesCatalog
{
    public static readonly IReadOnlyList<ExoticSpeciesDef> All = new[]
    {
        // PROVEN — live-CE-verified killable, stages 1 & 2 (cat8 NULL/stub).
        new ExoticSpeciesDef(DinoSpecies.Therizinosaurus, PlacementKind.Cat8Therizinosaurus, Category: 8,
            Enabled: true, Blocker: null, FreeStages: new[] { 1, 2 }),

        // PROVEN — same-category grounded raptor; no EXE patch, so no stage constraint (Category n/a).
        new ExoticSpeciesDef(DinoSpecies.RaptorHeavy, PlacementKind.SameCategoryNoPatch, Category: -1,
            Enabled: true, Blocker: null, FreeStages: Array.Empty<int>()),

        // GATED — imports + AI-dispatches but renders invisible in 0102 (engine draw-gate, RE paused;
        // memory trex-boss-trigger-investigation). cat3 free only in stage 1.
        new ExoticSpeciesDef(DinoSpecies.Tyrannosaurus, PlacementKind.Cat3Tyrannosaurus, Category: 3,
            Enabled: false, Blocker: "renders invisible in 0102 (engine draw-gate, RE paused)",
            FreeStages: new[] { 1 }),

        // GATED — the per-stage cat7 flyer / cat5 swarm AI handler is not located.
        new ExoticSpeciesDef(DinoSpecies.Pteranodon, PlacementKind.CatFlyerOrSwarm, Category: 7,
            Enabled: false, Blocker: "cat7 flyer AI handler not located per stage", FreeStages: Array.Empty<int>()),
        // GATED — cat5 handler LOCATED + live-verified (0x5C8116; stage-1 cat5 slot is free), but the compy is a
        // coordinated-GROUP entity: a single-victim cross-import spawns one, leaves the group-coordination block
        // entity+0x1C0..+0x1CC all-zero, and the swarm AI NULL-derefs it in multiple functions (entry 0x5C6ADD,
        // kill/hit 0x5C7E0A, …) → renders wrong, won't attack, crashes on hit. Needs the native group-init RE'd
        // + multi-instance placement, not a single swap (docs/reference/dc1/_registries/EXE-SYMBOLS.md SWARM-INTO-0102; memory swarm-cat5-handler).
        new ExoticSpeciesDef(DinoSpecies.Swarm, PlacementKind.CatFlyerOrSwarm, Category: 5,
            Enabled: false,
            // Deep multi-layer port (docs/decisions/dc1/spawn/SWARM-0102-GROUP-SPAWN-PLAN.md, 2026-06-24). SOLVED: 4-member
            // group spawn (op58 type-0x17 coordination effect + consecutive op20/op59), per-member +0x1C8 link,
            // and the stage-1 effect-dispatch handler patch — the coordination effect is now byte-identical to
            // native and the swarm renders + animates for seconds. REMAINING BLOCKER: a swarm behavior SCD task
            // desyncs (PC runs into the relocated heap closure → invalid op) — a resource-LAYOUT dependency, not
            // a table-slot patch. The --swap-species swarm path is a lab tool that reproduces this; not shippable.
            Blocker: "deep stage/resource port: spawn+coordination-link+effect-dispatch all solved (renders/animates), " +
                     "but a swarm behavior SCD task desyncs into the relocated heap closure (layout dependency)",
            FreeStages: Array.Empty<int>()),
    };

    /// <summary>The species the pass may actually place this increment.</summary>
    public static IEnumerable<ExoticSpeciesDef> Enabled => All.Where(d => d.Enabled);

    public static ExoticSpeciesDef? For(DinoSpecies species) => All.FirstOrDefault(d => d.Species == species);
}
