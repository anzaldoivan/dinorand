namespace DinoRand.Randomizer.Definitions;

/// <summary>
/// DC1's fixed cutscene cast (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §5.1). The unit of swapping, mirroring
/// BioRand's <c>Actor</c> string identity (<c>VoiceRandomiser.cs</c>) but closed over DC1's small,
/// fixed cast. <see cref="Regina"/> is the protagonist (the player avatar) and is randomized by a
/// separate toggle + character-selection (D3), not by the supporting-cast shuffle.
/// </summary>
public enum VoiceActor
{
    /// <summary>An unmapped / not-yet-labelled clip. Default so an un-tagged record is inert. Also where the
    /// non-cast machine voices (<c>computer</c>, <c>computer-lab</c>) land: they are labelled in
    /// <c>data/dc1/voice.json</c> but are not swappable cast, so the loader maps them here.</summary>
    Unknown = 0,
    Regina,
    Gail,
    Rick,
    Kirk,
    Cooper,
    Tom,
    Colonel,
}

/// <summary>
/// Category tag on a voice clip (BioRand's <c>Kind</c>, parsed from the donor filename's <c>_kind</c>
/// token). A replacement must match Kind. DC1's set is a conceptual subset; which kinds DC1 actually
/// uses is settled with the SOUNDS voice-slot map (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §7 R3).
/// </summary>
public enum VoiceKind
{
    /// <summary>Default: a normal cutscene line.</summary>
    Dialogue = 0,
    Radio,
    Hurt,
    Death,
}

