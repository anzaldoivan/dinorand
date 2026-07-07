using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;

namespace DinoRand.Randomizer.Maps;

/// <summary>
/// The hand-authored progression-logic overlay parsed from <c>data/dc1/map.json</c> — the
/// <c>requiresRoom</c> / door <c>requires</c> / item <c>requires</c> fields (schema documented in the
/// file's <c>_derivation.requires</c>). Stamps a <see cref="Requirement"/> onto matching nodes,
/// edges, and node items by code, so a new puzzle/key gate is a JSON entry rather than a code change
/// (<c>docs/decisions/cross/GRAPH-LOGIC-PARITY-PLAN.md</c> §4.1/§5). Sibling of <see cref="DoorMap"/>, which owns the
/// door-rando half of the same file; this class deliberately ignores those keys and vice-versa.
///
/// <para>Empty overlay (no room declares any requirement) is the common case today and reproduces
/// the pre-overlay graph exactly.</para>
/// </summary>
public sealed class MapRequirements : IRequirementOverlay
{
    /// <summary>Per-room authored requirements, keyed by room <c>SSRR</c> code.</summary>
    private sealed record RoomReq(
        IReadOnlyList<int> RequiresRoom,                          // requiresRoom (AND of visited rooms)
        IReadOnlyDictionary<int, IReadOnlyList<int>> DoorReq,     // dest code -> required items (AND)
        IReadOnlyDictionary<int, IReadOnlyList<int>> DoorReqRoom, // dest code -> required rooms (AND)
        IReadOnlyDictionary<int, IReadOnlyList<int>> ItemReq,     // pickup item id -> required items (AND)
        IReadOnlyDictionary<(short X, short Z), ItemPriority> ItemPriorities, // pickup position -> priority
        IReadOnlySet<int> ItemLinks);                            // relocation-twin item ids (one shared assignment)

    private readonly IReadOnlyDictionary<int, RoomReq> _rooms;

    private MapRequirements(IReadOnlyDictionary<int, RoomReq> rooms) => _rooms = rooms;

    /// <summary>True when no room authored any requirement (the graph is unaffected).</summary>
    public bool IsEmpty => _rooms.Count == 0;

    public void ApplyTo(RoomGraph graph)
    {
        foreach (var node in graph.Nodes)
        {
            if (!_rooms.TryGetValue(node.Code, out var req)) continue;

            // requiresRoom → a room-state gate on entering this room.
            node.Requires = req.RequiresRoom.Count == 0
                ? Requirement.None
                : new Requirement(System.Array.Empty<int>(), req.RequiresRoom);

            // door requires / requiresRoom → an item-AND ∧ room-state-AND gate on the edge to that
            // destination (BioRand MapRoomDoor.Requires / .RequiresRoom). Both optional; compose.
            if (req.DoorReq.Count > 0 || req.DoorReqRoom.Count > 0)
                foreach (var edge in node.Edges)
                {
                    var hasItems = req.DoorReq.TryGetValue(edge.Target.Code, out var items);
                    var hasRooms = req.DoorReqRoom.TryGetValue(edge.Target.Code, out var rooms);
                    if (hasItems || hasRooms)
                        edge.Requires = new Requirement(
                            items ?? System.Array.Empty<int>(),
                            rooms ?? System.Array.Empty<int>());
                }

            // item requires → an item-AND guard on every pickup of that id in this room.
            if (req.ItemReq.Count > 0)
                foreach (var ni in node.Items)
                    if (req.ItemReq.TryGetValue(ni.Record.OriginalItemId, out var items))
                        ni.Requires = new Requirement(items, System.Array.Empty<int>());

            // item priorities → stamp the pickup at a given position (the placement quad's first
            // corner) with its authored priority (docs/decisions/cross/ITEM-RANDO-PLAN.md §7.1; only Fixed today).
            if (req.ItemPriorities.Count > 0)
                foreach (var ni in node.Items)
                    if (PositionOf(ni.Record) is { } pos && req.ItemPriorities.TryGetValue(pos, out var pri))
                        ni.Priority = pri;

            // item links → tag every pickup of a relocation-twin id in this room with that id's hex as
            // its link key, so the passes mirror the group's canonical assignment across all copies.
            if (req.ItemLinks.Count > 0)
                foreach (var ni in node.Items)
                    if (req.ItemLinks.Contains(ni.Record.OriginalItemId))
                        ni.Link = ni.Record.OriginalItemId.ToString("x2");
        }
    }

    /// <summary>The pickup's position — the placement quad's first corner (X@+0x04, Z@+0x06 of the
    /// raw record). <c>null</c> when the record carries no decoded quad (the priority overlay then
    /// matches nothing, leaving the pickup <see cref="ItemPriority.Normal"/>).</summary>
    private static (short X, short Z)? PositionOf(FileFormats.Stage.ItemRecord r)
    {
        if (r.Raw is not { Length: >= 0x08 }) return null;
        return ((short)(r.Raw[0x04] | r.Raw[0x05] << 8), (short)(r.Raw[0x06] | r.Raw[0x07] << 8));
    }

