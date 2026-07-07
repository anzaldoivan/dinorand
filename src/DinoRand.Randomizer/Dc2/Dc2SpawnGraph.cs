using System.Text.Json;

namespace DinoRand.Randomizer.Dc2;

/// <summary>
/// Loads <c>data/dc2/spawn-graph.json</c> (tools/dc2_re/edit_spawn.py) into per-room
/// <see cref="Dc2SpawnRecord"/> lists for the cross-species planner. Each room key is the 3-char
/// <c>st_id</c> ("202", "40A"); per spawn we take the TYPE operand (value/mode/blob-offset) and SLOT.
/// </summary>
public sealed class Dc2SpawnGraph
{
    /// <summary>Embedded-resource logical name (see DinoRand.Randomizer.csproj).</summary>
    public const string DefaultResourceName = "DinoRand.Randomizer.Data.dc2.spawn-graph.json";

    private readonly IReadOnlyDictionary<string, IReadOnlyList<Dc2SpawnRecord>> _byRoom;

    private Dc2SpawnGraph(IReadOnlyDictionary<string, IReadOnlyList<Dc2SpawnRecord>> byRoom) =>
        _byRoom = byRoom;

    /// <summary>The spawns for a room key ("202"), or <c>null</c> if the graph has no such room.</summary>
    public IReadOnlyList<Dc2SpawnRecord>? ForRoom(string roomKey) => _byRoom.GetValueOrDefault(roomKey);

    /// <summary>The <c>st_id</c> key for a stage/room, e.g. (2,2)→"202", (4,10)→"40A".</summary>
    public static string RoomKey(int stage, int room) => $"{stage:X}{room:X2}";

    public static Dc2SpawnGraph LoadEmbedded()
    {
        var asm = typeof(Dc2SpawnGraph).Assembly;
        using var s = asm.GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException($"embedded resource '{DefaultResourceName}' not found");
        return Parse(s);
    }

    public static Dc2SpawnGraph Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var map = new Dictionary<string, IReadOnlyList<Dc2SpawnRecord>>();
        foreach (var room in doc.RootElement.GetProperty("rooms").EnumerateObject())
        {
            var list = new List<Dc2SpawnRecord>();
            if (room.Value.ValueKind == JsonValueKind.Object &&
                room.Value.TryGetProperty("spawns", out var spawns))
            {
                foreach (var sp in spawns.EnumerateArray())
                {
                    if (!sp.TryGetProperty("fields", out var f) ||
                        !f.TryGetProperty("TYPE", out var t)) continue;
                    int type = t.GetProperty("value").GetInt32();
                    int mode = t.GetProperty("mode").GetInt32();
                    int off  = t.GetProperty("value_off").GetInt32();
                    int slot = f.TryGetProperty("SLOT", out var sl) && sl.TryGetProperty("value", out var sv)
                        ? sv.GetInt32() : -1;
                    int variant = -1, variantMode = -1, variantOff = -1;
                    if (f.TryGetProperty("VARIANT", out var v))
                    {
                        variant = v.GetProperty("value").GetInt32();
                        variantMode = v.GetProperty("mode").GetInt32();
                        variantOff = v.GetProperty("value_off").GetInt32();
                    }
                    list.Add(new Dc2SpawnRecord(type, mode, off, slot, variant, variantMode, variantOff));
                }
            }
            map[room.Name] = list;
        }
        return new Dc2SpawnGraph(map);
    }
}
