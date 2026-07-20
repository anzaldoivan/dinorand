#nullable enable
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DinoRand.App;

/// <summary>
/// Per-game UI state for one game (install path / last seed). Each selectable game
/// (<c>GameDefinition.Id</c>) keeps its own slice so switching games never carries another game's
/// path or seed — mirrors BioRand's <c>GamePath1..CV</c> + <c>Seed1..CV</c> per-game state
/// (docs/decisions/cross/GAME-SELECTOR-PLAN.md). Voice donor settings are intentionally <b>not</b> here — the BioRand
/// datapacks are cross-game compatible, so they live shared on <see cref="AppSettings"/>.
/// </summary>
public sealed class GameSettings
{
    public string? GamePath { get; set; }
    public string? LastSeed { get; set; }   // AppSeed.ToString()
}

/// <summary>
/// Persisted UI state at <c>%APPDATA%\DinoRand\settings.json</c>. All load/save is
/// best-effort: a missing or corrupt file silently falls back to defaults so the app
/// always starts.
/// </summary>
/// <remarks>Framework-agnostic (System.Text.Json + Environment.SpecialFolder only) — the serialization
/// surface (<see cref="FromJson"/>/<see cref="ToJson"/>/<see cref="ForGame"/>) is Avalonia-free and unit
/// tested. Install/seed are per-game (<see cref="GameSettings"/> slices keyed by game id); voice donor
/// settings are shared across games (cross-game datapacks). The flat <see cref="GamePath"/>/
/// <see cref="LastSeed"/> are kept only to migrate a pre-per-game settings file on first load.</remarks>
public sealed class AppSettings
{
    /// <summary>Last-selected game id (<c>GameDefinition.Id</c>, e.g. "dc1"/"dc2"), restored on launch
    /// via <c>GameCatalog.FromId</c>. <c>null</c>/unknown ⇒ default (DC1).</summary>
    public string? SelectedGameId { get; set; }

    /// <summary>Per-game UI state, keyed by <c>GameDefinition.Id</c>. Use <see cref="ForGame"/> to read
    /// or create a game's slice.</summary>
    public Dictionary<string, GameSettings> Games { get; set; } = new();

    // --- Shared across games. The BioRand voice datapacks are cross-game compatible, so the pack folder,
    //     the cross-game flag and the donor pins are global — set once, used by every game. ---

    /// <summary>Voice rando: filesystem root of the BioRand donor datapacks. Shared across games. Feeds
    /// <c>RandomizerConfig.VoicePacksRoot</c> (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §12.1).</summary>
    public string? VoicePacksRoot { get; set; }

    /// <summary>BGM import: filesystem root of the BGM datapacks (folder = mood tag). Feeds
    /// <c>RandomizerConfig.BgmPacksRoot</c> (docs/decisions/cross/BGM-RANDO-PLAN.md).</summary>
    public string? BgmPacksRoot { get; set; }

    /// <summary>Voice rando: the "Randomize Cutscene Voices" master toggle (shared)
    /// (<c>RandomizerConfig.RandomizeVoices</c>).</summary>
    public bool RandomizeCutsceneVoices { get; set; }

    /// <summary>Voice rando: "Allow voices from other games" (shared)
    /// (<c>RandomizerConfig.IncludeCrossGameVoices</c>).</summary>
    public bool IncludeCrossGameVoices { get; set; }

    /// <summary>Archipelago connect tab: last-used <c>host:port</c> and slot name (AP-CLIENT-PLAN.md
    /// increment 2). Shared, not per-game — AP support is DC1-only today. The password is
    /// deliberately NOT persisted.</summary>
    public string? ApHostPort { get; set; }
    public string? ApSlot { get; set; }

    /// <summary>Voice rando: per-character donor pins (target actor ⇒ donor actor), shared across games —
    /// targets are distinct character names per game so a single dict holds all of them
    /// (<c>RandomizerConfig.VoiceDonors</c>).</summary>
    public Dictionary<string, string>? VoiceDonors { get; set; }

    // --- Legacy flat per-game fields (pre per-game). Present only in old settings files; folded into the
    //     selected game's slice by MigrateLegacy() on load, then written back as null. ---
    public string? GamePath { get; set; }
    public string? LastSeed { get; set; }

    /// <summary>The settings slice for a game id, created empty on first access. A game with no saved
    /// slice starts blank (e.g. DC2 does not inherit DC1's install path).</summary>
    public GameSettings ForGame(string id)
    {
        if (!Games.TryGetValue(id, out var slice))
        {
            slice = new GameSettings();
            Games[id] = slice;
        }
        return slice;
    }

    /// <summary>Fold a pre-per-game settings file (flat <see cref="GamePath"/>/<see cref="LastSeed"/>) into
    /// the selected game's slice (or DC1 if none), then clear them so they don't re-migrate. Voice fields
    /// were already top-level and stay shared — nothing to migrate there.</summary>
    private void MigrateLegacy()
    {
        bool hasLegacy = GamePath is not null || LastSeed is not null;

        if (hasLegacy && Games.Count == 0)
        {
            var slice = ForGame(SelectedGameId ?? "dc1");
            slice.GamePath = GamePath;
            slice.LastSeed = LastSeed;
        }

        GamePath = null;
        LastSeed = null;
    }

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DinoRand", "settings.json");

    private static JsonSerializerOptions JsonOpts => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Deserialize settings from JSON and migrate any legacy flat fields. Disk-free, so it
    /// (with <see cref="ToJson"/>) is the unit-testable round-trip surface.</summary>
    public static AppSettings FromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new();
        settings.MigrateLegacy();
        return settings;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return FromJson(File.ReadAllText(SettingsPath));
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, ToJson());
        }
        catch { }
    }
}
