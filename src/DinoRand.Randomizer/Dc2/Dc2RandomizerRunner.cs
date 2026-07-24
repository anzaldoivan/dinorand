using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Dc2.Passes;
using DinoRand.Randomizer.Output;

namespace DinoRand.Randomizer.Dc2;

/// <summary>
/// Orchestrates a DC2 run — the parallel-stack twin of <see cref="RandomizerRunner"/>, typed to
/// <see cref="Dc2RoomFile"/> and <see cref="DinoCrisis2"/> so DC1's runner is never touched
/// (docs/parity/BIORAND-REUSE-VALIDATION.md Q3). Mirrors BioRand's per-game <c>Re2Randomiser</c>:
/// the game registers its helpers, the runner drives the shared loop.
///
/// <para>Loads rooms and runs the DC2 passes, writing the randomized room files <b>non-destructively</b>
/// to <paramref name="outputDir"/> (via <see cref="Dc2OutputDirSink"/>). Install overlays the output
/// directory onto the game's Data via <see cref="Install.GameInstaller"/>.</para>
/// </summary>
public sealed class Dc2RandomizerRunner
{
    private readonly DinoCrisis2 _game;

    // DC2 pass order. Gameplay/data passes precede model, voice, and title output/cosmetic passes.
    private readonly IReadOnlyList<IDc2RandomizationPass> _passes = new IDc2RandomizationPass[]
    {
        new Dc2ItemRandomizer(),
        new Dc2EnemyRandomizer(),
        new Dc2RaptorTierRandomizer(), // after the enemy pass: reads its working bytes, skips converted raptors
        new Dc2PlayerModelSwap(),
        new Dc2VoiceRandomizer(), // LIVE 2026-07-05: emits swapped Speech/NNNN.dat loose files
        new Dc2CircuitShuffle(),  // after the room-byte passes: builds on their working bytes (K110)
        new Dc2PlateKeyRekey(),   // ST205 SAT-9 routing + blue-panel recolour (K118)
        // Always-on cosmetic: seed watermark into TITLE.DAT/TITLE2.DAT (BioRand parity;
        // docs/decisions/cross/SEED-WATERMARK-PLAN.md). Last — it touches nothing other passes read.
        new Passes.Dc2TitleWatermarkPass(),
    };

    public Dc2RandomizerRunner(DinoCrisis2 game) => _game = game;

    /// <param name="emitSpoiler">Write the per-seed spoiler file (<c>&lt;encoded-seed&gt;_spoiler.md</c>) beside the room files (docs/decisions/cross/SPOILER-LOG-PLAN.md).
    /// Built strictly AFTER the passes have emitted every game file — turning it off changes no
    /// other output byte (regression-locked).</param>
    /// <param name="ct">Cancels the GENERATE phase only — see <see cref="RandomizerRunner.Run"/>;
    /// the overlay onto <c>Data\</c> is deliberately not cancellable.</param>
    public Dc2RunResult Run(string installDir, string outputDir, Seed seed, RandomizerConfig config,
                            bool emitSpoiler = true, CancellationToken ct = default)
    {
        var logLines = new List<string>();
        void Log(string line) => logLines.Add(line);

        Log($"DinoRand — {_game.DisplayName}");
        Log($"seed={seed}  install={installDir}");

        // 0. Prepare the (reused) working dir for this run.
        Dc2RunArtifactWriter.Prepare(outputDir, Log);

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

        // 2b/3. Emit per-game artifacts after every pass has completed.
        Dc2RunArtifactWriter.Write(outputDir, rooms, context, sink, seed, config,
            logLines, Log, emitSpoiler);

        return new Dc2RunResult(rooms.Count, sink.RoomsWritten, outputDir, logLines,
                                sink.WrittenFiles.ToList());
    }
}

/// <param name="WrittenFiles">The exact file names this run emitted to <paramref name="OutputDir"/> —
/// pass to <see cref="Install.GameInstaller.Install"/> as its <c>onlyFiles</c> allow-list so a stale
/// file in a reused dir is never installed (docs/decisions/dc2/install/DC2-INSTALL-INTEGRITY-PLAN.md).</param>
public sealed record Dc2RunResult(int RoomCount, int RoomsWritten, string OutputDir,
                                  IReadOnlyList<string> Log, IReadOnlyList<string> WrittenFiles);