    /// <summary>Load the overlay from the embedded <c>data/dc1/map.json</c> (same resource as
    /// <see cref="DoorMap.DefaultResourceName"/>).</summary>
    public static MapRequirements LoadDefault()
    {
        var asm = typeof(MapRequirements).Assembly;
        using var stream = asm.GetManifestResourceStream(DoorMap.DefaultResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded map '{DoorMap.DefaultResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Parse the requirement overlay from the <c>map.json</c> schema. Rooms with no
    /// requirement fields are skipped, so the result is empty for an un-authored map.</summary>
    public static MapRequirements Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rooms = new Dictionary<int, RoomReq>();

        foreach (var prop in doc.RootElement.GetProperty("rooms").EnumerateObject())
        {
            var v = prop.Value;
            var requiresRoom = ParseCodeArray(v, "requiresRoom");
            var doorReq = ParseRequiresMap(v, "doors", "requires", IntField);
            var doorReqRoom = ParseRequiresMap(v, "doors", "requiresRoom", CodeField);
            var itemReq = ParseRequiresMap(v, "items", "requires", IntField);
            var itemPriorities = ParseItemPriorities(v);
            var itemLinks = ParseItemLinks(v);

            if (requiresRoom.Count == 0 && doorReq.Count == 0 && doorReqRoom.Count == 0
                && itemReq.Count == 0 && itemPriorities.Count == 0 && itemLinks.Count == 0)
                continue; // un-authored room — nothing to stamp

            rooms[ParseCode(prop.Name)] =
                new RoomReq(requiresRoom, doorReq, doorReqRoom, itemReq, itemPriorities, itemLinks);
        }

        return new MapRequirements(rooms);
    }

    /// <summary>Parse a room's optional <c>itemPriorities</c> array — <c>[ { "at": "X,Z",
    /// "priority": "Fixed" } ]</c> — into a position → priority map. <c>at</c> is the placement quad's
    /// first corner (decimal <c>X,Z</c>). Unknown/Normal priorities are dropped (nothing to stamp).</summary>
    private static IReadOnlyDictionary<(short X, short Z), ItemPriority> ParseItemPriorities(JsonElement room)
    {
        var map = new Dictionary<(short, short), ItemPriority>();
        if (!room.TryGetProperty("itemPriorities", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return map;

        foreach (var e in arr.EnumerateArray())
        {
            if (!e.TryGetProperty("at", out var at) || !e.TryGetProperty("priority", out var pr)) continue;
            if (!Enum.TryParse<ItemPriority>(pr.GetString(), ignoreCase: true, out var priority)
                || priority == ItemPriority.Normal) continue;
            var parts = at.GetString()!.Split(',');
            map[(short.Parse(parts[0], CultureInfo.InvariantCulture),
                 short.Parse(parts[1], CultureInfo.InvariantCulture))] = priority;
        }
        return map;
    }

    /// <summary>Parse a room's optional <c>itemLinks</c> array — <c>[ { "id": "46" }, … ]</c> — into a
    /// set of relocation-twin item ids (hex strings, matching the <c>items</c>-key convention). Entries
    /// without a parseable <c>id</c> are skipped.</summary>
    private static IReadOnlySet<int> ParseItemLinks(JsonElement room)
    {
        var set = new HashSet<int>();
        if (!room.TryGetProperty("itemLinks", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return set;

        foreach (var e in arr.EnumerateArray())
        {
            if (!e.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
            if (int.TryParse(id.GetString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                set.Add(v);
        }
        return set;
    }

    private static int IntField(JsonElement e) => e.GetInt32();          // item ids (decimal/numeric)
    private static int CodeField(JsonElement e) => ParseCode(e.GetString()!); // room SSRR strings

    /// <summary>Parse <c>{ "&lt;container&gt;": { "&lt;code&gt;": { "&lt;field&gt;": [v,...] } } }</c> into a
    /// code → values map, decoding each array element with <paramref name="read"/>. Entries with an
    /// empty/absent field are dropped.</summary>
    private static IReadOnlyDictionary<int, IReadOnlyList<int>> ParseRequiresMap(
        JsonElement room, string container, string field, Func<JsonElement, int> read)
    {
        var map = new Dictionary<int, IReadOnlyList<int>>();
        if (!room.TryGetProperty(container, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return map;

        foreach (var entry in obj.EnumerateObject())
        {
            if (!entry.Value.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            var vals = arr.EnumerateArray().Select(read).ToArray();
            if (vals.Length > 0) map[ParseCode(entry.Name)] = vals;
        }
        return map;
    }

    private static IReadOnlyList<int> ParseCodeArray(JsonElement room, string name)
    {
        if (!room.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return System.Array.Empty<int>();
        return arr.EnumerateArray().Select(e => ParseCode(e.GetString()!)).ToArray();
    }

    private static int ParseCode(string code) =>
        int.Parse(code, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
