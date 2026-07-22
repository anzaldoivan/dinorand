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


        // --- Install / restore -------------------------------------------------
        // (The standalone "Generate" command was removed — "Install to Game" regenerates via the same
        //  runners and the runner still writes SPOILER.md, so no generate-only path is needed.)

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
            CanPlay = !_gameDrmProtected && ResolvePlayExe() is not null;
            // Never leave a disabled tab on screen (the game folder can be cleared while it shows).
            if (!ApTabEnabled)
                SelectedTabIndex = 0;
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
            // Cancels the GENERATE phase only (S4): it writes to the working mod dir, so aborting
            // touches no game file. The overlay onto Data\ runs to completion by design.
            using var installStop = new CancellationTokenSource();
            _installStop = installStop;
            var ct = installStop.Token;
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
                        var runRes = new Dc2RandomizerRunner(dc2).Run(gamePath, outPath, seed, config, ct: ct);
                        // Scope the overlay to THIS run's output so a stale/foreign *.dat left in the
                        // reused mod dir is never installed (docs/decisions/dc2/install/DC2-INSTALL-INTEGRITY-PLAN.md).
                        var res = GameInstaller.Install(dataDir, outPath, seed.ToString(), runRes.WrittenFiles);
                        string tn = "";
                        if (config.FixDc2MotionTrail)
                            try { tn = $" {Dc2MotionTrailInstaller.Apply(gamePath)}"; }
                            catch (Exception tex) { tn = $" (motion-trail fix skipped: {tex.Message})"; }
                        // REbirth DoorSkip passthrough (K115): one config.ini [DLL] key, best-effort.
                        // Restore does NOT undo it — flip the key back in config.ini / the REbirth launcher.
                        if (config.Dc2DoorSkip)
                        {
                            try
                            {
                                tn += Dc2DoorSkipInstaller.Apply(gamePath)
                                    ? " door-skip:on" : " (door-skip: config.ini not found)";
                            }
                            catch (Exception dex) { tn += $" (door-skip skipped: {dex.Message})"; }
                        }
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
                            try { tn += $" shop:{Dc2ShopShuffleInstaller.Apply(gamePath, seed.Value,
                                shuffleCatalogMasks: !config.Dc2RandomizeWeapons)}"; }
                            catch (Exception shex) { tn += $" (shop shuffle skipped: {shex.Message})"; }
                        }
                        // Cross-character weapons: eight WEP_P grafts built from the user's own Data
                        // files + a Dino2.exe catalog/owner-flag patch (shared .bak contract, so the
                        // Restore button's full-exe restore reverts the exe half too).
                        if (config.Dc2CrossCharWeapons && !config.Dc2RandomizeWeapons)
                        {
                            try { tn += $" cross-char-weapons:{Dc2CrossCharWeaponInstaller.Apply(gamePath)}"; }
                            catch (Exception ccex) { tn += $" (cross-character weapons skipped: {ccex.Message})"; }
                        }
                        // Character-shared SUB weapons: two owner bits in the Dino2.exe item catalog
                        // (shared .bak contract, so the Restore button reverts it too).
                        if (config.Dc2SharedWeapons)
                        {
                            try { tn += $" shared-weapons:{Dc2SharedWeaponInstaller.Apply(gamePath)}"; }
                            catch (Exception swex) { tn += $" (shared weapons skipped: {swex.Message})"; }
                        }
                        // Elevator puzzle-code scramble: candidate imm32 table inside Dino2.exe (shared
                        // .bak contract, so the Restore button's full-exe restore reverts it too).
                        if (config.Dc2ScramblePuzzleCodes)
                        {
                            try { tn += $" puzzle-codes:{Dc2PuzzleCodeScrambleInstaller.Apply(gamePath, seed.Value)}"; }
                            catch (Exception pcex) { tn += $" (puzzle-code scramble skipped: {pcex.Message})"; }
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
                        // Randomized ownership is last: starting-loadout application restores catalog
                        // owner bits to its verified baseline, so the final exact layout must follow it.
                        if (config.Dc2RandomizeWeapons)
                        {
                            try { tn += $" randomized-weapons:{Dc2RandomizedWeaponInstaller.Apply(gamePath, seed)}"; }
                            catch (Exception rwex) { tn += $" (randomized weapons skipped: {rwex.Message})"; }
                        }
                        return (res, tn);
                    });
                    InstallStatusBrush = Brushes.Green;
                    InstallStatusText = $"✓ Seed {_appSeed} installed correctly. Restore to undo.";
                    CurrentSlice.GamePath = GamePath;
                    CurrentSlice.LastSeed = _appSeed.ToString();
                    _settings.Save();
                    return;
                }

                var shuffleBgm = ShuffleBgm;
                var randomizeBoxes = RandomizeBoxes;
                var boxReroll = BoxModeIndex == 1;
                var scramblePuzzleCodes = ScramblePuzzleCodes;
                var dc1DoorSkip = Dc1DoorSkip;
                var dc1FastForwardCutscenes = Dc1FastForwardCutscenes;
                var startInvPlan = BuildStartingInventoryPlan(config);
                var (ir, bgmNote, bgmFailed, boxNote, boxFailed) = await Task.Run(() =>
                {
                    new RandomizerRunner(game).Run(gamePath, outPath, seed, config, ct: ct);
                    // The shared coordinator owns the DC1 overlay/backup boundary. Keep the GUI's
                    // established per-patch status handling below: passing the default config and
                    // no inventory plan makes this call overlay-only, while the existing UI copy,
                    // exception mapping, and optional EXE-patch ordering remain unchanged.
                    var res = RandomizationInstallCoordinator.InstallDc1(
                        dataDir,
                        outPath,
                        seed,
                        new RandomizerConfig(),
                        createStartingInventoryPlan: () => null,
                        output: _ => { },
                        overlayFailure: ex => throw ex)!.InstallResult;

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

                    if (dc1DoorSkip)
                        try
                        {
                            GameInstaller.PatchExeDoorSkip(dataDir, seed.ToString());
                            xn = (xn.Length == 0 ? "" : xn + ". ") + "door skip applied (near-instant transitions on the next launch)";
                        }
                        catch (IOException) { xn = (xn.Length == 0 ? "" : xn + ". ") + "door skip NOT applied — DINO.exe is locked; close the game and re-install"; xf = true; }
                        catch (Exception dex) { xn = (xn.Length == 0 ? "" : xn + ". ") + $"door skip NOT applied — {dex.Message}"; xf = true; }

                    if (dc1FastForwardCutscenes)
                        try
                        {
                            GameInstaller.PatchExeFastForwardCutscenes(dataDir, seed.ToString());
                            xn = (xn.Length == 0 ? "" : xn + ". ") + "fast-forward cutscenes applied (EXPERIMENTAL/crash risk; seen on the next launch)";
                        }
                        catch (IOException) { xn = (xn.Length == 0 ? "" : xn + ". ") + "fast-forward cutscenes NOT applied — DINO.exe is locked; close the game and re-install"; xf = true; }
                        catch (Exception fex) { xn = (xn.Length == 0 ? "" : xn + ". ") + $"fast-forward cutscenes NOT applied — {fex.Message}"; xf = true; }

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
            catch (OperationCanceledException)
            {
                // Generate phase aborted — only the working mod dir was written, the game is untouched.
                InstallStatusBrush = null;
                InstallStatusText = "Install cancelled — your game files were not changed.";
            }
            catch (Exception ex)
            {
                InstallStatusBrush = Brushes.Red;
                InstallStatusText = FriendlyError(ex);
            }
            finally
            {
                _installStop = null;
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
            IsBusy = true;                 // greys Play (CanPlayNow) and shows the busy bar during restore
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
                    ? $"✓ Restored original files." + (exeRestored ? " Dino2.exe restored." : "")
                    : exeRestored ? "✓ Dino2.exe restored." : "Nothing to restore.";
            }
            catch (Exception ex)
            {
                InstallStatusBrush = Brushes.Red;
                InstallStatusText = FriendlyError(ex);
            }
            finally
            {
                IsBusy = false;
                UpdateInstallButtons();
            }
        }

        /// <summary>Map an install/restore exception to a short, actionable line for the status label,
        /// keeping the raw exception out of the UI (it goes to the debug log only). Public + static so
        /// it is unit-tested directly, like the other pure helpers here (<see cref="ResolveDataDir"/>).</summary>
        public static string FriendlyError(Exception ex)
        {
            Debug.WriteLine(ex);   // raw detail stays in the log, never on the user-facing status line
            return ex switch
            {
                IOException io when io.Message.Contains("being used by another process")
                    => "Close the game (and any window open in its game folder), then install again.",
                FileNotFoundException or DirectoryNotFoundException
                    => "Couldn't find a game file — check the Game Location points at your DINO.exe / Dino2.exe folder.",
                UnauthorizedAccessException
                    => "Windows blocked the change — move the game out of Program Files, or run DinoRand as administrator.",
                _ => "Install failed — see the log for details.",
            };
        }

    }
}
