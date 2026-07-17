using System;
using System.Collections.Generic;
using System.IO;
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
        Assert.False(vm.CanRandomizeItems);               // DC2 doesn't support items → greyed
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
