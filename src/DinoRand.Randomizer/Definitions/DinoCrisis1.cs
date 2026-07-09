using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Definitions;

/// <summary>
/// Dino Crisis 1 as data. Item / key ids are the REAL values from the game's item
/// table (see <c>data/dc1/items.json</c>, sourced from the residentevil123 forum and
/// cross-checked against the item record in <c>st10c.dat</c> @0x26453). Enemy ids
/// remain provisional (the forum table doesn't enumerate them). The full item names
/// live in the JSON; this class keeps the engine-relevant subset.
/// </summary>
public sealed class DinoCrisis1 : GameDefinition
{
    public override string Id => "dc1";
    public override string DisplayName => "Dino Crisis";
    public override string ExecutableName => "DINO.exe";

    /// <summary>DC1 is full-parity: every randomizer option group is supported (docs/decisions/cross/DC2-OPTION-GATING-PLAN.md).
    /// The enum-guard test (<c>Dc1_supports_every_feature</c>) fails if a new <see cref="GameFeature"/> is
    /// added without listing it here.</summary>
    public override IReadOnlySet<GameFeature> SupportedFeatures { get; } = new HashSet<GameFeature>
    {
        GameFeature.Items, GameFeature.Enemies, GameFeature.Doors, GameFeature.KeyItems,
        GameFeature.StartingInventory, GameFeature.Voices, GameFeature.Bgm, GameFeature.EmergencyBoxes,
        GameFeature.PuzzleCodes,
    };

