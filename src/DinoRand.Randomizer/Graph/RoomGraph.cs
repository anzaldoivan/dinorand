using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Graph;

/// <summary>
/// The world as a graph of rooms connected by doors. Built from the parsed stages and
/// consumed by key-item logic (Phase 1+) and door randomization (Phase 3). Also
/// exportable to DGML for visual debugging, like BioRand's maps.
///
/// <para>Nodes are keyed by <see cref="RoomNode.NodeCode"/>, not room code: an atomic room is one
/// node (<c>NodeCode == Code</c>), while a room with an intra-room entry-direction partition splits
/// into one node per sub-region (REGION-SCHEMA-PLAN.md §2). Un-split rooms are byte-identical to the
/// pre-split graph.</para>
/// </summary>
public sealed class RoomGraph
{
    private readonly Dictionary<int, RoomNode> _nodes = new(); // keyed by NodeCode

    public IReadOnlyCollection<RoomNode> Nodes => _nodes.Values;

    public static RoomGraph Build(IReadOnlyList<RoomFile> rooms,
                                  IRequirementOverlay? requirements = null)
    {
        var graph = new RoomGraph();
        var splits = requirements?.NodeSplits ?? EmptySplits;

        foreach (var room in rooms)
        {
            int code = ((room.Stage & 0xff) << 8) | (room.Room & 0xff);
            if (splits.TryGetValue(code, out var split))
            {
                // One node per sub-region. Item records live on the primary region (index 0) — no split
                // room ships an item behind its partition, so this keeps keysByRoom/PickupReachable correct
                // (REGION-SCHEMA-PLAN.md §2); revisit if a future split room hides a gated pickup.
                foreach (var rd in split.Regions)
                    graph.GetOrAddRegion(room.Stage, room.Room, rd.Index, rd.Name);
                var primary = graph.GetOrAddRegion(room.Stage, room.Room, 0, split.Regions[0].Name);
                foreach (var item in room.Items)
                    primary.Items.Add(new NodeItem(item, code));
            }
            else
            {
                var node = graph.GetOrAdd(room.Stage, room.Room);
                foreach (var item in room.Items)
                    node.Items.Add(new NodeItem(item, code));
            }
        }

        foreach (var room in rooms)
        {
            int code = ((room.Stage & 0xff) << 8) | (room.Room & 0xff);
            foreach (var door in room.Doors)
            {
                // RoomScript preserves every raw 0x28 record for lossless editing. Decoded transition
                // activations are graph edges by default; an explicitly authored overlay may add a
                // verified non-init room transition. Do not infer traversability from reciprocity,
                // which would erase legitimate one-way story doors.
                if (!door.IsTraversableRoomTransition
                    && !(requirements?.IsAuthoredTraversableRoomTransition(code, door) ?? false))
                    continue;
                var source = OwningRegion(graph, splits, room.Stage, room.Room, door.TargetCode);
                var target = LandingRegion(graph, splits, door.TargetStage, door.TargetRoom, code);
                source.Edges.Add(new RoomEdge(target, door));
            }
        }

        // Intra-room crossing edges (REGION-SCHEMA-PLAN.md §2 accessFrom). Absent for 0309 (the shuttle
        // crossing predicate is [uncertain — needs CE]); wired here for future fence migration.
        foreach (var (code, split) in splits)
            foreach (var rd in split.Regions)
                if (rd.Internal is { } ie)
                {
                    var from = graph.GetRegion(code, ie.FromIndex);
                    var to = graph.GetRegion(code, rd.Index);
                    if (from is null || to is null) continue;
                    var synthetic = new DoorRecord { TargetStage = to.Stage, TargetRoom = to.Room, DoorType = 0 };
                    from.Edges.Add(new RoomEdge(to, synthetic) { Requires = ie.Rule });
                }

        requirements?.ApplyTo(graph);
        return graph;
    }

    /// <summary>The region node of <paramref name="destStage"/>/<paramref name="destRoom"/> a traversal
    /// <i>from</i> <paramref name="fromCode"/> lands in: the destination's sub-region that owns the
    /// reciprocal door back to the source (entry-direction landing). Atomic destination ⇒ its single
    /// node; no reciprocal match ⇒ the primary region.</summary>
    private static RoomNode LandingRegion(RoomGraph g, IReadOnlyDictionary<int, RegionSplit> splits,
                                          int destStage, int destRoom, int fromCode)
    {
        int destCode = ((destStage & 0xff) << 8) | (destRoom & 0xff);
        if (splits.TryGetValue(destCode, out var split))
        {
            var region = split.Regions.FirstOrDefault(r => r.DoorDests.Contains(fromCode))
                         ?? split.Regions[0];
            return g.GetOrAddRegion(destStage, destRoom, region.Index, region.Name);
        }
        return g.GetOrAdd(destStage, destRoom);
    }

    /// <summary>The region node of a split source room that owns the door to <paramref name="destCode"/>
    /// (the door sits physically on that sub-region's side). Atomic source ⇒ its single node; no owner
    /// match ⇒ the primary region.</summary>
    private static RoomNode OwningRegion(RoomGraph g, IReadOnlyDictionary<int, RegionSplit> splits,
                                         int stage, int room, int destCode)
    {
        int code = ((stage & 0xff) << 8) | (room & 0xff);
        if (splits.TryGetValue(code, out var split))
        {
            var region = split.Regions.FirstOrDefault(r => r.DoorDests.Contains(destCode))
                         ?? split.Regions[0];
            return g.GetOrAddRegion(stage, room, region.Index, region.Name);
        }
        return g.GetOrAdd(stage, room);
    }

    public RoomNode GetOrAdd(int stage, int room) => GetOrAddRegion(stage, room, 0, null);

    public RoomNode GetOrAddRegion(int stage, int room, int regionIndex, string? regionName)
    {
        int nodeCode = (regionIndex << 16) | ((stage & 0xff) << 8) | (room & 0xff);
        if (!_nodes.TryGetValue(nodeCode, out var node))
        {
            node = new RoomNode(stage, room, regionIndex, regionName);
            _nodes[nodeCode] = node;
        }
        return node;
    }

    /// <summary>The region node for <paramref name="code"/> (SSRR) at <paramref name="regionIndex"/>, or
    /// <c>null</c> if not present.</summary>
    public RoomNode? GetRegion(int code, int regionIndex) =>
        _nodes.TryGetValue((regionIndex << 16) | code, out var n) ? n : null;

    private static readonly IReadOnlyDictionary<int, RegionSplit> EmptySplits = new Dictionary<int, RegionSplit>();

    /// <summary>Export a DGML graph for inspection (BioRand-style diagnostics).</summary>
    public string ToDgml()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<DirectedGraph xmlns=\"http://schemas.microsoft.com/vs/2009/dgml\">");
        sb.AppendLine("  <Nodes>");
        foreach (var n in _nodes.Values)
            sb.AppendLine($"    <Node Id=\"{n.Id}\" />");
        sb.AppendLine("  </Nodes>");
        sb.AppendLine("  <Links>");
        foreach (var n in _nodes.Values)
            foreach (var e in n.Edges)
                sb.AppendLine($"    <Link Source=\"{n.Id}\" Target=\"{e.Target.Id}\" />");
        sb.AppendLine("  </Links>");
        sb.AppendLine("</DirectedGraph>");
        return sb.ToString();
    }
}
