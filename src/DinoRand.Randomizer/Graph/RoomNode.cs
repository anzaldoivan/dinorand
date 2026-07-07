using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Logic;

namespace DinoRand.Randomizer.Graph;

public sealed class RoomNode
{
    public RoomNode(int stage, int room)
    {
        Stage = stage;
        Room = room;
    }

    public int Stage { get; }
    public int Room { get; }
    public string Id => $"ST{Stage}:R{Room}";

    /// <summary>The room as an <c>SSRR</c> code (<c>stage&lt;&lt;8 | room</c>), matching
    /// <see cref="DoorRecord.TargetCode"/> and the room codes used as keys in
    /// <c>data/dc1/map.json</c> / <c>room-data.json</c>.</summary>
    public int Code => ((Stage & 0xff) << 8) | (Room & 0xff);

    public List<RoomEdge> Edges { get; } = new();

    /// <summary>The item pickups physically in this room — the <i>same</i> <see cref="ItemRecord"/>
    /// instances the passes mutate, so an edit propagates without a copy (plan §4.2). Populated by
    /// <see cref="RoomGraph.Build"/>; door-target-only nodes (no room file) stay empty.</summary>
    public List<NodeItem> Items { get; } = new();

    /// <summary>Room-state gate for <i>entering</i> this room (the map.json <c>requiresRoom</c> case),
    /// AND-applied on top of every incoming edge's own gate. <see cref="Requirement.None"/> by
    /// default.</summary>
    public Requirement Requires { get; set; } = Requirement.None;
}

/// <summary>Randomization priority of a pickup (BioRand's <c>ItemPriority</c> subset, docs/decisions/cross/ITEM-RANDO-PLAN.md
/// §7.1). <see cref="Normal"/> participates fully; <see cref="Fixed"/> stays exactly vanilla (never
/// rerolled or pool-placed) — a per-item form of <see cref="Definitions.GameDefinition.ItemProtectedRoomCodes"/>.
/// BioRand's <c>Low</c>/<c>Hidden</c> only affect <i>key</i> placement, which the item pass does not own,
/// so they are deferred to a <see cref="Logic.KeyItemPlacer"/> increment.</summary>
public enum ItemPriority
{
    Normal,
    Fixed,
}

/// <summary>An item pickup attached to a <see cref="RoomNode"/>: the live record plus its optional
/// guard (the map.json item <c>requires</c> case — a pickup reachable only once the guard is held).</summary>
public sealed record NodeItem(ItemRecord Record)
{
    public Requirement Requires { get; set; } = Requirement.None;

    /// <summary>Randomization priority, stamped from the map.json item overlay (default
    /// <see cref="ItemPriority.Normal"/>). <see cref="ItemPriority.Fixed"/> ⇒ the item pass leaves it vanilla.</summary>
    public ItemPriority Priority { get; set; } = ItemPriority.Normal;

    /// <summary>Relocation-twin link key, stamped from the map.json <c>itemLinks</c> overlay (default
    /// <c>null</c> = unlinked). Records in the same room sharing a non-null <see cref="Link"/> are the
    /// same physical pickup duplicated across script states (the game relocates one logical item across
    /// several records), so they must take ONE shared assignment — the key shuffle / item pass mirror
    /// the group's canonical record onto the rest, never desyncing or duplicating it. Today the key is
    /// the original item-id hex (BioRand's <c>MapRoomItem.Link</c>, clean-room).</summary>
    public string? Link { get; set; }
}

/// <summary>A directed door from one room to another. <see cref="Requires"/> is the composite
/// item-AND gate authored on this door edge (the map.json door <c>requires</c> / puzzle-gate case),
/// AND-applied on top of the door-type key-set OR gate.</summary>
public sealed record RoomEdge(RoomNode Target, DoorRecord Door)
{
    public Requirement Requires { get; set; } = Requirement.None;
}
