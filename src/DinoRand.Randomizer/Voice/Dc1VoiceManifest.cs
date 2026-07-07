using System.Text.Json;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Voice;

/// <summary>
/// The DC1 <b>target</b> voice manifest (<c>data/dc1/voice.json</c>) — which install voice bank under
/// <c>Sound\VOICE\</c> belongs to which cast member, in BioRand's <c>voice.json</c> format
/// (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §3/§11). BioRand never shipped a DC1 target map; this is net-new,
/// authored by fingerprint-matching Regina's ripped corpus to the install banks (user-confirmed
/// 2026-06-27, docs/reference/dc1/voice/VOICE-DECODE-REPORT.md). Only Regina is labelled so far — the supporting cast
/// awaits cutscene-script RE (§9.2).
///
/// <para>This is the slot table the emission gate (<see cref="VoiceManifestLayout.IsDecoded"/>) guards:
/// the pass overwrites a labelled bank with a same-actor donor clip. Loading is pure inspection — no
/// audio is touched here.</para>
/// </summary>
public sealed class Dc1VoiceManifest
{
    /// <summary>Logical resource name of the embedded default manifest (see the .csproj EmbeddedResource).</summary>
    public const string DefaultResourceName = "DinoRand.Randomizer.Data.dc1.voice.json";

    private readonly IReadOnlyList<VoiceClip> _clips;

    private Dc1VoiceManifest(IReadOnlyList<VoiceClip> clips) => _clips = clips;

    /// <summary>Every labelled target slot (one per install bank).</summary>
    public IReadOnlyList<VoiceClip> Clips => _clips;

    /// <summary>The labelled slots for one actor (e.g. all of Regina's banks).</summary>
    public IEnumerable<VoiceClip> ClipsFor(VoiceActor actor) => _clips.Where(c => c.Actor == actor);

    /// <summary>The distinct actors the manifest currently labels.</summary>
    public IReadOnlySet<VoiceActor> LabelledActors => _clips.Select(c => c.Actor).ToHashSet();

    /// <summary>Load the embedded <c>data/dc1/voice.json</c>.</summary>
    public static Dc1VoiceManifest LoadDefault()
    {
        var asm = typeof(Dc1VoiceManifest).Assembly;
        using var stream = asm.GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded voice manifest '{DefaultResourceName}' not found. Resources: " +
                string.Join(", ", asm.GetManifestResourceNames()));
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Load a manifest from a JSON file path.</summary>
    public static Dc1VoiceManifest Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// Parse the BioRand-format manifest. Keys starting with <c>_</c> are metadata (provenance / notes)
    /// and skipped; every other key is an install-relative bank path mapping to <c>{player, actor, kind}</c>.
    /// A missing <c>kind</c> means <see cref="VoiceKind.Dialogue"/> (BioRand convention); an unknown actor
    /// is an error (the manifest must only name DC1 cast).
    /// </summary>
    public static Dc1VoiceManifest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var clips = new List<VoiceClip>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.StartsWith('_')) continue;          // metadata key
            var v = prop.Value;

            if (!v.TryGetProperty("actor", out var actorEl) || actorEl.GetString() is not { } actorName)
                throw new InvalidDataException($"Voice clip '{prop.Name}' has no actor.");
            // Known DC1 cast → their VoiceActor; any other label (a by-ear correction to a non-cast voice
            // such as the endgame 'computer', or — until §9.2 lands them in the enum — a supporting-cast
            // member) maps to Unknown, so it is simply NOT a Regina swap target. The real character string
            // is preserved in voice.json (the human-label record); the loader only needs Regina-vs-not.
            VoiceDataPack.TryParseActor(actorName, out var actor); // out = Unknown when not known cast

            var kind = VoiceKind.Dialogue;
            if (v.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String)
                kind = ParseKind(k.GetString()!);

            // RoomCode/Cutscene addressing is not yet decoded for these banks (§9.2); left 0.
            clips.Add(new VoiceClip(prop.Name, actor, kind, RoomCode: 0, Cutscene: 0));
        }

        return new Dc1VoiceManifest(clips);
    }

    private static VoiceKind ParseKind(string token) => token.Trim().ToLowerInvariant() switch
    {
        "radio" => VoiceKind.Radio,
        "hurt" => VoiceKind.Hurt,
        "death" => VoiceKind.Death,
        _ => VoiceKind.Dialogue,
    };
}
