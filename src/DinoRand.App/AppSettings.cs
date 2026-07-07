#nullable enable
using System.IO;
using System.Text.Json;

namespace DinoRand.App;

/// <summary>
/// Persisted UI state at <c>%APPDATA%\DinoRand\settings.json</c>. All load/save is
/// best-effort: a missing or corrupt file silently falls back to defaults so the app
/// always starts.
/// </summary>
public sealed class AppSettings
{
    public string? GamePath { get; set; }
    public string? LastSeed { get; set; }   // AppSeed.ToString()

    /// <summary>
    /// Phase 4 voice rando: filesystem root of the BioRand donor datapacks (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md
    /// §12.1). Feeds <c>RandomizerConfig.VoicePacksRoot</c>. Persisted but not yet surfaced in the GUI —
    /// the folder picker lands with the gate flip (§12.5), since the voice pass is gated and emits nothing
    /// until then. <c>null</c> ⇒ no donor source (the gated pass stays a no-op anyway).
    /// </summary>
    public string? VoicePacksRoot { get; set; }

    /// <summary>Phase 4 voice rando: the "Randomize Cutscene Voices" master toggle. Not in the shared seed,
    /// so persisted here. Feeds <c>RandomizerConfig.RandomizeVoices</c> (docs/decisions/dc1/voice/VOICE-UI-PLAN.md §10).</summary>
    public bool RandomizeCutsceneVoices { get; set; }

    /// <summary>Phase 4 voice rando: "Allow voices from other games". Feeds
    /// <c>RandomizerConfig.IncludeCrossGameVoices</c>.</summary>
    public bool IncludeCrossGameVoices { get; set; }

    /// <summary>
    /// Phase 4 voice rando: per-character donor pins (target actor name ⇒ donor actor name), from the App's
    /// per-character dropdowns. A target absent (or "Random") draws a random donor each seed. Feeds
    /// <c>RandomizerConfig.VoiceDonors</c> (docs/decisions/dc1/voice/VOICE-UI-PLAN.md §10).
    /// </summary>
    public Dictionary<string, string>? VoiceDonors { get; set; }

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DinoRand", "settings.json");

    private static JsonSerializerOptions JsonOpts => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(SettingsPath), JsonOpts) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { }
    }
}
