using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;
using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Dc2.Passes;
using DinoRand.Randomizer.Definitions;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The two DC2 room-editing passes driven directly through <see cref="Dc2RandomizationContext"/>
/// on synthetic rooms at REAL spawn-graph keys, with blobs sized to fit the graph's byte-cited
/// offsets — so the decision paths run with zero game bytes. Rooms used: ST201 (4 generic literal
/// spawns + a wave table) for the enemy pass, ST202 (two mode-0 raptor variant literals, no wave)
/// for the tier pass; ST407 (excluded set-piece) and ST704 (protected aquatic) for the skip gates.
/// </summary>
public class Dc2PassSyntheticTests
{
    // ST202's two mode-0 raptor spawns (data/dc2/spawn-graph.json): TYPE / VARIANT literal offsets.
    private const int St202TypeA = 6068, St202VarA = 6092;
    private const int St202TypeB = 6380, St202VarB = 6404;
    // ST104's two wave-descriptor species bytes (data/dc2/wave-descriptors.json type_off 0x2c9/0x649).
    private const int St104WaveTypeOffA = 0x2C9, St104WaveTypeOffB = 0x649;

    /// <summary>A single-LZSS0-entry DC2 package whose decompressed blob is <paramref name="blobLen"/>
    /// zeros with the given words preset — big enough for the room's real graph offsets.</summary>
    private static byte[] BigBlobRoom(int blobLen, params (int Off, short Value)[] words)
    {
        var blob = new byte[blobLen];
        foreach (var (off, v) in words) { blob[off] = (byte)v; blob[off + 1] = (byte)(v >> 8); }
        return SyntheticRoom.Package(GianPackage.Dc2EntrySize, (GianEntryType.Lzss0, Lzss.Compress(blob)));
    }

    /// <summary>Materialize one room on disk (the sink needs a SourcePath-named output file).</summary>
    private static Dc2RoomFile FileRoom(string dir, int stage, int room, byte[] package)
    {
        var path = Path.Combine(dir, $"ST{stage:X}{room:X2}.DAT");
        File.WriteAllBytes(path, package);
        return Dc2RoomFile.ReadFromFile(stage, room, path);
    }

    private static (Dc2RandomizationContext Ctx, Dc2OutputDirSink Sink, List<string> Log) NewContext(
        IReadOnlyList<Dc2RoomFile> rooms, RandomizerConfig config, string outDir, int seed = 42)
    {
        var log = new List<string>();
        var sink = new Dc2OutputDirSink(outDir);
        var ctx = new Dc2RandomizationContext(new DinoCrisis2(), rooms, new Seed(seed), config, log.Add, sink);
        return (ctx, sink, log);
    }

    // ---- enemy pass -------------------------------------------------------------------------------

