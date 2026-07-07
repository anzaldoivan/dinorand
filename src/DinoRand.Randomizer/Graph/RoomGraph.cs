using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Graph;

/// <summary>
/// The world as a graph of rooms connected by doors. Built from the parsed stages and
/// consumed by key-item logic (Phase 1+) and door randomization (Phase 3). Also
/// exportable to DGML for visual debugging, like BioRand's maps.
/// </summary>
public sealed class RoomGraph
{
    private readonly Dictionary<(int stage, int room), RoomNode> _nodes = new();

    public IReadOnlyCollection<RoomNode> Nodes => _nodes.Values;

    public static RoomGraph Build(IReadOnlyList<RoomFile> rooms,
                                  IRequirementOverlay? requirements = null)
    {
        var graph = new RoomGraph();

        foreach (var room in rooms)
        {
            var node = graph.GetOrAdd(room.Stage, room.Room);
            // Attach the live item records so the passes read them off the node (plan §4.4) and so
            // an item guard can be stamped per pickup by the overlay.
            foreach (var item in room.Items)
                node.Items.Add(new NodeItem(item));
        }

        foreach (var room in rooms)
        {
            var from = graph.GetOrAdd(room.Stage, room.Room);
            foreach (var door in room.Doors)
            {
                var to = graph.GetOrAdd(door.TargetStage, door.TargetRoom);
                from.Edges.Add(new RoomEdge(to, door));
            }
        }

        // Stamp hand-authored progression logic (puzzle gates / room-state / item-guards) onto the
        // graph. Absent ⇒ pure door-type gating (today's behaviour, byte-for-byte).
        requirements?.ApplyTo(graph);

        return graph;
    }

    public RoomNode GetOrAdd(int stage, int room)
    {
        var key = (stage, room);
        if (!_nodes.TryGetValue(key, out var node))
        {
            node = new RoomNode(stage, room);
            _nodes[key] = node;
        }
        return node;
    }

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
