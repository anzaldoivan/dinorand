using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;            // DataFormat
using Avalonia.Input.Platform;   // IClipboard (+ SetValueAsync / TryGetTextAsync)
using Avalonia.Media;            // IBrush / Brushes (foreground + border colours, lifted verbatim)
using Avalonia.Threading;        // Dispatcher (AP log lines arrive on a background thread)
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DinoRand.App.Services;
using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Ap;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Dc2.Passes;   // Dc2CharacterSkin
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Install;
using DinoRand.Randomizer.Voice;

namespace DinoRand.App
{
    /// <summary>
    /// MVVM view-model for <c>MainWindow</c> (AVALONIA-PORT decided code-behind first; this is the later
    /// MVVM refactor). It holds every bound control value as an <c>[ObservableProperty]</c>, exposes the
    /// window's actions as <c>[RelayCommand]</c>s, and preserves the original code-behind's bidirectional
    /// seed↔UI projection verbatim:
    ///   • <see cref="_appSeed"/> is the source of truth.
    ///   • editing any option rebuilds the seed (<see cref="UpdateSeedFromUi"/>);
    ///   • pasting / typing a <c>DINO-…</c> seed rebuilds the options (<see cref="UpdateUiFromSeed"/>);
    ///   • a <see cref="_suspend"/> re-entrancy guard breaks the feedback loop (was <c>_suspendEvents</c>),
    ///     so the seed box never fights the user while typing.
    /// Dialogs/file pickers come in through <see cref="IFilePicker"/>/<see cref="IDialogs"/> (swappable);
    /// the clipboard and the non-bindable <c>PieChart</c> are bridged by the thin view glue in code-behind.
    /// </summary>
    public sealed partial class MainWindowViewModel : ObservableObject
    {
        private const string DefaultGamePath = @"C:\Games\dinorand\english";
        private const int AmmoQuantityCenter = 7;

        /// <summary>The install path a game with no saved slice starts on: the DC1 default for the
        /// default game, blank for any other (so a fresh DC2 slice starts empty, not on DC1's path).</summary>
        private static string DefaultGamePathFor(GameDefinition game) =>
            game.Id == GameCatalog.Default.Id ? DefaultGamePath : "";
        private const string RandomDonorTag = "random";

        private static string WorkingModDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DinoRand", "mod_dinorand");

        private readonly IFilePicker _filePicker;
        private readonly IDialogs _dialogs;
        private readonly Func<IClipboard> _clipboard;

        private AppSeed _appSeed = AppSeed.Random();
        private readonly AppSettings _settings;
        private bool _suspend;
        private bool _gameDrmProtected;

        /// <summary>Id of the game whose <see cref="GameSettings"/> slice currently backs the bound
        /// fields. Tracked separately from <see cref="SelectedGame"/> so a game switch can save the
        /// <i>outgoing</i> game's slice before loading the incoming one.</summary>
        private string _currentGameId = GameCatalog.Default.Id;

        /// <summary>The persisted settings slice for the active game — each game keeps its own path /
        /// seed / voice prefs so switching games never carries another game's state.</summary>
        private GameSettings CurrentSlice => _settings.ForGame(_currentGameId);

        /// <summary>Raised once per <see cref="UpdateUiFromSeed"/> (where the old code-behind called
        /// <c>UpdateItemPie()</c>). The non-bindable <c>PieChart</c> can't take a binding, so the view
        /// subscribes to this and rebuilds the chart from <see cref="CurrentConfig"/>.</summary>
        public event Action PieDataChanged;

        /// <summary>The live config behind the current seed, for the view's PieChart glue (read-only).</summary>
        public RandomizerConfig CurrentConfig => _appSeed.Config;

        // <paramref name="settings"/> defaults to the on-disk store (%APPDATA%\DinoRand\settings.json);
        // tests pass an in-memory AppSettings so they neither read nor depend on a real settings file.
        // <paramref name="apRunner"/> / <paramref name="uiPost"/> are the AP connect tab's two seams
        // (a fake runner + a synchronous post make the connect/disconnect state machine unit-testable).
        public MainWindowViewModel(IFilePicker filePicker, IDialogs dialogs, Func<IClipboard> clipboard,
            AppSettings settings = null, ApRunner apRunner = null, Action<Action> uiPost = null)
        {
            _filePicker = filePicker;
            _dialogs = dialogs;
            _clipboard = clipboard;
            _settings = settings ?? AppSettings.Load();
            _apRunner = apRunner ?? Dc1ApRunner.Run;
            _uiPost = uiPost ?? (a => Dispatcher.UIThread.Post(a));

            BuildStartingInventoryEditor();

            // DC2 distribution rows: slider changes funnel into the seed like every other option;
            // the fixed-donor dropdown starts on the first pinnable species.
            foreach (var row in Dc2WeightOptions)
                row.Changed = UpdateSeedFromUi;
            foreach (var row in Dc2RaptorTierOptions)
                row.Changed = UpdateSeedFromUi;
            _selectedDc2FixedSpecies = Dc2FixedSpeciesOptions[0];
            RefreshDc2WeightVisibility(); // initial state: toggles start false ⇒ boss/setpiece rows hidden

            // Mirror Window_Loaded: seed the persisted fields under the guard so their change-hooks don't
            // run a wasteful (and immediately-overwritten) UpdateSeedFromUi before the seed is established.
            _suspend = true;
            var restoredGame = GameCatalog.FromId(_settings.SelectedGameId) ?? GameCatalog.Default;
            SelectedGameIndex = GameCatalog.All.ToList().FindIndex(g => g.Id == restoredGame.Id);
            _currentGameId = restoredGame.Id;
            var slice = _settings.ForGame(restoredGame.Id);
            GamePath = slice.GamePath ?? DefaultGamePathFor(restoredGame);
            // Voice donor settings are shared across games (cross-game datapacks), not per-slice.
            VoicePacksRoot = _settings.VoicePacksRoot ?? "";
            IsVoicesChecked = _settings.RandomizeCutsceneVoices;
            CrossGameVoices = _settings.IncludeCrossGameVoices;
            BgmPacksRoot = _settings.BgmPacksRoot ?? "";
            // AP connect tab: last-used server/slot (never the password).
            ApHostPort = _settings.ApHostPort ?? DefaultApHostPort;
            ApSlot = _settings.ApSlot ?? "";
            _suspend = false;

            if (slice.LastSeed is { } last && AppSeed.TryParse(last, out var parsed))
                _appSeed = NormalizeSeed(parsed);
            else
                _appSeed = AppSeed.Random();

            UpdateUiFromSeed();
            PopulateVoiceDonors();
            ApplyGameCapabilities();
            ValidateGamePath();
            RefreshInstallStatus();
        }

