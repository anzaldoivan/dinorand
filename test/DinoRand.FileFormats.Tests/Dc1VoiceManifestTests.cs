using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Voice;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Locks the DC1 target voice manifest (<c>data/dc1/voice.json</c>, docs/dc1/VOICE-RANDO-PLAN.md §11):
/// BioRand-format <c>path → {player, actor, kind}</c>, filled with the user-confirmed Regina banks.
/// Guards that it loads, names only known cast, and — against the real install — that every labelled
/// bank actually exists. No bytes are emitted (labelling only; the emission gate stays closed).
/// </summary>
public class Dc1VoiceManifestTests
{
    [Fact]
    public void Manifest_Parses_BioRandFormat_AndSkipsMetadataKeys()
    {
        const string json = """
        {
          "_source": "test",
          "_actorsLabelled": ["regina"],
          "Sound/VOICE/xa_ep09b.dat": { "player": 0, "actor": "regina" },
          "Sound/VOICE/xa10f00.dat": { "player": 0, "actor": "regina", "kind": "radio" }
        }
        """;
        var m = Dc1VoiceManifest.Parse(json);

        Assert.Equal(2, m.Clips.Count); // the two underscore keys are skipped
        Assert.All(m.Clips, c => Assert.Equal(VoiceActor.Regina, c.Actor));
        Assert.Contains(m.Clips, c => c.Path == "Sound/VOICE/xa_ep09b.dat" && c.Kind == VoiceKind.Dialogue);
        Assert.Contains(m.Clips, c => c.Kind == VoiceKind.Radio); // explicit kind honoured
    }

    [Fact]
    public void Manifest_NonCastActor_MapsToUnknown_NotARegina()
    {
        // A non-cast / by-ear label (e.g. the endgame 'computer', or a donor name) is accepted and maps to
        // Unknown — recorded but never a Regina swap target (§12.8). A missing actor still throws.
        var m = Dc1VoiceManifest.Parse("""{ "Sound/VOICE/x.dat": { "actor": "computer" } }""");
        Assert.Equal(VoiceActor.Unknown, Assert.Single(m.Clips).Actor);

        Assert.Throws<InvalidDataException>(
            () => Dc1VoiceManifest.Parse("""{ "Sound/VOICE/x.dat": { "player": 0 } }"""));
    }

    [Fact]
    public void Manifest_LoadDefault_IsTheShippedReginaTable()
    {
        var m = Dc1VoiceManifest.LoadDefault();

        Assert.NotEmpty(m.Clips);
        Assert.All(m.Clips, c => Assert.StartsWith("Sound/VOICE/", c.Path));
        // The 2026-06-27 human folder-curation pass labelled the full cutscene cast: 630 banks across
        // regina/gail/rick/kirk/tom/colonel + the non-cast computer/computer-lab machine voices (the 19
        // 'others' banks stay unlabelled). Regina is still the largest single cast.
        Assert.InRange(m.Clips.Count, 600, 660);
        Assert.Contains(VoiceActor.Regina, m.LabelledActors);
        Assert.Contains(VoiceActor.Gail, m.LabelledActors);
        Assert.Contains(VoiceActor.Rick, m.LabelledActors);
        Assert.Contains(VoiceActor.Kirk, m.LabelledActors);   // full-cast curation (§12.8)
        Assert.Contains(VoiceActor.Tom, m.LabelledActors);
        Assert.Contains(VoiceActor.Colonel, m.LabelledActors);
        // computer / computer-lab are non-cast machine voices → recorded in voice.json but Unknown in-engine.
        Assert.Contains(VoiceActor.Unknown, m.LabelledActors);
        Assert.InRange(m.ClipsFor(VoiceActor.Regina).Count(), 150, 175); // Regina is the largest cast
    }

    [Fact]
    public void Manifest_EveryLabelledBank_ExistsInRealInstall()
    {
        var voiceDir = Dc1VoiceCatalog.FindVoiceDir(Path.Combine(RepoRoot(), "english"));
        if (voiceDir == null) return; // no game data on this machine; skip

        var install = Path.GetDirectoryName(Path.GetDirectoryName(voiceDir))!; // …/english
        foreach (var clip in Dc1VoiceManifest.LoadDefault().Clips)
            Assert.True(File.Exists(Path.Combine(install, clip.Path.Replace('/', Path.DirectorySeparatorChar))),
                $"manifest bank not found on disk: {clip.Path}");
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "biorand")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
