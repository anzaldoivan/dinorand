using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Dc2.Passes;

namespace DinoRand.Randomizer.Dc2;

/// <summary>
/// Orchestrates a DC2 run — the parallel-stack twin of <see cref="RandomizerRunner"/>, typed to
/// <see cref="Dc2RoomFile"/> and <see cref="DinoCrisis2"/> so DC1's runner is never touched
/// (docs/parity/BIORAND-REUSE-VALIDATION.md Q3). Mirrors BioRand's per-game <c>Re2Randomiser</c>:
/// the game registers its helpers, the runner drives the shared loop.
///
/// <para>Loads rooms and runs the DC2 passes, writing the randomized room files <b>non-destructively</b>
/// to <paramref name="outputDir"/> (via <see cref="Dc2OutputDirSink"/>). The enemy pass is live
/// (cross-species TYPE swap); item/door passes are still no-ops pending their record decodes. Install
/// overlays the output dir onto the game's Data via <see cref="Install.GameInstaller"/>.</para>
/// </summary>
public sealed class Dc2RandomizerRunner
{
    private readonly DinoCrisis2 _game;

    // DC2 pass order (will grow as decoders land). Items/doors are blocked on their record decodes.
    private readonly IReadOnlyList<IDc2RandomizationPass> _passes = new IDc2RandomizationPass[]
    {
        new Dc2EnemyRandomizer(),
        new Dc2RaptorTierRandomizer(), // after the enemy pass: reads its working bytes, skips converted raptors
        new Dc2PlayerModelSwap(),
        new Dc2VoiceRandomizer(), // LIVE 2026-07-05: emits swapped Speech/NNNN.dat loose files
        new Dc2CircuitShuffle(),  // after the room-byte passes: builds on their working bytes (K110)
        new Dc2PlateKeyRekey(),   // ST205 SAT-9 routing + blue-panel recolour (K118)
        // Always-on cosmetic: seed watermark into TITLE.DAT/TITLE2.DAT (BioRand parity;
        // docs/decisions/cross/SEED-WATERMARK-PLAN.md). Last — it touches nothing other passes read.
        new Passes.Dc2TitleWatermarkPass(),

        // TODO(dc2): new Dc2DoorRandomizer(), new Dc2ItemRandomizer(), once OPEN #2/#5 decode.
    };

    public Dc2RandomizerRunner(DinoCrisis2 game) => _game = game;

    /// <param name="emitSpoiler">Write <c>SPOILER.md</c> beside the room files (docs/decisions/cross/SPOILER-LOG-PLAN.md).
    /// Built strictly AFTER the passes have emitted every game file — turning it off changes no
    /// other output byte (regression-locked).</param>
    /// <param name="ct">Cancels the GENERATE phase only — see <see cref="RandomizerRunner.Run"/>;
    /// the overlay onto <c>Data\</c> is deliberately not cancellable.</param>
    public Dc2RunResult Run(string installDir, string outputDir, Seed seed, RandomizerConfig config,
                            bool emitSpoiler = true, CancellationToken ct = default)
    {
        var logLines = new List<string>();
        void Log(string line) => logLines.Add(line);

        Log($"DinoRand — {_game.DisplayName} (DC2 scaffold; record decoders pending)");
        Log($"seed={seed}  install={installDir}");

        // 0. Clean stale room files from the (reused) working dir so it holds only this run's output —
        //    a stale/foreign *.dat must never survive into the install (docs/decisions/dc2/install/DC2-INSTALL-INTEGRITY-PLAN.md).
        int cleared = RunOutputDir.ClearStaleRoomFiles(outputDir);
        if (cleared > 0) Log($"[clean] removed {cleared} stale room file(s) from {outputDir}");

        // 1. Discover and load per-room files (read-only).
        var refs = _game.EnumerateRooms(installDir);
        var rooms = new List<Dc2RoomFile>();
        foreach (var rref in refs)
            rooms.Add(Dc2RoomFile.ReadFromFile(rref.Stage, rref.Room, rref.Path));
        Log($"[load] {rooms.Count} DC2 room files across {refs.Select(r => r.Stage).Distinct().Count()} stages");

        // 2. Run the passes. Enemy randomization writes the edited slot-5 TYPE literals to the OUTPUT DIR
        //    (non-destructive) — the DC2 analogue of DC1's "generate to a mod dir" step. Install then
        //    overlays that dir onto rebirth\Data via GameInstaller (docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md).
        var sink = new Dc2OutputDirSink(outputDir);
        var context = new Dc2RandomizationContext(_game, rooms, seed, config, Log, sink,
                                                  _game.GetDataDir(installDir));
        foreach (var pass in _passes)
        {
            if (!pass.IsEnabled(config)) continue;
            ct.ThrowIfCancellationRequested();   // output-dir writes only — safe to abort between passes
            pass.Apply(context);
        }

        // 2b. Optional CR .d2p sidecar (docs/parity/NONDESTRUCTIVE-INSTALL-PARITY.md): word-diff each
        //     touched room's SCD blob into <out>\patch\ for the wrapper's patch\ST*.d2p override
        //     (LOADER-DC2.md §5). Purely additive — no other output byte changes; a stale patch dir
        //     from a previous roll never survives (same integrity rule as room files/spoiler).
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
            Log($"[d2p] {patchCount} CR room patch file(s) → {patchDir}");
        }