        // --- Seed / options state (bound) --------------------------------------

        [ObservableProperty] private string _seedText;
        [ObservableProperty] private string _seedQrString;
        [ObservableProperty] private IBrush _seedBorder;   // null ⇒ themed default (via NullToUnsetConverter)

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ReplacePoolVisible))]
        [NotifyPropertyChangedFor(nameof(ItemRatiosVisible))]
        private bool _randomizeItems;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Dc2EnemyOptionsVisible))]
        private bool _randomizeEnemies;

        // DC2-only cross-species donor-pool sub-options (docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md). They map to
        // RandomizerConfig.IncludeDc2{Setpiece,Boss}Enemies and show only for DC2 with enemy rando on.
        [ObservableProperty] private bool _includeDc2SetpieceEnemies;
        [ObservableProperty] private bool _includeDc2BossEnemies;

        // DC2 EXPERIMENTAL (off): "Allow Enemy swaps in the Water Levels" — lifts the aquatic-room block and
        // admits aquatic (wave-only) donors (K72). Maps to RandomizerConfig.Dc2AllowWaterLevelEnemySwaps.
        [ObservableProperty] private bool _dc2AllowWaterLevelEnemySwaps;

        // DC1 EXPERIMENTAL (off — in-game witness pending): shorten whitelisted cutscene brackets to their
        // side effects (CUTSCENE-SKIP-FEASIBILITY.md §9.3). Not seed-encoded — session-only, lost on seed paste.
        // ponytail: the GUI CheckBox that drives this is HIDDEN (MainWindow.axaml IsVisible=False) because the
        // lever does not shorten cutscenes in-game yet. Property + wiring + CLI path deliberately kept so the
        // control can be un-hidden once CutsceneShortener is CE-verified in-game. Do not delete.
        [ObservableProperty] private bool _shortenCutscenes;

        // DC2 (off): REbirth DoorSkip passthrough — Install writes DoorSkip = 1 into config.ini [DLL]
        // (K115). A user-config write, never seed-encoded; Restore does NOT touch it.
        // ponytail: the GUI CheckBox is HIDDEN (MainWindow.axaml IsVisible=False), matching the hidden
        // DC1 cutscene-shorten control — door/cutscene skip is off the user surface this release.
        // Defaults false (off). Property + wiring + CLI path kept; un-hide when skip ships. Do not delete.
        [ObservableProperty] private bool _dc2DoorSkip;

        // DC2 donor distribution (docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md D7): mode selector
        // (0 = Weighted, 1 = Fixed), the fixed-donor pick, and one weight slider row per registry
        // species. All changes funnel into UpdateSeedFromUi (the block seed-encodes, AppSeed D6).
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Dc2WeightedOptionsVisible))]
        [NotifyPropertyChangedFor(nameof(Dc2FixedOptionsVisible))]
        private int _dc2EnemyModeIndex;

        [ObservableProperty] private Dc2FixedSpeciesOption _selectedDc2FixedSpecies;

        /// <summary>Pinnable donors for fixed mode: every Known+LAND species (a pin IS the
        /// boss/setpiece opt-in, plan D3), plus the aquatic (wave-only) donors while the
        /// experimental water toggle is on — the same pool the pass validates the pin against.</summary>
        public IReadOnlyList<Dc2FixedSpeciesOption> Dc2FixedSpeciesOptions =>
            Dc2AllowWaterLevelEnemySwaps ? WaterFixedSpeciesOptions : LandFixedSpeciesOptions;

        // Cached per toggle value: the ComboBox matches SelectedItem by reference, so the
        // instances must be stable across gets.
        private static readonly IReadOnlyList<Dc2FixedSpeciesOption> LandFixedSpeciesOptions =
            BuildFixedSpeciesOptions(allowWater: false);
        private static readonly IReadOnlyList<Dc2FixedSpeciesOption> WaterFixedSpeciesOptions =
            BuildFixedSpeciesOptions(allowWater: true);

        private static IReadOnlyList<Dc2FixedSpeciesOption> BuildFixedSpeciesOptions(bool allowWater) =>
            Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: true, allowWater)
                .OrderBy(s => s.Type)
                .Select(s => new Dc2FixedSpeciesOption(s.Creature, s.Type))
                .ToList();

        private static readonly Dc2EnemyDistribution Dc2Distribution = Dc2EnemyDistribution.LoadEmbedded();

        /// <summary>One weight slider per registry species (weighted mode). Rows notify the VM on
        /// slide via <see cref="Dc2WeightOption.Changed"/> (wired in the constructor).</summary>
        public ObservableCollection<Dc2WeightOption> Dc2WeightOptions { get; } = new(
            Dc2Distribution.Rows
                .Select(r => new Dc2WeightOption(r.Creature, r.Type, r.DefaultWeight)));

        public bool Dc2WeightedOptionsVisible => Dc2EnemyModeIndex == 0;
        public bool Dc2FixedOptionsVisible => Dc2EnemyModeIndex == 1;

        // DC2 raptor tier randomization (docs/reference/dc2/enemies/RAPTOR-TIER-RE.md §4): master toggle, one weight
        // row per decoded variant (V5 = the blue/super raptor, rare by default), and the
        // "Blue Raptor Spawn Condition" combo-threshold slider (1–20 hits; 20 = vanilla).
        private static readonly Dc2RaptorTierTable Dc2RaptorTiers = Dc2RaptorTierTable.LoadEmbedded();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Dc2RaptorTierOptionsVisible))]
        private bool _dc2RandomizeRaptorTiers;

        // DC2 shop shuffle (docs/decisions/dc2/shop/DC2-SHOP-RANDO-PLAN.md): reversible Dino2.exe price +
        // stock-unlock permutation. Seed-string byte 16 bit 2.
        [ObservableProperty]
        private bool _dc2ShuffleShop;

        // DC2 puzzle randomization (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md): ONE master toggle
        // driving both subflags via RandomizerConfig.Dc2RandomizePuzzles — the elevator-code scramble
        // (K108, Dino2.exe) and the stungun-circuit shuffle (K110, ST607/ST402 room files).
        // Seed-string byte 16 bits 3+4. Default ON for DC2 (ApplyGameCapabilities).
        [ObservableProperty]
        private bool _dc2RandomizePuzzles;

        // DC2 cross-character weapons (DC2-CROSS-CHAR-WEAPON-MODEL-SWAP.md): each character can wield
        // the other's weapons on their OWN body model. Eight WEP_P grafts built at install time from
        // the user's own Data files + a Dino2.exe catalog/owner-flag patch.
        // Seed-string byte 16 bit 6. Default OFF (four of the eight pairs await an in-game witness).
        [ObservableProperty]
        private bool _dc2CrossCharWeapons;

        // DC2 randomized MAIN ownership: exact seeded three/three split. Implies the cross-character
        // graft prerequisite and is mutually exclusive with shared weapons. Seed byte 16 bit 7.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanUseDc2SharedWeapons))]
        private bool _dc2RandomizeWeapons;

        public bool CanUseDc2SharedWeapons => !Dc2RandomizeWeapons;

        // DC2 EXPERIMENTAL: share the SUB weapons (Machete, Large Stungun) between Regina and Dylan
        // via the item-catalog owner bits (K125). Not seed-encoded. Default OFF.
        [ObservableProperty]
        private bool _dc2SharedWeapons;

        // DC2 starting main weapon (docs/decisions/dc2/loadout/DC2-STARTING-LOADOUT-PLAN.md): new-game bootstrap
        // equip-immediate patch; subweapon bytes never touched (Machete/Stun Gun kept). Seed-string
        // byte 22. Dropdown index 0 = random-from-band, n = the band's (n-1)th id; names beyond the
        // canonical shotgun/handgun are unverified until the I3 in-game witness.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Dc2StartWeaponOptionsVisible))]
        private bool _dc2RandomizeStartWeapon;

        [ObservableProperty] private int _dc2DylanStartWeaponIndex;
        [ObservableProperty] private int _dc2ReginaStartWeaponIndex;

        // Add-and-equip: also install the weapon-ring div-0 zero-guard, unlocking each character's
        // full band for the RANDOM roll (SUBs, other-char mains, the fire-empty Grenade Gun). The
        // explicit dropdowns still offer only owned mains, which are safe in either mode.
        [ObservableProperty] private bool _dc2AddAndEquipStartWeapon;

        // I3-witnessed names (DC2-STARTING-LOADOUT-PLAN.md witness table); absent ids = unverified.
        // Only owned mains are offered (catalog 0x704260: MAIN flag + the character's ownership bit);
        // non-owned-mains (a SUB / other-character main / fire-empty 0x04,0x07) brick the weapon menu
        // (div-0) and are omitted from the offered set — see Dc2StartingLoadoutPatch.Selectable*Ids.
        // Names confirmed in-game (2026-07-05): 0x03 Rocket Launcher, 0x04 Solid Cannon,
        // 0x05 Flamethrower, 0x09 Antitank Rifle. Others render as "0xNN (unverified)" until witnessed.
        private static readonly Dictionary<byte, string> Dc2DylanWeaponNames = new()
            { [0x01] = "Shotgun", [0x03] = "Rocket Launcher", [0x04] = "Solid Cannon",
              [0x05] = "Flamethrower", [0x09] = "Antitank Rifle" };
        private static readonly Dictionary<byte, string> Dc2ReginaWeaponNames = new()
            { [0x02] = "Handgun", [0x05] = "Flamethrower" };

        public IReadOnlyList<string> Dc2DylanStartWeaponOptions { get; } =
            BuildStartWeaponLabels(Dc2StartingLoadoutPatch.SelectableDylanIds, Dc2DylanWeaponNames);
        public IReadOnlyList<string> Dc2ReginaStartWeaponOptions { get; } =
            BuildStartWeaponLabels(Dc2StartingLoadoutPatch.SelectableReginaIds, Dc2ReginaWeaponNames);

        public bool Dc2StartWeaponOptionsVisible => Dc2RandomizeStartWeapon;

        private static string[] BuildStartWeaponLabels(byte[] band, IReadOnlyDictionary<byte, string> names) =>
            new[] { "Random" }
                .Concat(band.Select(id => names.TryGetValue(id, out var n)
                    ? $"0x{id:X2} — {n}"
                    : $"0x{id:X2} (unverified)"))
                .ToArray();

        /// <summary>One weight slider per raptor tier variant (reuses <see cref="Dc2WeightOption"/>;
        /// Type carries the variant number).</summary>
        public ObservableCollection<Dc2WeightOption> Dc2RaptorTierOptions { get; } = new(
            Dc2RaptorTiers.Rows.Select(r => new Dc2WeightOption(r.Label, r.Variant, r.DefaultWeight)));

        public bool Dc2RaptorTierOptionsVisible => Dc2RandomizeRaptorTiers;

        /// <summary>The whole raptor-tier group shows for DC2 regardless of the enemy-swap toggle
        /// (the tier pass is independent of the cross-species pass).</summary>
        public bool Dc2RaptorPanelVisible => SelectedGame.Id == "dc2";

        [ObservableProperty] private double _dc2BlueRaptorCombo = 20;

        /// <summary>0 = RoomTier (colour == strength), 1 = MixedTiers (colour = strongest in room).</summary>
        [ObservableProperty] private int _dc2RaptorColourModeIndex;

        // DC2-only character-skin swap: Dylan renders as Gail/Rick via their Extra Crisis WP graft
        // files + the WP-gate exe patch (docs/reference/dc2/models/DC2-EXTRA-CRISIS-ROSTER-DECODE.md §7-9). Index into
        // Dc2CharacterSkin (0=Dylan, 1=Gail, 2=Rick, 3=Random); hidden without GameFeature.PlayerModel.
        [ObservableProperty] private int _dc2CharacterSkinIndex;

        // Same enum/indexing for Regina (0=Stock ... 3=Random). Cross-rig graft, in-game verified;
        // known cosmetic tech debt: the skin also shows in in-engine cutscenes.
        [ObservableProperty] private int _dc2ReginaSkinIndex;

        [ObservableProperty] private bool _randomizeDoors;
        [ObservableProperty] private bool _shuffleKeyItems;
        // On by default; NOT seed-encoded (like RelocateDdkDiscs) — a cosmetic/QoL placement constraint,
        // not part of the seed identity. PICKUP-VISUAL-PLACEMENT-PLAN.md.
        [ObservableProperty] private bool _avoidHiddenPickupSpots = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CustomSupplyEnabled))]
        [NotifyPropertyChangedFor(nameof(CustomSupplyLabel))]
        private bool _randomizeStartingInventory;

        [ObservableProperty] private bool _crossGameVoices;
        [ObservableProperty] private bool? _isVoicesChecked;

        [ObservableProperty] private double _difficulty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ItemRatiosVisible))]
        private bool _replaceItemPool;

        [ObservableProperty] private double _ammo;
        [ObservableProperty] private double _health;
        [ObservableProperty] private double _ammoQuantity;
        [ObservableProperty] private double _weaponUpgrade;
        [ObservableProperty] private bool _preUpgradedWeapons;
        [ObservableProperty] private bool _weaponHandgun;
        [ObservableProperty] private bool _weaponShotgun;
        [ObservableProperty] private bool _weaponGrenade;

        // Computed visibility / enablement (were imperative IsVisible/IsEnabled writes in UpdateUiFromSeed).
        public bool ReplacePoolVisible => RandomizeItems;
        public bool ItemRatiosVisible => RandomizeItems && ReplaceItemPool;
        public bool CustomSupplyEnabled => !RandomizeStartingInventory;
        public string CustomSupplyLabel => RandomizeStartingInventory
            ? "Custom supply slots (disabled while 'Randomize starting inventory' is on)"
            : "Custom supply slots (optional — sets the supply kit explicitly):";

        // --- Game selection (bound) --------------------------------------------

        /// <summary>ComboBox labels for the selectable games (docs/decisions/cross/GAME-SELECTOR-PLAN.md). Drawn from
        /// <see cref="GameCatalog.All"/>; an unfinished game (<see cref="GameDefinition.IsImplemented"/>
        /// false) is suffixed "(experimental)" and fenced out of Generate/Install.</summary>
        public IReadOnlyList<string> Games { get; } = GameCatalog.All
            .Select(g => g.IsImplemented ? g.DisplayName : $"{g.DisplayName} (experimental)")
            .ToList();

        [ObservableProperty] private int _selectedGameIndex;

        /// <summary>The currently selected game definition — the single routing seam that replaces the
        /// old hardcoded <c>new DinoCrisis1()</c>.</summary>
        public GameDefinition SelectedGame => GameCatalog.All[SelectedGameIndex];

        // --- Per-game option capability (docs/decisions/cross/DC2-OPTION-GATING-PLAN.md). Each is bound to the matching
        //     option's IsEnabled; they recompute on game switch via ApplyGameCapabilities(). A game that
        //     doesn't support a feature greys its option (and the option is force-unchecked on switch). ---
        public bool CanRandomizeItems => SelectedGame.Supports(GameFeature.Items);
        public bool CanRandomizeEnemies => SelectedGame.Supports(GameFeature.Enemies);

        /// <summary>The DC2 cross-species donor sub-options (setpiece/boss) show only for DC2 with enemy
        /// randomization on — they have no meaning for DC1. docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md.</summary>
        public bool Dc2EnemyOptionsVisible => RandomizeEnemies && SelectedGame.Id == "dc2";

        /// <summary>The DC2 player model swap shows only where the game supports it (DC2 today) —
        /// the option is hidden, not greyed, for DC1. docs/decisions/dc2/models/DC2-PLAYER-SWAP-PARITY-PLAN.md.</summary>
        public bool CanSwapDc2PlayerCharacters => SelectedGame.Supports(GameFeature.PlayerModel);
        public bool CanRandomizeDoors => SelectedGame.Supports(GameFeature.Doors);
        public bool CanShuffleKeyItems => SelectedGame.Supports(GameFeature.KeyItems);
        // Cutscene shortening is DC1-only today (the flag(2,2) bracket protocol is a DC1 decode, cont.74).
        public bool CanShortenCutscenes => SelectedGame.Id == "dc1";

        // Door skip is a DC1-only DINO.exe patch (mode-6 door machine, cont.78).
        public bool CanDc1DoorSkip => SelectedGame.Id == "dc1";

        // Fast-forward cutscenes is a DC1-only DINO.exe patch (SCD-VM tick multiplier, cont.79 v2).
        public bool CanDc1FastForwardCutscenes => SelectedGame.Id == "dc1";

        // The key-item scatter (into ammo/health pickups) and the DDK Input/Code disc relocation have no
        // toggles: both ride on the key shuffle, config-built as ShuffleKeyItemsIntoPickups = ShuffleKeyItems
        // and RelocateDdkDiscs = ShuffleKeyItems, so "Shuffle Key Items" does all three key-shuffle behaviors
        // at once. The RandomizerConfig flags stay independent for tests. PROGRESSION-KEY-RELOCATION-RESEARCH.md /
        // docs/decisions/dc1/items/KEY-ITEM-SCATTER-DATA-AUDIT.md.
        public bool CanRandomizeStartingInventory => SelectedGame.Supports(GameFeature.StartingInventory);
        public bool CanRandomizeVoices => SelectedGame.Supports(GameFeature.Voices);
        public bool CanShuffleBgm => SelectedGame.Supports(GameFeature.Bgm);
        // External BGM import is DC1-only for now (DC2 music lives in undecoded MS_ container payloads —
        // docs/decisions/cross/BGM-RANDO-PLAN.md).
        public bool CanImportBgm => CanShuffleBgm && SelectedGame.Id == "dc1";
        public bool CanRandomizeBoxes => SelectedGame.Supports(GameFeature.EmergencyBoxes);
        public bool CanScramblePuzzleCodes => SelectedGame.Supports(GameFeature.PuzzleCodes);

        /// <summary>Re-raise the capability flags for the current game and force every unsupported option
        /// OFF so a disabled (greyed) option can never leak a <c>true</c> into the seed. Per-game seed
        /// isolation keeps this from corrupting the other game's saved toggles.</summary>
        private void ApplyGameCapabilities()
        {
            // Game-specific list content (supply items / weapons) → real DC1 lists or a placeholder.
            BuildStartingInventoryEditor();

            var prevSuspend = _suspend;
            _suspend = true;
            if (!CanRandomizeItems) RandomizeItems = false;
            if (!CanRandomizeEnemies) RandomizeEnemies = false;
            // DC2-only sub-options: clear on any game that can't host them, so a greyed/hidden toggle
            // never leaks a true into the config (DC1 ignores these fields anyway).
            if (SelectedGame.Id != "dc2")
            {
                IncludeDc2SetpieceEnemies = false; IncludeDc2BossEnemies = false;
                Dc2AllowWaterLevelEnemySwaps = false;
                // Distribution block back to defaults too, so a non-DC2 game never emits it.
                Dc2EnemyModeIndex = 0;
                foreach (var row in Dc2WeightOptions)
                    row.Weight = Dc2Distribution.DefaultWeights.GetValueOrDefault(row.Type);
                // Raptor tier block back to defaults too, so a non-DC2 game never emits it.
                Dc2RandomizeRaptorTiers = false;
                Dc2ShuffleShop = false;
                Dc2RandomizePuzzles = false;
                Dc2CrossCharWeapons = false;
                Dc2RandomizeWeapons = false;
                Dc2SharedWeapons = false;
                Dc2RandomizeStartWeapon = false;
                Dc2AddAndEquipStartWeapon = false;
                Dc2DoorSkip = false;
                Dc2DylanStartWeaponIndex = 0;
                Dc2ReginaStartWeaponIndex = 0;
                Dc2BlueRaptorCombo = 20;
                Dc2RaptorColourModeIndex = 0;
                foreach (var row in Dc2RaptorTierOptions)
                    row.Weight = Dc2RaptorTiers.DefaultWeights.GetValueOrDefault(row.Type);
            }
            else
            {
                // GUI defaults for DC2: these start ON (DC2-PUZZLE-RANDO-PLAN.md + maintainer request).
                // Runs after UpdateUiFromSeed on startup/game-switch, so it wins over a persisted
                // seed's cleared bits; the RandomizerConfig/CLI defaults stay off, and a seed pasted
                // AFTER entering DC2 still round-trips exactly (this hook doesn't run on seed paste).
                Dc2RandomizePuzzles = true;
                IncludeDc2BossEnemies = true;      // T-Rex in the cross-species donor pool by default
                Dc2ShuffleShop = true;
                Dc2RandomizeRaptorTiers = true;
                Dc2AddAndEquipStartWeapon = true;  // effective only when Randomize Starting Weapon is on
            }
            if (!CanSwapDc2PlayerCharacters) { Dc2CharacterSkinIndex = 0; Dc2ReginaSkinIndex = 0; }
            if (!CanRandomizeDoors) RandomizeDoors = false;
            // GUI default: Shuffle Key Items starts ON where the game supports it (DC1), forced OFF
            // otherwise. Like the DC2 defaults above, this wins over a persisted seed on startup/game-switch
            // but not on paste (this hook doesn't run on seed paste); the RandomizerConfig/CLI default stays off.
            ShuffleKeyItems = CanShuffleKeyItems;
            // GUI default: Insert upgraded weapons starts ON (item-pool block; DC1-only visible, harmless for DC2).
            PreUpgradedWeapons = true;
            if (!CanShortenCutscenes) ShortenCutscenes = false;
            if (!CanDc1DoorSkip) Dc1DoorSkip = false;
            if (!CanDc1FastForwardCutscenes) Dc1FastForwardCutscenes = false;
            if (!CanRandomizeStartingInventory) RandomizeStartingInventory = false;
            if (!CanRandomizeVoices) IsVoicesChecked = false;
            if (!CanShuffleBgm) ShuffleBgm = false;
            if (!CanImportBgm) ImportBgm = false;
            if (!CanRandomizeBoxes) RandomizeBoxes = false;
            if (!CanScramblePuzzleCodes) ScramblePuzzleCodes = false;
            _suspend = prevSuspend;

            OnPropertyChanged(nameof(CanRandomizeItems));
            OnPropertyChanged(nameof(CanRandomizeEnemies));
            OnPropertyChanged(nameof(Dc2EnemyOptionsVisible));
            OnPropertyChanged(nameof(Dc2RaptorPanelVisible));
            OnPropertyChanged(nameof(CanSwapDc2PlayerCharacters));
            OnPropertyChanged(nameof(CanRandomizeDoors));
            OnPropertyChanged(nameof(CanShuffleKeyItems));
            OnPropertyChanged(nameof(CanShortenCutscenes));
            OnPropertyChanged(nameof(CanDc1DoorSkip));
            OnPropertyChanged(nameof(CanDc1FastForwardCutscenes));
            OnPropertyChanged(nameof(CanRandomizeStartingInventory));
            OnPropertyChanged(nameof(CanRandomizeVoices));
            OnPropertyChanged(nameof(ShowDc1VoiceCast));
            OnPropertyChanged(nameof(ShowDc2VoiceCast));
            OnPropertyChanged(nameof(CanShuffleBgm));
            OnPropertyChanged(nameof(CanImportBgm));
            OnPropertyChanged(nameof(CanRandomizeBoxes));
            OnPropertyChanged(nameof(CanScramblePuzzleCodes));
            OnPropertyChanged(nameof(CanUseArchipelago));
            OnPropertyChanged(nameof(GameContentPlaceholder));

            // Re-sync the seed to the (possibly forced-off) toggles so the seed string and the checkboxes
            // agree. No-op for a fully-supported game. Guarded internally by _suspend.
            UpdateSeedFromUi();
        }

        partial void OnSelectedGameIndexChanged(int value)
        {
            if (_suspend)
                return;

            // Save the OUTGOING game's slice from the current UI, then swap to the newly-selected game
            // and load ITS slice — so each game keeps its own path/seed/voice prefs and switching never
            // carries the other game's state (BioRand saves on selection change, MainWindow.xaml.cs:1059).
            PersistCurrentSliceFromUi();

            _currentGameId = SelectedGame.Id;
            _settings.SelectedGameId = SelectedGame.Id;

            var slice = _settings.ForGame(_currentGameId);
            _suspend = true;
            GamePath = slice.GamePath ?? DefaultGamePathFor(SelectedGame);
            // Voice donor settings are shared across games, so they are NOT swapped on a game change.
            _appSeed = slice.LastSeed is { } last && AppSeed.TryParse(last, out var parsed)
                ? NormalizeSeed(parsed)
                : AppSeed.Random();
            _suspend = false;

            UpdateUiFromSeed();
            PopulateVoiceDonors();
            ApplyGameCapabilities();
            _settings.Save();
            ValidateGamePath();
            RefreshInstallStatus();
        }

        /// <summary>Snapshot the current UI's per-game state (install path / seed) into the active game's
        /// slice. Called before switching games and before generate/install so the slice is the durable
        /// per-game record. Voice donor settings are shared (persisted on <see cref="_settings"/> directly),
        /// so they are not written here.</summary>
        private void PersistCurrentSliceFromUi()
        {
            var slice = _settings.ForGame(_currentGameId);
            slice.GamePath = GamePath;
            slice.LastSeed = _appSeed.ToString();
            _settings.VoicePacksRoot = string.IsNullOrWhiteSpace(VoicePacksRoot) ? null : VoicePacksRoot.Trim();
            _settings.RandomizeCutsceneVoices = IsVoicesChecked == true;
            _settings.IncludeCrossGameVoices = CrossGameVoices;
        }

        // --- Paths + validation labels (bound) ---------------------------------

        [ObservableProperty] private string _gamePath;
        [ObservableProperty] private string _voicePacksRoot;
        [ObservableProperty] private string _gameValidationText;
        [ObservableProperty] private IBrush _gameValidationBrush;
        [ObservableProperty] private string _voicePacksValidationText;
        [ObservableProperty] private IBrush _voicePacksValidationBrush;

        [ObservableProperty] private string _ratioVanillaText;
        [ObservableProperty] private IBrush _ratioVanillaBrush;

        [ObservableProperty] private string _installStatusText;
        [ObservableProperty] private IBrush _installStatusBrush;

        // --- Busy + button enablement (bound) ----------------------------------

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanPlayNow))]
        [NotifyPropertyChangedFor(nameof(ShouldConfirmClose))]
        [NotifyPropertyChangedFor(nameof(CloseConfirmMessage))]
        private bool _isBusy;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ApTabEnabled))]
        private bool _canInstall;
        [ObservableProperty] private bool _canRestore;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanPlayNow))]
        private bool _canPlay;

        /// <summary>Play is offered only when a game exe resolves AND no generate/install/restore is
        /// running — so the game can't be launched mid-install. Recomputed via NotifyPropertyChangedFor
        /// on both source flags (the codebase's derived-property pattern, no XAML multi-binding).</summary>
        public bool CanPlayNow => CanPlay && !IsBusy;

        // --- Install options (bound) -------------------------------------------

        [ObservableProperty] private bool _shuffleBgm;

        // Door skip (experimental, DC1): reversible DINO.exe patch applied at install (cont.78). Default OFF.
        [ObservableProperty] private bool _dc1DoorSkip;

        // Fast-forward cutscenes (experimental/crash risk, DC1): reversible DINO.exe patch applied at
        // install (cont.79 v2 guarded tick multiplier). Default OFF, not seed-encoded.
        [ObservableProperty] private bool _dc1FastForwardCutscenes;

        // External BGM import (DC1): overwrite Sound/BGM/ slots with tagged donor tracks from BgmPacksRoot.
        [ObservableProperty] private bool _importBgm;
        [ObservableProperty] private string _bgmPacksRoot = "";

        [ObservableProperty] private bool _scramblePuzzleCodes = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BoxModeEnabled))]
        private bool _randomizeBoxes = true;

        [ObservableProperty] private int _boxModeIndex = 1; // default = Reroll from box pool
        public bool BoxModeEnabled => RandomizeBoxes;

        // --- Starting-inventory editor (bound) ---------------------------------

        public ObservableCollection<StartWeaponOption> StartWeapons { get; } = new();
        public ObservableCollection<SupplyOption> SupplyItemsList { get; } = new();
        [ObservableProperty] private StartWeaponOption _selectedStartWeapon;
        [ObservableProperty] private SupplyOption _selectedItem0;
        [ObservableProperty] private SupplyOption _selectedItem1;
        [ObservableProperty] private SupplyOption _selectedItem2;
        [ObservableProperty] private string _count0 = "1";
        [ObservableProperty] private string _count1 = "1";
        [ObservableProperty] private string _count2 = "1";

        // --- Voice donors (bound) ----------------------------------------------

        public ObservableCollection<DonorOption> VoiceDonorOptions { get; } = new();
        [ObservableProperty] private DonorOption _selectedDonorRegina;
        [ObservableProperty] private DonorOption _selectedDonorRick;
        [ObservableProperty] private DonorOption _selectedDonorGail;
        [ObservableProperty] private DonorOption _selectedDonorKirk;
        // DC2 cast (folder-curation 2026-07-05). "regina" is shared with DC1's pin key on purpose —
        // same character, same persisted donor.
        [ObservableProperty] private DonorOption _selectedDonorDylan;
        [ObservableProperty] private DonorOption _selectedDonorDc2Regina;
        [ObservableProperty] private DonorOption _selectedDonorDavid;
        [ObservableProperty] private DonorOption _selectedDonorOldDylan;

        /// <summary>Which cast grid the voices group shows (the rows are per-game hard-coded, like DC1's).</summary>
        public bool ShowDc1VoiceCast => CanRandomizeVoices && SelectedGame.Id == "dc1";
        public bool ShowDc2VoiceCast => CanRandomizeVoices && SelectedGame.Id == "dc2";

        // --- Change hooks (were the *_Changed / *_ValueChanged handlers) --------

        partial void OnSeedTextChanged(string value)
        {
            if (_suspend)
                return;
            if (AppSeed.TryParse(value, out var parsed))
            {
                _appSeed = NormalizeSeed(parsed);
                UpdateUiFromSeed(updateSeedText: false);
            }
            else
            {
                SeedBorder = Brushes.Red;
            }
        }

        partial void OnRandomizeItemsChanged(bool value) => UpdateSeedFromUi();
        partial void OnRandomizeEnemiesChanged(bool value) => UpdateSeedFromUi();
        // The pool toggles also drive weight-row visibility — refreshed OUTSIDE UpdateSeedFromUi so
        // it happens on every path (user click, seed paste, game switch), _suspend included.
        partial void OnIncludeDc2SetpieceEnemiesChanged(bool value)
        {
            RefreshDc2WeightVisibility();
            UpdateSeedFromUi();
        }

        partial void OnIncludeDc2BossEnemiesChanged(bool value)
        {
            RefreshDc2WeightVisibility();
            UpdateSeedFromUi();
        }

        partial void OnDc2AllowWaterLevelEnemySwapsChanged(bool value)
        {
            // Admits aquatic species to the pool ⇒ their weight rows become relevant. Not seed-encoded
            // (experimental), so no UpdateSeedFromUi — just refresh the visible weight set.
            RefreshDc2WeightVisibility();

            // The fixed-species dropdown swaps between the land/water lists; remap the selection
            // by TYPE onto the new instance list. Toggle off with an aquatic pin ⇒ fall back to
            // the first land donor (a stale aquatic pin would make the pass skip every room).
            OnPropertyChanged(nameof(Dc2FixedSpeciesOptions));
            SelectedDc2FixedSpecies = Dc2FixedSpeciesOptions
                .FirstOrDefault(o => o.Type == SelectedDc2FixedSpecies?.Type)
                ?? Dc2FixedSpeciesOptions[0];
        }

        /// <summary>Show each weight row iff its species is in the donor pool under the current
        /// toggles — the tested registry rule (<see cref="Dc2SpeciesTable.IsDonorPoolMember"/>), so
        /// the UI can never offer a weight the pick can't use. Values are kept, only hidden.</summary>
        private void RefreshDc2WeightVisibility()
        {
            foreach (var row in Dc2WeightOptions)
                row.IsVisible = Dc2SpeciesTable.IsDonorPoolMember(
                    row.Type, IncludeDc2SetpieceEnemies, IncludeDc2BossEnemies, Dc2AllowWaterLevelEnemySwaps);
        }
        partial void OnDc2EnemyModeIndexChanged(int value) => UpdateSeedFromUi();
        partial void OnDc2RandomizeRaptorTiersChanged(bool value) => UpdateSeedFromUi();
        partial void OnDc2ShuffleShopChanged(bool value) => UpdateSeedFromUi();
        partial void OnDc2RandomizePuzzlesChanged(bool value) => UpdateSeedFromUi();
        partial void OnDc2CrossCharWeaponsChanged(bool value) => UpdateSeedFromUi();
        partial void OnDc2RandomizeWeaponsChanged(bool value)
        {
            if (value) Dc2SharedWeapons = false;
            UpdateSeedFromUi();
        }
        partial void OnShortenCutscenesChanged(bool value) => UpdateSeedFromUi();
        partial void OnDc2DoorSkipChanged(bool value) => UpdateSeedFromUi();
        partial void OnDc2RandomizeStartWeaponChanged(bool value) => UpdateSeedFromUi();
        partial void OnDc2AddAndEquipStartWeaponChanged(bool value) => UpdateSeedFromUi();
        partial void OnDc2DylanStartWeaponIndexChanged(int value) => UpdateSeedFromUi();
        partial void OnDc2ReginaStartWeaponIndexChanged(int value) => UpdateSeedFromUi();
        partial void OnDc2BlueRaptorComboChanged(double value) => UpdateSeedFromUi();
        partial void OnDc2RaptorColourModeIndexChanged(int value) => UpdateSeedFromUi();
        partial void OnSelectedDc2FixedSpeciesChanged(Dc2FixedSpeciesOption value) => UpdateSeedFromUi();
        partial void OnDc2CharacterSkinIndexChanged(int value) => UpdateSeedFromUi();
        partial void OnDc2ReginaSkinIndexChanged(int value) => UpdateSeedFromUi();
        partial void OnRandomizeDoorsChanged(bool value) => UpdateSeedFromUi();
        // Key shuffle is a single toggle: scatter (into ammo/health) and DDK-disc relocation ride on it,
        // config-built as ShuffleKeyItemsIntoPickups = RelocateDdkDiscs = ShuffleKeyItems.
        partial void OnShuffleKeyItemsChanged(bool value) => UpdateSeedFromUi();
        partial void OnRandomizeStartingInventoryChanged(bool value) => UpdateSeedFromUi();
        // Import external music: its only effect path is config.RandomizeBgm (UpdateSeedFromUi) → the
        // BgmRandomizer pass. Without this trigger the derivation was dead — a toggle never reached the
        // config. (ShuffleBgm/boxes/puzzle-codes need no hook: Install() reads those properties directly.)
        partial void OnImportBgmChanged(bool value) => UpdateSeedFromUi();
        partial void OnReplaceItemPoolChanged(bool value) => UpdateSeedFromUi();
        partial void OnPreUpgradedWeaponsChanged(bool value) => UpdateSeedFromUi();
        partial void OnWeaponHandgunChanged(bool value) => UpdateSeedFromUi();
        partial void OnWeaponShotgunChanged(bool value) => UpdateSeedFromUi();
        partial void OnWeaponGrenadeChanged(bool value) => UpdateSeedFromUi();
        partial void OnDifficultyChanged(double value) => UpdateSeedFromUi();
        partial void OnAmmoQuantityChanged(double value) => UpdateSeedFromUi();
        partial void OnWeaponUpgradeChanged(double value) => UpdateSeedFromUi();

        partial void OnIsVoicesCheckedChanged(bool? value) => UpdateSeedFromUi();

        partial void OnCrossGameVoicesChanged(bool value)
        {
            if (_suspend)
                return;
            PopulateVoiceDonors();
            UpdateSeedFromUi();
        }

        partial void OnSelectedDonorReginaChanged(DonorOption value) => UpdateSeedFromUi();
        partial void OnSelectedDonorRickChanged(DonorOption value) => UpdateSeedFromUi();
        partial void OnSelectedDonorGailChanged(DonorOption value) => UpdateSeedFromUi();
        partial void OnSelectedDonorKirkChanged(DonorOption value) => UpdateSeedFromUi();
        partial void OnSelectedDonorDylanChanged(DonorOption value) => UpdateSeedFromUi();
        partial void OnSelectedDonorDc2ReginaChanged(DonorOption value) => UpdateSeedFromUi();
        partial void OnSelectedDonorDavidChanged(DonorOption value) => UpdateSeedFromUi();
        partial void OnSelectedDonorOldDylanChanged(DonorOption value) => UpdateSeedFromUi();

        // Ammo/Health share the ratio-zero coercion (docs/decisions/cross/ITEM-RATIO-ZERO-PLAN.md): they can't both be 0.
        partial void OnAmmoChanged(double value) => OnRatioSliderChanged(coerceTarget: RatioTarget.Health);
        partial void OnHealthChanged(double value) => OnRatioSliderChanged(coerceTarget: RatioTarget.Ammo);

        private enum RatioTarget { Ammo, Health }

        private void OnRatioSliderChanged(RatioTarget coerceTarget)
        {
            if (_suspend)
                return;
            if (Ammo == 0 && Health == 0)
            {
                _suspend = true;
                try
                {
                    if (coerceTarget == RatioTarget.Ammo) Ammo = 1;
                    else Health = 1;
                }
                finally { _suspend = false; }
            }
            UpdateSeedFromUi();
        }

    }

    /// <summary>The AP session entry point as a seam — <see cref="DinoRand.Randomizer.Ap.Dc1ApRunner.Run"/>
    /// in production, a fake in the connect-tab state-machine tests.</summary>
    public delegate int ApRunner(string hostPort, string slot, string password, string install,
        string outDir, Action<string> log, Action<string> error, CancellationToken ct);

    /// <summary>A starting-weapon dropdown choice. <see cref="WeaponId"/> is null for "Vanilla".</summary>
    public sealed class StartWeaponOption
    {
        public string Display { get; }
        public int? WeaponId { get; }
        public StartWeaponOption(string display, int? weaponId) { Display = display; WeaponId = weaponId; }
        public override string ToString() => Display;
    }

    /// <summary>A custom-supply-slot dropdown choice. <see cref="Id"/> is 0 for "(empty)".</summary>
    public sealed class SupplyOption
    {
        public string Display { get; }
        public int Id { get; }
        public SupplyOption(string display, int id) { Display = display; Id = id; }
        public override string ToString() => Display;
    }

    /// <summary>A fixed-mode donor dropdown choice (docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md D7):
    /// <see cref="Type"/> is the species ctor TYPE the pin maps to.</summary>
    public sealed class Dc2FixedSpeciesOption
    {
        public string Display { get; }
        public int Type { get; }
        public Dc2FixedSpeciesOption(string display, int type) { Display = display; Type = type; }
        public override string ToString() => Display;
    }

    /// <summary>One weighted-mode slider row: a species and its weight (0–15). <see cref="Changed"/>
    /// funnels slides into the VM's <c>UpdateSeedFromUi</c> (wired at construction; suspended
    /// updates are guarded there like every other option).</summary>
    public sealed class Dc2WeightOption : ObservableObject
    {
        public string Creature { get; }
        public int Type { get; }
        public Action Changed { get; set; }

        private double _weight;
        public double Weight
        {
            get => _weight;
            set { if (SetProperty(ref _weight, value)) Changed?.Invoke(); }
        }

        /// <summary>Display-only: a species outside the toggled donor pool hides its row (the VM
        /// keeps it in lock-step with <c>Dc2SpeciesTable.IsDonorPoolMember</c>). Hiding never touches
        /// <see cref="Weight"/> — the value still round-trips in the seed.</summary>
        private bool _isVisible = true;
        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }

        public Dc2WeightOption(string creature, int type, byte weight)
        {
            Creature = creature;
            Type = type;
            _weight = weight;
        }
    }

    /// <summary>A voice-donor dropdown choice. <see cref="Tag"/> is null for "Default", "random" for
    /// "Random", else "actor.game".</summary>
    public sealed class DonorOption
    {
        public string Display { get; }
        public string Tag { get; }
        public DonorOption(string display, string tag) { Display = display; Tag = tag; }
        public override string ToString() => Display;
    }
}