    [Fact]
    public void EnemyPass_FixedMode_InvalidPin_SkipsEveryRoom()
    {
        var root = Directory.CreateTempSubdirectory("dc2pass").FullName;
        try
        {
            var room = FileRoom(root, 2, 1, BigBlobRoom(0x3000));
            var config = new RandomizerConfig
            {
                RandomizeEnemies = true,
                Dc2EnemyMode = Dc2EnemyDistributionMode.Fixed,
                Dc2FixedSpeciesType = null, // missing pin
            };
            var (ctx, sink, log) = NewContext(new[] { room }, config, Path.Combine(root, "out"));

            new Dc2EnemyRandomizer().Apply(ctx);

            Assert.Equal(0, sink.RoomsWritten);
            Assert.Contains(log, l => l.Contains("pass skipped"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EnemyPass_FixedPin_ConvertsSt104WaveDescriptors()
    {
        var root = Directory.CreateTempSubdirectory("dc2pass").FullName;
        try
        {
            // ST104 is a WAVE-ONLY room (zero op-0x1a spawns, two native-raptor descriptors) and is
            // not excluded/cutscene-flagged. A raptor pin would be refused by the donor≠native
            // collision rule, so pin Allosaurus (0x08).
            var room = FileRoom(root, 1, 4, BigBlobRoom(0x1000));
            var config = new RandomizerConfig
            {
                RandomizeEnemies = true,
                Dc2EnemyMode = Dc2EnemyDistributionMode.Fixed,
                Dc2FixedSpeciesType = 0x08,
            };
            var (ctx, sink, log) = NewContext(new[] { room }, config, Path.Combine(root, "out"));

            new Dc2EnemyRandomizer().Apply(ctx);

            Assert.Equal(1, sink.RoomsWritten);
            // both wave descriptors' species bytes (desc+1) now carry the pinned donor type
            var outBytes = File.ReadAllBytes(Path.Combine(root, "out", "ST104.DAT"));
            Assert.Equal(0x08, Dc2SpawnEditor.ReadByteFromPackage(outBytes, St104WaveTypeOffA));
            Assert.Equal(0x08, Dc2SpawnEditor.ReadByteFromPackage(outBytes, St104WaveTypeOffB));
            Assert.Contains(log, l => l.Contains("fixed=Allosaurus"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EnemyPass_ExcludedAndAquaticRooms_AreNeverTouched()
    {
        var root = Directory.CreateTempSubdirectory("dc2pass").FullName;
        try
        {
            // ST407 = excluded turret set-piece; ST704 = protected aquatic (Plesiosaurus).
            var setPiece = FileRoom(root, 4, 7, BigBlobRoom(0x3000));
            var aquatic = FileRoom(root, 7, 4, BigBlobRoom(0x3000));
            var config = new RandomizerConfig
            {
                RandomizeEnemies = true,
                Dc2EnemyMode = Dc2EnemyDistributionMode.Fixed,
                Dc2FixedSpeciesType = 0x02,
            };
            var (ctx, sink, log) = NewContext(new[] { setPiece, aquatic }, config, Path.Combine(root, "out"));

            new Dc2EnemyRandomizer().Apply(ctx);

            Assert.Equal(0, sink.RoomsWritten);
            Assert.Contains(log, l => l.Contains("1 set-piece rooms excluded") && l.Contains("1 aquatic rooms protected"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---- raptor tier pass ---------------------------------------------------------------------------

    private static byte[] St202Package(short typeValue) => BigBlobRoom(0x3000,
        (St202TypeA, typeValue), (St202TypeB, typeValue));

    [Fact]
    public void TierPass_RewritesRaptorVariants_Deterministically()
    {
        var root = Directory.CreateTempSubdirectory("dc2pass").FullName;
        try
        {
            var config = new RandomizerConfig { Dc2RandomizeRaptorTiers = true };
            var validVariants = Dc2RaptorTierTable.LoadEmbedded().Rows.Select(r => r.Variant).ToHashSet();

            short[] Run(string sub)
            {
                var dir = Directory.CreateDirectory(Path.Combine(root, sub)).FullName;
                var room = FileRoom(dir, 2, 2, St202Package(0x02)); // both spawns still raptor
                var (ctx, sink, _) = NewContext(new[] { room }, config, Path.Combine(dir, "out"), seed: 99);
                new Dc2RaptorTierRandomizer().Apply(ctx);
                Assert.Equal(1, sink.RoomsWritten);
                var bytes = File.ReadAllBytes(Path.Combine(dir, "out", "ST202.DAT"));
                // TYPE literals untouched — only the VARIANT words were rewritten
                Assert.Equal(0x02, Dc2SpawnEditor.ReadOperandFromPackage(bytes, St202TypeA));
                Assert.Equal(0x02, Dc2SpawnEditor.ReadOperandFromPackage(bytes, St202TypeB));
                return new[]
                {
                    Dc2SpawnEditor.ReadOperandFromPackage(bytes, St202VarA),
                    Dc2SpawnEditor.ReadOperandFromPackage(bytes, St202VarB),
                };
            }

            var a = Run("a");
            var b = Run("b");
            Assert.Equal(a, b);                                     // same seed ⇒ same draw
            Assert.All(a, v => Assert.Contains(v & 0xF, validVariants)); // tier nibble is a real tier
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TierPass_SpawnsConvertedAwayFromRaptor_AreSkipped()
    {
        var root = Directory.CreateTempSubdirectory("dc2pass").FullName;
        try
        {
            // Current TYPE = 0x06 (not raptor): the variant operand belongs to another category now —
            // rewriting it would clamp a foreign .TEX index. The pass must leave the room untouched.
            var room = FileRoom(root, 2, 2, St202Package(0x06));
            var (ctx, sink, _) = NewContext(new[] { room },
                new RandomizerConfig { Dc2RandomizeRaptorTiers = true }, Path.Combine(root, "out"));

            new Dc2RaptorTierRandomizer().Apply(ctx);
            Assert.Equal(0, sink.RoomsWritten);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TierPass_AllWeightsZero_WritesNothing()
    {
        var root = Directory.CreateTempSubdirectory("dc2pass").FullName;
        try
        {
            var zeroWeights = Dc2RaptorTierTable.LoadEmbedded().Rows
                .ToDictionary(r => r.Variant, _ => (byte)0);
            var room = FileRoom(root, 2, 2, St202Package(0x02));
            var (ctx, sink, _) = NewContext(new[] { room },
                new RandomizerConfig { Dc2RandomizeRaptorTiers = true, Dc2RaptorTierWeights = zeroWeights },
                Path.Combine(root, "out"));

            new Dc2RaptorTierRandomizer().Apply(ctx);
            Assert.Equal(0, sink.RoomsWritten);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