        // 3. Outputs: enemies are ROOM-FILE editable (K59 — the op-0x1a TYPE literal; NOT EXE-side),
        //    written above to outputDir. TODO(dc2): door/item room-file edits land on the same sink once
        //    their record decodes (OPEN #2/#5); a CR .d2p sink is a future IDc2OutputSink. The ddraw.dll
        //    MotionTrail wrapper fix (config.FixDc2MotionTrail) is now an INSTALL-time action (it patches
        //    the game in place), applied by the caller after overlay — not here, so Generate stays
        //    non-destructive.
        Log($"[write] {sink.RoomsWritten} randomized room file(s) + {sink.FilesWritten} other Data file(s) → {outputDir}");

        // Spoiler log (docs/decisions/cross/SPOILER-LOG-PLAN.md): a pure projection of what the passes recorded,
        // written strictly AFTER every game file above so emitting it cannot affect them. A stale
        // spoiler from a previous roll is removed when suppressed.
        Directory.CreateDirectory(outputDir);
        var spoilerPath = Path.Combine(outputDir, Spoiler.SpoilerLogBuilder.FileName);
        if (emitSpoiler)
        {
            var debug = new Spoiler.SpoilerDebugInfo(
                Spoiler.SeedString.Encode(seed, config), seed.Value, _game.Id,
                Spoiler.SpoilerLogBuilder.AppVersion(),
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                Spoiler.SpoilerLogBuilder.DumpConfig(config), logLines,
                sink.WrittenFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList());
            File.WriteAllText(spoilerPath,
                Spoiler.SpoilerLogBuilder.Build(new Spoiler.SpoilerDocument(debug, context.Spoiler.Sections)));
        }
        else if (File.Exists(spoilerPath))
            File.Delete(spoilerPath);

        return new Dc2RunResult(rooms.Count, sink.RoomsWritten, outputDir, logLines,
                                sink.WrittenFiles.ToList());
    }
}

/// <param name="WrittenFiles">The exact file names this run emitted to <paramref name="OutputDir"/> —
/// pass to <see cref="Install.GameInstaller.Install"/> as its <c>onlyFiles</c> allow-list so a stale
/// file in a reused dir is never installed (docs/decisions/dc2/install/DC2-INSTALL-INTEGRITY-PLAN.md).</param>
public sealed record Dc2RunResult(int RoomCount, int RoomsWritten, string OutputDir,
                                  IReadOnlyList<string> Log, IReadOnlyList<string> WrittenFiles);
