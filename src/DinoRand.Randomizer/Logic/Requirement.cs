namespace DinoRand.Randomizer.Logic;

/// <summary>
/// A composite progression predicate carried on a graph edge, node, or item — BioRand's exact model
/// (<c>ref/classic/IntelOrca.Biohazard.BioRand/RandomItems/ItemRandomiser.cs:548,551-553</c>):
/// flat arrays, AND semantics. <see cref="Items"/> are item ids that must all be <i>held</i>;
/// <see cref="RoomsVisited"/> are room codes that must all be <i>reachable</i>. OR is never an array
/// operator — it stays structural (two edges) plus the existing door-type key-set OR
/// (<see cref="Definitions.GameDefinition.KeyItemsForDoor"/>). See
/// <c>docs/decisions/cross/GRAPH-LOGIC-PARITY-PLAN.md</c> §4.2 / Decision B.
///
/// <para>Null-safe by design: <c>default(Requirement)</c> (both arrays null) behaves exactly like
/// <see cref="None"/>, so it is a valid optional-parameter / record-field default.</para>
/// </summary>
public readonly record struct Requirement(
    IReadOnlyList<int>? Items,
    IReadOnlyList<int>? RoomsVisited)
{
    /// <summary>The empty requirement — always satisfied.</summary>
    public static readonly Requirement None = new(System.Array.Empty<int>(), System.Array.Empty<int>());

    /// <summary>An item-only requirement (a held-key AND set), no room-state.</summary>
    public static Requirement OfItems(params int[] items) => new(items, System.Array.Empty<int>());

    /// <summary>A room-state-only requirement (visited-rooms AND set), no held items.</summary>
    public static Requirement OfRooms(params int[] rooms) => new(System.Array.Empty<int>(), rooms);

    public bool IsEmpty => (Items?.Count ?? 0) == 0 && (RoomsVisited?.Count ?? 0) == 0;

    /// <summary>
    /// True when every required item is in <paramref name="heldItems"/> and every required room is in
    /// <paramref name="visitedRooms"/> (the current reachable set). A null array counts as no constraint.
    /// </summary>
    public bool SatisfiedBy(IReadOnlySet<int> heldItems, IReadOnlySet<int> visitedRooms) =>
        (Items is null || Items.All(heldItems.Contains)) &&
        (RoomsVisited is null || RoomsVisited.All(visitedRooms.Contains));
}
