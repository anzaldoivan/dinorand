using System.Reflection;
using System.Text.Json;

namespace DinoRand.Randomizer.Maps;

/// <summary>Door-rando role of a room in the segmented connector (BioRand's
/// <c>DoorRandoCategory</c> subset — see ref Map.cs / DoorRandomiser.cs).</summary>
public enum RoomCategory
{
    /// <summary>An ordinary room placed inside a segment/area.</summary>
    Segment,
    /// <summary>A hub/gate room that joins segments (high decoded door degree).</summary>
    Bridge,
    /// <summary>A supply/save room the connector tries to reach early in a segment.</summary>
    Box,
    /// <summary>Never connected by the door pass (kept exactly as vanilla).</summary>
    Exclude,
}

/// <summary>
/// The hand-/auto-authored door-rando metadata for a game, a subset of BioRand's <c>Map</c>
/// schema (ref/classic/IntelOrca.Biohazard.BioRand/Map.cs:77-186) tailored to Dino Crisis 1:
/// per-room <see cref="RoomCategory"/>, the begin/end rooms, and the per-room one-way
/// <c>staticTargets</c> that the reciprocal-only shuffle must skip (plan §6, decision 3).
///
/// <para>The shipped map is <c>data/dc1/map.json</c>, embedded into this assembly and loaded by
/// <see cref="LoadDefault"/>. Rooms are keyed by their <c>SSRR</c> code (e.g. <c>0x010d</c>);
/// stage-variant duplicates (7/8/9), demo stages (A/B/C), and the unused ST60E shell (060E) are
/// intentionally absent, so resolving by id (never name) automatically scopes the pass to the 96
/// real story rooms.</para>
/// </summary>
public sealed class DoorMap
{
    /// <summary>Logical resource name of the embedded default map (see the .csproj EmbeddedResource).</summary>
    public const string DefaultResourceName = "DinoRand.Randomizer.Data.dc1.map.json";

    private readonly IReadOnlyDictionary<int, RoomEntry> _rooms;

    private DoorMap(int startCode, int endCode, IReadOnlyDictionary<int, RoomEntry> rooms)
    {
        StartCode = startCode;
        EndCode = endCode;
        _rooms = rooms;
    }

    /// <summary>Begin room (<c>SSRR</c>) — the connector's spanning-tree root.</summary>
    public int StartCode { get; }

    /// <summary>End room (<c>SSRR</c>) — must stay reachable for the connector to declare success.</summary>
    public int EndCode { get; }

    /// <summary>The room codes this map governs (the in-scope story rooms).</summary>
    public IReadOnlyCollection<int> RoomCodes => (IReadOnlyCollection<int>)_rooms.Keys;

    /// <summary>True if <paramref name="code"/> is a room the map governs (in scope for the pass).</summary>
    public bool Contains(int code) => _rooms.ContainsKey(code);

    /// <summary>Category of a room, or <see cref="RoomCategory.Exclude"/> if it is not in the map
    /// (so out-of-scope rooms — variants/demo — are never connected).</summary>
    public RoomCategory CategoryOf(int code)
        => _rooms.TryGetValue(code, out var r) ? r.Category : RoomCategory.Exclude;

    /// <summary>
    /// True when the door from <paramref name="fromCode"/> to <paramref name="toCode"/> is tagged
    /// one-way/scripted (a <c>staticTarget</c>) on either side, so the reciprocal-only shuffle must
    /// leave it vanilla.
    /// </summary>
    public bool IsStaticEdge(int fromCode, int toCode)
        => HasStaticTarget(fromCode, toCode) || HasStaticTarget(toCode, fromCode);

    private bool HasStaticTarget(int fromCode, int toCode)
        => _rooms.TryGetValue(fromCode, out var r) && r.StaticTargets.Contains(toCode);

    /// <summary>Load the embedded <c>data/dc1/map.json</c>.</summary>
    public static DoorMap LoadDefault()
    {
        var asm = typeof(DoorMap).Assembly;
        using var stream = asm.GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded door map '{DefaultResourceName}' not found. Resources: " +
                string.Join(", ", asm.GetManifestResourceNames()));
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Parse a door map from JSON (the <c>data/dc1/map.json</c> schema).</summary>
    public static DoorMap Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var be = root.GetProperty("beginEnd");
        int start = ParseCode(be.GetProperty("start").GetString()!);
        int end = ParseCode(be.GetProperty("end").GetString()!);

        var rooms = new Dictionary<int, RoomEntry>();
        foreach (var prop in root.GetProperty("rooms").EnumerateObject())
        {
            int code = ParseCode(prop.Name);
            var v = prop.Value;
            var category = Enum.TryParse<RoomCategory>(
                v.GetProperty("category").GetString(), ignoreCase: true, out var c)
                ? c : RoomCategory.Segment;

            var statics = new HashSet<int>();
            if (v.TryGetProperty("staticTargets", out var st) && st.ValueKind == JsonValueKind.Array)
                foreach (var t in st.EnumerateArray())
                    statics.Add(ParseCode(t.GetString()!));

            rooms[code] = new RoomEntry(category, statics);
        }

        return new DoorMap(start, end, rooms);
    }

    private static int ParseCode(string ssrr)
        => int.Parse(ssrr, System.Globalization.NumberStyles.HexNumber);

    private sealed record RoomEntry(RoomCategory Category, IReadOnlySet<int> StaticTargets);
}
