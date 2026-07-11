using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Graph;

namespace DinoRand.Randomizer.Definitions;

/// <summary>
/// The randomizer option groups a game can support — one value per option group in the frontend, used to
/// disable options a game doesn't implement yet (docs/decisions/cross/DC2-OPTION-GATING-PLAN.md). A game declares its set
/// via <see cref="GameDefinition.SupportedFeatures"/>.
/// </summary>
public enum GameFeature
{
    Items, Enemies, Doors, KeyItems, StartingInventory, Voices, Bgm, EmergencyBoxes,

    /// <summary>DC1: scramble the shared keypad-code puzzle family (Management Office / Lounge /
    /// Computer-Room-gas safes + Stabilizer codes) so each accepted 4-digit code is seed-derived, keeping the
    /// in-game document that states the code in sync (<see cref="Install.GameInstaller.PatchExeSyncPuzzleCodes"/>,
    /// docs/reference/dc1/puzzle/MGMT-OFFICE-SAFE-PUZZLE-DECODE.md §17). DC2 does not opt in.</summary>
    PuzzleCodes,

    /// <summary>Player character model swap (DC2: whole-file Regina ↔ Dylan,
    /// docs/decisions/dc2/models/DC2-PLAYER-SWAP-PARITY-PLAN.md). DC1's costume swap is measured feasible
    /// (docs/decisions/cross/HUMANOID-MODEL-SWAP-FEASIBILITY.md) but not yet wired, so DC1 does not opt in.</summary>
    PlayerModel,
}

