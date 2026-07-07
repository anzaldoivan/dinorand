using System.Text.Json;

namespace DinoRand.Randomizer.Spoiler;

/// <summary>
/// DC1 item id → display name, from the embedded <c>data/dc1/items.json</c> <c>allItems</c>
/// table (the full 116-id list; the engine's <c>ItemPool</c> keeps only the shuffleable subset).
/// Display-only decoration for the spoiler tables — unknown ids fall back to hex, never a throw.
/// </summary>
public static class Dc1ItemNames
{
    public const string DefaultResourceName = "DinoRand.Randomizer.Data.dc1.items.json";

    private static readonly Lazy<IReadOnlyDictionary<int, string>> ById = new(Load);

    public static string NameOf(int itemId)
        => ById.Value.TryGetValue(itemId, out var name) ? name : $"0x{itemId:X2}";

    private static IReadOnlyDictionary<int, string> Load()
    {
        var asm = typeof(Dc1ItemNames).Assembly;
        using var s = asm.GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException($"embedded resource '{DefaultResourceName}' not found");
        using var doc = JsonDocument.Parse(s);
        var map = new Dictionary<int, string>();
        foreach (var item in doc.RootElement.GetProperty("allItems").EnumerateArray())
        {
            int id = Convert.ToInt32(item.GetProperty("id").GetString()!, 16);
            // The JSON stores literal quotes doubled ("" Model PA3 "") — normalize for display.
            map[id] = item.GetProperty("name").GetString()!.Replace("\"\"", "\"");
        }
        return map;
    }
}

/// <summary>
/// DC1 room code → display name, from the already-embedded <c>data/dc1/map.json</c>
/// (<c>rooms.&lt;code&gt;.name</c>). Null for a room the map doesn't cover.
/// </summary>
public static class Dc1RoomNames
{
    private static readonly Lazy<IReadOnlyDictionary<int, string>> ByCode = new(Load);

    public static string? NameOf(int roomCode)
        => ByCode.Value.TryGetValue(roomCode, out var name) ? name : null;

    /// <summary>"0x10B Locker Room" when named, else "0x10B".</summary>
    public static string Describe(int roomCode)
        => NameOf(roomCode) is { } n ? $"0x{roomCode:X3} {n}" : $"0x{roomCode:X3}";

    private static IReadOnlyDictionary<int, string> Load()
    {
        var asm = typeof(Dc1RoomNames).Assembly;
        using var s = asm.GetManifestResourceStream(Maps.DoorMap.DefaultResourceName)
            ?? throw new InvalidOperationException(
                $"embedded resource '{Maps.DoorMap.DefaultResourceName}' not found");
        using var doc = JsonDocument.Parse(s);
        var map = new Dictionary<int, string>();
        foreach (var room in doc.RootElement.GetProperty("rooms").EnumerateObject())
            if (room.Value.TryGetProperty("name", out var name) && name.GetString() is { } n
                && int.TryParse(room.Name, System.Globalization.NumberStyles.HexNumber, null, out int code))
                map[code] = n;
        return map;
    }
}
