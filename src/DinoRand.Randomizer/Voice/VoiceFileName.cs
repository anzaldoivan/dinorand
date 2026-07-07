using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Voice;

/// <summary>
/// Parses a BioRand donor clip filename into its metadata (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §3/§5.3),
/// mirroring BioRand's <c>GetThingsFromFileName</c> convention: <c>&lt;actor&gt;&lt;index&gt;_&lt;kind&gt;-&lt;cond&gt;-&lt;cond&gt;…</c>.
/// The <c>_kind</c> token and each <c>-condition</c> token are optional. Examples (from
/// <c>biorand/datapacks/dinocrisis/data/voice/regina.dc1/</c>):
/// <list type="bullet">
///   <item><c>regina001</c> → kind=Dialogue, no conditions, index 1</item>
///   <item><c>regina002-nokirk</c> → conditions [nokirk]</item>
///   <item><c>regina023-kirk-william</c> → conditions [kirk, william]</item>
///   <item><c>Regina139_radio</c> → kind=Radio</item>
/// </list>
/// Case-insensitive; the actor-name prefix is supplied by the folder, so only the trailing
/// index/kind/conditions are read here.
/// </summary>
public static class VoiceFileName
{
    /// <summary>The parsed pieces of a donor filename (stem only, no extension).</summary>
    public readonly record struct Parsed(int Index, VoiceKind Kind, IReadOnlyList<string> Conditions);

    /// <summary>
    /// Parse the filename <paramref name="stem"/> (without directory or extension). Tolerant: an
    /// unrecognised <c>_kind</c> token falls back to <see cref="VoiceKind.Dialogue"/>; a missing index
    /// yields 0. Conditions are lower-cased.
    /// </summary>
    public static Parsed Parse(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
            return new Parsed(0, VoiceKind.Dialogue, Array.Empty<string>());

        // Split conditions first (everything after the first '-'); the head holds actor+index+_kind.
        var dash = stem.Split('-');
        var head = dash[0];
        var conditions = dash.Skip(1)
            .Select(c => c.Trim().ToLowerInvariant())
            .Where(c => c.Length > 0)
            .ToArray();

        // The kind is the token after '_' in the head (if any); the rest is actor+index.
        VoiceKind kind = VoiceKind.Dialogue;
        var underscore = head.IndexOf('_');
        var actorAndIndex = head;
        if (underscore >= 0)
        {
            actorAndIndex = head[..underscore];
            kind = ParseKind(head[(underscore + 1)..]);
        }

        // The index is the trailing run of digits on the actor+index token.
        int i = actorAndIndex.Length;
        while (i > 0 && char.IsDigit(actorAndIndex[i - 1])) i--;
        int index = 0;
        if (i < actorAndIndex.Length)
            int.TryParse(actorAndIndex[i..], out index);

        return new Parsed(index, kind, conditions);
    }

    private static VoiceKind ParseKind(string token) => token.Trim().ToLowerInvariant() switch
    {
        "radio" => VoiceKind.Radio,
        "hurt" => VoiceKind.Hurt,
        "death" => VoiceKind.Death,
        _ => VoiceKind.Dialogue,
    };
}