/// <summary>
/// A game described as data, not code. Adding Dino Crisis 2 later means supplying
/// another <see cref="GameDefinition"/> (plus any format quirks in FileFormats), not
/// rewriting the engine. Mirrors biorand's per-game split (RE1/RE2/RE3/RECV modules)
/// and biohazard-utils' per-format room types (IRdt → Rdt1/Rdt2/RdtCv). docs/reference/cross/architecture/DESIGN.md §5/§6.
/// </summary>
public abstract class GameDefinition
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }

    /// <summary>The game's main executable filename (DC1 <c>DINO.exe</c>, DC2 <c>Dino2.exe</c>). Drives the
    /// DRM/protector validation so the check inspects the right exe per game (docs/decisions/cross/DC2-OPTION-GATING-PLAN.md
    /// follow-up). Abstract so every game must name its executable.</summary>
    public abstract string ExecutableName { get; }

    /// <summary>The option groups this game supports. Default <b>empty</b> — a new (or stub) game opts in
    /// per feature, so an unfinished game's options stay disabled until explicitly enabled. DC1 overrides
    /// to the full set; the DC2 stub inherits empty. docs/decisions/cross/DC2-OPTION-GATING-PLAN.md.</summary>
    public virtual IReadOnlySet<GameFeature> SupportedFeatures => EmptyFeatures;

    private static readonly IReadOnlySet<GameFeature> EmptyFeatures = new HashSet<GameFeature>();

    /// <summary>Whether this game supports a given option group (drives the option's enabled state).</summary>
    public bool Supports(GameFeature feature) => SupportedFeatures.Contains(feature);

    /// <summary>
    /// Whether this game's randomizer pipeline is complete enough to run end-to-end. The game-selector
    /// shows every <see cref="GameCatalog"/> entry, but <c>false</c> here fences the game out of the live
    /// pipeline (the UI blocks Generate and labels it experimental) — so an incomplete
    /// <see cref="GameDefinition"/> like the DC2 stub can ship visibly without its empty key/item/door
    /// placeholders ever reaching <c>RandomizerRunner</c>. Default <c>true</c>; a stub overrides to
    /// <c>false</c> and flips back by dropping the override once ready. docs/decisions/cross/GAME-SELECTOR-PLAN.md §4.
    /// </summary>
    public virtual bool IsImplemented => true;

    /// <summary>Item ids the player progresses through; must never be lost/duplicated.</summary>
    public abstract IReadOnlySet<int> KeyItemIds { get; }

    /// <summary>Non-key items eligible for the pool, with relative weights.</summary>
    public abstract IReadOnlyList<ItemPoolEntry> ItemPool { get; }

    /// <summary>
    /// Rooms whose enemy layout is hand-scripted (T-Rex set-pieces) and must never be permuted,
    /// keyed by room code <c>Stage*0x100 + Room</c> (e.g. <c>0x202</c> for <c>st202</c>). Enemy
    /// species itself is bound to the loaded model resource, not an id byte, so the enemy pass
    /// permutes (model, motion) pointer pairs among a room's same-category records rather than
    /// substituting from a global id pool — see <see cref="EnemyRecord"/>.
    /// </summary>
    public abstract IReadOnlySet<int> ScriptedEnemyRoomCodes { get; }

    /// <summary>
    /// Rooms that play a scripted <b>cutscene</b> involving their <c>0x20</c> entities, keyed by room
    /// code (<c>Stage*0x100 + Room</c>). Excluded from enemy permutation in addition to
    /// <see cref="ScriptedEnemyRoomCodes"/>. Distinct concern: these entities are <i>dinosaurs</i>
    /// (not NPCs — see docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.15), but their <c>(model, motion)</c> is bound to a
    /// choreographed scene, so reshuffling it — even to a same-species variant — can desync the
    /// animation the cutscene drives. Default empty; a game with no such scenes overrides nothing.
    /// </summary>
    public virtual IReadOnlySet<int> CutsceneRoomCodes => new HashSet<int>();

    /// <summary>
    /// Rooms where a room script <b>choreographs</b> an enemy record (binds its slot and installs a
    /// scripted behavior — waypoint walk + completion flag; STATIC-SCD-RE cont.49/59), keyed by room
    /// code (<c>Stage*0x100 + Room</c>). A superset-style third tier beside
    /// <see cref="ScriptedEnemyRoomCodes"/>/<see cref="CutsceneRoomCodes"/>, derived by census rather
    /// than hand-curated, and consulted by the enemy passes only when
    /// <see cref="RandomizerConfig.Dc1CutsceneSafeEnemies"/> is enabled (default seeds stay
    /// byte-identical). Default empty.
    /// </summary>
    public virtual IReadOnlySet<int> ChoreographyRoomCodes => new HashSet<int>();

    /// <summary>
    /// Rooms whose item pickups must stay <b>vanilla</b> (never rerolled or pool-placed), keyed by room
    /// code (<c>Stage*0x100 + Room</c>). For set-piece spots whose specific item is progression-critical
    /// — e.g. the Grenade Launcher finale caches — so <see cref="Passes.ItemRandomizer"/> skips the whole
    /// room. Default empty; a game with no protected spots overrides nothing. docs/decisions/cross/ITEM-RANDO-PLAN.md §7.1.
    /// </summary>
    public virtual IReadOnlySet<int> ItemProtectedRoomCodes => new HashSet<int>();

    /// <summary>Room code (<c>stage&lt;&lt;8 | room</c>) the player starts in — the flood-fill root.</summary>
    public abstract int StartRoomCode { get; }

    /// <summary>Room code that must stay reachable for a seed to be beatable (an endgame room).</summary>
    public abstract int GoalRoomCode { get; }

    /// <summary>
    /// Optional hand-authored progression-logic overlay (puzzle gates / room-state / item-guards),
    /// applied onto the room graph after it is built (<see cref="RoomGraph.Build"/>). Null ⇒ no extra
    /// requirements beyond door-type gating — the graph reproduces today's behaviour exactly. The
    /// data-over-code home for new gates (<c>docs/decisions/cross/GRAPH-LOGIC-PARITY-PLAN.md</c> §4.2): a new gate is a
    /// JSON entry, never a code path. Default null; a game with no authored gates overrides nothing.
    /// </summary>
    public virtual IRequirementOverlay? Requirements => null;

    /// <summary>
    /// Key item ids that open a door given its door-type byte (<see cref="DoorRecord.DoorType"/>,
    /// record <c>+0x28</c>), with <b>OR</b> semantics — holding any one opens the door. An empty
    /// result means the door is not key-item gated (a normal/shortcut/story door). This is the
    /// door-graph gate map proven in <c>docs/reference/dc1/_registries/STATIC-SCD-RE.md</c> (cont.13): the door interaction
    /// handler selects the required key from the <i>type</i> byte (not the <c>+0x27</c> lock flag,
    /// which is only the group-9 "already unlocked" state bit) — for type ≥ 9 the type byte is
    /// literally the item id (<c>GetFlag(group 11, type)</c>), and types 6/7/8 are the Key Card
    /// ladder.
    /// </summary>
    public abstract IReadOnlyCollection<int> KeyItemsForDoor(int doorType);

    // --- Item-metadata layer (the BioRand IItemHelper analog; data-over-code, see docs/decisions/cross/ITEM-RANDO-PLAN.md).
    // Default-empty so a game with no weapon model opts in by overriding. ---

    /// <summary>Item ids that are weapons (placed by the item pass, never overwritten with consumables).</summary>
    public virtual IReadOnlyCollection<int> WeaponIds => Array.Empty<int>();

    /// <summary>Item ids that are weapon parts / upgrades (treated like weapons for placement).</summary>
    public virtual IReadOnlyCollection<int> WeaponPartIds => Array.Empty<int>();

    /// <summary>Weapons the player starts with, so their ammo is always linked in.</summary>
    public virtual IReadOnlyCollection<int> StartingWeaponIds => Array.Empty<int>();

    /// <summary>Ammo item ids compatible with <paramref name="weaponId"/> (the linked-ammo rule:
    /// ammo only appears for a weapon the seed actually grants). Empty for a non-weapon id.</summary>
    public virtual IReadOnlyCollection<int> AmmoForWeapon(int weaponId) => Array.Empty<int>();

    /// <summary>The weapon a placed part upgrades into (so the part links that weapon's ammo — e.g.
    /// Handgun Slides → Glock 35 → 40S&W), or <c>null</c> if the part introduces no new weapon/ammo.
    /// Closes the dead-weapon gap (docs/decisions/cross/ITEM-RANDO-PLAN.md §6).</summary>
    public virtual int? WeaponUpgradeFromPart(int partId) => null;

    /// <summary>The base weapon an upgrade <paramref name="partId"/> requires to be useful (mirror of
    /// <c>items.json.weaponPartBase</c>), or <c>null</c> if unknown. Drives the weapon-upgrade-chance
    /// feature: a part is only placed when its base weapon is in the seed. docs/decisions/cross/ITEM-RANDO-PLAN.md §7.3.</summary>
    public virtual int? WeaponForPart(int partId) => null;

    /// <summary>The pre-upgraded variants a base weapon can be placed as — each <c>(part, result)</c>
    /// pair = "apply this part to get this upgraded weapon id" (mirror of
    /// <c>items.json.weaponUpgradeVariants</c>). Drives the experimental pre-upgraded-weapon feature
    /// (<see cref="RandomizerConfig.PreUpgradedWeaponChance"/>); empty by default. docs/decisions/cross/ITEM-RANDO-PLAN.md §7.3.</summary>
    public virtual IReadOnlyList<(int Part, int Result)> WeaponUpgradeVariants(int baseWeapon)
        => Array.Empty<(int, int)>();

    /// <summary>The <see cref="WeaponFamily"/> a weapon / weapon-part id belongs to (mirror of
    /// <c>items.json.weaponFamilies</c>), or <c>null</c> for a non-weapon id (or a game with no families).
    /// A <c>null</c> family is always treated as enabled by the item pass — the per-family toggle only
    /// gates ids it knows. Drives <see cref="RandomizerConfig.EnabledWeaponFamilies"/>. docs/decisions/cross/ITEM-RANDO-PLAN.md §7.4.</summary>
    public virtual WeaponFamily? WeaponFamilyOf(int itemId) => null;

    /// <summary>The weapon families this game exposes as enable/disable toggles, in UI/CLI display order
    /// (flag + label). Empty by default; a game with a weapon model overrides it. docs/decisions/cross/ITEM-RANDO-PLAN.md §7.4.</summary>
    public virtual IReadOnlyList<(WeaponFamily Flag, string Name)> WeaponFamilies
        => Array.Empty<(WeaponFamily, string)>();

    /// <summary>The item id of the consumable "Plug" spent to open emergency boxes, or <c>null</c> if the
    /// game has no such mechanic. Plugs are key items (never rerolled), so the item pass conserves them;
    /// this id lets <see cref="Logic.PlugEconomy"/> count the plug supply. docs/decisions/cross/ITEM-RANDO-PLAN.md §7.4.</summary>
    public virtual int? PlugItemId => null;

    /// <summary>The game's emergency (storage) boxes — each a room + a plug cost (mirror of
    /// <c>data/dc1/emergency-boxes.json</c>). Optional storage, never progression gates; the randomizer
    /// tracks them only to surface the plug economy (supply vs demand). Empty by default; a game with no
    /// boxes overrides nothing. docs/decisions/cross/ITEM-RANDO-PLAN.md §7.4.</summary>
    public virtual IReadOnlyList<EmergencyBox> EmergencyBoxes => Array.Empty<EmergencyBox>();

    /// <summary>Discover the game's per-room files (stNXX.dat) inside an install dir.</summary>
    public abstract IReadOnlyList<RoomFileRef> EnumerateRooms(string installDir);

    /// <summary>
    /// Locate the game's data folder (where the per-room files live) inside an install
    /// dir, or <c>null</c> if none is found. Same discovery <see cref="EnumerateRooms"/>
    /// uses; the installer overlays randomized files here against a backup.
    /// </summary>
    public abstract string? GetDataDir(string installDir);
}

/// <summary>A located room file: its stage/room ids and path on disk.</summary>
public sealed record RoomFileRef(int Stage, int Room, string Path);

/// <summary>
/// Broad pickup class used to bias the item distribution. The UI exposes one ratio
/// per category (the pie chart's adjustable slices); <see cref="Passes.ItemRandomizer"/>
/// scales each entry's <see cref="ItemPoolEntry.Weight"/> by its category ratio.
/// </summary>
public enum ItemCategory
{
    Ammo,
    Health,
}

public sealed record ItemPoolEntry(int ItemId, string Name, double Weight,
                                   ItemCategory Category = ItemCategory.Ammo);
