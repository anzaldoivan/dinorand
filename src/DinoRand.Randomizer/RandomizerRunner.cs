using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;
using DinoRand.Randomizer.Output;
using DinoRand.Randomizer.Passes;

namespace DinoRand.Randomizer;

/// <summary>
/// Orchestrates a run: discover &amp; load per-room files → build graph → run the enabled
/// passes in order → write randomized copies plus a placement log and DGML map. Never
/// touches the originals; output goes to <c>mod_dinorand/</c> (BioRand's
/// <c>mod_biorand</c> convention).
/// </summary>
public sealed class RandomizerRunner
{
    private readonly GameDefinition _game;

    // Pass order is fixed. The door shuffle runs FIRST and rebuilds the graph, so progression /
    // key logic sees the rewired world (plan §5.2); then progression logic, then content shuffles.
    private readonly IReadOnlyList<IRandomizationPass> _passes = new IRandomizationPass[]
    {
        new DoorRandomizer(),
        new ProgressionPass(),
        new ItemRandomizer(),
        // Gated off by default (NormalizePickupVisuals). Runs after progression + item relocation so it sees
        // final ids: rewrites a relocated key/weapon's ground visual to the generic panel where the landing
        // spot's visual doesn't match. Lever A, docs/decisions/dc1/items/PICKUP-GROUND-MODEL-FEASIBILITY.md.
        new NormalizePickupVisualsPass(),
        // Gated off by default (ShortenCutscenes): in-place rewrite of whitelisted cutscene brackets
        // to their side effects (cont.74). No relocation — runs before the splicing passes, whose
        // relocation handles the new gotos as ordinary branch sites.
        new CutsceneShortenPass(),
        new EnemyRandomizer(),
        // Default DC1 enemy-randomizer extension: runs after the in-room permute and imports a foreign species
        // into eligible rooms, declaring the EXE patches they need on the context (the installer applies them).
        // docs/decisions/dc1/enemies/CROSS-SPECIES-PASS-PLAN.md.
        new CrossSpeciesEnemyPass(),
        // Gated off by default: per-placement enemy maxHP override (writes +6). Runs after both the in-room
        // permute and cross-species import so a newly imported presettable cat-2 record receives the same
        // named enemy-hp RNG stream as a native cat-2. Theri remains a cat-8 output override and is ineligible.
        // docs/decisions/dc1/spawn/ENEMY-SPAWN-SYSTEM.md "Gap 4 — REVERSED".
        new EnemyHpRandomizer(),
        // Gated off by default (ImportPickupModels, Lever B): donor ground meshes for relocated
        // pickups. MUST run after every ScriptInjector-splicing pass (the enemy passes above) — a
        // later mid-script insertion would shift the appended mesh out from under the record's
        // stored pointer. docs/decisions/dc1/items/PICKUP-GROUND-MODEL-FEASIBILITY.md ("Lever B plan").
        new PickupModelImportPass(),
        // Gated off by default (Dc1CutsceneSafeEnemies): palette-tint fallback for choreography-census
        // rooms the two enemy passes above refuse to touch. Must run AFTER every room-record-mutating
        // pass — its room-output override freezes the serialized bytes.
        new Dc1CutscenePalettePass(),
        // Phase 4, experimental + hard-gated off (VoiceManifestLayout.IsDecoded=false): cutscene
        // character-voice rando. Wired in but emits nothing until the DC1 voice addressing is decoded.
        // docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md.
        new VoiceRandomizer(),
        // External BGM import (off by default; no-op without a BgmPacksRoot). Overwrites Sound/BGM/ slots
        // with transcoded same-tag donor tracks — a pure loose-file overlay, reversed by Restore.
        // docs/decisions/cross/BGM-RANDO-PLAN.md.
        new Bgm.BgmRandomizer(),
        // Always-on cosmetic: seed watermark drawn into the title screen (BioRand parity;
        // docs/decisions/cross/SEED-WATERMARK-PLAN.md). Last — it touches nothing other passes read.
        new TitleWatermarkPass(),
    };

    public RandomizerRunner(GameDefinition game) => _game = game;

    /// <summary>Test seam for the mandatory post-pass verification gate.</summary>
    internal Func<RandomizationContext, KeyItemPlacer.PlacementResult>? FinalProgressionVerifier { get; init; }

    /// <param name="emitSpoiler">Write <c>SPOILER.md</c> beside the room files (docs/decisions/cross/SPOILER-LOG-PLAN.md).
    /// The spoiler is a pure projection of the passes' recorded decisions, built strictly AFTER every
    /// game file is written — turning it off changes no other output byte (regression-locked).</param>
    /// <param name="ct">Cancels the GENERATE phase only — every write here lands in
    /// <paramref name="outputDir"/> (the working mod dir), so aborting touches no game file. The
    /// overlay onto <c>Data\</c> is <see cref="Install.GameInstaller.Install"/>, which is
    /// deliberately NOT cancellable: it is sub-second, and tearing it in half is exactly the
    /// partial-install state the GUI's close warning exists to avoid.</param>
    public RunResult Run(string installDir, string outputDir, Seed seed, RandomizerConfig config,
                         bool emitSpoiler = true, CancellationToken ct = default)
    {
        var logLines = new List<string>();
        void Log(string line) => logLines.Add(line);

        Log($"DinoRand — {_game.DisplayName}");
        Log($"seed={seed}  install={installDir}");

        // 0. Prepare the (reused) working dir for this run.
        Dc1RunArtifactWriter.Prepare(outputDir, Log);

        // 1. Discover and load per-room files (read-only; originals untouched).
        var refs = _game.EnumerateRooms(installDir);
        var rooms = new List<RoomFile>();
        foreach (var rref in refs)
            rooms.Add(RoomFile.ReadFromFile(rref.Stage, rref.Room, rref.Path));
        Log($"[load] {rooms.Count} room files across {refs.Select(r => r.Stage).Distinct().Count()} stages");

        // 2. Build the room graph (used by key-item logic / door pass).
        var graph = RoomGraph.Build(rooms, _game.Requirements);
        Log($"[graph] {graph.Nodes.Count} rooms");

        // 3. Run passes.
        var context = new RandomizationContext(_game, rooms, graph, seed, config, Log, installDir);
        var transaction = DoorKeyStateSnapshot.Capture(rooms);
        try
        {
            foreach (var pass in _passes)
            {
                if (!pass.IsEnabled(config)) continue;
                ct.ThrowIfCancellationRequested();   // between passes: nothing is written yet
                pass.Apply(context);
            }

            if (FinalProgressionVerifier is not null
                || config.EnsureBeatable || config.ShuffleKeyItems || config.RandomizeDoors)
            {
                var final = FinalProgressionVerifier?.Invoke(context)
                    ?? KeyItemPlacer.Verify(context.Graph, _game, _game.StartRoomCode, _game.GoalRoomCode,
                        KeyShuffleTransaction.KeysByRoom(context.Graph, _game));
                foreach (var line in final.Log) Log(line);
                if (!final.Success)
                    throw new InvalidOperationException(
                        "final door/key verification failed; installable output was not produced");
            }
        }
        catch
        {
            transaction.Restore();
            context.RebuildGraph();
            throw;
        }

        // 4. Emit per-game artifacts after every pass has completed.
        Dc1RunArtifactWriter.Write(outputDir, refs, rooms, graph, context, seed, config,
            logLines, emitSpoiler, ct);

        return new RunResult(rooms.Count, graph.Nodes.Count, outputDir, logLines);
    }
}

public sealed record RunResult(int RoomsWritten, int RoomCount, string OutputDir,
                               IReadOnlyList<string> Log);
