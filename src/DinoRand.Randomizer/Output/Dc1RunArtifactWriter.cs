using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Install;
using DinoRand.Randomizer.Spoiler;

namespace DinoRand.Randomizer.Output;

internal static class Dc1RunArtifactWriter
{
    public static void Prepare(string outputDir, Action<string> log)
    {
        // Clean stale room files from the (reused) working dir so it holds only this run's output
        // (docs/decisions/dc2/install/DC2-INSTALL-INTEGRITY-PLAN.md — same hygiene for both games).
        int cleared = RunOutputDir.ClearStaleRoomFiles(outputDir);
        if (Directory.Exists(outputDir))
        {
            cleared += DeleteIfPresent(Path.Combine(outputDir, ExePatchPlan.FileName));
            cleared += DeleteIfPresent(Path.Combine(outputDir, "Data", "t_image.imd"));
            foreach (var relative in new[] { "Sound/VOICE", "Sound/BGM", "Speech" })
            {
                var directory = Path.Combine(outputDir,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(directory)) continue;
                foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    cleared += DeleteIfPresent(path);
            }
        }
        if (cleared > 0) log($"[clean] removed {cleared} stale installable file(s) from {outputDir}");
    }

    private static int DeleteIfPresent(string path)
    {
        if (!File.Exists(path)) return 0;
        try { File.Delete(path); return 1; }
        catch (IOException) { return 0; }
    }

    public static void Write(
        string outputDir,
        IReadOnlyList<RoomFileRef> refs,
        IReadOnlyList<RoomFile> rooms,
        RoomGraph graph,
        RandomizationContext context,
        Seed seed,
        RandomizerConfig config,
        IReadOnlyList<string> logLines,
        bool emitSpoiler,
        CancellationToken ct)
    {
        // Write outputs (mirrors original file names so a loader can override them).
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
            ct.ThrowIfCancellationRequested();   // mod-dir writes only — safe to abort mid-way
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
        // written strictly AFTER every game file above so emitting it cannot affect them. The encoded seed is
        // computed once and owns the per-seed file name; stale generated names and the legacy fixed name are
        // removed from the reused output directory for both emission modes.
        var seedString = SeedString.Encode(seed, config);
        var spoilerPath = Path.Combine(outputDir, SpoilerLogBuilder.FileNameFor(seedString));
        SpoilerLogBuilder.RemoveStaleFiles(outputDir);
        if (emitSpoiler)
        {
            var outputFiles = refs.Select(r => Path.GetFileName(r.Path))
                .Concat(context.LooseFiles.Keys)
                .Concat(context.ExePatchRequests.Count > 0 ? new[] { ExePatchPlan.FileName } : Array.Empty<string>())
                .Concat(new[] { "log_dinorand.txt", "map.dgml" })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            var debug = new SpoilerDebugInfo(
                seedString, seed.Value, context.Game.Id,
                SpoilerLogBuilder.AppVersion(),
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                SpoilerLogBuilder.DumpConfig(config), logLines, outputFiles);
            File.WriteAllText(spoilerPath,
                SpoilerLogBuilder.Build(new SpoilerDocument(debug, context.Spoiler.Sections)));
        }
    }
}
