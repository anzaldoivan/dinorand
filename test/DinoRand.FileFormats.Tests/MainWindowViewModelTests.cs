using System.Threading.Tasks;
using Avalonia.Input.Platform;
using DinoRand.App;
using DinoRand.App.Services;
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
}
