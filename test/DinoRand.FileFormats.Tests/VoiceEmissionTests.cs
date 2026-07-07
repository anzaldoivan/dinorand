using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Passes;
using DinoRand.Randomizer.Voice;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The voice emission spine (docs/dc1/VOICE-RANDO-PLAN.md §12): BioRand-parity selection
/// (<see cref="VoiceEmission.Plan"/>), the codec build (<see cref="VoiceEmission.BuildFiles"/>), the
/// loose-file context seam, and the pass composition. The codec path is exercised against the real
/// in-repo donor packs; these unit tests build bytes in memory and do not touch a game install.
/// </summary>
public class VoiceEmissionTests
{
    private static VoiceClipSource Donor(string actor, VoiceKind kind, int idx, string game = "dc1") =>
        new(actor, game, kind, System.Array.Empty<string>(), idx, $"{actor}{idx}.ogg");

    private static VoiceClip Target(string path, VoiceActor actor, VoiceKind kind = VoiceKind.Dialogue) =>
        new(path, actor, kind, 0, 0);

    private static CharacterVoiceMap Map(params (VoiceActor from, string to)[] entries)
    {
        var m = new CharacterVoiceMap();
        foreach (var (f, t) in entries) m.Set(f, t);
        return m;
    }

    // --- selection (pure) ---------------------------------------------------------------------

    [Fact]
    public void Plan_OnlyRemappedTargets_AreOverwritten()
    {
        var targets = new[] { Target("a.dat", VoiceActor.Regina), Target("b.dat", VoiceActor.Gail) };
        var pool = new[] { Donor("leon", VoiceKind.Dialogue, 1), Donor("leon", VoiceKind.Dialogue, 2) };

        var writes = VoiceEmission.Plan(targets, Map((VoiceActor.Regina, "leon")), pool, new System.Random(1));

        Assert.Single(writes);                          // Gail is unmapped ⇒ left vanilla (DE4)
        Assert.Equal("a.dat", writes[0].TargetBankPath);
        Assert.Equal("leon", writes[0].Donor.Actor);    // drew the mapped donor actor
    }

    [Fact]
    public void Plan_GameSpecificDonor_DrawsOnlyThatGame()
    {
        // claire exists in three games; a game-specific donor token ("claire.recv") must NOT mix in the
        // re2 / re2r performances (the cross-game mixing bug).
        var pool = new[]
        {
            Donor("claire", VoiceKind.Dialogue, 1, "recv"),
            Donor("claire", VoiceKind.Dialogue, 2, "recv"),
            Donor("claire", VoiceKind.Dialogue, 1, "re2"),
            Donor("claire", VoiceKind.Dialogue, 2, "re2r"),
        };
        var targets = new[]
        {
            Target("a.dat", VoiceActor.Regina), Target("b.dat", VoiceActor.Regina),
            Target("c.dat", VoiceActor.Regina),
        };

        var writes = VoiceEmission.Plan(targets, Map((VoiceActor.Regina, "claire.recv")), pool, new System.Random(1));

        Assert.Equal(3, writes.Count);
        Assert.All(writes, w => Assert.Equal("recv", w.Donor.Game)); // only the recv performance, never re2/re2r
    }

    [Fact]
    public void Plan_MatchesKind()
    {
        var targets = new[] { Target("r.dat", VoiceActor.Regina, VoiceKind.Radio) };
        var pool = new[] { Donor("leon", VoiceKind.Dialogue, 1), Donor("leon", VoiceKind.Radio, 2) };

        var writes = VoiceEmission.Plan(targets, Map((VoiceActor.Regina, "leon")), pool, new System.Random(1));

        Assert.Equal(VoiceKind.Radio, writes[0].Donor.Kind); // same-kind preferred
    }

    [Fact]
    public void Plan_FallsBackToAnyKind_WhenActorHasNoSuchKind()
    {
        var targets = new[] { Target("r.dat", VoiceActor.Regina, VoiceKind.Radio) };
        var pool = new[] { Donor("leon", VoiceKind.Dialogue, 1) }; // no radio at all

        var writes = VoiceEmission.Plan(targets, Map((VoiceActor.Regina, "leon")), pool, new System.Random(1));

        Assert.Single(writes);                               // still gets a clip (fallback)
        Assert.Equal(VoiceKind.Dialogue, writes[0].Donor.Kind);
    }

    [Fact]
    public void Plan_DrawsWithoutReplacement_UntilExhausted()
    {
        var targets = Enumerable.Range(0, 3).Select(i => Target($"t{i}.dat", VoiceActor.Regina)).ToArray();
        var pool = new[] { Donor("leon", VoiceKind.Dialogue, 1), Donor("leon", VoiceKind.Dialogue, 2),
                           Donor("leon", VoiceKind.Dialogue, 3) };

        var writes = VoiceEmission.Plan(targets, Map((VoiceActor.Regina, "leon")), pool, new System.Random(7));

        Assert.Equal(3, writes.Count);
        Assert.Equal(3, writes.Select(w => w.Donor.Index).Distinct().Count()); // no repeat across the 3
    }

