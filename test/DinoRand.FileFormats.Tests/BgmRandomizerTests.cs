using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Bgm;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Voice;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// DC1 external BGM import + the shared BGM manifest/datapack/codec (docs/decisions/cross/BGM-RANDO-PLAN.md).
/// Covers the default-off flags, the mood-tag manifest parser + embedded defaults, the stereo-preserving
/// WAV I/O added for BGM, the ogg/wav→RIFF transcode (channel + rate conform), and the end-to-end import
/// pass against a synthetic install + datapack (no game data or fixtures needed).
/// </summary>
public class BgmRandomizerTests
{
    // --- flags --------------------------------------------------------------------------------

    [Fact]
    public void Flags_DefaultOff()
    {
        var cfg = new RandomizerConfig();
        Assert.False(cfg.RandomizeBgm);
        Assert.Null(cfg.BgmPacksRoot);
    }

    [Fact]
    public void Pass_IsEnabled_WhenToggleOn()
    {
        var pass = new BgmRandomizer();
        Assert.False(pass.IsEnabled(new RandomizerConfig()));
        Assert.True(pass.IsEnabled(new RandomizerConfig { RandomizeBgm = true }));
    }

    // --- manifest -----------------------------------------------------------------------------

    [Fact]
    public void Manifest_Parse_TagsSlots_SkipsMetadata_FallsBackToAll()
    {
        var m = BgmManifest.Parse("""
            { "_comment": "meta ignored", "safe": ["me_10", "me_11"], "danger": ["mf_20"] }
            """);
        Assert.Equal("safe", m.TagOf("me_10"));
        Assert.Equal("safe", m.TagOf("ME_11"));          // case-insensitive slot match
        Assert.Equal("danger", m.TagOf("mf_20"));
        Assert.Equal(BgmManifest.DefaultTag, m.TagOf("mr_99")); // untagged ⇒ 'all'
        Assert.Contains("safe", m.Tags);
        Assert.DoesNotContain("_comment", m.Tags);
    }

    [Fact]
    public void Manifest_LoadDefault_EmbeddedForBothGames()
    {
        // The v1 manifests ship with the mood tags declared but empty (no curation yet) ⇒ every slot 'all'.
        var dc1 = BgmManifest.LoadDefault("dc1");
        var dc2 = BgmManifest.LoadDefault("dc2");
        Assert.Equal(BgmManifest.DefaultTag, dc1.TagOf("me_00SL"));
        Assert.Equal(BgmManifest.DefaultTag, dc2.TagOf("MS_0000.DAT"));
        Assert.Contains("danger", dc1.Tags);             // schema/vocabulary present for future curation
    }

    // --- stereo WAV I/O (added for BGM) -------------------------------------------------------

    [Fact]
    public void WavAudio_Stereo_RoundTrips_ChannelsAndRate()
    {
        var interleaved = new float[128 * 2];
        for (int f = 0; f < 128; f++) { interleaved[f * 2] = f / 128f; interleaved[f * 2 + 1] = -f / 128f; }

        var wav = WavAudio.WritePcm16(interleaved, channels: 2, sampleRate: 44100);
        var (back, ch, rate) = WavAudio.ReadPcmInterleaved(wav);

        Assert.Equal(2, ch);
        Assert.Equal(44100, rate);
        Assert.Equal(interleaved.Length, back.Length);
        for (int i = 0; i < interleaved.Length; i++)
            Assert.True(Math.Abs(back[i] - interleaved[i]) < 0.001f);
    }

    // --- codec transcode --------------------------------------------------------------------

    [Fact]
    public void Codec_Transcode_ConformsChannelsAndRate_MonoToStereo()
    {
        var mono = WavAudio.WritePcm16(new float[500], channels: 1, sampleRate: 22050);
        using var src = new MemoryStream(mono);

        var outBytes = BgmCodec.Transcode(src, ".wav", targetChannels: 2, targetRate: 44100);
        var (samples, ch, rate) = WavAudio.ReadPcmInterleaved(outBytes);

        Assert.Equal(2, ch);
        Assert.Equal(44100, rate);
        Assert.Equal(0, samples.Length % 2);            // interleaved stereo
        Assert.True(samples.Length > 500);              // upsampled 22050 -> 44100
    }

    [Fact]
    public void Codec_Transcode_DownmixesStereoToMono()
    {
        var stereo = WavAudio.WritePcm16(new float[400 * 2], channels: 2, sampleRate: 44100);
        using var src = new MemoryStream(stereo);

        var outBytes = BgmCodec.Transcode(src, ".wav", targetChannels: 1, targetRate: 44100);
        var (_, ch, rate) = WavAudio.ReadPcmInterleaved(outBytes);

        Assert.Equal(1, ch);
        Assert.Equal(44100, rate);
    }

    [Fact]
    public void Codec_Transcode_CapsLength()
    {
        // A donor far longer than the cap is truncated to MaxSeconds at the target rate.
        int rate = 44100;
        var longMono = WavAudio.WritePcm16(new float[(int)(BgmCodec.MaxSeconds * rate) + rate * 30], 1, rate);
        using var src = new MemoryStream(longMono);

        var (samples, _, _) = WavAudio.ReadPcmInterleaved(BgmCodec.Transcode(src, ".wav", 1, rate));
        Assert.Equal((long)(BgmCodec.MaxSeconds * rate), samples.Length);
    }

