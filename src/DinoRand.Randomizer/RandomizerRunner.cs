using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Install;
using DinoRand.Randomizer.Passes;
using DinoRand.Randomizer.Spoiler;

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
        new EnemyRandomizer(),
        // Gated off by default: per-placement enemy maxHP override (writes +6). Runs after the permute so it
        // sets HP on the final species assignment. docs/decisions/dc1/spawn/ENEMY-SPAWN-SYSTEM.md "Gap 4 — REVERSED".
        new EnemyHpRandomizer(),
        // Experimental, gated off by default: runs after the in-room permute and imports a foreign species
        // into eligible rooms, declaring the EXE patches they need on the context (the installer applies them).
        // docs/decisions/dc1/enemies/CROSS-SPECIES-PASS-PLAN.md.
        new CrossSpeciesEnemyPass(),
        // Phase 4, experimental + hard-gated off (VoiceManifestLayout.IsDecoded=false): cutscene
        // character-voice rando. Wired in but emits nothing until the DC1 voice addressing is decoded.
        // docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md.
        new VoiceRandomizer(),
    };

    public RandomizerRunner(GameDefinition game) => _game = game;

    /// <param name="emitSpoiler">Write <c>SPOILER.md</c> beside the room files (docs/decisions/cross/SPOILER-LOG-PLAN.md).
    /// The spoiler is a pure projection of the passes' recorded decisions, built strictly AFTER every
    /// game file is written — turning it off changes no other output byte (regression-locked).</param>
    public RunResult Run(string installDir, string outputDir, Seed seed, RandomizerConfig config,
                         bool emitSpoiler = true)
    {
        var logLines = new List<string>();
        void Log(string line) => logLines.Add(line);

        Log($"DinoRand — {_game.DisplayName}");
        Log($"seed={seed}  install={installDir}");

        // 0. Clean stale room files from the (reused) working dir so it holds only this run's output
        //    (docs/decisions/dc2/install/DC2-INSTALL-INTEGRITY-PLAN.md — same hygiene for both games).
        int cleared = RunOutputDir.ClearStaleRoomFiles(outputDir);
        if (cleared > 0) Log($"[clean] removed {cleared} stale room file(s) from {outputDir}");

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
        foreach (var pass in _passes)
        {
            if (!pass.IsEnabled(config)) continue;
            pass.Apply(context);
        }

        // 4. Write outputs (mirrors original file names so a loader can override them).
        Directory.CreateDirectory(outputDir);

        // Clear stale room files from a previous run so the output dir reflects exactly
        // this seed — otherwise the installer (which overlays every *.dat it finds here)
        // could apply a leftover room from an earlier roll.
        var fresh = refs.Select(r => Path.GetFileName(r.Path))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in Directory.EnumerateFiles(outputDir, "*.dat"))
            if (!fresh.Contains(Path.GetFileName(existing)))
                File.Delete(existing);

        foreach (var (rref, room) in refs.Zip(rooms))
        {
            // A pass may supply final bytes (a post-serialization transform like a texture import); otherwise
            // emit the RoomFile as edited in place.
            var bytes = context.TryGetRoomOutput(room, out var overridden) ? overridden : room.Write();
            File.WriteAllBytes(Path.Combine(outputDir, Path.GetFileName(rref.Path)), bytes);
        }

        // Loose (non-room) files a pass produced — e.g. voice banks under Sound\VOICE\ (VOICE-RANDO-PLAN.md
        // §12.3). Written under the mod dir at their install-relative subpath; GameInstaller overlays them.
        // (Empty in normal runs today: the voice pass is hard-gated, so it registers nothing yet.)
        foreach (var (relPath, bytes) in context.LooseFiles)
        {
            var dest = Path.Combine(outputDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
        }

        // EXE-patch sidecar: write it when a pass requested patches, else clear any stale one so a fresh seed
        // (or one that placed nothing) never installs a previous roll's exe edits (mirrors the .dat cleanup).
        var planPath = Path.Combine(outputDir, ExePatchPlan.FileName);
        if (context.ExePatchRequests.Count > 0)
        {
            // Dedup identical requests: several rooms in one stage emit the same stage-global cat-slot /
            // hit-reaction patch (the patch is stage-scoped). ExePatchRequest is a value record, so Distinct
            // collapses them; per-room requests (e.g. enemy-SE) differ and are kept.
            var requests = context.ExePatchRequests.Distinct().ToList();
            new ExePatchPlan(ExePatchPlan.CurrentVersion, requests).Write(outputDir);
        }
        else if (File.Exists(planPath))
            File.Delete(planPath);

        File.WriteAllText(Path.Combine(outputDir, "log_dinorand.txt"),
            string.Join(Environment.NewLine, logLines));
        File.WriteAllText(Path.Combine(outputDir, "map.dgml"), graph.ToDgml());

        // Spoiler log (docs/decisions/cross/SPOILER-LOG-PLAN.md): a pure projection of what the passes recorded,
        // written strictly AFTER every game file above so emitting it cannot affect them. A stale
        // spoiler from a previous roll is removed when suppressed (mirrors the exe-plan cleanup).
        var spoilerPath = Path.Combine(outputDir, SpoilerLogBuilder.FileName);
        if (emitSpoiler)
        {
            var outputFiles = refs.Select(r => Path.GetFileName(r.Path))
                .Concat(context.LooseFiles.Keys)
                .Concat(context.ExePatchRequests.Count > 0 ? new[] { ExePatchPlan.FileName } : Array.Empty<string>())
                .Concat(new[] { "log_dinorand.txt", "map.dgml" })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            var debug = new SpoilerDebugInfo(
                SeedString.Encode(seed, config), seed.Value, _game.Id,
                SpoilerLogBuilder.AppVersion(),
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                SpoilerLogBuilder.DumpConfig(config), logLines, outputFiles);
            File.WriteAllText(spoilerPath,
                SpoilerLogBuilder.Build(new SpoilerDocument(debug, context.Spoiler.Sections)));
        }
        else if (File.Exists(spoilerPath))
            File.Delete(spoilerPath);

        return new RunResult(rooms.Count, graph.Nodes.Count, outputDir, logLines);
    }
}

public sealed record RunResult(int RoomsWritten, int RoomCount, string OutputDir,
                               IReadOnlyList<string> Log);
