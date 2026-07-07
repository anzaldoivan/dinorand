using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Passes;
using DinoRand.Randomizer.Voice;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Phase 4 cutscene character-voice swap (docs/dc1/VOICE-RANDO-PLAN.md). Covers the default-off flags,
/// the hard gate that keeps the pass emitting nothing, the BioRand filename-convention parser, the
/// datapack loader (against the in-repo dinocrisis pack), the seeded swap planner, and the deferred
/// model-swap / ogg-codec placeholders.
/// </summary>
public class VoiceRandomizerTests
{
    // --- flags + gate -------------------------------------------------------------------------

    [Fact]
    public void Flags_DefaultOff()
    {
        var cfg = new RandomizerConfig();
        Assert.False(cfg.RandomizeVoices);
        Assert.Null(cfg.VoiceDonors);
        Assert.False(cfg.IncludeCrossGameVoices);
    }

    [Fact]
    public void Pass_IsEnabled_WhenMasterToggleOn()
    {
        var pass = new VoiceRandomizer();
        Assert.False(pass.IsEnabled(new RandomizerConfig()));
        Assert.True(pass.IsEnabled(new RandomizerConfig { RandomizeVoices = true }));
    }

    [Fact]
    public void Manifest_IsDecoded_SoTheGateIsOpen()
    {
        Assert.True(VoiceManifestLayout.IsDecoded);
    }

    [Fact]
    public void Apply_WhenEnabledWithoutPacksRoot_IsNoOp_LogsButDoesNotThrow()
    {
        var log = new List<string>();
        var ctx = new RandomizationContext(
            new DinoCrisis1(), Array.Empty<RoomFile>(), new RoomGraph(),
            new Seed(1234), new RandomizerConfig { RandomizeVoices = true }, // no VoicePacksRoot ⇒ nothing to draw
            log.Add);

        var ex = Record.Exception(() => new VoiceRandomizer().Apply(ctx));

        Assert.Null(ex);
        Assert.Empty(ctx.LooseFiles);        // no donor source ⇒ no voice banks emitted
        Assert.Empty(ctx.ExePatchRequests);
    }

    // --- filename convention parser -----------------------------------------------------------

    [Fact]
    public void FileName_Parses_PlainIndex()
    {
        var p = VoiceFileName.Parse("regina001");
        Assert.Equal(1, p.Index);
        Assert.Equal(VoiceKind.Dialogue, p.Kind);
        Assert.Empty(p.Conditions);
    }

    [Fact]
    public void FileName_Parses_KindToken()
    {
        var p = VoiceFileName.Parse("Regina139_radio");
        Assert.Equal(139, p.Index);
        Assert.Equal(VoiceKind.Radio, p.Kind);
        Assert.Empty(p.Conditions);
    }

    [Fact]
    public void FileName_Parses_SingleCondition()
    {
        var p = VoiceFileName.Parse("regina002-nokirk");
        Assert.Equal(2, p.Index);
        Assert.Equal(new[] { "nokirk" }, p.Conditions);
    }

    [Fact]
    public void FileName_Parses_MultipleConditions()
    {
        var p = VoiceFileName.Parse("regina023-kirk-william");
        Assert.Equal(23, p.Index);
        Assert.Equal(new[] { "kirk", "william" }, p.Conditions);
    }

    // --- datapack loader (real in-repo dinocrisis pack) ---------------------------------------

    private static string DinocrisisPack =>
        Path.Combine(RepoRoot(), "biorand", "datapacks", "dinocrisis");

    [Fact]
    public void DataPack_Loads_ReginaCorpus_FromRealPack()
    {
        if (!Directory.Exists(DinocrisisPack)) return; // pack is read-only fixture; skip if absent

        var clips = VoiceDataPack.Load(DinocrisisPack);

        Assert.NotEmpty(clips);
        Assert.All(clips, c => Assert.Equal("regina", c.Actor)); // pack only ships Regina (open string identity)
        Assert.All(clips, c => Assert.Equal("dc1", c.Game));
        Assert.All(clips, c => Assert.True(c.IsNativeDc1));
        Assert.Contains(clips, c => c.Kind == VoiceKind.Radio);   // Regina139_radio
        Assert.Contains(clips, c => c.Kind == VoiceKind.Hurt);    // data/hurt/regina.dc1/*
        Assert.Contains(clips, c => c.Conditions.Contains("nokirk"));
    }

    [Fact]
    public void DataPack_Load_EmptyOnMissingDir()
    {
        Assert.Empty(VoiceDataPack.Load(Path.Combine(Path.GetTempPath(), "no_such_pack_" + Guid.NewGuid())));
    }