/// <summary>
/// One <b>donor</b> voice clip from a datapack (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §5.1) — the DC1 analogue of
/// a pool-candidate <c>VoiceSample</c>. Built by <see cref="Voice.VoiceDataPack"/> from the BioRand
/// layout <c>data/voice/&lt;actor&gt;.&lt;game&gt;/&lt;index&gt;_&lt;kind&gt;-&lt;cond&gt;.ext</c>; the
/// metadata is the filename, decoded by <see cref="Voice.VoiceFileName"/>.
/// </summary>
/// <param name="Actor">Which character speaks — the lower-cased <c>&lt;actor&gt;</c> folder name. A
/// <b>string</b>, not <see cref="VoiceActor"/>: donors come from any game (cross-game, plan §12.1), so
/// the donor cast is open (e.g. <c>leon</c>, <c>claire</c>) while DC1 <i>targets</i> stay the enum.</param>
/// <param name="Game">Source-game tag (e.g. <c>dc1</c>, <c>re2</c>) from the folder suffix.</param>
/// <param name="Kind">Category; a replacement must match it.</param>
/// <param name="Conditions">Actor-presence predicates from the filename (e.g. <c>nokirk</c>, <c>rick</c>).</param>
/// <param name="Index">The numeric clip index from the filename (ordering / identity within an actor).</param>
/// <param name="Path">Readable identifier for the donor clip: an absolute file path for a folder pack,
/// or a <c>&lt;zip&gt;!&lt;entry&gt;</c> identifier (e.g. <c>dinocrisis.zip!data/voice/regina.dc1/regina001.ogg</c>)
/// for a zip pack. The extension is still the audio format, but a zip identifier is not itself an openable
/// path — use <see cref="Open"/> to read the bytes.</param>
public sealed record VoiceClipSource(
    string Actor, string Game, VoiceKind Kind,
    IReadOnlyList<string> Conditions, int Index, string Path)
{
    private readonly Func<Stream>? _open;

    /// <summary>
    /// Opens the donor clip's bytes as a readable, seekable stream. Defaults to
    /// <c>File.OpenRead(Path)</c> (folder packs), so existing construction and tests are unchanged; a zip
    /// pack sets this to stream the archive entry on demand (<see cref="Voice.ZipPackSource"/>), so its
    /// clips transcode without the file ever existing on disk.
    /// </summary>
    public Func<Stream> Open
    {
        get => _open ?? (() => File.OpenRead(Path));
        init => _open = value;
    }

    /// <summary><c>true</c> when this clip is from DC1 itself (vs a cross-game donor).</summary>
    public bool IsNativeDc1 => string.Equals(Game, "dc1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The game-specific donor identity <c>&lt;actor&gt;.&lt;game&gt;</c> (lower-cased) — the same actor in
    /// two games (e.g. <c>claire.recv</c> vs <c>claire.re2r</c>) is a <b>different</b> donor, so a swap never
    /// mixes performances across games. Used as the donor token by <see cref="Voice.VoiceSwapPlanner"/> and
    /// the draw pool. (A bare actor name is still accepted for the dev preview path.)
    /// </summary>
    public string Key => $"{Actor}.{Game}".ToLowerInvariant();
}

/// <summary>
/// One DC1 cutscene voice line we can overwrite — the target slot
/// (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §5.1).
///
/// <para><b>UNVERIFIED ADDRESSING.</b> <see cref="Path"/> plus the <see cref="RoomCode"/>/
/// <see cref="Cutscene"/> key are placeholders: DC1's cutscene-voice file addressing is not yet
/// reverse-engineered (<see cref="VoiceManifestLayout.IsDecoded"/> = <c>false</c>), so no slot manifest
/// is authored and none is read for emission. The type exists so the gated pass and its tests compile
/// against the intended shape.</para>
/// </summary>
/// <param name="Path">On-disk DC1 clip path (loose WAV under <c>SOUNDS\</c>). Unverified.</param>
/// <param name="Actor">Who originally speaks the line.</param>
/// <param name="Kind">Category; a replacement must match it.</param>
/// <param name="RoomCode">DC1 room code the line plays in (<c>stage&lt;&lt;8 | room</c>). Unverified key.</param>
/// <param name="Cutscene">Per-room cutscene index. Unverified — DC1's cutscene numbering is undecoded.</param>
public readonly record struct VoiceClip(
    string Path, VoiceActor Actor, VoiceKind Kind, int RoomCode, int Cutscene);

/// <summary>
/// The actor swap result (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §5.1/§12.1): for each DC1 cast member, which
/// <b>donor actor's</b> voice should play in their place. The DC1 reduction of BioRand's per-room
/// <c>oldActor → newActor</c> map. The key is a DC1 <see cref="VoiceActor"/> (a labelled target); the
/// value is a donor actor <b>name string</b> (open, possibly cross-game — see <see cref="VoiceClipSource.Actor"/>).
/// An actor with no entry keeps its own voice (<b>left vanilla</b>, DE4), so an empty map is a no-op.
/// </summary>
public sealed class CharacterVoiceMap
{
    private readonly Dictionary<VoiceActor, string> _map = new();

    /// <summary>Map <paramref name="from"/>'s lines to donor actor <paramref name="toDonorActor"/>.</summary>
    public void Set(VoiceActor from, string toDonorActor) => _map[from] = toDonorActor;

    /// <summary>True (with the donor actor name) when <paramref name="actor"/> is remapped; false ⇒ leave vanilla.</summary>
    public bool TryResolve(VoiceActor actor, out string donorActor) => _map.TryGetValue(actor, out donorActor!);

    /// <summary><c>true</c> when no remap is set (every actor keeps its own voice ⇒ provably a no-op).</summary>
    public bool IsEmpty => _map.Count == 0;

    /// <summary>The remap entries (for logging / inspection).</summary>
    public IReadOnlyDictionary<VoiceActor, string> Entries => _map;
}

/// <summary>
/// Decode flag for the voice-randomization pass (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §7 R1, §13).
///
/// <para><b>OPEN (2026-06-27).</b> DC1's cutscene-voice file addressing is now decoded: each <c>xa*</c>
/// bank under <c>Sound\VOICE\</c> is labelled with its speaking actor in <c>data/dc1/voice.json</c> (630
/// banks across 8 actors), and the byte-overwrite path was verified in-game via the preview harness (§12.7).
/// So <see cref="IsDecoded"/> is <c>true</c> and <see cref="Passes.VoiceRandomizer"/> now <b>emits</b> the
/// swapped banks with the seed (loose-file install, reversed by Restore). The supporting-cast labels for
/// Rick/Gail/Kirk are folder-curated (not all by-ear verified), so an occasional line may be mis-voiced;
/// it is reversible and the feature is opt-in (<see cref="RandomizerConfig.RandomizeVoices"/>, default off).</para>
/// </summary>
public static class VoiceManifestLayout
{
    /// <summary>
    /// <c>true</c>: the DC1 cutscene-voice addressing is decoded (per-bank actor labels in
    /// <c>data/dc1/voice.json</c>) and the overwrite path is in-game-verified, so the pass emits.
    /// </summary>
    public const bool IsDecoded = true;
}
