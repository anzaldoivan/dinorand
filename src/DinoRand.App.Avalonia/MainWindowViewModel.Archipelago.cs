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
        // --- Archipelago connect tab (AP-CLIENT-PLAN.md D3, increment 2) -------
        // The whole session (connect → logic_version gate → placement install → 4 Hz poll loop)
        // is Dc1ApRunner — the SAME implementation `dinorand --ap-connect` runs. This tab only owns
        // the connect/disconnect state machine and pipes the runner's log lines into the log view.

        private const string DefaultApHostPort = "archipelago.gg:38281";
        private const int ApLogMaxLines = 500;

        private readonly ApRunner _apRunner;
        private readonly Action<Action> _uiPost;
        private CancellationTokenSource _apStop;
        private CancellationTokenSource _installStop;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RandomizerTabActive))]
        private int _selectedTabIndex;

        /// <summary>The bottom-pinned "Install to Game" panel belongs to the randomizer tab only —
        /// the AP tab does its own (placement) install through the runner.</summary>
        public bool RandomizerTabActive => SelectedTabIndex == 0;

        /// <summary>The AP tab is selectable only once a usable game resolves: connecting installs
        /// the multiworld's placement into that install, so without one the tab's Connect could only
        /// ever fail. Deliberately the SAME predicate as the Install button (<see cref="CanInstall"/>
        /// = a resolved Data dir and no DRM wrapper) so the two can't drift apart.</summary>
        public bool ApTabEnabled => CanInstall;

        [ObservableProperty] private string _apHostPort = DefaultApHostPort;
        [ObservableProperty] private string _apSlot = "";
        [ObservableProperty] private string _apPassword = "";   // session-only, never persisted

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ApConnectButtonText))]
        [NotifyPropertyChangedFor(nameof(ApFieldsEnabled))]
        [NotifyPropertyChangedFor(nameof(WindowTitle))]
        [NotifyPropertyChangedFor(nameof(ShouldConfirmClose))]
        [NotifyPropertyChangedFor(nameof(CloseConfirmMessage))]
        private bool _apRunning;

        [ObservableProperty] private string _apStatusText = "";
        [ObservableProperty] private IBrush _apStatusBrush;

        /// <summary>The runner's own progress lines (identical to what the CLI prints), newest last.</summary>
        public ObservableCollection<string> ApLog { get; } = new();

        public string ApConnectButtonText => ApRunning ? "Disconnect" : "Connect";
        public bool ApFieldsEnabled => !ApRunning;

        /// <summary>AP support is DC1-only today (plan D4), so the tab's Connect is gated on the game.</summary>
        public bool CanUseArchipelago => SelectedGame.Id == "dc1";

        /// <summary>The running session, or null when idle. Completes only after the UI state has been
        /// reset, so a test can await it and then assert; the UI itself only reads <see cref="ApRunning"/>.</summary>
        public Task ApSessionTask { get; private set; }

        // --- Shutdown safety (GUI-SHUTDOWN-AND-CANCEL-PLAN.md) ----------------
        // Closing the window kills whatever is running, and the GAME KEEPS RUNNING — so a silent
        // close can leave someone playing with no checks reaching the multiworld, or leave the
        // game's Data\ folder half-overlaid. Deliberately ahead of every AP reference client
        // (none warn); justified because none of them are an embedded panel whose host window
        // closing leaves the game alive. The view owns the Avalonia close dance; the DECISION and
        // the copy live here so they are unit-testable without a window.

        public const string DefaultWindowTitle = "DinoRand: Dino Crisis Randomizer";

        /// <summary>Connected state in the title bar, so the taskbar itself carries it (the
        /// PopTracker / kvui convention).</summary>
        public string WindowTitle => ApRunning
            ? $"DinoRand — ● Connected: {ApSlot}@{ApHostPort}"
            : DefaultWindowTitle;

        /// <summary>True when closing would interrupt something the user cares about. An idle
        /// close stays instant and silent — a confirmation nobody would ever answer "no" to is
        /// exactly the anti-pattern the platform guidance warns about.</summary>
        public bool ShouldConfirmClose => IsBusy || ApRunning;

        /// <summary>Install wins when both are true: a half-written game folder is the worse
        /// consequence. The AP copy deliberately does NOT claim lost checks — the poll engine
        /// re-derives them from the game's flag bank on reconnect; what is really lost is time,
        /// and the other players waiting on those checks.</summary>
        public string CloseConfirmMessage => IsBusy
            ? "DinoRand is still installing to your game.\n\nClosing now interrupts it and can leave "
              + "the game's files partially modified — use “Restore Originals” to put them back.\n\nClose anyway?"
            : "Your Archipelago session is still connected.\n\nThe game will keep running, but closing "
              + "DinoRand stops your checks from reaching the multiworld until you reconnect — other "
              + "players waiting on your items will stay blocked.\n\nClose anyway?";

        /// <summary>Cancel everything in flight, without waiting: the AP session's own finally-path
        /// saves its state and disconnects. Called on the confirmed close path — never awaited on
        /// the UI thread (that deadlocks).</summary>
        public void CancelRunningWork()
        {
            _apStop?.Cancel();
            _installStop?.Cancel();
        }

        /// <summary>One button, two meanings (the AP client convention): start a session, or cancel the
        /// running one — cancellation is exactly the CLI's Ctrl-C path (clean disconnect + state save).</summary>
        [RelayCommand]
        private void ApToggleConnect()
        {
            if (_apStop is { } running)
            {
                ApSetStatus("Disconnecting…", null);
                running.Cancel();
                return;
            }

            if (!CanUseArchipelago)
            {
                ApSetStatus($"Archipelago support is Dino Crisis 1 only — {SelectedGame.DisplayName} is not supported yet.", Brushes.Red);
                return;
            }
            if (string.IsNullOrWhiteSpace(ApSlot))
            {
                ApSetStatus("Enter the slot name you used in your YAML.", Brushes.Red);
                return;
            }
            var gamePath = ResolveGameDir(GamePath);
            if (CurrentDataDir().Length == 0)
            {
                ApSetStatus("Set a valid game folder on the Randomizer tab first.", Brushes.Red);
                return;
            }

            _settings.ApHostPort = ApHostPort;
            _settings.ApSlot = ApSlot;
            _settings.Save();

            ApLog.Clear();
            ApRunning = true;
            ApSetStatus("Connecting… the placement install can take several minutes.", null);

            var host = string.IsNullOrWhiteSpace(ApHostPort) ? DefaultApHostPort : ApHostPort.Trim();
            var slot = ApSlot.Trim();
            var password = string.IsNullOrEmpty(ApPassword) ? null : ApPassword;
            var outDir = WorkingModDir + "_ap";
            var cts = new CancellationTokenSource();
            _apStop = cts;

            ApSessionTask = Task.Run(
                    () => _apRunner(host, slot, password, gamePath, outDir, ApAppendLog, ApAppendLog, cts.Token))
                .ContinueWith(t => _uiPost(() =>
                {
                    if (t.IsFaulted)
                        ApSetStatus(t.Exception?.GetBaseException().Message ?? "Session failed.", Brushes.Red);
                    else if (t.Result == 0)
                        ApSetStatus("Disconnected.", null);
                    else
                        ApSetStatus("Session ended — see the log above.", Brushes.OrangeRed);

                    cts.Dispose();
                    _apStop = null;
                    ApRunning = false;
                }));
        }

        // Called from the runner's background thread — marshal to the UI thread before touching
        // the bound collection (Avalonia rejects cross-thread collection changes).
        private void ApAppendLog(string line) => _uiPost(() =>
        {
            ApLog.Add(line);
            while (ApLog.Count > ApLogMaxLines)
                ApLog.RemoveAt(0);
            // The first line the runner emits after login confirms the session is live.
            if (ApRunning && line.StartsWith("connected:", StringComparison.Ordinal))
                ApSetStatus(line, Brushes.Green);
        });

        private void ApSetStatus(string text, IBrush brush)
        {
            ApStatusText = text;
            ApStatusBrush = brush;
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
}
