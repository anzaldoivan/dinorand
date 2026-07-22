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

    }
}
