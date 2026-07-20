using System.Text.Json;
using System.Text.Json.Serialization;

namespace DinoRand.ApClient;

/// <summary>
/// Per-slot persisted client state (AP-CLIENT-PLAN.md §1): lives in a JSON next to the mod
/// output. On reconnect, a seed-name mismatch resets everything (different multiworld); the
/// server stays authoritative for checked locations — <see cref="SentLocationIds"/> only
/// suppresses duplicate sends, and all checked ids are re-sent on connect anyway.
/// </summary>
public sealed class ApSlotState
{
    [JsonPropertyName("seedName")] public string SeedName { get; set; } = "";
    [JsonPropertyName("slotName")] public string SlotName { get; set; } = "";
    /// <summary>Highest ReceivedItems index whose grant has been applied (consumable
    /// once-only bookkeeping). -1 = nothing applied.</summary>
    [JsonPropertyName("appliedThrough")] public int AppliedThrough { get; set; } = -1;
    [JsonPropertyName("sentLocationIds")] public List<long> SentLocationIds { get; set; } = new();

    public static ApSlotState LoadOrNew(string path, string seedName, string slotName)
    {
        ApSlotState? s = null;
        if (File.Exists(path))
        {
            try { s = JsonSerializer.Deserialize<ApSlotState>(File.ReadAllText(path)); }
            catch (JsonException) { /* corrupt state — start fresh; server is authoritative */ }
        }
        if (s is null || s.SeedName != seedName || s.SlotName != slotName)
            s = new ApSlotState { SeedName = seedName, SlotName = slotName };
        return s;
    }

    /// <summary>
    /// Crash-safe write: serialize to a sibling temp file, then swap it in atomically. A torn
    /// state file would fail <see cref="LoadOrNew"/>'s parse and reset <see cref="AppliedThrough"/>
    /// to -1, which re-grants already-delivered consumables — so the window where the file is
    /// half-written must not exist. (<c>File.Move(overwrite: true)</c> is NOT guaranteed atomic;
    /// <c>File.Replace</c> maps to Win32 <c>ReplaceFile</c> but requires an existing target, hence
    /// the first-write fallback.)
    /// </summary>
    public void Save(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

        var tmp = full + ".tmp";   // same directory ⇒ same volume ⇒ the swap can be atomic
        File.WriteAllText(tmp, json);
        if (File.Exists(full))
            File.Replace(tmp, full, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(tmp, full);
    }

    /// <summary>Conventional state-file path for a slot, inside the mod output dir.</summary>
    public static string PathFor(string outDir, string slotName)
    {
        var safe = string.Concat(slotName.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        return Path.Combine(outDir, $"ap_state_{safe}.json");
    }
}
