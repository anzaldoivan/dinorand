using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Spoiler;
using DinoRand.Randomizer.Logic;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Both runners end-to-end over the SYNTHETIC mock corpora (BioRand's whole-runner smoke pattern,
/// <c>ref/classic .../TestRandomizer.cs</c>) — these always run, including on CI where the
/// real-install runner tests (<see cref="SpoilerRunnerTests"/>) skip. Pinned invariants: result
/// counts, outputs re-parse, same seed ⇒ identical room bytes, stale-file cleanup, spoiler
/// emit/delete, and the all-passes-off passthrough.
/// </summary>
public class RunnerMockCorpusTests
{
    private static string SpoilerPath(string outputDir, Seed seed, RandomizerConfig config) =>
        Path.Combine(outputDir, SpoilerLogBuilder.FileNameFor(SeedString.Encode(seed, config)));

    /// <summary>Temp DC1 install: <c>install/Data/st*.dat</c> copied from the mock corpus.</summary>
    private static string NewDc1Install(string root)
    {
        var dataDir = Directory.CreateDirectory(Path.Combine(root, "install", "Data")).FullName;
        foreach (var f in Directory.EnumerateFiles(MockRooms.Dc1DataDir(), "*.dat"))
            File.Copy(f, Path.Combine(dataDir, Path.GetFileName(f)));
        return Path.Combine(root, "install");
    }

    /// <summary>Temp DC2 install: <c>install/rebirth/Data/ST8[90-95].DAT</c> — stage-8 room ids
    /// OUTSIDE the embedded spawn graph, so the enemy pass exercises its skip paths rather than
    /// applying real-room offsets to small synthetic blobs.</summary>
    private static string NewDc2Install(string root)
    {
        var dataDir = Directory.CreateDirectory(Path.Combine(root, "install", "rebirth", "Data")).FullName;
        for (int r = 90; r <= 95; r++)
            File.WriteAllBytes(Path.Combine(dataDir, $"ST8{r}.DAT"), SyntheticRoom.Dc2Room(r - 90));
        return Path.Combine(root, "install");
    }

    private static string NewTemp() => Directory.CreateTempSubdirectory("runnermock").FullName;

    // ---- DC1 --------------------------------------------------------------------------------------

    [Fact]
    public void Dc1_Run_OutputsReparse_AndCountsAreConsistent()
    {
        var root = NewTemp();
        try
        {
            var install = NewDc1Install(root);
            var outDir = Path.Combine(root, "out");
            var config = new RandomizerConfig { EnsureBeatable = false };
            var result = new RandomizerRunner(new DinoCrisis1())
                .Run(install, outDir, new Seed(12345), config);

            Assert.Equal(12, result.RoomsWritten);
            // RoomCount = graph nodes, which includes phantom door-destination rooms outside the corpus
            Assert.True(result.RoomCount >= result.RoomsWritten);
            Assert.NotEmpty(result.Log);

            // every emitted room re-parses cleanly — the "never ship an unparseable room" invariant
            var outputs = Directory.EnumerateFiles(outDir, "*.dat").ToList();
            Assert.Equal(12, outputs.Count);
            foreach (var f in outputs)
            {
                var rf = RoomFile.Read(0, 0, File.ReadAllBytes(f));
                Assert.True(rf.ParsedCleanly, $"{Path.GetFileName(f)} did not re-parse cleanly");
            }
            Assert.True(File.Exists(Path.Combine(outDir, "log_dinorand.txt")));
            Assert.True(File.Exists(Path.Combine(outDir, "map.dgml")));
            Assert.True(File.Exists(SpoilerPath(outDir, new Seed(12345), config)));
            Assert.False(File.Exists(Path.Combine(outDir, SpoilerLogBuilder.LegacyFileName)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Dc1_SameSeed_ProducesIdenticalRoomBytes()
    {
        var root = NewTemp();
        try
        {
            var install = NewDc1Install(root);
            var runner = new RandomizerRunner(new DinoCrisis1());
            var config = new RandomizerConfig { EnsureBeatable = false };
            runner.Run(install, Path.Combine(root, "a"), new Seed(777), config);
            runner.Run(install, Path.Combine(root, "b"), new Seed(777), config);

            foreach (var fa in Directory.EnumerateFiles(Path.Combine(root, "a"), "*.dat"))
            {
                var fb = Path.Combine(root, "b", Path.GetFileName(fa));
                Assert.Equal(File.ReadAllBytes(fa), File.ReadAllBytes(fb));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Dc1_AllPassesDisabled_IsBytePassthrough()
    {
        var root = NewTemp();
        try
        {
            var install = NewDc1Install(root);
            var outDir = Path.Combine(root, "out");
            var config = new RandomizerConfig
            {
                RandomizeItems = false, RandomizeEnemies = false,
                ShuffleKeyItems = false, EnsureBeatable = false,
            };
            new RandomizerRunner(new DinoCrisis1()).Run(install, outDir, new Seed(5), config);

            foreach (var src in Directory.EnumerateFiles(Path.Combine(install, "Data"), "*.dat"))
                Assert.Equal(File.ReadAllBytes(src),
                    File.ReadAllBytes(Path.Combine(outDir, Path.GetFileName(src))));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Dc1_ReusedOutputDir_StaleFilesAreRemoved()
    {
        var root = NewTemp();
        try
        {
            var install = NewDc1Install(root);
            var outDir = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;

            // Leftovers from "another seed" must not survive, while unrelated markdown and nested
            // files remain untouched by the root-level spoiler cleanup.
            File.WriteAllBytes(Path.Combine(outDir, "stZ99.dat"), new byte[] { 1, 2, 3 });
            File.WriteAllText(Path.Combine(outDir, SpoilerLogBuilder.FileNameFor("DINO-stale")), "stale");
            File.WriteAllText(Path.Combine(outDir, SpoilerLogBuilder.LegacyFileName), "legacy");
            File.WriteAllText(Path.Combine(outDir, "notes.md"), "keep");
            var nestedSpoiler = Path.Combine(outDir, "nested", SpoilerLogBuilder.FileNameFor("DINO-nested"));
            Directory.CreateDirectory(Path.GetDirectoryName(nestedSpoiler)!);
            File.WriteAllText(nestedSpoiler, "keep");

            var config = new RandomizerConfig { EnsureBeatable = false };
            var result = new RandomizerRunner(new DinoCrisis1())
                .Run(install, outDir, new Seed(9),
                    config, emitSpoiler: false);

            Assert.False(File.Exists(Path.Combine(outDir, "stZ99.dat")), "stale .dat survived the run");
            Assert.False(File.Exists(Path.Combine(outDir, SpoilerLogBuilder.FileNameFor("DINO-stale"))),
                "suppressed run left a stale dynamic spoiler behind");
            Assert.False(File.Exists(Path.Combine(outDir, SpoilerLogBuilder.LegacyFileName)),
                "suppressed run left legacy SPOILER.md behind");
            Assert.True(File.Exists(Path.Combine(outDir, "notes.md")));
            Assert.True(File.Exists(nestedSpoiler), "nested files must not be part of root-level cleanup");
            Assert.Contains(result.Log, l => l.Contains("[clean]"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Dc1_InjectedFinalVerificationFailure_ProducesNoInstallableArtifacts()
    {
        var root = NewTemp();
        try
        {
            var install = NewDc1Install(root);
            var outDir = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;
            File.WriteAllBytes(Path.Combine(outDir, "st100.dat"), new byte[] { 1 });
            File.WriteAllText(Path.Combine(outDir, "exe-patch-plan.json"), "stale");
            var staleLoose = Path.Combine(outDir, "Sound", "VOICE", "stale.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(staleLoose)!);
            File.WriteAllBytes(staleLoose, new byte[] { 2 });
            var runner = new RandomizerRunner(new DinoCrisis1())
            {
                FinalProgressionVerifier = _ => new KeyItemPlacer.PlacementResult(
                    false, Array.Empty<(KeyItemPlacer.Spot, int)>(), new[] { "injected failure" })
            };

            Assert.Throws<InvalidOperationException>(() =>
                runner.Run(install, outDir, new Seed(1),
                    new RandomizerConfig { EnsureBeatable = false }));
            Assert.False(Directory.Exists(outDir)
                         && Directory.EnumerateFiles(outDir, "*.dat").Any());
            Assert.False(File.Exists(Path.Combine(outDir, "exe-patch-plan.json")));
            Assert.False(File.Exists(staleLoose));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---- DC2 --------------------------------------------------------------------------------------

    [Fact]
    public void Dc2_Run_CountsAndSpoiler_OnOutOfGraphCorpus()
    {
        var root = NewTemp();
        try
        {
            var install = NewDc2Install(root);
            var outDir = Path.Combine(root, "out");
            var config = new RandomizerConfig();
            var result = new Dc2RandomizerRunner(new DinoCrisis2())
                .Run(install, outDir, new Seed(1001), config);

            Assert.Equal(6, result.RoomCount);
            Assert.Equal(0, result.RoomsWritten);   // no spawn-graph entries ⇒ every room skipped
            Assert.Empty(result.WrittenFiles);
            Assert.True(File.Exists(SpoilerPath(outDir, new Seed(1001), config)));
            Assert.False(File.Exists(Path.Combine(outDir, SpoilerLogBuilder.LegacyFileName)));
            Assert.Contains(result.Log, l => l.Contains("[dc2-enemy]"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Dc2_SpoilerSuppressed_RemovesStaleSpoiler()
    {
        var root = NewTemp();
        try
        {
            var install = NewDc2Install(root);
            var outDir = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;
            File.WriteAllText(Path.Combine(outDir, SpoilerLogBuilder.FileNameFor("DINO-stale")), "stale");
            File.WriteAllText(Path.Combine(outDir, SpoilerLogBuilder.LegacyFileName), "legacy");

            new Dc2RandomizerRunner(new DinoCrisis2())
                .Run(install, outDir, new Seed(1), new RandomizerConfig(), emitSpoiler: false);
            Assert.False(File.Exists(Path.Combine(outDir, SpoilerLogBuilder.FileNameFor("DINO-stale"))));
            Assert.False(File.Exists(Path.Combine(outDir, SpoilerLogBuilder.LegacyFileName)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
