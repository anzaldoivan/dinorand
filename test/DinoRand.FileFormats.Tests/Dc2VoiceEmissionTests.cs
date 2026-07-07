using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Dc2.Passes;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Install;
using DinoRand.Randomizer.Voice;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The DC2 voice emission tail (docs/dc2/VOICE-DECODE-REPORT.md §5-6): the DC2 twin of
/// <see cref="VoiceEmissionTests"/>. Pins the RCA of the "swap didn't work in cutscenes" defect —
/// the pass was gated and emitted nothing, the installer didn't know the <c>Speech/</c> subtree, and
/// the output sink couldn't create the subdir. Classic REbirth reads <c>Speech\%04d.dat</c> itself
/// (ddraw.dll strings witness; CR ships no speech override — its wavebanks are SFX-only), so the
/// loose-file overwrite lever is valid on rebirth installs.
/// </summary>
public class Dc2VoiceEmissionTests
{
    private static VoiceClipSource Donor(string actor, int idx, string game) =>
        new(actor, game, VoiceKind.Dialogue, System.Array.Empty<string>(), idx, $"{actor}{idx}.wav");

    private static RandomizerConfig Config(
        bool crossGame = true, params (string target, string donor)[] donors) => new()
    {
        RandomizeVoices = true,
        IncludeCrossGameVoices = crossGame,
        VoiceDonors = donors.ToDictionary(d => d.target, d => d.donor),
    };

    // --- planner (pure) -----------------------------------------------------------------------

    [Fact]
    public void PlanDc2_MapsConfiguredCast_LeavesDefaultVanilla()
    {
        var pool = new[] { Donor("gail", 1, "dc1"), Donor("dylan", 1, "dc2") };
        var map = VoiceSwapPlanner.PlanDc2(
            Config(crossGame: true, ("regina", "gail.dc1"), ("old-dylan", "random")),
            pool, new System.Random(1));

        Assert.Equal("gail.dc1", map[Dc2VoiceActor.Regina]);
        Assert.True(map.ContainsKey(Dc2VoiceActor.OldDylan));   // random ⇒ resolved to some eligible key
        Assert.False(map.ContainsKey(Dc2VoiceActor.Dylan));     // no entry ⇒ Default ⇒ vanilla
        Assert.False(map.ContainsKey(Dc2VoiceActor.David));
        Assert.False(map.ContainsKey(Dc2VoiceActor.Unknown));   // unknown is never a swap target
    }

    [Fact]
    public void PlanDc2_CrossGameOff_OnlyDc2DonorsEligible()
    {
        // DC2's native gate is ".dc2" (NOT IsNativeDc1): with cross-game OFF and only DC1 donors in
        // the pool, there is nothing to draw from ⇒ empty map, pass is a no-op.
        var dc1Only = new[] { Donor("gail", 1, "dc1") };
        Assert.Empty(VoiceSwapPlanner.PlanDc2(
            Config(crossGame: false, ("regina", "random")), dc1Only, new System.Random(1)));

        var withDc2 = new[] { Donor("gail", 1, "dc1"), Donor("paula", 1, "dc2") };
        var map = VoiceSwapPlanner.PlanDc2(
            Config(crossGame: false, ("regina", "random")), withDc2, new System.Random(1));
        Assert.Equal("paula.dc2", map[Dc2VoiceActor.Regina]);   // the dc1 donor was filtered out
    }

    // --- emission build (synthetic pack, real loaders) ------------------------------------------

    private static string MakePacksRoot(string dir)
    {
        // One folder pack with a real (tiny) PCM WAV donor: data/voice/gail.dc1/gail-001.wav.
        var actorDir = Path.Combine(dir, "pack1", "data", "voice", "gail.dc1");
        Directory.CreateDirectory(actorDir);
        var wav = WavAudio.WritePcm16Mono(new PcmAudio(new float[2205], 22050));
        File.WriteAllBytes(Path.Combine(actorDir, "gail-001.wav"), wav);
        File.WriteAllBytes(Path.Combine(actorDir, "gail-002.wav"), wav);
        return dir;
    }

