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
    public sealed partial class MainWindowViewModel : ObservableObject
    {
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
                Dc2RandomizePuzzles = _appSeed.Config.Dc2RandomizePuzzles;
                Dc2CrossCharWeapons = _appSeed.Config.Dc2CrossCharWeapons;
                Dc2RandomizeWeapons = _appSeed.Config.Dc2RandomizeWeapons;
                Dc2SharedWeapons = !Dc2RandomizeWeapons && _appSeed.Config.Dc2SharedWeapons;
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
                ShortenCutscenes = _appSeed.Config.ShortenCutscenes;
                Dc2DoorSkip = _appSeed.Config.Dc2DoorSkip;
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
                Dc2RandomizePuzzles = Dc2RandomizePuzzles,
                Dc2CrossCharWeapons = Dc2CrossCharWeapons,
                Dc2RandomizeWeapons = Dc2RandomizeWeapons,
                Dc2SharedWeapons = !Dc2RandomizeWeapons && Dc2SharedWeapons,
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
                AvoidHiddenPickupSpots = AvoidHiddenPickupSpots,
                // Session-only (not seed-encoded): the cutscene shorten lever (DC1, experimental) and the
                // REbirth DoorSkip ini passthrough (DC2). Both are lost on seed paste by design.
                ShortenCutscenes = ShortenCutscenes && CanShortenCutscenes,
                Dc2DoorSkip = Dc2DoorSkip,
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
            // Derive the no-toggle flags (NormalizePickupVisuals) from the options just built.
            ApplyDerivedFlags(config);
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
            bool changed = config.NormalizeRatios();
            changed |= ApplyDerivedFlags(config);
            return changed ? seed.WithConfig(config) : seed;
        }

        /// <summary>Apply the config flags the GUI DERIVES rather than exposes as their own control, so
        /// <see cref="CurrentConfig"/> is consistent no matter how the config was set (option toggle or a
        /// pasted share-seed). Returns whether anything changed.
        ///
        /// <para><b>NormalizePickupVisuals</b> (Lever A, PICKUP-GROUND-MODEL-FEASIBILITY.md): on whenever ANY
        /// item randomization runs — normal item pickups (<see cref="RandomizerConfig.RandomizeItems"/>) OR
        /// key items (<see cref="RandomizerConfig.ShuffleKeyItems"/>) — so a relocated pickup never renders
        /// as the wrong or an invisible item. It is not seed-encoded, so it must be re-derived here (a pasted
        /// seed carries the two source flags, not this one).</para></summary>
        private static bool ApplyDerivedFlags(RandomizerConfig config)
        {
            bool normalize = config.RandomizeItems || config.ShuffleKeyItems;
            if (config.NormalizePickupVisuals == normalize) return false;
            config.NormalizePickupVisuals = normalize;
            return true;
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

    }
}
