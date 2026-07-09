using DinoRand.Randomizer.Dc2;

namespace DinoRand.Randomizer.Definitions;

/// <summary>
/// Dino Crisis 2 (Rebirth Edition) as data — the second <see cref="GameDefinition"/>, added beside
/// <see cref="DinoCrisis1"/> with <b>zero change to the base class or to DC1</b>
/// (docs/parity/BIORAND-REUSE-VALIDATION.md Q3). Mirrors BioRand registering a new per-game module
/// (<c>Re2Randomiser</c>) next to the others.
///
/// <para><b>⚠ STUB.</b> Room discovery is real (<c>rebirth/Data/ST*.DAT</c>); everything that depends
/// on the in-room record decode — key items, item pool, scripted rooms, door gating, start/goal —
/// is a placeholder pending docs/reference/dc2/_registries/KNOWLEDGE-AND-QUESTIONS.md OPEN&#160;#1/#2/#5/#6. The
/// <see cref="EnemyHelper"/> (BioRand <c>IEnemyHelper</c> port) is the one piece that is ready.</para>
/// </summary>
public sealed class DinoCrisis2 : GameDefinition
{
    public override string Id => "dc2";
    public override string DisplayName => "Dino Crisis 2";
    public override string ExecutableName => "Dino2.exe";

    /// <summary>Implemented (generate/installable from the frontend) as of the cross-species enemy
    /// randomizer shipping (docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md). DC2 still supports only
    /// <see cref="GameFeature.Enemies"/> — item/key/door randomization stay greyed until their in-room
    /// record decode lands (KaQ OPEN #2/#5/#6) — but it is no longer fenced out of Generate/Install.
    /// docs/decisions/cross/GAME-SELECTOR-PLAN.md §8.</summary>
    public override bool IsImplemented => true;

    /// <summary>DC2 opts into <see cref="GameFeature.Enemies"/> — the cross-species enemy randomizer is the
    /// first shipped DC2 feature (the room-file slot-5 TYPE swap, docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md), so
    /// the UI's "Randomize enemies" option (plus the DC2 setpiece/boss donor sub-toggles) enables for DC2.
    /// <see cref="GameFeature.PlayerModel"/> is the <b>character-skin swap</b> (Dylan renders as
    /// Extra Crisis Gail/Rick via their engine-native WP graft files + the WP-gate exe patch,
    /// docs/reference/dc2/models/DC2-EXTRA-CRISIS-ROSTER-DECODE.md §7–9, in-game verified) — it replaces the withdrawn
    /// whole-file Regina ↔ Dylan swap, whose fire-crash class (per-weapon fire tails addressed by
    /// .text-baked VAs, docs/decisions/dc2/models/DC2-PLAYER-SWAP-FIRE-CRASH-RCA.md) the graft never touches.
    /// <see cref="GameFeature.Voices"/> exposes the DC2 voice UI (cast labelled by the 2026-07-05
    /// folder-curation pass, data/dc2/voice.json); emission itself still waits on
    /// <see cref="Voice.Dc2VoiceManifestLayout.IsDecoded"/>.
    /// Every other option group stays fenced until its in-room record decode lands (KaQ OPEN #2/#5/#6).</summary>
    public override IReadOnlySet<GameFeature> SupportedFeatures { get; } =
        new HashSet<GameFeature> { GameFeature.Enemies, GameFeature.PlayerModel, GameFeature.Voices };

    /// <summary>
    /// DC2-specific enemy abstraction (BioRand <c>IEnemyHelper</c> analogue). Deliberately a member of
    /// <see cref="DinoCrisis2"/> and <b>not</b> of <see cref="GameDefinition"/> — adding it to the base
    /// would force a change to shared/DC1 code, which DC1 doesn't need (its species is model-bound, not
    /// an id). The DC2 runner reads it off the concrete type. See Q3 isolation plan.
    /// </summary>
    public IDc2EnemyHelper EnemyHelper { get; } = new Dc2EnemyHelper();

    // --- Placeholders below: DC2 item/key/door semantics are OPEN until the records decode. ---

    public override IReadOnlySet<int> KeyItemIds { get; } = new HashSet<int>();          // TODO(dc2): OPEN #5
    public override IReadOnlyList<ItemPoolEntry> ItemPool { get; } = Array.Empty<ItemPoolEntry>(); // TODO(dc2)
    public override IReadOnlySet<int> ScriptedEnemyRoomCodes { get; } = new HashSet<int>(); // TODO(dc2)
    public override int StartRoomCode => 0x000;  // TODO(dc2): real start room (KaQ K14 id space)
    public override int GoalRoomCode => 0x000;   // TODO(dc2): real goal room
    public override IReadOnlyCollection<int> KeyItemsForDoor(int doorType) => Array.Empty<int>(); // TODO(dc2): OPEN #2

    // ST + stage digit + room. Exact DC2 room-id scheme is TBD (KaQ K14); two-digit room assumed for now.
    private static readonly System.Text.RegularExpressions.Regex RoomPattern =
        new(@"^ST(\d)(\d{2})\.DAT$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Case-INSENSITIVE room glob so a differently-cased room file is never dropped on Linux/WSL/CI
    // (the DC1-side St502.dat class of bug — STATIC-SCD-RE cont.42). RoomPattern (IgnoreCase) filters.
    private static readonly EnumerationOptions CaseInsensitive =
        new() { MatchCasing = MatchCasing.CaseInsensitive };

    public override string? GetDataDir(string installDir) => FindDataDir(installDir);

    public override IReadOnlyList<RoomFileRef> EnumerateRooms(string installDir)
    {
        var dataDir = FindDataDir(installDir);
        if (dataDir is null) return Array.Empty<RoomFileRef>();

        var rooms = new List<RoomFileRef>();
        foreach (var path in Directory.EnumerateFiles(dataDir, "ST*.DAT", CaseInsensitive))
        {
            var m = RoomPattern.Match(Path.GetFileName(path));
            if (!m.Success) continue;
            int stage = Convert.ToInt32(m.Groups[1].Value, 10);
            int room = Convert.ToInt32(m.Groups[2].Value, 10);
            rooms.Add(new RoomFileRef(stage, room, path));
        }
        return rooms.OrderBy(r => r.Stage).ThenBy(r => r.Room).ToList();
    }

    // DC2 Rebirth basis = rebirth/Data (docs/reference/dc2/architecture/ARCHITECTURE-OVERVIEW.md). english/japanese are reference-only
    // fallbacks; their Data differs byte-for-byte (KaQ K3) so the room reader must re-validate (T0).
    private static string? FindDataDir(string installDir)
    {
        string[] candidates =
        {
            Path.Combine(installDir, "rebirth", "Data"),
            Path.Combine(installDir, "Data"),
            Path.Combine(installDir, "english", "Data"),
        };
        foreach (var c in candidates)
            if (HasRoomFiles(c)) return c;
        return null;
    }

    private static bool HasRoomFiles(string dir) =>
        Directory.Exists(dir) &&
        Directory.EnumerateFiles(dir, "ST*.DAT", CaseInsensitive).Any(f => RoomPattern.IsMatch(Path.GetFileName(f)));
}