    // --- end-to-end import pass ---------------------------------------------------------------

    [Fact]
    public void Emit_NoPacksRoot_IsNoOp()
    {
        var ctx = NewContext(new RandomizerConfig { RandomizeBgm = true }, installDir: null);
        BgmRandomizer.Emit(ctx);
        Assert.Empty(ctx.LooseFiles);
    }

    [Fact]
    public void Emit_EmptyPack_IsNoOp()
    {
        using var tmp = new TempDir();
        var packs = tmp.Sub("packs");                    // exists but holds no music
        var ctx = NewContext(new RandomizerConfig { RandomizeBgm = true, BgmPacksRoot = packs }, tmp.Path);
        BgmRandomizer.Emit(ctx);
        Assert.Empty(ctx.LooseFiles);
    }

    [Fact]
    public void Emit_ImportsEverySlot_MatchingSlotFormat()
    {
        using var tmp = new TempDir();
        var (install, packs) = BuildInstallAndPack(tmp);

        var ctx = NewContext(new RandomizerConfig { RandomizeBgm = true, BgmPacksRoot = packs }, install);
        BgmRandomizer.Emit(ctx);

        // One overwrite per slot, keyed under Sound/BGM, each a valid RIFF at that slot's own format.
        Assert.Equal(3, ctx.LooseFiles.Count);
        Assert.Contains("Sound/BGM/me_00.dat", ctx.LooseFiles.Keys);

        var stereo44 = ctx.LooseFiles["Sound/BGM/me_00.dat"];      // slot authored stereo 44100
        var (_, ch, rate) = WavAudio.ReadPcmInterleaved(stereo44);
        Assert.Equal(2, ch);
        Assert.Equal(44100, rate);

        var mono22 = ctx.LooseFiles["Sound/BGM/mf_01.dat"];        // slot authored mono 22050
        var (_, ch2, rate2) = WavAudio.ReadPcmInterleaved(mono22);
        Assert.Equal(1, ch2);
        Assert.Equal(22050, rate2);
    }

    [Fact]
    public void Emit_IsDeterministic_ForSeed()
    {
        using var tmp = new TempDir();
        var (install, packs) = BuildInstallAndPack(tmp);
        var cfg = new RandomizerConfig { RandomizeBgm = true, BgmPacksRoot = packs };

        var a = NewContext(cfg, install, seed: 99);
        var b = NewContext(cfg, install, seed: 99);
        BgmRandomizer.Emit(a);
        BgmRandomizer.Emit(b);

        Assert.Equal(a.LooseFiles.Keys.OrderBy(k => k), b.LooseFiles.Keys.OrderBy(k => k));
        foreach (var key in a.LooseFiles.Keys)
            Assert.Equal(a.LooseFiles[key], b.LooseFiles[key]);   // byte-identical for the same seed
    }

    // --- helpers ------------------------------------------------------------------------------

    private static RandomizationContext NewContext(RandomizerConfig cfg, string? installDir, int seed = 1) =>
        new(new DinoCrisis1(), Array.Empty<RoomFile>(), new RoomGraph(),
            new Seed(seed), cfg, _ => { }, installDir);

    /// <summary>A synthetic DC1 install (3 RIFF slots under Sound/BGM) + a datapack (one 'all' donor track).</summary>
    private static (string Install, string Packs) BuildInstallAndPack(TempDir tmp)
    {
        var bgmDir = tmp.Sub(Path.Combine("game", "Sound", "BGM"));
        WriteRiff(Path.Combine(bgmDir, "me_00.dat"), channels: 2, rate: 44100, frames: 200);
        WriteRiff(Path.Combine(bgmDir, "mf_01.dat"), channels: 1, rate: 22050, frames: 200);
        WriteRiff(Path.Combine(bgmDir, "ms_02.dat"), channels: 2, rate: 44100, frames: 200);

        var allDir = tmp.Sub(Path.Combine("packs", "mypack", "data", "bgm", "all"));
        WriteRiff(Path.Combine(allDir, "track_a.wav"), channels: 2, rate: 32000, frames: 300);
        WriteRiff(Path.Combine(allDir, "track_b.wav"), channels: 1, rate: 16000, frames: 300);

        return (Path.Combine(tmp.Path, "game"), Path.Combine(tmp.Path, "packs"));
    }

    private static void WriteRiff(string path, int channels, int rate, int frames)
    {
        var interleaved = new float[frames * channels];
        for (int i = 0; i < interleaved.Length; i++) interleaved[i] = (i % 64) / 64f - 0.5f;
        File.WriteAllBytes(path, WavAudio.WritePcm16(interleaved, channels, rate));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dinorand_bgm_" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string Sub(string rel)
        {
            var p = System.IO.Path.Combine(Path, rel);
            Directory.CreateDirectory(p);
            return p;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }
}
