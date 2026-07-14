using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;            // DataFormat
using Avalonia.Input.Platform;   // IClipboard (+ SetValueAsync / TryGetTextAsync)
using Avalonia.Media;            // IBrush / Brushes (foreground + border colours, lifted verbatim)
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DinoRand.App.Services;
using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer;
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
        public MainWindowViewModel(IFilePicker filePicker, IDialogs dialogs, Func<IClipboard> clipboard,
            AppSettings settings = null)
        {
            _filePicker = filePicker;
            _dialogs = dialogs;
            _clipboard = clipboard;
            _settings = settings ?? AppSettings.Load();

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

        // DC2 donor distribution (docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md D7): mode selector
        // (0 = Weighted, 1 = Fixed), the fixed-donor pick, and one weight slider row per registry
        // species. All changes funnel into UpdateSeedFromUi (the block seed-encodes, AppSeed D6).
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Dc2WeightedOptionsVisible))]
        [NotifyPropertyChangedFor(nameof(Dc2FixedOptionsVisible))]
        private int _dc2EnemyModeIndex;

        [ObservableProperty] private Dc2FixedSpeciesOption _selectedDc2FixedSpecies;

        /// <summary>Pinnable donors for fixed mode: every Known+LAND species (a pin IS the
        /// boss/setpiece opt-in, plan D3).</summary>
        public IReadOnlyList<Dc2FixedSpeciesOption> Dc2FixedSpeciesOptions { get; } =
            Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: true)
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
                Dc2RandomizeStartWeapon = false;
                Dc2AddAndEquipStartWeapon = false;
                Dc2DylanStartWeaponIndex = 0;
                Dc2ReginaStartWeaponIndex = 0;
                Dc2BlueRaptorCombo = 20;
                Dc2RaptorColourModeIndex = 0;
                foreach (var row in Dc2RaptorTierOptions)
                    row.Weight = Dc2RaptorTiers.DefaultWeights.GetValueOrDefault(row.Type);
            }
            if (!CanSwapDc2PlayerCharacters) { Dc2CharacterSkinIndex = 0; Dc2ReginaSkinIndex = 0; }
            if (!CanRandomizeDoors) RandomizeDoors = false;
            if (!CanShuffleKeyItems) ShuffleKeyItems = false;
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
            OnPropertyChanged(nameof(CanRandomizeStartingInventory));
            OnPropertyChanged(nameof(CanRandomizeVoices));
            OnPropertyChanged(nameof(ShowDc1VoiceCast));
            OnPropertyChanged(nameof(ShowDc2VoiceCast));
            OnPropertyChanged(nameof(CanShuffleBgm));
            OnPropertyChanged(nameof(CanImportBgm));
            OnPropertyChanged(nameof(CanRandomizeBoxes));
            OnPropertyChanged(nameof(CanScramblePuzzleCodes));
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

        [ObservableProperty] private string _progressText;
        [ObservableProperty] private IBrush _progressBrush;
        [ObservableProperty] private string _installStatusText;
        [ObservableProperty] private IBrush _installStatusBrush;

        // --- Busy + button enablement (bound) ----------------------------------

        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private bool _canGenerate;
        [ObservableProperty] private bool _canInstall;
        [ObservableProperty] private bool _canRestore;
        [ObservableProperty] private bool _canPlay;

        // --- Install options (bound) -------------------------------------------

        [ObservableProperty] private bool _shuffleBgm;

        // External BGM import (DC1): overwrite Sound/BGM/ slots with tagged donor tracks from BgmPacksRoot.
        [ObservableProperty] private bool _importBgm;
        [ObservableProperty] private string _bgmPacksRoot = "";

        [ObservableProperty] private bool _scramblePuzzleCodes;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BoxModeEnabled))]
        private bool _randomizeBoxes;

        [ObservableProperty] private int _boxModeIndex;
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

        // --- Seed <-> UI synchronisation (lifted verbatim) ---------------------

        private void UpdateUiFromSeed(bool updateSeedText = true)
        {
            _suspend = true;
            try
            {
                var seedString = _appSeed.ToString();
                if (updateSeedText)
                    SeedText = seedString;
                SeedQrString = seedString;
                SeedBorder = null;   // ClearValue equivalent (NullToUnsetConverter)

                RandomizeItems = _appSeed.Config.RandomizeItems;
                RandomizeEnemies = _appSeed.Config.RandomizeEnemies;
                IncludeDc2SetpieceEnemies = _appSeed.Config.IncludeDc2SetpieceEnemies;
                IncludeDc2BossEnemies = _appSeed.Config.IncludeDc2BossEnemies;
                Dc2EnemyModeIndex = _appSeed.Config.Dc2EnemyMode == Dc2EnemyDistributionMode.Fixed ? 1 : 0;
                SelectedDc2FixedSpecies = Dc2FixedSpeciesOptions
                    .FirstOrDefault(o => o.Type == _appSeed.Config.Dc2FixedSpeciesType)
                    ?? Dc2FixedSpeciesOptions[0];
                var effWeights = Dc2Distribution.EffectiveWeights(_appSeed.Config.Dc2SpeciesWeights);
                foreach (var row in Dc2WeightOptions)
                    row.Weight = effWeights.GetValueOrDefault(row.Type);
                Dc2CharacterSkinIndex = (int)_appSeed.Config.Dc2CharacterSkin;
                Dc2ReginaSkinIndex = (int)_appSeed.Config.Dc2ReginaSkin;
                Dc2RandomizeRaptorTiers = _appSeed.Config.Dc2RandomizeRaptorTiers;
                Dc2ShuffleShop = _appSeed.Config.Dc2ShuffleShop;
                Dc2RandomizeStartWeapon = _appSeed.Config.Dc2RandomizeStartWeapon;
                Dc2AddAndEquipStartWeapon = _appSeed.Config.Dc2AddAndEquipStartWeapon;
                Dc2DylanStartWeaponIndex = _appSeed.Config.Dc2DylanStartWeaponId is { } dId
                    ? Array.IndexOf(Dc2StartingLoadoutPatch.SelectableDylanIds, dId) + 1 : 0;
                Dc2ReginaStartWeaponIndex = _appSeed.Config.Dc2ReginaStartWeaponId is { } rId
                    ? Array.IndexOf(Dc2StartingLoadoutPatch.SelectableReginaIds, rId) + 1 : 0;
                Dc2BlueRaptorCombo = _appSeed.Config.Dc2BlueRaptorComboThreshold;
                Dc2RaptorColourModeIndex = (int)_appSeed.Config.Dc2RaptorColourMode;
                var effTierWeights = Dc2RaptorTiers.EffectiveWeights(_appSeed.Config.Dc2RaptorTierWeights);
                foreach (var row in Dc2RaptorTierOptions)
                    row.Weight = effTierWeights.GetValueOrDefault(row.Type);
                RandomizeDoors = _appSeed.Config.RandomizeDoors;
                ShuffleKeyItems = _appSeed.Config.ShuffleKeyItems;
                RandomizeStartingInventory = _appSeed.Config.RandomizeStartingInventory;
                Difficulty = Math.Round(_appSeed.Config.EnemyDifficulty * 31);

                ReplaceItemPool = _appSeed.Config.ReplaceItemPool;
                Ammo = _appSeed.Config.RatioAmmo;
                Health = _appSeed.Config.RatioHealth;
                int ammoQtyLevel = _appSeed.Config.AmmoQuantity - _appSeed.Config.AmmoReduction;
                AmmoQuantity = Math.Clamp(AmmoQuantityCenter + ammoQtyLevel, 0, AmmoQuantityCenter * 2);
                WeaponUpgrade = Math.Round(_appSeed.Config.WeaponUpgradeChance * 15);
                UpdateRatioVanillaHint();
                PreUpgradedWeapons = _appSeed.Config.PreUpgradedWeaponChance > 0;

                var families = _appSeed.Config.EnabledWeaponFamilies;
                WeaponHandgun = families.HasFlag(WeaponFamily.Handgun);
                WeaponShotgun = families.HasFlag(WeaponFamily.Shotgun);
                WeaponGrenade = families.HasFlag(WeaponFamily.GrenadeGun);

                // ReplacePoolVisible / ItemRatiosVisible / CustomSupply* recompute off the properties above.
                PieDataChanged?.Invoke();
            }
            finally
            {
                _suspend = false;
            }
        }

        /// <summary>The weight-slider rows as a config override map — <c>null</c> when every row sits
        /// on its curated default, so an untouched UI produces a default (block-free) seed.</summary>
        private IReadOnlyDictionary<int, byte> CollectDc2Weights()
        {
            bool allDefault = Dc2WeightOptions.All(r =>
                (byte)Math.Round(r.Weight) == Dc2Distribution.DefaultWeights.GetValueOrDefault(r.Type));
            return allDefault
                ? null
                : Dc2WeightOptions.ToDictionary(r => r.Type, r => (byte)Math.Round(r.Weight));
        }

        /// <summary>Raptor-tier rows as a config override map — <c>null</c> on all-default rows,
        /// same contract as <see cref="CollectDc2Weights"/>.</summary>
        private IReadOnlyDictionary<int, byte> CollectDc2RaptorTierWeights()
        {
            bool allDefault = Dc2RaptorTierOptions.All(r =>
                (byte)Math.Round(r.Weight) == Dc2RaptorTiers.DefaultWeights.GetValueOrDefault(r.Type));
            return allDefault
                ? null
                : Dc2RaptorTierOptions.ToDictionary(r => r.Type, r => (byte)Math.Round(r.Weight));
        }

        private void UpdateSeedFromUi()
        {
            if (_suspend)
                return;

            var voicesOn = IsVoicesChecked == true;
            var voiceDonors = CollectVoiceDonors();

            var config = new RandomizerConfig
            {
                RandomizeItems = RandomizeItems,
                RandomizeEnemies = RandomizeEnemies,
                IncludeDc2SetpieceEnemies = IncludeDc2SetpieceEnemies,
                IncludeDc2BossEnemies = IncludeDc2BossEnemies,
                Dc2AllowWaterLevelEnemySwaps = Dc2AllowWaterLevelEnemySwaps,
                Dc2EnemyMode = Dc2EnemyModeIndex == 1
                    ? Dc2EnemyDistributionMode.Fixed : Dc2EnemyDistributionMode.Weighted,
                Dc2FixedSpeciesType = Dc2EnemyModeIndex == 1 ? SelectedDc2FixedSpecies?.Type : null,
                Dc2SpeciesWeights = CollectDc2Weights(),
                Dc2CharacterSkin = (Dc2CharacterSkin)Dc2CharacterSkinIndex,
                Dc2ReginaSkin = (Dc2CharacterSkin)Dc2ReginaSkinIndex,
                Dc2RandomizeRaptorTiers = Dc2RandomizeRaptorTiers,
                Dc2ShuffleShop = Dc2ShuffleShop,
                Dc2RandomizeStartWeapon = Dc2RandomizeStartWeapon,
                Dc2AddAndEquipStartWeapon = Dc2AddAndEquipStartWeapon,
                Dc2DylanStartWeaponId = Dc2DylanStartWeaponIndex > 0
                    ? Dc2StartingLoadoutPatch.SelectableDylanIds[Dc2DylanStartWeaponIndex - 1] : null,
                Dc2ReginaStartWeaponId = Dc2ReginaStartWeaponIndex > 0
                    ? Dc2StartingLoadoutPatch.SelectableReginaIds[Dc2ReginaStartWeaponIndex - 1] : null,
                Dc2RaptorTierWeights = CollectDc2RaptorTierWeights(),
                Dc2RaptorColourMode = (Dc2RaptorColourMode)Dc2RaptorColourModeIndex,
                Dc2BlueRaptorComboThreshold = (int)Math.Round(Dc2BlueRaptorCombo),
                RandomizeDoors = RandomizeDoors,
                ShuffleKeyItems = ShuffleKeyItems,
                // Scatter (into ammo/health pickups) and DDK disc relocation ride on the key shuffle — no
                // separate GUI toggles; "Shuffle Key Items" turns on all three key-shuffle behaviors.
                // PROGRESSION-KEY-RELOCATION-RESEARCH.md / KEY-ITEM-SCATTER-DATA-AUDIT.md.
                ShuffleKeyItemsIntoPickups = ShuffleKeyItems,
                RelocateDdkDiscs = ShuffleKeyItems,
                RandomizeStartingInventory = RandomizeStartingInventory,
                RandomizeVoices = voicesOn,
                IncludeCrossGameVoices = CrossGameVoices,
                VoicePacksRoot = string.IsNullOrWhiteSpace(VoicePacksRoot) ? null : VoicePacksRoot.Trim(),
                VoiceDonors = voiceDonors.Count > 0 ? voiceDonors : null,
                RandomizeBgm = ImportBgm && CanImportBgm,
                BgmPacksRoot = string.IsNullOrWhiteSpace(BgmPacksRoot) ? null : BgmPacksRoot.Trim(),
                EnemyDifficulty = Difficulty / 31.0,
                ReplaceItemPool = ReplaceItemPool,
                RatioAmmo = (byte)Ammo,
                RatioHealth = (byte)Health,
                AmmoQuantity = (byte)Math.Clamp((int)Math.Round(AmmoQuantity) - AmmoQuantityCenter, 0, AmmoQuantityCenter),
                AmmoReduction = (byte)Math.Clamp(AmmoQuantityCenter - (int)Math.Round(AmmoQuantity), 0, AmmoQuantityCenter),
                WeaponUpgradeChance = WeaponUpgrade / 15.0,
                PreUpgradedWeaponChance = PreUpgradedWeapons ? 0.13 : 0.0,
                EnabledWeaponFamilies =
                    (WeaponHandgun ? WeaponFamily.Handgun : WeaponFamily.None) |
                    (WeaponShotgun ? WeaponFamily.Shotgun : WeaponFamily.None) |
                    (WeaponGrenade ? WeaponFamily.GrenadeGun : WeaponFamily.None),
            };
            // Voice donor settings are shared across games (cross-game datapacks).
            _settings.VoicePacksRoot = config.VoicePacksRoot;
            _settings.BgmPacksRoot = config.BgmPacksRoot;
            _settings.RandomizeCutsceneVoices = config.RandomizeVoices;
            _settings.IncludeCrossGameVoices = config.IncludeCrossGameVoices;
            _settings.VoiceDonors = voiceDonors.Count > 0 ? voiceDonors : null;
            _appSeed = _appSeed.WithConfig(config);
            UpdateUiFromSeed();
        }

        private static AppSeed NormalizeSeed(AppSeed seed)
        {
            var config = seed.Config;
            return config.NormalizeRatios() ? seed.WithConfig(config) : seed;
        }

        private void UpdateRatioVanillaHint()
        {
            int ammo = _appSeed.Config.RatioAmmo, health = _appSeed.Config.RatioHealth;
            if (ammo == health)
            {
                RatioVanillaText = "✓ Vanilla mix — equal bias keeps the game's own ammo/health split.";
                RatioVanillaBrush = Brushes.SeaGreen;
            }
            else
            {
                RatioVanillaText = ammo > health ? "Biased toward ammo." : "Biased toward health.";
                RatioVanillaBrush = Brushes.Gray;
            }
        }

        [RelayCommand]
        private void Randomize()
        {
            _appSeed = _appSeed.WithNewSeed();
            UpdateUiFromSeed();
        }

        [RelayCommand]
        private async Task Copy()
        {
            // Avalonia 12 clipboard is data-format based (the old SetTextAsync was removed).
            try { if (_clipboard() is { } cb) await cb.SetValueAsync(DataFormat.Text, _appSeed.ToString()); }
            catch { /* clipboard can be transiently locked; ignore */ }
        }

        [RelayCommand]
        private async Task Paste()
        {
            try
            {
                var text = _clipboard() is { } cb ? await cb.TryGetTextAsync() : null;
                if (!string.IsNullOrWhiteSpace(text))
                    SeedText = text.Trim();   // change-hook parses it, exactly like the old TextChanged
            }
            catch { }
        }

        // --- Starting-inventory editor -----------------------------------------

        private static readonly (int Id, string Name)[] SupplyItems =
        {
            (0x10, "SG Bullets"), (0x11, "Slag Bullets"), (0x12, "An. Darts S"), (0x13, "An. Darts M"),
            (0x14, "An. Darts L"), (0x15, "Poison Dart"), (0x16, "9mm Parabellum"), (0x17, "40S&W Bullets"),
            (0x18, "Grenade Bullets"), (0x19, "Heat Bullets"), (0x1A, "inf. Grenades"), (0x1B, "Hemostat"),
            (0x1C, "Med. Pak S"), (0x1D, "Med. Pak M"), (0x1E, "Med. Pak L"), (0x1F, "Resuscitation"),
            (0x20, "An. Aid"), (0x21, "Recovery Aid"), (0x22, "Intensifier"), (0x23, "Multiplier"),
        };

        /// <summary>Placeholder shown in a game-specific list (voice donors, supply items, weapons) and the
        /// voice section when the selected game doesn't support that option yet — instead of DC1's cast/items.
        /// Public so the view can bind the voice-section placeholder line.</summary>
        public string GameContentPlaceholder => $"— not available for {SelectedGame.DisplayName} yet —";

        /// <summary>Rebuild the starting-inventory editor lists for the selected game. DC1 lists its real
        /// weapons/items; a game without StartingInventory support (DC2 stub) shows a single placeholder.</summary>
        private void BuildStartingInventoryEditor()
        {
            StartWeapons.Clear();
            SupplyItemsList.Clear();

            if (CanRandomizeStartingInventory)
            {
                StartWeapons.Add(new StartWeaponOption("Vanilla (Handgun)", null));
                foreach (var (id, name) in new[] { (0x05, "Handgun (Glock 34)"), (0x01, "Shotgun"), (0x09, "Grenade Gun") })
                    StartWeapons.Add(new StartWeaponOption(name, id));

                SupplyItemsList.Add(new SupplyOption("(empty)", 0));
                foreach (var (id, name) in SupplyItems)
                    SupplyItemsList.Add(new SupplyOption($"{name} (0x{id:X2})", id));
            }
            else
            {
                StartWeapons.Add(new StartWeaponOption(GameContentPlaceholder, null));
                SupplyItemsList.Add(new SupplyOption(GameContentPlaceholder, 0));
            }

            SelectedStartWeapon = StartWeapons[0];
            SelectedItem0 = SelectedItem1 = SelectedItem2 = SupplyItemsList[0];
        }

        // Returns null when nothing changes the starting inventory (callers use `is { }`).
        private StartingInventoryPlan BuildStartingInventoryPlan(RandomizerConfig config)
        {
            bool setWeapon = false; int? weaponId = null;
            if (SelectedStartWeapon?.WeaponId is int wid)
            {
                setWeapon = true; weaponId = wid; config.StartingWeapons = new[] { wid };
            }
            else
            {
                config.StartingWeapons = null;
            }

            bool randomize = config.RandomizeStartingInventory;
            var custom = new List<(int Id, int Count)>();
            if (!randomize)
                foreach (var (item, count) in new[]
                         {
                             (SelectedItem0, Count0), (SelectedItem1, Count1), (SelectedItem2, Count2),
                         })
                    if (item is { Id: var id } && id != 0)
                        custom.Add((id, int.TryParse(count, out var c) && c >= 1 ? Math.Min(c, 255) : 1));

            if (!setWeapon && custom.Count == 0 && !randomize)
                return null;

            return new StartingInventoryPlan(
                RandomizeSupply: randomize,
                CustomSupply: custom.Count > 0 ? custom : null,
                SetWeapon: setWeapon,
                WeaponId: weaponId);
        }

        // --- Randomize Cutscene Voices -----------------------------------------

        private static readonly string[] VoiceTargets = { "regina", "rick", "gail", "kirk" };

        [RelayCommand]
        private async Task BrowseVoicePacks()
        {
            var path = await _filePicker.PickFolderAsync(new FolderPickerRequest(
                "Select the folder holding the voice donor datapacks",
                SuggestedStartPath: Directory.Exists(VoicePacksRoot) ? VoicePacksRoot : null));
            if (path is not null)
            {
                VoicePacksRoot = path;
                OnVoicePacksRootChanged();
            }
        }

        // Called by the view's LostFocus glue and by the folder picker.
        public void OnVoicePacksRootChanged()
        {
            PopulateVoiceDonors();
            UpdateSeedFromUi();
        }

        [RelayCommand]
        private async Task BrowseBgmPacks()
        {
            var path = await _filePicker.PickFolderAsync(new FolderPickerRequest(
                "Select the folder holding the BGM datapacks (each with data/bgm/<tag>/*.ogg|wav)",
                SuggestedStartPath: Directory.Exists(BgmPacksRoot) ? BgmPacksRoot : null));
            if (path is not null)
                BgmPacksRoot = path;
        }

        private void PopulateVoiceDonors()
        {
            // A game without voice support (DC2 stub) shows a single placeholder instead of DC1's donor
            // cast; the voice-target rows themselves are hidden in the view (bound to CanRandomizeVoices).
            if (!CanRandomizeVoices)
            {
                bool wasSusp = _suspend;
                _suspend = true;
                VoiceDonorOptions.Clear();
                VoiceDonorOptions.Add(new DonorOption(GameContentPlaceholder, null));
                SelectedDonorRegina = SelectedDonorRick = SelectedDonorGail =
                    SelectedDonorKirk = SelectedDonorDylan = SelectedDonorDc2Regina =
                    SelectedDonorDavid = SelectedDonorOldDylan = VoiceDonorOptions[0];
                _suspend = wasSusp;
                SetVoicePacksStatus(0);
                return;
            }

            var allActors = VoiceDataPack.ListActors(VoicePacksRoot).ToList();
            SetVoicePacksStatus(allActors.Count);

            bool crossGame = CrossGameVoices;
            var donors = allActors
                .Where(a => crossGame || string.Equals(a.Game, SelectedGame.Id, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Actor, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Game, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Capture the previous selection (current pick, else the persisted pin) before the rebuild.
            string prevRegina = PreviousDonorTag(SelectedDonorRegina, "regina");
            string prevRick = PreviousDonorTag(SelectedDonorRick, "rick");
            string prevGail = PreviousDonorTag(SelectedDonorGail, "gail");
            string prevKirk = PreviousDonorTag(SelectedDonorKirk, "kirk");
            string prevDylan = PreviousDonorTag(SelectedDonorDylan, "dylan");
            string prevDc2Regina = PreviousDonorTag(SelectedDonorDc2Regina, "regina");
            string prevDavid = PreviousDonorTag(SelectedDonorDavid, "david");
            string prevOldDylan = PreviousDonorTag(SelectedDonorOldDylan, "old-dylan");

            bool wasSuspended = _suspend;
            _suspend = true;
            try
            {
                VoiceDonorOptions.Clear();
                VoiceDonorOptions.Add(new DonorOption("Default (keep own voice)", null));
                VoiceDonorOptions.Add(new DonorOption("Random", RandomDonorTag));
                foreach (var (actor, game) in donors)
                    VoiceDonorOptions.Add(new DonorOption(
                        $"{actor} ({game})", $"{actor}.{game}".ToLowerInvariant()));

                SelectedDonorRegina = SelectDonor(prevRegina);
                SelectedDonorRick = SelectDonor(prevRick);
                SelectedDonorGail = SelectDonor(prevGail);
                SelectedDonorKirk = SelectDonor(prevKirk);
                SelectedDonorDylan = SelectDonor(prevDylan);
                SelectedDonorDc2Regina = SelectDonor(prevDc2Regina);
                SelectedDonorDavid = SelectDonor(prevDavid);
                SelectedDonorOldDylan = SelectDonor(prevOldDylan);
            }
            finally
            {
                _suspend = wasSuspended;
            }
        }

        private string PreviousDonorTag(DonorOption current, string target)
        {
            if (current is not null)
                return current.Tag;
            return _settings.VoiceDonors is { } d && d.TryGetValue(target, out var saved) ? saved : null;
        }

        private DonorOption SelectDonor(string tag)
            => VoiceDonorOptions.FirstOrDefault(
                   o => string.Equals(o.Tag, tag, StringComparison.OrdinalIgnoreCase))
               ?? VoiceDonorOptions[0];

        private void SetVoicePacksStatus(int count)
        {
            if (string.IsNullOrWhiteSpace(VoicePacksRoot))
            {
                VoicePacksValidationText = "";
                return;
            }
            if (count > 0)
            {
                VoicePacksValidationText = $"✓ Found {count} character voice packs.";
                VoicePacksValidationBrush = Brushes.Green;
            }
            else
            {
                VoicePacksValidationText =
                    "No character voice packs (data/voice/<actor>.<game>/) found under this folder.";
                VoicePacksValidationBrush = Brushes.Red;
            }
        }

        private Dictionary<string, string> CollectVoiceDonors()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Only the active game's cast is collected, so a DC1 pin never leaks into a DC2 config.
            var picks = SelectedGame.Id == "dc2"
                ? new[]
                {
                    ("dylan", SelectedDonorDylan), ("regina", SelectedDonorDc2Regina),
                    ("david", SelectedDonorDavid), ("old-dylan", SelectedDonorOldDylan),
                }
                : new[]
                {
                    ("regina", SelectedDonorRegina), ("rick", SelectedDonorRick),
                    ("gail", SelectedDonorGail), ("kirk", SelectedDonorKirk),
                };
            foreach (var (target, pick) in picks)
                if (pick?.Tag is { Length: > 0 } donor)
                    map[target] = donor;
            return map;
        }

        // --- Game / output paths -----------------------------------------------

        [RelayCommand]
        private async Task BrowseGame()
        {
            var initialDir = ResolveGameDir(GamePath);
            var request = BuildExecutablePickerRequest(
                SelectedGame, Directory.Exists(initialDir) ? initialDir : null);
            var path = await _filePicker.PickFileAsync(request);
            if (path is not null)
            {
                GamePath = path;
                ValidateGamePath();
            }
        }

        /// <summary>The "Browse for game executable" picker request for a game — titled and filtered to
        /// that game's <see cref="GameDefinition.ExecutableName"/> (DC1 DINO.exe / DC2 Dino2.exe), so
        /// selecting DC2 asks for Dino2.exe, not DINO.exe. Pure (no picker call) for unit testing.</summary>
        public static FilePickerRequest BuildExecutablePickerRequest(GameDefinition game, string suggestedStartPath)
        {
            var exe = game.ExecutableName;
            return new FilePickerRequest(
                $"Select the {game.DisplayName} executable ({exe})",
                FileTypes: new[]
                {
                    new FilePickerFileFilter($"{game.DisplayName} executable ({exe})", new[] { exe }),
                    new FilePickerFileFilter("Executable files (*.exe)", new[] { "*.exe" }),
                    new FilePickerFileFilter("All files (*.*)", new[] { "*" }),
                },
                SuggestedStartPath: suggestedStartPath);
        }

        private static string ResolveGameDir(string pathOrExe)
        {
            if (string.IsNullOrWhiteSpace(pathOrExe))
                return pathOrExe ?? "";
            try
            {
                if (File.Exists(pathOrExe))
                    return Path.GetDirectoryName(pathOrExe) ?? pathOrExe;
            }
            catch { }
            return pathOrExe;
        }

        // Called by the view's LostFocus glue.
        public void ValidateGamePath()
        {
            var raw = GamePath;
            var folder = ResolveGameDir(raw);
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    GameValidationText = "Game folder / executable not found.";
                    GameValidationBrush = Brushes.Red;
                    _gameDrmProtected = false;
                    RefreshInstallStatus();
                    return;
                }

                var detection = InspectGameExe(raw);
                if (detection.IsProtected)
                {
                    _gameDrmProtected = true;
                    GameValidationText = "⛔ DRM-protected game (Steam/Enigma) — DinoRand cannot modify it. " +
                                         "Use the DRM-free executable (Classic REbirth / GOG).";
                    GameValidationBrush = Brushes.Red;
                    RefreshInstallStatus();
                    return;
                }
                _gameDrmProtected = false;

                var rooms = SelectedGame.EnumerateRooms(folder);
                if (rooms.Count > 0)
                {
                    GameValidationText = "✓ Game files found — ready to randomize.";
                    GameValidationBrush = Brushes.Green;
                }
                else
                {
                    GameValidationText = "No Dino Crisis room files (st*.dat) found under this folder.";
                    GameValidationBrush = Brushes.Red;
                }
            }
            catch (Exception ex)
            {
                GameValidationText = ex.Message;
                GameValidationBrush = Brushes.Red;
            }
            RefreshInstallStatus();
        }

        // --- Generate ----------------------------------------------------------

        [RelayCommand]
        private async Task Generate()
        {
            if (_gameDrmProtected)
            {
                ProgressBrush = Brushes.Red;
                ProgressText = "DRM-protected game (Steam/Enigma) — DinoRand cannot operate on it.";
                return;
            }
            var game = SelectedGame;
            if (!game.IsImplemented)
            {
                ProgressBrush = Brushes.Red;
                ProgressText = $"⛔ {game.DisplayName} is experimental — not yet supported.";
                return;
            }
            CanGenerate = false;
            IsBusy = true;
            ProgressBrush = null;
            ProgressText = "Generating…";
            try
            {
                var gamePath = ResolveGameDir(GamePath);
                var outPath = WorkingModDir;
                var seed = _appSeed.Seed;
                var config = _appSeed.Config;
                // DC2 uses its own runner (cross-species enemy swap) and writes non-destructively to the
                // working mod dir via Dc2OutputDirSink; DC1 uses the shared RandomizerRunner. Both are a
                // pure "generate to a folder" — Install overlays it (docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md).
                var (roomsWritten, writtenDir) = await Task.Run(() =>
                {
                    if (game is DinoCrisis2 dc2)
                    {
                        var r = new Dc2RandomizerRunner(dc2).Run(gamePath, outPath, seed, config);
                        return (r.RoomsWritten, r.OutputDir);
                    }
                    var rr = new RandomizerRunner(game).Run(gamePath, outPath, seed, config);
                    return (rr.RoomsWritten, rr.OutputDir);
                });
                ProgressBrush = Brushes.Green;
                ProgressText = $"✓ Seed generated → {writtenDir} (SPOILER.md: debug info + spoilers)";

                CurrentSlice.GamePath = GamePath;
                CurrentSlice.LastSeed = _appSeed.ToString();
                _settings.Save();
            }
            catch (Exception ex)
            {
                ProgressBrush = Brushes.Red;
                ProgressText = $"Error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                CanGenerate = true;
            }
        }

        // --- Install / restore -------------------------------------------------

        private string CurrentDataDir() => ResolveDataDir(SelectedGame, GamePath);

        /// <summary>Resolve the game's <c>Data\</c> directory for the given install path using the
        /// <b>selected</b> game's resolver (DC1 → <c>english\Data</c>, DC2 → <c>rebirth\Data</c>), so the
        /// backup folder (<c>&lt;dataDir&gt;\.dinorand_backup</c>) and install status are per-game and don't
        /// collide. Pure for unit testing; <c>""</c> when nothing resolves. Was hardcoded to DC1.</summary>
        public static string ResolveDataDir(GameDefinition game, string gamePath)
        {
            try
            {
                var folder = ResolveGameDir(gamePath);
                return string.IsNullOrWhiteSpace(folder) ? "" : game.GetDataDir(folder) ?? "";
            }
            catch { return ""; }
        }

        /// <summary>The game executable that belongs to a resolved <c>Data\</c> dir — its sibling in the
        /// game root (parent of <c>Data\</c>). A DC2 install ships a <c>Dino2.exe</c> in each of
        /// <c>english/</c>, <c>japanese/</c> and <c>rebirth/</c>; this returns the one matching the resolved
        /// data branch so the DRM check inspects the same build the installer would touch (not an arbitrary
        /// recursive hit). <c>null</c> when no such exe exists. Pure for unit testing.</summary>
        public static string LocateExeForDataDir(string dataDir, string exeName)
        {
            if (string.IsNullOrWhiteSpace(dataDir))
                return null;
            var gameRoot = Path.GetDirectoryName(
                dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(gameRoot))
                return null;
            var candidate = Path.Combine(gameRoot, exeName);
            return File.Exists(candidate) ? candidate : null;
        }

        /// <summary>The exe "Play" launches: the SELECTED game's executable, preferring the build beside the
        /// resolved Data dir (so with DC2's english/japanese/rebirth copies present we launch the same build
        /// the installer targets), falling back to a recursive search.</summary>
        private string? ResolvePlayExe() =>
            LocateExeForDataDir(CurrentDataDir(), SelectedGame.ExecutableName)
            ?? GameInstaller.FindGameExe(GamePath, SelectedGame.ExecutableName);

        private ExeProtectionResult InspectGameExe(string gamePathOrExe)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(gamePathOrExe))
                    return ExeProtectionResult.Clean;

                // Inspect the SELECTED game's executable (DC1 DINO.exe / DC2 Dino2.exe), so a DRM-wrapped
                // (Enigma) DC2 Dino2.exe is detected too. ExeProtection.Inspect(path) names the file itself.
                var exeName = SelectedGame.ExecutableName;

                if (File.Exists(gamePathOrExe) &&
                    string.Equals(Path.GetFileName(gamePathOrExe), exeName, StringComparison.OrdinalIgnoreCase))
                    return ExeProtection.Inspect(gamePathOrExe);

                // Prefer the exe beside the resolved Data dir, so with multiple build/language copies
                // present (DC2's english/japanese/rebirth) we inspect the build matching the data branch.
                var aligned = LocateExeForDataDir(ResolveDataDir(SelectedGame, gamePathOrExe), exeName);
                if (aligned is not null)
                    return ExeProtection.Inspect(aligned);

                // Fallback: first matching exe anywhere under the folder (e.g. an unusual layout).
                var folder = ResolveGameDir(gamePathOrExe);
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    return ExeProtectionResult.Clean;
                var exePath = Directory
                    .EnumerateFiles(folder, exeName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                return exePath is null ? ExeProtectionResult.Clean : ExeProtection.Inspect(exePath);
            }
            catch { return ExeProtectionResult.Clean; }
        }

        private void UpdateInstallButtons()
        {
            var dataDir = CurrentDataDir();
            CanInstall = !_gameDrmProtected && dataDir.Length > 0;
            CanRestore = !_gameDrmProtected && dataDir.Length > 0 && GameInstaller.IsInstalled(dataDir);
            CanGenerate = !_gameDrmProtected;
            CanPlay = !_gameDrmProtected && ResolvePlayExe() is not null;
        }

        private void RefreshInstallStatus()
        {
            UpdateInstallButtons();
            var dataDir = CurrentDataDir();
            if (dataDir.Length == 0)
            {
                InstallStatusText = "";
            }
            else if (GameInstaller.IsInstalled(dataDir))
            {
                var manifest = GameInstaller.ReadManifest(dataDir);
                InstallStatusBrush = Brushes.Green;
                InstallStatusText = manifest?.Seed is { } s
                    ? $"Installed: seed {s}"
                    : "Installed (originals backed up)";
            }
            else if (GameInstaller.HasBackup(dataDir))
            {
                InstallStatusBrush = null;
                InstallStatusText = "Not installed — originals are backed up and kept for reuse.";
            }
            else
            {
                InstallStatusBrush = null;
                InstallStatusText = "Not installed.";
            }
        }

        [RelayCommand]
        private async Task Install()
        {
            if (_gameDrmProtected)
            {
                InstallStatusBrush = Brushes.Red;
                InstallStatusText = "DRM-protected game (Steam/Enigma) — DinoRand cannot install to it.";
                return;
            }
            var game = SelectedGame;
            if (!game.IsImplemented)
            {
                InstallStatusBrush = Brushes.Red;
                InstallStatusText = $"⛔ {game.DisplayName} is experimental — not yet supported.";
                return;
            }
            var dataDir = CurrentDataDir();
            if (dataDir.Length == 0)
            {
                InstallStatusBrush = Brushes.Red;
                InstallStatusText = "Set a valid game folder first.";
                return;
            }

            bool firstInstall = !GameInstaller.HasBackup(dataDir);
            var confirm = await _dialogs.ConfirmAsync(
                "Install to Game",
                "DinoRand will overlay the randomized rooms onto the game's Data folder"
                + (firstInstall
                    ? ", backing up your original files first to a backup folder that is kept for reuse."
                    : " (your original files are already backed up and will be reused).")
                + "\n\nThis is reversible at any time with “Restore Originals”. Close the game first if it's running."
                + "\n\nProceed with install?");
            if (!confirm)
                return;

            CanInstall = false;
            CanRestore = false;
            IsBusy = true;
            InstallStatusBrush = null;
            InstallStatusText = "Generating and installing…";
            try
            {
                var gamePath = ResolveGameDir(GamePath);
                var outPath = WorkingModDir;
                var seed = _appSeed.Seed;
                var config = _appSeed.Config;

                // DC2: route to its runner (non-destructive → outPath), overlay via GameInstaller (the same
                // .dinorand_backup contract as DC1, reversed by Restore), then apply the ddraw MotionTrail
                // wrapper fix in place (best-effort). DC2 has no DINO.exe, so the DC1 BGM/box/inventory EXE
                // patches below never apply — this branch returns before them. docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md.
                if (game is DinoCrisis2 dc2)
                {
                    var (ir2, trailNote) = await Task.Run(() =>
                    {
                        var runRes = new Dc2RandomizerRunner(dc2).Run(gamePath, outPath, seed, config);
                        // Scope the overlay to THIS run's output so a stale/foreign *.dat left in the
                        // reused mod dir is never installed (docs/decisions/dc2/install/DC2-INSTALL-INTEGRITY-PLAN.md).
                        var res = GameInstaller.Install(dataDir, outPath, seed.ToString(), runRes.WrittenFiles);
                        string tn = "";
                        if (config.FixDc2MotionTrail)
                            try { tn = $" {Dc2MotionTrailInstaller.Apply(gamePath)}"; }
                            catch (Exception tex) { tn = $" (motion-trail fix skipped: {tex.Message})"; }
                        // Character skin: the WP graft files are in the overlay; the exe gate patch
                        // makes them load (docs/reference/dc2/models/DC2-EXTRA-CRISIS-ROSTER-DECODE.md §9).
                        if (config.Dc2CharacterSkin != Dc2CharacterSkin.Stock
                            || config.Dc2ReginaSkin != Dc2CharacterSkin.Stock)
                        {
                            try { tn += $" skin-gate:{Dc2CharacterSkinInstaller.Apply(gamePath)}"; }
                            catch (Exception sex) { tn += $" (skin gate patch skipped: {sex.Message})"; }
                            // Classic REbirth installs play SFX from CR\data\<pkg>\snd.wbk, not the
                            // CORE SOUND bank — swap those too so the voice follows the skin.
                            try
                            {
                                tn += $" voice:{Dc2CharacterSkinInstaller.ApplyCrWavebanks(gamePath,
                                    Dc2PlayerModelSwap.ResolveSkin(config.Dc2CharacterSkin, seed),
                                    Dc2PlayerModelSwap.ResolveSkin(config.Dc2ReginaSkin, seed, "dc2-regina-skin"))}";
                            }
                            catch (Exception vex) { tn += $" (CR voice swap skipped: {vex.Message})"; }
                        }
                        // Raptor tiers: room-file edits are in the overlay; the wave pair table +
                        // blue-raptor combo threshold are exe patches (docs/reference/dc2/enemies/RAPTOR-TIER-RE.md §4).
                        if (config.Dc2RandomizeRaptorTiers
                            || config.Dc2BlueRaptorComboThreshold != DinoRand.FileFormats.Exe.Dc2RaptorPatch.VanillaComboThreshold)
                        {
                            try { tn += $" raptor-tiers:{Dc2RaptorTierInstaller.Apply(gamePath, seed, config)}"; }
                            catch (Exception rex) { tn += $" (raptor tier exe patch skipped: {rex.Message})"; }
                        }
                        // Killable injected T-Rex: auto-applied whenever the run can spawn a T-Rex
                        // (boss enemies / fixed-T-Rex pin), so a randomized T-Rex dies normally while the
                        // vanilla boss rooms ST200/ST903 stay untouched (docs/decisions/dc2/enemies/DC2-TREX-KILLABLE-LEVER-PLAN.md).
                        if (Dc2TrexKillableInstaller.WantedFor(config))
                        {
                            try { tn += $" trex-killable:{Dc2TrexKillableInstaller.Apply(gamePath, restore: false)}"; }
                            catch (Exception tex) { tn += $" (killable-T-Rex exe patch skipped: {tex.Message})"; }
                        }
                        // In-bounds Mosasaurus grab: auto-applied whenever the run can inject an E80
                        // (water-level swaps / fixed-mosa pin), so its grab no longer launches the player
                        // out of bounds in land rooms while ST700/702/703/704 stay untouched
                        // (docs/decisions/dc2/enemies/DC2-MOSA-GRAB-SUPPRESS-PLAN.md).
                        if (Dc2MosaGrabSuppressInstaller.WantedFor(config))
                        {
                            try { tn += $" mosa-no-grab:{Dc2MosaGrabSuppressInstaller.Apply(gamePath, restore: false)}"; }
                            catch (Exception mex) { tn += $" (mosa-no-grab exe patch skipped: {mex.Message})"; }
                        }
                        // Killable injected Triceratops: auto-applied whenever the run can inject an E70
                        // (setpiece enemies / fixed-Triceratops pin), remapping its out-of-range death
                        // animation index so it dies instead of crashing (RCA §7b).
                        if (Dc2TriceratopsKillableInstaller.WantedFor(config))
                        {
                            try { tn += $" triceratops-killable:{Dc2TriceratopsKillableInstaller.Apply(gamePath, restore: false)}"; }
                            catch (Exception cex) { tn += $" (killable-Triceratops exe patch skipped: {cex.Message})"; }
                        }
                        // Inostra spawn guard: a NULL-cursor guard on the shared PSX-recompiled emergence
                        // emitter's tick driver. Auto-applied for ANY cross-species run — byte-identical
                        // when the emitter is armed, so it only ever no-ops the un-armed crash path an
                        // injected donor can trigger (docs/decisions/dc2/crash-rcas/DC2-INOSTRA-SPAWN-DESCRIPTOR-NULL-RCA.md).
                        if (Dc2InostraSpawnGuardInstaller.WantedFor(config))
                        {
                            try { tn += $" inostra-spawn-guard:{Dc2InostraSpawnGuardInstaller.Apply(gamePath, restore: false)}"; }
                            catch (Exception iex) { tn += $" (inostra-spawn-guard exe patch skipped: {iex.Message})"; }
                        }
                        // Shop shuffle: prices + stock-unlock masks inside Dino2.exe (shared .bak
                        // contract, so the Restore button's full-exe restore reverts it too).
                        if (config.Dc2ShuffleShop)
                        {
                            try { tn += $" shop:{Dc2ShopShuffleInstaller.Apply(gamePath, seed.Value)}"; }
                            catch (Exception shex) { tn += $" (shop shuffle skipped: {shex.Message})"; }
                        }
                        // Starting main weapon: bootstrap equip-immediate exe patch (shared .bak
                        // contract, so the Restore button's full-exe restore reverts it too).
                        if (config.Dc2RandomizeStartWeapon)
                        {
                            try
                            {
                                tn += $" start-weapon:{Dc2StartingLoadoutInstaller.Apply(gamePath, seed.Value,
                                    config.Dc2DylanStartWeaponId, config.Dc2ReginaStartWeaponId,
                                    addAndEquip: config.Dc2AddAndEquipStartWeapon)}";
                            }
                            catch (Exception swex) { tn += $" (start weapon skipped: {swex.Message})"; }
                        }
                        return (res, tn);
                    });
                    InstallStatusBrush = Brushes.Green;
                    InstallStatusText = $"✓ Seed installed correctly.{trailNote} Restore to undo.";
                    CurrentSlice.GamePath = GamePath;
                    CurrentSlice.LastSeed = _appSeed.ToString();
                    _settings.Save();
                    return;
                }

                var shuffleBgm = ShuffleBgm;
                var randomizeBoxes = RandomizeBoxes;
                var boxReroll = BoxModeIndex == 1;
                var scramblePuzzleCodes = ScramblePuzzleCodes;
                var startInvPlan = BuildStartingInventoryPlan(config);
                var (ir, bgmNote, bgmFailed, boxNote, boxFailed) = await Task.Run(() =>
                {
                    new RandomizerRunner(game).Run(gamePath, outPath, seed, config);
                    var res = GameInstaller.Install(dataDir, outPath, seed.ToString());

                    var (bn, bf) = ("", false);
                    if (shuffleBgm)
                        try
                        {
                            GameInstaller.PatchExeShuffleBgm(dataDir, seed.Value, seed.ToString());
                            bn = "music shuffled (heard after the next launch)";
                        }
                        catch (IOException) { bn = "music NOT shuffled — DINO.exe is locked; close the game and re-install"; bf = true; }
                        catch (Exception bex) { bn = $"music NOT shuffled — {bex.Message}"; bf = true; }

                    var (xn, xf) = ("", false);
                    if (randomizeBoxes)
                        try
                        {
                            if (boxReroll) GameInstaller.PatchExeRerollBoxes(dataDir, seed.Value, seed.ToString());
                            else GameInstaller.PatchExeShuffleBoxes(dataDir, seed.Value, seed.ToString());
                            xn = (boxReroll ? "boxes rerolled" : "boxes shuffled") + " (seen after the next launch)";
                        }
                        catch (IOException) { xn = "boxes NOT randomized — DINO.exe is locked; close the game and re-install"; xf = true; }
                        catch (Exception xex) { xn = $"boxes NOT randomized — {xex.Message}"; xf = true; }

                    if (startInvPlan is { } sip)
                        try
                        {
                            GameInstaller.PatchExeStartingInventory(dataDir, sip, seed.Value, seed.ToString());
                            xn = (xn.Length == 0 ? "" : xn + ". ") + "starting inventory patched (seen on the next new game)";
                        }
                        catch (IOException) { xn = (xn.Length == 0 ? "" : xn + ". ") + "starting inventory NOT patched — DINO.exe is locked; close the game and re-install"; xf = true; }
                        catch (Exception iex) { xn = (xn.Length == 0 ? "" : xn + ". ") + $"starting inventory NOT patched — {iex.Message}"; xf = true; }

                    if (scramblePuzzleCodes)
                        try
                        {
                            GameInstaller.PatchExeSyncPuzzleCodes(dataDir, seed.Value, seed.ToString());
                            xn = (xn.Length == 0 ? "" : xn + ". ") + "puzzle codes scrambled (seen after the next launch)";
                        }
                        catch (IOException) { xn = (xn.Length == 0 ? "" : xn + ". ") + "puzzle codes NOT scrambled — DINO.exe is locked; close the game and re-install"; xf = true; }
                        catch (Exception pex) { xn = (xn.Length == 0 ? "" : xn + ". ") + $"puzzle codes NOT scrambled — {pex.Message}"; xf = true; }

                    return (res, bn, bf, xn, xf);
                });
                InstallStatusBrush = (bgmFailed || boxFailed) ? Brushes.OrangeRed : Brushes.Green;
                var exeNote = bgmNote;
                if (boxNote.Length > 0) exeNote = exeNote.Length == 0 ? boxNote : exeNote + ". " + boxNote;
                InstallStatusText =
                    "✓ Seed installed correctly."
                    + (exeNote.Length == 0 ? " Restore to undo." : $" {exeNote}. Restore to undo.");

                CurrentSlice.GamePath = GamePath;
                CurrentSlice.LastSeed = _appSeed.ToString();
                _settings.Save();
            }
            catch (Exception ex)
            {
                InstallStatusBrush = Brushes.Red;
                InstallStatusText = $"Error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                UpdateInstallButtons();
            }
        }

        [RelayCommand]
        private async Task Restore()
        {
            var dataDir = CurrentDataDir();
            if (dataDir.Length == 0)
                return;

            CanInstall = false;
            CanRestore = false;
            InstallStatusBrush = null;
            InstallStatusText = "Restoring…";
            try
            {
                var rr = await Task.Run(() => GameInstaller.Restore(dataDir));
                // DC2: also undo the character-skin WP-gate exe patch when its backup exists
                // (best-effort; the motion-trail ddraw fix is deliberately left in place).
                bool exeRestored = false;
                if (SelectedGame is DinoCrisis2)
                {
                    var gameRoot = Path.GetDirectoryName(Path.GetFullPath(dataDir.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
                    try
                    {
                        exeRestored = await Task.Run(() => Dc2CharacterSkinInstaller.Restore(
                            Path.Combine(gameRoot, Dc2CharacterSkinInstaller.ExeName)));
                    }
                    catch { /* locked exe etc. — room restore already succeeded */ }
                    try { await Task.Run(() => Dc2CharacterSkinInstaller.RestoreCrWavebanks(gameRoot)); }
                    catch { /* CR wavebanks are cosmetic; room restore already succeeded */ }
                }
                InstallStatusBrush = Brushes.Green;
                InstallStatusText = rr.Restored > 0
                    ? $"✓ Restored {rr.Restored} original room files." + (exeRestored ? " Dino2.exe restored." : "")
                    : exeRestored ? "✓ Dino2.exe restored." : "Nothing to restore.";
            }
            catch (Exception ex)
            {
                InstallStatusBrush = Brushes.Red;
                InstallStatusText = $"Error: {ex.Message}";
            }
            finally
            {
                UpdateInstallButtons();
            }
        }

        [RelayCommand]
        private void Play()
        {
            if (_gameDrmProtected)
            {
                InstallStatusBrush = Brushes.Red;
                InstallStatusText = "DRM-protected game (Steam/Enigma) — launch it from its own launcher instead.";
                return;
            }
            var exe = ResolvePlayExe();
            if (exe is null)
            {
                InstallStatusBrush = Brushes.Red;
                InstallStatusText = $"Couldn't find {SelectedGame.ExecutableName} to launch — check the game location.";
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                });
            }
            catch (Exception ex)
            {
                InstallStatusBrush = Brushes.Red;
                InstallStatusText = $"Couldn't launch the game: {ex.Message}";
            }
        }
    }

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