    [Fact]
    public void DataPack_ParsesActorAndGame_FromFolderSuffix()
    {
        Assert.True(VoiceDataPack.TryParseActor("regina", out var a) && a == VoiceActor.Regina);
        Assert.True(VoiceDataPack.TryParseActor("KIRK", out var k) && k == VoiceActor.Kirk);
        Assert.False(VoiceDataPack.TryParseActor("leon", out _));     // not in DC1 cast
        Assert.False(VoiceDataPack.TryParseActor("unknown", out _));  // the sentinel is rejected
    }

    // --- swap planner -------------------------------------------------------------------------

    private static IReadOnlyList<VoiceClipSource> Pool(params (VoiceActor a, string g)[] actors) =>
        actors.Select(x => new VoiceClipSource(
            x.a.ToString().ToLowerInvariant(), x.g, VoiceKind.Dialogue, Array.Empty<string>(), 1, "x.ogg")).ToList();

    [Fact]
    public void Planner_MasterOff_IsNoOp()
    {
        var map = VoiceSwapPlanner.Plan(new RandomizerConfig(),
            Pool((VoiceActor.Gail, "dc1")), new Random(1));
        Assert.True(map.IsEmpty);
    }

    // Every swappable target set to "random" (what the App produces when each dropdown is on Random).
    private static Dictionary<string, string> AllRandom() =>
        VoiceSwapPlanner.SwappableCast.ToDictionary(
            a => a.ToString().ToLowerInvariant(), _ => "random", StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Planner_MasterOn_AllDefault_IsNoOp()
    {
        // Master on but no per-character entries ⇒ everyone is on Default (keep own voice) ⇒ nothing maps.
        var map = VoiceSwapPlanner.Plan(
            new RandomizerConfig { RandomizeVoices = true },
            Pool((VoiceActor.Gail, "dc1"), (VoiceActor.Kirk, "dc1")), new Random(7));
        Assert.True(map.IsEmpty);
    }

    [Fact]
    public void Planner_RandomEntries_RemapTheWholeSwappableCast()
    {
        var map = VoiceSwapPlanner.Plan(
            new RandomizerConfig { RandomizeVoices = true, VoiceDonors = AllRandom() },
            Pool((VoiceActor.Gail, "dc1"), (VoiceActor.Kirk, "dc1")), new Random(7));

        Assert.False(map.IsEmpty);
        foreach (var member in VoiceSwapPlanner.SwappableCast)
            Assert.Contains(member, map.Entries.Keys);
        Assert.DoesNotContain(VoiceActor.Cooper, map.Entries.Keys); // Cooper/Tom/Colonel left vanilla
    }

    [Fact]
    public void Planner_DefaultEntries_AreLeftVanilla()
    {
        // Only Regina is pinned; the other three have no entry ⇒ Default ⇒ untouched.
        var cfg = new RandomizerConfig
        {
            RandomizeVoices = true,
            VoiceDonors = new Dictionary<string, string> { ["regina"] = "gail", ["rick"] = "random" },
        };
        var map = VoiceSwapPlanner.Plan(cfg,
            Pool((VoiceActor.Gail, "dc1"), (VoiceActor.Kirk, "dc1")), new Random(3));

        Assert.True(map.TryResolve(VoiceActor.Regina, out var regina));
        Assert.Equal("gail.dc1", regina);                    // pinned donor honoured (game-specific key)
        Assert.True(map.TryResolve(VoiceActor.Rick, out _));  // "random" ⇒ mapped
        Assert.DoesNotContain(VoiceActor.Gail, map.Entries.Keys); // Default ⇒ vanilla
        Assert.DoesNotContain(VoiceActor.Kirk, map.Entries.Keys); // Default ⇒ vanilla
    }

    // A cross-game actor present in several games (game suffix varies). Built raw — Claire isn't DC1 cast.
    private static IReadOnlyList<VoiceClipSource> ClairePool(params string[] games) =>
        games.Select((g, i) => new VoiceClipSource(
            "claire", g, VoiceKind.Dialogue, Array.Empty<string>(), i + 1, "x.ogg")).ToList();

    [Fact]
    public void Planner_GameSpecificPin_IsHonoured_NotMixed()
    {
        var cfg = new RandomizerConfig
        {
            RandomizeVoices = true,
            IncludeCrossGameVoices = true,
            VoiceDonors = new Dictionary<string, string> { ["regina"] = "claire.recv" },
        };
        var map = VoiceSwapPlanner.Plan(cfg, ClairePool("recv", "re2", "re2r"), new Random(1));

        Assert.True(map.TryResolve(VoiceActor.Regina, out var donor));
        Assert.Equal("claire.recv", donor); // exact game-specific performance, not bare "claire"
    }

    [Fact]
    public void Planner_Random_ResolvesToOneGameSpecificDonor()
    {
        var cfg = new RandomizerConfig
        {
            RandomizeVoices = true,
            IncludeCrossGameVoices = true,
            VoiceDonors = new Dictionary<string, string> { ["regina"] = "random" },
        };
        var map = VoiceSwapPlanner.Plan(cfg, ClairePool("re2", "re2r"), new Random(1));

        Assert.True(map.TryResolve(VoiceActor.Regina, out var donor));
        Assert.Contains('.', donor);                       // a single "actor.game" key, never bare "claire"
        Assert.Contains(donor, new[] { "claire.re2", "claire.re2r" });
    }

    [Fact]
    public void Planner_CrossGame_GatedByFlag()
    {
        var pool = Pool((VoiceActor.Kirk, "re2")); // only a cross-game donor available
        var donors = AllRandom();

        var off = VoiceSwapPlanner.Plan(
            new RandomizerConfig { RandomizeVoices = true, VoiceDonors = donors }, pool, new Random(5));
        Assert.True(off.IsEmpty); // cross-game excluded ⇒ no donor actors ⇒ no-op

        var on = VoiceSwapPlanner.Plan(
            new RandomizerConfig { RandomizeVoices = true, VoiceDonors = donors, IncludeCrossGameVoices = true },
            pool, new Random(5));
        Assert.False(on.IsEmpty);
    }

    [Fact]
    public void Planner_IsDeterministic_ForSeedAndConfig()
    {
        var cfg = new RandomizerConfig { RandomizeVoices = true, VoiceDonors = AllRandom() };
        var pool = Pool((VoiceActor.Gail, "dc1"), (VoiceActor.Kirk, "dc1"), (VoiceActor.Rick, "dc1"));

        var a = VoiceSwapPlanner.Plan(cfg, pool, new Random(42));
        var b = VoiceSwapPlanner.Plan(cfg, pool, new Random(42));

        foreach (var member in VoiceSwapPlanner.SwappableCast)
        {
            Assert.True(a.TryResolve(member, out var da));
            Assert.True(b.TryResolve(member, out var db));
            Assert.Equal(da, db);
        }
    }

    // --- deferred placeholders ----------------------------------------------------------------

    [Fact]
    public void CharacterModelSwap_IsDeferred_AndThrows()
    {
        Assert.Throws<NotImplementedException>(
            () => CharacterModelSwap.Swap(VoiceActor.Gail, VoiceActor.Kirk));
    }

    // --- audio codec (NVorbis ogg decode + DC1 8-bit encode) ----------------------------------

    private static string ReginaPack =>
        Path.Combine(RepoRoot(), "biorand", "datapacks", "dinocrisis", "data", "voice", "regina.dc1");

    [Fact]
    public void WavAudio_RoundTrips_16BitAnd8Bit_Mono()
    {
        // A short ramp; assert read→write→read preserves the canonical mono signal within quantization.
        var samples = new float[64];
        for (int i = 0; i < samples.Length; i++) samples[i] = (i / 32f) - 1f; // [-1, 1)
        var src = new PcmAudio(samples, 22050);

        var wav16 = WavAudio.WritePcm16Mono(src);
        var back16 = WavAudio.ReadPcm(wav16);
        Assert.Equal(src.Samples.Length, back16.Samples.Length);
        for (int i = 0; i < samples.Length; i++)
            Assert.True(Math.Abs(back16.Samples[i] - samples[i]) < 0.001f);

        var wav8 = WavAudio.WritePcm8Mono(src);
        var back8 = WavAudio.ReadPcm(wav8);
        for (int i = 0; i < samples.Length; i++)
            Assert.True(Math.Abs(back8.Samples[i] - samples[i]) < 0.02f); // 8-bit ⇒ coarser
    }

    [Fact]
    public void WavAudio_Resample_HitsTargetRate_AndScalesLength()
    {
        var src = new PcmAudio(new float[1000], 18900); // donor rate
        var dst = src.Resample(Dc1VoiceFormat.SampleRate);
        Assert.Equal(Dc1VoiceFormat.SampleRate, dst.SampleRate);
        Assert.InRange(dst.Samples.Length, 1100, 1200); // ~1000 * 22050/18900 ≈ 1167
    }

    [Fact]
    public void PcWavCodec_DecodesRealReginaOgg_ToCanonicalMonoWav()
    {
        var ogg = Directory.Exists(ReginaPack)
            ? Directory.EnumerateFiles(ReginaPack, "*.ogg").OrderBy(p => p).FirstOrDefault()
            : null;
        if (ogg == null) return; // donor pack is a read-only fixture; skip if absent

        var wav = new PcWavCodec().DecodeToWav(ogg);
        var pcm = WavAudio.ReadPcm(wav); // canonical decode must be valid RIFF/WAVE PCM
        Assert.True(pcm.Samples.Length > 0);
        Assert.True(pcm.SampleRate > 0);
    }

    [Fact]
    public void PcWavCodec_EncodeForTarget_ProducesDc1VoiceFormat_16BitMono22k()
    {
        var ogg = Directory.Exists(ReginaPack)
            ? Directory.EnumerateFiles(ReginaPack, "*.ogg").OrderBy(p => p).FirstOrDefault()
            : null;
        if (ogg == null) return;

        var codec = new PcWavCodec();
        var target = codec.EncodeForTarget(codec.DecodeToWav(ogg));

        // Header must declare the verified DC1 voice-slot format: PCM, mono, 22050 Hz, 16-bit.
        Assert.Equal((byte)'R', target[0]);
        Assert.Equal(1, BitConverter.ToUInt16(target, 20));                      // PCM
        Assert.Equal(Dc1VoiceFormat.Channels, BitConverter.ToUInt16(target, 22));
        Assert.Equal(Dc1VoiceFormat.SampleRate, BitConverter.ToInt32(target, 24));
        Assert.Equal(Dc1VoiceFormat.BitsPerSample, BitConverter.ToUInt16(target, 34));
        Assert.True(WavAudio.ReadPcm(target).Samples.Length > 0);               // re-parses cleanly
    }

    [Fact]
    public void PcWavCodec_EncodeForTarget_PreservesGivenSlotSampleRate()
    {
        // A synthetic 18900 Hz mono donor (self-contained; no fixture needed).
        var donor = WavAudio.WritePcm16Mono(new PcmAudio(new float[512], 18900));
        var codec = new PcWavCodec();

        // Default (null) ⇒ the 22050 Hz slot rate; an explicit rate ⇒ that slot's native rate (e.g. 44100).
        Assert.Equal(Dc1VoiceFormat.SampleRate, BitConverter.ToInt32(codec.EncodeForTarget(donor), 24));
        Assert.Equal(44100, BitConverter.ToInt32(codec.EncodeForTarget(donor, 44100), 24));
        // Both stay 16-bit mono regardless of rate.
        var at44k = codec.EncodeForTarget(donor, 44100);
        Assert.Equal(1, BitConverter.ToUInt16(at44k, 22));
        Assert.Equal(16, BitConverter.ToUInt16(at44k, 34));
    }

    // --- DC1 on-disk voice inventory (real install, skipped if absent) -------------------------

    private static string? InstallVoiceDir =>
        Dc1VoiceCatalog.FindVoiceDir(Path.Combine(RepoRoot(), "english"));

    [Fact]
    public void VoiceCatalog_Enumerates_RealInstall_AllDialogueIsMono22k16Bit()
    {
        var dir = InstallVoiceDir;
        if (dir == null) return; // no game data on this machine; skip

        var files = Dc1VoiceCatalog.Enumerate(dir);
        Assert.NotEmpty(files);

        var dialogue = files.Where(f => f.IsDialogue).ToList();
        Assert.NotEmpty(dialogue);
        Assert.All(dialogue, f =>
        {
            Assert.Equal(Dc1VoiceFormat.Channels, f.Channels);          // xa* dialogue is mono
            // 647/649 are 22050 Hz; xa3041ca.dat and xa60000.dat ship at 44100 Hz (real-data outliers).
            Assert.Contains(f.SampleRate, new[] { Dc1VoiceFormat.SampleRate, 44100 });
            Assert.Equal(Dc1VoiceFormat.BitsPerSample, f.BitsPerSample);// 16-bit
        });
    }

    [Fact]
    public void VoiceCatalog_FindVoiceDir_NullWhenAbsent()
    {
        Assert.Null(Dc1VoiceCatalog.FindVoiceDir(Path.Combine(Path.GetTempPath(), "no_install_" + Guid.NewGuid())));
    }

    // --- helpers ------------------------------------------------------------------------------

    /// <summary>Walk up from the test bin dir to the repo root (the folder holding <c>biorand/</c>).</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "biorand")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
