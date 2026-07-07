using System.Globalization;
using System.Text.Json;

namespace DinoRand.Randomizer.Dc2;

/// <summary>One armed (or preload-referenced) wave descriptor in a room's SCD blob (K65):
/// <paramref name="TypeOff"/> is the blob offset of the ONE-BYTE species lever (<c>desc+1</c> —
/// the category op-0x4f/op-0x23 load AND the wave spawner's <c>[actor+0x58]</c>), live-validated
/// on ST105 (Gates W1/W2: Oviraptor + Tyrannosaurus). <paramref name="Armed"/> is false for a
/// record only referenced by an op-0x23 preload (edited anyway to keep residency coherent).
/// <paramref name="VariantOff"/>/<paramref name="Variant"/> = the <c>desc+4</c> variant byte
/// (raptor tier pair-table index offset, docs/reference/dc2/enemies/RAPTOR-TIER-RE.md §2); -1 when the scanner
/// did not record one.</summary>
public sealed record Dc2WaveDescriptor(
    int DescOff, int TypeOff, int NativeType, bool Armed,
    int VariantOff = -1, int Variant = -1);

/// <summary>A dormant generic TYPE-0x10 op-0x1a spawn whose MODEL_BASE global resolves to a real
/// E-file base (<paramref name="MbBase"/>) — e.g. ST105's zone-3 raptor ambush. A wave swap removes
/// that base's residency, so these must be NORMALIZED to the donor (<c>Dc2GenericSpawnNormalize</c>,
/// K64/K65): TYPE word → donor, MODEL_BASE + HP pushes → literal 0 (mode byte at <c>push+1</c>,
/// value word at <c>push+2</c>), turning them into the K59/K61-proven self-loading hardcoded form.</summary>
public sealed record Dc2GenericCreatureSpawn(
    int TypePushOff, int MbPushOff, int HpPushOff, int HpPushMode, int MbBase);

/// <summary>A room's wave-spawn data: the descriptors and the generic creature spawns tied to them.</summary>
public sealed record Dc2WaveRoom(
    IReadOnlyList<Dc2WaveDescriptor> Descriptors,
    IReadOnlyList<Dc2GenericCreatureSpawn> GenericCreatureSpawns);

/// <summary>
/// Loads <c>data/dc2/wave-descriptors.json</c> (tools/dc2_re/scan_wave_descriptors.py) — the K65
/// dataset behind the NATIVE wave-spawn species lever. Companion of <see cref="Dc2SpawnGraph"/>
/// (which pins the op-0x1a literals); together they cover both enemy-creation paths.
/// </summary>
public sealed class Dc2WaveTable
{
    /// <summary>Embedded-resource logical name (see DinoRand.Randomizer.csproj).</summary>
    public const string DefaultResourceName = "DinoRand.Randomizer.Data.dc2.wave-descriptors.json";

    private readonly IReadOnlyDictionary<string, Dc2WaveRoom> _byRoom;

    private Dc2WaveTable(IReadOnlyDictionary<string, Dc2WaveRoom> byRoom) => _byRoom = byRoom;

    /// <summary>The wave data for a room key ("105"), or <c>null</c> if the room arms no waves.</summary>
    public Dc2WaveRoom? ForRoom(string roomKey) => _byRoom.GetValueOrDefault(roomKey);

    public IReadOnlyCollection<string> Rooms => (IReadOnlyCollection<string>)_byRoom.Keys;

    public static Dc2WaveTable LoadEmbedded()
    {
        var asm = typeof(Dc2WaveTable).Assembly;
        using var s = asm.GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException($"embedded resource '{DefaultResourceName}' not found");
        return Parse(s);
    }

    public static Dc2WaveTable Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var map = new Dictionary<string, Dc2WaveRoom>();
        foreach (var room in doc.RootElement.GetProperty("rooms").EnumerateObject())
        {
            var descs = new List<Dc2WaveDescriptor>();
            foreach (var w in room.Value.GetProperty("wave_descriptors").EnumerateArray())
            {
                descs.Add(new Dc2WaveDescriptor(
                    Hex(w.GetProperty("desc_off")),
                    Hex(w.GetProperty("type_off")),
                    w.GetProperty("native_type").GetInt32(),
                    Armed: w.GetProperty("arm_sites").GetArrayLength() > 0,
                    VariantOff: w.TryGetProperty("variant_off", out var vo) ? Hex(vo) : -1,
                    Variant: w.TryGetProperty("variant", out var v) ? v.GetInt32() : -1));
            }
            var generics = new List<Dc2GenericCreatureSpawn>();
            foreach (var g in room.Value.GetProperty("generic_creature_spawns").EnumerateArray())
            {
                generics.Add(new Dc2GenericCreatureSpawn(
                    Hex(g.GetProperty("type_push_off")),
                    Hex(g.GetProperty("mb_push_off")),
                    Hex(g.GetProperty("hp_push_off")),
                    g.GetProperty("hp_push_mode").GetInt32(),
                    Hex(g.GetProperty("mb_base"))));
            }
            map[room.Name] = new Dc2WaveRoom(descs, generics);
        }
        return new Dc2WaveTable(map);
    }

    /// <summary>Offsets are emitted as "0x…" strings by the scanner.</summary>
    private static int Hex(JsonElement e) =>
        int.Parse(e.GetString()!.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
