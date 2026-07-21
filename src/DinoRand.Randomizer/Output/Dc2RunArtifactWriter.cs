using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Spoiler;

namespace DinoRand.Randomizer.Output;

internal static class Dc2RunArtifactWriter
{
    public static void Prepare(string outputDir, Action<string> log)
    {
        // Clean stale room files from the (reused) working dir so it holds only this run's output —
        // a stale/foreign *.dat must never survive into the install (docs/decisions/dc2/install/DC2-INSTALL-INTEGRITY-PLAN.md).
        int cleared = RunOutputDir.ClearStaleRoomFiles(outputDir);
        if (cleared > 0) log($"[clean] removed {cleared} stale room file(s) from {outputDir}");
    }

    public static void Write(
        string outputDir,
        IReadOnlyList<Dc2RoomFile> rooms,
        Dc2RandomizationContext context,
        Dc2OutputDirSink sink,
        Seed seed,
        RandomizerConfig config,
        IReadOnlyList<string> logLines,
        Action<string> log,
        bool emitSpoiler)
    {
        // Optional CR .d2p sidecar (docs/parity/NONDESTRUCTIVE-INSTALL-PARITY.md): word-diff each
        // touched room's SCD blob into <out>\patch\ for the wrapper's patch\ST*.d2p override
        // (LOADER-DC2.md §5). Purely additive — no other output byte changes; a stale patch dir
        // from a previous roll never survives (same integrity rule as room files/spoiler).
        var patchDir = Path.Combine(outputDir, Dc2D2pWriter.PatchDirName);
        if (Directory.Exists(patchDir)) Directory.Delete(patchDir, recursive: true);
        if (config.Dc2EmitD2pPatches)
        {
            int patchCount = 0;
            foreach (var room in rooms)
            {
                var current = context.CurrentBytes(room);
                if (ReferenceEquals(current, room.OriginalBytes)) continue;   // untouched this run
                if (Dc2D2pWriter.BuildFromPackages(room.OriginalBytes, current) is not { } d2p) continue;
                Directory.CreateDirectory(patchDir);
                File.WriteAllBytes(Path.Combine(patchDir, Dc2D2pWriter.FileNameFor(room.Stage, room.Room)), d2p);
                patchCount++;
            }
            log($"[d2p] {patchCount} CR room patch file(s) → {patchDir}");
        }

        // Outputs: enemies are ROOM-FILE editable (K59 — the op-0x1a TYPE literal; NOT EXE-side),
        // written above to outputDir. TODO(dc2): door/item room-file edits land on the same sink once
        // their record decodes (OPEN #2/#5); a CR .d2p sink is a future IDc2OutputSink. The ddraw.dll
        // MotionTrail wrapper fix (config.FixDc2MotionTrail) is now an INSTALL-time action (it patches
        // the game in place), applied by the caller after overlay — not here, so Generate stays
        // non-destructive.
        log($"[write] {sink.RoomsWritten} randomized room file(s) + {sink.FilesWritten} other Data file(s) → {outputDir}");

        // Spoiler log (docs/decisions/cross/SPOILER-LOG-PLAN.md): a pure projection of what the passes recorded,
        // written strictly AFTER every game file above so emitting it cannot affect them. A stale
        // spoiler from a previous roll is removed when suppressed.
        Directory.CreateDirectory(outputDir);
        var spoilerPath = Path.Combine(outputDir, SpoilerLogBuilder.FileName);
        if (emitSpoiler)
        {
            var debug = new SpoilerDebugInfo(
                SeedString.Encode(seed, config), seed.Value, context.Game.Id,
                SpoilerLogBuilder.AppVersion(),
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                SpoilerLogBuilder.DumpConfig(config), logLines,
                sink.WrittenFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList());
            File.WriteAllText(spoilerPath,
                SpoilerLogBuilder.Build(new SpoilerDocument(debug, context.Spoiler.Sections)));
        }
        else if (File.Exists(spoilerPath))
            File.Delete(spoilerPath);
    }
}