    // Rooms are per-room files stNXX.dat across 12 stages (1–9, A=10, B=11, C=12);
    // stage early areas: 1F, 2F, S1, Facility Outdoors, S2, S3, Op. Wipeout 1/2/3.
    // Matches e.g. st100.dat .. st114.dat (stage 1), stA07.dat, stC10.dat. The
    // stage-level st1.dat / st1.bin files are excluded by the two-hex-digit room part.
    private static readonly System.Text.RegularExpressions.Regex RoomPattern =
        new(@"^st([0-9a-c])([0-9a-f]{2})\.dat$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Room-file globs must be case-INSENSITIVE: the GOG english/Data ships room 0502 as `St502.dat`
    // (capital S), and .NET's default search-pattern matching is case-sensitive on Linux/WSL/CI — a
    // plain "st*.dat" glob silently drops it, deleting room 0502 from the door graph. Force
    // case-insensitive matching so RoomPattern (already IgnoreCase) is the single source of truth.
    // (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.42.)
    private static readonly EnumerationOptions CaseInsensitive =
        new() { MatchCasing = MatchCasing.CaseInsensitive };

    // Progression spine = the game's "Keys" item block, ids 0x2B–0x6F (plugs, room/area
    // keys, ID/key cards, panel keys, batteries, crane cards, stabilizer/initializer,
    // core parts, key chips, DDK input/code discs, …). These must be placed by logic and
    // never rerolled as ordinary pickups. (A few ids in the range are unused/"??" — kept
    // in the set is harmless since they never appear in rooms.)
    public override IReadOnlySet<int> KeyItemIds { get; } =
        new HashSet<int>(Enumerable.Range(0x2B, 0x6F - 0x2B + 1));

    // Shuffleable consumables = ammo (0x10–0x1A) + health (0x1B–0x23). Real ids/names
    // from data/dc1/items.json; weights bias ammo over healing.
    public override IReadOnlyList<ItemPoolEntry> ItemPool { get; } = new[]
    {
        new ItemPoolEntry(0x16, "9mm Parabellum", 5.0, ItemCategory.Ammo),
        new ItemPoolEntry(0x17, "40S&W Bullets", 3.0, ItemCategory.Ammo),
        new ItemPoolEntry(0x10, "SG Bullets", 2.5, ItemCategory.Ammo),
        new ItemPoolEntry(0x11, "Slag Bullets", 1.5, ItemCategory.Ammo),
        new ItemPoolEntry(0x12, "An. Darts S", 2.0, ItemCategory.Ammo),
        new ItemPoolEntry(0x13, "An. Darts M", 1.5, ItemCategory.Ammo),
        new ItemPoolEntry(0x14, "An. Darts L", 1.0, ItemCategory.Ammo),
        new ItemPoolEntry(0x18, "Grenade Bullets", 1.0, ItemCategory.Ammo),
        new ItemPoolEntry(0x1C, "Med. Pak S", 3.0, ItemCategory.Health),
        new ItemPoolEntry(0x1D, "Med. Pak M", 1.5, ItemCategory.Health),
        new ItemPoolEntry(0x1E, "Med. Pak L", 0.7, ItemCategory.Health),
        new ItemPoolEntry(0x1B, "Hemostat", 1.5, ItemCategory.Health),
        new ItemPoolEntry(0x1F, "Resuscitation", 0.5, ItemCategory.Health),
    };

    // Scripted T-Rex set-pieces — never permuted. From data/dc1/placements.md: 0200
    // (Communication Antenna chase), 0202 (Chief's Room window-smash), 0600 (Carrying Out B3
    // scene), 0610 (Hovercraft Storage final battle). The 0610 final-battle room is the only one
    // of these whose ordinary enemies (two distinct raptor models) would otherwise be eligible
    // for the in-room permute, so excluding it by code matters; the rest are belt-and-braces
    // (their enemies are singletons per AI category). See docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.11.
    public override IReadOnlySet<int> ScriptedEnemyRoomCodes { get; } =
        new HashSet<int> { 0x200, 0x202, 0x600, 0x610 };

    // --- Item-metadata layer (mirrors data/dc1/items.json; locked by ItemTableTests). docs/decisions/cross/ITEM-RANDO-PLAN.md.
    // Weapons 0x01-0x0a, parts 0x0b-0x0f (categories in items.json). Regina starts with the Handgun.
    public override IReadOnlyCollection<int> WeaponIds { get; } =
        new[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a };
    public override IReadOnlyCollection<int> WeaponPartIds { get; } =
        new[] { 0x0b, 0x0c, 0x0d, 0x0e, 0x0f };
    public override IReadOnlyCollection<int> StartingWeaponIds { get; } = new[] { 0x05 };

    // weapon id -> compatible ammo ids (linked-ammo rule). Mirror of items.json.weaponAmmo.
    private static readonly IReadOnlyDictionary<int, int[]> WeaponAmmo = new Dictionary<int, int[]>
    {
        [0x01] = new[] { 0x10, 0x11 }, [0x02] = new[] { 0x10, 0x11 },
        [0x03] = new[] { 0x10, 0x11 }, [0x04] = new[] { 0x10, 0x11 },
        [0x05] = new[] { 0x16 },       [0x06] = new[] { 0x16 },
        [0x07] = new[] { 0x17 },       [0x08] = new[] { 0x17 },
        [0x09] = new[] { 0x18, 0x19, 0x12, 0x13, 0x14, 0x15 },
        [0x0a] = new[] { 0x18, 0x19, 0x12, 0x13, 0x14, 0x15, 0x1a },
    };
    public override IReadOnlyCollection<int> AmmoForWeapon(int weaponId)
        => WeaponAmmo.TryGetValue(weaponId, out var a) ? a : Array.Empty<int>();

    // weapon-part id -> weapon it upgrades into (mirror of items.json.weaponPartUpgrades). Only the
    // Handgun Slides introduce new ammo (Glock 34 -> Glock 35 -> 40S&W); see items.json note.
    private static readonly IReadOnlyDictionary<int, int> PartUpgrades = new Dictionary<int, int>
    {
        [0x0e] = 0x07,
    };
    public override int? WeaponUpgradeFromPart(int partId)
        => PartUpgrades.TryGetValue(partId, out var w) ? w : null;

    // weapon-part id -> base weapon it requires (mirror of items.json.weaponPartBase). Drives the
    // weapon-upgrade-chance feature (docs/decisions/cross/ITEM-RANDO-PLAN.md §7.3): a part is only placed when its base
    // weapon is in the seed. Handgun base (0x05) is the start weapon, so 0x0d/0x0e always qualify.
    private static readonly IReadOnlyDictionary<int, int> PartBase = new Dictionary<int, int>
    {
        [0x0b] = 0x01, [0x0c] = 0x01, [0x0d] = 0x05, [0x0e] = 0x05, [0x0f] = 0x09,
    };
    public override int? WeaponForPart(int partId)
        => PartBase.TryGetValue(partId, out var w) ? w : null;

    // base weapon -> (part, resulting variant) for the experimental pre-upgraded-weapon feature
    // (mirror of items.json.weaponUpgradeVariants). The variant ids are never vanilla pickups, so the
    // feature is default-off + unvalidated (docs/decisions/cross/ITEM-RANDO-PLAN.md §7.3). Grenade 0x09->0x0a is medium-conf.
    private static readonly IReadOnlyDictionary<int, (int Part, int Result)[]> UpgradeVariants =
        new Dictionary<int, (int, int)[]>
        {
            [0x01] = new[] { (0x0b, 0x02), (0x0c, 0x03) },
            [0x02] = new[] { (0x0c, 0x04) },
            [0x05] = new[] { (0x0d, 0x06), (0x0e, 0x07) },
            [0x07] = new[] { (0x0d, 0x08) },
            [0x09] = new[] { (0x0f, 0x0a) },
        };
    public override IReadOnlyList<(int Part, int Result)> WeaponUpgradeVariants(int baseWeapon)
        => UpgradeVariants.TryGetValue(baseWeapon, out var v) ? v : Array.Empty<(int, int)>();

    // Weapon families for the per-family EnabledWeapons toggles (mirror of items.json.weaponFamilies,
    // locked by ItemTableTests). Every weapon/part id 0x01-0x0f maps to exactly one family; the order of
    // FamilyList is the UI/CLI display order. docs/decisions/cross/ITEM-RANDO-PLAN.md §7.4.
    private static readonly IReadOnlyDictionary<int, WeaponFamily> FamilyOfId = new Dictionary<int, WeaponFamily>
    {
        [0x05] = WeaponFamily.Handgun, [0x06] = WeaponFamily.Handgun, [0x07] = WeaponFamily.Handgun,
        [0x08] = WeaponFamily.Handgun, [0x0d] = WeaponFamily.Handgun, [0x0e] = WeaponFamily.Handgun,
        [0x01] = WeaponFamily.Shotgun, [0x02] = WeaponFamily.Shotgun, [0x03] = WeaponFamily.Shotgun,
        [0x04] = WeaponFamily.Shotgun, [0x0b] = WeaponFamily.Shotgun, [0x0c] = WeaponFamily.Shotgun,
        [0x09] = WeaponFamily.GrenadeGun, [0x0a] = WeaponFamily.GrenadeGun, [0x0f] = WeaponFamily.GrenadeGun,
    };
    public override WeaponFamily? WeaponFamilyOf(int itemId)
        => FamilyOfId.TryGetValue(itemId, out var f) ? f : null;

    public override IReadOnlyList<(WeaponFamily Flag, string Name)> WeaponFamilies { get; } = new[]
    {
        (WeaponFamily.Handgun, "Handgun"),
        (WeaponFamily.Shotgun, "Shotgun"),
        (WeaponFamily.GrenadeGun, "Grenade Gun"),
    };

    // Plug (0x2b) is the consumable key spent to open emergency boxes; it is also a key item
    // (KeyItemIds 0x2B-0x6F), so the item pass never rerolls it. docs/decisions/cross/ITEM-RANDO-PLAN.md §7.4.
    public override int? PlugItemId => 0x2B;

    // Emergency boxes: room + plug cost, mirror of data/dc1/emergency-boxes.json (room codes verified
    // against room-data.json; plug counts are region/difficulty-invariant per the source FAQ). Locked by
    // EmergencyBoxTests. Optional storage — never a progression gate; see PlugEconomy. Four rooms hold two
    // boxes each (B3 / Experiment Room Hall / Central Stairway / Power Freq. Room).
    public override IReadOnlyList<EmergencyBox> EmergencyBoxes { get; } = new[]
    {
        new EmergencyBox(0x0105, 1, "Control Room Hall"),
        new EmergencyBox(0x0113, 2, "Elevator Hall"),
        new EmergencyBox(0x0104, 1, "Strategy Room"),
        new EmergencyBox(0x0306, 2, "Main Hallway B1"),
        new EmergencyBox(0x0301, 2, "Research Area Hall"),
        new EmergencyBox(0x0205, 1, "Communication Room"),
        new EmergencyBox(0x0602, 1, "Control Room B3 (Yellow)"),
        new EmergencyBox(0x0602, 3, "Control Room B3 (Red)"),
        new EmergencyBox(0x0502, 1, "Experiment Room Hall (Green)"),
        new EmergencyBox(0x0502, 2, "Experiment Room Hall (Red)"),
        new EmergencyBox(0x0608, 1, "Central Stairway (Green)"),
        new EmergencyBox(0x0608, 3, "Central Stairway (Red)"),
        new EmergencyBox(0x050C, 2, "Power Freq. Room (Red)"),
        new EmergencyBox(0x050C, 1, "Power Freq. Room (Green)"),
        new EmergencyBox(0x060D, 1, "Underground Heliport"),
        new EmergencyBox(0x0610, 1, "Hovercraft Storage"),
        new EmergencyBox(0x0612, 1, "Hovercraft"),
    };

    // Scripted CUTSCENE rooms — their 0x20 entities are dinosaurs (NOT NPCs; the NPC characters are
    // not 0x20 entities — see docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.15), but each scene choreographs those
    // dinosaurs by slot, so permuting their (model, motion) — even within one species — can desync the
    // animation the scene plays. Each room is a documented scripted scene with ≥2 distinct same-
    // category pairs (so it would otherwise be permuted):
    //   0x010d Backyard of the Facility   — opening "Gail intro" scene (placements.md), Cooper/raptor.
    //   0x0112 The Backyard               — the paired backyard scene room (forum room list).
    //   0x0109 Lecture Room               — the Gail-scripted raptor kill (placements.md).
    //   0x030a Hallway Carrying Materials — Tom's death cutscene, killed by the raptor (forum).
    //   0x030e Hall B1 (beta)             — the "Gail catches Kirk" cutscene room (forum).
    // The lone humanoid 0x20 entity in the game (the st50c "Researcher" corpse) is a singleton, so the
    // pass's ≥2-records rule already leaves it untouched; it needs no entry here.
    public override IReadOnlySet<int> CutsceneRoomCodes { get; } =
        new HashSet<int> { 0x010d, 0x0112, 0x0109, 0x030a, 0x030e };

    // Grenade Launcher set-piece rooms — kept entirely vanilla by the item pass. In each, the Grenade
    // Gun (0x09) sits clustered with its Grenade rounds (0x18) at one pickup quad as a progression-
    // critical finale cache (verified across english/Data; 060d is also GoalRoomCode). Protecting them
    // does NOT cost the weapon from the shuffle pool — the Grenade Gun is independently findable as a
    // standalone pickup in 0x0402, so it stays randomizable and keeps its ammo linked. docs/decisions/cross/ITEM-RANDO-PLAN.md.
    public override IReadOnlySet<int> ItemProtectedRoomCodes { get; } =
        new HashSet<int> { 0x060d, 0x0610, 0x0612 };

    // Backyard of the Facility — the game's opening room (BFS root for progression logic).
    public override int StartRoomCode => 0x010d;

    // Underground Heliport — an endgame room behind the Key Card Lv. A (0x3a) door chain
    // (0611->060d, type 8). Empty-handed it is unreachable; with the door keys it is reachable,
    // so it is a real progression goal under the door-graph logic (see docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.13).
    public override int GoalRoomCode => 0x060d;

    // Hand-authored progression-logic overlay (puzzle gates / room-state / item-guards) from
    // data/dc1/map.json. Loaded once and reused; map.json authors 12 door `requires` + 4 door
    // `requiresRoom` gates today (provenance: map-requirements.md for the lock axis, placement-gates.md
    // for the placement axis), which MapRequirements stamps onto the graph. Rooms with no requirement
    // field are skipped, so an un-authored room is unaffected.
    private readonly Graph.IRequirementOverlay _requirements = Maps.MapRequirements.LoadDefault();
    public override Graph.IRequirementOverlay Requirements => _requirements;

    // Key Card ladder: the door interaction handler maps held cards to a "level" via
    // GetFlag(group 11, id) — Lv. C (0x38) -> level 6, Lv. B (0x39) -> 7, Lv. A (0x3a) -> 8 — and
    // opens a door of type t in {6,7,8} iff the held level >= t, so a higher card opens lower-level
    // doors. KeyItemsForDoor therefore returns every card of sufficient level (OR semantics).
    private static readonly int[] KeyCardLadder = { 0x38, 0x39, 0x3a }; // index 0 -> door type 6

    public override IReadOnlyCollection<int> KeyItemsForDoor(int doorType)
    {
        if (doorType is >= 6 and <= 8)
            return KeyCardLadder[(doorType - 6)..]; // type 8 -> {0x3a}; type 6 -> all three cards
        if (doorType is >= 9 and <= 0xfc)
            return new[] { doorType };              // the type byte is literally the required item id
        // ponytail: types 1/3 are story-flag READERS (GetFlag(9,lock)) and type 2 a SELF-latch SETTER
        // (STATIC-SCD-RE.md cont.40), not free — but they are return-shortcuts whose destination is
        // always independently reachable, so modelling them free never changes VANILLA reachability and
        // needs no gate here. The group-9 latch only matters for door-rando (destination shuffle can
        // strand a type-1 producer); model it in KeyItemPlacer.Reachable when RandomizeDoors ships.
        return Array.Empty<int>();                  // 0..5 free-to-cross; 0xfd/fe/ff special non-door action
    }

    public override string? GetDataDir(string installDir) => FindDataDir(installDir);

    public override IReadOnlyList<RoomFileRef> EnumerateRooms(string installDir)
    {
        // Room files live in a "Data" folder under a language dir (english/, japanese/…)
        // in the GOG/Steam release, which shares data files with the SourceNext port.
        var dataDir = FindDataDir(installDir);
        if (dataDir is null) return Array.Empty<RoomFileRef>();

        var rooms = new List<RoomFileRef>();
        foreach (var path in Directory.EnumerateFiles(dataDir, "st*.dat", CaseInsensitive))
        {
            var m = RoomPattern.Match(Path.GetFileName(path));
            if (!m.Success) continue;
            int stage = Convert.ToInt32(m.Groups[1].Value, 16);
            int room = Convert.ToInt32(m.Groups[2].Value, 16);
            rooms.Add(new RoomFileRef(stage, room, path));
        }
        return rooms.OrderBy(r => r.Stage).ThenBy(r => r.Room).ToList();
    }

    private static string? FindDataDir(string installDir)
    {
        // A candidate is only valid if it actually holds room files. (On a
        // case-insensitive filesystem, …/Data can otherwise collide with an unrelated
        // dir such as our own data/ JSON folder — see docs/reference/cross/architecture/DESIGN.md deviations.)
        string[] candidates =
        {
            Path.Combine(installDir, "Data"),
            Path.Combine(installDir, "english", "Data"),
        };
        foreach (var c in candidates)
            if (HasRoomFiles(c)) return c;

        // Fall back to any "<lang>/Data" containing room files.
        if (Directory.Exists(installDir))
            foreach (var sub in Directory.EnumerateDirectories(installDir))
            {
                var data = Path.Combine(sub, "Data");
                if (HasRoomFiles(data)) return data;
            }
        return null;
    }

    private static bool HasRoomFiles(string dir) =>
        Directory.Exists(dir) &&
        Directory.EnumerateFiles(dir, "st*.dat", CaseInsensitive).Any(f => RoomPattern.IsMatch(Path.GetFileName(f)));
}
