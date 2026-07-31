using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DinoRand.App;
using DinoRand.App.Services;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public sealed class ApGuiReenableTests
{
    [Fact]
    public void Player_yaml_quotes_arbitrary_names_and_contains_only_the_minimum_world_document()
    {
        var yaml = PlayerYamlRenderer.Render("A:\"player\nname");

        Assert.Equal("name: \"A:\\\"player\\nname\"\ngame: Dino Crisis 1\n", yaml);
        Assert.DoesNotContain("server:", yaml);
        Assert.DoesNotContain("password:", yaml);
        Assert.DoesNotContain("options:", yaml);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Player_yaml_rejects_blank_names(string name)
    {
        Assert.Throws<ArgumentException>(() => PlayerYamlRenderer.Render(name));
    }

    [Fact]
    public async Task Save_player_yaml_uses_the_picker_seam_and_writes_the_safe_document()
    {
        var root = Path.Combine(Path.GetTempPath(), "dinorand_ap_yaml_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Player.yaml");
        var picker = new SavingPicker(path);
        try
        {
            var vm = new MainWindowViewModel(picker, new AcceptingDialogs(), () => null!, new AppSettings());
            vm.ApPlayerName = "A:\"player";

            await vm.SavePlayerYamlCommand.ExecuteAsync(null);

            Assert.Equal("Player.yaml", picker.Request.SuggestedFileName);
            Assert.Equal("name: \"A:\\\"player\"\ngame: Dino Crisis 1\n", File.ReadAllText(path));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Dc2_can_show_the_ap_tab_but_cannot_connect()
    {
        var vm = new MainWindowViewModel(new SavingPicker(null), new AcceptingDialogs(), () => null!, new AppSettings());
        vm.SelectedGameIndex = 1;

        Assert.False(vm.CanUseArchipelago);
        Assert.False(vm.ApConnectEnabled);
    }

    [Fact]
    public async Task Connect_confirmation_happens_before_the_runner_starts()
    {
        var root = Path.Combine(Path.GetTempPath(), "dinorand_ap_confirm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        File.WriteAllBytes(Path.Combine(root, "Data", "st102.dat"), new byte[16]);
        bool runnerStarted = false;
        var dialogs = new GateDialogs(false);
        try
        {
            var vm = new MainWindowViewModel(new SavingPicker(null), dialogs, () => null!, new AppSettings(),
                (h, s, p, i, o, l, e, ct) => { runnerStarted = true; return 0; }, a => a());
            vm.GamePath = root;
            vm.ApSlot = "Player";

            await vm.ApToggleConnectCommand.ExecuteAsync(null);

            Assert.True(dialogs.ConfirmCalled);
            Assert.False(runnerStarted);
            Assert.False(vm.ApRunning);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Runner_session_ready_log_marks_connected_and_updates_title_and_status()
    {
        var root = Path.Combine(Path.GetTempPath(), "dinorand_ap_ready_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        File.WriteAllBytes(Path.Combine(root, "Data", "st102.dat"), new byte[16]);
        using var started = new ManualResetEventSlim();
        try
        {
            var vm = new MainWindowViewModel(new SavingPicker(null), new AcceptingDialogs(), () => null!,
                new AppSettings(), (h, s, p, i, o, log, error, ct) =>
                {
                    log("connected: seed TEST, slot #1 'Player', goal room 060d");
                    log("session-ready: seed TEST; placement installed; starting DINO.exe poll");
                    started.Set();
                    ct.WaitHandle.WaitOne();
                    return 0;
                }, a => a());
            vm.GamePath = root;
            vm.ApSlot = "Player";

            vm.ApToggleConnectCommand.Execute(null);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

            Assert.True(vm.ApConnected);
            Assert.Contains("Connected", vm.WindowTitle);
            Assert.Equal("Connected — ready to poll.", vm.ApStatusText);

            vm.ApToggleConnectCommand.Execute(null);
            await vm.ApSessionTask;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("error: server world logic_version 3 is obsolete")]
    [InlineData("error: slot_data has no placement — preflight failed")]
    public async Task Early_auth_log_does_not_mark_the_session_connected_when_validation_or_preflight_fails(
        string failure)
    {
        var root = Path.Combine(Path.GetTempPath(), "dinorand_ap_lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        File.WriteAllBytes(Path.Combine(root, "Data", "st102.dat"), new byte[16]);
        using var started = new ManualResetEventSlim();
        try
        {
            var vm = new MainWindowViewModel(new SavingPicker(null), new AcceptingDialogs(), () => null!,
                new AppSettings(), (h, s, p, i, o, log, error, ct) =>
                {
                    log("connected: seed TEST, slot #1 'Player', goal room 060d");
                    log(failure);
                    started.Set();
                    ct.WaitHandle.WaitOne();
                    return 1; // auth succeeded, but install/preflight did not complete
                }, a => a());
            vm.GamePath = root;
            vm.ApSlot = "Player";

            vm.ApToggleConnectCommand.Execute(null);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(vm.ApConnected);
            Assert.Contains("Connecting", vm.WindowTitle);
            vm.ApToggleConnectCommand.Execute(null);
            await vm.ApSessionTask;

            Assert.False(vm.ApConnected);
            Assert.DoesNotContain("Connected", vm.WindowTitle);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class SavingPicker : IFilePicker
    {
        private readonly string? _path;
        public SaveFileRequest Request { get; private set; } = null!;

        public SavingPicker(string? path) => _path = path;
        public Task<string?> PickFileAsync(FilePickerRequest request) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(FolderPickerRequest request) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(SaveFileRequest request)
        {
            Request = request;
            return Task.FromResult(_path);
        }
    }

    private sealed class AcceptingDialogs : IDialogs
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;
        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    }

    private sealed class GateDialogs : IDialogs
    {
        private readonly bool _result;
        public bool ConfirmCalled { get; private set; }
        public GateDialogs(bool result) => _result = result;
        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmCalled = true;
            return Task.FromResult(_result);
        }
        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;
        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    }
}
