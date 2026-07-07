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
}
