using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using Microsoft.Win32;
using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Install;
using DinoRand.Randomizer.Voice;

namespace DinoRand.App
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml. Keeps a single <see cref="AppSeed"/> as the
    /// source of truth: the seed text, the QR code, and the option controls are always
    /// projections of it. Editing options rebuilds the seed; pasting a seed rebuilds the
    /// options. <see cref="_suspendEvents"/> breaks the feedback loop during programmatic
    /// updates.
    /// </summary>
    public partial class MainWindow : Window
    {
        // Default game location: the English version of the install. Used until the user
        // browses elsewhere (their choice is then remembered in settings.json).
        private const string DefaultGamePath = @"C:\Games\dinorand\english";

        // Fixed per-user staging folder for generated room files. The game has no mod-load path, so the
        // output is only ever consumed by "Install to Game" (which overlays it into the real Data\ folder).
        // There's therefore nothing for the user to pick — we stage here and surface the path on success.
        private static string WorkingModDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DinoRand", "mod_dinorand");

        private AppSeed _appSeed = AppSeed.Random();
        private AppSettings _settings = AppSettings.Load();
        private bool _suspendEvents;

        // Set by ValidateGamePath: true when the selected game's DINO.exe is wrapped by a DRM protector
        // (e.g. The Enigma Protector on the Steam build). DinoRand refuses to operate on a protected game —
        // for TOS compliance and because the patch offsets are invalid against an encrypted image.
        private bool _gameDrmProtected;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            txtGamePath.Text = _settings.GamePath ?? DefaultGamePath;

            // Voice controls are App-setting driven (not in the seed): initialise them from settings.
            txtVoicePacksRoot.Text = _settings.VoicePacksRoot ?? "";
            grpVoices.IsChecked = _settings.RandomizeCutsceneVoices;
            chkCrossGameVoices.IsChecked = _settings.IncludeCrossGameVoices;

            if (_settings.LastSeed is { } last && AppSeed.TryParse(last, out var parsed))
                _appSeed = NormalizeSeed(parsed);
            else
                _appSeed = AppSeed.Random();

            UpdateUiFromSeed();
            PopulateVoiceDonors();              // reads the packs folder (names only); selects saved donors
            PopulateStartingInventoryEditor();
            ValidateGamePath();
            RefreshInstallStatus();
        }

        // --- Starting-inventory editor (EXPERIMENTAL) --------------------------

        /// <summary>Supply items (ammo + health, ids 0x10..0x23) offered in the custom-slot dropdowns.</summary>
        private static readonly (int Id, string Name)[] SupplyItems =
        {
            (0x10, "SG Bullets"), (0x11, "Slag Bullets"), (0x12, "An. Darts S"), (0x13, "An. Darts M"),
            (0x14, "An. Darts L"), (0x15, "Poison Dart"), (0x16, "9mm Parabellum"), (0x17, "40S&W Bullets"),
            (0x18, "Grenade Bullets"), (0x19, "Heat Bullets"), (0x1A, "inf. Grenades"), (0x1B, "Hemostat"),
            (0x1C, "Med. Pak S"), (0x1D, "Med. Pak M"), (0x1E, "Med. Pak L"), (0x1F, "Resuscitation"),
            (0x20, "An. Aid"), (0x21, "Recovery Aid"), (0x22, "Intensifier"), (0x23, "Multiplier"),
        };

        /// <summary>Starting-weapon choices: Tag is null (vanilla), "none", or the weapon id (int).</summary>
        private void PopulateStartingInventoryEditor()
        {
            cboStartWeapon.Items.Clear();
            cboStartWeapon.Items.Add(new ComboBoxItem { Content = "Vanilla (Handgun)", Tag = null });
            // No "None" option: a weaponless start isn't deliverable yet (the engine re-equips a default
            // Handgun via an undecoded path), so we only offer Vanilla or a specific weapon. STARTING-INVENTORY.md.
            foreach (var (id, name) in new[] { (0x05, "Handgun (Glock 34)"), (0x01, "Shotgun"), (0x09, "Grenade Gun") })
                cboStartWeapon.Items.Add(new ComboBoxItem { Content = name, Tag = id });
            cboStartWeapon.SelectedIndex = 0;

            foreach (var cbo in new[] { cboItem0, cboItem1, cboItem2 })
            {
                cbo.Items.Clear();
                cbo.Items.Add(new ComboBoxItem { Content = "(empty)", Tag = 0 });
                foreach (var (id, name) in SupplyItems)
                    cbo.Items.Add(new ComboBoxItem { Content = $"{name} (0x{id:X2})", Tag = id });
                cbo.SelectedIndex = 0;
            }
        }

        /// <summary>Build the combined starting-inventory patch plan from the editor + the Randomize toggle,
        /// reflecting the weapon choice into <paramref name="config"/> so the item pass places a removed
        /// weapon. Returns null when nothing changes the starting inventory.</summary>
        private StartingInventoryPlan BuildStartingInventoryPlan(RandomizerConfig config)
        {
            // Weapon: Vanilla (Tag null) leaves the grant untouched and clears any prior override; a weapon
            // id (int Tag) sets it. (No "None" — a weaponless start isn't supported yet.)
            bool setWeapon = false; int? weaponId = null;
            if ((cboStartWeapon.SelectedItem as ComboBoxItem)?.Tag is int wid)
            {
                setWeapon = true; weaponId = wid; config.StartingWeapons = new[] { wid };
            }
            else
            {
                config.StartingWeapons = null; // Vanilla — reset any override so the item pass uses the default
            }

            // Supply kit: when Randomize is on it owns the supply slots (the manual editor is disabled), so
            // ignore the rows; otherwise each non-empty row → (id, count) sets the kit explicitly.
            bool randomize = config.RandomizeStartingInventory;
            var custom = new List<(int Id, int Count)>();
            if (!randomize)
                foreach (var (cbo, txt) in new[] { (cboItem0, txtCount0), (cboItem1, txtCount1), (cboItem2, txtCount2) })
                    if ((cbo.SelectedItem as ComboBoxItem)?.Tag is int id && id != 0)
                        custom.Add((id, int.TryParse(txt.Text, out var c) && c >= 1 ? Math.Min(c, 255) : 1));

            if (!setWeapon && custom.Count == 0 && !randomize)
                return null; // nothing to patch

            return new StartingInventoryPlan(
                RandomizeSupply: randomize,
                CustomSupply: custom.Count > 0 ? custom : null,
                SetWeapon: setWeapon,
                WeaponId: weaponId);
        }

        // Midpoint of the ammo-quantity slider (Maximum=2×this). Slider value == this ⇒ vanilla amount;
        // above ⇒ AmmoQuantity (more), below ⇒ AmmoReduction (less). Matches both config bytes' 0..7 range.
        private const int AmmoQuantityCenter = 7;

        // --- Seed <-> UI synchronisation ---------------------------------------

        private void UpdateUiFromSeed(bool updateSeedText = true)
        {
            _suspendEvents = true;
            try
            {
                var seedString = _appSeed.ToString();
                if (updateSeedText)
                    txtSeed.Text = seedString;
                seedQr.SeedString = seedString;
                txtSeed.ClearValue(Control.BorderBrushProperty);

                chkItems.IsChecked = _appSeed.Config.RandomizeItems;
                chkEnemies.IsChecked = _appSeed.Config.RandomizeEnemies;
                chkDoors.IsChecked = _appSeed.Config.RandomizeDoors;
                chkShuffleKeys.IsChecked = _appSeed.Config.ShuffleKeyItems;
                chkInventory.IsChecked = _appSeed.Config.RandomizeStartingInventory;
                // Voice controls are App-setting/user driven (not encoded in the shared seed), so they are
                // initialised once in Window_Loaded and not reflected here — reflecting a pasted seed must
                // not wipe the packs folder or donor picks.
                sliderDifficulty.Value = Math.Round(_appSeed.Config.EnemyDifficulty * 31);

                // "Randomize starting inventory" rolls the supply kit, so the manual supply-slot editor is
                // disabled while it's on (they both target the supply kit). The weapon dropdown stays active —
                // randomizing the supply never touches the weapon grant, so e.g. "random supply + start with
                // Shotgun" or "random supply + no weapon" are valid combinations.
                if (pnlCustomSupply is not null)
                {
                    bool randomizeInv = _appSeed.Config.RandomizeStartingInventory;
                    pnlCustomSupply.IsEnabled = !randomizeInv;
                    lblCustomSupply.Text = randomizeInv
                        ? "Custom supply slots (disabled while 'Randomize starting inventory' is on)"
                        : "Custom supply slots (optional — sets the supply kit explicitly):";
                }

                chkReplacePool.IsChecked = _appSeed.Config.ReplaceItemPool;
                sliderAmmo.Value = _appSeed.Config.RatioAmmo;
                sliderHealth.Value = _appSeed.Config.RatioHealth;
                // Ammo-quantity is a signed dial centred on vanilla: slider 0..14, midpoint 7 = vanilla.
                // level = AmmoQuantity (more) − AmmoReduction (less); the two config bytes are mutually
                // exclusive, so exactly one is non-zero off-centre.
                int ammoQtyLevel = _appSeed.Config.AmmoQuantity - _appSeed.Config.AmmoReduction;
                sliderAmmoQuantity.Value = Math.Clamp(AmmoQuantityCenter + ammoQtyLevel, 0, AmmoQuantityCenter * 2);
                sliderWeaponUpgrade.Value = Math.Round(_appSeed.Config.WeaponUpgradeChance * 15);
                UpdateRatioVanillaHint();
                chkPreUpgradedWeapons.IsChecked = _appSeed.Config.PreUpgradedWeaponChance > 0;

                var families = _appSeed.Config.EnabledWeaponFamilies;
                chkWeaponHandgun.IsChecked = families.HasFlag(WeaponFamily.Handgun);
                chkWeaponShotgun.IsChecked = families.HasFlag(WeaponFamily.Shotgun);
                chkWeaponGrenade.IsChecked = families.HasFlag(WeaponFamily.GrenadeGun);

                // The replace toggle is offered whenever items are randomized; the distribution panel
                // (ratios + ammo quantity) only applies to replace mode.
                chkReplacePool.Visibility =
                    _appSeed.Config.RandomizeItems ? Visibility.Visible : Visibility.Collapsed;
                pnlItemRatios.Visibility =
                    _appSeed.Config.RandomizeItems && _appSeed.Config.ReplaceItemPool
                        ? Visibility.Visible : Visibility.Collapsed;
                UpdateItemPie();
            }
            finally
            {
                _suspendEvents = false;
            }
        }

        private void UpdateSeedFromUi()
        {
            if (_suspendEvents)
                return;

            // Phase 4 voice swap (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §13) — installed with the seed. The master
            // group enables it; the per-character dropdowns pin donors (empty map ⇒ all random). These
            // fields are not seed-encoded, so they are persisted in AppSettings instead (below).
            var voicesOn = grpVoices.IsChecked == true;
            var voiceDonors = CollectVoiceDonors();

            var config = new RandomizerConfig
            {
                RandomizeItems = chkItems.IsChecked == true,
                RandomizeEnemies = chkEnemies.IsChecked == true,
                RandomizeDoors = chkDoors.IsChecked == true,
                ShuffleKeyItems = chkShuffleKeys.IsChecked == true,
                RandomizeStartingInventory = chkInventory.IsChecked == true,
                RandomizeVoices = voicesOn,
                IncludeCrossGameVoices = chkCrossGameVoices.IsChecked == true,
                VoicePacksRoot = string.IsNullOrWhiteSpace(txtVoicePacksRoot.Text) ? null : txtVoicePacksRoot.Text.Trim(),
                VoiceDonors = voiceDonors.Count > 0 ? voiceDonors : null,
                EnemyDifficulty = sliderDifficulty.Value / 31.0,
                ReplaceItemPool = chkReplacePool.IsChecked == true,
                RatioAmmo = (byte)sliderAmmo.Value,
                RatioHealth = (byte)sliderHealth.Value,
                // Centred dial → split the signed level back into the two mutually-exclusive config bytes
                // (positive = AmmoQuantity/more, negative = AmmoReduction/less, midpoint = vanilla = both 0).
                AmmoQuantity = (byte)Math.Clamp((int)Math.Round(sliderAmmoQuantity.Value) - AmmoQuantityCenter, 0, AmmoQuantityCenter),
                AmmoReduction = (byte)Math.Clamp(AmmoQuantityCenter - (int)Math.Round(sliderAmmoQuantity.Value), 0, AmmoQuantityCenter),
                WeaponUpgradeChance = sliderWeaponUpgrade.Value / 15.0,
                // Experimental/unvalidated: checked ⇒ a small fixed chance (0.13 ≈ nibble 2),
                // unchecked ⇒ 0.0. Off by default. docs/decisions/cross/ITEM-RANDO-PLAN.md §7.3.
                PreUpgradedWeaponChance = chkPreUpgradedWeapons.IsChecked == true ? 0.13 : 0.0,
                // Per-family weapon toggles (§7.4): OR in each checked family. All checked = All = the
                // byte-identical default.
                EnabledWeaponFamilies =
                    (chkWeaponHandgun.IsChecked == true ? WeaponFamily.Handgun : WeaponFamily.None) |
                    (chkWeaponShotgun.IsChecked == true ? WeaponFamily.Shotgun : WeaponFamily.None) |
                    (chkWeaponGrenade.IsChecked == true ? WeaponFamily.GrenadeGun : WeaponFamily.None),
            };
            // Persist the voice section's settings (not seed-encoded) so they survive a restart.
            _settings.VoicePacksRoot = config.VoicePacksRoot;
            _settings.RandomizeCutsceneVoices = config.RandomizeVoices;
            _settings.IncludeCrossGameVoices = config.IncludeCrossGameVoices;
            _settings.VoiceDonors = voiceDonors.Count > 0 ? voiceDonors : null;
            _appSeed = _appSeed.WithConfig(config);
            UpdateUiFromSeed();
        }

        private void Option_Changed(object sender, RoutedEventArgs e) => UpdateSeedFromUi();

        private void sliderDifficulty_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => UpdateSeedFromUi();

        private void sliderItemRatio_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suspendEvents)
                return;
            // Coerce: Ammo and Health can't both be 0 (docs/decisions/cross/ITEM-RATIO-ZERO-PLAN.md). If a drag would zero
            // both, bump the OTHER slider to 1 so 0/0 is simply unreachable from the UI.
            if (sliderAmmo.Value == 0 && sliderHealth.Value == 0)
            {
                _suspendEvents = true;
                try
                {
                    if (ReferenceEquals(sender, sliderHealth)) sliderAmmo.Value = 1;
                    else sliderHealth.Value = 1;
                }
                finally { _suspendEvents = false; }
            }
            UpdateSeedFromUi();
        }

        /// <summary>Adopt a parsed seed for editing, migrating a legacy 0/0 ratio to the canonical
        /// 16/16 (bit-identical run, now a valid authored config) so the editable UI never rests on the
        /// 0/0 fallback. <c>AppSeed.TryParse</c> still decodes legacy seeds to 0/0 (locked).
        /// docs/decisions/cross/ITEM-RATIO-ZERO-PLAN.md.</summary>
        private static AppSeed NormalizeSeed(AppSeed seed)
        {
            var config = seed.Config;
            return config.NormalizeRatios() ? seed.WithConfig(config) : seed;
        }

        /// <summary>
        /// Repaint the item-distribution pie from the current ratios. Port of the reference
        /// <c>UpdateItemPie</c>, reduced to three DC1 slices (Keys / Ammo / Health). The Keys slice is
        /// a fixed illustrative reserve (1/8); the other two scale with the ratio sliders. Pure
        /// visualization of the config knobs — it does not read back from the randomizer.
        /// </summary>
        private void UpdateItemPie()
        {
            const double keyItems = 1 / 8.0;
            int totalRest = _appSeed.Config.RatioAmmo + _appSeed.Config.RatioHealth;
            if (totalRest == 0)
                totalRest = 1;
            double remaining = (1 - keyItems) / totalRest;

            pieItemRatios.Records.Clear();
            pieItemRatios.Records.Add(new PieChart.Record
            { Name = "Keys", Value = keyItems, Color = Colors.LightBlue });
            pieItemRatios.Records.Add(new PieChart.Record
            { Name = "Ammo", Value = _appSeed.Config.RatioAmmo * remaining, Color = Colors.IndianRed });
            pieItemRatios.Records.Add(new PieChart.Record
            { Name = "Health", Value = _appSeed.Config.RatioHealth * remaining, Color = Colors.MediumSeaGreen });
            pieItemRatios.Update();
        }

        /// <summary>Surface the vanilla semantics of the Ammo/Health pair: equal bias reproduces the
        /// game's own pickup split (the engine's intrinsic weights), so call that out explicitly rather
        /// than letting the user guess. Off-balance, name which way it's tilted.</summary>
        private void UpdateRatioVanillaHint()
        {
            if (lblRatioVanilla is null)
                return;
            int ammo = _appSeed.Config.RatioAmmo, health = _appSeed.Config.RatioHealth;
            if (ammo == health)
            {
                lblRatioVanilla.Text = "✓ Vanilla mix — equal bias keeps the game's own ammo/health split.";
                lblRatioVanilla.Foreground = Brushes.SeaGreen;
            }
            else
            {
                lblRatioVanilla.Text = ammo > health ? "Biased toward ammo." : "Biased toward health.";
                lblRatioVanilla.Foreground = Brushes.Gray;
            }
        }

        private void txtSeed_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suspendEvents)
                return;

            if (AppSeed.TryParse(txtSeed.Text, out var parsed))
            {
                _appSeed = NormalizeSeed(parsed);
                // Don't rewrite the textbox the user is typing in.
                UpdateUiFromSeed(updateSeedText: false);
            }
            else
            {
                txtSeed.BorderBrush = Brushes.Red;
            }
        }

        private void btnRandomize_Click(object sender, RoutedEventArgs e)
        {
            _appSeed = _appSeed.WithNewSeed();
            UpdateUiFromSeed();
        }

        private void btnCopy_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(_appSeed.ToString()); }
            catch { /* clipboard can be transiently locked; ignore */ }
        }

        private void btnPaste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                    txtSeed.Text = text.Trim();
            }
            catch { }
        }

        // --- Game / output paths ----------------------------------------------

        private void btnBrowseGame_Click(object sender, RoutedEventArgs e)
        {
            // Pick the game EXECUTABLE (DINO.exe), the way BioRand's GameLocationBox does: an exe filter
            // pins the exact build (and the right language folder, since each lang dir has its own DINO.exe),
            // which also lets the DRM check inspect that exact file. The install folder is derived from it
            // (ResolveGameDir) for the room/Data logic, so picking the exe and picking the folder are
            // interchangeable downstream.
            var dialog = new OpenFileDialog
            {
                Title = "Select the Dino Crisis executable (DINO.exe)",
                Filter = $"Dino Crisis executable ({GameInstaller.ExeName})|{GameInstaller.ExeName}" +
                         "|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                FileName = GameInstaller.ExeName,
                CheckFileExists = false,
                CheckPathExists = false,
            };
            var initialDir = ResolveGameDir(txtGamePath.Text);
            if (Directory.Exists(initialDir))
                dialog.InitialDirectory = initialDir;
            if (dialog.ShowDialog(this) == true)
            {
                txtGamePath.Text = dialog.FileName;
                ValidateGamePath();
            }
        }

        // --- Randomize Cutscene Voices: packs folder + per-character donor dropdowns (docs §13/UI §10) ---

        // The combo Tag value for the "Random" item (shuffle this character). "Default (keep own voice)"
        // uses a null Tag — no config entry — so the character is left vanilla.
        private const string RandomDonorTag = "random";

        // The swappable cast and their donor dropdowns (target name ⇒ combo). Mirrors
        // VoiceSwapPlanner.SwappableCast.
        private (string Target, ComboBox Combo)[] VoiceDonorCombos() => new[]
        {
            ("regina", cboDonorRegina),
            ("rick", cboDonorRick),
            ("gail", cboDonorGail),
            ("kirk", cboDonorKirk),
        };

        private void btnBrowseVoicePacks_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select the folder holding the voice donor datapacks",
            };
            if (Directory.Exists(txtVoicePacksRoot.Text))
                dialog.InitialDirectory = txtVoicePacksRoot.Text;
            if (dialog.ShowDialog(this) == true)
            {
                txtVoicePacksRoot.Text = dialog.FolderName;
                OnVoicePacksRootChanged();
            }
        }

        private void txtVoicePacksRoot_LostFocus(object sender, RoutedEventArgs e) => OnVoicePacksRootChanged();

        // Shared by all four donor dropdowns.
        private void cboVoiceDonor_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSeedFromUi();

        // Master group toggle: children enable/disable as a unit; fold the change into the config.
        private void grpVoices_CheckedChanged(object sender, RoutedEventArgs e) => UpdateSeedFromUi();

        // "Allow voices from other games" decides which donors the dropdowns list, so repopulate on change
        // (decision: filter the dropdowns to the allowed donors — docs/decisions/dc1/voice/VOICE-UI-PLAN.md).
        private void chkCrossGameVoices_Changed(object sender, RoutedEventArgs e)
        {
            if (_suspendEvents) return;
            PopulateVoiceDonors();
            UpdateSeedFromUi();
        }

        private void OnVoicePacksRootChanged()
        {
            PopulateVoiceDonors();
            UpdateSeedFromUi();
        }

        // Fill each character's donor dropdown with the discovered donor actors, "Random" first (the
        // default). With "Allow voices from other games" off, only DC1's own cast is listed (just Regina).
        // Reads only folder/zip entry NAMES (no audio decode), so it stays fast even on a large zip.
        private void PopulateVoiceDonors()
        {
            // Read all discovered packs once (names only — fast even on a large zip): the total drives the
            // "N character voice packs found" status, then we filter to the eligible donors for the dropdowns.
            var allActors = VoiceDataPack.ListActors(txtVoicePacksRoot.Text).ToList();
            SetVoicePacksStatus(allActors.Count);

            bool crossGame = chkCrossGameVoices.IsChecked == true;
            var donors = allActors
                .Where(a => crossGame || string.Equals(a.Game, "dc1", StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Actor, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Game, StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool wasSuspended = _suspendEvents;
            _suspendEvents = true;                       // don't churn the seed while rebuilding items
            try
            {
                foreach (var (target, combo) in VoiceDonorCombos())
                {
                    // Tri-state pick: null/absent ⇒ Default (keep own voice), "random" ⇒ shuffle, else a
                    // donor name. Keep the live selection across a repopulate; first fill uses the setting.
                    string previous = combo.SelectedItem is ComboBoxItem cur
                        ? cur.Tag as string
                        : (_settings.VoiceDonors is { } d && d.TryGetValue(target, out var saved) ? saved : null);

                    combo.Items.Clear();
                    combo.Items.Add(new ComboBoxItem { Content = "Default (keep own voice)", Tag = null });
                    combo.Items.Add(new ComboBoxItem { Content = "Random", Tag = RandomDonorTag });
                    foreach (var (actor, game) in donors)
                        // Tag is the game-specific donor key so picking e.g. "claire (recv)" never mixes in
                        // her re2 / re2r performances (matches VoiceClipSource.Key).
                        combo.Items.Add(new ComboBoxItem
                        {
                            Content = $"{actor} ({game})",
                            Tag = $"{actor}.{game}".ToLowerInvariant(),
                        });

                    SelectComboDonor(combo, previous);
                }
            }
            finally
            {
                _suspendEvents = wasSuspended;
            }
        }

        // Select the item whose donor actor matches (case-insensitive); falls back to "Random" (index 0,
        // Tag null — which also matches a null/"Random" request).
        private static void SelectComboDonor(ComboBox combo, string actor)
        {
            foreach (ComboBoxItem item in combo.Items)
                if (string.Equals(item.Tag as string, actor, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            combo.SelectedIndex = 0;
        }

        // Report how many character voice packs (<actor>.<game> folders, across all packs/zips) were found
        // under the packs root — mirrors the game folder's "✓ Found N room files." feedback.
        private void SetVoicePacksStatus(int count)
        {
            if (string.IsNullOrWhiteSpace(txtVoicePacksRoot.Text))
            {
                txtVoicePacksValidation.Text = "";
                return;
            }
            if (count > 0)
            {
                txtVoicePacksValidation.Text = $"✓ Found {count} character voice packs.";
                txtVoicePacksValidation.Foreground = Brushes.Green;
            }
            else
            {
                txtVoicePacksValidation.Text =
                    "No character voice packs (data/voice/<actor>.<game>/) found under this folder.";
                txtVoicePacksValidation.Foreground = Brushes.Red;
            }
        }

        // The per-character donor pins (target ⇒ donor); "Random" picks are omitted (absent ⇒ random).
        private Dictionary<string, string> CollectVoiceDonors()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (target, combo) in VoiceDonorCombos())
                if ((combo.SelectedItem as ComboBoxItem)?.Tag is string donor && donor.Length > 0)
                    map[target] = donor;
            return map;
        }

        // Normalize the game-location box to a folder: if it points at a file (e.g. the chosen DINO.exe),
        // return its containing directory; otherwise return it unchanged. Lets the user enter EITHER the
        // executable or the install folder while every downstream consumer keeps working with a folder.
        private static string ResolveGameDir(string pathOrExe)
        {
            if (string.IsNullOrWhiteSpace(pathOrExe))
                return pathOrExe ?? "";
            try
            {
                if (File.Exists(pathOrExe))
                    return Path.GetDirectoryName(pathOrExe) ?? pathOrExe;
            }
            catch { /* malformed path — fall through and let validation report it */ }
            return pathOrExe;
        }

        private void txtGamePath_LostFocus(object sender, RoutedEventArgs e) => ValidateGamePath();

        private void ValidateGamePath()
        {
            // The box may hold the chosen DINO.exe or the install folder; resolve to the folder for the
            // room/Data logic while keeping the raw text for the (exe-precise) DRM check.
            var raw = txtGamePath.Text;
            var folder = ResolveGameDir(raw);
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    txtGameValidation.Text = "Game folder / executable not found.";
                    txtGameValidation.Foreground = Brushes.Red;
                    _gameDrmProtected = false;
                    RefreshInstallStatus();
                    return;
                }

                // DRM gate first: a protected (Steam/Enigma) DINO.exe is refused outright, regardless of
                // whether room files are present, so the user sees the TOS reason instead of a misleading "ready".
                var detection = InspectGameExe(raw);
                if (detection.IsProtected)
                {
                    _gameDrmProtected = true;
                    txtGameValidation.Text = "⛔ DRM-protected game (Steam/Enigma) — DinoRand cannot modify it. " +
                                             "Use the DRM-free executable (Classic REbirth / GOG).";
                    txtGameValidation.Foreground = Brushes.Red;
                    RefreshInstallStatus();
                    return;
                }
                _gameDrmProtected = false;

                var rooms = new DinoCrisis1().EnumerateRooms(folder);
                if (rooms.Count > 0)
                {
                    txtGameValidation.Text = $"✓ Found {rooms.Count} room files.";
                    txtGameValidation.Foreground = Brushes.Green;
                }
                else
                {
                    txtGameValidation.Text = "No Dino Crisis room files (st*.dat) found under this folder.";
                    txtGameValidation.Foreground = Brushes.Red;
                }
            }
            catch (Exception ex)
            {
                txtGameValidation.Text = ex.Message;
                txtGameValidation.Foreground = Brushes.Red;
            }
            RefreshInstallStatus();
        }

        // --- Generate ----------------------------------------------------------

        private async void btnGenerate_Click(object sender, RoutedEventArgs e)
        {
            if (_gameDrmProtected)
            {
                lblProgress.Foreground = Brushes.Red;
                lblProgress.Text = "DRM-protected game (Steam/Enigma) — DinoRand cannot operate on it.";
                return;
            }
            btnGenerate.IsEnabled = false;
            genProgress.Visibility = Visibility.Visible;
            lblProgress.Foreground = SystemColors.ControlTextBrush;
            lblProgress.Text = "Generating…";
            try
            {
                var gamePath = ResolveGameDir(txtGamePath.Text);
                var outPath = WorkingModDir;
                var seed = _appSeed.Seed;
                var config = _appSeed.Config;
                var result = await Task.Run(() =>
                    new RandomizerRunner(new DinoCrisis1()).Run(gamePath, outPath, seed, config));
                lblProgress.Foreground = Brushes.Green;
                lblProgress.Text = $"✓ {result.RoomsWritten} rooms written → {result.OutputDir}";

                _settings.GamePath = txtGamePath.Text;
                _settings.LastSeed = _appSeed.ToString();
                _settings.Save();
            }
            catch (Exception ex)
            {
                lblProgress.Foreground = Brushes.Red;
                lblProgress.Text = $"Error: {ex.Message}";
            }
            finally
            {
                genProgress.Visibility = Visibility.Collapsed;
                btnGenerate.IsEnabled = true;
            }
        }

        // --- Install / restore ------------------------------------------------

        // Resolves the game's Data folder from the current game-path box, or "" if it
        // can't be found (used to gate the install/restore buttons and read status).
        private string CurrentDataDir()
        {
            try
            {
                var folder = ResolveGameDir(txtGamePath.Text);
                return string.IsNullOrWhiteSpace(folder) ? "" : new DinoCrisis1().GetDataDir(folder) ?? "";
            }
            catch { return ""; }
        }

        // Classify the game's DINO.exe (DRM-free vs Enigma/packed) from the location box, which may hold the
        // exe itself or the install folder. An explicitly-picked DINO.exe is inspected directly; otherwise the
        // DINO.exe under the resolved folder is used. Returns Clean when none is found (each action surfaces its
        // own "exe not found" path) so this never blocks spuriously.
        private static ExeProtectionResult InspectGameExe(string gamePathOrExe)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(gamePathOrExe))
                    return ExeProtectionResult.Clean;

                // Box points at a file: if it's a DINO.exe, inspect that exact file.
                if (File.Exists(gamePathOrExe) &&
                    string.Equals(Path.GetFileName(gamePathOrExe), GameInstaller.ExeName, StringComparison.OrdinalIgnoreCase))
                    return ExeProtection.Inspect(gamePathOrExe);

                var folder = ResolveGameDir(gamePathOrExe);
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    return ExeProtectionResult.Clean;
                var exePath = Directory
                    .EnumerateFiles(folder, GameInstaller.ExeName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                return exePath is null ? ExeProtectionResult.Clean : ExeProtection.Inspect(exePath);
            }
            catch { return ExeProtectionResult.Clean; }
        }

        // Enable/disable the install & restore buttons for the current game folder.
        // Restore is only offered when a backup exists. Leaves the status text alone so a
        // freshly-shown success message survives a post-action refresh. A DRM-protected game
        // disables every action (see _gameDrmProtected / ValidateGamePath).
        private void UpdateInstallButtons()
        {
            var dataDir = CurrentDataDir();
            btnInstall.IsEnabled = !_gameDrmProtected && dataDir.Length > 0;
            // Restore is only offered when the mod is currently applied (the backup itself is kept forever).
            btnRestore.IsEnabled = !_gameDrmProtected && dataDir.Length > 0 && GameInstaller.IsInstalled(dataDir);
            btnGenerate.IsEnabled = !_gameDrmProtected;
            // Play needs a resolvable DINO.exe and a non-DRM game; it's independent of install state.
            btnPlay.IsEnabled = !_gameDrmProtected && GameInstaller.FindGameExe(txtGamePath.Text) is not null;
        }

        // Buttons + a status line describing the current install state. Used on load and
        // when the game path changes (not after install/restore, which set their own text).
        private void RefreshInstallStatus()
        {
            UpdateInstallButtons();
            var dataDir = CurrentDataDir();
            if (dataDir.Length == 0)
            {
                lblInstallStatus.Text = "";
            }
            else if (GameInstaller.IsInstalled(dataDir))
            {
                var manifest = GameInstaller.ReadManifest(dataDir);
                lblInstallStatus.Foreground = Brushes.Green;
                lblInstallStatus.Text = manifest?.Seed is { } s
                    ? $"Installed: seed {s}"
                    : "Installed (originals backed up)";
            }
            else if (GameInstaller.HasBackup(dataDir))
            {
                // Restored (or never-applied) but the pristine backup is retained for reuse.
                lblInstallStatus.ClearValue(TextBlock.ForegroundProperty);
                lblInstallStatus.Text = "Not installed — originals are backed up and kept for reuse.";
            }
            else
            {
                lblInstallStatus.ClearValue(TextBlock.ForegroundProperty);
                lblInstallStatus.Text = "Not installed.";
            }
        }

        private async void btnInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_gameDrmProtected)
            {
                lblInstallStatus.Foreground = Brushes.Red;
                lblInstallStatus.Text = "DRM-protected game (Steam/Enigma) — DinoRand cannot install to it.";
                return;
            }
            var dataDir = CurrentDataDir();
            if (dataDir.Length == 0)
            {
                lblInstallStatus.Foreground = Brushes.Red;
                lblInstallStatus.Text = "Set a valid game folder first.";
                return;
            }

            // Confirm before writing into the real game folder. Reassure the user the originals are backed
            // up (and kept) and the action is reversible, and remind them to close the game first since the
            // install can patch DINO.exe.
            bool firstInstall = !GameInstaller.HasBackup(dataDir);
            var confirm = MessageBox.Show(this,
                "DinoRand will overlay the randomized rooms onto the game's Data folder"
                + (firstInstall
                    ? ", backing up your original files first to a backup folder that is kept for reuse."
                    : " (your original files are already backed up and will be reused).")
                + "\n\nThis is reversible at any time with “Restore Originals”. Close the game first if it's running."
                + "\n\nProceed with install?",
                "Install to Game", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK)
                return;

            btnInstall.IsEnabled = false;
            btnRestore.IsEnabled = false;
            genProgress.Visibility = Visibility.Visible;
            lblInstallStatus.ClearValue(TextBlock.ForegroundProperty);
            lblInstallStatus.Text = "Generating and installing…";
            try
            {
                var gamePath = ResolveGameDir(txtGamePath.Text);
                var outPath = WorkingModDir;
                var seed = _appSeed.Seed;
                var config = _appSeed.Config;
                var shuffleBgm = chkShuffleBgm.IsChecked == true;
                var randomizeBoxes = chkRandomizeBoxes.IsChecked == true;
                var boxReroll = cboBoxMode.SelectedIndex == 1; // 0 = Shuffle, 1 = Reroll from box pool
                // Read the starting-inventory editor on the UI thread, reflect the weapon choice into `config`
                // (so the item pass can place a removed weapon), and build the combined patch plan.
                var startInvPlan = BuildStartingInventoryPlan(config);
                var (ir, bgmNote, bgmFailed, boxNote, boxFailed) = await Task.Run(() =>
                {
                    // Generate fresh so what we overlay matches the current seed, then overlay.
                    new RandomizerRunner(new DinoCrisis1()).Run(gamePath, outPath, seed, config);
                    var res = GameInstaller.Install(dataDir, outPath, seed.ToString());

                    // Music shuffle + box randomization are additive EXE patches (they compose with the room
                    // overlay and share the same backup/manifest, so Restore reverses them too). The rooms are
                    // already installed, so a locked/failed exe write is reported as a warning, not a failure.
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

                    // Starting inventory (weapon + supply) is one additive EXE patch (shares the backup/manifest,
                    // reversed by Restore), seeded from the run seed; a locked/failed exe write is a warning.
                    if (startInvPlan is { } sip)
                        try
                        {
                            GameInstaller.PatchExeStartingInventory(dataDir, sip, seed.Value, seed.ToString());
                            xn = (xn.Length == 0 ? "" : xn + ". ") + "starting inventory patched (seen on the next new game)";
                        }
                        catch (IOException) { xn = (xn.Length == 0 ? "" : xn + ". ") + "starting inventory NOT patched — DINO.exe is locked; close the game and re-install"; xf = true; }
                        catch (Exception iex) { xn = (xn.Length == 0 ? "" : xn + ". ") + $"starting inventory NOT patched — {iex.Message}"; xf = true; }

                    return (res, bn, bf, xn, xf);
                });
                lblInstallStatus.Foreground = (bgmFailed || boxFailed) ? Brushes.OrangeRed : Brushes.Green;
                var exeNote = bgmNote;
                if (boxNote.Length > 0) exeNote = exeNote.Length == 0 ? boxNote : exeNote + ". " + boxNote;
                lblInstallStatus.Text =
                    $"✓ Installed {ir.Overlaid} rooms ({ir.BackedUp} backed up)."
                    + (exeNote.Length == 0 ? " Restore to undo." : $" {exeNote}. Restore to undo.");

                _settings.GamePath = txtGamePath.Text;
                _settings.LastSeed = _appSeed.ToString();
                _settings.Save();
            }
            catch (Exception ex)
            {
                lblInstallStatus.Foreground = Brushes.Red;
                lblInstallStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                genProgress.Visibility = Visibility.Collapsed;
                UpdateInstallButtons();
            }
        }

        private async void btnRestore_Click(object sender, RoutedEventArgs e)
        {
            var dataDir = CurrentDataDir();
            if (dataDir.Length == 0)
                return;

            btnInstall.IsEnabled = false;
            btnRestore.IsEnabled = false;
            lblInstallStatus.ClearValue(TextBlock.ForegroundProperty);
            lblInstallStatus.Text = "Restoring…";
            try
            {
                var rr = await Task.Run(() => GameInstaller.Restore(dataDir));
                lblInstallStatus.Foreground = Brushes.Green;
                lblInstallStatus.Text = rr.Restored > 0
                    ? $"✓ Restored {rr.Restored} original room files."
                    : "Nothing to restore.";
            }
            catch (Exception ex)
            {
                lblInstallStatus.Foreground = Brushes.Red;
                lblInstallStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                UpdateInstallButtons();
            }
        }

        private void btnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_gameDrmProtected)
            {
                lblInstallStatus.Foreground = Brushes.Red;
                lblInstallStatus.Text = "DRM-protected game (Steam/Enigma) — launch it from its own launcher instead.";
                return;
            }
            var exe = GameInstaller.FindGameExe(txtGamePath.Text);
            if (exe is null)
            {
                lblInstallStatus.Foreground = Brushes.Red;
                lblInstallStatus.Text = $"Couldn't find {GameInstaller.ExeName} to launch — check the game location.";
                return;
            }
            try
            {
                // Launch via the shell so it runs exactly as a double-click would; the working directory
                // must be the exe's folder so the game finds its Data\ with relative paths.
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                });
            }
            catch (Exception ex)
            {
                lblInstallStatus.Foreground = Brushes.Red;
                lblInstallStatus.Text = $"Couldn't launch the game: {ex.Message}";
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                e.Handled = true;
            }
            catch { }
        }
    }
}
