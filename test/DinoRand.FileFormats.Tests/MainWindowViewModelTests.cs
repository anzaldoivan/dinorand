using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using DinoRand.App;
using DinoRand.App.Services;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The Avalonia GUI view-model, exercised decoupled from the randomizer engine and any real game
/// install. The three DI seams (<see cref="IFilePicker"/> / <see cref="IDialogs"/> / clipboard) are
/// faked; every constructor-path filesystem touch is best-effort (<c>AppSettings.Load</c>) or guarded
/// by <c>Directory.Exists</c> (the Windows default path never resolves on the CI host), so the VM
/// constructs and its seed↔UI projection runs without touching disk or the engine. The
/// Generate/Install/Restore commands (which <c>new</c> the real runners) are deliberately NOT tested.
/// </summary>
public class MainWindowViewModelTests
{
    private sealed class FakeFilePicker : IFilePicker
    {
        public Task<string?> PickFileAsync(FilePickerRequest request) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(FolderPickerRequest request) => Task.FromResult<string?>(null);
    }

    private sealed class FakeDialogs : IDialogs
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;
        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    }

    // The clipboard Func is only pulled by Copy/Paste commands, never on any path under test.
    // Inject a fresh in-memory AppSettings so tests neither read nor depend on a real settings file
    // (a shared %APPDATA% file would make the starting game/seed order-dependent across tests).
    private static MainWindowViewModel NewVm() =>
        new(new FakeFilePicker(), new FakeDialogs(), () => null!, new AppSettings());

    [Fact]
    public void Constructs_with_fakes_on_dc1_with_a_shareable_seed()
    {
        var vm = NewVm();

        Assert.Equal("dc1", vm.SelectedGame.Id);          // default game
        Assert.StartsWith("DINO-", vm.SeedText);          // ctor established a real seed
        Assert.Equal(vm.SeedText, vm.SeedQrString);       // QR mirrors the seed box
    }

    [Fact]
    public void Toggling_options_encodes_into_the_seed_and_round_trips_through_a_paste()
    {
        var vm = NewVm();
        vm.RandomizeDoors = true;
        vm.ShuffleKeyItems = true;

        Assert.True(vm.CurrentConfig.RandomizeDoors);
        Assert.True(vm.CurrentConfig.ShuffleKeyItems);
        // "Shuffle Key Items" now also scatters into ammo/health pickups with no separate toggle — the
        // scatter rides on the shuffle (config-built ShuffleKeyItemsIntoPickups = ShuffleKeyItems).
        Assert.True(vm.CurrentConfig.ShuffleKeyItemsIntoPickups);
        var seed = vm.SeedText;

        // Pasting that seed into a fresh VM reproduces the same run (BioRand-style share string).
        var pasted = NewVm();
        pasted.SeedText = seed;

        Assert.True(pasted.CurrentConfig.RandomizeDoors);
        Assert.True(pasted.CurrentConfig.ShuffleKeyItems);
        Assert.True(pasted.CurrentConfig.ShuffleKeyItemsIntoPickups);
        Assert.Equal(seed, pasted.SeedText);
    }

    [Fact]
    public void Shuffle_key_items_does_not_enable_pickup_model_import_by_default()
    {
        var vm = NewVm();
        vm.ShuffleKeyItems = true;

        Assert.True(vm.CanShuffleKeyItemsModelChange);
        Assert.False(vm.ShuffledKeyItemsModelChange);
        Assert.False(vm.CurrentConfig.ImportPickupModels);

        vm.ShuffledKeyItemsModelChange = true;

        Assert.True(vm.CurrentConfig.ImportPickupModels);
    }

    [Fact]
    public void Shuffled_key_item_model_change_is_cleared_for_dc2()
    {
        var vm = NewVm();
        vm.ShuffledKeyItemsModelChange = true;

        vm.SelectedGameIndex = 1;

        Assert.False(vm.CanShuffleKeyItemsModelChange);
        Assert.False(vm.ShuffledKeyItemsModelChange);
        Assert.False(vm.CurrentConfig.ImportPickupModels);
    }

    [Fact]
    public void Dc1_enemy_hp_option_defaults_off_and_round_trips_through_the_seed()
    {
        var vm = NewVm();
        vm.RandomizeEnemies = false;

        Assert.True(vm.CanRandomizeEnemyHp);
        Assert.False(vm.RandomizeEnemyHp);
        Assert.False(vm.CurrentConfig.RandomizeEnemyHp);
        Assert.False(vm.EnemyDifficultyEnabled);

        vm.RandomizeEnemyHp = true;

        Assert.True(vm.CurrentConfig.RandomizeEnemyHp);
        Assert.True(vm.EnemyDifficultyEnabled);
        var seed = vm.SeedText;

        var pasted = NewVm();
        pasted.SeedText = seed;

        Assert.True(pasted.RandomizeEnemyHp);
        Assert.True(pasted.CurrentConfig.RandomizeEnemyHp);
        Assert.Equal(seed, pasted.SeedText);
    }

    [Fact]
    public void Enemy_hp_option_is_dc1_only_and_preserved_in_its_game_slice()
    {
        var vm = NewVm();
        vm.RandomizeEnemyHp = true;

        vm.SelectedGameIndex = 1;

        Assert.Equal("dc2", vm.SelectedGame.Id);
        Assert.False(vm.CanRandomizeEnemyHp);
        Assert.False(vm.RandomizeEnemyHp);
        Assert.False(vm.CurrentConfig.RandomizeEnemyHp);

        vm.SelectedGameIndex = 0;

        Assert.Equal("dc1", vm.SelectedGame.Id);
        Assert.True(vm.CanRandomizeEnemyHp);
        Assert.True(vm.RandomizeEnemyHp);
        Assert.True(vm.CurrentConfig.RandomizeEnemyHp);
    }

    [Fact]
    public void Dc2_randomized_weapons_clears_and_disables_shared_weapons_then_roundtrips()
    {
        var vm = NewVm();
        vm.SelectedGameIndex = 1;
        vm.Dc2SharedWeapons = true;

        vm.Dc2RandomizeWeapons = true;

        Assert.False(vm.Dc2SharedWeapons);
        Assert.False(vm.CanUseDc2SharedWeapons);
        Assert.True(vm.CurrentConfig.Dc2RandomizeWeapons);
        Assert.False(vm.CurrentConfig.Dc2SharedWeapons);

        var pasted = NewVm();
        pasted.SelectedGameIndex = 1;
        pasted.SeedText = vm.SeedText;
        Assert.True(pasted.Dc2RandomizeWeapons);
        Assert.False(pasted.CanUseDc2SharedWeapons);

        pasted.Dc2RandomizeWeapons = false;
        Assert.True(pasted.CanUseDc2SharedWeapons);
    }

    // NormalizePickupVisuals (Lever A, PICKUP-GROUND-MODEL-FEASIBILITY.md) has no GUI toggle — the GUI
    // derives it: ON whenever ANY item randomization runs (normal item pickups OR key items), so a
    // relocated pickup never renders as the wrong/invisible item. These pin that derivation + its
    // boundaries, and that a pasted share-seed re-derives it (the flag is not seed-encoded).

    [Fact]
    public void NormalizePickupVisuals_on_when_only_item_pickups_randomized()
    {
        var vm = NewVm();
        vm.ShuffleKeyItems = false;
        vm.RandomizeItems = true;

        Assert.True(vm.CurrentConfig.NormalizePickupVisuals);
    }

    [Fact]
    public void NormalizePickupVisuals_on_when_only_key_items_shuffled()
    {
        var vm = NewVm();
        vm.RandomizeItems = false;
        vm.ShuffleKeyItems = true;

        Assert.True(vm.CurrentConfig.NormalizePickupVisuals);
    }

    [Fact]
    public void NormalizePickupVisuals_on_when_both_item_and_key_randomization_on()
    {
        var vm = NewVm();
        vm.RandomizeItems = true;
        vm.ShuffleKeyItems = true;

        Assert.True(vm.CurrentConfig.NormalizePickupVisuals);
    }

    [Fact]
    public void NormalizePickupVisuals_off_when_no_item_randomization()
    {
        var vm = NewVm();
        vm.RandomizeItems = false;
        vm.ShuffleKeyItems = false;

        Assert.False(vm.CurrentConfig.NormalizePickupVisuals);
    }

    [Fact]
    public void NormalizePickupVisuals_is_rederived_after_pasting_a_share_seed()
    {
        // Author a seed with item randomization on, then paste its share string into a fresh VM.
        // The flag is not seed-encoded, so the paste path must re-derive it (not read a stored false).
        var author = NewVm();
        author.ShuffleKeyItems = false;
        author.RandomizeItems = true;
        var seed = author.SeedText;
        Assert.True(author.CurrentConfig.NormalizePickupVisuals);

        var pasted = NewVm();
        pasted.SeedText = seed;

        Assert.True(pasted.CurrentConfig.RandomizeItems);
        Assert.True(pasted.CurrentConfig.NormalizePickupVisuals);
    }

    [Fact]
    public void Invalid_seed_text_flags_the_border_without_changing_the_config()
    {
        var vm = NewVm();
        vm.RandomizeDoors = true;
        var before = vm.CurrentConfig.RandomizeDoors;

        vm.SeedText = "definitely-not-a-DINO-seed";

        Assert.NotNull(vm.SeedBorder);                    // red border on parse failure
        Assert.Equal(before, vm.CurrentConfig.RandomizeDoors);   // config untouched
    }

    [Fact]
    public void Item_ratio_rows_are_visible_only_with_pool_replacement_on()
    {
        var vm = NewVm();

        vm.RandomizeItems = true;
        vm.ReplaceItemPool = false;
        Assert.True(vm.ReplacePoolVisible);
        Assert.False(vm.ItemRatiosVisible);               // pool shown, but replacement off

        vm.ReplaceItemPool = true;
        Assert.True(vm.ItemRatiosVisible);                // both on

        vm.RandomizeItems = false;
        Assert.False(vm.ReplacePoolVisible);              // whole pool group hidden
        Assert.False(vm.ItemRatiosVisible);
    }

    [Fact]
    public void Switching_games_reapplies_capabilities_and_keeps_each_games_path_separate()
    {
        var vm = NewVm();
        Assert.Equal("dc1", vm.SelectedGame.Id);
        Assert.True(vm.CanRandomizeItems);                // DC1 supports items
        Assert.False(vm.Dc2RaptorPanelVisible);

        vm.GamePath = @"X:\only-dc1";
        vm.SelectedGameIndex = 1;                         // → DC2

        Assert.Equal("dc2", vm.SelectedGame.Id);
        Assert.True(vm.CanRandomizeItems);                // DC2 v2 supports direct-source item shuffle
        Assert.True(vm.CanShuffleKeyItems);
        Assert.True(vm.Dc2RaptorPanelVisible);
        Assert.NotEqual(@"X:\only-dc1", vm.GamePath);     // DC2 slice starts blank, not DC1's path

        vm.SelectedGameIndex = 0;                         // back to DC1
        Assert.Equal(@"X:\only-dc1", vm.GamePath);        // DC1's path restored from its slice
    }

    [Fact]
    public void Dc2_cross_species_suboptions_show_only_for_dc2_with_enemy_rando_on()
    {
        var vm = NewVm();
        vm.RandomizeEnemies = true;
        Assert.False(vm.Dc2EnemyOptionsVisible);          // DC1: no cross-species donor pool

        vm.SelectedGameIndex = 1;                         // → DC2
        vm.RandomizeEnemies = true;
        Assert.True(vm.Dc2EnemyOptionsVisible);
    }

    // --- Fixed-species dropdown contents (ENEMY-DISTRIBUTION-PLAN.md D3/D7 + K72) -----------
    // The dropdown must mirror the SAME pool the pass validates the pin against
    // (Dc2SpeciesTable.DonorPool(setpiece: true, boss: true, allowWater: <water toggle>)) —
    // setpiece/boss always pinnable (a pin IS the opt-in), aquatic only behind the
    // experimental water toggle, never a species the pass would reject.

    [Fact]
    public void Fixed_species_dropdown_default_is_the_land_pool_with_setpiece_and_boss()
    {
        var vm = NewVm();
        var types = vm.Dc2FixedSpeciesOptions.Select(o => o.Type).ToHashSet();

        var expected = Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: true)
            .Select(s => s.Type).ToHashSet();
        Assert.Equal(expected, types);

        Assert.Contains(0x09, types);   // Triceratops (setpiece)
        Assert.Contains(0x06, types);   // Giganotosaurus (setpiece)
        Assert.Contains(0x03, types);   // T-Rex (boss)
        Assert.DoesNotContain(0x0a, types); // Mosasaurus — K72 crash guard, water toggle off
        Assert.DoesNotContain(0x04, types); // Pteranodon — flyer, never a donor
    }

    [Fact]
    public void Fixed_species_dropdown_admits_aquatic_donors_only_with_the_water_toggle()
    {
        var vm = NewVm();
        vm.Dc2AllowWaterLevelEnemySwaps = true;

        var types = vm.Dc2FixedSpeciesOptions.Select(o => o.Type).ToHashSet();
        var expected = Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: true, allowWater: true)
            .Select(s => s.Type).ToHashSet();
        Assert.Equal(expected, types);
        Assert.Contains(0x0a, types);       // Mosasaurus now pinnable (engine accepts it, wave-only)
        Assert.DoesNotContain(0x04, types); // flyer still excluded

        vm.Dc2AllowWaterLevelEnemySwaps = false;
        Assert.DoesNotContain(0x0a, vm.Dc2FixedSpeciesOptions.Select(o => o.Type));
    }

    [Fact]
    public void Turning_the_water_toggle_off_resets_an_aquatic_pin_to_a_safe_land_donor()
    {
        var vm = NewVm();
        vm.Dc2AllowWaterLevelEnemySwaps = true;
        vm.SelectedDc2FixedSpecies = vm.Dc2FixedSpeciesOptions.First(o => o.Type == 0x0a); // pin Mosasaurus

        vm.Dc2AllowWaterLevelEnemySwaps = false;

        // Selection must land on a member of the (now land-only) list — never a stale aquatic
        // pin the pass would silently skip every room for.
        Assert.Contains(vm.SelectedDc2FixedSpecies, vm.Dc2FixedSpeciesOptions);
        Assert.NotEqual(0x0a, vm.SelectedDc2FixedSpecies.Type);
    }

    // --- Play button gating (GUI de-clutter: Play must grey while a generate/install/restore runs)
    // CanPlayNow = CanPlay && !IsBusy. The XAML Play button binds to CanPlayNow; the binding only
    // refreshes if both source flags raise a change notification for it (NotifyPropertyChangedFor).

    [Fact]
    public void Play_is_offered_only_when_a_game_resolves_and_nothing_is_running()
    {
        var vm = NewVm();

        vm.CanPlay = true; vm.IsBusy = false;
        Assert.True(vm.CanPlayNow);                        // exe resolved, idle → playable

        vm.IsBusy = true;
        Assert.False(vm.CanPlayNow);                       // install/restore running → greyed

        vm.IsBusy = false; vm.CanPlay = false;
        Assert.False(vm.CanPlayNow);                       // no exe → greyed regardless
    }

    [Fact]
    public void CanPlayNow_raises_change_notification_when_busy_toggles()
    {
        // The Play button's IsEnabled binding won't update mid-install unless IsBusy raises a
        // PropertyChanged for CanPlayNow. This guards that NotifyPropertyChangedFor wiring.
        var vm = NewVm();
        vm.CanPlay = true;
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.IsBusy = true;

        Assert.Contains(nameof(vm.CanPlayNow), raised);
    }

    // --- Archipelago connect tab (AP-CLIENT-PLAN.md increment 2) ---------------------------------
    // The tab owns exactly one piece of logic: the connect/disconnect state machine around
    // Dc1ApRunner. It is driven here with a fake runner (blocks until cancelled, like a real
    // session) and a synchronous "UI post", so no server, no game and no dispatcher are involved.

    /// <summary>A throwaway DC1-shaped install (Data\ holding a room file) so the tab's
    /// "valid game folder" guard resolves without a real game.</summary>
    private static string NewFakeDc1Install()
    {
        var root = Path.Combine(Path.GetTempPath(), "dinorand_ap_vm_" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        Directory.CreateDirectory(data);
        File.WriteAllBytes(Path.Combine(data, "st102.dat"), new byte[16]);   // DC1 room-file name shape
        return root;
    }

    // The tab is pointless (and its Connect misleading) without a game to install into, so it is
    // selectable only once a usable game resolves — same predicate the Install button uses.

    [Fact]
    public void Ap_tab_is_not_selectable_until_a_game_folder_resolves()
    {
        var vm = NewVm();

        vm.GamePath = "";
        vm.ValidateGamePath();
        Assert.False(vm.ApTabEnabled);                       // nothing selected → tab greyed

        var install = NewFakeDc1Install();
        try
        {
            vm.GamePath = install;
            vm.ValidateGamePath();
            Assert.True(vm.ApTabEnabled);                    // real game folder → tab available
        }
        finally
        {
            try { Directory.Delete(install, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Clearing_the_game_folder_pulls_the_user_off_the_ap_tab()
    {
        var vm = NewVm();
        var install = NewFakeDc1Install();
        try
        {
            vm.GamePath = install;
            vm.ValidateGamePath();
            vm.SelectedTabIndex = 1;                         // user is on the Archipelago tab

            vm.GamePath = "";
            vm.ValidateGamePath();

            Assert.False(vm.ApTabEnabled);
            Assert.Equal(0, vm.SelectedTabIndex);            // never leave a disabled tab showing
        }
        finally
        {
            try { Directory.Delete(install, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Ap_connect_then_disconnect_runs_the_session_once_and_returns_to_idle()
    {
        using var started = new ManualResetEventSlim();
        CancellationToken seen = default;
        int runs = 0;

        int FakeRunner(string hostPort, string slot, string password, string install, string outDir,
            Action<string> log, Action<string> error, CancellationToken ct)
        {
            runs++;
            seen = ct;
            log($"connected: seed TEST, slot #1 '{slot}', goal room 060d");
            started.Set();
            ct.WaitHandle.WaitOne();          // a live session blocks until cancelled
            log("disconnected");
            return 0;
        }

        var vm = new MainWindowViewModel(new FakeFilePicker(), new FakeDialogs(), () => null!,
            new AppSettings(), FakeRunner, a => a());
        var install = NewFakeDc1Install();
        try
        {
            vm.GamePath = install;
            vm.ApSlot = "Regina";

            vm.ApToggleConnectCommand.Execute(null);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "the runner never started");
            Assert.True(vm.ApRunning);
            Assert.Equal("Disconnect", vm.ApConnectButtonText);
            Assert.False(vm.ApFieldsEnabled);                       // fields lock while connected

            vm.ApToggleConnectCommand.Execute(null);                // same button = disconnect
            await vm.ApSessionTask;

            Assert.True(seen.IsCancellationRequested);              // cancellation = the CLI's Ctrl-C path
            Assert.Equal(1, runs);                                  // toggled, never started twice
            Assert.False(vm.ApRunning);
            Assert.Equal("Connect", vm.ApConnectButtonText);
            Assert.True(vm.ApFieldsEnabled);
            Assert.Contains(vm.ApLog, l => l.StartsWith("connected:", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(install, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Ap_connect_refuses_without_a_slot_name_and_never_starts_a_session()
    {
        bool ran = false;
        var vm = new MainWindowViewModel(new FakeFilePicker(), new FakeDialogs(), () => null!,
            new AppSettings(),
            (h, s, p, i, o, l, e, ct) => { ran = true; return 0; },
            a => a());

        vm.ApSlot = "";
        vm.ApToggleConnectCommand.Execute(null);

        Assert.False(ran);
        Assert.False(vm.ApRunning);
        Assert.Null(vm.ApSessionTask);
        Assert.Contains("slot name", vm.ApStatusText);
    }

    // --- Shutdown safety (GUI-SHUTDOWN-AND-CANCEL-PLAN.md) ---------------------------------------
    // Closing the window kills the AP session / install while the GAME KEEPS RUNNING, so the close
    // is confirmed — but ONLY when something is actually in flight. A confirmation nobody would
    // answer "no" to is the anti-pattern; an idle close must stay instant and silent.

    [Fact]
    public void Idle_close_is_never_confirmed()
    {
        var vm = NewVm();

        Assert.False(vm.IsBusy);
        Assert.False(vm.ApRunning);
        Assert.False(vm.ShouldConfirmClose);
    }

    [Fact]
    public void Close_is_confirmed_while_connected_or_installing_and_install_wins_the_message()
    {
        var vm = NewVm();

        vm.ApRunning = true;
        Assert.True(vm.ShouldConfirmClose);
        Assert.Contains("Archipelago", vm.CloseConfirmMessage);

        vm.ApRunning = false;
        vm.IsBusy = true;
        Assert.True(vm.ShouldConfirmClose);
        Assert.Contains("installing", vm.CloseConfirmMessage);

        // Both in flight: the half-written game folder is the worse consequence, so it wins.
        vm.ApRunning = true;
        Assert.Contains("installing", vm.CloseConfirmMessage);
        Assert.Contains("Restore Originals", vm.CloseConfirmMessage);
    }

    [Fact]
    public void Confirm_close_state_raises_change_notifications_for_the_dialog_decision()
    {
        // The view reads ShouldConfirmClose at close time, but the title bar binds live — without
        // these notifications the window title would keep saying "Connected" after disconnect.
        var vm = NewVm();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ApRunning = true;

        Assert.Contains(nameof(vm.ShouldConfirmClose), raised);
        Assert.Contains(nameof(vm.CloseConfirmMessage), raised);
        Assert.Contains(nameof(vm.WindowTitle), raised);
    }

    [Fact]
    public void Window_title_carries_the_connected_slot_and_reverts_on_disconnect()
    {
        var vm = NewVm();
        Assert.Equal(MainWindowViewModel.DefaultWindowTitle, vm.WindowTitle);

        vm.ApSlot = "Regina";
        vm.ApHostPort = "archipelago.gg:38281";
        vm.ApRunning = true;
        Assert.Contains("Regina", vm.WindowTitle);
        Assert.Contains("Connected", vm.WindowTitle);

        vm.ApRunning = false;
        Assert.Equal(MainWindowViewModel.DefaultWindowTitle, vm.WindowTitle);
    }

    [Fact]
    public async Task Cancelling_running_work_stops_a_live_ap_session()
    {
        // The confirmed-close path calls CancelRunningWork() instead of letting the process die,
        // so the session takes its normal finally-path (state save + clean disconnect).
        using var started = new ManualResetEventSlim();
        int FakeRunner(string hostPort, string slot, string password, string install, string outDir,
            Action<string> log, Action<string> error, CancellationToken ct)
        {
            started.Set();
            ct.WaitHandle.WaitOne();
            return 0;
        }

        var vm = new MainWindowViewModel(new FakeFilePicker(), new FakeDialogs(), () => null!,
            new AppSettings(), FakeRunner, a => a());
        var install = NewFakeDc1Install();
        try
        {
            vm.GamePath = install;
            vm.ApSlot = "Regina";
            vm.ApToggleConnectCommand.Execute(null);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

            vm.CancelRunningWork();
            await vm.ApSessionTask;

            Assert.False(vm.ApRunning);
            Assert.False(vm.ShouldConfirmClose);             // nothing left in flight
        }
        finally
        {
            try { Directory.Delete(install, recursive: true); } catch { }
        }
    }

    // --- Friendly install/restore errors (GUI de-clutter: no raw exception strings in the UI) -----
    // The screenshot's "The process cannot access the file ... because it is being used by another
    // process" must map to a plain, actionable line; the raw exception is kept only in the log.

    [Fact]
    public void FriendlyError_maps_a_locked_file_to_a_close_the_game_message()
    {
        var msg = MainWindowViewModel.FriendlyError(
            new IOException("The process cannot access the file 'DINO.exe' because it is being used by another process."));

        Assert.Contains("Close the game", msg);
        Assert.DoesNotContain("process cannot access", msg);   // raw detail never surfaced
    }

    [Fact]
    public void FriendlyError_maps_missing_paths_to_a_check_location_message()
    {
        Assert.Contains("Game Location", MainWindowViewModel.FriendlyError(new FileNotFoundException()));
        Assert.Contains("Game Location", MainWindowViewModel.FriendlyError(new DirectoryNotFoundException()));
    }

    [Fact]
    public void FriendlyError_maps_permission_denied_to_an_admin_hint()
    {
        var msg = MainWindowViewModel.FriendlyError(new UnauthorizedAccessException("Access to the path is denied."));

        Assert.Contains("administrator", msg);
        Assert.DoesNotContain("Access to the path", msg);      // raw detail never surfaced
    }

    [Fact]
    public void FriendlyError_falls_back_without_leaking_the_raw_exception()
    {
        var msg = MainWindowViewModel.FriendlyError(new InvalidOperationException("boom: internal detail 0xDEAD"));

        Assert.Contains("log", msg);
        Assert.DoesNotContain("0xDEAD", msg);                  // no internal detail in the UI line
    }

    // --- Hidden-but-wired: "Import external music" is IsVisible=False in the view, but its binding
    // must still flow into the config so un-hiding it is a one-attribute change (no dead control).

    [Fact]
    public void ImportBgm_still_flows_into_the_config_for_dc1_though_the_control_is_hidden()
    {
        var vm = NewVm();                                  // default game = DC1 (supports BGM import)
        Assert.True(vm.CanImportBgm);

        vm.ImportBgm = true;
        Assert.True(vm.CurrentConfig.RandomizeBgm);        // ImportBgm → config.RandomizeBgm, unbroken
    }
}
