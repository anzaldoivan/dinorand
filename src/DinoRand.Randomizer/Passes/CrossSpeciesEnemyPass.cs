using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Passes;

/// <summary>
/// Phase 2 (experimental, gated by <see cref="RandomizerConfig.CrossRoomEnemySpecies"/>, default off). Places
/// <b>foreign</b> dinosaur species into rooms that did not ship with them — the integrated successor to the
/// dormant cross-room pass and the standalone <c>--swap-species</c> CLI lab command. Unlike the same-species
/// in-room permute (<see cref="EnemyRandomizer"/>) this imports a species the room (and, for cat8/cat3, the
/// stage) never natively hosts, which needs both a room <c>.dat</c> edit <b>and</b> EXE patches; the pass declares
/// the latter as <see cref="Install.ExePatchRequest"/>s on the context and the installer applies them
/// (docs/decisions/dc1/enemies/CROSS-SPECIES-PASS-PLAN.md).
///
/// <para>The decision (which species into which room, honouring stage-scoping) is the pure
/// <see cref="CrossSpeciesPlanner"/>; the heavy per-room import is the injectable
/// <see cref="ICrossSpeciesImporter"/>. Only <see cref="ExoticSpeciesCatalog.Enabled"/> species with an
/// available donor are placed; scripted T-Rex and cutscene rooms are skipped, as in the in-room permute.</para>
/// </summary>
public sealed class CrossSpeciesEnemyPass : IRandomizationPass
{
    private readonly Func<IReadOnlyList<RoomFile>, ICrossSpeciesImporter> _importerFactory;

    /// <summary>Default ctor: uses the real <see cref="CrossSpeciesImporter"/>. Tests inject a fake importer.</summary>
    public CrossSpeciesEnemyPass(Func<IReadOnlyList<RoomFile>, ICrossSpeciesImporter>? importerFactory = null)
        => _importerFactory = importerFactory ?? (corpus => new CrossSpeciesImporter(corpus));

    public string Name => "cross-species-enemies";

    public bool IsEnabled(RandomizerConfig config) => config.CrossRoomEnemySpecies;

    public void Apply(RandomizationContext context)
    {
        var rng = context.Seed.RngFor(Name);
        var scripted = context.Game.ScriptedEnemyRoomCodes;
        var cutscene = context.Game.CutsceneRoomCodes;
        double chance = 0.25 + 0.5 * Math.Clamp(context.Config.EnemyDifficulty, 0, 1);

        var corpus = context.AllRooms().ToList();
        var importer = _importerFactory(corpus);
        var donors = importer.AvailableDonors;
        if (donors.Count == 0)
        {
            context.Log("[cross-species] no donor species could be extracted — nothing placed");
            return;
        }

        // Build the (pure) placement candidates: a room is a candidate when it isn't scripted/cutscene and has a
        // randomizable Velociraptor to replace.
        var byCode = new Dictionary<int, RoomFile>();
        var candidates = new List<RoomCandidate>();
        foreach (var room in corpus)
        {
            int code = room.Stage * 0x100 + room.Room;
            byCode[code] = room;
            bool eligible = room.Enemies.Count > 0 && !scripted.Contains(code) && !cutscene.Contains(code);
            bool hasVictim = eligible && room.Enemies.Any(e => e.IsRandomizableDino && e.Species == DinoSpecies.Velociraptor);
            var present = room.Enemies.Select(e => e.Species).ToHashSet();
            candidates.Add(new RoomCandidate(room.Stage, room.Room, hasVictim, present));
        }

        var placements = CrossSpeciesPlanner.Plan(candidates, donors, chance, rng);

        int placed = 0, skipped = 0;
        foreach (var p in placements)
        {
            var room = byCode[p.Stage * 0x100 + p.Room];
            var def = ExoticSpeciesCatalog.For(p.Species)!;
            var victim = room.Enemies.First(e => e.IsRandomizableDino && e.Species == DinoSpecies.Velociraptor);
            int idx = room.Enemies.IndexOf(victim);

            if (!importer.TryImport(context, room, idx, def, out var patches, out var note))
            {
                skipped++;
                context.Log($"[cross-species] {room} {p.Species}: skipped — {note}");
                continue;
            }
            context.ExePatchRequests.AddRange(patches);
            placed++;
            context.Log($"[cross-species] {room} slot{victim.Slot}: Velociraptor -> {p.Species} ({note})");
        }

        context.Log($"[cross-species] placed {placed} foreign species ({skipped} skipped) across "
                    + $"{placements.Select(p => p.Stage * 0x100 + p.Room).Distinct().Count()} rooms; "
                    + $"{context.ExePatchRequests.Count} exe patches queued (donors: {string.Join(", ", donors)})");
    }
}