    [Fact]
    public void Plan_UnmappedAndEmptyMap_ProduceNothing()
    {
        var targets = new[] { Target("a.dat", VoiceActor.Regina) };
        Assert.Empty(VoiceEmission.Plan(targets, new CharacterVoiceMap(),
            new[] { Donor("leon", VoiceKind.Dialogue, 1) }, new System.Random(1)));
    }

    [Fact]
    public void Plan_IsDeterministic()
    {
        var targets = Enumerable.Range(0, 5).Select(i => Target($"t{i}.dat", VoiceActor.Regina)).ToArray();
        var pool = Enumerable.Range(1, 6).Select(i => Donor("leon", VoiceKind.Dialogue, i)).ToArray();
        var map = Map((VoiceActor.Regina, "leon"));

        var a = VoiceEmission.Plan(targets, map, pool, new System.Random(42));
        var b = VoiceEmission.Plan(targets, map, pool, new System.Random(42));

        Assert.Equal(a.Select(w => w.Donor.Index), b.Select(w => w.Donor.Index));
    }

    // --- codec build (real donor packs) -------------------------------------------------------

    private static string PacksRoot => Path.Combine(RepoRoot(), "biorand", "datapacks");

    [Fact]
    public void BuildFiles_TranscodesMappedReginaBanks_ToDc1Format()
    {
        if (!Directory.Exists(PacksRoot)) return; // donor packs are a read-only fixture; skip if absent

        // A small target manifest so the test transcodes a few banks, not all 127.
        var manifest = Dc1VoiceManifest.Parse("""
        {
          "Sound/VOICE/xa10501.dat": { "player": 0, "actor": "regina" },
          "Sound/VOICE/xa10503.dat": { "player": 0, "actor": "regina" },
          "Sound/VOICE/xa_ep09b.dat": { "player": 0, "actor": "regina" }
        }
        """);
        // In-universe (cross-game off) ⇒ the only DC1 donor actor is Regina, so she maps to herself and
        // her real ripped oggs are the donors — enough to prove the transcode/output path end to end.
        // Regina is set to "random" (Default would leave her vanilla, emitting nothing).
        var config = new RandomizerConfig
        {
            RandomizeVoices = true,
            VoiceDonors = new System.Collections.Generic.Dictionary<string, string> { ["regina"] = "random" },
        };

        var files = VoiceEmission.BuildFiles(config, PacksRoot, new System.Random(3), new PcWavCodec(), manifest);

        Assert.Equal(3, files.Count);
        Assert.All(files.Keys, k => Assert.StartsWith("Sound/VOICE/", k));
        foreach (var bytes in files.Values)
        {
            Assert.Equal((byte)'R', bytes[0]);                               // RIFF
            Assert.Equal(Dc1VoiceFormat.Channels, BitConverter.ToUInt16(bytes, 22));
            Assert.Equal(Dc1VoiceFormat.SampleRate, BitConverter.ToInt32(bytes, 24));
            Assert.Equal(Dc1VoiceFormat.BitsPerSample, BitConverter.ToUInt16(bytes, 34));
        }
    }

    [Fact]
    public void BuildFiles_NoToggles_IsEmpty()
    {
        if (!Directory.Exists(PacksRoot)) return;
        var manifest = Dc1VoiceManifest.Parse("""{ "Sound/VOICE/x.dat": { "actor": "regina" } }""");
        Assert.Empty(VoiceEmission.BuildFiles(new RandomizerConfig(), PacksRoot, new System.Random(1),
            new PcWavCodec(), manifest));
    }

    // --- context seam + gated pass ------------------------------------------------------------

    [Fact]
    public void Context_AddLooseFile_NormalizesSeparators_AndRoundTrips()
    {
        var ctx = NewContext(new RandomizerConfig());
        ctx.AddLooseFile(@"Sound\VOICE\xa.dat", new byte[] { 1, 2, 3 });

        Assert.True(ctx.LooseFiles.ContainsKey("Sound/VOICE/xa.dat")); // backslashes normalized to '/'
        Assert.Equal(new byte[] { 1, 2, 3 }, ctx.LooseFiles["Sound/VOICE/xa.dat"]);
    }

    [Fact]
    public void Emit_WithoutPacksRoot_RegistersNothing()
    {
        var ctx = NewContext(new RandomizerConfig { RandomizeVoices = true }); // no VoicePacksRoot
        VoiceRandomizer.Emit(ctx);
        Assert.Empty(ctx.LooseFiles);
    }

    private static RandomizationContext NewContext(RandomizerConfig config) => new(
        new DinoCrisis1(), System.Array.Empty<RoomFile>(), new RoomGraph(),
        new Seed(1234), config, _ => { });

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "biorand")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
