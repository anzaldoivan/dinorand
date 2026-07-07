using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Dc2.Passes;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Voice;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Locks the DC2 target voice scaffold (docs/dc2/VOICE-DECODE-REPORT.md): the manifest
/// (<c>data/dc2/voice.json</c>, BioRand format, every key a real rebirth Speech bank), the catalog's
/// decode of the real install, and the closed emission gate (<see cref="Dc2VoiceManifestLayout"/>) —
/// the DC2 pass must emit nothing until the human categorization pass labels the banks.
/// </summary>
public class Dc2VoiceManifestTests
{
    [Fact]
    public void Manifest_Parses_BioRandFormat_AndSkipsMetadataKeys()
    {
        const string json = """
        {
          "_source": "test",
          "_cast": ["dylan","regina"],
          "Speech/0000.dat": { "player": 0, "actor": "dylan" },
          "Speech/0001.dat": { "player": 0, "actor": "unknown" },
          "Speech/0002.dat": { "player": 0, "actor": "computer" },
          "Speech/0004.dat": { "player": 0, "actor": "old-dylan" }
        }
        """;
        var m = Dc2VoiceManifest.Parse(json);

        Assert.Equal(4, m.Clips.Count); // the two underscore keys are skipped
        Assert.Contains(m.Clips, c => c.Path == "Speech/0000.dat" && c.Actor == Dc2VoiceActor.Dylan);
        // the hyphenated folder-curation label maps to its enum member
        Assert.Contains(m.Clips, c => c.Path == "Speech/0004.dat" && c.Actor == Dc2VoiceActor.OldDylan);
        // 'unknown' and any non-cast label map to Unknown — recorded, never a swap target.
        Assert.Equal(2, m.ClipsFor(Dc2VoiceActor.Unknown).Count());

        Assert.Throws<InvalidDataException>(
            () => Dc2VoiceManifest.Parse("""{ "Speech/0003.dat": { "player": 0 } }"""));
    }

    [Fact]
    public void Manifest_LoadDefault_IsTheCuratedBankTable()
    {
        var m = Dc2VoiceManifest.LoadDefault();

        // 284 mono dialogue banks enumerated from the real rebirth Speech dir. The 2026-07-05 human
        // folder-curation pass (voice-export/dc2-characters/<actor>/NNNN.wav) labelled 214 of them;
        // the 70 unknown banks stay untouched (Paula curation is WIP — her lines are among them).
        Assert.Equal(284, m.Clips.Count);
        Assert.All(m.Clips, c => Assert.StartsWith("Speech/", c.Path));
        Assert.Equal(17, m.ClipsFor(Dc2VoiceActor.David).Count());
        Assert.Equal(118, m.ClipsFor(Dc2VoiceActor.Dylan).Count());
        Assert.Equal(18, m.ClipsFor(Dc2VoiceActor.OldDylan).Count());
        Assert.Equal(61, m.ClipsFor(Dc2VoiceActor.Regina).Count());
        Assert.Equal(70, m.ClipsFor(Dc2VoiceActor.Unknown).Count());
    }

    [Fact]
    public void Manifest_EveryBank_ExistsInRealInstall()
    {
        var install = RebirthDir();
        if (install == null) return; // no game data on this machine; skip

        foreach (var clip in Dc2VoiceManifest.LoadDefault().Clips)
            Assert.True(File.Exists(Path.Combine(install, clip.Path.Replace('/', Path.DirectorySeparatorChar))),
                $"manifest bank not found on disk: {clip.Path}");
    }

    [Fact]
    public void Catalog_Enumerates_RealInstall_AllDialogueIsMono18900Hz16Bit()
    {
        var speechDir = RebirthDir() is { } install ? Dc2VoiceCatalog.FindSpeechDir(install) : null;
        if (speechDir == null) return; // no game data on this machine; skip

        var files = Dc2VoiceCatalog.Enumerate(speechDir);
        Assert.Equal(305, files.Count); // 284 dialogue + 21 stereo non-voice
        var dialogue = files.Where(f => f.IsDialogue).ToList();
        Assert.Equal(284, dialogue.Count);
        Assert.All(dialogue, f =>
        {
            Assert.Equal(Dc2VoiceFormat.Channels, f.Channels);
            Assert.Equal(Dc2VoiceFormat.SampleRate, f.SampleRate);
            Assert.Equal(Dc2VoiceFormat.BitsPerSample, f.BitsPerSample);
        });
        Assert.All(files.Where(f => !f.IsDialogue), f => Assert.Equal(2, f.Channels));
    }

    [Fact]
    public void Catalog_FindSpeechDir_NullWhenAbsent()
    {
        Assert.Null(Dc2VoiceCatalog.FindSpeechDir(Path.Combine(Path.GetTempPath(), "no_install_" + Guid.NewGuid())));
    }

    [Fact]
    public void Pass_GateOpen_EmptyDonorPool_EmitsNothing()
    {
        Assert.True(Dc2VoiceManifestLayout.IsDecoded); // flipped 2026-07-05 (labels + RCA §6)

        var pass = new Dc2VoiceRandomizer();
        var emptyPacks = Path.Combine(Path.GetTempPath(), "dc2_voice_empty_" + Guid.NewGuid());
        Directory.CreateDirectory(emptyPacks);
        var config = new RandomizerConfig
        {
            RandomizeVoices = true, VoicePacksRoot = emptyPacks,
            VoiceDonors = new Dictionary<string, string> { ["regina"] = "random" },
        };
        Assert.True(pass.IsEnabled(config));
        Assert.False(pass.IsEnabled(new RandomizerConfig())); // same master toggle as DC1

        var log = new List<string>();
        var sink = new Dc2OutputDirSink(Path.Combine(Path.GetTempPath(), "dc2_voice_gate_" + Guid.NewGuid()));
        var ctx = new Dc2RandomizationContext(new DinoCrisis2(), Array.Empty<FileFormats.Stage.Dc2.Dc2RoomFile>(),
                                              new Seed(1234), config, log.Add, sink);
        pass.Apply(ctx);

        Assert.Equal(0, sink.RoomsWritten + sink.FilesWritten); // no eligible donors ⇒ no bytes
    }

    [Fact]
    public void Codec_EncodesToDc2DialogueFormat()
    {
        // A canonical 16-bit mono WAV at a foreign rate re-encodes to the DC2 bank form (16-bit/18900/mono).
        var source = WavAudio.WritePcm16Mono(new PcmAudio(new float[22050], 22050));
        var encoded = new Dc2WavCodec().EncodeForTarget(source);

        var pcm = WavAudio.ReadPcm(encoded);
        Assert.Equal(Dc2VoiceFormat.SampleRate, pcm.SampleRate);
    }

    private static string? RebirthDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "4249140_DinoCrisis2")))
            dir = Path.GetDirectoryName(dir);
        if (dir == null) return null;
        var rebirth = Path.Combine(dir, "4249140_DinoCrisis2", "rebirth");
        return Directory.Exists(rebirth) ? rebirth : null;
    }
}
