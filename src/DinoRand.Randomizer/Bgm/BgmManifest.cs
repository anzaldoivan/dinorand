using System.Text.Json;

namespace DinoRand.Randomizer.Bgm;

/// <summary>
/// A per-game BGM mood-tag manifest (<c>data/&lt;game&gt;/bgm.json</c>): <c>tag → [slot name]</c>, where a
/// slot name is the game's own track name (DC1 <c>Sound/BGM/</c> stem; DC2
/// <c>Dc2MusicTablePatch.CanonicalNames</c> entry). Tags are the mood axis BGM rando shuffles/imports
/// within — safe-room / event / stage music is just the tag a slot carries, no separate mechanism
/// (docs/decisions/cross/BGM-RANDO-PLAN.md). Keys starting with <c>_</c> are metadata and skipped
/// (mirrors <see cref="Voice.Dc1VoiceManifest"/>). Any slot not listed resolves to <see cref="DefaultTag"/>.
/// </summary>
public sealed class BgmManifest
{
    /// <summary>The fallback tag for any slot not explicitly listed (and the datapack's fallback donor folder).</summary>
    public const string DefaultTag = "all";

    // tag -> ordered slot names; and the reverse slot -> tag (a slot lands in at most one tag; first wins).
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _slotsByTag;
    private readonly IReadOnlyDictionary<string, string> _tagBySlot;

    private BgmManifest(
        IReadOnlyDictionary<string, IReadOnlyList<string>> slotsByTag,
        IReadOnlyDictionary<string, string> tagBySlot)
    {
        _slotsByTag = slotsByTag;
        _tagBySlot = tagBySlot;
    }

    /// <summary>The tags the manifest declares (excludes the implicit <see cref="DefaultTag"/>).</summary>
    public IReadOnlyCollection<string> Tags => (IReadOnlyCollection<string>)_slotsByTag.Keys;

    /// <summary>The tag a slot is assigned, or <see cref="DefaultTag"/> when it is untagged.
    /// Matching is case-insensitive on the slot name.</summary>
    public string TagOf(string slot) =>
        _tagBySlot.TryGetValue(slot, out var tag) ? tag : DefaultTag;

    /// <summary>Logical resource name of the embedded default manifest for a game ("dc1"/"dc2").</summary>
    public static string ResourceName(string game) => $"DinoRand.Randomizer.Data.{game}.bgm.json";

    /// <summary>Load the embedded <c>data/&lt;game&gt;/bgm.json</c> (<paramref name="game"/> = "dc1"/"dc2").</summary>
    public static BgmManifest LoadDefault(string game)
    {
        var asm = typeof(BgmManifest).Assembly;
        var name = ResourceName(game);
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded BGM manifest '{name}' not found. Resources: {string.Join(", ", asm.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Load a manifest from a JSON file path.</summary>
    public static BgmManifest Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Parse the <c>{ "&lt;tag&gt;": ["&lt;slot&gt;", …], "_comment": … }</c> form.</summary>
    public static BgmManifest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var slotsByTag = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var tagBySlot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.StartsWith('_')) continue;            // metadata key
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;

            var slots = new List<string>();
            foreach (var el in prop.Value.EnumerateArray())
            {
                if (el.GetString() is not { Length: > 0 } slot) continue;
                slots.Add(slot);
                tagBySlot.TryAdd(slot, prop.Name);              // first tag claiming a slot wins
            }
            slotsByTag[prop.Name] = slots;
        }

        return new BgmManifest(slotsByTag, tagBySlot);
    }
}
