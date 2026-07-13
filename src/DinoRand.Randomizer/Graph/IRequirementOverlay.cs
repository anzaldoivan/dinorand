using DinoRand.Randomizer.Logic;

namespace DinoRand.Randomizer.Graph;

/// <summary>
/// A hand-authored progression-logic overlay (puzzle gates / room-state / item-guards) that a
/// <see cref="Definitions.GameDefinition"/> supplies as data, applied onto a built
/// <see cref="RoomGraph"/> by room/door/item code. Absent (null) ⇒ pure door-type gating, i.e. the
/// graph reproduces today's behaviour byte-for-byte. See <c>docs/decisions/cross/GRAPH-LOGIC-PARITY-PLAN.md</c> §4.2.
/// </summary>
public interface IRequirementOverlay
{
    /// <summary>Stamp this overlay's requirements onto the matching edges, nodes, and items of
    /// <paramref name="graph"/>. Idempotent: re-applying after a rebuild restamps from scratch.</summary>
    void ApplyTo(RoomGraph graph);

    /// <summary>Rooms that <see cref="RoomGraph.Build"/> must split into per-region sub-nodes, keyed by
    /// room <c>SSRR</c> code (REGION-SCHEMA-PLAN.md §2). Empty ⇒ every room is atomic (today's graph,
    /// byte-identical). Used for intra-room, <i>entry-direction</i> partitions the flat door/room-state
    /// gates cannot express (the 0309 shuttle). Default empty so alternate/fake overlays need not
    /// implement it.</summary>
    IReadOnlyDictionary<int, RegionSplit> NodeSplits => EmptyNodeSplits;

    private static readonly IReadOnlyDictionary<int, RegionSplit> EmptyNodeSplits =
        new Dictionary<int, RegionSplit>();
}

/// <summary>The sub-region decomposition of one room (REGION-SCHEMA-PLAN.md §2). <see cref="Regions"/>
/// is ordered; index 0 is the <b>primary</b> region (keeps the room's <see cref="RoomNode.Code"/>
/// identity, so <c>NodeCode == Code</c>).</summary>
public sealed record RegionSplit(IReadOnlyList<RegionDef> Regions);

/// <summary>One sub-region of a split room: its <see cref="Name"/>, its <see cref="Index"/> (0 =
/// primary), the external door <see cref="DoorDests"/> physically reachable from it (SSRR codes), and an
/// optional intra-room <see cref="Internal"/> crossing edge from another region into this one.</summary>
public sealed record RegionDef(
    string Name,
    int Index,
    IReadOnlySet<int> DoorDests,
    InternalEdge? Internal = null);

/// <summary>An intra-room crossing edge into a region from <see cref="FromIndex"/>, gated by
/// <see cref="Rule"/> (REGION-SCHEMA-PLAN.md §2 <c>accessFrom</c>). Absent ⇒ no on-foot crossing (the
/// two sub-regions connect only through their external doors — the tightest safe model, used for the
/// 0309 shuttle whose crossing predicate is <c>[uncertain — needs CE]</c>).</summary>
public sealed record InternalEdge(int FromIndex, Requirement Rule);
