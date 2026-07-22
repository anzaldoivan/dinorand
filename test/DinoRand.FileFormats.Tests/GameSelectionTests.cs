using System;
using System.IO;
using System.Linq;
using DinoRand.App;
using DinoRand.App.Services;
using DinoRand.Randomizer.Definitions;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The backup folder lives under the resolved <c>Data\</c> dir (<c>&lt;dataDir&gt;\.dinorand_backup</c>),
/// so backups only stay separated between games if the data dir is resolved with the <b>selected</b>
/// game's resolver — not always DC1's. DC1 anchors to <c>english\Data</c>, the DC2 Rebirth basis to
/// <c>rebirth\Data</c>; a DC2 install that hardcodes DC1's resolver would back up the wrong tree.
/// </summary>
public class DataDirResolutionTests
{
    [Fact]
    public void DataDir_resolves_per_selected_game_so_backups_do_not_collide()
    {
        var root = Path.Combine(Path.GetTempPath(), "dinorand_ddir_" + Guid.NewGuid().ToString("N"));
        try
        {
            var englishData = Path.Combine(root, "english", "Data");
            var rebirthData = Path.Combine(root, "rebirth", "Data");
            Directory.CreateDirectory(englishData);
            Directory.CreateDirectory(rebirthData);
            // Each game's room-file glob differs in case (DC1 "st*.dat", DC2 "ST*.DAT"); match each so the
            // test holds on a case-sensitive filesystem too.
            File.WriteAllBytes(Path.Combine(englishData, "st100.dat"), new byte[16]);
            File.WriteAllBytes(Path.Combine(rebirthData, "ST100.DAT"), new byte[16]);

            var dc1Dir = MainWindowViewModel.ResolveDataDir(new DinoCrisis1(), root);
            var dc2Dir = MainWindowViewModel.ResolveDataDir(new DinoCrisis2(), root);

            Assert.Equal(englishData, dc1Dir);   // DC1 anchors to english\Data
            Assert.Equal(rebirthData, dc2Dir);   // DC2 anchors to rebirth\Data
            Assert.NotEqual(dc1Dir, dc2Dir);      // ⇒ each game's .dinorand_backup is under its own Data
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Drm_check_targets_the_exe_beside_the_resolved_data_dir_not_an_arbitrary_copy()
    {
        // DC2 ships a Dino2.exe in english/, japanese/ AND rebirth/. The DRM check must inspect the one
        // matching the RESOLVED data dir (rebirth for DC2), not whichever a recursive search hits first.
        var root = Path.Combine(Path.GetTempPath(), "dinorand_exe_" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var lang in new[] { "english", "japanese", "rebirth" })
            {
                var data = Path.Combine(root, lang, "Data");
                Directory.CreateDirectory(data);
                File.WriteAllBytes(Path.Combine(data, "ST100.DAT"), new byte[16]);
                File.WriteAllBytes(Path.Combine(root, lang, "Dino2.exe"), new byte[16]);
            }

            var dataDir = MainWindowViewModel.ResolveDataDir(new DinoCrisis2(), root);   // rebirth\Data
            var exe = MainWindowViewModel.LocateExeForDataDir(dataDir, "Dino2.exe");

            Assert.Equal(Path.Combine(root, "rebirth", "Dino2.exe"), exe);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LocateExeForDataDir_is_null_when_no_exe_beside_the_data_dir()
    {
        var root = Path.Combine(Path.GetTempPath(), "dinorand_noexe_" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = Path.Combine(root, "rebirth", "Data");
            Directory.CreateDirectory(data);   // Data present, but no Dino2.exe beside it
            Assert.Null(MainWindowViewModel.LocateExeForDataDir(data, "Dino2.exe"));
            Assert.Null(MainWindowViewModel.LocateExeForDataDir("", "Dino2.exe"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>
/// The "Browse for game executable" picker must target the SELECTED game's executable — DC1 DINO.exe,
/// DC2 Dino2.exe — so selecting DC2 doesn't keep asking for DINO.exe. Tests the pure request builder the
/// Browse command consumes (the picker call itself is UI).
/// </summary>
public class ExecutablePickerTests
{
    [Fact]
    public void Picker_targets_dc2_executable_when_dc2_is_selected()
    {
        var req = MainWindowViewModel.BuildExecutablePickerRequest(new DinoCrisis2(), null);

        Assert.Contains("Dino2.exe", req.Title);
        Assert.Contains(req.FileTypes!, f => f.Patterns.Contains("Dino2.exe"));
        Assert.DoesNotContain(req.FileTypes!, f => f.Patterns.Contains("DINO.exe"));
    }

    [Fact]
    public void Picker_targets_dc1_executable_for_dc1()
    {
        var req = MainWindowViewModel.BuildExecutablePickerRequest(new DinoCrisis1(), null);

        Assert.Contains("DINO.exe", req.Title);
        Assert.Contains(req.FileTypes!, f => f.Patterns.Contains("DINO.exe"));
    }

    [Fact]
    public void Picker_keeps_the_generic_exe_and_all_files_fallbacks()
    {
        var req = MainWindowViewModel.BuildExecutablePickerRequest(new DinoCrisis1(), null);

        Assert.Contains(req.FileTypes!, f => f.Patterns.Contains("*.exe"));
        Assert.Contains(req.FileTypes!, f => f.Patterns.Contains("*"));
    }
}

/// <summary>
/// Game-selector seam (docs/GAME-SELECTOR-PLAN.md §9). These exercise the data-first routing the
/// Avalonia ComboBox binds to — the registry of games, the default selection, the per-game
/// "pipeline ready" flag that gates DC2, and id→definition lookup used for routing/restore. No
/// Avalonia dependency: the rule lives in DinoRand.Randomizer so it can't drift from the UI.
/// </summary>
public class GameSelectionTests
{
    [Fact]
    public void Default_is_dc1()
    {
        var def = GameCatalog.Default;
        Assert.Equal("dc1", def.Id);
        Assert.True(def.IsImplemented);
    }

    [Fact]
    public void All_contains_dc1_then_dc2()
    {
        var ids = GameCatalog.All.Select(g => g.Id).ToArray();
        Assert.Equal(new[] { "dc1", "dc2" }, ids);
    }

    [Fact]
    public void Both_games_are_implemented()
    {
        // DC2 flipped to implemented once its cross-species enemy randomizer became generate/installable
        // from the frontend (docs/dc2/CROSS-SPECIES-RANDO-PLAN.md). It still supports only GameFeature.Enemies
        // — the other option groups stay greyed — but it is no longer fenced out of Generate/Install.
        Assert.True(new DinoCrisis1().IsImplemented);
        Assert.True(new DinoCrisis2().IsImplemented);
    }

    [Fact]
    public void Default_is_the_first_implemented_game()
    {
        // The default must be a playable game, not merely the first list entry — so that adding
        // an unfinished game ahead of DC1 later can't silently make the UI default to a stub.
        Assert.True(GameCatalog.Default.IsImplemented);
    }

    [Theory]
    [InlineData("dc1", typeof(DinoCrisis1))]
    [InlineData("dc2", typeof(DinoCrisis2))]
    public void FromId_routes_to_the_right_definition(string id, System.Type expected)
    {
        var def = GameCatalog.FromId(id);
        Assert.NotNull(def);
        Assert.IsType(expected, def);
    }

    [Fact]
    public void FromId_is_null_for_unknown_game()
    {
        Assert.Null(GameCatalog.FromId("dc99"));
    }

    [Theory]
    [InlineData("dc1", "DINO.exe")]
    [InlineData("dc2", "Dino2.exe")]
    public void Each_game_declares_its_executable_name(string id, string exe)
    {
        // Drives the DRM/validation check at the right executable: DC1 = DINO.exe, DC2 = Dino2.exe.
        Assert.Equal(exe, GameCatalog.FromId(id)!.ExecutableName);
    }
}

/// <summary>
/// Per-game option capability (docs/DC2-OPTION-GATING-PLAN.md): each game declares which randomizer
/// features it supports, so the UI can disable options a game doesn't implement yet. DC1 supports the
/// full set; the DC2 stub supports none. Pure seam — no Avalonia.
/// </summary>
public class GameFeatureSupportTests
{
    [Fact]
    public void Dc1_supports_every_feature_except_player_model()
    {
        // Guard: adding a GameFeature forces DC1 to declare it (DC1 is full-parity today) — with the one
        // deliberate exception of PlayerModel: the DC1 costume swap is measured feasible
        // (docs/HUMANOID-MODEL-SWAP-FEASIBILITY.md) but its UI/pass is a separate decision, so DC1 stays
        // opted out (docs/dc2/DC2-PLAYER-SWAP-PARITY-PLAN.md §3.1).
        var dc1 = new DinoCrisis1();
        foreach (GameFeature f in System.Enum.GetValues(typeof(GameFeature)))
        {
            if (f == GameFeature.PlayerModel)
                Assert.False(dc1.Supports(f), "DinoCrisis1 does not wire the player model swap yet");
            else
                Assert.True(dc1.Supports(f), $"DinoCrisis1 should support {f}");
        }
    }

    [Fact]
    public void Dc2_supports_items_keys_enemies_player_model_and_voices()
    {
        // DC2's shipped features include the fixture-backed item/key writer and progression pass,
        // the cross-species enemy randomizer (the file-edit TYPE swap,
        // docs/dc2/CROSS-SPECIES-RANDO-PLAN.md), the character-skin swap (PlayerModel — Dylan
        // renders as Gail/Rick via their engine-native WP graft files + the WP-gate exe patch,
        // docs/dc2/DC2-CHARACTER-SKIN-SWAP-PLAN.md; replaces the withdrawn whole-file swap of
        // docs/dc2/DC2-PLAYER-SWAP-FIRE-CRASH-RCA.md), and Voices (the 2026-07-05 folder-curation
        // cast labels in data/dc2/voice.json expose the voice UI; emission itself still waits on
        // Dc2VoiceManifestLayout.IsDecoded). Other option groups stay fenced until their DC2
        // contracts land.
        var dc2 = new DinoCrisis2();
        Assert.True(dc2.Supports(GameFeature.Items));
        Assert.True(dc2.Supports(GameFeature.KeyItems));
        Assert.True(dc2.Supports(GameFeature.Enemies));
        Assert.True(dc2.Supports(GameFeature.PlayerModel));
        Assert.True(dc2.Supports(GameFeature.Voices));
        foreach (GameFeature f in System.Enum.GetValues(typeof(GameFeature)))
            if (f is not (GameFeature.Items or GameFeature.KeyItems or GameFeature.Enemies
                or GameFeature.PlayerModel or GameFeature.Voices))
                Assert.False(dc2.Supports(f), $"DinoCrisis2 should not support {f} yet");
    }

    [Fact]
    public void Supports_reflects_SupportedFeatures_membership()
    {
        var dc1 = new DinoCrisis1();
        Assert.Equal(dc1.SupportedFeatures.Contains(GameFeature.Items), dc1.Supports(GameFeature.Items));
        Assert.Equal(dc1.SupportedFeatures.Contains(GameFeature.Voices), dc1.Supports(GameFeature.Voices));
    }
}

/// <summary>
/// Per-game settings isolation (docs/GAME-SELECTOR-PLAN.md): each game keeps its own install path /
/// seed / voice prefs (BioRand's GamePath1..CV + Seed1..CV model), so selecting DC2 must not carry
/// DC1's saved path. Exercised through AppSettings' Avalonia-free POCO surface — no disk, no UI.
/// </summary>
public class GameSettingsIsolationTests
{
    [Fact]
    public void ForGame_returns_independent_slices()
    {
        var s = new AppSettings();
        s.ForGame("dc1").GamePath = @"C:\Games\dc1";
        s.ForGame("dc2").GamePath = @"D:\Games\dc2";

        Assert.Equal(@"C:\Games\dc1", s.ForGame("dc1").GamePath);
        Assert.Equal(@"D:\Games\dc2", s.ForGame("dc2").GamePath);
    }

    [Fact]
    public void Selecting_a_new_game_does_not_inherit_another_games_path()
    {
        // The core ask: configure DC1, then DC2's slice must start blank — not DC1's path.
        var s = new AppSettings();
        s.ForGame("dc1").GamePath = @"C:\Games\dc1";

        Assert.Null(s.ForGame("dc2").GamePath);
    }

    [Fact]
    public void ForGame_returns_the_same_slice_instance_per_id()
    {
        var s = new AppSettings();
        Assert.Same(s.ForGame("dc1"), s.ForGame("dc1"));
    }

    [Fact]
    public void Roundtrip_preserves_both_game_slices()
    {
        var s = new AppSettings { SelectedGameId = "dc2" };
        s.ForGame("dc1").GamePath = @"C:\Games\dc1";
        s.ForGame("dc1").LastSeed = "DINO-aaa";
        s.ForGame("dc2").GamePath = @"D:\Games\dc2";
        s.ForGame("dc2").LastSeed = "DINO-bbb";

        var back = AppSettings.FromJson(s.ToJson());

        Assert.Equal("dc2", back.SelectedGameId);
        Assert.Equal(@"C:\Games\dc1", back.ForGame("dc1").GamePath);
        Assert.Equal("DINO-aaa", back.ForGame("dc1").LastSeed);
        Assert.Equal(@"D:\Games\dc2", back.ForGame("dc2").GamePath);
        Assert.Equal("DINO-bbb", back.ForGame("dc2").LastSeed);
    }

    [Fact]
    public void Legacy_flat_settings_migrate_into_the_selected_game_slice()
    {
        // A pre-per-game settings.json: flat gamePath/lastSeed at the root, with a selected game.
        var legacy = """
            { "selectedGameId": "dc2", "gamePath": "C:\\Old\\Path", "lastSeed": "DINO-old" }
            """;

        var s = AppSettings.FromJson(legacy);

        Assert.Equal(@"C:\Old\Path", s.ForGame("dc2").GamePath);
        Assert.Equal("DINO-old", s.ForGame("dc2").LastSeed);
        Assert.Null(s.GamePath);   // legacy flat field cleared after migration
        Assert.Null(s.LastSeed);
    }

    [Fact]
    public void Legacy_flat_settings_without_selected_game_migrate_into_dc1()
    {
        var legacy = """{ "gamePath": "C:\\Old\\Path" }""";

        var s = AppSettings.FromJson(legacy);

        Assert.Equal(@"C:\Old\Path", s.ForGame("dc1").GamePath);
        Assert.Null(s.ForGame("dc2").GamePath);
    }

    [Fact]
    public void Voice_donor_settings_are_shared_not_per_game()
    {
        // BioRand voice datapacks are cross-game compatible, so the pack folder, cross-game flag and
        // donor pins live at the top level (shared), NOT in a per-game slice — set once, seen by both
        // games, and survive a round-trip.
        var s = new AppSettings { VoicePacksRoot = @"C:\packs", IncludeCrossGameVoices = true };
        s.VoiceDonors = new System.Collections.Generic.Dictionary<string, string> { ["regina"] = "leon.dc1" };

        var back = AppSettings.FromJson(s.ToJson());

        Assert.Equal(@"C:\packs", back.VoicePacksRoot);
        Assert.True(back.IncludeCrossGameVoices);
        Assert.Equal("leon.dc1", back.VoiceDonors!["regina"]);
        // And they are not duplicated onto a game slice.
        Assert.Empty(back.Games); // no per-game slice created just for voice
    }
}
