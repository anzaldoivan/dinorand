using System.ComponentModel;
using Avalonia.Input.Platform;
using DinoRand.App;
using DinoRand.App.Services;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Spoiler;
using Xunit;

namespace DinoRand.FileFormats.Tests.Refactor;

public sealed class ViewModelCharacterizationTests
{
    private sealed class Picker : IFilePicker
    {
        public Task<string?> PickFileAsync(FilePickerRequest request) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(FolderPickerRequest request) => Task.FromResult<string?>(null);
    }

    private sealed class Dialogs : IDialogs
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;
        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    }

    private static MainWindowViewModel NewVm() => new(new Picker(), new Dialogs(), () => null!, new AppSettings());

    [Fact]
    public void Avalonia_default_projection_preserves_its_difference_from_plain_CLI_config()
    {
        var cli = new RandomizerConfig();
        var vm = NewVm();
        vm.SeedText = SeedString.Encode(new Seed(42), new RandomizerConfig());

        Assert.Equal("dc1", vm.SelectedGame.Id);
        Assert.True(vm.RandomizeItems);
        Assert.True(vm.RandomizeEnemies);
        Assert.False(cli.NormalizePickupVisuals);
        Assert.True(vm.CurrentConfig.NormalizePickupVisuals);
        Assert.False(vm.CurrentConfig.ImportPickupModels);
        Assert.Equal("DINO-", vm.SeedText[..5]);
    }

    [Fact]
    public void DC2_projection_capabilities_and_notifications_are_stable()
    {
        var vm = NewVm();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is not null) changed.Add(e.PropertyName); };
        vm.SelectedGameIndex = 1;
        vm.RandomizeEnemies = true;
        vm.IncludeDc2BossEnemies = true;
        vm.Dc2RandomizeRaptorTiers = true;

        Assert.Equal("dc2", vm.SelectedGame.Id);
        Assert.True(vm.CanRandomizeItems);
        Assert.True(vm.CanShuffleKeyItems);
        Assert.True(vm.Dc2EnemyOptionsVisible);
        Assert.True(vm.CurrentConfig.IncludeDc2BossEnemies);
        Assert.True(vm.CurrentConfig.Dc2RandomizeRaptorTiers);
        Assert.Contains(nameof(vm.SelectedGameIndex), changed);
        Assert.DoesNotContain(nameof(vm.SelectedGame), changed);
        Assert.DoesNotContain(nameof(vm.CurrentConfig), changed);
        Assert.StartsWith("DINO-", vm.SeedText);
    }
}