    [Fact]
    public void BuildFilesDc2_TranscodesMappedBanks_ToDc2Format()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "dc2_voice_emit_" + System.Guid.NewGuid());
        try
        {
            var packsRoot = MakePacksRoot(tmp);
            var manifest = Dc2VoiceManifest.Parse("""
            {
              "Speech/0001.dat": { "player": 0, "actor": "regina" },
              "Speech/0002.dat": { "player": 0, "actor": "regina" },
              "Speech/0003.dat": { "player": 0, "actor": "dylan" },
              "Speech/0004.dat": { "player": 0, "actor": "unknown" }
            }
            """);

            var files = VoiceEmission.BuildFilesDc2(
                Config(crossGame: true, ("regina", "gail.dc1")), packsRoot,
                new System.Random(3), new Dc2WavCodec(), manifest);

            // Only the remapped actor's banks; dylan (Default) and unknown stay vanilla.
            Assert.Equal(new[] { "Speech/0001.dat", "Speech/0002.dat" }, files.Keys.OrderBy(k => k));
            foreach (var bytes in files.Values)
            {
                Assert.Equal((byte)'R', bytes[0]);                                    // RIFF
                Assert.Equal(Dc2VoiceFormat.Channels, System.BitConverter.ToUInt16(bytes, 22));
                Assert.Equal(Dc2VoiceFormat.SampleRate, System.BitConverter.ToInt32(bytes, 24));
                Assert.Equal(Dc2VoiceFormat.BitsPerSample, System.BitConverter.ToUInt16(bytes, 34));
            }
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // --- pass composition + sink ----------------------------------------------------------------

    [Fact]
    public void Pass_GateOpen_EmitsSpeechBanksThroughSink()
    {
        Assert.True(Dc2VoiceManifestLayout.IsDecoded); // labels landed 2026-07-05 ⇒ the tail emits

        var tmp = Path.Combine(Path.GetTempPath(), "dc2_voice_pass_" + System.Guid.NewGuid());
        try
        {
            var packsRoot = MakePacksRoot(Path.Combine(tmp, "packs"));
            var outDir = Path.Combine(tmp, "out");
            var config = new RandomizerConfig
            {
                RandomizeVoices = true,
                IncludeCrossGameVoices = true,
                VoicePacksRoot = packsRoot,
                VoiceDonors = new Dictionary<string, string> { ["regina"] = "gail.dc1" },
            };

            var sink = new Dc2OutputDirSink(outDir);
            var log = new List<string>();
            var ctx = new Dc2RandomizationContext(
                new DinoCrisis2(), System.Array.Empty<FileFormats.Stage.Dc2.Dc2RoomFile>(),
                new Seed(1234), config, log.Add, sink);

            new Dc2VoiceRandomizer().Apply(ctx);

            // The shipped manifest labels 61 regina banks — every one gets a swapped WAV, written
            // under the Speech/ subdir (the sink must create it).
            Assert.Equal(61, sink.FilesWritten);
            Assert.All(sink.WrittenFiles, f => Assert.StartsWith("Speech/", f));
            Assert.True(File.Exists(Path.Combine(outDir, "Speech", "0005.dat"))
                        || sink.WrittenFiles.Any(f => File.Exists(Path.Combine(outDir, f))));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void Pass_NoPacksRootOrNoDonors_EmitsNothing()
    {
        var sink = new Dc2OutputDirSink(Path.Combine(Path.GetTempPath(), "dc2_voice_noop_" + System.Guid.NewGuid()));
        var ctx = new Dc2RandomizationContext(
            new DinoCrisis2(), System.Array.Empty<FileFormats.Stage.Dc2.Dc2RoomFile>(),
            new Seed(1234), new RandomizerConfig { RandomizeVoices = true }, _ => { }, sink);

        new Dc2VoiceRandomizer().Apply(ctx); // no VoicePacksRoot ⇒ logged no-op

        Assert.Equal(0, sink.FilesWritten);
    }

    // --- installer: Speech/ loose subtree --------------------------------------------------------

    [Fact]
    public void Install_OverlaysSpeechBank_AndRestoreReverses()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "dc2_speech_install_" + System.Guid.NewGuid());
        try
        {
            // Game root with Data\ (install anchor) and a vanilla Speech bank; mod dir with a swap.
            var dataDir = Path.Combine(tmp, "rebirth", "Data");
            var speechDir = Path.Combine(tmp, "rebirth", "Speech");
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(speechDir);
            var bank = Path.Combine(speechDir, "0001.dat");
            File.WriteAllBytes(bank, new byte[] { 1, 1, 1 });

            var modDir = Path.Combine(tmp, "mod");
            Directory.CreateDirectory(Path.Combine(modDir, "Speech"));
            File.WriteAllBytes(Path.Combine(modDir, "Speech", "0001.dat"), new byte[] { 2, 2, 2 });

            GameInstaller.Install(dataDir, modDir, seed: "test");
            Assert.Equal(new byte[] { 2, 2, 2 }, File.ReadAllBytes(bank)); // overlaid to game root

            GameInstaller.Restore(dataDir);
            Assert.Equal(new byte[] { 1, 1, 1 }, File.ReadAllBytes(bank)); // pristine back
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }
}
