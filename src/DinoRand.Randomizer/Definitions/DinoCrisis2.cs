using DinoRand.Randomizer.Dc2;

namespace DinoRand.Randomizer.Definitions;

/// <summary>
/// Dino Crisis 2 (Rebirth Edition) as data — the second <see cref="GameDefinition"/>, added beside
/// <see cref="DinoCrisis1"/> with <b>zero change to the base class or to DC1</b>
/// (docs/parity/BIORAND-REUSE-VALIDATION.md Q3). Mirrors BioRand registering a new per-game module
/// (<c>Re2Randomiser</c>) next to the others.
///
/// Item/key behavior is loaded from the generated v2 fixture contract and is restricted to its
/// 42 proven op-0x35/op-0x2c writer sites; SAT-1 physical triggers remain a separate surface.
/// </summary>
public sealed class DinoCrisis2 : GameDefinition
{
    public override string Id => "dc2";
    public override string DisplayName => "Dino Crisis 2";
    public override string ExecutableName => "Dino2.exe";

    /// <summary>The generate/install pipeline supports the feature set declared below.</summary>
    public override bool IsImplemented => true;

    /// <summary>DC2 opts into its implemented item, key, enemy, player-model, and voice surfaces.
    /// <see cref="GameFeature.PlayerModel"/> is the <b>character-skin swap</b> (Dylan renders as
    /// Extra Crisis Gail/Rick via their engine-native WP graft files + the WP-gate exe patch,
    /// docs/reference/dc2/models/DC2-EXTRA-CRISIS-ROSTER-DECODE.md §7–9, in-game verified) — it replaces the withdrawn
    /// whole-file Regina ↔ Dylan swap, whose fire-crash class (per-weapon fire tails addressed by
    /// .text-baked VAs, docs/decisions/dc2/models/DC2-PLAYER-SWAP-FIRE-CRASH-RCA.md) the graft never touches.
    /// <see cref="GameFeature.Voices"/> exposes the DC2 voice UI (cast labelled by the 2026-07-05
    /// folder-curation pass, data/dc2/voice.json); emission itself still waits on
    /// <see cref="Voice.Dc2VoiceManifestLayout.IsDecoded"/>.
    /// Other option groups stay fenced until their required contracts land.</summary>
    public override IReadOnlySet<GameFeature> SupportedFeatures { get; } =
        new HashSet<GameFeature>
        {
            GameFeature.Items,
            GameFeature.KeyItems,
            GameFeature.Enemies,
            GameFeature.PlayerModel,
            GameFeature.Voices,
        };

    /// <summary>
    /// DC2-specific enemy abstraction (BioRand <c>IEnemyHelper</c> analogue). Deliberately a member of
    /// <see cref="DinoCrisis2"/> and <b>not</b> of <see cref="GameDefinition"/> — adding it to the base
    /// would force a change to shared/DC1 code, which DC1 doesn't need (its species is model-bound, not
    /// an id). The DC2 runner reads it off the concrete type. See Q3 isolation plan.
    /// </summary>
    public IDc2EnemyHelper EnemyHelper { get; } = new Dc2EnemyHelper();

    public override IReadOnlySet<int> KeyItemIds { get; } =
        Enumerable.Range(0x21, 0x34 - 0x21 + 1).ToHashSet();

    /// <summary>The exact health multiset at the v2 fillable writer sites. Entries have neutral
    /// weight because standalone DC2 permutes the fixture multiset rather than sampling it.</summary>
    public override IReadOnlyList<ItemPoolEntry> ItemPool { get; } =
        Dc2ItemData.LoadEmbedded().Locations
            .Where(x => x.RewriteClass == FileFormats.Stage.Dc2.Dc2ItemEditor.ItemRewriteClass.Health)
            .Select(x => new ItemPoolEntry(x.ItemId, x.ItemName, 1d, ItemCategory.Health))
            .ToArray();

    public override IReadOnlySet<int> ScriptedEnemyRoomCodes { get; } = new HashSet<int>(); // TODO(dc2)
    public override int StartRoomCode => 0x101;
    public override int GoalRoomCode => 0x504;
    public override IReadOnlyCollection<int> KeyItemsForDoor(int doorType) => Array.Empty<int>();

    // ST + decimal stage digit + two hexadecimal room digits (KaQ K14).
    private static readonly System.Text.RegularExpressions.Regex RoomPattern =
        new(@"^ST(\d)([0-9A-F]{2})\.DAT$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
            int room = Convert.ToInt32(m.Groups[2].Value, 16);
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
