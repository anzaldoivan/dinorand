using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;

namespace DinoRand.Randomizer.Maps;

/// <summary>
/// The hand-authored progression-logic overlay parsed from <c>data/dc1/map.json</c> — the
/// <c>requiresRoom</c> / door <c>requires</c> / item <c>requires</c> fields and the explicit
/// <c>traversableTransitions</c> room-transition contract (schema documented in the file's
/// <c>_derivation</c> metadata). Stamps a <see cref="Requirement"/> onto matching nodes,
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
        IReadOnlyDictionary<int, ItemPriority> ItemPriorityRecords, // stable record offset -> priority
        IReadOnlySet<(short X, short Z)> ScatterTargets,        // legal key-item scatter-target positions
        IReadOnlySet<int> ScatterTargetRecords,                 // stable record offsets
        IReadOnlyDictionary<(short X, short Z), PickupVisual> ItemVisuals, // pickup position -> ground visual
        IReadOnlyDictionary<int, PickupVisual> ItemVisualRecords, // stable record offset -> visual
        IReadOnlySet<int> ItemLinks,                            // relocation-twin item ids (one shared assignment)
        IReadOnlyDictionary<int, string> ItemLinkRecords,       // stable record offset -> sync group id
        IReadOnlyDictionary<int, string> ItemGroups,            // stable record offset -> logical pickup id
        IReadOnlyDictionary<int, Requirement> RegionDoorGates,   // laser-fence region: VANILLA dest code -> cross rule
        IReadOnlyDictionary<(short X, short Z), Requirement> RegionItemGates, // fence-behind pickup position -> reach rule
        IReadOnlySet<int> TraversableTransitions);               // explicitly authored non-init destinations

    private readonly IReadOnlyDictionary<int, RoomReq> _rooms;
    private readonly IReadOnlyDictionary<int, RegionSplit> _nodeSplits;

    private MapRequirements(IReadOnlyDictionary<int, RoomReq> rooms,
                            IReadOnlyDictionary<int, RegionSplit> nodeSplits)
    {
        _rooms = rooms;
        _nodeSplits = nodeSplits;
    }

    /// <summary>True when no room authored any requirement (the graph is unaffected).</summary>
    public bool IsEmpty => _rooms.Count == 0 && _nodeSplits.Count == 0;

    /// <summary>Rooms the graph must split into per-region sub-nodes (REGION-SCHEMA-PLAN.md §2), parsed
    /// from a room's <c>regions</c> object when it declares <c>"nodeSplit": true</c>.</summary>
    public IReadOnlyDictionary<int, RegionSplit> NodeSplits => _nodeSplits;

    /// <summary>True when the map explicitly promotes this raw non-init record's vanilla destination
    /// to a graph transition. The target is matched before any door-rando retargeting.</summary>
    public bool IsAuthoredTraversableRoomTransition(int sourceCode, FileFormats.Stage.DoorRecord door)
    {
        if (door.IsTraversableRoomTransition) return false;
        if (!_rooms.TryGetValue(sourceCode, out var req)) return false;
        int vanillaTarget = door.OriginalTargetCode == 0 ? door.TargetCode : door.OriginalTargetCode;
        return req.TraversableTransitions.Contains(vanillaTarget);
    }

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
            // destination (BioRand MapRoomDoor.Requires / .RequiresRoom). Both optional; compose. Keyed
            // by the physical doorway (its VANILLA destination) like the region gate below, so the gate
            // travels with the door under a shuffle instead of floating; for a fixed door
            // OriginalTargetCode == Target.Code, so this is byte-identical with door-rando off.
            if (req.DoorReq.Count > 0 || req.DoorReqRoom.Count > 0)
                foreach (var edge in node.Edges)
                {
                    var hasItems = req.DoorReq.TryGetValue(edge.Door.OriginalTargetCode, out var items);
                    var hasRooms = req.DoorReqRoom.TryGetValue(edge.Door.OriginalTargetCode, out var rooms);
                    if (hasItems || hasRooms)
                        edge.Requires = new Requirement(
                            items ?? System.Array.Empty<int>(),
                            rooms ?? System.Array.Empty<int>());
                }

            // laser-fence region door gate → the rule to cross a partition, bound to the physical
            // doorway (its VANILLA destination) rather than the current edge target, so it travels with
            // the door under a shuffle instead of floating (REGION-SCHEMA-PLAN.md). For a fixed door
            // OriginalTargetCode == Target.Code, so this reproduces the pre-migration door-level gate
            // exactly; under door-rando the gate stays on the fence's own doorway.
            if (req.RegionDoorGates.Count > 0)
                foreach (var edge in node.Edges)
                    if (req.RegionDoorGates.TryGetValue(edge.Door.OriginalTargetCode, out var rule))
                        edge.Requires = rule;

            // item requires → an item-AND guard on every pickup of that id in this room.
            if (req.ItemReq.Count > 0)
                foreach (var ni in node.Items)
                    if (req.ItemReq.TryGetValue(ni.Record.OriginalItemId, out var items))
                        ni.Requires = new Requirement(items, System.Array.Empty<int>());

            // laser-fence region item gate → a reach guard on the pickups sitting behind a fence in an
            // item-only segment (0606's east segment), keyed by position like itemPriorities.
            if (req.RegionItemGates.Count > 0)
                foreach (var ni in node.Items)
                    if (PositionOf(ni.Record) is { } pos && req.RegionItemGates.TryGetValue(pos, out var rule))
                        ni.Requires = rule;

            // item priorities → stamp the pickup at a given position (the placement quad's first
            // corner) with its authored priority (docs/decisions/cross/ITEM-RANDO-PLAN.md §7.1; only Fixed today).
            if (req.ItemPriorities.Count > 0 || req.ItemPriorityRecords.Count > 0)
                foreach (var ni in node.Items)
                    if (req.ItemPriorityRecords.TryGetValue(ni.Record.FileOffset, out var recordPri))
                        ni.Priority = recordPri;
                    else if (PositionOf(ni.Record) is { } pos && req.ItemPriorities.TryGetValue(pos, out var pri))
                    {
                        // Legacy/synthetic rooms may not carry production record offsets. Fall back to
                        // position only when none of this room's explicit physical targets matched;
                        // otherwise a coordinate collision must not inherit another record's policy.
                        bool explicitTargetPresent = node.Items.Any(other =>
                            req.ItemPriorityRecords.ContainsKey(other.Record.FileOffset)
                            && PositionOf(other.Record) == pos);
                        if (!explicitTargetPresent) ni.Priority = pri;
                    }

            // scatter targets → mark stable record targets as legal key-item scatter spots. Legacy
            // position entries remain supported for synthetic fixtures only.
            if (req.ScatterTargets.Count > 0 || req.ScatterTargetRecords.Count > 0)
                foreach (var ni in node.Items)
                    if (req.ScatterTargetRecords.Contains(ni.Record.FileOffset)
                        || (PositionOf(ni.Record) is { } pos && req.ScatterTargets.Contains(pos)))
                        ni.IsScatterTarget = true;

            // ground visuals → stamp the pickup's decoded visual class (generated overlay, non-default
            // classes only; docs/decisions/dc1/items/PICKUP-GROUND-MODEL-FEASIBILITY.md).
            if (req.ItemVisuals.Count > 0 || req.ItemVisualRecords.Count > 0)
                foreach (var ni in node.Items)
                    if (req.ItemVisualRecords.TryGetValue(ni.Record.FileOffset, out var recordVis))
                        ni.Visual = recordVis;
                    else if (PositionOf(ni.Record) is { } pos && req.ItemVisuals.TryGetValue(pos, out var vis))
                        ni.Visual = vis;

            // item links → tag explicit record-offset synchronization groups. Legacy id-only entries
            // remain parseable, but generated policy never infers membership from repeated ids.
            if (req.ItemLinks.Count > 0 || req.ItemLinkRecords.Count > 0)
                foreach (var ni in node.Items)
                    if (req.ItemLinkRecords.TryGetValue(ni.Record.FileOffset, out var link))
                        ni.Link = link;
                    else if (req.ItemLinks.Contains(ni.Record.OriginalItemId))
                        ni.Link = ni.Record.OriginalItemId.ToString("x2");

            foreach (var ni in node.Items)
                if (req.ItemGroups.TryGetValue(ni.Record.FileOffset, out var logicalId))
                    ni.LogicalId = new LogicalPickupId(logicalId);
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
        var nodeSplits = new Dictionary<int, RegionSplit>();

        foreach (var prop in doc.RootElement.GetProperty("rooms").EnumerateObject())
        {
            var v = prop.Value;
            var requiresRoom = ParseCodeArray(v, "requiresRoom");
            var traversableTransitions = ParseCodeArray(v, "traversableTransitions").ToHashSet();
            var doorReq = ParseRequiresMap(v, "doors", "requires", IntField);
            var doorReqRoom = ParseRequiresMap(v, "doors", "requiresRoom", CodeField);
            var itemReq = ParseRequiresMap(v, "items", "requires", IntField);
            var itemPriorities = ParseItemPriorities(v);
            var itemPriorityRecords = ParseItemPriorityRecords(v);
            var scatterTargets = ParseScatterTargets(v);
            var scatterTargetRecords = ParseRecordSet(v, "scatterTargets");
            var itemVisuals = ParseItemVisuals(v);
            var itemVisualRecords = ParseItemVisualRecords(v);
            var itemLinks = ParseItemLinks(v);
            var itemLinkRecords = ParseItemLinkRecords(v);
            var itemGroups = ParseItemGroups(v);

            // A node-split room (REGION-SCHEMA-PLAN.md §2) models its partition as real sub-region nodes in
            // RoomGraph.Build, so it does NOT also get the flattened fence door/item gates (no double-gate).
            var split = ParseNodeSplit(v);
            var (regionDoorGates, regionItemGates) = split is null
                ? ParseRegions(v)
                : ((IReadOnlyDictionary<int, Requirement>)new Dictionary<int, Requirement>(),
                   (IReadOnlyDictionary<(short X, short Z), Requirement>)new Dictionary<(short, short), Requirement>());
            if (split is not null) nodeSplits[ParseCode(prop.Name)] = split;

            if (requiresRoom.Count == 0 && doorReq.Count == 0 && doorReqRoom.Count == 0
                && itemReq.Count == 0 && itemPriorities.Count == 0 && itemPriorityRecords.Count == 0
                && scatterTargets.Count == 0 && scatterTargetRecords.Count == 0
                && itemVisuals.Count == 0 && itemVisualRecords.Count == 0
                && itemLinks.Count == 0 && itemLinkRecords.Count == 0 && itemGroups.Count == 0
                && regionDoorGates.Count == 0 && regionItemGates.Count == 0
                && traversableTransitions.Count == 0)
                continue; // un-authored room — nothing to stamp

            rooms[ParseCode(prop.Name)] =
                new RoomReq(requiresRoom, doorReq, doorReqRoom, itemReq, itemPriorities,
                            itemPriorityRecords, scatterTargets, scatterTargetRecords,
                            itemVisuals, itemVisualRecords, itemLinks,
                            itemLinkRecords, itemGroups,
                            regionDoorGates, regionItemGates, traversableTransitions);
        }

        return new MapRequirements(rooms, nodeSplits);
    }

    /// <summary>Parse a room's optional <c>regions</c> object (intra-room laser-fence partitions —
    /// REGION-SCHEMA-PLAN.md). Each non-primary region's <c>accessFrom</c> rule is compiled to (a) door
    /// gates keyed by the region's VANILLA door destinations, so <see cref="ApplyTo"/> can bind them to
    /// the physical doorway via <c>DoorRecord.OriginalTargetCode</c> (the gate then travels with the door
    /// under a shuffle instead of floating), and (b) item gates keyed by the segment's pickup positions
    /// (0606's east segment). A primary/entry region (no <c>accessFrom</c>) or an always-open fence
    /// (empty rule <c>{}</c> — self-enabling / collocated with a typed door / init-forced-down) yields no
    /// gate, so such fence rooms stay behaviourally atomic.</summary>
    private static (IReadOnlyDictionary<int, Requirement> Doors,
                    IReadOnlyDictionary<(short X, short Z), Requirement> Items) ParseRegions(JsonElement room)
    {
        var doors = new Dictionary<int, Requirement>();
        var items = new Dictionary<(short, short), Requirement>();
        if (!room.TryGetProperty("regions", out var regs) || regs.ValueKind != JsonValueKind.Object)
            return (doors, items);

        foreach (var reg in regs.EnumerateObject())
        {
            if (!reg.Value.TryGetProperty("accessFrom", out var af) || af.ValueKind != JsonValueKind.Object)
                continue; // primary/entry region — nothing gates into it
            // accessFrom is keyed by source region; our data always crosses from the single primary, so
            // AND every source rule together (a region reached from >1 side needs all their rules).
            var reqItems = new List<int>();
            var reqRooms = new List<int>();
            foreach (var src in af.EnumerateObject())
            {
                if (src.Value.TryGetProperty("requires", out var ri) && ri.ValueKind == JsonValueKind.Array)
                    reqItems.AddRange(ri.EnumerateArray().Select(IntField));
                if (src.Value.TryGetProperty("requiresRoom", out var rr) && rr.ValueKind == JsonValueKind.Array)
                    reqRooms.AddRange(rr.EnumerateArray().Select(CodeField));
            }
            if (reqItems.Count == 0 && reqRooms.Count == 0) continue; // always-open fence — no gate

            var rule = new Requirement(reqItems.ToArray(), reqRooms.ToArray());
            if (reg.Value.TryGetProperty("doors", out var dd) && dd.ValueKind == JsonValueKind.Array)
                foreach (var d in dd.EnumerateArray())
                    doors[ParseCode(d.GetString()!)] = rule;
            if (reg.Value.TryGetProperty("items", out var ii) && ii.ValueKind == JsonValueKind.Array)
                foreach (var p in ii.EnumerateArray())
                {
                    var parts = p.GetString()!.Split(',');
                    items[(short.Parse(parts[0], CultureInfo.InvariantCulture),
                           short.Parse(parts[1], CultureInfo.InvariantCulture))] = rule;
                }
        }
        return (doors, items);
    }

    /// <summary>Parse a node-split room (REGION-SCHEMA-PLAN.md §2): <c>"nodeSplit": true</c> plus a
    /// <c>regions</c> object whose entries become sub-region nodes, in declared order (first = primary,
    /// index 0). Each region's <c>doors</c> array lists the external SSRR destinations physically on its
    /// side; an optional <c>accessFrom</c> compiles to an intra-room crossing edge (absent ⇒ no on-foot
    /// crossing — the 0309 shuttle). Returns <c>null</c> when the room is not node-split.</summary>
    private static RegionSplit? ParseNodeSplit(JsonElement room)
    {
        if (!room.TryGetProperty("nodeSplit", out var ns) || ns.ValueKind != JsonValueKind.True)
            return null;
        if (!room.TryGetProperty("regions", out var regs) || regs.ValueKind != JsonValueKind.Object)
            return null;

        var names = regs.EnumerateObject().Select(r => r.Name).ToList();
        var defs = new List<RegionDef>();
        int index = 0;
        foreach (var reg in regs.EnumerateObject())
        {
            var doors = new HashSet<int>();
            if (reg.Value.TryGetProperty("doors", out var dd) && dd.ValueKind == JsonValueKind.Array)
                foreach (var d in dd.EnumerateArray())
                    doors.Add(ParseCode(d.GetString()!));

            InternalEdge? internalEdge = null;
            if (reg.Value.TryGetProperty("accessFrom", out var af) && af.ValueKind == JsonValueKind.Object)
                foreach (var src in af.EnumerateObject())
                {
                    var reqItems = new List<int>();
                    var reqRooms = new List<int>();
                    if (src.Value.TryGetProperty("requires", out var ri) && ri.ValueKind == JsonValueKind.Array)
                        reqItems.AddRange(ri.EnumerateArray().Select(IntField));
                    if (src.Value.TryGetProperty("requiresRoom", out var rr) && rr.ValueKind == JsonValueKind.Array)
                        reqRooms.AddRange(rr.EnumerateArray().Select(CodeField));
                    int fromIndex = names.IndexOf(src.Name);
                    if (fromIndex < 0) continue;
                    internalEdge = new InternalEdge(fromIndex,
                        new Requirement(reqItems.ToArray(), reqRooms.ToArray()));
                    break; // one crossing edge modeled; multi-source regions revisit when a case needs it
                }

            defs.Add(new RegionDef(reg.Name, index++, doors, internalEdge));
        }
        return defs.Count > 0 ? new RegionSplit(defs) : null;
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

    private static IReadOnlyDictionary<int, ItemPriority> ParseItemPriorityRecords(JsonElement room)
    {
        var map = new Dictionary<int, ItemPriority>();
        if (!room.TryGetProperty("itemPriorities", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return map;
        foreach (var entry in arr.EnumerateArray())
        {
            if (!entry.TryGetProperty("priority", out var value)
                || !Enum.TryParse<ItemPriority>(value.GetString(), true, out var priority)
                || priority == ItemPriority.Normal
                || !entry.TryGetProperty("records", out var records)
                || records.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var record in records.EnumerateArray())
            {
                int offset = ParseOffset(record.GetString()!);
                if (!map.TryAdd(offset, priority))
                    throw new InvalidDataException($"duplicate item-priority record target 0x{offset:x}");
            }
        }
        return map;
    }

    private static IReadOnlyDictionary<int, string> ParseItemGroups(JsonElement room)
    {
        var map = new Dictionary<int, string>();
        if (!room.TryGetProperty("itemGroups", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return map;
        foreach (var group in arr.EnumerateArray())
        {
            if (!group.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !group.TryGetProperty("records", out var records)
                || records.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("itemGroups entries require string id and records array");
            string logicalId = id.GetString()!;
            foreach (var record in records.EnumerateArray())
            {
                int offset = ParseOffset(record.GetString()!);
                if (!map.TryAdd(offset, logicalId))
                    throw new InvalidDataException($"physical record 0x{offset:x} belongs to multiple item groups");
            }
        }
        return map;
    }

    /// <summary>Parse a room's optional <c>scatterTargets</c> array — <c>[ { "at": "X,Z" }, … ]</c> —
    /// into a set of legal key-item scatter-target positions (the placement quad's first corner, decimal
    /// <c>X,Z</c>), keyed like <c>itemPriorities</c>. Generated by <c>scripts/gen_item_map.py</c>.</summary>
    private static IReadOnlySet<(short X, short Z)> ParseScatterTargets(JsonElement room)
    {
        var set = new HashSet<(short, short)>();
        if (!room.TryGetProperty("scatterTargets", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return set;

        foreach (var e in arr.EnumerateArray())
        {
            if (e.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
                continue;
            if (!e.TryGetProperty("at", out var at)) continue;
            var parts = at.GetString()!.Split(',');
            set.Add((short.Parse(parts[0], CultureInfo.InvariantCulture),
                     short.Parse(parts[1], CultureInfo.InvariantCulture)));
        }
        return set;
    }

    /// <summary>Parse a room's optional <c>itemVisuals</c> array — <c>[ { "at": "X,Z", "visual":
    /// "interaction-only" | "bespoke-mesh" }, … ]</c> — into a position → <see cref="PickupVisual"/>
    /// map (the placement quad's first corner, decimal <c>X,Z</c>, keyed like <c>itemPriorities</c>).
    /// Generated by <c>scripts/gen_item_map.py</c>; only non-default classes are emitted
    /// (<c>generic-panel</c> is the implicit default). Unknown classes are skipped.</summary>
    private static IReadOnlyDictionary<(short X, short Z), PickupVisual> ParseItemVisuals(JsonElement room)
    {
        var map = new Dictionary<(short, short), PickupVisual>();
        if (!room.TryGetProperty("itemVisuals", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return map;

        foreach (var e in arr.EnumerateArray())
        {
            if (e.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
                continue;
            if (!e.TryGetProperty("at", out var at) || !e.TryGetProperty("visual", out var vi)) continue;
            PickupVisual? visual = vi.GetString() switch
            {
                "interaction-only" => PickupVisual.InteractionOnly,
                "bespoke-mesh" => PickupVisual.BespokeMesh,
                "generic-panel" => PickupVisual.GenericPanel,
                _ => null,
            };
            if (visual is null) continue;
            var parts = at.GetString()!.Split(',');
            map[(short.Parse(parts[0], CultureInfo.InvariantCulture),
                 short.Parse(parts[1], CultureInfo.InvariantCulture))] = visual.Value;
        }
        return map;
    }

    private static IReadOnlySet<int> ParseRecordSet(JsonElement room, string property)
    {
        var set = new HashSet<int>();
        if (!room.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return set;
        foreach (var entry in arr.EnumerateArray())
            if (entry.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
                foreach (var record in records.EnumerateArray())
                    if (!set.Add(ParseOffset(record.GetString()!)))
                        throw new InvalidDataException($"duplicate {property} physical target {record.GetString()}");
        return set;
    }

    private static IReadOnlyDictionary<int, PickupVisual> ParseItemVisualRecords(JsonElement room)
    {
        var map = new Dictionary<int, PickupVisual>();
        if (!room.TryGetProperty("itemVisuals", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return map;
        foreach (var entry in arr.EnumerateArray())
        {
            if (!entry.TryGetProperty("visual", out var value)
                || !entry.TryGetProperty("records", out var records)
                || records.ValueKind != JsonValueKind.Array)
                continue;
            PickupVisual? visual = value.GetString() switch
            {
                "interaction-only" => PickupVisual.InteractionOnly,
                "bespoke-mesh" => PickupVisual.BespokeMesh,
                "generic-panel" => PickupVisual.GenericPanel,
                _ => null,
            };
            if (visual is null) continue;
            foreach (var record in records.EnumerateArray())
            {
                int offset = ParseOffset(record.GetString()!);
                if (!map.TryAdd(offset, visual.Value))
                    throw new InvalidDataException($"duplicate item-visual record target 0x{offset:x}");
            }
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
            if (e.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
                continue;
            if (!e.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
            if (int.TryParse(id.GetString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                set.Add(v);
        }
        return set;
    }

    private static IReadOnlyDictionary<int, string> ParseItemLinkRecords(JsonElement room)
    {
        var map = new Dictionary<int, string>();
        if (!room.TryGetProperty("itemLinks", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return map;
        foreach (var group in arr.EnumerateArray())
        {
            if (!group.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !group.TryGetProperty("records", out var records)
                || records.ValueKind != JsonValueKind.Array)
                continue; // legacy id-only entry
            string link = id.GetString()!;
            foreach (var record in records.EnumerateArray())
            {
                int offset = ParseOffset(record.GetString()!);
                if (!map.TryAdd(offset, link))
                    throw new InvalidDataException($"physical record 0x{offset:x} belongs to multiple item links");
            }
        }
        return map;
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

    private static int ParseOffset(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? int.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : int.Parse(value, CultureInfo.InvariantCulture);
}
